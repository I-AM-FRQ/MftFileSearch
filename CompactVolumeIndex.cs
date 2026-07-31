using System.Runtime.InteropServices;
using System.Text;

namespace MftFileSearch;

/// <summary>后台服务使用的紧凑只读卷索引：名称保存在单一 UTF-8 字节池，记录不持有托管字符串。</summary>
internal sealed class CompactVolumeIndex
{
    private readonly byte[] _namePool;
    private const int MaxCachedQueryEntries = 50_000;
    private const int MaxCachedQueries = 32;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private readonly CompactRecord[] _records;
    private readonly object _overlayLock = new();
    private readonly Dictionary<long, OverlayRecord> _overlayRecords = [];
    private readonly HashSet<long> _deletedFrns = [];
    private readonly object _queryCacheLock = new();
    private readonly Dictionary<QueryCacheKey, LinkedListNode<QueryCacheEntry>> _queryCache = [];
    private readonly LinkedList<QueryCacheEntry> _queryCacheLru = []; 

    private CompactVolumeIndex(
        string root,
        DateTime scannedUtc,
        byte[] namePool,
        CompactRecord[] records,
        int fileCount,
        UsnJournalState journal)
    {
        Root = root;
        ScannedUtc = scannedUtc;
        _namePool = namePool;
        _records = records;
        FileCount = fileCount;
        Journal = journal;
    }

    internal string Root { get; }
    internal DateTime ScannedUtc { get; }
    internal int FileCount { get; }
    internal UsnJournalState Journal { get; private set; }

    internal int RecordCount => _records.Length;

    internal static Builder CreateBuilder(string root) => new(root);

    internal sealed class Builder
    {
        private readonly string _root;
        private readonly Utf8NamePoolBuilder _pool = new();
        private readonly List<CompactRecord> _records = [];

        internal Builder(string root)
        {
            _root = root;
        }

        internal int RecordCount => _records.Count;

        internal void Append(ulong nameHash, long frn, long parentFrn, bool isFile, string name)
        {
            (int offset, int length) = _pool.Append(name);
            Signature128 signature = Signature128.FromUtf8(_pool.GetSpan(offset, length));
            _records.Add(new CompactRecord(nameHash, frn, offset, checked((ushort)length), isFile, signature));
        }

        internal CompactVolumeIndex Build(UsnJournalState journal)
        {
            CompactRecord[] sortedRecords = _records.ToArray();
            Array.Sort(sortedRecords, static (left, right) =>
            {
                int comparison = left.NameHash.CompareTo(right.NameHash);
                return comparison != 0 ? comparison : left.Frn.CompareTo(right.Frn);
            });
            return new CompactVolumeIndex(
                _root,
                DateTime.UtcNow,
                _pool.ToArray(),
                sortedRecords,
                sortedRecords.Count(record => record.IsFile),
                journal);
        }
    }

    internal IEnumerable<CompactSearchHit> FindExact(string query, bool isFile, ulong hash)
    {
        var pattern = new Utf8NamePattern(query);
        (HashSet<long> shadowedFrns, OverlayRecord[] overlay) = GetOverlaySnapshot();
        int index = LowerBound(hash);
        while (index < _records.Length && _records[index].NameHash == hash)
        {
            CompactRecord record = _records[index++];
            if (!shadowedFrns.Contains(record.Frn) &&
                record.IsFile == isFile &&
                pattern.Equals(_namePool.AsSpan(record.NameOffset, record.NameLength)))
            {
                yield return new CompactSearchHit(record.Frn, GetName(record));
            }
        }

        foreach (OverlayRecord record in overlay)
        {
            if (record.IsFile == isFile &&
                string.Equals(record.Name, query, StringComparison.OrdinalIgnoreCase))
            {
                yield return new CompactSearchHit(record.Frn, record.Name);
            }
        }
    }

    internal IEnumerable<CompactSearchHit> FindContains(string query, bool isFile)
    {
        var pattern = new Utf8NamePattern(query);
        (HashSet<long> shadowedFrns, OverlayRecord[] overlay) = GetOverlaySnapshot();
        int[] recordIndexes = GetOrBuildMatches(pattern, isFile);
        foreach (int recordIndex in recordIndexes)
        {
            CompactRecord record = _records[recordIndex];
            if (!shadowedFrns.Contains(record.Frn))
            {
                yield return new CompactSearchHit(record.Frn, GetName(record));
            }
        }

        foreach (OverlayRecord record in overlay)
        {
            if (record.IsFile == isFile && record.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                yield return new CompactSearchHit(record.Frn, record.Name);
            }
        }
    }

