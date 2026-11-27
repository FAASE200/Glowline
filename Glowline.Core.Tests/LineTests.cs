using Glowline.Models;
using Glowline.Services;

namespace Glowline.Core.Tests;

static class DisplayLineTests
{
    static List<LrcLine> Source() => new()
    {
        new() { Time = 10, Text = "uno", Words = new List<LrcWord> { new() { Time = 10, Text = "uno" } } },
        new() { Time = 12, Text = "사랑해" },
        new() { Time = 14, Text = "tres" },
    };

    [Test]
    static void AppliesLineOffsetAndShiftsWords()
    {
        var offsets = new Dictionary<string, int> { ["10000"] = 500 };
        var lines = LyricsService.BuildDisplayLines(Source(), offsets, romanize: false);
        Assert.Near(10.5, lines[0].Time);
        Assert.Near(10.5, lines[0].Words![0].Time);
        Assert.Near(10, lines[0].SourceTime);
        Assert.Near(12, lines[1].Time);
    }

    [Test]
    static void OffsetCannotJumpPastNeighbours()
    {
        var offsets = new Dictionary<string, int> { ["12000"] = 5000, ["14000"] = -5000 };
        var lines = LyricsService.BuildDisplayLines(Source(), offsets, romanize: false);
        Assert.True(lines[0].Time < lines[1].Time);
        Assert.True(lines[1].Time < lines[2].Time);
        Assert.Near(13.99, lines[1].Time, 1e-9);
    }

    [Test]
    static void RomanizesOnlyWhenEnabled()
    {
        Assert.Equal("", LyricsService.BuildDisplayLines(Source(), null, romanize: false)[1].Romanized);
        Assert.Equal("saranghae", LyricsService.BuildDisplayLines(Source(), null, romanize: true)[1].Romanized);
        Assert.Equal("", LyricsService.BuildDisplayLines(Source(), null, romanize: true)[0].Romanized);
    }

    [Test]
    static void LineIdUsesOriginalTimeAfterAdjusting()
    {
        var offsets = new Dictionary<string, int> { ["10000"] = 300 };
        var adjusted = LyricsService.BuildDisplayLines(Source(), offsets, romanize: false);
        // Rebuilding from already-adjusted lines must not stack the offset.
        var again = LyricsService.BuildDisplayLines(adjusted, offsets, romanize: false);
        Assert.Near(10.3, again[0].Time, 1e-9);
    }
}

static class ManualQueryTests
{
    [Test]
    static void SplitsArtistAndTitle()
    {
        Assert.Equal(("Yellow", "Coldplay"), LyricsService.SplitManualQuery("Coldplay - Yellow", "Otro"));
        Assert.Equal(("Dynamite", "BTS"), LyricsService.SplitManualQuery("BTS – Dynamite", null));
    }

    [Test]
    static void FallsBackToCurrentArtist()
    {
        Assert.Equal(("Yellow", "Coldplay"), LyricsService.SplitManualQuery("  Yellow ", "Coldplay"));
        Assert.Equal(("Yellow", ""), LyricsService.SplitManualQuery("Yellow", null));
    }
}
