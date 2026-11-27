using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Glowline.Models;

namespace Glowline;

public static class LyricText
{
    static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);
    static readonly Regex SpaceBeforePunct = new(@"\s+([,.;:!?…])", RegexOptions.Compiled);
    static readonly Regex DoubleSpace = new(@" {2,}", RegexOptions.Compiled);

    public static List<LrcLine> Process(List<LrcLine> lines)
    {
        List<LrcLine>? result = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var hasParen = line.Text.IndexOf('(') >= 0;
            var wordsHaveParen = line.Words is { Count: > 0 } &&
                                 line.Words.Any(w => w.Text.IndexOf('(') >= 0);
            if (!hasParen && !wordsHaveParen)
            {
                result?.Add(line);
                continue;
            }

            result ??= new List<LrcLine>(lines.Take(i));
            var (main, background) = SplitText(line.Text);
            var (mainWords, backgroundWords) = SplitWords(line.Words);
            result.Add(new LrcLine
            {
                Time = line.Time,
                Text = main,
                Background = background,
                Words = mainWords,
                BackgroundWords = backgroundWords,
                Romanized = line.Romanized,
                SourceTime = line.SourceTime,
            });
        }

        return result ?? lines;
    }

    static (string Main, string Background) SplitText(string text)
    {
        var main = new StringBuilder(text.Length);
        var background = new StringBuilder();
        var depth = 0;

        foreach (var ch in text)
        {
            if (ch == '(')
            {
                if (depth == 0 && background.Length > 0 && background[^1] != ' ')
                    background.Append(' ');
                depth++;
                continue;
            }

            if (ch == ')')
            {
                if (depth > 0) depth--;
                continue;
            }

            if (depth > 0) background.Append(ch);
            else main.Append(ch);
        }

        return (Clean(main.ToString()), Clean(background.ToString()));
    }

    static string Clean(string text)
    {
        var cleaned = MultiSpace.Replace(text, " ");
        cleaned = SpaceBeforePunct.Replace(cleaned, "$1");
        return cleaned.Trim();
    }

    static (IReadOnlyList<LrcWord>? Main, IReadOnlyList<LrcWord>? Background) SplitWords(
        IReadOnlyList<LrcWord>? words)
    {
        if (words is null || words.Count == 0) return (words, null);
        if (!words.Any(w => w.Text.IndexOf('(') >= 0)) return (words, null);

        var main = new List<LrcWord>(words.Count);
        var background = new List<LrcWord>();
        var depth = 0;

        foreach (var word in words)
        {
            var mainSb = new StringBuilder(word.Text.Length);
            var backgroundSb = new StringBuilder();
            foreach (var ch in word.Text)
            {
                if (ch == '(')
                {
                    depth++;
                    continue;
                }

                if (ch == ')')
                {
                    if (depth > 0) depth--;
                    continue;
                }

                if (depth > 0) backgroundSb.Append(ch);
                else mainSb.Append(ch);
            }

            var mainText = DoubleSpace.Replace(mainSb.ToString(), " ");
            if (!string.IsNullOrWhiteSpace(mainText))
                main.Add(new LrcWord { Time = word.Time, Text = mainText });

            var backgroundText = DoubleSpace.Replace(backgroundSb.ToString(), " ").Trim();
            if (backgroundText.Length > 0)
            {
                if (background.Count > 0) backgroundText = " " + backgroundText;
                background.Add(new LrcWord { Time = word.Time, Text = backgroundText });
            }
        }

        return (main.Count > 0 ? main : null, background.Count > 0 ? background : null);
    }
}
