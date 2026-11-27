using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Glowline.Models;

namespace Glowline.Services;

public sealed class LrcmuxClient
{
    const string BaseUrl = "https://api.lrcmux.dev/get";
    const int MaxLines = 2000;
    const int MaxWordsPerLine = 200;
    const int MaxMetaChars = 512;
    const int MaxLineChars = 4096;
    const int MaxWordChars = 512;
    const int MaxStartMs = 24 * 60 * 60 * 1000;
    const double MaxDurationSeconds = 24 * 60 * 60;

    static readonly ExternalHostPolicy Hosts = ExternalHostPolicy.Of("lrcmux.dev");
    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = ExternalHttp.MaxJsonDepth,
    };

    static HttpClient CreateClient() =>
        ExternalHttp.CreateClient(TimeSpan.FromSeconds(20), AppInfo.UserAgent,
            "application/json");

    public async Task<LrclibTrack?> GetAsync(
        string title, string artist, string? album, double? durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var parts = new List<string>
        {
            $"title={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(title))}",
        };
        if (!string.IsNullOrWhiteSpace(artist))
            parts.Add($"artist={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(artist))}");
        if (!string.IsNullOrWhiteSpace(album))
            parts.Add($"album={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(album))}");
        if (durationSeconds is > 0 and < 86400) parts.Add($"duration={(int)Math.Round(durationSeconds.Value)}");
        parts.Add("level=word");
        parts.Add("format=json");
        var url = BaseUrl + "?" + string.Join("&", parts);

        try
        {
            var uri = Hosts.Require(url);
            using var resp = await ExternalHttp.SendAsync(Http, uri, Hosts, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"Lrcmux HTTP {(int)resp.StatusCode} for '{artist} — {title}'");
                return null;
            }

            using var doc = await ExternalHttp.ReadJsonAsync(resp, ExternalHttp.MaxJsonBytes, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ExternalDataException("Lrcmux devolvió un objeto inesperado.");

            var data = doc.RootElement.Deserialize<Response>(JsonOpts);
            return ToTrack(data, title, artist);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write("Lrcmux get: timeout");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Lrcmux get: " + ex.Message);
            return null;
        }
    }

    internal static LrclibTrack? TrackFromJson(string json, string fallbackTitle, string fallbackArtist)
    {
        using var doc = JsonDocument.Parse(json, ExternalHttp.JsonOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var data = doc.RootElement.Deserialize<Response>(JsonOpts);
        return ToTrack(data, fallbackTitle, fallbackArtist);
    }

    static LrclibTrack? ToTrack(Response? data, string fallbackTitle, string fallbackArtist)
    {
        if (data?.Lines is not { Count: > 0 } lines) return null;
        if (lines.Count > MaxLines)
        {
            Log.Write($"Lrcmux: {lines.Count} líneas exceden el límite");
            return null;
        }

        var track = new LrclibTrack
        {
            TrackName = FirstNonEmpty(data.Track?.Title, fallbackTitle),
            ArtistName = FirstNonEmpty(data.Track?.Artist, fallbackArtist),
            AlbumName = ExternalHttp.CleanText(data.Track?.Album, MaxMetaChars),
            Duration = ValidDuration(data.Track?.Duration),
            Instrumental = data.Meta?.Instrumental ?? false,
            Source = SourceLabel(data.Meta?.Source),
        };

        var cleaned = new List<Line>(lines.Count);
        var totalWords = 0;
        foreach (var line in lines)
        {
            if (line is null) continue;
            if (line.Start is > MaxStartMs) return null;
            if (line.Words is { Count: > MaxWordsPerLine }) return null;

            var text = Sanitize(line.Text);
            List<Word>? words = null;
            if (line.Words is { Count: > 0 } sourceWords)
            {
                words = new List<Word>(sourceWords.Count);
                foreach (var word in sourceWords)
                {
                    if (word is null) continue;
                    if (word.Start is not >= 0) continue;
                    if (word.Start is > MaxStartMs) return null;
                    var wordText = SanitizeWord(word.Text);
                    if (wordText.Length == 0) continue;
                    words.Add(new Word { Start = word.Start, Text = wordText });
                }
                if (words.Count == 0) words = null;
            }
            if (words is not null)
            {
                totalWords += words.Count;
                if (totalWords > ExternalHttp.MaxLyricWords) return null;
            }
            cleaned.Add(new Line { Start = line.Start, Text = text, Words = words });
        }

        var timed = cleaned
            .Where(l => l.Start is >= 0 && (l.Text!.Length > 0 || l.Words is { Count: > 0 }))
            .ToList();
        if (timed.Count == 0)
        {
            track.PlainLyrics = string.Join("\n",
                cleaned.Select(l => l.Text!).Where(t => t.Length > 0));
            return track;
        }

        var sb = new StringBuilder();
        var hasWords = false;
        foreach (var line in timed)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append('[').Append(Stamp(line.Start!.Value)).Append(']');

            var words = line.Words;
            var usableWords = words is { Count: > 0 } &&
                              words.All(w => w.Start is >= 0 && w.Text!.Length > 0);
            if (usableWords)
            {
                hasWords = true;
                var aligned = AlignWords(words!, line.Text!);
                if (aligned is not null)
                {
                    foreach (var word in aligned)
                        sb.Append('<').Append(Stamp(word.Start!.Value)).Append('>').Append(word.Text!);
                }
                else
                {
                    foreach (var word in words!)
                        sb.Append('<').Append(Stamp(word.Start!.Value)).Append('>').Append(word.Text!);
                }
            }
            else
            {
                sb.Append(line.Text);
            }

            if (sb.Length > ExternalHttp.MaxLyricsChars)
            {
                Log.Write("Lrcmux: letra generada demasiado larga");
                return null;
            }
        }

        track.SyncedLyrics = sb.ToString();
        track.HasWordSync = hasWords;
        return track;
    }

    static List<Word>? AlignWords(List<Word> words, string lineText)
    {
        if (lineText.Length == 0) return null;
        var result = new List<Word>(words.Count);
        var cursor = 0;

        foreach (var word in words)
        {
            var text = word.Text!.Trim();
            if (text.Length == 0) continue;
            var match = lineText.IndexOf(text, cursor, StringComparison.Ordinal);
            if (match < 0) return null;

            if (result.Count == 0)
            {
                result.Add(new Word { Start = word.Start, Text = lineText[..match] + text });
            }
            else
            {
                var gap = lineText[cursor..match];
                if (gap.Length > 0) result[^1].Text += gap;
                result.Add(new Word { Start = word.Start, Text = text });
            }
            cursor = match + text.Length;
        }

        if (result.Count == 0) return null;
        if (cursor < lineText.Length) result[^1].Text += lineText[cursor..];
        return string.Concat(result.Select(word => word.Text)) == lineText ? result : null;
    }

    static string FirstNonEmpty(string? value, string fallback)
    {
        var text = ExternalHttp.CleanText(value, MaxMetaChars);
        return text.Length > 0 ? text : ExternalHttp.CleanText(fallback, MaxMetaChars);
    }

    static double? ValidDuration(double? value) =>
        value is { } duration && ExternalHttp.IsFiniteInRange(duration, 0, MaxDurationSeconds)
            ? duration
            : null;

    static string SourceLabel(JsonElement? source)
    {
        if (source is not { } element) return "Lrcmux";
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String)
        {
            var value = ExternalHttp.CleanText(name.GetString(), 128);
            if (value.Length > 0) return "Lrcmux · " + value;
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = ExternalHttp.CleanText(element.GetString(), 128);
            if (value.Length > 0) return "Lrcmux · " + value;
        }
        return "Lrcmux";
    }

    static string Sanitize(string? text)
    {
        var value = ExternalHttp.CleanText(text, MaxLineChars);
        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    static string SanitizeWord(string? text)
    {
        var value = ExternalHttp.CleanText(text, MaxWordChars);
        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    static string Stamp(int milliseconds)
    {
        var centiseconds = (int)Math.Round(Math.Clamp(milliseconds, 0, MaxStartMs) / 10.0,
            MidpointRounding.AwayFromZero);
        var minutes = centiseconds / 6000;
        centiseconds %= 6000;
        var seconds = centiseconds / 100;
        centiseconds %= 100;
        return $"{minutes:00}:{seconds:00}.{centiseconds:00}";
    }

    sealed class Response
    {
        [JsonPropertyName("track")] public Track? Track { get; set; }
        [JsonPropertyName("meta")] public Meta? Meta { get; set; }
        [JsonPropertyName("lines")] public List<Line>? Lines { get; set; }
    }

    sealed class Track
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("artist")] public string? Artist { get; set; }
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
    }

    sealed class Meta
    {
        [JsonPropertyName("instrumental")] public bool? Instrumental { get; set; }
        [JsonPropertyName("source")] public JsonElement? Source { get; set; }
    }

    sealed class Line
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("start")] public int? Start { get; set; }
        [JsonPropertyName("words")] public List<Word>? Words { get; set; }
    }

    sealed class Word
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("start")] public int? Start { get; set; }
    }
}