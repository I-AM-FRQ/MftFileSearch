# MftFileSearch

[English](README.md) | [简体中文](README.zh-CN.md)

> **Windows 系统下的毫秒级本地文件搜索工具，支持 Codex、Claude Code、pi 等主流 Agent。**

MftFileSearch 面向本地 NTFS 卷，首次从 NTFS 主文件表（MFT）建立紧凑索引，之后通过 NTFS USN Journal 增量同步变更。它支持完整文件名和完整文件夹名的精确查询、扩展名统计，并提供无需 .NET Runtime、数据库引擎或额外 DLL 的 Native AOT 单文件程序。

查询结果会通过当前 NTFS 文件引用号（FRN）进行确认。因此同一卷内移动、重命名或删除文件/目录后，不会返回过期的索引路径。

## 特性

- **毫秒级精确索引查询**：支持完整文件名/文件夹名精确搜索，也支持实用的名称片段包含搜索。
- **无状态分页查询**：通过 `NEXT_OFFSET` 按需继续下一页，避免 Agent 一次加载过多路径。
- **紧凑持久化 `.mftdb` 索引**：默认存放在 EXE 同目录。
- **USN Journal 增量更新**：无需反复遍历整棵目录树。
- **Unicode 控制台输入输出**：正确处理中文及其他非 ASCII 路径。
- **Native AOT 单文件 EXE**：不需要安装 .NET Runtime、SQLite 或其他 DLL。
- **跨 Agent Skill 包**：符合 [Agent Skills](https://agentskills.io/specification) 标准，支持 Codex、Claude Code、pi 及其他兼容 Agent。

## 下载

请从 [Releases](https://github.com/I-AM-FRQ/MftFileSearch/releases) 下载当前 Windows x64 版本：

- `MftFileSearch-win-x64.zip`：独立 EXE 程序包。
- `mft-file-search-skill-win-x64.zip`：完整 Agent Skill 包，包含 `SKILL.md` 和内置 EXE。

## 快速开始

以管理员身份打开 PowerShell 或 CMD，进入 `MftFileSearch.exe` 所在目录：

```powershell
.\MftFileSearch.exe scan <驱动器|all>
.\MftFileSearch.exe update <驱动器|all>
.\MftFileSearch.exe search <完整文件名>
.\MftFileSearch.exe search-part <文件名片段>
```

首次使用先运行 `scan` 建立索引。之后，当查询需要包含近期文件变动时，先运行 `update` 同步增量变更。

## 索引文件位置

未指定 `--db` 时，索引文件默认写入 EXE 同目录：

```text
file-index.mftdb
```

如需使用自定义索引文件，所有相关命令都应传入相同的 `--db` 路径：

```powershell
.\MftFileSearch.exe --db <索引文件> scan <驱动器|all>
.\MftFileSearch.exe --db <索引文件> update <驱动器|all>
.\MftFileSearch.exe --db <索引文件> search <完整文件名>
```

## 命令参考

### `scan <驱动器|all>`

全量枚举指定本地 NTFS 卷的 MFT，并原子替换该卷的索引数据。

```powershell
.\MftFileSearch.exe scan <驱动器>
.\MftFileSearch.exe scan all
```

首次使用、索引格式升级、USN Journal 被重建或历史记录不足、以及主动要求重建某卷时，应执行全量扫描。该操作通常需要管理员权限。

### `update <驱动器|all>`

读取上次检查点后的 USN Journal 记录，增量同步创建、删除、移动、重命名及相关变更，不会重新遍历整张 MFT。

```powershell
.\MftFileSearch.exe update <驱动器>
.\MftFileSearch.exe update all
```

若程序提示必须全量扫描，请对受影响卷执行 `scan`。

### `search <文件名>`

按完整文件名精确查询；英文名称不区分大小写。每行输出一个当前真实的绝对路径。

```powershell
.\MftFileSearch.exe search <完整文件名>
```

只传入主文件名，不传完整路径。该精确查询命令不支持通配符、子串或仅扩展名查询。未命中时不输出内容，退出码仍为 `0`。

### `search-part <文件名片段>`

按文件名包含指定片段查询；英文名称不区分大小写。程序先匹配索引中的名称，再通过 NTFS 确认输出的当前真实路径。

```powershell
.\MftFileSearch.exe search-part <文件名片段>
```

这是包含查询，不是通配符或正则表达式查询。查询结果采用分页：默认每页输出 100 条当前路径，单页最多 1,000 条。存在后续结果时，程序会在标准错误输出 `NEXT_OFFSET=<数值>`。使用同一查询和该数值获取下一页：

```powershell
.\MftFileSearch.exe search-part <文件名片段> --limit <每页数量> --offset <下一页偏移量>
```

可使用更长的片段减少页数。

### `search-dir <文件夹名>`

按最后一级完整文件夹名称精确查询；每行输出一个当前真实的绝对目录路径。

```powershell
.\MftFileSearch.exe search-dir <完整文件夹名>
```

只传入最后一级目录名称，不传完整路径。常见目录名称可能匹配多条结果。

### `search-dir-part <文件夹名片段>`

按最后一级文件夹名称包含指定片段查询。

```powershell
.\MftFileSearch.exe search-dir-part <文件夹名片段>
```

与文件名片段查询一样，支持 `--limit` 和 `--offset` 分页协议。

### `count <扩展名>`

统计指定扩展名的唯一 NTFS 文件记录数，不遍历目录树。

```powershell
.\MftFileSearch.exe count <扩展名>
```

扩展名可带或不带前导点。硬链接按同一个 NTFS 文件记录只计一次，而不是每个路径计一次。若统计需要包括近期变动，请先执行 `update`。

### 分页查询

四种名称查询均支持分页：

```text
search <文件名> [--limit <每页数量>] [--offset <偏移量>]
search-part <文件名片段> [--limit <每页数量>] [--offset <偏移量>]
search-dir <文件夹名> [--limit <每页数量>] [--offset <偏移量>]
search-dir-part <文件夹名片段> [--limit <每页数量>] [--offset <偏移量>]
```

默认每页 `100` 条，允许范围为 `1` 到 `1,000`。使用上一页输出的 `NEXT_OFFSET`，并保持命令、查询文本和每页数量不变，即可继续下一页。分页读取当前索引；若文件系统必须是最新状态，请在多页查询开始前执行 `update`。

### `volumes`

显示已索引卷。输出字段使用制表符分隔，依次为卷根目录、索引文件记录数和 UTC 索引时间。

```powershell
.\MftFileSearch.exe volumes
.\MftFileSearch.exe --db <索引文件> volumes
```

## 帮助与退出码

```powershell
.\MftFileSearch.exe --help
.\MftFileSearch.exe -h
.\MftFileSearch.exe /?
```

`--db <索引文件>` 必须位于命令之前：

```powershell
.\MftFileSearch.exe --db <索引文件> update <驱动器|all>
```

| 退出码 | 含义 |
| --- | --- |
| `0` | 命令成功；`search` 和 `search-dir` 未命中也使用该退出码。 |
| `2` | 命令或参数无效。 |
| `3` | 索引、权限、卷、NTFS 或 USN Journal 操作失败。 |

## Agent 支持

仓库包含自带 EXE、符合 [Agent Skills](https://agentskills.io/specification) 标准的 Skill，支持 **Codex**、**Claude Code**、**pi** 以及其他加载 `SKILL.md` 的兼容 Agent：

```text
skills/mft-file-search/
├── SKILL.md
└── tools/MftFileSearch.exe
```

Skill 为 Agent 提供建立索引、增量同步、精确/片段查询、分页获取结果、扩展名统计和 USN Journal 恢复的标准工作流。内置 Native AOT EXE 可直接使用，无需单独下载或安装 .NET。

复制时必须保留完整的 `mft-file-search` 目录，包括 `tools\MftFileSearch.exe`；只复制 `SKILL.md` 无法运行。

### Codex

将完整目录复制到 Codex 的 Skills 目录。未设置 `CODEX_HOME` 时，默认使用 `~\.codex`：

```powershell
$skillSource = "C:\path\to\mft-file-search"
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME ".codex" }
New-Item -ItemType Directory -Force -Path (Join-Path $codexHome "skills")
Copy-Item $skillSource (Join-Path $codexHome "skills\mft-file-search") -Recurse
```

### Claude Code

将完整目录复制到 Claude Code 全局 Skills 目录：

```powershell
$skillSource = "C:\path\to\mft-file-search"
New-Item -ItemType Directory -Force -Path (Join-Path $HOME ".claude\skills")
Copy-Item $skillSource (Join-Path $HOME ".claude\skills\mft-file-search") -Recurse
```

### pi

在 pi 中可临时加载仓库内的 Skill：

```powershell
pi --skill .\skills\mft-file-search
```

根目录 `package.json` 也声明了 `pi.skills`，可作为 pi Package 安装：

```powershell
pi install git:github.com/I-AM-FRQ/MftFileSearch
```

### 其他兼容 Agent

若需要项目级共享，可将目录复制到 `.agents\skills\mft-file-search`。支持 Agent Skills 目录布局的 Agent 会根据各自配置发现它：

```powershell
$skillSource = "C:\path\to\mft-file-search"
New-Item -ItemType Directory -Force -Path ".\.agents\skills"
Copy-Item $skillSource ".\.agents\skills\mft-file-search" -Recurse
```

默认索引写入 `tools\file-index.mftdb`；如需其他位置，请传入 `--db`：

```powershell
& .\tools\MftFileSearch.exe --db <索引文件> update <驱动器|all>
```

## 要求与限制

- Windows 10 或更高版本。
- 仅支持本地、已就绪的 NTFS 卷。
- `scan`、`update` 和真实路径确认通常需要管理员权限。
- 当前索引格式为 `.mftdb v5`；旧 SQLite `.db` 及 `.mftdb v1/v2/v3/v4` 索引需要重新执行 `scan` 后才能使用名称片段搜索。
- 完整名称与名称片段查询均支持分页；不支持通配符、正则表达式或路径片段查询。

## 从源码构建

构建机需要 .NET 8 SDK 和 Visual Studio C++ Build Tools。在 **x64 Native Tools Command Prompt for Visual Studio** 中执行：

```bat
dotnet publish -c Release -r win-x64 -o .\publish-aot
```

开发和验证说明请参阅 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 许可证

本项目采用 [MIT License](LICENSE)。
