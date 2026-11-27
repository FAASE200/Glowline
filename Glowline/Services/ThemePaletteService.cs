using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glowline.Models;

namespace Glowline.Services;

public sealed class ThemePalette
{
    public InterfaceTheme Theme { get; init; } = InterfaceTheme.Light;
    public Color Ink { get; init; } = Color.FromRgb(0x11, 0x11, 0x13);
    public Color Muted { get; init; } = Color.FromRgb(0x6E, 0x6E, 0x73);
    public Color Faint { get; init; } = Color.FromRgb(0x74, 0x74, 0x7A);
    public Color Line { get; init; } = Color.FromRgb(0xE1, 0xE1, 0xE6);
    public Color Paper { get; init; } = Colors.White;
    public Color Canvas { get; init; } = Color.FromRgb(0xF5, 0xF5, 0xF7);
    public Color Accent { get; init; } = Color.FromRgb(0x11, 0x11, 0x13);
    public Color AccentSoft { get; init; } = Color.FromRgb(0xEC, 0xEC, 0xEF);
    public Color Hover { get; init; } = Color.FromRgb(0xF0, 0xF0, 0xF3);
    public Color Track { get; init; } = Color.FromRgb(0xE4, 0xE4, 0xE9);
    public Color Thumb { get; init; } = Colors.White;
    public Color ThumbStroke { get; init; } = Color.FromRgb(0xC9, 0xC9, 0xCF);
    public Color ScrollThumb { get; init; } = Color.FromRgb(0xC9, 0xC9, 0xCF);
    public Color ScrollThumbHover { get; init; } = Color.FromRgb(0xA1, 0xA1, 0xA8);
    public Color Border { get; init; } = Color.FromRgb(0xE1, 0xE1, 0xE6);
    public Color GlassSurface { get; init; } = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
    public Color GlassBorder { get; init; } = Color.FromArgb(0x1F, 0x11, 0x11, 0x13);
    public Color GlassFill { get; init; } = Color.FromArgb(0x0B, 0x11, 0x11, 0x13);
    public Color GlassFillHover { get; init; } = Color.FromArgb(0x14, 0x11, 0x11, 0x13);
    public Color GlassFillActive { get; init; } = Color.FromArgb(0x1F, 0x11, 0x11, 0x13);
    public Color OverlayHover { get; init; } = Colors.Black;
    public Color OnAccent { get; init; } = Colors.White;

    public static ThemePalette Neutral { get; } = new();

    public static ThemePalette DarkNeutral { get; } = new()
    {
        Theme = InterfaceTheme.Dark,
        Ink = Color.FromRgb(0xF4, 0xF4, 0xF6),
        Muted = Color.FromRgb(0xB2, 0xB2, 0xBB),
        Faint = Color.FromRgb(0x8D, 0x8D, 0x97),
        Line = Color.FromRgb(0x2D, 0x31, 0x3A),
        Paper = Color.FromRgb(0x0F, 0x12, 0x19),
        Canvas = Color.FromRgb(0x08, 0x0A, 0x10),
        Accent = Color.FromRgb(0xF4, 0xF4, 0xF6),
        AccentSoft = Color.FromRgb(0x22, 0x22, 0x27),
        Hover = Color.FromRgb(0x18, 0x1C, 0x25),
        Track = Color.FromRgb(0x33, 0x39, 0x46),
        Thumb = Color.FromRgb(0xF4, 0xF4, 0xF6),
        ThumbStroke = Color.FromRgb(0x58, 0x5F, 0x6C),
        ScrollThumb = Color.FromRgb(0x69, 0x71, 0x80),
        ScrollThumbHover = Color.FromRgb(0x90, 0x99, 0xAA),
        Border = Color.FromArgb(0x23, 0xFF, 0xFF, 0xFF),
        GlassSurface = Color.FromArgb(0xC2, 0x0A, 0x0D, 0x16),
        GlassBorder = Color.FromArgb(0x23, 0xFF, 0xFF, 0xFF),
        GlassFill = Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF),
        GlassFillHover = Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF),
        GlassFillActive = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
        OverlayHover = Colors.White,
        OnAccent = Color.FromRgb(0x08, 0x0A, 0x0F),
    };

    public bool SameAs(ThemePalette other) =>
        Theme == other.Theme &&
        Ink == other.Ink && Muted == other.Muted && Faint == other.Faint && Line == other.Line &&
        Paper == other.Paper && Canvas == other.Canvas && Accent == other.Accent &&
        AccentSoft == other.AccentSoft && Hover == other.Hover && Track == other.Track &&
        Thumb == other.Thumb && ThumbStroke == other.ThumbStroke &&
        ScrollThumb == other.ScrollThumb && ScrollThumbHover == other.ScrollThumbHover &&
        Border == other.Border && GlassSurface == other.GlassSurface && GlassBorder == other.GlassBorder &&
        GlassFill == other.GlassFill && GlassFillHover == other.GlassFillHover &&
        GlassFillActive == other.GlassFillActive && OverlayHover == other.OverlayHover &&
        OnAccent == other.OnAccent;
}

