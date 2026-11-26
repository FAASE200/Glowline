using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LyricGlow.Services;

public sealed record ReleaseAsset(string Name, long Id, long Size, string? Digest, string? DownloadUrl = null);
public sealed record ReleaseInfo(Version Version, string Tag, IReadOnlyList<ReleaseAsset> Assets, string? HtmlUrl);

public sealed class ReleaseClient
{
    public const string Owner = "FAASE200";
    public const string Repo = "Glowline";

    const string WebBase = "https://github.com/" + Owner + "/" + Repo;
    const string ApiBase = "https://api.github.com/repos/" + Owner + "/" + Repo;
    const string ManifestUrl = WebBase + "/releases/latest/download/manifest.json";

    readonly HttpClient _api;
    readonly HttpClient _downloads;

    public ReleaseClient()
    {
        _api = BuildClient(TimeSpan.FromSeconds(25), json: true);
        _downloads = BuildClient(TimeSpan.FromMinutes(20), json: false);
    }

    HttpClient BuildClient(TimeSpan timeout, bool json)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Glowline-Updater/2.0");
        if (json)
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        return client;
    }

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        var manifest = await TryGetManifestAsync(ct);
        if (manifest is not null) return manifest;
        return await GetLatestFromApiAsync(ct);
    }

    async Task<ReleaseInfo?> TryGetManifestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _downloads.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseManifest(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    async Task<ReleaseInfo?> GetLatestFromApiAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _api.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = GetString(root, "tag_name");
        var version = ParseVersion(tag);
        if (version is null) throw new InvalidDataException($"Versión de release no reconocida: {tag}");
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nodes.EnumerateArray())
            {
                assets.Add(new ReleaseAsset(
                    GetString(item, "name"),
                    item.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0,
                    item.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                    GetNullableString(item, "digest")));
            }
        }
        return new ReleaseInfo(version, tag, assets, GetNullableString(root, "html_url"));
    }

    public static ReleaseInfo ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = GetStringAny(root, "tag", "tag_name").Trim();
        var versionText = GetStringAny(root, "version", "tag");
        var version = ParseVersion(versionText) ?? ParseVersion(tag);
        if (version is null) throw new InvalidDataException($"Versión de manifest no reconocida: {versionText}");
        if (tag.Length == 0) tag = "v" + Format(version);

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nodes.EnumerateArray())
            {
                var name = GetStringAny(item, "Name", "name");
                if (name.Length == 0) continue;
                var sha = GetStringAny(item, "Sha256", "sha256", "Digest", "digest");
                var digest = IsSha256Digest(sha)
                    ? (sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? sha.ToLowerInvariant()
                        : "sha256:" + sha.ToLowerInvariant())
                    : null;
                var url = WebBase + "/releases/download/" +
                          Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);
                assets.Add(new ReleaseAsset(
                    name,
                    0,
                    GetInt64Any(item, "Size", "size"),
                    digest,
                    url));
            }
        }

        var html = WebBase + "/releases/tag/" + Uri.EscapeDataString(tag);
        return new ReleaseInfo(version, tag, assets, html);
    }

    public ReleaseAsset? FindAsset(ReleaseInfo release, params string[] names)
    {
        foreach (var name in names)
        {
            var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (asset is not null) return asset;
        }
        return null;
    }

    public async Task<string> DownloadAsync(ReleaseAsset asset, string destination,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(directory)) throw new IOException("Ruta de descarga no válida.");
        Directory.CreateDirectory(directory);
        var part = destination + ".part";
        var direct = !string.IsNullOrWhiteSpace(asset.DownloadUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            direct ? asset.DownloadUrl! : ApiBase + "/releases/assets/" + asset.Id);
        if (!direct)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        double reported = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                readTotal += read;
                var value = total > 0 ? Math.Clamp((double)readTotal / total, 0, 1) : 0;
                if (value - reported >= 0.02 || value >= 1)
                {
                    reported = value;
                    progress?.Invoke(value);
                }
            }
        }
        var hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!DigestMatches(asset.Digest, hex))
        {
            try { File.Delete(part); } catch { }
            throw new InvalidDataException("La descarga no coincide con el hash SHA-256 de GitHub.");
        }
        File.Move(part, destination, true);
        return destination;
    }

    public static Version? ParseVersion(string? tag)
    {
        var text = tag?.Trim() ?? "";
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        return Version.TryParse(text, out var version) ? new Version(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build) : null;
    }

    public static string Format(Version version) => version.ToString(3);

    public static bool IsSha256Digest(string? digest)
    {
        if (string.IsNullOrEmpty(digest)) return false;
        var hex = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : digest;
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }

    public static bool DigestMatches(string? digest, string hash) =>
        IsSha256Digest(digest) &&
        string.Equals(digest!.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : digest,
            hash, StringComparison.OrdinalIgnoreCase);

    static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    static string? GetNullableString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static string GetStringAny(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetString(element, name);
            if (value.Length > 0) return value;
        }
        return "";
    }

    static long GetInt64Any(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number))
                return number;
        }
        return 0;
    }
}
