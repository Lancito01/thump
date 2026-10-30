using System.Diagnostics;
using System.Runtime.InteropServices;
using Thump.Audio;
using Thump.Colors;
using Thump.Hue;

namespace Thump;

public readonly record struct LightOutput(int ChannelId, string Name, Rgb Color);

/// <summary>
/// The 50 Hz loop: audio features + colour source → per-light colours → Hue bridge (or just the on-screen preview).
/// </summary>
public sealed class SyncEngine : IDisposable
{
    const double FrameHz = 50;

    readonly AppSettings _settings;
    readonly AudioAnalyzer _audio = new();
    readonly AlbumArtSource _albumArt = new();

    Thread? _thread;
    volatile bool _running;
    EntertainmentStreamer? _streamer;
    HueBridgeClient? _bridge;
    string? _streamingAreaId;

    // Snapshots for the UI (replaced atomically).
    public volatile LightOutput[] Outputs = [];
    public volatile AudioFeatures? LastFeatures;

    public event Action<string>? Status;
    public AlbumArtSource AlbumArt => _albumArt;
    public bool IsStreaming => _streamer != null;
    public string AudioDeviceName => _audio.DeviceName;

    // Per-light smoothing state
    readonly Dictionary<int, double> _envelopes = [];
    readonly Dictionary<int, Rgb> _screenColors = [];
    double _paletteOffset, _paletteTarget;
    long _lastKickCount;

    public SyncEngine(AppSettings settings)
    {
        _settings = settings;
        // New track → new kick drum: relearn its strength and tempo from scratch.
        _albumArt.Changed += _ => _audio.ResetKickLearning();
    }

