using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.Media.Control;
using Glowline.Localization;
using Glowline.Models;
using Glowline.Services;

namespace Glowline;

internal static class SelfTest
{
    public static int Run()
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var failures = 0;
        var report = new StringBuilder();

        void Check(bool ok, string name)
        {
            report.AppendLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) failures++;
        }

        try
        {
            var lrc = "[offset:+250]\n[00:12.50]Line A\n[00:15.00][01:00.00]Line B\n[00:20.25]<00:20.25>Hello <00:21.00>world";
            var lines = LrcParser.Parse(lrc);
            Check(lines.Count == 4, $"parser count == 4 (got {lines.Count})");
            Check(lines.Count > 0 && Math.Abs(lines[0].Time - 12.75) < 0.01, "parser offset +250ms");
            Check(lines.Count > 1 && lines[1].Text == "Line B", "parser multi-timestamp");
            Check(lines.Count > 2 && lines[2].Text == "Hello world", "parser strips word tags");
            var wordIndex = lines.FindIndex(l => l.Words is { Count: > 0 });
            Check(wordIndex >= 0 && lines[wordIndex].Words!.Count == 2, "parser word count == 2");
            Check(wordIndex >= 0 && Math.Abs(lines[wordIndex].Words![1].Time - 21.25) < 0.01, "parser word timing +250ms");

            var yaml = "version: '1.0'\nmetadata:\n  title: X\nlines:\n- text: Hello world\n  start_ms: 5000\n  end_ms: 8000\n  words:\n  - text: 'Hello '\n    start_ms: 5000\n  - text: world\n    start_ms: 6500\n";
            var yamlLines = LyricsFileParser.Parse(yaml);
            Check(yamlLines is { Count: 1 } && yamlLines[0].Words is { Count: 2 } &&
                  Math.Abs(yamlLines[0].Words![1].Time - 6.5) < 0.001, "lyricsfile parser");

            var intro = new System.Collections.Generic.List<LrcLine> { new() { Time = 8.9, Text = "first" } };
            Check(IntroDots.IsIntro(intro, 3), "intro dots shown during 8.9s intro");
            Check(!IntroDots.IsIntro(intro, 9), "intro dots hidden after first line");
            var quick = new System.Collections.Generic.List<LrcLine> { new() { Time = 2.0, Text = "first" } };
            Check(!IntroDots.IsIntro(quick, 0.5), "intro dots skipped under 7s");

            var paren = LrcParser.Parse("[00:01.00]Hello (oh oh) world\n[00:02.00](just backing)");
            var processedParen = LyricText.Process(paren);
            Check(processedParen.Count == 2 && processedParen[0].Text == "Hello world" &&
                  processedParen[0].Background == "oh oh", "parenthetical text moved down");
            Check(processedParen.Count == 2 && processedParen[1].Text.Length == 0 &&
                  processedParen[1].Background == "just backing", "parenthetical-only line");

            var withWords = new System.Collections.Generic.List<LrcLine>
            {
                new()
                {
                    Time = 1,
                    Text = "Hello (oh) world",
                    Words = new System.Collections.Generic.List<LrcWord>
                    {
                        new() { Time = 1, Text = "Hello " },
                        new() { Time = 1.2, Text = "(oh) " },
                        new() { Time = 1.5, Text = "world" },
                    },
                },
            };
            var processedWords = LyricText.Process(withWords);
            Check(processedWords[0].Words is { Count: 2 } &&
                  !string.Concat(processedWords[0].Words!.Select(w => w.Text)).Contains('(') &&
                  processedWords[0].Background == "oh", "parenthetical words split");

            var plusNoSpaceJson = """
{"type":"Word","metadata":{"source":"Test","title":"Test","artist":"Artist"},"lyrics":[{"time":1000,"duration":2000,"text":"Hello, there friend","syllabus":[{"time":1000,"text":"Hello,"},{"time":1500,"text":"there"},{"time":1800,"text":"friend"}]}]}
""";
            var plusNoSpaceTrack = LyricsPlusClient.TrackFromJson(plusNoSpaceJson, "Fallback", "Fallback");
            var plusNoSpaceLines = LrcParser.Parse(plusNoSpaceTrack?.SyncedLyrics ?? "");
            Check(plusNoSpaceTrack?.HasWordSync == true && plusNoSpaceLines.Count == 1 &&
                  plusNoSpaceLines[0].Text == "Hello, there friend" &&
                  plusNoSpaceLines[0].Words is { Count: 3 } &&
                  string.Concat(plusNoSpaceLines[0].Words!.Select(w => w.Text)) == "Hello, there friend",
                "LyricsPlus word conversion preserves missing spaces");

            var plusLineJson = """
{"type":"Line","metadata":{},"lyrics":[{"time":1500,"text":"Only line timing"},{"time":2500,"text":"Second line"}]}
""";
            var plusLineTrack = LyricsPlusClient.TrackFromJson(plusLineJson, "Fallback", "Fallback");
            var plusLineParsed = LrcParser.Parse(plusLineTrack?.SyncedLyrics ?? "");
            Check(plusLineTrack?.HasWordSync == false && plusLineParsed.Count == 2 &&
                  Math.Abs(plusLineParsed[0].Time - 1.5) < 0.01 &&
                  plusLineParsed.All(line => line.Words is null),
                "LyricsPlus line conversion keeps line-only timing");

            var plusCjkJson = """
{"type":"Word","metadata":{},"lyrics":[{"time":1000,"text":"你好世界","syllabus":[{"time":1000,"text":"你好"},{"time":1500,"text":"世界"}]}]}
""";
            var plusCjkTrack = LyricsPlusClient.TrackFromJson(plusCjkJson, "Fallback", "Fallback");
            var plusCjkLines = LrcParser.Parse(plusCjkTrack?.SyncedLyrics ?? "");
            Check(plusCjkLines.Count == 1 && plusCjkLines[0].Text == "你好世界" &&
                  plusCjkLines[0].Words is { Count: 2 } &&
                  string.Concat(plusCjkLines[0].Words!.Select(w => w.Text)) == "你好世界",
                "LyricsPlus word conversion avoids inventing spaces for CJK");

            var plusMismatchJson = """
{"type":"Word","metadata":{},"lyrics":[{"time":1000,"text":"Hello there friend","syllabus":[{"time":1000,"text":"Hello,"},{"time":1500,"text":"there"},{"time":1800,"text":"friend"}]}]}
""";
            var plusMismatchTrack = LyricsPlusClient.TrackFromJson(plusMismatchJson, "Fallback", "Fallback");
            var plusMismatchLines = LrcParser.Parse(plusMismatchTrack?.SyncedLyrics ?? "");
            var plusMismatchWords = plusMismatchLines.FirstOrDefault()?.Words;
            Check(plusMismatchLines.Count == 1 && plusMismatchLines[0].Text == "Hello there friend" &&
                  plusMismatchWords is { Count: 3 } &&
                  string.Concat(plusMismatchWords!.Select(w => w.Text)) == "Hello there friend",
                "LyricsPlus word conversion falls back to line chunks");

            var plusCreditsJson = """
{"type":"Line","metadata":{"title":"Bohemian Rhapsody","artist":"Queen","songWriters":["Freddie Mercury"]},"lyrics":[{"time":0,"duration":90,"text":"Bohemian Rhapsody (Remastered 2011) - Queen"},{"time":138,"duration":36,"text":"Freddie Mercury"},{"time":370,"duration":2624,"text":"Is this the real life"},{"time":2000,"duration":100,"text":"Mike Stock/Matt Aitken/Peter Waterman"}]}
""";
            var plusCreditsTrack = LyricsPlusClient.TrackFromJson(plusCreditsJson, "Bohemian Rhapsody", "Queen");
            var plusCreditsLines = LrcParser.Parse(plusCreditsTrack?.SyncedLyrics ?? "");
            Check(plusCreditsLines.Count == 1 && plusCreditsLines[0].Text == "Is this the real life",
                "LyricsPlus conversion discards title and songwriter credit lines");

            Check(new AppSettings().LyricsSource == LyricSource.Lrcmux, "default lyric source is Lrcmux");
            Check(new AppSettings().InterfaceTheme == InterfaceTheme.Dark, "default interface theme is Dark");
            Check(new AppSettings().InterfaceGlassEnabled, "translucent interface is enabled by default");
            Check(new AppSettings().Language == AppLanguage.English, "default language is English");
            Check(new AppSettings().CoverPaletteEnabled, "cover palette is enabled by default");
            var appDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var dataDirectory = Path.GetFullPath(SettingsService.DataDir);
            Check(!dataDirectory.StartsWith(appDirectory, StringComparison.OrdinalIgnoreCase),
                "settings live outside the app directory");
            var settingsJson = System.Text.Json.JsonSerializer.Serialize(new AppSettings
            {
                Language = AppLanguage.Japanese,
                FontScale = 1.25,
                CoverPaletteEnabled = false,
                LyricsSource = LyricSource.LyricsPlus,
                InterfaceTheme = InterfaceTheme.Light,
                InterfaceGlassEnabled = false,
            });
            var restoredSettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(settingsJson);
            Check(restoredSettings?.Language == AppLanguage.Japanese &&
                  Math.Abs(restoredSettings.FontScale - 1.25) < 0.001 &&
                  !restoredSettings.CoverPaletteEnabled &&
                  restoredSettings.LyricsSource == LyricSource.LyricsPlus &&
                  restoredSettings.InterfaceTheme == InterfaceTheme.Light &&
                  !restoredSettings.InterfaceGlassEnabled,
                "settings round-trip preserves preferences");
            var designSettingsJson = System.Text.Json.JsonSerializer.Serialize(new AppSettings
            {
                ActiveDesign = new DesignProfile { Name = "Persistent design", Layout = DesignLayout.Minimal },
            });
            var restoredDesignSettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(designSettingsJson);
            Check(restoredDesignSettings?.ActiveDesign?.Name == "Persistent design" &&
                  restoredDesignSettings.ActiveDesign.Layout == DesignLayout.Minimal,
                "settings round-trip preserves active design");
            var artworkSeed = new DesignProfile();
            artworkSeed.Artwork.SizePercent = 34.6;
            var artworkDesigns = new DesignService(artworkSeed);
            Check(Math.Abs(artworkDesigns.Active.Artwork.AnimatedSizePercent - 34.6) < 0.001,
                "existing designs inherit the static cover size for animations");
            artworkDesigns.Active.Artwork.SizePercent = 50;
            Check(Math.Abs(artworkDesigns.Active.Artwork.AnimatedSizePercent - 34.6) < 0.001,
                "animated cover size stays independent from the static size");
            artworkDesigns.Active.Artwork.AnimatedSizePercent = 5;
            Check(Math.Abs(artworkDesigns.Active.Artwork.AnimatedSizePercent - 10) < 0.001,
                "animated cover size clamps low");
            artworkDesigns.Active.Artwork.AnimatedSizePercent = 90;
            Check(Math.Abs(artworkDesigns.Active.Artwork.AnimatedSizePercent - 60) < 0.001,
                "animated cover size clamps high");
            Check(UpdateService.IsSha256Digest("sha256:" + new string('a', 64)),
                "updater accepts a valid SHA-256 digest");
            Check(!UpdateService.IsSha256Digest(null) && !UpdateService.IsSha256Digest("sha256:abc"),
                "updater rejects missing or short digests");
            var glowTestPath = Path.Combine(SettingsService.DataDir, "selftest.glow");
            try
            {
                var testProfile = new DesignProfile { Name = "Test glow", Layout = DesignLayout.Karaoke };
                var testPreview = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII=");
                GlowPackageService.ExportAsync(testProfile, testPreview, glowTestPath).GetAwaiter().GetResult();
                var imported = GlowPackageService.ImportAsync(glowTestPath).GetAwaiter().GetResult();
                Check(imported.Profile.Name == "Test glow" && imported.Profile.Layout == DesignLayout.Karaoke,
                    $"glow package round-trip preserves design (got '{imported.Profile.Name}' {imported.Profile.Layout})");
                Check(imported.PreviewPng is { Length: > 24 }, "glow package round-trip preserves preview");
                var edge = new DesignProfile();
                edge.Typography.TitleSize = 48;
                edge.Typography.ArtistSize = 10;
                edge.Typography.LyricSize = 64;
                edge.Typography.LineHeight = 2;
                edge.Lyrics.WidthPercent = 100;
                edge.Lyrics.Spacing = 80;
                edge.Lyrics.VerticalOffset = -200;
                edge.Artwork.SizePercent = 60;
                edge.Artwork.AnimatedSizePercent = 20;
                edge.Artwork.CornerRadius = 60;
                edge.Artwork.BorderThickness = 8;
                edge.Background.Blur = 120;
                edge.Background.Dim = 1;
                edge.Background.CoverScale = 2;
                edge.Animations.WordDurationMs = 400;
                edge.Animations.TransitionDurationMs = 1200;
                edge.Animations.WordIntensity = 1;
                edge.Animations.CoverIntensity = 2;
                GlowPackageService.ExportAsync(edge, null, glowTestPath).GetAwaiter().GetResult();
                var edgeBack = GlowPackageService.ImportAsync(glowTestPath).GetAwaiter().GetResult().Profile;
                Check(edgeBack.Typography.TitleSize == 48 && edgeBack.Typography.LyricSize == 64 &&
                      edgeBack.Lyrics.WidthPercent == 100 && edgeBack.Lyrics.Spacing == 80 &&
                      edgeBack.Lyrics.VerticalOffset == -200 && edgeBack.Artwork.SizePercent == 60 &&
                      edgeBack.Artwork.AnimatedSizePercent == 20 &&
                      edgeBack.Artwork.CornerRadius == 60 && edgeBack.Artwork.BorderThickness == 8 &&
                      edgeBack.Background.Blur == 120 && edgeBack.Background.Dim == 1 &&
                      edgeBack.Background.CoverScale == 2 && edgeBack.Animations.WordDurationMs == 400 &&
                      edgeBack.Animations.TransitionDurationMs == 1200 && edgeBack.Animations.WordIntensity == 1,
                    "glow package round-trip preserves boundary values");
                File.WriteAllText(glowTestPath, "not a zip");
                var rejected = false;
                try { GlowPackageService.ImportAsync(glowTestPath).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "glow package rejects invalid archives");
                var invalidProfile = new DesignProfile();
                invalidProfile.Palette.BackgroundColor = "not-a-color";
                var rejectedExport = false;
                try { GlowPackageService.ExportAsync(invalidProfile, null, glowTestPath).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejectedExport = true; }
                Check(rejectedExport, "glow export rejects invalid design values");
                var rejectedPreview = false;
                try { GlowPackageService.ExportAsync(new DesignProfile(), new byte[32], glowTestPath).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejectedPreview = true; }
                Check(rejectedPreview, "glow export rejects invalid preview images");
                var rejectedName = false;
                try
                {
                    GlowPackageService.ExportAsync(new DesignProfile { Name = new string('n', 201) }, null, glowTestPath)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidDataException) { rejectedName = true; }
                Check(rejectedName, "glow export rejects oversized profile names");
            }
            finally
            {
                try { File.Delete(glowTestPath); } catch { }
            }
            var manifestJson = """
                {
                  "schemaVersion": 1,
                  "product": "Glowline",
                  "version": "9.9.9",
                  "tag": "v9.9.9",
                  "notes": "Fixed bugs and improvements",
                  "assets": [
                    { "Name": "Glowline.exe", "Size": 123, "Sha256": "0000000000000000000000000000000000000000000000000000000000000000" }
                  ]
                }
                """;
            var manifest = ReleaseClient.ParseManifest(manifestJson);
            Check(manifest.Version == new Version(9, 9, 9) && manifest.Tag == "v9.9.9",
                "public update manifest parses version and tag");
            Check(manifest.Notes == "Fixed bugs and improvements",
                "public update manifest parses release notes");
            Check(manifest.Assets.Count == 1 &&
                  manifest.Assets[0].DownloadUrl == "https://github.com/FAASE200/Glowline/releases/download/v9.9.9/Glowline.exe" &&
                  ReleaseClient.IsSha256Digest(manifest.Assets[0].Digest),
                "public update manifest builds download URL and digest");
            Check(!typeof(UpdateService).Assembly.GetManifestResourceNames()
                    .Any(n => n.Contains("update.token", StringComparison.OrdinalIgnoreCase)),
                "updater token is not embedded");
            Check(Loc.Languages.Count == 10, $"10 languages registered (got {Loc.Languages.Count})");
            var missing = Loc.MissingKeys();
            Check(missing.Count == 0,
                $"localization dictionaries complete (missing {missing.Count}: {string.Join(", ", missing.Take(5))})");
            Loc.SetLanguage(AppLanguage.Spanish);
            var spanishLabel = Loc.T("Language");
            Check(spanishLabel == "Idioma", $"Spanish dictionary is active (got '{spanishLabel}', culture {Loc.Culture.Name})");
            Check(Loc.T("Designer.Value.LyricAnimation.Pulse") == "Pulso" &&
                  Loc.T("Designer.Value.DesignLayout.Karaoke") == "Karaoke",
                "designer enum labels are localized");
            Loc.SetLanguage(AppLanguage.Japanese);
            var japaneseLabel = Loc.T("Language");
            Check(japaneseLabel == "言語", $"Japanese dictionary switches (got '{japaneseLabel}', culture {Loc.Culture.Name})");
            Check(Loc.F("CurrentVersionFormat", "9.9.9").Contains("9.9.9"),
                $"formatted strings keep arguments (got '{Loc.F("CurrentVersionFormat", "9.9.9")}')");
            Loc.SetLanguage(AppLanguage.Spanish);
            Check(new CoverArtResult { PixelWidth = 600, PixelHeight = 600 }.IsHighResolution,
                "600px player cover is high quality");
            Check(!new CoverArtResult { PixelWidth = 599, PixelHeight = 1000 }.IsHighResolution,
                "under-600px player cover needs external art");

            var coverTrack = new TrackInfo
            {
                Title = "Alo",
                Artist = "Katteyes & Sinaka",
                Album = "Alo - Single",
                DurationSeconds = 145,
            };
            var correctCover = CoverArtService.Score(
                coverTrack, "Alo", "Katteyes y Sinaka", "Alo - Single", 145, out var correctTitleOk);
            var wrongArtist = CoverArtService.Score(
                coverTrack, "Alo", "Otro Artista", "Alo - Single", 145, out _);
            var wrongAlbum = CoverArtService.Score(
                coverTrack, "Alo", "Katteyes & Sinaka", "Otro Álbum", 145, out _);
            var similarTitle = CoverArtService.Score(
                coverTrack, "Alo Alo", "Katteyes & Sinaka", "Alo - Single", 145, out _);
            Check(correctTitleOk && correctCover > 0,
                $"cover accepts the same song with collab formatting (score {correctCover:F1})");
            Check(double.IsNegativeInfinity(wrongArtist), "cover rejects a similar title by another artist");
            Check(double.IsNegativeInfinity(wrongAlbum), "cover rejects the same title from another album");
            Check(double.IsNegativeInfinity(similarTitle), "cover rejects a similar but different title");
            Check(CoverArtService.SameAlbum("BORONDO", "BORONDO (Deluxe)"),
                "cover reuses the same album artwork across editions");
            Check(!CoverArtService.SameAlbum("BORONDO", "Otro álbum"),
                "cover does not reuse artwork from another album");
            Check(CoverArtService.CacheKey(coverTrack) != CoverArtService.CacheKey(new TrackInfo
                {
                    Title = coverTrack.Title,
                    Artist = coverTrack.Artist,
                    Album = "Otro Álbum",
                    DurationSeconds = coverTrack.DurationSeconds,
                }),
                "cover cache separates albums with the same artist+title");
            Check(!LyricsService.HasUsableSync(new LrclibTrack { PlainLyrics = "solo texto" }),
                "plain lyrics need LRCLIB fallback");
            Check(LyricsService.HasUsableSync(new LrclibTrack
                {
                    SyncedLyrics = "[00:01.00]Line A\n[00:02.00]Line B",
                }),
                "synced lyrics are usable without fallback");

            var master = "#EXTM3U\n" +
                         "#EXT-X-STREAM-INF:CODECS=\"hvc1.2\",RESOLUTION=1080x1080\nhttps://cdn.test/h.m3u8\n" +
                         "#EXT-X-STREAM-INF:CODECS=\"avc1.64001f\",RESOLUTION=960x960\nhttps://cdn.test/a960.m3u8\n" +
                         "#EXT-X-STREAM-INF:CODECS=\"avc1.64001f\",RESOLUTION=486x486\nhttps://cdn.test/a486.m3u8\n";
            var animQuery = AnimatedArtworkService.SearchQuery(new TrackInfo
            {
                Artist = "Linkin Park",
                Title = "Numb",
                Album = "Meteora",
            });
            Check(animQuery == "artist=Linkin%20Park&album=Meteora",
                $"animated searches by artist+album only (got '{animQuery}')");

            var variant = AnimatedArtworkService.PickAvcVariant(
                master, new Uri("https://cdn.test/master.m3u8"));
            Check(variant is { Width: 960 },
                $"animated picks AVC 960 (got {variant?.Width.ToString() ?? "null"})");
            var media = "#EXTM3U\n#EXT-X-MAP:URI=\"video-.mp4\",BYTERANGE=\"896@0\"\n" +
                        "#EXTINF:2.8,\n#EXT-X-BYTERANGE:1000@896\nvideo-.mp4\n";
            var file = AnimatedArtworkService.FindMediaFile(
                media, new Uri("https://cdn.test/path/playlist.m3u8"));
            Check(file?.ToString() == "https://cdn.test/path/video-.mp4",
                "animated resolves the fMP4 file");

            var mp4Path = Path.Combine(Path.GetTempPath(),
                "glowline-fmp4-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                File.WriteAllBytes(mp4Path, MakeTestMp4());
                var rebased = AnimatedArtworkService.NormalizeFmp4(mp4Path);
                var patched = File.ReadAllBytes(mp4Path);
                var bases = ReadTfdtBases(patched);
                Check(rebased && bases.SequenceEqual(new uint[] { 0, 68 }),
                    $"fMP4 timeline rebased (got {string.Join(',', bases)})");
                Check(ReadBoxDuration(patched, "mvhd") == 136,
                    $"fMP4 duration filled (got {ReadBoxDuration(patched, "mvhd")})");
            }
            finally
            {
                try { File.Delete(mp4Path); } catch { }
            }

            var t0 = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
            var smoother = new TimelineSmoother();
            Check(Math.Abs(smoother.Update(3, true, t0, t0, "web") - 3) < 0.01,
                "timeline starts at raw");
            Check(Math.Abs(smoother.Update(3, true, t0.AddSeconds(1), t0.AddSeconds(1), "web") - 4) < 0.05,
                "timeline advances when raw is frozen");
            Check(Math.Abs(smoother.Update(3, true, t0.AddSeconds(2), t0.AddSeconds(2), "web") - 5) < 0.05,
                "timeline keeps advancing");
            Check(Math.Abs(smoother.Update(4, true, t0.AddSeconds(3), t0.AddSeconds(3), "web") - 4) < 0.05,
                "timeline follows a live raw update");
            smoother.NotifySeek(60, t0.AddSeconds(4));
            Check(Math.Abs(smoother.Update(4, true, t0.AddSeconds(4.2), t0.AddSeconds(4.2), "web") - 60.2) < 0.1,
                "timeline holds seek while raw is stale");
            Check(Math.Abs(smoother.Update(60, true, t0.AddSeconds(5), t0.AddSeconds(5), "web") - 61) < 0.1,
                "timeline continues after seek");
            Check(Math.Abs(smoother.Update(60, false, t0.AddSeconds(6), t0.AddSeconds(6), "web") - 60) < 0.01,
                "timeline pause uses raw");
            Check(Math.Abs(smoother.Update(60, true, t0.AddSeconds(8), t0.AddSeconds(8), "web") - 60) < 0.05,
                "timeline resumes from raw");

            Check(!new AppSettings().SoftwareRendering, "software rendering is off by default");

            var hostPolicy = ExternalHostPolicy.Of("example.com");
            Check(hostPolicy.Allows(new Uri("https://api.example.com/path")),
                "external policy accepts an allowlisted subdomain");
            Check(!hostPolicy.Allows(new Uri("http://api.example.com/path")),
                "external policy rejects plain HTTP");
            Check(!hostPolicy.Allows(new Uri("https://example.com.evil.test/path")),
                "external policy rejects lookalike hosts");
            Check(!hostPolicy.Allows(new Uri("https://user:pw@example.com/path")),
                "external policy rejects URL credentials");
            Check(!hostPolicy.Allows(new Uri("https://example.com:8443/path")),
                "external policy rejects non-default ports");
            Check(ExternalHttp.IsJsonMediaType("application/json") &&
                  ExternalHttp.IsJsonMediaType("application/problem+json") &&
                  !ExternalHttp.IsJsonMediaType("text/javascript") &&
                  ExternalHttp.IsJsonMediaType("text/javascript", allowJavaScript: true),
                "external content types accept only JSON (plus the iTunes exception)");
            using (var wrongType = new HttpResponseMessage
                   {
                       Content = new StringContent("{}", Encoding.UTF8, "text/html"),
                   })
            {
                var rejectedType = false;
                try { ExternalHttp.EnsureJsonContentType(wrongType); }
                catch (ExternalDataException) { rejectedType = true; }
                Check(rejectedType, "external JSON rejects HTML responses");
            }
            using (var oversized = new HttpResponseMessage
                   {
                       Content = new ByteArrayContent(new byte[128]),
                   })
            {
                var rejectedSize = false;
                try
                {
                    ExternalHttp.ReadBytesAsync(oversized, 64, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (ExternalDataException) { rejectedSize = true; }
                Check(rejectedSize, "external reader rejects oversized bodies");
            }

            Check(LrcParser.Parse(new string('x', 512 * 1024 + 1)).Count == 0,
                "parser rejects oversized LRC input");
            var hostileTime = LrcParser.Parse("[9999:99.99]Clamped");
            Check(hostileTime.Count == 1 && Math.Abs(hostileTime[0].Time - 86400) < 0.01,
                "parser clamps hostile timestamps to 24h");
            var manyLrc = new StringBuilder();
            for (var i = 0; i < 6001; i++) manyLrc.Append("[00:01.00]line\n");
            Check(LrcParser.Parse(manyLrc.ToString()).Count == 0,
                "parser rejects LRC with too many lines");
            var manyWords = new StringBuilder("[00:01.00]");
            for (var i = 0; i < 501; i++) manyWords.Append("<0:0>w");
            var manyWordsLines = LrcParser.Parse(manyWords.ToString());
            Check(manyWordsLines.Count == 1 && manyWordsLines[0].Words is null,
                "parser drops word timing above the cap");
            Check(LyricsFileParser.Parse(new string(' ', 512 * 1024 + 1)) is null,
                "lyricsfile parser rejects oversized input");

            var lrcmuxValid = LrcmuxClient.TrackFromJson("""
{"track":{"title":"Song","artist":"Artist","duration":180},"lines":[{"text":"Hello ","start":1000,"words":[{"text":"Hello ","start":1000},{"text":"world","start":1500}]}]}
""", "Fallback", "Fallback");
            Check(lrcmuxValid is { HasWordSync: true } &&
                  LrcParser.Parse(lrcmuxValid.SyncedLyrics!).Count == 1,
                "Lrcmux accepts a valid word-synced payload");
            var lrcmuxSpaced = LrcmuxClient.TrackFromJson("""
{"lines":[{"text":"Now he's thinkin'","start":0,"words":[{"text":"Now","start":0},{"text":"he's","start":500},{"text":"thinkin'","start":1000}]}]}
""", "Fallback", "Fallback");
            var lrcmuxSpacedLines = LrcParser.Parse(lrcmuxSpaced?.SyncedLyrics ?? "");
            Check(lrcmuxSpacedLines.Count == 1 && lrcmuxSpacedLines[0].Text == "Now he's thinkin'" &&
                  lrcmuxSpacedLines[0].Words is { Count: 3 } &&
                  string.Concat(lrcmuxSpacedLines[0].Words!.Select(w => w.Text)) == "Now he's thinkin'",
                "Lrcmux restores spaces between word tokens");
            Check(LrcmuxClient.TrackFromJson("""
{"lines":[{"text":"x","start":90000000}]}
""", "Fallback", "Fallback") is null,
                "Lrcmux rejects timestamps beyond 24h");
            var lrcmuxMany = new StringBuilder("{\"lines\":[");
            for (var i = 0; i < 6001; i++)
            {
                if (i > 0) lrcmuxMany.Append(',');
                lrcmuxMany.Append("{\"text\":\"x\",\"start\":").Append(i * 10).Append('}');
            }
            lrcmuxMany.Append("]}");
            Check(LrcmuxClient.TrackFromJson(lrcmuxMany.ToString(), "Fallback", "Fallback") is null,
                "Lrcmux rejects payloads with too many lines");
            var lrcmuxWords = new StringBuilder("{\"lines\":[{\"text\":\"x\",\"start\":0,\"words\":[");
            for (var i = 0; i < 501; i++)
            {
                if (i > 0) lrcmuxWords.Append(',');
                lrcmuxWords.Append("{\"text\":\"w\",\"start\":").Append(i).Append('}');
            }
            lrcmuxWords.Append("]}]}");
            Check(LrcmuxClient.TrackFromJson(lrcmuxWords.ToString(), "Fallback", "Fallback") is null,
                "Lrcmux rejects lines with too many words");

            Check(LyricsPlusClient.TrackFromJson("""
{"lyrics":[{"time":1000000000000,"text":"Hostile"}]}
""", "Fallback", "Fallback") is null,
                "LyricsPlus rejects timestamps beyond 24h");
            var plusMany = new StringBuilder("{\"lyrics\":[");
            for (var i = 0; i < 6001; i++)
            {
                if (i > 0) plusMany.Append(',');
                plusMany.Append("{\"time\":").Append(i * 10).Append(",\"text\":\"x\"}");
            }
            plusMany.Append("]}");
            Check(LyricsPlusClient.TrackFromJson(plusMany.ToString(), "Fallback", "Fallback") is null,
                "LyricsPlus rejects payloads with too many lines");
            var plusLongSyllable = "{\"lyrics\":[{\"time\":0,\"text\":\"x\",\"syllabus\":[{\"time\":0,\"text\":\"" +
                                   new string('s', 513) + "\"}]}]}";
            Check(LyricsPlusClient.TrackFromJson(plusLongSyllable, "Fallback", "Fallback") is null,
                "LyricsPlus rejects oversized syllables");

            Check(!LrcLibClient.ValidateTrack(new LrclibTrack
                  {
                      SyncedLyrics = new string('x', 512 * 1024 + 1),
                  }),
                "LRCLIB rejects lyrics above the size cap");
            Check(LrcLibClient.ValidateTrack(new LrclibTrack
                  {
                      TrackName = "Song",
                      ArtistName = "Artist",
                      SyncedLyrics = "[00:01.00]Line",
                  }),
                "LRCLIB accepts a valid track");
            Check(CoverArtService.IsAcceptableImageSize(4096, 4096) &&
                  !CoverArtService.IsAcceptableImageSize(4097, 100) &&
                  !CoverArtService.IsAcceptableImageSize(10000, 100),
                "cover art rejects oversized dimensions");

            var tooManyAssets = new StringBuilder(
                "{\"version\":\"9.9.9\",\"tag\":\"v9.9.9\",\"assets\":[");
            for (var i = 0; i < 65; i++)
            {
                if (i > 0) tooManyAssets.Append(',');
                tooManyAssets.Append("{\"name\":\"a").Append(i).Append("\"}");
            }
            tooManyAssets.Append("]}");
            var rejectedAssets = false;
            try { ReleaseClient.ParseManifest(tooManyAssets.ToString()); }
            catch (InvalidDataException) { rejectedAssets = true; }
            Check(rejectedAssets, "update manifest rejects too many assets");
        }
        catch (Exception ex)
        {
            Check(false, "parser threw: " + ex.Message);
        }

        try
        {
            var (ok, details) = Task.Run(async () =>
            {
                var sb = new StringBuilder();
                var allOk = true;

                var client = new LrcLibClient();
                var track = await client.GetExactAsync("Bohemian Rhapsody", "Queen", "A Night at the Opera", 355, CancellationToken.None);
                sb.AppendLine(track is null
                    ? "LRCLIB exact: no result"
                    : $"LRCLIB exact: {track.TrackName} — {track.ArtistName} (synced={track.HasSynced}, plain={track.HasPlain})");
                if (track is null || !track.HasSynced)
                {
                    allOk = false;
                }
                else
                {
                    var parsed = LrcParser.Parse(track.SyncedLyrics!);
                    var first = parsed.FirstOrDefault();
                    sb.AppendLine($"parsed lines: {parsed.Count}; first: [{(first?.Time ?? 0):F2}] {first?.Text}");
                    if (parsed.Count < 40) allOk = false;
                }

                var results = await client.SearchByQueryAsync("Bohemian Rhapsody Queen", CancellationToken.None);
                sb.AppendLine($"search 'Bohemian Rhapsody Queen': {results.Count} results");
                if (results.Count == 0) allOk = false;

                var lyrics = new LyricsService(TestSettings(LyricSource.Lrclib), client);
                lyrics.OnSnapshot(new SessionSnapshot
                {
                    Track = new TrackInfo { Title = "Dancing Queen", Artist = "ABBA", Album = "Arrival", DurationSeconds = 231 },
                    DurationSeconds = 231,
                    PlaybackStatus = "Paused",
                    CapturedAt = DateTimeOffset.Now,
                });
                for (var i = 0; i < 40 && lyrics.State is LyricsState.Idle or LyricsState.Searching; i++)
                    await Task.Delay(250);
                var wordLines = lyrics.Lines.Count(l => l.Words is { Count: > 0 });
                sb.AppendLine($"resolve test: state={lyrics.State}, picked='{lyrics.Found?.TrackName}', lines={lyrics.Lines.Count}, wordLines={wordLines}");
                if (lyrics.State != LyricsState.Synced) allOk = false;

                var lyricsWord = new LyricsService(TestSettings(LyricSource.Lrclib), client);
                lyricsWord.OnSnapshot(new SessionSnapshot
                {
                    Track = new TrackInfo { Title = "Espresso", Artist = "Sabrina Carpenter", DurationSeconds = 175 },
                    DurationSeconds = 175,
                    PlaybackStatus = "Paused",
                    CapturedAt = DateTimeOffset.Now,
                });
                for (var i = 0; i < 40 && lyricsWord.State is LyricsState.Idle or LyricsState.Searching; i++)
                    await Task.Delay(250);
                var wordLineCount = lyricsWord.Lines.Count(l => l.Words is { Count: > 0 });
                sb.AppendLine($"word-sync test: state={lyricsWord.State}, picked='{lyricsWord.Found?.TrackName}', wordLines={wordLineCount}");
                if (lyricsWord.State != LyricsState.Synced || wordLineCount < 5) allOk = false;

                var plus = new LyricsPlusClient();
                var plusTrack = await plus.GetAsync("Espresso", "Sabrina Carpenter", "", 175, CancellationToken.None);
                sb.AppendLine(plusTrack is null
                    ? "LyricsPlus get: no result"
                    : $"LyricsPlus get: {plusTrack.TrackName} — {plusTrack.ArtistName} " +
                      $"(source={plusTrack.Source}, synced={plusTrack.HasSynced}, words={plusTrack.HasWordSync})");
                if (plusTrack is null || !plusTrack.HasWordSync)
                {
                    allOk = false;
                }
                else
                {
                    var plusParsed = LrcParser.Parse(plusTrack.SyncedLyrics!);
                    var plusWordLines = plusParsed.Count(l => l.Words is { Count: > 0 });
                    var plusFirst = plusParsed.FirstOrDefault();
                    sb.AppendLine($"LyricsPlus parsed: {plusParsed.Count} lines, {plusWordLines} word lines; " +
                                  $"first=[{(plusFirst?.Time ?? 0):F2}] {plusFirst?.Text}");
                    if (plusParsed.Count < 5 || plusWordLines < 5) allOk = false;
                }

                var plusLyrics = new LyricsService(TestSettings(LyricSource.LyricsPlus), client);
                plusLyrics.OnSnapshot(new SessionSnapshot
                {
                    Track = new TrackInfo { Title = "Espresso", Artist = "Sabrina Carpenter", DurationSeconds = 175 },
                    DurationSeconds = 175,
                    PlaybackStatus = "Paused",
                    CapturedAt = DateTimeOffset.Now,
                });
                for (var i = 0; i < 40 && plusLyrics.State is LyricsState.Idle or LyricsState.Searching; i++)
                    await Task.Delay(250);
                var plusServiceWords = plusLyrics.Lines.Count(l => l.Words is { Count: > 0 });
                sb.AppendLine($"LyricsPlus service: state={plusLyrics.State}, source='{plusLyrics.Found?.Source}', " +
                              $"lines={plusLyrics.Lines.Count}, wordLines={plusServiceWords}");
                if (plusLyrics.State != LyricsState.Synced || plusServiceWords < 5 ||
                    plusLyrics.Found?.Source.StartsWith("LyricsPlus", StringComparison.Ordinal) != true)
                {
                    allOk = false;
                }

                var lrcmux = new LrcmuxClient();
                var lrcmuxTrack = await lrcmux.GetAsync("Espresso", "Sabrina Carpenter", "", 175, CancellationToken.None);
                sb.AppendLine(lrcmuxTrack is null
                    ? "Lrcmux get: no result"
                    : $"Lrcmux get: {lrcmuxTrack.TrackName} — {lrcmuxTrack.ArtistName} " +
                      $"(source={lrcmuxTrack.Source}, synced={lrcmuxTrack.HasSynced}, words={lrcmuxTrack.HasWordSync})");
                if (lrcmuxTrack is null || !lrcmuxTrack.HasWordSync)
                {
                    allOk = false;
                }
                else
                {
                    var lrcmuxLines = LrcParser.Parse(lrcmuxTrack.SyncedLyrics!);
                    var lrcmuxWordLines = lrcmuxLines.Count(l => l.Words is { Count: > 0 });
                    var lrcmuxFirst = lrcmuxLines.FirstOrDefault();
                    sb.AppendLine($"Lrcmux parsed: {lrcmuxLines.Count} lines, {lrcmuxWordLines} word lines; " +
                                  $"first=[{(lrcmuxFirst?.Time ?? 0):F2}] {lrcmuxFirst?.Text}");
                    if (lrcmuxWordLines < 5) allOk = false;
                }

                var lrcmuxLyrics = new LyricsService(TestSettings(LyricSource.Lrcmux), client);
                lrcmuxLyrics.OnSnapshot(new SessionSnapshot
                {
                    Track = new TrackInfo { Title = "Espresso", Artist = "Sabrina Carpenter", DurationSeconds = 175 },
                    DurationSeconds = 175,
                    PlaybackStatus = "Paused",
                    CapturedAt = DateTimeOffset.Now,
                });
                for (var i = 0; i < 40 && lrcmuxLyrics.State is LyricsState.Idle or LyricsState.Searching; i++)
                    await Task.Delay(250);
                var lrcmuxServiceWords = lrcmuxLyrics.Lines.Count(l => l.Words is { Count: > 0 });
                sb.AppendLine($"Lrcmux service: state={lrcmuxLyrics.State}, source='{lrcmuxLyrics.Found?.Source}', " +
                              $"lines={lrcmuxLyrics.Lines.Count}, wordLines={lrcmuxServiceWords}");
                if (lrcmuxLyrics.State != LyricsState.Synced || lrcmuxServiceWords < 5 ||
                    lrcmuxLyrics.Found?.Source.StartsWith("Lrcmux", StringComparison.Ordinal) != true)
                {
                    allOk = false;
                }

                try
                {
                    var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    var sessions = mgr.GetSessions();
                    sb.AppendLine($"SMTC sessions: {sessions.Count}");
                    foreach (var s in sessions)
                    {
                        var extra = "";
                        try
                        {
                            var tl = s.GetTimelineProperties();
                            extra = $" pos={tl.Position} end={tl.EndTime} last={tl.LastUpdatedTime:HH:mm:ss}";
                        }
                        catch
                        {
                        }
                        sb.AppendLine($"  {s.SourceAppUserModelId}{extra}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("SMTC error: " + ex.Message);
                }

                return (allOk, sb.ToString());
            }).GetAwaiter().GetResult();

            Check(ok, "LRCLIB synced fetch + parse");
            report.Append(details);
        }
        catch (Exception ex)
        {
            Check(false, "network test threw: " + ex.Message);
        }

        report.AppendLine(failures == 0 ? "RESULT: OK" : $"RESULT: {failures} failure(s)");

        var path = Path.Combine(SettingsService.DataDir, "selftest.txt");
        try
        {
            Directory.CreateDirectory(SettingsService.DataDir);
            File.WriteAllText(path, report.ToString());
        }
        catch
        {
        }

        try
        {
            Console.WriteLine(report.ToString());
            Console.WriteLine("Report: " + path);
        }
        catch
        {
        }

        return failures == 0 ? 0 : 1;
    }

    static byte[] MakeTestMp4()
    {
        var mvhd = FullBox("mvhd", 24);
        WriteU32(mvhd, 20, 24);
        var mdhd = FullBox("mdhd", 24);
        WriteU32(mdhd, 20, 24);
        var tkhd = FullBox("tkhd", 28);
        var trak = Box("trak", tkhd, Box("mdia", mdhd));
        var moov = Box("moov", mvhd, trak);

        byte[] Fragment(uint baseTime)
        {
            var tfdt = FullBox("tfdt", 4);
            WriteU32(tfdt, 12, baseTime);
            var trun = FullBox("trun", 4);
            WriteU32(trun, 12, 68);
            return Box("moof", FullBox("mfhd", 4), Box("traf", tfdt, trun));
        }

        return Combine(
            Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0")),
            moov,
            Fragment(240),
            Fragment(308));
    }

    static byte[] Box(string type, params byte[][] children)
    {
        var box = new byte[8 + children.Sum(c => c.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, box, 4);
        var offset = 8;
        foreach (var child in children)
        {
            child.CopyTo(box, offset);
            offset += child.Length;
        }
        return box;
    }

    static byte[] FullBox(string type, int bodyLength)
    {
        var payload = new byte[4 + bodyLength];
        return Box(type, payload);
    }

    static byte[] Combine(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    static void WriteU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    static ulong ReadU64(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));

    static List<uint> ReadTfdtBases(byte[] data)
    {
        var bases = new List<uint>();
        for (var i = 0; i + 12 <= data.Length; i++)
        {
            if (data[i + 4] != 't' || data[i + 5] != 'f' ||
                data[i + 6] != 'd' || data[i + 7] != 't') continue;
            var payload = i + 8;
            bases.Add(ReadU32(data, payload + 4));
            i += 11;
        }
        return bases;
    }

    static long ReadBoxDuration(byte[] data, string type)
    {
        for (var i = 0; i + 8 <= data.Length; i++)
        {
            if (Encoding.ASCII.GetString(data, i + 4, 4) != type) continue;
            var payload = i + 8;
            return data[payload] == 1
                ? (long)ReadU64(data, payload + 24)
                : ReadU32(data, payload + 16);
        }
        return -1;
    }

    static SettingsService TestSettings(LyricSource source)
    {
        var settings = new SettingsService();
        settings.Settings.LyricsSource = source;
        settings.Settings.ManualOverrides.Clear();
        return settings;
    }
}
