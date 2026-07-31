# MftFileSearch

[English](README.md) | [简体中文](README.zh-CN.md)

> 面向本地 Windows NTFS 卷的文件搜索引擎：单文件 Native AOT EXE、紧凑实时内存服务，以及内置 Agent Skill。

MftFileSearch 扫描 NTFS 主文件表（MFT），按文件或目录的基础名称进行搜索。每一条返回路径都会通过其当前 NTFS 文件引用号（FRN）实时确认，因此已删除、重命名或移动的项目不会以过期路径返回。

默认使用纯内存后台服务：启动时建立紧凑 RAM 索引，运行期间按卷持续应用 NTFS 文件变化。

## 能做什么

- 按完整名称或名称片段搜索本地文件和文件夹。
- 支持中文和英文名称；英文搜索不区分大小写。
- 文件或文件夹被重命名、移动、删除后，不返回旧路径，而是返回当前真实路径。
- 一次只返回适量结果，可从上一页结束处继续查看。
- 统计某个扩展名的文件数量，例如 `.txt`、`.png`。
- 服务运行时自动跟踪文件变化：新建、重命名、移动和删除通常几秒内可搜索到，无需手动刷新。
- 能同时处理多条搜索，不对网络开放端口。
- 即使索引数百万文件，内存通常也只占数百 MB。
- 一个 Windows EXE 即可运行，不需要额外安装运行库或数据库。
- 通过内置 Skill 支持 Codex、Claude Code、pi 和其他兼容 AI 编程助手。

## 实测数据

以下数据来自本地 `C:` 与 `D:` NTFS 卷，约 **299 万条索引文件记录**。它们是参考值而非性能承诺；硬件、MFT 规模、缓存热度、文件系统活动和管理员权限都会影响结果。

| 场景 | 结果 |
| --- | --- |
| 纯内存服务初始扫描后私有内存 | **241.80 MB** |
| 纯内存服务初始扫描后工作集 | **245.55 MB** |
| 首次搜索 | 服务端 **25.462 ms** |
| 再次搜索同一个名称 | 服务端 **0.601 ms** |
| 搜索中文名称 | 服务端 **7.834 ms** |
| 64 条并发混合精确/文件片段/目录片段请求 | **64/64 成功**，**0 失败**，墙钟 **59.99 ms** |
| 同一 64 请求测试的服务端中位数 / P95 / 最大值 | **1.260 / 28.086 / 44.230 ms** |
| USN 实时同步的创建 / 重命名 / 删除可见时间 | 约 **2 秒 / 2 秒 / 1 秒**，无需 reload |

64 请求测试在服务已经预热的状态下进行，混合英文、中文、精确文件名、文件片段和目录片段查询。再次搜索同一个名称会更快，因为服务会记住最近的结果。第一次搜索片段时仍会返回完整正确的结果，只是可能比再次搜索慢一些。

## 环境要求

- Windows 10 或更高版本。
- 仅支持本地、已就绪 NTFS 卷；FAT、exFAT、ReFS、网络共享和仅云端占位文件不在支持范围内。
- MFT 枚举、USN Journal 访问和 FRN 路径确认通常需要以管理员身份运行。
- 服务仅限当前 Windows 用户本地访问，不暴露网络端口。

## 快速开始

在 pi 中重新加载扩展并启动服务：

```text
/reload
/mft-service-start
```

服务会先把本地 NTFS 卷扫描到内存中，之后可持续接受搜索，并自动跟踪后续文件变化。

如需从 PowerShell 供其他集成使用：

```powershell
.\MftFileSearch.exe serve --pipe mft-file-search-service
```

## 可用搜索

服务支持以下操作：

| 操作 | 效果 |
| --- | --- |
| `search` | 按完整名称查找文件。 |
| `search-part` | 按名称中的关键词或片段查找文件。 |
| `search-dir` | 按完整名称查找文件夹。 |
| `search-dir-part` | 按名称中的关键词或片段查找文件夹。 |
| `count` | 统计某个扩展名的文件数量。 |
| `volumes` | 查看已扫描卷和记录数量。 |

结果按页返回。使用返回的 `nextCursor` 可从上一页结束的位置继续，无需从头搜索。没有结果也属于成功操作。

## 纯内存服务

以客户端指定的管道名称启动服务：

```powershell
.\MftFileSearch.exe serve --pipe mft-file-search-service
```

启动时服务直接把所有已就绪本地 NTFS 卷扫描为紧凑内存结构。在扫描开始前，它会记录每个卷的 USN Journal 检查点，因此扫描期间发生的变化会由下一轮同步收集。

初始扫描后，后台线程约每秒对每个卷执行一次：

1. 读取上一次内存检查点之后的 USN 记录。
2. 将新增或改名后的记录写入小型覆盖层。
3. 标记删除或已被新记录替代的 FRN，隐藏旧基线记录。
4. 每次查询自动合并基线和覆盖层。
5. 当 Journal 历史不可用、Journal ID 改变或覆盖层达到压实阈值时，只重建对应的一个卷。

所有搜索数据和更新状态都只保存在内存中。停止服务或 Windows 重启后，下次启动会重新扫描 MFT。

### 服务协议

服务每次命名管道连接读取一行 JSON 请求，并写回一行 JSON 响应。

请求：

```json
{"command":"search-part","args":["project-notes","--limit","25","--offset","0"],"cursor":null}
```

响应字段：

```json
{
  "protocolVersion": 3,
  "code": 0,
  "status": "success",
  "elapsedMs": 1.26,
  "paths": ["C:\\Example\\file.txt"],
  "nextOffset": 25,
  "nextCursor": "opaque-token",
  "output": null,
  "error": null,
  "shutdown": false
}
```

服务支持：`search`、`search-part`、`search-dir`、`search-dir-part`、`count`、`volumes`、`reload` 与 `shutdown`。

- `nextCursor` 只在同一个服务进程、相同命令和查询中有效。
- 游标 10 分钟后过期，最多同时保留 128 个；`reload` 会让所有游标失效。
- 使用游标继续下一页不会重建前面已经计算过的名称候选。
- `reload` 会显式重新扫描所有已就绪 NTFS 卷并使全部游标失效。

## pi 集成

内置 pi 扩展向模型提供文件/目录精确和片段搜索、下一页、扩展名统计、服务状态、启动、停止及重载工具，并提供对应 Slash Commands。

典型生命周期：

```text
/reload
/mft-service-start
/mft-service-status
/mft-service-reload
/mft-service-stop
```

服务会跨 pi `/reload` 和会话切换保持运行，直到手动停止或 Windows 重启。正常文件系统变化不需要 `/mft-service-reload`，由 USN 实时同步处理。

## Agent Skill

完整 Skill 包在：

```text
skills/mft-file-search/
├── SKILL.md
└── tools/MftFileSearch.exe
```

推荐直接使用内置 pi 扩展。保留 Skill 目录是为了兼容需要 Agent Skills 的其他宿主。

## 退出码

| 代码 | 含义 |
| --- | --- |
| `0` | 命令成功，包括精确搜索无结果。 |
| `2` | 命令或参数无效。 |
| `3` | 索引、权限、卷、NTFS 或 USN Journal 操作失败。 |

## 从源码构建

构建机需要 .NET 8 SDK 和 Visual Studio C++ Build Tools。在 Visual Studio 的 x64 Native Tools Command Prompt 中执行：

```bat
dotnet publish -c Release -r win-x64 -o .\publish-aot
```

开发时运行托管构建：

```powershell
dotnet build -c Release
```

## 许可证

[MIT](LICENSE)
