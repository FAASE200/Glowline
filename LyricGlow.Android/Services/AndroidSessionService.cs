using System;
using System.IO;
using System.Linq;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using LyricGlow.Android.Platforms;
using LyricGlow.Models;
using LyricGlow.Services;

namespace LyricGlow.Android.Services;

public sealed class AndroidSessionService : IDisposable
{
    readonly Context _context;
    readonly MediaSessionManager _manager;
    readonly Handler _handler;
    readonly SessionCallback _callback = new();
    MediaController? _controller;
    MediaMetadata? _metadata;
    PlaybackState? _playback;
    bool _running;

    public SessionSnapshot? Current { get; private set; }
    public event Action<SessionSnapshot>? SnapshotChanged;

    public AndroidSessionService(Context context)
    {
        _context = context;
        _manager = (MediaSessionManager)(context.GetSystemService(Context.MediaSessionService)
            ?? throw new InvalidOperationException("MediaSessionManager unavailable."));
        _handler = new Handler(Looper.MainLooper!);
        _callback.Changed += Refresh;
        MediaNotificationListener.EnabledChanged += Refresh;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        Refresh();
        _handler.PostDelayed(Poll, 500);
    }

    public void Stop()
    {
        _running = false;
        _handler.RemoveCallbacks(Poll);
        DetachController();
        MediaNotificationListener.EnabledChanged -= Refresh;
    }

    void Poll()
    {
        if (!_running) return;
        Refresh();
        _handler.PostDelayed(Poll, Current?.IsPlaying == true ? 500 : 900);
    }

    void Refresh()
    {
        if (!_running) return;
        try
        {
            var listener = new ComponentName(_context.PackageName!, "LyricGlow.Android.Platforms.MediaNotificationListener");
            var sessions = _manager.GetActiveSessions(listener);
            var preferred = _context.PackageName;
            var selected = sessions.FirstOrDefault(s => string.Equals(s.PackageName, preferred, StringComparison.Ordinal))
                        ?? sessions.FirstOrDefault();
            if (selected is null)
            {
                DetachController();
                Current = null;
                SnapshotChanged?.Invoke(new SessionSnapshot());
                return;
            }

            if (_controller is null || !string.Equals(_controller.PackageName, selected.PackageName, StringComparison.Ordinal))
            {
                DetachController();
                _controller = selected;
                _controller.RegisterCallback(_callback);
            }

            _metadata = _controller.Metadata;
            _playback = _controller.PlaybackState;
            var snapshot = BuildSnapshot(_controller, _metadata, _playback);
            Current = snapshot;
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Write("Android media refresh: " + ex.Message);
        }
    }

