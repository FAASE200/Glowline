using System.Threading.Tasks;
using Glowline.Services;

namespace Glowline.Android.Services;

public sealed class AndroidMediaArtworkProvider : IMediaArtworkProvider
{
    readonly AndroidSessionService _session;

    public AndroidMediaArtworkProvider(AndroidSessionService session) => _session = session;

    public Task<byte[]?> ReadThumbnailAsync(string? expectedTitle = null, string? expectedArtist = null) =>
        _session.ReadThumbnailAsync(expectedTitle, expectedArtist);
}
