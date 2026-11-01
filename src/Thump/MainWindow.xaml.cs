using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    readonly List<(Ellipse Dot, DropShadowEffect Glow, TextBlock Label)> _previewDots = [];
    static readonly Color Pink = Color.FromRgb(0xFF, 0x19, 0x78);
    static readonly Color TrackColor = Color.FromRgb(0x23, 0x23, 0x31);
    int _tick;
    bool _skipSave;
    bool _quitting;
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    UpdateInfo? _pendingUpdate;
    string? _pendingMsi;

    /// <summary>Set by <see cref="App"/>; null when running headless (screenshots).</summary>
    public TrayIcon? Tray { get; set; }
    public bool IsStreaming => _engine.IsStreaming;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _settings;
        _engine = new SyncEngine(_settings);
        _engine.Status += msg => Dispatcher.BeginInvoke(() => { StatusText.Text = msg; UpdateSyncButton(); });
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        IsVisibleChanged += (_, _) =>
        {
            // Nothing to draw while hidden in the tray; the engine keeps running on its own thread.
            if (IsVisible) _uiTimer.Start(); else _uiTimer.Stop();
        };
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

        var meterBrush = new LinearGradientBrush(Pink, Color.FromRgb(0xFF, 0xB0, 0x20), new Point(0, 1), new Point(0, 0));
        meterBrush.Freeze();
        for (int i = 0; i < AudioAnalyzer.Bands.Length; i++)
        {
            var bar = new Rectangle { Fill = meterBrush, VerticalAlignment = VerticalAlignment.Bottom, RadiusX = 4, RadiusY = 4 };
            var cell = new Border { Background = new SolidColorBrush(TrackColor), CornerRadius = new CornerRadius(4), Margin = new Thickness(3, 0, 3, 0), Child = bar };
            BandMeters.Children.Add(cell);
            _meters.Add(bar);
            BandLabels.Children.Add(new TextBlock { Text = BandShortNames[i], FontSize = 10, TextAlignment = TextAlignment.Center, Foreground = (Brush)FindResource("Muted") });
        }

        SourceInitialized += (_, _) => UseDarkTitleBar();
        _settings.PropertyChanged += OnSettingChanged;
        UpdateColorPanels();
        _uiTimer.Tick += (_, _) => RenderUi();

        VersionText.Text = $"Thump {Updater.Current.ToString(3)}" + (Updater.IsInstalled ? "" : " (portable)");
        UpdateSongCacheStatus();
        Loaded += async (_, _) => await MaybeTakeScreenshotAsync();
        Closing += OnClosing;
    }

    /// <summary>Starts audio, the engine and the bridge connection. Runs whether or not the window is shown.</summary>
    public async Task InitializeAsync()
    {
        _engine.Start();
        StatusText.Text = $"Preview mode — listening to {_engine.AudioDeviceName}.";
        try { Startup.Apply(_settings.StartWithWindows); } catch { /* registry locked down: not fatal */ }
        if (_settings.Username != null) await RefreshAreasAsync(quiet: true);
        if (_settings.AutoStartSync && AreaBox.SelectedItem is EntertainmentArea && !_engine.IsStreaming)
            await ToggleSyncAsync();
        if (_settings.CheckForUpdates)
        {
            _updateTimer.Start();
            await Task.Delay(TimeSpan.FromSeconds(8));
            await CheckForUpdatesAsync(manual: false);
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Really exits (tray menu → Quit), as opposed to closing the window.</summary>
    public void Quit()
    {
        _quitting = true;
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_quitting && _settings.RunInBackground && Tray != null)
        {
            e.Cancel = true;
            Hide();
            try { _settings.Save(); } catch { /* keep running regardless */ }
            if (!_settings.TrayHintShown)
            {
                _settings.TrayHintShown = true;
                Tray.Notify("Thump is still running", _engine.IsStreaming
                    ? "Your lights keep syncing. Right-click the tray icon to stop or quit."
                    : "It's in the notification area. Right-click the icon to quit.");
            }
            return;
        }

        _uiTimer.Stop();
        _updateTimer.Stop();
        _engine.Dispose();
        if (!_skipSave)
            try { _settings.Save(); } catch { /* nothing useful to do on exit */ }
        Application.Current.Shutdown();
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ColorSource):
                UpdateColorPanels();
                break;
            case nameof(AppSettings.StartWithWindows):
                try { Startup.Apply(_settings.StartWithWindows); }
                catch (Exception ex) { UpdateStatus.Text = $"Couldn't change startup: {ex.Message}"; }
                break;
            case nameof(AppSettings.RememberSongs) or nameof(AppSettings.SongCacheMb):
                UpdateSongCacheStatus();
                break;
            case nameof(AppSettings.CheckForUpdates):
                if (_settings.CheckForUpdates) { _updateTimer.Start(); _ = CheckForUpdatesAsync(manual: false); }
                else _updateTimer.Stop();
                break;
        }
    }

    void UpdateSongCacheStatus()
    {
        // Trim right away when the cap is lowered, so the number shown is what is really on disk.
        if (_settings.RememberSongs) _engine.Songs.Trim((long)(_settings.SongCacheMb * 1024 * 1024));
        var (songs, bytes) = _engine.Songs.Usage();
        SongCacheStatus.Text = songs == 0 ? "No songs remembered." : $"{songs:N0} song{(songs == 1 ? "" : "s")}, {bytes / 1024.0:N0} KB";
    }

    void ForgetSongs_Click(object sender, RoutedEventArgs e)
    {
        _engine.Songs.Clear();
        UpdateSongCacheStatus();
    }

    void UpdateColorPanels()
    {
        var src = _settings.ColorSource;
        PresetPanel.Visibility = src is ColorSource.Preset or ColorSource.AlbumArt ? Visibility.Visible : Visibility.Collapsed;
        CustomPanel.Visibility = src == ColorSource.Custom ? Visibility.Visible : Visibility.Collapsed;
        ScreenPanel.Visibility = src == ColorSource.Screen ? Visibility.Visible : Visibility.Collapsed;
        MotionPanel.Visibility = src == ColorSource.Screen ? Visibility.Collapsed : Visibility.Visible;
        PresetLabel.Text = src == ColorSource.AlbumArt ? "Fallback preset (when nothing is playing)" : "Preset";
        SourceHint.Text = src switch
        {
            ColorSource.AlbumArt => "Colors come from the cover of whatever is playing (Spotify, browsers, Tidal, Apple Music…), and change with every track.",
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
                var glow = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 0, Opacity = 0 };
                var dot = new Ellipse { Width = 38, Height = 38, Margin = new Thickness(7), Effect = glow, Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), StrokeThickness = 1 };
                var label = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Center, Width = 56, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("Muted") };
                var stack = new StackPanel { Margin = new Thickness(0, 0, 4, 2) };
                stack.Children.Add(dot);
                stack.Children.Add(label);
                PreviewLights.Items.Add(stack);
                _previewDots.Add((dot, glow, label));
            }
        }
        for (int i = 0; i < outputs.Length; i++)
        {
            var c = outputs[i].Color;
            var (dot, glow, label) = _previewDots[i];
            dot.Fill = new SolidColorBrush(ToMedia(c));
            // Glow in the light's own colour, as strong as the light is bright.
            glow.Color = ToMedia(c.Normalized());
            glow.Opacity = Math.Clamp(c.Max * 1.2, 0, 1);
            label.Text = outputs[i].Name;
        }

        if (_engine.LastFeatures is { } f)
        {
            for (int i = 0; i < _meters.Count; i++)
                _meters[i].Height = Math.Max(4, f.Levels[i] * 84);
            Flash(KickPill, KickText, f.KickEnvelope);
            Flash(ClapPill, ClapText, f.ClapEnvelope);
            Flash(HatPill, HatText, f.HatEnvelope);
            HatPill.Visibility = f.ModelActive ? Visibility.Visible : Visibility.Collapsed;
            BpmText.Text = f.BeatLocked ? $"{f.Bpm:0} BPM · tempo confidence {f.TempoConfidence:0%}"
                : f.Bpm > 0 ? $"~{f.Bpm:0} BPM · checking the beat… {f.TempoConfidence:0%}"
                : "listening for the beat…";
            ModelText.Text = f.ModelActive
                ? "Drums: Kickstep (kick, clap/snare, hats)"
                : $"Kick: {Learned(f.KickLearned, f.KickConfidence)}  ·  Clap/snare: {Learned(f.ClapLearned, f.ClapConfidence)}"
                             + (f.Remembered ? "  ·  remembered from last time" : "");
        }

        if (_tick % 15 == 0) RenderPaletteSwatches();
    }

    void Flash(Border pill, TextBlock text, double env)
    {
        pill.Background = new SolidColorBrush(Lerp(TrackColor, Pink, env));
        text.Foreground = new SolidColorBrush(Lerp(Color.FromRgb(0x8C, 0x8C, 0xA1), System.Windows.Media.Colors.White, env));
    }

    /// <summary>How far the model has got, and how sure it was about the last hit it flashed for.</summary>
    static string Learned(double learned, double lastConfidence) =>
        learned <= 0 ? "not heard yet" : learned < 1 ? $"learning {learned:0%}" : $"last hit {lastConfidence:0%} sure";

    void RenderPaletteSwatches()
    {
        // What the lights actually get: one distinct colour per light, variations included.
        FillSwatches(PaletteSwatches, _settings.ColorSource == ColorSource.Screen ? [] : _engine.Palette, 40);
    }

    static void FillSwatches(Panel panel, IEnumerable<Rgb> colors, double size)
    {
        var list = colors.ToList();
        if (panel.Children.Count == list.Count &&
            panel.Children.OfType<Border>().Select(b => (string)b.ToolTip).SequenceEqual(list.Select(c => c.ToHex())))
            return; // unchanged; avoid rebuilding every refresh
        panel.Children.Clear();
        foreach (var c in list)
            panel.Children.Add(new Border { Width = size, Height = size * 0.55, CornerRadius = new CornerRadius(size * 0.275), Margin = new Thickness(0, 0, 5, 0), Background = new SolidColorBrush(ToMedia(c)), ToolTip = c.ToHex() });
    }

    static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    static Color ToMedia(Rgb c) =>
        Color.FromRgb((byte)(Math.Clamp(c.R, 0, 1) * 255), (byte)(Math.Clamp(c.G, 0, 1) * 255), (byte)(Math.Clamp(c.B, 0, 1) * 255));

    void ShowNowPlaying(NowPlaying? np)
    {
        TrackTitle.Text = np == null || np.Title.Length == 0 ? "Nothing playing" : np.Title;
        TrackArtist.Text = np?.Artist ?? "";
        ArtBorder.Background = (Brush)FindResource("Track");
        ArtGlow.Opacity = 0;
        if (np?.ArtPng != null)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(np.ArtPng);
            img.EndInit();
            ArtBorder.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            if (np.Palette.Count > 0)
            {
                ArtGlow.Color = ToMedia(np.Palette[0]);
                ArtGlow.Opacity = 0.85;
            }
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
            // Also on startup: the pairing is remembered, so say so (only Pair used to set this).
            PairStatus.Text = areas.Count == 0 && !quiet
                ? "No entertainment areas yet — create one in the Hue app."
                : $"Paired with {_settings.BridgeIp} ✓";
        }
        catch (Exception ex)
        {
            if (!quiet) PairStatus.Text = $"Couldn't load areas: {ex.Message}";
            else
            {
                PairStatus.Text = $"Paired with {_settings.BridgeIp}, but it isn't answering right now.";
                StatusText.Text = $"Bridge unreachable ({ex.Message}) — preview mode.";
            }
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

    void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ---------- Sync ----------

    async void SyncButton_Click(object sender, RoutedEventArgs e) => await ToggleSyncAsync();

    public async Task ToggleSyncAsync()
    {
        if (!SyncButton.IsEnabled) return; // already connecting
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

    void UpdateSyncButton()
    {
        SyncButton.Content = _engine.IsStreaming ? "Stop light sync" : "Start light sync";
        SyncButton.Style = (Style)FindResource(_engine.IsStreaming ? "StopButton" : "PrimaryButton");
        Tray?.SetStatus(_engine.IsStreaming ? "Thump — syncing your lights" : "Thump");
    }

    // ---------- Updates ----------

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    async Task CheckForUpdatesAsync(bool manual)
    {
        UpdateStatus.Text = "Checking for updates…";
        try
        {
            var update = await Updater.CheckAsync();
            if (update == null)
            {
                UpdateStatus.Text = $"You're on the latest version ({Updater.Current.ToString(3)}).";
                return;
            }
            _pendingUpdate = update;
            if (!Updater.IsInstalled || update.MsiUrl == null)
            {
                // Portable copy: we can't update in place, just point at the release.
                ShowUpdateBanner($"Thump {update.Tag} is available", "Download");
                UpdateStatus.Text = $"{update.Tag} is available on GitHub.";
                return;
            }

            UpdateStatus.Text = $"Downloading {update.Tag}…";
            _pendingMsi = await Updater.DownloadAsync(update);
            UpdateStatus.Text = $"{update.Tag} is ready to install.";

            if (_settings.AutoInstallUpdates && !_engine.IsStreaming)
            {
                InstallPendingUpdate();
                return;
            }
            ShowUpdateBanner($"Thump {update.Tag} is ready", "Install & restart");
            if (!IsVisible)
                Tray?.Notify($"Thump {update.Tag} is ready", "Click to install and restart Thump.", InstallPendingUpdate);
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = manual ? $"Update check failed: {ex.Message}" : "";
        }
    }

    void ShowUpdateBanner(string text, string action)
    {
        UpdateBannerText.Text = text;
        UpdateBannerButton.Content = action;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    void UpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingMsi != null) InstallPendingUpdate();
        else if (_pendingUpdate != null) Updater.OpenReleasePage(_pendingUpdate);
    }

    void InstallPendingUpdate()
    {
        if (_pendingMsi == null) return;
        Updater.InstallAfterExit(_pendingMsi, startHidden: !IsVisible);
        Quit();
    }

    /// <summary>
    /// For docs: <c>Thump.exe --screenshot out.png [--tab Colors]</c> renders the window to a PNG after a few
    /// seconds of live audio, then exits without saving settings.
    /// </summary>
    async Task MaybeTakeScreenshotAsync()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--screenshot");
        if (i < 0 || i + 1 >= args.Length) return;
        int t = Array.IndexOf(args, "--tab");
        if (t > 0 && t + 1 < args.Length)
            Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(x => (string)x.Header == args[t + 1]) ?? Tabs.SelectedItem;

        await Task.Delay(6000);
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(args[i + 1])) enc.Save(fs);
        _skipSave = true;
        Quit();
    }

    // Windows 10 draws a white title bar unless the window opts into dark mode.
    void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));      // same, Windows 10 before 20H1
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
