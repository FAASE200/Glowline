using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Glowline.Models;

namespace Glowline;

public sealed class WordHighlightLine
{
    readonly TextBlock _target;
    readonly List<SolidColorBrush> _brushes = new();
    readonly List<WordMotion?> _motions = new();
    readonly List<Run> _backgroundRuns = new();
    readonly List<SolidColorBrush> _backgroundBrushes = new();
    readonly Color _dim;
    readonly Color _bright;

    IReadOnlyList<LrcWord> _words = Array.Empty<LrcWord>();
    IReadOnlyList<LrcWord> _backgroundWords = Array.Empty<LrcWord>();
    int _painted = -2;
    int _backgroundPainted = -2;
    double _lastPosition = double.MinValue;

    public TimeSpan FadeTime { get; set; }
    public Color BackgroundColor { get; set; } = Colors.Transparent;
    public Color BackgroundBrightColor { get; set; } = Colors.White;
    public WordAnimationStyle Style { get; set; } = WordAnimationStyle.Fade;
    public double MotionStrength { get; set; } = 0.65;

    sealed class WordMotion
    {
        public TranslateTransform Translate { get; } = new();
        public ScaleTransform Scale { get; } = new();
        public BlurEffect? Blur { get; set; }
        public TransformGroup Group { get; } = new();

        public WordMotion(TextBlock block)
        {
            Group.Children.Add(Scale);
            Group.Children.Add(Translate);
            block.RenderTransform = Group;
            block.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }

    public WordHighlightLine(TextBlock target, Color dim, Color bright, TimeSpan fadeTime)
    {
        _target = target;
        _dim = dim;
        _bright = bright;
        FadeTime = fadeTime;
        // TextBlock.TextEffects are ignored as soon as the block uses explicit inlines
        // (colored Runs), so per-word motion needs real child elements instead.
    }

    public void SetLine(LrcLine? line)
    {
        _target.Inlines.Clear();
        _brushes.Clear();
        _motions.Clear();
        _backgroundRuns.Clear();
        _backgroundBrushes.Clear();
        _backgroundWords = Array.Empty<LrcWord>();
        _painted = -2;
        _backgroundPainted = -2;
        _lastPosition = double.MinValue;

        if (line?.Words is { Count: > 0 })
        {
            _words = line.Words;
            for (var i = 0; i < _words.Count; i++)
            {
                var word = _words[i];
                var text = word.Text.TrimEnd();
                if (text.Length == 0) text = " ";
                var brush = new SolidColorBrush(_dim);

                if (Style == WordAnimationStyle.Fade)
                {
                    _target.Inlines.Add(new Run(text) { Foreground = brush });
                    _motions.Add(null);
                }
                else
                {
                    var child = new TextBlock
                    {
                        Text = text,
                        Foreground = brush,
                    };
                    _target.Inlines.Add(new InlineUIContainer(child)
                    {
                        BaselineAlignment = BaselineAlignment.TextTop,
                    });
                    var motion = new WordMotion(child);
                    if (Style == WordAnimationStyle.Blur)
                    {
                        motion.Blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
                        child.Effect = motion.Blur;
                    }
                    _motions.Add(motion);
                }

                if (i < _words.Count - 1)
                    _target.Inlines.Add(new Run(" "));
                _brushes.Add(brush);
            }
        }
        else
        {
            _words = Array.Empty<LrcWord>();
            if (line is not null && line.Text.Length > 0)
                _target.Inlines.Add(new Run(line.Text));
        }

        if (line is not null && line.Background.Length > 0)
            AppendBackground(line);
        if (line is not null)
            LyricRender.AppendRomanized(_target, line, BackgroundColor);
    }

    void AppendBackground(LrcLine line)
    {
        if (_target.Inlines.Count > 0)
            _target.Inlines.Add(new LineBreak());

        var size = Math.Max(10, _target.FontSize * 0.56);
        if (line.BackgroundWords is { Count: > 0 })
        {
            _backgroundWords = line.BackgroundWords;
            foreach (var word in _backgroundWords)
            {
                var brush = new SolidColorBrush(BackgroundColor);
                var run = new Run(word.Text)
                {
                    Foreground = brush,
                    FontSize = size,
                    FontWeight = FontWeights.Normal,
                };
                _backgroundRuns.Add(run);
                _backgroundBrushes.Add(brush);
                _target.Inlines.Add(run);
            }
        }
        else
        {
            _target.Inlines.Add(new Run(line.Background)
            {
                Foreground = new SolidColorBrush(BackgroundColor),
                FontSize = size,
                FontWeight = FontWeights.Normal,
            });
        }
    }

    public void Paint(double position, bool animate)
    {
        if (_words.Count == 0 && _backgroundWords.Count == 0) return;

        if (position < _lastPosition - 0.35)
            _lastPosition = position;
        else if (position < _lastPosition)
            position = _lastPosition;
        else
            _lastPosition = position;

        var painted = PaintedIndex(_words, position);
        var backgroundPainted = PaintedIndex(_backgroundWords, position);
        if (painted == _painted && backgroundPainted == _backgroundPainted) return;

        PaintWords(painted, _painted, animate);
        PaintRuns(_backgroundRuns, _backgroundBrushes, backgroundPainted, _backgroundPainted,
            BackgroundColor, BackgroundBrightColor, animate);
        _painted = painted;
        _backgroundPainted = backgroundPainted;
    }

