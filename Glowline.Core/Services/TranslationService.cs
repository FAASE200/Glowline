using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glowline.Localization;

namespace Glowline.Services;

/// <summary>
/// Translates the lyrics line by line into the app language. Off by default:
/// turning it on sends the lyrics text to Google Translate. Results are
/// cached on disk per song and language, so each song is sent only once.
/// </summary>
public static class TranslationService
{
    const int MaxChunkChars = 450; // MyMemory's limit per request is 500
    const int MaxChunkLines = 60;
    const long MaxResponseBytes = 1L * 1024 * 1024;
    const int CacheVersion = 1;

    static readonly ExternalHostPolicy Hosts = ExternalHostPolicy.Of("translate.googleapis.com", "api.mymemory.translated.net");
    static readonly HttpClient Http = ExternalHttp.CreateClient(
        TimeSpan.FromSeconds(20), AppInfo.UserAgent, "application/json");

    sealed class CacheFile
    {
        public int Version { get; set; }
        public string Language { get; set; } = "";
        public List<string> Lines { get; set; } = new();
    }

    /// <summary>Language code used by the translation service for an app language.</summary>
    public static string LanguageCode(AppLanguage language) => language switch
    {
        AppLanguage.Spanish => "es",
        AppLanguage.English => "en",
        AppLanguage.PortugueseBrazil => "pt",
        AppLanguage.French => "fr",
        AppLanguage.German => "de",
        AppLanguage.Italian => "it",
        AppLanguage.Japanese => "ja",
        AppLanguage.Korean => "ko",
        AppLanguage.ChineseSimplified => "zh-CN",
        AppLanguage.Russian => "ru",
        _ => "en",
    };

    static string BaseLanguage(string code) =>
        code.Split('-')[0].ToLowerInvariant();

