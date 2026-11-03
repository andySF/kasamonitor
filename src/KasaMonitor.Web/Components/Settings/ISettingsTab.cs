using Microsoft.AspNetCore.Components;

namespace KasaMonitor.Web.Components.Settings;

/// <summary>A /settings tab: <see cref="IsDirty"/> = its form differs from what was loaded (asked before leaving it).</summary>
public interface ISettingsTab
{
    bool IsDirty { get; }

    /// <summary>Raised after a render that changed <see cref="IsDirty"/>, so the page can arm its leave confirmation.</summary>
    event Action? DirtyChanged;
}

/// <summary>
/// Base of the /settings tabs: after every render (each edit re-renders the tab) it compares <see cref="IsDirty"/> with
/// the previous value and raises <see cref="DirtyChanged"/> when it flipped.
/// </summary>
public abstract class SettingsTabBase : ComponentBase, ISettingsTab
{
    private bool _lastDirty;

    public abstract bool IsDirty { get; }

    public event Action? DirtyChanged;

    protected override void OnAfterRender(bool firstRender)
    {
        var dirty = IsDirty;
        if (dirty == _lastDirty) return;
        _lastDirty = dirty;
        DirtyChanged?.Invoke();
    }
}
