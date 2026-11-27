using System;
using System.IO;
using Microsoft.Win32;

namespace Glowline.Services;

/// <summary>
/// One-time migration from the app's former name (before 0.9.40).
/// This is the only place that still knows that name: it moves the user's
/// settings, cache and saved designs to the new data folder, carries the
/// "start with Windows" option over, and lets old exported .glow designs
/// still be imported. Remove it once no supported version uses the old name.
/// </summary>
public static class LegacyMigration
{
    const string FormerName = "LyricGlow";

    /// <summary>Format id written by .glow files exported before 0.9.40.</summary>
    public const string FormerGlowFormatId = "lyricglow.glow";

    public static void Run()
    {
        MoveDataFolder();
        MoveAutostart();
    }

    static void MoveDataFolder()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldDir = Path.Combine(localAppData, FormerName);
            var newDir = StoragePaths.DataDirectory;
            if (!Directory.Exists(oldDir)) return;
            if (string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
                return;

            if (!Directory.Exists(newDir))
            {
                Directory.Move(oldDir, newDir);
                Log.Write("migration: data folder moved to " + newDir);
                return;
            }

            // Both exist (for example, the new version already ran once):
            // copy over only what the new folder doesn't have yet.
            CopyMissing(oldDir, newDir);
            Directory.Delete(oldDir, recursive: true);
            Log.Write("migration: data folder merged into " + newDir);
        }
        catch (Exception ex)
        {
            Log.Write("migration data: " + ex.Message);
        }
    }

    static void CopyMissing(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
        foreach (var dir in Directory.GetDirectories(source))
            CopyMissing(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    static void MoveAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key?.GetValue(FormerName) is null) return;
            key.DeleteValue(FormerName, false);
            Autostart.Set(true);
            Log.Write("migration: start-with-Windows moved to the new name");
        }
        catch (Exception ex)
        {
            Log.Write("migration autostart: " + ex.Message);
        }
    }
}
