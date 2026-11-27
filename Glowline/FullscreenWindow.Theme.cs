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

// Lyrics view theme, chrome and cover sizes.
public partial class FullscreenWindow
{
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

    // 26% is the historical size; the rest of the range scales from that base
    // so the setting still has an effect even when the window is large.
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
}

