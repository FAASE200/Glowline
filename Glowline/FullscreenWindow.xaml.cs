using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glowline.Localization;
using Glowline.Models;
using Glowline.Rendering;
using Glowline.Services;

namespace Glowline;

public partial class FullscreenWindow : Window
{
    readonly SettingsService _settings;
    readonly MediaSessionService _media;
    readonly LyricsService _lyrics;
    readonly CoverArtService _cover;
    readonly AnimatedArtworkService _animated;

    readonly List<TextBlock> _blocks = new();
    readonly List<double> _heights = new();
    readonly List<double> _tops = new();
    IReadOnlyList<LrcLine> _lines = Array.Empty<LrcLine>();

    WordHighlightLine? _wordLine;
    int _currentLine = int.MinValue;
    int _hoverLine = -1;
    double _scrollOffset;
    bool _scrollInit;
    bool _isOpen;
    bool _allowClose;
    bool _windowed;
    bool _settingsPanelOpen;
    bool _loadingSettingsPanel;
    bool _chromeHidden;
    int _lastMouseX = int.MinValue;
    int _lastMouseY = int.MinValue;
    bool _centeredTheme;
    bool _fusionLight = true;
    int _interludeGapLine = -1;
    double _interludeGap;
    string _artKey = "";
    bool _artLoaded;
    bool _artBusy;
    DateTime _artNextAt = DateTime.MaxValue;
    DateTime _artWaitSince = DateTime.MaxValue;
    bool _artClearedForTrack = true;
    byte[]? _artBytes;
    string _artAlbum = "";
    string _artSource = "";
    bool _artHighRes;
    bool _usingA = true;
    CancellationTokenSource? _animCts;
    string _animTrackKey = "";
    Color? _detectedFusionColor;
    bool _fixedBackground;
    bool _customPalette;
    Color _customBackground = Colors.Black;
    Color _customBackgroundSecondary = Color.FromRgb(0x17, 0x17, 0x1B);
    Color _customText = Colors.White;
    Color _customSecondary = Color.FromRgb(0xA8, 0xA8, 0xB0);
    Color _customAccent = Color.FromRgb(0xF5, 0xF5, 0xF7);
    Color _customLyricActive = Colors.White;
    Color _customLyricInactive = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
    readonly TranslateTransform _coverTranslate = new();
    readonly ScaleTransform _coverScale = new();
    readonly RotateTransform _coverRotate = new();
    readonly TransformGroup _coverTransform = new();
    double _designCoverScale = 1;
    readonly TranslateTransform _verseShift = new();
    readonly ScaleTransform _verseScale = new();
    readonly TransformGroup _verseTransform = new();
    double _coverMotionTime;
    bool _coverMotionIdle = true;
    bool _animatedReady;
    bool _animatedPlaying;
    DateTime _animLogAt = DateTime.MinValue;
    Storyboard? _coverSizeStoryboard;
    bool _coverSizeAnimating;

    DateTime _lastFrame = DateTime.MinValue;
    bool _frameHooked;
    DateTime _lastClockUi = DateTime.MinValue;

    FontFamily _fontFamily = new("Segoe UI Variable Display, Segoe UI");
    FontWeight _fontWeight = FontWeights.SemiBold;
    double _baseFontSize = 29;
    double _spacing = 24;
    double _scrollRate = 12;
    TimeSpan _wordFade = TimeSpan.FromMilliseconds(110);
    string? _appliedFont;
    string? _appliedWeight;
    double _appliedScale = -1;
    int _appliedSoftness = -1;
    WordAnimationStyle _appliedWordAnimation = (WordAnimationStyle)(-1);
    double _appliedMotion = -1;
    string _appliedDesignFont = "";
    string _appliedDesignWeight = "";
    double _appliedDesignLyricSize = -1;
    double _appliedDesignLineHeight = -1;
    double _appliedDesignSpacing = -1;
    DesignAlignment _appliedDesignAlignment = (DesignAlignment)(-1);
    LyricAnimation _appliedDesignWordAnimation = (LyricAnimation)(-1);
    int _appliedDesignWordMs = -1;
    double _appliedDesignWordIntensity = -1;
    bool _appliedDesignReduceMotion;

