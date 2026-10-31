using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Thump.Audio;

public enum Band { Sub, Bass, LowMid, Mid, High, Air }

/// <summary>A snapshot of what the music is doing right now.</summary>
public sealed record AudioFeatures(
    double[] Levels,         // per band, 0..1 after adaptive normalisation
    double KickEnvelope,     // 1 on a detected kick, decaying to 0
    long KickCount,          // increments on every kick
    double ClapEnvelope,     // the same for claps and snares
    long ClapCount,
    double Bpm,              // followed tempo, 0 if unknown
    bool BeatLocked,         // hits are landing where the beat grid predicts
    double TempoConfidence,  // 0..1, how well recent hits matched the predicted grid
    double KickLearned,      // 0..1, how much the kick model knows about this track
    double ClapLearned,
    double KickConfidence,   // 0..1, how sure it was about the latest kick
    double ClapConfidence,
    bool Remembered,         // this track's models came from the song cache
    bool ModelActive = false,      // the learned drum model drives the kick and clap envelopes
    double HatEnvelope = 0,        // hi-hats (model only)
    double OldKickEnvelope = 0,    // the hand-tuned detectors, for comparison while the model is on
    double OldClapEnvelope = 0);

/// <summary>
/// Captures whatever Windows is playing (WASAPI loopback) and turns it into band levels plus kick and clap/snare
/// detection. Unlike an "average loudness" approach, every band is normalised on its own, so a rumbling sub-bass line
/// isn't drowned out by hats and synths. Hits come from two <see cref="HitDetector"/>s that learn each track by ear,
/// and the tempo from a <see cref="BeatTracker"/> that follows their confident hits.
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
    public const int Hop = 512;

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

    readonly SpectralFeatures _spectrum = new();
    readonly HitDetector _kick = HitDetector.Kick();
    readonly HitDetector _clap = HitDetector.Clap();
    readonly BeatTracker _beat = new();
    readonly ModelFeatures _model = new();
    double _kickEnv, _clapEnv, _hatEnv, _oldKickEnv, _oldClapEnv;
    DrumModel? _drums;
    readonly double[] _lastDrumHit = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    readonly bool[] _drumAbove = new bool[DrumModel.Drums];
    long _kickCount, _clapCount;
    long _samplesSeen;
    bool _remembered;

    /// <summary>1 = default; higher accepts sounds the model is less sure are kicks.</summary>
    public double KickSensitivity { get; set; } = 1.0;
    public double KickDecayMs { get; set; } = 180;
    public double ClapSensitivity { get; set; } = 1.0;
    public double ClapDecayMs { get; set; } = 140;
    /// <summary>Seconds for the per-band auto-gain to forget a loud peak.</summary>
    public double AdaptSeconds { get; set; } = 6;

    public string DeviceName { get; private set; } = "";

    /// <summary>Where the experimental drum model's weights are looked for. Not shipped yet.</summary>
    public static readonly string DrumModelPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thump", "drums.thdm");
    /// <summary>Use the learned drum model (if its weights are installed) instead of the hand-tuned detectors.</summary>
    public bool UseDrumModel { get; set; }

    /// <summary>
    /// Offline analysis only (dataset export, scoring): called after every hop with the time the lights would react,
    /// the <see cref="ModelFeatures"/> values for that hop, and the kick and clap confidences if either fired.
    /// </summary>
    public Action<double, float[], double?, double?>? HopAnalyzed { get; set; }

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
            return new AudioFeatures((double[])_levels.Clone(), _kickEnv, _kickCount, _clapEnv, _clapCount,
                _beat.Bpm, _beat.Locked, _beat.Confidence, _kick.Learned, _clap.Learned,
                _kick.LastConfidence, _clap.LastConfidence, _remembered,
                _drums != null && UseDrumModel, _hatEnv, _oldKickEnv, _oldClapEnv);
        }
    }

    /// <summary>Called by the engine every frame so envelopes keep decaying while no audio arrives.</summary>
    public void Tick(double dtSeconds)
    {
        lock (_lock)
        {
            _kickEnv *= Math.Exp(-dtSeconds * 1000 / Math.Max(20, KickDecayMs));
            _clapEnv *= Math.Exp(-dtSeconds * 1000 / Math.Max(20, ClapDecayMs));
            _oldKickEnv *= Math.Exp(-dtSeconds * 1000 / Math.Max(20, KickDecayMs));
            _oldClapEnv *= Math.Exp(-dtSeconds * 1000 / Math.Max(20, ClapDecayMs));
            _hatEnv *= Math.Exp(-dtSeconds * 1000 / 90);
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
            _clap.Sensitivity = ClapSensitivity;
            _spectrum.Compute(_fft, FftSize, binHz, dt);
            double time = (double)(_samplesSeen - Hop) / _sampleRate; // the hop the detectors are deciding about
            bool model = UseDrumModel && LoadDrumModel();
            double? kick = _kick.Process(_fft, FftSize, binHz, dt, _spectrum, _beat, _samplesSeen, Hop, _sampleRate);
            if (kick != null)
            {
                _oldKickEnv = 1;
                if (!model)
                {
                    _kickEnv = 1;
                    _kickCount++;
                    if (kick.Value >= 0.6) _beat.Observe(time, 1);
                }
            }
            double? clap = _clap.Process(_fft, FftSize, binHz, dt, _spectrum, _beat, _samplesSeen, Hop, _sampleRate);
            if (clap != null)
            {
                _oldClapEnv = 1;
                if (!model)
                {
                    _clapEnv = 1;
                    _clapCount++;
                    if (clap.Value >= 0.65) _beat.Observe(time, 0.6);
                }
            }
            if (model || HopAnalyzed != null) _model.Compute(_fft, FftSize, binHz);
            if (model) RunDrumModel(dt);
            _beat.Feed(_spectrum.Flux, time);
            _beat.Tick(time, dt);
            if (HopAnalyzed != null)
            {
                HopAnalyzed((double)_samplesSeen / _sampleRate, _model.Values, kick, clap);
            }
        }
    }

    bool LoadDrumModel()
    {
        if (_drums != null) return true;
        if (_drumsTried) return false;
        _drumsTried = true;
        try { _drums = File.Exists(DrumModelPath) ? DrumModel.Load(DrumModelPath) : null; }
        catch { _drums = null; }
        return _drums != null;
    }
    bool _drumsTried;

    /// <summary>
    /// One hop of the learned model: a drum fires when its probability rises over its threshold, at most once per
    /// refractory time. Sensitivity moves the threshold in log-odds, so 1 is the tuned balance and higher flashes more.
    /// </summary>
    void RunDrumModel(double dt)
    {
        var m = _drums!;
        m.Step(_model.Values, dt);
        double now = (double)_samplesSeen / _sampleRate;
        // The model answers Delay hops after the hit; the beat tracker wants the hit's own time.
        double hitTime = now - (m.Delay + 1) * dt;
        double[] sensitivity = [KickSensitivity, ClapSensitivity, 1];
        for (int d = 0; d < DrumModel.Drums; d++)
        {
            double th = m.Thresholds[d], logit = Math.Log(th / (1 - th)) - 2 * Math.Log(Math.Max(0.05, sensitivity[d]));
            bool above = m.Probabilities[d] >= 1 / (1 + Math.Exp(-logit));
            bool fire = above && !_drumAbove[d] && now - _lastDrumHit[d] >= m.Refractory;
            _drumAbove[d] = above;
            if (!fire) continue;
            _lastDrumHit[d] = now;
            switch (d)
            {
                case 0: _kickEnv = 1; _kickCount++; _beat.Observe(hitTime, 1); break;
                case 1: _clapEnv = 1; _clapCount++; _beat.Observe(hitTime, 0.6); break;
                default: _hatEnv = 1; break;
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

    /// <summary>A new track: forget the last one's drums and tempo, or pick up what was remembered about this one.</summary>
    public void StartTrack(SongModel? remembered = null)
    {
        lock (_lock)
        {
            _kick.Reset();
            _clap.Reset();
            // The drum model carries over: it knows drums in general, not this track.
            _beat.Reset(remembered?.BeatPeriod ?? 0);
            _remembered = remembered != null;
            if (remembered == null) return;
            _kick.Import(remembered.Kick);
            _clap.Import(remembered.Clap);
        }
    }

    /// <summary>What has been learned about the current track so far.</summary>
    public SongModel ExportModel()
    {
        lock (_lock)
            return new SongModel
            {
                BeatPeriod = _beat.Locked ? _beat.Period : 0,
                Kick = _kick.Export(),
                Clap = _clap.Export(),
            };
    }

    /// <summary>Feeds mono samples directly (used by tests and offline analysis).</summary>
    public void Feed(ReadOnlySpan<float> mono, int sampleRate)
    {
        _sampleRate = sampleRate;
        foreach (var x in mono) Push(x);
    }

    public void Dispose() => Stop();
}
