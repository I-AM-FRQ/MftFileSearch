# MftFileSearch

[English](README.md) | [简体中文](README.zh-CN.md)

> **A millisecond-level Windows file search tool for local NTFS volumes, with a bundled Skill for Codex, Claude Code, pi, and other Agent Skills-compatible agents.**

MftFileSearch builds a compact local index from the NTFS Master File Table (MFT), then keeps it current through the NTFS USN Journal. It provides fast exact file-name and directory-name lookup, indexed extension counts, and a portable Native AOT executable with no .NET Runtime, database engine, or additional DLL dependency.

Search results are confirmed against the current NTFS File Reference Number (FRN), so same-volume moves, renames, and deletions do not produce stale indexed paths.

## Highlights

- **Fast exact indexed search** plus practical filename and directory-name substring search.
- **Persistent pure-memory service** for pi: it keeps a compact RAM-only MFT index current with per-volume USN Journal synchronization.
- **Cursor pagination** with `NEXT_CURSOR`, so later pages continue without rescanning earlier matches.
- **Compact persistent `.mftdb` index** stored beside the executable by default.
- **Incremental USN Journal updates** instead of repeatedly walking directory trees.
- **Unicode-safe console I/O**, including Chinese and other non-ASCII paths.
- **Native AOT, single executable**: no .NET Runtime, SQLite, or extra DLLs required.
- **Cross-agent Skill package** following the [Agent Skills](https://agentskills.io/specification) standard for Codex, Claude Code, pi, and other compatible Agent harnesses.

## Download

Download the current Windows x64 package from [Releases](https://github.com/I-AM-FRQ/MftFileSearch/releases):

- `MftFileSearch-win-x64.zip`: standalone executable package.
- `mft-file-search-skill-win-x64.zip`: complete Agent Skill package with `SKILL.md` and the bundled executable.

## Quick Start

Open PowerShell or Command Prompt as Administrator and run commands from the directory containing `MftFileSearch.exe`:

```powershell
.\MftFileSearch.exe scan <drive|all>
.\MftFileSearch.exe update <drive|all>
.\MftFileSearch.exe search <full-file-name>
.\MftFileSearch.exe search-part <file-name-fragment>
```

Run `scan` once to build the initial index. Afterwards, run `update` to synchronize changes before queries that must include recent file-system changes.

## Index Location

Without `--db`, the index is saved beside the executable:

```text
file-index.mftdb
```

To use a custom index, pass the same `--db` path to every related command:

```powershell
.\MftFileSearch.exe --db <index-file> scan <drive|all>
.\MftFileSearch.exe --db <index-file> update <drive|all>
.\MftFileSearch.exe --db <index-file> search <full-file-name>
```

## Commands

### `scan <drive|all>`

Fully enumerates the MFT of the selected local NTFS volume and atomically replaces that volume's index data.

```powershell
.\MftFileSearch.exe scan <drive>
.\MftFileSearch.exe scan all
```

Use a full scan for first use, after an index-format upgrade, when the USN Journal is reset or its required history has expired, or when a volume must be rebuilt deliberately. It normally requires Administrator privileges.

### `update <drive|all>`

Reads USN Journal records since the saved checkpoint and applies creates, deletes, moves, renames, and related changes without re-enumerating the full MFT.

```powershell
.\MftFileSearch.exe update <drive>
.\MftFileSearch.exe update all
```

If the command reports that a full scan is required, run `scan` for the affected volume.

### `search <file-name>`

Performs an exact file-name lookup. English names are case-insensitive. Each result is emitted as one current absolute path.

```powershell
.\MftFileSearch.exe search <full-file-name>
```

Provide only the base file name. Wildcards, substring search, and extension-only search are not supported by this exact command. No match produces no output and still exits with `0`.

### `search-part <file-name-fragment>`

Finds files whose base name contains the supplied text. English names are case-insensitive. The query checks indexed names and then confirms each returned path with NTFS.

```powershell
.\MftFileSearch.exe search-part <file-name-fragment>
```

This is a contains search, not a wildcard or regular-expression engine. Results are paginated: the default page contains 100 current paths and a page can contain up to 1,000. When more matches exist, the program writes `NEXT_OFFSET=<number>` to standard error. Use that value with the same query to request the next page:

```powershell
.\MftFileSearch.exe search-part <file-name-fragment> --limit <page-size> --offset <next-offset>
```

Use a longer fragment to reduce the number of pages.

### `search-dir <directory-name>`

Performs an exact final-directory-name lookup. Each result is emitted as one current absolute directory path.

```powershell
.\MftFileSearch.exe search-dir <full-directory-name>
```

Provide only the final directory name. Multiple matches are expected for common names.

### `search-dir-part <directory-name-fragment>`

Finds directories whose final name contains the supplied text.

```powershell
.\MftFileSearch.exe search-dir-part <directory-name-fragment>
```

Results use the same `--limit` and `--offset` pagination protocol as file-name substring search.

### `count <extension>`

Returns the number of unique indexed NTFS file records with the requested extension, without traversing directories.

```powershell
.\MftFileSearch.exe count <extension>
```

The extension may include or omit its leading dot. Hard links count once per NTFS file record, not once per path. Run `update` first when the count must include recent changes.

### Pagination

All four name-search commands support pagination:

```text
search <file-name> [--limit <page-size>] [--offset <offset>]
search-part <file-name-fragment> [--limit <page-size>] [--offset <offset>]
search-dir <directory-name> [--limit <page-size>] [--offset <offset>]
search-dir-part <directory-name-fragment> [--limit <page-size>] [--offset <offset>]
```

The default page size is `100`; the allowed range is `1` to `1,000`. The standalone CLI supports stateless `NEXT_OFFSET` pagination. The named-pipe service additionally returns an opaque `nextCursor`; send it back with the same command and query to continue without rebuilding the name-match candidate list. Service cursors expire after ten minutes and are invalidated by a service reload.

### `serve --pipe <pipe-name>`

Starts a local named-pipe query service and directly scans all ready local NTFS MFTs into a compact in-memory index. The service does not read or write `.mftdb`. After the initial scan, it polls each volume's NTFS USN Journal once per second and applies create, delete, move, and rename changes through a small in-memory overlay. If Journal history is unavailable or its ID changes, only the affected volume is rescanned. Large overlays are likewise compacted by rescanning that one volume. This is intended for integrations such as the bundled pi extension; normal command-line use does not need it. The pipe is restricted to the current Windows user and does not open a network port. The service uses fixed-size, case-folded trigram signatures and a small LRU query cache for fast substring search without a full inverted index. It accepts read-only name queries, `count`, `volumes`, and `reload` requests over its local pipe. `reload` rescans all ready NTFS MFTs and replaces the in-memory index. It exits after a `shutdown` request.

```powershell
.\MftFileSearch.exe serve --pipe <pipe-name>
```

The pi extension can start this service lazily on its first read query or with `/mft-service-start`. It stays running across pi restarts until `/mft-service-stop` is used. The first service start and every reload pay the full MFT scan cost; later queries reuse the RAM-only index and normally observe filesystem changes within about one polling interval. The standalone CLI `scan`/`update` commands remain available for users who want the persistent `.mftdb` workflow.

### `volumes`

Lists indexed volumes as tab-separated volume root, indexed file-record count, and UTC index timestamp.

```powershell
.\MftFileSearch.exe volumes
.\MftFileSearch.exe --db <index-file> volumes
```

## Help and Exit Codes

```powershell
.\MftFileSearch.exe --help
.\MftFileSearch.exe -h
.\MftFileSearch.exe /?
```

`--db <index-file>` must appear before the command:

```powershell
.\MftFileSearch.exe --db <index-file> update <drive|all>
```

| Code | Meaning |
| --- | --- |
| `0` | Command completed successfully. Empty `search` and `search-dir` results also use this code. |
| `2` | Invalid command or arguments. |
| `3` | Index, permissions, volume, NTFS, or USN Journal operation failed. |

## Agent Support

The repository includes a self-contained [Agent Skills](https://agentskills.io/specification) package for **Codex**, **Claude Code**, **pi**, and other agents that load `SKILL.md`-based Skills:

```text
skills/mft-file-search/
├── SKILL.md
└── tools/MftFileSearch.exe
```

The Skill gives an Agent a workflow for pure-memory MFT service control, exact and substring queries, paged result retrieval, extension counts, and current-path confirmation. The bundled Native AOT executable is ready to run without a separate download or a .NET installation.

Copy the complete `mft-file-search` directory, including `tools\MftFileSearch.exe`; copying only `SKILL.md` is not sufficient.

### Codex

Copy the directory to Codex's Skills directory. `CODEX_HOME` defaults to `~\.codex` when it is not set:

```powershell
$skillSource = "C:\path\to\mft-file-search"
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME ".codex" }
New-Item -ItemType Directory -Force -Path (Join-Path $codexHome "skills")
Copy-Item $skillSource (Join-Path $codexHome "skills\mft-file-search") -Recurse
```

### Claude Code

Copy the directory to the global Claude Code Skills directory:

```powershell
$skillSource = "C:\path\to\mft-file-search"
New-Item -ItemType Directory -Force -Path (Join-Path $HOME ".claude\skills")
Copy-Item $skillSource (Join-Path $HOME ".claude\skills\mft-file-search") -Recurse
```

### pi

Load the local Skill temporarily:

```powershell
pi --skill .\skills\mft-file-search
```

The repository `package.json` also declares `pi.skills`, so it can be installed as a pi package:

```powershell
pi install git:github.com/I-AM-FRQ/MftFileSearch
```

### Other compatible Agents

For a project-local shared Skill, copy the directory to `.agents\skills\mft-file-search`. Agents that support the Agent Skills layout can then discover it according to their own configuration:

```powershell
$skillSource = "C:\path\to\mft-file-search"
New-Item -ItemType Directory -Force -Path ".\.agents\skills"
Copy-Item $skillSource ".\.agents\skills\mft-file-search" -Recurse
```

The default index is `tools\file-index.mftdb`; use `--db` to select another location:

```powershell
& .\tools\MftFileSearch.exe --db <index-file> update <drive|all>
```

## Requirements and Limitations

- Windows 10 or later.
- Local, ready NTFS volumes only.
- `scan`, `update`, and live path confirmation normally require Administrator privileges.
- The current index format is `.mftdb v5`; older SQLite `.db` and `.mftdb v1/v2/v3/v4` indexes require a new `scan` before substring search is available.
- Exact name queries and substring name queries support pagination; wildcard, regular-expression, and path-fragment search are not supported.

## Build from Source

The build machine needs the .NET 8 SDK and Visual Studio C++ Build Tools. In **x64 Native Tools Command Prompt for Visual Studio**, run:

```bat
dotnet publish -c Release -r win-x64 -o .\publish-aot
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and verification guidance.

## License

This project is released under the [MIT License](LICENSE).
