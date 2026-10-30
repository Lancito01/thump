using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Thump.Audio;
using Thump.Colors;
using Thump.Hue;

namespace Thump;

public partial class MainWindow : Window
{
    public static LightRole[] Roles { get; } = Enum.GetValues<LightRole>();

    static readonly string[] BandShortNames = ["Sub", "Bass", "Lo-mid", "Mid", "High", "Air"];

    readonly AppSettings _settings = AppSettings.Load();
    readonly SyncEngine _engine;
    readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly List<Rectangle> _meters = [];
    readonly List<(Ellipse Dot, TextBlock Label)> _previewDots = [];
    int _tick;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _settings;
        _engine = new SyncEngine(_settings);
        _engine.Status += msg => Dispatcher.BeginInvoke(() => { StatusText.Text = msg; UpdateSyncButton(); });
        _engine.AlbumArt.Changed += np => Dispatcher.BeginInvoke(() => ShowNowPlaying(np));

        SourceBox.ItemsSource = Enum.GetValues<ColorSource>();
        MotionBox.ItemsSource = Enum.GetValues<ColorMotion>();
        PresetBox.ItemsSource = Palettes.Presets.Keys;
        BandList.ItemsSource = _settings.Bands;

        var devices = new List<(string? Id, string Name)> { (null, "Default output device") };
        devices.AddRange(AudioAnalyzer.ListOutputDevices().Select(d => ((string?)d.Id, d.Name)));
        DeviceBox.ItemsSource = devices.Select(d => d.Name).ToList();
        DeviceBox.Tag = devices;
        DeviceBox.SelectedIndex = Math.Max(0, devices.FindIndex(d => d.Id == _settings.AudioDeviceId));

