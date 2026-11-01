namespace KasaMonitor.Core.Models;

public static class PlugModelName
{
    /// <summary>Short model for metric labels: "KP115(EU)" -> "KP115", "P115" -> "P115".</summary>
    public static string Short(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";
        var idx = model.IndexOf('(');
        return (idx > 0 ? model[..idx] : model).Trim();
    }
}
