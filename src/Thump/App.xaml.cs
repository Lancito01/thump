using System.Windows;
using System.Windows.Threading;

namespace Thump;

public partial class App : Application
{
    // One Thump per user: a second launch just brings the running one to the front.
    const string InstanceName = "Thump.SingleInstance";
    Mutex? _instance;
    EventWaitHandle? _showSignal;
    TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        var screenshot = e.Args.Contains("--screenshot");

        _instance = new Mutex(true, $@"Local\{InstanceName}", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceName}.Show");
        if (!first && !screenshot)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new MainWindow();
        MainWindow = window;
        if (!screenshot)
        {
            _tray = new TrayIcon(window);
            window.Tray = _tray;
            new Thread(() =>
            {
                while (_showSignal.WaitOne())
                    Dispatcher.BeginInvoke(window.ShowFromTray);
            }) { IsBackground = true, Name = "Thump show signal" }.Start();
        }

        if (!e.Args.Contains(Thump.Startup.TrayArg)) window.Show();
        _ = window.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Thump", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
