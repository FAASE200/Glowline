using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glowline.Localization;
using Glowline.Models;
using Glowline.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Glowline;

public partial class MainWindow : Window
{
    sealed class PlayerItem
    {
        public string? Id { get; }
        readonly string _display;
        public PlayerItem(string? id, string display)
        {
            Id = id;
            _display = display;
        }
        public override string ToString() => _display;
    }

    sealed class FontItem
    {
        public string Family { get; }
        readonly string _display;
        public FontItem(string family, string display)
        {
            Family = family;
            _display = display;
        }
        public override string ToString() => _display;
    }

    sealed class WeightItem
    {
        public string Value { get; }
        readonly string _display;
        public WeightItem(string value, string display)
        {
            Value = value;
            _display = display;
        }
        public override string ToString() => _display;
    }

    sealed class SearchItem
    {
        public LrclibTrack Track { get; }
        public SearchItem(LrclibTrack track) => Track = track;
        public override string ToString()
        {
            var mode = Track.HasSynced
                ? (Track.HasWordSync ? Loc.T("SearchModeWordByWord") : Loc.T("SearchModeByLine"))
                : Track.HasPlain ? Loc.T("SearchModePlain") : Loc.T("SearchModeNone");
            var duration = Track.Duration is > 0
                ? TimeSpan.FromSeconds(Track.Duration.Value).ToString(@"m\:ss")
                : "--:--";
            var provider = string.IsNullOrEmpty(Track.Provider) ? "LRCLIB" : Track.Provider;
            return $"{Track.TrackName} — {Track.ArtistName} · {duration} · {mode} · {provider}";
        }
    }

    readonly SettingsService _settings;
    readonly MediaSessionService _media;
    readonly LyricsService _lyrics;
    readonly CoverArtService _cover;
    readonly AnimatedArtworkService _animated;
    readonly WinForms.NotifyIcon _tray;
    readonly Drawing.Icon _trayIcon;
    readonly DispatcherTimer _uiTimer;
    readonly List<string> _diag = new();
    WinForms.ToolStripMenuItem _trayOpenItem = null!;
    WinForms.ToolStripMenuItem _traySettingsItem = null!;
    WinForms.ToolStripMenuItem _trayModeItem = null!;
    WinForms.ToolStripMenuItem _trayExitItem = null!;
    string _coverStatusKey = "CoverStaticDash";
    object?[] _coverStatusArgs = Array.Empty<object?>();
    string? _animStatusKey;
    object?[] _animStatusArgs = Array.Empty<object?>();
    string? _animErrorKey;
    string? _searchMessageKey;
    object?[] _searchMessageArgs = Array.Empty<object?>();

    DateTime _diagUiAt = DateTime.MinValue;
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
    CancellationTokenSource? _animCts;
    string _animKey = "";
    bool _loading = true;
    bool _allowClose;
    bool _trayHintShown;
    bool _updateNotified;
    CancellationTokenSource? _searchCts;

    public MainWindow(
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
        ApplyThemePalette(ThemePaletteService.Current);
        Closed += (_, __) => ThemePaletteService.Changed -= OnThemePaletteChanged;
        StateChanged += (_, __) => UpdateCaptionGlyphs();
        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 24) Height = Math.Max(MinHeight, work.Height - 24);
        if (Width > work.Width - 24) Width = Math.Max(MinWidth, work.Width - 24);
        Loaded += (_, __) => Dispatcher.BeginInvoke(
            new Action(FitToCurrentMonitor), DispatcherPriority.Loaded);
        PathInfo.Text = Loc.F("DataPathFormat", SettingsService.DataDir);
        LoadSettingsIntoUi();
        UpdateCaptionGlyphs();
        RefreshDesignerUi();

        (_trayIcon, _tray) = BuildTray();
        if (App.Updates is not null)
        {
            App.Updates.Changed += OnUpdateChanged;
            RefreshUpdateUi();
        }

