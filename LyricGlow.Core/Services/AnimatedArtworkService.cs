using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

public sealed class AnimatedCoverResult
{
    public string? Path { get; init; }
    public string Source { get; init; } = "";
    public string? ErrorKey { get; init; }
    public bool Available => Path is not null;
}

public sealed class AnimatedArtworkService
{
    const string SearchUrl = "https://artwork.m8tec.top/api/v1/artwork/search";
    const long MaxVideoBytes = 30L * 1024 * 1024;
    const long MaxPlaylistBytes = 1L * 1024 * 1024;
    static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(7);
    static readonly ExternalHostPolicy SearchHosts = ExternalHostPolicy.Of("m8tec.top");
    static readonly ExternalHostPolicy MediaHosts = ExternalHostPolicy.Of("itunes.apple.com", "m8tec.top");
    static readonly HttpClient Http = CreateClient();
    static readonly Regex CodecAttr = new(@"CODECS=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex ResolutionAttr = new(@"RESOLUTION=(\d+)x(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex MapUri = new(@"#EXT-X-MAP:URI=""([^""]+)""", RegexOptions.Compiled);

    readonly CoverArtService _cover;
    readonly ConcurrentDictionary<string, Task<AnimatedCoverResult>> _inflight = new();
    readonly ConcurrentDictionary<string, AnimatedCoverResult> _memory = new();

    public AnimatedArtworkService(CoverArtService cover)
    {
        _cover = cover;
    }

    public async Task<AnimatedCoverResult> GetAsync(TrackInfo track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Artist))
            return new AnimatedCoverResult { ErrorKey = "AnimNoArtist" };

        if (string.IsNullOrWhiteSpace(track.Album))
        {
            var art = await _cover.GetAsync(track, ct);
            if (string.IsNullOrWhiteSpace(art.Album))
                return new AnimatedCoverResult { ErrorKey = "AnimNoAlbum" };

            track = new TrackInfo
            {
                Title = track.Title,
                Artist = track.Artist,
                Album = art.Album,
                DurationSeconds = track.DurationSeconds,
                SourceAppId = track.SourceAppId,
            };
            Log.Write($"animated: álbum resuelto '{art.Album}' vía {art.Source}");
        }

        var key = CacheKey(track);
        if (_memory.TryGetValue(key, out var cached)) return cached;

        var task = _inflight.GetOrAdd(key, _ => ResolveAsync(track, key, CancellationToken.None));
        try
        {
            var result = await task.WaitAsync(ct);
            if (result.Available) _memory[key] = result;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("animated: " + ex.Message);
            return new AnimatedCoverResult { ErrorKey = "AnimSearchFailed" };
        }
        finally
        {
            if (task.IsCompleted)
                _inflight.TryRemove(key, out _);
            else
                _ = task.ContinueWith(t => _inflight.TryRemove(key, out _), TaskScheduler.Default);
        }
    }

    async Task<AnimatedCoverResult> ResolveAsync(TrackInfo track, string key, CancellationToken ct)
    {
        var path = VideoPath(key);
        if (File.Exists(path) && new FileInfo(path).Length > 100_000 && LooksLikeMp4(path))
        {
            return new AnimatedCoverResult { Path = path, Source = "m8tec" };
        }

        var negative = NegativePath(key);
        if (File.Exists(negative) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(negative) < NegativeTtl)
        {
            return new AnimatedCoverResult { ErrorKey = "CoverNoAnimation" };
        }

        try
        {
            var search = $"{SearchUrl}?{SearchQuery(track)}";
            using var resp = await ExternalHttp.SendAsync(Http, SearchHosts.Require(search), SearchHosts, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                MarkNegative(key);
                return new AnimatedCoverResult { ErrorKey = "CoverNoAnimation" };
            }
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"animated: m8tec HTTP {(int)resp.StatusCode}");
                return new AnimatedCoverResult { ErrorKey = "AnimM8tecUnavailable" };
            }

