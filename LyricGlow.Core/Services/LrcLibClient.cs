using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LyricGlow.Models;

namespace LyricGlow.Services;

public sealed class LrcLibClient
{
    const int MaxSearchResults = 100;
    const int MaxMetaChars = 512;
    const double MaxDurationSeconds = 24 * 60 * 60;

    static readonly ExternalHostPolicy Hosts = ExternalHostPolicy.Of("lrclib.net");
    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = ExternalHttp.MaxJsonDepth,
    };

    static HttpClient CreateClient() =>
        ExternalHttp.CreateClient(TimeSpan.FromSeconds(15), "LyricGlow/0.9 (synchronized lyrics)",
            "application/json");

    public async Task<LrclibTrack?> GetExactAsync(string title, string artist, string? album, double? durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;

        var parts = new List<string>
        {
            $"track_name={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(title))}",
            $"artist_name={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(artist))}",
        };
        if (!string.IsNullOrWhiteSpace(album))
            parts.Add($"album_name={Uri.EscapeDataString(ExternalHttp.ClampQueryValue(album))}");
        if (durationSeconds is > 0 and < 86400) parts.Add($"duration={(int)Math.Round(durationSeconds.Value)}");
        var url = "https://lrclib.net/api/get?" + string.Join("&", parts);
        return await GetSingleAsync(url, ct);
    }

    public async Task<LrclibTrack?> GetByIdAsync(long id, CancellationToken ct)
    {
        if (id <= 0) return null;
        return await GetSingleAsync($"https://lrclib.net/api/get/{id}", ct);
    }

    async Task<LrclibTrack?> GetSingleAsync(string url, CancellationToken ct)
    {
        try
        {
            var uri = Hosts.Require(url);
            using var resp = await ExternalHttp.SendAsync(Http, uri, Hosts, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"LRCLIB HTTP {(int)resp.StatusCode} for {url}");
                return null;
            }

            using var doc = await ExternalHttp.ReadJsonAsync(resp, ExternalHttp.MaxJsonBytes, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ExternalDataException("LRCLIB devolvió un objeto inesperado.");

            var track = doc.RootElement.Deserialize<LrclibTrack>(JsonOpts);
            if (!ValidateTrack(track))
            {
                Log.Write("LRCLIB: respuesta rechazada por validación");
                return null;
            }
            return track;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write("LRCLIB get: timeout");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("LRCLIB get: " + ex.Message);
            return null;
        }
    }

    public async Task<List<LrclibTrack>> SearchByQueryAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(ExternalHttp.ClampQueryValue(query));
            var uri = Hosts.Require(url);
            using var resp = await ExternalHttp.SendAsync(Http, uri, Hosts, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"LRCLIB search HTTP {(int)resp.StatusCode}");
                return new List<LrclibTrack>();
            }

            using var doc = await ExternalHttp.ReadJsonAsync(resp, ExternalHttp.MaxJsonBytes, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalDataException("LRCLIB search no devolvió una lista.");

            var results = new List<LrclibTrack>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (results.Count >= MaxSearchResults) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var track = item.Deserialize<LrclibTrack>(JsonOpts);
                if (ValidateTrack(track)) results.Add(track!);
            }
            return results;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write("LRCLIB search: timeout");
            return new List<LrclibTrack>();
        }
        catch (Exception ex)
        {
            Log.Write("LRCLIB search: " + ex.Message);
            return new List<LrclibTrack>();
        }
    }

    internal static bool ValidateTrack(LrclibTrack? track)
    {
        if (track is null) return false;

        track.TrackName = ExternalHttp.CleanText(track.TrackName, MaxMetaChars);
        track.ArtistName = ExternalHttp.CleanText(track.ArtistName, MaxMetaChars);
        track.AlbumName = ExternalHttp.CleanText(track.AlbumName, MaxMetaChars);
        track.Source = ExternalHttp.CleanText(track.Source, 128);
        if (track.Source.Length == 0) track.Source = "LRCLIB";

        if (track.Duration is { } duration &&
            !ExternalHttp.IsFiniteInRange(duration, 0, MaxDurationSeconds))
        {
            track.Duration = null;
        }

        if (!ExternalHttp.LyricsTextOk(track.PlainLyrics, out _)) return false;
        if (!ExternalHttp.LyricsTextOk(track.SyncedLyrics, out _)) return false;
        if (!ExternalHttp.LyricsTextOk(track.LyricsFile, out _)) return false;
        if (track.PlainLyrics is { Length: 0 }) track.PlainLyrics = null;
        if (track.SyncedLyrics is { Length: 0 }) track.SyncedLyrics = null;
        if (track.LyricsFile is { Length: 0 }) track.LyricsFile = null;
        return true;
    }
}