using System.Net;
using LoupixDeck.Plugin.Kasa.Kasa;
using LoupixDeck.Plugin.Kasa.Simulation;

// kasa-probe: talk to Kasa plugs the way the plugin does, and print what happens.
// A TP-Link account (only needed by newer firmware) comes from KASA_USERNAME and KASA_PASSWORD,
// so it stays out of shell history.

const string Usage = """
    Usage: kasa-probe <command> [arguments]

      discover [ip ...]               Find plugs: broadcast, plus a direct question to each listed address
      info <ip>                       Read a plug: model, firmware protocol, sockets, state, status light
      on|off|toggle <ip> [socket]     Switch a plug, or one socket of a strip (name or number), then read it back
      led <ip> on|off                 Switch the status light, then read it back
      simulate [klap-ip]              Run pretend plugs on this computer until Ctrl+C: a KP105 on 127.0.0.1
                                      (older firmware) and, given an address of this computer, a KP303 on it
                                      (newer firmware; needs port 80 free)

    Set KASA_USERNAME and KASA_PASSWORD for plugs on newer firmware (the Kasa app account).
    """;

if (args.Length == 0)
{
    Console.WriteLine(Usage);
    return 1;
}

var username = Environment.GetEnvironmentVariable("KASA_USERNAME") ?? string.Empty;
var password = Environment.GetEnvironmentVariable("KASA_PASSWORD") ?? string.Empty;
var account = new KasaCredentials(username.Trim(), password);

if (args[0] == "simulate") return await Simulator.RunAsync(args.Skip(1).ToList(), account);

using var lan = new KasaLanBackend(() => account);
try
{
    switch (args[0])
    {
        case "discover":
            return await Discover(args.Skip(1).Select(IPAddress.Parse).ToList());
        case "info" when args.Length > 1:
            return Print(await Read(IPAddress.Parse(args[1])));
        case "on" or "off" or "toggle" when args.Length > 1:
            return await Switch(IPAddress.Parse(args[1]), args[0], args.Length > 2 ? string.Join(' ', args.Skip(2)) : null);
        case "led" when args.Length > 2 && args[2] is "on" or "off":
        {
            var host = await Read(IPAddress.Parse(args[1]));
            Console.WriteLine($"before: status light {Flag(host.LedOn)}");
            await lan.SetLedAsync(host, args[2] == "on", CancellationToken.None);
            Console.WriteLine($"after:  status light {Flag((await lan.ReadAsync(host, CancellationToken.None)).LedOn)}");
            return 0;
        }
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (KasaException ex)
{
    Console.Error.WriteLine($"Kasa: {ex.Message} ({ex.Failure})" + (ex.InnerException is { } inner ? $": {inner.Message}" : ""));
    if (ex.Failure == KasaFailure.SignIn) Console.Error.WriteLine("Set KASA_USERNAME and KASA_PASSWORD to the Kasa app account this plug is registered to.");
    return 2;
}
catch (FormatException)
{
    Console.Error.WriteLine("That is not an IP address.");
    return 1;
}

async Task<HostInfo> Read(IPAddress address)
{
    var host = await lan.ReadAsync(new HostInfo { DeviceId = string.Empty, Address = address }, CancellationToken.None);
    return host;
}

async Task<int> Discover(List<IPAddress> addresses)
{
    Console.WriteLine("Asking for 3 s" + (addresses.Count > 0 ? $" (broadcast, and {string.Join(", ", addresses)} directly)" : " (broadcast)") + "…");
    var found = await lan.DiscoverAsync(addresses, CancellationToken.None);
    if (found.Count == 0)
    {
        Console.WriteLine("No plugs answered. Over a VPN or across subnets, broadcasts do not get through: list the plugs' addresses.");
        return 1;
    }
    foreach (var host in found)
    {
        if (host.Problem is not null) Console.WriteLine($"{host.Address}: {host.Model} {host.Problem}");
        else Print(host);
    }
    return 0;
}

int Print(HostInfo host)
{
    var protocol = host.Encryption switch
    {
        KasaEncryption.Xor => "older firmware (XOR on TCP 9999)",
        KasaEncryption.Klap => $"newer firmware (KLAP on HTTP 80, signed in with {lan.SignedInAs(host.Address) ?? "?"})",
        _ => "unknown protocol"
    };
    Console.WriteLine($"{host.Name}  [{host.Model}  {host.Address}  {host.Mac}]");
    Console.WriteLine($"    {protocol}; status light {Flag(host.LedOn)}; id {host.DeviceId}");
    var n = 0;
    foreach (var outlet in host.Outlets)
    {
        n++;
        Console.WriteLine(host.IsStrip ? $"    socket {n} \"{outlet.Name}\": {Flag(outlet.On)}" : $"    power: {Flag(outlet.On)}");
    }
    return 0;
}

async Task<int> Switch(IPAddress address, string action, string? socket)
{
    var host = await Read(address);
    var outlets = host.Outlets.ToList();
    if (socket is not null)
    {
        var pick = int.TryParse(socket, out var number) && number >= 1 && number <= outlets.Count
            ? outlets[number - 1]
            : outlets.FirstOrDefault(o => o.Name.StartsWith(socket, StringComparison.OrdinalIgnoreCase));
        if (pick is null)
        {
            Console.Error.WriteLine($"No socket matches \"{socket}\". Try: kasa-probe info {address}");
            return 1;
        }
        outlets = [pick];
    }
    else if (host.IsStrip)
    {
        Console.WriteLine("No socket given: switching the whole strip.");
    }

    foreach (var outlet in outlets)
    {
        var on = action switch { "on" => true, "off" => false, _ => outlet.On != true };
        Console.WriteLine($"{outlet.Name}: {Flag(outlet.On)} → {Flag(on)}");
        await lan.SetPowerAsync(host, outlet.ChildId, on, CancellationToken.None);
    }
    Console.WriteLine("after:");
    return Print(await lan.ReadAsync(host, CancellationToken.None));
}

static string Flag(bool? value) => value switch { true => "on", false => "off", null => "?" };
