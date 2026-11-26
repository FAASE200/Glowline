using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using LyricGlow.Models;

namespace LyricGlow.Services;

public static class LrcParser
{
    static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly Regex OffsetTag = new(@"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex WordTag = new(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled);

    public static List<LrcLine> Parse(string lrc)
    {
        var lines = new List<LrcLine>();
        var offsetMs = 0;

        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var offsetMatch = OffsetTag.Match(raw);
            if (offsetMatch.Success)
            {
                if (int.TryParse(offsetMatch.Groups[1].Value, out var off)) offsetMs = off;
                continue;
            }

            var lineMatches = TimeTag.Matches(raw);
            if (lineMatches.Count == 0) continue;

            var withoutLineTags = TimeTag.Replace(raw, "");
            var text = WordTag.Replace(withoutLineTags, "").Trim();
            if (text.Length == 0) continue;

            IReadOnlyList<LrcWord>? words = null;
            if (lineMatches.Count == 1)
            {
                var wordMatches = WordTag.Matches(withoutLineTags);
                if (wordMatches.Count > 0)
                {
                    var parsedWords = new List<LrcWord>(wordMatches.Count);
                    for (var i = 0; i < wordMatches.Count; i++)
                    {
                        var wordMatch = wordMatches[i];
                        var start = wordMatch.Index + wordMatch.Length;
                        var end = i + 1 < wordMatches.Count ? wordMatches[i + 1].Index : withoutLineTags.Length;
                        var segment = withoutLineTags[start..end];
                        if (segment.Length == 0) continue;
                        if (parsedWords.Count == 0 && wordMatch.Index > 0)
                        {
                            var prefix = withoutLineTags[..wordMatch.Index].Trim();
                            if (prefix.Length > 0) segment = prefix + " " + segment;
                        }
                        parsedWords.Add(new LrcWord { Time = ParseTime(wordMatch, offsetMs), Text = segment });
                    }
                    if (parsedWords.Count > 0) words = parsedWords;
                }
            }

            foreach (Match m in lineMatches)
            {
                lines.Add(new LrcLine { Time = ParseTime(m, offsetMs), Text = text, Words = words });
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));

        var dedup = new List<LrcLine>(lines.Count);
        foreach (var line in lines)
        {
            if (dedup.Count > 0)
            {
                var last = dedup[^1];
                if (Math.Abs(last.Time - line.Time) < 0.001 && last.Text == line.Text) continue;
            }
            dedup.Add(line);
        }

        return dedup;
    }

    static double ParseTime(Match m, int offsetMs)
    {
        var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        double fraction = 0;
        if (m.Groups[3].Success)
        {
            fraction = double.Parse("0." + m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        var time = minutes * 60 + seconds + fraction + offsetMs / 1000.0;
        return time < 0 ? 0 : time;
    }
}
