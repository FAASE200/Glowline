using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Glowline.Models;

namespace Glowline;

public sealed class IntroDots : StackPanel
{
    public const double ThresholdSeconds = 7.0;

    static readonly double[] LightAt = { 0.3, 0.6, 0.9 };

    readonly Ellipse[] _dots = new Ellipse[3];
    readonly ScaleTransform[] _scales = new ScaleTransform[3];
    bool _fadingOut;

    public IntroDots()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;

        for (var i = 0; i < _dots.Length; i++)
        {
            var scale = new ScaleTransform(1, 1);
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Margin = new Thickness(5, 0, 5, 0),
                Fill = Brushes.White,
                Opacity = 0.28,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            _dots[i] = dot;
            _scales[i] = scale;
            Children.Add(dot);
        }
    }

    public void SetBrush(Brush brush)
    {
        foreach (var dot in _dots) dot.Fill = brush;
    }

    public void Apply(bool active, double positionSeconds, double startSeconds, double endSeconds)
    {
        if (!active)
        {
            if (_fadingOut || Visibility != Visibility.Visible) return;
            _fadingOut = true;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            fade.Completed += (_, __) =>
            {
                if (!_fadingOut) return;
                Visibility = Visibility.Collapsed;
                BeginAnimation(OpacityProperty, null);
                Opacity = 1;
                _fadingOut = false;
            };
            BeginAnimation(OpacityProperty, fade);
            return;
        }

        if (_fadingOut || Visibility != Visibility.Visible)
        {
            _fadingOut = false;
            BeginAnimation(OpacityProperty, null);
            Visibility = Visibility.Visible;
            Opacity = 0;
            BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        var progress = endSeconds > startSeconds
            ? Math.Clamp((positionSeconds - startSeconds) / (endSeconds - startSeconds), 0, 1)
            : 1;

        // Respiración lenta mientras se espera el verso.
        var breathTime = Math.Max(0, positionSeconds - startSeconds);
        var breath = 1 + 0.09 * Math.Sin(2 * Math.PI * breathTime / 2.4);

        // En el último tramo crecen de más y se achican hasta desaparecer
        // justo cuando entra el verso.
        var remaining = Math.Max(0, endSeconds - positionSeconds);
        var swell = endSeconds > startSeconds ? Math.Clamp(1 - remaining / 1.3, 0, 1) : 0;
        double entryScale;
        double entryFade;
        if (swell < 0.45)
        {
            entryScale = 1 + 0.65 * SmoothStep(swell / 0.45);
            entryFade = 1;
        }
        else
        {
            var t = SmoothStep((swell - 0.45) / 0.55);
            entryScale = 1.65 * (1 - t);
            entryFade = 1 - t;
        }

        for (var i = 0; i < _dots.Length; i++)
        {
            var lit = SmoothStep((progress - (LightAt[i] - 0.08)) / 0.10);
            var opacity = (0.28 + 0.72 * lit) * entryFade;
            var scale = breath * entryScale;
            if (Math.Abs(_dots[i].Opacity - opacity) > 0.001) _dots[i].Opacity = opacity;
            if (Math.Abs(_scales[i].ScaleX - scale) > 0.001)
                _scales[i].ScaleX = _scales[i].ScaleY = scale;
        }
    }

    static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    public static bool IsIntro(IReadOnlyList<LrcLine> lines, double positionSeconds,
        double threshold = ThresholdSeconds) =>
        lines.Count > 0 &&
        lines[0].Time >= threshold &&
        positionSeconds >= 0 &&
        positionSeconds < lines[0].Time;
}
