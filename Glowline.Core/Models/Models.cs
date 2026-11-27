using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Glowline.Localization;

namespace Glowline.Models;

public sealed class TrackInfo
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public double? DurationSeconds { get; init; }
    public string SourceAppId { get; init; } = "";

    string? _key;

    // Computed once: the full-screen view reads it every frame (per-track
    // offset) and normalizing it with a regex is not free.
    public string Key => _key ??= TrackKey.Make(Artist, Title, DurationSeconds);

    public string DisplayName => Artist.Length > 0 ? $"{Artist} — {Title}" : Title;
}

public static class TrackKey
{
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Make(string artist, string title, double? durationSeconds)
    {
        var d = durationSeconds is > 0 and < 86400
            ? ((int)Math.Round(durationSeconds.Value)).ToString()
            : "0";
        return $"{Normalize(artist)}|{Normalize(title)}|{d}";
    }

    public static string Normalize(string? s)
    {
        var text = s ?? "";
        if (text.Length > 1024) text = text[..1024];
        return Whitespace.Replace(text.Trim().ToLowerInvariant(), " ");
    }
}

public sealed class LrcLine
{
    public double Time { get; init; }
    public string Text { get; init; } = "";
    public string Background { get; init; } = "";
    public IReadOnlyList<LrcWord>? Words { get; init; }
    public IReadOnlyList<LrcWord>? BackgroundWords { get; init; }
    /// <summary>Pronunciation in Latin letters (empty when not applicable).</summary>
    public string Romanized { get; init; } = "";
    /// <summary>Original time from the source, before per-line adjustments.</summary>
    public double SourceTime { get; init; } = double.NaN;
}

public sealed class LrcWord
{
    public double Time { get; init; }
    public string Text { get; init; } = "";
}

public sealed class LrclibTrack
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("trackName")] public string TrackName { get; set; } = "";
    [JsonPropertyName("artistName")] public string ArtistName { get; set; } = "";
    [JsonPropertyName("albumName")] public string AlbumName { get; set; } = "";
    [JsonPropertyName("duration")] public double? Duration { get; set; }
    [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
    [JsonPropertyName("hasWordSync")] public bool HasWordSync { get; set; }
    [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
    [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    [JsonPropertyName("lyricsfile")] public string? LyricsFile { get; set; }
    public string Source { get; set; } = "LRCLIB";
    /// <summary>Service that provided the lyrics (LRCLIB, Lrcmux or LyricsPlus).</summary>
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";

    [JsonIgnore] public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);
    [JsonIgnore] public bool HasPlain => !string.IsNullOrWhiteSpace(PlainLyrics);
}

public enum LyricSource
{
    Lrcmux,
    LyricsPlus,
    Lrclib,
}

public enum InterfaceTheme
{
    Light,
    Dark,
}

public enum WordAnimationStyle
{
    Fade,
    Slide,
    Zoom,
    Bounce,
    Rise,
    Blur,
    Elastic,
    Pulse,
    Wave,
}

public sealed class AppSettings
{
    public double FontScale { get; set; } = 1.0;
    public int GlobalOffsetMs { get; set; }
    public Dictionary<string, int> PerTrackOffsetMs { get; set; } = new();
    public Dictionary<string, long> ManualOverrides { get; set; } = new();
    public string? PreferredSourceAppId { get; set; }
    public bool SoftwareRendering { get; set; }
    public bool CoverPaletteEnabled { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InterfaceTheme InterfaceTheme { get; set; } = InterfaceTheme.Dark;
    public bool InterfaceGlassEnabled { get; set; } = true;
    public bool SoundSenseEnabled { get; set; } = true;
    public double SoundSenseIntensity { get; set; } = 0.7;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LyricSource LyricsSource { get; set; } = LyricSource.Lrcmux;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WordAnimationStyle WordAnimation { get; set; } = WordAnimationStyle.Fade;
    public double WordMotionStrength { get; set; } = 0.65;
    public bool IntroDotsEnabled { get; set; } = true;
    public bool InterludeAnimationEnabled { get; set; } = true;
    public bool AnimatedCoversEnabled { get; set; }
    public bool AutoUpdateEnabled { get; set; } = true;
    public int SchemaVersion { get; set; }
    public string LyricsFontFamily { get; set; } = "Segoe UI Variable Display";
    public string LyricsFontWeight { get; set; } = "SemiBold";
    public int AnimationSoftness { get; set; } = 50;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppLanguage Language { get; set; } = AppLanguage.English;
    public DesignProfile? ActiveDesign { get; set; }
    public int MobilePresetIndex { get; set; } = 1;

    // ---- 0.9.40 ----
    /// <summary>Per-line timing: track → (original time in ms → offset in ms).</summary>
    public Dictionary<string, Dictionary<string, int>> PerLineOffsetMs { get; set; } = new();
    /// <summary>Lyrics picked by hand from Lrcmux or LyricsPlus (track → provider).</summary>
    public Dictionary<string, string> ManualPins { get; set; } = new();
    public bool RomanizationEnabled { get; set; }
    public bool MiniLyricsEnabled { get; set; }
    public double? MiniLyricsLeft { get; set; }
    public double? MiniLyricsTop { get; set; }
    public bool GlobalHotkeysEnabled { get; set; } = true;
    public bool ObsOverlayEnabled { get; set; }
    public int ObsOverlayPort { get; set; } = 7531;
    public bool DiscordPresenceEnabled { get; set; }
}
