using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glowline.Models;

namespace Glowline.Services;

public sealed class GlowPackage
{
    public required DesignProfile Profile { get; init; }
    public byte[]? PreviewPng { get; init; }
}

/// <summary>
/// Lee y escribe el formato .glow (ZIP: manifest.json + preview.png opcional).
/// La validación es intencionalmente estricta: un paquete importado nunca debe
/// poder llevar valores imposibles ni rutas maliciosas al diseño activo.
/// </summary>
public static class GlowPackageService
{
    public const string FormatId = "glowline.glow";
    public const int SchemaVersion = 1;

    const long MaxZipBytes = 25L * 1024 * 1024;
    const long MaxUncompressedBytes = 64L * 1024 * 1024;
    const long MaxManifestBytes = 1024 * 1024;
    const long MaxPreviewBytes = 8L * 1024 * 1024;
    const int MaxEntries = 32;
    const string ManifestName = "manifest.json";
    const string PreviewName = "preview.png";
    static readonly HashSet<string> ValidFontWeights = new(StringComparer.Ordinal)
    {
        "Light", "Normal", "Medium", "SemiBold", "Bold", "Black",
    };

    public static async Task ExportAsync(DesignProfile profile, byte[]? previewPng, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (profile.Name.Length > 200 || profile.Author.Length > 200 || profile.Description.Length > 2000)
            throw new InvalidDataException("El nombre, autor o descripción del diseño superan la longitud máxima permitida.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(dir)) throw new IOException($"Ruta de destino no válida: '{path}'.");
        Directory.CreateDirectory(dir);

        if (previewPng is { Length: > 0 })
        {
            if (previewPng.Length > MaxPreviewBytes)
                throw new InvalidDataException("La miniatura supera el tamaño máximo permitido.");
            if (!IsPng(previewPng))
                throw new InvalidDataException("La miniatura no es un PNG válido.");
        }

        var json = BuildManifestJson(profile);
        ParseManifest(json);
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       0x10000, useAsync: true))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteEntryAsync(zip, ManifestName, Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
                if (previewPng is { Length: > 0 })
                    await WriteEntryAsync(zip, PreviewName, previewPng, ct).ConfigureAwait(false);
            }

