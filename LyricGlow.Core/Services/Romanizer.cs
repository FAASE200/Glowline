using System;
using System.Collections.Generic;
using System.Text;

namespace LyricGlow.Services;

/// <summary>
/// Local (offline) romanization shown under each line to show how it is
/// pronounced. Covers Korean (Hangul, simplified Revised Romanization),
/// Japanese written in kana (Hepburn) and the Cyrillic alphabet.
/// Japanese lines with kanji are not romanized: without a dictionary the
/// result would be misleading, so nothing is shown instead.
/// </summary>
public static class Romanizer
{
    /// <summary>
    /// Returns the romanization, or an empty string if the line has no
    /// script that can be romanized (or is Japanese with kanji).
    /// </summary>
    public static string Romanize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var hasHangul = false;
        var hasKana = false;
        var hasCyrillic = false;
        foreach (var ch in text)
        {
            if (IsHangulSyllable(ch) || IsHangulJamo(ch)) hasHangul = true;
            else if (IsKana(ch)) hasKana = true;
            else if (IsCyrillic(ch)) hasCyrillic = true;
        }

        if (!hasHangul && !hasKana && !hasCyrillic) return "";
        // Japanese with kanji: there is no reliable reading without a dictionary.
        if (hasKana && !hasHangul && ContainsHan(text)) return "";

        var sb = new StringBuilder(text.Length * 2);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (IsHangulSyllable(ch))
            {
                var start = i;
                while (i < text.Length && IsHangulSyllable(text[i])) i++;
                AppendHangul(sb, text.AsSpan(start, i - start));
                continue;
            }
            if (IsKana(ch) || (ch == 'ー' && i > 0 && IsKana(text[i - 1])))
            {
                var start = i;
                while (i < text.Length && (IsKana(text[i]) || text[i] == 'ー')) i++;
                AppendKana(sb, text.AsSpan(start, i - start));
                continue;
            }
            if (IsCyrillic(ch))
            {
                AppendCyrillic(sb, ch, i + 1 < text.Length ? text[i + 1] : '\0', i == 0 || !char.IsLetter(text[i - 1]));
                i++;
                continue;
            }

