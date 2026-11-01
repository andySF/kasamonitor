namespace KasaMonitor.Core.Models;

/// <summary>One read-only value shown in the device details card (Dashboard).</summary>
public sealed record DeviceDetailItem(string Section, string Label, string Value);

/// <summary>
/// Settings and state read from the plug on demand (not part of the 10 s polling). <see cref="Errors"/> holds the
/// requests the device rejected or that failed, so the card can show what is missing without failing as a whole.
/// </summary>
public sealed record DeviceDetails(
    DateTimeOffset ReadAtUtc,
    IReadOnlyList<DeviceDetailItem> Items,
    IReadOnlyList<string> Errors)
{
    public static class Sections
    {
        public const string State = "State";
        public const string Timer = "Auto-off / timer";
        public const string Protection = "Protection";
        public const string Network = "Network";
        public const string Device = "Device";
        public const string Time = "Time";
        public const string Schedules = "Schedules";
    }

    public string? Get(string label) => Items.FirstOrDefault(i => i.Label == label)?.Value;
}
