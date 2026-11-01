namespace KasaMonitor.Core.Protocol;

public static class MacFormat
{
    /// <summary>"C0:06:C3:70:2B:FC" / "B0-19-21-C4-C0-28" -> "c006c3702bfc" (the form stored in Plugs.Mac).</summary>
    public static string Normalize(string raw) =>
        new string(raw.Where(c => c != ':' && c != '-').ToArray()).ToLowerInvariant();
}
