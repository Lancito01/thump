using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Thump.Audio;

public enum Band { Sub, Bass, LowMid, Mid, High, Air }

/// <summary>A snapshot of what the music is doing right now.</summary>
public sealed record AudioFeatures(
    double[] Levels,      // per band, 0..1 after adaptive normalisation
    double KickEnvelope,  // 1 on a detected kick, decaying to 0
    long KickCount,       // increments on every kick
    double Bpm,           // followed tempo, 0 if unknown
    bool BeatLocked,      // kicks are landing on a steady (possibly drifting) grid
    double Learned);      // 0..1, how much the kick model knows about this track

/// <summary>
/// Captures whatever Windows is playing (WASAPI loopback) and turns it into band levels plus kick-drum detection.
/// Unlike an "average loudness" approach, every band is normalised on its own, so a rumbling sub-bass line
/// isn't drowned out by hats and synths. Kicks come from <see cref="KickDetector"/>, which learns each track.
/// </summary>
public sealed class AudioAnalyzer : IDisposable
{
    public static readonly (Band Band, double Lo, double Hi, string Label)[] Bands =
    [
        (Band.Sub,    20,    60,    "Sub 20–60 Hz"),
        (Band.Bass,   60,    150,   "Bass 60–150 Hz"),
        (Band.LowMid, 150,   400,   "Low-mid 150–400 Hz"),
        (Band.Mid,    400,   2000,  "Mid 400 Hz–2 kHz"),
        (Band.High,   2000,  8000,  "High 2–8 kHz"),
        (Band.Air,    8000,  20000, "Air 8–20 kHz"),
    ];

    const int FftSize = 2048;
    const int FftLog2 = 11;
    const int Hop = 512;

    readonly object _lock = new();
    readonly float[] _ring = new float[FftSize];
    readonly Complex[] _fft = new Complex[FftSize];
    readonly float[] _window = new float[FftSize];
    readonly double[] _levels = new double[Bands.Length];
    readonly double[] _bandPeak = new double[Bands.Length];
    readonly double[] _bandFloor = new double[Bands.Length];
    int _ringPos, _sinceHop;
    long _lastAnalyzeMs;

    WasapiLoopbackCapture? _capture;
    int _sampleRate = 48000;

    readonly KickDetector _kick = new();
    double _kickEnv;
    long _kickCount;
    long _samplesSeen;

    /// <summary>1 = default; higher accepts hits that are less kick-like.</summary>
    public double KickSensitivity { get; set; } = 1.0;
    public double KickDecayMs { get; set; } = 180;
    /// <summary>Seconds for the per-band auto-gain to forget a loud peak.</summary>
    public double AdaptSeconds { get; set; } = 6;

    public string DeviceName { get; private set; } = "";

    public void Start(string? deviceId = null)
    {
        Stop();
        var device = deviceId == null
            ? new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : new MMDeviceEnumerator().GetDevice(deviceId);
        DeviceName = device.FriendlyName;
        _capture = new WasapiLoopbackCapture(device);
        _sampleRate = _capture.WaveFormat.SampleRate;
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    public static IReadOnlyList<(string Id, string Name)> ListOutputDevices() =>
        new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => (d.ID, d.FriendlyName)).ToList();

    public void Stop()
    {
        if (_capture == null) return;
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { /* device may have vanished */ }
        _capture.Dispose();
        _capture = null;
    }

    public AudioFeatures Snapshot()
    {
        lock (_lock)
        {
            // Let levels fall back to silence if loopback stops delivering (WASAPI sends nothing when idle).
            return new AudioFeatures((double[])_levels.Clone(), _kickEnv, _kickCount, _kick.Bpm, _kick.Locked, _kick.Confidence);
        }
    }

