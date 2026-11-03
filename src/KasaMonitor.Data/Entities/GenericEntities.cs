namespace KasaMonitor.Data.Entities;

/// <summary>A generic device (table GenericDevices, 0.11). (Integration, ExternalId) is unique; times are unix seconds; enums as text.</summary>
public sealed class GenericDeviceEntity
{
    public long Id { get; set; }
    public string Integration { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool NameIsCustom { get; set; }
    public string Room { get; set; } = "";
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? ViaDevice { get; set; }
    public string AutoCategory { get; set; } = "";
    public string? CategoryOverride { get; set; }
    public bool IsArchived { get; set; }
    public long FirstSeenUtc { get; set; }
    public long? LastSeenUtc { get; set; }
}

/// <summary>
/// A generic entity (table GenericEntities, 0.11): the description in the common model (enums as text), the raw discovery
/// payload, removed or not, the last value. (Integration, ExternalId = unique_id) is unique.
/// </summary>
public sealed class GenericEntityEntity
{
    public long Id { get; set; }
    public long DeviceId { get; set; }
    public string Integration { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Component { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Class { get; set; } = "";
    public string? ClassRaw { get; set; }
    public string? Unit { get; set; }
    public string? StateClass { get; set; }
    public int? Precision { get; set; }
    public string Category { get; set; } = "";
    public bool Writable { get; set; }
    public string? Problem { get; set; }
    /// <summary>Round 2: the device sent no <c>state_class</c>; <c>measurement</c> was deduced from the class.</summary>
    public bool StateClassInferred { get; set; }
    public string DiscoveryJson { get; set; } = "";
    public bool IsRemoved { get; set; }
    public double? LastNumber { get; set; }
    public string? LastText { get; set; }
    public long? LastTs { get; set; }
}

/// <summary>One value (table GenericReadings, clustered key (EntityId, Ts) — R6: an integer key, millions of rows a year).</summary>
public sealed class GenericReadingEntity
{
    public long EntityId { get; set; }
    public long Ts { get; set; }
    public double? Value { get; set; }
    public string? Text { get; set; }
}

/// <summary>One entity over one UTC hour (table GenericHourly, clustered key (EntityId, HourTs)), kept forever.</summary>
public sealed class GenericHourlyEntity
{
    public long EntityId { get; set; }
    public long HourTs { get; set; }
    public double? Min { get; set; }
    public double? Avg { get; set; }
    public double? Max { get; set; }
    public double? Last { get; set; }
    public int Count { get; set; }
    public double? Delta { get; set; }
}
