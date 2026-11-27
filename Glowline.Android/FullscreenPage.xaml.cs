using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Android.OS;
using Android.Views;
using Glowline.Android.Rendering;
using Glowline.Android.Services;
using Glowline.Localization;
using Glowline.Models;
using Glowline.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using MauiTextAlignment = Microsoft.Maui.TextAlignment;
using MauiView = Microsoft.Maui.Controls.View;
using AndroidColor = global::Android.Graphics.Color;
using AndroidColorDrawable = global::Android.Graphics.Drawables.ColorDrawable;

namespace Glowline.Android;

public partial class FullscreenPage : ContentPage
{
    sealed class WordView
    {
        public required Label Label { get; init; }
        public required double Time { get; init; }
        public bool Lit { get; set; }
    }

    sealed class LineView
    {
        public required LrcLine Line { get; init; }
        public required int Index { get; init; }
        public required VerticalStackLayout Root { get; init; }
        public MauiView? Main { get; set; }
        public MauiView? Backing { get; set; }
        public List<WordView> Words { get; set; } = new();
        public List<WordView> BackWords { get; set; } = new();
        public double Height { get; set; }
    }

    sealed class DotsView
    {
        public required HorizontalStackLayout Row { get; init; }
        public required BoxView[] Dots { get; init; }
        public bool Active { get; set; }
        public bool Fading { get; set; }
    }

    const double ArtFadeMilliseconds = 460;
    const double InterludeGap = 34;
    const double ChromeIdleSeconds = 2.6;
    static readonly double[] DotLightAt = { 0.3, 0.6, 0.9 };
    static readonly Color BackgroundVocal = Color.FromRgba(255, 255, 255, 0x80);

    readonly IDispatcherTimer _timer;
    readonly ParticleField _particles = new();
    readonly List<DotsView> _dots = new();
    readonly List<LineView> _lines = new();

    DateTime _lastTick = DateTime.MinValue;
    bool _loaded;
    int _themeVersion;
    double _lastPageWidth = -1;
    double _lastPageHeight = -1;

    IReadOnlyList<LrcLine> _renderedLines = Array.Empty<LrcLine>();
    AppLanguage _renderedLanguage = AppLanguage.Spanish;
    LyricsState _renderedState = LyricsState.Idle;
    string _designSignature = "";
    bool _designReady;
    bool _centered;
    bool _customPalette;

    int _currentLine = int.MinValue;
    int _gapLine = -1;
    double _gap;
    double _scrollOffset;
    bool _scrollInit;
    double _hostWidth;
    double _hostHeight;
    double _contentWidth;
    double _spacing = 24;
    double _lyricSize = 34;
    double _lineHeight = 1.2;
    string _fontFamily = "InterSemiBold";
    MauiTextAlignment _textAlignment = MauiTextAlignment.Center;

    Color _paletteBackground = Color.FromArgb("#F1F1F3");
    Color _palettePrimary = Color.FromArgb("#101013");
    Color _paletteSecondary = Color.FromArgb("#55555C");
    Color _paletteMuted = Color.FromArgb("#8A8A90");
    Color _paletteVocal = Color.FromRgba(255, 255, 255, 0x80);
    Color _paletteAccent = Color.FromArgb("#FF375F");
    bool _lightSurface = true;
    Color _darkText = Colors.White;
    Color _darkSecondary = Color.FromRgba(255, 255, 255, 0xB8);
    Color _darkMuted = Color.FromRgba(255, 255, 255, 0x66);
    Color _darkAccent = Colors.White;
    Color _lightAccent = Color.FromArgb("#101013");
    Color _customBackground = Color.FromArgb("#0A0A0C");
    Color _customBackgroundSecondary = Color.FromArgb("#17171B");
    Color _customText = Color.FromArgb("#F5F5F7");
    Color _customSecondary = Color.FromArgb("#A8A8B0");
    Color _customAccent = Color.FromArgb("#FF375F");
    Color _customLyricActive = Colors.White;
    Color _customLyricInactive = Color.FromRgba(255, 255, 255, 0x66);
    Color _blockText = Colors.White;
    Color _wordDim = Color.FromRgba(255, 255, 255, 0x38);
    Color _wordBright = Colors.White;
    Color _vocal = BackgroundVocal;
    Color _statusText = Color.FromRgba(255, 255, 255, 0x99);
    Color _plainText = Color.FromRgba(255, 255, 255, 0xDC);
    int _wordDuration = 110;

    byte[]? _lastCover;
    byte[]? _lastBackdrop;
    bool _darkCoverUsingA = true;
    bool _lightCoverUsingA = true;
    bool _darkBackdropUsingA = true;
    bool _lightBackdropUsingA = true;
    Image? _darkBackdropCurrent;
    Image? _lightBackdropCurrent;
    double _coverMotionTime;
    DateTime _chromeHideAt = DateTime.MaxValue;
    bool _chromeVisible = true;
    bool _seeking;
    DateTime _nextDesignCheck = DateTime.MinValue;
    DateTime _nextClockUpdate = DateTime.MinValue;
    DateTime _nextTrackUpdate = DateTime.MinValue;
    double _lastDuration = -1;
    bool _wide;
    double _infoWidth;
    double _scrollDiff;
    bool _tickFast = true;
    int _resettle;

    AppState State => AppState.Instance;
    DesignProfile Design => State.Design;

