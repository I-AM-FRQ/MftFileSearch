using System.Text;

namespace MftFileSearch;

/// <summary>紧凑 MFT 索引。文件与目录保存名称、FRN 和目录关系；路径仅在命中页中按 FRN 还原。</summary>
internal sealed class FileIndexDatabase
{
    private static readonly byte[] Magic = "MFTIDX01"u8.ToArray();
    private const int FormatVersion = 5;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private readonly string _databasePath;
    private Dictionary<string, VolumeIndex> _volumes = new(StringComparer.OrdinalIgnoreCase);

    internal FileIndexDatabase(string databasePath)
    {
        _databasePath = databasePath;
    }

    internal void Initialize()
    {
        if (!File.Exists(_databasePath))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(_databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("索引版本不支持增量更新。请使用 scan 重新建立 .mftdb 索引。");
            }

            int volumeCount = reader.ReadInt32();
            if (volumeCount is < 0 or > 256)
            {
                throw new InvalidDataException("索引文件中的卷数量无效。");
            }

            var volumes = new Dictionary<string, VolumeIndex>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < volumeCount; index++)
            {
                string root = ReadString(reader);
                DateTime scannedUtc = new(reader.ReadInt64(), DateTimeKind.Utc);
                ulong journalId = reader.ReadUInt64();
                long nextUsn = reader.ReadInt64();
                int fileCount = reader.ReadInt32();
                int directoryCount = reader.ReadInt32();
                if (fileCount < 0 || directoryCount < 0)
                {
                    throw new InvalidDataException("索引文件中的记录数量无效。");
                }

                var directories = new Dictionary<long, DirectoryRecord>(directoryCount);
                for (int directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
                {
                    long frn = reader.ReadInt64();
                    directories[frn] = new DirectoryRecord(reader.ReadInt64(), ReadString(reader));
                }

                int extensionCount = reader.ReadInt32();
                if (extensionCount is < 1 or > ushort.MaxValue + 1)
                {
                    throw new InvalidDataException("索引文件中的扩展名数量无效。");
                }

                var extensions = new string[extensionCount];
                for (int extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
                {
                    extensions[extensionIndex] = ReadString(reader);
                }

                int fileRecordCount = reader.ReadInt32();
                if (fileRecordCount < 0)
                {
                    throw new InvalidDataException("索引文件中的文件记录数无效。");
                }

                var files = new FileRecord[fileRecordCount];
                for (int fileIndex = 0; fileIndex < fileRecordCount; fileIndex++)
                {
                    files[fileIndex] = new FileRecord(reader.ReadUInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt16(), ReadString(reader));
                    if (files[fileIndex].ExtensionId >= extensions.Length)
                    {
                        throw new InvalidDataException("索引文件中的扩展名 ID 无效。");
                    }
                }

                volumes.Add(root, new VolumeIndex(root, scannedUtc, journalId, nextUsn, fileCount, directories, extensions, files));
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("索引文件包含无效尾部数据。");
            }

            _volumes = volumes;
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("索引文件已损坏或不完整。请删除后重新扫描。", exception);
        }
    }

    /// <summary>全量扫描并替换一个卷的索引。</summary>
    internal int RebuildVolumeIndex(string driveName, Action<int>? progress = null)
    {
        string root = NormalizeDrive(driveName);
        var directories = new Dictionary<long, DirectoryRecord>();
        var filesByKey = new Dictionary<FileRecordKey, FileRecord>();
        var extensionIds = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = 0 };
        var extensions = new List<string> { string.Empty };
        UsnJournalState journal;

        // 在扫描前设置检查点；扫描期间发生的变更会由首次 update 补上。
        using (var reader = new UsnJournalReader(root))
        {
            journal = reader.GetState();
        }

        using (var scanner = new MftScanner())
        {
            foreach (MftNodeRecord node in scanner.EnumerateNodes(root))
            {
                if (node.IsFile)
                {
                    FileRecord file = new(
                        CalculateFileNameHash(node.FileName),
                        node.Frn,
                        node.ParentFrn,
                        GetOrAddExtensionId(node.FileName, extensionIds, extensions),
                        node.FileName);
                    filesByKey[GetFileKey(file)] = file;
                    if (filesByKey.Count % 10_000 == 0)
                    {
                        progress?.Invoke(filesByKey.Count);
                    }
                }
                else
                {
                    directories[node.Frn] = new DirectoryRecord(node.ParentFrn, node.FileName);
                }
            }
        }

