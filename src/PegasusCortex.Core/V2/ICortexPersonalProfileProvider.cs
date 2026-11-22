using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Core.V2;

/// <summary>
/// Supplies a current, bounded personal-layer availability snapshot. Implementors
/// must change Generation on unload/recovery and authenticate their own source;
/// Profile is display metadata and never supplies the application actor identity.
/// </summary>
public interface ICortexPersonalProfileProvider
{
    ValueTask<CortexPersonalProfileV2> GetAsync(CancellationToken cancellationToken);
}

public sealed class CortexPersonalProfileException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
