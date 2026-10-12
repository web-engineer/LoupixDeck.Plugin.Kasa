using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Kasa.Kasa;
using LoupixDeck.Plugin.Kasa.Simulation;
using Xunit;

namespace LoupixDeck.Plugin.Kasa.Tests;

/// <summary>
/// Wire compatibility. The expected bytes come from python-kasa 0.11 (the reference open-source
/// Kasa client), generated with fixed seeds and the account user@example.com / secret, so these
/// tests pin the plugin to what real plugs accept rather than to itself.
/// </summary>
public class PythonKasaVectorTests
{
    private const string SysInfoQuery = """{"system":{"get_sysinfo":{}}}""";
    private static readonly byte[] LocalSeed = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] RemoteSeed = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray();
    private static readonly KasaCredentials Account = new("user@example.com", "secret");

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void Xor_frame_matches()
    {
        Assert.Equal("0000001dd0f281f88bff9af7d5ef94b6d1b4c09fec95e68fe187e8caf08bf68bf6", Hex(XorTransport.Frame(SysInfoQuery)));
        Assert.Equal(SysInfoQuery, XorTransport.Decrypt(XorTransport.Encrypt(SysInfoQuery)));
    }

    [Theory]
    [InlineData(1, "9d6ce791ce44316e9e52fc1bc9d36496")]
    [InlineData(2, "b039216532fc844e9ae0cc8fe3ea911c9ba09c641bb96a3e1f776d93f7b5ae9b")]
    public void Auth_hash_matches(int version, string expected) => Assert.Equal(expected, Hex(KlapTransport.AuthHash(version, Account)));

    [Theory]
    [InlineData(1, "74b751bdd208bc155ae2a6d4167cc44d", "5873dd45edd01f09c1ef2e7819369e8e")]
    [InlineData(2, "49fc4839a2733ffbf464337648f65dc411778d4c03ec82c5e4cccc682163fa64", "b8628ab91c74f531603f6d5b45e730e54c123a7247b8978272c03f1e13560cea")]
    public void Fallback_account_hashes_match(int version, string kasaSetup, string blank)
    {
        Assert.Equal(kasaSetup, Hex(KlapTransport.AuthHash(version, KasaCredentials.KasaSetup)));
        Assert.Equal(blank, Hex(KlapTransport.AuthHash(version, KasaCredentials.Blank)));
    }

    [Theory]
    [InlineData(1, "c2d456076f946c707000874d2e226a1c09e89a21e459272b8ec0f01cdaf774cf", "da670547fdea58b7c8fa92720e4b1c9ea8ad0013cafc5c36d2327cb9d2e7eb7c")]
    [InlineData(2, "4bae4b2771117a942ffdc68b598de5a2396ec1a4110078d5fe129d21c9207929", "c0e6f98bbbdd133f8ae2de55ad9a70a7ea8d547f47d5b9cf1506ebf6ac898973")]
    public void Handshake_hashes_match(int version, string h1, string h2)
    {
        var auth = KlapTransport.AuthHash(version, Account);
        Assert.Equal(h1, Hex(KlapTransport.Handshake1Hash(version, LocalSeed, RemoteSeed, auth)));
        Assert.Equal(h2, Hex(KlapTransport.Handshake2Hash(version, LocalSeed, RemoteSeed, auth)));
    }

    [Fact]
    public void Session_cipher_matches()
    {
        var cipher = new KlapCipher(LocalSeed, RemoteSeed, KlapTransport.AuthHash(1, Account));
        var (payload, seq) = cipher.Encrypt(SysInfoQuery);
        Assert.Equal(1518256671, seq);
        Assert.Equal("fee06f7321c05429a63a2c88d0437251559c832035e80eac597ec390af23408dad01a5b6977828dfe46080b37ef387a383ad52c4d8b17376d8552682cde92874", Hex(payload));

        var reply = Convert.FromHexString("1f677ed7d42f534669dbc47b264d09d39c978dfc1d177092b76459d9cd7c4217ad01a5b6977828dfe46080b37ef387a387dcb07ca2e309cc495bad4486711d72d17caca8b8ce59978eeffbeace8b27e0bfc3f963f47228f8aec019177e2d147dee422323c2eab1902d09245ca5f12c92");
        Assert.Equal("""{"system":{"get_sysinfo":{"alias":"Desk","relay_state":1,"err_code":0}}}""", cipher.Decrypt(reply, seq));
    }
}

public class KlapTransportTests
{
    private static readonly KasaCredentials Account = new("office@example.com", "hunter2");

    private static KlapTransport Client(FakeKasaDevice device, KasaCredentials? configured) =>
        new(new Uri("http://plug.test/app/"), () => configured, device.AsHandler(), null);

