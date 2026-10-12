using System.Text.Json;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>One way of getting a JSON request to a device and its answer back (XOR or KLAP).</summary>
internal interface IKasaTransport : IDisposable
{
    string Name { get; }

    Task<JsonObject> SendAsync(JsonObject request, CancellationToken ct);
}

/// <summary>The TP-Link account a newer-firmware plug was set up with, from the plugin settings.</summary>
internal sealed record KasaCredentials(string Username, string Password)
{
    public static readonly KasaCredentials Blank = new(string.Empty, string.Empty);

    /// <summary>The account every Kasa plug accepts until it is linked to someone's account.</summary>
    public static readonly KasaCredentials KasaSetup = new("kasa@tp-link.net", "kasaSetup");

    public bool IsBlank => Username.Length == 0 && Password.Length == 0;

    public override string ToString() => IsBlank ? "(none)" : Username;
}

internal static class KasaJson
{
    public static JsonObject ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new KasaException("Reply was not a JSON object", KasaFailure.Protocol);
        }
        catch (JsonException ex)
        {
            throw new KasaException("Reply was not JSON", KasaFailure.Protocol, ex);
        }
    }

    /// <summary>
    /// The result of <c>{module:{method:…}}</c> from a reply, throwing when the device reports an
    /// error code (e.g. "module not support").
    /// </summary>
    public static JsonObject Result(JsonObject reply, string module, string method)
    {
        if (reply[module]?[method] is not JsonObject result)
        {
            throw new KasaException(reply[module]?["err_msg"]?.ToString() ?? $"No {module}.{method} in the reply", KasaFailure.Protocol);
        }
        if (result["err_code"] is JsonValue code && code.TryGetValue<int>(out var err) && err != 0)
        {
            throw new KasaException(result["err_msg"]?.ToString() ?? $"Error {err}", KasaFailure.Protocol);
        }
        return result;
    }
}
