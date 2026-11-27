using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LyricGlow.Models;

namespace LyricGlow.Services;

public sealed class CoverArtResult
{
    public const int HighResolutionSide = 600;

    public byte[]? Bytes { get; init; }
    public string Source { get; init; } = "";
    public string Album { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public bool HasImage => Bytes is { Length: > 0 };
    public int MinSide => Math.Min(PixelWidth, PixelHeight);
    public bool IsHighResolution => MinSide >= HighResolutionSide;
}

public sealed class CoverArtService
{
    const int MinImageBytes = 8_000;
    const long MaxImageBytes = 40L * 1024 * 1024;
    const int MaxImageSide = 4096;
    const long MaxImagePixels = 16L * 1024 * 1024;
    const int MaxSearchResults = 50;
    const int MaxMetaChars = 512;
    const long MaxMetaBytes = 64 * 1024;
    const double MaxDurationSeconds = 24 * 60 * 60;
    static readonly TimeSpan DiskTtl = TimeSpan.FromDays(45);
    static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
    static readonly TimeSpan LowResTtl = TimeSpan.FromSeconds(12);
    const int MemoryMaxEntries = 24;
    const long MemoryMaxBytes = 64L * 1024 * 1024;
    const int AlbumCacheMaxEntries = 32;

    static readonly ExternalHostPolicy ItunesHosts = ExternalHostPolicy.Of("itunes.apple.com");
    static readonly ExternalHostPolicy DeezerHosts = ExternalHostPolicy.Of("deezer.com");
    static readonly ExternalHostPolicy ArtworkHosts = ExternalHostPolicy.Of("mzstatic.com", "dzcdn.net");
    static readonly HttpClient Http = CreateClient();
    static readonly Regex FeatSuffix = new(
        @"\s*[\(\[](?:feat|ft|with)\.?\s[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EditionSuffix = new(
        @"\s*[-–—]\s*(?:remaster(?:ed)?|deluxe|bonus|single version|radio edit|explicit)[^\(\)]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ArtistFeat = new(
        @"\s+(?:feat|ft)\.?\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ArtworkSize = new(
        @"/(\d+)x(\d+)(bb)?\.(jpg|jpeg|png)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> VariantWords = new(StringComparer.Ordinal)
    {
        "remix", "remixed", "live", "version", "edit", "acoustic", "acustico",
        "instrumental", "extended", "radio", "remaster", "remastered", "deluxe",
        "bonus", "single", "feat", "ft", "with", "from", "soundtrack", "en",
        "vivo", "original", "mix", "sped", "slowed",
    };
    static readonly HashSet<string> BadArtistTokens = new(StringComparer.Ordinal)
    {
        "tribute", "cover", "karaoke", "lullaby", "rockabye", "made",
        "famous", "style", "inspired", "instrumental",
    };

    readonly IMediaArtworkProvider _media;
    readonly IImageDecoder _decoder;
    readonly ConcurrentDictionary<string, Task<CoverArtResult>> _inflight = new();
    readonly Dictionary<string, (CoverArtResult Result, DateTime At)> _memory = new();
    long _memoryBytes;
    readonly Dictionary<string, (CoverArtResult Result, long Seq)> _albumCache = new();
    long _albumSeq;
    readonly object _cacheLock = new();
    readonly object _thumbLock = new();
    byte[]? _lastThumbBytes;
    string _lastThumbTrackKey = "";
    string _lastThumbAlbumKey = "";
    DateTime _itunesCooldownUntil = DateTime.MinValue;

    public CoverArtService(IMediaArtworkProvider media, IImageDecoder decoder)
    {
        _media = media;
        _decoder = decoder;
    }

    public async Task<CoverArtResult> GetAsync(TrackInfo track, CancellationToken ct)
    {
        var key = CacheKey(track);
        lock (_cacheLock)
        {
            if (_memory.TryGetValue(key, out var cached))
            {
                var ttl = MemoryTtl(cached.Result);
                if (DateTime.UtcNow - cached.At < ttl) return cached.Result;
            }
        }

        var task = _inflight.GetOrAdd(key, _ => ResolveAsync(track, CancellationToken.None));
        try
        {
            var result = await task.WaitAsync(ct);
            lock (_cacheLock) RememberMemory(key, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("cover: " + ex.Message);
            return new CoverArtResult();
        }
        finally
        {
            // Solo se quita LA tarea que se usó aquí: si ya terminó y otro
            // resolve reemplazó la entrada, borrarla mataría la tarea nueva.
            if (task.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Task<CoverArtResult>>(key, task));
            else
                _ = task.ContinueWith(_ =>
                    _inflight.TryRemove(new KeyValuePair<string, Task<CoverArtResult>>(key, task)),
                    TaskScheduler.Default);
        }
    }

    static TimeSpan MemoryTtl(CoverArtResult result) =>
        !result.HasImage ? NegativeTtl :
        result.IsHighResolution ? TimeSpan.FromHours(6) : LowResTtl;

    // Debe llamarse con _cacheLock tomado.
    void RememberMemory(string key, CoverArtResult result)
    {
        RemoveMemoryLocked(key);
        _memory[key] = (result, DateTime.UtcNow);
        _memoryBytes += ImageSizeOf(result);
        TrimMemory();
    }

    void TrimMemory()
    {
        while (_memory.Count > MemoryMaxEntries || _memoryBytes > MemoryMaxBytes)
        {
            string? oldestKey = null;
            var oldestAt = DateTime.MaxValue;
            foreach (var entry in _memory)
            {
                if (entry.Value.At > oldestAt) continue;
                oldestAt = entry.Value.At;
                oldestKey = entry.Key;
            }
            // Si dos entradas comparten marca de tiempo no se puede avanzar;
            // salir evita un bucle infinito y deja el tope de entradas como red.
            if (oldestKey is null) return;
            RemoveMemoryLocked(oldestKey);
        }
    }

    // Quita la entrada y descuenta sus bytes UNA sola vez.
    void RemoveMemoryLocked(string key)
    {
        if (!_memory.Remove(key, out var removed)) return;
        _memoryBytes -= ImageSizeOf(removed.Result);
        if (_memoryBytes < 0) _memoryBytes = 0;
    }

    static long ImageSizeOf(CoverArtResult result) => result.Bytes?.Length ?? 0;

    async Task<CoverArtResult> ResolveAsync(TrackInfo track, CancellationToken ct)
    {
        var thumb = ReadPlayerThumb(track, await _media.ReadThumbnailAsync(track.Title, track.Artist));

        // La carátula del reproductor manda si ya tiene resolución suficiente;
        // iTunes/Deezer solo se consultan cuando la miniatura es pequeña o falta.
        if (thumb.IsHighResolution)
        {
            RememberAlbum(track, thumb, track.Album);
            Log.Write($"cover: '{track.DisplayName}' -> reproductor " +
                      $"{thumb.PixelWidth}x{thumb.PixelHeight} (calidad suficiente)");
            return thumb;
        }

        var albumKey = AlbumCacheKey(track.Album, track.Artist);
        if (albumKey.Length > 0 && TryGetAlbum(albumKey, out var albumArt))
        {
            Log.Write($"cover: '{track.DisplayName}' -> misma carátula del álbum " +
                      $"({albumArt.Source} {albumArt.PixelWidth}x{albumArt.PixelHeight})");
            return albumArt;
        }

        var disk = LoadDisk(track);
        if (disk.HasImage)
        {
            RememberAlbum(track, disk, string.IsNullOrWhiteSpace(track.Album) ? disk.Album : track.Album);
            Log.Write($"cover: '{track.DisplayName}' -> {disk.Source} en caché " +
                      $"{disk.PixelWidth}x{disk.PixelHeight}");
            return disk;
        }

        var itunes = await TryItunesAsync(track, ct);
        if (itunes.HasImage)
        {
            var chosen = PickBetter(itunes, thumb);
            if (ReferenceEquals(chosen, itunes)) SaveDisk(track, "itunes", itunes);
            RememberAlbum(track, chosen, string.IsNullOrWhiteSpace(track.Album) ? chosen.Album : track.Album);
            Log.Write($"cover: '{track.DisplayName}' -> iTunes {itunes.PixelWidth}x{itunes.PixelHeight} " +
                      $"'{itunes.Title}' / '{itunes.Artist}' / '{itunes.Album}'" +
                      $" (reproductor {(thumb.HasImage ? $"{thumb.PixelWidth}x{thumb.PixelHeight}" : "sin miniatura")})");
            return chosen;
        }

        var deezer = await TryDeezerAsync(track, ct);
        if (deezer.HasImage)
        {
            var chosen = PickBetter(deezer, thumb);
            if (ReferenceEquals(chosen, deezer)) SaveDisk(track, "deezer", deezer);
            RememberAlbum(track, chosen, string.IsNullOrWhiteSpace(track.Album) ? chosen.Album : track.Album);
            Log.Write($"cover: '{track.DisplayName}' -> Deezer {deezer.PixelWidth}x{deezer.PixelHeight} " +
                      $"'{deezer.Title}' / '{deezer.Artist}' / '{deezer.Album}'" +
                      $" (reproductor {(thumb.HasImage ? $"{thumb.PixelWidth}x{thumb.PixelHeight}" : "sin miniatura")})");
            return chosen;
        }

        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            var albumCover = await TryItunesAlbumAsync(track, ct);
            if (!albumCover.HasImage) albumCover = await TryDeezerAlbumAsync(track, ct);
            if (albumCover.HasImage)
            {
                RememberAlbum(track, albumCover, track.Album);
                Log.Write($"cover: '{track.DisplayName}' -> {albumCover.Source} álbum " +
                          $"{albumCover.PixelWidth}x{albumCover.PixelHeight} " +
                          $"'{albumCover.Album}' / '{albumCover.Artist}'");
                return albumCover;
            }
        }

        if (thumb.HasImage)
        {
            RememberAlbum(track, thumb, track.Album);
            Log.Write($"cover: '{track.DisplayName}' -> reproductor " +
                      $"{thumb.PixelWidth}x{thumb.PixelHeight} (sin respaldo externo)");
            return thumb;
        }

        Log.Write($"cover: '{track.DisplayName}' -> sin carátula");
        return new CoverArtResult();
    }

    CoverArtResult ReadPlayerThumb(TrackInfo track, byte[]? bytes)
    {
        var hasBytes = bytes is { Length: > 0 };
        var albumKey = NormalizeMatch(track.Album);
        var stale = false;
        lock (_thumbLock)
        {
            if (hasBytes && _lastThumbBytes is { Length: > 0 } &&
                track.Key != _lastThumbTrackKey &&
                bytes.AsSpan().SequenceEqual(_lastThumbBytes) &&
                !(albumKey.Length > 0 && albumKey == _lastThumbAlbumKey))
            {
                stale = true;
            }

            if (hasBytes)
            {
                _lastThumbBytes = bytes;
                _lastThumbTrackKey = track.Key;
                _lastThumbAlbumKey = albumKey;
            }
        }

        if (stale)
        {
            Log.Write($"cover: '{track.DisplayName}' -> miniatura del reproductor desfasada (se ignora)");
            return new CoverArtResult();
        }

        return MakeResult(bytes, "Reproductor", track.Album, track.Artist, track.Title);
    }

    void RememberAlbum(TrackInfo track, CoverArtResult result, string album)
    {
        if (!result.HasImage) return;
        if (result.Source == "Reproductor" && !result.IsHighResolution) return;
        var key = AlbumCacheKey(album, track.Artist);
        if (key.Length == 0) return;
        lock (_cacheLock)
        {
            // Seq = último uso. Se conserva el de la entrada previa al reemplazar,
            // para que "ganar" una carátula no la convierta en la más reciente.
            long seq;
            if (_albumCache.TryGetValue(key, out var existing))
            {
                seq = existing.Seq;
                if (existing.Result.HasImage && result.MinSide < existing.Result.MinSide) return;
            }
            else
            {
                seq = ++_albumSeq;
            }
            _albumCache[key] = (result, seq);

            while (_albumCache.Count > AlbumCacheMaxEntries)
            {
                var oldestKey = "";
                var oldestSeq = long.MaxValue;
                foreach (var entry in _albumCache)
                {
                    if (entry.Value.Seq >= oldestSeq) continue;
                    oldestSeq = entry.Value.Seq;
                    oldestKey = entry.Key;
                }
                if (oldestKey.Length == 0) return;
                _albumCache.Remove(oldestKey);
            }
        }
    }

    // Debe llamarse con _cacheLock tomado.
    void TouchAlbumLocked(string key)
    {
        if (_albumCache.TryGetValue(key, out var entry))
            _albumCache[key] = (entry.Result, ++_albumSeq);
    }

    bool TryGetAlbum(string albumKey, out CoverArtResult albumArt)
    {
        lock (_cacheLock)
        {
            if (!_albumCache.TryGetValue(albumKey, out var entry) || !entry.Result.HasImage)
            {
                albumArt = new CoverArtResult();
                return false;
            }
            TouchAlbumLocked(albumKey);
            albumArt = entry.Result;
            return true;
        }
    }

    internal static string CacheKey(TrackInfo track) =>
        track.Key + "|" + NormalizeMatch(track.Album);

    static string AlbumCacheKey(string album, string artist)
    {
        var normalizedAlbum = NormalizeMatch(album);
        if (normalizedAlbum.Length == 0) return "";
        var primaryArtist = PrimaryArtistKey(artist);
        return primaryArtist + "|" + normalizedAlbum;
    }

    static string PrimaryArtistKey(string? artist)
    {
        var capped = artist is { Length: > 512 } ? artist[..512] : artist ?? "";
        var cleaned = ArtistFeat.Replace(capped, " ");
        var folded = FoldAccents(cleaned).ToLowerInvariant();
        var first = Regex.Split(folded, @"\s*(?:,|&|/|\+|\b(?:y|and|x|vs\.?)\b)\s*")
            .FirstOrDefault(part => part.Trim().Length > 0);
        if (first is not null &&
            first.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2)
            return NormalizeMatch(first);
        return NormalizeMatch(artist);
    }

    static CoverArtResult PickBetter(CoverArtResult candidate, CoverArtResult current)
    {
        if (!candidate.HasImage) return current;
        if (!current.HasImage) return candidate;
        return candidate.MinSide >= current.MinSide ? candidate : current;
    }

    async Task<CoverArtResult> TryItunesAsync(TrackInfo track, CancellationToken ct, bool includeAlbum = true)
    {
        if (DateTime.UtcNow < _itunesCooldownUntil) return new CoverArtResult();

        var term = string.Join(" ", new[] { track.Artist, track.Title, includeAlbum ? track.Album : "" }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (term.Length == 0) return new CoverArtResult();

        var url = "https://itunes.apple.com/search?term=" +
                  Uri.EscapeDataString(ExternalHttp.ClampQueryValue(term)) + "&entity=song&limit=10";
        try
        {
            var uri = ItunesHosts.Require(url);
            using var resp = await ExternalHttp.SendAsync(Http, uri, ItunesHosts, ct);
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                _itunesCooldownUntil = DateTime.UtcNow.AddMinutes(2);
                Log.Write($"cover: iTunes limit {(int)resp.StatusCode}; usando Deezer");
                return new CoverArtResult();
            }
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"cover: iTunes HTTP {(int)resp.StatusCode}");
                return new CoverArtResult();
            }

            using var doc = await ExternalHttp.ReadJsonAsync(
                resp, ExternalHttp.MaxSearchJsonBytes, ct, allowJavaScript: true);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return new CoverArtResult();

            string? artwork = null;
            var best = double.NegativeInfinity;
            var bestTitle = "";
            var bestArtist = "";
            var bestAlbum = "";
            var count = 0;
            foreach (var item in results.EnumerateArray())
            {
                if (++count > MaxSearchResults) break;
                if (item.ValueKind != JsonValueKind.Object) continue;

                var title = GetString(item, "trackName");
                var artist = GetString(item, "artistName");
                var album = GetString(item, "collectionName");
                var candidate = GetString(item, "artworkUrl100");
                if (candidate.Length == 0 || !IsArtworkUrl(candidate)) continue;

                double? duration = null;
                if (item.TryGetProperty("trackTimeMillis", out var ms) &&
                    ms.TryGetDouble(out var value) && ExternalHttp.IsFiniteInRange(value, 0, MaxDurationSeconds * 1000))
                    duration = value / 1000.0;

                var score = Score(track, title, artist, album, duration, out var titleOk);
                if (!titleOk || score <= best) continue;
                best = score;
                artwork = candidate;
                bestTitle = title;
                bestArtist = artist;
                bestAlbum = album;
            }

            if (artwork is null || best < 0)
            {
                if (includeAlbum && !string.IsNullOrWhiteSpace(track.Album))
                {
                    Log.Write($"cover: iTunes sin coincidencia con álbum para '{track.DisplayName}'; reintento artista+título");
                    return await TryItunesAsync(track, ct, includeAlbum: false);
                }
                Log.Write($"cover: iTunes sin coincidencia para '{track.DisplayName}'");
                return new CoverArtResult();
            }

            byte[]? bytes = null;
            foreach (var size in new[] { 3000, 1200, 600 })
            {
                bytes = await DownloadImageAsync(ArtworkAt(artwork, size), ct);
                if (bytes is not null) break;
            }
            if (bytes is null)
            {
                Log.Write($"cover: iTunes sin imagen descargable para '{track.DisplayName}'");
                return new CoverArtResult();
            }
            return MakeResult(bytes, "iTunes", bestAlbum, bestArtist, bestTitle);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("cover iTunes: " + ex.Message);
            return new CoverArtResult();
        }
    }

