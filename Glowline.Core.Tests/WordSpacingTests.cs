using Glowline.Models;
using Glowline.Services;

namespace Glowline.Core.Tests;

static class WordSpacingTests
{
    static List<LrcWord> Words(params string[] texts) =>
        texts.Select((t, i) => new LrcWord { Time = i, Text = t }).ToList();

    [Test]
    static void PiecesOfOneWordAreNotSeparated()
    {
        // "Mucha culera pa' que guardin tu fiche" with "guardin" timed in two pieces.
        var gaps = WordSpacing.GapAfter(Words("Mucha ", "culera ", "pa' ", "que ", "guar", "din ", "tu ", "fiche"));
        Assert.True(gaps[3]);
        Assert.False(gaps[4]);
        Assert.True(gaps[5]);
        Assert.True(gaps[6]);
    }

    [Test]
    static void AMarkOnTheNextPieceAlsoSeparates()
    {
        var gaps = WordSpacing.GapAfter(Words("Mucha", " culera", " pa"));
        Assert.True(gaps[0]);
        Assert.True(gaps[1]);
    }

    [Test]
    static void WithoutAnyMarksEveryPieceIsAWord()
    {
        var gaps = WordSpacing.GapAfter(Words("Mucha", "culera", "pa"));
        Assert.True(gaps[0]);
        Assert.True(gaps[1]);
        Assert.True(gaps[2]);
    }
}
