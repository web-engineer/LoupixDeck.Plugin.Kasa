using LoupixDeck.Plugin.Kasa.Kasa;
using LoupixDeck.Plugin.Kasa.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Kasa.Commands;

/// <summary>
/// The main plug key: switches an outlet (or a whole strip, or a group) and shows whether it is on.
/// Action: <c>toggle</c> (default), <c>on</c> or <c>off</c>.
/// </summary>
internal sealed class PowerCommand(KasaPlugin plugin) : KasaCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Kasa.Power";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Plug on/off", Glyphs.PowerPlug,
        "Switches a plug, a socket of a power strip, or a whole strip, and shows whether it is on. Action: toggle, on or off.");

    public override async Task Execute(CommandContext ctx)
    {
        var plugs = Targets(ctx);
        if (plugs.Count == 0)
        {
            Hint(ctx, Plugin.NoPlugsReason);
            ctx.Host.Logger.Warn($"{Name}: no plug matches \"{Param(ctx, 0)}\"");
            return;
        }

        var action = Action(ctx);
        if (action == "toggle") await ReadUnknownAsync(plugs).ConfigureAwait(false);
        // A group toggles together: off if any outlet is on, otherwise on.
        var on = action switch
        {
            "on" => true,
            "off" => false,
            _ => !plugs.Any(p => Plugs.GetState(p).On == true)
        };
        foreach (var plug in plugs) Plugs.SetPower(plug, on);
    }

    /// <summary>A toggle needs to know where it starts; read devices whose state is not known yet (briefly).</summary>
    private async Task ReadUnknownAsync(IReadOnlyList<KasaPlug> plugs)
    {
        var unknown = plugs.Where(p => Plugs.GetState(p).On is null).Select(p => p.DeviceId).Distinct().ToList();
        if (unknown.Count == 0) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await Task.WhenAll(unknown.Select(id => Plugs.RefreshHostAsync(id, cts.Token))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Toggle from what is known.
        }
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var plugs = Targets(ctx);
        if (DrawUnavailable(canvas, plugs)) return true;

        var action = Action(ctx);
        PlugKey.DrawPlug(canvas, Caption(ctx, plugs), Combined(plugs), action == "toggle" ? null : action.ToUpperInvariant());
        return true;
    }

    /// <summary>One state for a group: on if any outlet is on, offline only if all are.</summary>
    private PlugState Combined(IReadOnlyList<KasaPlug> plugs)
    {
        var states = plugs.Select(Plugs.GetState).ToList();
        if (states.Count == 1) return states[0];
        return new PlugState
        {
            Online = states.All(s => s.Online == false) ? false : states.Any(s => s.Online == true) ? true : null,
            On = states.Any(s => s.On == true) ? true : states.All(s => s.On == false) ? false : null,
            Error = states.Select(s => s.Error).FirstOrDefault(e => e is not null)
        };
    }
}
