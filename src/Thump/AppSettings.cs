using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Thump.Audio;

namespace Thump;

public enum ColorSource { Preset, AlbumArt, Screen, Custom }
public enum ColorMotion { StepOnKick, Drift, Static }
public enum LightRole { Mix, Bass, Kick, Clap, Mids, Highs, Ambient }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BandSetting : Observable
{
    double _weight = 1;
    public Band Band { get; set; }
    [JsonIgnore] public string Label => AudioAnalyzer.Bands[(int)Band].Label;
    /// <summary>How strongly this band drives brightness (0 = ignored).</summary>
    public double Weight { get => _weight; set => Set(ref _weight, value); }
}

public sealed class LightSetting : Observable
{
    bool _enabled = true;
    LightRole _role = LightRole.Mix;
    public int ChannelId { get; set; }
    [JsonIgnore] public string Name { get; set; } = "";
    [JsonIgnore] public double X { get; set; }
    [JsonIgnore] public double Y { get; set; }
    [JsonIgnore] public double Z { get; set; }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public LightRole Role { get => _role; set => Set(ref _role, value); }
}

public sealed class AppSettings : Observable
{
    /// <summary>Bumped when a default changes in a way existing users should get too.</summary>
    /// <remarks>0 when missing from an older settings file; new installs and every save write the current version.</remarks>
    public int SettingsVersion { get; set; }
    const int CurrentVersion = 2;

