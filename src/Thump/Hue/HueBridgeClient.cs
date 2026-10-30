using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thump.Hue;

public record DiscoveredBridge(string Id, string Ip);

public record EntertainmentChannel(int ChannelId, double X, double Y, double Z, string Name);

public record EntertainmentArea(string Id, string Name, string Status, IReadOnlyList<EntertainmentChannel> Channels)
{
    public override string ToString() => $"{Name} ({Channels.Count} channels)";
}

public record PairResult(string Username, string ClientKey);

/// <summary>
/// Talks to the Hue bridge over its REST APIs (v1 for pairing, CLIP v2 for everything else).
/// </summary>
public sealed class HueBridgeClient : IDisposable
{
    readonly HttpClient _http;
    readonly string _ip;
    readonly string? _appKey;

    public HueBridgeClient(string ip, string? appKey = null)
    {
        _ip = ip;
        _appKey = appKey;
        var handler = new HttpClientHandler
        {
            // The bridge serves a self-signed certificate on the LAN; only ever used against _ip.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        if (appKey != null) _http.DefaultRequestHeaders.Add("hue-application-key", appKey);
    }

    public static async Task<List<DiscoveredBridge>> DiscoverAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var list = await http.GetFromJsonAsync<JsonArray>("https://discovery.meethue.com/", ct) ?? [];
        return list
            .Select(n => new DiscoveredBridge(n?["id"]?.GetValue<string>() ?? "", n?["internalipaddress"]?.GetValue<string>() ?? ""))
            .Where(b => b.Ip.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Polls the bridge until the user presses the link button (or the timeout elapses).
    /// </summary>
    public async Task<PairResult> PairAsync(IProgress<string>? progress, TimeSpan timeout, CancellationToken ct = default)
    {
        var devicetype = $"thump#{Environment.MachineName}".ToLowerInvariant();
        var body = new { devicetype = devicetype[..Math.Min(40, devicetype.Length)], generateclientkey = true };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await _http.PostAsJsonAsync($"https://{_ip}/api", body, ct);
            var arr = await resp.Content.ReadFromJsonAsync<JsonArray>(ct);
            var first = arr?.FirstOrDefault();
            if (first?["success"] is JsonNode ok)
                return new PairResult(ok["username"]!.GetValue<string>(), ok["clientkey"]!.GetValue<string>());

            var err = first?["error"];
            if (err?["type"]?.GetValue<int>() != 101) // 101 = link button not pressed
                throw new InvalidOperationException(err?["description"]?.GetValue<string>() ?? "Unexpected bridge response");

            var left = (int)(deadline - DateTime.UtcNow).TotalSeconds;
            progress?.Report($"Press the link button on the bridge… ({left}s)");
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException("Link button was not pressed in time.");
    }

    /// <summary>The PSK identity for Entertainment streaming.</summary>
    public async Task<string> GetApplicationIdAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"https://{_ip}/auth/v1", ct);
        if (resp.Headers.TryGetValues("hue-application-id", out var vals))
            return vals.First();
        throw new InvalidOperationException("Bridge did not return an application id (is the firmware up to date?)");
    }

    async Task<JsonArray> GetResourceAsync(string type, CancellationToken ct)
    {
        var node = await _http.GetFromJsonAsync<JsonObject>($"https://{_ip}/clip/v2/resource/{type}", ct);
        var errors = node?["errors"]?.AsArray();
        if (errors is { Count: > 0 })
            throw new InvalidOperationException(errors[0]?["description"]?.GetValue<string>());
        return node?["data"]?.AsArray() ?? [];
    }

    public async Task<List<EntertainmentArea>> GetEntertainmentAreasAsync(CancellationToken ct = default)
    {
        var configs = await GetResourceAsync("entertainment_configuration", ct);
        var services = await GetResourceAsync("entertainment", ct);
        var devices = await GetResourceAsync("device", ct);

        var deviceNames = devices.ToDictionary(d => d!["id"]!.GetValue<string>(), d => d!["metadata"]?["name"]?.GetValue<string>() ?? "Light");
        var serviceToName = services.ToDictionary(
            s => s!["id"]!.GetValue<string>(),
            s => deviceNames.GetValueOrDefault(s!["owner"]?["rid"]?.GetValue<string>() ?? "", "Light"));

        var result = new List<EntertainmentArea>();
        foreach (var c in configs)
        {
            var channels = new List<EntertainmentChannel>();
            foreach (var ch in c!["channels"]?.AsArray() ?? [])
            {
                var pos = ch!["position"];
                var rid = ch["members"]?.AsArray().FirstOrDefault()?["service"]?["rid"]?.GetValue<string>() ?? "";
                var members = ch["members"]?.AsArray().Count ?? 0;
                var name = serviceToName.GetValueOrDefault(rid, "Light");
                var segIndex = ch["members"]?.AsArray().FirstOrDefault()?["index"]?.GetValue<int>() ?? 0;
                if (segIndex > 0 || IsMultiSegment(c, rid)) name += $" #{segIndex + 1}";
                channels.Add(new EntertainmentChannel(
                    ch["channel_id"]!.GetValue<int>(),
                    pos?["x"]?.GetValue<double>() ?? 0,
                    pos?["y"]?.GetValue<double>() ?? 0,
                    pos?["z"]?.GetValue<double>() ?? 0,
                    name));
            }
            result.Add(new EntertainmentArea(
                c["id"]!.GetValue<string>(),
                c["metadata"]?["name"]?.GetValue<string>() ?? "Entertainment area",
                c["status"]?.GetValue<string>() ?? "inactive",
                channels));
        }
        return result;
    }

    static bool IsMultiSegment(JsonNode config, string serviceRid) =>
        (config["channels"]?.AsArray() ?? [])
            .Count(ch => ch?["members"]?.AsArray().Any(m => m?["service"]?["rid"]?.GetValue<string>() == serviceRid) == true) > 1;

    public Task StartStreamingAsync(string areaId, CancellationToken ct = default) => SetActionAsync(areaId, "start", ct);
    public Task StopStreamingAsync(string areaId, CancellationToken ct = default) => SetActionAsync(areaId, "stop", ct);

    async Task SetActionAsync(string areaId, string action, CancellationToken ct)
    {
        var resp = await _http.PutAsJsonAsync($"https://{_ip}/clip/v2/resource/entertainment_configuration/{areaId}", new { action }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Bridge refused '{action}': {text}");
        }
    }

    public void Dispose() => _http.Dispose();
}
