using System.Windows;
using Forms = System.Windows.Forms;

namespace Thump;

/// <summary>Notification-area icon: keeps Thump reachable while the window is closed.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _syncItem;
    Action? _balloonClick;

    public TrayIcon(MainWindow window)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/thump.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Thump",
            Visible = true,
        };

        var open = new Forms.ToolStripMenuItem("Open Thump", null, (_, _) => window.ShowFromTray());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        _syncItem = new Forms.ToolStripMenuItem("Start light sync", null, async (_, _) => await window.ToggleSyncAsync());
        var quit = new Forms.ToolStripMenuItem("Quit Thump", null, (_, _) => window.Quit());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([open, _syncItem, new Forms.ToolStripSeparator(), quit]);
        menu.Opening += (_, _) => _syncItem.Text = window.IsStreaming ? "Stop light sync" : "Start light sync";
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) window.ShowFromTray();
        };
        _icon.BalloonTipClicked += (_, _) => (_balloonClick ?? window.ShowFromTray)();
    }

    public void SetStatus(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Notify(string title, string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