    /// <summary>Starts audio capture and the render loop (preview only until <see cref="StartStreamingAsync"/>).</summary>
    public void Start()
    {
        RestartAudio();
        _albumArt.Start();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Thump engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void RestartAudio()
    {
        try
        {
            _audio.Start(_settings.AudioDeviceId);
        }
        catch (Exception e)
        {
            Status?.Invoke($"Audio capture failed: {e.Message}");
            if (_settings.AudioDeviceId != null) { _settings.AudioDeviceId = null; _audio.Start(); }
        }
    }

    public async Task StartStreamingAsync(EntertainmentArea area)
    {
        await StopStreamingAsync();
        var s = _settings;
        if (s.BridgeIp == null || s.Username == null || s.ClientKey == null)
            throw new InvalidOperationException("Pair with a bridge first.");

        _bridge = new HueBridgeClient(s.BridgeIp, s.Username);
        s.ApplicationId ??= await _bridge.GetApplicationIdAsync();
        await _bridge.StartStreamingAsync(area.Id);
        _streamingAreaId = area.Id;
        try
        {
            _streamer = await Task.Run(() => EntertainmentStreamer.Connect(s.BridgeIp, s.ApplicationId, s.ClientKey, area.Id));
        }
        catch
        {
            await _bridge.StopStreamingAsync(area.Id);
            _streamingAreaId = null;
            throw;
        }
        Status?.Invoke($"Streaming to \"{area.Name}\"");
    }

    public async Task StopStreamingAsync()
    {
        var streamer = _streamer;
        _streamer = null;
        streamer?.Dispose();
        if (_bridge != null && _streamingAreaId != null)
        {
            try { await _bridge.StopStreamingAsync(_streamingAreaId); } catch { /* lights return to normal after a timeout anyway */ }
        }
        _bridge?.Dispose();
        _bridge = null;
        _streamingAreaId = null;
    }

    void Loop()
    {
        timeBeginPeriod(1);
        ScreenSampler? screen = null;
        var clock = Stopwatch.StartNew();
        double last = 0, nextFrame = 0;
        int frame = 0;
        var palette = new List<Rgb>();
        try
        {
            while (_running)
            {
                double now = clock.Elapsed.TotalSeconds;
                double dt = Math.Clamp(now - last, 0.001, 0.1);
                last = now;

                var s = _settings;
                _audio.KickSensitivity = s.KickSensitivity;
                _audio.KickDecayMs = s.KickDecayMs;
                _audio.AdaptSeconds = s.AdaptSeconds;
                _audio.Tick(dt);
                var f = _audio.Snapshot();
                LastFeatures = f;

                bool screenMode = s.ColorSource == ColorSource.Screen;
                if (screenMode)
                {
                    screen ??= new ScreenSampler();
                    if (frame % 2 == 0) screen.Capture();
                }
                if (frame % 10 == 0) palette = CurrentPalette(s);

                var outputs = Render(s, f, dt, palette, screenMode ? screen : null);
                Outputs = outputs;

                var streamer = _streamer;
                if (streamer != null)
                {
                    try
                    {
                        streamer.Send(outputs.Select(o => (o.ChannelId, o.Color.R, o.Color.G, o.Color.B)).ToList());
                    }
                    catch (Exception e)
                    {
                        Status?.Invoke($"Lost connection to bridge: {e.Message}");
                        _ = StopStreamingAsync();
                    }
                }

                frame++;
                nextFrame += 1 / FrameHz;
                var wait = nextFrame - clock.Elapsed.TotalSeconds;
                if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                else if (wait < -0.2) nextFrame = clock.Elapsed.TotalSeconds; // fell behind (e.g. sleep): resync
            }
        }
        finally
        {
            screen?.Dispose();
            timeEndPeriod(1);
        }
    }

    List<Rgb> CurrentPalette(AppSettings s)
    {
        List<Rgb> p = s.ColorSource switch
        {
            ColorSource.AlbumArt when _albumArt.Current?.Palette is { Count: > 0 } art => [.. art],
            ColorSource.AlbumArt => Palettes.Parse(Palettes.Presets[s.Preset]), // nothing playing: fall back
            ColorSource.Custom => Palettes.Parse(s.CustomColors.Split([',', ' ', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)),
            _ => Palettes.Parse(Palettes.Presets.GetValueOrDefault(s.Preset) ?? Palettes.Presets.First().Value),
        };
        return p.Select(c => c.Vivid(s.Vividness).Normalized()).ToList();
    }

    LightOutput[] Render(AppSettings s, AudioFeatures f, double dt, List<Rgb> palette, ScreenSampler? screen)
    {
        var lights = CurrentLights(s);
        var L = f.Levels;
        var w = s.Bands.Select(b => b.Weight).ToArray();

        double Weighted(params Band[] bands)
        {
            double sum = 0, ws = 0;
            foreach (var b in bands) { sum += w[(int)b] * L[(int)b]; ws += w[(int)b]; }
            return ws > 0 ? sum / ws : 0;
        }

        double mix = Weighted(Band.Sub, Band.Bass, Band.LowMid, Band.Mid, Band.High, Band.Air);
        double bass = Weighted(Band.Sub, Band.Bass);
        double mids = Weighted(Band.LowMid, Band.Mid);
        double highs = Weighted(Band.High, Band.Air);
        double kick = f.KickEnvelope;

        // Palette motion
        if (f.KickCount != _lastKickCount)
        {
            if (s.ColorMotion == ColorMotion.StepOnKick && f.KickCount - _lastKickCount < 4) _paletteTarget += 1;
            _lastKickCount = f.KickCount;
        }
        if (s.ColorMotion == ColorMotion.Drift) _paletteTarget += dt * s.DriftSpeed;
        _paletteOffset += (_paletteTarget - _paletteOffset) * (1 - Math.Exp(-dt / 0.08));

        var outputs = new List<LightOutput>(lights.Count);
        int idx = 0;
        foreach (var light in lights)
        {
            if (!light.Enabled)
            {
                outputs.Add(new LightOutput(light.ChannelId, light.Name, Rgb.Black));
                continue;
            }

            double level = light.Role switch
            {
                LightRole.Bass => bass + s.KickPunch * kick,
                LightRole.Kick => kick,
                LightRole.Mids => mids,
                LightRole.Highs => highs,
                LightRole.Ambient => 1,
                _ => mix + s.KickPunch * kick,
            };
            level = Math.Pow(Math.Clamp(level * s.Gain, 0, 1), s.Contrast);

            // Fast attack, slower release, per light.
            double env = _envelopes.GetValueOrDefault(light.ChannelId);
            double tau = (level > env ? s.AttackMs : s.ReleaseMs) / 1000;
            env += (level - env) * (1 - Math.Exp(-dt / Math.Max(0.001, tau)));
            _envelopes[light.ChannelId] = env;

            double brightness = s.MinBrightness + (s.MaxBrightness - s.MinBrightness) * env;
            Rgb color;
            if (screen != null)
            {
                // x: -1 left … 1 right; z: -1 floor … 1 ceiling (Hue entertainment coordinates)
                var target = screen.Sample((light.X + 1) / 2, 1 - (light.Z + 1) / 2, 0.18).Vivid(s.Vividness);
                var prev = _screenColors.GetValueOrDefault(light.ChannelId, target);
                var smoothed = Rgb.Lerp(prev, target, 1 - Math.Exp(-dt / Math.Max(0.001, s.ScreenSmoothingMs / 1000)));
                _screenColors[light.ChannelId] = smoothed;
                double audioScale = light.Role == LightRole.Ambient ? 1 : 1 - s.ScreenAudioMix + s.ScreenAudioMix * brightness / Math.Max(0.01, s.MaxBrightness);
                color = smoothed * (s.MaxBrightness * audioScale);
            }
            else
            {
                color = PaletteAt(palette, idx + _paletteOffset) * brightness;
                if (light.Role is LightRole.Mix or LightRole.Highs)
                    color = Rgb.Lerp(color, Rgb.White * brightness, Math.Clamp(highs * highs * s.Sparkle, 0, 1));
            }
            outputs.Add(new LightOutput(light.ChannelId, light.Name, color));
            idx++;
        }
        return outputs.ToArray();
    }

    static Rgb PaletteAt(List<Rgb> p, double pos)
    {
        if (p.Count == 1) return p[0];
        pos = ((pos % p.Count) + p.Count) % p.Count;
        int i = (int)pos;
        double t = pos - i;
        // Ease so colours dwell, then move quickly — feels more on-beat than a linear fade.
        t = t * t * (3 - 2 * t);
        return Rgb.Lerp(p[i], p[(i + 1) % p.Count], t);
    }

    static readonly List<LightSetting> PreviewLights =
    [
        new() { ChannelId = 0, Name = "Left", X = -0.9, Z = 0 },
        new() { ChannelId = 1, Name = "Left-center", X = -0.4, Z = 0.5 },
        new() { ChannelId = 2, Name = "Center", X = 0, Z = 0.8, Role = LightRole.Kick },
        new() { ChannelId = 3, Name = "Right-center", X = 0.4, Z = 0.5 },
        new() { ChannelId = 4, Name = "Right", X = 0.9, Z = 0 },
    ];

    /// <summary>Lights of the selected entertainment area; null shows the virtual preview lights.</summary>
    public volatile List<LightSetting>? Lights;

    List<LightSetting> CurrentLights(AppSettings s) => Lights is { Count: > 0 } l ? l : PreviewLights;

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        StopStreamingAsync().Wait(2000);
        _audio.Dispose();
        _albumArt.Dispose();
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
}
