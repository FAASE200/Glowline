using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Glowline.Services;

public sealed class ExternalDataException : Exception
{
    public ExternalDataException(string message) : base(message) { }
}

public sealed class ExternalHostPolicy
{
    readonly string[] _suffixes;

    public ExternalHostPolicy(params string[] suffixes)
    {
        var cleaned = new List<string>(suffixes.Length);
        foreach (var suffix in suffixes)
        {
            var value = suffix.Trim().TrimStart('.').ToLowerInvariant();
            if (value.Length == 0) throw new ArgumentException("Host vacío", nameof(suffixes));
            cleaned.Add(value);
        }
        _suffixes = cleaned.ToArray();
    }

    public static ExternalHostPolicy Of(params string[] suffixes) => new(suffixes);

    public bool Allows(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        foreach (var suffix in _suffixes)
        {
            if (host == suffix) return true;
            if (host.EndsWith("." + suffix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public Uri Require(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !Allows(uri))
            throw new ExternalDataException("URL externa no permitida.");
        return uri;
    }
}

public static class ExternalHttp
{
    public const long MaxJsonBytes = 2L * 1024 * 1024;
    public const long MaxSearchJsonBytes = 1L * 1024 * 1024;
    public const long MaxLyricsChars = 256L * 1024;
    public const int MaxJsonDepth = 32;
    public const int MaxLyricWords = 20_000;
    public const int MaxRedirects = 4;

    public static readonly JsonDocumentOptions JsonOptions = new()
    {
        MaxDepth = MaxJsonDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static HttpClient CreateClient(TimeSpan timeout, string userAgent, params string[] accept)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli,
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        foreach (var value in accept)
            http.DefaultRequestHeaders.Accept.ParseAdd(value);
        return http;
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Uri uri,
        ExternalHostPolicy policy,
        CancellationToken ct,
        Action<HttpRequestMessage>? configure = null)
    {
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!policy.Allows(current))
                throw new ExternalDataException("Host externo no permitido.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            configure?.Invoke(request);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new ExternalDataException("Redirección externa sin destino.");

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new ExternalDataException("Demasiadas redirecciones externas.");
    }

    static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public static bool IsJsonMediaType(string? mediaType, bool allowJavaScript = false)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;
        var value = mediaType.Trim().ToLowerInvariant();
        if (value is "application/json" or "text/json") return true;
        if (value.EndsWith("+json", StringComparison.Ordinal)) return true;
        return allowJavaScript && value is "text/javascript" or "application/javascript";
    }

    public static void EnsureJsonContentType(HttpResponseMessage response, bool allowJavaScript = false)
    {
        if (!IsJsonMediaType(response.Content.Headers.ContentType?.MediaType, allowJavaScript))
            throw new ExternalDataException("El servicio no devolvió JSON.");
    }

    public static void EnsureContentType(HttpResponseMessage response, params string[] allowedMediaTypes)
    {
        var actual = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(actual))
            throw new ExternalDataException("Respuesta externa sin tipo de contenido.");

        foreach (var allowed in allowedMediaTypes)
        {
            if (string.Equals(actual, allowed, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new ExternalDataException("Tipo de contenido externo no permitido: " + actual);
    }

    public static async Task<byte[]> ReadBytesAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared is not null && declared > maxBytes)
            throw new ExternalDataException("Respuesta externa demasiado grande.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var capacity = declared is > 0 and < 16L * 1024 * 1024 ? (int)declared.Value : 81920;
        using var buffer = new MemoryStream(capacity);
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new ExternalDataException("Respuesta externa demasiado grande.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static async Task<string> ReadStringAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(response, maxBytes, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken ct, bool allowJavaScript = false)
    {
        EnsureJsonContentType(response, allowJavaScript);
        var bytes = await ReadBytesAsync(response, maxBytes, ct).ConfigureAwait(false);
        var json = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsMemory(3)
            : bytes.AsMemory(0);
        try
        {
            return JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ExternalDataException("JSON externo inválido: " + ex.Message);
        }
    }

    public static string ClampQueryValue(string? value, int maxLength = 300)
    {
        var text = value?.Trim() ?? "";
        if (text.Length <= maxLength) return text;
        var limit = maxLength;
        if (char.IsHighSurrogate(text[limit - 1]) && char.IsLowSurrogate(text[limit])) limit--;
        return text[..limit];
    }

    public static bool IsFiniteInRange(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;

    public static string CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Trim();
        if (text.Length > maxLength) text = text[..maxLength];
        return text.Replace('\0', ' ');
    }

    public static bool LyricsTextOk(string? text, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Length > MaxLyricsChars)
        {
            reason = "letras demasiado largas";
            return false;
        }
        var lines = 1;
        foreach (var ch in text)
        {
            if (ch == '\n' && ++lines > 2000)
            {
                reason = "demasiadas líneas";
                return false;
            }
        }
        return true;
    }
}