    /// <summary>Called by the engine every frame so envelopes keep decaying while no audio arrives.</summary>
    public void Tick(double dtSeconds)
    {
        lock (_lock)
        {
            _kickEnv *= Math.Exp(-dtSeconds * 1000 / Math.Max(20, KickDecayMs));
            if (Environment.TickCount64 - _lastAnalyzeMs > 250)
                for (int b = 0; b < _levels.Length; b++) _levels[b] *= Math.Exp(-dtSeconds * 8);
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture!.WaveFormat;
        int ch = fmt.Channels;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample == 32 && fmt.Encoding == WaveFormatEncoding.Extensible)
        {
            int frames = e.BytesRecorded / (4 * ch);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += BitConverter.ToSingle(e.Buffer, (f * ch + c) * 4);
                Push(sum / ch);
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            int frames = e.BytesRecorded / (2 * ch);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += BitConverter.ToInt16(e.Buffer, (f * ch + c) * 2) / 32768f;
                Push(sum / ch);
            }
        }
    }

    void Push(float sample)
    {
        _ring[_ringPos] = sample;
        _ringPos = (_ringPos + 1) % FftSize;
        _samplesSeen++;
        if (++_sinceHop >= Hop)
        {
            _sinceHop = 0;
            Analyze();
        }
    }

    void Analyze()
    {
        if (_window[1] == 0)
            for (int i = 0; i < FftSize; i++)
                _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);

        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = _ring[(_ringPos + i) % FftSize] * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftLog2, _fft);

        double binHz = (double)_sampleRate / FftSize;
        double dt = (double)Hop / _sampleRate;
        // Per-hop decay factor so the adaptive peak forgets over AdaptSeconds.
        double forget = Math.Exp(-dt / Math.Max(0.5, AdaptSeconds));

        lock (_lock)
        {
            _lastAnalyzeMs = Environment.TickCount64;
            for (int b = 0; b < Bands.Length; b++)
            {
                double e = BandEnergy(Bands[b].Lo, Bands[b].Hi, binHz);
                double db = 10 * Math.Log10(e + 1e-12);

                // Adaptive range per band: peak jumps up instantly and sinks slowly, floor tracks quiet parts.
                if (_bandPeak[b] == 0) { _bandPeak[b] = db; _bandFloor[b] = db - 30; }
                _bandPeak[b] = Math.Max(db, _bandPeak[b] - (1 - forget) * 40);
                _bandFloor[b] = db < _bandFloor[b] ? db : _bandFloor[b] + (1 - forget) * 8;
                _bandFloor[b] = Math.Min(_bandFloor[b], _bandPeak[b] - 24); // at least 24 dB of range

                double norm = (db - _bandFloor[b]) / Math.Max(1, _bandPeak[b] - _bandFloor[b]);
                // Absolute gate so digital silence doesn't get stretched into flicker.
                if (db < -95) norm = 0;
                _levels[b] = Math.Clamp(norm, 0, 1);
            }

            _kick.Sensitivity = KickSensitivity;
            if (_kick.Process(_fft, FftSize, binHz, dt, _samplesSeen, Hop, _sampleRate))
            {
                _kickEnv = 1;
                _kickCount++;
            }
        }
    }

    double BandEnergy(double lo, double hi, double binHz)
    {
        int a = Math.Max(1, (int)Math.Floor(lo / binHz));
        int z = Math.Min(FftSize / 2 - 1, (int)Math.Ceiling(hi / binHz));
        double sum = 0;
        for (int i = a; i <= z; i++) sum += _fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y;
        return sum / Math.Max(1, z - a + 1);
    }

    /// <summary>Forget everything learned about the current track's kick (call on track change).</summary>
    public void ResetKickLearning()
    {
        lock (_lock) _kick.Reset();
    }

    /// <summary>Feeds mono samples directly (used by tests and offline analysis).</summary>
    public void Feed(ReadOnlySpan<float> mono, int sampleRate)
    {
        _sampleRate = sampleRate;
        foreach (var x in mono) Push(x);
    }

    public void Dispose() => Stop();
}
