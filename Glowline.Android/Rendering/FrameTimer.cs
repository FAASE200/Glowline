using System;
using Glowline.Services;
using AndroidChoreographer = global::Android.Views.Choreographer;

namespace Glowline.Android.Rendering;

/// <summary>
/// A timer that ticks in step with the screen's refresh (Android's Choreographer),
/// at 60, 90 or 120 Hz depending on the phone. A normal dispatcher timer is not
/// tied to the screen, so its ticks arrive unevenly and motion looks choppy.
/// An interval of 20 ms or less means "every frame"; a longer one skips frames
/// until that much time has passed (used while nothing is moving).
/// Must be created and used on the UI thread.
/// </summary>
public sealed class FrameTimer
{
    sealed class Callback : global::Java.Lang.Object, AndroidChoreographer.IFrameCallback
    {
        readonly FrameTimer _owner;

        public Callback(FrameTimer owner) => _owner = owner;

        public void DoFrame(long frameTimeNanos) => _owner.OnFrame();
    }

    readonly Callback _callback;
    readonly AndroidChoreographer _choreographer = AndroidChoreographer.Instance!;
    DateTime _lastTick = DateTime.MinValue;

    public FrameTimer() => _callback = new Callback(this);

    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(16);

    public bool IsRunning { get; private set; }

    public event EventHandler? Tick;

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _lastTick = DateTime.MinValue;
        _choreographer.PostFrameCallback(_callback);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _choreographer.RemoveFrameCallback(_callback);
    }

    void OnFrame()
    {
        if (!IsRunning) return;
        var now = DateTime.Now;
        var every = Interval <= TimeSpan.FromMilliseconds(20);
        // Ticks a little early still count, so a 200 ms interval doesn't drift to 216 ms.
        if (every || _lastTick == DateTime.MinValue || now - _lastTick >= Interval - TimeSpan.FromMilliseconds(8))
        {
            _lastTick = now;
            try
            {
                Tick?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("frame tick: " + ex);
            }
        }
        // The tick may have stopped the timer.
        if (IsRunning) _choreographer.PostFrameCallback(_callback);
    }
}
