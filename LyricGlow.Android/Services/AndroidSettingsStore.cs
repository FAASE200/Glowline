using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LyricGlow.Models;
using LyricGlow.Services;
using Microsoft.Maui.Storage;

namespace LyricGlow.Android.Services;

public sealed class AndroidSettingsStore : IAppSettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };
    const long MaxSettingsBytes = 2L * 1024 * 1024;
    readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "settings.json");
    readonly SemaphoreSlim _gate = new(1, 1);
    CancellationTokenSource? _saveCts;

    public AppSettings Settings { get; private set; } = new();

    public AndroidSettingsStore()
    {
        var changed = false;
        try
        {
            if (File.Exists(_path))
            {
                var info = new FileInfo(_path);
                if (info.Length is > 0 and <= MaxSettingsBytes)
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllBytes(_path), JsonOptions);
                    if (loaded is not null) Settings = loaded;
                }
                else if (info.Length > MaxSettingsBytes)
                {
                    Log.Write("Android settings: archivo excede el límite");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Android settings load: " + ex.Message);
            TryKeepCorruptBackup();
        }

        changed = Normalize();
        if (changed) ScheduleSave();
    }

    void TryKeepCorruptBackup()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Copy(_path, $"{_path}.corrupt-{stamp}.bak", overwrite: false);
        }
        catch (Exception ex)
        {
            Log.Write("Android settings corrupt backup: " + ex.Message);
        }
    }

    bool Normalize()
    {
        Settings.FontScale = double.IsFinite(Settings.FontScale) ? Math.Clamp(Settings.FontScale, 0.6, 1.8) : 1;
        Settings.WordMotionStrength = double.IsFinite(Settings.WordMotionStrength) ? Math.Clamp(Settings.WordMotionStrength, 0, 1) : 0.65;
        Settings.GlobalOffsetMs = Math.Clamp(Settings.GlobalOffsetMs, -300000, 300000);
        Settings.PerTrackOffsetMs ??= new();
        Settings.ManualOverrides ??= new();
        Settings.ActiveDesign ??= new DesignProfile();
        Settings.LyricsFontFamily = CleanSetting(Settings.LyricsFontFamily, 128, "Segoe UI Variable Display");
        Settings.LyricsFontWeight = CleanSetting(Settings.LyricsFontWeight, 32, "SemiBold");
        TrimDictionary(Settings.PerTrackOffsetMs, 2000, 512);
        TrimDictionary(Settings.ManualOverrides, 2000, 512);
        if (Settings.ActiveDesign.Layout == DesignLayout.Adaptive)
            PresetLibrary.Apply(Settings.ActiveDesign, Math.Clamp(Settings.MobilePresetIndex, 0, 4));

        if (!Enum.IsDefined(typeof(LyricSource), Settings.LyricsSource))
            Settings.LyricsSource = LyricSource.Lrcmux;
        if (!Enum.IsDefined(typeof(InterfaceTheme), Settings.InterfaceTheme))
            Settings.InterfaceTheme = InterfaceTheme.Dark;
        if (!Enum.IsDefined(typeof(WordAnimationStyle), Settings.WordAnimation))
            Settings.WordAnimation = WordAnimationStyle.Fade;

        var changed = false;
        if (Settings.SchemaVersion < 3)
        {
            if (Settings.LyricsSource != LyricSource.Lrclib)
                Settings.LyricsSource = LyricSource.Lrcmux;
            Settings.SchemaVersion = 3;
            changed = true;
        }
        if (Settings.SchemaVersion < 4)
        {
            Settings.SoftwareRendering = false;
            Settings.SchemaVersion = 4;
            changed = true;
        }
        return changed;
    }

    static string CleanSetting(string? value, int maxLength, string fallback)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) return fallback;
        return text.Length <= maxLength ? text : fallback;
    }

    static void TrimDictionary<T>(Dictionary<string, T> values, int maxEntries, int maxKeyChars)
    {
        var keys = new List<string>(values.Keys);
        var kept = 0;
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > maxKeyChars || kept >= maxEntries)
            {
                values.Remove(key);
                continue;
            }
            kept++;
        }
    }

    public void ScheduleSave()
    {
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        _ = SaveSoonAsync(_saveCts.Token);
    }

    async Task SaveSoonAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
            await SaveNowAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write("Android settings save: " + ex.Message); }
    }

    public async Task SaveNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(FileSystem.AppDataDirectory);
            var temp = _path + ".tmp";
            TrimDictionary(Settings.PerTrackOffsetMs, 2000, 512);
            TrimDictionary(Settings.ManualOverrides, 2000, 512);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Settings, JsonOptions), ct);
            File.Move(temp, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
