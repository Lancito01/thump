using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Thump;

public sealed record UpdateInfo(Version Version, string Tag, string? MsiUrl, string? Sha256, string PageUrl);

/// <summary>
/// Opt-in updates from GitHub Releases. Only ever talks to the GitHub API for this repository, only downloads the
/// release's .msi, and verifies it against the SHA-256 digest GitHub publishes for the asset.
/// </summary>
public static class Updater
{
    const string Repo = "Lancito01/thump";
    static readonly string UpdatesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thump", "updates");

    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));

    /// <summary>Installed via the MSI (per-user Programs folder), so updates can be applied in place.</summary>
    public static bool IsInstalled { get; } = (Environment.ProcessPath ?? "").StartsWith(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Thump"),
        StringComparison.OrdinalIgnoreCase);

    public static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Thump", "Thump.exe");

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Thump/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Returns the latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient();
        var rel = await http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        var tag = rel?["tag_name"]?.GetValue<string>();
        if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out var v) || Normalize(v) <= Current) return null;

        var msi = rel!["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>().EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true);
        var digest = msi?["digest"]?.GetValue<string>();
        return new UpdateInfo(
            Normalize(v), tag,
            msi?["browser_download_url"]?.GetValue<string>(),
            digest?.StartsWith("sha256:") == true ? digest[7..] : null,
            rel["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repo}/releases/latest");
    }

    /// <summary>Downloads (or reuses) the installer for <paramref name="update"/> and verifies its hash.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (update.MsiUrl == null) throw new InvalidOperationException("This release has no installer.");
        var uri = new Uri(update.MsiUrl);
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to download an update from {uri.Host}.");

        Directory.CreateDirectory(UpdatesDir);
        var path = Path.Combine(UpdatesDir, $"Thump-{update.Version}.msi");
        if (File.Exists(path) && (update.Sha256 == null || await HashAsync(path, ct) == update.Sha256)) return path;

        var tmp = path + ".part";
        using (var http = NewClient())
        using (var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await resp.Content.CopyToAsync(fs, ct);
        }
        if (update.Sha256 != null && await HashAsync(tmp, ct) != update.Sha256)
        {
            File.Delete(tmp);
            throw new InvalidOperationException("Downloaded update didn't match its published checksum.");
        }
        File.Move(tmp, path, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(UpdatesDir, "*.msi").Where(f => f != path)) TryDelete(old);
        return path;
    }

    /// <summary>
    /// Runs the installer once this process has exited, then starts the updated Thump again.
    /// The caller must shut down right after.
    /// </summary>
    public static void InstallAfterExit(string msiPath, bool startHidden)
    {
        var args = startHidden ? $" {Startup.TrayArg}" : "";
        var script =
            $"timeout /t 2 /nobreak >nul & " +
            $"msiexec /i \"{msiPath}\" /passive /norestart & " +
            $"start \"\" \"{InstalledExe}\"{args}";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {script}")
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
        });
    }

    public static void OpenReleasePage(UpdateInfo update) =>
        Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });

    static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* in use or gone */ }
    }
}
