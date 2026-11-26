using System;
using System.Collections.Generic;
using System.Globalization;
using LyricGlow.Models;

namespace LyricGlow.Services;

public static class LyricsFileParser
{
    public static List<LrcLine>? Parse(string? lyricsFile)
    {
        if (string.IsNullOrWhiteSpace(lyricsFile)) return null;

        var lines = new List<LrcLine>();
        var started = false;

        string? currentText = null;
        double currentStart = -1;
        List<LrcWord>? currentWords = null;
        string? wordText = null;
        double wordStart = -1;

        void FlushWord()
        {
            if (currentWords is not null && wordText is not null && wordStart >= 0)
                currentWords.Add(new LrcWord { Time = wordStart, Text = wordText });
            wordText = null;
            wordStart = -1;
        }

        void FlushLine()
        {
            FlushWord();
            if (currentText is not null && currentStart >= 0)
            {
                currentWords?.RemoveAll(w => w.Text.Length == 0);
                lines.Add(new LrcLine
                {
                    Time = currentStart,
                    Text = currentText,
                    Words = currentWords is { Count: > 0 } ? currentWords : null,
                });
            }
            currentText = null;
            currentStart = -1;
            currentWords = null;
        }

        foreach (var raw in lyricsFile.Replace("\r", "").Split('\n'))
        {
            if (!started)
            {
                if (raw.Trim() == "lines:") started = true;
                continue;
            }

            if (raw.StartsWith("- text:", StringComparison.Ordinal))
            {
                FlushLine();
                currentText = YamlValue(raw["- text:".Length..]);
                currentWords = new List<LrcWord>();
            }
            else if (raw.StartsWith("  - text:", StringComparison.Ordinal))
            {
                FlushWord();
                wordText = YamlValue(raw["  - text:".Length..]);
            }
            else if (raw.StartsWith("  start_ms:", StringComparison.Ordinal))
            {
                currentStart = ParseMs(raw["  start_ms:".Length..]);
            }
            else if (raw.StartsWith("    start_ms:", StringComparison.Ordinal))
            {
                wordStart = ParseMs(raw["    start_ms:".Length..]);
            }
        }
        FlushLine();

        lines.RemoveAll(l => l.Text.Length == 0);
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines.Count > 0 ? lines : null;
    }

    static double ParseMs(string raw)
    {
        var value = YamlValue(raw);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            ? ms / 1000.0
            : -1;
    }

    static string YamlValue(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"");
        return value;
    }
}
