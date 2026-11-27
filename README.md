<p align="center">
  <img src="Glowline/Branding/pulse-logo-icon-256.png" width="96" alt="Glowline logo">
</p>

<h1 align="center">Glowline</h1>

<p align="center">
  <strong>Synced lyrics for Windows and Android.</strong><br>
  Word-by-word timing, animated album art and a clean full-screen view.
</p>

<p align="center">
  <a href="https://github.com/FAASE200/Glowline/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/FAASE200/Glowline?label=release"></a>
  <a href="https://github.com/FAASE200/Glowline/actions/workflows/build.yml"><img alt="Build" src="https://github.com/FAASE200/Glowline/actions/workflows/build.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
</p>

Glowline follows the track playing in your media player and shows time-synced
lyrics in a clean full-screen view. On Windows it uses the System Media
Transport Controls (SMTC) to detect playback, so it works with Spotify, Apple
Music, YouTube Music in the browser and any other player that shows up in the
Windows media overlay. Word-by-word highlighting is used when the lyrics source
provides it.

Releases include the Windows installer, a portable Windows build and an
experimental Android APK.

## Features

- Full-screen lyrics with smooth verse transitions and word-level timing.
- Lyrics sources: Lrcmux, LyricsPlus and LRCLIB, with automatic fallback.
- Album-art palette, light and dark themes, and translucent surfaces.
- Animated artwork when the source provides it.
- Ten interface languages: Spanish, English, Portuguese, French, German,
  Italian, Japanese, Korean, Simplified Chinese and Russian.
- Import and export `.glow` design files.
- **Floating mini lyrics**: a small always-on-top window with the current line.
- **Global shortcuts** that work while you play or work in other apps.
- **Share a line as an image** (cover, colors and the line) for TikTok or
  Instagram.
- **OBS overlay**: show the lyrics live in your stream or recordings.
- **Discord status** with the song you're listening to.
- **Per-line timing fixes** and **manual search** across all three sources.
- **Romanization** for Korean, Japanese (kana) and Cyrillic lyrics, worked out
  locally.
- Automatic updates from this repository's GitHub Releases, verified with
  SHA-256 before they are installed.

## Download

Download the latest `GlowlineSetup-<version>.exe` from
[Releases](https://github.com/FAASE200/Glowline/releases). The installer
includes the self-contained Windows x64 build, so no .NET installation is
required. A portable `.exe` that needs no installation is also attached.

Windows may show a SmartScreen warning because the installer is not
code-signed. Choose **More info → Run anyway** to continue.

The in-app updater downloads the new installer. Per-user installations apply it
on exit; per-machine installations open the installer for confirmation.

## Requirements

- Windows 10 or Windows 11, 64-bit.
- A media player that exposes a Windows SMTC session.

## Keyboard shortcuts

In the full-screen view:

| Key | Action |
| --- | --- |
| `Space` | Play / pause |
| `←` / `→` | Shift lyrics timing by −250 ms / +250 ms |
| `F11` | Toggle between full screen and window |
| `Esc` | Close the settings panel or leave full screen |

Clicking a lyric line seeks the player to that line. Right-clicking it lets you
share it as an image or nudge just that line earlier or later.

Anywhere in Windows (can be turned off in Settings → Extras):

| Keys | Action |
| --- | --- |
| `Ctrl+Alt+Shift+L` | Open Glowline |
| `Ctrl+Alt+Shift+M` | Show or hide the mini lyrics |
| `Ctrl+Alt+Shift+←` / `→` | Shift lyrics timing by −250 ms / +250 ms |
| `Ctrl+Alt+Shift+S` | Share the current line as an image |

## OBS overlay

Turn on **Settings → Extras → OBS overlay**, copy the link
(`http://localhost:7531/` by default) and add it in OBS as a **Browser** source.
The page has a transparent background and can be tuned with URL options:

| Option | Effect |
| --- | --- |
| `size=64` | Text size in pixels |
| `align=left` / `align=right` | Text alignment |
| `cover=0` | Hide the album art |
| `track=0` | Hide title and artist |
| `next=0` | Hide the next line |
| `box=1` | Dark rounded background behind the text |
| `theme=light` | Dark text for light scenes |

The overlay only accepts connections from your own computer.

## Troubleshooting

- **No lyrics appear.** Make sure the player shows up in the Windows media
  overlay (the volume flyout). Some browser tabs and players don't publish an
  SMTC session.
- **Lyrics are early or late.** Use `←` / `→` in full screen, or the timing
  options in settings, to adjust the offset globally or just for the current
  song.
- **Wrong lyrics.** Use the manual search to pick another result; Glowline
  remembers your choice for that song. Type `Artist - Title` to search Lrcmux
  and LyricsPlus for a different song.
- **One line is off.** Right-click it in full screen and nudge it earlier or
  later.
- **A global shortcut doesn't work.** Another app already uses it; Settings →
  Extras marks which ones are taken.
- **Reporting a bug.** Please attach the log file from
  `%LOCALAPPDATA%\Glowline\log.txt` (and `log.old.txt` if it exists) to your
  [issue](https://github.com/FAASE200/Glowline/issues/new/choose).

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
dotnet build Glowline/Glowline.csproj -c Release
```

Run the core tests (no extra packages needed; this also works on Linux and
macOS):

```powershell
dotnet run --project Glowline.Core.Tests -c Release
```

To produce the portable development build:

```powershell
dotnet publish Glowline/Glowline.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o Glowline/publish
```

To build the Windows installer:

```powershell
Glowline/tools/build-installers.ps1 -Version 0.9.40
```

The Android project is included in the source tree; its APK is published as
an experimental download.

### Project layout

| Folder | Contents |
| --- | --- |
| `Glowline.Core` | Platform-independent logic: lyrics sources, LRC parsing, caching, artwork lookup and the update client. |
| `Glowline` | Windows app (WPF): media session, full-screen view, settings and installer. |
| `Glowline.Android` | Android app (.NET MAUI), experimental. |
| `Glowline.Core.Tests` | Dependency-free tests for `Glowline.Core`. |

Every push and pull request is built and tested on Windows by
[GitHub Actions](.github/workflows/build.yml).

## Privacy

Glowline does not collect analytics, create accounts, or run its own servers.
The optional OBS overlay is a local web page served only to your own computer,
and the optional Discord status talks only to the Discord app on your
computer.
To find lyrics and artwork, it sends the current track's title, artist, album
and duration to third-party lyrics and artwork providers. Sound analysis, when
enabled, runs locally. Settings and caches stay on your device; old cached
lyrics are cleaned up automatically. Automatic update checks contact GitHub.

## License

Glowline is released under the [MIT License](LICENSE).

The Glowline name and logo are brand assets of fSS and are not
covered by the MIT License. See [NOTICE](NOTICE) for third-party notices and
disclaimers.

## Disclaimer

Glowline is not affiliated with, endorsed by, or sponsored by Apple Inc. or any
other music service. Lyrics and artwork are provided by third-party services
and remain the property of their respective owners. Glowline does not host or
distribute lyrics or artwork; users are responsible for complying with the
terms of the services they use.
