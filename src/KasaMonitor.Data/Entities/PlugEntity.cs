namespace KasaMonitor.Data.Entities;

public sealed class PlugEntity
{
    public Guid Id { get; set; }
    public string Mac { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Alias { get; set; } = "";
    public bool AliasIsCustom { get; set; }
    public string Model { get; set; } = "";
    public string HwVer { get; set; } = "";
    public string SwVer { get; set; } = "";
    public string OemId { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public bool IsArchived { get; set; }
    public string? Notes { get; set; }

    /// <summary>legacy | klap | aes | unsupported (KasaMonitor.Core.Models.PlugProtocols). Existing rows migrate to legacy.</summary>
    public string Protocol { get; set; } = "legacy";

    /// <summary>Non-null: the plug answered discovery but cannot be queried; it is listed, never polled.</summary>
    public string? UnsupportedReason { get; set; }

    /// <summary>Measured channel of a multi-channel device (Shelly EM: 0/1); 0 for plugs. Unique together with <see cref="Mac"/>.</summary>
    public int Channel { get; set; }

    /// <summary>KasaMonitor.Core.Models.PlugRole: 0 = plug, 1 = main meter (at most one), 2 = sub-meter.</summary>
    public int Role { get; set; }

    /// <summary>Live stream (1–10 s) for this channel; default true, false for sub-meters.</summary>
    public bool StreamEnabled { get; set; } = true;
    /// <summary><see cref="KasaMonitor.Core.Nilm.PlugNilmState"/>: 0 used, 1 ignored automatically, 2 ignored by the user, 3 always used.</summary>
    public int NilmIgnored { get; set; }

    /// <summary>„Un singur aparat pe priză”: the plug measures exactly one appliance (<see cref="ApplianceName"/>).</summary>
    public bool SingleAppliance { get; set; }

    /// <summary>Name of the single appliance (e.g. „Frigider”); the alias when empty.</summary>
    public string? ApplianceName { get; set; }

    /// <summary>Category key of the single appliance (<see cref="KasaMonitor.Core.Nilm.ApplianceCategories"/>).</summary>
    public string? ApplianceCategory { get; set; }
}
