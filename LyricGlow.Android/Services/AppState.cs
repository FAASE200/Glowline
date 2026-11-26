using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LyricGlow.Models;
using LyricGlow.Localization;
using LyricGlow.Services;
using Microsoft.Maui.Controls;

namespace LyricGlow.Android.Services;

public sealed class AppState
{
    public static AppState Instance { get; private set; } = null!;

    public AndroidSettingsStore SettingsStore { get; }
    public AndroidSessionService Session { get; }
    public LyricsService Lyrics { get; }
    public CoverArtService CoverArt { get; }
    public AnimatedArtworkService AnimatedCover { get; }
    public AppSettings Settings => SettingsStore.Settings;
    public DesignProfile Design => Settings.ActiveDesign!;
    public bool IsPlaying => Session.Current?.IsPlaying == true;

    CancellationTokenSource? _coverCts;
    long _coverGeneration;
    string? _artworkKey;
    string _artAlbum = "";
    string _artSource = "";
    bool _artHighRes;
    bool _started;

    public event Action? Changed;
    public event Action? CoverChanged;
    public event Action? AnimatedChanged;

    public TrackInfo? Track { get; private set; }
    public byte[]? CoverBytes { get; private set; }
    public byte[]? BackdropBlurBytes { get; private set; }
    public byte[]? BackdropSharpBytes { get; private set; }
    public FusionPalette CoverPalette { get; private set; } = FusionPalette.Default;
    public string? AnimatedPath { get; private set; }
    public string? AnimatedStatus { get; private set; }
    public LyricsState LyricsState => Lyrics.State;

    private AppState(AndroidSettingsStore settings, AndroidSessionService session)
    {
        SettingsStore = settings;
        Session = session;
        Lyrics = new LyricsService(settings, new LrcLibClient());
        CoverArt = new CoverArtService(new AndroidMediaArtworkProvider(session), new AndroidImageDecoder());
        AnimatedCover = new AnimatedArtworkService(CoverArt);
        MobileLoc.SetLanguage(Settings.Language);
        ApplyInterfaceTheme();
    }

    public void ApplyInterfaceTheme()
    {
        if (Application.Current is { } app)
            app.UserAppTheme = Settings.InterfaceTheme == InterfaceTheme.Dark ? AppTheme.Dark : AppTheme.Light;
    }

    public static AppState Create(AndroidSettingsStore settings, AndroidSessionService session)
    {
        Instance = new AppState(settings, session);
        return Instance;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Session.SnapshotChanged += OnSnapshot;
        Lyrics.Changed += RaiseChanged;
        Session.Start();
    }

    public void Stop()
    {
        _started = false;
        Session.SnapshotChanged -= OnSnapshot;
        Lyrics.Changed -= RaiseChanged;
        Session.Stop();
        _coverCts?.Cancel();
    }

    void OnSnapshot(SessionSnapshot snapshot)
    {
        Lyrics.OnSnapshot(snapshot);
        Track = snapshot.Track;
        if (Track is null)
        {
            Track = null;
            _artworkKey = null;
            _artAlbum = "";
            _artSource = "";
            _artHighRes = false;
            CoverBytes = null;
            BackdropBlurBytes = null;
            BackdropSharpBytes = null;
            CoverPalette = FusionPalette.Default;
            AnimatedPath = null;
            CoverChanged?.Invoke();
            AnimatedChanged?.Invoke();
            RaiseChanged();
            return;
        }

        var key = Track.Key;
        var trackChanged = !string.Equals(key, _artworkKey, StringComparison.Ordinal);
        Track = snapshot.Track;
        if (trackChanged)
        {
            _artworkKey = key;
            var keepArt = CoverArtService.SameAlbum(Track.Album, _artAlbum) &&
                          (_artSource != "Reproductor" || _artHighRes);
            if (!keepArt)
            {
                _artAlbum = "";
                _artSource = "";
                _artHighRes = false;
                CoverBytes = null;
                BackdropBlurBytes = null;
                BackdropSharpBytes = null;
                CoverPalette = FusionPalette.Default;
                CoverChanged?.Invoke();
            }
            if (Track is { } current) _ = RefreshArtworkAsync(current);
        }
        RaiseChanged();
    }

