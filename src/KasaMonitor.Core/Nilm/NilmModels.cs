namespace KasaMonitor.Core.Nilm;

/// <summary>Where the label of a detected event comes from (column <c>MeterEvents.Source</c>).</summary>
public enum EventLabelSource
{
    /// <summary>Not labelled: the event only belongs to its DBSCAN signature (if any).</summary>
    None = 0,

    /// <summary>A plug's level changed by the same ΔP around the event (<see cref="PlugEventMatcher"/>): label = the plug's alias.</summary>
    AutoPlug = 1,

    /// <summary>Labelled by the user on /consumatori ("a fost boilerul").</summary>
    User = 2,

    /// <summary>Predicted by the ML.NET classifier with probability ≥ 0.7.</summary>
    Model = 3,

    /// <summary>
    /// Moved by its run's duration (<see cref="DurationBands"/>): never moved again by the re-clustering, counted in the
    /// signature's centre, not an example of the classifier (ML.NET has no duration among its features).
    /// </summary>
    DurationRule = 4,
}

/// <summary>How the name of a signature was decided (column <c>ApplianceSignatures.LabelSource</c>).</summary>
public enum SignatureLabelSource
{
    /// <summary>Found by clustering, named "Necunoscut #n".</summary>
    Cluster = 0,

    /// <summary>≥ 60 % of its events were matched to the same plug: confirmed by the plug, never added to "rest of the house".</summary>
    Plug = 1,

    /// <summary>Renamed, merged or labelled by the user: kept as it is by the nightly re-clustering.</summary>
    User = 2,
}

/// <summary>Codes and Romanian names of the label sources (single owner: UI, metrics, docs).</summary>
public static class NilmSources
{
    public static string Code(EventLabelSource s) => s switch
    {
        EventLabelSource.AutoPlug => "auto-plug",
        EventLabelSource.User => "user",
        EventLabelSource.Model => "model",
        EventLabelSource.DurationRule => "duration",
        _ => "none",
    };

    public static string Name(EventLabelSource s) => s switch
    {
        EventLabelSource.AutoPlug => "din priză",
        EventLabelSource.User => "utilizator",
        EventLabelSource.Model => "model ML",
        EventLabelSource.DurationRule => "după durată",
        _ => "—",
    };

    /// <summary>Value of the <c>source</c> label of <c>kasa_appliance_power_w</c>: cluster | plug | user.</summary>
    public static string Code(SignatureLabelSource s) => s switch
    {
        SignatureLabelSource.Plug => "plug",
        SignatureLabelSource.User => "user",
        _ => "cluster",
    };

    public static string Name(SignatureLabelSource s) => s switch
    {
        SignatureLabelSource.Plug => "confirmat din priză",
        SignatureLabelSource.User => "utilizator",
        _ => "grupare automată",
    };

    public static EventLabelSource EventFromInt(int v) => Enum.IsDefined(typeof(EventLabelSource), v) ? (EventLabelSource)v : EventLabelSource.None;
    public static SignatureLabelSource SignatureFromInt(int v) => Enum.IsDefined(typeof(SignatureLabelSource), v) ? (SignatureLabelSource)v : SignatureLabelSource.Cluster;
}

/// <summary>
/// Whether a plug's changes are used to label events and to train the classifier (column <c>Plugs.NilmIgnored</c>).
/// A plug that matches more than <see cref="PlugNilmStates.MaxMatchesPerHour"/> events in an hour (a variable load such
/// as a UPS) is ignored automatically; the user can undo that (<see cref="AlwaysUse"/>) or ignore a plug by hand.
/// </summary>
public enum PlugNilmState
{
    /// <summary>Used; ignored automatically when it turns out to be noisy.</summary>
    Auto = 0,

    /// <summary>Ignored automatically (too many matches per hour).</summary>
    IgnoredAuto = 1,

    /// <summary>Ignored by the user.</summary>
    IgnoredManual = 2,

    /// <summary>Used, never ignored automatically (the user undid an automatic exclusion).</summary>
    AlwaysUse = 3,
}