        for (int i = 0; i < AudioAnalyzer.Bands.Length; i++)
        {
            var bar = new Rectangle { Fill = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0), RadiusX = 2, RadiusY = 2 };
            var cell = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2E)), Margin = new Thickness(3, 0, 3, 0) };
            cell.Children.Add(bar);
            BandMeters.Children.Add(cell);
            _meters.Add(bar);
            BandLabels.Children.Add(new TextBlock { Text = BandShortNames[i], FontSize = 10, TextAlignment = TextAlignment.Center, Foreground = (Brush)FindResource("Muted") });
        }

        _settings.PropertyChanged += OnSettingChanged;
        UpdateColorPanels();
        _uiTimer.Tick += (_, _) => RenderUi();

        Loaded += async (_, _) =>
        {
            _engine.Start();
            _uiTimer.Start();
            StatusText.Text = $"Preview mode — listening to {_engine.AudioDeviceName}.";
            if (_settings.Username != null) await RefreshAreasAsync(quiet: true);
        };
        Closing += OnClosing;
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        _uiTimer.Stop();
        _engine.Dispose();
        try { _settings.Save(); } catch { /* nothing useful to do on exit */ }
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ColorSource)) UpdateColorPanels();
    }

    void UpdateColorPanels()
    {
        var src = _settings.ColorSource;
        PresetPanel.Visibility = src is ColorSource.Preset or ColorSource.AlbumArt ? Visibility.Visible : Visibility.Collapsed;
        CustomPanel.Visibility = src == ColorSource.Custom ? Visibility.Visible : Visibility.Collapsed;
        ScreenPanel.Visibility = src == ColorSource.Screen ? Visibility.Visible : Visibility.Collapsed;
        MotionPanel.Visibility = src == ColorSource.Screen ? Visibility.Collapsed : Visibility.Visible;
        SourceHint.Text = src switch
        {
            ColorSource.AlbumArt => "Colors come from the album art of whatever is playing (Spotify, browser, Tidal, Apple Music…). The preset below is used when nothing is playing.",
            ColorSource.Screen => "Each light copies the part of your screen it sits next to (based on its position in the entertainment area). The music can still drive brightness.",
            ColorSource.Custom => "Your own palette.",
            _ => "A fixed palette. Lights step through it on every kick, or drift slowly.",
        };
    }

    // ---------- Live view ----------

    void RenderUi()
    {
        _tick++;
        var outputs = _engine.Outputs;
        if (_previewDots.Count != outputs.Length)
        {
            PreviewLights.Items.Clear();
            _previewDots.Clear();
            foreach (var o in outputs)
            {
                var dot = new Ellipse { Width = 40, Height = 40, Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x40)), StrokeThickness = 1 };
                var label = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Center, Width = 52, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("Muted") };
                var stack = new StackPanel { Margin = new Thickness(0, 0, 6, 8) };
                stack.Children.Add(dot);
                stack.Children.Add(label);
                PreviewLights.Items.Add(stack);
                _previewDots.Add((dot, label));
            }
        }
        for (int i = 0; i < outputs.Length; i++)
        {
            var c = outputs[i].Color;
            var (dot, label) = _previewDots[i];
            dot.Fill = new SolidColorBrush(ToMedia(c));
            label.Text = outputs[i].Name;
        }

        if (_engine.LastFeatures is { } f)
        {
            for (int i = 0; i < _meters.Count; i++)
                _meters[i].Height = Math.Max(1, f.Levels[i] * 90);
            KickLed.Fill = new SolidColorBrush(Color.FromRgb((byte)(0x33 + f.KickEnvelope * 0xCC), (byte)(0x1A + f.KickEnvelope * 0x13), (byte)(0x1A + f.KickEnvelope * 0x3B)));
            BpmText.Text = f.BeatLocked ? $"{f.Bpm:0} BPM · locked" : $"learning this track… {f.Learned:0%}";
        }

        if (_tick % 15 == 0) RenderPaletteSwatches();
    }

    void RenderPaletteSwatches()
    {
        IEnumerable<Rgb> colors = _settings.ColorSource switch
        {
            ColorSource.AlbumArt when _engine.AlbumArt.Current?.Palette is { Count: > 0 } p => p,
            ColorSource.Custom => Palettes.Parse(_settings.CustomColors.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)),
            ColorSource.Screen => [],
            _ => Palettes.Parse(Palettes.Presets.GetValueOrDefault(_settings.Preset) ?? []),
        };
        FillSwatches(PaletteSwatches, colors, 36);
    }

    static void FillSwatches(Panel panel, IEnumerable<Rgb> colors, double size)
    {
        panel.Children.Clear();
        foreach (var c in colors)
            panel.Children.Add(new Border { Width = size, Height = size * 0.6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 0), Background = new SolidColorBrush(ToMedia(c)), ToolTip = c.ToHex() });
    }

    static Color ToMedia(Rgb c) =>
        Color.FromRgb((byte)(Math.Clamp(c.R, 0, 1) * 255), (byte)(Math.Clamp(c.G, 0, 1) * 255), (byte)(Math.Clamp(c.B, 0, 1) * 255));

    void ShowNowPlaying(NowPlaying? np)
    {
        TrackTitle.Text = np == null || np.Title.Length == 0 ? "Nothing playing" : np.Title;
        TrackArtist.Text = np?.Artist ?? "";
        ArtImage.Source = null;
        if (np?.ArtPng != null)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(np.ArtPng);
            img.EndInit();
            ArtImage.Source = img;
        }
        FillSwatches(ArtPalette, np?.Palette ?? [], 18);
    }

    // ---------- Bridge ----------

    async void Discover_Click(object sender, RoutedEventArgs e)
    {
        PairStatus.Text = "Searching…";
        try
        {
            var bridges = await HueBridgeClient.DiscoverAsync();
            if (bridges.Count == 0) { PairStatus.Text = "No bridge found — enter its IP manually."; return; }
            _settings.BridgeIp = bridges[0].Ip;
            PairStatus.Text = bridges.Count == 1 ? $"Found bridge at {bridges[0].Ip}" : $"Found {bridges.Count} bridges: {string.Join(", ", bridges.Select(b => b.Ip))}";
        }
        catch (Exception ex)
        {
            PairStatus.Text = $"Discovery failed: {ex.Message}";
        }
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var ip = _settings.BridgeIp?.Trim();
        if (string.IsNullOrEmpty(ip)) { PairStatus.Text = "Enter or discover the bridge IP first."; return; }
        try
        {
            using var client = new HueBridgeClient(ip);
            var result = await client.PairAsync(new Progress<string>(s => PairStatus.Text = s), TimeSpan.FromSeconds(30));
            _settings.Username = result.Username;
            _settings.ClientKey = result.ClientKey;
            using var authed = new HueBridgeClient(ip, result.Username);
            _settings.ApplicationId = await authed.GetApplicationIdAsync();
            _settings.Save();
            PairStatus.Text = "Paired ✓";
            await RefreshAreasAsync(quiet: false);
        }
        catch (Exception ex)
        {
            PairStatus.Text = ex.Message;
        }
    }

    async void RefreshAreas_Click(object sender, RoutedEventArgs e) => await RefreshAreasAsync(quiet: false);

    async Task RefreshAreasAsync(bool quiet)
    {
        if (_settings.BridgeIp == null || _settings.Username == null)
        {
            if (!quiet) PairStatus.Text = "Pair with the bridge first.";
            return;
        }
        try
        {
            using var client = new HueBridgeClient(_settings.BridgeIp, _settings.Username);
            var areas = await client.GetEntertainmentAreasAsync();
            AreaBox.ItemsSource = areas;
            AreaBox.SelectedItem = areas.FirstOrDefault(a => a.Id == _settings.AreaId) ?? areas.FirstOrDefault();
            if (areas.Count == 0 && !quiet)
                PairStatus.Text = "No entertainment areas yet — create one in the Hue app.";
        }
        catch (Exception ex)
        {
            if (!quiet) PairStatus.Text = $"Couldn't load areas: {ex.Message}";
            else StatusText.Text = $"Bridge unreachable ({ex.Message}) — preview mode.";
        }
    }

    void AreaBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AreaBox.SelectedItem is not EntertainmentArea area) return;
        _settings.AreaId = area.Id;

        var saved = _settings.Lights.GetValueOrDefault(area.Id) ?? [];
        var lights = area.Channels.Select(ch =>
        {
            var prev = saved.FirstOrDefault(l => l.ChannelId == ch.ChannelId);
            return new LightSetting
            {
                ChannelId = ch.ChannelId,
                Name = ch.Name,
                X = ch.X, Y = ch.Y, Z = ch.Z,
                Enabled = prev?.Enabled ?? true,
                Role = prev?.Role ?? LightRole.Mix,
            };
        }).ToList();
        _settings.Lights[area.Id] = lights;
        _engine.Lights = lights;
        LightList.ItemsSource = lights;
        NoLightsHint.Visibility = Visibility.Collapsed;
    }

    void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox.Tag is not List<(string? Id, string Name)> devices || DeviceBox.SelectedIndex < 0) return;
        var id = devices[DeviceBox.SelectedIndex].Id;
        if (id == _settings.AudioDeviceId || !IsLoaded) { _settings.AudioDeviceId = id; return; }
        _settings.AudioDeviceId = id;
        _engine.RestartAudio();
        StatusText.Text = $"Listening to {_engine.AudioDeviceName}.";
    }

    void ResetAudio_Click(object sender, RoutedEventArgs e) => _settings.ResetAudio();

    // ---------- Sync ----------

    async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        try
        {
            if (_engine.IsStreaming)
            {
                await _engine.StopStreamingAsync();
                StatusText.Text = "Stopped — preview mode.";
            }
            else
            {
                if (AreaBox.SelectedItem is not EntertainmentArea area)
                {
                    StatusText.Text = "Pair a bridge and pick an entertainment area on the Bridge tab first.";
                    return;
                }
                StatusText.Text = "Connecting…";
                await _engine.StartStreamingAsync(area);
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't start: {ex.Message}";
        }
        finally
        {
            SyncButton.IsEnabled = true;
            UpdateSyncButton();
        }
    }

    void UpdateSyncButton() => SyncButton.Content = _engine.IsStreaming ? "Stop light sync" : "Start light sync";
}