    async Task RefreshArtworkAsync(TrackInfo track)
    {
        _coverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _coverCts = cts;
        var generation = ++_coverGeneration;
        try
        {
            var result = await CoverArt.GetAsync(track, cts.Token);
            if (generation != _coverGeneration || cts.IsCancellationRequested) return;
            var bytes = result.Bytes;
            var (palette, blurBackdrop, sharpBackdrop) = await Task.Run(() => (
                LyricGlow.Android.Services.CoverPalette.Sample(bytes),
                AndroidImageDecoder.CreateBackdrop(bytes, blurred: true),
                AndroidImageDecoder.CreateBackdrop(bytes, blurred: false)));
            if (generation != _coverGeneration || cts.IsCancellationRequested) return;
            CoverBytes = bytes;
            BackdropBlurBytes = blurBackdrop;
            BackdropSharpBytes = sharpBackdrop;
            CoverPalette = palette;
            _artAlbum = !string.IsNullOrWhiteSpace(result.Album) ? result.Album : track.Album;
            _artSource = result.Source;
            _artHighRes = result.IsHighResolution;
            CoverChanged?.Invoke();
            AnimatedPath = null;
            AnimatedStatus = null;
            AnimatedChanged?.Invoke();
            if (!Settings.AnimatedCoversEnabled || string.IsNullOrWhiteSpace(track.Artist)) return;
            var animated = await AnimatedCover.GetAsync(track, cts.Token);
            if (generation != _coverGeneration || cts.IsCancellationRequested) return;
            if (animated.Available)
            {
                AnimatedPath = animated.Path;
                AnimatedStatus = animated.Source;
            }
            else
            {
                AnimatedStatus = animated.ErrorKey;
            }
            AnimatedChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("Android artwork state: " + ex.Message);
        }
    }

    public double Position => Lyrics.GetAdjustedPosition();

    public int CurrentLineIndex(double position)
    {
        var lines = Lyrics.Lines;
        if (lines.Count == 0) return -1;
        var index = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time > position + 0.001) break;
            index = i;
        }
        return index;
    }

    public void TogglePlayPause() => Session.TogglePlayPause();
    public void Next() => Session.Next();
    public void Previous() => Session.Previous();

    public void SeekTo(double seconds)
    {
        var target = Math.Max(0, seconds);
        Session.SeekTo((long)(target * 1000));
        Lyrics.NotifySeek(target);
        RaiseChanged();
    }

    public void SeekToLine(double lineTime)
    {
        var target = Lyrics.GetSeekPosition(lineTime);
        Session.SeekTo((long)(target * 1000));
        Lyrics.NotifySeek(target);
        RaiseChanged();
    }

    public void SetLanguage(AppLanguage language)
    {
        Settings.Language = language;
        MobileLoc.SetLanguage(language);
        SettingsStore.ScheduleSave();
        RaiseChanged();
    }

    public void SetPreset(int index)
    {
        var design = new DesignProfile();
        PresetLibrary.Apply(design, index);
        Settings.ActiveDesign = design;
        Settings.MobilePresetIndex = index;
        Settings.WordAnimation = (WordAnimationStyle)(int)design.Animations.WordAnimation;
        SettingsStore.ScheduleSave();
        RaiseChanged();
    }

    public void SetAnimatedCovers(bool enabled)
    {
        Settings.AnimatedCoversEnabled = enabled;
        SettingsStore.ScheduleSave();
        if (Track is not null) _ = RefreshArtworkAsync(Track);
        RaiseChanged();
    }

    public void Save() => SettingsStore.ScheduleSave();
    void RaiseChanged() => Changed?.Invoke();
}
