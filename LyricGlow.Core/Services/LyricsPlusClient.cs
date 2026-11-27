using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LyricGlow.Models;

namespace LyricGlow.Services;

public sealed class LyricsPlusClient
{
    const string BaseUrl = "https://lyricsplus.prjktla.my.id";
    const int MaxLines = 2000;
    const int MaxSyllablesPerLine = 200;
    const int MaxTextChars = 4096;
    const int MaxSyllableChars = 512;
    const int MaxMetaChars = 512;
    const int MaxSongWriters = 50;
    const double MaxDurationSeconds = 24 * 60 * 60;
    const double MaxTimeMs = MaxDurationSeconds * 1000;

    static readonly ExternalHostPolicy Hosts = ExternalHostPolicy.Of("lyricsplus.prjktla.my.id");
    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = ExternalHttp.MaxJsonDepth,
    };
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    static HttpClient CreateClient() =>
        ExternalHttp.CreateClient(TimeSpan.FromSeconds(25), "LyricGlow/0.9 (synchronized lyrics)",
            "application/json");

    public async Task<LrclibTrack?> GetAsync(
        string title, string artist, string? album, double? durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;

        var parts = new List<string>
        {
            $"title={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(title))}",
            $"artist={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(artist))}",
        };
        if (!string.IsNullOrWhiteSpace(album))
            parts.Add($"album={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(album))}");
        if (durationSeconds is > 0 and < 86400) parts.Add($"duration={(int)Math.Round(durationSeconds.Value)}");

        var url = BaseUrl + "/v2/lyrics/get?" + string.Join("&", parts);

        try
        {
            var uri = Hosts.Require(url);
            using var response = await ExternalHttp.SendAsync(Http, uri, Hosts, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == (HttpStatusCode)429)
            {
                Log.Write($"LyricsPlus rate limit for '{artist} — {title}'");
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"LyricsPlus HTTP {(int)response.StatusCode} for '{artist} — {title}'");
                return null;
            }

            using var doc = await ExternalHttp.ReadJsonAsync(response, ExternalHttp.MaxJsonBytes, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ExternalDataException("LyricsPlus devolvió un objeto inesperado.");

            var payload = doc.RootElement.Deserialize<ApiResponse>(JsonOptions);
            return ToTrack(payload, title, artist);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write("LyricsPlus get: timeout");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("LyricsPlus get: " + ex.Message);
            return null;
        }
    }

    internal static LrclibTrack? TrackFromJson(string json, string fallbackTitle, string fallbackArtist)
    {
        using var doc = JsonDocument.Parse(json, ExternalHttp.JsonOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        return ToTrack(doc.RootElement.Deserialize<ApiResponse>(JsonOptions), fallbackTitle, fallbackArtist);
    }

    static LrclibTrack? ToTrack(ApiResponse? response, string fallbackTitle, string fallbackArtist)
    {
        if (response?.Lyrics is not { Count: > 0 } lyricEntries) return null;
        if (lyricEntries.Count > MaxLines)
        {
            Log.Write($"LyricsPlus: {lyricEntries.Count} líneas exceden el límite");
            return null;
        }

        var metadata = response.Metadata;
        var songWriters = CleanWriters(metadata?.SongWriters);
        var lines = lyricEntries
            .Where(line => line is not null && IsLineValid(line))
            .Select((line, index) => (line, index))
            .Where(entry => !IsCreditLine(
                entry.line.Text,
                entry.line.Duration,
                entry.index,
                FirstNonEmpty(metadata?.Title, fallbackTitle),
                FirstNonEmpty(metadata?.Artist, fallbackArtist),
                songWriters))
            .OrderBy(entry => entry.line.Time)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.line)
            .ToList();
        if (lines.Count == 0) return null;
        if (lines.Sum(line => line.Syllabus?.Count ?? 0) > ExternalHttp.MaxLyricWords)
        {
            Log.Write("LyricsPlus: demasiadas sílabas");
            return null;
        }

        var synced = BuildSyncedLyrics(lines);
        var plain = BuildPlainLyrics(lines);
        if (synced.Length > ExternalHttp.MaxLyricsChars ||
            !ExternalHttp.LyricsTextOk(plain, out _))
        {
            Log.Write("LyricsPlus: letra excede el límite");
            return null;
        }
        if (synced.Length == 0 && plain.Length == 0) return null;

        var track = new LrclibTrack
        {
            TrackName = FirstNonEmpty(metadata?.Title, fallbackTitle),
            ArtistName = FirstNonEmpty(metadata?.Artist, fallbackArtist),
            AlbumName = ExternalHttp.CleanText(metadata?.Album, MaxMetaChars),
            Duration = ParseDuration(metadata?.TotalDuration, response.ProcessingTime?.SelectedSongMetadata?.Duration),
            Source = SourceLabel(response),
        };

        if (synced.Length > 0)
        {
            track.SyncedLyrics = synced;
            track.HasWordSync = HasWordSync(lines);
        }
        else
        {
            track.PlainLyrics = plain;
        }

        return track;
    }

    static List<string>? CleanWriters(List<string>? writers)
    {
        if (writers is not { Count: > 0 }) return null;
        var result = new List<string>(Math.Min(writers.Count, MaxSongWriters));
        foreach (var writer in writers)
        {
            if (result.Count >= MaxSongWriters) break;
            var text = ExternalHttp.CleanText(writer, MaxMetaChars);
            if (text.Length > 0) result.Add(text);
        }
        return result.Count > 0 ? result : null;
    }

    static bool IsLineValid(Line line)
    {
        if (!ExternalHttp.IsFiniteInRange(line.Time, 0, MaxTimeMs)) return false;
        if (!ExternalHttp.IsFiniteInRange(line.Duration, 0, MaxTimeMs)) return false;
        if (line.Text is { Length: > MaxTextChars }) return false;
        if (line.Syllabus is not { Count: > 0 } syllables) return true;
        if (syllables.Count > MaxSyllablesPerLine) return false;

        foreach (var syllable in syllables)
        {
            if (syllable is null) return false;
            if (!ExternalHttp.IsFiniteInRange(syllable.Time, 0, MaxTimeMs)) return false;
            if (!ExternalHttp.IsFiniteInRange(syllable.Duration, 0, MaxTimeMs)) return false;
            if (syllable.Text is { Length: > MaxSyllableChars }) return false;
        }
        return true;
    }

    static string BuildSyncedLyrics(IReadOnlyList<Line> lines)
    {
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            var text = Normalize(line.Text);
            var segments = BuildSegments(line.Syllabus, text);

            if (segments.Count > 0)
            {
                if (result.Length > 0) result.Append('\n');
                result.Append('[').Append(Stamp(TimeMs(line.Time))).Append(']');
                foreach (var segment in segments)
                    result.Append('<').Append(Stamp(TimeMs(segment.Time))).Append('>').Append(segment.Text);
                continue;
            }

            if (text.Length > 0)
            {
                if (result.Length > 0) result.Append('\n');
                result.Append('[').Append(Stamp(TimeMs(line.Time))).Append(']').Append(text);
            }
        }

        return result.ToString();
    }

    static List<Segment> BuildSegments(List<Syllable>? syllables, string lineText)
    {
        var segments = BuildSegmentsCore(syllables, lineText);
        segments.Sort((left, right) => left.Time.CompareTo(right.Time));
        return segments;
    }

    static List<Segment> BuildSegmentsCore(List<Syllable>? syllables, string lineText)
    {
        var timed = (syllables ?? [])
            .Where(syllable => syllable is not null && syllable.Time >= 0 && !string.IsNullOrWhiteSpace(syllable.Text))
            .Select(syllable => (Time: TimeMs(syllable.Time), Text: Normalize(syllable.Text)))
            .Where(token => token.Text.Length > 0)
            .ToList();
        if (timed.Count == 0) return [];

        var segments = timed.Select(token => new Segment(token.Time, token.Text)).ToList();
        if (lineText.Length == 0) return segments;

        var raw = string.Concat(timed.Select(token => token.Text));
        if (raw == lineText) return segments;

        var spaced = string.Join(" ", timed.Select(token => token.Text));
        if (spaced == lineText)
        {
            for (var i = 0; i < segments.Count - 1; i++)
                segments[i].Text += " ";
            return segments;
        }

        var aligned = TryAlignSegments(timed, lineText);
        if (aligned is not null) return aligned;

        return SplitByChunks(lineText, timed.Select(token => token.Time).ToList());
    }

    static List<Segment>? TryAlignSegments(List<(long Time, string Text)> timed, string lineText)
    {
        var result = new List<Segment>(timed.Count);
        var cursor = 0;

        foreach (var token in timed)
        {
            var text = token.Text;
            var match = lineText.IndexOf(text, cursor, StringComparison.Ordinal);
            if (match < 0)
            {
                var trimmed = text.Trim();
                if (trimmed.Length == 0) return null;
                text = trimmed;
                match = lineText.IndexOf(text, cursor, StringComparison.Ordinal);
            }
            if (match < 0) return null;

            if (result.Count == 0)
            {
                result.Add(new Segment(token.Time, lineText[cursor..match] + text));
            }
            else
            {
                var gap = lineText[cursor..match];
                if (gap.Length > 0) result[^1].Text += gap;
                result.Add(new Segment(token.Time, text));
            }
            cursor = match + text.Length;
        }

        if (cursor < lineText.Length) result[^1].Text += lineText[cursor..];
        return string.Concat(result.Select(segment => segment.Text)) == lineText ? result : null;
    }

    static List<Segment> SplitByChunks(string lineText, IReadOnlyList<long> times)
    {
        var chunks = SplitTextChunks(lineText);
        var count = Math.Min(chunks.Count, times.Count);
        if (count == 0) return [];

        var result = new List<Segment>(count);
        for (var i = 0; i < count; i++)
        {
            var text = chunks[i];
            if (i == count - 1) text += string.Concat(chunks.Skip(count));
            result.Add(new Segment(times[i], text));
        }
        return result;
    }

    static List<string> SplitTextChunks(string text)
    {
        var chunks = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index > start) chunks.Add(text[start..index]);
        }

        return chunks;
    }

    static bool HasWordSync(IReadOnlyList<Line> lines) =>
        lines.Any(line => line.Syllabus is { Count: > 1 });

    static string BuildPlainLyrics(IEnumerable<Line> lines) =>
        string.Join('\n', lines.Select(line => Normalize(line.Text)).Where(text => text.Length > 0));

    static string SourceLabel(ApiResponse response)
    {
        var provider = FirstNonEmpty(
            response.Metadata?.Source,
            response.ProcessingTime?.WinnerSource,
            response.ProcessingTime?.SelectedSongMetadata?.Source);
        return provider.Length == 0 ? "LyricsPlus" : "LyricsPlus · " + provider;
    }

    static double? ParseDuration(string? totalDuration, double? fallbackSeconds)
    {
        var parsed = ParseSeconds(totalDuration);
        if (parsed is > 0) return parsed;
        return fallbackSeconds is { } fallback &&
               ExternalHttp.IsFiniteInRange(fallback, 0, MaxDurationSeconds) && fallback > 0
            ? fallback
            : null;
    }

    static double? ParseSeconds(string? value)
    {
        value = Normalize(value);
        if (value.Length == 0 || value.Length > 64) return null;

        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length > 4) return null;
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var component))
                    return null;
                if (!ExternalHttp.IsFiniteInRange(component, 0, MaxDurationSeconds)) return null;
                total = total * 60 + component;
                if (total > MaxDurationSeconds) return null;
            }
            return total;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
               ExternalHttp.IsFiniteInRange(seconds, 0, MaxDurationSeconds)
            ? seconds
            : null;
    }

    static bool IsCreditLine(
        string? rawText, double durationMs, int index, string title, string artist, List<string>? songWriters)
    {
        var text = Normalize(rawText);
        if (text.Length == 0) return true;

        if (LooksLikeSlashSeparatedNames(text)) return true;
        if (songWriters?.Any(writer => SamePerson(text, writer)) == true) return true;
        if (index > 2) return false;

        if (durationMs <= 150 && LooksLikePersonNames(text)) return true;

        var compactText = Compact(text);
        var compactTitle = Compact(title);
        var compactArtist = Compact(artist);
        if (compactTitle.Length == 0 || compactArtist.Length == 0) return false;

        return compactText == compactTitle ||
               compactText == compactArtist ||
               compactText == compactTitle + compactArtist ||
               compactText == compactArtist + compactTitle ||
               (compactText.StartsWith(compactTitle) && compactText.Contains(compactArtist));
    }

    static bool SamePerson(string left, string? right) =>
        Compact(left).Length > 0 && Compact(right) == Compact(left);

    static bool LooksLikeSlashSeparatedNames(string text)
    {
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && parts.All(LooksLikePersonNames);
    }

    static bool LooksLikePersonNames(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 2 or > 4) return false;

        foreach (var word in words)
        {
            if (!char.IsUpper(word[0])) return false;
            if (word.Any(ch => !char.IsLetterOrDigit(ch) && ch != '\'' && ch != '’' && ch != '.' && ch != '-'))
                return false;
        }

        return true;
    }

    static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var result = new StringBuilder(Math.Min(value.Length, MaxTextChars));
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) result.Append(char.ToLowerInvariant(ch));
        }
        return result.ToString();
    }

    static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = ExternalHttp.CleanText(value, MaxMetaChars);
            if (text.Length > 0) return text;
        }
        return "";
    }

    static string Normalize(string? value) => Whitespace.Replace(value ?? "", " ").Trim();

    static long TimeMs(double value) =>
        double.IsFinite(value) ? (long)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, MaxTimeMs) : 0;

    static string Stamp(long milliseconds)
    {
        milliseconds = Math.Clamp(milliseconds, 0, (long)MaxTimeMs);
        var centiseconds = (long)Math.Round(milliseconds / 10.0, MidpointRounding.AwayFromZero);
        var minutes = centiseconds / 6000;
        centiseconds %= 6000;
        var seconds = centiseconds / 100;
        centiseconds %= 100;
        return $"{minutes:00}:{seconds:00}.{centiseconds:00}";
    }

    sealed class Segment(long time, string text)
    {
        public long Time { get; } = time;
        public string Text { get; set; } = text;
    }

    sealed class ApiResponse
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("metadata")] public LyricsMetadata? Metadata { get; set; }
        [JsonPropertyName("lyrics")] public List<Line>? Lyrics { get; set; }
        [JsonPropertyName("processingTime")] public ProcessTiming? ProcessingTime { get; set; }
    }

    sealed class LyricsMetadata
    {
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("artist")] public string? Artist { get; set; }
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("songWriters")] public List<string>? SongWriters { get; set; }
        [JsonPropertyName("totalDuration")] public string? TotalDuration { get; set; }
    }

    sealed class Line
    {
        [JsonPropertyName("time")] public double Time { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("syllabus")] public List<Syllable>? Syllabus { get; set; }
    }

    sealed class Syllable
    {
        [JsonPropertyName("time")] public double Time { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    sealed class ProcessTiming
    {
        [JsonPropertyName("winnerSource")] public string? WinnerSource { get; set; }
        [JsonPropertyName("selectedSongMetadata")] public PickedSongMetadata? SelectedSongMetadata { get; set; }
    }

    sealed class PickedSongMetadata
    {
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
    }
}