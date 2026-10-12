using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>
/// The real plugs, straight over the local network; there is no cloud in the path. Each device
/// is spoken to with the protocol its firmware uses: XOR on TCP 9999 for older firmware, KLAP on
/// HTTP 80 for newer. A device whose protocol is not known yet is tried with both.
/// </summary>
internal sealed class KasaLanBackend(Func<KasaCredentials?> credentials) : IPlugBackend
{
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<(IPAddress, KasaEncryption), IKasaTransport> _transports = new();

    public string Name => "LAN";

    /// <summary>Which account a KLAP device accepted, by address, for the "Find plugs" report.</summary>
    public string? SignedInAs(IPAddress address) =>
        _transports.TryGetValue((address, KasaEncryption.Klap), out var t) && t is KlapTransport klap ? klap.SignedInAs : null;

    public async Task<IReadOnlyList<HostInfo>> DiscoverAsync(IReadOnlyList<IPAddress> addresses, CancellationToken ct)
    {
        var hits = await KasaDiscovery.RunAsync(addresses, DiscoveryWindow, ct).ConfigureAwait(false);
        var found = await Task.WhenAll(hits.Select(hit => FromHitAsync(hit, ct))).ConfigureAwait(false);
        var hosts = found.OfType<HostInfo>().ToList();

        // Listed addresses that stayed quiet may just block UDP (some VPNs do): ask them over TCP/HTTP.
        var quiet = addresses.Where(a => hosts.All(h => !h.Address.Equals(a))).ToList();
        var direct = await Task.WhenAll(quiet.Select(async address =>
        {
            try
            {
                return await ReadAsync(new HostInfo { DeviceId = string.Empty, Address = address }, ct).ConfigureAwait(false);
            }
            catch (KasaException ex) when (ex.Failure != KasaFailure.Offline)
            {
                return new HostInfo { DeviceId = $"ip:{address}", Address = address, Problem = ex.Message };
            }
            catch (KasaException)
            {
                return null;
            }
        })).ConfigureAwait(false);
        hosts.AddRange(direct.OfType<HostInfo>());
        return hosts;
    }

    private async Task<HostInfo?> FromHitAsync(DiscoveryHit hit, CancellationToken ct)
    {
        if (hit.SysInfo is not null)
        {
            try
            {
                return SysInfo.Parse(hit.SysInfo, hit.Address, KasaEncryption.Xor);
            }
            catch (KasaException ex)
            {
                return ex.Failure == KasaFailure.Unsupported ? null : Broken(hit, ex.Message);
            }
        }

        if (hit.Family?.StartsWith("SMART.", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Tapo-protocol models (some newer Kasa ones too): a different JSON API, not supported yet.
            return hit.Family.Contains("PLUG", StringComparison.OrdinalIgnoreCase)
                ? Broken(hit, $"{hit.Model} uses the Tapo protocol, not supported yet")
                : null;
        }
        if (hit.EncryptType is not null && !hit.EncryptType.Equals("KLAP", StringComparison.OrdinalIgnoreCase))
        {
            return Broken(hit, $"{hit.Model} uses {hit.EncryptType}, not supported yet");
        }

        try
        {
            return await ReadAsync(new HostInfo { DeviceId = string.Empty, Address = hit.Address, Encryption = KasaEncryption.Klap }, ct)
                .ConfigureAwait(false);
        }
        catch (KasaException ex)
        {
            return ex.Failure == KasaFailure.Unsupported ? null : Broken(hit, ex.Message);
        }
    }

    /// <summary>A device we found but cannot use yet; listed so the settings page can say why.</summary>
    private static HostInfo Broken(DiscoveryHit hit, string problem) => new()
    {
        DeviceId = $"ip:{hit.Address}",
        Address = hit.Address,
        Model = hit.Model ?? string.Empty,
        Name = hit.Model ?? hit.Address.ToString(),
        Problem = problem
    };

    public async Task<HostInfo> ReadAsync(HostInfo host, CancellationToken ct)
    {
        var (reply, encryption) = await SendAsync(host, SysInfo.Query, ct).ConfigureAwait(false);
        return SysInfo.Parse(KasaJson.Result(reply, "system", "get_sysinfo"), host.Address, encryption);
    }

    public async Task SetPowerAsync(HostInfo host, string? childId, bool on, CancellationToken ct)
    {
        var (reply, _) = await SendAsync(host, SysInfo.SetRelay(childId, on), ct).ConfigureAwait(false);
        KasaJson.Result(reply, "system", "set_relay_state");
    }

    public async Task SetLedAsync(HostInfo host, bool on, CancellationToken ct)
    {
        var (reply, _) = await SendAsync(host, SysInfo.SetLed(on), ct).ConfigureAwait(false);
        KasaJson.Result(reply, "system", "set_led_off");
    }

    /// <summary>
    /// Sends with the device's protocol. Unknown: XOR first, then KLAP. Known XOR but port 9999
    /// refused: the firmware was updated to KLAP, so switch.
    /// </summary>
    private async Task<(JsonObject Reply, KasaEncryption Used)> SendAsync(HostInfo host, JsonObject request, CancellationToken ct)
    {
        if (host.Encryption == KasaEncryption.Klap) return (await Transport(host.Address, KasaEncryption.Klap).SendAsync(request, ct).ConfigureAwait(false), KasaEncryption.Klap);

        try
        {
            return (await Transport(host.Address, KasaEncryption.Xor).SendAsync(Clone(request), ct).ConfigureAwait(false), KasaEncryption.Xor);
        }
        catch (KasaException ex) when (ex.Failure == KasaFailure.Offline &&
                                       (host.Encryption == KasaEncryption.Unknown || ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }))
        {
            return (await Transport(host.Address, KasaEncryption.Klap).SendAsync(request, ct).ConfigureAwait(false), KasaEncryption.Klap);
        }
    }

    private static JsonObject Clone(JsonObject request) => (JsonObject)request.DeepClone();

    private IKasaTransport Transport(IPAddress address, KasaEncryption encryption) =>
        _transports.GetOrAdd((address, encryption), key => key.Item2 == KasaEncryption.Klap
            ? new KlapTransport(key.Item1, credentials)
            : new XorTransport(key.Item1));

    /// <summary>Forgets KLAP sessions, e.g. after the account in the settings changed.</summary>
    public void ResetSessions()
    {
        foreach (var key in _transports.Keys.Where(k => k.Item2 == KasaEncryption.Klap).ToList())
        {
            if (_transports.TryRemove(key, out var transport)) transport.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var transport in _transports.Values) transport.Dispose();
        _transports.Clear();
    }
}
