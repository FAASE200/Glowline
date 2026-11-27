using System.Threading.Tasks;

namespace Glowline.Services;

public sealed class DesktopMediaArtworkProvider : IMediaArtworkProvider
{
    readonly MediaSessionService _media;

    public DesktopMediaArtworkProvider(MediaSessionService media) => _media = media;

    public Task<byte[]?> ReadThumbnailAsync(string? expectedTitle = null, string? expectedArtist = null) =>
        _media.ReadThumbnailAsync(expectedTitle, expectedArtist);
}
