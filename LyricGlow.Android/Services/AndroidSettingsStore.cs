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
    };
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
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
                if (loaded is not null) Settings = loaded;
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
        if (Settings.ActiveDesign.Layout == DesignLayout.Adaptive)
            PresetLibrary.Apply(Settings.ActiveDesign, Math.Clamp(Settings.MobilePresetIndex, 0, 4));

        if (!Enum.IsDefined(typeof(LyricSource), Settings.LyricsSource))
            Settings.LyricsSource = LyricSource.Lrcmux;
        if (!Enum.IsDefined(typeof(InterfaceTheme), Settings.InterfaceTheme))
            Settings.InterfaceTheme = InterfaceTheme.Dark;

        var changed = false;
        if (Settings.SchemaVersion < 3)
        {
            if (Settings.LyricsSource != LyricSource.Lrclib)
                Settings.LyricsSource = LyricSource.Lrcmux;
            Settings.SchemaVersion = 3;
            changed = true;
        }
        return changed;
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
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Settings, JsonOptions), ct);
            File.Move(temp, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