        VolumeIndex rebuilt = CreateVolumeIndex(root, DateTime.UtcNow, journal, directories, extensions, filesByKey.Values);
        SaveWithReplacement(root, rebuilt);
        return rebuilt.FileCount;
    }

    /// <summary>仅应用上次检查点之后的 NTFS USN Journal 记录。</summary>
    internal IncrementalUpdateResult UpdateVolumeIndex(string driveName, Action<int>? progress = null)
    {
        string root = NormalizeDrive(driveName);
        if (!_volumes.TryGetValue(root, out VolumeIndex? existing))
        {
            return IncrementalUpdateResult.FullScanRequired("该卷尚未建立索引，请先执行 scan。");
        }

        var directories = new Dictionary<long, DirectoryRecord>(existing.Directories);
        var filesByKey = existing.Files.ToDictionary(GetFileKey);
        var extensions = new List<string>(existing.Extensions);
        var extensionIds = extensions
            .Select((extension, id) => (extension, id))
            .ToDictionary(item => item.extension, item => (ushort)item.id, StringComparer.OrdinalIgnoreCase);
        var deletedDirectories = new HashSet<long>();
        int processedChanges = 0;
        UsnJournalReadResult journalResult;

        using (var reader = new UsnJournalReader(root))
        {
            UsnJournalState state = reader.GetState();
            if (state.JournalId != existing.JournalId)
            {
                return IncrementalUpdateResult.FullScanRequired("USN Journal 已重建，请执行 scan 重新建立该卷索引。");
            }

            if (existing.NextUsn < state.FirstUsn || existing.NextUsn < state.LowestValidUsn || existing.NextUsn > state.NextUsn)
            {
                return IncrementalUpdateResult.FullScanRequired("所需的 USN 变更记录已被清理，请执行 scan 重新建立该卷索引。");
            }

            journalResult = reader.ReadChanges(existing.NextUsn, change =>
            {
                ApplyChange(change, directories, filesByKey, extensionIds, extensions, deletedDirectories);
                processedChanges++;
                if (processedChanges % 10_000 == 0)
                {
                    progress?.Invoke(processedChanges);
                }
            });
        }

        if (journalResult.RequiresFullScan)
        {
            return IncrementalUpdateResult.FullScanRequired("USN Journal 在更新过程中发生变化，请执行 scan 重新建立该卷索引。");
        }

        RemoveDeletedDirectoryDescendants(directories, filesByKey, deletedDirectories);
        VolumeIndex updated = CreateVolumeIndex(root, DateTime.UtcNow, journalResult.Journal, directories, extensions, filesByKey.Values);
        SaveWithReplacement(root, updated);
        return new IncrementalUpdateResult(false, null, journalResult.RecordCount, updated.FileCount);
    }

    internal SearchPage SearchExactFileName(string fileName, int limit, int offset)
    {
        ulong hash = CalculateFileNameHash(fileName);
        return SearchNamePage(
            volume => GetFilesByHash(volume, hash).Select(file => new NameRecord(file.Frn, file.Name)),
            name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase),
            limit,
            offset);
    }

    internal SearchPage SearchExactDirectoryName(string directoryName, int limit, int offset) =>
        SearchNamePage(
            volume => volume.Directories.Select(pair => new NameRecord(pair.Key, pair.Value.Name)),
            name => string.Equals(name, directoryName, StringComparison.OrdinalIgnoreCase),
            limit,
            offset);

    internal SearchPage SearchFileNameContains(string query, int limit, int offset) =>
        SearchNamePage(
            volume => volume.Files.Select(file => new NameRecord(file.Frn, file.Name)),
            name => name.Contains(query, StringComparison.OrdinalIgnoreCase),
            limit,
            offset);

    internal SearchPage SearchDirectoryNameContains(string query, int limit, int offset) =>
        SearchNamePage(
            volume => volume.Directories.Select(pair => new NameRecord(pair.Key, pair.Value.Name)),
            name => name.Contains(query, StringComparison.OrdinalIgnoreCase),
            limit,
            offset);

    private SearchPage SearchNamePage(
        Func<VolumeIndex, IEnumerable<NameRecord>> candidatesForVolume,
        Func<string, bool> isMatch,
        int limit,
        int offset)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int matchedOffset = 0;

        foreach (VolumeIndex volume in _volumes.Values.OrderBy(volume => volume.Root, StringComparer.OrdinalIgnoreCase))
        {
            using var resolver = new NtfsFileIdPathResolver(volume.Root);
            foreach (NameRecord candidate in candidatesForVolume(volume))
            {
                if (!isMatch(candidate.Name))
                {
                    continue;
                }

                if (matchedOffset < offset)
                {
                    matchedOffset++;
                    continue;
                }

                if (paths.Count >= limit)
                {
                    return new SearchPage(paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), matchedOffset);
                }

                matchedOffset++;
                string? currentPath = resolver.TryGetCurrentPath(candidate.Frn);
                if (currentPath is not null &&
                    string.Equals(Path.GetFileName(currentPath), candidate.Name, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(currentPath);
                }
            }
        }

        return new SearchPage(paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), null);
    }

    private static IEnumerable<FileRecord> GetFilesByHash(VolumeIndex volume, ulong hash)
    {
        int index = LowerBound(volume.Files, hash);
        while (index < volume.Files.Length && volume.Files[index].NameHash == hash)
        {
            yield return volume.Files[index++];
        }
    }

    internal int CountByExtension(string extension)
    {
        string normalizedExtension = NormalizeExtension(extension);
        int total = 0;
        foreach (VolumeIndex volume in _volumes.Values)
        {
            int extensionId = Array.FindIndex(volume.Extensions, value =>
                string.Equals(value, normalizedExtension, StringComparison.OrdinalIgnoreCase));
            if (extensionId >= 0)
            {
                total += volume.Files.Count(file => file.ExtensionId == extensionId);
            }
        }

        return total;
    }

    internal IReadOnlyList<VolumeIndexInfo> GetVolumes() => _volumes.Values
        .OrderBy(volume => volume.Root, StringComparer.OrdinalIgnoreCase)
        .Select(volume => new VolumeIndexInfo(volume.Root, volume.ScannedUtc, volume.FileCount, volume.NextUsn))
        .ToArray();

    private static void ApplyChange(
        UsnChangeRecord change,
        Dictionary<long, DirectoryRecord> directories,
        Dictionary<FileRecordKey, FileRecord> filesByKey,
        Dictionary<string, ushort> extensionIds,
        List<string> extensions,
        ISet<long> deletedDirectories)
    {
        if ((change.Reason & UsnReasonFileDelete) != 0 ||
            (!change.IsDirectory && (change.Reason & UsnReasonRenameOldName) != 0))
        {
            if (change.IsDirectory)
            {
                deletedDirectories.Add(change.Frn);
            }
            else
            {
                filesByKey.Remove(new FileRecordKey(CalculateFileNameHash(change.FileName), change.Frn, change.ParentFrn));
            }

            return;
        }

        if (change.IsDirectory)
        {
            deletedDirectories.Remove(change.Frn);
            directories[change.Frn] = new DirectoryRecord(change.ParentFrn, change.FileName);
        }
        else
        {
            FileRecord file = new(
                CalculateFileNameHash(change.FileName),
                change.Frn,
                change.ParentFrn,
                GetOrAddExtensionId(change.FileName, extensionIds, extensions),
                change.FileName);
            filesByKey[GetFileKey(file)] = file;
        }
    }

    private static void RemoveDeletedDirectoryDescendants(
        Dictionary<long, DirectoryRecord> directories,
        Dictionary<FileRecordKey, FileRecord> filesByKey,
        ISet<long> deletedDirectories)
    {
        if (deletedDirectories.Count == 0)
        {
            return;
        }

        var removed = new HashSet<long>(deletedDirectories);
        bool changed;
        do
        {
            changed = false;
            foreach ((long frn, DirectoryRecord directory) in directories)
            {
                if (removed.Contains(directory.ParentFrn) && removed.Add(frn))
                {
                    changed = true;
                }
            }
        } while (changed);

        foreach (long directoryFrn in removed)
        {
            directories.Remove(directoryFrn);
        }

        foreach (FileRecordKey fileKey in filesByKey
                     .Where(pair => removed.Contains(pair.Value.ParentFrn))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            filesByKey.Remove(fileKey);
        }
    }

    private static VolumeIndex CreateVolumeIndex(
        string root,
        DateTime scannedUtc,
        UsnJournalState journal,
        Dictionary<long, DirectoryRecord> directories,
        IReadOnlyList<string> extensions,
        IEnumerable<FileRecord> files)
    {
        FileRecord[] orderedFiles = files.ToArray();
        Array.Sort(orderedFiles, static (left, right) =>
        {
            int comparison = left.NameHash.CompareTo(right.NameHash);
            return comparison != 0 ? comparison : left.Frn.CompareTo(right.Frn);
        });

        return new VolumeIndex(
            root,
            scannedUtc,
            journal.JournalId,
            journal.NextUsn,
            orderedFiles.Length,
            directories,
            extensions.ToArray(),
            orderedFiles);
    }

    private void SaveWithReplacement(string root, VolumeIndex replacement)
    {
        var updatedVolumes = new Dictionary<string, VolumeIndex>(_volumes, StringComparer.OrdinalIgnoreCase)
        {
            [root] = replacement
        };
        SaveAtomically(updatedVolumes);
        _volumes = updatedVolumes;
    }

    private void SaveAtomically(IReadOnlyDictionary<string, VolumeIndex> volumes)
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _databasePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(volumes.Count);

                foreach (VolumeIndex volume in volumes.Values.OrderBy(volume => volume.Root, StringComparer.OrdinalIgnoreCase))
                {
                    WriteString(writer, volume.Root);
                    writer.Write(volume.ScannedUtc.Ticks);
                    writer.Write(volume.JournalId);
                    writer.Write(volume.NextUsn);
                    writer.Write(volume.FileCount);
                    writer.Write(volume.Directories.Count);

                    foreach ((long frn, DirectoryRecord directoryRecord) in volume.Directories)
                    {
                        writer.Write(frn);
                        writer.Write(directoryRecord.ParentFrn);
                        WriteString(writer, directoryRecord.Name);
                    }

                    writer.Write(volume.Extensions.Length);
                    foreach (string extension in volume.Extensions)
                    {
                        WriteString(writer, extension);
                    }

                    writer.Write(volume.Files.Length);
                    foreach (FileRecord file in volume.Files)
                    {
                        writer.Write(file.NameHash);
                        writer.Write(file.Frn);
                        writer.Write(file.ParentFrn);
                        writer.Write(file.ExtensionId);
                        WriteString(writer, file.Name);
                    }
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _databasePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? BuildFullPath(VolumeIndex volume, long parentFrn, string requestedFileName)
    {
        var parts = new Stack<string>();
        var visitedFrns = new HashSet<long>();
        parts.Push(requestedFileName);

        long currentFrn = parentFrn;
        while (volume.Directories.TryGetValue(currentFrn, out DirectoryRecord? directory))
        {
            if (directory.ParentFrn == currentFrn)
            {
                break;
            }

            if (!visitedFrns.Add(currentFrn))
            {
                return null;
            }

            parts.Push(directory.Name);
            currentFrn = directory.ParentFrn;
        }

        return volume.Root + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static int LowerBound(FileRecord[] files, ulong hash)
    {
        int low = 0;
        int high = files.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (files[middle].NameHash < hash)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static FileRecordKey GetFileKey(FileRecord file) =>
        new(file.NameHash, file.Frn, file.ParentFrn);

    private static ushort GetOrAddExtensionId(
        string fileName,
        Dictionary<string, ushort> extensionIds,
        List<string> extensions)
    {
        string extension = NormalizeExtension(Path.GetExtension(fileName));
        if (extensionIds.TryGetValue(extension, out ushort id))
        {
            return id;
        }

        if (extensions.Count > ushort.MaxValue)
        {
            throw new InvalidDataException("卷内扩展名种类超出索引格式限制。");
        }

        id = (ushort)extensions.Count;
        extensions.Add(extension);
        extensionIds.Add(extension, id);
        return id;
    }

    private static string NormalizeExtension(string extension)
    {
        string trimmed = extension.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return (trimmed[0] == '.' ? trimmed : "." + trimmed).ToUpperInvariant();
    }

    private static ulong CalculateFileNameHash(string fileName)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        foreach (char character in fileName.ToUpperInvariant())
        {
            hash ^= (ulong)(character & 0xFF);
            hash *= prime;
            hash ^= (ulong)(character >> 8);
            hash *= prime;
        }

        return hash;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("文件名长度超出索引格式限制。");
        }

        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    internal static string NormalizeDrive(string driveName)
    {
        string trimmed = driveName.Trim().TrimEnd('\\', '/');
        if (trimmed.Length != 2 || trimmed[1] != ':')
        {
            throw new ArgumentException("驱动器必须类似 C: 或 C:\\。", nameof(driveName));
        }

        return trimmed.ToUpperInvariant();
    }

    private sealed record VolumeIndex(
        string Root,
        DateTime ScannedUtc,
        ulong JournalId,
        long NextUsn,
        int FileCount,
        Dictionary<long, DirectoryRecord> Directories,
        string[] Extensions,
        FileRecord[] Files);

    private sealed record DirectoryRecord(long ParentFrn, string Name);
    private readonly record struct NameRecord(long Frn, string Name);
    private readonly record struct FileRecord(ulong NameHash, long Frn, long ParentFrn, ushort ExtensionId, string Name);
    private readonly record struct FileRecordKey(ulong NameHash, long Frn, long ParentFrn);
}

internal sealed record VolumeIndexInfo(string Root, DateTime ScannedUtc, int FileCount, long NextUsn);
internal sealed record SearchPage(IReadOnlyList<string> Paths, int? NextOffset);
internal sealed record IncrementalUpdateResult(bool RequiresFullScan, string? Message, int ProcessedRecords, int FileCount)
{
    internal static IncrementalUpdateResult FullScanRequired(string message) => new(true, message, 0, 0);
}
