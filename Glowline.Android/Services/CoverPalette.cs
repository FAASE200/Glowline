using System;
using Android.Graphics;
using AndroidColor = Android.Graphics.Color;
using MauiColor = Microsoft.Maui.Graphics.Color;

namespace Glowline.Android.Services;

public readonly record struct FusionPalette(
    MauiColor Background,
    MauiColor Primary,
    MauiColor Secondary,
    MauiColor Muted,
    MauiColor Vocal,
    MauiColor Accent,
    bool Light,
    bool Detected)
{
    public static FusionPalette Default { get; } =
        CoverPalette.Compute(MauiColor.FromArgb("#F1F1F3"), detected: false);
}

// Mirrors the desktop border sampling: the dominant quantized color of the
// artwork frame wins only when it covers at least 55% of the border pixels;
// Detected (automatic fusion layout) requires a truly fixed frame.
public static class CoverPalette
{
    public static FusionPalette Sample(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return FusionPalette.Default;
        Bitmap? bitmap = null;
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
            if (bounds.OutWidth < 8 || bounds.OutHeight < 8) return FusionPalette.Default;
            var sample = 1;
            while (bounds.OutWidth / sample > 320 || bounds.OutHeight / sample > 320) sample *= 2;
            var options = new BitmapFactory.Options { InSampleSize = sample };
            bitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
            if (bitmap is null || bitmap.Width < 8 || bitmap.Height < 8) return FusionPalette.Default;

            var w = bitmap.Width;
            var h = bitmap.Height;
            var pixels = new int[w * h];
            bitmap.GetPixels(pixels, 0, w, 0, 0, w, h);

            var side = Math.Min(w, h);
            var inset = Math.Clamp((int)Math.Round(side * 0.008), 2, side / 4);
            var band = Math.Clamp((int)Math.Round(side * 0.01), 2, side / 8);
            var innerW = w - inset * 2;
            var innerH = h - inset * 2;
            if (innerW <= 0 || innerH <= 0) return FusionPalette.Default;

            var histogram = new int[4096];
            var total = 0;

            void Scan(int x0, int y0, int width, int height)
            {
                if (width <= 0 || height <= 0) return;
                for (var y = y0; y < y0 + height && y < h; y++)
                {
                    var row = y * w;
                    for (var x = x0; x < x0 + width && x < w; x++)
                    {
                        var pixel = pixels[row + x];
                        if (AndroidColor.GetAlphaComponent(pixel) < 32) continue;
                        var bin = ((AndroidColor.GetRedComponent(pixel) >> 4) << 8) |
                                  ((AndroidColor.GetGreenComponent(pixel) >> 4) << 4) |
                                  (AndroidColor.GetBlueComponent(pixel) >> 4);
                        histogram[bin]++;
                        total++;
                    }
                }
            }

            void ScanBorder()
            {
                Scan(inset, inset, innerW, band);
                Scan(inset, h - inset - band, innerW, band);
                var sideHeight = Math.Max(1, innerH - band * 2);
                Scan(inset, inset + band, band, sideHeight);
                Scan(w - inset - band, inset + band, band, sideHeight);
            }

            ScanBorder();
            if (total == 0) return FusionPalette.Default;

            var dominant = 0;
            for (var i = 1; i < histogram.Length; i++)
                if (histogram[i] > histogram[dominant]) dominant = i;
            if (histogram[dominant] / (double)total < 0.55) return FusionPalette.Default;

            long sumR = 0, sumG = 0, sumB = 0;
            var count = 0;
            var edgeTotal = new int[4];
            var edgeDominant = new int[4];
            void Average(int x0, int y0, int width, int height, int edge)
            {
                if (width <= 0 || height <= 0) return;
                for (var y = y0; y < y0 + height && y < h; y++)
                {
                    var row = y * w;
                    for (var x = x0; x < x0 + width && x < w; x++)
                    {
                        var pixel = pixels[row + x];
                        if (AndroidColor.GetAlphaComponent(pixel) < 32) continue;
                        var bin = ((AndroidColor.GetRedComponent(pixel) >> 4) << 8) |
                                  ((AndroidColor.GetGreenComponent(pixel) >> 4) << 4) |
                                  (AndroidColor.GetBlueComponent(pixel) >> 4);
                        edgeTotal[edge]++;
                        if (bin != dominant) continue;
                        edgeDominant[edge]++;
                        sumR += AndroidColor.GetRedComponent(pixel);
                        sumG += AndroidColor.GetGreenComponent(pixel);
                        sumB += AndroidColor.GetBlueComponent(pixel);
                        count++;
                    }
                }
            }

            Average(inset, inset, innerW, band, 0);
            Average(inset, h - inset - band, innerW, band, 1);
            var sideBandHeight = Math.Max(1, innerH - band * 2);
            Average(inset, inset + band, band, sideBandHeight, 2);
            Average(w - inset - band, inset + band, band, sideBandHeight, 3);
            if (count == 0) return FusionPalette.Default;

            var sampled = MauiColor.FromRgb(
                (byte)Math.Round(sumR / (double)count),
                (byte)Math.Round(sumG / (double)count),
                (byte)Math.Round(sumB / (double)count));

            // Fondo fijo = mismo color dominante en todo el marco, no solo en promedio.
            var fixedBackground = histogram[dominant] / (double)total >= 0.85;
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
            return Compute(sampled, detected: fixedBackground);
        }
        catch
        {
            return FusionPalette.Default;
        }
        finally
        {
            bitmap?.Recycle();
        }
    }

    public static FusionPalette Compute(MauiColor sampled, bool detected)
    {
        var light = FusionUsesDarkText(sampled);
        var baseForeground = light ? MauiColor.FromRgb(0x0B, 0x0B, 0x0E) : MauiColor.FromRgb(0xFF, 0xFF, 0xFF);
        var primary = EnsureContrast(
            Mix(baseForeground, sampled, light ? 0.08 : 0.06), sampled, light);
        var secondary = EnsureContrast(Mix(primary, sampled, 0.20), sampled, light);
        var muted = EnsureContrast(Mix(primary, sampled, 0.30), sampled, light);
        var accent = Mix(primary, sampled, 0.28);
        return new FusionPalette(
            sampled, primary, secondary, muted, WithAlpha(secondary, 0xB3), accent, light, detected);
    }

    public static MauiColor Mix(MauiColor from, MauiColor to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte R(MauiColor c) => (byte)Math.Round(c.Red * 255);
        byte G(MauiColor c) => (byte)Math.Round(c.Green * 255);
        byte B(MauiColor c) => (byte)Math.Round(c.Blue * 255);
        return MauiColor.FromRgb(
            (byte)Math.Round(R(from) + (R(to) - R(from)) * amount),
            (byte)Math.Round(G(from) + (G(to) - G(from)) * amount),
            (byte)Math.Round(B(from) + (B(to) - B(from)) * amount));
    }

    public static double Luminance(MauiColor color)
    {
        double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var r = (byte)Math.Round(color.Red * 255);
        var g = (byte)Math.Round(color.Green * 255);
        var b = (byte)Math.Round(color.Blue * 255);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    public static bool FusionUsesDarkText(MauiColor color)
    {
        var luminance = Luminance(color);
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05);
    }

    public static double Contrast(MauiColor first, MauiColor second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    static MauiColor EnsureContrast(MauiColor color, MauiColor background, bool lightSurface)
    {
        if (Contrast(color, background) >= 4.5) return color;
        var anchor = lightSurface ? MauiColor.FromRgb(0, 0, 0) : MauiColor.FromRgb(255, 255, 255);
        for (var i = 0; i < 24; i++)
        {
            color = Mix(color, anchor, 0.08);
            if (Contrast(color, background) >= 4.5) return color;
        }
        return color;
    }

    public static MauiColor WithAlpha(MauiColor color, int alpha) =>
        MauiColor.FromRgba(
            (byte)Math.Round(color.Red * 255),
            (byte)Math.Round(color.Green * 255),
            (byte)Math.Round(color.Blue * 255),
            (byte)Math.Clamp(alpha, 0, 255));
}
