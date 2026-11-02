namespace KasaMonitor.Core.Models;

public sealed record PlugLiveState(
    Guid PlugId,
    string Mac,
    string Alias,
    bool IsOnline,
    EmeterReading? LastReading,
    DateTimeOffset LastSampleUtc,
    string Model = "",
    string Protocol = PlugProtocols.Legacy,
    int Channel = 0,
    PlugRole Role = PlugRole.Plug,
    string? ApplianceName = null
)
{
    /// <summary>
    /// The name shown wherever the plug stands for an appliance (bubbles of the home page): the appliance of a
    /// single-appliance plug (<see cref="Plug.Appliance"/>, carried in <see cref="ApplianceName"/>), else the alias. The
    /// metric labels keep the alias.
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(ApplianceName) ? Alias : ApplianceName;

    /// <summary>Value of the <c>mac</c> metric label: the MAC for channel 0, "&lt;mac&gt;-&lt;channel&gt;" otherwise.</summary>
    public string MacLabel => PlugChannel.MacLabel(Mac, Channel);
}
