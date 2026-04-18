# MirrorGrab

A Windows download manager for repack mirror links (FitGirl, ElAmigos, DODI, etc.). Resolves direct links from supported hosts, downloads in parallel with multi-part HTTP, and optionally extracts archives on completion. Built as a faster alternative to slow torrents and the manual copy-link-into-IDM workflow.

## Features

- Batch jobs grouped under a chosen name (one folder per batch)
- Direct-link extraction for `fuckingfast.co` and `datanodes.to`; other URLs pass through unchanged
- 16-part parallel downloads where the host supports HTTP `Range`
- Configurable batch concurrency (default 8 files in flight)
- Pause, resume and cancel per file or per batch; resume survives app restarts
- Optional auto-extract for `.zip`, `.7z`, `.rar` (including `.partNN.rar` multipart)
- Single-instance enforcement, system tray, close-to-tray option
- Dark UI built on Avalonia 11

## Requirements

- Windows 10 or later (x64)
- .NET 8 SDK (only required to build from source)
- 7-Zip (optional) — used as a fallback extractor for archives SharpCompress cannot open

## Build

```cmd
git clone https://github.com/Gavri-dev/MirrorGrab.git
cd MirrorGrab
dotnet build -c Release
```

Run from source:

```cmd
dotnet run --project src/MirrorGrab -c Release
```

## Single-file release

```cmd
dotnet publish src/MirrorGrab -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The self-contained executable is written to:

```
src/MirrorGrab/bin/Release/net8.0/win-x64/publish/MirrorGrab.exe
```

No .NET runtime install is required on the target machine — the binary can be copied and run directly.

## Usage

1. Click **Add Batch**, paste a list of mirror URLs (or drop a `.txt` file), and assign the batch a name.
2. The app resolves direct links and downloads them concurrently into `<DownloadDir>/<BatchName>/`.
3. With auto-extract enabled in Settings, completed archives are unpacked in place; multi-part RARs are joined automatically.

Settings and batch state are stored under `%LocalAppData%\MirrorGrab\`.

## Supported hosts

Direct-link resolution is implemented for:

- `fuckingfast.co`
- `datanodes.to`

URLs from other hosts are treated as direct download links.

## License

MIT