    static SessionSnapshot BuildSnapshot(MediaController controller, MediaMetadata? metadata, PlaybackState? state)
    {
        var title = Text(metadata, MediaMetadata.MetadataKeyTitle);
        var artist = Text(metadata, MediaMetadata.MetadataKeyArtist);
        var album = Text(metadata, MediaMetadata.MetadataKeyAlbum);
        var duration = Number(metadata, MediaMetadata.MetadataKeyDuration) / 1000.0;
        var raw = Math.Max(0, (state?.Position ?? 0) / 1000.0);
        var speed = state?.PlaybackSpeed ?? 0;
        var playing = state?.State is PlaybackStateCode.Playing or PlaybackStateCode.FastForwarding
            or PlaybackStateCode.Rewinding or PlaybackStateCode.Buffering
            or PlaybackStateCode.SkippingToNext or PlaybackStateCode.SkippingToPrevious;
        if (playing && state is not null)
        {
            var elapsed = Math.Max(0, SystemClock.ElapsedRealtime() - state.LastPositionUpdateTime) / 1000.0;
            raw += elapsed * speed;
        }
        if (duration > 0) raw = Math.Min(raw, duration);

        return new SessionSnapshot
        {
            Track = string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)
                ? null
                : new TrackInfo
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    DurationSeconds = duration > 0 ? duration : null,
                    SourceAppId = controller.PackageName ?? "",
                },
            PositionSeconds = raw,
            DurationSeconds = duration > 0 ? duration : null,
            IsPlaying = playing,
            PlaybackStatus = playing ? "Playing" : "Paused",
            SourceAppId = controller.PackageName ?? "",
            CapturedAt = DateTimeOffset.Now,
            RawTimelinePosition = TimeSpan.FromMilliseconds(state?.Position ?? 0),
            TimelineLastUpdated = DateTimeOffset.Now,
        };
    }

    static string Text(MediaMetadata? metadata, string key) =>
        metadata?.GetString(key)?.Trim() ?? "";

    static double Number(MediaMetadata? metadata, string key) =>
        metadata?.GetLong(key) ?? 0;

    public async Task<byte[]?> ReadThumbnailAsync(string? expectedTitle = null, string? expectedArtist = null)
    {
        var metadata = _metadata;
        if (metadata is null) return null;
        if (!string.IsNullOrEmpty(expectedTitle) &&
            !string.Equals(Text(metadata, MediaMetadata.MetadataKeyTitle), expectedTitle, StringComparison.Ordinal))
            return null;
        if (!string.IsNullOrEmpty(expectedArtist) &&
            !string.Equals(Text(metadata, MediaMetadata.MetadataKeyArtist), expectedArtist, StringComparison.Ordinal))
            return null;

        try
        {
            var bitmap = metadata.GetBitmap(MediaMetadata.MetadataKeyAlbumArt)
                         ?? metadata.GetBitmap(MediaMetadata.MetadataKeyArt);
            if (bitmap is not null)
            {
                using var stream = new MemoryStream();
                if (!bitmap.Compress(Bitmap.CompressFormat.Png, 100, stream)) return null;
                return stream.ToArray();
            }

            var uri = metadata.GetString(MediaMetadata.MetadataKeyAlbumArtUri)
                      ?? metadata.GetString(MediaMetadata.MetadataKeyArtUri);
            if (string.IsNullOrWhiteSpace(uri)) return null;
            using var input = _context.ContentResolver!.OpenInputStream(global::Android.Net.Uri.Parse(uri));
            if (input is null) return null;
            using var copy = new MemoryStream();
            await input.CopyToAsync(copy);
            return copy.ToArray();
        }
        catch (Exception ex)
        {
            Log.Write("Android artwork read: " + ex.Message);
            return null;
        }
    }

    public void TogglePlayPause()
    {
        var controller = _controller;
        if (controller is null) return;
        try
        {
            var controls = controller.GetTransportControls();
            if (controls is null) return;
            var playing = _playback?.State is PlaybackStateCode.Playing or PlaybackStateCode.Buffering
                or PlaybackStateCode.FastForwarding or PlaybackStateCode.Rewinding
                or PlaybackStateCode.SkippingToNext or PlaybackStateCode.SkippingToPrevious;
            if (playing) controls.Pause();
            else controls.Play();
        }
        catch (Exception ex)
        {
            Log.Write("Android play/pause: " + ex.Message);
        }
    }

    public void Next()
    {
        try { _controller?.GetTransportControls()?.SkipToNext(); }
        catch (Exception ex) { Log.Write("Android next: " + ex.Message); }
    }

    public void Previous()
    {
        try { _controller?.GetTransportControls()?.SkipToPrevious(); }
        catch (Exception ex) { Log.Write("Android previous: " + ex.Message); }
    }

    public void SeekTo(long milliseconds)
    {
        try { _controller?.GetTransportControls().SeekTo(Math.Max(0, milliseconds)); }
        catch (Exception ex) { Log.Write("Android seek: " + ex.Message); }
    }

    void DetachController()
    {
        if (_controller is null) return;
        try { _controller.UnregisterCallback(_callback); } catch { }
        try { _controller.Dispose(); } catch { }
        _controller = null;
        _metadata = null;
        _playback = null;
    }

    public void Dispose()
    {
        Stop();
        _callback.Changed -= Refresh;
        _handler.Dispose();
    }

    sealed class SessionCallback : MediaController.Callback
    {
        public event Action? Changed;
        public override void OnMetadataChanged(MediaMetadata? metadata) => Changed?.Invoke();
        public override void OnPlaybackStateChanged(PlaybackState? state) => Changed?.Invoke();
        public override void OnSessionDestroyed() => Changed?.Invoke();
    }
}
