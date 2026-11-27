using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Glowline.Models;

namespace Glowline.Rendering;

public sealed class ParticleField : Canvas
{
    sealed class Particle
    {
        public Ellipse Shape { get; init; } = new();
        public double X { get; set; }
        public double Y { get; set; }
        public double Speed { get; set; }
        public double Drift { get; set; }
        public double Phase { get; set; }
        public ScaleTransform Scale { get; } = new();
    }

    readonly List<Particle> _particles = new();
    readonly Random _random = new(1729);
    double _time;
    double _intensity = 0.5;
    ParticleKind _kind;
    bool _running;

    public ParticleKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            Rebuild();
        }
    }

    public double Intensity
    {
        get => _intensity;
        set
        {
            if (Math.Abs(_intensity - value) < 0.0001) return;
            _intensity = value;
            if (_running) Rebuild();
        }
    }

    public bool ReducedMotion { get; set; }

    public ParticleField()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        SizeChanged += (_, __) =>
        {
            if (_running) Rebuild();
        };
    }

    public void Start()
    {
        _running = true;
        Visibility = Visibility.Visible;
        if (_particles.Count == 0) Rebuild();
    }

    public void Stop()
    {
        _running = false;
        Children.Clear();
        _particles.Clear();
        Visibility = Visibility.Collapsed;
    }

    public void Update(double dt, double beat)
    {
        if (!_running || ReducedMotion || Kind == ParticleKind.None || ActualWidth <= 1 || ActualHeight <= 1) return;
        _time += Math.Clamp(dt, 0, 0.1);
        var pulse = 1 + Math.Clamp(beat, 0, 1) * 0.18;

        foreach (var particle in _particles)
        {
            particle.Y -= particle.Speed * dt;
            particle.X += Math.Sin(_time * 0.7 + particle.Phase) * particle.Drift * dt;
            if (particle.Y < -12) particle.Y = ActualHeight + 12;
            if (particle.X < -12) particle.X = ActualWidth + 12;
            if (particle.X > ActualWidth + 12) particle.X = -12;

            Canvas.SetLeft(particle.Shape, particle.X);
            Canvas.SetTop(particle.Shape, particle.Y);
            var scale = pulse * (0.85 + 0.15 * Math.Sin(_time + particle.Phase));
            particle.Scale.ScaleX = particle.Scale.ScaleY = scale;
        }
    }

    void Rebuild()
    {
        Children.Clear();
        _particles.Clear();
        if (!_running || Kind == ParticleKind.None) return;

        var count = Math.Clamp((int)(8 + Intensity * 26), 8, 36);
        for (var i = 0; i < count; i++)
        {
            var size = Kind switch
            {
                ParticleKind.Dust => _random.Next(1, 4),
                ParticleKind.Stars => _random.Next(3, 7),
                ParticleKind.Sparks => _random.Next(2, 5),
                ParticleKind.Bubbles => _random.Next(5, 13),
                _ => 2,
            };
            var color = Kind switch
            {
                ParticleKind.Stars => Color.FromRgb(0xFF, 0xD8, 0x8A),
                ParticleKind.Sparks => Color.FromRgb(0xFF, 0x6B, 0x81),
                ParticleKind.Bubbles => Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
                _ => Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
            };
            var shape = Kind == ParticleKind.Bubbles
                ? new Ellipse
                {
                    Width = size,
                    Height = size,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1,
                    Fill = Brushes.Transparent,
                }
                : new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new SolidColorBrush(color),
                };
            shape.RenderTransformOrigin = new Point(0.5, 0.5);
            Children.Add(shape);

            var particle = new Particle
            {
                Shape = shape,
                X = _random.NextDouble() * Math.Max(1, ActualWidth),
                Y = _random.NextDouble() * Math.Max(1, ActualHeight),
                Speed = 4 + _random.NextDouble() * 12 * (0.5 + Intensity),
                Drift = 2 + _random.NextDouble() * 8,
                Phase = _random.NextDouble() * Math.PI * 2,
            };
            shape.RenderTransform = particle.Scale;
            _particles.Add(particle);
        }
    }
}
