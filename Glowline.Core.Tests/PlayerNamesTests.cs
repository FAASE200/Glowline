using Glowline.Services;

namespace Glowline.Core.Tests;

static class PlayerNamesTests
{
    [Test]
    static void KnownPlayersGetTheirFamiliarName()
    {
        Assert.Equal("Apple Music", PlayerNames.Friendly("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"));
        Assert.Equal("Spotify", PlayerNames.Friendly("Spotify.exe"));
        Assert.Equal("Spotify", PlayerNames.Friendly("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"));
        Assert.Equal("Google Chrome", PlayerNames.Friendly("Chrome"));
        Assert.Equal("Microsoft Edge", PlayerNames.Friendly("MSEdge"));
        Assert.Equal("Brave", PlayerNames.Friendly("Brave"));
    }

    [Test]
    static void ITunesIsNotCalledAppleMusic()
    {
        Assert.Equal("iTunes", PlayerNames.Friendly("AppleInc.iTunes_nzyj5cx40ttqa!iTunes"));
    }

    [Test]
    static void UnknownPlayersAreTidiedUp()
    {
        Assert.Equal("Cider", PlayerNames.Friendly("Cider.exe"));
        Assert.Equal("Player", PlayerNames.Friendly("Some.Player_abc123!App"));
    }

    [Test]
    static void EmptyIdGivesAnEmptyName()
    {
        Assert.Equal("", PlayerNames.Friendly(""));
        Assert.Equal("", PlayerNames.Friendly(null));
        Assert.Equal("", PlayerNames.Friendly("   "));
    }
}
