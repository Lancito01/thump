using System.IO;
using Thump.Audio;

namespace Thump.Tests;

public sealed class SongCacheTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "thump-cache-" + Guid.NewGuid().ToString("N"));

    static SongModel Model(string key) => new()
    {
        Key = key,
        BeatPeriod = 0.47,
        Kick = new HitModel { Hit = new double[11], Other = new double[11], Typical = 12, Learned = 30 },
        Clap = new HitModel { Hit = new double[8], Other = new double[8], Typical = 9, Learned = 20 },
    };

    [Fact]
    public void Round_trips_a_song()
    {
        var cache = new SongCache(_dir);
        var key = SongCache.KeyFor("ero808", "IRONIC")!;
        cache.Save(Model(key), 1 << 20);
        var back = cache.TryLoad(key);
        Assert.NotNull(back);
        Assert.Equal(0.47, back.BeatPeriod);
        Assert.Equal(30, back.Kick.Learned);
        Assert.Null(cache.TryLoad(SongCache.KeyFor("someone", "else")!));
    }

    [Fact]
    public void Same_song_from_another_player_matches() =>
        Assert.Equal(SongCache.KeyFor("ero808", "IRONIC"), SongCache.KeyFor(" Ero808 ", "ironic "));

    [Fact]
    public void Untitled_media_is_not_cached() => Assert.Null(SongCache.KeyFor("Someone", "  "));

    [Fact]
    public void Over_the_cap_forgets_the_least_recently_played()
    {
        var cache = new SongCache(_dir);
        cache.Save(Model("a"), 1 << 20);
        long one = cache.Usage().Bytes;
        cache.Save(Model("b"), 1 << 20);
        cache.Save(Model("c"), 1 << 20);
        // Play "a" again so "b" is now the stalest.
        var files = Directory.GetFiles(_dir);
        foreach (var f in files) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-1));
        Assert.NotNull(cache.TryLoad("a"));

        cache.Save(Model("d"), one * 3 + one / 2); // room for three
        Assert.Equal(3, cache.Usage().Songs);
        Assert.NotNull(cache.TryLoad("a"));
        Assert.NotNull(cache.TryLoad("d"));
        Assert.Equal(1, new[] { "b", "c" }.Count(k => cache.TryLoad(k) == null));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var cache = new SongCache(_dir);
        cache.Save(Model("a"), 1 << 20);
        cache.Clear();
        Assert.Equal((0, 0L), cache.Usage());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
