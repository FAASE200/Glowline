using System.IO;
using System.Windows.Media.Imaging;

namespace LyricGlow.Services;

public sealed class DesktopImageDecoder : IImageDecoder
{
    public (int Width, int Height) GetSize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}
