namespace KasaMonitor.Core.Models;

/// <summary>TP-Link cloud account credentials used to derive the KLAP auth hash. Never logged.</summary>
public sealed record TapoCredentials(string Username, string Password)
{
    public override string ToString() => $"TapoCredentials({Username}, ***)";
}
