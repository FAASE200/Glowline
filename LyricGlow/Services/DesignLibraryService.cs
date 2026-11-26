using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LyricGlow.Models;

namespace LyricGlow.Services;

/// <summary>
/// Biblioteca local de diseños guardados como paquetes .glow en
/// %LocalAppData%/LyricGlow/designs. Solo perfiles y previews: nunca assets remotos ni código.
/// </summary>
public sealed class DesignLibraryService
{
    public string LibraryDirectory { get; }

    public DesignLibraryService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LyricGlow", "designs"))
    {
    }

    public DesignLibraryService(string libraryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryDirectory);
        LibraryDirectory = Path.GetFullPath(libraryDirectory);
    }

    public async Task SaveAsync(DesignProfile profile, byte[]? previewPng = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Directory.CreateDirectory(LibraryDirectory);

        var id = SafeId(profile.Id);
        var target = Path.Combine(LibraryDirectory, id + ".glow");
        await GlowPackageService.ExportAsync(profile, previewPng, target, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DesignProfile>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<DesignProfile>();
        if (!Directory.Exists(LibraryDirectory)) return result;

        string[] files;
        try { files = Directory.GetFiles(LibraryDirectory, "*.glow"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"No se pudo listar la biblioteca de diseños en '{LibraryDirectory}'.", ex);
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            // Un paquete corrupto no debe tumbar la lista completa: se omite.
            try
            {
                var pkg = await GlowPackageService.ImportAsync(file, ct).ConfigureAwait(false);
                result.Add(pkg.Profile);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Write($"[DesignLibrary] Se omite '{Path.GetFileName(file)}': {ex.Message}");
            }
        }

        return result;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = Path.Combine(LibraryDirectory, SafeId(id) + ".glow");

        await Task.Run(() =>
        {
            if (File.Exists(path)) File.Delete(path);
        }, ct).ConfigureAwait(false);
    }

    static string SafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("El diseño debe tener un id.", nameof(id));
        var trimmed = id.Trim();
        // Rechaza ids con rutas o caracteres que puedan escapar de la carpeta de la biblioteca.
        if (trimmed.Contains("..") || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Id de diseño no válido: '{trimmed}'.", nameof(id));
        return trimmed;
    }
}