    [Fact]
    public async Task Signs_in_with_the_configured_account_and_reads()
    {
        var device = new FakeKasaDevice(FakeKasaDevice.Kp303(), Account);
        using var klap = Client(device, Account);

        var reply = await klap.SendAsync(SysInfo.Query, CancellationToken.None);

        Assert.Equal("Bench strip", reply["system"]?["get_sysinfo"]?["alias"]?.ToString());
        Assert.Equal(Account.Username, klap.SignedInAs);
    }

    [Fact]
    public async Task An_unlinked_plug_accepts_the_kasa_setup_account_without_settings()
    {
        var device = new FakeKasaDevice(FakeKasaDevice.Kp105());
        using var klap = Client(device, null);

        await klap.SendAsync(SysInfo.Query, CancellationToken.None);

        Assert.Equal("Kasa setup account", klap.SignedInAs);
    }

    [Fact]
    public async Task A_wrong_account_is_a_sign_in_failure()
    {
        var device = new FakeKasaDevice(FakeKasaDevice.Kp105(), Account);
        using var klap = Client(device, new KasaCredentials("office@example.com", "wrong"));

        var ex = await Assert.ThrowsAsync<KasaException>(() => klap.SendAsync(SysInfo.Query, CancellationToken.None));
        Assert.Equal(KasaFailure.SignIn, ex.Failure);
    }

    [Fact]
    public async Task Keeps_the_session_and_signs_in_again_when_the_plug_drops_it()
    {
        var device = new FakeKasaDevice(FakeKasaDevice.Kp105(), Account);
        using var klap = Client(device, Account);

        await klap.SendAsync(SysInfo.Query, CancellationToken.None);
        await klap.SendAsync(SysInfo.SetRelay(null, true), CancellationToken.None);
        device.DropSession = true;
        var reply = await klap.SendAsync(SysInfo.Query, CancellationToken.None);

        Assert.Equal(1, reply["system"]?["get_sysinfo"]?["relay_state"]?.GetValue<int>());
        Assert.Equal(3, device.Requests);
    }
}

public class XorTransportTests
{
    /// <summary>The fake device on a real loopback socket, answering framed XOR requests.</summary>
    private static (int Port, Task Server) Serve(FakeKasaDevice device, int connections, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < connections; i++)
                {
                    using var client = await listener.AcceptTcpClientAsync(ct);
                    var stream = client.GetStream();
                    var header = new byte[4];
                    await stream.ReadExactlyAsync(header, ct);
                    var body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header)];
                    await stream.ReadExactlyAsync(body, ct);
                    await stream.WriteAsync(device.HandleXorFrame(body), ct);
                }
            }
            finally
            {
                listener.Stop();
            }
        }, ct);
        return (port, server);
    }

    [Fact]
    public async Task Switches_a_strip_socket_over_tcp()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var device = new FakeKasaDevice(FakeKasaDevice.Kp303());
        var (port, server) = Serve(device, 2, cts.Token);
        using var xor = new XorTransport(IPAddress.Loopback, port);

        var host = SysInfo.Parse(KasaJson.Result(await xor.SendAsync(SysInfo.Query, cts.Token), "system", "get_sysinfo"), IPAddress.Loopback, KasaEncryption.Xor);
        var fan = host.Outlets.Single(o => o.Name == "Fan");
        KasaJson.Result(await xor.SendAsync(SysInfo.SetRelay(fan.ChildId, true), cts.Token), "system", "set_relay_state");
        await server;

        var after = SysInfo.Parse(device.SysInfo, IPAddress.Loopback, KasaEncryption.Xor);
        Assert.Equal([true, false, true], after.Outlets.Select(o => o.On));
    }

    [Fact]
    public async Task Nothing_listening_is_offline()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var xor = new XorTransport(IPAddress.Loopback, port);

        var ex = await Assert.ThrowsAsync<KasaException>(() => xor.SendAsync(SysInfo.Query, CancellationToken.None));
        Assert.Equal(KasaFailure.Offline, ex.Failure);
        Assert.Equal(SocketError.ConnectionRefused, Assert.IsType<SocketException>(ex.InnerException).SocketErrorCode);
    }
}

