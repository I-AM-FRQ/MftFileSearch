---
name: mft-file-search
description: Search local Windows NTFS files and directories through a pure-memory MFT service. Supports exact names, name fragments, pagination, extension counts, and automatic filesystem-change tracking.
license: MIT
compatibility: Windows 10 or later; local NTFS volumes; Administrator privileges are normally required.
---

# MFT File Search

Use this Skill to search local files and directories on the current Windows computer. The default workflow is a **pure-memory background service**: it scans ready NTFS volumes when it starts, then automatically follows creates, deletes, moves, and renames.

The service runs locally for the current user and does not expose a network port. Stopping it or restarting Windows causes a fresh scan the next time it starts.

Chinese version: [SKILL.md](SKILL.md).

## Rules

- Use this Skill's search tools when the user asks to locate a local file or directory.
- Use exact search when the complete name is known; otherwise use fragment search.
- Return a small first page. Continue only when the user needs more results, or when the current page has no usable paths but another page remains.
- English names are case-insensitive; Chinese names can be searched directly.
- Results are current paths. Moved, renamed, and deleted items should not be returned at their old paths.
- Normal filesystem changes synchronize automatically. Do not reload for ordinary changes.
- Reload only when the user explicitly requests a full refresh, the service is unhealthy, or the service reports that it is needed.

## Service Usage

The host should start the local service and send requests through a Windows named pipe:

```powershell
& .\tools\MftFileSearch.exe serve --pipe <pipe-name>
```

The initial scan takes time; later searches use the in-memory results. The host can expose its own start, stop, status, and full-reload controls.

- Start: scan ready NTFS volumes into memory.
- Status: confirm whether the service is running.
- Full reload: rescan all ready NTFS volumes; use only for an explicitly requested complete refresh.
- Stop: end the service and free its memory.

## File Searches

### Complete file name

Send a `search` request, for example with the complete name `example-document.txt`.

- The query is a complete base file name including its extension.
- Use `--limit` to control page size.

### Part of a file name

Send a `search-part` request.

- The query is a word or fragment from the file name.
- Longer fragments usually produce fewer and faster matches.

## Directory Searches

### Complete directory name

Send a `search-dir` request.

- The query is the final directory name only, not a complete path.

### Part of a directory name

Send a `search-dir-part` request.

- The query is a word or fragment from the directory name.

## Pagination

A result can include:

```text
NEXT_OFFSET=<number>
NEXT_CURSOR=<token>
```

To get another page, repeat the request with the same query type, query text, and page size:

- pass the preceding page's `nextOffset`;
- also pass `cursor` when `NEXT_CURSOR` is present;
- never invent or modify a cursor.

Cursors expire after about ten minutes and are invalidated by a full service reload. Start again from the first page in either case.

## Extension Counts and Status

- Send a `count` request to count an extension such as `.txt` or `png`.
- Send a `volumes` request to inspect scanned volumes, record counts, and scan time.

An extension count indicates quantity only. It does not identify file locations.

## Result Handling

- No results is a normal outcome; tell the user that no match was found.
- Preserve Unicode paths exactly, including Chinese characters.
- Do not treat a result as permanent; a later request should use the service's current result.
- Surface tool errors instead of guessing paths.
