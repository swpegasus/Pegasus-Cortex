namespace Pegasus.Cortex.Core.V2;

/// <summary>Metadata remains authoritative even when its exact payload cannot be verified.</summary>
public sealed class CortexPayloadUnavailableException : InvalidOperationException
{
    public const string Code = "cold_payload_unavailable";
    public CortexPayloadUnavailableException(Exception? cause = null)
        : base("The retained cold payload is unavailable or failed verification; no record was recreated or replayed.", cause) { }
}