    async Task<CoverArtResult> TryDeezerAsync(TrackInfo track, CancellationToken ct, bool includeAlbum = true)
    {
        var query = string.Join(" ", new[] { track.Title, track.Artist, includeAlbum ? track.Album : "" }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (query.Length == 0) return new CoverArtResult();

        var url = "https://api.deezer.com/search?q=" +
                  Uri.EscapeDataString(ExternalHttp.ClampQueryValue(query)) + "&limit=10";
        try
        {
            var uri = DeezerHosts.Require(url);
            using var resp = await ExternalHttp.SendAsync(Http, uri, DeezerHosts, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"cover: Deezer HTTP {(int)resp.StatusCode}");
                return new CoverArtResult();
            }

            using var doc = await ExternalHttp.ReadJsonAsync(resp, ExternalHttp.MaxSearchJsonBytes, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return new CoverArtResult();

            string? artwork = null;
            var best = double.NegativeInfinity;
            var bestTitle = "";
            var bestArtist = "";
            var bestAlbum = "";
            var count = 0;
            foreach (var item in data.EnumerateArray())
            {
                if (++count > MaxSearchResults) break;
                if (item.ValueKind != JsonValueKind.Object) continue;

                var title = GetString(item, "title");
                var artist = item.TryGetProperty("artist", out var artistNode)
                    ? GetString(artistNode, "name") : "";
                var album = item.TryGetProperty("album", out var albumNode)
                    ? GetString(albumNode, "title") : "";
                if (!item.TryGetProperty("album", out var albumArt)) continue;

                var candidate = GetString(albumArt, "cover_xl");
                if (candidate.Length == 0) candidate = GetString(albumArt, "cover_big");
                if (candidate.Length == 0 || !IsArtworkUrl(candidate)) continue;

                double? duration = null;
                if (item.TryGetProperty("duration", out var d) &&
                    d.TryGetDouble(out var value) && ExternalHttp.IsFiniteInRange(value, 0, MaxDurationSeconds))
                    duration = value;

                var score = Score(track, title, artist, album, duration, out var titleOk);
                if (!titleOk || score <= best) continue;
                best = score;
                artwork = candidate;
                bestTitle = title;
                bestArtist = artist;
                bestAlbum = album;
            }

            if (artwork is null || best < 0)
            {
                if (includeAlbum && !string.IsNullOrWhiteSpace(track.Album))
                {
                    Log.Write($"cover: Deezer sin coincidencia con álbum para '{track.DisplayName}'; reintento artista+título");
                    return await TryDeezerAsync(track, ct, includeAlbum: false);
                }
                return new CoverArtResult();
            }
            var bytes = await DownloadImageAsync(artwork, ct);
            return MakeResult(bytes, "Deezer", bestAlbum, bestArtist, bestTitle);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("cover Deezer: " + ex.Message);
            return new CoverArtResult();
        }
    }

    async Task<CoverArtResult> TryItunesAlbumAsync(TrackInfo track, CancellationToken ct)
    {
        if (DateTime.UtcNow < _itunesCooldownUntil || string.IsNullOrWhiteSpace(track.Album))
            return new CoverArtResult();

        var term = string.Join(" ", new[] { track.Artist, track.Album }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (term.Length == 0) return new CoverArtResult();

        var url = "https://itunes.apple.com/search?term=" +
                  Uri.EscapeDataString(ExternalHttp.ClampQueryValue(term)) + "&entity=album&limit=5";
        try
        {
            var uri = ItunesHosts.Require(url);
            using var resp = await ExternalHttp.SendAsync(Http, uri, ItunesHosts, ct);
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                _itunesCooldownUntil = DateTime.UtcNow.AddMinutes(2);
                return new CoverArtResult();
            }
            if (!resp.IsSuccessStatusCode) return new CoverArtResult();

            using var doc = await ExternalHttp.ReadJsonAsync(
                resp, ExternalHttp.MaxSearchJsonBytes, ct, allowJavaScript: true);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return new CoverArtResult();

            string? artwork = null;
            var best = double.NegativeInfinity;
            var bestAlbum = "";
            var bestArtist = "";
            var count = 0;
            foreach (var item in results.EnumerateArray())
            {
                if (++count > MaxSearchResults) break;
                if (item.ValueKind != JsonValueKind.Object) continue;

                var album = GetString(item, "collectionName");
                var artist = GetString(item, "artistName");
                var candidate = GetString(item, "artworkUrl100");
                if (candidate.Length == 0 || !IsArtworkUrl(candidate)) continue;

                var score = ScoreAlbum(track, album, artist);
                if (score <= best) continue;
                best = score;
                artwork = candidate;
                bestAlbum = album;
                bestArtist = artist;
            }

            if (artwork is null) return new CoverArtResult();

            byte[]? bytes = null;
            foreach (var size in new[] { 3000, 1200, 600 })
            {
                bytes = await DownloadImageAsync(ArtworkAt(artwork, size), ct);
                if (bytes is not null) break;
            }
            return bytes is null
                ? new CoverArtResult()
                : MakeResult(bytes, "iTunes", bestAlbum, bestArtist, "");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("cover iTunes álbum: " + ex.Message);
            return new CoverArtResult();
        }
    }

    async Task<CoverArtResult> TryDeezerAlbumAsync(TrackInfo track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Album)) return new CoverArtResult();

