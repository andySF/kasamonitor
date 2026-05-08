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
}