    public FullscreenPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        RootGrid.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        DarkLayout.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        LightLayout.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        DarkContent.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        LightContent.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        DarkChrome.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        LightChrome.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        foreach (var container in new Layout[]
                 {
                     DarkLyricsHost, DarkVerseLayer, DarkScrollLayer, DarkLyricsStack, DarkDotsRow,
                     LightLyricsHost, LightVerseLayer, LightScrollLayer, LightLyricsStack, LightDotsRow,
                 })
        {
            container.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        }
        DarkPlainScroll.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        LightPlainScroll.SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;
        ApplyResponsiveLayout();
        ParticleView.Drawable = _particles;
        ParticleView.IsVisible = false;
        ConfigureStaticLabels();
        BuildDots(DarkDotsRow, _dots);
        BuildDots(LightDotsRow, _dots);
        AddTap(DarkLayout, OnScreenTap);
        AddTap(LightLayout, OnScreenTap);
        DarkLyricsHost.SizeChanged += (_, __) => OnHostResized();
        LightLyricsHost.SizeChanged += (_, __) => OnHostResized();
        SizeChanged += (_, __) => OnPageResized();

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, __) => Tick();
    }

    void ConfigureStaticLabels()
    {
        foreach (var label in new[]
                 {
                     DarkTitleLabel, DarkArtistLabel, DarkAlbumLabel,
                     LightTitleLabel, LightArtistLabel, LightAlbumLabel,
                     DarkPlainLabel, LightPlainLabel, DarkStatusLabel, LightStatusLabel,
                 })
        {
            label.FontAutoScalingEnabled = false;
        }
        DarkVerseLayer.AnchorX = LightVerseLayer.AnchorX = 0.5;
        DarkVerseLayer.AnchorY = LightVerseLayer.AnchorY = 0.5;
        DarkCoverBorder.AnchorX = LightCoverBorder.AnchorX = 0.5;
        DarkCoverBorder.AnchorY = LightCoverBorder.AnchorY = 0.5;
        DarkBackdropA.AnchorX = DarkBackdropB.AnchorX = 0.5;
        DarkBackdropA.AnchorY = DarkBackdropB.AnchorY = 0.5;
        LightBackdropA.AnchorX = LightBackdropB.AnchorX = 0.5;
        LightBackdropA.AnchorY = LightBackdropB.AnchorY = 0.5;
    }

    void BuildDots(HorizontalStackLayout row, List<DotsView> list)
    {
        var dots = new BoxView[3];
        for (var i = 0; i < dots.Length; i++)
        {
            var dot = new BoxView
            {
                WidthRequest = 10,
                HeightRequest = 10,
                CornerRadius = new CornerRadius(5),
                Color = Colors.White,
                Opacity = 0.28,
                AnchorX = 0.5,
                AnchorY = 0.5,
            };
            dots[i] = dot;
            row.Children.Add(dot);
        }
        list.Add(new DotsView { Row = row, Dots = dots });
    }

    static void AddTap(MauiView view, Action action)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, __) => action();
        view.GestureRecognizers.Add(tap);
    }

    void OnScreenTap() => WakeChrome();

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RootGrid.Opacity = 0;
        _loaded = true;
        State.Start();
        State.CoverChanged += OnCoverChanged;
        MobileLoc.LanguageChanged += OnLanguageChanged;
        SetImmersive(true);
        ApplyResponsiveLayout();
        _designSignature = "";
        _renderedLines = Array.Empty<LrcLine>();
        _renderedState = (LyricsState)(-1);
        ApplyDesign(force: true);
        _themeVersion++;
        DarkLayout.IsVisible = !_centered;
        LightLayout.IsVisible = _centered;
        DarkLayout.Opacity = _centered ? 0 : 1;
        LightLayout.Opacity = _centered ? 1 : 0;
        UpdateTrackInfo(force: true);
        UpdateClock(force: true);
        WakeChrome();
        _chromeHideAt = DateTime.Now.AddSeconds(ChromeIdleSeconds);
        _lastTick = DateTime.MinValue;
        _timer.Start();
        AppForeground.Changed += OnForegroundChanged;
        _ = PlayEntrance();
    }

    void OnForegroundChanged(bool active)
    {
        if (!_loaded) return;
        if (!active)
        {
            _timer.Stop();
            return;
        }
        _lastTick = DateTime.MinValue;
        RequestActiveTick();
        if (!_timer.IsRunning) _timer.Start();
    }

    void RequestActiveTick()
    {
        _tickFast = true;
        if (_timer.Interval != TimeSpan.FromMilliseconds(33))
            _timer.Interval = TimeSpan.FromMilliseconds(33);
        if (_loaded && !_timer.IsRunning) _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _timer.Stop();
        _loaded = false;
        AppForeground.Changed -= OnForegroundChanged;
        State.CoverChanged -= OnCoverChanged;
        MobileLoc.LanguageChanged -= OnLanguageChanged;
        SetImmersive(false);
        ApplyWindowBackground(Color.FromArgb("#08080A"));
        base.OnDisappearing();
    }

    async Task PlayEntrance()
    {
        RootGrid.CancelAnimations();
        RootGrid.Opacity = 0;
        await Task.Delay(60);
        if (!IsLoaded)
        {
            RootGrid.Opacity = 1;
            return;
        }
        await RootGrid.FadeTo(1, 320, Easing.CubicOut);
        RootGrid.Opacity = 1;
    }

    void OnCoverChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_loaded) return;
            _designSignature = "";
            ApplyDesign(force: true);
        });
    }

    void OnLanguageChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_loaded) return;
            _designSignature = "";
            ApplyDesign(force: true);
            UpdateTrackInfo(force: true);
        });
    }

    void OnPageResized()
    {
        if (!_loaded) return;
        var width = Width;
        var height = Height;
        if (width <= 1 || height <= 1) return;
        if (Math.Abs(width - _lastPageWidth) < 0.5 && Math.Abs(height - _lastPageHeight) < 0.5)
            return;
        _lastPageWidth = width;
        _lastPageHeight = height;
        ApplyResponsiveLayout();
        UpdateCoverSizes();
        _designSignature = "";
        ApplyDesign(force: true);
    }

    void OnHostResized()
    {
        if (!_loaded) return;
        var host = _centered ? LightLyricsHost : DarkLyricsHost;
        var width = host.Width;
        if (width > 1 && Math.Abs(width - _hostWidth) > 0.5)
        {
            _designSignature = "";
            ApplyDesign(force: true);
            return;
        }
        if (host.Height > 1 && Math.Abs(host.Height - _hostHeight) > 1)
        {
            _hostHeight = host.Height;
            _resettle = 3;
        }
        _scrollInit = false;
    }

    void SetImmersive(bool enabled)
    {
        var window = Platform.CurrentActivity?.Window;
        if (window is null) return;
        if (enabled)
        {
            window.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                var attributes = window.Attributes!;
                attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                window.Attributes = attributes;
            }
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                window.SetDecorFitsSystemWindows(false);
                var controller = window.InsetsController;
                if (controller is not null)
                {
                    controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                    controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                }
            }
            else
            {
                window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                    SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation |
                    SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutStable);
            }
        }
        else
        {
            window.ClearFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                var attributes = window.Attributes!;
                attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Default;
                window.Attributes = attributes;
            }
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                window.SetDecorFitsSystemWindows(true);
                window.InsetsController?.Show(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
            }
            else
            {
                window.DecorView.SystemUiVisibility = StatusBarVisibility.Visible;
            }
        }
    }

    void Tick()
    {
        if (!_loaded) return;
        var dt = _lastTick == DateTime.MinValue ? 0.033 : (DateTime.Now - _lastTick).TotalSeconds;
        _lastTick = DateTime.Now;
        if (dt <= 0 || dt > 0.5) dt = 0.033;

        if (DateTime.Now >= _nextDesignCheck)
        {
            _nextDesignCheck = DateTime.Now.AddMilliseconds(2000);
            ApplyDesign(force: false);
        }
        if (DateTime.Now >= _nextTrackUpdate)
        {
            _nextTrackUpdate = DateTime.Now.AddMilliseconds(200);
            UpdateTrackInfo(force: false);
        }

        EnsureLyricsState();
        var synced = State.LyricsState == LyricsState.Synced && _renderedLines.Count > 0;
        if (synced && _resettle > 0)
        {
            _resettle--;
            MeasureAll();
            _scrollInit = false;
        }
        if (synced) UpdateLyrics(dt);
        else ResetPerformanceState();

        UpdateCoverMotion(dt);
        UpdateParticles(dt);
        UpdateChromeIdle();

        if (DateTime.Now >= _nextClockUpdate)
        {
            _nextClockUpdate = DateTime.Now.AddMilliseconds(180);
            UpdateClock(force: false);
        }
        UpdateTickRate();
    }

    void UpdateTickRate()
    {
        var design = Design;
        var motion = !design.ReduceMotion && design.Animations.CoverAnimation
            is CoverAnimation.Float or CoverAnimation.Breathe or CoverAnimation.Tilt or CoverAnimation.Parallax;
        var fast = State.IsPlaying || _scrollDiff > 0.3 || _gapLine >= 0 || _gap > 0.005
            || _dots[0].Active || _dots[1].Active
            || motion || ParticleView.IsVisible;
        if (fast == _tickFast) return;
        _tickFast = fast;
        _timer.Interval = TimeSpan.FromMilliseconds(fast ? 33 : 200);
    }

    void EnsureLyricsState()
    {
        var state = State.LyricsState;
        var lines = State.Lyrics.Lines;
        var language = State.Settings.Language;
        if (_renderedState == state &&
            ReferenceEquals(_renderedLines, lines) &&
            _renderedLanguage == language)
            return;
        _renderedState = state;
        _renderedLanguage = language;
        if (state == LyricsState.Synced && lines.Count > 0)
        {
            RebuildLyrics(animate: _designReady);
        }
        else
        {
            _renderedLines = lines;
            _lines.Clear();
            ClearActiveStack();
            ApplyContentMode();
        }
    }

    void ClearActiveStack()
    {
        (_centered ? LightLyricsStack : DarkLyricsStack).Children.Clear();
        _currentLine = int.MinValue;
        _gapLine = -1;
        _gap = 0;
    }

    void ApplyDesign(bool force)
    {
        if (!_loaded && !force) return;
        var design = Design;
        var signature = BuildSignature(design);
        if (!force && _designReady && signature == _designSignature) return;
        _designSignature = signature;

        var centered = design.Layout switch
        {
            DesignLayout.Lateral => false,
            DesignLayout.Fusion or DesignLayout.Minimal or DesignLayout.Karaoke => true,
            _ => State.CoverPalette.Detected,
        };
        var themeChanged = centered != _centered;
        _centered = centered;

        ComputePalette(design);
        ApplyPaletteToUi(design);
        ApplyTypography(design);
        ApplyArtwork(design);
        ApplyControls(design);
        ApplyBackground(design);
        ApplyParticles(design);
        UpdateCover(force: false);

        var crossFade = themeChanged && _designReady;
        if (crossFade)
        {
            var incoming = centered ? (MauiView)LightLayout : DarkLayout;
            var outgoing = centered ? (MauiView)DarkLayout : LightLayout;
            incoming.CancelAnimations();
            incoming.IsVisible = true;
            incoming.Opacity = 0;
            outgoing.IsVisible = true;
        }
        else if (themeChanged || !_designReady)
        {
            _themeVersion++;
            DarkLayout.IsVisible = !_centered;
            LightLayout.IsVisible = _centered;
            DarkLayout.Opacity = _centered ? 0 : 1;
            LightLayout.Opacity = _centered ? 1 : 0;
        }

        RebuildLyrics(animate: _designReady);
        ApplyContentMode();

        if (crossFade)
        {
            var incoming = centered ? (MauiView)LightLayout : DarkLayout;
            var outgoing = centered ? (MauiView)DarkLayout : LightLayout;
            if (outgoing.Opacity < 0.02)
            {
                _themeVersion++;
                incoming.Opacity = 1;
                outgoing.IsVisible = false;
                outgoing.Opacity = 1;
            }
            else
            {
                _ = CrossFadeLayouts(incoming, outgoing, centered, ++_themeVersion);
            }
        }

        _designReady = true;
        UpdateCoverSizes();
    }

    async Task CrossFadeLayouts(MauiView incoming, MauiView outgoing, bool centered, int version)
    {
        await Task.WhenAll(
            incoming.FadeTo(1, (uint)ArtFadeMilliseconds, Easing.CubicOut),
            outgoing.FadeTo(0, (uint)ArtFadeMilliseconds, Easing.CubicIn));
        if (_themeVersion != version || _centered != centered) return;
        incoming.Opacity = 1;
        outgoing.IsVisible = false;
        outgoing.Opacity = 1;
    }

    string BuildSignature(DesignProfile d) => string.Join("|",
        d.Id, d.Layout, d.ReduceMotion,
        d.Palette.BackgroundColor, d.Palette.BackgroundSecondaryColor, d.Palette.GradientAngle,
        d.Palette.TextColor, d.Palette.SecondaryTextColor, d.Palette.AccentColor,
        d.Palette.LyricActiveColor, d.Palette.LyricInactiveColor, d.Palette.AutoContrast, d.Palette.CoverFusion,
        d.Typography.FontFamily, d.Typography.FontWeight, d.Typography.TitleSize,
        d.Typography.ArtistSize, d.Typography.LyricSize, d.Typography.LineHeight,
        d.Lyrics.Alignment, d.Lyrics.WidthPercent, d.Lyrics.Spacing, d.Lyrics.VerticalOffset,
        d.Lyrics.InactiveOpacity, d.Lyrics.ShowBackingVocals, d.Lyrics.IntroDots, d.Lyrics.InterludeSeparation,
        d.Artwork.Visible, d.Artwork.SizePercent, d.Artwork.CornerRadius, d.Artwork.BorderThickness,
        d.Artwork.ShadowOpacity, d.Artwork.Fusion,
        d.Background.Kind, d.Background.Blur, d.Background.Dim, d.Background.CoverScale,
        d.Background.Particles, d.Background.ParticleIntensity, d.Background.BeatReactive,
        d.Animations.WordAnimation, d.Animations.WordDurationMs, d.Animations.WordIntensity,
        d.Animations.VerseTransition, d.Animations.TransitionDurationMs,
        d.Animations.CoverAnimation, d.Animations.CoverIntensity, d.Animations.Easing,
        d.Controls.ShowTransport, d.Controls.ShowProgress, d.Controls.ShowMetadata, d.Controls.ShowTitle,
        d.Controls.CornerRadius, d.Controls.ChromeStyle,
        State.Settings.FontScale, State.Settings.AnimationSoftness,
        _customPalette ? 0 : State.CoverPalette.GetHashCode(),
        State.Settings.Language);

    void ComputePalette(DesignProfile design)
    {
        _customPalette = !design.Palette.AutoContrast ||
                         design.Background.Kind is BackgroundKind.Solid or BackgroundKind.Gradient;
        if (_customPalette)
        {
            _customBackground = ParseColor(design.Palette.BackgroundColor, Color.FromArgb("#0A0A0C"));
            _customBackgroundSecondary = ParseColor(design.Palette.BackgroundSecondaryColor, Color.FromArgb("#17171B"));
            _customText = ParseColor(design.Palette.TextColor, Color.FromArgb("#F5F5F7"));
            _customSecondary = ParseColor(design.Palette.SecondaryTextColor, Color.FromArgb("#A8A8B0"));
            _customAccent = ParseColor(design.Palette.AccentColor, Color.FromArgb("#FF375F"));
            _customLyricActive = ParseColor(design.Palette.LyricActiveColor, Colors.White);
            _customLyricInactive = ParseColor(design.Palette.LyricInactiveColor, Color.FromRgba(255, 255, 255, 0x66));
            if (design.Palette.AutoContrast)
            {
                var light = CoverPalette.Luminance(_customBackground) >= 0.35;
                _customText = light ? Color.FromRgb(0x11, 0x11, 0x14) : Colors.White;
                _customSecondary = CoverPalette.Mix(_customText, _customBackground, light ? 0.32 : 0.24);
                _customLyricActive = _customText;
                _customLyricInactive = WithAlpha(_customText, 0x66);
            }
            _paletteBackground = _customBackground;
            _palettePrimary = _customText;
            _paletteSecondary = _customSecondary;
            _paletteMuted = _customSecondary;
            _paletteAccent = _customAccent;
            _paletteVocal = WithAlpha(_customSecondary, 0xB3);
            _lightSurface = CoverPalette.Luminance(_customBackground) >= 0.35;
        }
        else
        {
            var palette = State.CoverPalette;
            _paletteBackground = palette.Background;
            _palettePrimary = palette.Primary;
            _paletteSecondary = palette.Secondary;
            _paletteMuted = palette.Muted;
            _paletteVocal = palette.Vocal;
            _paletteAccent = palette.Accent;
            _lightSurface = palette.Light;
        }
        _darkText = _customPalette ? _customText : Color.FromArgb("#F5F5F7");
        _darkSecondary = _customPalette ? _customSecondary : Color.FromRgba(255, 255, 255, 0xB8);
        _darkMuted = _customPalette ? _customSecondary : Color.FromRgba(255, 255, 255, 0x66);
        _darkAccent = _customPalette ? _customAccent : Colors.White;
        _lightAccent = _customPalette ? _customAccent : _palettePrimary;

        _blockText = _customPalette ? _customText : _centered ? _palettePrimary : Color.FromArgb("#F5F5F7");
        _wordDim = _customPalette
            ? _customLyricInactive
            : _centered ? WithAlpha(_palettePrimary, 0x59) : WithAlpha(Colors.White, 0x38);
        _wordBright = _customPalette ? _customLyricActive : _centered ? _palettePrimary : Colors.White;
        _vocal = _customPalette
            ? WithAlpha(_customSecondary, 0xB3)
            : _centered ? _paletteVocal : BackgroundVocal;
        _statusText = _customPalette
            ? _customSecondary
            : _centered ? _paletteMuted : Color.FromRgba(255, 255, 255, 0x99);
        _plainText = _customPalette
            ? _customSecondary
            : _centered ? _paletteSecondary : Color.FromRgba(255, 255, 255, 0xDC);
        _wordDuration = Math.Clamp(design.Animations.WordDurationMs, 40, 500);
    }

    void ApplyTypography(DesignProfile design)
    {
        _fontFamily = FontForWeight(design.Typography.FontWeight);
        var scale = Math.Clamp(State.Settings.FontScale, 0.6, 1.8);
        var boost = (_centered ? 1.18 : 1.05) * (_wide ? 0.8 : 1.0);
        _lyricSize = Math.Clamp(
            Math.Clamp(design.Typography.LyricSize, 16, 64) * scale * boost, 12, 120);
        _spacing = Math.Clamp(design.Lyrics.Spacing, 0, 80) * Math.Max(0.85, scale) * (_wide ? 0.8 : 1.0);
        _lineHeight = Math.Clamp(design.Typography.LineHeight, 0.8, 2.0);
        _textAlignment = _centered || design.Lyrics.Alignment == DesignAlignment.Center
            ? MauiTextAlignment.Center
            : design.Lyrics.Alignment == DesignAlignment.Right ? MauiTextAlignment.End : MauiTextAlignment.Start;

        var titleSize = Math.Clamp(design.Typography.TitleSize, 8, 120);
        var artistSize = Math.Clamp(design.Typography.ArtistSize, 8, 120);
        var albumSize = Math.Max(9, artistSize - 3);
        foreach (var title in new[] { DarkTitleLabel, LightTitleLabel })
        {
            title.FontFamily = _fontFamily;
            title.FontSize = titleSize;
        }
        foreach (var artist in new[] { DarkArtistLabel, LightArtistLabel })
        {
            artist.FontFamily = "Inter";
            artist.FontSize = artistSize;
        }
        foreach (var album in new[] { DarkAlbumLabel, LightAlbumLabel })
        {
            album.FontFamily = "Inter";
            album.FontSize = albumSize;
        }
        foreach (var plain in new[] { DarkPlainLabel, LightPlainLabel })
        {
            plain.FontFamily = _fontFamily;
            plain.FontSize = Math.Max(16, _lyricSize * 0.72);
            plain.LineHeight = _lineHeight;
            plain.HorizontalTextAlignment = _textAlignment;
        }
        foreach (var status in new[] { DarkStatusLabel, LightStatusLabel })
        {
            status.FontFamily = _fontFamily;
            status.FontSize = Math.Max(16, Math.Min(24, _lyricSize * 0.68));
            status.HorizontalTextAlignment = MauiTextAlignment.Center;
        }
    }

    void ApplyPaletteToUi(DesignProfile design)
    {
        var background = _paletteBackground;
        var lightText = _palettePrimary;
        var lightSecondary = _paletteSecondary;
        var lightMuted = _paletteMuted;
        var lightChrome = WithAlpha(lightText, _lightSurface ? 0x10 : 0x14);
        var lightTransport = WithAlpha(lightText, 0x12);
        var darkChrome = WithAlpha(_darkText, 0x14);
        var darkTransport = WithAlpha(_darkText, 0x12);

        var pageBackground = _lightSurface ? background : Color.FromArgb("#08080A");
        BackgroundColor = pageBackground;
        ApplyWindowBackground(pageBackground);

        DarkTitleLabel.TextColor = _darkText;
        DarkArtistLabel.TextColor = _darkSecondary;
        DarkAlbumLabel.TextColor = _darkMuted;
        DarkCoverPlaceholder.TextColor = WithAlpha(_darkText, 0x22);

        LightTitleLabel.TextColor = lightText;
        LightArtistLabel.TextColor = lightSecondary;
        LightAlbumLabel.TextColor = lightMuted;
        LightCoverPlaceholder.TextColor = WithAlpha(lightText, 0x2E);
        LightCoverBorder.BackgroundColor = background;
        DarkCoverBorder.BackgroundColor = _customPalette ? _customBackgroundSecondary : Color.FromArgb("#17171B");

        DarkBackButton.BackgroundColor = darkChrome;
        DarkSettingsButton.BackgroundColor = darkChrome;
        DarkBackButton.TextColor = _darkText;
        DarkSettingsButton.TextColor = _darkText;
        LightBackButton.BackgroundColor = lightChrome;
        LightSettingsButton.BackgroundColor = lightChrome;
        LightBackButton.TextColor = lightText;
        LightSettingsButton.TextColor = lightText;

        DarkPreviousButton.BackgroundColor = darkTransport;
        DarkPlayButton.BackgroundColor = darkTransport;
        DarkNextButton.BackgroundColor = darkTransport;
        DarkPreviousButton.TextColor = _darkText;
        DarkPlayButton.TextColor = _darkText;
        DarkNextButton.TextColor = _darkText;
        LightPreviousButton.BackgroundColor = lightTransport;
        LightPlayButton.BackgroundColor = lightTransport;
        LightNextButton.BackgroundColor = lightTransport;
        LightPreviousButton.TextColor = lightText;
        LightPlayButton.TextColor = lightText;
        LightNextButton.TextColor = lightText;

        DarkPositionLabel.TextColor = _darkMuted;
        DarkDurationLabel.TextColor = _darkMuted;
        LightPositionLabel.TextColor = lightMuted;
        LightDurationLabel.TextColor = lightMuted;
        DarkPositionSlider.MinimumTrackColor = _darkAccent;
        DarkPositionSlider.MaximumTrackColor = WithAlpha(_darkText, 0x26);
        DarkPositionSlider.ThumbColor = _darkText;
        LightPositionSlider.MinimumTrackColor = _lightAccent;
        LightPositionSlider.MaximumTrackColor = WithAlpha(lightText, 0x20);
        LightPositionSlider.ThumbColor = _lightSurface ? lightText : Colors.White;

        DarkPlainLabel.TextColor = _plainText;
        LightPlainLabel.TextColor = _plainText;
        DarkStatusLabel.TextColor = _statusText;
        LightStatusLabel.TextColor = _statusText;

        LightScrim.BackgroundColor = WithAlpha(background, 0x8C);
        var dotColor = _customPalette ? _customText : _centered ? _palettePrimary : Colors.White;
        foreach (var dots in _dots)
        {
            foreach (var dot in dots.Dots) dot.Color = dotColor;
            dots.Row.HorizontalOptions = _centered || design.Lyrics.Alignment == DesignAlignment.Center
                ? LayoutOptions.Center
                : design.Lyrics.Alignment == DesignAlignment.Right ? LayoutOptions.End : LayoutOptions.Start;
        }
    }

    void ApplyWindowBackground(Color color)
    {
        try
        {
            var window = Platform.CurrentActivity?.Window;
            if (window is null) return;
            var argb = ((int)Math.Round(color.Alpha * 255) << 24) |
                       ((int)Math.Round(color.Red * 255) << 16) |
                       ((int)Math.Round(color.Green * 255) << 8) |
                       (int)Math.Round(color.Blue * 255);
            window.SetBackgroundDrawable(new AndroidColorDrawable(new AndroidColor(argb)));
            window.SetStatusBarColor(new AndroidColor(argb));
            window.SetNavigationBarColor(new AndroidColor(argb));
            if (Navigation is NavigationPage navigation)
            {
                navigation.BarBackgroundColor = color;
                navigation.BarTextColor = color;
                navigation.BackgroundColor = color;
            }
        }
        catch
        {
        }
    }

    void ApplyArtwork(DesignProfile design)
    {
        var artwork = design.Artwork;
        var hidden = !artwork.Visible || design.Layout is DesignLayout.Minimal or DesignLayout.Karaoke;
        DarkCoverBorder.IsVisible = !hidden;
        LightCoverBorder.IsVisible = !hidden;
        var fusionArtwork = artwork.Fusion ||
                            (_centered && design.Palette.CoverFusion && !_customPalette && State.CoverPalette.Detected);
        var radius = fusionArtwork ? 0 : artwork.CornerRadius;
        var thickness = fusionArtwork ? 2 : artwork.BorderThickness;
        var borderColor = fusionArtwork ? _paletteBackground : _customPalette ? _customAccent : _paletteAccent;
        var shadow = fusionArtwork || artwork.ShadowOpacity <= 0
            ? null
            : new Microsoft.Maui.Controls.Shadow
            {
                Brush = new SolidColorBrush(Colors.Black),
                Opacity = (float)Math.Clamp(artwork.ShadowOpacity, 0, 1),
                Radius = 26,
                Offset = new Point(0, 12),
            };

        foreach (var border in new[] { DarkCoverBorder, LightCoverBorder })
        {
            border.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) };
            border.StrokeThickness = thickness;
            border.Stroke = new SolidColorBrush(borderColor);
            border.Shadow = shadow;
        }
        LightCoverMask.IsVisible = false;
        DarkCoverMask.IsVisible = false;
    }

    void ApplyControls(DesignProfile design)
    {
        var controls = design.Controls;
        DarkTitleLabel.IsVisible = controls.ShowTitle;
        LightTitleLabel.IsVisible = controls.ShowTitle;
        DarkArtistLabel.IsVisible = controls.ShowMetadata;
        DarkAlbumLabel.IsVisible = controls.ShowMetadata;
        LightArtistLabel.IsVisible = controls.ShowMetadata;
        LightAlbumLabel.IsVisible = controls.ShowMetadata;
        DarkTransportButtons.IsVisible = controls.ShowTransport;
        LightTransportButtons.IsVisible = controls.ShowTransport;
        DarkProgressRow.IsVisible = controls.ShowProgress;
        LightProgress.IsVisible = controls.ShowProgress;
        DarkBottom.IsVisible = controls.ShowTransport || controls.ShowProgress;
        LightTransport.IsVisible = controls.ShowTransport || controls.ShowProgress;
        DarkSettingsButton.IsVisible = controls.ChromeStyle != ChromeStyle.Hidden;
        LightSettingsButton.IsVisible = controls.ChromeStyle != ChromeStyle.Hidden;

        var radius = controls.ChromeStyle == ChromeStyle.Rounded
            ? Math.Clamp((int)Math.Round(controls.CornerRadius), 0, 999)
            : 0;
        foreach (var button in new[]
                 {
                     DarkPreviousButton, DarkPlayButton, DarkNextButton,
                     LightPreviousButton, LightPlayButton, LightNextButton,
                 })
        {
            button.CornerRadius = radius;
        }
        if (controls.ChromeStyle == ChromeStyle.Hidden)
        {
            DarkPreviousButton.BackgroundColor = Colors.Transparent;
            DarkPlayButton.BackgroundColor = Colors.Transparent;
            DarkNextButton.BackgroundColor = Colors.Transparent;
            LightPreviousButton.BackgroundColor = Colors.Transparent;
            LightPlayButton.BackgroundColor = Colors.Transparent;
            LightNextButton.BackgroundColor = Colors.Transparent;
        }
        ApplyChromeState();
    }

    void ApplyChromeState()
    {
        var opacity = _chromeVisible ? 1 : 0;
        foreach (var element in new Layout[] { DarkChrome, LightChrome })
        {
            element.CancelAnimations();
            element.Opacity = opacity;
            element.InputTransparent = !_chromeVisible;
            element.CascadeInputTransparent = true;
        }
        foreach (var element in new Layout[] { DarkBottom, LightTransport })
        {
            if (!element.IsVisible) continue;
            element.CancelAnimations();
            element.Opacity = opacity;
            element.InputTransparent = !_chromeVisible;
            element.CascadeInputTransparent = true;
        }
    }

    void ApplyBackground(DesignProfile design)
    {
        var background = design.Background;
        var allowsBackdrop = background.Kind is BackgroundKind.CoverBlur or BackgroundKind.CoverSolid;
        DarkTint.IsVisible = allowsBackdrop;
        LightScrim.IsVisible = allowsBackdrop;

        var backgroundBrush = background.Kind == BackgroundKind.Gradient
            ? MakeGradient(_customBackground, _customBackgroundSecondary, design.Palette.GradientAngle)
            : new SolidColorBrush(background.Kind == BackgroundKind.Solid ? _customBackground : Colors.Black);
        RootGrid.Background = allowsBackdrop
            ? new SolidColorBrush(Colors.Black)
            : backgroundBrush;
        LightLayout.Background = allowsBackdrop
            ? new SolidColorBrush(_paletteBackground)
            : backgroundBrush;

        var scale = (float)Math.Clamp(background.CoverScale, 0.5, 2);
        DarkBackdropA.Scale = DarkBackdropB.Scale = scale;
        LightBackdropA.Scale = LightBackdropB.Scale = scale;
    }

    void ApplyParticles(DesignProfile design)
    {
        _particles.Kind = design.Background.Particles;
        _particles.Intensity = design.Background.ParticleIntensity;
        _particles.ReducedMotion = design.ReduceMotion;
        var enabled = design.Background.Particles != ParticleKind.None && !design.ReduceMotion;
        ParticleView.IsVisible = enabled;
        if (enabled)
        {
            _particles.Start();
            ParticleView.Invalidate();
        }
        else
        {
            _particles.Stop();
        }
    }

    static void SetRows(Grid grid, params GridLength[] heights)
    {
        grid.RowDefinitions.Clear();
        foreach (var height in heights) grid.RowDefinitions.Add(new RowDefinition { Height = height });
    }

    static void SetColumns(Grid grid, params GridLength[] widths)
    {
        grid.ColumnDefinitions.Clear();
        foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
    }

    static void Place(MauiView view, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(view, row);
        Grid.SetColumn(view, column);
        Grid.SetRowSpan(view, rowSpan);
        Grid.SetColumnSpan(view, columnSpan);
    }

    void ApplyResponsiveLayout()
    {
        var width = Width > 1 ? Width : 400;
        var height = Height > 1 ? Height : 800;
        var wide = width > height * 1.08;
        _wide = wide;
        _infoWidth = Math.Clamp(width * 0.30, 220, 320);

        if (wide)
        {
            DarkContent.Padding = new Thickness(72, 54, 36, 18);
            LightContent.Padding = new Thickness(72, 54, 36, 18);
            SetColumns(DarkContent, GridLength.Auto, GridLength.Star);
            SetRows(DarkContent, GridLength.Star, GridLength.Auto);
            Place(DarkHeader, 0, 0);
            Place(DarkLyricsHost, 0, 1, rowSpan: 2);
            Place(DarkBottom, 1, 0);
            SetColumns(LightContent, GridLength.Auto, GridLength.Star);
            SetRows(LightContent, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Star);
            Place(LightCoverBorder, 0, 0);
            Place(LightMeta, 1, 0);
            Place(LightTransport, 2, 0);
            Place(LightLyricsHost, 0, 1, rowSpan: 4);

            SetColumns(DarkHeader, GridLength.Star);
            SetRows(DarkHeader, GridLength.Auto, GridLength.Auto);
            Place(DarkCoverBorder, 0, 0);
            Place(DarkMeta, 1, 0);
            DarkHeader.VerticalOptions = LayoutOptions.Center;
            DarkMeta.VerticalOptions = LayoutOptions.Start;
            DarkMeta.HorizontalOptions = LayoutOptions.Start;
            LightMeta.HorizontalOptions = LayoutOptions.Center;
            LightTransport.HorizontalOptions = LayoutOptions.Center;

            DarkHeader.WidthRequest = _infoWidth;
            DarkMeta.WidthRequest = _infoWidth;
            DarkBottom.WidthRequest = _infoWidth;
            LightMeta.WidthRequest = _infoWidth;
            LightTransport.WidthRequest = _infoWidth;
        }
        else
        {
            DarkContent.Padding = new Thickness(18, 54, 18, 10);
            LightContent.Padding = new Thickness(18, 54, 18, 10);
            SetColumns(DarkContent, GridLength.Star);
            SetRows(DarkContent, GridLength.Auto, GridLength.Star, GridLength.Auto);
            Place(DarkHeader, 0, 0);
            Place(DarkLyricsHost, 1, 0);
            Place(DarkBottom, 2, 0);
            SetColumns(LightContent, GridLength.Star);
            SetRows(LightContent, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Star, GridLength.Auto);
            Place(LightCoverBorder, 0, 0);
            Place(LightMeta, 1, 0);
            Place(LightTransport, 2, 0);
            Place(LightLyricsHost, 3, 0);

            SetColumns(DarkHeader, GridLength.Auto, GridLength.Star);
            SetRows(DarkHeader, GridLength.Star);
            Place(DarkCoverBorder, 0, 0);
            Place(DarkMeta, 0, 1);
            DarkHeader.VerticalOptions = LayoutOptions.Fill;
            DarkMeta.VerticalOptions = LayoutOptions.Center;
            DarkMeta.HorizontalOptions = LayoutOptions.Fill;
            LightMeta.HorizontalOptions = LayoutOptions.Fill;
            LightTransport.HorizontalOptions = LayoutOptions.Fill;

            DarkHeader.WidthRequest = -1;
            DarkMeta.WidthRequest = -1;
            DarkBottom.WidthRequest = -1;
            LightMeta.WidthRequest = -1;
            LightTransport.WidthRequest = -1;
        }
    }

    void UpdateCoverSizes()
    {
        var design = Design;
        var width = Width > 1 ? Width : 400;
        var height = Height > 1 ? Height : 800;
        var percent = Math.Clamp(design.Artwork.SizePercent / 100.0, 0.1, 0.6);
        if (_wide)
        {
            var side = Math.Clamp(height * percent * 1.15, 96, Math.Min(240, height * 0.31));
            LightCoverBorder.WidthRequest = side;
            LightCoverBorder.HeightRequest = side;
            DarkCoverBorder.WidthRequest = side;
            DarkCoverBorder.HeightRequest = side;
            return;
        }
        var lightSide = Math.Clamp(Math.Min(width * percent * 1.55, height * 0.30), 96, 340);
        var darkSide = Math.Clamp(Math.Min(width * percent * 1.15, height * 0.22), 84, 300);
        LightCoverBorder.WidthRequest = lightSide;
        LightCoverBorder.HeightRequest = lightSide;
        DarkCoverBorder.WidthRequest = darkSide;
        DarkCoverBorder.HeightRequest = darkSide;
    }

    void UpdateCover(bool force)
    {
        var cover = State.CoverBytes;
        var coverChanged = !ReferenceEquals(cover, _lastCover);
        if (force || coverChanged)
        {
            _lastCover = cover;
            var source = ImageSourceFrom(cover);
            var darkIn = _darkCoverUsingA ? DarkCoverB : DarkCoverA;
            var darkOut = _darkCoverUsingA ? DarkCoverA : DarkCoverB;
            CrossFadeImage(darkIn, darkOut, source, 1);
            _darkCoverUsingA = !_darkCoverUsingA;
            var lightIn = _lightCoverUsingA ? LightCoverB : LightCoverA;
            var lightOut = _lightCoverUsingA ? LightCoverA : LightCoverB;
            CrossFadeImage(lightIn, lightOut, source, 1);
            _lightCoverUsingA = !_lightCoverUsingA;
            DarkCoverPlaceholder.IsVisible = source is null;
            LightCoverPlaceholder.IsVisible = source is null;
        }

        var kind = Design.Background.Kind;
        var allows = kind is BackgroundKind.CoverBlur or BackgroundKind.CoverSolid;
        var backdrop = !allows
            ? null
            : kind == BackgroundKind.CoverBlur ? State.BackdropBlurBytes : State.BackdropSharpBytes;
        var backdropChanged = !ReferenceEquals(backdrop, _lastBackdrop);
        if (force || backdropChanged)
        {
            _lastBackdrop = backdrop;
            var source = ImageSourceFrom(backdrop);
            var target = allows ? Math.Clamp(Design.Background.Dim, 0, 1) : 0;
            if (source is null)
            {
                FadeOutImage(DarkBackdropA);
                FadeOutImage(DarkBackdropB);
                FadeOutImage(LightBackdropA);
                FadeOutImage(LightBackdropB);
                _darkBackdropCurrent = null;
                _lightBackdropCurrent = null;
            }
            else
            {
                var darkIn = _darkBackdropUsingA ? DarkBackdropB : DarkBackdropA;
                var darkOut = _darkBackdropUsingA ? DarkBackdropA : DarkBackdropB;
                CrossFadeImage(darkIn, darkOut, source, target);
                _darkBackdropCurrent = darkIn;
                _darkBackdropUsingA = !_darkBackdropUsingA;

                var lightIn = _lightBackdropUsingA ? LightBackdropB : LightBackdropA;
                var lightOut = _lightBackdropUsingA ? LightBackdropA : LightBackdropB;
                CrossFadeImage(lightIn, lightOut, source, target);
                _lightBackdropCurrent = lightIn;
                _lightBackdropUsingA = !_lightBackdropUsingA;
            }
        }
        else
        {
            var target = allows ? Math.Clamp(Design.Background.Dim, 0, 1) : 0;
            if (_darkBackdropCurrent is not null && Math.Abs(_darkBackdropCurrent.Opacity - target) > 0.001)
                _darkBackdropCurrent.Opacity = target;
            if (_lightBackdropCurrent is not null && Math.Abs(_lightBackdropCurrent.Opacity - target) > 0.001)
                _lightBackdropCurrent.Opacity = target;
        }
    }

    static ImageSource? ImageSourceFrom(byte[]? bytes) =>
        bytes is { Length: > 0 } ? ImageSource.FromStream(() => new MemoryStream(bytes)) : null;

    static void CrossFadeImage(Image incoming, Image outgoing, ImageSource? source, double target)
    {
        incoming.CancelAnimations();
        incoming.Source = source;
        incoming.Opacity = 0;
        outgoing.CancelAnimations();
        _ = Task.WhenAll(
            incoming.FadeTo(target, (uint)ArtFadeMilliseconds, Easing.CubicOut),
            outgoing.FadeTo(0, (uint)ArtFadeMilliseconds, Easing.CubicIn));
    }

    static void FadeOutImage(Image image)
    {
        image.CancelAnimations();
        if (image.Opacity <= 0.001) return;
        _ = image.FadeTo(0, (uint)ArtFadeMilliseconds, Easing.CubicIn);
    }

    void RebuildLyrics(bool animate)
    {
        var stack = _centered ? LightLyricsStack : DarkLyricsStack;
        stack.Children.Clear();
        _lines.Clear();
        _currentLine = int.MinValue;
        _gapLine = -1;
        _gap = 0;
        _renderedLines = State.Lyrics.Lines;
        _renderedLanguage = State.Settings.Language;
        _renderedState = State.LyricsState;
        _hostWidth = (_centered ? LightLyricsHost.Width : DarkLyricsHost.Width);
        if (!(_hostWidth > 1)) _hostWidth = Width > 1 ? Width - 36 : 360;
        _hostHeight = (_centered ? LightLyricsHost.Height : DarkLyricsHost.Height);
        if (!(_hostHeight > 1)) _hostHeight = Height > 1 ? Height * 0.42 : 400;
        _contentWidth = ComputeContentWidth(_hostWidth);
        stack.WidthRequest = _contentWidth;
        stack.HorizontalOptions = _centered ? LayoutOptions.Center : LayoutOptions.Start;
        stack.Spacing = _spacing;

        foreach (var line in _renderedLines)
        {
            var root = new VerticalStackLayout
            {
                Spacing = 2,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = _contentWidth,
            };
            var main = MakePlainMain(line);
            root.Children.Add(main);
            var backing = MakePlainBacking(line);
            if (backing is not null) root.Children.Add(backing);
            var index = _lines.Count;
            AddTap(root, () => SeekToLine(index));
            stack.Children.Add(root);
            _lines.Add(new LineView
            {
                Line = line,
                Index = index,
                Root = root,
                Main = main,
                Backing = backing,
            });
        }

        MeasureAll();
        var pos = State.Position;
        var intro = Design.Lyrics.IntroDots && IntroDotsActive(_renderedLines, pos);
        var current = intro ? -1 : State.CurrentLineIndex(pos);
        ApplyCurrentLine(current, pos, immediate: true);
        _scrollInit = false;
        _scrollOffset = CenterTarget();
        ApplyScroll();
        ApplyLineOpacities(-1, intro);
        ApplyDots(false, pos, 0, 0, intro ? DotsShiftForIntro() : 0);
        _resettle = 3;
        if (animate)
        {
            var layer = _centered ? LightVerseLayer : DarkVerseLayer;
            layer.CancelAnimations();
            layer.Opacity = 0;
            _ = layer.FadeTo(1, 300, Easing.CubicOut);
        }
        UpdateWordPaint(pos);
        ApplyContentMode();
    }

    double ComputeContentWidth(double hostWidth)
    {
        if (!_centered) return Math.Max(120, hostWidth);
        var percent = Math.Clamp(Design.Lyrics.WidthPercent, 10, 100) / 100.0;
        var wanted = hostWidth * percent;
        return Math.Clamp(wanted, Math.Min(240, hostWidth), hostWidth);
    }

    Label MakePlainMain(LrcLine line)
    {
        var label = new Label
        {
            Text = line.Text,
            FontSize = _lyricSize,
            FontFamily = _fontFamily,
            FontAutoScalingEnabled = false,
            TextColor = _blockText,
            HorizontalTextAlignment = _textAlignment,
            LineBreakMode = LineBreakMode.WordWrap,
            LineHeight = _lineHeight,
            HorizontalOptions = LayoutOptions.Fill,
        };
        return label;
    }

    Label? MakePlainBacking(LrcLine line)
    {
        if (!Design.Lyrics.ShowBackingVocals || line.Background.Length == 0) return null;
        return new Label
        {
            Text = line.Background,
            FontSize = Math.Max(11, _lyricSize * 0.56),
            FontFamily = "Inter",
            FontAutoScalingEnabled = false,
            TextColor = _vocal,
            HorizontalTextAlignment = _textAlignment,
            LineBreakMode = LineBreakMode.WordWrap,
            HorizontalOptions = LayoutOptions.Fill,
        };
    }

    FlexLayout MakeWordLayout(IReadOnlyList<LrcWord> words, Color dim, Color bright,
        double position, bool animateWords, out List<WordView> views)
    {
        var row = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            JustifyContent = _centered ? FlexJustify.Center
                : _textAlignment == MauiTextAlignment.End ? FlexJustify.End
                : _textAlignment == MauiTextAlignment.Start ? FlexJustify.Start : FlexJustify.Center,
            AlignItems = FlexAlignItems.Center,
            HorizontalOptions = LayoutOptions.Fill,
        };
        views = new List<WordView>(words.Count);
        foreach (var word in words)
        {
            var text = word.Text.TrimEnd();
            if (text.Length == 0) text = " ";
            var label = new Label
            {
                Text = text,
                FontSize = _lyricSize,
                FontFamily = _fontFamily,
                FontAutoScalingEnabled = false,
                TextColor = dim,
                LineBreakMode = LineBreakMode.NoWrap,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalOptions = LayoutOptions.Center,
            };
            var view = new WordView { Label = label, Time = word.Time };
            if (!animateWords && word.Time <= position + 0.001)
            {
                view.Lit = true;
                label.TextColor = bright;
            }
            row.Children.Add(label);
            views.Add(view);
        }
        return row;
    }

    void ApplyCurrentLine(int index, double position, bool immediate)
    {
        if (index == _currentLine) return;
        var previous = _currentLine;
        _currentLine = index;

        if (previous >= 0 && previous < _lines.Count)
        {
            SetLinePlain(_lines[previous]);
            RemeasureLine(previous);
        }
        if (index >= 0 && index < _lines.Count)
        {
            SetLineActive(_lines[index], position, animateWords: !immediate);
            RemeasureLine(index);
        }
        UpdateGapTranslations();
        if (!immediate && (previous >= 0 || index >= 0)) AnimateVerseTransition();
    }

    void SetLinePlain(LineView view)
    {
        if (view.Main is not Label)
        {
            var label = MakePlainMain(view.Line);
            ReplaceChild(view.Root, view.Main, label);
            view.Main = label;
        }
        view.Words.Clear();
        if (view.Backing is FlexLayout)
        {
            var backing = MakePlainBacking(view.Line);
            if (backing is not null)
            {
                ReplaceChild(view.Root, view.Backing, backing);
                view.Backing = backing;
            }
        }
        view.BackWords.Clear();
    }

    void SetLineActive(LineView view, double position, bool animateWords)
    {
        var line = view.Line;
        if (line.Words is { Count: > 0 })
        {
            var row = MakeWordLayout(line.Words, _wordDim, _wordBright, position, animateWords, out var words);
            ReplaceChild(view.Root, view.Main, row);
            view.Main = row;
            view.Words = words;
        }
        else
        {
            view.Words.Clear();
            if (view.Main is not Label)
            {
                var label = MakePlainMain(line);
                ReplaceChild(view.Root, view.Main, label);
                view.Main = label;
            }
        }

        if (!Design.Lyrics.ShowBackingVocals)
        {
            view.BackWords.Clear();
            if (view.Backing is not null)
            {
                view.Root.Children.Remove(view.Backing);
                view.Backing = null;
            }
        }
        else if (line.BackgroundWords is { Count: > 0 })
        {
            var row = MakeWordLayout(line.BackgroundWords, _vocal, Colors.White, position, animateWords, out var words);
            if (view.Backing is null)
            {
                view.Backing = row;
                view.Root.Children.Add(row);
            }
            else
            {
                ReplaceChild(view.Root, view.Backing, row);
                view.Backing = row;
            }
            view.BackWords = words;
        }
        else
        {
            view.BackWords.Clear();
            if (view.Backing is FlexLayout)
            {
                var backing = MakePlainBacking(line);
                if (backing is not null)
                {
                    ReplaceChild(view.Root, view.Backing, backing);
                    view.Backing = backing;
                }
            }
        }
    }

    static void ReplaceChild(VerticalStackLayout root, MauiView? oldView, MauiView newView)
    {
        if (oldView is null)
        {
            root.Children.Add(newView);
            return;
        }
        var index = root.Children.IndexOf(oldView);
        if (index < 0)
        {
            root.Children.Add(newView);
            return;
        }
        root.Children.RemoveAt(index);
        root.Children.Insert(index, newView);
    }

    void MeasureAll()
    {
        foreach (var view in _lines)
        {
            view.Root.WidthRequest = _contentWidth;
            view.Height = Math.Max(1, view.Root.Measure(_contentWidth, double.PositiveInfinity).Height);
        }
        UpdateStackHeight();
        UpdateGapTranslations();
    }

    void UpdateStackHeight()
    {
        var total = InterludeGap;
        foreach (var view in _lines) total += view.Height;
        if (_lines.Count > 1) total += _spacing * (_lines.Count - 1);
        var stack = _centered ? LightLyricsStack : DarkLyricsStack;
        if (Math.Abs(stack.HeightRequest - total) > 0.5) stack.HeightRequest = total;
        var scroll = _centered ? LightScrollLayer : DarkScrollLayer;
        if (Math.Abs(scroll.HeightRequest - total) > 0.5) scroll.HeightRequest = total;
    }

    void RemeasureLine(int index)
    {
        if (index < 0 || index >= _lines.Count) return;
        var view = _lines[index];
        view.Root.WidthRequest = _contentWidth;
        view.Height = Math.Max(1, view.Root.Measure(_contentWidth, double.PositiveInfinity).Height);
        UpdateStackHeight();
    }

    void UpdateLyrics(double dt)
    {
        var design = Design;
        var position = State.Position;
        var intro = design.Lyrics.IntroDots && IntroDotsActive(_renderedLines, position);
        var interlude = false;
        var gapLine = -1;
        double gapStart = 0, gapEnd = 0;
        if (!intro && design.Lyrics.InterludeSeparation)
            interlude = TryGetInterlude(position, out gapLine, out gapStart, out gapEnd);

        AdvanceGap(interlude, gapLine, dt);

        var index = intro ? -1 : State.CurrentLineIndex(position);
        if (index != _currentLine) ApplyCurrentLine(index, position, immediate: false);
        if (!intro) UpdateWordPaint(position);

        if (_hostHeight > 1)
        {
            var target = CenterTarget();
            if (!_scrollInit)
            {
                _scrollOffset = target;
                _scrollInit = true;
            }
            var diff = target - _scrollOffset;
            var rate = Math.Max(5, 18 - Math.Clamp(State.Settings.AnimationSoftness, 0, 100) * 0.12);
            _scrollOffset += Math.Abs(diff) < 0.3 ? diff : diff * (1 - Math.Exp(-rate * dt));
            _scrollDiff = Math.Abs(target - _scrollOffset);
            ApplyScroll();
        }

        ApplyLineOpacities(index, intro);

        if (intro)
        {
            ApplyDots(true, position, 0, _renderedLines[0].Time, DotsShiftForIntro());
        }
        else if (_gapLine >= 0)
        {
            var gapTop = TopOf(_gapLine) + _lines[_gapLine].Height + _scrollOffset;
            var gapCenter = gapTop + (_spacing + InterludeGap * _gap) / 2;
            ApplyDots(true, position, gapStart, gapEnd, gapCenter - _hostHeight / 2);
        }
        else
        {
            ApplyDots(false, position, 0, 0, 0);
        }
    }

    void ResetPerformanceState()
    {
        _scrollDiff = 0;
        if (_gapLine >= 0)
        {
            _gapLine = -1;
            _gap = 0;
            foreach (var view in _lines) view.Root.TranslationY = 0;
        }
        ApplyDots(false, 0, 0, 0, 0);
    }

    void ApplyScroll()
    {
        var layer = _centered ? LightScrollLayer : DarkScrollLayer;
        layer.TranslationY = _scrollOffset;
    }

    void ApplyLineOpacities(int index, bool intro)
    {
        if (_lines.Count == 0 || _hostHeight <= 1) return;
        var inactive = Math.Clamp(Design.Lyrics.InactiveOpacity, 0, 1);
        var y = 0.0;
        for (var i = 0; i < _lines.Count; i++)
        {
            var view = _lines[i];
            var top = y + GapShift(i) + _scrollOffset;
            var height = view.Height;
            var center = top + height / 2;
            var distance = Math.Abs(center - _hostHeight / 2);
            var fade = Math.Clamp(1 - distance / (_hostHeight * 0.42), 0, 1);
            var opacity = intro ? inactive * fade : i == index ? fade : inactive * fade;
            if (Math.Abs(view.Root.Opacity - opacity) > 0.003) view.Root.Opacity = opacity;
            var interactive = !intro && opacity > 0.05;
            if (view.Root.InputTransparent != !interactive) view.Root.InputTransparent = !interactive;
            y += height + _spacing;
        }
    }

    void UpdateWordPaint(double position)
    {
        if (_currentLine < 0 || _currentLine >= _lines.Count) return;
        var view = _lines[_currentLine];
        PaintWords(view.Words, position, _wordDim, _wordBright, motion: true);
        PaintWords(view.BackWords, position, _vocal, Colors.White, motion: false);
    }

    void PaintWords(List<WordView> words, double position, Color dim, Color bright, bool motion)
    {
        foreach (var word in words)
        {
            var lit = word.Time <= position + 0.001;
            if (lit == word.Lit) continue;
            word.Lit = lit;
            var label = word.Label;
            var from = label.TextColor ?? dim;
            label.CancelAnimations();
            TweenColor(label, from, lit ? bright : dim, (uint)Math.Max(40, _wordDuration));
            if (lit)
            {
                if (motion) PlayWordMotion(label);
            }
            else
            {
                label.Scale = 1;
                label.TranslationY = 0;
                label.Opacity = 1;
            }
        }
    }

    static void TweenColor(Label label, Color from, Color to, uint duration)
    {
        if (from == to)
        {
            label.TextColor = to;
            return;
        }
        var animation = new Animation(t =>
        {
            label.TextColor = Color.FromRgba(
                (float)(from.Red + (to.Red - from.Red) * t),
                (float)(from.Green + (to.Green - from.Green) * t),
                (float)(from.Blue + (to.Blue - from.Blue) * t),
                (float)(from.Alpha + (to.Alpha - from.Alpha) * t));
        }, 0, 1);
        animation.Commit(label, "wordColor", 16, duration, Easing.CubicOut);
    }

    void PlayWordMotion(Label label)
    {
        var design = Design;
        if (design.ReduceMotion) return;
        var style = design.Animations.WordAnimation;
        if (style == LyricAnimation.Fade) return;
        var strength = Math.Clamp(design.Animations.WordIntensity, 0, 1);
        if (strength <= 0.01) return;
        var duration = (uint)Math.Clamp(_wordDuration * 1.7, 110, 420);
        switch (style)
        {
            case LyricAnimation.Slide:
                label.TranslationY = 9 * strength;
                _ = label.TranslateTo(0, 0, duration, Easing.CubicOut);
                break;
            case LyricAnimation.Zoom:
                label.Scale = 1 - 0.20 * strength;
                _ = label.ScaleTo(1, duration, Easing.CubicOut);
                break;
            case LyricAnimation.Bounce:
                _ = BounceWord(label, strength, duration);
                break;
            case LyricAnimation.Rise:
                label.TranslationY = 14 * strength;
                _ = label.TranslateTo(0, 0, duration, Easing.CubicOut);
                break;
            case LyricAnimation.Blur:
                label.Opacity = 0.35;
                label.Scale = 1 + 0.04 * strength;
                _ = Task.WhenAll(
                    label.FadeTo(1, duration, Easing.CubicOut),
                    label.ScaleTo(1, duration, Easing.CubicOut));
                break;
            case LyricAnimation.Elastic:
                label.Scale = 1 - 0.28 * strength;
                _ = label.ScaleTo(1, (uint)(duration * 1.2), Easing.SpringOut);
                break;
            case LyricAnimation.Pulse:
                _ = PulseWord(label, strength, duration);
                break;
            case LyricAnimation.Wave:
                _ = WaveWord(label, strength, duration);
                break;
        }
    }

    static async Task BounceWord(Label label, double strength, uint duration)
    {
        var up = (uint)Math.Max(20, duration * 0.35);
        var down = (uint)Math.Max(30, duration - up);
        label.TranslationY = 0;
        label.Scale = 1;
        await Task.WhenAll(
            label.TranslateTo(0, -6 * strength, up, Easing.CubicOut),
            label.ScaleTo(1 + 0.05 * strength, up, Easing.CubicOut));
        await Task.WhenAll(
            label.TranslateTo(0, 0, down, Easing.BounceOut),
            label.ScaleTo(1, down, Easing.CubicOut));
    }

    static async Task PulseWord(Label label, double strength, uint duration)
    {
        var up = (uint)Math.Max(30, duration * 0.45);
        var down = (uint)Math.Max(40, duration - up);
        label.Scale = 1 - 0.18 * strength;
        await label.ScaleTo(1 + 0.12 * strength, up, Easing.CubicOut);
        await label.ScaleTo(1, down, Easing.CubicOut);
    }

    static async Task WaveWord(Label label, double strength, uint duration)
    {
        var first = (uint)Math.Max(20, duration * 0.30);
        var second = (uint)Math.Max(20, duration * 0.35);
        var third = (uint)Math.Max(20, duration - first - second);
        label.TranslationY = 0;
        await label.TranslateTo(0, -7 * strength, first, Easing.SinInOut);
        await label.TranslateTo(0, 4 * strength, second, Easing.SinInOut);
        await label.TranslateTo(0, 0, third, Easing.SinInOut);
    }

    void AnimateVerseTransition()
    {
        var layer = _centered ? LightVerseLayer : DarkVerseLayer;
        var design = Design;
        layer.CancelAnimations();
        layer.TranslationY = 0;
        layer.Scale = 1;
        layer.Opacity = 1;
        if (design.ReduceMotion || design.Animations.VerseTransition == VerseTransition.None) return;

        var duration = (uint)Math.Clamp(design.Animations.TransitionDurationMs, 80, 1200);
        var easing = EasingFor(design.Animations.Easing);
        switch (design.Animations.VerseTransition)
        {
            case VerseTransition.Slide:
                layer.TranslationY = 10;
                _ = layer.TranslateTo(0, 0, duration, easing);
                break;
            case VerseTransition.Roll:
                layer.TranslationY = 24;
                _ = layer.TranslateTo(0, 0, duration, easing);
                break;
            case VerseTransition.Scale:
                layer.Scale = 0.965;
                _ = layer.ScaleTo(1, duration, easing);
                break;
            case VerseTransition.Fade:
                layer.Opacity = 0.86;
                _ = layer.FadeTo(1, duration, easing);
                break;
        }
    }

    static Easing EasingFor(EasingKind kind) => kind switch
    {
        EasingKind.EaseInOut => Easing.CubicInOut,
        EasingKind.Linear => Easing.Linear,
        EasingKind.Spring => Easing.SpringOut,
        EasingKind.Bounce => Easing.BounceOut,
        _ => Easing.CubicOut,
    };

    double TopOf(int index)
    {
        if (index < 0 || index >= _lines.Count) return 0;
        var y = 0.0;
        for (var i = 0; i < index; i++) y += _lines[i].Height + _spacing;
        return y + GapShift(index);
    }

    double GapShift(int index) =>
        _gapLine >= 0 && index > _gapLine ? InterludeGap * _gap : 0;

    double CenterTarget()
    {
        var index = _currentLine;
        if (index < 0 && _lines.Count > 0) index = 0;
        if (index < 0 || index >= _lines.Count) return 0;
        return _hostHeight / 2 - (TopOf(index) + _lines[index].Height / 2) +
               Design.Lyrics.VerticalOffset;
    }

    void AdvanceGap(bool active, int gapLine, double dt)
    {
        if (active)
        {
            _gapLine = gapLine;
        }
        else if (_gapLine < 0)
        {
            return;
        }
        var target = active ? 1.0 : 0.0;
        var next = _gap + (target - _gap) * (1 - Math.Exp(-10 * dt));
        if (!active && next < 0.005)
        {
            next = 0;
            _gapLine = -1;
        }
        if (Math.Abs(next - _gap) < 0.0005) return;
        _gap = next;
        UpdateGapTranslations();
    }

    void UpdateGapTranslations()
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var shift = (float)GapShift(i);
            if (Math.Abs(_lines[i].Root.TranslationY - shift) > 0.01)
                _lines[i].Root.TranslationY = shift;
        }
    }

    double LineFinishedAt(int index)
    {
        if (index < 0 || index >= _renderedLines.Count) return 0;
        var line = _renderedLines[index];
        var finish = line.Time + 4.0;
        if (line.Words is { Count: > 0 }) finish = Math.Max(line.Time + 1.0, line.Words[^1].Time + 0.6);
        if (index + 1 < _renderedLines.Count) finish = Math.Min(finish, _renderedLines[index + 1].Time);
        return finish;
    }

    bool IsInterludeGap(int index)
    {
        if (index < 0 || index + 1 >= _renderedLines.Count) return false;
        return _renderedLines[index + 1].Time - LineFinishedAt(index) > 12.0;
    }

    bool TryGetInterlude(double position, out int current, out double start, out double end)
    {
        current = State.CurrentLineIndex(position);
        start = 0;
        end = 0;
        if (current < 0 || current + 1 >= _renderedLines.Count || !IsInterludeGap(current))
            return false;
        start = LineFinishedAt(current);
        end = _renderedLines[current + 1].Time;
        return position >= start && position < end;
    }

    static bool IntroDotsActive(IReadOnlyList<LrcLine> lines, double position) =>
        lines.Count > 0 && lines[0].Time >= 7 && position >= 0 && position < lines[0].Time;

    double DotsShiftForIntro()
    {
        var firstTop = _lines.Count > 0 ? TopOf(0) + _scrollOffset : 0;
        return firstTop - _hostHeight / 2 - 34;
    }

    void ApplyDots(bool active, double position, double start, double end, double shift)
    {
        var dots = _centered ? _dots[1] : _dots[0];
        var other = _centered ? _dots[0] : _dots[1];
        HideDots(other);
        dots.Active = active;
        if (!active)
        {
            HideDots(dots);
            return;
        }

        var row = dots.Row;
        row.TranslationY = shift;
        row.CancelAnimations();
        if (!row.IsVisible)
        {
            row.IsVisible = true;
            row.Opacity = 0;
        }
        if (row.Opacity < 0.999) _ = row.FadeTo(1, 200, Easing.CubicOut);

        var progress = end > start ? Math.Clamp((position - start) / (end - start), 0, 1) : 1;
        var breathTime = Math.Max(0, position - start);
        var breath = 1 + 0.09 * Math.Sin(2 * Math.PI * breathTime / 2.4);
        var remaining = Math.Max(0, end - position);
        var swell = end > start ? Math.Clamp(1 - remaining / 1.3, 0, 1) : 0;
        double entryScale;
        double entryFade;
        if (swell < 0.45)
        {
            entryScale = 1 + 0.65 * SmoothStep(swell / 0.45);
            entryFade = 1;
        }
        else
        {
            var t = SmoothStep((swell - 0.45) / 0.55);
            entryScale = 1.65 * (1 - t);
            entryFade = 1 - t;
        }
        for (var i = 0; i < dots.Dots.Length; i++)
        {
            var lit = SmoothStep((progress - (DotLightAt[i] - 0.08)) / 0.10);
            var opacity = (0.28 + 0.72 * lit) * entryFade;
            var scale = breath * entryScale;
            if (Math.Abs(dots.Dots[i].Opacity - opacity) > 0.001) dots.Dots[i].Opacity = opacity;
            if (Math.Abs(dots.Dots[i].Scale - scale) > 0.001) dots.Dots[i].Scale = scale;
        }
    }

    void HideDots(DotsView dots)
    {
        dots.Active = false;
        if (!dots.Row.IsVisible || dots.Fading) return;
        dots.Fading = true;
        _ = FadeDotsOut(dots);
    }

    async Task FadeDotsOut(DotsView dots)
    {
        await dots.Row.FadeTo(0, 240, Easing.CubicOut);
        if (!dots.Active)
        {
            dots.Row.IsVisible = false;
            dots.Row.Opacity = 1;
        }
        dots.Fading = false;
    }

    static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    void ApplyContentMode()
    {
        var state = State.LyricsState;
        var synced = state == LyricsState.Synced && _renderedLines.Count > 0;
        var plain = state == LyricsState.Plain;
        var status = !synced && !plain;
        if (_centered)
        {
            LightVerseLayer.IsVisible = synced;
            LightPlainScroll.IsVisible = plain;
            LightStatusLabel.IsVisible = status;
        }
        else
        {
            DarkVerseLayer.IsVisible = synced;
            DarkPlainScroll.IsVisible = plain;
            DarkStatusLabel.IsVisible = status;
        }
        if (plain)
        {
            var text = State.Lyrics.PlainLyrics ?? "";
            if (DarkPlainLabel.Text != text) DarkPlainLabel.Text = text;
            if (LightPlainLabel.Text != text) LightPlainLabel.Text = text;
            var scroll = _centered ? LightPlainScroll : DarkPlainScroll;
            scroll.Opacity = 0;
            _ = scroll.FadeTo(1, 280, Easing.CubicOut);
            try { _ = scroll.ScrollToAsync(0, 0, false); } catch { }
        }
        if (status)
        {
            var message = State.LyricsState switch
            {
                LyricsState.Searching => MobileLoc.T("StatusSearchingLyrics"),
                LyricsState.Instrumental => MobileLoc.T("StatusInstrumentalFull"),
                LyricsState.NotFound => MobileLoc.T("StatusLyricsNotFoundFull"),
                LyricsState.Error => MobileLoc.T("StatusLyricsErrorFull"),
                _ => MobileLoc.T("StatusNoPlaybackFull"),
            };
            DarkStatusLabel.Text = message;
            LightStatusLabel.Text = message;
        }
    }

    void UpdateChromeIdle()
    {
        if (!_chromeVisible || _seeking) return;
        if (DateTime.Now < _chromeHideAt) return;
        SetChromeVisible(false, true);
    }

    void WakeChrome()
    {
        _chromeHideAt = DateTime.Now.AddSeconds(ChromeIdleSeconds);
        if (!_chromeVisible) SetChromeVisible(true, true);
        else
        {
            _chromeHideAt = DateTime.Now.AddSeconds(ChromeIdleSeconds);
        }
    }

    void SetChromeVisible(bool visible, bool animate)
    {
        if (_chromeVisible == visible) return;
        _chromeVisible = visible;
        var chrome = new Layout[] { DarkChrome, LightChrome };
        var bottoms = new Layout[] { DarkBottom, LightTransport };
        foreach (var element in chrome)
        {
            element.InputTransparent = !visible;
            element.CascadeInputTransparent = true;
            element.CancelAnimations();
            if (!animate)
            {
                element.Opacity = visible ? 1 : 0;
                continue;
            }
            _ = element.FadeTo(visible ? 1 : 0, visible ? 220u : 320u,
                visible ? Easing.CubicOut : Easing.CubicIn);
        }
        foreach (var element in bottoms)
        {
            if (!element.IsVisible) continue;
            element.InputTransparent = !visible;
            element.CascadeInputTransparent = true;
            element.CancelAnimations();
            if (!animate)
            {
                element.Opacity = visible ? 1 : 0;
                continue;
            }
            _ = element.FadeTo(visible ? 1 : 0, visible ? 220u : 320u,
                visible ? Easing.CubicOut : Easing.CubicIn);
        }
    }

    void UpdateCoverMotion(double dt)
    {
        var design = Design;
        var animation = design.Animations.CoverAnimation;
        var reset = design.ReduceMotion || animation == CoverAnimation.None;
        if (reset)
        {
            SetCoverMotion(0, 0, 0, 1);
            return;
        }
        _coverMotionTime += Math.Clamp(dt, 0, 0.1);
        var intensity = Math.Clamp(design.Animations.CoverIntensity, 0, 2);
        var wave = Math.Sin(_coverMotionTime * 1.4);
        var x = 0.0;
        var y = 0.0;
        var rotation = 0.0;
        var scale = 1.0;
        switch (animation)
        {
            case CoverAnimation.Float:
                y = wave * 5 * intensity;
                break;
            case CoverAnimation.Breathe:
                scale = 1 + wave * 0.018 * intensity;
                break;
            case CoverAnimation.Tilt:
                rotation = wave * 2.5 * intensity;
                break;
            case CoverAnimation.Parallax:
                x = wave * 7 * intensity;
                break;
        }
        SetCoverMotion(x, y, rotation, scale);
    }

    void SetCoverMotion(double x, double y, double rotation, double scale)
    {
        DarkCoverBorder.TranslationX = LightCoverBorder.TranslationX = x;
        DarkCoverBorder.TranslationY = LightCoverBorder.TranslationY = y;
        DarkCoverBorder.Rotation = LightCoverBorder.Rotation = rotation;
        DarkCoverBorder.Scale = LightCoverBorder.Scale = scale;
    }

    void UpdateParticles(double dt)
    {
        if (!ParticleView.IsVisible) return;
        _particles.Update(dt, 0);
        ParticleView.Invalidate();
    }

    void UpdateTrackInfo(bool force)
    {
        var track = State.Track;
        var title = track?.Title ?? MobileLoc.T("NoPlayback");
        var artist = track?.Artist ?? "";
        var album = string.IsNullOrWhiteSpace(track?.Album) ? "" : track!.Album;
        if (force || DarkTitleLabel.Text != title) DarkTitleLabel.Text = title;
        if (force || LightTitleLabel.Text != title) LightTitleLabel.Text = title;
        if (force || DarkArtistLabel.Text != artist) DarkArtistLabel.Text = artist;
        if (force || LightArtistLabel.Text != artist) LightArtistLabel.Text = artist;
        if (force || DarkAlbumLabel.Text != album) DarkAlbumLabel.Text = album;
        if (force || LightAlbumLabel.Text != album) LightAlbumLabel.Text = album;
        var glyph = State.IsPlaying ? "Ⅱ" : "▶";
        if (DarkPlayButton.Text != glyph) DarkPlayButton.Text = glyph;
        if (LightPlayButton.Text != glyph) LightPlayButton.Text = glyph;
    }

    void UpdateClock(bool force)
    {
        var position = State.Lyrics.GetDisplayPosition();
        var duration = State.Lyrics.Track?.DurationSeconds ??
                       State.Session.Current?.DurationSeconds ??
                       State.Track?.DurationSeconds ?? 0;
        var maximum = Math.Max(1, duration);
        if (Math.Abs(maximum - _lastDuration) > 0.5 || DarkPositionSlider.Maximum != maximum)
        {
            _lastDuration = maximum;
            DarkPositionSlider.Maximum = maximum;
            LightPositionSlider.Maximum = maximum;
        }
        if (!_seeking)
        {
            var clamped = Math.Clamp(position, 0, maximum);
            if (Math.Abs(DarkPositionSlider.Value - clamped) > 0.05)
            {
                DarkPositionSlider.Value = clamped;
                LightPositionSlider.Value = clamped;
            }
        }
        var posText = Clock(position);
        var durText = duration > 0 ? Clock(duration) : "--:--";
        if (force || DarkPositionLabel.Text != posText)
        {
            DarkPositionLabel.Text = posText;
            LightPositionLabel.Text = posText;
        }
        if (force || DarkDurationLabel.Text != durText)
        {
            DarkDurationLabel.Text = durText;
            LightDurationLabel.Text = durText;
        }
    }

    void SeekToLine(int index)
    {
        var lines = State.Lyrics.Lines;
        if (index < 0 || index >= lines.Count) return;
        State.SeekToLine(lines[index].Time);
        WakeChrome();
        RequestActiveTick();
    }

    void OnSeekStart(object? sender, EventArgs e)
    {
        _seeking = true;
        WakeChrome();
    }

    void OnSeekEnd(object? sender, EventArgs e)
    {
        var value = ReferenceEquals(sender, LightPositionSlider)
            ? LightPositionSlider.Value
            : DarkPositionSlider.Value;
        State.SeekTo(value);
        _seeking = false;
        WakeChrome();
        RequestActiveTick();
    }

    async void OnBack(object? sender, EventArgs e)
    {
        WakeChrome();
        await Navigation.PopAsync();
    }

    async void OnSettings(object? sender, EventArgs e)
    {
        WakeChrome();
        await Navigation.PushAsync(new SettingsPage());
    }

    void OnPlayPause(object? sender, EventArgs e)
    {
        WakeChrome();
        State.TogglePlayPause();
        UpdateTrackInfo(force: true);
        RequestActiveTick();
    }

    void OnPrevious(object? sender, EventArgs e)
    {
        WakeChrome();
        State.Previous();
        RequestActiveTick();
    }

    void OnNext(object? sender, EventArgs e)
    {
        WakeChrome();
        State.Next();
        RequestActiveTick();
    }

    static string Clock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        return seconds >= 3600
            ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss")
            : $"{(int)seconds / 60}:{(int)seconds % 60:00}";
    }

    static Color ParseColor(string value, Color fallback)
    {
        try { return Color.FromArgb(value); }
        catch { return fallback; }
    }

    static Color WithAlpha(Color color, int alpha) =>
        color.WithAlpha(Math.Clamp(alpha, 0, 255) / 255f);

    static string FontForWeight(string weight) => weight switch
    {
        "Bold" or "Black" => "InterBold",
        "Medium" or "SemiBold" => "InterSemiBold",
        _ => "Inter",
    };

    static Brush MakeGradient(Color first, Color second, double angle)
    {
        var radians = angle * Math.PI / 180.0;
        var dx = Math.Cos(radians) * 0.5;
        var dy = Math.Sin(radians) * 0.5;
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(first, 0),
                new GradientStop(second, 1),
            },
            new Point(0.5 - dx, 0.5 - dy),
            new Point(0.5 + dx, 0.5 + dy));
    }
}
