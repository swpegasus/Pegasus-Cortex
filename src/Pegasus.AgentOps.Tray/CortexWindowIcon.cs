using System.Runtime.CompilerServices;

namespace Pegasus.AgentOps.Tray;

internal static class CortexWindowIcon
{
    private static readonly ConditionalWeakTable<Form, Icon> Assigned = new();

    internal static void Apply(Form window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.IsDisposed || Assigned.TryGetValue(window, out _)) return;
        using var stream = typeof(CortexWindowIcon).Assembly.GetManifestResourceStream("Pegasus.AgentOps.Tray.Icon")
            ?? throw new InvalidOperationException("The Cortex window icon resource is unavailable.");
        using var source = new Icon(stream);
        var owned = (Icon)source.Clone();
        try
        {
            window.Icon = owned;
            Assigned.Add(window, owned);
            window.Disposed += (_, _) => owned.Dispose();
        }
        catch { owned.Dispose(); throw; }
    }
}
