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

## Download

1. Get `MirrorGrab-vX.Y.Z-win-x64.zip` from the [latest release](https://github.com/Gavri-dev/MirrorGrab/releases/latest).
2. Extract it anywhere. Keep the `resolver` folder next to `MirrorGrab.exe` — without it,
   `fuckingfast.co` links can't be resolved.
3. Run `MirrorGrab.exe`. No installer, and no .NET runtime needed.

The binaries are unsigned: if SmartScreen warns, choose **More info → Run anyway**. Each
release includes a `SHA256SUMS.txt` to verify the download.

## Requirements

- Windows 10 or later (x64)
- **Microsoft Edge WebView2 Runtime** — used to clear the Cloudflare Turnstile that
  `fuckingfast.co` now puts in front of its links. Ships with current Windows/Edge; if
  missing, install the Evergreen runtime from Microsoft.
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

## Self-contained release

```cmd
dotnet publish src/MirrorGrab -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

This writes the app and its `fuckingfast.co` resolver, each as a self-contained single file:

```
src/MirrorGrab/bin/Release/net8.0/win-x64/publish/
├── MirrorGrab.exe
└── resolver/
    └── FfResolver.exe
```

No .NET runtime install is required on the target machine. Ship `MirrorGrab.exe` together
with the `resolver` folder beside it: without it, `fuckingfast.co` links can't be resolved
(other hosts still work).

## Usage

1. Click **Add Batch**, paste a list of mirror URLs (or drop a `.txt` file), and assign the batch a name.
2. The app resolves direct links and downloads them concurrently into `<DownloadDir>/<BatchName>/`.
3. With auto-extract enabled in Settings, completed archives are unpacked in place; multi-part RARs are joined automatically.

Settings and batch state are stored under `%LocalAppData%\MirrorGrab\`.

## Supported hosts

Direct-link resolution is implemented for:

- `fuckingfast.co` — resolved through a headless WebView2 browser (see below), since the
  site moved its links behind a Cloudflare Turnstile captcha and an htmx request.
- `datanodes.to`

URLs from other hosts are treated as direct download links.

### fuckingfast.co and the captcha

`fuckingfast.co` no longer exposes a scrapeable direct link — the download is gated by a
Cloudflare Turnstile check and handed out only via a signed `dl.fuckingfast.co/dl/...` URL.
MirrorGrab resolves these with a bundled sidecar (`resolver/FfResolver.exe`, a hidden
WebView2 browser):

1. It loads each page invisibly and lets the managed/invisible Turnstile clear itself.
2. If Cloudflare escalates to an interactive checkbox (common when many links are resolved
   quickly from one IP), a small **"please complete the check"** window appears — tick it
   once. The resulting clearance cookie is cached, so the remaining links in the batch go
   back to resolving invisibly.

The signed link that comes back supports HTTP `Range`, so the normal multi-part download
engine handles it with no cookies or session sharing required.

## License

MIT
