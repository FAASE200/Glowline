using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LyricGlow.Localization;
using NAudio.Wave;

namespace LyricGlow.Services;

public sealed class SoundSenseService : IDisposable
{
    const int HistorySize = 500;
    const int HistoryHz = 100;
    static readonly bool SoundLog = Environment.GetEnvironmentVariable("LYRICGLOW_SOUND_LOG") == "1";

    readonly object _gate = new();
    readonly float[] _history = new float[HistorySize];
    // El autocorrelator de BPM corre cada 2 s; reutilizar el buffer evita
    // asignar un double[] nuevo en cada pasada.
    readonly double[] _bpmScratch = new double[HistorySize];

    WasapiLoopbackCapture? _capture;
    bool _enabled;
    bool _disposed;
    volatile bool _disposedFlag;
    long _lastStartAttempt;
    long _lastSignalAt;
    long _lastBeatAt;
    long _lastBpmAt;

    float _fastBass;
    float _slowBass;
    float _bpm;
    double _lp1;
    double _lp2;
    double _bassAlpha;
    double _bandLow;
    double _bandMid;
    double _bandLowAlpha;
    double _bandMidAlpha;
    float _bassLevel;
    float _midLevel;
    float _trebleLevel;
    float _energyLevel;
    float _bassPeak;
    float _midPeak;
    float _treblePeak;
    float _energyPeak;
    int _historyIndex;
    int _historyCount;
    int _sampleRate;
    int _channels;
    int _bytesPerSample;
    int _chunkFrames;
    bool _floatFormat;
    string? _deviceErrorKey;
    long _diagAt;
    int _diagBeats;
    float _diagMaxRatio;

