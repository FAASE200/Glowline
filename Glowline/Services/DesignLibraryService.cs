using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glowline.Models;

namespace Glowline.Services;

/// <summary>
/// Local library of designs saved as .glow packages in
/// %LocalAppData%/Glowline/designs. Profiles and previews only: never remote assets or code.
/// </summary>
public sealed class DesignLibraryService
{
    public string LibraryDirectory { get; }

    public DesignLibraryService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Glowline", "designs"))
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
            throw new IOException($"Could not list the design library in '{LibraryDirectory}'.", ex);
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            // A corrupt package must not take down the whole list: it is skipped.
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
            throw new ArgumentException("The design must have an id.", nameof(id));
        var trimmed = id.Trim();
        // Rejects ids with paths or characters that could escape the library folder.
        if (trimmed.Contains("..") || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid design id: '{trimmed}'.", nameof(id));
        return trimmed;
    }
}
