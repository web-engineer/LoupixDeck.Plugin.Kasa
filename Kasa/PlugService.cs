using System.Collections.Concurrent;
using System.Net;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>A switchable outlet as keys see it: a plug, or one socket of a strip.</summary>
internal sealed record KasaPlug(string Id, string DeviceId, string? ChildId, string Name, string Model, string HostName)
{
    public bool IsSocket => ChildId is not null;

    public override string ToString() => IsSocket ? $"{HostName} › {Name}" : Name;
}

/// <summary>What the plugin believes an outlet is doing. Null means not known yet.</summary>
internal sealed record PlugState
{
    public static readonly PlugState Unknown = new();

    public bool? Online { get; init; }
    public bool? On { get; init; }

    /// <summary>The last control or read error, cleared by the next success.</summary>
    public string? Error { get; init; }

    public DateTimeOffset? ReadAt { get; init; }
}

/// <summary>
/// The plugin's model of the plugs. Keys read from it synchronously (cached state only, never the
/// network). Requests to one device go one at a time; a control updates the cached state at once
/// and is confirmed by reading the device back. Devices are read only while one of their keys is
/// on screen, and discovery runs every minute to follow address changes.
/// </summary>
internal sealed class PlugService : IDisposable
{
    private static readonly TimeSpan DiscoverInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RediscoverAfterFailure = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConfirmDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DemandWindow = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ChangedCoalesce = TimeSpan.FromMilliseconds(100);

    private readonly Action<string> _info;
    private readonly Action<string, Exception?> _error;
    private readonly ConcurrentDictionary<string, HostInfo> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PlugState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastControl = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _demand = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextPoll = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _changedLock = new();
    private readonly SemaphoreSlim _discoverLock = new(1, 1);

    private IPlugBackend _backend;
    private IReadOnlyList<IPAddress> _addresses = [];
    private IReadOnlyList<HostInfo> _unusable = [];
    private bool _changedScheduled;
    private DateTimeOffset _nextDiscover = DateTimeOffset.MinValue;
    private Task? _poller;

    public PlugService(IPlugBackend backend, Action<string> info, Action<string, Exception?> error)
    {
        _backend = backend;
        _info = info;
        _error = error;
    }

    /// <summary>Raised (coalesced) whenever plugs or their state changed, so keys can redraw.</summary>
    public event Action? Changed;

    /// <summary>Raised after discovery found devices, so the plugin can cache where they are.</summary>
    public event Action<IReadOnlyList<HostInfo>>? HostsFound;

    public IPlugBackend Backend => _backend;

    /// <summary>How often a device whose key is on screen is read.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public DateTimeOffset? DiscoveredAt { get; private set; }

    /// <summary>Usable devices, by name.</summary>
    public IReadOnlyList<HostInfo> Hosts =>
        _hosts.Values.Where(h => h.Outlets.Count > 0).OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Devices the last discovery found but cannot use (sign-in failed, unsupported model).</summary>
    public IReadOnlyList<HostInfo> Unusable => _unusable;

    public IReadOnlyList<KasaPlug> Plugs => Hosts.SelectMany(h => h.Outlets.Select(o => ToPlug(h, o))).ToList();

    // ---- lifecycle ----

    public void Start() => _poller ??= Task.Run(() => PollLoopAsync(_cts.Token));

    /// <summary>Switches between the LAN and the demo plugs; everything known starts afresh.</summary>
    public void UseBackend(IPlugBackend backend)
    {
        var old = _backend;
        _backend = backend;
        _hosts.Clear();
        _states.Clear();
        _nextPoll.Clear();
        _unusable = [];
        DiscoveredAt = null;
        _nextDiscover = DateTimeOffset.MinValue;
        old.Dispose();
        RaiseChanged();
    }

    /// <summary>Addresses to ask directly as well as broadcasting (VPNs, other subnets).</summary>
    public void UseAddresses(IReadOnlyList<IPAddress> addresses)
    {
        _addresses = addresses;
        _nextDiscover = DateTimeOffset.MinValue;
    }

    /// <summary>Restores devices saved by an earlier run, so keys work before the first discovery.</summary>
    public void LoadCachedHosts(IReadOnlyList<HostInfo> hosts)
    {
        foreach (var host in hosts) _hosts.TryAdd(host.DeviceId, host);
        if (hosts.Count > 0) RaiseChanged();
    }

    // ---- reading ----

    public HostInfo? FindHost(string deviceId) => _hosts.GetValueOrDefault(deviceId);

