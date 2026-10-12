using LoupixDeck.Plugin.Kasa.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Kasa.Commands;

/// <summary>
/// Switches the status light on a plug or strip (handy where a glowing plug is a nuisance) and
/// shows whether it is lit. A strip has one light for all its sockets. Action: toggle, on or off.
/// </summary>
internal sealed class LedCommand(KasaPlugin plugin) : KasaCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Kasa.Led";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Status light", Glyphs.Led,
        "Switches the status light on a plug or power strip and shows whether it is lit. Action: toggle, on or off.");

    public override Task Execute(CommandContext ctx)
    {
        var devices = Targets(ctx).Select(p => p.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (devices.Count == 0)
        {
            Hint(ctx, Plugin.NoPlugsReason);
            return Task.CompletedTask;
        }

        var on = Action(ctx) switch
        {
            "on" => true,
            "off" => false,
            _ => !devices.Any(id => Plugs.LedOn(id) == true)
        };
        foreach (var id in devices) Plugs.SetLed(id, on);
        return Task.CompletedTask;
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var plugs = Targets(ctx);
        if (DrawUnavailable(canvas, plugs)) return true;

        var devices = plugs.Select(p => p.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lit = devices.Select(Plugs.LedOn).ToList();
        bool? on = lit.Any(l => l == true) ? true : lit.All(l => l == false) ? false : null;
        var caption = devices.Count == 1 ? Plugs.FindHost(devices[0])?.Name ?? Caption(ctx, plugs) : $"{devices.Count} lights";
        var action = Action(ctx);
        PlugKey.DrawLed(canvas, caption, on, action == "toggle" ? null : action.ToUpperInvariant());
        return true;
    }
}
