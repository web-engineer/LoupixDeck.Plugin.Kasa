using System.Net;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>
/// Where plugs, their state and controls come from: the plugs on the LAN, or the built-in demo
/// plugs. Everything is addressed by <see cref="HostInfo"/>, one physical device; a power strip's
/// outlets are reached through it by child id.
/// </summary>
internal interface IPlugBackend : IDisposable
{
    /// <summary>Short name for logs and the settings page, e.g. "LAN".</summary>
    string Name { get; }

    /// <summary>
    /// Finds the plugs that answer within a few seconds: broadcast discovery on every network this
    /// computer is on, plus a direct question to each listed address (for VPNs and other subnets).
    /// </summary>
    Task<IReadOnlyList<HostInfo>> DiscoverAsync(IReadOnlyList<IPAddress> addresses, CancellationToken ct);

    /// <summary>Reads a device's state (all outlets and the status light) at its last known address.</summary>
    Task<HostInfo> ReadAsync(HostInfo host, CancellationToken ct);

    /// <summary>Switches the device's relay, or one outlet of a strip when <paramref name="childId"/> is set.</summary>
    Task SetPowerAsync(HostInfo host, string? childId, bool on, CancellationToken ct);

    /// <summary>Switches the device's status light (the LED on the plug or strip).</summary>
    Task SetLedAsync(HostInfo host, bool on, CancellationToken ct);
}

/// <summary>Why a request failed, in words a key can show.</summary>
internal enum KasaFailure
{
    /// <summary>No answer: unplugged, off the network, or a different address now.</summary>
    Offline,

    /// <summary>A newer-firmware plug refused the TP-Link account in the settings.</summary>
    SignIn,

    /// <summary>The device answered with something we could not use.</summary>
    Protocol,

    /// <summary>A device family the plugin does not speak (e.g. Tapo-protocol models).</summary>
    Unsupported
}

/// <summary>A failure the user should see in words, e.g. "Offline" or "Sign-in failed".</summary>
internal sealed class KasaException(string message, KasaFailure failure, Exception? inner = null) : Exception(message, inner)
{
    public KasaFailure Failure { get; } = failure;
}
