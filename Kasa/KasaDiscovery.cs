using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>A device that answered discovery.</summary>
/// <param name="SysInfo">The legacy (port 9999) answer carries the full sysinfo; the newer one does not.</param>
/// <param name="Family">From the newer answer, e.g. "IOT.SMARTPLUGSWITCH" or "SMART.KASAPLUG".</param>
/// <param name="EncryptType">From the newer answer: "KLAP" or "AES".</param>
internal sealed record DiscoveryHit(IPAddress Address, JsonObject? SysInfo, string? Model, string? Family, string? EncryptType);

/// <summary>
/// UDP discovery, as the Kasa app does it: the XOR-encrypted get_sysinfo to port 9999 (older
/// firmware) and TP-Link's discovery probe to port 20002 (KLAP firmware), sent to the broadcast
/// address of every network this computer is on and straight to any listed address. Broadcasts do
/// not cross VPNs or routers; the direct question does.
/// </summary>
internal static class KasaDiscovery
{
    public const int LegacyPort = 9999;
    public const int ProbePort = 20002;

    /// <summary>The original fixed TDP v2 probe: version 2, op 1, no payload, with its CRC.</summary>
    internal static readonly byte[] Probe = Convert.FromHexString("020000010000000000000000463cb5d3");

    private static readonly byte[] LegacyQuery = XorTransport.Encrypt(SysInfo.Query.ToJsonString());

    /// <summary>
    /// The probe current python-kasa and the Kasa app send instead: the same header carrying an
    /// RSA public key (which devices may use to encrypt part of their answer; we only read the
    /// plain part), a random serial and a CRC32 over the whole message. Sent alongside the fixed one.
    /// </summary>
    internal static readonly Lazy<byte[]> KeyedProbe = new(BuildKeyedProbe);

    internal static byte[] BuildKeyedProbe()
    {
        using var rsa = RSA.Create(2048);
        var payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["params"] = new JsonObject { ["rsa_key"] = rsa.ExportSubjectPublicKeyInfoPem() + "\n" }
        }.ToJsonString());

        var message = new byte[16 + payload.Length];
        message[0] = 2;                                                        // TDP version
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 1);           // op: probe
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)payload.Length);
        message[6] = 17;                                                       // flags
        RandomNumberGenerator.Fill(message.AsSpan(8, 4));                      // serial
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(12), 0x5A6B7C8D); // CRC placeholder
        payload.CopyTo(message, 16);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(12), Crc32(message));
        return message;
    }

    /// <summary>The zlib CRC-32 (python's binascii.crc32).</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public static async Task<IReadOnlyList<DiscoveryHit>> RunAsync(IReadOnlyList<IPAddress> addresses, TimeSpan wait, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var targets = BroadcastAddresses().Concat(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)).Distinct().ToList();
        var hits = new Dictionary<IPAddress, DiscoveryHit>();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(wait);

        // UDP is lossy and plugs are slow to wake: ask three times across the window.
        var sender = Task.Run(async () =>
        {
            for (var round = 0; round < 3 && !deadline.IsCancellationRequested; round++)
            {
                foreach (var target in targets)
                {
                    await Send(udp, LegacyQuery, target, LegacyPort).ConfigureAwait(false);
                    await Send(udp, Probe, target, ProbePort).ConfigureAwait(false);
                    await Send(udp, KeyedProbe.Value, target, ProbePort).ConfigureAwait(false);
                }
                try
                {
                    await Task.Delay(wait / 3, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (Parse(packet.Buffer, packet.RemoteEndPoint) is not { } hit) continue;
                // A device that answers both keeps the newer answer: it says which protocol to use.
                if (!hits.TryGetValue(hit.Address, out var seen) || seen.SysInfo is not null && hit.EncryptType is not null)
                    hits[hit.Address] = hit;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The window closed.
        }
        await sender.ConfigureAwait(false);
        return hits.Values.ToList();
    }

    internal static DiscoveryHit? Parse(byte[] data, IPEndPoint from)
    {
        try
        {
            if (from.Port == LegacyPort)
            {
                var json = KasaJson.ParseObject(XorTransport.Decrypt(data));
                var sysinfo = json["system"]?["get_sysinfo"] as JsonObject;
                return sysinfo is null ? null : new DiscoveryHit(from.Address, sysinfo, sysinfo["model"]?.ToString(), null, null);
            }
            if (from.Port == ProbePort && data.Length > 16)
            {
                var json = KasaJson.ParseObject(Encoding.UTF8.GetString(data, 16, data.Length - 16));
                if (json["result"] is not JsonObject result) return null;
                var address = IPAddress.TryParse(result["ip"]?.ToString(), out var ip) ? ip : from.Address;
                return new DiscoveryHit(address, null, result["device_model"]?.ToString(), result["device_type"]?.ToString(),
                    result["mgt_encrypt_schm"]?["encrypt_type"]?.ToString());
            }
        }
        catch (KasaException)
        {
            // Something else on the port; ignore it.
        }
        return null;
    }

    private static async Task Send(UdpClient udp, byte[] data, IPAddress target, int port)
    {
        try
        {
            await udp.SendAsync(data, new IPEndPoint(target, port)).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // An interface that went away, or no route to a listed address; the others still count.
        }
    }

    /// <summary>255.255.255.255 plus each IPv4 network's own broadcast (macOS sends the former out of one interface only).</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in SafeInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 255)) continue; // point-to-point (VPN) links have no broadcast
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }

    private static NetworkInterface[] SafeInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