    readonly DispatcherTimer _idleTimer;
    DropShadowEffect? _darkGlow;
    DropShadowEffect? _fusionGlow;
    readonly SolidColorBrush _fusionBrush = new(Colors.White);
    double _sensePulse;
    Color _blockText = Colors.White;
    Color _wordDim = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
    Color _wordBright = Colors.White;
    Color _backgroundVocal = BackgroundVocal;
    Color _statusText = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
    Color _plainText = Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF);
    Color _fusionBackground = Colors.White;
    Color _fusionPrimary = Color.FromRgb(0x11, 0x11, 0x14);
    Color _fusionSecondary = Color.FromRgb(0x55, 0x55, 0x5C);
    Color _fusionMuted = Color.FromRgb(0x8A, 0x8A, 0x90);
    Color _fusionVocal = Color.FromArgb(0xB3, 0x55, 0x55, 0x5C);

    const double BaseLyricFontSize = 29;
    const double BaseSpacing = 24;
    const double InterludeGap = 34.0;
    static readonly TimeSpan ArtFadeDuration = TimeSpan.FromMilliseconds(460);
    static readonly Color BackgroundVocal = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
    static readonly bool LayoutLog = Environment.GetEnvironmentVariable("GLOWLINE_LAYOUT_LOG") == "1";
    static readonly bool AnimLog = Environment.GetEnvironmentVariable("GLOWLINE_ANIM_LOG") == "1";

    public FullscreenWindow(
        SettingsService settings,
        MediaSessionService media,
        LyricsService lyrics,
        CoverArtService cover,
        AnimatedArtworkService animated)
    {
        _settings = settings;
        _media = media;
        _lyrics = lyrics;
        _cover = cover;
        _animated = animated;
        InitializeComponent();
        ThemePaletteService.Changed += OnThemePaletteChanged;
        ApplyQuickPalette(ThemePaletteService.Current);
        LightLayout.Background = _fusionBrush;
        LightBackdropScrim.Background = _fusionBrush;
        LightCoverBorder.Background = _fusionBrush;
        LightCoverBack.Background = _fusionBrush;
        LightCoverMask.BorderBrush = _fusionBrush;
        _darkGlow = new DropShadowEffect
        {
            Color = Colors.White,
            ShadowDepth = 0,
            BlurRadius = 26,
            Opacity = 0,
            RenderingBias = RenderingBias.Performance,
        };
        _coverTransform.Children.Add(_coverScale);
        _coverTransform.Children.Add(_coverRotate);
        _coverTransform.Children.Add(_coverTranslate);
        _verseTransform.Children.Add(_verseScale);
        _verseTransform.Children.Add(_verseShift);
        LyricsCanvas.RenderTransform = _verseTransform;
        _fusionGlow = new DropShadowEffect
        {
            Color = Color.FromRgb(0xFF, 0x5C, 0x8A),
            ShadowDepth = 0,
            BlurRadius = 18,
            Opacity = 0,
            RenderingBias = RenderingBias.Performance,
        };
        AnimatedCoverLight.OpacityMask = CreateFeatherMask(0.08);
        ApplyDesignProfile();
        DarkCoverContent.SizeChanged += (_, __) => UpdateCoverClips();
        LightCoverContent.SizeChanged += (_, __) => UpdateCoverClips();

        SourceInitialized += (_, __) => ApplyDarkTitleBar();

        SizeChanged += (_, __) => OnHostResized();
        LyricsHost.SizeChanged += (_, __) => OnHostResized();
        LyricsCanvas.SizeChanged += (_, __) => OnHostResized();
        _lyrics.Changed += OnLyricsChanged;
        _media.SnapshotChanged += OnSnapshot;
        App.Designs.ActiveChanged += OnDesignChanged;
        // While any Rendering handler is attached WPF redraws at the monitor's
        // refresh rate, so the frame loop is only attached while it is visible.
        IsVisibleChanged += (_, __) => SyncFrameLoop();
        StateChanged += (_, __) => SyncFrameLoop();
        PreviewKeyDown += OnKeyDown;
        MouseMove += (_, __) =>
        {
            if (!NativeMethods.GetCursorPos(out var point)) return;
            if (point.X == _lastMouseX && point.Y == _lastMouseY) return;
            _lastMouseX = point.X;
            _lastMouseY = point.Y;
            WakeChrome();
        };

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _idleTimer.Tick += (_, __) => HideChrome();

        Closing += (_, e) =>
        {
            if (App.IsExiting || _allowClose) return;
            e.Cancel = true;
            App.MainWindowRef?.ExitApp();
        };

        Closed += (_, __) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            _frameHooked = false;
            _lyrics.Changed -= OnLyricsChanged;
            _media.SnapshotChanged -= OnSnapshot;
            App.Designs.ActiveChanged -= OnDesignChanged;
            ThemePaletteService.Changed -= OnThemePaletteChanged;
            _idleTimer.Stop();
            StopAnimated();
        };

        PrevButton.Click += async (_, __) => await _media.SkipPreviousAsync();
        PlayPauseButton.Click += async (_, __) => await _media.TogglePlayPauseAsync();
        NextButton.Click += async (_, __) => await _media.SkipNextAsync();
        LightPrevButton.Click += async (_, __) => await _media.SkipPreviousAsync();
        LightPlayPauseButton.Click += async (_, __) => await _media.TogglePlayPauseAsync();
        LightNextButton.Click += async (_, __) => await _media.SkipNextAsync();
        CloseButton.Click += (_, __) => App.MainWindowRef?.ExitApp();
        SettingsButton.Click += (_, __) => ToggleSettingsPanel();
        SettingsCloseButton.Click += (_, __) => CloseSettingsPanel();
        SettingsAdvancedButton.Click += (_, __) =>
        {
            CloseSettingsPanel();
            SetFullscreenMode(false);
            App.MainWindowRef?.ShowSettings();
        };
        SettingsExitButton.Click += (_, __) => App.MainWindowRef?.ExitApp();
        SettingsAnimatedCheck.Checked += (_, __) => ApplyQuickAnimatedSetting();
        SettingsAnimatedCheck.Unchecked += (_, __) => ApplyQuickAnimatedSetting();
        SettingsSoundSenseCheck.Checked += (_, __) => ApplyQuickSoundSenseSetting();
        SettingsSoundSenseCheck.Unchecked += (_, __) => ApplyQuickSoundSenseSetting();
        SettingsSourceLrcmux.Checked += (_, __) => ApplyQuickLyricsSource(LyricSource.Lrcmux);
        SettingsSourceLyricsPlus.Checked += (_, __) => ApplyQuickLyricsSource(LyricSource.LyricsPlus);
        SettingsSourceLrclib.Checked += (_, __) => ApplyQuickLyricsSource(LyricSource.Lrclib);
        SettingsThemeLightRadio.Checked += (_, __) => QuickInterfaceTheme_Changed(_, __);
        SettingsThemeDarkRadio.Checked += (_, __) => QuickInterfaceTheme_Changed(_, __);
        SettingsInterfaceGlassCheck.Checked += (_, __) => ApplyQuickInterfaceGlass();
        SettingsInterfaceGlassCheck.Unchecked += (_, __) => ApplyQuickInterfaceGlass();
        SettingsWordFade.Checked += (_, __) => ApplyQuickWordAnimation(WordAnimationStyle.Fade);
        SettingsWordSlide.Checked += (_, __) => ApplyQuickWordAnimation(WordAnimationStyle.Slide);
        SettingsWordZoom.Checked += (_, __) => ApplyQuickWordAnimation(WordAnimationStyle.Zoom);
        SettingsWordBounce.Checked += (_, __) => ApplyQuickWordAnimation(WordAnimationStyle.Bounce);
        SettingsFontSlider.ValueChanged += (_, e) => ApplyQuickFontScale(e.NewValue);
        SettingsFullscreenRadio.Checked += (_, __) =>
        {
            if (!_loadingSettingsPanel && _windowed) SetFullscreenMode(true);
        };
        SettingsWindowedRadio.Checked += (_, __) =>
        {
            if (!_loadingSettingsPanel && !_windowed) SetFullscreenMode(false);
        };
    }

    public void OpenFullscreen(Window? source = null) => OpenPrimary();

    public void OpenPrimary()
    {
        if (_isOpen) return;
        _isOpen = true;
        SyncFrameLoop();
        Opacity = 0;
        WindowState = WindowState.Normal;
        Left = 0;
        Top = 0;
        Width = 1280;
        Height = 800;
        Show();
        SetFullscreenMode(true);
        Opacity = 1;
        RefreshPrimaryContent();
        AnimateEnter();
        ApplyAnimatedCoverSetting();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isOpen) return;
            RecenterLyricsHost();
            OnHostResized();
        }), DispatcherPriority.ContextIdle);
    }

    void RefreshPrimaryContent()
    {
        UpdateTrackInfo();
        UpdateTransport();
        OnLyricsStateForDisplay();
        UpdateLightArtSize();
        UpdateWindowModeUi();
        LoadSettingsPanel();
        WakeChrome();
    }

    public void ShowPrimaryWindow()
    {
        if (!_isOpen)
        {
            OpenPrimary();
            return;
        }
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _windowed ? WindowState.Normal : WindowState.Maximized;
        Activate();
        Focus();
        WakeChrome();
    }

    public void ToggleWindowedMode() => SetFullscreenMode(_windowed);

    void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var palette = ThemePaletteService.Current;
            var dark = palette.Theme == InterfaceTheme.Dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            var caption = palette.Canvas.R | (palette.Canvas.G << 8) | (palette.Canvas.B << 16);
            var text = palette.Ink.R | (palette.Ink.G << 8) | (palette.Ink.B << 16);
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch
        {
        }
    }

    public void SetFullscreenMode(bool fullscreen)
    {
        _windowed = !fullscreen;
        if (!_isOpen) return;

        WindowState = WindowState.Normal;
        if (fullscreen)
        {
            if (App.MainWindowRef is { IsVisible: true } settings)
            {
                settings.Topmost = false;
                settings.Hide();
            }
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;

            var hwnd = new WindowInteropHelper(this).Handle;
            var monitor = hwnd != IntPtr.Zero
                ? NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)
                : IntPtr.Zero;
            if (monitor != IntPtr.Zero)
            {
                var info = new NativeMethods.MONITORINFO
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
                };
                if (NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    NativeMethods.SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }
            }
            WindowState = WindowState.Maximized;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_windowed && _isOpen)
                {
                    WindowState = WindowState.Maximized;
                    RecenterLyricsHost();
                    OnHostResized();
                }
            }), DispatcherPriority.Loaded);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Topmost = false;
            ShowInTaskbar = true;
            ApplyDarkTitleBar();

            void ApplyWindowedBounds()
            {
                WindowState = WindowState.Normal;
                ApplyDarkTitleBar();
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
                var work = screen?.WorkingArea ??
                    new System.Drawing.Rectangle(0, 0, 1920, 1080);
                var dpi = VisualTreeHelper.GetDpi(this);
                var width = (int)Math.Max(MinWidth,
                    Math.Min((int)Math.Round(1280 * dpi.DpiScaleX), work.Width - 80));
                var height = (int)Math.Max(MinHeight,
                    Math.Min((int)Math.Round(800 * dpi.DpiScaleY), work.Height - 80));
                var left = work.Left + (work.Width - width) / 2;
                var top = work.Top + (work.Height - height) / 2;
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            ApplyWindowedBounds();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_windowed && _isOpen) ApplyWindowedBounds();
            }), DispatcherPriority.Loaded);
        }

        UpdateWindowModeUi();
        Activate();
        Focus();
        WakeChrome();
    }

    void UpdateWindowModeUi()
    {
        if (HintText is null) return;
        HintText.Text = Loc.T(_windowed ? "HintWindowed" : "HintFullscreen");
        if (SettingsFullscreenRadio is not null)
        {
            _loadingSettingsPanel = true;
            SettingsFullscreenRadio.IsChecked = !_windowed;
            SettingsWindowedRadio.IsChecked = _windowed;
            _loadingSettingsPanel = false;
        }
    }

    void ApplyTextStyle(bool force)
    {
        var s = _settings.Settings;
        var design = App.Designs.Active;
        var scale = Math.Clamp(s.FontScale, 0.6, 1.8);
        var typography = design.Typography;
        var lyrics = design.Lyrics;
        var animations = design.Animations;

        if (!force &&
            string.Equals(typography.FontFamily, _appliedDesignFont, StringComparison.Ordinal) &&
            string.Equals(typography.FontWeight, _appliedDesignWeight, StringComparison.Ordinal) &&
            Math.Abs(typography.LyricSize - _appliedDesignLyricSize) < 0.001 &&
            Math.Abs(typography.LineHeight - _appliedDesignLineHeight) < 0.001 &&
            Math.Abs(lyrics.Spacing - _appliedDesignSpacing) < 0.001 &&
            lyrics.Alignment == _appliedDesignAlignment &&
            animations.WordAnimation == _appliedDesignWordAnimation &&
            animations.WordDurationMs == _appliedDesignWordMs &&
            Math.Abs(animations.WordIntensity - _appliedDesignWordIntensity) < 0.001 &&
            design.ReduceMotion == _appliedDesignReduceMotion &&
            string.Equals(s.LyricsFontFamily, _appliedFont, StringComparison.Ordinal) &&
            string.Equals(s.LyricsFontWeight, _appliedWeight, StringComparison.Ordinal) &&
            Math.Abs(scale - _appliedScale) < 0.001 &&
            s.AnimationSoftness == _appliedSoftness &&
            s.WordAnimation == _appliedWordAnimation &&
            Math.Abs(s.WordMotionStrength - _appliedMotion) < 0.001)
        {
            return;
        }

        _appliedFont = s.LyricsFontFamily;
        _appliedWeight = s.LyricsFontWeight;
        _appliedScale = scale;
        _appliedSoftness = s.AnimationSoftness;
        _appliedWordAnimation = s.WordAnimation;
        _appliedMotion = s.WordMotionStrength;
        _appliedDesignFont = typography.FontFamily;
        _appliedDesignWeight = typography.FontWeight;
        _appliedDesignLyricSize = typography.LyricSize;
        _appliedDesignLineHeight = typography.LineHeight;
        _appliedDesignSpacing = lyrics.Spacing;
        _appliedDesignAlignment = lyrics.Alignment;
        _appliedDesignWordAnimation = animations.WordAnimation;
        _appliedDesignWordMs = animations.WordDurationMs;
        _appliedDesignWordIntensity = animations.WordIntensity;
        _appliedDesignReduceMotion = design.ReduceMotion;

        _fontFamily = new FontFamily(string.IsNullOrWhiteSpace(design.Typography.FontFamily)
            ? "Segoe UI Variable Display"
            : design.Typography.FontFamily);
        _fontWeight = MapFontWeight(design.Typography.FontWeight);
        var boost = _centeredTheme ? 1.18 : 1.05;
        _baseFontSize = Math.Clamp(design.Typography.LyricSize, 16, 64) * scale * boost;
        _spacing = Math.Clamp(design.Lyrics.Spacing, 0, 80) * Math.Max(0.85, scale);
        _scrollRate = TextStyle.ScrollRate(s);
        _wordFade = TimeSpan.FromMilliseconds(Math.Clamp(design.Animations.WordDurationMs, 40, 500));

        var titleSize = Math.Clamp(design.Typography.TitleSize, 8, 120);
        var artistSize = Math.Clamp(design.Typography.ArtistSize, 8, 120);
        var albumSize = Math.Max(9, artistSize - 3);
        foreach (var title in new[] { TitleText, LightTitle })
        {
            title.FontFamily = _fontFamily;
            title.FontWeight = _fontWeight;
            title.FontSize = titleSize;
            title.MaxHeight = Math.Max(60, titleSize * 2.8);
        }
        foreach (var artist in new[] { ArtistText, LightArtist })
        {
            artist.FontFamily = _fontFamily;
            artist.FontSize = artistSize;
        }
        foreach (var album in new[] { AlbumText, LightAlbum })
        {
            album.FontFamily = _fontFamily;
            album.FontSize = albumSize;
        }

        var alignment = CurrentLyricAlignment();
        foreach (var block in _blocks)
        {
            block.FontFamily = _fontFamily;
            block.FontWeight = _fontWeight;
            block.FontSize = _baseFontSize;
            block.LineHeight = _baseFontSize * Math.Clamp(design.Typography.LineHeight, 0.8, 2.0);
            block.TextAlignment = alignment;
        }

        if (_wordLine is not null)
        {
            _wordLine.FadeTime = _wordFade;
            _wordLine.Style = MapWordAnimation(design.Animations.WordAnimation);
            _wordLine.MotionStrength = Math.Clamp(design.Animations.WordIntensity, 0, 1);
        }
        if (_blocks.Count > 0)
        {
            for (var i = 0; i < _blocks.Count && i < _lines.Count; i++)
                LyricRender.SetPlain(_blocks[i], EffectiveLine(_lines[i]), _backgroundVocal);

            var current = _currentLine;
            _currentLine = int.MinValue;
            ApplyCurrentLine(current);

            LayoutBlocks();
            _scrollOffset = CenterTarget();
            ScrollTransform.Y = _scrollOffset;
            _scrollInit = LyricsCanvas.ActualHeight > 1;
        }
    }

    void AnimateEnter()
    {
        RootGrid.Opacity = 0;
        RootGrid.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space &&
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) return;
        if (_settingsPanelOpen && e.Key is Key.Space or Key.Left or Key.Right) return;
        switch (e.Key)
        {
            case Key.F11:
                ToggleWindowedMode();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_settingsPanelOpen) CloseSettingsPanel();
                else if (!_windowed) SetFullscreenMode(false);
                e.Handled = true;
                break;
            case Key.Space:
                _ = _media.TogglePlayPauseAsync();
                e.Handled = true;
                break;
            case Key.Left:
                _lyrics.AdjustGlobalOffset(-250);
                e.Handled = true;
                break;
            case Key.Right:
                _lyrics.AdjustGlobalOffset(250);
                e.Handled = true;
                break;
        }
    }

    void WakeChrome()
    {
        if (!_isOpen) return;
        if (_chromeHidden)
        {
            _chromeHidden = false;
            ChromeLayer.IsEnabled = true;
            ChromeLayer.IsHitTestVisible = true;
            ChromeLayer.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(ChromeLayer.Opacity, 1, TimeSpan.FromMilliseconds(220)));
            SetTransportsVisible(true);
            RecenterLyricsHost();
            Cursor = Cursors.Arrow;
        }
        _idleTimer.Stop();
        if (!_settingsPanelOpen) _idleTimer.Start();
    }

    void HideChrome()
    {
        if (!_isOpen || _settingsPanelOpen) return;
        _chromeHidden = true;
        ChromeLayer.IsEnabled = false;
        var anim = new DoubleAnimation(ChromeLayer.Opacity, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, __) =>
        {
            if (_chromeHidden) ChromeLayer.IsHitTestVisible = false;
        };
        ChromeLayer.BeginAnimation(OpacityProperty, anim);
        SetTransportsVisible(false);
        RecenterLyricsHost();
        Cursor = Cursors.None;
    }

    void SetTransportsVisible(bool visible)
    {
        var controls = App.Designs.Active.Controls;
        var present = controls.ShowTransport || controls.ShowProgress;
        AnimateTransport(DarkTransport, DarkTransportShift, present, visible, controls.ShowTransport);
        AnimateTransport(LightTransport, LightTransportShift, present, visible, controls.ShowTransport);
    }

    static void AnimateTransport(
        FrameworkElement transport, TranslateTransform shift, bool present, bool visible, bool enabled)
    {
        var active = present && visible;
        var interactive = active && enabled;
        var targetOpacity = active ? 1.0 : 0.0;
        var targetY = active ? 0.0 : 7.0;

        if (!present)
        {
            transport.BeginAnimation(OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            transport.Opacity = 0;
            shift.Y = 7;
            transport.Visibility = Visibility.Collapsed;
            transport.IsHitTestVisible = false;
            transport.IsEnabled = false;
            return;
        }

        transport.Visibility = Visibility.Visible;
        transport.IsHitTestVisible = interactive;
        transport.IsEnabled = interactive;
        if (Math.Abs(transport.Opacity - targetOpacity) < 0.001 &&
            Math.Abs(shift.Y - targetY) < 0.001)
            return;

        var duration = active
            ? TimeSpan.FromMilliseconds(240)
            : TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase
        {
            EasingMode = active ? EasingMode.EaseOut : EasingMode.EaseIn,
        };
        transport.BeginAnimation(OpacityProperty,
            new DoubleAnimation(transport.Opacity, targetOpacity, duration)
            {
                EasingFunction = ease,
            });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(shift.Y, targetY, duration)
            {
                EasingFunction = ease,
            });
    }

    void OnSnapshot(SessionSnapshot snap)
    {
        if (!_isOpen) return;
        UpdateTrackInfo();
        UpdateTransport();
        EnsureAnimatedPlaying();
    }

    void OnLyricsChanged()
    {
        if (!_isOpen) return;
        UpdateTrackInfo();
        if (_lyrics.State == LyricsState.Synced &&
            ReferenceEquals(_lines, _lyrics.Lines) && _blocks.Count == _lines.Count)
        {
            ApplyCurrentLine(_lyrics.GetLineIndex(_lyrics.GetAdjustedPosition()));
            _scrollOffset = CenterTarget();
            ScrollTransform.Y = _scrollOffset;
            return;
        }
        OnLyricsStateForDisplay();
    }

    void OnLyricsStateForDisplay()
    {
        switch (_lyrics.State)
        {
            case LyricsState.Synced:
                RebuildLyrics(animate: true);
                break;
            case LyricsState.Plain:
                ShowPlain();
                break;
            case LyricsState.Searching:
                ShowStatus(Loc.T("StatusSearchingLyrics"));
                break;
            case LyricsState.Instrumental:
                ShowStatus(Loc.T("StatusInstrumentalFull"));
                break;
            case LyricsState.NotFound:
                ShowStatus(Loc.T("StatusLyricsNotFoundFull"));
                break;
            case LyricsState.Error:
                ShowStatus(Loc.T("StatusLyricsErrorFull"));
                break;
            default:
                ShowStatus(Loc.T("StatusNoPlaybackFull"));
                break;
        }
    }

    void ShowStatus(string text)
    {
        StatusMessage.Text = text;
        StatusMessage.Visibility = Visibility.Visible;
        LyricsCanvas.Visibility = Visibility.Collapsed;
        PlainScrollFull.Visibility = Visibility.Collapsed;
    }

    void ShowPlain()
    {
        PlainFullText.Text = _lyrics.PlainLyrics ?? "";
        PlainScrollFull.Visibility = Visibility.Visible;
        PlainScrollFull.ScrollToHome();
        PlainScrollFull.Opacity = 0;
        PlainScrollFull.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
        StatusMessage.Visibility = Visibility.Collapsed;
        LyricsCanvas.Visibility = Visibility.Collapsed;
    }

    void UpdateTrackInfo()
    {
        var track = App.SnapshotMode
            ? _lyrics.Track ?? _media.LastSnapshot?.Track
            : _media.LastSnapshot?.Track ?? _lyrics.Track;
        var title = track?.Title ?? Loc.T("NoPlayback");
        var artist = track?.Artist ?? "";
        var album = string.IsNullOrWhiteSpace(track?.Album) ? "" : track!.Album;

        TitleText.Text = title;
        ArtistText.Text = artist;
        AlbumText.Text = album;
        LightTitle.Text = title;
        LightArtist.Text = artist;
        LightAlbum.Text = album;

        if (App.SnapshotMode) return;

        if (track is null)
        {
            StopAnimated();
            if (_artKey.Length > 0)
            {
                _artKey = "";
                _artLoaded = false;
                _artBytes = null;
                _artAlbum = "";
                _artSource = "";
                _artHighRes = false;
                _artNextAt = DateTime.MaxValue;
                _artWaitSince = DateTime.MaxValue;
                _artClearedForTrack = true;
                FadeArtToPlaceholder();
            }
            return;
        }

        if (track.Key != _artKey)
        {
            var keepArt = CoverArtService.SameAlbum(track.Album, _artAlbum) &&
                          (_artSource != "Player" || _artHighRes);
            _artKey = track.Key;
            _artLoaded = keepArt;
            _artNextAt = DateTime.Now;
            _artWaitSince = DateTime.Now;
            _artClearedForTrack = true;
            if (!keepArt)
            {
                _artBytes = null;
                _artAlbum = "";
                _artSource = "";
                _artHighRes = false;
                FadeArtToPlaceholder();
            }
            StopAnimated();
            _ = RefreshAnimatedAsync(track);
        }

        if (!_artBusy && DateTime.Now >= _artNextAt)
            _ = RefreshArtAsync(track);
    }

    void UpdateTransport()
    {
        var playing = _media.LastSnapshot?.IsPlaying == true;
        var animate = !App.Designs.Active.ReduceMotion;
        DarkPlayPauseIcon.Animate = animate;
        LightPlayPauseIcon.Animate = animate;
        DarkPlayPauseIcon.Playing = playing;
        LightPlayPauseIcon.Playing = playing;
    }

    void SyncFrameLoop()
    {
        var run = _isOpen && IsVisible && WindowState != WindowState.Minimized;
        if (run == _frameHooked) return;
        _frameHooked = run;
        if (run)
        {
            _lastFrame = DateTime.Now;
            CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (!_isOpen || !IsVisible || WindowState == WindowState.Minimized) return;

        var now = DateTime.Now;
        if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly &&
            (now - _lastFrame).TotalMilliseconds < 33)
            return;

        // Paused: nothing moves much, so there's no need to run at monitor speed.
        if (!_lyrics.IsPlaying && (now - _lastFrame).TotalMilliseconds < 50) return;

        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0 || dt > 0.25) dt = 0.016;

        ApplySoundSense(dt);
        ApplyCoverMotion(dt);
        DesignParticles.Update(dt, App.SoundSense?.Beat ?? 0);
        ApplyTextStyle(false);

        if (_lyrics.State == LyricsState.Synced && _lines.Count > 0 && _blocks.Count > 0)
        {
            var pos = _lyrics.GetAdjustedPosition();
            var intro = App.Designs.Active.Lyrics.IntroDots && IntroDots.IsIntro(_lines, pos);
            var interlude = false;
            var gapLine = -1;
            double gapStart = 0, gapEnd = 0;
            if (!intro && App.Designs.Active.Lyrics.InterludeSeparation)
                interlude = TryGetInterlude(pos, out gapLine, out gapStart, out gapEnd);
            AdvanceInterludeGap(interlude, gapLine, dt);

            var index = intro ? -1 : _lyrics.GetLineIndex(pos);
            if (index != _currentLine)
            {
                ApplyCurrentLine(index);
                if (LayoutLog) Log.Write($"layout line={index} {DebugLayout()}");
            }
            if (!intro) _wordLine?.Paint(pos, animate: !App.Designs.Active.ReduceMotion);

            var hostHeight = LyricsCanvas.ActualHeight;
            if (intro)
            {
                DarkIntroDots.Apply(true, pos, 0, _lines[0].Time);
                var firstTop = _tops.Count > 0 ? _tops[0] + _scrollOffset : 0;
                DarkDotsShift.Y = firstTop - (hostHeight > 1 ? hostHeight / 2 : 0) - 34;
                _hoverLine = -1;
            }
            else if (interlude)
            {
                DarkIntroDots.Apply(true, pos, gapStart, gapEnd);
                var gapTop = _tops[gapLine] + _heights[gapLine] + _scrollOffset;
                var gapCenter = gapTop + (_spacing + GapExtra(gapLine)) / 2;
                DarkDotsShift.Y = gapCenter - (hostHeight > 1 ? hostHeight / 2 : 0);
            }
            else
            {
                DarkIntroDots.Apply(false, 0, 0, 0);
                if (_interludeGapLine >= 0 && _interludeGapLine < _tops.Count)
                {
                    var gapTop = _tops[_interludeGapLine] + _heights[_interludeGapLine] + _scrollOffset;
                    var gapCenter = gapTop + (_spacing + GapExtra(_interludeGapLine)) / 2;
                    DarkDotsShift.Y = gapCenter - (hostHeight > 1 ? hostHeight / 2 : 0);
                }
            }

            if (hostHeight > 1)
            {
                var target = CenterTarget();
                if (!_scrollInit)
                {
                    _scrollOffset = target;
                    _scrollInit = true;
                }
                var diff = target - _scrollOffset;
                _scrollOffset += Math.Abs(diff) < 0.3 ? diff : diff * (1 - Math.Exp(-_scrollRate * dt));
                ScrollTransform.Y = _scrollOffset;

                for (var i = 0; i < _blocks.Count; i++)
                {
                    var block = _blocks[i];
                    var top = _tops[i] + _scrollOffset;
                    var visible = top < hostHeight + 100 && top + _heights[i] > -100;
                    var want = visible ? Visibility.Visible : Visibility.Collapsed;
                    if (block.Visibility != want) block.Visibility = want;
                    block.IsHitTestVisible = visible && !intro;
                    if (!visible) continue;

                    var center = top + _heights[i] / 2;
                    var distance = Math.Abs(center - hostHeight / 2);
                    var fade = Math.Clamp(1 - distance / (hostHeight * 0.42), 0, 1);
                    var inactiveOpacity = Math.Clamp(App.Designs.Active.Lyrics.InactiveOpacity, 0, 1);
                    var opacity = intro ? inactiveOpacity * fade : (i == _currentLine ? fade : inactiveOpacity * fade);
                    if (!intro && i == _hoverLine && i != _currentLine)
                        opacity = Math.Min(1, opacity + 0.22 * fade);
                    if (Math.Abs(block.Opacity - opacity) > 0.003)
                        block.Opacity = opacity;
                }
            }
        }
        else
        {
            DarkIntroDots.Apply(false, 0, 0, 0);
        }

        UpdateProgressAndTime();
    }

    void ApplySoundSense(double dt)
    {
        var s = _settings.Settings;
        var design = App.Designs.Active;
        var enabled = s.SoundSenseEnabled && design.Background.BeatReactive && !design.ReduceMotion;
        var intensity = enabled ? Math.Clamp(s.SoundSenseIntensity, 0, 1) : 0;

        void ResetVisuals()
        {
            if (Math.Abs(BackdropScale.ScaleX - _designCoverScale) > 0.0001 ||
                Math.Abs(BackdropScale.ScaleY - _designCoverScale) > 0.0001)
                BackdropScale.ScaleX = BackdropScale.ScaleY = _designCoverScale;
            if (_fusionGlow is { Opacity: > 0 }) _fusionGlow.Opacity = 0;
            if (_darkGlow is { Opacity: > 0 }) _darkGlow.Opacity = 0;
        }

        if (!enabled || intensity <= 0)
        {
            _sensePulse = 0;
            ResetVisuals();
            return;
        }

        var beat = App.SoundSense?.Beat ?? 0;
        if (beat <= 0 && _sensePulse < 0.001)
        {
            _sensePulse = 0;
            ResetVisuals();
            return;
        }

        var k = 1 - Math.Exp(-16 * dt);
        _sensePulse += (beat - _sensePulse) * k;
        if (_sensePulse < 0.001) _sensePulse = 0;
        var pulse = _sensePulse * intensity;

        BackdropScale.ScaleX = BackdropScale.ScaleY =
            _designCoverScale * (1.0 + 0.035 * intensity * (1 - _sensePulse));
        if (_fusionGlow is not null) _fusionGlow.Opacity = 0.65 * pulse;
        if (_darkGlow is not null) _darkGlow.Opacity = 0.70 * pulse;
    }

    void UpdateProgressAndTime()
    {
        var pos = Math.Max(0, _lyrics.GetDisplayPosition());
        var duration = _lyrics.Track?.DurationSeconds ?? _media.LastSnapshot?.DurationSeconds;
        var durationText = duration is > 0 ? FormatClock(duration.Value) : "--:--";

        var darkWidth = ProgressTrack.ActualWidth;
        if (darkWidth > 1)
        {
            ProgressFillScale.ScaleX = duration is > 0
                ? Math.Clamp(pos / duration.Value, 0, 1)
                : 0;
            if (DurText.Text != durationText) DurText.Text = durationText;
        }

        var lightWidth = LightProgressTrack.ActualWidth;
        if (lightWidth > 1)
        {
            LightProgressFillScale.ScaleX = duration is > 0
                ? Math.Clamp(pos / duration.Value, 0, 1)
                : 0;
            if (LightDurText.Text != durationText) LightDurText.Text = durationText;
        }

        if ((DateTime.Now - _lastClockUi).TotalMilliseconds > 250)
        {
            _lastClockUi = DateTime.Now;
            var text = FormatClock(pos);
            PosText.Text = text;
            LightPosText.Text = text;
        }
    }

    static string FormatClock(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
