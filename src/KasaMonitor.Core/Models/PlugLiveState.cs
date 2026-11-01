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
    int Channel = 0
)
{
    /// <summary>Value of the <c>mac</c> metric label: the MAC for channel 0, "&lt;mac&gt;-&lt;channel&gt;" otherwise.</summary>
    public string MacLabel => PlugChannel.MacLabel(Mac, Channel);
}
