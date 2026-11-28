using Glowline.Localization;
using Glowline.Models;
using Glowline.Services;

namespace Glowline.Core.Tests;

static class TranslationTests
{
    [Test]
    static void ParsesTextAndDetectedLanguage()
    {
        // Raw string: the \n stays as the two characters JSON expects.
        var json = """[[["Hola\n","Hello\n",null,null,10],["mundo","world",null,null,10]],null,"en"]""";
        var (text, detected) = TranslationService.ParseResponse(json);
        Assert.Equal("Hola\nmundo", text);
        Assert.Equal("en", detected);
    }

    [Test]
    static void RejectsUnexpectedReplies()
    {
        Assert.Throws<ExternalDataException>(() => TranslationService.ParseResponse("{}"));
        Assert.Throws<ExternalDataException>(() => TranslationService.ParseResponse("[]"));
    }

    [Test]
    static void SplitsLinesOnlyWhenTheCountMatches()
    {
        var parts = TranslationService.SplitLines("uno \r\n dos\ntres", 3);
        Assert.NotNull(parts);
        Assert.Equal("dos", parts![1]);
        Assert.Null(TranslationService.SplitLines("uno\ndos", 3));
    }

    [Test]
    static void EveryAppLanguageHasACode()
    {
        foreach (AppLanguage language in Enum.GetValues<AppLanguage>())
            Assert.True(TranslationService.LanguageCode(language).Length >= 2);
        Assert.Equal("zh-CN", TranslationService.LanguageCode(AppLanguage.ChineseSimplified));
    }

    [Test]
    static void DisplayLinesCarryTheirTranslation()
    {
        var source = new List<LrcLine>
        {
            new() { Time = 1, Text = "one" },
            new() { Time = 2, Text = "two" },
        };
        var lines = LyricsService.BuildDisplayLines(source, null, romanize: false, new[] { "uno", "" });
        Assert.Equal("uno", lines[0].Translation);
        Assert.Equal("", lines[1].Translation);
        Assert.Equal("", LyricsService.BuildDisplayLines(source, null, romanize: false)[0].Translation);
    }
}

static class MyMemoryTests
{
    static System.Text.Json.JsonElement Parse(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement;

    [Test]
    static void ReadsTheTranslatedText()
    {
        var (text, detected) = TranslationService.ParseMyMemory(Parse(
            """{"responseData":{"translatedText":"Te quiero &amp; mucho"},"quotaFinished":false,"responseStatus":200}"""));
        Assert.Equal("Te quiero & mucho", text);
        Assert.Null(detected);
    }

    [Test]
    static void QuotaAndErrorsAreRejected()
    {
        Assert.Throws<ExternalDataException>(() => TranslationService.ParseMyMemory(Parse(
            """{"responseData":{"translatedText":"x"},"quotaFinished":true,"responseStatus":200}""")));
        Assert.Throws<ExternalDataException>(() => TranslationService.ParseMyMemory(Parse(
            """{"responseData":{"translatedText":"x"},"responseStatus":"403"}""")));
        Assert.Throws<ExternalDataException>(() => TranslationService.ParseMyMemory(Parse("{}")));
    }
}
