using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MftFileSearch;

internal sealed class MftQueryServer
{
    private const int DefaultSearchResults = 100;
    private const int MaxSearchResults = 1_000;
    private const int MaxActiveCursors = 128;
    private const int MaxConcurrentClients = 8;
    private static readonly TimeSpan JournalSyncInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CursorLifetime = TimeSpan.FromMinutes(10);
    private readonly string _pipeName;
    private readonly FileIndexDatabase _database;
    private readonly Dictionary<string, SearchCursor> _cursors = new(StringComparer.Ordinal);
    private readonly object _cursorLock = new();
    private readonly ReaderWriterLockSlim _indexLock = new();
    private string? _lastJournalSyncError;
    private CompactSyncResult _lastJournalSyncResult = CompactSyncResult.Empty;
    private long _journalSyncChangeTotal;
    private long _journalSyncUpdateTotal;
    private long _journalSyncRescanTotal;

    internal MftQueryServer(string pipeName, FileIndexDatabase database)
    {
        _pipeName = pipeName;
        _database = database;
    }

    internal int Run()
    {
        _database.RebuildAllVolumesCompactInMemory();

        using var shutdown = new CancellationTokenSource();
        Task journalSyncWorker = Task.Run(() => RunJournalSyncLoop(shutdown.Token));
        using var concurrency = new SemaphoreSlim(MaxConcurrentClients, MaxConcurrentClients);
        var workers = new List<Task>();
        while (!shutdown.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                pipe.WaitForConnectionAsync(shutdown.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }

            workers.Add(Task.Run(() => HandleConnection(pipe, concurrency, shutdown)));
            workers.RemoveAll(worker => worker.IsCompleted);
        }

        try
        {
            Task.WaitAll(workers.Append(journalSyncWorker).ToArray());
        }
        catch (AggregateException)
        {
            // Request workers turn operational errors into structured responses.
        }

        return Success;
    }

    private void RunJournalSyncLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Task.Delay(JournalSyncInterval, cancellationToken).GetAwaiter().GetResult();
                _indexLock.EnterWriteLock();
                try
                {
                    _lastJournalSyncResult = _database.SyncCompactMemoryIndex();
                    _journalSyncChangeTotal += _lastJournalSyncResult.ChangeRecords;
                    _journalSyncUpdateTotal += _lastJournalSyncResult.UpdatedVolumes;
                    _journalSyncRescanTotal += _lastJournalSyncResult.RescannedVolumes;
                    _lastJournalSyncError = null;
                }
                finally
                {
                    _indexLock.ExitWriteLock();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A transient volume or journal error must not take the search service offline.
                _lastJournalSyncError = exception.Message;
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream pipe, SemaphoreSlim concurrency, CancellationTokenSource shutdown)
    {
        using (pipe)
        try
        {
            concurrency.Wait(shutdown.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            string? requestLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            MftServerRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(requestLine, MftQueryJsonContext.Default.MftServerRequest);
            }
            catch (JsonException)
            {
                WriteResponse(writer, Failure(OperationFailed, "错误：服务请求格式无效。", stopwatch));
                return;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Command))
            {
                WriteResponse(writer, Failure(InvalidArguments, "错误：服务请求缺少命令。", stopwatch));
                return;
            }

            if (string.Equals(request.Command, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                WriteResponse(writer, SuccessResponse(stopwatch, shutdown: true));
                shutdown.Cancel();
                return;
            }

            try
            {
                MftServerResponse response = Execute(request);
                WriteResponse(writer, response with { ElapsedMs = GetElapsedMilliseconds(stopwatch) });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException)
            {
                WriteResponse(writer, Failure(OperationFailed, $"错误：{exception.Message}", stopwatch));
            }
        }
        catch (IOException)
        {
            // A caller may disconnect before writing a complete request.
        }
        finally
        {
            concurrency.Release();
        }
    }

    private MftServerResponse Execute(MftServerRequest request)
    {
        RemoveExpiredCursors();
        string command = request.Command.ToLowerInvariant();
        string[] args = request.Args ?? [];
        return command switch
        {
            "search" or "search-part" or "search-dir" or "search-dir-part" => Search(command, args, request.Cursor),
            "count" => ReadIndex(() => Count(args)),
            "volumes" => ReadIndex(() => Volumes(args)),
            "reload" => Reload(args),
            _ => Failure(InvalidArguments, $"错误：服务不支持命令“{request.Command}”。")
        };
    }