public static class PlugNilmStates
{
    /// <summary>More matched events than this in one hour mark a plug as noisy (ignored automatically).</summary>
    public const int MaxMatchesPerHour = 20;

    public static bool IsIgnored(PlugNilmState s) => s is PlugNilmState.IgnoredAuto or PlugNilmState.IgnoredManual;

    public static PlugNilmState FromInt(int v) => Enum.IsDefined(typeof(PlugNilmState), v) ? (PlugNilmState)v : PlugNilmState.Auto;

    /// <summary>Romanian badge text (null when the plug is used).</summary>
    public static string? Badge(PlugNilmState s) => s switch
    {
        PlugNilmState.IgnoredAuto => "exclusă automat din învățare (zgomotoasă)",
        PlugNilmState.IgnoredManual => "exclusă din învățare",
        _ => null,
    };

    /// <summary>The badge of a plug: one with a single appliance labels its events in any state, so it never says „exclusă”.</summary>
    public static string? Badge(PlugNilmState s, bool singleAppliance) =>
        singleAppliance && IsIgnored(s) ? "etichetează, nu antrenează modelul" : Badge(s);
}

/// <summary>How a run ended (column <c>ApplianceRuns.EndReason</c>).</summary>
public enum RunEndReason
{
    /// <summary>The matching switch-off of the same signature.</summary>
    Paired = 0,

    /// <summary>Another switch-off brought the house to (or below) its level before the switch-on: the appliance must be off.</summary>
    HouseBelowLevel = 1,

    /// <summary>No switch-off within <see cref="ApplianceRunBuilder.MaxOpen"/>: closed as uncertain, energy only up to that limit.</summary>
    Timeout = 2,
}

/// <summary>Appliance categories of /consumatori: key (stored, metric label), Romanian name, Bootstrap icon.</summary>
public static class ApplianceCategories
{
    public sealed record Category(string Key, string Name, string Icon);

    public const string Other = "other";

    public static readonly IReadOnlyList<Category> All = new Category[]
    {
        new("fridge", "frigider", "bi-snow"),
        new("boiler", "boiler", "bi-droplet-half"),
        new("cooking", "plită / cuptor", "bi-fire"),
        new("washer", "mașină de spălat", "bi-water"),
        new("hvac", "AC / pompă de căldură", "bi-fan"),
        new("lighting", "iluminat", "bi-lightbulb"),
        new("electronics", "electronică", "bi-tv"),
        new(Other, "altele", "bi-question-circle"),
    };

    public static Category Get(string? key) => All.FirstOrDefault(c => c.Key == key) ?? All[^1];
    public static string Normalize(string? key) => Get(key).Key;
}

/// <summary>
/// When the transient ratio of a switch-on is a real measurement. The rule: <b>known</b> when the step reached its new
/// level inside one sample (<see cref="MeterEvent.RiseSeconds"/> = 0) <b>or</b> the ratio is above
/// <see cref="MinInrushRatio"/>; <b>unknown</b> only when the step took ≥ 1 s (<see cref="MaxRiseSeconds"/>) and the ratio
/// is ≤ 1.5. Why: a step that falls between two 1-second samples, or a transition started by a small wobble before the
/// real step, can only make the measured peak <i>smaller</i> (ratio ≈ 0–1 instead of the true value); a peak above 1.5
/// can only be real (a motor's inrush, which also makes the rise last 2 s or more). The stored value stays as measured;
/// only its use changes: an unknown transient is never compared (clustering, matching, merging) and never enters a
/// signature's mean.
/// </summary>
public static class TransientRule
{
    /// <summary>A switch-on that took this many seconds or more to reach its new level may have missed its peak.</summary>
    public const double MaxRiseSeconds = 1;

    /// <summary>A ratio above this is a real inrush peak, whatever the rise.</summary>
    public const double MinInrushRatio = 1.5;

    public static bool IsKnown(double riseSeconds, double ratio) => riseSeconds < MaxRiseSeconds || ratio > MinInrushRatio;
}