            switch (ch)
            {
                case '、': sb.Append(", "); break;
                case '。': sb.Append(". "); break;
                case '！': sb.Append("! "); break;
                case '？': sb.Append("? "); break;
                case '「' or '」' or '『' or '』': sb.Append('"'); break;
                case '　': sb.Append(' '); break;
                default: sb.Append(ch); break;
            }
            i++;
        }

        var result = CollapseSpaces(sb.ToString());
        return string.Equals(result, text.Trim(), StringComparison.Ordinal) ? "" : result;
    }

    // ---------------------------------------------------------------- Hangul

    static readonly string[] Initials =
    {
        "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h",
    };

    static readonly string[] Vowels =
    {
        "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo",
        "u", "wo", "we", "wi", "yu", "eu", "ui", "i",
    };

    // Final consonant before another consonant or at the end of a word.
    static readonly string[] Finals =
    {
        "", "k", "k", "k", "n", "n", "n", "t", "l", "k", "m", "l", "l", "l", "p", "l",
        "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", "t",
    };

    // Final consonant when the next syllable starts with ㅇ (liaison):
    // the consonant moves to the next syllable with its initial sound.
    static readonly string[] FinalsLinked =
    {
        "", "g", "kk", "ks", "n", "nj", "nh", "d", "r", "lg", "lm", "lb", "ls", "lt", "lp", "lh",
        "m", "b", "ps", "s", "ss", "ng", "j", "ch", "k", "t", "p", "",
    };

    static void AppendHangul(StringBuilder sb, ReadOnlySpan<char> word)
    {
        for (var i = 0; i < word.Length; i++)
        {
            var code = word[i] - 0xAC00;
            var initial = code / (21 * 28);
            var vowel = code % (21 * 28) / 28;
            var final = code % 28;

            sb.Append(Initials[initial]);
            sb.Append(Vowels[vowel]);
            if (final == 0) continue;

            var nextStartsWithSilent = i + 1 < word.Length && (word[i + 1] - 0xAC00) / (21 * 28) == 11;
            if (nextStartsWithSilent && final != 21)
                sb.Append(FinalsLinked[final]);
            else
                sb.Append(Finals[final]);
        }
    }

    // ------------------------------------------------------------------ Kana

    static readonly Dictionary<string, string> KanaMap = BuildKanaMap();

    static Dictionary<string, string> BuildKanaMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string hiragana, string romaji)
        {
            map[hiragana] = romaji;
            map[ToKatakana(hiragana)] = romaji;
        }

        var basic = new (string Kana, string Romaji)[]
        {
            ("あ", "a"), ("い", "i"), ("う", "u"), ("え", "e"), ("お", "o"),
            ("か", "ka"), ("き", "ki"), ("く", "ku"), ("け", "ke"), ("こ", "ko"),
            ("さ", "sa"), ("し", "shi"), ("す", "su"), ("せ", "se"), ("そ", "so"),
            ("た", "ta"), ("ち", "chi"), ("つ", "tsu"), ("て", "te"), ("と", "to"),
            ("な", "na"), ("に", "ni"), ("ぬ", "nu"), ("ね", "ne"), ("の", "no"),
            ("は", "ha"), ("ひ", "hi"), ("ふ", "fu"), ("へ", "he"), ("ほ", "ho"),
            ("ま", "ma"), ("み", "mi"), ("む", "mu"), ("め", "me"), ("も", "mo"),
            ("や", "ya"), ("ゆ", "yu"), ("よ", "yo"),
            ("ら", "ra"), ("り", "ri"), ("る", "ru"), ("れ", "re"), ("ろ", "ro"),
            ("わ", "wa"), ("ゐ", "i"), ("ゑ", "e"), ("を", "o"), ("ん", "n"),
            ("が", "ga"), ("ぎ", "gi"), ("ぐ", "gu"), ("げ", "ge"), ("ご", "go"),
            ("ざ", "za"), ("じ", "ji"), ("ず", "zu"), ("ぜ", "ze"), ("ぞ", "zo"),
            ("だ", "da"), ("ぢ", "ji"), ("づ", "zu"), ("で", "de"), ("ど", "do"),
            ("ば", "ba"), ("び", "bi"), ("ぶ", "bu"), ("べ", "be"), ("ぼ", "bo"),
            ("ぱ", "pa"), ("ぴ", "pi"), ("ぷ", "pu"), ("ぺ", "pe"), ("ぽ", "po"),
            ("ぁ", "a"), ("ぃ", "i"), ("ぅ", "u"), ("ぇ", "e"), ("ぉ", "o"),
            ("ゃ", "ya"), ("ゅ", "yu"), ("ょ", "yo"), ("ゎ", "wa"), ("ゔ", "vu"),
        };
        foreach (var (kana, romaji) in basic) Add(kana, romaji);

        // Combinations with small ゃゅょ (youon).
        var youonBases = new (string Kana, string Stem)[]
        {
            ("き", "ky"), ("ぎ", "gy"), ("し", "sh"), ("じ", "j"), ("ち", "ch"), ("ぢ", "j"),
            ("に", "ny"), ("ひ", "hy"), ("び", "by"), ("ぴ", "py"), ("み", "my"), ("り", "ry"),
        };
        foreach (var (kana, stem) in youonBases)
        {
            Add(kana + "ゃ", stem + "a");
            Add(kana + "ゅ", stem + "u");
            Add(kana + "ょ", stem + "o");
        }

        // Extended combinations common in katakana (loanwords).
        var extended = new (string Kana, string Romaji)[]
        {
            ("ふぁ", "fa"), ("ふぃ", "fi"), ("ふぇ", "fe"), ("ふぉ", "fo"),
            ("てぃ", "ti"), ("でぃ", "di"), ("とぅ", "tu"), ("どぅ", "du"),
            ("うぃ", "wi"), ("うぇ", "we"), ("うぉ", "wo"),
            ("しぇ", "she"), ("じぇ", "je"), ("ちぇ", "che"),
            ("ゔぁ", "va"), ("ゔぃ", "vi"), ("ゔぇ", "ve"), ("ゔぉ", "vo"),
        };
        foreach (var (kana, romaji) in extended) Add(kana, romaji);
        return map;
    }

    static string ToKatakana(string hiragana)
    {
        var chars = hiragana.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'ぁ' and <= 'ゖ') chars[i] = (char)(chars[i] + 0x60);
        }
        return new string(chars);
    }

    static void AppendKana(StringBuilder sb, ReadOnlySpan<char> word)
    {
        var geminate = false;
        for (var i = 0; i < word.Length;)
        {
            var ch = word[i];
            if (ch is 'っ' or 'ッ')
            {
                geminate = true;
                i++;
                continue;
            }

            if (ch == 'ー')
            {
                // Long vowel: repeat the last written vowel.
                for (var k = sb.Length - 1; k >= 0; k--)
                {
                    if ("aeiou".IndexOf(sb[k]) >= 0)
                    {
                        sb.Append(sb[k]);
                        break;
                    }
                }
                i++;
                continue;
            }

            string? romaji = null;
            var length = 0;
            if (i + 1 < word.Length && KanaMap.TryGetValue(word.Slice(i, 2).ToString(), out var pair))
            {
                romaji = pair;
                length = 2;
            }
            else if (KanaMap.TryGetValue(ch.ToString(), out var single))
            {
                romaji = single;
                length = 1;
            }

            if (romaji is null)
            {
                sb.Append(ch);
                i++;
                continue;
            }

            if (geminate)
            {
                sb.Append(romaji.StartsWith("ch", StringComparison.Ordinal) ? 't' : romaji[0]);
                geminate = false;
            }

            // ん before a vowel or y is written n' to keep syllables apart.
            if (romaji == "n" && i + length < word.Length &&
                KanaMap.TryGetValue(word[i + length].ToString(), out var next) &&
                next.Length > 0 && (next[0] is 'a' or 'i' or 'u' or 'e' or 'o' || next[0] == 'y'))
            {
                sb.Append("n'");
            }
            else
            {
                sb.Append(romaji);
            }
            i += length;
        }
    }

    // -------------------------------------------------------------- Cyrillic

    static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        // Ukrainian and Belarusian.
        ['є'] = "ye", ['і'] = "i", ['ї'] = "yi", ['ґ'] = "g", ['ў'] = "w",
    };

    static void AppendCyrillic(StringBuilder sb, char ch, char next, bool wordStart)
    {
        var lower = char.ToLowerInvariant(ch);
        if (!Cyrillic.TryGetValue(lower, out var latin))
        {
            sb.Append(ch);
            return;
        }

        // "е" at the start of a word sounds like "ye" (ель → yel).
        if (lower == 'е' && wordStart) latin = "ye";
        if (latin.Length == 0) return;

        if (char.IsUpper(ch))
        {
            var nextIsUpper = next != '\0' && char.IsUpper(next);
            latin = nextIsUpper
                ? latin.ToUpperInvariant()
                : char.ToUpperInvariant(latin[0]) + latin[1..];
        }
        sb.Append(latin);
    }

    // ------------------------------------------------------------- Helpers

    static bool IsHangulSyllable(char ch) => ch is >= '가' and <= '힣';

    static bool IsHangulJamo(char ch) => ch is >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    static bool IsKana(char ch) =>
        ch is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ';

    static bool IsCyrillic(char ch) => ch is >= 'Ѐ' and <= 'ӿ';

    static bool IsHan(char ch) => ch is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    static bool ContainsHan(string text)
    {
        foreach (var ch in text)
        {
            if (IsHan(ch)) return true;
        }
        return false;
    }

    static string CollapseSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
