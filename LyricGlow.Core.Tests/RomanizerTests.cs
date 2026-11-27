using LyricGlow.Services;

namespace LyricGlow.Core.Tests;

static class RomanizerTests
{
    [Test]
    static void RomanizesKorean()
    {
        Assert.Equal("saranghae", Romanizer.Romanize("사랑해"));
        Assert.Equal("annyeonghaseyo", Romanizer.Romanize("안녕하세요"));
        Assert.Equal("joayo", Romanizer.Romanize("좋아요"));
        Assert.Equal("eumak", Romanizer.Romanize("음악"));
        Assert.Equal("neoui moksori (oh)", Romanizer.Romanize("너의 목소리 (oh)"));
    }

    [Test]
    static void RomanizesKana()
    {
        Assert.Equal("arigatou", Romanizer.Romanize("ありがとう"));
        Assert.Equal("kyatto", Romanizer.Romanize("きゃっと"));
        Assert.Equal("koohii", Romanizer.Romanize("コーヒー"));
        Assert.Equal("kon'ya", Romanizer.Romanize("こんや"));
        Assert.Equal("matcha", Romanizer.Romanize("まっちゃ"));
        Assert.Equal("fantajii, ne.", Romanizer.Romanize("ファンタジー、ね。"));
    }

    [Test]
    static void SkipsJapaneseWithKanji()
    {
        Assert.Equal("", Romanizer.Romanize("東京へ行こう"));
    }

    [Test]
    static void RomanizesCyrillic()
    {
        Assert.Equal("Privet, mir", Romanizer.Romanize("Привет, мир"));
        Assert.Equal("Rossiya", Romanizer.Romanize("Россия"));
        Assert.Equal("yel", Romanizer.Romanize("ель"));
        Assert.Equal("SSSR", Romanizer.Romanize("СССР"));
    }

    [Test]
    static void IgnoresLatinText()
    {
        Assert.Equal("", Romanizer.Romanize("Hello world"));
        Assert.Equal("", Romanizer.Romanize("Canción de amor"));
        Assert.Equal("", Romanizer.Romanize(""));
        Assert.Equal("", Romanizer.Romanize(null));
    }
}