/// <summary>
/// One detected step of the main meter (table <c>MeterEvents</c>): a change between two steady states of the house
/// power. <see cref="DeltaP"/> is signed (+ = something turned on); <see cref="DeltaQ"/> is the change of reactive power
/// |Q| = √(S² − P²); <see cref="TransientRatio"/> = peak in the first 2 s / steady ΔP (motors 3–8, resistive ≈ 1);
/// <see cref="RiseSeconds"/> = seconds from the first changed second to the new steady state;
/// <see cref="PowerBeforeW"/> = house power in the 5 s before.
/// </summary>
public sealed record MeterEvent(
    long Id,
    Guid PlugId,
    DateTimeOffset TsUtc,
    double DeltaP,
    double DeltaQ,
    double DeltaPf,
    double TransientRatio,
    double RiseSeconds,
    double PowerBeforeW,
    long? SignatureId = null,
    string? Label = null,
    EventLabelSource Source = EventLabelSource.None,
    Guid? LabelPlugId = null)
{
    public bool IsOn => DeltaP > 0;

    /// <summary>A switch-on whose transient ratio is a real measurement (<see cref="TransientRule"/>).</summary>
    public bool TransientKnown => IsOn && TransientRule.IsKnown(RiseSeconds, TransientRatio);
}

/// <summary>
/// A group of similar events = one component of an appliance (table <c>ApplianceSignatures</c>). Means are over the
/// events' magnitudes: <see cref="MeanDeltaP"/> = mean |ΔP|, <see cref="MeanDeltaQ"/> = mean ΔQ seen from the switch-on
/// side, <see cref="MeanTransient"/> = mean transient ratio of the switch-ons whose transient is known
/// (<see cref="TransientRule"/>; 0 = none known). <see cref="MinRunSeconds"/> and <see cref="MaxRunSeconds"/> = its duration
/// band (<see cref="DurationBands"/>; null = no limit); <see cref="SplitFromId"/> = the signature it was split from by duration.
/// </summary>
public sealed record ApplianceSignature(
    long Id,
    string Name,
    string Icon,
    string Category,
    double MeanDeltaP,
    double MeanDeltaQ,
    double MeanTransient,
    int Count,
    DateTimeOffset? LastSeenUtc,
    SignatureLabelSource LabelSource,
    Guid? PlugId,
    bool Hidden,
    int? MinRunSeconds = null,
    int? MaxRunSeconds = null,
    long? SplitFromId = null)
{
    /// <summary>
    /// Measured by a plug (<see cref="PlugId"/>, kept when the user renames it): its energy is already in Σ plugs, so it is
    /// never counted as a detected appliance (cards, detected energy, <c>kasa_appliance_*</c>) nor added to the rest of the house.
    /// </summary>
    public bool ConfirmedByPlug => PlugId is not null;

    /// <summary>Labelled by the user or linked to a plug (never reused by the clustering for another group).</summary>
    public bool IsLabelled => LabelSource == SignatureLabelSource.User || PlugId is not null;

    /// <summary>
    /// Labelled (<see cref="IsLabelled"/>) and not hidden: an anchor of the classification. A new event tries these first,
    /// with a wider tolerance (<see cref="SignatureClusterer.AnchorScale"/>), and the nightly re-clustering gives them the
    /// events within that radius before clustering the rest. A hidden signature (the user chose to ignore it) attracts
    /// nothing.
    /// </summary>
    public bool IsAnchor => IsLabelled && !Hidden;
}

/// <summary>
/// One run of an appliance (table <c>ApplianceRuns</c>): a switch-on and how it ended (<see cref="EndReason"/>). Open while
/// <see cref="EndUtc"/> is null; <see cref="EstimatedWh"/> is set when it closes. <see cref="HouseBeforeW"/> = house power
/// before the switch-on.
/// </summary>
public sealed record ApplianceRun(
    long Id,
    long SignatureId,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    double PowerW,
    double? EstimatedWh,
    long StartEventId,
    long? EndEventId,
    double HouseBeforeW = 0,
    RunEndReason EndReason = RunEndReason.Paired)
{
    public bool IsOpen => EndUtc is null;
}
