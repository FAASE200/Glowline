using System;
using System.Collections.Generic;
using System.Globalization;
using LyricGlow.Models;

namespace LyricGlow.Services;

public static class LyricsFileParser
{
    const int MaxInputChars = 512 * 1024;
    const int MaxLines = 2000;
    const int MaxWords = 200;
    const int MaxTextChars = 4096;
    const int MaxRawLineChars = 8192;
    const double MaxTimeSeconds = 24 * 60 * 60;

    public static List<LrcLine>? Parse(string? lyricsFile)
    {
        if (string.IsNullOrWhiteSpace(lyricsFile)) return null;
        if (lyricsFile.Length > MaxInputChars)
        {
            Log.Write("lyricsfile: entrada excede el límite");
            return null;
        }

        var lines = new List<LrcLine>();
        var started = false;
        var invalid = false;

        string? currentText = null;
        double currentStart = -1;
        List<LrcWord>? currentWords = null;
        string? wordText = null;
        double wordStart = -1;
        var totalWords = 0;

        void FlushWord()
        {
            if (currentWords is not null && wordText is not null && wordStart >= 0)
            {
                if (currentWords.Count >= MaxWords || totalWords >= ExternalHttp.MaxLyricWords)
                {
                    invalid = true;
                    return;
                }
                totalWords++;
                currentWords.Add(new LrcWord { Time = wordStart, Text = wordText });
            }
            wordText = null;
            wordStart = -1;
        }

        void FlushLine()
        {
            FlushWord();
            if (invalid) return;
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

        var rawLines = lyricsFile.Replace("\r", "").Split('\n');
        if (rawLines.Length > MaxLines)
        {
            Log.Write("lyricsfile: demasiadas líneas");
            return null;
        }

        foreach (var raw in rawLines)
        {
            if (raw.Length > MaxRawLineChars)
            {
                invalid = true;
                break;
            }
            if (!started)
            {
                if (raw.Trim() == "lines:") started = true;
                continue;
            }

            if (raw.StartsWith("- text:", StringComparison.Ordinal))
            {
                FlushLine();
                if (invalid) break;
                currentText = ExternalHttp.CleanText(YamlValue(raw["- text:".Length..]), MaxTextChars);
                currentWords = new List<LrcWord>();
            }
            else if (raw.StartsWith("  - text:", StringComparison.Ordinal))
            {
                FlushWord();
                if (invalid) break;
                wordText = ExternalHttp.CleanText(YamlValue(raw["  - text:".Length..]), MaxTextChars);
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
        if (invalid)
        {
            Log.Write("lyricsfile: contenido inválido");
            return null;
        }

        lines.RemoveAll(l => l.Text.Length == 0);
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines.Count > 0 ? lines : null;
    }

    static double ParseMs(string raw)
    {
        var value = YamlValue(raw);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) &&
               ExternalHttp.IsFiniteInRange(ms, 0, MaxTimeSeconds * 1000)
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
