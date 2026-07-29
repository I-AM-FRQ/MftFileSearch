---
name: mft-file-search
Perform exact and substring local file and directory lookup on Windows NTFS volumes with the bundled MftFileSearch executable. Use for MFT indexing, USN Journal incremental updates, extension counts, and current-path confirmation.
license: MIT
compatibility: Windows 10 or later, local NTFS volumes, and an administrator token for scans, updates, and live NTFS path confirmation.
---

# MFT File Search

Use this skill to query local Windows files through the bundled Native AOT executable at `tools\MftFileSearch.exe`. It needs no .NET Runtime or additional DLLs and uses a compact `.mftdb` index, NTFS MFT records, and the USN Journal.

Run commands from the skill directory. In PowerShell, use `&` before the executable path:

```powershell
& .\tools\MftFileSearch.exe --help
```

By default, the index is stored beside the bundled EXE as `tools\file-index.mftdb`.

## Index lifecycle

### First use or recovery

A missing index, an incompatible `.mftdb` format, a rebuilt USN Journal, or a Journal history gap requires a full scan:

```powershell
& .\tools\MftFileSearch.exe scan all
```

Use `scan C:` or `scan D:` when only one volume needs rebuilding. A full scan may take time and normally requires an administrator token. Do not perform it merely for a normal search unless no usable index exists or the user explicitly asks to rebuild.

### Freshness update

When the user requests current results or says files have changed, synchronize the existing index first:

```powershell
& .\tools\MftFileSearch.exe update all
```

`update` reads only USN Journal changes and does not re-enumerate the whole MFT. If it reports that a full scan is required, report that condition and use `scan` only when appropriate.

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

This is a case-insensitive contains match for English names. It is not wildcard or regular-expression search. Results are paginated: the default page is 100 paths, and the command emits `NEXT_OFFSET=<number>` on standard error when another page exists. Repeat the same query with `--offset <number>` and the same `--limit` to continue. For Agent calls, request a small page first and continue only when needed. This command requires a `.mftdb v5` index, so an older index must be rebuilt with `scan` first.

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
- For a multi-page result, return the current page first. Continue only when needed, using the returned `NEXT_OFFSET` with the same query and page size.
- Do not use an arbitrarily large page size; a smaller page keeps Agent context focused.
- Do not infer file locations from an extension count.
- Preserve UTF-8 output; Windows paths can contain Chinese and other Unicode characters.
- Treat exit code `3` as an operational failure and surface its message. Exit code `2` means invalid usage.
