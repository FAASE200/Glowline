using LyricGlow.Models;
using LyricGlow.Services;

namespace LyricGlow.Core.Tests;

static class ReleaseClientTests
{
    const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    static void ParsesVersionTags()
    {
        Assert.Equal(new Version(0, 9, 21), ReleaseClient.ParseVersion("v0.9.21"));
        Assert.Equal(new Version(1, 2, 0), ReleaseClient.ParseVersion("1.2"));
        Assert.Null(ReleaseClient.ParseVersion("latest"));
        Assert.Null(ReleaseClient.ParseVersion(null));
    }

    [Test]
    static void ValidatesSha256Digests()
    {
        Assert.True(ReleaseClient.IsSha256Digest(Hash));
        Assert.True(ReleaseClient.IsSha256Digest("sha256:" + Hash));
        Assert.False(ReleaseClient.IsSha256Digest("sha256:abc"));
        Assert.False(ReleaseClient.IsSha256Digest(null));
        Assert.True(ReleaseClient.DigestMatches("sha256:" + Hash.ToUpperInvariant(), Hash));
        Assert.False(ReleaseClient.DigestMatches(null, Hash));
    }

    [Test]
    static void ParsesManifestWithDownloadUrls()
    {
        var json = $$"""
            {
              "version": "0.9.22",
              "tag": "v0.9.22",
              "assets": [
                { "name": "LyricGlowSetup-0.9.22.exe", "size": 1234, "sha256": "{{Hash}}" },
                { "name": "" }
              ]
            }
            """;
        var release = ReleaseClient.ParseManifest(json);
        Assert.Equal(new Version(0, 9, 22), release.Version);
        Assert.Equal(1, release.Assets.Count);
        var asset = release.Assets[0];
        Assert.Equal("sha256:" + Hash, asset.Digest);
        Assert.Equal(1234L, asset.Size);
        Assert.Equal(
            "https://github.com/FAASE200/Glowline/releases/download/v0.9.22/LyricGlowSetup-0.9.22.exe",
            asset.DownloadUrl);
    }

    [Test]
    static void ManifestWithoutHashHasNoDigest()
    {
        var release = ReleaseClient.ParseManifest("""{"version":"1.0.0","assets":[{"name":"x.exe"}]}""");
        Assert.Null(release.Assets[0].Digest);
        Assert.Equal("v1.0.0", release.Tag);
    }

    [Test]
    static void RejectsManifestWithoutVersion()
    {
        Assert.Throws<InvalidDataException>(() => ReleaseClient.ParseManifest("""{"assets":[]}"""));
    }
}

static class LyricsServiceTests
{
    static LrclibTrack Result(long id, string title, string artist, double duration, bool synced, bool word = false,
        string album = "") => new()
    {
        Id = id,
        TrackName = title,
        ArtistName = artist,
        AlbumName = album,
        Duration = duration,
        HasWordSync = word,
        SyncedLyrics = synced ? "[00:01.00]a\n[00:02.00]b" : null,
        PlainLyrics = "a\nb",
    };

    [Test]
    static void PickBestPrefersMatchingDuration()
    {
        var track = new TrackInfo { Title = "Song", Artist = "Artist", DurationSeconds = 200 };
        var best = LyricsService.PickBest(new List<LrclibTrack>
        {
            Result(1, "Song", "Artist", 260, synced: true),
            Result(2, "Song", "Artist", 200.4, synced: true),
        }, track);
        Assert.Equal(2L, Assert.NotNull(best).Id);
    }

    [Test]
    static void PickBestPrefersSyncedOverPlain()
    {
        var track = new TrackInfo { Title = "Song", Artist = "Artist", DurationSeconds = 200 };
        var best = LyricsService.PickBest(new List<LrclibTrack>
        {
            Result(1, "Song", "Artist", 200, synced: false),
            Result(2, "Song", "Artist", 201, synced: true),
        }, track);
        Assert.Equal(2L, Assert.NotNull(best).Id);
    }

