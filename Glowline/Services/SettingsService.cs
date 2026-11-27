using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Glowline.Models;

namespace Glowline.Services;

public sealed class SettingsService : IAppSettingsStore
{
    public static string DataDir => StoragePaths.DataDirectory;

    public static string CacheDir => StoragePaths.CacheDirectory;

    const double MinFontScale = 0.6;
    const double MaxFontScale = 1.8;
    // Offsets outside this range make no sense as a manual correction.
    const int OffsetLimitMs = 5 * 60 * 1000;
    const long MaxSettingsBytes = 2L * 1024 * 1024;
    const int MaxDictionaryEntries = 2000;
    const int MaxKeyChars = 512;
    const int MaxTextChars = 256;
    static readonly JsonSerializerOptions LoadOptions = new()
    {
        MaxDepth = 32,
    };

    readonly string _path = Path.Combine(DataDir, "settings.json");
    DispatcherTimer? _saveTimer;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(CacheDir);
            if (File.Exists(_path))
            {
                var loaded = TryLoad();
                if (loaded is not null)
                {
                    Settings = loaded;
                    Normalize(Settings);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings load: " + ex.Message);
        }
    }

    AppSettings? TryLoad()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= 0) return null;
            if (info.Length > MaxSettingsBytes)
            {
                Log.Write("settings: file exceeds the limit and is ignored");
                return null;
            }
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(_path), LoadOptions);
        }
        catch (Exception ex)
        {
            // Without a copy of the unreadable json every setting would be lost on save.
            Log.Write("settings parse: " + ex.Message);
            TryKeepCorruptBackup();
            return null;
        }
    }

    void TryKeepCorruptBackup()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxSettingsBytes) return;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var backup = $"{_path}.corrupt-{stamp}.bak";
            File.Copy(_path, backup, overwrite: false);
            Log.Write($"settings: copy of the damaged file at {backup}");
        }
        catch (Exception ex)
        {
            Log.Write("settings corrupt backup: " + ex.Message);
        }
    }

    public void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, __) =>
            {
                _saveTimer!.Stop();
                Save();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Save()
    {
        var temp = _path + ".tmp";
        try
        {
            Normalize(Settings);
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(temp,
                JsonSerializer.Serialize(Settings, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("settings save: " + ex.Message);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    static void Normalize(AppSettings s)
    {
        s.FontScale = Clamp(s.FontScale, MinFontScale, MaxFontScale, 1.0);
        if (!Enum.IsDefined(typeof(LyricSource), s.LyricsSource))
            s.LyricsSource = LyricSource.Lrcmux;
        if (!Enum.IsDefined(typeof(InterfaceTheme), s.InterfaceTheme))
            s.InterfaceTheme = InterfaceTheme.Dark;
        if (!Enum.IsDefined(typeof(WordAnimationStyle), s.WordAnimation))
            s.WordAnimation = WordAnimationStyle.Fade;
        s.SoundSenseIntensity = Clamp(s.SoundSenseIntensity, 0.0, 1.0, 0.7);
        s.WordMotionStrength = Clamp(s.WordMotionStrength, 0.0, 1.0, 0.65);
        s.AnimationSoftness = Clamp(s.AnimationSoftness, 0, 100, 50);
        s.GlobalOffsetMs = Clamp(s.GlobalOffsetMs, -OffsetLimitMs, OffsetLimitMs, 0);
        s.MobilePresetIndex = Math.Clamp(s.MobilePresetIndex, 0, 4);

        s.LyricsFontFamily = CleanSetting(s.LyricsFontFamily, 128, "Segoe UI Variable Display");
        s.LyricsFontWeight = CleanSetting(s.LyricsFontWeight, 32, "SemiBold");
        if (s.PreferredSourceAppId is { Length: > MaxTextChars })
            s.PreferredSourceAppId = s.PreferredSourceAppId[..MaxTextChars];

        if (s.PerTrackOffsetMs is null)
        {
            s.PerTrackOffsetMs = new Dictionary<string, int>();
        }
        else
        {
            var kept = 0;
            var keys = new List<string>(s.PerTrackOffsetMs.Keys);
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyChars ||
                    kept >= MaxDictionaryEntries)
                {
                    s.PerTrackOffsetMs.Remove(key);
                    continue;
                }
                // Rewriting a value while enumerating the dictionary would throw InvalidOperationException.
                s.PerTrackOffsetMs[key] = Clamp(s.PerTrackOffsetMs[key],
                    -OffsetLimitMs, OffsetLimitMs, 0);
                kept++;
            }
        }

        if (s.ManualOverrides is null)
        {
            s.ManualOverrides = new Dictionary<string, long>();
        }
        else
        {
            var kept = 0;
            var keys = new List<string>(s.ManualOverrides.Keys);
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyChars ||
                    kept >= MaxDictionaryEntries || s.ManualOverrides[key] <= 0)
                {
                    s.ManualOverrides.Remove(key);
                    continue;
                }
                kept++;
            }
        }

        NormalizeExtras(s);
    }

    // 0.9.40 settings. A hand-edited or damaged settings.json must not leave
    // null dictionaries or out-of-range values.
    static void NormalizeExtras(AppSettings s)
    {
        s.PerLineOffsetMs ??= new Dictionary<string, Dictionary<string, int>>();
        var tracks = 0;
        foreach (var key in new List<string>(s.PerLineOffsetMs.Keys))
        {
            var map = s.PerLineOffsetMs[key];
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyChars || map is null ||
                tracks >= MaxDictionaryEntries)
            {
                s.PerLineOffsetMs.Remove(key);
                continue;
            }
            foreach (var lineKey in new List<string>(map.Keys))
            {
                if (lineKey.Length is 0 or > 16 || map.Count > 2000)
                {
                    map.Remove(lineKey);
                    continue;
                }
                map[lineKey] = Clamp(map[lineKey], -LyricsService.MaxLineOffsetMs, LyricsService.MaxLineOffsetMs, 0);
            }
            if (map.Count == 0) s.PerLineOffsetMs.Remove(key);
            else tracks++;
        }

        s.ManualPins ??= new Dictionary<string, string>();
        var pins = 0;
        foreach (var key in new List<string>(s.ManualPins.Keys))
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyChars || pins >= MaxDictionaryEntries ||
                s.ManualPins[key] is not ("Lrcmux" or "LyricsPlus"))
            {
                s.ManualPins.Remove(key);
                continue;
            }
            pins++;
        }

        if (s.ObsOverlayPort is < 1024 or > 65535) s.ObsOverlayPort = 7531;
        if (s.MiniLyricsLeft is { } left && !double.IsFinite(left)) s.MiniLyricsLeft = null;
        if (s.MiniLyricsTop is { } top && !double.IsFinite(top)) s.MiniLyricsTop = null;
    }

    static string CleanSetting(string? value, int maxLength, string fallback)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) return fallback;
        return text.Length <= maxLength ? text : fallback;
    }

    static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    static int Clamp(int value, int min, int max, int fallback) =>
        value < min || value > max ? fallback : value;
}
