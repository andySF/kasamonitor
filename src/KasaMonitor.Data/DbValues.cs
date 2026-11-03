namespace KasaMonitor.Data;

/// <summary>
/// Values made storable before they reach SQL Server. A <c>float</c> column refuses NaN and ±Infinity (SQLite stored them),
/// and one such value fails the whole <c>SaveChanges</c>: every nullable <c>double</c> written by a repository goes through
/// <see cref="Finite"/> (a new column too).
/// </summary>
public static class DbValues
{
    /// <summary>The value when finite, otherwise null (NaN, ±Infinity).</summary>
    public static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;
}
