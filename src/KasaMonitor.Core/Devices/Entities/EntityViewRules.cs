namespace KasaMonitor.Core.Devices.Entities;

/// <summary>How the generated page shows an entity (0.11, audit §5).</summary>
public enum EntityView
{
    /// <summary>A numeric measurement: big value + unit, chart of min / mean / max per bucket.</summary>
    Chart,
    /// <summary>A counter (<c>total</c> / <c>total_increasing</c>): its value and the consumption per period, never a cost.</summary>
    Counter,
    /// <summary>A number without a state class: value + last change; chart only when ticked „Istoric”.</summary>
    Value,
    /// <summary>Text, enum, timestamp, duration: a formatted text card (changes timeline when ticked „Istoric”).</summary>
    Text,
    /// <summary>On/off: a badge with the class's Romanian text (+ timeline when ticked „Istoric”).</summary>
    Binary,
    /// <summary>Switch / number / select / button: shown with its value and „comandă (neactivată)” (R8).</summary>
    Command,
    /// <summary>A component the app does not accept: listed at the end with its reason.</summary>
    Unsupported,
}

/// <summary>Where an entity goes on the page.</summary>
public enum EntitySection
{
    Main,
    /// <summary>„Diagnostic”, folded by default.</summary>
    Diagnostic,
    /// <summary>Hidden by the user (preference „arată” off).</summary>
    Hidden,
    Unsupported,
}

/// <summary>The decision for one entity: view, section, whether its history is shown, whether its command can be used.</summary>
public sealed record EntityViewDecision(EntityView View, EntitySection Section, bool History, bool CommandEnabled);

/// <summary>
/// The defaults of the per-entity preferences (<see cref="EntityPrefs"/>, null = default): history for measurements and
/// counters, metrics for the main measured classes of primary entities (cardinality rule, audit §4), visible unless the
/// user hid it.
/// </summary>
public static class EntityDefaults
{
    /// <summary>The kinds of a number that has a series worth keeping.</summary>
    public static bool HasSeries(DeviceEntity e) =>
        e.Kind == EntityKind.Sensor && e.DataType == EntityDataType.Number
        && e.StateClass is StateClass.Measurement or StateClass.Total or StateClass.TotalIncreasing or StateClass.MeasurementAngle;

    /// <summary>History by default for the measurements and counters of the main entities (not the diagnostics: link quality, Wi-Fi …).</summary>
    public static bool InHistory(DeviceEntity e) => e.Kind != EntityKind.Unsupported && e.Problem is null && e.Category == EntityCategory.Primary && HasSeries(e);

    public static bool InMetrics(DeviceEntity e) =>
        e.Category == EntityCategory.Primary && e.Problem is null && e.DataType == EntityDataType.Number
        && e.Kind is EntityKind.Sensor && DeviceClasses.IsMainMeasurement(e.Class);

    public static bool EffectiveInHistory(DeviceEntity e, EntityPrefs? p) => p?.InHistory ?? InHistory(e);

    public static bool EffectiveInMetrics(DeviceEntity e, EntityPrefs? p) => p?.InMetrics ?? InMetrics(e);

    public static bool EffectiveVisible(EntityPrefs? p) => p?.Visible ?? true;

    /// <summary>The name shown: the user's, else the entity's.</summary>
    public static string Name(DeviceEntity e, EntityPrefs? p) => string.IsNullOrWhiteSpace(p?.NameOverride) ? e.Name : p!.NameOverride!.Trim();

    /// <summary>The decimals shown: the user's, the entity's, else the class's default.</summary>
    public static int Decimals(DeviceEntity e, EntityPrefs? p) =>
        Math.Clamp(p?.PrecisionOverride ?? e.Precision ?? DeviceClasses.DefaultDecimals(p?.ClassOverride ?? e.Class), 0, 6);

    /// <summary>The unit shown: the user's, else the canonical one of the description.</summary>
    public static string? Unit(DeviceEntity e, EntityPrefs? p) => string.IsNullOrWhiteSpace(p?.UnitOverride) ? e.Unit : p!.UnitOverride!.Trim();

    /// <summary>The class in effect (the user's correction wins).</summary>
    public static DeviceClass Class(DeviceEntity e, EntityPrefs? p) => p?.ClassOverride ?? e.Class;
}

/// <summary>
/// The rendering rules of the generated device page (0.11, audit §5): <c>Kind × StateClass × DeviceClass × EntityCategory</c>
/// (the data type alone does not decide: a number can be a measurement, a counter, a code or a diagnostic). Pure; the page
/// only draws what this returns.
/// </summary>
public static class EntityViewRules
{
    /// <summary>R8: commands from KasaMonitor are not enabled in 0.11, whatever the preference says.</summary>
    public const bool CommandsAllowed = false;

    public static EntityViewDecision For(DeviceEntity e, EntityPrefs? p)
    {
        if (e.Kind == EntityKind.Unsupported) return new(EntityView.Unsupported, EntitySection.Unsupported, false, false);

        var view = ViewOf(e);
        var section = !EntityDefaults.EffectiveVisible(p) ? EntitySection.Hidden
            : e.Category != EntityCategory.Primary && p?.Visible != true ? EntitySection.Diagnostic
            : EntitySection.Main;
        var history = view switch
        {
            EntityView.Chart or EntityView.Counter => EntityDefaults.EffectiveInHistory(e, p),
            EntityView.Value or EntityView.Text or EntityView.Binary => p?.InHistory == true,
            _ => false,
        };
        return new(view, section, history, view == EntityView.Command && CommandsAllowed && p?.CommandsEnabled == true);
    }

    private static EntityView ViewOf(DeviceEntity e) => e.Kind switch
    {
        EntityKind.Binary => EntityView.Binary,
        EntityKind.Switch or EntityKind.Number or EntityKind.Select or EntityKind.Button => EntityView.Command,
        EntityKind.Text => EntityView.Text,
        _ when e.DataType != EntityDataType.Number => EntityView.Text,
        _ when e.StateClass is StateClass.Total or StateClass.TotalIncreasing => EntityView.Counter,
        _ when e.StateClass is StateClass.Measurement or StateClass.MeasurementAngle => EntityView.Chart,
        _ => EntityView.Value,
    };

    /// <summary>Within one view, the order of the classes: power and climate first, electrical details after, the rest last.</summary>
    public static int ClassRank(DeviceClass c) => c switch
    {
        DeviceClass.Power => 0,
        DeviceClass.Energy => 1,
        DeviceClass.Temperature => 2,
        DeviceClass.Humidity => 3,
        DeviceClass.CarbonDioxide or DeviceClass.Pm25 or DeviceClass.Pm10 or DeviceClass.Pm1 or DeviceClass.VolatileOrganicCompounds => 4,
        DeviceClass.Pressure or DeviceClass.Illuminance => 5,
        DeviceClass.Voltage => 6,
        DeviceClass.Current => 7,
        DeviceClass.ApparentPower or DeviceClass.ReactivePower or DeviceClass.PowerFactor or DeviceClass.Frequency => 8,
        DeviceClass.None => 20,
        _ => 10,
    };

    /// <summary>The order of the cards: measurements, counters, values, binaries, texts, commands.</summary>
    public static int Order(EntityView v) => v switch
    {
        EntityView.Chart => 0,
        EntityView.Counter => 1,
        EntityView.Value => 2,
        EntityView.Binary => 3,
        EntityView.Text => 4,
        EntityView.Command => 5,
        _ => 6,
    };
}