    private MftServerResponse Search(string command, string[] args, string? cursorToken)
    {
        if (!TryReadSearchArguments(args, out string query, out int limit, out int offset, out string? error))
        {
            return Failure(InvalidArguments, error!);
        }

        SearchCursor cursor;
        if (!string.IsNullOrWhiteSpace(cursorToken))
        {
            lock (_cursorLock)
            {
                if (!_cursors.TryGetValue(cursorToken, out cursor!) ||
                    !string.Equals(cursor.Command, command, StringComparison.Ordinal) ||
                    !string.Equals(cursor.Query, query, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(InvalidArguments, "分页游标无效或已过期。请从第一页重新查询。");
                }

                cursor.ExpiresUtc = DateTime.UtcNow + CursorLifetime;
            }
        }
        else
        {
            cursor = ReadIndex(() => new SearchCursor(command, query, _database.GetSearchCandidates(command, query), offset));
        }

        lock (cursor.Gate)
        {
            SearchCandidatePage page = _database.ResolveSearchCandidates(cursor.Candidates, cursor.Position, limit, cursor.SeenPaths);
            cursor.Position = page.NextIndex ?? cursor.Candidates.Count;
            if (page.NextIndex is null)
            {
                if (!string.IsNullOrWhiteSpace(cursorToken))
                {
                    lock (_cursorLock)
                    {
                        _cursors.Remove(cursorToken);
                    }
                }

                return SuccessResponse(paths: page.Paths.ToArray(), nextOffset: null, nextCursor: null);
            }

            string nextCursor = cursorToken ?? AddCursor(cursor);
            return SuccessResponse(paths: page.Paths.ToArray(), nextOffset: page.NextIndex, nextCursor: nextCursor);
        }
    }

    private MftServerResponse Count(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return Failure(InvalidArguments, "用法：MftFileSearch count <扩展名>");
        }

        return SuccessResponse(output: _database.CountByExtension(args[0]).ToString());
    }

    private MftServerResponse Volumes(string[] args)
    {
        if (args.Length != 0)
        {
            return Failure(InvalidArguments, "用法：MftFileSearch volumes");
        }

        string stdout = string.Join(
            Environment.NewLine,
            _database.GetVolumes().Select(volume => $"{volume.Root}\t{volume.FileCount}\t{volume.ScannedUtc:O}"));
        stdout += Environment.NewLine +
            $"USN_SYNC\tchanges={_lastJournalSyncResult.ChangeRecords}\tupdatedVolumes={_lastJournalSyncResult.UpdatedVolumes}\trescannedVolumes={_lastJournalSyncResult.RescannedVolumes}\ttotalChanges={_journalSyncChangeTotal}\ttotalUpdatedVolumes={_journalSyncUpdateTotal}\ttotalRescannedVolumes={_journalSyncRescanTotal}";
        if (!string.IsNullOrWhiteSpace(_lastJournalSyncError))
        {
            stdout += Environment.NewLine + $"USN_SYNC_ERROR\t{_lastJournalSyncError}";
        }

        return SuccessResponse(output: stdout);
    }

