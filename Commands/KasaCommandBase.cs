using LoupixDeck.Plugin.Kasa.Kasa;
using LoupixDeck.Plugin.Kasa.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Kasa.Commands;

/// <summary>
/// Shared plumbing for Kasa commands. Every command takes the outlet(s) as its first parameter,
/// <c>Device</c>: an outlet id, a strip's device id (all its sockets), several joined with "+", or
/// "*" for every outlet. The device menu fills it in, so users never type an id.
/// </summary>
internal abstract class KasaCommandBase(KasaPlugin plugin) : IPluginCommand
{
    public const string DeviceParameter = "Device";
    public const string ActionParameter = "Action";

    protected KasaPlugin Plugin { get; } = plugin;

    protected PlugService Plugs => Plugin.Plugs;

    public abstract CommandDescriptor Descriptor { get; }

    public virtual ButtonTargets SupportedTargets => ButtonTargets.All;

    public abstract Task Execute(CommandContext ctx);

    /// <summary>How often keys redraw from the cache; state changes also redraw them at once.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    protected static string? Param(CommandContext ctx, int index) =>
        ctx.Parameters.Length > index && !string.IsNullOrWhiteSpace(ctx.Parameters[index]) ? ctx.Parameters[index].Trim() : null;

    /// <summary>toggle (default), on or off.</summary>
    protected static string Action(CommandContext ctx) => Param(ctx, 1)?.ToLowerInvariant() switch
    {
        "on" => "on",
        "off" => "off",
        _ => "toggle"
    };

    /// <summary>The outlets the command acts on, marked as on screen so their state stays fresh.</summary>
    protected IReadOnlyList<KasaPlug> Targets(CommandContext ctx)
    {
        var plugs = Plugs.Resolve(Param(ctx, 0));
        foreach (var plug in plugs) Plugs.Touch(plug);
        return plugs;
    }

    /// <summary>A name for the target: the outlet's name, the strip's name, "All plugs" or "3 plugs".</summary>
    protected string Caption(CommandContext ctx, IReadOnlyList<KasaPlug> plugs)
    {
        var selector = Param(ctx, 0);
        if (selector == DeviceSelector.All) return "All plugs";
        if (plugs.Count == 1) return plugs[0].Name;
        if (selector is not null && Plugs.FindHost(selector) is { } host) return host.Name;
        return $"{plugs.Count} plugs";
    }

    /// <summary>Draws why a key has no plug to show, or returns false when there is one.</summary>
    protected bool DrawUnavailable(IRenderCanvas canvas, IReadOnlyList<KasaPlug> plugs)
    {
        if (plugs.Count > 0) return false;
        PlugKey.DrawMessage(canvas, "power-plug-off-outline", Plugin.NoPlugsReason, PlugKey.Faded);
        return true;
    }

    /// <summary>Short on-key feedback for a touch button; silent elsewhere.</summary>
    protected static void Hint(CommandContext ctx, string text)
    {
        if (ctx.Target == ButtonTargets.TouchButton && ctx.SourceIndex is { } slot)
        {
            ctx.Host.OverlayTouchText(slot, text, TimeSpan.FromSeconds(2));
        }
    }

    protected static CommandDescriptor Describe(string name, string displayName, string icon, string description) => new()
    {
        CommandName = name,
        DisplayName = displayName,
        Group = KasaPlugin.GroupName,
        Icon = icon,
        Description = description,
        ParameterTemplate = $"({{{DeviceParameter}}},{{{ActionParameter}}})",
        Parameters =
        [
            new CommandParameter(DeviceParameter, typeof(string)),
            new CommandParameter(ActionParameter, typeof(string)) { DefaultValue = "toggle" }
        ],
        // Listed per plug through the device menu, which fills in the Device parameter.
        HiddenFromMenu = true,
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };
}

/// <summary>Material Design Icons code points for the command picker.</summary>
internal static class Glyphs
{
    public const string PowerPlug = "\U000F06A5";
    public const string PowerSocket = "\U000F07E8";
    public const string Led = "\U000F032C";
}
