using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LoupixDeck.Plugin.Kasa.Kasa;

namespace LoupixDeck.Plugin.Kasa.Simulation;

/// <summary>
/// Runs <see cref="FakeKasaDevice"/>s on real sockets: an older-firmware KP105 on 127.0.0.1
/// (TCP and UDP 9999) and, when given one of this computer's addresses, a newer-firmware KP303 on
/// it (HTTP 80 and UDP 20002). Point the plugin at them by listing those addresses in its settings.
/// </summary>
/// <remarks>
/// macOS lets an ordinary user listen on port 80 only on all addresses at once, so the KP303's HTTP
/// side does that; it still only advertises itself on the given address. Port 9999 on that address
/// is closed, as on a real plug with KLAP firmware.
/// </remarks>
internal static class Simulator
{
    public static async Task<int> RunAsync(List<string> args, KasaCredentials account)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var tasks = new List<Task>();
        var plug = new FakeKasaDevice(FakeKasaDevice.Kp105());
        tasks.Add(ServeXorAsync(plug, IPAddress.Loopback, cts.Token));
        tasks.Add(ServeUdpAsync(IPAddress.Loopback, XorTransport.DefaultPort, plug.HandleXorDiscovery, cts.Token));
        Console.WriteLine("KP105 \"Desk lamp\" (older firmware) on 127.0.0.1");

        if (args.Count > 0)
        {
            var address = IPAddress.Parse(args[0]);
            var strip = new FakeKasaDevice(FakeKasaDevice.Kp303(), account.IsBlank ? null : account);
            tasks.Add(ServeKlapAsync(strip, cts.Token));
            tasks.Add(ServeUdpAsync(address, KasaDiscovery.ProbePort, d => d.Length >= 16 && d[0] == 2 ? strip.ProbeReply(address) : null, cts.Token));
            Console.WriteLine($"KP303 \"Bench strip\" (newer firmware) on {address}, account: {(account.IsBlank ? "Kasa setup (no settings needed)" : account.Username)}");
        }

        Console.WriteLine("List these addresses under Plug addresses in the plugin settings. Ctrl+C to stop.");
        // Stop at the first listener that fails, rather than once they all have.
        var first = await Task.WhenAny(tasks);
        if (first.Exception?.InnerException is { } ex and not OperationCanceledException)
        {
            Console.Error.WriteLine($"Could not listen: {ex.Message}");
            cts.Cancel();
            return 2;
        }
        return 0;
    }

    private static async Task ServeXorAsync(FakeKasaDevice device, IPAddress address, CancellationToken ct)
    {
        var listener = new TcpListener(address, XorTransport.DefaultPort);
        listener.Start();
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var header = new byte[4];
                        try
                        {
                            while (true)
                            {
                                await stream.ReadExactlyAsync(header, ct);
                                var body = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                                await stream.ReadExactlyAsync(body, ct);
                                await stream.WriteAsync(device.HandleXorFrame(body), ct);
                                Log("XOR request", body.Length);
                            }
                        }
                        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
                        {
                            // Client finished.
                        }
                    }
                }, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeUdpAsync(IPAddress address, int port, Func<byte[], byte[]?> answer, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(address, port));
        while (true)
        {
            var packet = await udp.ReceiveAsync(ct);
            if (answer(packet.Buffer) is { } reply)
            {
                await udp.SendAsync(reply, packet.RemoteEndPoint, ct);
                Log($"discovery :{port}", packet.Buffer.Length);
            }
        }
    }

    /// <summary>Just enough HTTP/1.1 for KLAP: POSTs with a Content-Length, one request per connection.</summary>
    private static async Task ServeKlapAsync(FakeKasaDevice device, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, 80);
        listener.Start();
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await HandleHttpAsync(device, client.GetStream(), ct);
                        }
                        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException or FormatException)
                        {
                            // Client went away or sent something we do not speak.
                        }
                    }
                }, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleHttpAsync(FakeKasaDevice device, NetworkStream stream, CancellationToken ct)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 8192 && !(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            await stream.ReadExactlyAsync(one, ct);
            head.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var target = lines[0].Split(' ')[1];
        var headers = lines.Skip(1).Where(l => l.Contains(':'))
            .ToDictionary(l => l[..l.IndexOf(':')].Trim(), l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
        var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length) : 0];
        await stream.ReadExactlyAsync(body, ct);

        var uri = new Uri(new Uri("http://plug"), target);
        var path = uri.AbsolutePath.Split('/').Last();
        var seq = int.TryParse(System.Web.HttpUtility.ParseQueryString(uri.Query)["seq"], out var n) ? n : (int?)null;
        var reply = device.HandleKlap(path, seq, body, headers.GetValueOrDefault("Cookie"));

        var response = new StringBuilder()
            .Append($"HTTP/1.1 {(int)reply.Status} {reply.Status}\r\n")
            .Append("Content-Type: application/octet-stream\r\n")
            .Append($"Content-Length: {reply.Body.Length}\r\n")
            .Append("Connection: close\r\n");
        if (reply.SetCookie is not null) response.Append($"Set-Cookie: {reply.SetCookie}\r\n");
        response.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), ct);
        await stream.WriteAsync(reply.Body, ct);
        Log($"KLAP {path} → {(int)reply.Status}", body.Length);
    }

    private static void Log(string what, int bytes) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {what} ({bytes} bytes)");
}
