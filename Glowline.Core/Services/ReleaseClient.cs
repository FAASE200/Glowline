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

namespace Glowline.Services;

public sealed record ReleaseAsset(string Name, long Id, long Size, string? Digest, string? DownloadUrl = null);
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    IReadOnlyList<ReleaseAsset> Assets,
    string? HtmlUrl,
    string? Notes = null);

public sealed class ReleaseClient
{
    public const string Owner = "FAASE200";
    public const string Repo = "Glowline";
    public const string RepoUrl = "https://github.com/" + Owner + "/" + Repo;
    public const long MaxDownloadBytes = 1L * 1024 * 1024 * 1024;
    public const int MaxNotesChars = 32 * 1024;
    const int MaxAssets = 64;
    const int MaxNameChars = 256;
    const int MaxTagChars = 64;
    const long MaxApiJsonBytes = 4L * 1024 * 1024;
    const long MaxManifestBytes = 1L * 1024 * 1024;

    const string WebBase = RepoUrl;
    const string ApiBase = "https://api.github.com/repos/" + Owner + "/" + Repo;
    const string ManifestUrl = WebBase + "/releases/latest/download/manifest.json";

    static readonly ExternalHostPolicy WebHosts =
        ExternalHostPolicy.Of("github.com", "githubusercontent.com");
    static readonly ExternalHostPolicy ApiHosts =
        ExternalHostPolicy.Of("api.github.com", "github.com", "githubusercontent.com");

    readonly HttpClient _api;
    readonly HttpClient _downloads;

    public ReleaseClient()
    {
        _api = BuildClient(TimeSpan.FromSeconds(25), json: true);
        _downloads = BuildClient(TimeSpan.FromMinutes(20), json: false);
    }