        var query = string.Join(" ", new[] { track.Artist, track.Album }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (query.Length == 0) return new CoverArtResult();

        var url = "https://api.deezer.com/search/album?q=" +
                  Uri.EscapeDataString(ExternalHttp.ClampQueryValue(query)) + "&limit=5";
        try
        {
            var uri = DeezerHosts.Require(url);
            using var resp = await ExternalHttp.SendAsync(Http, uri, DeezerHosts, ct);
            if (!resp.IsSuccessStatusCode) return new CoverArtResult();

            using var doc = await ExternalHttp.ReadJsonAsync(resp, ExternalHttp.MaxSearchJsonBytes, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return new CoverArtResult();

            string? artwork = null;
            var best = double.NegativeInfinity;
            var bestAlbum = "";
            var bestArtist = "";
            var count = 0;
            foreach (var item in data.EnumerateArray())
            {
                if (++count > MaxSearchResults) break;
                if (item.ValueKind != JsonValueKind.Object) continue;

                var album = GetString(item, "title");
                var artist = item.TryGetProperty("artist", out var artistNode)
                    ? GetString(artistNode, "name") : "";
                var candidate = GetString(item, "cover_xl");
                if (candidate.Length == 0) candidate = GetString(item, "cover_big");
                if (candidate.Length == 0 || !IsArtworkUrl(candidate)) continue;

                var score = ScoreAlbum(track, album, artist);
                if (score <= best) continue;
                best = score;
                artwork = candidate;
                bestAlbum = album;
                bestArtist = artist;
            }

            if (artwork is null) return new CoverArtResult();
            var bytes = await DownloadImageAsync(artwork, ct);
            return bytes is null
                ? new CoverArtResult()
                : MakeResult(bytes, "Deezer", bestAlbum, bestArtist, "");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("cover Deezer álbum: " + ex.Message);
            return new CoverArtResult();
        }
    }

    static double ScoreAlbum(TrackInfo track, string album, string artist)
    {
        if (AlbumMatch(track.Album, album) != true) return double.NegativeInfinity;
        if (!ArtistMatch(track.Artist, artist)) return double.NegativeInfinity;

        var score = 4.0;
        if (NormalizeMatch(album) == NormalizeMatch(track.Album)) score += 2;
        if (NormalizeMatch(artist) == NormalizeMatch(track.Artist)) score += 2;
        return score;
    }

    static async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ArtworkHosts.Allows(uri))
            {
                Log.Write("cover download: host de imagen no permitido");
                return null;
            }

