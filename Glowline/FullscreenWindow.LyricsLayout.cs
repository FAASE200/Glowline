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
using Glowline.Localization;
using Glowline.Models;
using Glowline.Rendering;
using Glowline.Services;

namespace Glowline;

// Building, measuring and scrolling the lyric lines.
public partial class FullscreenWindow
{
    void RebuildLyrics(bool animate)
    {
        animate &= !App.Designs.Active.ReduceMotion;
        LinesLayer.Children.Clear();
        _blocks.Clear();
        _heights.Clear();
        _tops.Clear();
        _wordLine = null;
        _currentLine = int.MinValue;
        _hoverLine = -1;
        _interludeGapLine = -1;
        _interludeGap = 0;
        _lines = _lyrics.Lines;

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var lineIndex = i;
            var block = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = _fontFamily,
                Foreground = new SolidColorBrush(_blockText),
                FontWeight = _fontWeight,
                FontSize = _baseFontSize,
                TextAlignment = CurrentLyricAlignment(),
                Background = Brushes.Transparent,
                Opacity = 0,
                Visibility = Visibility.Hidden,
                Cursor = Cursors.Hand,
            };
            LyricRender.SetPlain(block, EffectiveLine(line), _backgroundVocal);
            block.MouseLeftButtonDown += (_, e) =>
            {
                _ = SeekToLineAsync(lineIndex);
                e.Handled = true;
            };
            block.MouseRightButtonUp += (_, e) =>
            {
                ShowLineMenu(block, lineIndex);
                e.Handled = true;
            };
            block.MouseEnter += (_, __) => _hoverLine = lineIndex;
            block.MouseLeave += (_, __) =>
            {
                if (_hoverLine == lineIndex) _hoverLine = -1;
            };
            Canvas.SetLeft(block, 0);
            Canvas.SetTop(block, 0);
            LinesLayer.Children.Add(block);
            _blocks.Add(block);
            _heights.Add(0);
            _tops.Add(0);
        }

        StatusMessage.Visibility = Visibility.Collapsed;
        PlainScrollFull.Visibility = Visibility.Collapsed;
        LyricsCanvas.Visibility = Visibility.Visible;

        ApplyCurrentLine(_lyrics.GetLineIndex(_lyrics.GetAdjustedPosition()));
        LayoutBlocks();
        if (LayoutLog) Log.Write("layout rebuild " + DebugLayout());
        _scrollOffset = CenterTarget();
        ScrollTransform.Y = _scrollOffset;
        _scrollInit = LyricsCanvas.ActualHeight > 1;

        if (animate)
        {
            LyricsCanvas.Opacity = 0;
            LyricsCanvas.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            LyricsCanvas.Opacity = 1;
        }
    }

    void ApplyCurrentLine(int index)
    {
        EnsureBlockWidths();
        if (index == _currentLine) return;
        var previous = _currentLine;
        _currentLine = index;

        if (previous >= 0 && previous < _blocks.Count)
        {
            SetBlockPlain(previous);
            _blocks[previous].Effect = null;
            MeasureBlock(previous);
        }

        _wordLine = null;
        if (index >= 0 && index < _blocks.Count)
        {
            var block = _blocks[index];
            block.Effect = _centeredTheme ? _fusionGlow : _darkGlow;
            var highlight = new WordHighlightLine(
                block,
                _wordDim,
                _wordBright,
                _wordFade)
            {
                BackgroundColor = _backgroundVocal,
                Style = MapWordAnimation(App.Designs.Active.Animations.WordAnimation),
                MotionStrength = Math.Clamp(App.Designs.Active.Animations.WordIntensity, 0, 1),
            };
            highlight.SetLine(EffectiveLine(_lines[index]));
            _wordLine = highlight;
            MeasureBlock(index);
        }

        if (previous >= 0 || index >= 0)
        {
            UpdateBlockTops();
            AnimateVerseTransition();
        }
    }

    void AnimateVerseTransition()
    {
        var design = App.Designs.Active;
        _verseShift.BeginAnimation(TranslateTransform.YProperty, null);
        _verseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _verseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _verseShift.Y = 0;
        _verseScale.ScaleX = _verseScale.ScaleY = 1;
        if (design.ReduceMotion || design.Animations.VerseTransition == VerseTransition.None) return;

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(design.Animations.TransitionDurationMs, 80, 1200));
        var ease = CreateDesignEasing(design.Animations.Easing);
        if (design.Animations.VerseTransition is VerseTransition.Slide or VerseTransition.Roll)
        {
            var from = design.Animations.VerseTransition == VerseTransition.Slide ? 10 : 24;
            _verseShift.Y = from;
            _verseShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
        }
        else if (design.Animations.VerseTransition == VerseTransition.Scale)
        {
            const double from = 0.965;
            _verseScale.ScaleX = _verseScale.ScaleY = from;
            _verseScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(from, 1, duration) { EasingFunction = ease });
            _verseScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(from, 1, duration) { EasingFunction = ease });
        }
        else if (design.Animations.VerseTransition == VerseTransition.Fade)
        {
            LyricsCanvas.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
        }
    }

    static IEasingFunction CreateDesignEasing(EasingKind kind) => kind switch
    {
        EasingKind.EaseInOut => new CubicEase { EasingMode = EasingMode.EaseInOut },
        EasingKind.Linear => new SineEase { EasingMode = EasingMode.EaseInOut },
        EasingKind.Spring => new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 },
        EasingKind.Bounce => new BounceEase { EasingMode = EasingMode.EaseOut, Bounciness = 2 },
        _ => new CubicEase { EasingMode = EasingMode.EaseOut },
    };

    void SetBlockPlain(int index)
    {
        LyricRender.SetPlain(_blocks[index], EffectiveLine(_lines[index]), _backgroundVocal);
    }

    async System.Threading.Tasks.Task SeekToLineAsync(int index)
    {
        var lines = _lyrics.Lines;
        if (index < 0 || index >= lines.Count) return;
        var position = _lyrics.GetSeekPosition(lines[index].Time);
        if (await _media.SeekAsync(position))
            _lyrics.NotifySeek(position);
    }

    void EnsureBlockWidths()
    {
        if (_blocks.Count == 0) return;
        var width = Math.Max(200, LyricsCanvas.ActualWidth);
        foreach (var block in _blocks)
        {
            if (double.IsNaN(block.Width) || Math.Abs(block.Width - width) > 0.5)
            {
                Log.Write($"lyrics widths refreshed: canvas={width:F0} first={block.Width:F0}");
                LayoutBlocks();
                return;
            }
        }
    }

    void MeasureBlock(int index)
    {
        if (index < 0 || index >= _blocks.Count) return;
        var block = _blocks[index];
        var collapsed = block.Visibility == Visibility.Collapsed;
        if (collapsed) block.Visibility = Visibility.Hidden;
        var width = Math.Max(200, LyricsCanvas.ActualWidth);
        block.Width = width;
        block.Measure(new Size(width, double.PositiveInfinity));
        _heights[index] = block.DesiredSize.Height;
        if (collapsed) block.Visibility = Visibility.Collapsed;
    }

    void LayoutBlocks()
    {
        if (_blocks.Count == 0) return;
        var width = Math.Max(200, LyricsCanvas.ActualWidth);

        // Collapsed elements always measure 0; reveal them for measurement so a
        // relayout doesn't shrink the off-screen lines to zero height.
        var collapsed = new List<int>();
        for (var i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Visibility != Visibility.Collapsed) continue;
            _blocks[i].Visibility = Visibility.Hidden;
            collapsed.Add(i);
        }

        double y = 0;
        for (var i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            block.Width = width;
            block.Measure(new Size(width, double.PositiveInfinity));
            var height = block.DesiredSize.Height;
            _heights[i] = height;
            _tops[i] = y;
            Canvas.SetTop(block, y);
            y += height + _spacing + GapExtra(i);
        }

        foreach (var i in collapsed) _blocks[i].Visibility = Visibility.Collapsed;
    }

    double LineFinishedAt(int index)
    {
        if (index < 0 || index >= _lines.Count) return 0;
        var line = _lines[index];
        var finish = line.Time + 4.0;
        if (line.Words is { Count: > 0 })
            finish = Math.Max(line.Time + 1.0, line.Words[^1].Time + 0.6);
        if (index + 1 < _lines.Count)
            finish = Math.Min(finish, _lines[index + 1].Time);
        return finish;
    }

    bool IsInterludeGap(int index)
    {
        if (index < 0 || index + 1 >= _lines.Count) return false;
        return _lines[index + 1].Time - LineFinishedAt(index) > 12.0;
    }

    double GapExtra(int index) =>
        index == _interludeGapLine ? InterludeGap * _interludeGap : 0.0;

    void UpdateBlockTops()
    {
        if (_blocks.Count == 0 || _tops.Count != _blocks.Count) return;
        double y = 0;
        for (var i = 0; i < _blocks.Count; i++)
        {
            _tops[i] = y;
            Canvas.SetTop(_blocks[i], y);
            y += _heights[i] + _spacing + GapExtra(i);
        }
    }

    void AdvanceInterludeGap(bool active, int gapLine, double dt)
    {
        if (active)
        {
            if (_interludeGapLine != gapLine)
                Log.Write($"interlude gap open line={gapLine}");
            _interludeGapLine = gapLine;
        }
        else if (_interludeGapLine < 0)
        {
            return;
        }

        var target = active ? 1.0 : 0.0;
        var next = _interludeGap + (target - _interludeGap) * (1 - Math.Exp(-10 * dt));
        if (!active && next < 0.005)
        {
            next = 0;
            _interludeGapLine = -1;
            Log.Write("interlude gap closed");
        }

        if (Math.Abs(next - _interludeGap) < 0.0005) return;
        _interludeGap = next;
        UpdateBlockTops();
    }

    bool TryGetInterlude(double position, out int current, out double start, out double end)
    {
        current = _lyrics.GetLineIndex(position);
        start = 0;
        end = 0;
        if (current < 0 || current + 1 >= _lines.Count || !IsInterludeGap(current)) return false;

        start = LineFinishedAt(current);
        end = _lines[current + 1].Time;
        return position >= start && position < end;
    }

    double CenterTarget()
    {
        var index = _currentLine;
        if (index < 0 && _lines.Count > 0) index = 0;
        if (index < 0 || index >= _tops.Count) return 0;
        return LyricsCanvas.ActualHeight / 2 - (_tops[index] + _heights[index] / 2) +
               App.Designs.Active.Lyrics.VerticalOffset;
    }

    void OnHostResized()
    {
        if (!_isOpen) return;
        UpdateSettingsPanelSize();
        // Durante la transición de tamaño el reflow se difiere al final para no
        // reconstruir todos los bloques en cada fotograma.
        if (_coverSizeAnimating) return;
        LayoutBlocks();
        _scrollOffset = CenterTarget();
        ScrollTransform.Y = _scrollOffset;
        _scrollInit = true;
        UpdateLightArtSize();
    }
}

