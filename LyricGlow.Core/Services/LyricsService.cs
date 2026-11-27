using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LyricGlow.Models;

namespace LyricGlow.Services;

public enum LyricsState
{
    Idle,
    Searching,
    Synced,
    Plain,
    Instrumental,
    NotFound,
    Error,
}

public sealed class LyricsService
{
    sealed class CacheEntry
    {
        public int Version { get; set; }
        public DateTime FetchedAtUtc { get; set; }
        public LrclibTrack? Track { get; set; }
    }

    const long MaxCacheBytes = 4L * 1024 * 1024;
    static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        MaxDepth = ExternalHttp.MaxJsonDepth,
    };

    static readonly Regex FeatSuffix = new(
        @"\s*[\(\[](?:feat|ft|with)\.?\s[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex EditionSuffix = new(
        @"\s*[-–—]\s*(?:remaster(?:ed)?|deluxe|bonus|single version|radio edit|explicit)[^\(\)]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex ArtistFeat = new(
        @"\s+(?:feat|ft)\.?\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly IAppSettingsStore _settings;
    readonly LrcLibClient _client;
    readonly LrcmuxClient _lrcmux = new();
    readonly LyricsPlusClient _lyricsPlus = new();

    CancellationTokenSource? _cts;
    long _resolveGeneration;
    SessionSnapshot? _snapshot;
    string _currentKey = "";
    bool _smoothInit;
    double _shown;
    DateTimeOffset _shownAt;
    double _seekTarget = -1;
    DateTimeOffset _seekAt;

    public LyricsState State { get; private set; } = LyricsState.Idle;
    public TrackInfo? Track { get; private set; }
    public IReadOnlyList<LrcLine> Lines { get; private set; } = Array.Empty<LrcLine>();
    public string? PlainLyrics { get; private set; }
    public LrclibTrack? Found { get; private set; }
    public string? NoteKey { get; private set; }
    public bool NoUsableLines { get; private set; }
    public event Action? Changed;

    public LyricsService(IAppSettingsStore settings, LrcLibClient client)
    {
        _settings = settings;
        _client = client;
    }

    public bool IsPlaying => _snapshot?.IsPlaying == true;

    public void OnSnapshot(SessionSnapshot snap)
    {
        _snapshot = snap;
        var track = snap.Track;

        if (track is null || (track.Title.Length == 0 && track.Artist.Length == 0))
        {
            if (State != LyricsState.Idle)
            {
                _cts?.Cancel();
                _currentKey = "";
                Track = null;
                SetState(LyricsState.Idle, Array.Empty<LrcLine>(), null, null);
            }
            return;
        }

        if (track.Key != _currentKey)
        {
            _currentKey = track.Key;
            Track = track;
            _smoothInit = false;
            _ = ResolveAsync(track, ignoreCache: false);
        }
        else
        {
            Track = track;
        }
    }

    public void ForceResync()
    {
        if (Track is not null) _ = ResolveAsync(Track, ignoreCache: true);
    }

    public void ReloadSource()
    {
        if (Track is not null) _ = ResolveAsync(Track, ignoreCache: false);
    }

    async Task ResolveAsync(TrackInfo track, bool ignoreCache)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        // Cada resolución recibe una generación: la activa es la mayor. Así una
        // resolución cancelada o antigua no puede aplicar Error ni resultados.
        var generation = ++_resolveGeneration;

        var source = _settings.Settings.LyricsSource;
        SetState(LyricsState.Searching, Array.Empty<LrcLine>(), null, null);
        try
        {
            await Task.Delay(350, cts.Token);
            var key = track.Key;
            LrclibTrack? found;
            string? note = null;

            if (_settings.Settings.ManualOverrides.ContainsKey(key))
            {
                found = await ResolveLrclibAsync(track, ignoreCache, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (!IsCurrentResolution(generation, cts)) return;
                Apply(track, found, null);
                return;
            }

            if (source == LyricSource.Lrcmux)
            {
                var cacheKey = AlbumCacheKey(LyricSource.Lrcmux, key, track.Album);
                found = ignoreCache ? null : LoadCache(cacheKey);
                if (!ignoreCache && found is null) found = LoadCache(CacheKey(LyricSource.Lrcmux, key));
                if (!HasUsableSync(found))
                {
                    var fresh = await FindLrcmuxAsync(track, cts.Token);
                    if (fresh is not null) found = fresh;
                }

                Log.Write(found is null
                    ? $"resolve[Lrcmux] '{track.DisplayName}' dur={track.DurationSeconds?.ToString("F0") ?? "-"} -> sin resultados"
                    : $"resolve[Lrcmux] '{track.DisplayName}' dur={track.DurationSeconds?.ToString("F0") ?? "-"} -> '{found.TrackName}' / '{found.ArtistName}' ({found.Duration?.ToString("F0") ?? "?"}s, word={found.HasWordSync}, via={found.Source})");

                if (HasUsableSync(found))
                {
                    SaveCache(cacheKey, found!);
                }
                else if (found?.Instrumental != true)
                {
                    Log.Write($"resolve[Lrcmux] '{track.DisplayName}' -> respaldo LyricsPlus");
                    var backupKey = AlbumCacheKey(LyricSource.LyricsPlus, key, track.Album);
                    var backup = ignoreCache ? null : LoadCache(backupKey);
                    if (!HasUsableSync(backup))
                    {
                        var freshBackup = await FindLyricsPlusAsync(track, cts.Token);
                        if (freshBackup is not null) backup = freshBackup;
                    }

                    if (HasUsableSync(backup))
                    {
                        found = backup;
                        note = "LyricsNoteBackupLyricsPlus";
                        SaveCache(backupKey, found!);
                    }
                    else if (backup?.Instrumental == true)
                    {
                        found = backup;
                        note = "LyricsNoteBackupLyricsPlus";
                    }
                    else
                    {
                        var lrclib = await ResolveLrclibAsync(track, ignoreCache, cts.Token);
                        if (lrclib is not null)
                        {
                            found = lrclib;
                            note = "LyricsNoteBackupLrclib";
                        }
                        else
                        {
                            note = "LyricsNoteLrclibMissing";
                        }
                    }
                }

                cts.Token.ThrowIfCancellationRequested();
                if (!IsCurrentResolution(generation, cts)) return;
                Apply(track, found, note);
                return;
            }

            if (source == LyricSource.LyricsPlus)
            {
                var cacheKey = AlbumCacheKey(LyricSource.LyricsPlus, key, track.Album);
                found = ignoreCache ? null : LoadCache(cacheKey);
                if (!HasUsableSync(found))
                {
                    var fresh = await FindLyricsPlusAsync(track, cts.Token);
                    if (fresh is not null) found = fresh;
                }

                Log.Write(found is null
                    ? $"resolve[LyricsPlus] '{track.DisplayName}' dur={track.DurationSeconds?.ToString("F0") ?? "-"} -> sin resultados"
                    : $"resolve[LyricsPlus] '{track.DisplayName}' dur={track.DurationSeconds?.ToString("F0") ?? "-"} -> '{found.TrackName}' / '{found.ArtistName}' ({found.Duration?.ToString("F0") ?? "?"}s, word={found.HasWordSync}, via={found.Source})");

                if (HasUsableSync(found))
                {
                    SaveCache(cacheKey, found!);
                }
                else if (found?.Instrumental != true)
                {
                    Log.Write($"resolve[LyricsPlus] '{track.DisplayName}' -> respaldo Lrcmux");
                    var backupKey = AlbumCacheKey(LyricSource.Lrcmux, key, track.Album);
                    var backup = ignoreCache ? null : LoadCache(backupKey);
                    if (!HasUsableSync(backup))
                    {
                        var freshBackup = await FindLrcmuxAsync(track, cts.Token);
                        if (freshBackup is not null) backup = freshBackup;
                    }

                    if (HasUsableSync(backup))
                    {
                        found = backup;
                        note = "LyricsNoteBackupLrcmux";
                        SaveCache(backupKey, found!);
                    }
                    else if (backup?.Instrumental == true)
                    {
                        found = backup;
                        note = "LyricsNoteBackupLrcmux";
                    }
                    else
                    {
                        var lrclib = await ResolveLrclibAsync(track, ignoreCache, cts.Token);
                        if (lrclib is not null)
                        {
                            found = lrclib;
                            note = "LyricsNoteBackupLrclib";
                        }
                        else
                        {
                            note = "LyricsNoteLrclibMissing";
                        }
                    }
                }

                cts.Token.ThrowIfCancellationRequested();
                if (!IsCurrentResolution(generation, cts)) return;
                Apply(track, found, note);
                return;
            }

            found = await ResolveLrclibAsync(track, ignoreCache, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!IsCurrentResolution(generation, cts)) return;
            Apply(track, found, null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("resolve: " + ex);
            if (!IsCurrentResolution(generation, cts)) return;
            SetState(LyricsState.Error, Array.Empty<LrcLine>(), null, null);
        }
    }

    bool IsCurrentResolution(long generation, CancellationTokenSource cts) =>
        generation == _resolveGeneration && !cts.IsCancellationRequested;

    async Task<LrclibTrack?> FindLyricsPlusAsync(TrackInfo track, CancellationToken ct)
    {
        var found = await _lyricsPlus.GetAsync(track.Title, track.Artist, track.Album, track.DurationSeconds, ct);
        var (title, artist) = Relaxed(track);
        if (found is null && title.Length > 0 && (title != track.Title || artist != track.Artist))
            found = await _lyricsPlus.GetAsync(title, artist, track.Album, track.DurationSeconds, ct);
        return found;
    }

    async Task<LrclibTrack?> FindLrcmuxAsync(TrackInfo track, CancellationToken ct)
    {
        var found = await _lrcmux.GetAsync(track.Title, track.Artist, track.Album, track.DurationSeconds, ct);
        var (title, artist) = Relaxed(track);
        if (found is null && title.Length > 0 && (title != track.Title || artist != track.Artist))
            found = await _lrcmux.GetAsync(title, artist, track.Album, track.DurationSeconds, ct);
        return found;
    }

    async Task<LrclibTrack?> ResolveLrclibAsync(TrackInfo track, bool ignoreCache, CancellationToken ct)
    {
        var key = track.Key;
        var cacheKey = AlbumCacheKey(LyricSource.Lrclib, key, track.Album);
        var legacyCacheKey = CacheKey(LyricSource.Lrclib, key);
        var (relaxedTitle, relaxedArtist) = Relaxed(track);
        LrclibTrack? found = null;

        if (_settings.Settings.ManualOverrides.TryGetValue(key, out var overrideId))
            found = await _client.GetByIdAsync(overrideId, ct);

        if (found is null && !ignoreCache)
        {
            found = LoadCache(cacheKey);
            // Los archivos guardados con la clave antigua siguen siendo válidos.
            found ??= LoadCache(legacyCacheKey);
        }

        var album = TrackKey.Normalize(track.Album);

        if (found is null)
            found = await _client.GetExactAsync(track.Title, track.Artist, album.Length > 0 ? track.Album : null, null, ct);

        if (found is null && relaxedTitle.Length > 0 &&
            (relaxedTitle != track.Title || relaxedArtist != track.Artist))
        {
            found = await _client.GetExactAsync(relaxedTitle, relaxedArtist, null, null, ct);
        }

        if (found is null || !(found.HasSynced && found.HasWordSync))
        {
            var query = JoinQuery(track.Title, track.Artist);
            var results = await _client.SearchByQueryAsync(query, ct);
            if (results.Count == 0)
            {
                var relaxedQuery = JoinQuery(relaxedTitle, relaxedArtist);
                if (relaxedQuery.Length > 0 && relaxedQuery != query)
                    results = await _client.SearchByQueryAsync(relaxedQuery, ct);
            }
            if (found is not null) results.Insert(0, found);
            var best = PickBest(results, track);
            if (best is not null) found = best;
        }

        Log.Write(found is null
            ? $"resolve[LRCLIB] '{track.DisplayName}' dur={track.DurationSeconds?.ToString("F0") ?? "-"} -> sin resultados"
            : $"resolve[LRCLIB] '{track.DisplayName}' dur={track.DurationSeconds?.ToString("F0") ?? "-"} -> '{found.TrackName}' / '{found.ArtistName}' ({found.Duration?.ToString("F0") ?? "?"}s, word={found.HasWordSync})");

        if (found is not null) SaveCache(cacheKey, found);
        return found;
    }

    internal static bool HasUsableSync(LrclibTrack? track)
    {
        if (track is null || !track.HasSynced) return false;
        return ParseBestLines(track).Count > 1;
    }

    void Apply(TrackInfo track, LrclibTrack? found, string? note = null)
    {
        if (found is null)
        {
            SetState(LyricsState.NotFound, Array.Empty<LrcLine>(), null, null, note);
            return;
        }

        if (found.Instrumental)
        {
            SetState(LyricsState.Instrumental, Array.Empty<LrcLine>(), null, found, note);
            return;
        }

        if (found.HasSynced)
        {
            var lines = ParseBestLines(found);
            if (lines.Count > 1)
            {
                SetState(LyricsState.Synced, lines, null, found, note);
                return;
            }
        }

        if (found.HasPlain)
        {
            SetState(LyricsState.Plain, Array.Empty<LrcLine>(), found.PlainLyrics, found, note);
            return;
        }

        SetState(LyricsState.NotFound, Array.Empty<LrcLine>(), null, found, note, noUsableLines: true);
    }

    void SetState(LyricsState state, IReadOnlyList<LrcLine> lines, string? plain,
        LrclibTrack? found, string? noteKey = null, bool noUsableLines = false)
    {
        State = state;
        Lines = lines;
        PlainLyrics = plain;
        Found = found;
        NoteKey = noteKey;
        NoUsableLines = noUsableLines;
        Changed?.Invoke();
    }

    static (string Title, string Artist) Relaxed(TrackInfo track)
    {
        var title = FeatSuffix.Replace(track.Title, "");
        title = EditionSuffix.Replace(title, "").Trim();
        var artist = ArtistFeat.Replace(track.Artist, "").Trim();
        return (title, artist);
    }

    static string JoinQuery(string title, string artist) =>
        string.Join(" ", new[] { title, artist }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

    public static string SourceName(LyricSource source) => source switch
    {
        LyricSource.LyricsPlus => "LyricsPlus",
        LyricSource.Lrcmux => "Lrcmux",
        _ => "LRCLIB",
    };

    static string CacheKey(LyricSource source, string trackKey) => source switch
    {
        LyricSource.LyricsPlus => "lyricsplus|" + trackKey,
        LyricSource.Lrcmux => "lrcmux|" + trackKey,
        _ => trackKey,
    };

    // La cache de LRCLIB distingue el álbum (normalizado) para no reutilizar
    // la letra de un homónimo de otro disco. TrackInfo.Key no cambia.
    static string AlbumCacheKey(LyricSource source, string trackKey, string album)
    {
        var normalized = TrackKey.Normalize(album);
        return normalized.Length == 0 ? CacheKey(source, trackKey) : CacheKey(source, trackKey) + "|" + normalized;
    }

    static List<LrcLine> ParseBestLines(LrclibTrack found)
    {
        var fromFile = LyricsFileParser.Parse(found.LyricsFile);
        var lines = fromFile is { Count: > 1 } ? fromFile : LrcParser.Parse(found.SyncedLyrics!);
        return LyricText.Process(lines);
    }

    static LrclibTrack? PickBest(List<LrclibTrack> results, TrackInfo track)
    {
        if (results.Count == 0) return null;

        var title = TrackKey.Normalize(track.Title);
        var artist = TrackKey.Normalize(track.Artist);
        var album = TrackKey.Normalize(track.Album);
        double? duration = track.DurationSeconds;
        // La penalización por álbum distinto solo tiene sentido si algún
        // resultado trae álbum comparable; si no, no se desmerece a nadie.
        var anyAlbumMatch = album.Length > 0 && results.Any(r =>
            TrackKey.Normalize(r.AlbumName).Length > 0);

        double Score(LrclibTrack r)
        {
            double s;
            if (r.HasSynced) s = r.HasWordSync ? 6 : 4;
            else if (r.HasPlain) s = 1;
            else return double.NegativeInfinity;

            if (duration is > 0 && r.Duration is > 0)
            {
                var diff = Math.Abs(r.Duration.Value - duration.Value);
                if (diff <= 1) s += 8;
                else if (diff <= 2) s += 5;
                else if (diff <= 4) s += 2;
                else if (diff > 8) s -= 3;
            }

            var rt = TrackKey.Normalize(r.TrackName);
            var ra = TrackKey.Normalize(r.ArtistName);
            if (rt == title) s += 3;
            else if (rt.Contains(title) || title.Contains(rt)) s += 1;
            if (artist.Length > 0)
            {
                if (ra == artist) s += 2;
                else if (ra.Contains(artist) || artist.Contains(ra)) s += 1;
            }

            if (album.Length > 0)
            {
                var rab = TrackKey.Normalize(r.AlbumName);
                if (rab.Length > 0)
                {
                    if (rab == album) s += 3;
                    else if (anyAlbumMatch) s -= 2;
                }
            }

            return s;
        }

        return results
            .Where(r => r.HasSynced || r.HasPlain)
            .Select(r => (Track: r, Score: Score(r)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Track)
            .FirstOrDefault();
    }

    public double GetDisplayPosition()
    {
        var snap = _snapshot;
        if (snap is null) return 0;

        var now = DateTimeOffset.Now;
        var playing = snap.IsPlaying;
        var target = snap.PositionSeconds + (playing ? Math.Max(0, (now - snap.CapturedAt).TotalSeconds) : 0);
        if (!double.IsFinite(target)) target = 0;

        if (_seekTarget >= 0)
        {
            var since = (now - _seekAt).TotalSeconds;
            if (since > 2 || Math.Abs(snap.PositionSeconds - _seekTarget) < 0.7)
            {
                _seekTarget = -1;
            }
            else
            {
                target = _seekTarget + (playing ? since : 0);
                _shown = target;
                _shownAt = now;
                return target;
            }
        }

        if (!_smoothInit)
        {
            _smoothInit = true;
            _shown = target;
            _shownAt = now;
            return target;
        }

        var elapsed = Math.Max(0, (now - _shownAt).TotalSeconds);
        var predicted = _shown + (playing ? elapsed : 0);
        var err = target - predicted;
        double corrected;

        if (Math.Abs(err) > 0.9)
        {
            corrected = target;
        }
        else if (!playing)
        {
            corrected = Math.Abs(target - _shown) > 0.9 ? target : _shown;
        }
        else
        {
            corrected = predicted + err * Math.Min(1.0, elapsed * 6.0);
            if (corrected < _shown) corrected = _shown;
        }

        _shown = corrected;
        _shownAt = now;
        return corrected;
    }

    public double GetAdjustedPosition() => GetDisplayPosition() + CurrentOffsetMs / 1000.0;

    public double GetSeekPosition(double lineTime) =>
        Math.Max(0, lineTime - CurrentOffsetMs / 1000.0);

    public void NotifySeek(double positionSeconds)
    {
        var pos = Math.Max(0, positionSeconds);
        _seekTarget = pos;
        _seekAt = DateTimeOffset.Now;
        _shown = pos;
        _shownAt = DateTimeOffset.Now;
        _smoothInit = true;
    }

    public int CurrentOffsetMs
    {
        get
        {
            var s = _settings.Settings;
            var total = s.GlobalOffsetMs;
            if (Track is not null && s.PerTrackOffsetMs.TryGetValue(Track.Key, out var v)) total += v;
            return total;
        }
    }

    public int GetTrackOffsetMs() =>
        Track is not null && _settings.Settings.PerTrackOffsetMs.TryGetValue(Track.Key, out var v) ? v : 0;

    public bool HasManualOverride =>
        Track is not null && _settings.Settings.ManualOverrides.ContainsKey(Track.Key);

    public void ClearManualOverride()
    {
        if (Track is null) return;
        _settings.Settings.ManualOverrides.Remove(Track.Key);
        _settings.ScheduleSave();
        _ = ResolveAsync(Track, ignoreCache: true);
    }

    public void AdjustGlobalOffset(int deltaMs)
    {
        _settings.Settings.GlobalOffsetMs += deltaMs;
        _settings.ScheduleSave();
        Changed?.Invoke();
    }

    public void AdjustTrackOffset(int deltaMs)
    {
        if (Track is null) return;
        var dict = _settings.Settings.PerTrackOffsetMs;
        dict[Track.Key] = (dict.TryGetValue(Track.Key, out var v) ? v : 0) + deltaMs;
        _settings.ScheduleSave();
        Changed?.Invoke();
    }

    public void ResetGlobalOffset()
    {
        _settings.Settings.GlobalOffsetMs = 0;
        _settings.ScheduleSave();
        Changed?.Invoke();
    }

    public void ResetTrackOffset()
    {
        if (Track is null) return;
        _settings.Settings.PerTrackOffsetMs.Remove(Track.Key);
        _settings.ScheduleSave();
        Changed?.Invoke();
    }

    public int GetLineIndex(double position)
    {
        var lines = Lines;
        if (lines.Count == 0) return -1;
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].Time <= position)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }

    public Task<List<LrclibTrack>> SearchManualAsync(string query, CancellationToken ct) =>
        _client.SearchByQueryAsync(query, ct);

    public void ApplyManual(LrclibTrack result)
    {
        if (Track is null) return;
        // Invalida la resolución en curso para que no pise la elección manual.
        _resolveGeneration++;
        _cts?.Cancel();
        _settings.Settings.ManualOverrides[Track.Key] = result.Id;
        _settings.ScheduleSave();
        SaveCache(AlbumCacheKey(LyricSource.Lrclib, Track.Key, Track.Album), result);
        Apply(Track, result);
    }

    string CachePath(string key) =>
        Path.Combine(StoragePaths.CacheDirectory, Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24] + ".json");

    void SaveCache(string key, LrclibTrack track)
    {
        try
        {
            var entry = new CacheEntry { Version = 2, FetchedAtUtc = DateTime.UtcNow, Track = track };
            var json = JsonSerializer.Serialize(entry);
            if (json.Length > MaxCacheBytes) return;
            File.WriteAllText(CachePath(key), json);
        }
        catch (Exception ex)
        {
            Log.Write("cache save: " + ex.Message);
        }
    }

    LrclibTrack? LoadCache(string key)
    {
        try
        {
            var path = CachePath(key);
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxCacheBytes) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(
                File.ReadAllBytes(path), CacheJsonOptions);
            if (entry?.Track is null || entry.Version < 2) return null;
            if (DateTime.UtcNow - entry.FetchedAtUtc > TimeSpan.FromDays(14)) return null;
            if (!LrcLibClient.ValidateTrack(entry.Track)) return null;
            return entry.Track;
        }
        catch
        {
            return null;
        }
    }
}
