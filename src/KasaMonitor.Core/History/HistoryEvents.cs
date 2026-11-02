using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.History;

/// <summary>Where an event of the history comes from (the filter of the „Evenimente” section).</summary>
public enum HistoryEventSource { Relay, Boiler, Miele }

/// <summary>
/// One event of the history: a relay command, a boiler error or a gap in its readings, a Miele appliance disconnected.
/// <see cref="EndUtc"/>: when a state with a duration ended (null = still on at the end of the data, or instantaneous).
/// <see cref="Problem"/>: shown as a warning (a failed command, an error, a disconnection).
/// </summary>
public sealed record HistoryEvent(DateTimeOffset TsUtc, HistoryEventSource Source, string Subject, string Text, DateTimeOffset? EndUtc = null,
    bool Problem = false);

/// <summary>A boiler reading reduced to what the events need: its time and the active error (null or empty = none).</summary>
public sealed record HeatingReadingState(DateTimeOffset TsUtc, string? ActiveError);

/// <summary>
/// A boiler reading where something changes for the events: the first of the range, the first after a gap, or one whose
/// active error differs from the reading before (with that reading's time and error).
/// </summary>
public sealed record HeatingReadingChange(DateTimeOffset TsUtc, string? ActiveError, DateTimeOffset? PreviousTsUtc, string? PreviousError);

/// <summary>Builds the events of the history from the stored rows (pure).</summary>
public static class HistoryEvents
{
    /// <summary>The boiler is „fără citiri” when two readings are further apart than this (the cadence is ~2 min).</summary>
    public static readonly TimeSpan BoilerGap = TimeSpan.FromMinutes(15);

    /// <summary>„Pornit” / „Oprit” of a relay command, from the UI or another source, with its result.</summary>
    public static IEnumerable<HistoryEvent> Relay(IEnumerable<RelayCommand> commands, IReadOnlyDictionary<Guid, string> aliases) =>
        commands.Select(c => new HistoryEvent(c.TsUtc, HistoryEventSource.Relay, aliases.TryGetValue(c.PlugId, out var a) ? a : "priză ștearsă",
            (c.On ? "pornită" : "oprită") + (c.Source == RelayCommandSources.Ui ? " din interfață" : " (" + c.Source + ")")
            + (c.Succeeded ? "" : " — eșuat: " + c.Result), Problem: !c.Succeeded));

    /// <summary>
    /// The boiler's errors (a change of <see cref="HeatingReadingState.ActiveError"/>: from its first reading to the first
    /// reading without it) and the gaps between readings longer than <see cref="BoilerGap"/> („fără citiri”).
    /// </summary>
    public static IEnumerable<HistoryEvent> Boiler(IReadOnlyList<HeatingReadingState> readings, string subject)
    {
        var sorted = readings.OrderBy(r => r.TsUtc).ToList();
        var changes = new List<HeatingReadingChange>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var err = Norm(sorted[i].ActiveError);
            var prevErr = i > 0 ? Norm(sorted[i - 1].ActiveError) : null;
            DateTimeOffset? prevTs = i > 0 ? sorted[i - 1].TsUtc : null;
            if (prevTs is null || sorted[i].TsUtc - prevTs > BoilerGap || err != prevErr)
                changes.Add(new HeatingReadingChange(sorted[i].TsUtc, err, prevTs, prevErr));
        }
        return BoilerFromChanges(changes, subject);
    }

    /// <summary>
    /// The same events from only the readings where something changes (<see cref="HeatingReadingChange"/>: the first one,
    /// one after a gap, one whose error differs from the reading before), as the repository selects them in SQL.
    /// </summary>
    public static IEnumerable<HistoryEvent> BoilerFromChanges(IReadOnlyList<HeatingReadingChange> changes, string subject)
    {
        var events = new List<HistoryEvent>();
        string? error = null;
        DateTimeOffset errorFrom = default;
        foreach (var c in changes.OrderBy(x => x.TsUtc))
        {
            if (c.PreviousTsUtc is { } prev && c.TsUtc - prev > BoilerGap)
                events.Add(new HistoryEvent(prev, HistoryEventSource.Boiler, subject, "fără citiri (deconectată sau aplicația oprită)", c.TsUtc, Problem: true));
            var e = Norm(c.ActiveError);
            if (e == error) continue;
            if (error is not null)
                events.Add(new HistoryEvent(errorFrom, HistoryEventSource.Boiler, subject, "eroare " + error, c.TsUtc, Problem: true));
            error = e;
            errorFrom = c.TsUtc;
        }
        if (error is not null)
            events.Add(new HistoryEvent(errorFrom, HistoryEventSource.Boiler, subject, "eroare " + error, null, Problem: true));
        return events;
    }

    private static string? Norm(string? error) => string.IsNullOrWhiteSpace(error) ? null : error.Trim();

    /// <summary>
    /// A Miele appliance disconnected: from the first snapshot „neconectat” (status 255) or with a signal failure to the
    /// first one without it.
    /// </summary>
    public static IEnumerable<HistoryEvent> Miele(IEnumerable<MieleStateSnapshot> snapshots, string subject)
    {
        var events = new List<HistoryEvent>();
        DateTimeOffset? from = null;
        var what = "";
        foreach (var s in snapshots.OrderBy(x => x.TsUtc))
        {
            var notConnected = s.Values.StatusRaw == MieleTexts.StatusNotConnected;
            var bad = notConnected || s.Values.SignalFailure == true;
            if (bad && from is null)
            {
                from = s.TsUtc;
                what = notConnected ? "neconectat" : "eroare de semnal";
            }
            else if (!bad && from is { } f)
            {
                events.Add(new HistoryEvent(f, HistoryEventSource.Miele, subject, what, s.TsUtc, Problem: true));
                from = null;
            }
        }
        if (from is { } open) events.Add(new HistoryEvent(open, HistoryEventSource.Miele, subject, what, null, Problem: true));
        return events;
    }
}