    static int PaintedIndex(IReadOnlyList<LrcWord> words, double position)
    {
        var painted = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].Time <= position) painted = i;
            else break;
        }
        return painted;
    }

    void PaintWords(int painted, int previous, bool animate)
    {
        for (var i = 0; i < _brushes.Count; i++)
        {
            var bright = i <= painted;
            var wasBright = i <= previous;
            if (bright == wasBright) continue;
            AnimateBrush(_brushes[i], bright ? _bright : _dim, animate);
            AnimateMotion(i, bright, animate);
        }
    }

    void PaintRuns(List<Run> runs, List<SolidColorBrush> brushes, int painted, int previous,
        Color dimColor, Color brightColor, bool animate)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var bright = i <= painted;
            var wasBright = i <= previous;
            if (bright == wasBright) continue;
            AnimateBrush(brushes[i], bright ? brightColor : dimColor, animate);
        }
    }

    void AnimateMotion(int index, bool bright, bool animate)
    {
        if (index < 0 || index >= _motions.Count) return;
        var motion = _motions[index];
        if (motion is null) return;
        ResetMotion(motion);
        if (!animate || !bright || Style == WordAnimationStyle.Fade) return;

        var strength = Math.Clamp(MotionStrength, 0, 1);
        if (strength <= 0.01) return;

        var duration = TimeSpan.FromMilliseconds(
            Math.Clamp(FadeTime.TotalMilliseconds * 1.7, 110, 420));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        switch (Style)
        {
            case WordAnimationStyle.Slide:
            {
                var from = 9 * strength;
                motion.Translate.Y = from;
                motion.Translate.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
                break;
            }
            case WordAnimationStyle.Zoom:
            {
                var from = 1 - 0.20 * strength;
                motion.Scale.ScaleX = motion.Scale.ScaleY = from;
                motion.Scale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(from, 1, duration) { EasingFunction = ease });
                motion.Scale.BeginAnimation(
                    ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(from, 1, duration) { EasingFunction = ease });
                break;
            }
            case WordAnimationStyle.Bounce:
            {
                var lift = -6 * strength;
                var y = new DoubleAnimationUsingKeyFrames();
                y.KeyFrames.Add(new EasingDoubleKeyFrame(
                    lift, KeyTime.FromPercent(0.35), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                y.KeyFrames.Add(new EasingDoubleKeyFrame(
                    0, KeyTime.FromPercent(1), new BounceEase { Bounces = 1, Bounciness = 2 }));
                motion.Translate.BeginAnimation(TranslateTransform.YProperty, y);

                var scale = new DoubleAnimationUsingKeyFrames();
                scale.KeyFrames.Add(new EasingDoubleKeyFrame(
                    1 + 0.05 * strength, KeyTime.FromPercent(0.35),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                scale.KeyFrames.Add(new EasingDoubleKeyFrame(
                    1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseOut }));
                motion.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
                motion.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
                break;
            }
            case WordAnimationStyle.Rise:
            {
                var from = 14 * strength;
                motion.Translate.Y = from;
                motion.Translate.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
                break;
            }
            case WordAnimationStyle.Blur:
            {
                if (motion.Blur is null) break;
                var from = 10 * strength;
                motion.Blur.Radius = from;
                motion.Blur.BeginAnimation(
                    BlurEffect.RadiusProperty,
                    new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
                break;
            }
            case WordAnimationStyle.Elastic:
            {
                var from = 1 - 0.28 * strength;
                motion.Scale.ScaleX = motion.Scale.ScaleY = from;
                var elastic = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 3, Springiness = 6 };
                motion.Scale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(from, 1, duration) { EasingFunction = elastic });
                motion.Scale.BeginAnimation(
                    ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(from, 1, duration) { EasingFunction = elastic });
                break;
            }
            case WordAnimationStyle.Pulse:
            {
                var scale = new DoubleAnimationUsingKeyFrames();
                scale.KeyFrames.Add(new LinearDoubleKeyFrame(1 - 0.18 * strength, KeyTime.FromPercent(0)));
                scale.KeyFrames.Add(new LinearDoubleKeyFrame(1 + 0.12 * strength, KeyTime.FromPercent(0.45)));
                scale.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));
                motion.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
                motion.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
                break;
            }
            case WordAnimationStyle.Wave:
            {
                var wave = new DoubleAnimationUsingKeyFrames();
                wave.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                wave.KeyFrames.Add(new EasingDoubleKeyFrame(-7 * strength, KeyTime.FromPercent(0.3),
                    new SineEase { EasingMode = EasingMode.EaseInOut }));
                wave.KeyFrames.Add(new EasingDoubleKeyFrame(4 * strength, KeyTime.FromPercent(0.65),
                    new SineEase { EasingMode = EasingMode.EaseInOut }));
                wave.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                motion.Translate.BeginAnimation(TranslateTransform.YProperty, wave);
                break;
            }
        }
    }

    static void ResetMotion(WordMotion motion)
    {
        motion.Translate.BeginAnimation(TranslateTransform.XProperty, null);
        motion.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        motion.Translate.X = 0;
        motion.Translate.Y = 0;
        motion.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        motion.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        motion.Scale.ScaleX = 1;
        motion.Scale.ScaleY = 1;
        if (motion.Blur is not null)
        {
            motion.Blur.BeginAnimation(BlurEffect.RadiusProperty, null);
            motion.Blur.Radius = 0;
        }
    }

    void AnimateBrush(SolidColorBrush brush, Color target, bool animate)
    {
        if (animate)
        {
            var anim = new ColorAnimation(target, FadeTime)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
        }
    }
}
