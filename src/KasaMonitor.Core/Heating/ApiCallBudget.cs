namespace KasaMonitor.Core.Heating;

/// <summary>
/// Local accounting of the Viessmann call quota (sliding 24 h: <see cref="ViessmannApi.DailyCallLimit"/>, sliding
/// 10 min: <see cref="ViessmannApi.TenMinuteCallLimit"/>). A call is made only when both windows have room and no
/// 429 block is active; after a 429 nothing is called until the reset time the API gave (or, without one, until the
/// oldest call leaves the window that is full, at least 10 minutes). In memory: a restart starts from zero, the API's
/// own count stays authoritative through 429.
/// </summary>
public sealed class ApiCallBudget
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private readonly int _dailyLimit;
    private readonly int _tenMinuteLimit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<DateTimeOffset> _calls = new();
    private readonly object _sync = new();
    private DateTimeOffset? _blockedUntil;

    public ApiCallBudget(Func<DateTimeOffset>? clock = null, int dailyLimit = ViessmannApi.DailyCallLimit, int tenMinuteLimit = ViessmannApi.TenMinuteCallLimit)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _dailyLimit = dailyLimit;
        _tenMinuteLimit = tenMinuteLimit;
    }

    /// <summary>Calls made in the last 24 h (the window the API counts).</summary>
    public int CallsLast24h
    {
        get { lock (_sync) { Prune(_clock()); return _calls.Count; } }
    }

    public DateTimeOffset? BlockedUntil
    {
        get { lock (_sync) return _blockedUntil is { } b && b > _clock() ? b : null; }
    }

    /// <summary>Records a call if allowed; otherwise returns false and the time from which a call may be possible.</summary>
    public bool TryAcquire(out DateTimeOffset? retryAt)
    {
        lock (_sync)
        {
            var now = _clock();
            Prune(now);
            if (_blockedUntil is { } b && b > now)
            {
                retryAt = b;
                return false;
            }
            _blockedUntil = null;
            if (_calls.Count >= _dailyLimit)
            {
                retryAt = _calls.Peek() + Day;
                return false;
            }
            var lastTen = _calls.Count(c => c > now - TenMinutes);
            if (lastTen >= _tenMinuteLimit)
            {
                retryAt = _calls.First(c => c > now - TenMinutes) + TenMinutes;
                return false;
            }
            _calls.Enqueue(now);
            retryAt = null;
            return true;
        }
    }

    /// <summary>
    /// After an HTTP 429: blocks until <paramref name="resetUtc"/>; without it until the oldest call of the full window
    /// expires, and never less than 10 minutes. Returns the block end.
    /// </summary>
    public DateTimeOffset Block(DateTimeOffset? resetUtc)
    {
        lock (_sync)
        {
            var now = _clock();
            Prune(now);
            DateTimeOffset until;
            if (resetUtc is { } r && r > now) until = r;
            else if (_calls.Count >= _dailyLimit) until = _calls.Peek() + Day;
            else until = now + TenMinutes;
            if (until < now + TenMinutes && resetUtc is null) until = now + TenMinutes;
            _blockedUntil = until;
            return until;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_calls.Count > 0 && _calls.Peek() <= now - Day) _calls.Dequeue();
    }
}
