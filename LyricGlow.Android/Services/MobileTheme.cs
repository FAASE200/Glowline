using LyricGlow.Models;
using Microsoft.Maui.Graphics;

namespace LyricGlow.Android.Services;

public static class MobileTheme
{
    static bool Dark => AppState.Instance.Settings.InterfaceTheme == InterfaceTheme.Dark;
    public static bool IsDark => Dark;

    public static Color PageBackground => Color.FromArgb(Dark ? "#08080A" : "#F2F2F7");
    public static Color CardBackground => Color.FromArgb(Dark ? "#121216" : "#FFFFFF");
    public static Color CardBorder => Color.FromArgb(Dark ? "#22FFFFFF" : "#18000000");
    public static Color FieldBackground => Color.FromArgb(Dark ? "#242429" : "#EFEFF3");
    public static Color FieldTitle => Color.FromArgb(Dark ? "#85858D" : "#8A8A8F");
    public static Color Ink => Color.FromArgb(Dark ? "#F5F5F7" : "#1C1C1E");
    public static Color Muted => Color.FromArgb(Dark ? "#A8A8B0" : "#6E6E76");
    public static Color Faint => Color.FromArgb(Dark ? "#5F5F68" : "#9A9AA1");
    public static Color Accent => Color.FromArgb("#FF375F");
    public static Color Track => Color.FromArgb(Dark ? "#3A3A42" : "#D9D9E0");
    public static Color ButtonBackground => Color.FromArgb(Dark ? "#242429" : "#E5E5EA");
    public static Color SubtleButtonBackground => Color.FromArgb(Dark ? "#1FFFFFFF" : "#14000000");
    public static Color PillBackground => Color.FromArgb(Dark ? "#14FFFFFF" : "#0F000000");
    public static Color CoverBackground => Color.FromArgb(Dark ? "#242429" : "#E9E9EE");
    public static Color CoverGlyph => Color.FromArgb(Dark ? "#3AFFFFFF" : "#33000000");
    public static Color OnAccent => Colors.White;
}