    private int[] GetOrBuildMatches(Utf8NamePattern pattern, bool isFile)
    {
        var key = new QueryCacheKey(isFile, pattern.CacheKey);
        lock (_queryCacheLock)
        {
            if (_queryCache.TryGetValue(key, out LinkedListNode<QueryCacheEntry>? cached))
            {
                _queryCacheLru.Remove(cached);
                _queryCacheLru.AddFirst(cached);
                return cached.Value.RecordIndexes;
            }
        }

        var matches = new List<int>();
        for (int recordIndex = 0; recordIndex < _records.Length; recordIndex++)
        {
            CompactRecord record = _records[recordIndex];
            if (record.IsFile == isFile &&
                (!pattern.HasSignature || record.Signature.Contains(pattern.Signature)) &&
                pattern.IsContainedBy(_namePool.AsSpan(record.NameOffset, record.NameLength)))
            {
                matches.Add(recordIndex);
            }
        }

        int[] recordIndexes = matches.ToArray();
        if (recordIndexes.Length > MaxCachedQueryEntries)
        {
            return recordIndexes;
        }

        lock (_queryCacheLock)
        {
            if (_queryCache.TryGetValue(key, out LinkedListNode<QueryCacheEntry>? concurrent))
            {
                return concurrent.Value.RecordIndexes;
            }

            while (_queryCache.Count >= MaxCachedQueries)
            {
                LinkedListNode<QueryCacheEntry>? oldest = _queryCacheLru.Last;
                if (oldest is null)
                {
                    break;
                }

                _queryCache.Remove(oldest.Value.Key);
                _queryCacheLru.RemoveLast();
            }

            var entry = new QueryCacheEntry(key, recordIndexes);
            LinkedListNode<QueryCacheEntry> node = _queryCacheLru.AddFirst(entry);
            _queryCache.Add(key, node);
            return recordIndexes;
        }
    }

