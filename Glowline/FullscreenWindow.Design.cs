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

// Active design profile (background, cover, typography) and snapshot opening.
public partial class FullscreenWindow
{
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
            // The blur is baked into the 256x144 thumbnail: applying it live
            // over the whole window costs GPU on every frame without looking better.
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
        // Adaptive only switches to the merged vertical view when the cover
        // really has a solid background; everything else uses the two-column view.
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
        SyncFrameLoop();
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
        SyncFrameLoop();
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
        SyncFrameLoop();
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
}

