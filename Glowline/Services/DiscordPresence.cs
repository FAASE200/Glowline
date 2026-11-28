using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Glowline.Services;

/// <summary>
/// Shows the song that's playing on your Discord profile ("Listening to ...").
/// Talks to the Discord app on the same computer through its local pipe
/// (discord-ipc-N); it doesn't use the internet or ask you to sign in.
/// Discord rate-limits status changes, so only the song, artist and time
/// are sent, not every line.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    /// <summary>
    /// Id of the "Glowline" application in the Discord developer portal.
    /// It is not a secret. Empty = feature unavailable.
    /// </summary>
    public const string ClientId = "1575888637562982603";

    public static bool IsConfigured => ClientId.Length > 0;

    readonly SemaphoreSlim _gate = new(1, 1);
    NamedPipeClientStream? _pipe;
    bool _enabled;
    NowPlayingHub.Snapshot? _pending;
    DateTime _lastSent = DateTime.MinValue;
    DateTime _nextConnectAttempt = DateTime.MinValue;
    int _sending;

    /// <summary>Show the current lyric line instead of the artist (changes are rate-limited).</summary>
    public bool ShowLyrics { get; set; }

    public bool IsConnected => _pipe?.IsConnected == true;

    public bool IsEnabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        enabled &= IsConfigured;
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) _ = ClearAndCloseAsync();
    }

    public void Update(NowPlayingHub.Snapshot snapshot)
    {
        if (!_enabled) return;
        _pending = snapshot;
        if (Interlocked.Exchange(ref _sending, 1) == 1) return;
        _ = Task.Run(SendLoopAsync);
    }

    async Task SendLoopAsync()
    {
        try
        {
            // Discord accepts at most ~5 updates every 20 s: wait between sends
            // and only send the latest pending state.
            var wait = TimeSpan.FromSeconds(4) - (DateTime.UtcNow - _lastSent);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            var snapshot = Interlocked.Exchange(ref _pending, null);
            if (snapshot is null || !_enabled) return;
            await SendActivityAsync(snapshot).ConfigureAwait(false);
            _lastSent = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Write("discord: " + ex.Message);
            Close();
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
            if (_pending is not null && _enabled && Interlocked.Exchange(ref _sending, 1) == 0)
                _ = Task.Run(SendLoopAsync);
        }
    }

    async Task SendActivityAsync(NowPlayingHub.Snapshot s)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await EnsureConnectedAsync().ConfigureAwait(false)) return;

            object? activity = null;
            if (s.Title.Length > 0 && s.Playing)
            {
                var now = DateTimeOffset.UtcNow;
                var start = now - TimeSpan.FromSeconds(Math.Max(0, s.Position));
                long? end = s.Duration is > 0 ? (start + TimeSpan.FromSeconds(s.Duration.Value)).ToUnixTimeMilliseconds() : null;
                // "Apple Music · Glowline": the header of the card is always the
                // Discord application's name, so the player goes in the line below.
                var badge = s.Player.Length > 0 ? $"{s.Player} · Glowline" : "Glowline";
                var artist = s.Artist.Length > 0 ? s.Artist : "Glowline";
                // With lyrics on, the line goes where the artist was and the artist
                // moves to the cover's hover text.
                var line = ShowLyrics && s.HasLyrics ? s.Line.Trim() : "";
                var hover = line.Length > 0
                    ? (s.Album.Length > 0 ? $"{artist} — {s.Album}" : artist)
                    : (s.Album.Length > 0 ? s.Album : s.Title);
                activity = new
                {
                    type = 2, // "Listening to"
                    details = Clip(s.Title),
                    state = Clip(line.Length > 0 ? line : artist),
                    timestamps = end is null
                        ? (object)new { start = start.ToUnixTimeMilliseconds() }
                        : new { start = start.ToUnixTimeMilliseconds(), end = end.Value },
                    // The song's cover when it has a public address, with the Glowline
                    // icon as a small badge; otherwise just the Glowline icon.
                    assets = IsUsableImageUrl(s.CoverUrl)
                        ? (object)new
                        {
                            large_image = s.CoverUrl,
                            large_text = Clip(hover),
                            small_image = "glowline",
                            small_text = Clip(badge),
                        }
                        : new { large_image = "glowline", large_text = Clip(line.Length > 0 ? hover : s.Album.Length > 0 ? s.Album : badge) },
                };
            }

            await WriteFrameAsync(1, new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity },
                nonce = Guid.NewGuid().ToString(),
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<bool> EnsureConnectedAsync()
    {
        if (_pipe?.IsConnected == true) return true;
        if (DateTime.UtcNow < _nextConnectAttempt) return false;
        _nextConnectAttempt = DateTime.UtcNow.AddSeconds(15);
        Close();

        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(250).ConfigureAwait(false);
                _pipe = pipe;
                await WriteFrameAsync(0, new { v = 1, client_id = ClientId }).ConfigureAwait(false);
                await ReadFrameAsync().ConfigureAwait(false); // READY
                _ = Task.Run(DrainAsync);
                Log.Write("discord: connected to discord-ipc-" + i);
                return true;
            }
            catch
            {
                pipe.Dispose();
                _pipe = null;
            }
        }
        return false;
    }

    // Discord replies to every command; the replies must be read so the
    // pipe doesn't fill up.
    async Task DrainAsync()
    {
        try
        {
            while (_pipe?.IsConnected == true) await ReadFrameAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    async Task WriteFrameAsync(int opcode, object payload)
    {
        var pipe = _pipe ?? throw new IOException("Discord is not connected.");
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
        await pipe.WriteAsync(header).ConfigureAwait(false);
        await pipe.WriteAsync(body).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
    }

    async Task ReadFrameAsync()
    {
        var pipe = _pipe ?? throw new IOException("Discord is not connected.");
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 64 * 1024) throw new InvalidDataException("Invalid response from Discord.");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body).ConfigureAwait(false);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (opcode == 2) throw new IOException("Discord closed the connection: " + Encoding.UTF8.GetString(body));
    }

    async Task ClearAndCloseAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_pipe?.IsConnected == true)
                {
                    await WriteFrameAsync(1, new
                    {
                        cmd = "SET_ACTIVITY",
                        args = new { pid = Environment.ProcessId, activity = (object?)null },
                        nonce = Guid.NewGuid().ToString(),
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
        }
        Close();
    }

    // Discord accepts external images as https links of up to 256 characters.
    internal static bool IsUsableImageUrl(string url) =>
        url.Length is > 0 and <= 256 &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    static string Clip(string text) => text.Length <= 120 ? (text.Length < 2 ? text + "  " : text) : text[..117] + "...";

    void Close()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose()
    {
        _enabled = false;
        Close();
    }
}