    // App behaviour
    bool _runInBackground = true, _startWithWindows, _autoStartSync, _checkForUpdates, _autoInstallUpdates, _trayHintShown, _rememberSongs;
    double _songCacheMb = 50;
    /// <summary>Closing the window hides it to the tray; sync keeps running until Quit.</summary>
    public bool RunInBackground { get => _runInBackground; set => Set(ref _runInBackground, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool AutoStartSync { get => _autoStartSync; set => Set(ref _autoStartSync, value); }
    /// <summary>Opt-in: look for new releases on GitHub.</summary>
    public bool CheckForUpdates { get => _checkForUpdates; set => Set(ref _checkForUpdates, value); }
    /// <summary>Opt-in: install downloaded updates without asking (only while not syncing).</summary>
    public bool AutoInstallUpdates { get => _autoInstallUpdates; set => Set(ref _autoInstallUpdates, value); }
    public bool TrayHintShown { get => _trayHintShown; set => Set(ref _trayHintShown, value); }
    /// <summary>Opt-in: keep what was learned about each song on disk, so the next play starts out knowing it.</summary>
    public bool RememberSongs { get => _rememberSongs; set => Set(ref _rememberSongs, value); }
    /// <summary>Disk space for remembered songs; the least recently played are forgotten first.</summary>
    public double SongCacheMb { get => _songCacheMb; set => Set(ref _songCacheMb, value); }

    // Bridge
    string? _bridgeIp, _username, _clientKey, _applicationId, _areaId;
    public string? BridgeIp { get => _bridgeIp; set => Set(ref _bridgeIp, value); }
    public string? Username { get => _username; set => Set(ref _username, value); }
    public string? ClientKey { get => _clientKey; set => Set(ref _clientKey, value); }
    public string? ApplicationId { get => _applicationId; set => Set(ref _applicationId, value); }
    public string? AreaId { get => _areaId; set => Set(ref _areaId, value); }
    /// <summary>Per-area light assignments, keyed by entertainment area id.</summary>
    public Dictionary<string, List<LightSetting>> Lights { get; set; } = [];

    // Audio
    string? _audioDeviceId;
    double _gain = 1.2, _kickPunch = 0.6, _kickSensitivity = 1.0, _kickDecayMs = 180, _clapSensitivity = 1.0, _clapDecayMs = 140;
    double _attackMs = 15, _releaseMs = 160, _contrast = 1.8, _adaptSeconds = 6;
    double _minBrightness = 0.02, _maxBrightness = 1.0;
    public string? AudioDeviceId { get => _audioDeviceId; set => Set(ref _audioDeviceId, value); }
    public ObservableCollection<BandSetting> Bands { get; set; } = DefaultBands();
    public double Gain { get => _gain; set => Set(ref _gain, value); }
    public double KickPunch { get => _kickPunch; set => Set(ref _kickPunch, value); }
    public double KickSensitivity { get => _kickSensitivity; set => Set(ref _kickSensitivity, value); }
    public double KickDecayMs { get => _kickDecayMs; set => Set(ref _kickDecayMs, value); }
    public double ClapSensitivity { get => _clapSensitivity; set => Set(ref _clapSensitivity, value); }
    public double ClapDecayMs { get => _clapDecayMs; set => Set(ref _clapDecayMs, value); }
    public double AttackMs { get => _attackMs; set => Set(ref _attackMs, value); }
    public double ReleaseMs { get => _releaseMs; set => Set(ref _releaseMs, value); }
    public double Contrast { get => _contrast; set => Set(ref _contrast, value); }
    public double AdaptSeconds { get => _adaptSeconds; set => Set(ref _adaptSeconds, value); }
    public double MinBrightness { get => _minBrightness; set => Set(ref _minBrightness, value); }
    public double MaxBrightness { get => _maxBrightness; set => Set(ref _maxBrightness, value); }

    // Colour
    ColorSource _colorSource = ColorSource.AlbumArt;
    ColorMotion _colorMotion = ColorMotion.StepOnKick;
    string _preset = "Warehouse", _customColors = "#FF0000, #0000FF, #FFFFFF";
    double _driftSpeed = 0.1, _sparkle = 0.25, _vividness = 0.5, _screenAudioMix = 0.5, _screenSmoothingMs = 120;
    public ColorSource ColorSource { get => _colorSource; set => Set(ref _colorSource, value); }
    public ColorMotion ColorMotion { get => _colorMotion; set => Set(ref _colorMotion, value); }
    public string Preset { get => _preset; set => Set(ref _preset, value); }
    public string CustomColors { get => _customColors; set => Set(ref _customColors, value); }
    /// <summary>Palette steps per second in Drift mode.</summary>
    public double DriftSpeed { get => _driftSpeed; set => Set(ref _driftSpeed, value); }
    /// <summary>How much hi-hats/highs flash lights toward white.</summary>
    public double Sparkle { get => _sparkle; set => Set(ref _sparkle, value); }
    public double Vividness { get => _vividness; set => Set(ref _vividness, value); }
    /// <summary>In Screen mode: 0 = pure ambilight, 1 = brightness fully driven by the music.</summary>
    public double ScreenAudioMix { get => _screenAudioMix; set => Set(ref _screenAudioMix, value); }
    public double ScreenSmoothingMs { get => _screenSmoothingMs; set => Set(ref _screenSmoothingMs, value); }

    static ObservableCollection<BandSetting> DefaultBands() =>
    [
        new() { Band = Band.Sub, Weight = 1.6 },
        new() { Band = Band.Bass, Weight = 1.8 },
        new() { Band = Band.LowMid, Weight = 0.5 },
        new() { Band = Band.Mid, Weight = 0.25 },
        new() { Band = Band.High, Weight = 0.2 },
        new() { Band = Band.Air, Weight = 0.1 },
    ];

    public void ResetAudio()
    {
        var d = new AppSettings();
        for (int i = 0; i < Bands.Count; i++) Bands[i].Weight = d.Bands[i].Weight;
        Gain = d.Gain; KickPunch = d.KickPunch; KickSensitivity = d.KickSensitivity; KickDecayMs = d.KickDecayMs;
        ClapSensitivity = d.ClapSensitivity; ClapDecayMs = d.ClapDecayMs;
        AttackMs = d.AttackMs; ReleaseMs = d.ReleaseMs; Contrast = d.Contrast; AdaptSeconds = d.AdaptSeconds;
        MinBrightness = d.MinBrightness; MaxBrightness = d.MaxBrightness;
    }

    // Persistence

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thump");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s != null)
                {
                    if (s.Bands.Count != AudioAnalyzer.Bands.Length) s.Bands = DefaultBands();
                    if (s.SettingsVersion < 2) s.ColorSource = ColorSource.AlbumArt; // v2: album art is the default
                    s.SettingsVersion = CurrentVersion;
                    return s;
                }
            }
        }
        catch
        {
            // Corrupt settings: start fresh rather than crash.
        }
        return new AppSettings { SettingsVersion = CurrentVersion };
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
