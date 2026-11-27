using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LyricGlow.Services;

/// <summary>
/// Muestra en tu perfil de Discord la canción que suena ("Escuchando ...").
/// Habla con la app de Discord del mismo equipo por su tubería local
/// (discord-ipc-N); no usa internet ni pide iniciar sesión.
/// Discord limita los cambios de estado, así que solo se envía la canción,
/// el artista y el tiempo, no cada línea.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    /// <summary>
    /// Id de la aplicación "Glowline" en el portal de desarrolladores de
    /// Discord. No es secreto. Vacío = función no disponible.
    /// </summary>
    public const string ClientId = "";

    public static bool IsConfigured => ClientId.Length > 0;

    readonly SemaphoreSlim _gate = new(1, 1);
    NamedPipeClientStream? _pipe;
    bool _enabled;
    NowPlayingHub.Snapshot? _pending;
    DateTime _lastSent = DateTime.MinValue;
    DateTime _nextConnectAttempt = DateTime.MinValue;
    int _sending;

    public bool IsConnected => _pipe?.IsConnected == true;

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
            // Discord acepta como mucho ~5 cambios cada 20 s: se espera entre envíos
            // y solo se manda el último estado pendiente.
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
                activity = new
                {
                    type = 2, // "Escuchando"
                    details = Clip(s.Title),
                    state = Clip(s.Artist.Length > 0 ? s.Artist : "Glowline"),
                    timestamps = end is null
                        ? (object)new { start = start.ToUnixTimeMilliseconds() }
                        : new { start = start.ToUnixTimeMilliseconds(), end = end.Value },
                    assets = new { large_image = "glowline", large_text = Clip(s.Album.Length > 0 ? s.Album : "Glowline") },
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
                Log.Write("discord: conectado a discord-ipc-" + i);
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

    // Discord responde a cada comando; hay que leer las respuestas para que
    // la tubería no se llene.
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
        var pipe = _pipe ?? throw new IOException("Discord no está conectado.");
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
        var pipe = _pipe ?? throw new IOException("Discord no está conectado.");
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 64 * 1024) throw new InvalidDataException("Respuesta de Discord no válida.");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body).ConfigureAwait(false);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (opcode == 2) throw new IOException("Discord cerró la conexión: " + Encoding.UTF8.GetString(body));
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
