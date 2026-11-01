namespace KasaMonitor.Core.Models;

/// <summary>Channel identity of a plug (MAC + measured channel), shared by the UI and /metrics.</summary>
public static class PlugChannel
{
    /// <summary>"d885ac0b743c" for channel 0, "d885ac0b743c-1" for channel 1 (the <c>mac</c> metric label).</summary>
    public static string MacLabel(string mac, int channel) => channel == 0 ? mac : $"{mac}-{channel}";
}
