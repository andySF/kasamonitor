using System.Globalization;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Web.Ui;

/// <summary>Small presentation helpers shared by the pages (badges, relative times).</summary>
public static class UiFormat
{
    /// <summary>"acum 12 s", "acum 3 min", "acum 5 h", "acum 2 zile", "peste 1 min" (Romanian, like /plugs).</summary>
    public static string RelativeRo(DateTimeOffset when, DateTimeOffset now)
    {
        var d = now - when;
        var future = d < TimeSpan.Zero;
        var a = future ? -d : d;
        string body = a.TotalSeconds < 60 ? $"{(int)a.TotalSeconds} s"
            : a.TotalMinutes < 60 ? $"{(int)a.TotalMinutes} min"
            : a.TotalHours < 24 ? $"{(int)a.TotalHours} h"
            : (int)a.TotalDays == 1 ? "1 zi" : $"{(int)a.TotalDays} zile";
        return future ? $"peste {body}" : $"acum {body}";
    }

    public static string FullLocal(DateTimeOffset when) =>
        when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Bootstrap badge classes per protocol.</summary>
    public static string ProtocolBadge(string protocol) => protocol switch
    {
        PlugProtocols.Legacy => "text-bg-primary",
        PlugProtocols.Klap => "text-bg-success",
        PlugProtocols.Aes => "text-bg-info",
        _ => "text-bg-warning",
    };
}
