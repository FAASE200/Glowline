using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LyricGlow.Localization;
using LyricGlow.Models;
using LyricGlow.Rendering;
using LyricGlow.Services;

namespace LyricGlow;

// Debug helpers used by SelfTest and automated snapshots.
public partial class FullscreenWindow
{
    public void DebugSetAnimatedReady(bool ready)
    {
        _animatedReady = ready;
        UpdateLightArtSize();
    }

    public string DebugCoverSides() =>
        $"static={App.Designs.Active.Artwork.SizePercent:0} animated={App.Designs.Active.Artwork.AnimatedSizePercent:0} " +
        $"light={LightCoverBorder.Width:0} dark={DarkCoverBorder.Width:0} panel={DarkInfoPanel.Width:0} " +
        $"lightProgress={LightProgress.Width:0} darkProgress={DarkProgress.Width:0} active={_animatedReady}";

    public string DebugState()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"open={_isOpen} state={_lyrics.State} design={App.Designs.Active.Layout} theme={(_centeredTheme ? _fusionLight ? "fusion-light" : "fusion-dark" : "lateral-dark")} idx={_currentLine} ");
        sb.Append($"scroll={_scrollOffset:F1} host={LyricsHost.ActualWidth:F0}x{LyricsHost.ActualHeight:F0} blocks={_blocks.Count}");
        if (_blocks.Count > 0 && _currentLine >= 0 && _currentLine < _blocks.Count)
        {
            sb.Append($" topCur={Canvas.GetTop(_blocks[_currentLine]):F0} hCur={_heights[_currentLine]:F0} target={CenterTarget():F0}");
        }
        return sb.ToString();
    }

    public string DebugBackdrop()
    {
        string BrushName(Brush? brush) => brush switch
        {
            SolidColorBrush s => $"solid:{s.Color}",
            LinearGradientBrush => "gradient",
            null => "null",
            _ => brush.GetType().Name,
        };
        return $"backdrop op={BackdropImage.Opacity:0.###} src={BackdropImage.Source is not null} " +
               $"vis={BackdropImage.Visibility} effect={BackdropImage.Effect?.GetType().Name ?? "none"} " +
               $"lightBackdrop op={LightBackdropImage.Opacity:0.###} src={LightBackdropImage.Source is not null} " +
               $"effect={LightBackdropImage.Effect?.GetType().Name ?? "none"} " +
               $"dark={BrushName(DarkLayout.Background)} light={BrushName(LightLayout.Background)} " +
               $"lightVis={LightLayout.Visibility} root={BrushName(RootGrid.Background)}";
    }

    public string DebugHorizontalLayout()
    {
        var hostOrigin = LyricsHost.TransformToAncestor(RootGrid).Transform(new Point(0, 0));
        var centerDelta = hostOrigin.X + LyricsHost.ActualWidth / 2 - ActualWidth / 2;
        return $"hostX={hostOrigin.X:F1} hostW={LyricsHost.ActualWidth:F1} " +
               $"windowW={ActualWidth:F1} centerDelta={centerDelta:F1} " +
               $"column={Grid.GetColumn(LyricsHost)} row={Grid.GetRow(LyricsHost)}";
    }

    public void DebugSetSettingsPanel(bool open)
    {
        if (open) OpenSettingsPanel();
        else CloseSettingsPanel();
    }

    public string DebugGap()
    {
        var pos = _lyrics.GetAdjustedPosition();
        var index = _lyrics.GetLineIndex(pos);
        var found = $"picked='{_lyrics.Found?.TrackName}' dur={(int?)_lyrics.Found?.Duration} lines={_lines.Count}";
        if (index < 0 || index + 1 >= _lines.Count) return $"pos={pos:F1} idx={index} {found}";
        var line = _lines[index];
        var next = _lines[index + 1];
        var interlude = TryGetInterlude(pos, out _, out var start, out var end);
        return $"pos={pos:F1} idx={index} line=[{line.Time:F2}] '{line.Text}' words={line.Words?.Count ?? 0} " +
               $"finish={LineFinishedAt(index):F2} next=[{next.Time:F2}] '{next.Text}' " +
               $"interlude={interlude} span={start:F1}-{end:F1} {found}";
    }

    public string DebugLayout()
    {
        var sb = new StringBuilder();
        sb.Append($"spacing={_spacing:F1} font={_baseFontSize:F1} " +
                  $"canvas={LyricsCanvas.ActualWidth:F0}x{LyricsCanvas.ActualHeight:F0} " +
                  $"blocks={_blocks.Count} lines={_lines.Count}");
        var small = new List<string>();
        var heights = new StringBuilder();
        for (var i = 0; i < _blocks.Count; i++)
        {
            if (_heights[i] < 5) small.Add(i.ToString());
            if (i < 45) heights.Append(_heights[i].ToString("F0")).Append(',');
        }
        sb.Append($" small=[{string.Join(' ', small)}] heights=[{heights}]");
        return sb.ToString();
    }
}

