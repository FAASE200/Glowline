using System;
using System.Text.Json;
using LyricGlow.Models;

namespace LyricGlow.Services;

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
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Mi diseño" : profile.Name;
        profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id;
    }

    static double SanitizeArtworkSize(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 10, 60) : 26;
}
