namespace KasaMonitor.Data.Entities;

/// <summary>
/// The user's choices for one entity of the generated device page (table <c>EntityPrefs</c>, 0.11): visible, in the history,
/// in the metrics, a name / precision / class / unit correction and „comenzi permise”. <see cref="Key"/> is the stable
/// entity key (<c>plug:{guid}/power</c>, <c>dev:{id}/{unique_id}</c>); null columns = the default.
/// </summary>
public sealed class EntityPrefEntity
{
    public string Key { get; set; } = "";
    public bool? Visible { get; set; }
    public bool? InHistory { get; set; }
    public bool? InMetrics { get; set; }
    public string? NameOverride { get; set; }
    public int? PrecisionOverride { get; set; }
    /// <summary>The <c>DeviceClass</c> name (text, so a reordered enum never changes a stored value).</summary>
    public string? ClassOverride { get; set; }
    public string? UnitOverride { get; set; }
    public bool CommandsEnabled { get; set; }
}
