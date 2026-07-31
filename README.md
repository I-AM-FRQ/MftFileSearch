# MftFileSearch

[English](README.md) | [简体中文](README.zh-CN.md)

> A local Windows NTFS file-search engine with a standalone Native AOT executable, a compact live-updating memory service, and bundled Agent Skills support.

MftFileSearch scans NTFS Master File Table (MFT) records and searches file or directory base names. Every returned path is live-confirmed from its NTFS File Reference Number (FRN), so a deleted, renamed, or moved item is not returned as a stale path.

The default experience is a pure-memory background service. It builds a compact RAM index at startup and continuously applies per-volume NTFS changes while it runs.

## What It Does

- Search local files and folders by full name or part of a name.
- Find Chinese and English names; English searches ignore letter case.
- Return the file or folder's current path instead of an old path left behind after a rename, move, or deletion.
- Show only a manageable page of results and continue from where the previous page stopped.
- Count files by extension, such as `.txt` or `.png`.
- Keep search results up to date while the service is running: new, renamed, moved, and deleted items usually appear within a few seconds without a manual refresh.
- Handle several searches at the same time without exposing a network port.
- Stay around a few hundred MB of memory even when indexing millions of files.
- Run as one Windows executable with no separate runtime or database installation.
- Work with Codex, Claude Code, pi, and compatible AI coding agents through the bundled Skill.

## Measured Results

The following measurements were taken on local `C:` and `D:` NTFS volumes with approximately **2.99 million indexed file records**. They are reference measurements, not latency guarantees; hardware, MFT size, cache warmth, filesystem activity, and administrator privileges affect results.

| Scenario | Result |
| --- | --- |
| Pure-memory service private memory after initial scan | **241.80 MB** |
| Pure-memory service working set after initial scan | **245.55 MB** |
| First search | **25.462 ms** server time |
| Same search again | **0.601 ms** server time |
| Chinese name search | **7.834 ms** server time |
| 64 concurrent mixed exact/file/directory substring requests | **64/64 succeeded**, **0 failures**, **59.99 ms** wall-clock |
| Same 64-request run, server median / P95 / max | **1.260 / 28.086 / 44.230 ms** |
| Live USN create / rename / delete visibility | about **2 s / 2 s / 1 s**, no reload |

The 64-request test used a warm service and mixed English, Chinese, exact, file-substring, and directory-substring searches. Repeating the same search is faster because the service remembers recent results. A first-time substring search remains complete and correct; it can be slower than a repeated search.

## Requirements

- Windows 10 or later.
- Ready local NTFS volumes only. FAT, exFAT, ReFS, network shares, and cloud-only placeholders are outside the supported scope.
- MFT enumeration, USN Journal access, and FRN path confirmation normally require an elevated Administrator process.
- The service is local to the current Windows user. It does not expose a network port.

## Quick Start

For pi, reload the extension and start the service:

```text
/reload
/mft-service-start
```

The service scans local NTFS volumes into memory once, then stays available for searches and automatically follows later filesystem changes.

To start it from PowerShell for another integration:

```powershell
.\MftFileSearch.exe serve --pipe mft-file-search-service
```

## Available Searches

The service supports these operations:

| Operation | Result |
| --- | --- |
| `search` | Find files with an exact name. |
| `search-part` | Find files whose name contains a word or fragment. |
| `search-dir` | Find folders with an exact name. |
| `search-dir-part` | Find folders whose name contains a word or fragment. |
| `count` | Count files with an extension. |
| `volumes` | Show the scanned volumes and record counts. |

Results are returned in pages. Use the returned `nextCursor` to continue from the previous page without starting over. Empty results are still successful.

## Pure-Memory Service

Start the service with a pipe name chosen by the client:

```powershell
.\MftFileSearch.exe serve --pipe mft-file-search-service
```

At startup, the service scans every ready local NTFS volume directly into compact RAM structures. It records each volume's USN Journal checkpoint before scanning, so changes occurring during the scan are collected by the next sync pass.

After startup, a background worker polls each volume about once per second:

1. Reads USN records after the last in-memory checkpoint.
2. Adds created or renamed records to a small overlay.
3. Marks deleted or superseded FRNs so obsolete baseline records are hidden.
4. Merges the baseline and overlay for all queries.
5. Rebuilds only one volume when its Journal history is unavailable, its Journal ID changes, or the overlay reaches its compaction threshold.

All search data and update state live only in memory. Stopping the service or restarting Windows starts a fresh scan next time.

### Service Protocol

The service reads one JSON request per named-pipe connection and writes one JSON response line.

Request:

```json
{"command":"search-part","args":["project-notes","--limit","25","--offset","0"],"cursor":null}
```

Response fields:

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

Supported service commands are `search`, `search-part`, `search-dir`, `search-dir-part`, `count`, `volumes`, `reload`, and `shutdown`.

- `nextCursor` is valid only in the same service process, for the same command and query.
- Cursors expire after ten minutes, are limited to 128 active cursors, and are invalidated by `reload`.
- A cursor continuation avoids rebuilding earlier name-match candidates.
- `reload` explicitly rescans all ready NTFS volumes and invalidates every cursor.

## pi Integration

The bundled pi extension exposes model tools and slash commands for exact/substring file and directory searches, pagination, extension counts, service status, start, stop, and reload.

Typical lifecycle:

```text
/reload
/mft-service-start
/mft-service-status
/mft-service-reload
/mft-service-stop
```

The service persists across pi reloads and session changes until it is stopped or Windows restarts. Normal filesystem changes do not require `/mft-service-reload`; live USN synchronization handles them.

## Agent Skill

The complete Skill package is included here:

```text
skills/mft-file-search/
├── SKILL.md
└── tools/MftFileSearch.exe
```

The bundled pi extension is the recommended integration. The Skill directory is included for Agent Skills-compatible hosts that need it.

## Exit Codes

| Code | Meaning |
| --- | --- |
| `0` | Successful command, including an empty exact search. |
| `2` | Invalid command or arguments. |
| `3` | Index, permission, volume, NTFS, or USN Journal operation failed. |

## Build From Source

The build machine needs the .NET 8 SDK and Visual Studio C++ Build Tools. Run from an x64 Native Tools Command Prompt for Visual Studio:

```bat
dotnet publish -c Release -r win-x64 -o .\publish-aot
```

Run the managed build during development:

```powershell
dotnet build -c Release
```

## License

[MIT](LICENSE)
