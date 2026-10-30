using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace Thump.Colors;

public sealed record NowPlaying(string Title, string Artist, string App, byte[]? ArtPng, IReadOnlyList<Rgb> Palette);

/// <summary>
/// Reads the currently playing track from Windows' media session (Spotify, browsers, Tidal, foobar, …)
/// and extracts a colour palette from its album art.
/// </summary>
public sealed class AlbumArtSource : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    string _lastKey = "";

    public NowPlaying? Current { get; private set; }
    public event Action<NowPlaying?>? Changed;

    public void Start() => _ = PollLoop(_cts.Token);

    async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Refresh();
            }
            catch
            {
                // Media sessions come and go; just try again next tick.
            }
            await Task.Delay(1500, ct).ContinueWith(_ => { });
        }
    }

    async Task Refresh()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = _manager.GetCurrentSession();
        if (session == null)
        {
            if (Current != null) { Current = null; _lastKey = ""; Changed?.Invoke(null); }
            return;
        }

        var props = await session.TryGetMediaPropertiesAsync();
        var key = $"{session.SourceAppUserModelId}|{props.Artist}|{props.Title}|{props.Thumbnail != null}";
        if (key == _lastKey) return;

        byte[]? png = null;
        IReadOnlyList<Rgb> palette = [];
        if (props.Thumbnail != null)
        {
            using var ras = await props.Thumbnail.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            (png, palette) = Decode(ms.ToArray());
        }
        else
        {
            // Some players publish the title first and the thumbnail a moment later.
            await Task.Delay(500);
        }

        _lastKey = key;
        Current = new NowPlaying(props.Title ?? "", props.Artist ?? "", session.SourceAppUserModelId ?? "", png, palette);
        Changed?.Invoke(Current);
    }

    static (byte[] Png, IReadOnlyList<Rgb> Palette) Decode(byte[] data)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];

        // Small copy for palette extraction.
        var small = new TransformedBitmap(frame, new ScaleTransform(64.0 / frame.PixelWidth, 64.0 / frame.PixelHeight));
        var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);

        // Re-encode as PNG so the UI thread can display it without owning this thread's objects.
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(frame));
        using var outMs = new MemoryStream();
        enc.Save(outMs);

        return (outMs.ToArray(), Palettes.Extract(pixels));
    }

    public void Dispose() => _cts.Cancel();
}
