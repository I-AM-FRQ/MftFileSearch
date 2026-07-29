namespace MftFileSearch;

internal static class Program
{
    private const int Success = 0;
    private const int InvalidArguments = 2;
    private const int OperationFailed = 3;
    private const int DefaultSearchResults = 100;
    private const int MaxSearchResults = 1_000;

    private static int Main(string[] args)
    {
        // 索引名称以 UTF-8 写入；统一控制台编码，保证中文路径在 CMD/PowerShell 中正确显示。
        Console.InputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            return Run(args);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return OperationFailed;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? InvalidArguments : Success;
        }

        string databasePath = GetDefaultDatabasePath();
        int consumedOptions = ReadGlobalOptions(args, ref databasePath);
        if (consumedOptions < 0 || consumedOptions >= args.Length)
        {
            PrintHelp();
            return InvalidArguments;
        }

        string command = args[consumedOptions].ToLowerInvariant();
        string[] commandArguments = args[(consumedOptions + 1)..];
        var database = new FileIndexDatabase(databasePath);

        // A full scan replaces index data and must be able to upgrade an incompatible prior format.
        if (command != "scan")
        {
            database.Initialize();
        }

        return command switch
        {
            "scan" => Scan(database, commandArguments),
            "update" => Update(database, commandArguments),
            "count" => Count(database, commandArguments),
            "search" => Search(database, commandArguments),
            "search-part" => SearchPart(database, commandArguments),
            "search-dir" => SearchDirectory(database, commandArguments),
            "search-dir-part" => SearchDirectoryPart(database, commandArguments),
            "volumes" => PrintVolumes(database, commandArguments),
            "help" or "--help" or "-h" or "/?" => PrintHelpAndReturnSuccess(),
            _ => UnknownCommand(command)
        };
    }

    private static int ReadGlobalOptions(string[] args, ref string databasePath)
    {
        int index = 0;
        while (index < args.Length && args[index].StartsWith("--", StringComparison.Ordinal))
        {
            if (string.Equals(args[index], "--db", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    Console.Error.WriteLine("错误：--db 必须指定数据库文件路径。");
                    return -1;
                }

                databasePath = Path.GetFullPath(args[index + 1]);
                index += 2;
                continue;
            }

            break;
        }

        return index;
    }

    private static int Scan(FileIndexDatabase database, string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("用法：MftFileSearch scan <驱动器|all>");
            return InvalidArguments;
        }

        IEnumerable<DriveInfo> drives;
        if (string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase))
        {
            drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            string root = FileIndexDatabase.NormalizeDrive(args[0]);
            var drive = new DriveInfo(root + Path.DirectorySeparatorChar);
            if (!drive.IsReady)
            {
                Console.Error.WriteLine($"错误：驱动器 {root} 未就绪。");
                return OperationFailed;
            }

            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"错误：驱动器 {root} 的文件系统为 {drive.DriveFormat}，仅支持 NTFS。");
                return OperationFailed;
            }

            drives = [drive];
        }

        bool anyDrive = false;
        bool failed = false;
        foreach (DriveInfo drive in drives)
        {
            anyDrive = true;
            string root = FileIndexDatabase.NormalizeDrive(drive.Name);
            Console.Error.Write($"正在扫描 {root}，请稍候... ");

            try
            {
                int count = database.RebuildVolumeIndex(root, count =>
                {
                    Console.Error.Write($"\r正在扫描 {root}：已索引 {count:N0} 个文件...");
                });
                Console.Error.WriteLine($"\r已完成 {root}：索引 {count:N0} 个文件。                    ");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException)
            {
                failed = true;
                Console.Error.WriteLine($"\r扫描 {root} 失败：{exception.Message}                    ");
            }
        }

        if (!anyDrive)
        {
            Console.Error.WriteLine("没有找到可扫描的 NTFS 驱动器。");
            return OperationFailed;
        }

        return failed ? OperationFailed : Success;
    }

    private static int Update(FileIndexDatabase database, string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("用法：MftFileSearch update <驱动器|all>");
            return InvalidArguments;
        }

        IEnumerable<DriveInfo> drives;
        if (string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase))
        {
            drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            string root = FileIndexDatabase.NormalizeDrive(args[0]);
            var drive = new DriveInfo(root + Path.DirectorySeparatorChar);
            if (!drive.IsReady || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"错误：驱动器 {root} 未就绪或不是 NTFS。 ");
                return OperationFailed;
            }

            drives = [drive];
        }

        bool anyDrive = false;
        bool failed = false;
        foreach (DriveInfo drive in drives)
        {
            anyDrive = true;
            string root = FileIndexDatabase.NormalizeDrive(drive.Name);
            Console.Error.Write($"正在增量更新 {root}，请稍候... ");

            try
            {
                IncrementalUpdateResult result = database.UpdateVolumeIndex(root, count =>
                {
                    Console.Error.Write($"\r正在增量更新 {root}：已处理 {count:N0} 条变更...");
                });

                if (result.RequiresFullScan)
                {
                    failed = true;
                    Console.Error.WriteLine($"\r无法增量更新 {root}：{result.Message}                    ");
                    continue;
                }

                Console.Error.WriteLine($"\r已完成 {root}：处理 {result.ProcessedRecords:N0} 条变更，索引 {result.FileCount:N0} 个文件。                    ");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException)
            {
                failed = true;
                Console.Error.WriteLine($"\r更新 {root} 失败：{exception.Message}                    ");
            }
        }

        if (!anyDrive)
        {
            Console.Error.WriteLine("没有找到可更新的 NTFS 驱动器。");
            return OperationFailed;
        }

        return failed ? OperationFailed : Success;
    }

    private static int Count(FileIndexDatabase database, string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("用法：MftFileSearch count <扩展名>");
            return InvalidArguments;
        }

        Console.WriteLine(database.CountByExtension(args[0]));
        return Success;
    }

    private static int Search(FileIndexDatabase database, string[] args)
    {
        if (!TryReadSearchArguments(args, "search", out string query, out int limit, out int offset))
        {
            return InvalidArguments;
        }

        PrintSearchPage(database.SearchExactFileName(query, limit, offset));

        // 找不到文件并非命令错误，方便在批处理脚本中使用。
        return Success;
    }

    private static int SearchPart(FileIndexDatabase database, string[] args)
    {
        if (!TryReadSearchArguments(args, "search-part", out string query, out int limit, out int offset))
        {
            return InvalidArguments;
        }

        PrintSearchPage(database.SearchFileNameContains(query, limit, offset));

        return Success;
    }

    private static int SearchDirectory(FileIndexDatabase database, string[] args)
    {
        if (!TryReadSearchArguments(args, "search-dir", out string query, out int limit, out int offset))
        {
            return InvalidArguments;
        }

        PrintSearchPage(database.SearchExactDirectoryName(query, limit, offset));
        return Success;
    }

    private static int SearchDirectoryPart(FileIndexDatabase database, string[] args)
    {
        if (!TryReadSearchArguments(args, "search-dir-part", out string query, out int limit, out int offset))
        {
            return InvalidArguments;
        }

        PrintSearchPage(database.SearchDirectoryNameContains(query, limit, offset));

        return Success;
    }

    private static bool TryReadSearchArguments(string[] args, string command, out string query, out int limit, out int offset)
    {
        query = string.Empty;
        limit = DefaultSearchResults;
        offset = 0;
        if (args.Length is not 1 and not 3 and not 5 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine($"用法：MftFileSearch {command} <名称> [--limit <数量>] [--offset <偏移量>]");
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

            Console.Error.WriteLine($"错误：--limit 必须是 1 到 {MaxSearchResults:N0} 的整数，--offset 必须是非负整数。");
            return false;
        }

        query = args[0];
        return true;
    }

    private static void PrintSearchPage(SearchPage page)
    {
        foreach (string path in page.Paths)
        {
            Console.WriteLine(path);
        }

        if (page.NextOffset is int nextOffset)
        {
            Console.Error.WriteLine($"NEXT_OFFSET={nextOffset}");
        }
    }

    private static int PrintVolumes(FileIndexDatabase database, string[] args)
    {
        if (args.Length != 0)
        {
            Console.Error.WriteLine("用法：MftFileSearch volumes");
            return InvalidArguments;
        }

        foreach (VolumeIndexInfo volume in database.GetVolumes())
        {
            Console.WriteLine($"{volume.Root}\t{volume.FileCount}\t{volume.ScannedUtc:O}");
        }

        return Success;
    }

    private static bool IsHelp(string argument) =>
        argument is "help" or "--help" or "-h" or "/?";

    private static int PrintHelpAndReturnSuccess()
    {
        PrintHelp();
        return Success;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"错误：未知命令“{command}”。");
        PrintHelp();
        return InvalidArguments;
    }

    private static string GetDefaultDatabasePath() =>
        Path.Combine(AppContext.BaseDirectory, "file-index.mftdb");

    private static void PrintHelp()
    {
        Console.WriteLine("""
            MftFileSearch - NTFS MFT 文件与文件夹索引查询工具

            用法：
              MftFileSearch [--db <索引文件>] <命令> [参数]

            全局参数：
              --db <索引文件>  使用指定 .mftdb 文件；省略时使用 EXE 同目录的 file-index.mftdb。
              --help, -h, /?    显示本帮助。

            命令：
              scan <驱动器|all>
                  全量枚举指定 NTFS 卷的 MFT，并原子替换该卷索引。
                  all 表示所有已就绪的 NTFS 卷。首次使用、索引格式升级、或 USN Journal
                  不连续时必须执行此命令。通常需要管理员权限。

              update <驱动器|all>
                  从上次检查点读取 NTFS USN Journal，仅同步新增、删除、移动和重命名。
                  不重新枚举整张 MFT。若 Journal 被重建或历史记录被清理，会提示执行 scan。

              search <文件名> [--limit <数量>] [--offset <偏移量>]
                  按完整文件名精确查询，不区分英文大小写；默认每页 100 条，最大 1,000 条。
                  查询使用 FRN 向 NTFS 确认文件仍存在及当前路径，因此移动后不会返回旧路径。

              search-part <文件名片段> [--limit <数量>] [--offset <偏移量>]
                  按文件名包含片段查询，不区分英文大小写；默认每页 100 条，最大 1,000 条。

              search-dir <文件夹名> [--limit <数量>] [--offset <偏移量>]
                  按完整文件夹名称精确查询，不区分英文大小写；默认每页 100 条，最大 1,000 条。

              search-dir-part <文件夹名片段> [--limit <数量>] [--offset <偏移量>]
                  按文件夹名称包含片段查询，不区分英文大小写；默认每页 100 条，最大 1,000 条。

              count <扩展名>
                  统计索引中的唯一 NTFS 文件记录数。
                  不扫描目录树；新建、删除或改扩展名后应先执行 update。

              volumes
                  输出已索引卷的卷名、文件数和 UTC 索引时间，字段以制表符分隔。

            示例：
              MftFileSearch scan <驱动器|all>
              MftFileSearch update <驱动器|all>
              MftFileSearch search <完整文件名> --limit <数量> --offset <偏移量>
              MftFileSearch search-part <文件名片段> --limit <数量> --offset <偏移量>
              MftFileSearch search-dir <完整文件夹名> --limit <数量> --offset <偏移量>
              MftFileSearch search-dir-part <文件夹名片段> --limit <数量> --offset <偏移量>
              MftFileSearch count <扩展名>
              MftFileSearch --db <索引文件> update <驱动器|all>

            退出码：
              0  命令成功；search/search-dir 找不到结果也返回 0。
              2  命令或参数无效。
              3  索引、权限、卷或 NTFS/USN 操作失败。

            注意：
              - 仅支持本地、已就绪的 NTFS 卷。
              - 当前索引使用 .mftdb v5 格式；旧版本索引需重新执行 scan。
              - scan 或 update 写入时会先生成 .tmp 文件，成功后才替换旧索引。
            """);
    }
}
