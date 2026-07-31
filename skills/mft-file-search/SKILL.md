---
name: mft-file-search
description: 使用纯内存 MFT 服务，在 Windows 本地 NTFS 卷中搜索文件和文件夹。支持精确名称、名称片段、分页、扩展名统计和自动跟踪文件变化。
license: MIT
compatibility: Windows 10 或更高版本；本地 NTFS 卷；通常需要管理员权限。
---

# MFT 文件搜索

使用此 Skill 搜索当前 Windows 电脑上的本地文件和文件夹。默认工作方式是**纯内存后台服务**：服务启动时扫描可用的 NTFS 卷，之后自动跟踪新建、删除、移动和重命名等变化。

服务只在当前用户的本机中运行，不开放网络端口。停止服务或重启 Windows 后，再次启动会重新扫描。

英文版本见 [SKILL.en.md](SKILL.en.md)。

## 使用原则

- 用户要求搜索本地文件或文件夹时，优先使用本 Skill 提供的搜索工具。
- 已知完整文件名时使用精确搜索；只知道部分名称时使用片段搜索。
- 先返回一小页结果；只有用户需要更多结果时才继续下一页。
- 英文名称不区分大小写；中文名称可直接搜索。
- 搜索结果是当前真实路径。文件被移动、重命名或删除后，不应返回旧路径。
- 正常文件变化会自动同步，通常不需要重新扫描。
- 只有用户明确要求完整刷新、服务状态异常，或服务提示需要时，才使用重载操作。

## 服务控制

在 pi 中可使用：

```text
/mft-service-start
/mft-service-status
/mft-service-reload
/mft-service-stop
```

- `/mft-service-start`：启动服务并等待首次扫描完成。
- `/mft-service-status`：查看服务是否运行。
- `/mft-service-reload`：手动重新扫描全部可用 NTFS 卷；仅在用户明确要求完整刷新时使用。
- `/mft-service-stop`：停止服务并释放内存。

服务首次启动需要一些时间；后续搜索直接使用内存中的结果。

## 搜索文件

### 已知完整文件名

使用 `mft_search_file`，例如查询完整名称 `example-document.txt`。

- `fileName`：完整基础文件名，包含扩展名。
- `limit`：可选。模型工具默认返回 25 条，最多 50 条。

### 只知道文件名的一部分

使用 `mft_search_file_part`。

- `query`：文件名中的关键词或片段。
- `limit`：可选。模型工具默认 25 条，最多 50 条。
- 片段越长，结果通常越准确、越快。

## 搜索文件夹

### 已知完整文件夹名

使用 `mft_search_directory`。

- `directoryName`：最后一级文件夹名称，不传完整路径。

### 只知道文件夹名的一部分

使用 `mft_search_directory_part`。

- `query`：文件夹名称中的关键词或片段。

## 翻页

结果中可能包含：

```text
NEXT_OFFSET=<数字>
NEXT_CURSOR=<令牌>
```

需要更多结果时，调用 `mft_search_next_page`：

- 使用上一页的相同查询内容和查询类型；
- 传入 `nextOffset`；
- 如果有 `NEXT_CURSOR`，也传入 `cursor`；
- 不要自行修改或猜测 cursor。

游标大约 10 分钟后过期。服务重新扫描后游标也会失效；此时从第一页重新开始搜索。

## 扩展名统计和卷状态

- 使用 `mft_count_extension` 统计扩展名数量，例如 `.txt` 或 `png`。
- 使用 `mft_index_status` 查看已扫描卷、记录数和扫描时间。

扩展名统计只能说明数量，不能推断具体文件位置。

## 结果处理

- 搜索无结果属于正常情况，应明确告知用户未找到匹配项。
- 返回路径时保留原始 Unicode 文本，不要改写中文路径。
- 不要把搜索结果当作永久记录；下一次搜索应以服务的最新结果为准。
- 工具错误时说明错误信息；不要凭空猜测文件位置。
