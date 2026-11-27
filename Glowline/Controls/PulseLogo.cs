using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Glowline.Controls;

public sealed class PulseLogo : FrameworkElement
{
    static readonly Brush FallbackInk = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
    static readonly double[] BaseHeights = { 126, 252, 378, 252, 126 };
    readonly double[] _levels = new double[5];
    readonly DispatcherTimer _timer;

    bool _renderedLevels;
    bool _reactiveActive;

    public PulseLogo()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick += OnTick;
        Loaded += (_, __) => SyncTimer();
        Unloaded += (_, __) => _timer.Stop();
        IsVisibleChanged += (_, __) => SyncTimer();
    }

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(PulseLogo),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(PulseLogo),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public static readonly DependencyProperty IsReactiveProperty = DependencyProperty.Register(
        nameof(IsReactive), typeof(bool), typeof(PulseLogo),
        new FrameworkPropertyMetadata(true, OnReactiveChanged));

    public bool IsReactive
    {
        get => (bool)GetValue(IsReactiveProperty);
        set => SetValue(IsReactiveProperty, value);
    }

    static void OnReactiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PulseLogo logo) return;
        if (!(bool)e.NewValue)
        {
            Array.Clear(logo._levels);
            logo._reactiveActive = false;
            logo.InvalidateVisual();
        }
        logo.SyncTimer();
    }

    void SyncTimer()
    {
        if (IsLoaded && IsVisible && IsReactive && !App.SnapshotMode)
            _timer.Start();
        else
            _timer.Stop();
    }

    void OnTick(object? sender, EventArgs e)
    {
        var settings = App.Settings?.Settings;
        var sense = App.SoundSense;
        var active = IsReactive && !App.SnapshotMode && settings is { SoundSenseEnabled: true } &&
                     App.Designs?.Active.ReduceMotion != true && sense is not null;
        var wasActive = _reactiveActive;
        _reactiveActive = active;
        var intensity = active ? Math.Clamp(settings!.SoundSenseIntensity, 0, 1) : 0;
        var bass = active ? sense!.BassLevel : 0;
        var mid = active ? sense!.MidLevel : 0;
        var treble = active ? sense!.TrebleLevel : 0;
        var beat = active ? sense!.Beat : 0;
        var energy = active ? sense!.EnergyLevel : 0;
        double[] targets =
        {
            bass * 0.65 + mid * 0.35,
            bass * 0.45 + mid * 0.55,
            Math.Max(bass * 0.80 + beat * 0.35, energy * 0.60),
            mid * 0.45 + treble * 0.55,
            treble * 0.80 + mid * 0.20,
        };

        var changed = false;
        for (var i = 0; i < _levels.Length; i++)
        {
            var target = Math.Clamp(targets[i], 0, 1) * intensity;
            var smoothing = target > _levels[i] ? 0.42 : 0.16;
            var next = _levels[i] + (target - _levels[i]) * smoothing;
            if (Math.Abs(next - _levels[i]) > 0.0005)
            {
                _levels[i] = next;
                changed = true;
            }
            else if (_levels[i] != 0)
            {
                _levels[i] = 0;
                changed = true;
            }
        }

        if (changed || wasActive != active || !_renderedLevels)
        {
            _renderedLevels = true;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsNaN(Width) ? 48 : Width;
        var height = double.IsNaN(Height) ? 48 : Height;
        return new Size(
            double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? height : Math.Min(height, availableSize.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var unit = size / 640.0;
        var barWidth = 46 * unit;
        var gap = 34 * unit;
        var totalWidth = barWidth * 5 + gap * 4;
        var left = (ActualWidth - totalWidth) / 2;
        var ink = BarBrush ?? (TryFindResource("Ink") as Brush) ?? FallbackInk;
        var accent = AccentBrush ?? (TryFindResource("Accent") as Brush) ?? ink;
        var maxHeight = 378 * unit;
        var dotHeight = barWidth;

        for (var i = 0; i < BaseHeights.Length; i++)
        {
            var baseHeight = maxHeight * (BaseHeights[i] / 378.0);
            double height;
            if (_reactiveActive)
            {
                // En reposo el logo son cinco puntos; el audio los estira en barras.
                var level = Math.Clamp(_levels[i], 0, 1);
                var grown = baseHeight * (1 + level * 0.42);
                height = dotHeight + (grown - dotHeight) * level;
            }
            else
            {
                height = baseHeight * (1 + _levels[i] * 0.42);
            }
            var x = left + i * (barWidth + gap);
            var y = (ActualHeight - height) / 2;
            var brush = i == 2 ? accent : ink;
            var rect = new Rect(x, y, barWidth, height);
            if (_reactiveActive)
                dc.DrawRoundedRectangle(brush, null, rect, barWidth / 2, barWidth / 2);
            else
                dc.DrawRectangle(brush, null, rect);
        }
    }
}
