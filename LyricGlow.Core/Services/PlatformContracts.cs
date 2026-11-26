using System;
using System.IO;
using System.Threading.Tasks;
using LyricGlow.Models;

namespace LyricGlow.Services;

public interface IAppSettingsStore
{
    AppSettings Settings { get; }
    void ScheduleSave();
}

public interface IMediaArtworkProvider
{
    Task<byte[]?> ReadThumbnailAsync(string? expectedTitle = null, string? expectedArtist = null);
}

public interface IImageDecoder
{
    (int Width, int Height) GetSize(byte[] bytes);
}

public static class StoragePaths
{
    public static string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricGlow");

    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");
}
