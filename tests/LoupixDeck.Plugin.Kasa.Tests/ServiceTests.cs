using LoupixDeck.Plugin.Kasa.Kasa;
using Xunit;

namespace LoupixDeck.Plugin.Kasa.Tests;

public class PlugServiceTests
{
    private static async Task<PlugService> StartedAsync()
    {
        var service = new PlugService(new DemoBackend(), _ => { }, (_, _) => { });
        await service.DiscoverAsync(CancellationToken.None);
        return service;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Lists_every_outlet()
    {
        using var service = await StartedAsync();

        Assert.Equal(2, service.Hosts.Count);
        Assert.Equal(4, service.Plugs.Count);
        Assert.Equal(3, service.Plugs.Count(p => p.IsSocket));
    }

    [Fact]
    public async Task Resolves_ids_strips_groups_and_all()
    {
        using var service = await StartedAsync();
        var strip = service.Hosts.Single(h => h.IsStrip);
        var lamp = service.Plugs.Single(p => !p.IsSocket);

        Assert.Equal(3, service.Resolve(strip.DeviceId).Count);
        Assert.Equal([lamp], service.Resolve(lamp.Id));
        Assert.Equal(2, service.Resolve($"{lamp.Id}+{strip.Outlets[1].Id}").Count);
        Assert.Equal(4, service.Resolve("*").Count);
        Assert.Empty(service.Resolve("nothing"));
    }

    [Fact]
    public async Task A_control_shows_at_once_and_reaches_the_plug()
    {
        using var service = await StartedAsync();
        var iron = service.Plugs.Single(p => p.Name == "Soldering iron");
        Assert.False(service.GetState(iron).On);

        service.SetPower(iron, true);
        Assert.True(service.GetState(iron).On);

        var host = service.FindHost(iron.DeviceId)!;
        await Until(() => service.Backend.ReadAsync(host, CancellationToken.None).Result.Outlets.Single(o => o.Id == iron.Id).On == true);
        // The read-back confirms rather than undoes it.
        await Task.Delay(700);
        Assert.True(service.GetState(iron).On);
        Assert.Null(service.GetState(iron).Error);
    }

    [Fact]
    public async Task The_status_light_switches()
    {
        using var service = await StartedAsync();
        var strip = service.Hosts.Single(h => h.IsStrip);

        service.SetLed(strip.DeviceId, false);
        Assert.False(service.LedOn(strip.DeviceId));
        await Until(() => service.Backend.ReadAsync(strip, CancellationToken.None).Result.LedOn == false);
    }

    [Fact]
    public async Task Cached_devices_work_before_discovery()
    {
        using var service = new PlugService(new DemoBackend(), _ => { }, (_, _) => { });
        var cached = (await new DemoBackend().DiscoverAsync([], CancellationToken.None))
            .Select(h => HostInfo.FromCache(h.ToCache())!).ToList();

        service.LoadCachedHosts(cached);

        Assert.Equal(4, service.Plugs.Count);
        Assert.Null(service.GetState(service.Plugs[0]).On);
    }
}