    private MftServerResponse Reload(string[] args)
    {
        if (args.Length != 0)
        {
            return Failure(InvalidArguments, "reload 不接受参数。");
        }

        _indexLock.EnterWriteLock();
        try
        {
            IReadOnlyList<VolumeIndexInfo> volumes = _database.RebuildAllVolumesCompactInMemory();
            lock (_cursorLock)
            {
                _cursors.Clear();
            }

            int fileCount = volumes.Sum(volume => volume.FileCount);
            return SuccessResponse(output: $"已重新扫描 {volumes.Count} 个 NTFS 卷并建立 {fileCount:N0} 条内存索引。");
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    private T ReadIndex<T>(Func<T> action)
    {
        _indexLock.EnterReadLock();
        try
        {
            return action();
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    private static bool TryReadSearchArguments(string[] args, out string query, out int limit, out int offset, out string? error)
    {
        query = string.Empty;
        limit = DefaultSearchResults;
        offset = 0;
        error = null;
        if (args.Length is not 1 and not 3 and not 5 || string.IsNullOrWhiteSpace(args[0]))
        {
            error = "用法：MftFileSearch <搜索命令> <名称> [--limit <数量>] [--offset <偏移量>]";
            return false;
        }

        bool hasLimit = false;
        bool hasOffset = false;
        for (int index = 1; index < args.Length; index += 2)
        {
            if (string.Equals(args[index], "--limit", StringComparison.OrdinalIgnoreCase) &&
                !hasLimit &&
                int.TryParse(args[index + 1], out int requestedLimit) &&
                requestedLimit is >= 1 and <= MaxSearchResults)
            {
                limit = requestedLimit;
                hasLimit = true;
                continue;
            }

            if (string.Equals(args[index], "--offset", StringComparison.OrdinalIgnoreCase) &&
                !hasOffset &&
                int.TryParse(args[index + 1], out int requestedOffset) &&
                requestedOffset >= 0)
            {
                offset = requestedOffset;
                hasOffset = true;
                continue;
            }

            error = $"错误：--limit 必须是 1 到 {MaxSearchResults:N0} 的整数，--offset 必须是非负整数。";
            return false;
        }

        query = args[0];
        return true;
    }

    private string AddCursor(SearchCursor cursor)
    {
        lock (_cursorLock)
        {
            if (_cursors.Count >= MaxActiveCursors)
            {
                string? oldestToken = _cursors.OrderBy(pair => pair.Value.ExpiresUtc).Select(pair => pair.Key).FirstOrDefault();
                if (oldestToken is not null)
                {
                    _cursors.Remove(oldestToken);
                }
            }

            string token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
            cursor.ExpiresUtc = DateTime.UtcNow + CursorLifetime;
            _cursors.Add(token, cursor);
            return token;
        }
    }

    private void RemoveExpiredCursors()
    {
        lock (_cursorLock)
        {
            DateTime now = DateTime.UtcNow;
            foreach (string token in _cursors.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
            {
                _cursors.Remove(token);
            }
        }
    }

    private static MftServerResponse SuccessResponse(
        Stopwatch? stopwatch = null,
        string[]? paths = null,
        int? nextOffset = null,
        string? nextCursor = null,
        string? output = null,
        bool shutdown = false) =>
        new(3, Success, "success", stopwatch is null ? 0 : GetElapsedMilliseconds(stopwatch), paths ?? [], nextOffset, nextCursor, output, null, shutdown);

    private static MftServerResponse Failure(int code, string error, Stopwatch? stopwatch = null) =>
        new(3, code, code == InvalidArguments ? "invalid_arguments" : "failed", stopwatch is null ? 0 : GetElapsedMilliseconds(stopwatch), [], null, null, null, error, false);

    private static double GetElapsedMilliseconds(Stopwatch stopwatch) => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);

    private static void WriteResponse(StreamWriter writer, MftServerResponse response)
    {
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(response, MftQueryJsonContext.Default.MftServerResponse));
        }
        catch (IOException)
        {
            // A caller may cancel after sending its request. Keep the in-memory service available for later callers.
        }
    }

    private const int Success = 0;
    private const int InvalidArguments = 2;
    private const int OperationFailed = 3;
}

internal sealed record MftServerRequest(string Command, string[]? Args, string? Cursor);

/// <summary>本地命名管道的一行 JSON 响应。搜索路径使用 paths 数组，分页使用 nextOffset。</summary>
internal sealed record MftServerResponse(
    int ProtocolVersion,
    int Code,
    string Status,
    double ElapsedMs,
    string[] Paths,
    int? NextOffset,
    string? NextCursor,
    string? Output,
    string? Error,
    bool Shutdown = false);

internal sealed class SearchCursor
{
    internal SearchCursor(string command, string query, IReadOnlyList<SearchCandidate> candidates, int position)
    {
        Command = command;
        Query = query;
        Candidates = candidates;
        Position = position;
        ExpiresUtc = DateTime.UtcNow + TimeSpan.FromMinutes(10);
    }

    internal string Command { get; }
    internal string Query { get; }
    internal IReadOnlyList<SearchCandidate> Candidates { get; }
    internal object Gate { get; } = new();
    internal HashSet<string> SeenPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal int Position { get; set; }
    internal DateTime ExpiresUtc { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MftServerRequest))]
[JsonSerializable(typeof(MftServerResponse))]
internal sealed partial class MftQueryJsonContext : JsonSerializerContext
{
}
