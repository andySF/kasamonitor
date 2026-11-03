using KasaMonitor.Core.Devices.Entities;

namespace KasaMonitor.Web.Ui;

/// <summary>Formatting of entity values on the generated device page (0.11); the decisions (decimals, class, unit) are <see cref="EntityDefaults"/>'s.</summary>
public static class EntityFormat
{
    /// <summary>The value and its unit apart (the big number of a card); „—” without a value.</summary>
    public static (string Value, string? Unit) Parts(DeviceEntity e, EntityValue? v, EntityPrefs? p)
    {
        if (v is null || !v.HasValue) return ("—", null);
        var cls = EntityDefaults.Class(e, p);
        if (e.DataType == EntityDataType.Boolean && v.Number is { } b) return (DeviceClasses.BinaryText(cls, b != 0), null);
        if (v.Number is not { } n) return (Text(e, v.Text), null);
        if (cls == DeviceClass.Duration) return (UiFormat.DurationRo(n), null);
        var unit = EntityDefaults.Unit(e, p);
        var decimals = EntityDefaults.Decimals(e, p);
        // A counter in Wh reads better in kWh once it passes 10 kWh (the stored unit stays Wh).
        if (unit == "Wh" && Math.Abs(n) >= 10_000) return (HeatingFormat.Number(n / 1000, 2), "kWh");
        return (HeatingFormat.Number(n, decimals), unit);
    }

    /// <summary>One number of the entity (a counter's today / yesterday / month) with its unit, formatted like the value itself.</summary>
    public static string Amount(DeviceEntity e, double? number, EntityPrefs? p) =>
        number is null ? "—" : Value(e, new EntityValue(e.Key, number, null, null), p);

    /// <summary>The value with its unit in one text („22,4 °C”, „deschis”, „—”).</summary>
    public static string Value(DeviceEntity e, EntityValue? v, EntityPrefs? p)
    {
        var (value, unit) = Parts(e, v, p);
        return unit is null || value == "—" ? value : $"{value} {unit}";
    }

    /// <summary>A number of the entity's history (axis, tooltip), in the entity's decimals.</summary>
    public static int Decimals(DeviceEntity e, EntityPrefs? p) => EntityDefaults.Decimals(e, p);

    /// <summary>Whether a binary value needs attention (open, wet, low battery, problem …).</summary>
    public static bool IsAlert(DeviceEntity e, EntityValue? v, EntityPrefs? p) =>
        e.DataType == EntityDataType.Boolean && v?.Number is { } b && DeviceClasses.IsAlert(EntityDefaults.Class(e, p), b != 0);

    /// <summary>Texts: a timestamp shown local, the rest as received.</summary>
    private static string Text(DeviceEntity e, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "—";
        if (e.DataType == EntityDataType.Timestamp && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
            return UiFormat.FullLocal(ts);
        return text;
    }

    /// <summary>The Romanian name of a view, for the preferences table.</summary>
    public static string KindName(DeviceEntity e) => e.Kind switch
    {
        EntityKind.Binary => "stare",
        EntityKind.Switch => "comutator",
        EntityKind.Number => "număr reglabil",
        EntityKind.Select => "listă",
        EntityKind.Button => "buton",
        EntityKind.Text => "text",
        EntityKind.Unsupported => "neacceptat",
        _ => e.StateClass is StateClass.Total or StateClass.TotalIncreasing ? "contor"
            : e.StateClass is not null ? "măsurătoare"
            : e.DataType == EntityDataType.Number ? "valoare" : "text",
    };
}
