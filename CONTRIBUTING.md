# Contributing

## Development Setup

- Windows 10 or later on an NTFS volume.
- .NET 8 SDK.
- Visual Studio 2022 C++ Build Tools with the x64 toolchain for Native AOT publishing.
- Administrator token for MFT scans and USN Journal integration tests.

## Build

Run the standard build from the repository root:

```powershell
dotnet build -c Release
```

For the single-file Native AOT release, open **x64 Native Tools Command Prompt for Visual Studio** and run:

```bat
dotnet publish -c Release -r win-x64 -o .\publish-aot
```

## Verification

Use a disposable `.mftdb` index file while testing:

```powershell
.\publish-aot\MftFileSearch.exe --db <测试索引文件> scan <驱动器>
.\publish-aot\MftFileSearch.exe --db <测试索引文件> update <驱动器>
.\publish-aot\MftFileSearch.exe --db <测试索引文件> search <完整文件名>
.\publish-aot\MftFileSearch.exe --db <测试索引文件> search-dir <完整文件夹名>
.\publish-aot\MftFileSearch.exe --db <测试索引文件> count <扩展名>
```

Do not commit `.mftdb` files, generated `publish-*` directories, or build outputs. The only committed binary is `skills/mft-file-search/tools/MftFileSearch.exe`, which is the distributable executable bundled with the Agent Skill.

## Changes

- Keep the binary index format versioned. Any incompatible layout change must increment `FormatVersion` and document the required full reindex.
- Preserve the distinction between `scan` (full MFT build) and `update` (USN Journal incremental sync).
- Test file creation, rename, move, deletion, directory rename, and USN update behavior for changes that touch the index.
- Use focused commits with descriptive messages.
