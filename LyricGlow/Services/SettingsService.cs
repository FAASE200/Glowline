using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using LyricGlow.Models;

namespace LyricGlow.Services;

public sealed class SettingsService : IAppSettingsStore
{
    public static string DataDir => StoragePaths.DataDirectory;

    public static string CacheDir => StoragePaths.CacheDirectory;

    const double MinFontScale = 0.6;
    const double MaxFontScale = 1.8;
    // Los desplazamientos fuera de este rango no tienen sentido como corrección manual.
    const int OffsetLimitMs = 5 * 60 * 1000;

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
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            // Sin copia del json ilegible se perderían todos los ajustes al guardar.
            Log.Write("settings parse: " + ex.Message);
            TryKeepCorruptBackup();
            return null;
        }
    }

    void TryKeepCorruptBackup()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var backup = $"{_path}.corrupt-{stamp}.bak";
            File.Copy(_path, backup, overwrite: false);
            Log.Write($"settings: copia del archivo dañado en {backup}");
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
        s.SoundSenseIntensity = Clamp(s.SoundSenseIntensity, 0.0, 1.0, 0.7);
        s.WordMotionStrength = Clamp(s.WordMotionStrength, 0.0, 1.0, 0.65);
        s.AnimationSoftness = Clamp(s.AnimationSoftness, 0, 100, 50);
        s.GlobalOffsetMs = Clamp(s.GlobalOffsetMs, -OffsetLimitMs, OffsetLimitMs, 0);

        if (s.PerTrackOffsetMs is null)
        {
            s.PerTrackOffsetMs = new Dictionary<string, int>();
        }
        else
        {
            List<string>? stale = null;
            foreach (var entry in s.PerTrackOffsetMs)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key)) continue;
                (stale ??= new List<string>()).Add(entry.Key);
            }
            // Reescribir un valor mientras se enumera el diccionario lanzaría InvalidOperationException.
            if (stale is not null)
            {
                foreach (var key in stale) s.PerTrackOffsetMs.Remove(key);
            }
            var keys = new List<string>(s.PerTrackOffsetMs.Keys);
            foreach (var key in keys)
                s.PerTrackOffsetMs[key] = Clamp(s.PerTrackOffsetMs[key], -OffsetLimitMs, OffsetLimitMs, 0);
        }

        if (s.ManualOverrides is null) s.ManualOverrides = new Dictionary<string, long>();
    }

    static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    static int Clamp(int value, int min, int max, int fallback) =>
        value < min || value > max ? fallback : value;
}