            if (File.Exists(path)) File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            else File.Move(tempPath, path);
            tempPath = null;
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { /* el temporal puede no haberse creado */ }
            }
        }
    }

    public static async Task<GlowPackage> ImportAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("El paquete de diseño no existe.", fullPath);

        var info = new FileInfo(fullPath);
        if (info.Length > MaxZipBytes)
            throw new InvalidDataException($"El archivo supera el límite de {MaxZipBytes / (1024 * 1024)} MB.");

        // Todo se lee en memoria: un paquete inválido no deja nada extraído en disco.
        byte[] zipBytes = await Task.Run(() => File.ReadAllBytes(fullPath), ct).ConfigureAwait(false);

        using var ms = new MemoryStream(zipBytes, writable: false);
        using var zip = OpenValidatedArchive(ms, ct);

        var manifestEntry = zip.Entries[0]; // la entrada 0 está garantizada ser manifest.json por OpenValidatedArchive
        string manifestJson;
        using (var reader = new StreamReader(ReadEntryCapped(manifestEntry, MaxManifestBytes, ManifestName), Encoding.UTF8))
            manifestJson = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        var profile = ParseManifest(manifestJson);

        byte[]? preview = null;
        var previewEntry = zip.GetEntry(PreviewName);
        if (previewEntry is not null)
        {
            using (var stream = ReadEntryCapped(previewEntry, MaxPreviewBytes, PreviewName))
                preview = stream.ToArray();
            if (!IsPng(preview))
                throw new InvalidDataException("La miniatura preview.png no es un PNG válido.");
        }

        return new GlowPackage { Profile = profile, PreviewPng = preview };
    }

    static async Task WriteEntryAsync(ZipArchive zip, string name, byte[] data, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    static ZipArchive OpenValidatedArchive(MemoryStream ms, CancellationToken ct)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("El archivo no es un paquete .glow válido.", ex);
        }

        using (zip)
        {
            if (zip.Entries.Count == 0 || zip.Entries.Count > MaxEntries)
                throw new InvalidDataException($"El paquete debe tener entre 1 y {MaxEntries} entradas (tiene {zip.Entries.Count}).");

            if (!string.Equals(zip.Entries[0].FullName, ManifestName, StringComparison.Ordinal))
                throw new InvalidDataException($"La primera entrada debe ser '{ManifestName}'.");

            long total = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.Contains("..") || name.Contains('\\') || name.Contains('/')
                    || (name.IndexOf(':') >= 0) || !names.Add(name))
                    throw new InvalidDataException($"Ruta no permitida dentro del paquete: '{name}'.");
                if (!string.Equals(name, ManifestName, StringComparison.Ordinal)
                    && !string.Equals(name, PreviewName, StringComparison.Ordinal))
                    throw new InvalidDataException($"Entrada desconocida en el paquete: '{name}'.");
                if (entry.Length > MaxUncompressedBytes)
                    throw new InvalidDataException("Alguna entrada supera el tamaño descomprimido permitido.");
                total += entry.Length;
                ct.ThrowIfCancellationRequested();
            }
            if (total > MaxUncompressedBytes)
                throw new InvalidDataException($"El contenido descomprimido supera el límite de {MaxUncompressedBytes / (1024 * 1024)} MB.");
        }

        // El bloque using anterior solo validaba; se reabre para la lectura real.
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);
    }

    static MemoryStream ReadEntryCapped(ZipArchiveEntry entry, long cap, string label)
    {
        var buffer = new MemoryStream();
        using (var stream = entry.Open())
        {
            var chunk = new byte[0x10000];
            long read = 0;
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                read += n;
                if (read > cap)
                    throw new InvalidDataException($"'{label}' supera el tamaño máximo permitido.");
                buffer.Write(chunk, 0, n);
            }
        }
        buffer.Position = 0;
        return buffer;
    }

    static string BuildManifestJson(DesignProfile p)
    {
        var sb = new StringBuilder(2048);
        sb.Append('{');
        Str(sb, "format", FormatId); sb.Append(',');
        sb.Append("\"schemaVersion\":").Append(SchemaVersion).Append(',');
        sb.Append("\"exportedAt\":\"").Append(DateTimeOffset.UtcNow.ToString("O")).Append("\",");
        sb.Append("\"profile\":");
        AppendProfile(sb, p);
        sb.Append('}');
        return sb.ToString();
    }

    static void AppendProfile(StringBuilder sb, DesignProfile p)
    {
        sb.Append('{');
        Num(sb, "schemaVersion", p.SchemaVersion); sb.Append(',');
        Str(sb, "id", p.Id); sb.Append(',');
        Str(sb, "name", p.Name); sb.Append(',');
        Str(sb, "author", p.Author); sb.Append(',');
        Str(sb, "description", p.Description); sb.Append(',');
        Str(sb, "layout", p.Layout.ToString()); sb.Append(',');

        sb.Append("\"palette\":{");
        Str(sb, "backgroundColor", p.Palette.BackgroundColor); sb.Append(',');
        Str(sb, "backgroundSecondaryColor", p.Palette.BackgroundSecondaryColor); sb.Append(',');
        Num(sb, "gradientAngle", p.Palette.GradientAngle); sb.Append(',');
        Str(sb, "textColor", p.Palette.TextColor); sb.Append(',');
        Str(sb, "secondaryTextColor", p.Palette.SecondaryTextColor); sb.Append(',');
        Str(sb, "accentColor", p.Palette.AccentColor); sb.Append(',');
        Str(sb, "lyricActiveColor", p.Palette.LyricActiveColor); sb.Append(',');
        Str(sb, "lyricInactiveColor", p.Palette.LyricInactiveColor); sb.Append(',');
        Bool(sb, "autoContrast", p.Palette.AutoContrast); sb.Append(',');
        Bool(sb, "coverFusion", p.Palette.CoverFusion);
        sb.Append("},\"typography\":{");
        Str(sb, "fontFamily", p.Typography.FontFamily); sb.Append(',');
        Str(sb, "fontWeight", p.Typography.FontWeight); sb.Append(',');
        Num(sb, "titleSize", p.Typography.TitleSize); sb.Append(',');
        Num(sb, "artistSize", p.Typography.ArtistSize); sb.Append(',');
        Num(sb, "lyricSize", p.Typography.LyricSize); sb.Append(',');
        Num(sb, "lineHeight", p.Typography.LineHeight);
        sb.Append("},\"lyrics\":{");
        Str(sb, "alignment", p.Lyrics.Alignment.ToString()); sb.Append(',');
        Num(sb, "widthPercent", p.Lyrics.WidthPercent); sb.Append(',');
        Num(sb, "spacing", p.Lyrics.Spacing); sb.Append(',');
        Num(sb, "verticalOffset", p.Lyrics.VerticalOffset); sb.Append(',');
        Num(sb, "inactiveOpacity", p.Lyrics.InactiveOpacity); sb.Append(',');
        Bool(sb, "showBackingVocals", p.Lyrics.ShowBackingVocals); sb.Append(',');
        Bool(sb, "introDots", p.Lyrics.IntroDots); sb.Append(',');
        Bool(sb, "interludeSeparation", p.Lyrics.InterludeSeparation);
        sb.Append("},\"artwork\":{");
        Bool(sb, "visible", p.Artwork.Visible); sb.Append(',');
        Num(sb, "sizePercent", p.Artwork.SizePercent); sb.Append(',');
        Num(sb, "animatedSizePercent", p.Artwork.AnimatedSizePercent); sb.Append(',');
        Num(sb, "cornerRadius", p.Artwork.CornerRadius); sb.Append(',');
        Num(sb, "borderThickness", p.Artwork.BorderThickness); sb.Append(',');
        Num(sb, "shadowOpacity", p.Artwork.ShadowOpacity); sb.Append(',');
        Bool(sb, "fusion", p.Artwork.Fusion);
        sb.Append("},\"background\":{");
        Str(sb, "kind", p.Background.Kind.ToString()); sb.Append(',');
        Num(sb, "blur", p.Background.Blur); sb.Append(',');
        Num(sb, "dim", p.Background.Dim); sb.Append(',');
        Num(sb, "coverScale", p.Background.CoverScale); sb.Append(',');
        Str(sb, "particles", p.Background.Particles.ToString()); sb.Append(',');
        Num(sb, "particleIntensity", p.Background.ParticleIntensity); sb.Append(',');
        Bool(sb, "beatReactive", p.Background.BeatReactive);
        sb.Append("},\"animations\":{");
        Str(sb, "wordAnimation", p.Animations.WordAnimation.ToString()); sb.Append(',');
        Num(sb, "wordDurationMs", p.Animations.WordDurationMs); sb.Append(',');
        Num(sb, "wordIntensity", p.Animations.WordIntensity); sb.Append(',');
        Str(sb, "verseTransition", p.Animations.VerseTransition.ToString()); sb.Append(',');
        Num(sb, "transitionDurationMs", p.Animations.TransitionDurationMs); sb.Append(',');
        Str(sb, "coverAnimation", p.Animations.CoverAnimation.ToString()); sb.Append(',');
        Num(sb, "coverIntensity", p.Animations.CoverIntensity); sb.Append(',');
        Str(sb, "easing", p.Animations.Easing.ToString());
        sb.Append("},\"controls\":{");
        Bool(sb, "showTransport", p.Controls.ShowTransport); sb.Append(',');
        Bool(sb, "showProgress", p.Controls.ShowProgress); sb.Append(',');
        Bool(sb, "showMetadata", p.Controls.ShowMetadata); sb.Append(',');
        Bool(sb, "showTitle", p.Controls.ShowTitle); sb.Append(',');
        Num(sb, "cornerRadius", p.Controls.CornerRadius); sb.Append(',');
        Str(sb, "chromeStyle", p.Controls.ChromeStyle.ToString());
        sb.Append("},");
        Bool(sb, "reduceMotion", p.ReduceMotion);
        sb.Append('}');
    }

    static void Str(StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    static void Num(StringBuilder sb, string key, double value)
    {
        sb.Append('"').Append(key).Append("\":")
          .Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    static void Bool(StringBuilder sb, string key, bool value)
    {
        sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");
    }

    static DesignProfile ParseManifest(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, ExternalHttp.JsonOptions); }
        catch (JsonException ex)
        {
            throw new InvalidDataException("El manifiesto no es JSON válido.", ex);
        }

        using (doc)
        {
            var root = RequireObject(doc.RootElement, "$");

            var format = GetRequiredString(root, "format");
            if (!string.Equals(format, FormatId, StringComparison.Ordinal) &&
                !string.Equals(format, LegacyMigration.FormerGlowFormatId, StringComparison.Ordinal))
                throw new InvalidDataException($"Formato no reconocido: '{format}'.");

            var schema = GetRequiredInt(root, "schemaVersion");
            if (schema != SchemaVersion)
                throw new InvalidDataException($"schemaVersion {schema} no soportada (se esperaba {SchemaVersion}).");

            _ = GetRequiredString(root, "exportedAt");

            if (!root.TryGetProperty("profile", out var profileElement))
                throw new InvalidDataException("El manifiesto carece del objeto obligatorio 'profile'.");
            var p = RequireObject(profileElement, "profile");
            var profile = new DesignProfile
            {
                SchemaVersion = SchemaVersion,
                Id = SafeDesignId(GetString(p, "id", "")),
                Name = ClampLength(GetString(p, "name", "Mi diseño"), 200),
                Author = ClampLength(GetString(p, "author", ""), 200),
                Description = ClampLength(GetString(p, "description", ""), 2000),
                Layout = GetEnum<DesignLayout>(p, "layout", DesignLayout.Adaptive),
                ReduceMotion = GetBool(p, "reduceMotion", false),
            };

            if (TryGetObject(p, "palette", out var palette))
            {
                profile.Palette.BackgroundColor = GetColor(palette, "backgroundColor", profile.Palette.BackgroundColor);
                profile.Palette.BackgroundSecondaryColor = GetColor(palette, "backgroundSecondaryColor", profile.Palette.BackgroundSecondaryColor);
                profile.Palette.GradientAngle = GetDouble(palette, "gradientAngle", 0, 360, profile.Palette.GradientAngle);
                profile.Palette.TextColor = GetColor(palette, "textColor", profile.Palette.TextColor);
                profile.Palette.SecondaryTextColor = GetColor(palette, "secondaryTextColor", profile.Palette.SecondaryTextColor);
                profile.Palette.AccentColor = GetColor(palette, "accentColor", profile.Palette.AccentColor);
                profile.Palette.LyricActiveColor = GetColor(palette, "lyricActiveColor", profile.Palette.LyricActiveColor);
                profile.Palette.LyricInactiveColor = GetColor(palette, "lyricInactiveColor", profile.Palette.LyricInactiveColor);
                profile.Palette.AutoContrast = GetBool(palette, "autoContrast", profile.Palette.AutoContrast);
                profile.Palette.CoverFusion = GetBool(palette, "coverFusion", profile.Palette.CoverFusion);
            }

            if (TryGetObject(p, "typography", out var typo))
            {
                profile.Typography.FontFamily = ClampLength(GetString(typo, "fontFamily", profile.Typography.FontFamily), 128);
                var weight = GetString(typo, "fontWeight", profile.Typography.FontWeight);
                profile.Typography.FontWeight = ValidFontWeights.Contains(weight)
                    ? weight
                    : profile.Typography.FontWeight;
                profile.Typography.TitleSize = GetDouble(typo, "titleSize", 14, 48, profile.Typography.TitleSize);
                profile.Typography.ArtistSize = GetDouble(typo, "artistSize", 10, 32, profile.Typography.ArtistSize);
                profile.Typography.LyricSize = GetDouble(typo, "lyricSize", 16, 64, profile.Typography.LyricSize);
                profile.Typography.LineHeight = GetDouble(typo, "lineHeight", 0.8, 2.0, profile.Typography.LineHeight);
            }

            if (TryGetObject(p, "lyrics", out var lyrics))
            {
                profile.Lyrics.Alignment = GetEnum<DesignAlignment>(lyrics, "alignment", profile.Lyrics.Alignment);
                profile.Lyrics.WidthPercent = GetDouble(lyrics, "widthPercent", 20, 100, profile.Lyrics.WidthPercent);
                profile.Lyrics.Spacing = GetDouble(lyrics, "spacing", 0, 80, profile.Lyrics.Spacing);
                profile.Lyrics.VerticalOffset = GetDouble(lyrics, "verticalOffset", -200, 200, profile.Lyrics.VerticalOffset);
                profile.Lyrics.InactiveOpacity = GetDouble(lyrics, "inactiveOpacity", 0, 1, profile.Lyrics.InactiveOpacity);
                profile.Lyrics.ShowBackingVocals = GetBool(lyrics, "showBackingVocals", profile.Lyrics.ShowBackingVocals);
                profile.Lyrics.IntroDots = GetBool(lyrics, "introDots", profile.Lyrics.IntroDots);
                profile.Lyrics.InterludeSeparation = GetBool(lyrics, "interludeSeparation", profile.Lyrics.InterludeSeparation);
            }

            if (TryGetObject(p, "artwork", out var artwork))
            {
                profile.Artwork.Visible = GetBool(artwork, "visible", profile.Artwork.Visible);
                profile.Artwork.SizePercent = GetDouble(artwork, "sizePercent", 10, 60, profile.Artwork.SizePercent);
                profile.Artwork.AnimatedSizePercent = GetDouble(artwork, "animatedSizePercent", 10, 60, profile.Artwork.SizePercent);
                profile.Artwork.CornerRadius = GetDouble(artwork, "cornerRadius", 0, 60, profile.Artwork.CornerRadius);
                profile.Artwork.BorderThickness = GetDouble(artwork, "borderThickness", 0, 8, profile.Artwork.BorderThickness);
                profile.Artwork.ShadowOpacity = GetDouble(artwork, "shadowOpacity", 0, 1, profile.Artwork.ShadowOpacity);
                profile.Artwork.Fusion = GetBool(artwork, "fusion", profile.Artwork.Fusion);
            }

            if (TryGetObject(p, "background", out var bg))
            {
                profile.Background.Kind = GetEnum<BackgroundKind>(bg, "kind", profile.Background.Kind);
                profile.Background.Blur = GetDouble(bg, "blur", 0, 120, profile.Background.Blur);
                profile.Background.Dim = GetDouble(bg, "dim", 0, 1, profile.Background.Dim);
                profile.Background.CoverScale = GetDouble(bg, "coverScale", 0.5, 2, profile.Background.CoverScale);
                profile.Background.Particles = GetEnum<ParticleKind>(bg, "particles", profile.Background.Particles);
                profile.Background.ParticleIntensity = GetDouble(bg, "particleIntensity", 0, 2, profile.Background.ParticleIntensity);
                profile.Background.BeatReactive = GetBool(bg, "beatReactive", profile.Background.BeatReactive);
            }

            if (TryGetObject(p, "animations", out var anim))
            {
                profile.Animations.WordAnimation = GetEnum<LyricAnimation>(anim, "wordAnimation", profile.Animations.WordAnimation);
                profile.Animations.WordDurationMs = (int)GetIntInRange(anim, "wordDurationMs", 40, 400, profile.Animations.WordDurationMs);
                profile.Animations.WordIntensity = GetDouble(anim, "wordIntensity", 0, 1, profile.Animations.WordIntensity);
                profile.Animations.VerseTransition = GetEnum<VerseTransition>(anim, "verseTransition", profile.Animations.VerseTransition);
                profile.Animations.TransitionDurationMs = (int)GetIntInRange(anim, "transitionDurationMs", 100, 1200, profile.Animations.TransitionDurationMs);
                profile.Animations.CoverAnimation = GetEnum<CoverAnimation>(anim, "coverAnimation", profile.Animations.CoverAnimation);
                profile.Animations.CoverIntensity = GetDouble(anim, "coverIntensity", 0, 2, profile.Animations.CoverIntensity);
                profile.Animations.Easing = GetEnum<EasingKind>(anim, "easing", profile.Animations.Easing);
            }

            if (TryGetObject(p, "controls", out var ctl))
            {
                profile.Controls.ShowTransport = GetBool(ctl, "showTransport", profile.Controls.ShowTransport);
                profile.Controls.ShowProgress = GetBool(ctl, "showProgress", profile.Controls.ShowProgress);
                profile.Controls.ShowMetadata = GetBool(ctl, "showMetadata", profile.Controls.ShowMetadata);
                profile.Controls.ShowTitle = GetBool(ctl, "showTitle", profile.Controls.ShowTitle);
                profile.Controls.CornerRadius = GetDouble(ctl, "cornerRadius", 0, 999, profile.Controls.CornerRadius);
                profile.Controls.ChromeStyle = GetEnum<ChromeStyle>(ctl, "chromeStyle", profile.Controls.ChromeStyle);
            }

            return profile;
        }
    }

    static JsonElement RequireObject(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"'{name}' debe ser un objeto JSON.");
        return el;
    }

    static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (!parent.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;
        RequireObject(el, name);
        value = el;
        return true;
    }

    static string GetRequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"El manifiesto carece del campo obligatorio '{name}'.");
        return el.GetString()!;
    }

    static int GetRequiredInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number
            || !el.TryGetInt32(out var v))
            throw new InvalidDataException($"El campo '{name}' debe ser un entero.");
        return v;
    }

    static string GetString(JsonElement parent, string name, string fallback)
    {
        if (!parent.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind == JsonValueKind.String ? el.GetString() ?? fallback : fallback;
    }

    static bool GetBool(JsonElement parent, string name, bool fallback)
    {
        if (!parent.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"El campo '{name}' debe ser booleano."),
        };
    }

    static double GetDouble(JsonElement parent, string name, double min, double max, double fallback)
    {
        if (!parent.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v) || !double.IsFinite(v))
            throw new InvalidDataException($"El campo '{name}' debe ser un número finito.");
        if (v < min || v > max)
            throw new InvalidDataException($"El campo '{name}' está fuera del rango [{min}, {max}] (valor: {v.ToString(CultureInfo.InvariantCulture)}).");
        return v;
    }

    static long GetIntInRange(JsonElement parent, string name, long min, long max, long fallback)
    {
        if (!parent.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new InvalidDataException($"El campo '{name}' debe ser un entero.");
        if (v < min || v > max)
            throw new InvalidDataException($"El campo '{name}' está fuera del rango [{min}, {max}].");
        return v;
    }

    static TEnum GetEnum<TEnum>(JsonElement parent, string name, TEnum fallback) where TEnum : struct, Enum
    {
        var raw = GetString(parent, name, "");
        if (raw.Length == 0) return fallback;
        if (Enum.TryParse<TEnum>(raw, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidDataException($"Valor de enum desconocido para '{name}': '{raw}'.");
    }

    static string GetColor(JsonElement parent, string name, string fallback)
    {
        var value = GetString(parent, name, fallback);
        if (!IsValidHexColor(value))
            throw new InvalidDataException($"El campo '{name}' no es un color hexadecimal válido: '{value}'.");
        return value;
    }

    static bool IsValidHexColor(string value)
    {
        if (value.Length is not (7 or 9) || value[0] != '#') return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        }
        return true;
    }

    static bool IsPng(byte[] data) =>
        data is { Length: > 24 } &&
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
        data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    static string ClampLength(string value, int max) => value.Length <= max ? value : value[..max];

    static string SafeDesignId(string value) =>
        Guid.TryParseExact(value, "N", out var id) ? id.ToString("N") : Guid.NewGuid().ToString("N");
}
