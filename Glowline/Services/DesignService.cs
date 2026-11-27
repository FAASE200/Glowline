using System;
using System.Text.Json;
using Glowline.Models;

namespace Glowline.Services;

public sealed class DesignService
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    DesignProfile _active;

    public DesignService(DesignProfile? initial = null)
    {
        _active = initial is null ? new DesignProfile() : Clone(initial);
        Normalize(_active);
        // Los perfiles de escritorio sin editar conservan Adaptativo (que elige
        // cine o dos columnas según la carátula) y alinean las letras a la izquierda.
        if (IsLegacyDefault(_active))
            ApplyDesktopDefaults(_active);
    }

    public DesignProfile Active
    {
        get => _active;
        private set
        {
            _active = value ?? new DesignProfile();
            ActiveChanged?.Invoke();
        }
    }

    public event Action? ActiveChanged;

    public DesignProfile CloneActive() => Clone(_active);

    public void Apply(DesignProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var copy = Clone(profile);
        Normalize(copy);
        Active = copy;
    }

    public void Reset() => Active = CreateDefault();

    public static DesignProfile CreateDefault()
    {
        var profile = new DesignProfile();
        ApplyDesktopDefaults(profile);
        return profile;
    }

    public static DesignProfile Clone(DesignProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        return JsonSerializer.Deserialize<DesignProfile>(json) ?? new DesignProfile();
    }

    static void ApplyDesktopDefaults(DesignProfile profile)
    {
        profile.Lyrics.Alignment = DesignAlignment.Left;
        profile.SchemaVersion = 2;
    }

    static bool IsLegacyDefault(DesignProfile profile)
    {
        if (profile.SchemaVersion >= 2) return false;
        var reference = new DesignProfile { Id = profile.Id, SchemaVersion = profile.SchemaVersion };
        return JsonSerializer.Serialize(profile, JsonOptions) ==
               JsonSerializer.Serialize(reference, JsonOptions);
    }

    static void Normalize(DesignProfile profile)
    {
        profile.Palette ??= new DesignPalette();
        profile.Typography ??= new DesignTypography();
        profile.Lyrics ??= new DesignLyrics();
        profile.Artwork ??= new DesignArtwork();
        profile.Background ??= new DesignBackground();
        profile.Animations ??= new DesignAnimations();
        profile.Controls ??= new DesignControls();
        profile.Artwork.SizePercent = SanitizeArtworkSize(profile.Artwork.SizePercent);
        // Materializa el tamaño animado heredado para que ambos ajustes sean independientes.
        profile.Artwork.AnimatedSizePercent = SanitizeArtworkSize(profile.Artwork.AnimatedSizePercent);
        profile.Name = SanitizeText(profile.Name, 200, "Mi diseño");
        profile.Author = SanitizeText(profile.Author, 200, "");
        profile.Description = SanitizeText(profile.Description, 2000, "");
        profile.Id = SanitizeText(profile.Id, 64, Guid.NewGuid().ToString("N"));

        profile.Typography.FontFamily = SanitizeText(profile.Typography.FontFamily, 128,
            "Segoe UI Variable Display");
        profile.Typography.FontWeight = SanitizeText(profile.Typography.FontWeight, 32, "SemiBold");
        profile.Typography.TitleSize = Clamp(profile.Typography.TitleSize, 8, 120, 26);
        profile.Typography.ArtistSize = Clamp(profile.Typography.ArtistSize, 8, 120, 15);
        profile.Typography.LyricSize = Clamp(profile.Typography.LyricSize, 16, 64, 29);
        profile.Typography.LineHeight = Clamp(profile.Typography.LineHeight, 0.8, 2, 1.2);

        profile.Palette.BackgroundColor = SanitizeText(profile.Palette.BackgroundColor, 64, "#0A0A0C");
        profile.Palette.BackgroundSecondaryColor = SanitizeText(
            profile.Palette.BackgroundSecondaryColor, 64, "#17171B");
        profile.Palette.TextColor = SanitizeText(profile.Palette.TextColor, 64, "#F5F5F7");
        profile.Palette.SecondaryTextColor = SanitizeText(profile.Palette.SecondaryTextColor, 64, "#A8A8B0");
        profile.Palette.AccentColor = SanitizeText(profile.Palette.AccentColor, 64, "#FFFFFF");
        profile.Palette.LyricActiveColor = SanitizeText(profile.Palette.LyricActiveColor, 64, "#FFFFFF");
        profile.Palette.LyricInactiveColor = SanitizeText(profile.Palette.LyricInactiveColor, 64, "#66FFFFFF");
        profile.Palette.GradientAngle = Clamp(profile.Palette.GradientAngle, 0, 360, 45);

        profile.Lyrics.WidthPercent = Clamp(profile.Lyrics.WidthPercent, 10, 100, 62);
        profile.Lyrics.Spacing = Clamp(profile.Lyrics.Spacing, 0, 80, 24);
        profile.Lyrics.VerticalOffset = Clamp(profile.Lyrics.VerticalOffset, -500, 500, 0);
        profile.Lyrics.InactiveOpacity = Clamp(profile.Lyrics.InactiveOpacity, 0, 1, 0.42);

        profile.Artwork.CornerRadius = Clamp(profile.Artwork.CornerRadius, 0, 60, 18);
        profile.Artwork.BorderThickness = Clamp(profile.Artwork.BorderThickness, 0, 8, 1);
        profile.Artwork.ShadowOpacity = Clamp(profile.Artwork.ShadowOpacity, 0, 1, 0.55);

        profile.Background.Blur = Clamp(profile.Background.Blur, 0, 200, 55);
        profile.Background.Dim = Clamp(profile.Background.Dim, 0, 1, 0.55);
        profile.Background.CoverScale = Clamp(profile.Background.CoverScale, 0.5, 2, 1);
        profile.Background.ParticleIntensity = Clamp(profile.Background.ParticleIntensity, 0, 1, 0.5);

        profile.Animations.WordDurationMs = Clamp(profile.Animations.WordDurationMs, 40, 500, 110);
        profile.Animations.TransitionDurationMs = Clamp(profile.Animations.TransitionDurationMs, 0, 2000, 300);
        profile.Animations.WordIntensity = Clamp(profile.Animations.WordIntensity, 0, 1, 0.65);
        profile.Animations.CoverIntensity = Clamp(profile.Animations.CoverIntensity, 0, 2, 0.5);
    }

    static double SanitizeArtworkSize(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 10, 60) : 26;

    static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    static int Clamp(int value, int min, int max, int fallback) => Math.Clamp(value, min, max);

    static string SanitizeText(string? value, int maxLength, string fallback)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) return fallback;
        return text.Length <= maxLength ? text : fallback;
    }
}
