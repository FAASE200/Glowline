using System.Collections.Generic;
using Glowline.Models;

namespace Glowline.Services;

/// <summary>
/// Decides where a space goes between the timed pieces of a line. Some sources time
/// a word in pieces (syllables), and putting a space between every piece broke words
/// apart ("guardi n"). Where the source marks spaces, those marks are trusted; when it
/// marks none, every piece is a word.
/// </summary>
public static class WordSpacing
{
    /// <summary>For each piece, whether a space follows it.</summary>
    public static bool[] GapAfter(IReadOnlyList<LrcWord> words)
    {
        var gaps = new bool[words.Count];
        var marked = false;
        foreach (var word in words)
        {
            if (word.Text.Length > 0 && (char.IsWhiteSpace(word.Text[0]) || char.IsWhiteSpace(word.Text[^1])))
            {
                marked = true;
                break;
            }
        }

        for (var i = 0; i < words.Count; i++)
        {
            if (!marked || i == words.Count - 1)
            {
                gaps[i] = true;
                continue;
            }
            var current = words[i].Text;
            var next = words[i + 1].Text;
            gaps[i] = current.Length == 0 || char.IsWhiteSpace(current[^1]) ||
                      (next.Length > 0 && char.IsWhiteSpace(next[0]));
        }
        return gaps;
    }
}
