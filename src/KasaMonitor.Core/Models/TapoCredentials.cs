namespace KasaMonitor.Core.Models;

/// <summary>TP-Link cloud account credentials used to derive the KLAP auth hash. Never logged.</summary>
public sealed record TapoCredentials(string Username, string Password)
{
    public override string ToString() => $"TapoCredentials({Username}, ***)";
}

/// <summary>Admin password of Shelly Gen2+ devices with authentication enabled (user is always "admin"). Never logged.</summary>
public sealed record ShellyCredentials(string Password)
{
    public override string ToString() => "ShellyCredentials(admin, ***)";
}
