namespace KasaMonitor.Web.Components.Settings;

/// <summary>
/// Cascaded by /settings to its tabs: the single way a link moves to another tab, so the „unsaved changes” question is
/// asked first (<see cref="TabLink"/>). Blazor's enhanced navigation does not raise OnBeforeInternalNavigation for a
/// page that is interactive under a static router (checked live, 0.10.0), so the links go through the server instead.
/// </summary>
public sealed class SettingsNavigation(Func<string, bool, Task> select)
{
    /// <summary>Opens the tab <paramref name="key"/>; <paramref name="newWindow"/> = Ctrl/Shift/Cmd held (opens a browser tab, nothing to lose).</summary>
    public Task SelectAsync(string key, bool newWindow) => select(key, newWindow);
}