public static class ThemePaletteService
{
    public static bool Enabled { get; private set; } = true;
    public static InterfaceTheme Theme { get; private set; } = InterfaceTheme.Dark;
    public static ThemePalette Current { get; private set; } = ThemePalette.DarkNeutral;
    public static event Action<ThemePalette>? Changed;

    static double seedHue;
    static double seedSaturation;
    static Color seedColor;
    static bool hasSeed;

    public static void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Rebuild();
    }

    public static void SetInterfaceTheme(InterfaceTheme theme)
    {
        if (Theme == theme) return;
        Theme = theme;
        Rebuild();
    }

    public static void Reset() => Rebuild();

    public static void UpdateFromBitmap(BitmapSource? bitmap)
    {
        hasSeed = bitmap is not null && TrySample(bitmap, out seedHue, out seedSaturation, out seedColor);
        Rebuild();
    }

    static void Rebuild()
    {
        var palette = Enabled && hasSeed
            ? TryBuildPalette(seedHue, seedSaturation, seedColor)
            : null;
        Set(palette ?? (Theme == InterfaceTheme.Dark ? ThemePalette.DarkNeutral : ThemePalette.Neutral));
    }

    static void Set(ThemePalette palette)
    {
        if (Current.SameAs(palette)) return;
        Current = palette;
        Changed?.Invoke(palette);
    }

    static ThemePalette? TryBuildPalette(double hue, double saturation, Color seed)
    {
        return Theme == InterfaceTheme.Dark
            ? BuildDarkPalette(hue, saturation, seed)
            : BuildLightPalette(hue, saturation, seed);
    }

    static ThemePalette BuildLightPalette(double hue, double saturation, Color seed)
    {
        var accent = AccentColor(InterfaceTheme.Light, hue, saturation);
        var paper = FromHsl(hue, Math.Min(saturation * 0.16, 0.05), 0.985);
        var canvas = FromHsl(hue, Math.Min(saturation * 0.30, 0.11), 0.955);
        var line = FromHsl(hue, Math.Min(saturation * 0.22, 0.10), 0.875);
        var hover = FromHsl(hue, Math.Min(saturation * 0.24, 0.10), 0.935);
        var accentSoft = FromHsl(hue, Math.Min(saturation * 0.45, 0.30), 0.952);
        var track = FromHsl(hue, Math.Min(saturation * 0.18, 0.07), 0.895);
        var thumbStroke = FromHsl(hue, Math.Min(saturation * 0.14, 0.06), 0.80);
        var scrollThumb = FromHsl(hue, Math.Min(saturation * 0.10, 0.05), 0.78);
        var scrollHover = FromHsl(hue, Math.Min(saturation * 0.10, 0.05), 0.62);

        var ink = Mix(ThemePalette.Neutral.Ink, seed, 0.025);
        var muted = Mix(ThemePalette.Neutral.Muted, seed, 0.05);
        var faint = Mix(ThemePalette.Neutral.Faint, seed, 0.04);
        while (Contrast(muted, paper) < 4.5) muted = Mix(muted, Colors.Black, 0.04);
        while (Contrast(faint, paper) < 4.0) faint = Mix(faint, Colors.Black, 0.04);

        return new ThemePalette
        {
            Theme = InterfaceTheme.Light,
            Ink = ink,
            Muted = muted,
            Faint = faint,
            Line = line,
            Paper = paper,
            Canvas = canvas,
            Accent = accent,
            AccentSoft = accentSoft,
            Hover = hover,
            Track = track,
            Thumb = Colors.White,
            ThumbStroke = thumbStroke,
            ScrollThumb = scrollThumb,
            ScrollThumbHover = scrollHover,
            Border = line,
            GlassSurface = Color.FromArgb(0xD6, paper.R, paper.G, paper.B),
            GlassBorder = Color.FromArgb(0x1A, ink.R, ink.G, ink.B),
            GlassFill = Color.FromArgb(0x0B, ink.R, ink.G, ink.B),
            GlassFillHover = Color.FromArgb(0x14, ink.R, ink.G, ink.B),
            GlassFillActive = Color.FromArgb(0x1F, ink.R, ink.G, ink.B),
            OverlayHover = Colors.Black,
            OnAccent = Colors.White,
        };
    }

    static ThemePalette BuildDarkPalette(double hue, double saturation, Color seed)
    {
        var accent = AccentColor(InterfaceTheme.Dark, hue, saturation);
        var paper = FromHsl(hue, Math.Min(saturation * 0.22, 0.14), 0.085);
        var canvas = FromHsl(hue, Math.Min(saturation * 0.26, 0.18), 0.050);
        var line = FromHsl(hue, Math.Min(saturation * 0.22, 0.18), 0.230);
        var hover = FromHsl(hue, Math.Min(saturation * 0.24, 0.17), 0.140);
        var accentSoft = FromHsl(hue, Math.Min(saturation * 0.50, 0.34), 0.190);
        var track = FromHsl(hue, Math.Min(saturation * 0.20, 0.16), 0.290);
        var thumbStroke = FromHsl(hue, Math.Min(saturation * 0.14, 0.12), 0.430);
        var scrollThumb = FromHsl(hue, Math.Min(saturation * 0.12, 0.10), 0.540);
        var scrollHover = FromHsl(hue, Math.Min(saturation * 0.12, 0.10), 0.690);

        var ink = Mix(ThemePalette.DarkNeutral.Ink, seed, 0.025);
        var muted = Mix(ThemePalette.DarkNeutral.Muted, seed, 0.04);
        var faint = Mix(ThemePalette.DarkNeutral.Faint, seed, 0.03);
        while (Contrast(muted, paper) < 4.5) muted = Mix(muted, Colors.White, 0.04);
        while (Contrast(faint, paper) < 4.0) faint = Mix(faint, Colors.White, 0.04);

        var glassSurface = FromHslA(hue, Math.Min(saturation * 0.28, 0.22), 0.055, 0xB0);
        var hairline = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);

        return new ThemePalette
        {
            Theme = InterfaceTheme.Dark,
            Ink = ink,
            Muted = muted,
            Faint = faint,
            Line = line,
            Paper = paper,
            Canvas = canvas,
            Accent = accent,
            AccentSoft = accentSoft,
            Hover = hover,
            Track = track,
            Thumb = ink,
            ThumbStroke = thumbStroke,
            ScrollThumb = scrollThumb,
            ScrollThumbHover = scrollHover,
            Border = hairline,
            GlassSurface = glassSurface,
            GlassBorder = hairline,
            GlassFill = Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF),
            GlassFillHover = Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF),
            GlassFillActive = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
            OverlayHover = Colors.White,
            OnAccent = Contrast(accent, Colors.White) >= 4.0 ? Colors.White : Color.FromRgb(0x08, 0x0A, 0x0F),
        };
    }

    static bool TrySample(BitmapSource bitmap, out double hue, out double saturation, out Color seed)
    {
        hue = 0;
        saturation = 0;
        seed = default;
        try
        {
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (width < 2 || height < 2) return false;

            var scale = Math.Min(1.0, 56.0 / Math.Max(width, height));
            BitmapSource sample = bitmap;
            if (scale < 0.999)
                sample = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            if (sample.Format != PixelFormats.Bgra32)
                sample = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);

            var sampleWidth = sample.PixelWidth;
            var sampleHeight = sample.PixelHeight;
            if (sampleWidth < 2 || sampleHeight < 2) return false;

            var stride = sampleWidth * 4;
            var pixels = new byte[stride * sampleHeight];
            sample.CopyPixels(pixels, stride, 0);

            const int BinCount = 24;
            var weight = new double[BinCount];
            var sumR = new double[BinCount];
            var sumG = new double[BinCount];
            var sumB = new double[BinCount];

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                if (alpha < 128) continue;
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var hsl = ToHsl(Color.FromRgb(r, g, b));
                if (hsl.L < 0.10 || hsl.L > 0.92 || hsl.S < 0.16) continue;

                var pixelWeight = hsl.S * (1 - Math.Abs(hsl.L - 0.55) * 1.1);
                if (pixelWeight <= 0) continue;
                var bin = (int)(hsl.H * BinCount) % BinCount;
                weight[bin] += pixelWeight;
                sumR[bin] += r * pixelWeight;
                sumG[bin] += g * pixelWeight;
                sumB[bin] += b * pixelWeight;
            }

            var best = -1;
            var bestWeight = 0.0;
            for (var i = 0; i < BinCount; i++)
            {
                if (weight[i] <= bestWeight) continue;
                bestWeight = weight[i];
                best = i;
            }
            if (best < 0 || bestWeight <= 0) return false;

            seed = Color.FromRgb(
                (byte)Math.Round(sumR[best] / bestWeight),
                (byte)Math.Round(sumG[best] / bestWeight),
                (byte)Math.Round(sumB[best] / bestWeight));
            var sampledHsl = ToHsl(seed);
            if (sampledHsl.S < 0.10) return false;

            hue = sampledHsl.H;
            saturation = sampledHsl.S;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static Color AccentColor(InterfaceTheme theme, double hue, double saturation)
    {
        var sat = Math.Clamp(saturation, 0.55, 0.82);
        var background = theme == InterfaceTheme.Dark
            ? ThemePalette.DarkNeutral.Paper
            : ThemePalette.Neutral.Paper;
        var lightness = theme == InterfaceTheme.Dark ? 0.61 : 0.52;
        var step = theme == InterfaceTheme.Dark ? 0.015 : -0.015;
        Color color;
        var guard = 0;
        do
        {
            color = FromHsl(hue, sat, lightness);
            lightness += step;
            guard++;
        }
        while (Contrast(color, background) < 4.5 && guard < 30 && lightness is > 0.25 and < 0.88);
        return color;
    }

    static (double H, double S, double L) ToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (Math.Abs(max - min) < 0.00001)
            return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g)
            h = (b - r) / d + 2;
        else
            h = (r - g) / d + 4;
        return (h / 6, s, l);
    }

    static Color FromHsl(double h, double s, double l) => FromHslA(h, s, l, 255);

    static Color FromHslA(double h, double s, double l, byte alpha)
    {
        h = (h % 1 + 1) % 1;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s < 0.00001)
        {
            var gray = (byte)Math.Round(l * 255);
            return Color.FromArgb(alpha, gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromArgb(alpha, Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));
    }

    static byte Channel(double p, double q, double t)
    {
        t = (t % 1 + 1) % 1;
        double value;
        if (t < 1.0 / 6) value = p + (q - p) * 6 * t;
        else if (t < 1.0 / 2) value = q;
        else if (t < 2.0 / 3) value = p + (q - p) * (2.0 / 3 - t) * 6;
        else value = p;
        return (byte)Math.Round(value * 255);
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

    static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