    internal int CountByExtension(string extension)
    {
        var pattern = new Utf8NamePattern(extension);
        int count = 0;
        (HashSet<long> shadowedFrns, OverlayRecord[] overlay) = GetOverlaySnapshot();
        foreach (CompactRecord record in _records)
        {
            if (!shadowedFrns.Contains(record.Frn) && record.IsFile &&
                pattern.IsSuffixOf(_namePool.AsSpan(record.NameOffset, record.NameLength)))
            {
                count++;
            }
        }

        return count + overlay.Count(record => record.IsFile &&
            record.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    internal CompactUpdateResult ApplyUsnChanges(IReadOnlyList<UsnChangeRecord> changes, UsnJournalState journal)
    {
        lock (_overlayLock)
        {
            foreach (UsnChangeRecord change in changes)
            {
                bool deleted = (change.Reason & UsnReasonFileDelete) != 0 ||
                    (change.Reason & UsnReasonRenameOldName) != 0;
                if (deleted)
                {
                    _overlayRecords.Remove(change.Frn);
                    _deletedFrns.Add(change.Frn);
                    continue;
                }

                _deletedFrns.Add(change.Frn);
                _overlayRecords[change.Frn] = new OverlayRecord(
                    change.Frn,
                    !change.IsDirectory,
                    CalculateFileNameHash(change.FileName),
                    change.FileName);
            }

            Journal = journal;
            ClearQueryCache();
            return new CompactUpdateResult(_overlayRecords.Count + _deletedFrns.Count);
        }
    }

    private (HashSet<long> ShadowedFrns, OverlayRecord[] Overlay) GetOverlaySnapshot()
    {
        lock (_overlayLock)
        {
            var shadowed = new HashSet<long>(_deletedFrns);
            shadowed.UnionWith(_overlayRecords.Keys);
            return (shadowed, _overlayRecords.Values.ToArray());
        }
    }

    private void ClearQueryCache()
    {
        lock (_queryCacheLock)
        {
            _queryCache.Clear();
            _queryCacheLru.Clear();
        }
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

    private string GetName(CompactRecord record) => Encoding.UTF8.GetString(_namePool.AsSpan(record.NameOffset, record.NameLength));

    private int LowerBound(ulong hash)
    {
        int low = 0;
        int high = _records.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_records[middle].NameHash < hash)
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct CompactRecord
    {
        internal CompactRecord(ulong nameHash, long frn, int nameOffset, ushort nameLength, bool isFile, Signature128 signature)
        {
            NameHash = nameHash;
            Frn = frn;
            NameOffset = nameOffset;
            NameLength = nameLength;
            IsFile = isFile;
            Signature = signature;
        }

        internal ulong NameHash { get; }
        internal long Frn { get; }
        internal int NameOffset { get; }
        internal ushort NameLength { get; }
        internal bool IsFile { get; }
        internal Signature128 Signature { get; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct Signature128(ulong Low, ulong High)
    {
        internal static Signature128 FromUtf8(ReadOnlySpan<byte> value)
        {
            if (value.Length < 3)
            {
                return default;
            }

            ulong low = 0;
            ulong high = 0;
            for (int index = 0; index <= value.Length - 3; index++)
            {
                byte first = FoldAscii(value[index]);
                byte second = FoldAscii(value[index + 1]);
                byte third = FoldAscii(value[index + 2]);
                uint hash = (uint)(first * 31 + second * 131 + third * 521);
                int bit = (int)((hash ^ (hash >> 7) ^ (hash >> 13)) & 127);
                if (bit < 64)
                {
                    low |= 1UL << bit;
                }
                else
                {
                    high |= 1UL << (bit - 64);
                }
            }

            return new Signature128(low, high);
        }

        internal bool Contains(Signature128 other) =>
            (Low & other.Low) == other.Low && (High & other.High) == other.High;

        internal static byte[] FoldAscii(ReadOnlySpan<byte> value)
        {
            byte[] folded = value.ToArray();
            for (int index = 0; index < folded.Length; index++)
            {
                folded[index] = FoldAscii(folded[index]);
            }

            return folded;
        }

        private static byte FoldAscii(byte value) =>
            value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;
    }

    private readonly record struct QueryCacheKey(bool IsFile, string Query);
    private sealed record QueryCacheEntry(QueryCacheKey Key, int[] RecordIndexes);
    private sealed record OverlayRecord(long Frn, bool IsFile, ulong NameHash, string Name);

    private sealed class Utf8NamePattern
    {
        private readonly byte[] _bytes;
        private readonly string _value;
        private readonly bool _requiresUnicodeCaseComparison;

        internal Utf8NamePattern(string value)
        {
            _value = value;
            _bytes = Encoding.UTF8.GetBytes(value);
            _requiresUnicodeCaseComparison = value.Any(character => character > 0x7F &&
                char.ToUpperInvariant(character) != char.ToLowerInvariant(character));
            Signature = _requiresUnicodeCaseComparison ? default : Signature128.FromUtf8(_bytes);
            HasSignature = !_requiresUnicodeCaseComparison && _bytes.Length >= 3;
            CacheKey = _requiresUnicodeCaseComparison ? value.ToUpperInvariant() : Encoding.UTF8.GetString(Signature128.FoldAscii(_bytes));
        }

        internal Signature128 Signature { get; }
        internal bool HasSignature { get; }
        internal string CacheKey { get; }

        internal bool Equals(ReadOnlySpan<byte> value) =>
            _requiresUnicodeCaseComparison
                ? string.Equals(Encoding.UTF8.GetString(value), _value, StringComparison.OrdinalIgnoreCase)
                : value.Length == _bytes.Length && MatchesAt(value, 0);

        internal bool IsContainedBy(ReadOnlySpan<byte> value)
        {
            if (_requiresUnicodeCaseComparison)
            {
                return Encoding.UTF8.GetString(value).Contains(_value, StringComparison.OrdinalIgnoreCase);
            }

            for (int offset = 0; offset <= value.Length - _bytes.Length; offset++)
            {
                if (MatchesAt(value, offset))
                {
                    return true;
                }
            }

            return false;
        }

        internal bool IsSuffixOf(ReadOnlySpan<byte> value) =>
            _requiresUnicodeCaseComparison
                ? Encoding.UTF8.GetString(value).EndsWith(_value, StringComparison.OrdinalIgnoreCase)
                : value.Length >= _bytes.Length && MatchesAt(value, value.Length - _bytes.Length);

        private bool MatchesAt(ReadOnlySpan<byte> value, int offset)
        {
            for (int index = 0; index < _bytes.Length; index++)
            {
                byte expected = _bytes[index];
                byte actual = value[offset + index];
                if (expected is >= (byte)'a' and <= (byte)'z')
                {
                    expected -= 32;
                }

                if (actual is >= (byte)'a' and <= (byte)'z')
                {
                    actual -= 32;
                }

                if (actual != expected)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class Utf8NamePoolBuilder
    {
        private byte[] _buffer = new byte[1024 * 1024];
        private int _length;

        internal (int Offset, int Length) Append(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            EnsureCapacity(_length + byteCount);
            Encoding.UTF8.GetBytes(value.AsSpan(), _buffer.AsSpan(_length, byteCount));
            int offset = _length;
            _length += byteCount;
            return (offset, byteCount);
        }

        internal ReadOnlySpan<byte> GetSpan(int offset, int length) => _buffer.AsSpan(offset, length);

        internal byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length)
            {
                return;
            }

            int capacity = _buffer.Length;
            while (capacity < required)
            {
                capacity = checked(capacity * 2);
            }

            Array.Resize(ref _buffer, capacity);
        }
    }
}

internal readonly record struct CompactBuildRecord(ulong NameHash, long Frn, long ParentFrn, bool IsFile, string Name);
internal readonly record struct CompactSearchHit(long Frn, string Name);
