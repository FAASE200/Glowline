using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LyricGlow.Services;

/// <summary>
/// Servidor web mínimo para usar la letra como "Fuente de navegador" en OBS.
/// Solo escucha en 127.0.0.1 (no es accesible desde otros equipos), solo
/// responde GET y rechaza cualquier Host que no sea localhost, para que una
/// página web externa no pueda leerlo con trucos de DNS.
/// </summary>
public sealed class OverlayServer : IDisposable
{
    const int MaxRequestBytes = 8 * 1024;

    TcpListener? _listener;
    CancellationTokenSource? _cts;
    int _port;
    volatile string _stateJson = "{}";
    volatile byte[]? _cover;

    public bool IsRunning => _listener is not null;

    public int Port => _port;

    public string Url => $"http://localhost:{_port}/";

    public string? LastError { get; private set; }

    public void SetEnabled(bool enabled, int port)
    {
        port = Math.Clamp(port, 1024, 65535);
        if (enabled && IsRunning && port == _port) return;
        Stop();
        if (enabled) Start(port);
    }

    public void Update(NowPlayingHub.Snapshot snapshot, byte[]? cover)
    {
        _stateJson = NowPlayingHub.ToJson(snapshot);
        _cover = cover;
    }

    void Start(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _listener = listener;
            _port = port;
            LastError = null;
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(listener, _cts.Token);
            Log.Write($"overlay: escuchando en {Url}");
        }
        catch (Exception ex)
        {
            _listener = null;
            LastError = ex.Message;
            Log.Write($"overlay: no se pudo abrir el puerto {port}: {ex.Message}");
        }
    }

    void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            _ = HandleAsync(client, ct);
        }
    }

    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                var request = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request is null) return;

                var (method, target, host) = Parse(request);
                if (method != "GET" || !IsAllowedHost(host))
                {
                    await WriteAsync(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Forbidden"), timeout.Token);
                    return;
                }

                var path = target.Split('?', 2)[0];
                switch (path)
                {
                    case "/":
                    case "/index.html":
                        await WriteAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(OverlayPage.Html), timeout.Token);
                        break;
                    case "/state":
                        await WriteAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_stateJson), timeout.Token);
                        break;
                    case "/cover":
                        var cover = _cover;
                        if (cover is { Length: > 0 })
                            await WriteAsync(stream, 200, SniffImageType(cover), cover, timeout.Token);
                        else
                            await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No cover"), timeout.Token);
                        break;
                    default:
                        await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"), timeout.Token);
                        break;
                }
            }
            catch
            {
                // Conexiones cortadas o lentas: no hay nada que hacer.
            }
        }
    }

    static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }
        return null;
    }

    internal static (string Method, string Target, string Host) Parse(string request)
    {
        var lines = request.Split("\r\n");
        var first = lines[0].Split(' ');
        var method = first.Length > 0 ? first[0] : "";
        var target = first.Length > 1 ? first[1] : "/";
        var host = "";
        foreach (var line in lines)
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                host = line[5..].Trim();
                break;
            }
        }
        return (method, target, host);
    }

    internal static bool IsAllowedHost(string host)
    {
        var name = host;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.EndsWith("]", StringComparison.Ordinal)) name = host[..colon];
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1";
    }

    static string SniffImageType(byte[] bytes) =>
        bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png"
        : bytes.Length > 3 && bytes[0] == 0x52 && bytes[1] == 0x49 ? "image/webp"
        : "image/jpeg";

    static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken ct)
    {
        var reason = status switch { 200 => "OK", 403 => "Forbidden", _ => "Not Found" };
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "X-Content-Type-Options: nosniff\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}
