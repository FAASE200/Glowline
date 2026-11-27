using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Glowline.Models;

namespace Glowline;

public static class LyricRender
{
    public static void SetPlain(TextBlock block, LrcLine line, Color background)
    {
        block.Inlines.Clear();
        if (line.Text.Length > 0)
            block.Inlines.Add(new Run(line.Text));
        AppendBackground(block, line, background);
        AppendRomanized(block, line, background);
    }

    /// <summary>
    /// Appends the line's pronunciation in Latin letters (local
    /// romanization) below it, in smaller italic text.
    /// </summary>
    public static void AppendRomanized(TextBlock block, LrcLine line, Color color, double scale = 0.5)
    {
        if (line.Romanized.Length == 0) return;
        if (block.Inlines.Count > 0)
            block.Inlines.Add(new LineBreak());

        block.Inlines.Add(new Run(line.Romanized)
        {
            FontSize = Math.Max(10, block.FontSize * scale),
            FontWeight = FontWeights.Normal,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(color),
        });
    }

    public static void AppendBackground(TextBlock block, LrcLine line, Color color, double scale = 0.56)
    {
        if (line.Background.Length == 0) return;
        if (block.Inlines.Count > 0)
            block.Inlines.Add(new LineBreak());

        block.Inlines.Add(new Run(line.Background)
        {
            FontSize = Math.Max(10, block.FontSize * scale),
            FontWeight = FontWeights.Normal,
            Foreground = new SolidColorBrush(color),
        });
    }
}
