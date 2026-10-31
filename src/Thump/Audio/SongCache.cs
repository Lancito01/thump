using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Thump.Audio;

/// <summary>What one <see cref="HitDetector"/> learned about a song.</summary>
public sealed class HitModel
{
    public double[] Hit { get; set; } = [];
    public double[] Other { get; set; } = [];
    public int OtherCount { get; set; }
    public double Typical { get; set; }
    public int Learned { get; set; }
}

/// <summary>Everything worth remembering about a song so the next play starts out knowing it.</summary>
public sealed class SongModel
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string Key { get; set; } = "";
    /// <summary>For people browsing the folder; not used to match.</summary>
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    /// <summary>Seconds per beat, 0 if it never locked.</summary>
    public double BeatPeriod { get; set; }
    public HitModel Kick { get; set; } = new();
    public HitModel Clap { get; set; } = new();
}

/// <summary>
/// Opt-in, size-capped store of learned songs: one small JSON file per song (about 2 KB). When the cap is reached,
/// the songs that haven't been played for the longest are forgotten first.
/// </summary>
public sealed class SongCache(string dir)
{
    public static string DefaultDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thump", "songs");

    readonly object _io = new();

    /// <summary>The same song from any player matches; null when there's nothing to go on.</summary>
    public static string? KeyFor(string artist, string title)
    {
        title = title.Trim();
        return title.Length == 0 ? null : $"{artist.Trim()}\n{title}".ToLowerInvariant();
    }

    public SongModel? TryLoad(string key)
    {
        lock (_io)
        {
            try
            {
                var path = PathFor(key);
                if (!File.Exists(path)) return null;
                var m = JsonSerializer.Deserialize<SongModel>(File.ReadAllText(path));
                if (m == null || m.Key != key || m.Version != SongModel.CurrentVersion) return null;
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // "last played", for evicting the oldest first
                return m;
            }
            catch
            {
                return null; // unreadable entry: learn the song again
            }
        }
    }

    public void Save(SongModel m, long maxBytes)
    {
        lock (_io)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = PathFor(m.Key);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(m));
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // Disk full or folder locked: not worth interrupting the music for.
            }
            TrimUnlocked(maxBytes);
        }
    }

    /// <summary>Deletes the least recently played songs until the folder fits in <paramref name="maxBytes"/>.</summary>
    public void Trim(long maxBytes)
    {
        lock (_io) TrimUnlocked(maxBytes);
    }

    void TrimUnlocked(long maxBytes)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var files = new DirectoryInfo(dir).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= maxBytes) break;
                total -= f.Length;
                f.Delete();
            }
        }
        catch
        {
            // Try again next time.
        }
    }

    public (int Songs, long Bytes) Usage()
    {
        lock (_io)
        {
            try
            {
                if (!Directory.Exists(dir)) return (0, 0);
                var files = new DirectoryInfo(dir).GetFiles("*.json");
                return (files.Length, files.Sum(f => f.Length));
            }
            catch
            {
                return (0, 0);
            }
        }
    }

    public void Clear() => Trim(0);

    string PathFor(string key) =>
        Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant() + ".json");
}
