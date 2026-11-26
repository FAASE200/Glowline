using System;
using System.Collections.Generic;
using LyricGlow.Models;
using Microsoft.Maui.Graphics;

namespace LyricGlow.Android.Rendering;

public sealed class ParticleField : IDrawable
{
    sealed class Particle
    {
        public float Size { get; init; }
        public Color Color { get; init; } = Colors.White;
        public bool Stroke { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Speed { get; set; }
        public double Drift { get; set; }
        public double Phase { get; set; }
    }

    readonly List<Particle> _particles = new();
    readonly Random _random = new(1729);
    double _time;
    double _intensity = 0.5;
    double _width;
    double _height;
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

    public void Start()
    {
        _running = true;
        if (_particles.Count == 0) Rebuild();
    }

    public void Stop()
    {
        _running = false;
        _particles.Clear();
    }

    public void Update(double dt, double beat)
    {
        if (!_running || ReducedMotion || _kind == ParticleKind.None) return;
        _time += Math.Clamp(dt, 0, 0.1);
        var pulse = 1 + Math.Clamp(beat, 0, 1) * 0.18;
        foreach (var particle in _particles)
        {
            particle.Y -= particle.Speed * dt;
            particle.X += Math.Sin(_time * 0.7 + particle.Phase) * particle.Drift * dt;
            if (particle.Y < -12) particle.Y = _height + 12;
            if (particle.X < -12) particle.X = _width + 12;
            if (particle.X > _width + 12) particle.X = -12;
        }
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (!_running || ReducedMotion || _kind == ParticleKind.None) return;
        if (Math.Abs(dirtyRect.Width - _width) > 0.5 || Math.Abs(dirtyRect.Height - _height) > 0.5)
        {
            _width = dirtyRect.Width;
            _height = dirtyRect.Height;
            Rebuild();
        }
        if (_particles.Count == 0) return;

        const double pulse = 1.0;
        foreach (var particle in _particles)
        {
            var scale = pulse * (0.85 + 0.15 * Math.Sin(_time + particle.Phase));
            canvas.StrokeColor = particle.Color;
            canvas.FillColor = particle.Color;
            canvas.StrokeSize = 1;
            var radius = (float)(particle.Size * scale / 2);
            if (particle.Stroke) canvas.DrawCircle((float)particle.X, (float)particle.Y, radius);
            else canvas.FillCircle((float)particle.X, (float)particle.Y, radius);
        }
    }

    void Rebuild()
    {
        _particles.Clear();
        if (!_running || _kind == ParticleKind.None || _width <= 1 || _height <= 1) return;

        var count = Math.Clamp((int)(8 + Intensity * 26), 8, 36);
        for (var i = 0; i < count; i++)
        {
            var size = _kind switch
            {
                ParticleKind.Dust => _random.Next(1, 4),
                ParticleKind.Stars => _random.Next(3, 7),
                ParticleKind.Sparks => _random.Next(2, 5),
                ParticleKind.Bubbles => _random.Next(5, 13),
                _ => 2,
            };
            var color = _kind switch
            {
                ParticleKind.Stars => Color.FromRgb(0xFF, 0xD8, 0x8A),
                ParticleKind.Sparks => Color.FromRgb(0xFF, 0x6B, 0x81),
                ParticleKind.Bubbles => Color.FromRgba(0xFF, 0xFF, 0xFF, 0x66),
                _ => Color.FromRgba(0xFF, 0xFF, 0xFF, 0x66),
            };
            _particles.Add(new Particle
            {
                Size = size,
                Color = color,
                Stroke = _kind == ParticleKind.Bubbles,
                X = _random.NextDouble() * _width,
                Y = _random.NextDouble() * _height,
                Speed = 4 + _random.NextDouble() * 12 * (0.5 + Intensity),
                Drift = 2 + _random.NextDouble() * 8,
                Phase = _random.NextDouble() * Math.PI * 2,
            });
        }
    }
}
