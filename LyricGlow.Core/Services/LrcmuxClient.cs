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
using LyricGlow.Models;

namespace LyricGlow.Services;

public sealed class LrcmuxClient
{
    const string BaseUrl = "https://api.lrcmux.dev/get";
    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LyricGlow/0.9 (synchronized lyrics)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    public async Task<LrclibTrack?> GetAsync(
        string title, string artist, string? album, double? durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var parts = new List<string> { $"title={Uri.EscapeDataString(title)}" };
        if (!string.IsNullOrWhiteSpace(artist)) parts.Add($"artist={Uri.EscapeDataString(artist)}");
        if (!string.IsNullOrWhiteSpace(album)) parts.Add($"album={Uri.EscapeDataString(album)}");
        if (durationSeconds is > 0) parts.Add($"duration={(int)Math.Round(durationSeconds.Value)}");
        parts.Add("level=word");
        parts.Add("format=json");
        var url = BaseUrl + "?" + string.Join("&", parts);

        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"Lrcmux HTTP {(int)resp.StatusCode} for '{artist} — {title}'");
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<Response>(body, JsonOpts);
            if (data?.Lines is not { Count: > 0 }) return null;
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

    static LrclibTrack ToTrack(Response data, string fallbackTitle, string fallbackArtist)
    {
        var lines = data.Lines!;
        var source = SourceLabel(data.Meta?.Source);
        var track = new LrclibTrack
        {
            TrackName = string.IsNullOrWhiteSpace(data.Track?.Title) ? fallbackTitle : data.Track!.Title!,
            ArtistName = string.IsNullOrWhiteSpace(data.Track?.Artist) ? fallbackArtist : data.Track!.Artist!,
            AlbumName = data.Track?.Album ?? "",
            Duration = data.Track?.Duration,
            Instrumental = data.Meta?.Instrumental ?? false,
            Source = source,
        };

        var timed = lines
            .Where(l => l.Start is >= 0 &&
                        (!string.IsNullOrWhiteSpace(l.Text) || l.Words is { Count: > 0 }))
            .ToList();
        if (timed.Count == 0)
        {
            track.PlainLyrics = string.Join("\n",
                lines.Select(l => (l.Text ?? "").Trim()).Where(t => t.Length > 0));
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
                              words.All(w => w.Start is >= 0 && !string.IsNullOrWhiteSpace(w.Text));
            if (usableWords)
            {
                hasWords = true;
                foreach (var word in words!)
                    sb.Append('<').Append(Stamp(word.Start!.Value)).Append('>').Append(Sanitize(word.Text!));
            }
            else
            {
                sb.Append(Sanitize((line.Text ?? "").Trim()));
            }
        }

        track.SyncedLyrics = sb.ToString();
        track.HasWordSync = hasWords;
        return track;
    }

    static string SourceLabel(JsonElement? source)
    {
        if (source is not { } element) return "Lrcmux";
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String)
        {
            var value = name.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return "Lrcmux · " + value;
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return "Lrcmux · " + value;
        }
        return "Lrcmux";
    }

    static string Sanitize(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    static string Stamp(int milliseconds)
    {
        var centiseconds = (int)Math.Round(Math.Max(0, milliseconds) / 10.0, MidpointRounding.AwayFromZero);
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