    /// <summary>
    /// Translates <paramref name="texts"/>. The result has the same length:
    /// "" where a line has nothing to translate. Returns an empty list when
    /// the lyrics are already in the target language, and null when the
    /// service could not be reached.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> TranslateAsync(
        string trackKey, IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct)
    {
        if (texts.Count == 0) return Array.Empty<string>();

        var cached = LoadCache(trackKey, targetLanguage, texts.Count);
        if (cached is not null) return cached;

        try
        {
            var result = new string[texts.Count];
            Array.Fill(result, "");
            var any = false;

            var index = 0;
            while (index < texts.Count)
            {
                ct.ThrowIfCancellationRequested();
                var batch = new List<int>();
                var chars = 0;
                for (; index < texts.Count && batch.Count < MaxChunkLines; index++)
                {
                    var clean = Flatten(texts[index]);
                    if (clean.Length == 0) continue;
                    if (batch.Count > 0 && chars + clean.Length > MaxChunkChars) break;
                    batch.Add(index);
                    chars += clean.Length + 1;
                }
                if (batch.Count == 0) continue;

                var (lines, detected) = await TranslateBatchAsync(texts, batch, targetLanguage, ct)
                    .ConfigureAwait(false);
                // The lyrics are already in the app language: nothing to show.
                if (!any && detected is not null &&
                    BaseLanguage(detected) == BaseLanguage(targetLanguage))
                {
                    SaveCache(trackKey, targetLanguage, new string[texts.Count]);
                    return Array.Empty<string>();
                }
                for (var i = 0; i < batch.Count; i++)
                {
                    var original = Flatten(texts[batch[i]]);
                    var translated = lines[i];
                    // The service gives the same text back for words it doesn't know.
                    result[batch[i]] = string.Equals(original, translated, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : translated;
                    any = true;
                }
            }

            SaveCache(trackKey, targetLanguage, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("translate: " + ex.Message);
            return null;
        }
    }

    static async Task<(string[] Lines, string? Detected)> TranslateBatchAsync(
        IReadOnlyList<string> texts, List<int> batch, string target, CancellationToken ct)
    {
        var joined = string.Join("\n", batch.ConvertAll(i => Flatten(texts[i])));
        var (text, detected) = await RequestAsync(joined, target, ct).ConfigureAwait(false);
        var split = SplitLines(text, batch.Count);
        if (split is not null) return (split, detected);

        // The service merged or split lines: ask for each one on its own.
        var single = new string[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (one, _) = await RequestAsync(Flatten(texts[batch[i]]), target, ct).ConfigureAwait(false);
            single[i] = Flatten(one);
        }
        return (single, detected);
    }

    // Google's endpoint is unofficial and sometimes answers with a captcha page:
    // after a failure it rests for a while and MyMemory covers for it.
    static DateTime _googleRestUntil = DateTime.MinValue;

    static async Task<(string Text, string? Detected)> RequestAsync(string text, string target, CancellationToken ct)
    {
        if (DateTime.UtcNow >= _googleRestUntil)
        {
            try
            {
                return await RequestGoogleAsync(text, target, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write("translate (google): " + ex.Message);
                _googleRestUntil = DateTime.UtcNow.AddMinutes(10);
            }
        }
        return await RequestMyMemoryAsync(text, target, ct).ConfigureAwait(false);
    }

    static async Task<(string Text, string? Detected)> RequestMyMemoryAsync(
        string text, string target, CancellationToken ct)
    {
        var uri = Hosts.Require(
            "https://api.mymemory.translated.net/get?langpair=Autodetect%7C" + Uri.EscapeDataString(target) +
            "&q=" + Uri.EscapeDataString(text));
        using var response = await ExternalHttp.SendAsync(Http, uri, Hosts, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ExternalDataException("Translation service answered " + (int)response.StatusCode + ".");
        using var doc = await ExternalHttp.ReadJsonAsync(response, MaxResponseBytes, ct).ConfigureAwait(false);
        return ParseMyMemory(doc.RootElement);
    }

    /// <summary>Reads MyMemory's reply. It has no language detection to report.</summary>
    internal static (string Text, string? Detected) ParseMyMemory(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("responseData", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("translatedText", out var text) ||
            text.ValueKind != JsonValueKind.String)
            throw new ExternalDataException("Unexpected translation reply.");
        if (root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True)
            throw new ExternalDataException("The daily translation quota is used up.");
        var status = root.TryGetProperty("responseStatus", out var s)
            ? (s.ValueKind == JsonValueKind.Number ? s.GetInt32() : int.TryParse(s.GetString(), out var n) ? n : 0)
            : 200;
        if (status != 200)
            throw new ExternalDataException("Translation service answered " + status + ".");
        return (System.Net.WebUtility.HtmlDecode(text.GetString() ?? ""), null);
    }

    static async Task<(string Text, string? Detected)> RequestGoogleAsync(
        string text, string target, CancellationToken ct)
    {
        var uri = Hosts.Require(
            "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t&tl=" +
            Uri.EscapeDataString(target));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", text) }),
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ExternalDataException("Translation service answered " + (int)response.StatusCode + ".");
        var body = await ExternalHttp.ReadStringAsync(response, MaxResponseBytes, ct).ConfigureAwait(false);
        return ParseResponse(body);
    }

    /// <summary>Reads the text and the detected language out of the service's reply.</summary>
    internal static (string Text, string? Detected) ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json, ExternalHttp.JsonOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 ||
            root[0].ValueKind != JsonValueKind.Array)
            throw new ExternalDataException("Unexpected translation reply.");

        var text = new StringBuilder();
        foreach (var segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 &&
                segment[0].ValueKind == JsonValueKind.String)
                text.Append(segment[0].GetString());
        }

        string? detected = null;
        if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
            detected = root[2].GetString();
        return (text.ToString(), detected);
    }

    /// <summary>Splits the translated text back into lines; null if the count differs.</summary>
    internal static string[]? SplitLines(string text, int expected)
    {
        var parts = text.Replace("\r", "").Split('\n');
        if (parts.Length != expected) return null;
        for (var i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
        return parts;
    }

    static string Flatten(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Trim();

    // ------------------------------------------------------------------
    // Disk cache
    // ------------------------------------------------------------------

    static string CachePath(string trackKey, string language, int count)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{trackKey}|{language}|{count}")))[..24];
        return Path.Combine(StoragePaths.CacheDirectory, "translations", hash + ".json");
    }

    static IReadOnlyList<string>? LoadCache(string trackKey, string language, int count)
    {
        try
        {
            var path = CachePath(trackKey, language, count);
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 512 * 1024) return null;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllBytes(path));
            if (file is null || file.Version != CacheVersion || file.Language != language) return null;
            // All empty = the lyrics were already in this language.
            if (file.Lines.TrueForAll(string.IsNullOrEmpty)) return Array.Empty<string>();
            return file.Lines.Count == count ? file.Lines : null;
        }
        catch
        {
            return null;
        }
    }

    static void SaveCache(string trackKey, string language, IReadOnlyList<string> lines)
    {
        try
        {
            var path = CachePath(trackKey, language, lines.Count);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new CacheFile { Version = CacheVersion, Language = language };
            foreach (var line in lines) file.Lines.Add(line ?? "");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(file));
            Prune(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Log.Write("translate cache: " + ex.Message);
        }
    }

    // Keeps the folder small: only the 300 newest translations stay.
    static void Prune(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("*.json");
            if (files.Length <= 300) return;
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (var i = 300; i < files.Length; i++) files[i].Delete();
        }
        catch
        {
        }
    }
}
