using System.Collections.Concurrent;
using System.Net;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>
/// Pretend plugs for trying the plugin without any hardware: a KP105 and a three-socket KP303, in
/// the same shapes the real ones report, so every key, menu and setting works as it will on them.
/// Controls change the pretend state after a short delay.
/// </summary>
internal sealed class DemoBackend : IPlugBackend
{
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(120);

    private const string PlugId = "DEMO000000000000000000000000000000000105";
    private const string StripId = "DEMO000000000000000000000000000000000303";

    private readonly ConcurrentDictionary<string, HostInfo> _hosts = new();

    public DemoBackend()
    {
        _hosts[PlugId] = new HostInfo
        {
            DeviceId = PlugId, Address = IPAddress.Parse("192.0.2.105"), Encryption = KasaEncryption.Xor,
            Model = "KP105(UK)", Name = "Desk lamp", Mac = "DE:MO:00:00:01:05", LedOn = true,
            Outlets = [new OutletInfo(PlugId, null, "Desk lamp", true)]
        };
        _hosts[StripId] = new HostInfo
        {
            DeviceId = StripId, Address = IPAddress.Parse("192.0.2.33"), Encryption = KasaEncryption.Klap,
            Model = "KP303(UK)", Name = "Bench strip", Mac = "DE:MO:00:00:03:03", LedOn = true,
            Outlets =
            [
                new OutletInfo(StripId + "00", StripId + "00", "Monitors", true),
                new OutletInfo(StripId + "01", StripId + "01", "Soldering iron", false),
                new OutletInfo(StripId + "02", StripId + "02", "Fan", false)
            ]
        };
    }

    public string Name => "Demo plugs";

    public async Task<IReadOnlyList<HostInfo>> DiscoverAsync(IReadOnlyList<IPAddress> addresses, CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return _hosts.Values.OrderBy(h => h.Name).ToList();
    }

    public async Task<HostInfo> ReadAsync(HostInfo host, CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return _hosts.TryGetValue(host.DeviceId, out var known) ? known : throw new KasaException("No answer", KasaFailure.Offline);
    }

    public async Task SetPowerAsync(HostInfo host, string? childId, bool on, CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        Change(host, h => h with
        {
            Outlets = h.Outlets.Select(o => childId is null || o.ChildId == childId ? o with { On = on } : o).ToList()
        });
    }

    public async Task SetLedAsync(HostInfo host, bool on, CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        Change(host, h => h with { LedOn = on });
    }

    private void Change(HostInfo host, Func<HostInfo, HostInfo> change)
    {
        if (!_hosts.ContainsKey(host.DeviceId)) throw new KasaException("No answer", KasaFailure.Offline);
        _hosts.AddOrUpdate(host.DeviceId, _ => host, (_, old) => change(old));
    }

    public void Dispose()
    {
    }
}
