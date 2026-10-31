using Microsoft.Win32;

namespace Thump;

/// <summary>"Start with Windows" via the per-user Run key (no admin needed).</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Thump";

    /// <summary>Command-line flag: start hidden in the tray.</summary>
    public const string TrayArg = "--tray";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(Name, $"\"{Environment.ProcessPath}\" {TrayArg}"); // rewritten every launch, so it follows updates/moves
        else if (key.GetValue(Name) != null)
            key.DeleteValue(Name);
    }
}
