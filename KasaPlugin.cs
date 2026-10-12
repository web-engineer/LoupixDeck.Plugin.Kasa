using System.Net;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Kasa.Commands;
using LoupixDeck.Plugin.Kasa.Kasa;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Kasa;

/// <summary>
/// LoupixDeck plugin entry point. Owns the <see cref="PlugService"/> (the plugs on the LAN, or the
/// demo plugs), exposes a fixed set of commands that take the outlet as a parameter, and lists the
/// plugs as a menu so the parameter is filled in for the user.
/// </summary>
public sealed class KasaPlugin : LoupixPlugin, IPluginSettingsPage, IMenuContributor
{
    public const string GroupName = "Kasa";

    private const string AddressesKey = "addresses";
    private const string UsernameKey = "username";
    private const string PasswordKey = "password";
    private const string DemoKey = "demo";
    private const string PollSecondsKey = "poll_seconds";
    private const string DeviceCacheKey = "device_cache";

    private const string SupportUrl = "https://web-engineer.co.uk/contact";
    private const string IssuesUrl = "https://github.com/web-engineer/LoupixDeck.Plugin.Kasa/issues";

    private const int DefaultPollSeconds = 5;
    private static readonly TimeSpan MenuDiscoverWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MenuPollStep = TimeSpan.FromMilliseconds(100);

    private readonly Lazy<IReadOnlyList<IPluginCommand>> _commands;
    private IPluginHost _host = null!;
    private PlugService _plugs = null!;
    private Settings _applied = null!;

    public KasaPlugin()
    {
        _commands = new Lazy<IReadOnlyList<IPluginCommand>>(() => [new PowerCommand(this), new LedCommand(this)],
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "kasa",
        Name = "Kasa Smart Plugs",
        Version = new Version(0, 1, 0),
        SdkVersion = SdkInfo.Version,
        Author = "web-engineer",
        Description = "Switch TP-Link Kasa smart plugs and power strips from the deck, with live on/off state on the keys.",
        Icon = LoadEmbeddedIcon("LoupixDeck.Plugin.Kasa.icon.png")
    };

    internal PlugService Plugs => _plugs;

    /// <summary>Why a key has no plug to show, in a few words.</summary>
    internal string NoPlugsReason =>
        _plugs.Plugs.Count > 0 ? "Unknown plug"
        : _plugs.DiscoveredAt is null ? "Looking…"
        : "No plugs found";

    // ---- lifecycle ----

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _applied = ReadSettings();
        _plugs = new PlugService(CreateBackend(_applied), host.Logger.Info, (message, ex) => host.Logger.Error(message, ex))
        {
            PollInterval = TimeSpan.FromSeconds(_applied.PollSeconds)
        };
        _plugs.Changed += RefreshKeys;
        _plugs.HostsFound += SaveDeviceCache;
        if (!_applied.Demo) _plugs.LoadCachedHosts(LoadDeviceCache());
        _plugs.UseAddresses(_applied.Addresses);
        _plugs.Start();

        host.Logger.Info($"Kasa plugin started: {_plugs.Backend.Name}" +
                         (_applied.Addresses.Count > 0 ? $", also asking {string.Join(", ", _applied.Addresses)}" : ""));
    }