    public KasaPlug? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        foreach (var host in _hosts.Values)
        {
            var outlet = host.Outlets.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            if (outlet is not null) return ToPlug(host, outlet);
        }
        return null;
    }

    /// <summary>
    /// The outlets a command's Device parameter names: an outlet id, a device id (every socket of a
    /// strip), several joined with "+", or "*" for every outlet. Unknown ids are skipped.
    /// </summary>
    public IReadOnlyList<KasaPlug> Resolve(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return [];
        if (selector.Trim() == DeviceSelector.All) return Plugs;
        var plugs = new List<KasaPlug>();
        foreach (var part in selector.Split(DeviceSelector.Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Find(part) is { } plug) plugs.Add(plug);
            else if (_hosts.TryGetValue(part, out var host)) plugs.AddRange(host.Outlets.Select(o => ToPlug(host, o)));
        }
        return plugs.Distinct().ToList();
    }

    public PlugState GetState(KasaPlug plug) => _states.GetValueOrDefault(plug.Id) ?? PlugState.Unknown;

    public bool? LedOn(string deviceId) => _hosts.GetValueOrDefault(deviceId)?.LedOn;

    /// <summary>Marks a plug's device as on screen, so it is kept up to date. Cheap; call it from every render.</summary>
    public void Touch(KasaPlug plug)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = _demand.GetValueOrDefault(plug.DeviceId);
        _demand[plug.DeviceId] = now;
        // A device that just came on screen is read straight away rather than at the next interval.
        if (now - previous > DemandWindow) _nextPoll[plug.DeviceId] = DateTimeOffset.MinValue;
    }

    /// <summary>Runs discovery now and merges what it found; returns every device that answered.</summary>
    public async Task<IReadOnlyList<HostInfo>> DiscoverAsync(CancellationToken ct)
    {
        await _discoverLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _nextDiscover = DateTimeOffset.UtcNow + DiscoverInterval;
            var found = await _backend.DiscoverAsync(_addresses, ct).ConfigureAwait(false);
            foreach (var host in found.Where(h => h.Problem is null)) Merge(host);
            _unusable = found.Where(h => h.Problem is not null).ToList();
            DiscoveredAt = DateTimeOffset.Now;

            var usable = found.Where(h => h.Problem is null).ToList();
            if (usable.Count > 0) HostsFound?.Invoke(Hosts);
            _info($"{_backend.Name}: {usable.Count} device(s){(usable.Count > 0 ? ": " + string.Join(", ", usable) : "")}" +
                  (_unusable.Count > 0 ? $"; unusable: {string.Join(", ", _unusable.Select(u => $"{u.Address} ({u.Problem})"))}" : ""));
            RaiseChanged();
            return found;
        }
        finally
        {
            _discoverLock.Release();
        }
    }

    /// <summary>Reads one device now and updates its outlets' state.</summary>
    public async Task RefreshHostAsync(string deviceId, CancellationToken ct)
    {
        if (!_hosts.TryGetValue(deviceId, out var host)) return;
        var gate = _locks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var read = await _backend.ReadAsync(host, ct).ConfigureAwait(false);
            if (!string.Equals(read.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                // The address now belongs to another device (DHCP moved them round): find ours again.
                Offline(host, "Moved", DateTimeOffset.UtcNow);
                return;
            }
            Merge(read);
        }
        catch (KasaException ex)
        {
            Offline(host, ex.Failure == KasaFailure.Offline ? null : ex.Message, DateTimeOffset.UtcNow);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- controls (fire and forget; failures land on the outlet's state and in the log) ----

    public void SetPower(KasaPlug plug, bool on)
    {
        Update(plug.Id, s => s with { On = on });
        Control(plug.DeviceId, $"{plug} {(on ? "on" : "off")}", (host, ct) => _backend.SetPowerAsync(host, plug.ChildId, on, ct),
            [plug.Id]);
    }

    public void SetLed(string deviceId, bool on)
    {
        if (!_hosts.TryGetValue(deviceId, out var known)) return;
        _hosts[deviceId] = known with { LedOn = on };
        RaiseChanged();
        Control(deviceId, $"{known.Name} status light {(on ? "on" : "off")}", (host, ct) => _backend.SetLedAsync(host, on, ct), []);
    }

    private void Control(string deviceId, string what, Func<HostInfo, CancellationToken, Task> send, IReadOnlyList<string> plugIds)
    {
        if (!_hosts.TryGetValue(deviceId, out _)) return;
        _inFlight.AddOrUpdate(deviceId, 1, (_, n) => n + 1);
        _ = Task.Run(async () =>
        {
            var gate = _locks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
            try
            {
                await gate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    // Look the host up again: discovery may have moved it while we queued.
                    await send(_hosts[deviceId], _cts.Token).ConfigureAwait(false);
                    foreach (var id in plugIds) Update(id, s => s with { Online = true, Error = null });
                }
                finally
                {
                    _lastControl[deviceId] = DateTimeOffset.UtcNow;
                    gate.Release();
                }
            }
            catch (KasaException ex)
            {
                _error($"{what} failed: {ex.Message}", null);
                foreach (var id in plugIds) Update(id, s => s with { Error = ex.Failure == KasaFailure.Offline ? "Offline" : ex.Message });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _error($"{what} failed", ex);
            }
            finally
            {
                _inFlight.AddOrUpdate(deviceId, 0, (_, n) => Math.Max(0, n - 1));
            }
            await ConfirmAsync(deviceId).ConfigureAwait(false);
        });
    }

    /// <summary>Reads the device back once its controls have gone quiet, to confirm or correct the keys.</summary>
    private async Task ConfirmAsync(string deviceId)
    {
        try
        {
            await Task.Delay(ConfirmDelay, _cts.Token).ConfigureAwait(false);
            if (_inFlight.GetValueOrDefault(deviceId) > 0) return; // the last control's own confirmation follows
            _nextPoll[deviceId] = DateTimeOffset.UtcNow + PollInterval;
            await RefreshHostAsync(deviceId, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private bool IsBusy(string deviceId) =>
        _inFlight.GetValueOrDefault(deviceId) > 0 || DateTimeOffset.UtcNow - _lastControl.GetValueOrDefault(deviceId) < Settle;

    // ---- polling ----

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= _nextDiscover) await DiscoverQuietlyAsync(ct).ConfigureAwait(false);

                foreach (var host in _hosts.Values)
                {
                    if (now - _demand.GetValueOrDefault(host.DeviceId) > DemandWindow) continue;
                    if (now < _nextPoll.GetValueOrDefault(host.DeviceId) || IsBusy(host.DeviceId)) continue;
                    _nextPoll[host.DeviceId] = now + PollInterval;
                    // Reads run side by side: one slow (offline) device must not hold up the others.
                    _ = RefreshHostAsync(host.DeviceId, ct);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task DiscoverQuietlyAsync(CancellationToken ct)
    {
        try
        {
            await DiscoverAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error($"{_backend.Name}: discovery failed", ex);
        }
    }

    // ---- state ----

    /// <summary>Applies a read (or a discovery answer): topology, address and state, unless controls are in flight.</summary>
    private void Merge(HostInfo read)
    {
        var busy = IsBusy(read.DeviceId);
        _hosts.AddOrUpdate(read.DeviceId, read, (_, old) => busy ? read with { LedOn = old.LedOn } : read);
        var at = DateTimeOffset.UtcNow;
        foreach (var outlet in read.Outlets)
        {
            _states.AddOrUpdate(outlet.Id,
                _ => new PlugState { Online = true, On = outlet.On, ReadAt = at },
                (_, old) => busy
                    ? old with { Online = true }
                    : new PlugState { Online = true, On = outlet.On ?? old.On, ReadAt = at });
        }
        RaiseChanged();
    }

    private void Offline(HostInfo host, string? error, DateTimeOffset now)
    {
        foreach (var outlet in host.Outlets)
        {
            Update(outlet.Id, s => error is null ? s with { Online = false, Error = null } : s with { Error = error });
        }
        // Perhaps it has a new address: look again soon rather than in a minute.
        if (DiscoveredAt is null || _nextDiscover - now > RediscoverAfterFailure) _nextDiscover = now + RediscoverAfterFailure;
    }

    private void Update(string plugId, Func<PlugState, PlugState> change)
    {
        _states.AddOrUpdate(plugId, _ => change(PlugState.Unknown), (_, old) => change(old));
        RaiseChanged();
    }

    private static KasaPlug ToPlug(HostInfo host, OutletInfo outlet) =>
        new(outlet.Id, host.DeviceId, outlet.ChildId, outlet.Name, host.Model, host.Name);

    private void RaiseChanged()
    {
        lock (_changedLock)
        {
            if (_changedScheduled) return;
            _changedScheduled = true;
        }
        _ = Task.Delay(ChangedCoalesce).ContinueWith(_ =>
        {
            lock (_changedLock) _changedScheduled = false;
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _error("Refreshing keys failed", ex);
            }
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _backend.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Values of the Device parameter that are not a single id.</summary>
internal static class DeviceSelector
{
    public const string All = "*";
    public const char Separator = '+';
}
