# Glowline

**Synced lyrics for Windows and Android.**

Glowline follows the track playing in your media player and shows time-synced
lyrics in a clean full-screen view. On Windows it uses the System Media
Transport Controls (SMTC) to detect playback; word-by-word highlighting is used
when the lyrics source provides it.

The current public release includes only the Windows executable. Installers and
the Android APK will be added later.

> The Windows executable keeps the internal name `LyricGlow.exe` for update
> compatibility.

## Features

- Full-screen lyrics with smooth verse transitions and word-level timing.
- Lyrics sources: Lrcmux, LyricsPlus and LRCLIB, with automatic fallback.
- Album-art palette, light and dark themes, and translucent surfaces.
- Animated artwork when the source provides it.
- Ten interface languages: Spanish, English, Portuguese, French, German,
  Italian, Japanese, Korean, Simplified Chinese and Russian.
- Import and export `.glow` design files.
- Automatic updates from this repository's GitHub Releases.

## Download

Download the latest `LyricGlow.exe` from
[Releases](https://github.com/FAASE200/Glowline/releases). The release build is
self-contained for Windows x64, so no .NET installation is required.

Windows may show a SmartScreen warning because the executable is not
code-signed.

## Requirements

- Windows 10 or Windows 11, 64-bit.
- A media player that exposes a Windows SMTC session.

## Build from source

```powershell
dotnet build LyricGlow/LyricGlow.csproj -c Release
```

To produce the self-contained Windows executable used for releases:

```powershell
dotnet publish LyricGlow/LyricGlow.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The Android project is included in the source tree, but its APK is not part of
the current release.

## Privacy

Glowline does not collect analytics, create accounts, or run its own servers.
To find lyrics and artwork, it sends the current track's title, artist, album
and duration to third-party lyrics and artwork providers. Sound analysis, when
enabled, runs locally. Settings and caches stay on your device. Automatic
update checks contact GitHub.

## License

Glowline is released under the [MIT License](LICENSE).

The Glowline name and logo are brand assets of FaS Reprinted and are not
covered by the MIT License. See [NOTICE](NOTICE) for third-party notices and
disclaimers.

## Disclaimer

Glowline is not affiliated with, endorsed by, or sponsored by Apple Inc. or any
other music service. Lyrics and artwork are provided by third-party services
and remain the property of their respective owners. Glowline does not host or
distribute lyrics or artwork; users are responsible for complying with the
terms of the services they use.
