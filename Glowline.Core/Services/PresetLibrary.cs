using Glowline.Models;

namespace Glowline.Services;

public static class PresetLibrary
{
    public static void Apply(DesignProfile p, int index)
    {
        switch (index)
        {
            case 0:
                p.Layout = DesignLayout.Lateral;
                p.Artwork.Fusion = false; p.Palette.CoverFusion = false; p.Artwork.Visible = true; p.Artwork.SizePercent = 30;
                p.Background.Kind = BackgroundKind.Solid; p.Lyrics.Alignment = DesignAlignment.Left; p.Lyrics.WidthPercent = 55;
                break;
            case 1:
                p.Layout = DesignLayout.Fusion;
                p.Artwork.Visible = true; p.Artwork.Fusion = true; p.Palette.CoverFusion = true;
                p.Background.Kind = BackgroundKind.CoverBlur; p.Background.Blur = 60; p.Background.Dim = 0.5;
                p.Lyrics.Alignment = DesignAlignment.Center;
                break;
            case 2:
                p.Layout = DesignLayout.Minimal;
                p.Artwork.Visible = false; p.Artwork.Fusion = false;
                p.Background.Kind = BackgroundKind.Solid; p.Background.Particles = ParticleKind.None; p.Background.BeatReactive = false;
                p.Animations.WordAnimation = LyricAnimation.Fade; p.Animations.VerseTransition = VerseTransition.Fade;
                p.Controls.ChromeStyle = ChromeStyle.Minimal; p.Controls.ShowMetadata = false;
                p.Controls.ShowTransport = false; p.Controls.ShowProgress = false;
                break;
            case 3:
                p.Layout = DesignLayout.Fusion;
                p.Artwork.Visible = false; p.Artwork.Fusion = false;
                p.Palette.AutoContrast = false; p.Palette.BackgroundColor = "#08080A"; p.Palette.BackgroundSecondaryColor = "#17171B";
                p.Palette.TextColor = "#F5F5F7"; p.Palette.SecondaryTextColor = "#A8A8B0";
                p.Palette.LyricActiveColor = "#FFFFFF"; p.Palette.LyricInactiveColor = "#4DFFFFFF";
                p.Background.Kind = BackgroundKind.CoverBlur; p.Background.Blur = 90; p.Background.Dim = 0.72;
                p.Background.Particles = ParticleKind.Dust; p.Background.ParticleIntensity = 0.4;
                p.Lyrics.InactiveOpacity = 0.3;
                p.Typography.LyricSize = 34; p.Animations.Easing = EasingKind.EaseInOut; p.Animations.TransitionDurationMs = 600;
                break;
            case 4:
                p.Layout = DesignLayout.Karaoke;
                p.Artwork.Visible = false; p.Controls.ShowMetadata = false; p.Controls.ShowTransport = false;
                p.Controls.ShowProgress = false;
                p.Background.Kind = BackgroundKind.Solid; p.Palette.AutoContrast = false;
                p.Palette.BackgroundColor = "#08080A"; p.Palette.TextColor = "#F5F5F7";
                p.Palette.LyricActiveColor = "#FFFFFF"; p.Palette.LyricInactiveColor = "#66FFFFFF";
                p.Animations.WordAnimation = LyricAnimation.Pulse; p.Animations.WordIntensity = 0.8; p.Animations.WordDurationMs = 90;
                p.Palette.AccentColor = "#FFD23F"; p.Lyrics.Alignment = DesignAlignment.Center; p.Lyrics.WidthPercent = 80;
                p.Typography.LyricSize = 38; p.Typography.FontWeight = "Bold";
                break;
        }
    }
}
