using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Glowline.Models;

namespace Glowline.Services;

public sealed class MediaSessionService : IDisposable
{
    const int MaxMetaChars = 512;
    const double MaxDurationSeconds = 24 * 60 * 60;
    const long MaxThumbnailBytes = 20L * 1024 * 1024;

    readonly SettingsService _settings;
    readonly Dispatcher _dispatcher;
    readonly DispatcherTimer _poll;
    readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onMediaProps;
    readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _onTimeline;
    readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _onPlayback;
    readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _onSessionsChanged;

    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    GlobalSystemMediaTransportControlsSession? _currentSession;
    GlobalSystemMediaTransportControlsSessionMediaProperties? _props;
    DateTimeOffset _propsAt = DateTimeOffset.MinValue;
    volatile bool _propsDirty = true;
    bool _busy;
    bool _disposed;
    TimelineSmoother _timeline = new();

    public SessionSnapshot? LastSnapshot { get; private set; }
    public event Action<SessionSnapshot>? SnapshotChanged;

    public MediaSessionService(SettingsService settings)
    {
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _onMediaProps = (_, __) =>
        {
            _propsDirty = true;
            QueueRefresh();
        };
        _onTimeline = (_, __) => QueueRefresh();
        _onPlayback = (_, __) => QueueRefresh();
        _onSessionsChanged = (_, __) => QueueRefresh();
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _poll.Tick += async (_, __) => await RefreshAsync();
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += _onSessionsChanged;
            Log.Write("SMTC manager ready.");
        }
        catch (Exception ex)
        {
            Log.Write("SMTC init failed: " + ex);
        }

