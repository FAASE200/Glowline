using LyricGlow.Models;
using LyricGlow.Services;

namespace LyricGlow.Core.Tests;

static class LrcParserTests
{
    [Test]
    static void ParsesLinesInOrder()
    {
        var lines = LrcParser.Parse("[00:12.50]Segunda\n[00:01.00]Primera\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal("Primera", lines[0].Text);
        Assert.Near(1.0, lines[0].Time);
        Assert.Near(12.5, lines[1].Time);
    }

    [Test]
    static void SupportsCentisecondsAndMilliseconds()
    {
        var lines = LrcParser.Parse("[00:01.05]a\n[00:02.123]b\n[00:03]c");
        Assert.Near(1.05, lines[0].Time);
        Assert.Near(2.123, lines[1].Time);
        Assert.Near(3.0, lines[2].Time);
    }

    [Test]
    static void RepeatedTimeTagsCreateOneLineEach()
    {
        var lines = LrcParser.Parse("[00:10.00][00:40.00]Estribillo");
        Assert.Equal(2, lines.Count);
        Assert.Near(10, lines[0].Time);
        Assert.Near(40, lines[1].Time);
        Assert.Equal("Estribillo", lines[1].Text);
    }

    [Test]
    static void AppliesOffsetTagAndClampsAtZero()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:00.20]a\n[00:02.00]b");
        Assert.Near(0.7, lines[0].Time);
        Assert.Near(2.5, lines[1].Time);

        var negative = LrcParser.Parse("[offset:-1000]\n[00:00.50]a");
        Assert.Near(0, negative[0].Time);
    }

    [Test]
    static void IgnoresMetadataAndEmptyLines()
    {
        var lines = LrcParser.Parse("[ar:Artista]\n[ti:Título]\n\n[00:05.00]   \n[00:06.00]Hola");
        Assert.Equal(1, lines.Count);
        Assert.Equal("Hola", lines[0].Text);
    }

    [Test]
    static void ParsesEnhancedWordTimings()
    {
        var lines = LrcParser.Parse("[00:01.00]<00:01.00>Hola <00:01.50>mundo");
        var words = Assert.NotNull(lines[0].Words);
        Assert.Equal(2, words.Count);
        Assert.Equal("Hola ", words[0].Text);
        Assert.Near(1.5, words[1].Time);
        Assert.Equal("Hola mundo", lines[0].Text);
    }

    [Test]
    static void HandlesWindowsLineEndingsAndDuplicates()
    {
        var lines = LrcParser.Parse("[00:01.00]a\r\n[00:01.00]a\r\n[00:02.00]b\r\n");
        Assert.Equal(2, lines.Count);
    }
}

static class LyricsFileParserTests
{
    const string Sample = """
        version: 1
        lines:
        - text: 'Primera línea'
          start_ms: 1000
          words:
          - text: 'Primera '
            start_ms: 1000
          - text: 'línea'
            start_ms: 1400
        - text: "Con \"comillas\" y é"
          start_ms: 3000
        """;

    [Test]
    static void ParsesLinesAndWords()
    {
        var lines = Assert.NotNull(LyricsFileParser.Parse(Sample));
        Assert.Equal(2, lines.Count);
        Assert.Near(1.0, lines[0].Time);
        var words = Assert.NotNull(lines[0].Words);
        Assert.Equal(2, words.Count);
        Assert.Near(1.4, words[1].Time);
    }

    [Test]
    static void UnescapesDoubleQuotedYaml()
    {
        var lines = Assert.NotNull(LyricsFileParser.Parse(Sample));
        Assert.Equal("Con \"comillas\" y é", lines[1].Text);
    }

    [Test]
    static void UnescapeHandlesCommonSequences()
    {
        Assert.Equal("a\\b", LyricsFileParser.UnescapeDoubleQuoted(@"a\\b"));
        Assert.Equal("ñ", LyricsFileParser.UnescapeDoubleQuoted(@"\xF1"));
        Assert.Equal("😀", LyricsFileParser.UnescapeDoubleQuoted(@"\U0001F600"));
        Assert.Equal(@"\q", LyricsFileParser.UnescapeDoubleQuoted(@"\q"));
        Assert.Equal("fin\\", LyricsFileParser.UnescapeDoubleQuoted("fin\\"));
    }

    [Test]
    static void ReturnsNullWithoutLines()
    {
        Assert.Null(LyricsFileParser.Parse(null));
        Assert.Null(LyricsFileParser.Parse("version: 1\n"));
    }
}

static class LyricTextTests
{
    [Test]
    static void SplitsBackgroundVocals()
    {
        var input = new List<LrcLine> { new() { Time = 1, Text = "Te quiero (te quiero)" } };
        var line = LyricText.Process(input)[0];
        Assert.Equal("Te quiero", line.Text);
        Assert.Equal("te quiero", line.Background);
    }

    [Test]
    static void LeavesPlainLinesUntouched()
    {
        var input = new List<LrcLine> { new() { Time = 1, Text = "Sin paréntesis" } };
        Assert.True(ReferenceEquals(input, LyricText.Process(input)));
    }

    [Test]
    static void SplitsBackgroundWords()
    {
        var input = new List<LrcLine>
        {
            new()
            {
                Time = 1,
                Text = "Hola (eco)",
                Words = new List<LrcWord>
                {
                    new() { Time = 1, Text = "Hola " },
                    new() { Time = 1.5, Text = "(eco)" },
                },
            },
        };
        var line = LyricText.Process(input)[0];
        Assert.Equal(1, Assert.NotNull(line.Words).Count);
        Assert.Equal("eco", Assert.NotNull(line.BackgroundWords)[0].Text);
    }
}

static class TrackKeyTests
{
    [Test]
    static void NormalizesCaseAndWhitespace()
    {
        Assert.Equal("hola mundo", TrackKey.Normalize("  Hola   MUNDO "));
        Assert.Equal("", TrackKey.Normalize(null));
    }

    [Test]
    static void KeyIsStableAndRoundsDuration()
    {
        var track = new TrackInfo { Artist = "Artista", Title = "Canción", DurationSeconds = 199.6 };
        Assert.Equal("artista|canción|200", track.Key);
        Assert.True(ReferenceEquals(track.Key, track.Key));
        Assert.Equal("a|b|0", new TrackInfo { Artist = "A", Title = "B" }.Key);
    }
}
