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
using LyricGlow.Localization;
using LyricGlow.Models;
using LyricGlow.Rendering;
using LyricGlow.Services;

namespace LyricGlow;

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
    static readonly bool LayoutLog = Environment.GetEnvironmentVariable("LYRICGLOW_LAYOUT_LOG") == "1";
    static readonly bool AnimLog = Environment.GetEnvironmentVariable("LYRICGLOW_ANIM_LOG") == "1";

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
        CompositionTarget.Rendering += OnFrame;
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

    void ToggleSettingsPanel()
    {
        if (_settingsPanelOpen) CloseSettingsPanel();
        else OpenSettingsPanel();
    }

    void OpenSettingsPanel()
    {
        if (!_isOpen || _settingsPanelOpen) return;
        _settingsPanelOpen = true;
        _idleTimer.Stop();
        LoadSettingsPanel();
        UpdateSettingsPanelSize();
        SettingsPanel.Visibility = Visibility.Visible;

        SettingsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(
            SettingsPanel.Opacity, 1, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        SettingsPanelShift.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(SettingsPanelShift.Y, 0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        WakeChrome();
    }

    void CloseSettingsPanel()
    {
        if (!_settingsPanelOpen) return;
        _settingsPanelOpen = false;

        var fade = new DoubleAnimation(SettingsPanel.Opacity, 0, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, __) =>
        {
            if (!_settingsPanelOpen) SettingsPanel.Visibility = Visibility.Collapsed;
        };
        SettingsPanel.BeginAnimation(OpacityProperty, fade);
        SettingsPanelShift.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(SettingsPanelShift.Y, -8, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        WakeChrome();
    }

    void LoadSettingsPanel()
    {
        if (_loadingSettingsPanel) return;
        _loadingSettingsPanel = true;
        var s = _settings.Settings;
        SettingsAnimatedCheck.IsChecked = s.AnimatedCoversEnabled;
        SettingsSoundSenseCheck.IsChecked = s.SoundSenseEnabled;
        SettingsSourceLrcmux.IsChecked = s.LyricsSource == LyricSource.Lrcmux;
        SettingsSourceLyricsPlus.IsChecked = s.LyricsSource == LyricSource.LyricsPlus;
        SettingsSourceLrclib.IsChecked = s.LyricsSource == LyricSource.Lrclib;
        SettingsThemeLightRadio.IsChecked = s.InterfaceTheme == InterfaceTheme.Light;
        SettingsThemeDarkRadio.IsChecked = s.InterfaceTheme == InterfaceTheme.Dark;
        SettingsInterfaceGlassCheck.IsChecked = s.InterfaceGlassEnabled;
        SettingsWordFade.IsChecked = s.WordAnimation == WordAnimationStyle.Fade;
        SettingsWordSlide.IsChecked = s.WordAnimation == WordAnimationStyle.Slide;
        SettingsWordZoom.IsChecked = s.WordAnimation == WordAnimationStyle.Zoom;
        SettingsWordBounce.IsChecked = s.WordAnimation == WordAnimationStyle.Bounce;
        SettingsFontSlider.Value = Math.Clamp(s.FontScale * 100, 70, 160);
        SettingsFontText.Text = $"{(int)SettingsFontSlider.Value}%";
        var artwork = App.Designs.Active.Artwork;
        SettingsCoverSizeSlider.Value = Math.Clamp(artwork.SizePercent, 10, 60);
        SettingsCoverSizeText.Text = $"{(int)artwork.SizePercent}%";
        SettingsAnimatedCoverSizeSlider.Value = Math.Clamp(artwork.AnimatedSizePercent, 10, 60);
        SettingsAnimatedCoverSizeText.Text = $"{(int)artwork.AnimatedSizePercent}%";
        SettingsFullscreenRadio.IsChecked = !_windowed;
        SettingsWindowedRadio.IsChecked = _windowed;
        _loadingSettingsPanel = false;
    }

    void UpdateSettingsPanelSize()
    {
        if (SettingsScroll is null) return;
        var available = ActualHeight - 74 - 22 - 150;
        SettingsScroll.MaxHeight = Math.Max(220, available);
    }

    void ApplyQuickAnimatedSetting()
    {
        if (_loadingSettingsPanel) return;
        _settings.Settings.AnimatedCoversEnabled = SettingsAnimatedCheck.IsChecked == true;
        _settings.ScheduleSave();
        ApplyAnimatedCoverSetting();
        App.MainWindowRef?.RefreshSettingsUi();
    }

    void ApplyQuickSoundSenseSetting()
    {
        if (_loadingSettingsPanel) return;
        var on = SettingsSoundSenseCheck.IsChecked == true;
        _settings.Settings.SoundSenseEnabled = on;
        _settings.ScheduleSave();
        App.SoundSense?.SetEnabled(on);
        App.MainWindowRef?.RefreshSettingsUi();
    }

    void QuickCoverSize_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettingsPanel || SettingsCoverSizeText is null) return;
        var artwork = App.Designs.Active.Artwork;
        artwork.SizePercent = Math.Round(SettingsCoverSizeSlider.Value);
        SettingsCoverSizeText.Text = $"{(int)artwork.SizePercent}%";
        UpdateLightArtSize();
        App.MainWindowRef?.RefreshArtworkUi();
        App.MainWindowRef?.PersistActiveDesign();
    }

    void QuickAnimatedCoverSize_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettingsPanel || SettingsAnimatedCoverSizeText is null) return;
        var artwork = App.Designs.Active.Artwork;
        artwork.AnimatedSizePercent = Math.Round(SettingsAnimatedCoverSizeSlider.Value);
        SettingsAnimatedCoverSizeText.Text = $"{(int)artwork.AnimatedSizePercent}%";
        UpdateLightArtSize();
        App.MainWindowRef?.RefreshArtworkUi();
        App.MainWindowRef?.PersistActiveDesign();
    }

    void ApplyQuickLyricsSource(LyricSource source)
    {
        if (_loadingSettingsPanel) return;
        if (_settings.Settings.LyricsSource == source) return;
        _settings.Settings.LyricsSource = source;
        _settings.ScheduleSave();
        _lyrics.ReloadSource();
        App.MainWindowRef?.RefreshSettingsUi();
    }

    void QuickInterfaceTheme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettingsPanel) return;
        var theme = SettingsThemeDarkRadio.IsChecked == true ? InterfaceTheme.Dark : InterfaceTheme.Light;
        if (_settings.Settings.InterfaceTheme == theme) return;
        _settings.Settings.InterfaceTheme = theme;
        _settings.ScheduleSave();
        ThemePaletteService.SetInterfaceTheme(theme);
        App.MainWindowRef?.ApplyInterfaceThemeSettings();
        App.MainWindowRef?.RefreshSettingsUi();
    }

    void ApplyQuickInterfaceGlass()
    {
        if (_loadingSettingsPanel) return;
        _settings.Settings.InterfaceGlassEnabled = SettingsInterfaceGlassCheck.IsChecked == true;
        _settings.ScheduleSave();
        ApplyQuickPalette(ThemePaletteService.Current);
        App.MainWindowRef?.ApplyInterfaceThemeSettings();
        App.MainWindowRef?.RefreshSettingsUi();
    }

    public void ApplyInterfaceTheme()
    {
        if (SettingsThemeLightRadio is not null && SettingsThemeDarkRadio is not null)
        {
            var wasLoading = _loadingSettingsPanel;
            _loadingSettingsPanel = true;
            SettingsThemeLightRadio.IsChecked = _settings.Settings.InterfaceTheme == InterfaceTheme.Light;
            SettingsThemeDarkRadio.IsChecked = _settings.Settings.InterfaceTheme == InterfaceTheme.Dark;
            SettingsInterfaceGlassCheck.IsChecked = _settings.Settings.InterfaceGlassEnabled;
            _loadingSettingsPanel = wasLoading;
        }
        ApplyQuickPalette(ThemePaletteService.Current);
        ApplyDarkTitleBar();
    }

    void ApplyQuickWordAnimation(WordAnimationStyle style)
    {
        if (_loadingSettingsPanel) return;
        var mapped = (LyricAnimation)Enum.Parse(typeof(LyricAnimation), style.ToString());
        if (App.Designs.Active.Animations.WordAnimation == mapped) return;
        var design = App.Designs.CloneActive();
        design.Animations.WordAnimation = mapped;
        App.Designs.Apply(design);
        _settings.Settings.WordAnimation = style;
        _settings.ScheduleSave();
        App.MainWindowRef?.RefreshSettingsUi();
    }

    void ApplyQuickFontScale(double value)
    {
        var scale = Math.Clamp(value / 100.0, 0.7, 1.6);
        SettingsFontText.Text = $"{(int)value}%";
        if (_loadingSettingsPanel) return;
        _settings.Settings.FontScale = scale;
        _settings.ScheduleSave();
        ApplyTextStyle(force: false);
        App.MainWindowRef?.RefreshSettingsUi();
    }

    public void RefreshLocalizedText()
    {
        if (!_isOpen) return;
        UpdateWindowModeUi();
        OnLyricsChanged();
    }

    public void RefreshThemePalette()
    {
        if (!_isOpen) return;
        var bmp = CoverA.Source as BitmapSource ?? CoverB.Source as BitmapSource;
        if (bmp is not null)
        {
            ThemePaletteService.UpdateFromBitmap(bmp);
            return;
        }
        if (_artBytes is { Length: > 0 } bytes)
        {
            var decoded = DecodeArt(bytes);
            if (decoded is not null) ThemePaletteService.UpdateFromBitmap(decoded);
        }
    }

    void OnThemePaletteChanged(ThemePalette palette)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnThemePaletteChanged(palette)));
            return;
        }
        ApplyQuickPalette(palette);
    }

    void ApplyQuickPalette(ThemePalette palette)
    {
        var glass = _settings?.Settings.InterfaceGlassEnabled == true;
        SetQuickPaletteBrush("SettingsPaper", palette.Paper);
        SetQuickPaletteBrush("SettingsInk", palette.Ink);
        SetQuickPaletteBrush("SettingsMuted", palette.Muted);
        SetQuickPaletteBrush("SettingsLine", glass ? palette.GlassBorder : palette.Line);
        SetQuickPaletteBrush("SettingsAccent", palette.Accent);
        SetQuickPaletteBrush("SettingsTrack", palette.Track);
        SetQuickPaletteBrush("SettingsThumb", palette.Thumb);
        SetQuickPaletteBrush("SettingsThumbStroke", palette.ThumbStroke);
        SetQuickPaletteBrush("SettingsHover", palette.Hover);
        SetQuickPaletteBrush("SettingsScrollThumb", palette.ScrollThumb);
        SetQuickPaletteBrush("SettingsScrollThumbHover", palette.ScrollThumbHover);
        SetQuickPaletteBrush("SettingsBorder", glass ? palette.GlassBorder : palette.Border);
        SetQuickPaletteBrush("SettingsGlassSurface", glass ? palette.GlassSurface : palette.Paper);
        SetQuickPaletteBrush("SettingsGlassBorder", glass ? palette.GlassBorder : palette.Border);
        SetQuickPaletteBrush("SettingsGlassFill", palette.GlassFill);
        SetQuickPaletteBrush("SettingsGlassFillHover", palette.GlassFillHover);
        SetQuickPaletteBrush("SettingsGlassFillActive", palette.GlassFillActive);
        SetQuickPaletteBrush("SettingsOverlayHover", palette.OverlayHover);
        SetQuickPaletteBrush("SettingsOnAccent", palette.OnAccent);
    }

    void SetQuickPaletteBrush(string key, Color color) => Resources[key] = new SolidColorBrush(color);

    void OnDesignChanged()
    {
        if (!_isOpen) return;
        ApplyDesignProfile();
        if (!_customPalette && _detectedFusionColor is null)
            ApplyFusionPalette(Color.FromRgb(0xF1, 0xF1, 0xF3), animate: false);
        var centered = ResolveLayout(_fixedBackground);
        SetTheme(centered, _customPalette ? null : _detectedFusionColor, animateFusion: false, force: true);
        if (_settingsPanelOpen) LoadSettingsPanel();
    }

    void ApplyDesignProfile()
    {
        var design = App.Designs.Active;
        _customPalette = !design.Palette.AutoContrast ||
                         design.Background.Kind is BackgroundKind.Solid or BackgroundKind.Gradient;
        if (_customPalette)
        {
            _customBackground = ParseColor(design.Palette.BackgroundColor);
            _customBackgroundSecondary = ParseColor(design.Palette.BackgroundSecondaryColor);
            _customText = ParseColor(design.Palette.TextColor);
            _customSecondary = ParseColor(design.Palette.SecondaryTextColor);
            _customAccent = ParseColor(design.Palette.AccentColor);
            _customLyricActive = ParseColor(design.Palette.LyricActiveColor);
            _customLyricInactive = ParseColor(design.Palette.LyricInactiveColor);
            if (design.Palette.AutoContrast)
            {
                var light = Luminance(_customBackground) >= 0.35;
                _customText = light ? Color.FromRgb(0x11, 0x11, 0x14) : Colors.White;
                _customSecondary = Mix(_customText, _customBackground, light ? 0.32 : 0.24);
                _customLyricActive = _customText;
                _customLyricInactive = WithAlpha(_customText, 0x66);
            }
            ApplyCustomPalette();
        }
        ApplyDesignBackground();
        UpdateDesignControls();
        ConfigureDesignParticles();
        ApplyDesignArtworkStyle();
        ApplyTextStyle(force: true);
        UpdateLightArtSize();
    }

    void ApplyCustomPalette()
    {
        _fusionBackground = _customBackground;
        _fusionPrimary = _customText;
        _fusionSecondary = _customSecondary;
        _fusionMuted = _customSecondary;
        _fusionVocal = WithAlpha(_customSecondary, 0xB3);
        _fusionLight = Luminance(_customBackground) >= 0.35;
        _fusionBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _fusionBrush.Color = _customBackground;
        if (_fusionGlow is not null) _fusionGlow.Color = _customAccent;

        LightTitle.Foreground = new SolidColorBrush(_customText);
        LightArtist.Foreground = new SolidColorBrush(_customSecondary);
        LightAlbum.Foreground = new SolidColorBrush(_customSecondary);
        LightPosText.Foreground = new SolidColorBrush(_customSecondary);
        LightDurText.Foreground = new SolidColorBrush(_customSecondary);
        LightProgressTrack.Background = new SolidColorBrush(WithAlpha(_customText, 0x20));
        LightProgressFill.Background = new SolidColorBrush(_customAccent);

        TitleText.Foreground = new SolidColorBrush(_customText);
        ArtistText.Foreground = new SolidColorBrush(_customSecondary);
        AlbumText.Foreground = new SolidColorBrush(_customSecondary);
        PosText.Foreground = new SolidColorBrush(_customSecondary);
        DurText.Foreground = new SolidColorBrush(_customSecondary);
        ProgressTrack.Background = new SolidColorBrush(WithAlpha(_customText, 0x20));
        ProgressFill.Background = new SolidColorBrush(_customAccent);
    }

    void ApplyDesignBackground()
    {
        var design = App.Designs.Active;
        _designCoverScale = Math.Clamp(design.Background.CoverScale, 0.5, 2);
        BackdropScale.ScaleX = BackdropScale.ScaleY = _designCoverScale;
        LightBackdropScale.ScaleX = LightBackdropScale.ScaleY = _designCoverScale;
        BackdropImage.Effect = null;
        LightBackdropImage.Effect = null;
        DarkBackdropTint.Visibility = Visibility.Visible;

        if (design.Background.Kind == BackgroundKind.Gradient)
        {
            var brush = new LinearGradientBrush(_customBackground, _customBackgroundSecondary,
                Math.Clamp(design.Palette.GradientAngle, 0, 360));
            RootGrid.Background = brush;
            DarkLayout.Background = brush;
            LightLayout.Background = brush;
            DarkBackdropTint.Visibility = Visibility.Collapsed;
            BackdropImage.BeginAnimation(OpacityProperty, null);
            BackdropImage.Opacity = 0;
            LightBackdropImage.BeginAnimation(OpacityProperty, null);
            LightBackdropImage.Opacity = 0;
            LightBackdropScrim.Opacity = 0;
            return;
        }

        if (design.Background.Kind == BackgroundKind.Solid)
        {
            RootGrid.Background = new SolidColorBrush(_customBackground);
            DarkLayout.Background = new SolidColorBrush(_customBackground);
            LightLayout.Background = new SolidColorBrush(_customBackground);
            DarkBackdropTint.Visibility = Visibility.Collapsed;
            BackdropImage.BeginAnimation(OpacityProperty, null);
            BackdropImage.Opacity = 0;
            LightBackdropImage.BeginAnimation(OpacityProperty, null);
            LightBackdropImage.Opacity = 0;
            LightBackdropScrim.Opacity = 0;
            return;
        }

        RootGrid.Background = new SolidColorBrush(_customPalette ? _customBackground : Colors.Black);
        DarkLayout.Background = Brushes.Transparent;
        LightLayout.Background = _fusionBrush;
        var target = Math.Clamp(design.Background.Dim, 0, 1);
        BackdropImage.BeginAnimation(OpacityProperty, null);
        BackdropImage.Opacity = target;

        var blur = design.Background.Kind == BackgroundKind.CoverBlur
            ? Math.Clamp(design.Background.Blur, 0, 200)
            : 0;
        var coverSource = CoverA.Source as BitmapSource ?? CoverB.Source as BitmapSource;
        if (coverSource is not null)
        {
            // El desenfoque se hornea en la miniatura 256x144: aplicarlo en vivo
            // sobre la ventana completa cuesta GPU en cada fotograma sin verse mejor.
            var backdrop = CreateBackdrop(coverSource, blur);
            if (backdrop is not null)
            {
                BackdropImage.Source = backdrop;
                LightBackdropImage.Source = backdrop;
            }
        }

        EnsureLightBackdrop();
        LightBackdropImage.BeginAnimation(OpacityProperty, null);
        LightBackdropImage.Opacity = target;
        LightBackdropScrim.Opacity = 0.55;
    }

    void EnsureLightBackdrop()
    {
        if (LightBackdropImage.Source is not null) return;
        var source = CoverA.Source as BitmapSource ?? CoverB.Source as BitmapSource;
        if (source is not null)
            LightBackdropImage.Source = CreateBackdrop(source,
                App.Designs.Active.Background.Kind == BackgroundKind.CoverBlur
                    ? App.Designs.Active.Background.Blur
                    : 0);
    }

    void UpdateDesignControls()
    {
        var design = App.Designs.Active;
        var c = design.Controls;
        DarkTransportButtons.Visibility = c.ShowTransport ? Visibility.Visible : Visibility.Collapsed;
        LightTransportButtons.Visibility = c.ShowTransport ? Visibility.Visible : Visibility.Collapsed;
        DarkProgress.Visibility = c.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        LightProgress.Visibility = c.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = c.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        LightTitle.Visibility = c.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        ArtistText.Visibility = c.ShowMetadata ? Visibility.Visible : Visibility.Collapsed;
        AlbumText.Visibility = c.ShowMetadata ? Visibility.Visible : Visibility.Collapsed;
        LightArtist.Visibility = c.ShowMetadata ? Visibility.Visible : Visibility.Collapsed;
        LightAlbum.Visibility = c.ShowMetadata ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = c.ChromeStyle == ChromeStyle.Hidden ? Visibility.Collapsed : Visibility.Visible;
        ApplyChromeTheme();
        SetTransportsVisible(!_chromeHidden);
    }

    void ApplyDesignArtworkStyle()
    {
        var design = App.Designs.Active;
        var a = design.Artwork;
        var hidden = !a.Visible || design.Layout is DesignLayout.Minimal or DesignLayout.Karaoke;
        var fusion = a.Fusion || (_centeredTheme && design.Palette.CoverFusion &&
                                 _detectedFusionColor.HasValue && !_customPalette);
        LightCoverBorder.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        DarkCoverBorder.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        LightCoverBorder.CornerRadius = new CornerRadius(fusion ? 0 : a.CornerRadius);
        DarkCoverBorder.CornerRadius = new CornerRadius(fusion ? 0 : a.CornerRadius);
        LightCoverMask.CornerRadius = new CornerRadius(fusion ? 0 : a.CornerRadius);
        LightCoverMask.BorderThickness = new Thickness(fusion ? 2 : a.BorderThickness);
        LightCoverMask.BorderBrush = new SolidColorBrush(fusion ? _fusionBackground : _customAccent);
        DarkCoverMask.CornerRadius = new CornerRadius(fusion ? 0 : a.CornerRadius);
        DarkCoverMask.BorderThickness = new Thickness(fusion ? 2 : a.BorderThickness);
        DarkCoverMask.BorderBrush = new SolidColorBrush(fusion ? _fusionBackground : _customAccent);
        var shadow = fusion || a.ShadowOpacity <= 0
            ? null
            : new DropShadowEffect
            {
                BlurRadius = 55,
                ShadowDepth = 16,
                Opacity = a.ShadowOpacity,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance,
            };
        LightCoverBorder.Effect = shadow;
        DarkCoverBorder.Effect = a.Fusion || a.ShadowOpacity <= 0
            ? null
            : new DropShadowEffect
            {
                BlurRadius = 70,
                ShadowDepth = 20,
                Opacity = a.ShadowOpacity,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance,
            };
        UpdateCoverClips();
    }

    void UpdateCoverClips()
    {
        ApplyCoverClip(DarkCoverContent, DarkCoverBorder.CornerRadius.TopLeft);
        ApplyCoverClip(LightCoverContent, LightCoverBorder.CornerRadius.TopLeft);
    }

    static void ApplyCoverClip(FrameworkElement content, double radius)
    {
        var w = content.ActualWidth;
        var h = content.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            content.Clip = null;
            return;
        }
        var r = Math.Clamp(radius, 0, Math.Min(w, h) / 2);
        content.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    void ConfigureDesignParticles()
    {
        var design = App.Designs.Active;
        DesignParticles.Kind = design.Background.Particles;
        DesignParticles.Intensity = design.Background.ParticleIntensity;
        DesignParticles.ReducedMotion = design.ReduceMotion;
        if (design.Background.Particles == ParticleKind.None) DesignParticles.Stop();
        else DesignParticles.Start();
    }

    bool ResolveLayout(bool fixedBackground)
    {
        // Adaptativo solo pasa a la vista vertical fusionada cuando la carátula
        // tiene un fondo fijo de verdad; el resto usa la vista de dos columnas.
        return App.Designs.Active.Layout switch
        {
            DesignLayout.Adaptive => fixedBackground,
            DesignLayout.Lateral => false,
            DesignLayout.Fusion or DesignLayout.Minimal or DesignLayout.Karaoke => true,
            _ => fixedBackground,
        };
    }

    void ApplyCoverMotion(double dt)
    {
        var design = App.Designs.Active;
        var animation = design.Animations.CoverAnimation;
        if (design.ReduceMotion || animation == CoverAnimation.None)
        {
            if (_coverMotionIdle) return;
            _coverMotionIdle = true;
            _coverTranslate.X = _coverTranslate.Y = 0;
            _coverScale.ScaleX = _coverScale.ScaleY = 1;
            _coverRotate.Angle = 0;
            return;
        }

        _coverMotionIdle = false;
        _coverMotionTime += Math.Clamp(dt, 0, 0.1);
        var intensity = Math.Clamp(design.Animations.CoverIntensity, 0, 2);
        var wave = Math.Sin(_coverMotionTime * 1.4);
        _coverTranslate.X = animation == CoverAnimation.Parallax ? wave * 7 * intensity : 0;
        _coverTranslate.Y = animation == CoverAnimation.Float ? wave * 5 * intensity : 0;
        _coverRotate.Angle = animation == CoverAnimation.Tilt ? wave * 2.5 * intensity : 0;
        var scale = animation switch
        {
            CoverAnimation.Breathe => 1 + wave * 0.018 * intensity,
            CoverAnimation.Beat => 1 + (App.SoundSense?.Beat ?? 0) * 0.035 * intensity,
            _ => 1,
        };
        _coverScale.ScaleX = _coverScale.ScaleY = scale;
    }

    LrcLine EffectiveLine(LrcLine line)
    {
        if (App.Designs.Active.Lyrics.ShowBackingVocals || line.Background.Length == 0) return line;
        return new LrcLine
        {
            Time = line.Time,
            Text = line.Text,
            Words = line.Words,
            Background = "",
            BackgroundWords = null,
            Romanized = line.Romanized,
            SourceTime = line.SourceTime,
        };
    }

    static WordAnimationStyle MapWordAnimation(LyricAnimation animation) =>
        (WordAnimationStyle)Enum.Parse(typeof(WordAnimationStyle), animation.ToString());

    static FontWeight MapFontWeight(string weight) => weight switch
    {
        "Light" => FontWeights.Light,
        "Normal" => FontWeights.Normal,
        "Medium" => FontWeights.Medium,
        "Bold" => FontWeights.Bold,
        "Black" => FontWeights.Black,
        _ => FontWeights.SemiBold,
    };

    static TextAlignment MapAlignment(DesignAlignment alignment) => alignment switch
    {
        DesignAlignment.Left => TextAlignment.Left,
        DesignAlignment.Right => TextAlignment.Right,
        _ => TextAlignment.Center,
    };

    TextAlignment CurrentLyricAlignment() =>
        _centeredTheme ? TextAlignment.Center : MapAlignment(App.Designs.Active.Lyrics.Alignment);

    static Color ParseColor(string value)
    {
        try
        {
            var converted = ColorConverter.ConvertFromString(value);
            return converted is Color color ? color : Colors.White;
        }
        catch
        {
            return Colors.White;
        }
    }

    public void OpenForSnapshot()
    {
        _isOpen = true;
        WindowState = WindowState.Normal;
        Left = -20000;
        Top = -20000;
        Width = 1600;
        Height = 900;
        ShowActivated = false;
        Show();

        UpdateTrackInfo();
        UpdateTransport();
        OnLyricsStateForDisplay();
        UpdateLightArtSize();
        WakeChrome();
        _idleTimer.Stop();
        RootGrid.Opacity = 1;
    }

    public void CloseForSnapshot()
    {
        _isOpen = false;
        _idleTimer.Stop();
        StopAnimated();
        Hide();
    }

    public void DebugResize(double width, double height)
    {
        WindowState = WindowState.Normal;
        Width = width;
        Height = height;
        UpdateLayout();
    }

    public void CloseFullscreen()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _idleTimer.Stop();
        Cursor = Cursors.Arrow;
        _allowClose = true;
        StopAnimated();
        Close();
    }

    public void DebugApplyArt(byte[] bytes) => ApplyArtBytes(bytes);

    public void DebugSetPlaying(bool playing, bool animate)
    {
        DarkPlayPauseIcon.Animate = animate;
        LightPlayPauseIcon.Animate = animate;
        DarkPlayPauseIcon.Playing = playing;
        LightPlayPauseIcon.Playing = playing;
    }

    public void DebugSetChrome(bool visible)
    {
        if (visible) WakeChrome();
        else HideChrome();
    }

    public void DebugSetAnimatedReady(bool ready)
    {
        _animatedReady = ready;
        UpdateLightArtSize();
    }

    public string DebugCoverSides() =>
        $"static={App.Designs.Active.Artwork.SizePercent:0} animated={App.Designs.Active.Artwork.AnimatedSizePercent:0} " +
        $"light={LightCoverBorder.Width:0} dark={DarkCoverBorder.Width:0} panel={DarkInfoPanel.Width:0} " +
        $"lightProgress={LightProgress.Width:0} darkProgress={DarkProgress.Width:0} active={_animatedReady}";

    public string DebugState()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"open={_isOpen} state={_lyrics.State} design={App.Designs.Active.Layout} theme={(_centeredTheme ? _fusionLight ? "fusion-light" : "fusion-dark" : "lateral-dark")} idx={_currentLine} ");
        sb.Append($"scroll={_scrollOffset:F1} host={LyricsHost.ActualWidth:F0}x{LyricsHost.ActualHeight:F0} blocks={_blocks.Count}");
        if (_blocks.Count > 0 && _currentLine >= 0 && _currentLine < _blocks.Count)
        {
            sb.Append($" topCur={Canvas.GetTop(_blocks[_currentLine]):F0} hCur={_heights[_currentLine]:F0} target={CenterTarget():F0}");
        }
        return sb.ToString();
    }

    public string DebugBackdrop()
    {
        string BrushName(Brush? brush) => brush switch
        {
            SolidColorBrush s => $"solid:{s.Color}",
            LinearGradientBrush => "gradient",
            null => "null",
            _ => brush.GetType().Name,
        };
        return $"backdrop op={BackdropImage.Opacity:0.###} src={BackdropImage.Source is not null} " +
               $"vis={BackdropImage.Visibility} effect={BackdropImage.Effect?.GetType().Name ?? "none"} " +
               $"lightBackdrop op={LightBackdropImage.Opacity:0.###} src={LightBackdropImage.Source is not null} " +
               $"effect={LightBackdropImage.Effect?.GetType().Name ?? "none"} " +
               $"dark={BrushName(DarkLayout.Background)} light={BrushName(LightLayout.Background)} " +
               $"lightVis={LightLayout.Visibility} root={BrushName(RootGrid.Background)}";
    }

    public string DebugHorizontalLayout()
    {
        var hostOrigin = LyricsHost.TransformToAncestor(RootGrid).Transform(new Point(0, 0));
        var centerDelta = hostOrigin.X + LyricsHost.ActualWidth / 2 - ActualWidth / 2;
        return $"hostX={hostOrigin.X:F1} hostW={LyricsHost.ActualWidth:F1} " +
               $"windowW={ActualWidth:F1} centerDelta={centerDelta:F1} " +
               $"column={Grid.GetColumn(LyricsHost)} row={Grid.GetRow(LyricsHost)}";
    }

    public void DebugSetSettingsPanel(bool open)
    {
        if (open) OpenSettingsPanel();
        else CloseSettingsPanel();
    }

    public string DebugGap()
    {
        var pos = _lyrics.GetAdjustedPosition();
        var index = _lyrics.GetLineIndex(pos);
        var found = $"picked='{_lyrics.Found?.TrackName}' dur={(int?)_lyrics.Found?.Duration} lines={_lines.Count}";
        if (index < 0 || index + 1 >= _lines.Count) return $"pos={pos:F1} idx={index} {found}";
        var line = _lines[index];
        var next = _lines[index + 1];
        var interlude = TryGetInterlude(pos, out _, out var start, out var end);
        return $"pos={pos:F1} idx={index} line=[{line.Time:F2}] '{line.Text}' words={line.Words?.Count ?? 0} " +
               $"finish={LineFinishedAt(index):F2} next=[{next.Time:F2}] '{next.Text}' " +
               $"interlude={interlude} span={start:F1}-{end:F1} {found}";
    }

    public string DebugLayout()
    {
        var sb = new StringBuilder();
        sb.Append($"spacing={_spacing:F1} font={_baseFontSize:F1} " +
                  $"canvas={LyricsCanvas.ActualWidth:F0}x{LyricsCanvas.ActualHeight:F0} " +
                  $"blocks={_blocks.Count} lines={_lines.Count}");
        var small = new List<string>();
        var heights = new StringBuilder();
        for (var i = 0; i < _blocks.Count; i++)
        {
            if (_heights[i] < 5) small.Add(i.ToString());
            if (i < 45) heights.Append(_heights[i].ToString("F0")).Append(',');
        }
        sb.Append($" small=[{string.Join(' ', small)}] heights=[{heights}]");
        return sb.ToString();
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
                          (_artSource != "Reproductor" || _artHighRes);
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

    void FadeArtToPlaceholder()
    {
        _fixedBackground = false;
        if (!_customPalette) ApplyFusionPalette(Color.FromRgb(0xF1, 0xF1, 0xF3), animate: false);
        else ApplyCustomPalette();
        SetTheme(ResolveLayout(false), null, animateFusion: false, force: true);
        CoverPlaceholder.Visibility = Visibility.Visible;
        var duration = TimeSpan.FromMilliseconds(320);
        foreach (var image in new[] { CoverA, CoverB, LightCoverA, LightCoverB })
        {
            if (image.Source is null)
            {
                image.Opacity = 0;
                continue;
            }
            image.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(image.Opacity, 0, duration) { FillBehavior = FillBehavior.HoldEnd });
        }

        FadeBackdrop(BackdropImage, duration);
        FadeBackdrop(LightBackdropImage, duration);

        static void FadeBackdrop(Image image, TimeSpan duration)
        {
            if (image.Source is null)
            {
                image.Opacity = 0;
                return;
            }
            var fade = new DoubleAnimation(image.Opacity, 0, duration) { FillBehavior = FillBehavior.HoldEnd };
            fade.Completed += (_, __) =>
            {
                image.BeginAnimation(OpacityProperty, null);
                image.Source = null;
                image.Opacity = 0;
            };
            image.BeginAnimation(OpacityProperty, fade);
        }
    }

    async System.Threading.Tasks.Task RefreshArtAsync(TrackInfo track)
    {
        if (_artBusy) return;
        _artBusy = true;
        var key = track.Key;
        try
        {
            var art = await _cover.GetAsync(track, CancellationToken.None);
            if (key != _artKey) return;
            var artAlbum = !string.IsNullOrWhiteSpace(art.Album) ? art.Album : track.Album;
            var bytes = art.Bytes;
            if (bytes is null)
            {
                if (!_artClearedForTrack && DateTime.Now - _artWaitSince > TimeSpan.FromSeconds(6))
                {
                    _artClearedForTrack = true;
                    FadeArtToPlaceholder();
                }
                _artNextAt = DateTime.Now.AddSeconds(1);
                return;
            }

            if (_artBytes is not null && bytes.AsSpan().SequenceEqual(_artBytes))
            {
                _artAlbum = artAlbum;
                _artSource = art.Source;
                _artHighRes = art.IsHighResolution;
                _artLoaded = true;
                _artNextAt = DateTime.MaxValue;
                return;
            }

            var bmp = DecodeArt(bytes);
            if (key != _artKey || !_isOpen) return;
            if (bmp is null)
            {
                _artNextAt = DateTime.Now.AddSeconds(1);
                return;
            }

            ApplyArtBitmap(bmp);
            _artBytes = bytes;
            _artAlbum = artAlbum;
            _artSource = art.Source;
            _artHighRes = art.IsHighResolution;
            _artClearedForTrack = false;
            if (_artLoaded)
            {
                _artNextAt = DateTime.MaxValue;
            }
            else
            {
                _artLoaded = true;
                _artNextAt = DateTime.Now.AddSeconds(2.5);
            }
        }
        finally
        {
            _artBusy = false;
        }
    }

    public void ApplyAnimatedCoverSetting()
    {
        if (!_isOpen) return;
        if (!_settings.Settings.AnimatedCoversEnabled)
        {
            StopAnimated();
            return;
        }
        _ = RefreshAnimatedAsync(_lyrics.Track ?? _media.LastSnapshot?.Track);
    }

    async Task RefreshAnimatedAsync(TrackInfo? track)
    {
        if (!_settings.Settings.AnimatedCoversEnabled || track is null || !_isOpen)
        {
            StopAnimated();
            return;
        }
        if (_animTrackKey == track.Key && (_animatedReady || _animCts is not null)) return;

        _animCts?.Cancel();
        var cts = new CancellationTokenSource();
        _animCts = cts;
        var key = track.Key;
        _animTrackKey = key;
        Log.Write($"animated UI: buscando artista '{track.Artist}' + álbum '{track.Album}'");
        try
        {
            var result = await _animated.GetAsync(track, cts.Token);
            if (cts.IsCancellationRequested || _animTrackKey != key || !_isOpen) return;
            if (!result.Available || result.Path is null)
            {
                Log.Write("animated UI: no disponible (" + (result.ErrorKey ?? "none") + ")");
                StopAnimated();
                return;
            }
            Log.Write($"animated UI: iniciando {result.Source} {result.Path}");
            StartAnimated(result.Path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("fullscreen animated: " + ex.Message);
            StopAnimated();
        }
    }

    void StartAnimated(string path)
    {
        if (!_isOpen || !_settings.Settings.AnimatedCoversEnabled) return;
        Log.Write("animated UI: cargando MediaElement");
        var uri = new Uri(path);
        _animatedPlaying = false;

        foreach (var element in new[] { AnimatedCoverDark, AnimatedCoverLight })
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 0;
            element.Stop();
            element.Source = uri;
        }

        ActiveAnimated.Play();
        _animatedPlaying = true;
        CoverPlaceholder.Visibility = Visibility.Collapsed;
    }

    MediaElement ActiveAnimated => _centeredTheme ? AnimatedCoverLight : AnimatedCoverDark;

    void StopAnimated()
    {
        var wasReady = _animatedReady;
        var cts = _animCts;
        _animCts = null;
        _animTrackKey = "";
        _animatedReady = false;
        _animatedPlaying = false;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
        foreach (var element in new[] { AnimatedCoverDark, AnimatedCoverLight })
        {
            try
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = 0;
                element.Stop();
                element.Source = null;
            }
            catch
            {
            }
        }

        if (wasReady && _isOpen) UpdateLightArtSize(animate: true);
    }

    void AnimatedCover_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement element) return;
        if (!_isOpen || !_settings.Settings.AnimatedCoversEnabled)
        {
            element.Pause();
            return;
        }
        if (!ReferenceEquals(element, ActiveAnimated))
        {
            element.Pause();
            return;
        }

        var duration = element.NaturalDuration.HasTimeSpan
            ? element.NaturalDuration.TimeSpan.TotalSeconds.ToString("F2")
            : "auto";
        Log.Write($"animated UI: MediaOpened {element.NaturalVideoWidth}x{element.NaturalVideoHeight} " +
                  $"duración={duration}");
        _animatedReady = true;
        UpdateLightArtSize(animate: true);
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        element.Play();
        _animatedPlaying = true;
    }

    void AnimatedCover_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement element || !ReferenceEquals(element, ActiveAnimated))
            return;
        if (!_animatedReady) return;
        Log.Write("animated UI: fin, reiniciando");
        element.Position = TimeSpan.Zero;
        element.Play();
        _animatedPlaying = true;
    }

    void AnimatedCover_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Log.Write("animated playback: " + e.ErrorException.Message);
        if (sender is MediaElement element && ReferenceEquals(element, ActiveAnimated))
            StopAnimated();
    }

    void EnsureAnimatedPlaying()
    {
        if (!_animatedReady) return;
        var active = ActiveAnimated;
        if (AnimLog && (DateTime.Now - _animLogAt).TotalSeconds > 3)
        {
            _animLogAt = DateTime.Now;
            Log.Write($"animated UI: pos={active.Position.TotalSeconds:F2} " +
                      $"playing={_animatedPlaying} opacity={active.Opacity:F2} " +
                      $"bright={_fusionLight} size={active.ActualWidth:F0}x{active.ActualHeight:F0}");
        }
        if (_animatedPlaying) return;
        active.Play();
        _animatedPlaying = true;
    }

    void SyncAnimatedTheme()
    {
        if (!_animatedReady) return;
        var previous = _centeredTheme ? AnimatedCoverDark : AnimatedCoverLight;
        var current = ActiveAnimated;
        var position = previous.Position;

        previous.Pause();
        previous.BeginAnimation(OpacityProperty, null);
        previous.Opacity = 0;

        try
        {
            current.Position = position;
        }
        catch
        {
        }
        current.BeginAnimation(OpacityProperty, null);
        current.Opacity = 1;

        current.Play();
        _animatedPlaying = true;
    }

    void ApplyArtBytes(byte[] bytes)
    {
        var bmp = DecodeArt(bytes);
        if (bmp is not null) ApplyArtBitmap(bmp);
    }

    static BitmapSource? DecodeArt(byte[] bytes, int maxSide = 1024)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = maxSide;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    void ApplyArtBitmap(BitmapSource bmp)
    {
        var hadArt = CoverA.Source is not null || CoverB.Source is not null;
        var previousCentered = _centeredTheme;
        ApplyArtBitmaps(bmp);
        ThemePaletteService.UpdateFromBitmap(bmp);
        var fusion = TrySampleBorderColor(bmp, out var fusionColor, out var fixedBackground);
        _detectedFusionColor = fusion ? fusionColor : (Color?)null;
        _fixedBackground = fixedBackground;
        var centered = ResolveLayout(fixedBackground);
        SetTheme(centered, _customPalette ? null : _detectedFusionColor,
            animateFusion: hadArt && previousCentered);
    }

    static bool TrySampleBorderColor(BitmapSource source, out Color color, out bool fixedBackground)
    {
        color = Colors.White;
        fixedBackground = false;
        try
        {
            var full = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var w = full.PixelWidth;
            var h = full.PixelHeight;
            if (w < 8 || h < 8) return false;

            var side = Math.Min(w, h);
            var inset = Math.Clamp((int)Math.Round(side * 0.008), 2, side / 4);
            var band = Math.Clamp((int)Math.Round(side * 0.01), 2, side / 8);
            var innerW = w - inset * 2;
            var innerH = h - inset * 2;
            if (innerW <= 0 || innerH <= 0) return false;

            var histogram = new int[4096];
            var total = 0;
            var dominant = 0;
            long sumR = 0, sumG = 0, sumB = 0;
            var dominantCount = 0;
            var edgeTotal = new int[4];
            var edgeDominant = new int[4];

            void ScanRect(Int32Rect rect, bool histogramPass, int edge)
            {
                if (rect.Width <= 0 || rect.Height <= 0) return;
                var stride = rect.Width * 4;
                var buffer = new byte[stride * rect.Height];
                full.CopyPixels(rect, buffer, stride, 0);
                for (var i = 0; i < buffer.Length; i += 4)
                {
                    var b = buffer[i];
                    var g = buffer[i + 1];
                    var r = buffer[i + 2];
                    var bin = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                    if (histogramPass)
                    {
                        histogram[bin]++;
                        total++;
                    }
                    else
                    {
                        edgeTotal[edge]++;
                        if (bin != dominant) continue;
                        edgeDominant[edge]++;
                        sumR += r;
                        sumG += g;
                        sumB += b;
                        dominantCount++;
                    }
                }
            }

            void ScanBorder(bool histogramPass)
            {
                ScanRect(new Int32Rect(inset, inset, innerW, band), histogramPass, 0);
                ScanRect(new Int32Rect(inset, h - inset - band, innerW, band), histogramPass, 1);
                var sideBandHeight = Math.Max(1, innerH - band * 2);
                ScanRect(new Int32Rect(inset, inset + band, band, sideBandHeight), histogramPass, 2);
                ScanRect(new Int32Rect(w - inset - band, inset + band, band, sideBandHeight), histogramPass, 3);
            }

            ScanBorder(true);
            if (total == 0) return false;
            var max = 0;
            for (var i = 0; i < histogram.Length; i++)
            {
                if (histogram[i] <= max) continue;
                max = histogram[i];
                dominant = i;
            }
            var dominantShare = max / (double)total;
            if (dominantShare < 0.55) return false;

            ScanBorder(false);
            if (dominantCount == 0) return false;
            color = Color.FromRgb(
                (byte)Math.Round((double)sumR / dominantCount),
                (byte)Math.Round((double)sumG / dominantCount),
                (byte)Math.Round((double)sumB / dominantCount));

            // Fondo fijo = mismo color dominante en todo el marco, no solo en promedio.
            fixedBackground = dominantShare >= 0.85;
            if (fixedBackground)
            {
                for (var i = 0; i < 4; i++)
                {
                    if (edgeTotal[i] == 0 || edgeDominant[i] / (double)edgeTotal[i] < 0.80)
                    {
                        fixedBackground = false;
                        break;
                    }
                }
            }
            return true;
        }
        catch
        {
            fixedBackground = false;
            return false;
        }
    }

    static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    static double Luminance(Color color)
    {
        double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    static bool FusionUsesDarkText(Color color)
    {
        var luminance = Luminance(color);
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05);
    }

    static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    static Color EnsureContrast(Color color, Color background, bool lightSurface)
    {
        if (Contrast(color, background) >= 4.5) return color;
        var anchor = lightSurface ? Colors.Black : Colors.White;
        for (var i = 0; i < 24; i++)
        {
            color = Mix(color, anchor, 0.08);
            if (Contrast(color, background) >= 4.5) return color;
        }
        return color;
    }

    static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    void ApplyFusionPalette(Color sampledColor, bool animate)
    {
        var background = sampledColor;
        _fusionLight = FusionUsesDarkText(background);
        var baseForeground = _fusionLight ? Color.FromRgb(0x0B, 0x0B, 0x0E) : Colors.White;
        var primary = EnsureContrast(
            Mix(baseForeground, background, _fusionLight ? 0.08 : 0.06), background, _fusionLight);
        var secondary = EnsureContrast(Mix(primary, background, 0.20), background, _fusionLight);
        var muted = EnsureContrast(Mix(primary, background, 0.30), background, _fusionLight);
        var accent = Mix(primary, sampledColor, 0.28);

        _fusionBackground = background;
        _fusionPrimary = primary;
        _fusionSecondary = secondary;
        _fusionMuted = muted;
        _fusionVocal = WithAlpha(secondary, 0xB3);

        if (animate && _fusionBrush.Color != background)
        {
            _fusionBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(_fusionBrush.Color, background, ArtFadeDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            _fusionBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _fusionBrush.Color = background;
        }

        LightTitle.Foreground = new SolidColorBrush(primary);
        LightArtist.Foreground = new SolidColorBrush(secondary);
        LightAlbum.Foreground = new SolidColorBrush(muted);
        LightPosText.Foreground = new SolidColorBrush(muted);
        LightDurText.Foreground = new SolidColorBrush(muted);
        LightProgressTrack.Background = new SolidColorBrush(WithAlpha(primary, 0x20));
        LightProgressFill.Background = new SolidColorBrush(primary);

        var transportBackground = new SolidColorBrush(WithAlpha(primary, 0x12));
        foreach (var button in new[] { LightPrevButton, LightPlayPauseButton, LightNextButton })
        {
            button.Background = transportBackground;
            button.Foreground = new SolidColorBrush(primary);
        }

        if (_fusionGlow is not null) _fusionGlow.Color = accent;
        LightCoverMask.BorderThickness = new Thickness(2);
        LightCoverBorder.CornerRadius = new CornerRadius(0);
        LightCoverBack.CornerRadius = new CornerRadius(0);
        LightCoverBorder.Effect = null;
    }

    static ImageBrush CreateFeatherMask(double featherFraction)
    {
        const int size = 128;
        var feather = Math.Max(2.0, size * featherFraction);
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var edge = Math.Min(Math.Min(x, y), Math.Min(size - 1 - x, size - 1 - y));
                var t = Math.Clamp(edge / feather, 0, 1);
                t = t * t * (3 - 2 * t);
                var i = (y * size + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Math.Round(255 * t);
            }
        }

        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    static BitmapSource CropFusion(BitmapSource source)
    {
        try
        {
            if (source.PixelWidth < 8 || source.PixelHeight < 8) return source;
            var inset = Math.Max(1,
                (int)Math.Round(Math.Min(source.PixelWidth, source.PixelHeight) * 0.002));
            var rect = new Int32Rect(
                inset, inset,
                source.PixelWidth - inset * 2,
                source.PixelHeight - inset * 2);
            var cropped = new CroppedBitmap(source, rect);
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return source;
        }
    }

    void ApplyArtBitmaps(BitmapSource bmp)
    {
        var incoming = _usingA ? CoverB : CoverA;
        var outgoing = _usingA ? CoverA : CoverB;
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        incoming.Source = bmp;
        incoming.Opacity = 0;
        incoming.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, ArtFadeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        outgoing.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(outgoing.Opacity, 0, ArtFadeDuration));

        var lightIn = _usingA ? LightCoverB : LightCoverA;
        var lightOut = _usingA ? LightCoverA : LightCoverB;
        var lightArt = CropFusion(bmp);
        Panel.SetZIndex(lightIn, 1);
        Panel.SetZIndex(lightOut, 0);
        lightIn.Source = lightArt;
        lightIn.Opacity = 0;
        lightIn.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, ArtFadeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        lightOut.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(lightOut.Opacity, 0, ArtFadeDuration));
        _usingA = !_usingA;

        CoverPlaceholder.Visibility = Visibility.Collapsed;

        var backdrop = CreateBackdrop(bmp, App.Designs.Active.Background.Kind == BackgroundKind.CoverBlur
            ? App.Designs.Active.Background.Blur
            : 0);
        if (backdrop is not null)
        {
            var background = App.Designs.Active.Background;
            var target = background.Kind is BackgroundKind.CoverBlur or BackgroundKind.CoverSolid
                ? Math.Clamp(background.Dim, 0, 1)
                : 0;
            var firstBackdrop = BackdropImage.Source is null;
            BackdropImage.Source = backdrop;
            LightBackdropImage.Source = backdrop;
            if (firstBackdrop || target <= 0)
            {
                BackdropImage.BeginAnimation(OpacityProperty, null);
                BackdropImage.Opacity = 0;
                LightBackdropImage.BeginAnimation(OpacityProperty, null);
                LightBackdropImage.Opacity = 0;
            }
            if (target > 0)
            {
                BackdropImage.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(BackdropImage.Opacity, target, TimeSpan.FromMilliseconds(500)));
                LightBackdropImage.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(LightBackdropImage.Opacity, target, TimeSpan.FromMilliseconds(500)));
            }
        }
    }

    static BitmapSource? CreateBackdrop(BitmapSource source, double blur)
    {
        try
        {
            var blurred = blur > 0;
            var w = blurred ? 256 : 512;
            var h = blurred ? 144 : 288;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(
                    new ImageBrush(source) { Stretch = Stretch.UniformToFill },
                    null,
                    new Rect(0, 0, w, h));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var pixels = new byte[w * h * 4];
            rtb.CopyPixels(pixels, w * 4, 0);
            var buffer = new byte[pixels.Length];

            if (blurred)
            {
                var radius = Math.Clamp((int)Math.Round(blur * 10.0 / 55.0), 1, 30);
                for (var pass = 0; pass < 2; pass++)
                {
                    BlurAxis(pixels, buffer, w, h, radius, horizontal: true);
                    BlurAxis(buffer, pixels, w, h, radius, horizontal: false);
                }
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    static void BlurAxis(byte[] src, byte[] dst, int w, int h, int radius, bool horizontal)
    {
        var primary = horizontal ? w : h;
        var secondary = horizontal ? h : w;
        var step = horizontal ? 4 : w * 4;

        for (var s = 0; s < secondary; s++)
        {
            var baseIndex = horizontal ? s * w * 4 : s * 4;
            for (var i = 0; i < primary; i++)
            {
                int b = 0, g = 0, r = 0, n = 0;
                var from = Math.Max(0, i - radius);
                var to = Math.Min(primary - 1, i + radius);
                for (var k = from; k <= to; k++)
                {
                    var idx = baseIndex + k * step;
                    b += src[idx];
                    g += src[idx + 1];
                    r += src[idx + 2];
                    n++;
                }
                var o = baseIndex + i * step;
                dst[o] = (byte)(b / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(r / n);
                dst[o + 3] = 255;
            }
        }
    }

    void ApplyLyricsTheme(bool centered)
    {
        _blockText = _customPalette ? _customText : centered ? _fusionPrimary : Colors.White;
        _wordDim = _customPalette
            ? _customLyricInactive
            : centered ? WithAlpha(_fusionPrimary, 0x59) : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
        _wordBright = _customPalette ? _customLyricActive : centered ? _fusionPrimary : Colors.White;
        _backgroundVocal = _customPalette ? WithAlpha(_customSecondary, 0xB3) : centered ? _fusionVocal : BackgroundVocal;
        _statusText = _customPalette
            ? _customSecondary
            : centered ? _fusionMuted : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
        _plainText = _customPalette
            ? _customSecondary
            : centered ? _fusionSecondary : Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF);

        var alignment = centered ? TextAlignment.Center : App.Designs.Active.Lyrics.Alignment switch
        {
            DesignAlignment.Left => TextAlignment.Left,
            DesignAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
        StatusMessage.Foreground = new SolidColorBrush(_statusText);
        PlainFullText.Foreground = new SolidColorBrush(_plainText);
        PlainFullText.TextAlignment = alignment;
        DarkIntroDots.HorizontalAlignment = centered
            ? HorizontalAlignment.Center
            : App.Designs.Active.Lyrics.Alignment == DesignAlignment.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        DarkIntroDots.SetBrush(new SolidColorBrush(_customPalette ? _customText : centered ? _fusionPrimary : Colors.White));

        foreach (var block in _blocks)
        {
            block.Foreground = new SolidColorBrush(_blockText);
            block.TextAlignment = alignment;
        }
        for (var i = 0; i < _blocks.Count; i++) SetBlockPlain(i);

        var current = _currentLine;
        _currentLine = int.MinValue;
        ApplyCurrentLine(current);
    }

    void AttachLyricsHost(bool centered)
    {
        if (centered)
        {
            if (LyricsHost.Parent is Panel oldParent && oldParent != LightSharedLyricsSlot)
                oldParent.Children.Remove(LyricsHost);
            if (!LightSharedLyricsSlot.Children.Contains(LyricsHost))
            {
                LightSharedLyricsSlot.Children.Clear();
                LightSharedLyricsSlot.Children.Add(LyricsHost);
            }
            Grid.SetColumn(LyricsHost, 0);
            Grid.SetRow(LyricsHost, 0);
            Grid.SetColumnSpan(LyricsHost, 1);
            LyricsHost.Margin = new Thickness(0);
            LyricsHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            LyricsHost.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            if (LyricsHost.Parent is Panel oldParent && oldParent != DarkContentGrid)
                oldParent.Children.Remove(LyricsHost);
            if (!DarkContentGrid.Children.Contains(LyricsHost))
                DarkContentGrid.Children.Add(LyricsHost);
            Grid.SetColumn(LyricsHost, 1);
            Grid.SetRow(LyricsHost, 0);
            Grid.SetColumnSpan(LyricsHost, 1);
            LyricsHost.Margin = new Thickness(60, 0, 0, 0);
            LyricsHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            LyricsHost.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    void SetTheme(bool centered, Color? fusionColor = null, bool animateFusion = false, bool force = false)
    {
        var layoutChanged = _centeredTheme != centered;
        var nextBackground = fusionColor ?? _fusionBackground;
        var nextLight = fusionColor.HasValue
            ? FusionUsesDarkText(nextBackground)
            : _fusionLight;
        var paletteChanged = fusionColor.HasValue &&
            (nextBackground != _fusionBackground || nextLight != _fusionLight);
        if (!force && !layoutChanged && !paletteChanged) return;

        if (fusionColor.HasValue && !_customPalette) ApplyFusionPalette(fusionColor.Value, animateFusion);
        else if (_customPalette) ApplyCustomPalette();
        _centeredTheme = centered;
        LightCoverBorder.RenderTransform = null;
        DarkCoverBorder.RenderTransform = null;
        if (centered) LightCoverBorder.RenderTransform = _coverTransform;
        else DarkCoverBorder.RenderTransform = _coverTransform;
        var hideArtwork = App.Designs.Active.Layout is DesignLayout.Minimal or DesignLayout.Karaoke ||
                          !App.Designs.Active.Artwork.Visible;
        LightCoverBorder.Visibility = hideArtwork ? Visibility.Collapsed : Visibility.Visible;
        DarkCoverBorder.Visibility = hideArtwork ? Visibility.Collapsed : Visibility.Visible;
        ApplyDesignArtworkStyle();

        AttachLyricsHost(centered);
        ApplyLyricsTheme(centered);
        ApplyTextStyle(force: true);
        ApplyChromeTheme();
        UpdateLightArtSize();
        SetTransportsVisible(!_chromeHidden);
        SyncAnimatedTheme();

        if (!layoutChanged)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isOpen) return;
                RecenterLyricsHost();
                OnHostResized();
            }), DispatcherPriority.Loaded);
            return;
        }

        var show = centered ? (FrameworkElement)LightLayout : DarkLayout;
        var hide = centered ? (FrameworkElement)DarkLayout : LightLayout;

        show.IsHitTestVisible = true;
        hide.IsHitTestVisible = false;
        show.Visibility = Visibility.Visible;
        show.Opacity = 0;
        show.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, ArtFadeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        var hideAnim = new DoubleAnimation(hide.Opacity, 0, ArtFadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        hideAnim.Completed += (_, __) =>
        {
            if (_centeredTheme == centered) hide.Visibility = Visibility.Collapsed;
        };
        hide.BeginAnimation(OpacityProperty, hideAnim);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isOpen) return;
            RecenterLyricsHost();
            OnHostResized();
            _scrollOffset = CenterTarget();
            ScrollTransform.Y = _scrollOffset;
            _scrollInit = true;
        }), DispatcherPriority.Loaded);
    }

    void ApplyChromeTheme()
    {
        var foreground = _customPalette ? _customText : _centeredTheme ? _fusionPrimary : Colors.White;
        var lightSurface = _customPalette ? Luminance(_customBackground) >= 0.35 : _centeredTheme && _fusionLight;
        var chromeBg = new SolidColorBrush(WithAlpha(foreground, lightSurface ? (byte)0x10 : (byte)0x14));
        CloseButton.Background = chromeBg;
        SettingsButton.Background = chromeBg;
        CloseButton.Foreground = new SolidColorBrush(foreground);
        SettingsButton.Foreground = new SolidColorBrush(foreground);
        HintText.Foreground = new SolidColorBrush(WithAlpha(foreground, lightSurface ? (byte)0x66 : (byte)0x59));
        var controls = App.Designs.Active.Controls;
        var radius = controls.ChromeStyle == ChromeStyle.Rounded
            ? Math.Clamp(controls.CornerRadius, 0, 999)
            : 0;
        Resources["DesignTransportCorner"] = new CornerRadius(radius);
        var transportBg = controls.ChromeStyle == ChromeStyle.Hidden
            ? Brushes.Transparent
            : new SolidColorBrush(WithAlpha(foreground, 0x12));
        foreach (var button in new[] { PrevButton, PlayPauseButton, NextButton, LightPrevButton, LightPlayPauseButton, LightNextButton })
        {
            button.Foreground = new SolidColorBrush(foreground);
            button.Background = transportBg;
        }
    }

    void UpdateLightArtSize(bool animate = false)
    {
        var design = App.Designs.Active;
        var animated = _animatedReady && _settings.Settings.AnimatedCoversEnabled;
        var sizePercent = animated ? design.Artwork.AnimatedSizePercent : design.Artwork.SizePercent;
        var side = ScaledCoverSide(ActualWidth, ActualHeight, sizePercent, light: true);
        var darkSide = ScaledCoverSide(ActualWidth, ActualHeight, sizePercent, light: false);
        ApplyCoverSides(animate,
            (LightCoverBorder, side, true),
            (LightProgress, side, false),
            (DarkCoverBorder, darkSide, true),
            (DarkInfoPanel, darkSide, false),
            (DarkProgress, Math.Max(140, darkSide - 4), false));

        if (_centeredTheme)
        {
            LyricsHost.Width = Math.Clamp(ActualWidth * (design.Lyrics.WidthPercent / 100.0), 320, 1200);
            LyricsHost.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            LyricsHost.Width = double.NaN;
            LyricsHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    // 26% es el tamaño histórico; el resto del recorrido escala sobre esa base
    // para que el ajuste siga teniendo efecto aunque la ventana sea grande.
    static double ScaledCoverSide(double width, double height, double percent, bool light)
    {
        var baseline = light
            ? Math.Clamp(Math.Min(width * 0.26, height * 0.38), 80, 430)
            : Math.Clamp(Math.Min(width * 0.26, height * 0.45), 220, 380);
        var min = light ? 80 : 220;
        var max = Math.Max(min, light
            ? Math.Min(width * 0.62, height * 0.50)
            : Math.Min(width * 0.55, height * 0.70));
        var scale = Math.Clamp(percent / 26.0, 0.1, 2.4);
        return Math.Clamp(baseline * scale, min, max);
    }

    void ApplyCoverSides(bool animate, params (FrameworkElement Element, double Side, bool Height)[] targets)
    {
        var differs = false;
        foreach (var (element, side, _) in targets)
        {
            if (Math.Abs(element.Width - side) > 0.5)
            {
                differs = true;
                break;
            }
        }

        var storyboard = _coverSizeStoryboard;
        _coverSizeStoryboard = null;
        if (storyboard is not null) storyboard.Stop();
        if (!animate || !differs)
        {
            _coverSizeAnimating = false;
            foreach (var (element, side, height) in targets)
            {
                element.BeginAnimation(WidthProperty, null);
                element.BeginAnimation(HeightProperty, null);
                element.Width = side;
                if (height) element.Height = side;
            }
            return;
        }

        storyboard = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        foreach (var (element, side, height) in targets)
        {
            var fromWidth = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
            var width = new DoubleAnimation(fromWidth, side, duration) { EasingFunction = ease };
            Storyboard.SetTarget(width, element);
            Storyboard.SetTargetProperty(width, new PropertyPath(WidthProperty));
            storyboard.Children.Add(width);
            if (!height) continue;
            var fromHeight = double.IsNaN(element.Height) ? element.ActualHeight : element.Height;
            var animation = new DoubleAnimation(fromHeight, side, duration) { EasingFunction = ease };
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, new PropertyPath(HeightProperty));
            storyboard.Children.Add(animation);
        }
        storyboard.Completed += (_, __) =>
        {
            _coverSizeAnimating = false;
            OnHostResized();
        };
        _coverSizeStoryboard = storyboard;
        _coverSizeAnimating = true;
        storyboard.Begin();
    }

    public void RefreshArtworkLayout()
    {
        if (!_isOpen) return;
        ApplyDesignArtworkStyle();
        UpdateLightArtSize(_animatedReady);
    }

    void RecenterLyricsHost()
    {
        if (!_isOpen) return;
        AttachLyricsHost(_centeredTheme);
        if (_centeredTheme)
        {
            var design = App.Designs.Active;
            LyricsHost.Width = Math.Clamp(ActualWidth * (design.Lyrics.WidthPercent / 100.0), 320, 1200);
            LyricsHost.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            LyricsHost.Width = double.NaN;
            LyricsHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        LyricsHost.RenderTransform = null;
        LyricsHost.UpdateLayout();
        var canvasWidth = Math.Max(200, LyricsCanvas.ActualWidth);
        var staleWidths = 0;
        foreach (var block in _blocks)
            if (double.IsNaN(block.Width) || Math.Abs(block.Width - canvasWidth) > 0.5)
                staleWidths++;
        if (staleWidths > 0)
            Log.Write($"host recenter: {staleWidths} block widths refreshed to {canvasWidth:F0}");
        LayoutBlocks();
        if (_centeredTheme && LyricsHost.ActualWidth > 1)
        {
            var origin = LyricsHost.TransformToAncestor(RootGrid).Transform(new Point(0, 0));
            var delta = origin.X + LyricsHost.ActualWidth / 2 - ActualWidth / 2;
            if (Math.Abs(delta) > 0.5)
            {
                LyricsHost.RenderTransform = new TranslateTransform(-delta, 0);
                Log.Write($"host visual recenter: delta={delta:F1}");
            }
        }
        if (LayoutLog) Log.Write("host recenter: " + DebugHorizontalLayout());
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

    void RebuildLyrics(bool animate)
    {
        animate &= !App.Designs.Active.ReduceMotion;
        LinesLayer.Children.Clear();
        _blocks.Clear();
        _heights.Clear();
        _tops.Clear();
        _wordLine = null;
        _currentLine = int.MinValue;
        _hoverLine = -1;
        _interludeGapLine = -1;
        _interludeGap = 0;
        _lines = _lyrics.Lines;

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var lineIndex = i;
            var block = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = _fontFamily,
                Foreground = new SolidColorBrush(_blockText),
                FontWeight = _fontWeight,
                FontSize = _baseFontSize,
                TextAlignment = CurrentLyricAlignment(),
                Background = Brushes.Transparent,
                Opacity = 0,
                Visibility = Visibility.Hidden,
                Cursor = Cursors.Hand,
            };
            LyricRender.SetPlain(block, EffectiveLine(line), _backgroundVocal);
            block.MouseLeftButtonDown += (_, e) =>
            {
                _ = SeekToLineAsync(lineIndex);
                e.Handled = true;
            };
            block.MouseEnter += (_, __) => _hoverLine = lineIndex;
            block.MouseLeave += (_, __) =>
            {
                if (_hoverLine == lineIndex) _hoverLine = -1;
            };
            Canvas.SetLeft(block, 0);
            Canvas.SetTop(block, 0);
            LinesLayer.Children.Add(block);
            _blocks.Add(block);
            _heights.Add(0);
            _tops.Add(0);
        }

        StatusMessage.Visibility = Visibility.Collapsed;
        PlainScrollFull.Visibility = Visibility.Collapsed;
        LyricsCanvas.Visibility = Visibility.Visible;

        ApplyCurrentLine(_lyrics.GetLineIndex(_lyrics.GetAdjustedPosition()));
        LayoutBlocks();
        if (LayoutLog) Log.Write("layout rebuild " + DebugLayout());
        _scrollOffset = CenterTarget();
        ScrollTransform.Y = _scrollOffset;
        _scrollInit = LyricsCanvas.ActualHeight > 1;

        if (animate)
        {
            LyricsCanvas.Opacity = 0;
            LyricsCanvas.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            LyricsCanvas.Opacity = 1;
        }
    }

    void ApplyCurrentLine(int index)
    {
        EnsureBlockWidths();
        if (index == _currentLine) return;
        var previous = _currentLine;
        _currentLine = index;

        if (previous >= 0 && previous < _blocks.Count)
        {
            SetBlockPlain(previous);
            _blocks[previous].Effect = null;
            MeasureBlock(previous);
        }

        _wordLine = null;
        if (index >= 0 && index < _blocks.Count)
        {
            var block = _blocks[index];
            block.Effect = _centeredTheme ? _fusionGlow : _darkGlow;
            var highlight = new WordHighlightLine(
                block,
                _wordDim,
                _wordBright,
                _wordFade)
            {
                BackgroundColor = _backgroundVocal,
                Style = MapWordAnimation(App.Designs.Active.Animations.WordAnimation),
                MotionStrength = Math.Clamp(App.Designs.Active.Animations.WordIntensity, 0, 1),
            };
            highlight.SetLine(EffectiveLine(_lines[index]));
            _wordLine = highlight;
            MeasureBlock(index);
        }

        if (previous >= 0 || index >= 0)
        {
            UpdateBlockTops();
            AnimateVerseTransition();
        }
    }

    void AnimateVerseTransition()
    {
        var design = App.Designs.Active;
        _verseShift.BeginAnimation(TranslateTransform.YProperty, null);
        _verseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _verseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _verseShift.Y = 0;
        _verseScale.ScaleX = _verseScale.ScaleY = 1;
        if (design.ReduceMotion || design.Animations.VerseTransition == VerseTransition.None) return;

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(design.Animations.TransitionDurationMs, 80, 1200));
        var ease = CreateDesignEasing(design.Animations.Easing);
        if (design.Animations.VerseTransition is VerseTransition.Slide or VerseTransition.Roll)
        {
            var from = design.Animations.VerseTransition == VerseTransition.Slide ? 10 : 24;
            _verseShift.Y = from;
            _verseShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
        }
        else if (design.Animations.VerseTransition == VerseTransition.Scale)
        {
            const double from = 0.965;
            _verseScale.ScaleX = _verseScale.ScaleY = from;
            _verseScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(from, 1, duration) { EasingFunction = ease });
            _verseScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(from, 1, duration) { EasingFunction = ease });
        }
        else if (design.Animations.VerseTransition == VerseTransition.Fade)
        {
            LyricsCanvas.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
        }
    }

    static IEasingFunction CreateDesignEasing(EasingKind kind) => kind switch
    {
        EasingKind.EaseInOut => new CubicEase { EasingMode = EasingMode.EaseInOut },
        EasingKind.Linear => new SineEase { EasingMode = EasingMode.EaseInOut },
        EasingKind.Spring => new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 },
        EasingKind.Bounce => new BounceEase { EasingMode = EasingMode.EaseOut, Bounciness = 2 },
        _ => new CubicEase { EasingMode = EasingMode.EaseOut },
    };

    void SetBlockPlain(int index)
    {
        LyricRender.SetPlain(_blocks[index], EffectiveLine(_lines[index]), _backgroundVocal);
    }

    async System.Threading.Tasks.Task SeekToLineAsync(int index)
    {
        var lines = _lyrics.Lines;
        if (index < 0 || index >= lines.Count) return;
        var position = _lyrics.GetSeekPosition(lines[index].Time);
        if (await _media.SeekAsync(position))
            _lyrics.NotifySeek(position);
    }

    void EnsureBlockWidths()
    {
        if (_blocks.Count == 0) return;
        var width = Math.Max(200, LyricsCanvas.ActualWidth);
        foreach (var block in _blocks)
        {
            if (double.IsNaN(block.Width) || Math.Abs(block.Width - width) > 0.5)
            {
                Log.Write($"lyrics widths refreshed: canvas={width:F0} first={block.Width:F0}");
                LayoutBlocks();
                return;
            }
        }
    }

    void MeasureBlock(int index)
    {
        if (index < 0 || index >= _blocks.Count) return;
        var block = _blocks[index];
        var collapsed = block.Visibility == Visibility.Collapsed;
        if (collapsed) block.Visibility = Visibility.Hidden;
        var width = Math.Max(200, LyricsCanvas.ActualWidth);
        block.Width = width;
        block.Measure(new Size(width, double.PositiveInfinity));
        _heights[index] = block.DesiredSize.Height;
        if (collapsed) block.Visibility = Visibility.Collapsed;
    }

    void LayoutBlocks()
    {
        if (_blocks.Count == 0) return;
        var width = Math.Max(200, LyricsCanvas.ActualWidth);

        // Collapsed elements always measure 0; reveal them for measurement so a
        // relayout doesn't shrink the off-screen lines to zero height.
        var collapsed = new List<int>();
        for (var i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Visibility != Visibility.Collapsed) continue;
            _blocks[i].Visibility = Visibility.Hidden;
            collapsed.Add(i);
        }

        double y = 0;
        for (var i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            block.Width = width;
            block.Measure(new Size(width, double.PositiveInfinity));
            var height = block.DesiredSize.Height;
            _heights[i] = height;
            _tops[i] = y;
            Canvas.SetTop(block, y);
            y += height + _spacing + GapExtra(i);
        }

        foreach (var i in collapsed) _blocks[i].Visibility = Visibility.Collapsed;
    }

    double LineFinishedAt(int index)
    {
        if (index < 0 || index >= _lines.Count) return 0;
        var line = _lines[index];
        var finish = line.Time + 4.0;
        if (line.Words is { Count: > 0 })
            finish = Math.Max(line.Time + 1.0, line.Words[^1].Time + 0.6);
        if (index + 1 < _lines.Count)
            finish = Math.Min(finish, _lines[index + 1].Time);
        return finish;
    }

    bool IsInterludeGap(int index)
    {
        if (index < 0 || index + 1 >= _lines.Count) return false;
        return _lines[index + 1].Time - LineFinishedAt(index) > 12.0;
    }

    double GapExtra(int index) =>
        index == _interludeGapLine ? InterludeGap * _interludeGap : 0.0;

    void UpdateBlockTops()
    {
        if (_blocks.Count == 0 || _tops.Count != _blocks.Count) return;
        double y = 0;
        for (var i = 0; i < _blocks.Count; i++)
        {
            _tops[i] = y;
            Canvas.SetTop(_blocks[i], y);
            y += _heights[i] + _spacing + GapExtra(i);
        }
    }

    void AdvanceInterludeGap(bool active, int gapLine, double dt)
    {
        if (active)
        {
            if (_interludeGapLine != gapLine)
                Log.Write($"interlude gap open line={gapLine}");
            _interludeGapLine = gapLine;
        }
        else if (_interludeGapLine < 0)
        {
            return;
        }

        var target = active ? 1.0 : 0.0;
        var next = _interludeGap + (target - _interludeGap) * (1 - Math.Exp(-10 * dt));
        if (!active && next < 0.005)
        {
            next = 0;
            _interludeGapLine = -1;
            Log.Write("interlude gap closed");
        }

        if (Math.Abs(next - _interludeGap) < 0.0005) return;
        _interludeGap = next;
        UpdateBlockTops();
    }

    bool TryGetInterlude(double position, out int current, out double start, out double end)
    {
        current = _lyrics.GetLineIndex(position);
        start = 0;
        end = 0;
        if (current < 0 || current + 1 >= _lines.Count || !IsInterludeGap(current)) return false;

        start = LineFinishedAt(current);
        end = _lines[current + 1].Time;
        return position >= start && position < end;
    }

    double CenterTarget()
    {
        var index = _currentLine;
        if (index < 0 && _lines.Count > 0) index = 0;
        if (index < 0 || index >= _tops.Count) return 0;
        return LyricsCanvas.ActualHeight / 2 - (_tops[index] + _heights[index] / 2) +
               App.Designs.Active.Lyrics.VerticalOffset;
    }

    void OnHostResized()
    {
        if (!_isOpen) return;
        UpdateSettingsPanelSize();
        // Durante la transición de tamaño el reflow se difiere al final para no
        // reconstruir todos los bloques en cada fotograma.
        if (_coverSizeAnimating) return;
        LayoutBlocks();
        _scrollOffset = CenterTarget();
        ScrollTransform.Y = _scrollOffset;
        _scrollInit = true;
        UpdateLightArtSize();
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (!_isOpen || !IsVisible || WindowState == WindowState.Minimized) return;

        var now = DateTime.Now;
        if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly &&
            (now - _lastFrame).TotalMilliseconds < 33)
            return;

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
