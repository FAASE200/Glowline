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

// Portada estática y animada, muestreo de color y fondos difuminados.
public partial class FullscreenWindow
{
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
}

