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
using Glowline.Models;

namespace Glowline.Services;

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

    // Null-safe: a hand-edited setting saved as "null" must not crash the app
    // (Android loads settings without the Windows normalization).
    Dictionary<string, Dictionary<string, int>> LineOffsets =>
        _settings.Settings.PerLineOffsetMs ??= new Dictionary<string, Dictionary<string, int>>();

    Dictionary<string, string> ManualPins =>
        _settings.Settings.ManualPins ??= new Dictionary<string, string>();
    IReadOnlyList<LrcLine> _sourceLines = Array.Empty<LrcLine>();
    IReadOnlyList<string>? _translations;
    string _translationsLanguage = "";
    CancellationTokenSource? _translateCts;
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
        // Each resolve gets a generation and the active one is the highest, so a
        // cancelled or stale resolve cannot apply Error or results.
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

            if (ManualPins.ContainsKey(key))
            {
                var pinned = LoadPinned(key);
                if (pinned is not null)
                {
                    if (!IsCurrentResolution(generation, cts)) return;
                    Apply(track, pinned, null);
                    return;
                }
                // The pinned lyrics file is gone: fall back to the normal lookup.
                ManualPins.Remove(key);
                _settings.ScheduleSave();
            }

            if (source is LyricSource.Lrcmux or LyricSource.LyricsPlus)
            {
                var backupSource = source == LyricSource.Lrcmux ? LyricSource.LyricsPlus : LyricSource.Lrcmux;
                (found, note) = await ResolveWithFallbackAsync(track, source, backupSource, ignoreCache, cts.Token);
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

    /// <summary>
    /// Looks in the preferred source, then in the other word-synced source and
    /// finally in LRCLIB. Returns the lyrics and the note the UI shows when a
    /// fallback was used.
    /// </summary>
    async Task<(LrclibTrack? Found, string? Note)> ResolveWithFallbackAsync(
        TrackInfo track, LyricSource primary, LyricSource backup, bool ignoreCache, CancellationToken ct)
    {
        var key = track.Key;
        var primaryName = SourceName(primary);
        var duration = track.DurationSeconds?.ToString("F0") ?? "-";

        var cacheKey = AlbumCacheKey(primary, key, track.Album);
        var found = ignoreCache ? null : LoadCache(cacheKey);
        // Lrcmux lyrics cached before the album was part of the key are still valid.
        if (!ignoreCache && found is null && primary == LyricSource.Lrcmux)
            found = LoadCache(CacheKey(primary, key));
        if (!HasUsableSync(found))
        {
            var fresh = await FindAsync(primary, track, ct);
            if (fresh is not null) found = fresh;
            // Offline or the service is down: an old copy is better than nothing.
            else if (!ignoreCache && found is null) found = LoadCache(cacheKey, allowStale: true);
        }

        Log.Write(found is null
            ? $"resolve[{primaryName}] '{track.DisplayName}' dur={duration} -> no results"
            : $"resolve[{primaryName}] '{track.DisplayName}' dur={duration} -> '{found.TrackName}' / '{found.ArtistName}' ({found.Duration?.ToString("F0") ?? "?"}s, word={found.HasWordSync}, via={found.Source})");

        if (HasUsableSync(found))
        {
            SaveCache(cacheKey, found!);
            return (found, null);
        }

        if (found?.Instrumental == true) return (found, null);

        var backupNote = "LyricsNoteBackup" + SourceName(backup);
        Log.Write($"resolve[{primaryName}] '{track.DisplayName}' -> fallback {SourceName(backup)}");
        var backupKey = AlbumCacheKey(backup, key, track.Album);
        var fallback = ignoreCache ? null : LoadCache(backupKey);
        if (!HasUsableSync(fallback))
        {
            var freshBackup = await FindAsync(backup, track, ct);
            if (freshBackup is not null) fallback = freshBackup;
        }

        if (HasUsableSync(fallback))
        {
            SaveCache(backupKey, fallback!);
            return (fallback, backupNote);
        }

        if (fallback?.Instrumental == true) return (fallback, backupNote);

        var lrclib = await ResolveLrclibAsync(track, ignoreCache, ct);
        return lrclib is not null
            ? (lrclib, "LyricsNoteBackupLrclib")
            : (found, "LyricsNoteLrclibMissing");
    }

    Task<LrclibTrack?> FindAsync(LyricSource source, TrackInfo track, CancellationToken ct) => source switch
    {
        LyricSource.Lrcmux => FindLrcmuxAsync(track, ct),
        LyricSource.LyricsPlus => FindLyricsPlusAsync(track, ct),
        _ => ResolveLrclibAsync(track, ignoreCache: false, ct),
    };


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
            // Files saved with the old key are still valid.
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
            ? $"resolve[LRCLIB] '{track.DisplayName}' dur={track.DurationSeconds?.ToString("F0") ?? "-"} -> no results"
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
                _sourceLines = lines;
                _translations = null;
                SetState(LyricsState.Synced, BuildDisplayLines(track), null, found, note);
                StartTranslation(track);
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
        if (state != LyricsState.Synced)
        {
            _sourceLines = Array.Empty<LrcLine>();
            _translations = null;
            _translateCts?.Cancel();
        }
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

    // The LRCLIB cache tells albums apart (normalized) so it doesn't reuse
    // the lyrics of a same-named song from another record. TrackInfo.Key is unchanged.
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

    internal static LrclibTrack? PickBest(List<LrclibTrack> results, TrackInfo track)
    {
        if (results.Count == 0) return null;

        var title = TrackKey.Normalize(track.Title);
        var artist = TrackKey.Normalize(track.Artist);
        var album = TrackKey.Normalize(track.Album);
        double? duration = track.DurationSeconds;
        // The different-album penalty only makes sense if some
        // result has a comparable album; otherwise nobody is penalized.
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
        Track is not null && (_settings.Settings.ManualOverrides.ContainsKey(Track.Key) ||
                              ManualPins.ContainsKey(Track.Key));

    public void ClearManualOverride()
    {
        if (Track is null) return;
        _settings.Settings.ManualOverrides.Remove(Track.Key);
        if (ManualPins.Remove(Track.Key)) DeletePinned(Track.Key);
        _settings.ScheduleSave();
        _ = ResolveAsync(Track, ignoreCache: true);
    }

    // ------------------------------------------------------------------
    // Per-line timing and romanization
    // ------------------------------------------------------------------

    /// <summary>Limit for a single line adjustment (±5 s).</summary>
    public const int MaxLineOffsetMs = 5000;

    static string LineId(LrcLine line)
    {
        var time = double.IsNaN(line.SourceTime) ? line.Time : line.SourceTime;
        return ((long)Math.Round(time * 1000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public int GetLineOffsetMs(int index)
    {
        var lines = Lines;
        if (Track is null || index < 0 || index >= lines.Count) return 0;
        return LineOffsets.TryGetValue(Track.Key, out var map) && map is not null &&
               map.TryGetValue(LineId(lines[index]), out var value)
            ? value
            : 0;
    }

    public void AdjustLineOffset(int index, int deltaMs)
    {
        var lines = Lines;
        if (Track is null || State != LyricsState.Synced || index < 0 || index >= lines.Count) return;
        var all = LineOffsets;
        if (!all.TryGetValue(Track.Key, out var map) || map is null)
        {
            map = new Dictionary<string, int>();
            all[Track.Key] = map;
        }
        var id = LineId(lines[index]);
        var value = Math.Clamp((map.TryGetValue(id, out var current) ? current : 0) + deltaMs,
            -MaxLineOffsetMs, MaxLineOffsetMs);
        if (value == 0) map.Remove(id);
        else map[id] = value;
        if (map.Count == 0) all.Remove(Track.Key);
        _settings.ScheduleSave();
        RefreshDisplayLines();
    }

    public void ResetLineOffset(int index)
    {
        var lines = Lines;
        if (Track is null || index < 0 || index >= lines.Count) return;
        if (!LineOffsets.TryGetValue(Track.Key, out var map) || map is null) return;
        if (!map.Remove(LineId(lines[index]))) return;
        if (map.Count == 0) LineOffsets.Remove(Track.Key);
        _settings.ScheduleSave();
        RefreshDisplayLines();
    }

    /// <summary>
    /// Rebuilds the visible lines (per-line timing, romanization) without
    /// downloading the lyrics again.
    /// </summary>
    public void RefreshDisplayLines()
    {
        if (State != LyricsState.Synced || Track is null || _sourceLines.Count == 0) return;
        Lines = BuildDisplayLines(Track);
        Changed?.Invoke();
    }

    IReadOnlyList<LrcLine> BuildDisplayLines(TrackInfo track)
    {
        var source = _sourceLines;
        LineOffsets.TryGetValue(track.Key, out var offsets);
        var s = _settings.Settings;
        var translations = s.TranslationEnabled &&
                           _translationsLanguage == TranslationService.LanguageCode(s.Language)
            ? _translations
            : null;
        return BuildDisplayLines(source, offsets, s.RomanizationEnabled, translations);
    }

    /// <summary>
    /// Call after turning translation on or off, or changing the app
    /// language: fetches the translation if it is needed and redraws.
    /// </summary>
    public void RefreshTranslation()
    {
        if (Track is null || State != LyricsState.Synced) return;
        if (_settings.Settings.TranslationEnabled) StartTranslation(Track);
        else _translateCts?.Cancel();
        RefreshDisplayLines();
    }

    void StartTranslation(TrackInfo track)
    {
        var settings = _settings.Settings;
        if (!settings.TranslationEnabled || _sourceLines.Count == 0) return;
        var language = TranslationService.LanguageCode(settings.Language);
        if (_translations is not null && _translationsLanguage == language) return;

        _translateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _translateCts = cts;
        var source = _sourceLines;
        var texts = new List<string>(source.Count);
        foreach (var line in source) texts.Add(line.Text.Length > 0 ? line.Text : line.Background);
        _ = ApplyTranslationAsync(track, source, texts, language, cts);
    }

    async Task ApplyTranslationAsync(TrackInfo track, IReadOnlyList<LrcLine> source,
        List<string> texts, string language, CancellationTokenSource cts)
    {
        try
        {
            var result = await TranslationService.TranslateAsync(track.Key, texts, language, cts.Token);
            // Another song or new lyrics arrived while it was working.
            if (cts.IsCancellationRequested || !ReferenceEquals(source, _sourceLines)) return;
            if (result is null) return;
            _translations = result;
            _translationsLanguage = language;
            RefreshDisplayLines();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("translation: " + ex.Message);
        }
    }

    internal static IReadOnlyList<LrcLine> BuildDisplayLines(
        IReadOnlyList<LrcLine> source, IReadOnlyDictionary<string, int>? offsets, bool romanize,
        IReadOnlyList<string>? translations = null)
    {
        var result = new List<LrcLine>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            var line = source[i];
            var shiftMs = offsets is not null && offsets.TryGetValue(LineId(line), out var value) ? value : 0;
            var baseTime = double.IsNaN(line.SourceTime) ? line.Time : line.SourceTime;
            var time = baseTime + shiftMs / 1000.0;
            // An adjusted line can't move before the previous one or past the
            // next one: the binary-search index relies on the order.
            var lower = i > 0 ? result[i - 1].Time + 0.01 : 0;
            var next = i + 1 < source.Count ? source[i + 1] : null;
            var upper = next is null
                ? double.MaxValue
                : (double.IsNaN(next.SourceTime) ? next.Time : next.SourceTime) - 0.01;
            time = Math.Clamp(time, lower, Math.Max(lower, upper));
            var delta = time - line.Time;

            result.Add(new LrcLine
            {
                Time = time,
                Text = line.Text,
                Background = line.Background,
                Words = Shift(line.Words, delta),
                BackgroundWords = Shift(line.BackgroundWords, delta),
                Romanized = romanize ? Romanizer.Romanize(line.Text) : "",
                Translation = translations is not null && i < translations.Count ? translations[i] : "",
                SourceTime = baseTime,
            });
        }
        return result;
    }

    static IReadOnlyList<LrcWord>? Shift(IReadOnlyList<LrcWord>? words, double delta)
    {
        if (words is null || Math.Abs(delta) < 0.0005) return words;
        var shifted = new List<LrcWord>(words.Count);
        foreach (var word in words)
            shifted.Add(new LrcWord { Time = Math.Max(0, word.Time + delta), Text = word.Text });
        return shifted;
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

    /// <summary>
    /// Manual search across the three sources. LRCLIB takes free text;
    /// Lrcmux and LyricsPlus need a title and artist, taken from the query
    /// ("Artist - Title") or from the artist of the current track.
    /// </summary>
    public async Task<List<LrclibTrack>> SearchManualAsync(string query, CancellationToken ct)
    {
        var (title, artist) = SplitManualQuery(query, Track?.Artist);
        var lrclibTask = _client.SearchByQueryAsync(query, ct);
        var lrcmuxTask = title.Length > 0
            ? SafeAsync(() => _lrcmux.GetAsync(title, artist, null, null, ct))
            : Task.FromResult<LrclibTrack?>(null);
        var lyricsPlusTask = title.Length > 0
            ? SafeAsync(() => _lyricsPlus.GetAsync(title, artist, null, null, ct))
            : Task.FromResult<LrclibTrack?>(null);

        await Task.WhenAll(lrclibTask, lrcmuxTask, lyricsPlusTask).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var results = new List<LrclibTrack>();
        foreach (var (task, provider) in new[] { (lrcmuxTask, "Lrcmux"), (lyricsPlusTask, "LyricsPlus") })
        {
            var found = task.Result;
            if (found is null || (!found.HasSynced && !found.HasPlain)) continue;
            found.Provider = provider;
            results.Add(found);
        }
        foreach (var found in lrclibTask.Result)
        {
            found.Provider = "LRCLIB";
            results.Add(found);
        }
        return results;
    }

    internal static (string Title, string Artist) SplitManualQuery(string query, string? currentArtist)
    {
        var text = (query ?? "").Trim();
        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var index = text.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0) continue;
            var artist = text[..index].Trim();
            var title = text[(index + separator.Length)..].Trim();
            if (title.Length > 0 && artist.Length > 0) return (title, artist);
        }
        return (text, currentArtist?.Trim() ?? "");
    }

    static async Task<LrclibTrack?> SafeAsync(Func<Task<LrclibTrack?>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("manual search: " + ex.Message);
            return null;
        }
    }

    public void ApplyManual(LrclibTrack result)
    {
        if (Track is null) return;
        // Invalidates the resolve in progress so it doesn't overwrite the manual pick.
        _resolveGeneration++;
        _cts?.Cancel();
        var key = Track.Key;
        if (result.Provider is "Lrcmux" or "LyricsPlus")
        {
            // Lrcmux and LyricsPlus lyrics have no stable id, so a pinned copy
            // is stored that does not expire with the cache.
            _settings.Settings.ManualOverrides.Remove(key);
            ManualPins[key] = result.Provider;
            SavePinned(key, result);
        }
        else
        {
            if (ManualPins.Remove(key)) DeletePinned(key);
            _settings.Settings.ManualOverrides[key] = result.Id;
            SaveCache(AlbumCacheKey(LyricSource.Lrclib, key, Track.Album), result);
        }
        _settings.ScheduleSave();
        Apply(Track, result);
    }

    static string PinnedPath(string trackKey) =>
        Path.Combine(StoragePaths.CacheDirectory, "pinned", Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("pin|" + trackKey)))[..24] + ".json");

    void SavePinned(string trackKey, LrclibTrack track)
    {
        try
        {
            var path = PinnedPath(trackKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new CacheEntry { Version = 2, FetchedAtUtc = DateTime.UtcNow, Track = track });
            if (json.Length > MaxCacheBytes) return;
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("pinned save: " + ex.Message);
        }
    }

    LrclibTrack? LoadPinned(string trackKey)
    {
        try
        {
            var path = PinnedPath(trackKey);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxCacheBytes) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllBytes(path), CacheJsonOptions);
            if (entry?.Track is null || !LrcLibClient.ValidateTrack(entry.Track)) return null;
            return entry.Track;
        }
        catch
        {
            return null;
        }
    }

    static void DeletePinned(string trackKey)
    {
        try { File.Delete(PinnedPath(trackKey)); } catch { }
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
            var path = CachePath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Atomic write: if the app closes halfway, no truncated JSON is left
            // behind that would force the lyrics to be downloaded again.
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            PruneCacheOccasionally();
        }
        catch (Exception ex)
        {
            Log.Write("cache save: " + ex.Message);
        }
    }

    internal const int MaxCachedLyrics = 2000;
    static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(14);
    // Old copies are kept longer than they are trusted, for when there is no internet.
    static readonly TimeSpan StaleLifetime = TimeSpan.FromDays(90);
    static int _savesSincePrune = int.MaxValue / 2;

    // The lyrics cache used to grow forever. Every so many writes, expired
    // entries are removed and, if there are too many, the oldest ones.
    static void PruneCacheOccasionally()
    {
        if (Interlocked.Increment(ref _savesSincePrune) < 50) return;
        Interlocked.Exchange(ref _savesSincePrune, 0);
        _ = Task.Run(() => PruneCache(StoragePaths.CacheDirectory, MaxCachedLyrics, StaleLifetime, DateTime.UtcNow));
    }

    internal static int PruneCache(string directory, int maxFiles, TimeSpan lifetime, DateTime nowUtc)
    {
        var removed = 0;
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) return 0;
            var files = dir.GetFiles("*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (i < maxFiles && nowUtc - file.LastWriteTimeUtc <= lifetime) continue;
                try
                {
                    file.Delete();
                    removed++;
                }
                catch
                {
                }
            }

            foreach (var temp in dir.GetFiles("*.json.tmp", SearchOption.TopDirectoryOnly))
            {
                if (nowUtc - temp.LastWriteTimeUtc < TimeSpan.FromHours(1)) continue;
                try { temp.Delete(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Write("cache prune: " + ex.Message);
        }

        if (removed > 0) Log.Write($"cache prune: removed {removed} old lyrics");
        return removed;
    }


    LrclibTrack? LoadCache(string key, bool allowStale = false)
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
            if (!allowStale && DateTime.UtcNow - entry.FetchedAtUtc > CacheLifetime) return null;
            if (!LrcLibClient.ValidateTrack(entry.Track)) return null;
            return entry.Track;
        }
        catch
        {
            return null;
        }
    }
}
