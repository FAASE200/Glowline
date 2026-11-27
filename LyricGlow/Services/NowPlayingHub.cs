using System;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using LyricGlow.Models;

namespace LyricGlow.Services;

/// <summary>
/// Estado "lo que está sonando" para las integraciones (OBS y Discord).
/// Se calcula en el hilo de la interfaz, que es donde LyricsService es
/// seguro, y se publica como una instantánea inmutable.
/// </summary>
public sealed class NowPlayingHub : IDisposable
{
    public sealed record Snapshot(
        string TrackKey,
        string Title,
        string Artist,
        string Album,
        bool Playing,
        double Position,
        double? Duration,
        string Line,
        string Romanized,
        string Next,
        bool HasLyrics,
        long CoverVersion);

    readonly DispatcherTimer _timer;
    readonly OverlayServer _overlay = new();
    readonly DiscordPresence _discord = new();
    CancellationTokenSource? _coverCts;
    string _coverKey = "";
    byte[]? _cover;
    long _coverVersion;
    Snapshot? _last;

    public NowPlayingHub()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, __) => Tick();
    }

    public OverlayServer Overlay => _overlay;

    public DiscordPresence Discord => _discord;

    /// <summary>Aplica los ajustes actuales: arranca o detiene cada integración.</summary>
    public void Apply(AppSettings settings)
    {
        _overlay.SetEnabled(settings.ObsOverlayEnabled, settings.ObsOverlayPort);
        _discord.SetEnabled(settings.DiscordPresenceEnabled);
        var any = settings.ObsOverlayEnabled || settings.DiscordPresenceEnabled;
        if (any && !_timer.IsEnabled)
        {
            _timer.Start();
            Tick();
        }
        else if (!any && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    void Tick()
    {
        var lyrics = App.Lyrics;
        var track = lyrics.Track;
        if (track is null)
        {
            Publish(new Snapshot("", "", "", "", false, 0, null, "", "", "", false, _coverVersion));
            return;
        }

        if (track.Key != _coverKey) RefreshCover(track);

        string line = "", romanized = "", next = "";
        var hasLyrics = lyrics.State == LyricsState.Synced && lyrics.Lines.Count > 0;
        var position = lyrics.GetDisplayPosition();
        if (hasLyrics)
        {
            var lines = lyrics.Lines;
            var index = lyrics.GetLineIndex(lyrics.GetAdjustedPosition());
            if (index >= 0)
            {
                line = lines[index].Text.Length > 0 ? lines[index].Text : lines[index].Background;
                romanized = lines[index].Romanized;
            }
            if (index + 1 < lines.Count) next = lines[index + 1].Text;
        }

        Publish(new Snapshot(track.Key, track.Title, track.Artist, track.Album, lyrics.IsPlaying,
            position, track.DurationSeconds, line, romanized, next, hasLyrics, _coverVersion));
    }

    void Publish(Snapshot snapshot)
    {
        var changed = _last is null || _last with { Position = snapshot.Position } != snapshot;
        var seeked = _last is not null && Math.Abs(snapshot.Position - _last.Position) > 2.5 &&
                     snapshot.TrackKey == _last.TrackKey;
        _last = snapshot;
        _overlay.Update(snapshot, _cover);
        if (changed || seeked) _discord.Update(snapshot);
    }

    async void RefreshCover(TrackInfo track)
    {
        _coverKey = track.Key;
        _cover = null;
        _coverVersion++;
        _coverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _coverCts = cts;
        try
        {
            var result = await App.CoverArt.GetAsync(track, cts.Token);
            if (cts.IsCancellationRequested || _coverKey != track.Key) return;
            _cover = result.HasImage ? result.Bytes : null;
            _coverVersion++;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("nowplaying cover: " + ex.Message);
        }
    }

    public static string ToJson(Snapshot? snapshot)
    {
        if (snapshot is null) return "{}";
        return JsonSerializer.Serialize(new
        {
            title = snapshot.Title,
            artist = snapshot.Artist,
            album = snapshot.Album,
            playing = snapshot.Playing,
            position = Math.Round(snapshot.Position, 2),
            duration = snapshot.Duration,
            line = snapshot.Line,
            romanized = snapshot.Romanized,
            next = snapshot.Next,
            hasLyrics = snapshot.HasLyrics,
            cover = snapshot.CoverVersion,
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        _coverCts?.Cancel();
        _overlay.Dispose();
        _discord.Dispose();
    }
}
