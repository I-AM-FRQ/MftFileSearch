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
- Return a small first page. Continue only when the user needs more results.
- English names are case-insensitive; Chinese names can be searched directly.
- Results are current paths. Moved, renamed, and deleted items should not be returned at their old paths.
- Normal filesystem changes synchronize automatically. Do not reload for ordinary changes.
- Reload only when the user explicitly requests a full refresh, the service is unhealthy, or the service reports that it is needed.

## Service Controls

In pi, use:

```text
/mft-service-start
/mft-service-status
/mft-service-reload
/mft-service-stop
```

- `/mft-service-start`: starts the service and waits for the initial scan.
- `/mft-service-status`: checks whether it is running.
- `/mft-service-reload`: manually rescans all ready NTFS volumes; use only for an explicitly requested full refresh.
- `/mft-service-stop`: stops the service and frees its memory.

The initial scan takes time; later searches use the in-memory results.

## File Searches

### Complete file name

Use `mft_search_file`, for example with the complete name `example-document.txt`.

- `fileName`: complete base name including the extension.
- `limit`: optional; model tools default to 25 results and allow at most 50.

### Part of a file name

Use `mft_search_file_part`.

- `query`: a word or fragment from the file name.
- `limit`: optional; default 25, maximum 50.
- Longer fragments usually produce fewer and faster matches.

## Directory Searches

### Complete directory name

Use `mft_search_directory`.

- `directoryName`: final directory name only, not a complete path.

### Part of a directory name

Use `mft_search_directory_part`.

- `query`: a word or fragment from the directory name.

## Pagination

A result can include:

```text
NEXT_OFFSET=<number>
NEXT_CURSOR=<token>
```

To get another page, call `mft_search_next_page`:

- use the same query and query type as the preceding page;
- pass `nextOffset`;
- also pass `cursor` when `NEXT_CURSOR` is present;
- never invent or modify a cursor.

Cursors expire after about ten minutes and are invalidated by a service reload. Start again from the first page in either case.

## Extension Counts and Status

- Use `mft_count_extension` to count an extension such as `.txt` or `png`.
- Use `mft_index_status` to inspect scanned volumes, record counts, and scan time.

An extension count indicates quantity only. It does not identify file locations.

## Result Handling

- No results is a normal outcome; tell the user that no match was found.
- Preserve Unicode paths exactly, including Chinese characters.
- Do not treat a result as permanent; a later request should use the service's current result.
- Surface tool errors instead of guessing paths.
