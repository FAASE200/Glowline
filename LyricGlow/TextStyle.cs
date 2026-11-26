using System;
using System.Windows;
using System.Windows.Media;
using LyricGlow.Models;

namespace LyricGlow;

public static class TextStyle
{
    public static FontFamily GetFont(AppSettings settings)
    {
        var name = string.IsNullOrWhiteSpace(settings.LyricsFontFamily)
            ? "Segoe UI Variable Display"
            : settings.LyricsFontFamily;
        try
        {
            return new FontFamily(name);
        }
        catch
        {
            return new FontFamily("Segoe UI");
        }
    }

    public static FontWeight GetWeight(AppSettings settings) => settings.LyricsFontWeight switch
    {
        "Normal" => FontWeights.Normal,
        "Bold" => FontWeights.Bold,
        _ => FontWeights.SemiBold,
    };

    public static TimeSpan LineAnim(AppSettings settings) =>
        TimeSpan.FromMilliseconds(120 + Math.Clamp(settings.AnimationSoftness, 0, 100) * 2.2);

    public static TimeSpan WordFade(AppSettings settings) =>
        TimeSpan.FromMilliseconds(60 + Math.Clamp(settings.AnimationSoftness, 0, 100) * 1.2);

    public static double ScrollRate(AppSettings settings) =>
        Math.Max(5, 18 - Math.Clamp(settings.AnimationSoftness, 0, 100) * 0.12);
}