public class SysInfoTests
{
    [Fact]
    public void A_plug_is_one_outlet_under_its_device_id()
    {
        var host = SysInfo.Parse(FakeKasaDevice.Kp105(on: true), IPAddress.Loopback, KasaEncryption.Xor);

        Assert.False(host.IsStrip);
        var outlet = Assert.Single(host.Outlets);
        Assert.Equal(host.DeviceId, outlet.Id);
        Assert.Null(outlet.ChildId);
        Assert.True(outlet.On);
        Assert.True(host.LedOn);
        Assert.Equal("KP105(UK)", host.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_strip_lists_its_sockets_with_full_ids(bool shortChildIds)
    {
        var host = SysInfo.Parse(FakeKasaDevice.Kp303(shortChildIds: shortChildIds), IPAddress.Loopback, KasaEncryption.Klap);

        Assert.True(host.IsStrip);
        Assert.Equal(["Monitors", "Soldering iron", "Fan"], host.Outlets.Select(o => o.Name));
        Assert.All(host.Outlets, o => Assert.Equal(host.DeviceId.Length + 2, o.ChildId!.Length));
        Assert.Equal(host.DeviceId + "02", host.Outlets[2].Id);
        Assert.Equal([true, false, false], host.Outlets.Select(o => o.On));
    }

    [Fact]
    public void A_bulb_is_not_a_plug()
    {
        var bulb = FakeKasaDevice.Kp105();
        bulb["type"] = "IOT.SMARTBULB";
        var ex = Assert.Throws<KasaException>(() => SysInfo.Parse(bulb, IPAddress.Loopback, KasaEncryption.Xor));
        Assert.Equal(KasaFailure.Unsupported, ex.Failure);
    }

    [Fact]
    public void Socket_commands_carry_the_child_id()
    {
        Assert.Equal("""{"system":{"set_relay_state":{"state":1}},"context":{"child_ids":["ABC00"]}}""", SysInfo.SetRelay("ABC00", true).ToJsonString());
        Assert.Equal("""{"system":{"set_relay_state":{"state":0}}}""", SysInfo.SetRelay(null, false).ToJsonString());
        Assert.Equal("""{"system":{"set_led_off":{"off":1}}}""", SysInfo.SetLed(false).ToJsonString());
    }

    [Fact]
    public void Device_errors_are_reported()
    {
        var device = new FakeKasaDevice(FakeKasaDevice.Kp303());
        var reply = device.Handle(SysInfo.SetRelay("NOPE00", true));
        var ex = Assert.Throws<KasaException>(() => KasaJson.Result(reply, "system", "set_relay_state"));
        Assert.Equal("entry not exist", ex.Message);
    }

    [Fact]
    public void The_cache_round_trips()
    {
        var host = SysInfo.Parse(FakeKasaDevice.Kp303(), IPAddress.Parse("10.0.0.7"), KasaEncryption.Klap);
        var back = HostInfo.FromCache(JsonNode.Parse(host.ToCache().ToJsonString())!.AsObject())!;

        Assert.Equal(host.DeviceId, back.DeviceId);
        Assert.Equal(host.Address, back.Address);
        Assert.Equal(KasaEncryption.Klap, back.Encryption);
        Assert.Equal(host.Outlets.Select(o => (o.Id, o.ChildId, o.Name)), back.Outlets.Select(o => (o.Id, o.ChildId, o.Name)));
        Assert.All(back.Outlets, o => Assert.Null(o.On));
    }
}

public class DiscoveryTests
{
    [Fact]
    public void Reads_a_legacy_answer()
    {
        var device = new FakeKasaDevice(FakeKasaDevice.Kp105());
        var answer = device.HandleXorDiscovery(XorTransport.Encrypt(SysInfo.Query.ToJsonString()))!;

        var hit = KasaDiscovery.Parse(answer, new IPEndPoint(IPAddress.Parse("10.0.0.5"), KasaDiscovery.LegacyPort))!;

        Assert.Equal("KP105(UK)", hit.Model);
        Assert.NotNull(hit.SysInfo);
        Assert.Null(hit.EncryptType);
    }

    [Fact]
    public void Reads_a_klap_answer()
    {
        var device = new FakeKasaDevice(FakeKasaDevice.Kp303());
        var answer = device.ProbeReply(IPAddress.Parse("10.0.0.6"));

        var hit = KasaDiscovery.Parse(answer, new IPEndPoint(IPAddress.Parse("10.0.0.6"), KasaDiscovery.ProbePort))!;

        Assert.Equal("KLAP", hit.EncryptType);
        Assert.Equal("IOT.SMARTPLUGSWITCH", hit.Family);
        Assert.Equal("KP303(UK)", hit.Model);
        Assert.Null(hit.SysInfo);
    }

    [Fact]
    public void The_probe_crc_matches_the_known_probe()
    {
        // The fixed probe's last 4 bytes are the CRC of itself with the placeholder in their place.
        byte[] withPlaceholder = [.. KasaDiscovery.Probe[..12], 0x5A, 0x6B, 0x7C, 0x8D];
        Assert.Equal(0x463cb5d3u, KasaDiscovery.Crc32(withPlaceholder));

        var keyed = KasaDiscovery.BuildKeyedProbe();
        var crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(keyed.AsSpan(12));
        byte[] check = [.. keyed[..12], 0x5A, 0x6B, 0x7C, 0x8D, .. keyed[16..]];
        Assert.Equal(crc, KasaDiscovery.Crc32(check));
        Assert.Contains("BEGIN PUBLIC KEY", System.Text.Encoding.UTF8.GetString(keyed, 16, keyed.Length - 16));
    }

    [Fact]
    public void Ignores_noise() =>
        Assert.Null(KasaDiscovery.Parse([1, 2, 3], new IPEndPoint(IPAddress.Loopback, KasaDiscovery.LegacyPort)));
}
