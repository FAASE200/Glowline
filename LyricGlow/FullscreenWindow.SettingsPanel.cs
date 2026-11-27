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

// Panel de ajustes rápidos, tema de interfaz y paleta.
public partial class FullscreenWindow
{
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
}