            using var doc = await ExternalHttp.ReadJsonAsync(resp, ExternalHttp.MaxJsonBytes, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ExternalDataException("m8tec devolvió un objeto inesperado.");

            var masterUrl = GetString(doc.RootElement, "url");
            if (masterUrl.Length == 0)
            {
                MarkNegative(key);
                return new AnimatedCoverResult { ErrorKey = "CoverNoAnimation" };
            }

            var masterUri = MediaHosts.Require(masterUrl);
            var master = await ReadPlaylistAsync(masterUri, ct);
            var variant = PickAvcVariant(master, masterUri);
            if (variant is null || !MediaHosts.Allows(variant.Value.Uri))
            {
                Log.Write("animated: m8tec sin variante AVC válida");
                return new AnimatedCoverResult { ErrorKey = "AnimHevcOnly" };
            }

            var media = await ReadPlaylistAsync(variant.Value.Uri, ct);
            var fileUri = FindMediaFile(media, variant.Value.Uri);
            if (fileUri is null || !MediaHosts.Allows(fileUri))
            {
                Log.Write("animated: m8tec sin archivo de video válido");
                return new AnimatedCoverResult { ErrorKey = "AnimNoPlayableFile" };
            }

            var temp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = await DownloadAsync(fileUri, temp, ct);
            if (bytes <= 0 || !LooksLikeMp4(temp))
            {
                TryDelete(temp);
                Log.Write("animated: archivo MP4 inválido");
                return new AnimatedCoverResult { ErrorKey = "AnimInvalid" };
            }

            var rebased = NormalizeFmp4(temp);
            File.Move(temp, path, true);
            Prune();
            Log.Write($"animated: '{track.DisplayName}' -> {variant.Value.Width}x{variant.Value.Height} " +
                      $"({bytes} bytes{(rebased ? ", línea de tiempo ajustada" : "")})");

            return new AnimatedCoverResult
            {
                Path = path,
                Source = $"m8tec · {variant.Value.Width}×{variant.Value.Height}",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"animated '{track.DisplayName}': " + ex.Message);
            return new AnimatedCoverResult { ErrorKey = "AnimLoadFailed" };
        }
    }

    internal readonly record struct HlsVariant(Uri Uri, int Width, int Height);

    static async Task<string> ReadPlaylistAsync(Uri uri, CancellationToken ct)
    {
        using var resp = await ExternalHttp.SendAsync(Http, uri, MediaHosts, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ExternalDataException($"Playlist HTTP {(int)resp.StatusCode}");
        ExternalHttp.EnsureContentType(resp,
            "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl");
        var text = await ExternalHttp.ReadStringAsync(resp, MaxPlaylistBytes, ct);
        if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal) ||
            text.Count(ch => ch == '\n') > 20_000)
            throw new ExternalDataException("Playlist HLS inválida.");
        return text;
    }

    internal static HlsVariant? PickAvcVariant(string master, Uri baseUri)
    {
        if (master.Length > MaxPlaylistBytes) return null;
        var variants = new List<HlsVariant>();
        var lines = master.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;

            var codec = CodecAttr.Match(line).Groups[1].Value;
            var resolution = ResolutionAttr.Match(line);
            if (!resolution.Success || !codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase))
                continue;

            var next = i + 1;
            while (next < lines.Length)
            {
                var candidate = lines[next].Trim();
                if (candidate.Length > 0 && !candidate.StartsWith("#", StringComparison.Ordinal)) break;
                next++;
            }
            if (next >= lines.Length) continue;

            if (!int.TryParse(resolution.Groups[1].Value, out var width) ||
                !int.TryParse(resolution.Groups[2].Value, out var height))
                continue;

            try
            {
                variants.Add(new HlsVariant(new Uri(baseUri, lines[next].Trim()), width, height));
            }
            catch
            {
            }
            if (variants.Count >= 100) break;
        }

        if (variants.Count == 0) return null;
        var capped = variants.Where(v => v.Width <= 960).OrderByDescending(v => v.Width).ToList();
        if (capped.Count == 0)
            capped = variants.Where(v => v.Width <= 1080).OrderByDescending(v => v.Width).ToList();
        if (capped.Count == 0)
            capped = variants.OrderByDescending(v => v.Width).ToList();
        return capped[0];
    }

    internal static Uri? FindMediaFile(string media, Uri baseUri)
    {
        var map = MapUri.Match(media);
        if (map.Success)
        {
            try
            {
                return new Uri(baseUri, map.Groups[1].Value);
            }
            catch
            {
            }
        }

        foreach (var raw in media.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            try
            {
                return new Uri(baseUri, line);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    static async Task<long> DownloadAsync(Uri uri, string path, CancellationToken ct)
    {
        if (!MediaHosts.Allows(uri)) return 0;
        using var resp = await ExternalHttp.SendAsync(Http, uri, MediaHosts, ct);
        if (!resp.IsSuccessStatusCode) return 0;
        ExternalHttp.EnsureContentType(resp, "video/mp4", "video/quicktime", "application/mp4",
            "application/octet-stream");
        if (resp.Content.Headers.ContentLength is > MaxVideoBytes) return 0;

        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxVideoBytes)
            {
                output.Close();
                TryDelete(path);
                return 0;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    internal static bool NormalizeFmp4(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch
        {
            return false;
        }

        var tracks = new List<FragmentTrack>();
        var mdhd = new List<BoxRef>();
        var tkhd = new List<BoxRef>();
        var mehd = new List<BoxRef>();
        var mvhd = new BoxRef(-1, 0);

        var pos = 0;
        while (TryReadBox(data, pos, data.Length, out _, out var type, out var payload, out var end))
        {
            if (type == "moov")
                ReadMoov(data, payload, end, ref mvhd, mdhd, tkhd, mehd);
            else if (type == "moof")
                ReadMoof(data, payload, end, tracks);
            pos = end;
        }
        if (tracks.Count == 0) return false;

        var first = tracks.Min(t => t.Base);
        if (first <= 0) return false;

        long last = 0;
        foreach (var track in tracks)
            last = Math.Max(last, track.Base + track.Count);
        var total = last - first;
        if (total <= 0) return false;

        foreach (var track in tracks)
            WriteTfdt(data, track.Payload, track.Version, track.Base - first);

        var mediaTs = 0u;
        foreach (var box in mdhd)
        {
            var ts = ReadUInt32(data, box.Payload + 12);
            if (ts > 0)
            {
                mediaTs = ts;
                break;
            }
        }
        if (mediaTs == 0 && mvhd.Payload >= 0) mediaTs = ReadUInt32(data, mvhd.Payload + 12);
        if (mediaTs == 0) mediaTs = 1;

        var movieTs = mvhd.Payload >= 0 ? ReadUInt32(data, mvhd.Payload + 12) : 0;
        if (movieTs == 0) movieTs = mediaTs;
        var movieTicks = (long)Math.Round((double)total * movieTs / mediaTs);

        if (mvhd.Payload >= 0) WriteDuration(data, mvhd, 16, movieTicks);
        foreach (var box in mdhd) WriteDuration(data, box, 16, total);
        foreach (var box in tkhd) WriteDuration(data, box, 20, movieTicks);
        foreach (var box in mehd) WriteDuration(data, box, 4, movieTicks);

        try
        {
            File.WriteAllBytes(path, data);
            Log.Write($"animated: fMP4 ajustado {first / (double)mediaTs:F2}s, " +
                      $"duración {total / (double)mediaTs:F2}s");
            return true;
        }
        catch
        {
            return false;
        }
    }

    readonly record struct BoxRef(int Payload, byte Version);
    readonly record struct FragmentTrack(int Payload, byte Version, long Base, long Count);

    static void ReadMoov(byte[] data, int start, int end, ref BoxRef mvhd,
        List<BoxRef> mdhd, List<BoxRef> tkhd, List<BoxRef> mehd)
    {
        var pos = start;
        while (TryReadBox(data, pos, end, out _, out var type, out var payload, out var boxEnd))
        {
            if (type == "mvhd")
                mvhd = new BoxRef(payload, data[payload]);
            else if (type == "trak")
                ReadTrak(data, payload, boxEnd, mdhd, tkhd);
            else if (type == "mvex")
                ReadMvex(data, payload, boxEnd, mehd);
            pos = boxEnd;
        }
    }

    static void ReadTrak(byte[] data, int start, int end, List<BoxRef> mdhd, List<BoxRef> tkhd)
    {
        var pos = start;
        while (TryReadBox(data, pos, end, out _, out var type, out var payload, out var boxEnd))
        {
            if (type == "tkhd")
                tkhd.Add(new BoxRef(payload, data[payload]));
            else if (type == "mdia")
                ReadMdia(data, payload, boxEnd, mdhd);
            pos = boxEnd;
        }
    }

    static void ReadMdia(byte[] data, int start, int end, List<BoxRef> mdhd)
    {
        var pos = start;
        while (TryReadBox(data, pos, end, out _, out var type, out var payload, out var boxEnd))
        {
            if (type == "mdhd") mdhd.Add(new BoxRef(payload, data[payload]));
            pos = boxEnd;
        }
    }

    static void ReadMvex(byte[] data, int start, int end, List<BoxRef> mehd)
    {
        var pos = start;
        while (TryReadBox(data, pos, end, out _, out var type, out var payload, out var boxEnd))
        {
            if (type == "mehd") mehd.Add(new BoxRef(payload, data[payload]));
            pos = boxEnd;
        }
    }

    static void ReadMoof(byte[] data, int start, int end, List<FragmentTrack> tracks)
    {
        var pos = start;
        while (TryReadBox(data, pos, end, out _, out var type, out var payload, out var boxEnd))
        {
            if (type == "traf") ReadTraf(data, payload, boxEnd, tracks);
            pos = boxEnd;
        }
    }

    static void ReadTraf(byte[] data, int start, int end, List<FragmentTrack> tracks)
    {
        var tfdt = new BoxRef(-1, 0);
        long baseTime = 0;
        long count = 0;

        var pos = start;
        while (TryReadBox(data, pos, end, out _, out var type, out var payload, out var boxEnd))
        {
            if (type == "tfdt")
            {
                tfdt = new BoxRef(payload, data[payload]);
                baseTime = tfdt.Version == 1
                    ? (long)ReadUInt64(data, payload + 4)
                    : ReadUInt32(data, payload + 4);
            }
            else if (type == "trun")
            {
                count += ReadTrunDuration(data, payload, boxEnd);
            }
            pos = boxEnd;
        }

        if (tfdt.Payload >= 0)
            tracks.Add(new FragmentTrack(tfdt.Payload, tfdt.Version, baseTime, count));
    }

    static long ReadTrunDuration(byte[] data, int payload, int end)
    {
        if (payload + 8 > end) return 0;
        var flags = ReadUInt32(data, payload) & 0x00FFFFFF;
        var samples = ReadUInt32(data, payload + 4);
        var cursor = payload + 8;
        if ((flags & 0x000001) != 0) cursor += 4;
        if ((flags & 0x000004) != 0) cursor += 4;
        if ((flags & 0x000100) == 0) return samples;

        long duration = 0;
        for (var i = 0; i < samples; i++)
        {
            if (cursor + 4 > end) break;
            duration += ReadUInt32(data, cursor);
            cursor += 4;
            if ((flags & 0x000200) != 0) cursor += 4;
            if ((flags & 0x000400) != 0) cursor += 4;
            if ((flags & 0x000800) != 0) cursor += 4;
        }
        return duration;
    }

    static void WriteTfdt(byte[] data, int payload, byte version, long value)
    {
        if (version == 1)
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(payload + 4, 8), (ulong)value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(payload + 4, 4), (uint)value);
    }

    static void WriteDuration(byte[] data, BoxRef box, int version0Offset, long value)
    {
        if (version0Offset < 0 || box.Payload < 0) return;
        var offset = box.Payload + version0Offset + (box.Version == 1 && version0Offset != 4 ? 8 : 0);
        if (box.Version == 1)
        {
            if (offset + 8 > data.Length) return;
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, 8), (ulong)value);
        }
        else
        {
            if (value > uint.MaxValue || offset + 4 > data.Length) return;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), (uint)value);
        }
    }

    static bool TryReadBox(byte[] data, int pos, int limit,
        out long size, out string type, out int payload, out int end)
    {
        size = 0;
        type = "";
        payload = 0;
        end = pos;
        if (pos < 0 || pos + 8 > limit) return false;

        var raw = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
        type = Encoding.ASCII.GetString(data, pos + 4, 4);
        var header = 8;
        size = raw;
        if (raw == 1)
        {
            if (pos + 16 > limit) return false;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8, 8));
            header = 16;
        }
        else if (raw == 0)
        {
            size = limit - pos;
        }

        if (size < header || pos + size > limit) return false;
        payload = pos + header;
        end = pos + (int)size;
        return true;
    }

    static uint ReadUInt32(byte[] data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : 0;

    static ulong ReadUInt64(byte[] data, int offset) =>
        offset >= 0 && offset + 8 <= data.Length
            ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8))
            : 0;

    static bool LooksLikeMp4(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[12];
            if (stream.Read(head, 0, head.Length) != head.Length) return false;
            return head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p';
        }
        catch
        {
            return false;
        }
    }

    internal static string SearchQuery(TrackInfo track) =>
        $"artist={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(track.Artist))}" +
        $"&album={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(track.Album))}";

    static string CacheKey(TrackInfo track) =>
        TrackKey.Normalize(track.Artist) + "|" + TrackKey.Normalize(track.Album);

    static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];

    static string VideoPath(string key) =>
        Path.Combine(StoragePaths.CacheDirectory, "animated", Hash(key) + ".v2.mp4");

    static string NegativePath(string key) =>
        Path.Combine(StoragePaths.CacheDirectory, "animated", Hash(key) + ".none");

    static void MarkNegative(string key)
    {
        try
        {
            var path = NegativePath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
        }
    }

    static void Prune()
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(StoragePaths.CacheDirectory, "animated"));
            if (!dir.Exists) return;
            var files = dir.GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var file in files.Skip(20)) file.Delete();
            foreach (var marker in dir.GetFiles("*.none"))
            {
                if (DateTime.UtcNow - marker.LastWriteTimeUtc > TimeSpan.FromDays(30))
                    marker.Delete();
            }
        }
        catch
        {
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? ExternalHttp.CleanText(value.GetString(), 4096) : "";

    static HttpClient CreateClient() =>
        ExternalHttp.CreateClient(TimeSpan.FromSeconds(12),
            "LyricGlow/0.9.2 (personal Windows lyric overlay)", "application/json");
}
