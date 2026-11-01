using System.Globalization;
using System.Reflection;

namespace KasaMonitor.Web.Ui;

/// <summary>
/// Version shown in the navigation bar: "v0.4.0" from <see cref="AssemblyInformationalVersionAttribute"/> (set from
/// <c>Version</c> in Directory.Build.props; the SDK appends "+&lt;commit&gt;" when the build knows the git revision).
/// The tooltip adds the commit and the build time (write time of the web assembly) when available.
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<(string Short, string Title)> Value = new(() => Read(typeof(AppVersion).Assembly));

    /// <summary>"v0.4.0".</summary>
    public static string Short => Value.Value.Short;

    /// <summary>"KasaMonitor 0.4.0 · commit 66dbb9a · build 2026-11-01 16:05 UTC" (parts omitted when unknown).</summary>
    public static string Title => Value.Value.Title;

    public static (string Short, string Title) Read(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return Format(info, BuildTimeUtc(assembly));
    }

    /// <summary>Splits "0.4.0+66dbb9a…" into the version and the commit (first 7 characters).</summary>
    public static (string Short, string Title) Format(string informationalVersion, DateTimeOffset? builtUtc)
    {
        var plus = informationalVersion.IndexOf('+');
        var version = plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        var commit = plus >= 0 ? informationalVersion[(plus + 1)..] : null;
        var parts = new List<string> { $"KasaMonitor {version}" };
        if (!string.IsNullOrWhiteSpace(commit)) parts.Add($"commit {(commit.Length > 7 ? commit[..7] : commit)}");
        if (builtUtc is { } b) parts.Add("build " + b.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
        return ("v" + version, string.Join(" · ", parts));
    }

    private static DateTimeOffset? BuildTimeUtc(Assembly assembly)
    {
        try
        {
            return string.IsNullOrEmpty(assembly.Location) ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(assembly.Location), TimeSpan.Zero);
        }
        catch (Exception) { return null; }
    }
}
