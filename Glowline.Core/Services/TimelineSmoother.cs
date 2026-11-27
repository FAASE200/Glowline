using System;

namespace Glowline.Services;

public sealed class TimelineSmoother
{
    const double RawChangeEpsilon = 0.2;
    const double MaxTrustedAgeSeconds = 10;
    static readonly TimeSpan SeekGuard = TimeSpan.FromMilliseconds(1500);

    bool _initialized;
    bool _lastPlaying;
    string _signature = "";
    double _lastRaw = double.NaN;
    double _anchorRaw;
    DateTimeOffset _anchorAt;
    DateTimeOffset _seekGuardUntil = DateTimeOffset.MinValue;

    public double Update(
        double rawSeconds, bool playing, DateTimeOffset lastUpdated, DateTimeOffset now, string signature)
    {
        rawSeconds = double.IsFinite(rawSeconds) ? Math.Max(0, rawSeconds) : 0;
        var trackChanged = !string.Equals(signature, _signature, StringComparison.Ordinal);
        var playbackChanged = playing != _lastPlaying;
        var rawChanged = !double.IsNaN(_lastRaw) && Math.Abs(rawSeconds - _lastRaw) >= RawChangeEpsilon;

        if (!_initialized || trackChanged || playbackChanged || (rawChanged && now >= _seekGuardUntil))
        {
            _initialized = true;
            _anchorRaw = rawSeconds;
            _anchorAt = playing ? now - TrustedAge(lastUpdated, now) : now;
        }

        _lastRaw = rawSeconds;
        _lastPlaying = playing;
        _signature = signature;

        double position;
        if (playing)
            position = _anchorRaw + Math.Max(0, (now - _anchorAt).TotalSeconds);
        else if (now < _seekGuardUntil)
            position = _anchorRaw;
        else
            position = rawSeconds;

        return double.IsFinite(position) ? Math.Max(0, position) : 0;
    }

    public void NotifySeek(double targetSeconds, DateTimeOffset now)
    {
        targetSeconds = double.IsFinite(targetSeconds) ? Math.Max(0, targetSeconds) : 0;
        _initialized = true;
        _anchorRaw = targetSeconds;
        _anchorAt = now;
        _lastRaw = targetSeconds;
        _seekGuardUntil = now + SeekGuard;
    }

    static TimeSpan TrustedAge(DateTimeOffset lastUpdated, DateTimeOffset now)
    {
        var age = (now - lastUpdated).TotalSeconds;
        return age is > 0.05 and < MaxTrustedAgeSeconds ? TimeSpan.FromSeconds(age) : TimeSpan.Zero;
    }
}