    static HttpClient BuildClient(TimeSpan timeout, bool json)
    {
        var client = ExternalHttp.CreateClient(timeout, AppInfo.UserAgent + " updater");
        if (json)
        {
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await ExternalHttp.SendAsync(
                _downloads, WebHosts.Require(ManifestUrl), WebHosts, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            ExternalHttp.EnsureContentType(response,
                "application/json", "text/json", "text/plain", "application/octet-stream");
            var json = await ExternalHttp.ReadStringAsync(response, MaxManifestBytes, timeout.Token);
            return ParseManifest(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
        using var response = await ExternalHttp.SendAsync(
            _api, ApiHosts.Require(ApiBase + "/releases/latest"), ApiHosts, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = await ExternalHttp.ReadJsonAsync(response, MaxApiJsonBytes, ct);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unexpected response from GitHub.");

        var tag = ExternalHttp.CleanText(GetString(root, "tag_name"), MaxTagChars);
        var version = ParseVersion(tag);
        if (version is null) throw new InvalidDataException($"Unrecognized release version: {tag}");
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var item in nodes.EnumerateArray())
            {
                if (++count > MaxAssets) throw new InvalidDataException("Too many assets in the release.");
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = ExternalHttp.CleanText(GetString(item, "name"), MaxNameChars);
                if (name.Length == 0) continue;
                var size = item.TryGetProperty("size", out var sizeNode) &&
                           sizeNode.TryGetInt64(out var bytes) && bytes is >= 0 and <= MaxDownloadBytes
                    ? bytes : 0;
                assets.Add(new ReleaseAsset(
                    name,
                    item.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0,
                    size,
                    ExternalHttp.CleanText(GetNullableString(item, "digest"), 128)));
            }
        }
        var html = ExternalHttp.CleanText(GetNullableString(root, "html_url"), 512);
        if (html.Length > 0 && (!Uri.TryCreate(html, UriKind.Absolute, out var htmlUri) ||
                                !WebHosts.Allows(htmlUri)))
            html = "";
        return new ReleaseInfo(version, tag, assets, html.Length > 0 ? html : null, ReadNotes(root));
    }

    public async Task<string> GetReleaseNotesAsync(string tag, CancellationToken ct = default)
    {
        var cleanTag = ExternalHttp.CleanText(tag, MaxTagChars);
        if (cleanTag.Length == 0) return "";
        using var response = await ExternalHttp.SendAsync(
            _api,
            ApiHosts.Require(ApiBase + "/releases/tags/" + Uri.EscapeDataString(cleanTag)),
            ApiHosts,
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return "";
        using var doc = await ExternalHttp.ReadJsonAsync(response, MaxApiJsonBytes, ct).ConfigureAwait(false);
        return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadNotes(doc.RootElement) : "";
    }

    public static ReleaseInfo ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json, ExternalHttp.JsonOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Manifest has no root object.");

        var tag = ExternalHttp.CleanText(GetStringAny(root, "tag", "tag_name"), MaxTagChars);
        var versionText = ExternalHttp.CleanText(GetStringAny(root, "version", "tag"), MaxTagChars);
        var version = ParseVersion(versionText) ?? ParseVersion(tag);
        if (version is null) throw new InvalidDataException($"Unrecognized manifest version: {versionText}");
        if (tag.Length == 0) tag = "v" + Format(version);

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            if (nodes.GetArrayLength() > MaxAssets)
                throw new InvalidDataException("Too many assets in the manifest.");
            foreach (var item in nodes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = ExternalHttp.CleanText(GetStringAny(item, "Name", "name"), MaxNameChars);
                if (name.Length == 0) continue;
                var sha = ExternalHttp.CleanText(
                    GetStringAny(item, "Sha256", "sha256", "Digest", "digest"), 128);
                var digest = IsSha256Digest(sha)
                    ? (sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? sha.ToLowerInvariant()
                        : "sha256:" + sha.ToLowerInvariant())
                    : null;
                var size = GetInt64Any(item, "Size", "size");
                if (size is < 0 or > MaxDownloadBytes) size = 0;
                var url = WebBase + "/releases/download/" +
                          Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);
                assets.Add(new ReleaseAsset(name, 0, size, digest, url));
            }
        }

        var html = WebBase + "/releases/tag/" + Uri.EscapeDataString(tag);
        return new ReleaseInfo(version, tag, assets, html, ReadNotes(root));
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
        if (string.IsNullOrEmpty(directory)) throw new IOException("Invalid download path.");
        Directory.CreateDirectory(directory);
        var part = destination + ".part";
        var direct = !string.IsNullOrWhiteSpace(asset.DownloadUrl);
        var policy = direct ? WebHosts : ApiHosts;
        var uri = policy.Require(direct ? asset.DownloadUrl! : ApiBase + "/releases/assets/" + asset.Id);
        var client = _downloads;
        using var response = await ExternalHttp.SendAsync(client, uri, policy, ct, request =>
        {
            if (direct) return;
            // The assets API returns JSON metadata unless the binary is requested.
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ExternalHttp.EnsureContentType(response,
            "application/octet-stream", "binary/octet-stream", "application/x-msdownload",
            "application/vnd.microsoft.portable-executable", "application/zip",
            "application/vnd.android.package-archive");

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        if (total > MaxDownloadBytes || (asset.Size > 0 && total > asset.Size + 1024 * 1024))
            throw new InvalidDataException("The installer exceeds the allowed size.");
        double reported = 0;
        long readTotal = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    readTotal += read;
                    if (readTotal > MaxDownloadBytes)
                    {
                        output.Close();
                        try { File.Delete(part); } catch { }
                        throw new InvalidDataException("The installer exceeds the allowed size.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    var value = total > 0 ? Math.Clamp((double)readTotal / total, 0, 1) : 0;
                    if (value - reported >= 0.02 || value >= 1)
                    {
                        reported = value;
                        progress?.Invoke(value);
                    }
                }
            }
        }
        catch
        {
            // An interrupted or cancelled download must not leave an orphaned
            // .part file of tens of MB in the updates folder.
            try { File.Delete(part); } catch { }
            throw;
        }
        if (asset.Size > 0 && readTotal != asset.Size)
        {
            try { File.Delete(part); } catch { }
            throw new InvalidDataException("The downloaded size does not match the published one.");
        }
        var hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!DigestMatches(asset.Digest, hex))
        {
            try { File.Delete(part); } catch { }
            throw new InvalidDataException("The download does not match the SHA-256 hash from GitHub.");
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

    static string ReadNotes(JsonElement root)
    {
        var raw = GetStringAny(root, "notes", "changelog", "releaseNotes", "release_notes", "body");
        if (raw.Length == 0) return "";
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\0', ' ').Trim();
        if (text.Length > MaxNotesChars)
        {
            var limit = MaxNotesChars;
            if (char.IsHighSurrogate(text[limit - 1]) && char.IsLowSurrogate(text[limit])) limit--;
            text = text[..limit].TrimEnd();
        }
        return text;
    }

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
