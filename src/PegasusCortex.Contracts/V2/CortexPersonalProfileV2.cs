namespace Pegasus.Cortex.Contracts.V2;

/// <summary>
/// Optional personal-layer availability, not an authentication principal or a
/// database selector. A loaded generation changes whenever the layer recovers.
/// </summary>
public sealed record CortexPersonalProfileV2(
    string State,
    string? Profile = null,
    string? Generation = null,
    string? IssueCode = null)
{
    /// <summary>
    /// The optional profile pipe reports availability only. It never establishes
    /// that semantic data is missing and never selects or remaps a database.
    /// </summary>
    public bool IndicatesMissingData => false;

    /// <summary>A profile availability transition never requires database remapping.</summary>
    public bool RemapRequired => false;

    /// <summary>
    /// True when the signed-in host or its UI is expected to recover without
    /// changing profile identity or semantic storage.
    /// </summary>
    public bool Retryable => State == "not_loaded" &&
        IssueCode is "profile_pipe_unavailable" or "ui_unavailable" or "personal_profile_unavailable";
}
