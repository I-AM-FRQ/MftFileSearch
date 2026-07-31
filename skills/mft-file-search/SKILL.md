---
name: mft-file-search
Perform exact and substring local file and directory lookup on Windows NTFS volumes with the bundled MftFileSearch executable. Use the optional pure-memory MFT service for fast persistent queries, extension counts, and current-path confirmation.
license: MIT
compatibility: Windows 10 or later, local NTFS volumes, and an administrator token for scans, updates, and live NTFS path confirmation.
---

# MFT File Search

Use this skill to query local Windows files through the bundled Native AOT executable at `tools\MftFileSearch.exe`. It needs no .NET Runtime or additional DLLs and uses a compact `.mftdb` index, NTFS MFT records, and the USN Journal.

Run commands from the skill directory. In PowerShell, use `&` before the executable path:

```powershell
& .\tools\MftFileSearch.exe --help
```

For standalone CLI use, the optional persistent index is stored beside the bundled EXE as `tools\file-index.mftdb`. The pure-memory `serve` mode does not use that file.

## Pure-memory service lifecycle

For a persistent RAM-only query service, start `serve`. It directly scans all ready NTFS MFTs into compact service-process memory and does not read or write `.mftdb`. After the initial scan, it polls each volume's USN Journal about once per second and applies create, delete, move, and rename changes. If Journal history is unavailable, it rescans only the affected volume:

```powershell
& .\tools\MftFileSearch.exe serve --pipe <管道名称>
```

The service accepts local named-pipe requests from an integration such as the pi extension. It remains usable until it receives `shutdown` or Windows restarts. A service `reload` request performs another full MFT scan and atomically replaces its in-memory index.

A full MFT scan can take time and normally requires an administrator token. Normal filesystem changes are synchronized automatically; do not request a rescan for a normal search. Use `reload` only when the user explicitly wants a full refresh.

The standalone `scan` and `update` CLI commands remain available for the optional persistent `.mftdb` workflow, but do not apply to a pure-memory service.

## Queries

### Exact file-name search

Use the complete base filename, including its extension:

```powershell
& .\tools\MftFileSearch.exe search <完整文件名>
```

`search` is exact and case-insensitive for English names. It does not support wildcards, partial names, or extension-only search. The program confirms current paths using the NTFS file reference number, so paths remain accurate after same-volume moves and renames.

### File-name substring search

When the user knows only part of a file name, use `search-part`:

```powershell
& .\tools\MftFileSearch.exe search-part <文件名片段>
```

This is a case-insensitive contains match for English names. It is not wildcard or regular-expression search. The standalone CLI emits `NEXT_OFFSET=<number>` on standard error when another page exists. The named-pipe service additionally returns an opaque `nextCursor`; send it back with the same command and query to continue without rebuilding the match candidate list. In-memory fragment queries of three or more characters use a trigram inverted index; one- and two-character fragments fall back to a full in-memory scan. For Agent calls, request a small page first and continue only when needed.

### Exact folder-name search

Use only the last directory name:

```powershell
& .\tools\MftFileSearch.exe search-dir <完整文件夹名>
```

Multiple results are expected for common directory names. Do not claim a unique match unless exactly one result is returned.

### Directory-name substring search

When the user knows only part of a final directory name, use `search-dir-part`:

```powershell
& .\tools\MftFileSearch.exe search-dir-part <文件夹名片段>
```

This uses the same pagination protocol and `.mftdb v5` index requirement as file-name substring search.

### Extension counts

```powershell
& .\tools\MftFileSearch.exe count <扩展名>
```

The extension may include or omit its leading dot. The result counts unique NTFS file records, not hard-link paths. Run `update` first if the count must include recent changes.

### Inspect indexed volumes

```powershell
& .\tools\MftFileSearch.exe volumes
```

The output is tab-separated: volume root, indexed file-record count, and UTC index time.

## Custom index location

Without `--db`, the index lives next to the executable. Use the same explicit index path for all related commands when a custom location is required:

```powershell
& .\tools\MftFileSearch.exe --db <索引文件> update <驱动器|all>
& .\tools\MftFileSearch.exe --db <索引文件> search <完整文件名>
```

## Result handling

- Return every path emitted by `search` or `search-dir`.
- If an exact or substring search emits no path and exits with `0`, report that no indexed match was found.
- For a multi-page service result, return the current page first. Continue only when needed, using `nextOffset` and `nextCursor` with the same query and page size. Service cursors expire after ten minutes or after a service reload.
- Do not use an arbitrarily large page size; a smaller page keeps Agent context focused.
- Do not infer file locations from an extension count.
- Preserve UTF-8 output; Windows paths can contain Chinese and other Unicode characters.
- Treat exit code `3` as an operational failure and surface its message. Exit code `2` means invalid usage.
