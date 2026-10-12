using System.Net;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>How a device is spoken to; <see cref="Unknown"/> until it has answered once.</summary>
internal enum KasaEncryption
{
    Unknown,
    Xor,
    Klap
}

/// <summary>One switchable outlet: the plug itself, or one socket of a power strip.</summary>
/// <param name="Id">What keys store: the device id for a plug, the full child id for a strip socket.</param>
/// <param name="ChildId">Set for a strip socket; sent in the request's context to pick the socket.</param>
internal sealed record OutletInfo(string Id, string? ChildId, string Name, bool? On);

/// <summary>
/// One physical Kasa device as last read: where it is, how to talk to it, and the state of its
/// outlets and status light. Immutable; every read produces a new one.
/// </summary>
internal sealed record HostInfo
{
    public required string DeviceId { get; init; }
    public required IPAddress Address { get; init; }
    public KasaEncryption Encryption { get; init; }
    public string Model { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Mac { get; init; } = string.Empty;

    /// <summary>The status light; null when unknown or the model has none to switch.</summary>
    public bool? LedOn { get; init; }

    public IReadOnlyList<OutletInfo> Outlets { get; init; } = [];

    /// <summary>Set when the device was found but could not be read, e.g. a sign-in failure.</summary>
    public string? Problem { get; init; }

    public bool IsStrip => Outlets.Any(o => o.ChildId is not null);

    public override string ToString() => $"{(Name.Length > 0 ? Name : Model)} ({Model}) at {Address}";

    // ---- the device cache: where devices were, so keys work before discovery runs ----

    public JsonObject ToCache() => new()
    {
        ["deviceId"] = DeviceId,
        ["address"] = Address.ToString(),
        ["encryption"] = Encryption.ToString(),
        ["model"] = Model,
        ["name"] = Name,
        ["mac"] = Mac,
        ["outlets"] = new JsonArray(Outlets.Select(o => (JsonNode)new JsonObject
        {
            ["id"] = o.Id, ["childId"] = o.ChildId, ["name"] = o.Name
        }).ToArray())
    };

    public static HostInfo? FromCache(JsonObject json)
    {
        if (json["deviceId"]?.ToString() is not { Length: > 0 } id || !IPAddress.TryParse(json["address"]?.ToString(), out var address)) return null;
        return new HostInfo
        {
            DeviceId = id,
            Address = address,
            Encryption = Enum.TryParse<KasaEncryption>(json["encryption"]?.ToString(), out var e) ? e : KasaEncryption.Unknown,
            Model = json["model"]?.ToString() ?? string.Empty,
            Name = json["name"]?.ToString() ?? string.Empty,
            Mac = json["mac"]?.ToString() ?? string.Empty,
            Outlets = (json["outlets"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(o => o["id"] is not null)
                .Select(o => new OutletInfo(o["id"]!.ToString(), o["childId"]?.ToString(), o["name"]?.ToString() ?? string.Empty, null))
                .ToList()
        };
    }
}

/// <summary>Turns a <c>system.get_sysinfo</c> answer into a <see cref="HostInfo"/>.</summary>
internal static class SysInfo
{
    public static readonly JsonObject Query = new() { ["system"] = new JsonObject { ["get_sysinfo"] = new JsonObject() } };

    public static HostInfo Parse(JsonObject sysinfo, IPAddress address, KasaEncryption encryption)
    {
        var type = (sysinfo["mic_type"] ?? sysinfo["type"])?.ToString() ?? string.Empty;
        var model = sysinfo["model"]?.ToString() ?? string.Empty;
        if (type.Length > 0 && !type.Contains("PLUG", StringComparison.OrdinalIgnoreCase) && !type.Contains("SWITCH", StringComparison.OrdinalIgnoreCase))
        {
            throw new KasaException($"{model} is a {type}, not a plug", KasaFailure.Unsupported);
        }

        var deviceId = sysinfo["deviceId"]?.ToString();
        if (string.IsNullOrEmpty(deviceId)) throw new KasaException("No device id in the reply", KasaFailure.Protocol);
        var name = sysinfo["alias"]?.ToString() ?? model;

        var outlets = new List<OutletInfo>();
        if (sysinfo["children"] is JsonArray children && children.Count > 0)
        {
            var index = 0;
            foreach (var child in children.OfType<JsonObject>())
            {
                index++;
                var childId = child["id"]?.ToString();
                if (string.IsNullOrEmpty(childId)) continue;
                // Strips report either the full id or just the socket number ("00"); requests want the full one.
                var fullId = childId.Length <= 2 ? deviceId + childId : childId;
                var childName = child["alias"]?.ToString() is { Length: > 0 } alias ? alias : $"{name} socket {index}";
                outlets.Add(new OutletInfo(fullId, fullId, childName, Flag(child["state"])));
            }
        }
        else
        {
            outlets.Add(new OutletInfo(deviceId, null, name, Flag(sysinfo["relay_state"])));
        }

        return new HostInfo
        {
            DeviceId = deviceId,
            Address = address,
            Encryption = encryption,
            Model = model,
            Name = name,
            Mac = (sysinfo["mac"] ?? sysinfo["mic_mac"])?.ToString() ?? string.Empty,
            // led_off: 1 means the status light is switched off.
            LedOn = Flag(sysinfo["led_off"]) is { } off ? !off : null,
            Outlets = outlets
        };
    }

    public static JsonObject SetRelay(string? childId, bool on)
    {
        var request = new JsonObject { ["system"] = new JsonObject { ["set_relay_state"] = new JsonObject { ["state"] = on ? 1 : 0 } } };
        if (childId is not null) request["context"] = new JsonObject { ["child_ids"] = new JsonArray(childId) };
        return request;
    }

    public static JsonObject SetLed(bool on) =>
        new() { ["system"] = new JsonObject { ["set_led_off"] = new JsonObject { ["off"] = on ? 0 : 1 } } };

    private static bool? Flag(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var n) ? n != 0
        : node is JsonValue b && b.TryGetValue<bool>(out var flag) ? flag
        : null;
}
