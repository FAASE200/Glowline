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
    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LyricGlow/0.9 (synchronized lyrics)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    public async Task<LrclibTrack?> GetExactAsync(string title, string artist, string? album, double? durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;

        var parts = new List<string>
        {
            $"track_name={Uri.EscapeDataString(title)}",
            $"artist_name={Uri.EscapeDataString(artist)}",
        };
        if (!string.IsNullOrWhiteSpace(album)) parts.Add($"album_name={Uri.EscapeDataString(album)}");
        if (durationSeconds is > 0) parts.Add($"duration={(int)Math.Round(durationSeconds.Value)}");
        var url = "https://lrclib.net/api/get?" + string.Join("&", parts);
        return await GetSingleAsync(url, ct);
    }

    public async Task<LrclibTrack?> GetByIdAsync(long id, CancellationToken ct)
    {
        return await GetSingleAsync($"https://lrclib.net/api/get/{id}", ct);
    }

    async Task<LrclibTrack?> GetSingleAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"LRCLIB HTTP {(int)resp.StatusCode} for {url}");
                return null;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<LrclibTrack>(body, JsonOpts);
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
        return await SearchCoreAsync("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(query), ct);
    }

    async Task<List<LrclibTrack>> SearchCoreAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"LRCLIB search HTTP {(int)resp.StatusCode}");
                return new List<LrclibTrack>();
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<LrclibTrack>>(body, JsonOpts) ?? new List<LrclibTrack>();
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
}
