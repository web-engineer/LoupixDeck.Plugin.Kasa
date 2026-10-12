using LoupixDeck.Plugin.Kasa.Kasa;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Kasa.Rendering;

/// <summary>
/// How Kasa keys look: dark keys where a plug that is on glows green, with the outlet's name
/// underneath. Every key draws through here so they read as one set. Drawing uses cached state only.
/// </summary>
internal static class PlugKey
{
    public static readonly PluginColor Background = new(18, 18, 18);
    public static readonly PluginColor Text = new(235, 235, 235);
    public static readonly PluginColor Faded = new(150, 150, 150);
    public static readonly PluginColor Dim = new(70, 70, 70);
    public static readonly PluginColor Problem = new(232, 72, 72);
    public static readonly PluginColor OnColor = new(64, 196, 110);
    public static readonly PluginColor LedColor = new(90, 170, 255);

    /// <summary>An outlet (or group) and whether it is on: the main power key.</summary>
    public static void DrawPlug(IRenderCanvas canvas, string caption, PlugState state, string? badge = null)
    {
        canvas.Clear(Background);
        var (w, h) = (canvas.Width, canvas.Height);
        int cx = w / 2, cy = (int)(h * 0.42), r = (int)(w * 0.27);

        if (state.Online == false)
        {
            canvas.DrawSymbol("lan-disconnect", cx - r, cy - r, r * 2, r * 2, Problem);
            Caption(canvas, caption, "Offline");
            return;
        }

        if (state.On is null)
        {
            canvas.DrawCircle(cx, cy, r, 2, Dim);
            canvas.DrawSymbol("power-plug-outline", cx - r / 2 - 4, cy - r / 2 - 4, r + 8, r + 8, Faded);
            Caption(canvas, caption, state.Error);
            return;
        }

        if (state.On == true)
        {
            canvas.FillCircle(cx, cy, r + 6, OnColor with { A = 60 });
            canvas.FillCircle(cx, cy, r, OnColor);
            canvas.DrawSymbol("power-plug", cx - r / 2 - 2, cy - r / 2 - 2, r + 4, r + 4, new PluginColor(255, 255, 255, 240));
        }
        else
        {
            canvas.DrawCircle(cx, cy, r, 2, Dim);
            canvas.DrawSymbol("power-plug-off-outline", cx - r / 2 - 2, cy - r / 2 - 2, r + 4, r + 4, Faded);
        }

        if (badge is not null) canvas.DrawText(badge, 2, 2, w / 2, 14, Faded, 10, TextHAlign.Left, TextVAlign.Top);
        if (state.Error is not null) canvas.FillCircle(7, 7, 4, Problem);
        Caption(canvas, caption, state.Error);
    }

    /// <summary>A device's status light.</summary>
    public static void DrawLed(IRenderCanvas canvas, string caption, bool? on, string? badge = null)
    {
        canvas.Clear(Background);
        var (w, h) = (canvas.Width, canvas.Height);
        int cx = w / 2, cy = (int)(h * 0.42), r = (int)(w * 0.25);

        if (on == true)
        {
            canvas.FillCircle(cx, cy, r + 5, LedColor with { A = 50 });
            canvas.DrawSymbol("led-on", cx - r, cy - r, r * 2, r * 2, LedColor);
        }
        else
        {
            canvas.DrawSymbol("led-outline", cx - r, cy - r, r * 2, r * 2, on is null ? Dim : Faded);
        }
        if (badge is not null) canvas.DrawText(badge, 2, 2, w / 2, 14, Faded, 10, TextHAlign.Left, TextVAlign.Top);
        Caption(canvas, caption, null);
    }

    /// <summary>A plain message, e.g. "No plugs found" or "Unknown plug".</summary>
    public static void DrawMessage(IRenderCanvas canvas, string symbol, string message, PluginColor color)
    {
        canvas.Clear(Background);
        var (w, h) = (canvas.Width, canvas.Height);
        var s = (int)(w * 0.4);
        canvas.DrawSymbol(symbol, (w - s) / 2, (int)(h * 0.18), s, s, color);
        Caption(canvas, message, null, color);
    }

    /// <summary>The bottom line: the caption, or the problem in red when there is one.</summary>
    private static void Caption(IRenderCanvas canvas, string caption, string? problem, PluginColor? color = null)
    {
        var (w, h) = (canvas.Width, canvas.Height);
        var text = Fit(canvas, problem ?? caption, w - 6, 11);
        canvas.DrawText(text, 3, h - 22, w - 6, 18, problem is null ? color ?? Text : Problem, 11, TextHAlign.Center, TextVAlign.Middle);
    }

    private static string Fit(IRenderCanvas canvas, string text, int width, float size)
    {
        if (canvas.MeasureText(text, size) <= width) return text;
        for (var length = text.Length - 1; length > 1; length--)
        {
            var candidate = text[..length].TrimEnd() + "…";
            if (canvas.MeasureText(candidate, size) <= width) return candidate;
        }
        return text[..1];
    }
}
