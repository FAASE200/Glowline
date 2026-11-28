using System;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using Glowline.Models;

namespace Glowline.Services;

/// <summary>
/// "Now playing" state for the integrations (OBS and Discord).
/// It is computed on the UI thread, where LyricsService is safe to use,
/// and published as an immutable snapshot.
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
        long CoverVersion,
        string CoverUrl = "",
        string Player = "",
        string Translation = "");

    readonly DispatcherTimer _timer;
    readonly OverlayServer _overlay = new();
    readonly DiscordPresence _discord = new();
    CancellationTokenSource? _coverCts;
    string _coverKey = "";
    byte[]? _cover;
    string _coverUrl = "";
    long _coverVersion;
    Snapshot? _last;
    bool _discordLyrics;

    public NowPlayingHub()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, __) => Tick();
    }

    public OverlayServer Overlay => _overlay;

    public DiscordPresence Discord => _discord;

    /// <summary>Applies the current settings: starts or stops each integration.</summary>
    public void Apply(AppSettings settings)
    {
        _overlay.SetEnabled(settings.ObsOverlayEnabled, settings.ObsOverlayPort);
        var discordWasEnabled = _discord.IsEnabled;
        if (_discordLyrics != settings.DiscordShowLyrics)
        {
            _discordLyrics = settings.DiscordShowLyrics;
            _discord.ShowLyrics = _discordLyrics;
            _last = null; // send the status again with or without the line
        }
        _discord.SetEnabled(settings.DiscordPresenceEnabled);
        // Turning Discord on mid-song: look the cover up again to get its address.
        if (_discord.IsEnabled && !discordWasEnabled) _coverKey = "";
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

        string line = "", romanized = "", translation = "", next = "";
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
                translation = lines[index].Translation;
            }
            if (index + 1 < lines.Count) next = lines[index + 1].Text;
        }

        Publish(new Snapshot(track.Key, track.Title, track.Artist, track.Album, lyrics.IsPlaying,
            position, track.DurationSeconds, line, romanized, next, hasLyrics, _coverVersion, _coverUrl,
            PlayerNames.Friendly(track.SourceAppId), translation));
    }

    void Publish(Snapshot snapshot)
    {
        // Discord only shows song, artist and time: notify it when one of
        // those changes (or on a seek), not on every line.
        var last = _last;
        var discordChanged = last is null ||
                             last.TrackKey != snapshot.TrackKey ||
                             last.Playing != snapshot.Playing ||
                             last.Title != snapshot.Title ||
                             last.Artist != snapshot.Artist ||
                             last.Album != snapshot.Album ||
                             last.Duration != snapshot.Duration ||
                             last.CoverUrl != snapshot.CoverUrl ||
                             last.Player != snapshot.Player ||
                             (_discordLyrics && last.Line != snapshot.Line);
        // Between two ticks (200 ms) the position barely moves; a big jump is a seek.
        var expected = last is null ? 0 : last.Position + (last.Playing ? 0.2 : 0);
        var seeked = last is not null && last.TrackKey == snapshot.TrackKey &&
                     Math.Abs(snapshot.Position - expected) > 2.5;
        _last = snapshot;
        _overlay.Update(snapshot, _cover);
        if (discordChanged || seeked) _discord.Update(snapshot);
    }

    async void RefreshCover(TrackInfo track)
    {
        _coverKey = track.Key;
        _cover = null;
        _coverUrl = "";
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

            // Discord can only show covers that have a public https address.
            if (_discord.IsEnabled)
            {
                var url = await App.CoverArt.FindPublicArtworkUrlAsync(track, cts.Token);
                if (cts.IsCancellationRequested || _coverKey != track.Key) return;
                _coverUrl = url;
            }
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
            translation = snapshot.Translation,
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