    [Test]
    static void PickBestUsesAlbumToBreakTies()
    {
        var track = new TrackInfo { Title = "Intro", Artist = "Banda", Album = "Disco B", DurationSeconds = 90 };
        var best = LyricsService.PickBest(new List<LrclibTrack>
        {
            Result(1, "Intro", "Banda", 90, synced: true, album: "Disco A"),
            Result(2, "Intro", "Banda", 90, synced: true, album: "Disco B"),
        }, track);
        Assert.Equal(2L, Assert.NotNull(best).Id);
    }

    [Test]
    static void PickBestIgnoresEmptyResults()
    {
        var track = new TrackInfo { Title = "x", Artist = "y" };
        var empty = new LrclibTrack { Id = 1, TrackName = "x", ArtistName = "y" };
        Assert.Null(LyricsService.PickBest(new List<LrclibTrack> { empty }, track));
        Assert.Null(LyricsService.PickBest(new List<LrclibTrack>(), track));
    }

    [Test]
    static void UsableSyncNeedsMoreThanOneLine()
    {
        Assert.True(LyricsService.HasUsableSync(Result(1, "a", "b", 1, synced: true)));
        Assert.False(LyricsService.HasUsableSync(new LrclibTrack { SyncedLyrics = "[00:01.00]sola" }));
        Assert.False(LyricsService.HasUsableSync(null));
    }

    [Test]
    static void PruneCacheRemovesExpiredAndExcessFiles()
    {
        var dir = Directory.CreateTempSubdirectory("glowline-cache-test").FullName;
        try
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(dir, $"fresh{i}.json");
                File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, now.AddMinutes(-i));
            }
            var old = Path.Combine(dir, "old.json");
            File.WriteAllText(old, "{}");
            File.SetLastWriteTimeUtc(old, now.AddDays(-30));
            var keep = Path.Combine(dir, "settings.txt");
            File.WriteAllText(keep, "no tocar");

            var removed = LyricsService.PruneCache(dir, maxFiles: 3, TimeSpan.FromDays(14), now);

            Assert.Equal(3, removed);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(Path.Combine(dir, "fresh0.json")));
            Assert.False(File.Exists(Path.Combine(dir, "fresh4.json")));
            Assert.True(File.Exists(keep));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

static class TimelineSmootherTests
{
    [Test]
    static void ExtrapolatesWhilePlaying()
    {
        var smoother = new TimelineSmoother();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Near(10, smoother.Update(10, playing: true, t0, t0, "a"));
        Assert.Near(11.5, smoother.Update(10, playing: true, t0, t0.AddSeconds(1.5), "a"));
    }

    [Test]
    static void HoldsPositionWhenPaused()
    {
        var smoother = new TimelineSmoother();
        var t0 = DateTimeOffset.UnixEpoch;
        smoother.Update(42, playing: false, t0, t0, "a");
        Assert.Near(42, smoother.Update(42, playing: false, t0, t0.AddSeconds(5), "a"));
    }

    [Test]
    static void SeekIsHonouredDuringGuard()
    {
        var smoother = new TimelineSmoother();
        var t0 = DateTimeOffset.UnixEpoch;
        smoother.Update(10, playing: true, t0, t0, "a");
        smoother.NotifySeek(60, t0);
        // The player still reports the old position: it is ignored.
        Assert.Near(60.5, smoother.Update(10.5, playing: true, t0, t0.AddSeconds(0.5), "a"));
    }
}

static class AppInfoTests
{
    [Test]
    static void UserAgentIdentifiesTheApp()
    {
        Assert.True(AppInfo.UserAgent.StartsWith("Glowline/"));
        Assert.True(AppInfo.UserAgent.Contains(AppInfo.HomepageUrl));
        using var http = new HttpClient();
        Assert.True(http.DefaultRequestHeaders.UserAgent.TryParseAdd(AppInfo.UserAgent));
        Assert.True(http.DefaultRequestHeaders.UserAgent.TryParseAdd(AppInfo.UserAgent + " updater"));
    }
}

static class CoverArtTests
{
    [Test]
    static void ResizesItunesArtworkUrls()
    {
        const string url = "https://is1-ssl.mzstatic.com/image/thumb/Music/v4/ab/cd/source/100x100bb.jpg";
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/Music/v4/ab/cd/source/512x512bb.jpg",
            CoverArtService.ArtworkAt(url, 512));
    }
}
