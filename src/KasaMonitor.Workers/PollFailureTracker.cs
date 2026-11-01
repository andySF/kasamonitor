using System.Collections.Concurrent;

namespace KasaMonitor.Workers;

/// <summary>
/// Throttles poll-failure warnings per plug: the first failure is logged, then again after
/// <see cref="FailuresBetweenWarnings"/> more consecutive failures or after <see cref="TimeBetweenWarnings"/>,
/// whichever comes first. A success after failures is reported once (recovery).
/// </summary>
public sealed class PollFailureTracker
{
    public const int FailuresBetweenWarnings = 10;
    public static readonly TimeSpan TimeBetweenWarnings = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, State> _states = new();

    /// <returns>Whether to log now, and the number of consecutive failures so far.</returns>
    public (bool ShouldLog, int ConsecutiveFailures) RecordFailure(Guid plugId, DateTimeOffset now)
    {
        var st = _states.GetOrAdd(plugId, _ => new State());
        lock (st)
        {
            st.Consecutive++;
            st.SinceLastWarning++;
            var log = st.LastWarningUtc is null
                      || st.SinceLastWarning >= FailuresBetweenWarnings
                      || now - st.LastWarningUtc.Value >= TimeBetweenWarnings;
            if (log)
            {
                st.LastWarningUtc = now;
                st.SinceLastWarning = 0;
            }
            return (log, st.Consecutive);
        }
    }

    /// <returns>The number of consecutive failures that just ended, or 0 if the plug was not failing.</returns>
    public int RecordSuccess(Guid plugId)
    {
        if (!_states.TryRemove(plugId, out var st)) return 0;
        lock (st) return st.Consecutive;
    }

    private sealed class State
    {
        public int Consecutive;
        public int SinceLastWarning;
        public DateTimeOffset? LastWarningUtc;
    }
}