        Attach(Pick());
        _poll.Start();
        await RefreshAsync();
    }

    static readonly string[] PlaceholderTitles =
    {
        "sin reproducción", "not playing", "nothing playing", "no reproduction", "reproducción",
    };

    static bool IsPlaceholderTrack(string title, string artist)
    {
        if (artist.Length > 0) return false;
        var normalized = title.Trim().ToLowerInvariant();
        return normalized.Length == 0 || PlaceholderTitles.Contains(normalized);
    }

    GlobalSystemMediaTransportControlsSession? Pick()
    {
        if (_manager is null) return null;
        try
        {
            var sessions = _manager.GetSessions();
            var pref = _settings.Settings.PreferredSourceAppId;
            if (!string.IsNullOrEmpty(pref))
            {
                var exact = sessions.FirstOrDefault(s =>
                    string.Equals(s.SourceAppUserModelId, pref, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact;
            }

            // With two apps playing at once (e.g. Apple Music and a browser
            // video), picking "the first one playing" switched sessions on
            // every refresh and restarted the lyrics. Keeping the current session
            // while it is still playing avoids that back-and-forth.
            var previous = _session is null
                ? null
                : sessions.FirstOrDefault(s => SameSource(s, _session));
            if (previous is not null && IsPlayingSession(previous))
                return previous;

            foreach (var s in sessions)
            {
                if (IsPlayingSession(s)) return s;
            }

            return previous ?? sessions.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    static bool SameSource(GlobalSystemMediaTransportControlsSession a, GlobalSystemMediaTransportControlsSession b) =>
        !string.IsNullOrEmpty(a.SourceAppUserModelId) &&
        string.Equals(a.SourceAppUserModelId, b.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);

    static bool IsPlayingSession(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _session)) return;
        if (_session is not null)
        {
            try
            {
                _session.MediaPropertiesChanged -= _onMediaProps;
                _session.TimelinePropertiesChanged -= _onTimeline;
                _session.PlaybackInfoChanged -= _onPlayback;
            }
            catch
            {
            }
        }

        _session = session;
        // Volatile so SeekAsync sees the change made by RefreshAsync on the
        // input thread even while the seek is in the middle of an await.
        Volatile.Write(ref _currentSession, session);
        _props = null;
        _propsAt = DateTimeOffset.MinValue;
        _propsDirty = true;
        _timeline = new TimelineSmoother();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += _onMediaProps;
            _session.TimelinePropertiesChanged += _onTimeline;
            _session.PlaybackInfoChanged += _onPlayback;
            Log.Write("SMTC attached: " + _session.SourceAppUserModelId);
        }
    }

    void QueueRefresh()
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(new Action(async () => await RefreshAsync()));
    }

    async Task RefreshAsync()
    {
        // The disposed guard also covers callbacks queued before Dispose.
        if (_disposed || _busy) return;
        _busy = true;
        try
        {
            Attach(Pick());
            if (_session is null)
            {
                PublishEmpty();
                return;
            }

            var pb = _session.GetPlaybackInfo();
            var tl = _session.GetTimelineProperties();
            if (pb is null || tl is null) return;
            var playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var now = DateTimeOffset.Now;

            if (_props is null || _propsDirty || (now - _propsAt).TotalSeconds > 1.5)
            {
                _propsDirty = false;
                try
                {
                    _props = await _session.TryGetMediaPropertiesAsync();
                    _propsAt = now;
                }
                catch
                {
                    _propsDirty = true;
                }
            }

            var raw = Math.Max(0, tl.Position.TotalSeconds);
            if (!double.IsFinite(raw)) raw = 0;
            double end = tl.EndTime.TotalSeconds;
            double? duration = double.IsFinite(end) && end is > 1 and < MaxDurationSeconds ? end : null;

            var title = CleanMeta(_props?.Title);
            var artist = CleanMeta(_props?.Artist);
            var album = CleanMeta(_props?.AlbumTitle);
            var source = CleanMeta(_session.SourceAppUserModelId);
            var signature = $"{source}|{title}|{artist}|{album}|" +
                            (duration is > 0 ? ((int)Math.Round(duration.Value)).ToString() : "-");

            var pos = _timeline.Update(raw, playing, tl.LastUpdatedTime, now, signature);
            if (duration is not null && pos > duration + 3) duration = null;

            TrackInfo? track = null;
            if (!(title.Length == 0 && artist.Length == 0) && !IsPlaceholderTrack(title, artist))
            {
                track = new TrackInfo
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    DurationSeconds = duration,
                    SourceAppId = source,
                };
            }

            var snap = new SessionSnapshot
            {
                Track = track,
                PositionSeconds = pos,
                DurationSeconds = duration,
                IsPlaying = playing,
                PlaybackStatus = pb.PlaybackStatus.ToString(),
                SourceAppId = source,
                CapturedAt = now,
                RawTimelinePosition = tl.Position >= TimeSpan.Zero && raw <= MaxDurationSeconds
                    ? tl.Position
                    : TimeSpan.Zero,
                TimelineLastUpdated = tl.LastUpdatedTime,
            };
            LastSnapshot = snap;
            SnapshotChanged?.Invoke(snap);
        }
        catch (Exception ex)
        {
            Log.Write("SMTC refresh: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    void PublishEmpty()
    {
        var snap = new SessionSnapshot
        {
            Track = null,
            CapturedAt = DateTimeOffset.Now,
            PlaybackStatus = "NoSession",
        };
        LastSnapshot = snap;
        SnapshotChanged?.Invoke(snap);
    }

    public IReadOnlyList<string> GetSourceApps()
    {
        try
        {
            return _manager?.GetSessions()
                       .Select(s => s.SourceAppUserModelId)
                       .Where(x => !string.IsNullOrEmpty(x))
                       .Distinct()
                       .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                       .ToList()
                   ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public async Task<byte[]?> ReadThumbnailAsync(string? expectedTitle = null, string? expectedArtist = null)
    {
        try
        {
            var props = _props;
            if (props is null) return null;
            if (expectedTitle is not null &&
                !string.Equals(CleanMeta(props.Title), expectedTitle, StringComparison.Ordinal)) return null;
            if (expectedArtist is not null &&
                !string.Equals(CleanMeta(props.Artist), expectedArtist, StringComparison.Ordinal)) return null;

            var thumb = props.Thumbnail;
            if (thumb is null) return null;
            using var stream = await thumb.OpenReadAsync();
            var size = (uint)stream.Size;
            if (size == 0 || size > MaxThumbnailBytes) return null;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var bytes = new byte[(int)size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    static string CleanMeta(string? value)
    {
        var text = value ?? "";
        return text.Length <= MaxMetaChars ? text : text[..MaxMetaChars];
    }

    public async Task TogglePlayPauseAsync()
    {
        try
        {
            if (_session is not null) await _session.TryTogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            Log.Write("SMTC toggle: " + ex.Message);
        }
    }

    public async Task SkipNextAsync()
    {
        try
        {
            if (_session is not null) await _session.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Log.Write("SMTC next: " + ex.Message);
        }
    }

    public async Task SkipPreviousAsync()
    {
        try
        {
            if (_session is not null) await _session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Write("SMTC prev: " + ex.Message);
        }
    }

    public async Task<bool> SeekAsync(double positionSeconds)
    {
        if (_disposed) return false;
        if (double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds) || positionSeconds < 0)
            return false;

        var session = _session;
        var isApple = (session?.SourceAppUserModelId ?? "").Contains("AppleMusic", StringComparison.OrdinalIgnoreCase);

        if (isApple && await AppleMusicSeeker.SeekAsync(positionSeconds))
        {
            // Only shift the timeline if the captured session is still the current one.
            if (!IsCurrentSession(session)) return false;
            _timeline.NotifySeek(positionSeconds, DateTimeOffset.Now);
            return true;
        }

        try
        {
            if (session is not null)
            {
                var ticks = (long)Math.Round(positionSeconds * TimeSpan.TicksPerSecond);
                if (await session.TryChangePlaybackPositionAsync(ticks))
                {
                    if (!IsCurrentSession(session)) return false;
                    _timeline.NotifySeek(positionSeconds, DateTimeOffset.Now);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("SMTC seek: " + ex.Message);
        }

        return false;
    }

    bool IsCurrentSession(GlobalSystemMediaTransportControlsSession? session) =>
        !_disposed && session is not null && ReferenceEquals(session, _currentSession);

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
        if (_manager is not null)
        {
            try
            {
                _manager.SessionsChanged -= _onSessionsChanged;
            }
            catch
            {
            }
        }
        Attach(null);
    }
}