        _media.SnapshotChanged += OnSnapshot;
        _lyrics.Changed += OnLyricsChanged;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, __) =>
        {
            if (!IsVisible) return;
            UpdatePositionText();
            UpdateSoundSenseUi();
            if (_settings.Settings.SoundSenseEnabled) App.SoundSense?.EnsureStarted();
        };
        _uiTimer.Start();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
            {
                App.Primary?.ToggleWindowedMode();
                ShowPrimary();
                e.Handled = true;
            }
        };

        SearchResults.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && SearchResults.SelectedItem is SearchItem item)
            {
                _lyrics.ApplyManual(item.Track);
                e.Handled = true;
            }
        };

        SearchBox.TextChanged += (_, __) =>
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        Loc.LanguageChanged += OnLanguageChanged;
        _loading = false;
        OnLyricsChanged();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyThemePalette(ThemePaletteService.Current);
    }

    void OnThemePaletteChanged(ThemePalette palette)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnThemePaletteChanged(palette)));
            return;
        }
        ApplyThemePalette(palette);
    }

    void ApplyThemePalette(ThemePalette palette)
    {
        var glass = _settings.Settings.InterfaceGlassEnabled;
        SetPaletteBrush("Ink", palette.Ink);
        SetPaletteBrush("Muted", palette.Muted);
        SetPaletteBrush("Faint", palette.Faint);
        SetPaletteBrush("Line", palette.Line);
        SetPaletteBrush("Paper", glass ? WithAlpha(palette.GlassSurface, 0xE6) : palette.Paper);
        SetPaletteBrush("Canvas", glass ? WithAlpha(palette.Canvas, 0xEC) : palette.Canvas);
        SetPaletteBrush("Accent", palette.Accent);
        SetPaletteBrush("AccentSoft", palette.AccentSoft);
        SetPaletteBrush("Hover", palette.Hover);
        SetPaletteBrush("ScrollThumb", palette.ScrollThumb);
        SetPaletteBrush("ScrollThumbHover", palette.ScrollThumbHover);
        SetPaletteBrush("WindowBackground", glass ? WithAlpha(palette.GlassSurface, 0xE6) : palette.Canvas);
        SetPaletteBrush("TitleBarSurface", glass ? WithAlpha(palette.GlassSurface, 0xD9) : palette.Canvas);
        SetPaletteBrush("ElevatedSurface", palette.Paper);
        SetPaletteBrush("GlassSurface", palette.GlassSurface);
        SetPaletteBrush("GlassBorder", palette.GlassBorder);
        SetPaletteBrush("GlassFill", palette.GlassFill);
        SetPaletteBrush("GlassFillHover", palette.GlassFillHover);
        SetPaletteBrush("GlassFillActive", palette.GlassFillActive);
        SetPaletteBrush("OverlayHover", palette.OverlayHover);
        SetPaletteBrush("OnAccent", palette.OnAccent);
        UpdateWindowChrome(palette);
    }

    void SetPaletteBrush(string key, Color color) => Resources[key] = new SolidColorBrush(color);

    void UpdateWindowChrome(ThemePalette palette)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var one = palette.Theme == InterfaceTheme.Dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref one, sizeof(int));
            UpdateWindowCaption(WithAlpha(palette.Canvas, 0xFF), palette.Ink);
        }
        catch
        {
        }
    }

    static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    void UpdateWindowCaption(Color background, Color foreground)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var caption = background.R | (background.G << 8) | (background.B << 16);
            var text = foreground.R | (foreground.G << 8) | (foreground.B << 16);
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch
        {
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(new Action(FitToCurrentMonitor), DispatcherPriority.Loaded);
    }

    void FitToCurrentMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var screen = WinForms.Screen.FromHandle(hwnd);
        if (screen is null) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var work = screen.WorkingArea;
        var maxWidth = Math.Max(MinWidth, work.Width / dpi.DpiScaleX - 24);
        var maxHeight = Math.Max(MinHeight, work.Height / dpi.DpiScaleY - 24);
        if (Width > maxWidth) Width = maxWidth;
        if (Height > maxHeight) Height = maxHeight;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var widthPx = (int)Math.Round(Width * dpi.DpiScaleX);
        var heightPx = (int)Math.Round(Height * dpi.DpiScaleY);
        var x = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - widthPx));
        var y = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - heightPx));
        if (x != rect.Left || y != rect.Top)
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void ToggleMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    void UpdateCaptionGlyphs()
    {
        if (MaximizeButton is null) return;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && !App.IsExiting)
        {
            e.Cancel = true;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2600, "Glowline", Loc.T("TrayHint"),
                    WinForms.ToolTipIcon.None);
            }
            Topmost = false;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    sealed class DarkMenuColors : WinForms.ProfessionalColorTable
    {
        static readonly Drawing.Color Background = Drawing.Color.FromArgb(28, 28, 31);
        static readonly Drawing.Color Hover = Drawing.Color.FromArgb(45, 45, 50);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemBorder => Drawing.Color.Transparent;
        public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(60, 60, 66);
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(52, 52, 58);
        public override Drawing.Color SeparatorLight => Background;
        public override Drawing.Color CheckBackground => Hover;
        public override Drawing.Color CheckSelectedBackground => Hover;
    }

    (Drawing.Icon, WinForms.NotifyIcon) BuildTray()
    {
        var icon = CreateTrayIcon();
        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Drawing.Color.FromArgb(28, 28, 31),
            ForeColor = Drawing.Color.FromArgb(245, 245, 247),
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
        };
        _trayOpenItem = new WinForms.ToolStripMenuItem(Loc.T("TrayOpen"), null, (_, __) => ShowPrimary());
        _traySettingsItem = new WinForms.ToolStripMenuItem(Loc.T("TraySettings"), null, (_, __) => ShowSettings());
        _trayModeItem = new WinForms.ToolStripMenuItem(Loc.T("TrayToggleMode"), null,
            (_, __) => App.Primary?.ToggleWindowedMode());
        _trayExitItem = new WinForms.ToolStripMenuItem(Loc.T("TrayExit"), null, (_, __) => ExitApp());
        menu.Items.Add(_trayOpenItem);
        menu.Items.Add(_traySettingsItem);
        menu.Items.Add(_trayModeItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_trayExitItem);
        foreach (WinForms.ToolStripItem item in menu.Items)
            item.ForeColor = menu.ForeColor;

        var tray = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "Glowline",
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, __) => ShowPrimary();
        return (icon, tray);
    }

    static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 8, 8, 10));
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, 13, 13, 180, 90);
            path.AddArc(19, 0, 13, 13, 270, 90);
            path.AddArc(19, 19, 13, 13, 0, 90);
            path.AddArc(0, 19, 13, 13, 90, 90);
            path.CloseFigure();
            g.FillPath(background, path);

            int[] heights = { 126, 252, 378, 252, 126 };
            var unit = 32.0 / 640.0;
            var barWidth = 46 * unit;
            var gap = 34 * unit;
            var left = (32 - (barWidth * 5 + gap * 4)) / 2;
            using var ink = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 245, 245, 247));
            using var accent = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 255, 59, 48));
            for (var i = 0; i < heights.Length; i++)
            {
                var height = heights[i] * unit;
                var x = left + i * (barWidth + gap);
                var y = (32 - height) / 2;
                g.FillRectangle(i == 2 ? accent : ink, (float)x, (float)y, (float)barWidth, (float)height);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void ShowPrimary() => App.ShowPrimaryWindow();

    public void ShowSettings()
    {
        RefreshSettingsUi();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    public void RefreshSettingsUi()
    {
        var loading = _loading;
        _loading = true;
        LoadSettingsIntoUi();
        _loading = loading;
    }

    public void ApplyInterfaceThemeSettings() => ApplyThemePalette(ThemePaletteService.Current);

    public void OpenFullscreen(Window? source = null) => App.ShowPrimaryWindow();

    void RefreshDesignerUi() => ActiveDesignText.Text = Loc.F("ActiveDesignFormat", App.Designs.Active.Name);

    public void PersistActiveDesign()
    {
        _settings.Settings.ActiveDesign = DesignService.Clone(App.Designs.Active);
        _settings.ScheduleSave();
    }

    async void ImportDesign_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Glowline design|*.glow",
            Title = Loc.T("ImportDesign"),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var package = await GlowPackageService.ImportAsync(dialog.FileName);
            App.Designs.Apply(package.Profile);
            PersistActiveDesign();
            RefreshDesignerUi();
            RefreshArtworkUi();
            try
            {
                var library = new DesignLibraryService();
                await library.SaveAsync(package.Profile, package.PreviewPng);
            }
            catch (Exception ex)
            {
                Log.Write("design library: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Designer.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void ExportDesign_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Glowline design|*.glow",
            FileName = SanitizeDesignName(App.Designs.Active.Name) + ".glow",
            Title = Loc.T("ExportDesign"),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await GlowPackageService.ExportAsync(App.Designs.CloneActive(), null, dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Designer.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static string SanitizeDesignName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "design" : name;
    }

    public void DebugApplySnapshot(SessionSnapshot snap)
    {
        OnSnapshot(snap);
        UpdatePositionText();
    }

    public void DebugScrollToEnd()
    {
        DiagExpander.IsExpanded = true;
        PageScroll.ScrollToEnd();
    }

    public void DebugShowUpdateNotes(string version, string notes)
    {
        UpdateNotesTitle.Text = Loc.F("UpdateNotesTitleFormat", version);
        UpdateNotesText.Text = FormatReleaseNotes(notes);
        UpdateNotesBorder.Visibility = Visibility.Visible;
        UpdateStatusText.Text = Loc.F("UpdateReadyFormat", version);
        UpdateInstallButton.Content = Loc.F("RestartInstallFormat", version);
        UpdateInstallButton.Visibility = Visibility.Visible;
        UpdateNotesBorder.BringIntoView();
    }

    public void DebugHideUpdateNotes()
    {
        UpdateNotesBorder.Visibility = Visibility.Collapsed;
        UpdateInstallButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = Loc.T("UpdateNotChecked");
    }

    public void DebugScrollToDesigner() => DesignerCard.BringIntoView();

    void JumpToSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        if (FindName(name) is FrameworkElement target)
            target.BringIntoView();
    }

    void LoadSettingsIntoUi()
    {
        var s = _settings.Settings;
        FontScaleSlider.Value = Math.Clamp(s.FontScale * 100, 70, 160);
        FontScaleText.Text = $"{(int)FontScaleSlider.Value}%";

        SyncSourceRadios(s.LyricsSource);

        LanguageCombo.ItemsSource = Loc.Languages;
        LanguageCombo.SelectedIndex = 0;
        for (var i = 0; i < Loc.Languages.Count; i++)
        {
            if (Loc.Languages[i].Language == s.Language)
            {
                LanguageCombo.SelectedIndex = i;
                break;
            }
        }

        PopulateFonts();
        PopulateWeights();

        SoftnessSlider.Value = Math.Clamp(s.AnimationSoftness, 0, 100);
        SoftnessText.Text = $"{(int)SoftnessSlider.Value}";
        WordFadeRadio.IsChecked = s.WordAnimation == WordAnimationStyle.Fade;
        WordSlideRadio.IsChecked = s.WordAnimation == WordAnimationStyle.Slide;
        WordZoomRadio.IsChecked = s.WordAnimation == WordAnimationStyle.Zoom;
        WordBounceRadio.IsChecked = s.WordAnimation == WordAnimationStyle.Bounce;
        WordMotionSlider.Value = Math.Clamp(s.WordMotionStrength * 100, 0, 100);
        WordMotionText.Text = $"{(int)WordMotionSlider.Value}%";
        IntroDotsCheck.IsChecked = s.IntroDotsEnabled;
        InterludeAnimCheck.IsChecked = s.InterludeAnimationEnabled;

        StartWithWindowsCheck.IsChecked = Autostart.IsEnabled();
        SoftwareRenderCheck.IsChecked = s.SoftwareRendering;
        CoverPaletteCheck.IsChecked = s.CoverPaletteEnabled;
        ThemeLightRadio.IsChecked = s.InterfaceTheme == InterfaceTheme.Light;
        ThemeDarkRadio.IsChecked = s.InterfaceTheme == InterfaceTheme.Dark;
        InterfaceGlassCheck.IsChecked = s.InterfaceGlassEnabled;
        SoundSenseCheck.IsChecked = s.SoundSenseEnabled;
        SoundSenseIntensitySlider.Value = Math.Clamp(s.SoundSenseIntensity * 100, 0, 100);
        SoundSenseIntensityText.Text = $"{(int)SoundSenseIntensitySlider.Value}%";
        RefreshExtrasUi();
        AnimatedCoverCheck.IsChecked = s.AnimatedCoversEnabled;
        SetAnimStatus(s.AnimatedCoversEnabled ? "NoPlayback" : "CoverDisabled");
        SetCoverStatus("CoverStaticDash");
        LoadArtworkIntoUi();
        UpdateSoundSenseUi();
        LoadPlayers();
        RefreshUpdateUi();
    }

    void LoadArtworkIntoUi()
    {
        var a = App.Designs.Active.Artwork;
        CoverVisibleCheck.IsChecked = a.Visible;
        CoverSizeSlider.Value = Math.Clamp(a.SizePercent, 10, 60);
        CoverSizeText.Text = $"{(int)CoverSizeSlider.Value}%";
        AnimatedCoverSizeSlider.Value = Math.Clamp(a.AnimatedSizePercent, 10, 60);
        AnimatedCoverSizeText.Text = $"{(int)AnimatedCoverSizeSlider.Value}%";
        CoverRadiusSlider.Value = Math.Clamp(a.CornerRadius, 0, 60);
        CoverRadiusText.Text = $"{(int)CoverRadiusSlider.Value}";
        CoverShadowSlider.Value = Math.Clamp(a.ShadowOpacity * 100, 0, 100);
        CoverShadowText.Text = $"{(int)CoverShadowSlider.Value}%";
    }

    public void RefreshArtworkUi()
    {
        var loading = _loading;
        _loading = true;
        LoadArtworkIntoUi();
        _loading = loading;
    }

    void Artwork_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var a = App.Designs.Active.Artwork;
        a.Visible = CoverVisibleCheck.IsChecked == true;
        a.SizePercent = Math.Round(CoverSizeSlider.Value);
        a.AnimatedSizePercent = Math.Round(AnimatedCoverSizeSlider.Value);
        a.CornerRadius = Math.Round(CoverRadiusSlider.Value);
        a.ShadowOpacity = Math.Round(CoverShadowSlider.Value) / 100.0;
        CoverSizeText.Text = $"{(int)a.SizePercent}%";
        AnimatedCoverSizeText.Text = $"{(int)a.AnimatedSizePercent}%";
        CoverRadiusText.Text = $"{(int)a.CornerRadius}";
        CoverShadowText.Text = $"{(int)Math.Round(a.ShadowOpacity * 100)}%";
        App.Primary?.RefreshArtworkLayout();
        PersistActiveDesign();
    }

    void PopulateFonts()
    {
        var s = _settings.Settings;
        var fonts = new (string Family, string Key)[]
        {
            ("Segoe UI Variable Display", "FontSegoeVariable"),
            ("Segoe UI", "FontSegoe"),
            ("Bahnschrift", "FontBahnschrift"),
            ("Arial", "FontArial"),
            ("Georgia", "FontGeorgia"),
            ("Consolas", "FontConsolas"),
            ("Verdana", "FontVerdana"),
        };
        FontCombo.Items.Clear();
        foreach (var (family, key) in fonts)
            FontCombo.Items.Add(new FontItem(family, Loc.T(key)));
        FontCombo.SelectedIndex = 0;
        for (var i = 0; i < FontCombo.Items.Count; i++)
        {
            if (FontCombo.Items[i] is FontItem item &&
                string.Equals(item.Family, s.LyricsFontFamily, StringComparison.OrdinalIgnoreCase))
            {
                FontCombo.SelectedIndex = i;
                break;
            }
        }
    }

    void PopulateWeights()
    {
        var s = _settings.Settings;
        WeightCombo.Items.Clear();
        WeightCombo.Items.Add(new WeightItem("Normal", Loc.T("WeightNormal")));
        WeightCombo.Items.Add(new WeightItem("SemiBold", Loc.T("WeightSemibold")));
        WeightCombo.Items.Add(new WeightItem("Bold", Loc.T("WeightBold")));
        WeightCombo.SelectedIndex = 1;
        for (var i = 0; i < WeightCombo.Items.Count; i++)
        {
            if (WeightCombo.Items[i] is WeightItem item &&
                string.Equals(item.Value, s.LyricsFontWeight, StringComparison.OrdinalIgnoreCase))
            {
                WeightCombo.SelectedIndex = i;
                break;
            }
        }
    }

    static string CoverSourceLabel(string source) =>
        source == "Reproductor" ? Loc.T("CoverSourcePlayer") : source;

    void SetCoverStatus(string key, params object?[] args)
    {
        _coverStatusKey = key;
        _coverStatusArgs = args;
        UpdateCoverStatusText();
    }

    void UpdateCoverStatusText()
    {
        var args = _coverStatusArgs;
        if (args.Length > 0 && args[0] is string source)
        {
            var copy = (object?[])args.Clone();
            copy[0] = CoverSourceLabel(source);
            args = copy;
        }
        CoverStatusText.Text = Loc.F(_coverStatusKey, args);
    }

    void SetAnimStatus(string key, params object?[] args)
    {
        _animStatusKey = key;
        _animStatusArgs = args;
        _animErrorKey = null;
        UpdateAnimStatusText();
    }

    void SetAnimError(string errorKey)
    {
        _animStatusKey = "CoverUnavailableFormat";
        _animStatusArgs = Array.Empty<object?>();
        _animErrorKey = errorKey;
        UpdateAnimStatusText();
    }

    void UpdateAnimStatusText()
    {
        if (_animStatusKey is not { } key) return;
        AnimatedCoverStatusText.Text = key == "CoverUnavailableFormat" && _animErrorKey is { } errorKey
            ? Loc.F(key, Loc.T(errorKey))
            : Loc.F(key, _animStatusArgs);
    }

    void RefreshCoverStatus()
    {
        UpdateCoverStatusText();
        UpdateAnimStatusText();
    }

    void LoadPlayers()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var pref = _settings.Settings.PreferredSourceAppId;
            var sources = _media.GetSourceApps();

            PlayerCombo.Items.Clear();
            PlayerCombo.Items.Add(new PlayerItem(null, Loc.T("AutomaticRecommended")));
            foreach (var id in sources)
                PlayerCombo.Items.Add(new PlayerItem(id, FriendlyPlayerName(id)));
            if (!string.IsNullOrEmpty(pref) && !sources.Contains(pref, StringComparer.OrdinalIgnoreCase))
                PlayerCombo.Items.Add(new PlayerItem(pref, Loc.F("PlayerNotVisibleFormat", FriendlyPlayerName(pref))));

            PlayerCombo.SelectedIndex = 0;
            for (var i = 0; i < PlayerCombo.Items.Count; i++)
            {
                if (PlayerCombo.Items[i] is PlayerItem item &&
                    string.Equals(item.Id, pref, StringComparison.OrdinalIgnoreCase))
                {
                    PlayerCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    public void RefreshPlayersFromSessions()    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshPlayersFromSessions));
            return;
        }
        LoadPlayers();
    }

    static string FriendlyPlayerName(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("applemusic") || lower.Contains("apple")) return "Apple Music";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("wmplayer")) return "Windows Media Player";
        return id;
    }

    void OnSnapshot(SessionSnapshot snap)
    {
        var track = snap.Track;
        if (track is null)
        {
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
                FadeOutArt(resetPalette: true);
            }
            _animCts?.Cancel();
            _animKey = "";
            SetCoverStatus("CoverStaticDash");
            SetAnimStatus(_settings.Settings.AnimatedCoversEnabled
                ? "NoPlayback" : "CoverDisabled");
        }
        else
        {
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
                    FadeOutArt();
                }
                SetCoverStatus("CoverStaticSearching");
                _ = RefreshAnimatedStatusAsync(track);
            }

            if (!_artBusy && DateTime.Now >= _artNextAt)
                _ = LoadArtAsync(track);
        }

        ApplySnapshotText(snap);

        AppendDiag(snap);
        UpdatePositionText();
    }

    void ApplySnapshotText(SessionSnapshot? snap)
    {
        var track = snap?.Track;
        TrackTitle.Text = track?.Title ?? Loc.T("NoPlayback");
        TrackArtist.Text = track?.Artist ?? "";
        TrackAlbum.Text = string.IsNullOrWhiteSpace(track?.Album) ? "" : track!.Album;
        PlaybackStatus.Text = snap?.PlaybackStatus switch
        {
            "Playing" => Loc.T("Playing"),
            "Paused" => Loc.T("Paused"),
            "Changing" => Loc.T("PlaybackChanging"),
            "Stopped" or "Closed" => Loc.T("Stopped"),
            "NoSession" or null => Loc.T("NoSession"),
            _ => snap.PlaybackStatus,
        };
        StatusDot.Fill = new SolidColorBrush(snap?.PlaybackStatus switch
        {
            "Playing" => Color.FromRgb(0x34, 0xD3, 0x99),
            "Paused" => Color.FromRgb(0xFB, 0xBF, 0x24),
            "NoSession" => Color.FromRgb(0x6B, 0x72, 0x80),
            _ => Color.FromRgb(0x9C, 0xA3, 0xAF),
        });
        var isPlaying = snap?.IsPlaying == true;
        PlayPauseIcon.Animate = !App.Designs.Active.ReduceMotion;
        PlayPauseIcon.Playing = isPlaying;
    }

    async Task LoadArtAsync(TrackInfo track)
    {
        if (_artBusy) return;
        _artBusy = true;
        var key = track.Key;
        try
        {
            var art = await _cover.GetAsync(track, CancellationToken.None);
            if (key != _artKey) return;
            var artAlbum = !string.IsNullOrWhiteSpace(art.Album) ? art.Album : track.Album;
            if (!art.HasImage)
                SetCoverStatus("CoverStaticNone");
            else if (art.PixelWidth > 0)
                SetCoverStatus("CoverStaticSourceSizeFormat", art.Source, art.PixelWidth, art.PixelHeight);
            else
                SetCoverStatus("CoverStaticSourceFormat", art.Source);
            var bytes = art.Bytes;
            if (bytes is null)
            {
                if (!_artClearedForTrack && DateTime.Now - _artWaitSince > TimeSpan.FromSeconds(6))
                {
                    _artClearedForTrack = true;
                    FadeOutArt();
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
            if (key != _artKey) return;
            if (bmp is null)
            {
                _artNextAt = DateTime.Now.AddSeconds(1);
                return;
            }

            if (AlbumArt.Source is not null)
            {
                AlbumArtBack.BeginAnimation(OpacityProperty, null);
                AlbumArtBack.Source = AlbumArt.Source;
                AlbumArtBack.Opacity = 1;
            }

            AlbumArt.BeginAnimation(OpacityProperty, null);
            AlbumArt.Source = bmp;
            ThemePaletteService.UpdateFromBitmap(bmp);
            AlbumArt.Opacity = 0;
            AlbumArt.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });

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

    void FadeOutArt(bool resetPalette = false)
    {
        AlbumArtBack.BeginAnimation(OpacityProperty, null);
        AlbumArtBack.Source = null;
        AlbumArtBack.Opacity = 1;
        if (AlbumArt.Source is null)
        {
            if (resetPalette) ThemePaletteService.Reset();
            return;
        }

        var fade = new DoubleAnimation(AlbumArt.Opacity, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };
        fade.Completed += (_, __) =>
        {
            AlbumArt.BeginAnimation(OpacityProperty, null);
            AlbumArt.Source = null;
            AlbumArt.Opacity = 1;
            if (resetPalette) ThemePaletteService.Reset();
        };
        AlbumArt.BeginAnimation(OpacityProperty, fade);
    }

    async Task RefreshAnimatedStatusAsync(TrackInfo? track)
    {
        _animCts?.Cancel();
        if (!_settings.Settings.AnimatedCoversEnabled)
        {
            SetAnimStatus("CoverDisabled");
            return;
        }
        if (track is null)
        {
            SetAnimStatus("NoPlayback");
            return;
        }

        var cts = new CancellationTokenSource();
        _animCts = cts;
        var key = track.Key;
        _animKey = key;
        SetAnimStatus("CoverSearchingAnimation");
        try
        {
            var result = await _animated.GetAsync(track, cts.Token);
            if (cts.IsCancellationRequested || _animKey != key) return;
            if (result.Available)
                SetAnimStatus("CoverAvailableFormat", result.Source);
            else if (result.ErrorKey is { Length: > 0 } errorKey)
                SetAnimError(errorKey);
            else
                SetAnimStatus("CoverNoAnimation");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("animated status: " + ex.Message);
            SetAnimError("AnimSearchFailed");
        }
    }

    void AnimatedCover_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AnimatedCoverCheck.IsChecked == true;
        _settings.Settings.AnimatedCoversEnabled = on;
        _settings.ScheduleSave();

        if (!on)
        {
            _animCts?.Cancel();
            _animKey = "";
            SetAnimStatus("CoverDisabled");
        }
        else
        {
            _ = RefreshAnimatedStatusAsync(_lyrics.Track ?? _media.LastSnapshot?.Track);
        }

        App.Primary?.ApplyAnimatedCoverSetting();
    }

    static BitmapSource? DecodeArt(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256;
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

    void AppendDiag(SessionSnapshot snap)
    {
        var age = (DateTimeOffset.Now - snap.TimelineLastUpdated).TotalSeconds;
        var disp = _lyrics.GetDisplayPosition();
        var dur = snap.DurationSeconds is { } d ? d.ToString("F1") : "-";
        _diag.Add($"{DateTime.Now:HH:mm:ss.fff} | {snap.PlaybackStatus,-8} | " +
                  $"pos={snap.PositionSeconds,7:F1} | disp={disp,7:F1} | " +
                  $"tl={snap.RawTimelinePosition.TotalSeconds,7:F1} | upd={age,7:F1}s | " +
                  $"dur={dur,6} | {snap.Track?.DisplayName ?? "-"}");
        if (_diag.Count > 300) _diag.RemoveRange(0, _diag.Count - 300);

        if ((DateTime.Now - _diagUiAt).TotalMilliseconds < 2000) return;
        _diagUiAt = DateTime.Now;
        DiagBox.Text = string.Join(Environment.NewLine, _diag);
        DiagBox.ScrollToEnd();
    }

    void UpdatePositionText()
    {
        var snap = _media.LastSnapshot;
        if (snap?.Track is null)
        {
            PositionText.Text = "";
            return;
        }
        var pos = Math.Max(0, _lyrics.GetDisplayPosition());
        var dur = snap.DurationSeconds;
        PositionText.Text = dur is > 0 ? $"{FormatClock(pos)} / {FormatClock(dur.Value)}" : FormatClock(pos);
    }

    static string FormatClock(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    void OnLyricsChanged()
    {
        var source = _settings.Settings.LyricsSource;
        var sourceName = LyricsService.SourceName(source);
        var state = _lyrics.State;
        var status = state switch
        {
            LyricsState.Idle => Loc.T("StatusNoPlayback"),
            LyricsState.Searching => Loc.F("StatusSearchingLyricsInFormat", sourceName),
            LyricsState.Synced => Loc.T("StatusLyricsReady"),
            LyricsState.Plain => Loc.T("StatusPlain"),
            LyricsState.Instrumental => Loc.T("StatusInstrumental"),
            LyricsState.NotFound => Loc.F("StatusNotFoundInFormat", sourceName),
            LyricsState.Error => Loc.F("StatusErrorInFormat", sourceName),
            _ => "",
        };

        string? detail = state switch
        {
            LyricsState.Synced => Loc.F("LyricsDetailSyncedFormat",
                _lyrics.Lines.Count,
                _lyrics.Lines.Any(l => l.Words is { Count: > 0 })
                    ? Loc.T("SearchModeWordByWord") : Loc.T("SearchModeByLine"),
                _lyrics.Found?.Source ?? ""),
            LyricsState.Plain => Loc.F("LyricsDetailPlainFormat", _lyrics.Found?.Source ?? ""),
            LyricsState.NotFound when _lyrics.NoUsableLines => Loc.T("LyricsNoLines"),
            _ => null,
        };
        if (_lyrics.NoteKey is { } noteKey)
        {
            var note = Loc.T(noteKey);
            detail = string.IsNullOrEmpty(detail) ? note : detail + " · " + note;
        }
        if (detail is not null) status += " · " + detail;
        if (state == LyricsState.NotFound) status += " · " + Loc.T("LyricsTryManual");

        if (_lyrics.Found is { } found &&
            state is LyricsState.Synced or LyricsState.Plain or LyricsState.Instrumental)
        {
            var duration = found.Duration is > 0
                ? TimeSpan.FromSeconds(found.Duration.Value).ToString(@"m\:ss")
                : "--:--";
            status += $" · \"{found.TrackName}\" — {found.ArtistName} ({duration})";
        }
        var manual = _lyrics.HasManualOverride;
        if (manual) status += " · " + Loc.T("LyricsManualSuffix");

        LyricsStatus.Text = status;
        AutoLyricsButton.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        SyncSourceRadios(source);
        OffsetText.Text = Loc.F("GlobalOffsetFormat", FormatOffset(_settings.Settings.GlobalOffsetMs));
        TrackOffsetText.Text = Loc.F("TrackOffsetFormat", FormatOffset(_lyrics.GetTrackOffsetMs()));
    }

    void SyncSourceRadios(LyricSource source)
    {
        var plus = source == LyricSource.LyricsPlus;
        var lrcmux = source == LyricSource.Lrcmux;
        var lrclib = source == LyricSource.Lrclib;
        if (SourceLyricsPlusRadio.IsChecked == plus &&
            SourceLrcmuxRadio.IsChecked == lrcmux &&
            SourceLrcLibRadio.IsChecked == lrclib) return;

        var wasLoading = _loading;
        _loading = true;
        SourceLyricsPlusRadio.IsChecked = plus;
        SourceLrcmuxRadio.IsChecked = lrcmux;
        SourceLrcLibRadio.IsChecked = lrclib;
        _loading = wasLoading;
    }

    static string FormatOffset(int ms) =>
        (ms / 1000.0).ToString("+0.00;-0.00;0.00", Loc.Culture) + " s";

    void LyricsSource_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var source = SourceLyricsPlusRadio.IsChecked == true ? LyricSource.LyricsPlus
            : SourceLrcmuxRadio.IsChecked == true ? LyricSource.Lrcmux
            : LyricSource.Lrclib;
        if (_settings.Settings.LyricsSource == source) return;
        _settings.Settings.LyricsSource = source;
        _settings.ScheduleSave();
        _lyrics.ReloadSource();
    }

    void SoundSense_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = SoundSenseCheck.IsChecked == true;
        _settings.Settings.SoundSenseEnabled = on;
        _settings.ScheduleSave();
        App.SoundSense?.SetEnabled(on);
        UpdateSoundSenseUi();
    }

    void SoundSenseIntensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var value = Math.Clamp(e.NewValue, 0, 100);
        _settings.Settings.SoundSenseIntensity = value / 100.0;
        _settings.ScheduleSave();
        if (SoundSenseIntensityText is not null) SoundSenseIntensityText.Text = $"{(int)value}%";
    }

    void UpdateSoundSenseUi()
    {
        var sense = App.SoundSense;
        var on = _settings.Settings.SoundSenseEnabled;
        var bpm = sense?.Bpm ?? 0;
        var active = on && bpm >= 20;

        SoundSenseBpmText.Text = bpm >= 20 ? $"{bpm.ToString("F0", Loc.Culture)} BPM" : Loc.T("BpmPlaceholder");
        SoundSenseStatusText.Text = !on
            ? Loc.T("SenseDisabled")
            : sense?.Status ?? Loc.T("SenseStarting");
        SoundSenseDot.Fill = new SolidColorBrush(active
            ? Color.FromRgb(0x4A, 0xDE, 0x80)
            : on ? Color.FromRgb(0xF5, 0xB7, 0x4A) : Color.FromRgb(0x6B, 0x72, 0x80));
    }

    void FontScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Settings.FontScale = e.NewValue / 100.0;
        _settings.ScheduleSave();
        if (FontScaleText is not null) FontScaleText.Text = $"{(int)e.NewValue}%";
    }

    void LanguageCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not LanguageOption option) return;
        if (_settings.Settings.Language == option.Language) return;
        _settings.Settings.Language = option.Language;
        _settings.ScheduleSave();
        Loc.SetLanguage(option.Language);
    }

    void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLanguageChanged(sender, e)));
            return;
        }

        RefreshExtrasUi();
        _trayOpenItem.Text = Loc.T("TrayOpen");
        _traySettingsItem.Text = Loc.T("TraySettings");
        _trayModeItem.Text = Loc.T("TrayToggleMode");
        _trayExitItem.Text = Loc.T("TrayExit");

        var loading = _loading;
        _loading = true;
        try
        {
            for (var i = 0; i < LanguageCombo.Items.Count; i++)
            {
                if (LanguageCombo.Items[i] is LanguageOption option && option.Language == Loc.Current)
                {
                    if (LanguageCombo.SelectedIndex != i) LanguageCombo.SelectedIndex = i;
                    break;
                }
            }
            PathInfo.Text = Loc.F("DataPathFormat", SettingsService.DataDir);
            RefreshDesignerUi();
            PopulateFonts();
            PopulateWeights();
        }
        finally
        {
            _loading = loading;
        }

        LoadPlayers();
        ApplySnapshotText(_media.LastSnapshot);
        RefreshCoverStatus();
        UpdateSoundSenseUi();
        OnLyricsChanged();
        RefreshUpdateUi();

        if (_searchMessageKey is { } key)
        {
            SearchResults.Items.Clear();
            SearchResults.Items.Add(Loc.F(key, _searchMessageArgs));
        }
        else if (SearchResults.Items.Count > 0)
        {
            SearchResults.Items.Refresh();
        }

        App.Primary?.RefreshLocalizedText();
    }

    void FontCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontCombo.SelectedItem is not FontItem item) return;
        _settings.Settings.LyricsFontFamily = item.Family;
        _settings.ScheduleSave();
    }

    void WeightCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || WeightCombo.SelectedItem is not WeightItem item) return;
        _settings.Settings.LyricsFontWeight = item.Value;
        _settings.ScheduleSave();
    }

    void Softness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Settings.AnimationSoftness = (int)Math.Round(e.NewValue);
        _settings.ScheduleSave();
        if (SoftnessText is not null) SoftnessText.Text = $"{(int)e.NewValue}";
    }

    void WordAnimation_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var style = WordSlideRadio.IsChecked == true ? WordAnimationStyle.Slide
            : WordZoomRadio.IsChecked == true ? WordAnimationStyle.Zoom
            : WordBounceRadio.IsChecked == true ? WordAnimationStyle.Bounce
            : WordAnimationStyle.Fade;
        if (_settings.Settings.WordAnimation == style) return;
        _settings.Settings.WordAnimation = style;
        _settings.ScheduleSave();
    }

    void WordMotion_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Settings.WordMotionStrength = Math.Clamp(e.NewValue / 100.0, 0, 1);
        _settings.ScheduleSave();
        if (WordMotionText is not null) WordMotionText.Text = $"{(int)e.NewValue}%";
    }

    void IntroDots_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.IntroDotsEnabled = IntroDotsCheck.IsChecked == true;
        _settings.ScheduleSave();
    }

    void InterludeAnim_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.InterludeAnimationEnabled = InterludeAnimCheck.IsChecked == true;
        _settings.ScheduleSave();
    }

    void Resync_Click(object sender, RoutedEventArgs e) => _lyrics.ForceResync();

    void AutoLyrics_Click(object sender, RoutedEventArgs e) => _lyrics.ClearManualOverride();

    void Fullscreen_Click(object sender, RoutedEventArgs e) => OpenFullscreen();

    void Prev_Click(object sender, RoutedEventArgs e) => _ = _media.SkipPreviousAsync();

    void Play_Click(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    void Next_Click(object sender, RoutedEventArgs e) => _ = _media.SkipNextAsync();

    void GlobalOffsetMinus_Click(object sender, RoutedEventArgs e) => _lyrics.AdjustGlobalOffset(-250);

    void GlobalOffsetPlus_Click(object sender, RoutedEventArgs e) => _lyrics.AdjustGlobalOffset(250);

    void GlobalOffsetReset_Click(object sender, RoutedEventArgs e) => _lyrics.ResetGlobalOffset();

    void TrackOffsetMinus_Click(object sender, RoutedEventArgs e) => _lyrics.AdjustTrackOffset(-250);

    void TrackOffsetPlus_Click(object sender, RoutedEventArgs e) => _lyrics.AdjustTrackOffset(250);

    void TrackOffsetReset_Click(object sender, RoutedEventArgs e) => _lyrics.ResetTrackOffset();

    async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await RunSearchAsync();
    }

    async Task RunSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        SetSearchMessage("SearchSearching");
        try
        {
            var results = await _lyrics.SearchManualAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchResults.Items.Clear();
            _searchMessageKey = null;
            if (results.Count == 0)
            {
                SetSearchMessage("SearchNoResults");
                return;
            }
            foreach (var result in results.Take(30)) SearchResults.Items.Add(new SearchItem(result));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetSearchMessage("SearchErrorFormat", ex.Message);
        }
    }

    void SetSearchMessage(string key, params object?[] args)
    {
        _searchMessageKey = key;
        _searchMessageArgs = args;
        SearchResults.Items.Clear();
        SearchResults.Items.Add(Loc.F(key, args));
    }

    void SearchResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is SearchItem item) _lyrics.ApplyManual(item.Track);
    }

    void PlayerCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (PlayerCombo.SelectedItem is PlayerItem item)
        {
            _settings.Settings.PreferredSourceAppId = item.Id;
            _settings.ScheduleSave();
        }
    }

    void RefreshPlayers_Click(object sender, RoutedEventArgs e) => RefreshPlayersFromSessions();

    void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = StartWithWindowsCheck.IsChecked == true;
        Autostart.Set(enabled);
    }

    void SoftwareRender_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.SoftwareRendering = SoftwareRenderCheck.IsChecked == true;
        _settings.ScheduleSave();
        MessageBox.Show(Loc.T("RenderRestartMessage"), "Glowline");
    }

    void CoverPalette_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = CoverPaletteCheck.IsChecked == true;
        _settings.Settings.CoverPaletteEnabled = enabled;
        _settings.ScheduleSave();
        ThemePaletteService.SetEnabled(enabled);
        if (!enabled) return;
        if (AlbumArt.Source is BitmapSource bmp)
            ThemePaletteService.UpdateFromBitmap(bmp);
        else if (App.Primary is { } fullscreen)
            fullscreen.RefreshThemePalette();
        else
            ThemePaletteService.Reset();
    }

    void InterfaceTheme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = ThemeDarkRadio.IsChecked == true ? InterfaceTheme.Dark : InterfaceTheme.Light;
        if (_settings.Settings.InterfaceTheme == theme) return;
        _settings.Settings.InterfaceTheme = theme;
        _settings.ScheduleSave();
        ThemePaletteService.SetInterfaceTheme(theme);
        App.Primary?.ApplyInterfaceTheme();
    }

    void InterfaceGlass_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.InterfaceGlassEnabled = InterfaceGlassCheck.IsChecked == true;
        _settings.ScheduleSave();
        ApplyThemePalette(ThemePaletteService.Current);
        App.Primary?.ApplyInterfaceTheme();
    }

    void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = AutoUpdateCheck.IsChecked == true;
        _settings.Settings.AutoUpdateEnabled = enabled;
        _settings.ScheduleSave();
        if (enabled)
            _ = App.Updates?.CheckAsync(manual: false);
    }

    void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        if (App.Updates is null) return;
        _ = App.Updates.CheckAsync(manual: true);
    }

    void UpdateStar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleaseClient.RepoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("update star: " + ex.Message);
        }
    }

    void UpdateInstall_Click(object sender, RoutedEventArgs e) => ExitApp(restartUpdate: true);

    void OnUpdateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnUpdateChanged(sender, e)));
            return;
        }

        RefreshUpdateUi();
        var state = App.Updates?.State;
        if (state is { Phase: UpdatePhase.Ready } && !_updateNotified)
        {
            _updateNotified = true;
            _tray.ShowBalloonTip(3500, "Glowline",
                Loc.F("UpdateReadyBalloon", state.Info?.Version.ToString(3) ?? ""),
                WinForms.ToolTipIcon.None);
        }
    }

    /// <summary>Shows a Windows notification from the tray icon.</summary>
    public void ShowNotice(string text)
    {
        try
        {
            _tray.ShowBalloonTip(3500, "Glowline", text, WinForms.ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            Log.Write("notice: " + ex.Message);
        }
    }

    void RefreshUpdateUi()
    {
        var updates = App.Updates;
        if (updates is null) return;

        var loading = _loading;
        _loading = true;
        VersionText.Text = "v" + UpdateService.CurrentVersionText + " · fSS";
        UpdateVersionText.Text = Loc.F("CurrentVersionFormat", UpdateService.CurrentVersionText);
        AutoUpdateCheck.IsChecked = _settings.Settings.AutoUpdateEnabled;
        _loading = loading;

        var state = updates.State;
        UpdateStatusText.Text = state.Message.Length > 0 ? state.Message : Loc.T("UpdateNotChecked");
        UpdateCheckButton.IsEnabled = state.Phase != UpdatePhase.Checking;

        var downloading = state.Phase == UpdatePhase.Downloading;
        UpdateProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.IsIndeterminate = downloading && state.Progress <= 0;
        UpdateProgress.Value = Math.Round(Math.Clamp(state.Progress, 0, 1) * 100);

        var ready = state.Phase == UpdatePhase.Ready;
        UpdateInstallButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (ready && state.Info is { } info)
            UpdateInstallButton.Content = Loc.F("RestartInstallFormat", info.Version.ToString(3));

        if (state.Info is { } notesInfo && notesInfo.Version > UpdateService.CurrentVersion)
        {
            var notesTitle = Loc.F("UpdateNotesTitleFormat", notesInfo.Version.ToString(3));
            var notesText = notesInfo.Notes.Length > 0
                ? FormatReleaseNotes(notesInfo.Notes)
                : Loc.T("UpdateNotesUnavailable");
            if (UpdateNotesTitle.Text != notesTitle) UpdateNotesTitle.Text = notesTitle;
            if (UpdateNotesText.Text != notesText) UpdateNotesText.Text = notesText;
            if (UpdateNotesBorder.Visibility != Visibility.Visible)
                UpdateNotesBorder.Visibility = Visibility.Visible;
        }
        else if (UpdateNotesBorder.Visibility != Visibility.Collapsed)
        {
            UpdateNotesBorder.Visibility = Visibility.Collapsed;
        }
    }

    static string FormatReleaseNotes(string notes)
    {
        var builder = new StringBuilder(notes.Length + 32);
        foreach (var rawLine in notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var text = line.TrimStart();
            if (text.Length == 0)
            {
                builder.AppendLine();
                continue;
            }

            while (text.StartsWith('#')) text = text[1..].TrimStart();
            if (text.StartsWith("- ", StringComparison.Ordinal) ||
                text.StartsWith("* ", StringComparison.Ordinal) ||
                text.StartsWith("+ ", StringComparison.Ordinal))
                text = "• " + text[2..];
            text = text.Replace("**", "").Replace("__", "").Replace("`", "");
            builder.AppendLine(text);
        }
        return builder.ToString().Trim();
    }

    public void ExitApp(bool restartUpdate = false)
    {
        App.IsExiting = true;
        _allowClose = true;
        _settings.Save();
        App.Updates?.ApplyPendingOnExit(restartUpdate);
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon.Dispose();
        Application.Current.Shutdown();
    }
}