            using var resp = await ExternalHttp.SendAsync(Http, uri, ArtworkHosts, ct);
            if (!resp.IsSuccessStatusCode) return null;
            ExternalHttp.EnsureContentType(resp, "image/jpeg", "image/png", "image/gif");
            if (resp.Content.Headers.ContentLength is > MaxImageBytes) return null;

            var bytes = await ExternalHttp.ReadBytesAsync(resp, MaxImageBytes, ct);
            return bytes.Length >= MinImageBytes && LooksLikeImage(bytes) ? bytes : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("cover download: " + ex.Message);
            return null;
        }
    }

    internal static double Score(
        TrackInfo track, string title, string artist, string album, double? duration, out bool titleOk)
    {
        titleOk = false;
        var wantedTitle = NormalizeMatch(track.Title);
        var foundTitle = NormalizeMatch(title);
        if (wantedTitle.Length < 3 || foundTitle.Length < 3) return double.NegativeInfinity;

        var exactTitle = wantedTitle == foundTitle;
        var variantTitle = !exactTitle && IsVariantMatch(wantedTitle, foundTitle);
        if (!exactTitle && !variantTitle) return double.NegativeInfinity;

        if (!ArtistMatch(track.Artist, artist)) return double.NegativeInfinity;

        var albumMatch = AlbumMatch(track.Album, album);
        if (albumMatch == false) return double.NegativeInfinity;

        double? durationDiff = null;
        if (duration is > 0 && track.DurationSeconds is > 0)
        {
            durationDiff = Math.Abs(duration.Value - track.DurationSeconds.Value);
            if (durationDiff > 15) return double.NegativeInfinity;
        }

        // Una variante (remix/live/etc.) solo se acepta si el álbum o la
        // duración confirman que es la misma grabación.
        if (!exactTitle && !(albumMatch == true || (durationDiff is not null && durationDiff <= 5)))
            return double.NegativeInfinity;

        titleOk = true;
        var score = exactTitle ? 6 : 3;

        var wantedArtist = NormalizeMatch(track.Artist);
        var foundArtist = NormalizeMatch(artist);
        score += wantedArtist == foundArtist ? 5 : 3;

        if (durationDiff is not null)
        {
            if (durationDiff <= 2) score += 5;
            else if (durationDiff <= 5) score += 2;
        }

        if (albumMatch == true) score += 2;
        return score;
    }

    internal static bool ArtistMatch(string wantedRaw, string foundRaw)
    {
        var wantedCapped = wantedRaw is { Length: > 512 } ? wantedRaw[..512] : wantedRaw;
        var foundCapped = foundRaw is { Length: > 512 } ? foundRaw[..512] : foundRaw;
        var wanted = NormalizeMatch(ArtistFeat.Replace(wantedCapped ?? "", " "));
        var found = NormalizeMatch(ArtistFeat.Replace(foundCapped ?? "", " "));
        if (wanted.Length == 0 || found.Length == 0) return false;
        if (wanted == found) return true;

        var wantedTokens = ArtistTokens(wanted);
        var foundTokens = ArtistTokens(found);
        if (wantedTokens.Count == 0 || foundTokens.Count == 0) return false;
        if (wantedTokens.Count == 1 || foundTokens.Count == 1) return false;
        if (!wantedTokens.IsSubsetOf(foundTokens) && !foundTokens.IsSubsetOf(wantedTokens))
            return false;
        if (foundTokens.Any(BadArtistTokens.Contains)) return false;
        return wantedTokens.Intersect(foundTokens).Count() >= 2;
    }

    static bool? AlbumMatch(string wanted, string found)
    {
        var wantedAlbum = NormalizeMatch(wanted);
        var foundAlbum = NormalizeMatch(found);
        if (wantedAlbum.Length == 0 || foundAlbum.Length == 0) return null;
        if (wantedAlbum == foundAlbum) return true;

        var wantedTokens = TextTokens(wantedAlbum);
        var foundTokens = TextTokens(foundAlbum);
        return wantedTokens.Count > 0 && foundTokens.Count > 0 &&
               (wantedTokens.IsSubsetOf(foundTokens) || foundTokens.IsSubsetOf(wantedTokens));
    }

    internal static bool SameAlbum(string? wanted, string? found) =>
        AlbumMatch(wanted ?? "", found ?? "") == true;

    static bool IsVariantMatch(string wanted, string found)
    {
        var longer = wanted.Length > found.Length ? wanted : found;
        var shorter = wanted.Length > found.Length ? found : wanted;
        if (shorter.Length < 3) return false;

        string extra;
        if (longer.StartsWith(shorter + " ", StringComparison.Ordinal))
            extra = longer[shorter.Length..].Trim();
        else if (longer.EndsWith(" " + shorter, StringComparison.Ordinal))
            extra = longer[..^shorter.Length].Trim();
        else return false;

        var extraTokens = TextTokens(extra);
        return extraTokens.Count > 0 && extraTokens.All(VariantWords.Contains);
    }

    static HashSet<string> ArtistTokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token is not ("feat" or "ft" or "with" or "and" or "y" or "x" or "vs"))
            .ToHashSet(StringComparer.Ordinal);

    static HashSet<string> TextTokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    static string NormalizeMatch(string? value)
    {
        var capped = value is { Length: > 512 } ? value[..512] : value ?? "";
        var text = FeatSuffix.Replace(capped, " ");
        text = EditionSuffix.Replace(text, " ");
        text = FoldAccents(text).ToLowerInvariant().Replace('&', ' ');
        text = Regex.Replace(text, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    static string FoldAccents(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    static string ArtworkAt(string artworkUrl, int size) =>
        ArtworkSize.Replace(artworkUrl, $"/{size}x{size}bb.$4");

    static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? ExternalHttp.CleanText(value.GetString(), MaxMetaChars) : "";

    static bool IsArtworkUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && ArtworkHosts.Allows(uri);

    internal static bool IsAcceptableImageSize(int width, int height) =>
        width > 0 && height > 0 &&
        width <= MaxImageSide && height <= MaxImageSide &&
        (long)width * height <= MaxImagePixels;

    CoverArtResult MakeResult(
        byte[]? bytes, string source, string album = "", string artist = "", string title = "")
    {
        if (bytes is not { Length: > 0 }) return new CoverArtResult();
        var (width, height) = ImageSize(bytes);
        if (!IsAcceptableImageSize(width, height))
        {
            Log.Write($"cover: imagen rechazada {width}x{height}");
            return new CoverArtResult();
        }
        return new CoverArtResult
        {
            Bytes = bytes,
            Source = source,
            Album = album,
            Artist = artist,
            Title = title,
            PixelWidth = width,
            PixelHeight = height,
        };
    }

    (int Width, int Height) ImageSize(byte[] bytes)
    {
        try
        {
            return _decoder.GetSize(bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    static bool LooksLikeImage(byte[] bytes) =>
        bytes.Length >= 4 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xD8) ||
         (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
         (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46));

    static string CachePath(string key, string provider) =>
        Path.Combine(StoragePaths.CacheDirectory, "covers", Hash(key) + ".v2." + provider + ".img");

    static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    CoverArtResult LoadDisk(TrackInfo track)
    {
        foreach (var (provider, source) in new[]
                 {
                     ("itunes", "iTunes"),
                     ("deezer", "Deezer"),
                 })
        {
            try
            {
                var path = CachePath(CacheKey(track), provider);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MinImageBytes || info.Length > MaxImageBytes) continue;
                if (DateTime.UtcNow - info.LastWriteTimeUtc > DiskTtl) continue;
                var bytes = File.ReadAllBytes(path);
                if (!LooksLikeImage(bytes)) continue;

                var album = "";
                var artist = "";
                var title = "";
                var metaPath = path + ".json";
                if (File.Exists(metaPath))
                {
                    try
                    {
                        var metaInfo = new FileInfo(metaPath);
                        if (metaInfo.Length is > 0 and <= MaxMetaBytes)
                        {
                            using var meta = JsonDocument.Parse(File.ReadAllBytes(metaPath),
                                ExternalHttp.JsonOptions);
                            album = GetString(meta.RootElement, "album");
                            artist = GetString(meta.RootElement, "artist");
                            title = GetString(meta.RootElement, "title");
                        }
                    }
                    catch
                    {
                    }
                }
                return MakeResult(bytes, source, album, artist, title);
            }
            catch
            {
            }
        }

        return new CoverArtResult();
    }

    static void SaveDisk(TrackInfo track, string provider, CoverArtResult result)
    {
        try
        {
            var path = CachePath(CacheKey(track), provider);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, result.Bytes!);
            File.Move(temp, path, true);
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
            {
                source = result.Source,
                album = result.Album,
                artist = result.Artist,
                title = result.Title,
            }));
            PruneCovers();
        }
        catch (Exception ex)
        {
            Log.Write("cover cache: " + ex.Message);
        }
    }

    static void PruneCovers()
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(StoragePaths.CacheDirectory, "covers"));
            if (!dir.Exists) return;
            var files = dir.GetFiles("*.img").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var file in files.Skip(300))
            {
                file.Delete();
                var meta = file.FullName + ".json";
                if (File.Exists(meta)) File.Delete(meta);
            }
        }
        catch
        {
        }
    }

    static HttpClient CreateClient() =>
        ExternalHttp.CreateClient(TimeSpan.FromSeconds(8),
            "LyricGlow/0.9.2 (personal Windows lyric overlay)", "application/json", "image/*");
}
