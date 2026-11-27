using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glowline.Localization;
using Glowline.Models;
using Glowline.Services;

namespace Glowline;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    Thread? _showThread;
    bool _restoreDesignOnExit;
    DesignProfile? _originalDesign;

    public static bool IsExiting { get; set; }
    public static bool SnapshotMode { get; private set; }
    public static SettingsService Settings { get; private set; } = null!;
    public static DesignService Designs { get; private set; } = null!;
    public static LrcLibClient Client { get; private set; } = null!;
    public static LyricsService Lyrics { get; private set; } = null!;
    public static MediaSessionService Media { get; private set; } = null!;
    public static CoverArtService CoverArt { get; private set; } = null!;
    public static AnimatedArtworkService AnimatedCover { get; private set; } = null!;
    public static UpdateService? Updates { get; private set; }
    public static SoundSenseService? SoundSense { get; private set; }
    public static MainWindow? MainWindowRef { get; private set; }
    public static FullscreenWindow? Primary { get; private set; }
    public static GlobalHotkeyService? Hotkeys { get; private set; }
    public static NowPlayingHub? NowPlaying { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDirOverride = Environment.GetEnvironmentVariable("GLOWLINE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDirOverride))
            StoragePaths.DataDirectory = dataDirOverride;
        else
            LegacyMigration.Run();
        Loc.Initialize(AppLanguage.English);

        var applyTarget = GetArgValue(e.Args, "--apply-update");
        if (!string.IsNullOrEmpty(applyTarget))
        {
            UpdateService.RunApplyUpdate(
                applyTarget,
                GetArgValue(e.Args, "--wait-pid"),
                e.Args.Any(a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase)));
            Shutdown(0);
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        var snapshotDir = GetArgValue(e.Args, "--snapshot");
        SnapshotMode = snapshotDir is not null;
        var seekProbe = e.Args.Any(a => string.Equals(a, "--seekprobe", StringComparison.OrdinalIgnoreCase));

        if (!SnapshotMode && !seekProbe)
        {
            _mutex = new Mutex(true, "Glowline_SingleInstance", out var createdNew);
            if (!createdNew)
            {
                for (var i = 0; i < 20; i++)
                {
                    try
                    {
                        using var show = EventWaitHandle.OpenExisting("Glowline_Show");
                        show.Set();
                        break;
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        Thread.Sleep(50);
                    }
                    catch
                    {
                        break;
                    }
                }
                Shutdown();
                return;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Glowline_Show");
            _showThread = new Thread(() =>
            {
                try
                {
                    while (_showEvent.WaitOne())
                    {
                        if (IsExiting) break;
                        Dispatcher.BeginInvoke(new Action(ShowPrimaryWindow));
                    }
                }
                catch
                {
                }
            })
            {
                IsBackground = true,
            };
            _showThread.Start();
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled: " + args.Exception);
            args.Handled = true;
        };

        Settings = new SettingsService();
        ThemePaletteService.SetInterfaceTheme(Settings.Settings.InterfaceTheme);
        ThemePaletteService.SetEnabled(Settings.Settings.CoverPaletteEnabled);
        var migrated = false;
        if (Settings.Settings.SchemaVersion < 3)
        {
            if (Settings.Settings.LyricsSource != LyricSource.Lrclib)
                Settings.Settings.LyricsSource = LyricSource.Lrcmux;
            Settings.Settings.SchemaVersion = 3;
            migrated = true;
        }
        if (Settings.Settings.SchemaVersion < 4)
        {
            // La versión anterior activaba el render por software por defecto,
            // que en la mayoría de equipos solo reduce los fotogramas.
            if (Settings.Settings.SoftwareRendering)
            {
                Settings.Settings.SoftwareRendering = false;
                Log.Write("settings: render por software desactivado en la migración");
            }
            Settings.Settings.SchemaVersion = 4;
            migrated = true;
        }
        if (migrated) Settings.Save();

        Loc.SetLanguage(Settings.Settings.Language);
        var qaLanguage = Environment.GetEnvironmentVariable("GLOWLINE_QA_LANG");
        if (!string.IsNullOrWhiteSpace(qaLanguage))
        {
            var option = Loc.Languages.FirstOrDefault(l =>
                string.Equals(l.Code, qaLanguage, StringComparison.OrdinalIgnoreCase));
            if (option is not null) Loc.SetLanguage(option.Language);
        }
        Designs = new DesignService(Settings.Settings.ActiveDesign);
        _restoreDesignOnExit = SnapshotMode;
        _originalDesign = Settings.Settings.ActiveDesign is null
            ? null
            : DesignService.Clone(Settings.Settings.ActiveDesign);
        var designPreset = GetArgValue(e.Args, "--designpreset");
        if (int.TryParse(designPreset, out var presetIndex) && presetIndex is >= 0 and <= 4)
        {
            var preset = Designs.CloneActive();
            PresetLibrary.Apply(preset, presetIndex);
            Designs.Apply(preset);
            Log.Write($"design preset {presetIndex}: {Designs.Active.Layout}");
        }
        Updates = new UpdateService(Settings);

        var forceSoft = e.Args.Any(a => string.Equals(a, "--soft", StringComparison.OrdinalIgnoreCase));
        var forceHard = e.Args.Any(a => string.Equals(a, "--hard", StringComparison.OrdinalIgnoreCase));
        if (forceSoft || (Settings.Settings.SoftwareRendering && !forceHard))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        Client = new LrcLibClient();
        Lyrics = new LyricsService(Settings, Client);
        Media = new MediaSessionService(Settings);
        CoverArt = new CoverArtService(new DesktopMediaArtworkProvider(Media), new DesktopImageDecoder());
        AnimatedCover = new AnimatedArtworkService(CoverArt);
        Media.SnapshotChanged += Lyrics.OnSnapshot;

        SoundSense = new SoundSenseService();
        if (!SnapshotMode && Settings.Settings.SoundSenseEnabled)
            SoundSense.SetEnabled(true);

        var coverProbe = GetArgValue(e.Args, "--coverprobe");
        if (coverProbe is not null || e.Args.Any(a => a == "--coverprobe"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunCoverProbeAsync(coverProbe);
            return;
        }

        var animProbe = GetArgValue(e.Args, "--animprobe");
        if (animProbe is not null || e.Args.Any(a => a == "--animprobe"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunAnimProbeAsync(animProbe);
            return;
        }

        var animFrame = GetArgValue(e.Args, "--animframe");
        if (animFrame is not null || e.Args.Any(a => a == "--animframe"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunAnimFrameProbeAsync(animFrame);
            return;
        }

        if (seekProbe)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunSeekProbeAsync();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--fscycle", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunFullscreenCycleAsync();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindowRef = new MainWindow(Settings, Media, Lyrics, CoverArt, AnimatedCover);

        if (snapshotDir is not null)
        {
            Directory.CreateDirectory(snapshotDir);
            MainWindowRef.ShowActivated = false;
            MainWindowRef.Left = -20000;
            MainWindowRef.Top = -20000;
            MainWindowRef.Show();
            _ = RunSnapshotAsync(snapshotDir);
            return;
        }

        Primary = new FullscreenWindow(Settings, Media, Lyrics, CoverArt, AnimatedCover);
        Primary.OpenPrimary();
        _ = InitMediaAsync();
        Updates.StartAutoChecks();
        StartExtras();
    }

    // 0.9.40 extras: global shortcuts, mini lyrics, OBS and Discord.
    // Each one fails on its own without affecting the rest of the app.
    void StartExtras()
    {
        try
        {
            Hotkeys = new GlobalHotkeyService();
            Hotkeys.Apply(Settings.Settings.GlobalHotkeysEnabled);
        }
        catch (Exception ex)
        {
            Log.Write("hotkeys: " + ex.Message);
        }

        try
        {
            NowPlaying = new NowPlayingHub();
            NowPlaying.Apply(Settings.Settings);
        }
        catch (Exception ex)
        {
            Log.Write("integrations: " + ex.Message);
        }

        try
        {
            MiniLyricsWindow.Sync();
        }
        catch (Exception ex)
        {
            Log.Write("mini lyrics: " + ex.Message);
        }
        MainWindowRef?.RefreshExtrasUi();
    }

    public static void ShowPrimaryWindow()
    {
        if (MainWindowRef is not null)
        {
            MainWindowRef.Topmost = false;
            MainWindowRef.Hide();
        }
        Primary?.ShowPrimaryWindow();
    }

    async Task RunCoverProbeAsync(string? spec)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        try
        {
            TrackInfo? track = null;
            if (spec is not null)
            {
                var parts = spec.Split('|');
                track = new TrackInfo
                {
                    Artist = parts.ElementAtOrDefault(0) ?? "",
                    Title = parts.ElementAtOrDefault(1) ?? "",
                    Album = parts.ElementAtOrDefault(2) ?? "",
                    DurationSeconds = double.TryParse(parts.ElementAtOrDefault(3), out var duration)
                        ? duration : null,
                };
            }
            else
            {
                await Media.InitializeAsync();
                await Task.Delay(500);
                track = Media.LastSnapshot?.Track;
            }

            if (track is null)
            {
                Console.WriteLine("coverprobe: sin pista");
            }
            else
            {
                var art = await CoverArt.GetAsync(track, CancellationToken.None);
                Console.WriteLine($"coverprobe: {track.DisplayName} -> {art.Source} " +
                                  $"{art.PixelWidth}x{art.PixelHeight} " +
                                  $"{(art.Bytes?.Length ?? 0)} bytes");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("coverprobe: " + ex);
        }

        IsExiting = true;
        Shutdown(0);
    }

    async Task RunAnimProbeAsync(string? spec)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        try
        {
            var parts = (spec ?? "Linkin Park|Living Things").Split('|');
            var artist = parts.ElementAtOrDefault(0) ?? "Linkin Park";
            var album = parts.ElementAtOrDefault(1) ?? "Living Things";
            var result = await AnimatedCover.GetAsync(new TrackInfo
            {
                Artist = artist,
                Album = album,
            }, CancellationToken.None);
            Console.WriteLine(result.Available
                ? $"animprobe: {artist} — {album} -> {result.Source} {result.Path}"
                : $"animprobe: {artist} — {album} -> sin animación ({result.ErrorKey ?? "none"})");
        }
        catch (Exception ex)
        {
            Console.WriteLine("animprobe: " + ex);
        }

        IsExiting = true;
        Shutdown(0);
    }

    async Task RunAnimFrameProbeAsync(string? path)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Console.WriteLine("animframe: archivo no encontrado");
                IsExiting = true;
                Shutdown(1);
                return;
            }

            var player = new MediaPlayer { ScrubbingEnabled = true, Volume = 0, IsMuted = true };
            var opened = new TaskCompletionSource<bool>();
            player.MediaOpened += (_, __) => opened.TrySetResult(true);
            player.MediaFailed += (_, __) => opened.TrySetResult(false);
            player.Open(new Uri(path));
            player.Play();
            if (await Task.WhenAny(opened.Task, Task.Delay(5000)) != opened.Task || !opened.Task.Result)
            {
                Console.WriteLine("animframe: no se pudo abrir");
                player.Close();
                IsExiting = true;
                Shutdown(2);
                return;
            }

            player.Play();
            await Task.Delay(200);
            player.Pause();
            player.Position = TimeSpan.FromSeconds(1.5);
            await Task.Delay(350);
            var sample = new DrawingVisual();
            var w = player.NaturalVideoWidth > 0 ? player.NaturalVideoWidth : 960;
            var h = player.NaturalVideoHeight > 0 ? player.NaturalVideoHeight : 960;
            using (var dc = sample.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawVideo(player, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(sample);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            var outPath = Path.GetFullPath("animframe.png");
            using (var stream = File.Create(outPath)) encoder.Save(stream);

            var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
            var stride = w * 4;
            var buffer = new byte[stride * h];
            conv.CopyPixels(buffer, stride, 0);
            var rr = 0L; var gg = 0L; var bb = 0L; var n = 0L;
            const int band = 8;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (x >= band && x < w - band && y >= band && y < h - band) continue;
                    var i = y * stride + x * 4;
                    bb += buffer[i]; gg += buffer[i + 1]; rr += buffer[i + 2]; n++;
                }
            }
            var center = (h / 2) * stride + (w / 2) * 4;
            Console.WriteLine($"animframe: {w}x{h} borde=({rr / n},{gg / n},{bb / n}) " +
                              $"centro=({buffer[center + 2]},{buffer[center + 1]},{buffer[center]}) -> {outPath}");
            player.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("animframe: " + ex);
        }

        IsExiting = true;
        Shutdown(0);
    }

    async Task RunSeekProbeAsync()
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        try
        {
            await Media.InitializeAsync();
            await Task.Delay(500);
            var snap = Media.LastSnapshot;
            var (found, slider) = await AppleMusicSeeker.ProbeAsync();
            var line = $"seekprobe: UI found={found} slider={slider:F1}s " +
                       $"sm='{snap?.Track?.DisplayName ?? "-"}' pos={snap?.PositionSeconds ?? 0:F1}s " +
                       $"src={snap?.SourceAppId ?? "-"}";
            Log.Write(line);
            Console.WriteLine(line);
        }
        catch (Exception ex)
        {
            Log.Write("seekprobe: " + ex);
            Console.WriteLine("seekprobe: " + ex);
        }

        IsExiting = true;
        Shutdown(0);
    }

    async Task RunFullscreenCycleAsync()
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        try
        {
            for (var round = 1; round <= 2; round++)
            {
                var window = new FullscreenWindow(Settings, Media, Lyrics, CoverArt, AnimatedCover)
                {
                    Opacity = 0,
                    ShowActivated = false,
                };
                window.OpenFullscreen();
                await Task.Delay(650);
                var line = $"fscycle {round}: state={window.WindowState} " +
                           $"actual={window.ActualWidth:F0}x{window.ActualHeight:F0} " +
                           $"work={SystemParameters.WorkArea.Width:F0}x{SystemParameters.WorkArea.Height:F0}";
                Log.Write(line);
                Console.WriteLine(line);
                window.CloseFullscreen();
                await Task.Delay(250);
            }
        }
        catch (Exception ex)
        {
            Log.Write("fscycle: " + ex);
            Console.WriteLine("fscycle: " + ex);
        }

        IsExiting = true;
        Shutdown(0);
    }

    async Task RunSnapshotAsync(string dir)
    {
        try
        {
            var fusionProbeArt = Environment.GetEnvironmentVariable("GLOWLINE_QA_ART");
            var qaTrack = Environment.GetEnvironmentVariable("GLOWLINE_QA_TRACK");
            var qaPosition = double.TryParse(Environment.GetEnvironmentVariable("GLOWLINE_QA_POS"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var parsedPosition)
                ? parsedPosition
                : (double?)null;
            var track = new TrackInfo
            {
                Title = "Espresso",
                Artist = "Sabrina Carpenter",
                Album = "Short n' Sweet",
                DurationSeconds = 175,
            };
            if (!string.IsNullOrWhiteSpace(qaTrack))
            {
                var parts = qaTrack.Split('|');
                track = new TrackInfo
                {
                    Title = parts.ElementAtOrDefault(0) ?? track.Title,
                    Artist = parts.ElementAtOrDefault(1) ?? track.Artist,
                    Album = parts.ElementAtOrDefault(2) ?? "",
                    DurationSeconds = double.TryParse(parts.ElementAtOrDefault(3),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                        out var qaDuration)
                        ? qaDuration
                        : track.DurationSeconds,
                };
            }

            SessionSnapshot Fake(double positionSeconds) => new()
            {
                Track = track,
                DurationSeconds = track.DurationSeconds ?? 175,
                PositionSeconds = qaPosition ?? positionSeconds,
                PlaybackStatus = "Paused",
                CapturedAt = DateTimeOffset.Now,
            };

            Lyrics.OnSnapshot(Fake(18));

            for (var i = 0; i < 32 && Lyrics.State is LyricsState.Idle or LyricsState.Searching; i++)
                await Task.Delay(250);
            await Task.Delay(400);

            var fullscreen = new FullscreenWindow(Settings, Media, Lyrics, CoverArt, AnimatedCover);
            fullscreen.OpenForSnapshot();
            await Task.Delay(500);
            fullscreen.DebugApplyArt(MakeTestArt(whiteCover: false));
            await Task.Delay(700);
            Log.Write("fullscreen state: " + fullscreen.DebugState());
            Log.Write("fullscreen backdrop: " + fullscreen.DebugBackdrop());

            SnapshotWindow(MainWindowRef!, Path.Combine(dir, "main.png"), Color.FromRgb(0x0A, 0x0A, 0x0C));
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen.png"), Colors.Black);

            fullscreen.DebugSetPlaying(true, animate: false);
            await Task.Delay(120);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-pause.png"), Colors.Black);
            fullscreen.DebugSetChrome(false);
            await Task.Delay(140);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-chrome-hiding.png"), Colors.Black);
            fullscreen.DebugSetChrome(true);
            await Task.Delay(380);
            fullscreen.DebugSetPlaying(false, animate: false);

            if (!string.IsNullOrWhiteSpace(fusionProbeArt) && File.Exists(fusionProbeArt))
            {
                fullscreen.DebugApplyArt(File.ReadAllBytes(fusionProbeArt));
                await Task.Delay(900);
                Log.Write("fusion probe from lateral: " + fullscreen.DebugState() + " | " + fullscreen.DebugHorizontalLayout());
                SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-fusion-from-lateral.png"), Colors.Black);
                fullscreen.DebugApplyArt(MakeTestArt(whiteCover: false));
                await Task.Delay(900);
            }

            MainWindowRef!.DebugApplySnapshot(new SessionSnapshot
            {
                Track = new TrackInfo
                {
                    Title = "Un título de canción extraordinariamente largo que no cabe en una sola línea",
                    Artist = "Un artista con un nombre larguísimo y una colaboración más larga todavía",
                    Album = "Un álbum con un nombre igual de interminable que el resto",
                    DurationSeconds = 3725,
                },
                DurationSeconds = 3725,
                PositionSeconds = 1234,
                PlaybackStatus = "Playing",
                IsPlaying = true,
                CapturedAt = DateTimeOffset.Now,
            });
            MainWindowRef.Height = 1500;
            MainWindowRef.UpdateLayout();
            await Task.Delay(500);
            SnapshotWindow(MainWindowRef, Path.Combine(dir, "main-full.png"), Color.FromRgb(0x0A, 0x0A, 0x0C));

            MainWindowRef.Height = 840;
            MainWindowRef.UpdateLayout();
            await Task.Delay(300);
            SnapshotWindow(MainWindowRef, Path.Combine(dir, "main-840.png"), Color.FromRgb(0x0A, 0x0A, 0x0C));

            MainWindowRef.DebugScrollToEnd();
            await Task.Delay(500);
            SnapshotWindow(MainWindowRef, Path.Combine(dir, "main-840-bottom.png"), Color.FromRgb(0x0A, 0x0A, 0x0C));

            MainWindowRef.DebugShowUpdateNotes("0.9.22", """
                - Release notes now appear before installing an update.
                - New "Star on GitHub" button opens the public repository.
                - Update checks keep the notes visible while the installer downloads,
                  even when a long line needs to wrap inside the panel.
                - Download failures no longer hide the changes for the target version.
                """);
            await Task.Delay(600);
            SnapshotWindow(MainWindowRef, Path.Combine(dir, "main-updates.png"), Color.FromRgb(0x0A, 0x0A, 0x0C));
            MainWindowRef.DebugHideUpdateNotes();

            MainWindowRef.DebugScrollToDesigner();
            await Task.Delay(350);
            SnapshotWindow(MainWindowRef, Path.Combine(dir, "main-designer.png"), Color.FromRgb(0x0A, 0x0A, 0x0C));

            Lyrics.OnSnapshot(Fake(59));
            await Task.Delay(700);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-background.png"), Colors.Black);

            Lyrics.OnSnapshot(Fake(3));
            await Task.Delay(450);
            Log.Write("intro state: " + fullscreen.DebugState());
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-intro.png"), Colors.Black);

            Lyrics.OnSnapshot(Fake(6.5));
            await Task.Delay(450);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-intro-late.png"), Colors.Black);

            Lyrics.OnSnapshot(Fake(7.4));
            await Task.Delay(150);
            Log.Write("lyrics start state: " + fullscreen.DebugState());
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-transition.png"), Colors.Black);

            Lyrics.OnSnapshot(Fake(18));
            await Task.Delay(350);

            fullscreen.DebugApplyArt(MakeTestArt(whiteCover: true));
            await Task.Delay(120);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-enter.png"), Colors.White);
            await Task.Delay(780);
            Log.Write("fullscreen light state: " + fullscreen.DebugState());
            Log.Write("fullscreen light horizontal: " + fullscreen.DebugHorizontalLayout());
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light.png"), Colors.White);

            var originalWordAnimation = Settings.Settings.WordAnimation;
            foreach (var style in new[] { WordAnimationStyle.Slide, WordAnimationStyle.Zoom, WordAnimationStyle.Bounce })
            {
                Settings.Settings.WordAnimation = style;
                await Task.Delay(450);
                SnapshotWindow(fullscreen, Path.Combine(dir, $"fullscreen-light-{style.ToString().ToLowerInvariant()}.png"), Colors.White);
            }
            Settings.Settings.WordAnimation = originalWordAnimation;
            await Task.Delay(450);

            fullscreen.DebugSetSettingsPanel(true);
            await Task.Delay(300);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-settings.png"), Colors.White);
            fullscreen.DebugSetSettingsPanel(false);
            await Task.Delay(200);

            fullscreen.DebugApplyArt(MakeTestArt(
                whiteCover: true,
                backgroundOverride: Color.FromRgb(0xE3, 0xEC, 0xF7),
                accentOverride: Color.FromRgb(0x4C, 0x6F, 0xA8)));
            await Task.Delay(120);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-recolor-start.png"), Colors.White);
            await Task.Delay(780);
            Log.Write("fullscreen light recolor horizontal: " + fullscreen.DebugHorizontalLayout());
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-recolor.png"), Colors.White);

            fullscreen.DebugApplyArt(MakeTestArt(whiteCover: true));
            await Task.Delay(900);

            Lyrics.OnSnapshot(Fake(59));
            await Task.Delay(600);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-background.png"), Colors.White);

            Lyrics.OnSnapshot(Fake(3));
            await Task.Delay(450);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-intro.png"), Colors.White);

            Lyrics.OnSnapshot(Fake(6.5));
            await Task.Delay(450);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-intro-late.png"), Colors.White);

            Lyrics.OnSnapshot(Fake(6.9));
            await Task.Delay(300);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-intro-swell.png"), Colors.White);

            if (!string.IsNullOrWhiteSpace(fusionProbeArt) && File.Exists(fusionProbeArt))
            {
                fullscreen.DebugApplyArt(File.ReadAllBytes(fusionProbeArt));
                await Task.Delay(120);
                SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-fusion-dark-enter.png"), Colors.Black);
                await Task.Delay(780);
                Log.Write("fusion probe dark: " + fullscreen.DebugState() + " | " + fullscreen.DebugHorizontalLayout());
                SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-fusion-dark.png"), Colors.Black);
                fullscreen.DebugSetSettingsPanel(true);
                await Task.Delay(300);
                SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-fusion-dark-settings.png"), Colors.Black);
                fullscreen.DebugSetSettingsPanel(false);
                await Task.Delay(200);

                fullscreen.DebugResize(1912, 1079);
                await Task.Delay(400);
                Log.Write("fusion probe 1912: " + fullscreen.DebugState() + " | " + fullscreen.DebugHorizontalLayout());
                SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-fusion-dark-1912.png"), Colors.Black);
                fullscreen.DebugResize(1600, 900);
                await Task.Delay(400);
            }

            SessionSnapshot FakeGap(double positionSeconds) => new()
            {
                Track = new TrackInfo
                {
                    Title = "Bohemian Rhapsody",
                    Artist = "Queen",
                    Album = "A Night at the Opera",
                    DurationSeconds = 355,
                },
                DurationSeconds = 355,
                PositionSeconds = positionSeconds,
                PlaybackStatus = "Paused",
                CapturedAt = DateTimeOffset.Now,
            };

            Lyrics.OnSnapshot(FakeGap(105));
            for (var i = 0; i < 40 && Lyrics.State is LyricsState.Idle or LyricsState.Searching; i++)
                await Task.Delay(250);
            await Task.Delay(400);
            Log.Write("gap105: " + fullscreen.DebugGap());

            Lyrics.OnSnapshot(FakeGap(170));
            await Task.Delay(500);

            fullscreen.DebugApplyArt(MakeTestArt(whiteCover: false));
            await Task.Delay(900);
            Log.Write("gap state: " + fullscreen.DebugState() + " | " + fullscreen.DebugGap());
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-gap.png"), Colors.Black);

            var originalScale = Settings.Settings.FontScale;
            Settings.Settings.FontScale = 1.6;
            await Task.Delay(400);
            Settings.Settings.FontScale = originalScale;
            await Task.Delay(250);

            fullscreen.DebugApplyArt(MakeTestArt(whiteCover: true));
            await Task.Delay(900);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-gap.png"), Colors.White);

            fullscreen.DebugResize(1366, 768);
            await Task.Delay(500);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-1366x768.png"), Colors.White);

            fullscreen.DebugResize(1024, 720);
            await Task.Delay(500);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-light-1024x720.png"), Colors.White);

            fullscreen.DebugApplyArt(MakeTestArt(whiteCover: false));
            await Task.Delay(900);
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-dark-1024x720.png"), Colors.Black);

            SessionSnapshot FakeHooka(double positionSeconds) => new()
            {
                Track = new TrackInfo
                {
                    Title = "Hooka (feat. Plan B)",
                    Artist = "Don Omar & Plan B",
                    Album = "Meet the Orphans",
                    DurationSeconds = 237,
                },
                DurationSeconds = 238,
                PositionSeconds = positionSeconds,
                PlaybackStatus = "Paused",
                CapturedAt = DateTimeOffset.Now,
            };

            fullscreen.DebugResize(1600, 900);
            Lyrics.OnSnapshot(FakeHooka(90));
            for (var i = 0; i < 40 && !string.Equals(Lyrics.Found?.TrackName, "Hooka", StringComparison.OrdinalIgnoreCase); i++)
                await Task.Delay(250);
            await Task.Delay(500);
            fullscreen.DebugApplyArt(MakeTestArt(whiteCover: false));
            await Task.Delay(800);
            Log.Write("hooka layout: " + fullscreen.DebugState() + " | " + fullscreen.DebugLayout());
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-hooka.png"), Colors.Black);

            Lyrics.OnSnapshot(Fake(18));
            await Task.Delay(250);

            var coverBefore = fullscreen.DebugCoverSides();
            var animatedSetting = Settings.Settings.AnimatedCoversEnabled;
            Settings.Settings.AnimatedCoversEnabled = true;
            fullscreen.DebugSetAnimatedReady(true);
            await Task.Delay(350);
            Log.Write("cover size static: " + coverBefore);
            Log.Write("cover size animated: " + fullscreen.DebugCoverSides());
            SnapshotWindow(fullscreen, Path.Combine(dir, "fullscreen-animated-size.png"), Colors.Black);
            fullscreen.DebugSetAnimatedReady(false);
            Settings.Settings.AnimatedCoversEnabled = animatedSetting;
            await Task.Delay(350);
            Log.Write("cover size reverted: " + fullscreen.DebugCoverSides());

            fullscreen.CloseForSnapshot();
        }
        catch (Exception ex)
        {
            Log.Write("snapshot: " + ex);
        }

        IsExiting = true;
        Shutdown(0);
    }

    static byte[] MakeTestArt(bool whiteCover, Color? backgroundOverride = null, Color? accentOverride = null)
    {
        const int size = 480;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (whiteCover)
            {
                var background = backgroundOverride ?? Colors.White;
                var accent = accentOverride ?? Color.FromRgb(0xE8, 0x5A, 0x3C);
                dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size));
                dc.DrawEllipse(new SolidColorBrush(accent), null, new Point(240, 255), 95, 95);
                dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(145, 118, 190, 10));
            }
            else
            {
                var gradient = new LinearGradientBrush(
                    Color.FromRgb(0x23, 0x1B, 0x54),
                    Color.FromRgb(0xC9, 0x5F, 0xA4),
                    45);
                dc.DrawRectangle(gradient, null, new Rect(0, 0, size, size));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), null, new Point(240, 240), 120, 120);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xD1, 0x66)), null, new Point(240, 240), 60, 60);
            }
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static string? GetArgValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static void SnapshotWindow(Window window, string path, Color backdrop)
    {
        if (window.Content is not FrameworkElement element) return;
        var width = (int)Math.Ceiling(element.ActualWidth + element.Margin.Left + element.Margin.Right);
        var height = (int)Math.Ceiling(element.ActualHeight + element.Margin.Top + element.Margin.Bottom);
        if (width <= 0 || height <= 0) return;

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(0, 0, width, height));
        }
        rtb.Render(visual);
        rtb.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    async Task InitMediaAsync()
    {
        await Media.InitializeAsync();
        MainWindowRef?.RefreshPlayersFromSessions();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Updates?.ApplyPendingOnExit(restart: false); } catch { }
        try { Hotkeys?.Dispose(); } catch { }
        try { NowPlaying?.Dispose(); } catch { }
        SoundSense?.Dispose();
        Media?.Dispose();
        if (Designs is not null)
            Settings.Settings.ActiveDesign = _restoreDesignOnExit ? _originalDesign : Designs.CloneActive();
        Settings?.Save();
        _showEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