    public override void Shutdown()
    {
        if (_plugs is null) return;
        _plugs.Changed -= RefreshKeys;
        _plugs.HostsFound -= SaveDeviceCache;
        _plugs.Dispose();
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands.Value;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = GroupName,
            Description = "Kasa smart plugs and power strips: on/off and status light, with live state on the keys",
            Icon = Glyphs.PowerSocket
        }
    ];

    private void RefreshKeys()
    {
        foreach (var command in _commands.Value) _host.RequestButtonRefresh(command.Descriptor.CommandName);
    }

    // ---- IMenuContributor: Kasa › plug (› socket) › action ----

    /// <summary>
    /// The host turns each top-level node into a group of its own, so everything goes inside one node
    /// named after the plugin's group, which the host merges into that group's card.
    /// </summary>
    public async Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target) =>
        [new MenuNode { Name = GroupName, Children = await BuildMenuNodesAsync(target).ConfigureAwait(false) }];

    private async Task<IReadOnlyList<MenuNode>> BuildMenuNodesAsync(ButtonTargets target)
    {
        // Discovery starts with the plugin; the host gives a menu 5 s, so wait a little for the first
        // one rather than start another.
        for (var waited = TimeSpan.Zero; _plugs.Hosts.Count == 0 && _plugs.DiscoveredAt is null && waited < MenuDiscoverWait; waited += MenuPollStep)
        {
            await Task.Delay(MenuPollStep).ConfigureAwait(false);
        }

        var hosts = _plugs.Hosts;
        if (hosts.Count == 0)
        {
            return [Info(_plugs.DiscoveredAt is null
                ? "Still looking for Kasa plugs; try again in a moment"
                : "No Kasa plugs found. Over a VPN or on another network, list their addresses in Settings › Plugins › Kasa Smart Plugs")];
        }

        var nodes = new List<MenuNode>();
        foreach (var host in hosts)
        {
            if (!host.IsStrip)
            {
                nodes.Add(Folder(host.Name, [.. PowerLeaves(host.Outlets[0].Id), LedLeaf(host.DeviceId)]));
                continue;
            }

            var children = host.Outlets.Select(o => Folder(o.Name, PowerLeaves(o.Id))).ToList();
            children.Add(Folder("Whole strip", PowerLeaves(host.DeviceId)));
            children.Add(LedLeaf(host.DeviceId));
            nodes.Add(Folder(host.Name, children));
        }

        if (_plugs.Plugs.Count > 1) nodes.Add(Folder("All plugs", PowerLeaves(DeviceSelector.All)));
        return nodes;
    }

    private static List<MenuNode> PowerLeaves(string selector) =>
    [
        Leaf("On/off", PowerCommand.Name, selector, "toggle"),
        Leaf("Turn on", PowerCommand.Name, selector, "on"),
        Leaf("Turn off", PowerCommand.Name, selector, "off")
    ];

    private static MenuNode LedLeaf(string deviceId) => Leaf("Status light on/off", LedCommand.Name, deviceId, "toggle");

    private static MenuNode Leaf(string name, string command, string selector, string action) => new()
    {
        Name = name,
        CommandName = command,
        Parameters = new Dictionary<string, string>
        {
            [KasaCommandBase.DeviceParameter] = selector,
            [KasaCommandBase.ActionParameter] = action
        }
    };

    private static MenuNode Folder(string name, IEnumerable<MenuNode> children) => new() { Name = name, Children = children.ToList() };

    private static MenuNode Info(string text) => new() { Name = text };

    // ---- device cache ----

    private void SaveDeviceCache(IReadOnlyList<HostInfo> hosts)
    {
        if (_applied.Demo) return;
        try
        {
            _host.Settings.Set(DeviceCacheKey, new JsonArray(hosts.Select(h => (JsonNode)h.ToCache()).ToArray()).ToJsonString());
            _host.Settings.Save();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Could not save the plug list", ex);
        }
    }

    private IReadOnlyList<HostInfo> LoadDeviceCache()
    {
        try
        {
            var text = _host.Settings.Get(DeviceCacheKey, string.Empty);
            if (string.IsNullOrWhiteSpace(text) || JsonNode.Parse(text) is not JsonArray array) return [];
            return array.OfType<JsonObject>().Select(HostInfo.FromCache).OfType<HostInfo>().ToList();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Could not read the saved plug list", ex);
            return [];
        }
    }

    // ---- settings ----

    private sealed record Settings(IReadOnlyList<IPAddress> Addresses, KasaCredentials Credentials, bool Demo, int PollSeconds);

    private Settings ReadSettings()
    {
        var addresses = (_host.Settings.Get(AddressesKey, string.Empty) ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .Distinct()
            .ToList();
        var poll = (int)Math.Clamp(_host.Settings.Get(PollSecondsKey, (long)DefaultPollSeconds), 2, 3600);
        return new Settings(
            addresses,
            new KasaCredentials((_host.Settings.Get(UsernameKey, string.Empty) ?? string.Empty).Trim(), _host.Settings.Get(PasswordKey, string.Empty) ?? string.Empty),
            _host.Settings.Get(DemoKey, false),
            poll);
    }

    private IPlugBackend CreateBackend(Settings settings) =>
        settings.Demo ? new DemoBackend() : new KasaLanBackend(() => _applied.Credentials);

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema =>
    [
        new PluginSettingDescriptor
        {
            Key = "finding",
            Label = "Finding your plugs",
            Kind = PluginSettingKind.Heading,
            Description = "Plugs on the same network as this computer are found by themselves. Everything stays on your network: " +
                          "the plugin talks to the plugs directly, never through TP-Link's cloud."
        },
        new PluginSettingDescriptor
        {
            Key = AddressesKey,
            Label = "Plug addresses (optional)",
            Kind = PluginSettingKind.Text,
            DefaultValue = string.Empty,
            Description = "IP addresses to ask directly, separated by commas, for plugs that are not found by themselves: over a VPN, " +
                          "on another subnet, or on a separate Wi-Fi. The Kasa app shows each plug's address under Device Info."
        },
        new PluginSettingDescriptor
        {
            Key = "account",
            Label = "TP-Link account",
            Kind = PluginSettingKind.Heading,
            Description = "Only needed for plugs on newer firmware, which accept commands only from the TP-Link account they were set up " +
                          "with in the Kasa app. Older firmware ignores it. Leave it empty and Find plugs will tell you if one needs it."
        },
        new PluginSettingDescriptor
        {
            Key = UsernameKey,
            Label = "Email",
            Kind = PluginSettingKind.Text,
            DefaultValue = string.Empty,
            Description = "The email you sign in to the Kasa app with."
        },
        new PluginSettingDescriptor
        {
            Key = PasswordKey,
            Label = "Password",
            Kind = PluginSettingKind.Password,
            DefaultValue = string.Empty,
            Description = "Stored only on this computer. It never leaves it: plugs are sent a one-way hash of it, mixed with random " +
                          "values, to prove the plugin knows it."
        },
        new PluginSettingDescriptor
        {
            Key = DemoKey,
            Label = "Demo plugs",
            Kind = PluginSettingKind.Toggle,
            DefaultValue = false,
            Description = "A pretend KP105 plug and KP303 power strip, to try every key without hardware. Ignores real plugs while on."
        },
        new PluginSettingDescriptor
        {
            Key = PollSecondsKey,
            Label = "Read plugs every (seconds)",
            Kind = PluginSettingKind.Number,
            DefaultValue = (long)DefaultPollSeconds,
            Description = "2 to 3600. Plugs are read only while one of their keys is on screen, so a change made in the Kasa app " +
                          "or at the plug shows up within this time."
        },
        new PluginSettingDescriptor
        {
            Key = "about",
            Label = "About and support",
            Kind = PluginSettingKind.Heading,
            Description = "Made by web-engineer; not affiliated with TP-Link. For help, our public Slack and our tracker are on the contact page. " +
                          "Report bugs and request features on GitHub; please report security issues privately through the contact form."
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
    [
        new PluginSettingAction { Label = "Find plugs", Invoke = FindPlugsAsync },
        new PluginSettingAction { Label = "Get support", Invoke = () => OpenLink(SupportUrl) },
        new PluginSettingAction { Label = "Report a bug or request a feature", Invoke = () => OpenLink(IssuesUrl) }
    ];

    public void OnSettingsSaved()
    {
        var next = ReadSettings();
        var previous = _applied;
        _applied = next;

        _plugs.PollInterval = TimeSpan.FromSeconds(next.PollSeconds);
        if (next.Demo != previous.Demo)
        {
            _plugs.UseBackend(CreateBackend(next));
            if (!next.Demo) _plugs.LoadCachedHosts(LoadDeviceCache());
        }
        else if (next.Credentials != previous.Credentials && _plugs.Backend is KasaLanBackend lan)
        {
            lan.ResetSessions();
        }
        _plugs.UseAddresses(next.Addresses);
        RefreshKeys();
    }

    private async Task<string> FindPlugsAsync()
    {
        IReadOnlyList<HostInfo> found;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            found = await _plugs.DiscoverAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "Gave up after 30 seconds.";
        }

        var usable = found.Where(h => h.Problem is null).ToList();
        var problems = found.Where(h => h.Problem is not null).ToList();
        if (found.Count == 0)
        {
            return "No plugs answered. Check they are switched on and on this network. Over a VPN or on another subnet, " +
                   "list their addresses above and save first.";
        }

        var lines = usable.Select(h =>
        {
            var sockets = h.IsStrip ? $", {h.Outlets.Count} sockets" : "";
            var account = _plugs.Backend is KasaLanBackend lan && lan.SignedInAs(h.Address) is { } who ? $", signed in with {who}" : "";
            return $"{h.Name} ({h.Model}) at {h.Address}, {Describe(h.Encryption)}{sockets}{account}";
        }).Concat(problems.Select(h => $"{h.Name} at {h.Address}: {h.Problem}"));
        return $"Found {found.Count}: {string.Join("; ", lines)}.";
    }

    private static string Describe(KasaEncryption encryption) => encryption switch
    {
        KasaEncryption.Xor => "older firmware",
        KasaEncryption.Klap => "newer firmware",
        _ => "unknown firmware"
    };

    private Task<string> OpenLink(string url) =>
        Task.FromResult(_host.OpenBrowser(url) ? $"Opened {url}" : $"Could not open a browser; visit {url}");

    private static byte[]? LoadEmbeddedIcon(string name)
    {
        try
        {
            using var stream = typeof(KasaPlugin).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