    public double Bpm
    {
        get
        {
            lock (_gate)
            {
                if (!_enabled || !HasSignalLocked()) return 0;
                return _bpm;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || !_enabled) return Loc.T("SenseDisabled");
                if (_capture is null)
                    return _deviceErrorKey is { } key ? Loc.T(key) : Loc.T("SenseStarting");
                if (!HasSignalLocked()) return Loc.T("SenseWaitingAudio");
                if (_bpm < 20) return Loc.T("SenseDetectingBpm");
                return Loc.T("SenseBeatActive");
            }
        }
    }

    public float Beat
    {
        get
        {
            if (_disposedFlag) return 0;
            lock (_gate) if (!_enabled || _bpm < 20 || !HasSignalLocked()) return 0;
            var last = Interlocked.Read(ref _lastBeatAt);
            if (last == 0) return 0;
            var ms = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
            if (ms < 0 || ms > 1400) return 0;
            return (float)Math.Exp(-ms / 170.0);
        }
    }

    public float BassLevel
    {
        get
        {
            if (_disposedFlag) return 0;
            lock (_gate) return _enabled && HasSignalLocked() ? _bassLevel : 0;
        }
    }

    public float MidLevel
    {
        get
        {
            if (_disposedFlag) return 0;
            lock (_gate) return _enabled && HasSignalLocked() ? _midLevel : 0;
        }
    }

    public float TrebleLevel
    {
        get
        {
            if (_disposedFlag) return 0;
            lock (_gate) return _enabled && HasSignalLocked() ? _trebleLevel : 0;
        }
    }

    public float EnergyLevel
    {
        get
        {
            if (_disposedFlag) return 0;
            lock (_gate) return _enabled && HasSignalLocked() ? _energyLevel : 0;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed) return;
        if (enabled)
        {
            lock (_gate)
            {
                _enabled = true;
                _deviceErrorKey = null;
            }
            EnsureStarted();
            return;
        }

        lock (_gate) _enabled = false;
        StopCapture();
        lock (_gate)
        {
            ResetAnalyzerLocked();
            _deviceErrorKey = null;
        }
    }

    public void EnsureStarted()
    {
        if (_disposed) return;
        lock (_gate) if (!_enabled || _capture is not null) return;

        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastStartAttempt);
        if (last != 0 && now - last < Stopwatch.Frequency * 3) return;
        Interlocked.Exchange(ref _lastStartAttempt, now);

        lock (_gate)
        {
            if (_disposed || !_enabled || _capture is not null) return;
            try
            {
                var capture = new WasapiLoopbackCapture();
                var format = capture.WaveFormat;
                _sampleRate = Math.Max(8000, format.SampleRate);
                _channels = Math.Max(1, format.Channels);
                _bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
                _floatFormat = format.Encoding == WaveFormatEncoding.IeeeFloat;
                _chunkFrames = Math.Max(64, _sampleRate / HistoryHz);
                _bassAlpha = 1 - Math.Exp(-2 * Math.PI * 120.0 / _sampleRate);
                _bandLowAlpha = 1 - Math.Exp(-2 * Math.PI * 180.0 / _sampleRate);
                _bandMidAlpha = 1 - Math.Exp(-2 * Math.PI * 2600.0 / _sampleRate);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                _capture = capture;
                capture.StartRecording();
                _deviceErrorKey = null;
                ResetAnalyzerLocked();
                Log.Write($"SoundSense: captura iniciada ({_sampleRate} Hz, {_channels} ch, {format.BitsPerSample} bits)");
            }
            catch (Exception ex)
            {
                _capture = null;
                _deviceErrorKey = "SenseNoDevice";
                ResetAnalyzerLocked();
                Log.Write("SoundSense: " + ex.Message);
            }
        }
    }

    void StopCapture()
    {
        WasapiLoopbackCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        if (capture is null) return;
        try { capture.StopRecording(); } catch { }
        try
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }
        catch { }
    }

    void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _capture)) return;
            _capture = null;
            _deviceErrorKey = e.Exception is null ? "SenseCaptureStopped" : "SenseNoDevice";
            ResetAnalyzerLocked();
        }
        if (!_disposed && Volatile.Read(ref _enabled))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                EnsureStarted();
            });
        }
    }

    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _channels <= 0 || _bytesPerSample <= 0) return;
        var frameBytes = _bytesPerSample * _channels;
        var frames = e.BytesRecorded / frameBytes;
        if (frames <= 0) return;

        var buffer = e.Buffer;
        for (var offset = 0; offset < frames; offset += _chunkFrames)
        {
            var count = Math.Min(_chunkFrames, frames - offset);
            double sum = 0;
            double bassSum = 0;
            double midSum = 0;
            double trebleSum = 0;
            for (var i = 0; i < count; i++)
            {
                double sample = 0;
                var frame = offset + i;
                for (var ch = 0; ch < _channels; ch++)
                    sample += ReadSample(buffer, (frame * _channels + ch) * _bytesPerSample);
                sample /= _channels;
                sum += sample * sample;
                _lp1 += (sample - _lp1) * _bassAlpha;
                _lp2 += (_lp1 - _lp2) * _bassAlpha;
                bassSum += _lp2 * _lp2;
                _bandLow += (sample - _bandLow) * _bandLowAlpha;
                _bandMid += (sample - _bandMid) * _bandMidAlpha;
                var mid = _bandMid - _bandLow;
                var treble = sample - _bandMid;
                midSum += mid * mid;
                trebleSum += treble * treble;
            }
            var safeCount = Math.Max(1, count);
            var rms = (float)Math.Sqrt(sum / safeCount);
            var bassRms = (float)Math.Sqrt(bassSum / safeCount);
            var midRms = (float)Math.Sqrt(midSum / safeCount);
            var trebleRms = (float)Math.Sqrt(trebleSum / safeCount);
            ProcessChunk(rms, bassRms, midRms, trebleRms);
        }
    }

    double ReadSample(byte[] buffer, int index)
    {
        if (index < 0 || index + _bytesPerSample > buffer.Length) return 0;
        if (_floatFormat && _bytesPerSample >= 4)
            return BitConverter.ToSingle(buffer, index);
        if (_bytesPerSample == 2)
            return BitConverter.ToInt16(buffer, index) / 32768.0;
        if (_bytesPerSample == 3)
        {
            var v = buffer[index] | (buffer[index + 1] << 8) | (buffer[index + 2] << 16);
            if ((v & 0x800000) != 0) v |= ~0xFFFFFF;
            return v / 8388608.0;
        }
        if (_bytesPerSample >= 4)
            return BitConverter.ToInt32(buffer, index) / 2147483648.0;
        return 0;
    }

    void ProcessChunk(float rms, float bassRms, float midRms, float trebleRms)
    {
        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (!_enabled) return;

            if (rms > 0.0015f) Interlocked.Exchange(ref _lastSignalAt, now);

            UpdateVisualBandsLocked(rms, bassRms, midRms, trebleRms);

            // Los beats se miden sobre el canal de bajos (pasa-bajos ~120 Hz):
            // así una voz no dispara el brillo como si fuera un golpe.
            _fastBass += (bassRms - _fastBass) * 0.5f;
            _slowBass += (bassRms - _slowBass) * 0.012f;
            if (_slowBass <= 0f) _slowBass = bassRms;

            var minGap = _bpm >= 20 ? 60000.0 / _bpm * 0.55 : 140.0;
            var sinceBeat = (now - Interlocked.Read(ref _lastBeatAt)) * 1000.0 / Stopwatch.Frequency;
            if (_historyCount >= HistoryHz * 2 &&
                _fastBass > _slowBass * 1.35f && _fastBass > 0.0025f && _slowBass > 0.0005f &&
                sinceBeat > minGap)
            {
                Interlocked.Exchange(ref _lastBeatAt, now);
                _diagBeats++;
            }

            if (SoundLog)
            {
                var ratio = _slowBass > 1e-6f ? _fastBass / _slowBass : 0;
                if (ratio > _diagMaxRatio) _diagMaxRatio = ratio;
                if (_diagAt == 0) _diagAt = now;
                if (now - _diagAt >= Stopwatch.Frequency * 5)
                {
                    Log.Write($"SoundSense: {_diagBeats} beats/5s, ratio max {_diagMaxRatio:F2}, bass {_fastBass:F4}");
                    _diagAt = now;
                    _diagBeats = 0;
                    _diagMaxRatio = 0;
                }
            }

            _history[_historyIndex] = rms;
            _historyIndex = (_historyIndex + 1) % HistorySize;
            if (_historyCount < HistorySize) _historyCount++;

            if (_historyCount >= HistoryHz * 2 && now - _lastBpmAt >= Stopwatch.Frequency * 2)
            {
                _lastBpmAt = now;
                UpdateBpmLocked();
            }
        }
    }

    void UpdateVisualBandsLocked(float rms, float bassRms, float midRms, float trebleRms)
    {
        // Auto-gain lento por banda: el visualizador recibe niveles 0..1 relativos
        // al pico reciente, no amplitudes absolutas que cambian con el volumen.
        _bassPeak = Math.Max(bassRms, _bassPeak * 0.9985f);
        _midPeak = Math.Max(midRms, _midPeak * 0.9985f);
        _treblePeak = Math.Max(trebleRms, _treblePeak * 0.9985f);
        _energyPeak = Math.Max(rms, _energyPeak * 0.9985f);

        var bass = NormalizeBand(bassRms, _bassPeak);
        var mid = NormalizeBand(midRms, _midPeak);
        var treble = NormalizeBand(trebleRms, _treblePeak);
        var energy = NormalizeBand(rms, _energyPeak);

        _bassLevel = SmoothLevel(_bassLevel, bass);
        _midLevel = SmoothLevel(_midLevel, mid);
        _trebleLevel = SmoothLevel(_trebleLevel, treble);
        _energyLevel = SmoothLevel(_energyLevel, energy);
    }

    static float NormalizeBand(float value, float peak)
    {
        if (peak < 0.00035f) return 0;
        var ratio = Math.Clamp(value / peak, 0f, 1f);
        return (float)Math.Pow(ratio, 0.62);
    }

    static float SmoothLevel(float current, float target) =>
        current + (target - current) * (target > current ? 0.38f : 0.075f);

    void UpdateBpmLocked()
    {
        var n = Math.Min(_historyCount, HistoryHz * 9 / 2);
        if (n < HistoryHz) return;

        var samples = _bpmScratch;
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var index = (_historyIndex - n + i + HistorySize * 2) % HistorySize;
            var v = _history[index];
            samples[i] = v;
            sum += v;
        }
        var mean = sum / n;
        var energy = 0.0;
        for (var i = 0; i < n; i++)
        {
            samples[i] -= mean;
            energy += samples[i] * samples[i];
        }
        energy /= n;
        if (energy < 1e-9) return;

        var minLag = (int)(HistoryHz * 0.30);
        var maxLag = (int)(HistoryHz * 1.05);
        var best = double.MinValue;
        var bestLag = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double acc = 0;
            var count = n - lag;
            if (count < HistoryHz) continue;
            for (var i = lag; i < n; i++) acc += samples[i] * samples[i - lag];
            acc /= count;
            if (acc > best)
            {
                best = acc;
                bestLag = lag;
            }
        }

        if (bestLag == 0) return;
        var confidence = best / energy;
        if (confidence < 0.10) return;

        var bpm = 60.0 * HistoryHz / bestLag;
        while (bpm < 70) bpm *= 2;
        while (bpm > 180) bpm /= 2;

        _bpm = _bpm <= 0 ? (float)bpm : (float)(_bpm * 0.62 + bpm * 0.38);
        Log.Write($"SoundSense: {_bpm:F0} BPM (lag {bestLag}, conf {confidence:F2})");
    }

    bool HasSignalLocked()
    {
        var last = Interlocked.Read(ref _lastSignalAt);
        if (last == 0) return false;
        return (Stopwatch.GetTimestamp() - last) < Stopwatch.Frequency * 2;
    }

    void ResetAnalyzerLocked()
    {
        _fastBass = 0;
        _slowBass = 0;
        _lp1 = 0;
        _lp2 = 0;
        _bandLow = 0;
        _bandMid = 0;
        _bpm = 0;
        _bassLevel = 0;
        _midLevel = 0;
        _trebleLevel = 0;
        _energyLevel = 0;
        _bassPeak = 0;
        _midPeak = 0;
        _treblePeak = 0;
        _energyPeak = 0;
        _diagAt = 0;
        _diagBeats = 0;
        _diagMaxRatio = 0;
        _historyIndex = 0;
        _historyCount = 0;
        _lastBpmAt = 0;
        Interlocked.Exchange(ref _lastSignalAt, 0);
        Interlocked.Exchange(ref _lastBeatAt, 0);
        Array.Clear(_history);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposedFlag = true;
        lock (_gate) _enabled = false;
        StopCapture();
        lock (_gate) ResetAnalyzerLocked();
    }
}
