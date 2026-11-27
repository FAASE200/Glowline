using System.IO;
using System.Windows.Media.Imaging;

namespace Glowline.Services;

public sealed class DesktopImageDecoder : IImageDecoder
{
    const int MaxBytes = 40 * 1024 * 1024;

    public (int Width, int Height) GetSize(byte[] bytes)
    {
        if (bytes is not { Length: > 0 and <= MaxBytes }) return (0, 0);
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}
