using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LyricGlow.Controls;

public sealed class PlayPauseIcon : Control
{
    static readonly Geometry PlayShape = Geometry.Parse("M 6.4,4.4 L 17.6,12 L 6.4,19.6 Z");
    static readonly TimeSpan Transition = TimeSpan.FromMilliseconds(220);
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = new();
    double _from;
    double _target;
    double _progress;

    public PlayPauseIcon()
    {
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(15),
        };
        _timer.Tick += OnTick;
        Unloaded += (_, __) =>
        {
            _timer.Stop();
            _progress = _target;
            InvalidateVisual();
        };
    }

    public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(
        nameof(Playing), typeof(bool), typeof(PlayPauseIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnPlayingChanged));

    public bool Playing
    {
        get => (bool)GetValue(PlayingProperty);
        set => SetValue(PlayingProperty, value);
    }

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(PlayPauseIcon),
        new FrameworkPropertyMetadata(true, OnAnimateChanged));

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    static void OnAnimateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (PlayPauseIcon)d;
        if ((bool)e.NewValue) return;
        icon._timer.Stop();
        icon._progress = icon._target;
        icon.InvalidateVisual();
    }

    static void OnPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PlayPauseIcon)d).AnimateTo((bool)e.NewValue ? 1 : 0);

    void AnimateTo(double target)
    {
        _target = target;
        if (!Animate || !SystemParameters.ClientAreaAnimation)
        {
            _timer.Stop();
            _progress = target;
            InvalidateVisual();
            return;
        }

        if (Math.Abs(_progress - target) < 0.0001)
        {
            InvalidateVisual();
            return;
        }

        _from = _progress;
        _clock.Restart();
        _timer.Start();
    }

    void OnTick(object? sender, EventArgs e)
    {
        var t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / Transition.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        _progress = _from + (_target - _from) * eased;
        InvalidateVisual();
        if (t >= 1)
        {
            _timer.Stop();
            _progress = _target;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsNaN(Width) ? 24 : Width;
        var height = double.IsNaN(Height) ? 24 : Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var brush = Foreground ?? Brushes.White;
        var pen = new Pen(brush, 1.6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        try
        {
            DrawIcon(dc, brush, pen, pause: false, opacity: 1 - _progress, scale: 1 - 0.18 * _progress);
            DrawIcon(dc, brush, pen, pause: true, opacity: _progress, scale: 0.82 + 0.18 * _progress);
        }
        finally
        {
            dc.Pop();
        }
    }

    static void DrawIcon(
        DrawingContext dc, Brush brush, Pen pen, bool pause, double opacity, double scale)
    {
        if (opacity <= 0.001) return;
        dc.PushOpacity(opacity);
        dc.PushTransform(new ScaleTransform(scale, scale, 12, 12));
        try
        {
            if (pause)
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(6.8, 4.2, 3.6, 15.6), 1.6, 1.6);
                dc.DrawRoundedRectangle(brush, null, new Rect(13.6, 4.2, 3.6, 15.6), 1.6, 1.6);
            }
            else
            {
                dc.DrawGeometry(brush, pen, PlayShape);
            }
        }
        finally
        {
            dc.Pop();
            dc.Pop();
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // El control dibuja con OnRender, así que el cambio de color debe invalidar.
        if (e.Property == ForegroundProperty) InvalidateVisual();
    }
}