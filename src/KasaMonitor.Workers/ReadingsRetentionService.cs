using KasaMonitor.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <param name="CutoffUtc">Readings strictly older than this are deleted; null = unlimited retention.</param>
public sealed record RetentionPlan(int Months, DateTimeOffset? CutoffUtc, int RowsToDelete)
{
    /// <summary>The UI must ask before saving only when something would actually be deleted.</summary>
    public bool NeedsConfirmation => RowsToDelete > 0;
}

/// <summary>
/// Retention of the raw 10-second readings (table Samples) by <see cref="SettingKeys.ReadingsRetentionMonths"/>
/// (0 = keep everything, default) and of the raw live-stream samples (table LiveSamples) by
/// <see cref="SettingKeys.LiveRawRetentionDays"/> (default 30, 0 = keep everything). Daily/monthly history and the
/// minute aggregates (OneMinuteAggregates, LiveMinuteAggregates) are never touched.
/// </summary>
public sealed class ReadingsRetentionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<ReadingsRetentionService> _log;

    public ReadingsRetentionService(IServiceScopeFactory scopeFactory, ILogger<ReadingsRetentionService> log, Func<DateTimeOffset>? clock = null)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static DateTimeOffset? CutoffFor(int months, DateTimeOffset nowUtc) =>
        months <= 0 ? null : nowUtc.ToUniversalTime().AddMonths(-months);

    /// <summary>What saving <paramref name="months"/> would delete right now (real row count from the database).</summary>
    public async Task<RetentionPlan> PlanAsync(int months, CancellationToken ct)
    {
        var cutoff = CutoffFor(months, _clock());
        if (cutoff is null) return new RetentionPlan(months, null, 0);
        using var scope = _scopeFactory.CreateScope();
        var count = await scope.ServiceProvider.GetRequiredService<ISampleRepository>().CountOlderThanAsync(cutoff.Value, ct);
        return new RetentionPlan(months, cutoff, count);
    }

    public static DateTimeOffset? LiveCutoffFor(int days, DateTimeOffset nowUtc) =>
        days <= 0 ? null : nowUtc.ToUniversalTime().AddDays(-days);

    /// <summary>What saving <paramref name="days"/> for the live samples would delete right now (asked before saving a smaller value).</summary>
    public async Task<RetentionPlan> PlanLiveRawAsync(int days, CancellationToken ct)
    {
        var cutoff = LiveCutoffFor(days, _clock());
        if (cutoff is null) return new RetentionPlan(days, null, 0);
        using var scope = _scopeFactory.CreateScope();
        var count = await scope.ServiceProvider.GetRequiredService<ILiveRepository>().CountSamplesOlderThanAsync(cutoff.Value, ct);
        return new RetentionPlan(days, cutoff, count);
    }

    /// <summary>Deletes live samples older than <paramref name="days"/> days (0 = unlimited), in batches.</summary>
    public async Task<int> ApplyLiveRawAsync(int days, CancellationToken ct)
    {
        var cutoff = LiveCutoffFor(days, _clock());
        if (cutoff is null) return 0;
        using var scope = _scopeFactory.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<ILiveRepository>().DeleteSamplesOlderThanAsync(cutoff.Value, ct);
        if (deleted > 0)
            _log.LogInformation("Live stream retention ({Days} days): deleted {Rows} live samples older than {Cutoff:yyyy-MM-dd HH:mm} UTC",
                days, deleted, cutoff.Value);
        return deleted;
    }

    /// <summary>Deletes readings older than the window, in batches. Returns the number of rows deleted (0 for unlimited).</summary>
    public async Task<int> ApplyAsync(int months, CancellationToken ct)
    {
        var cutoff = CutoffFor(months, _clock());
        if (cutoff is null) return 0;
        using var scope = _scopeFactory.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<ISampleRepository>().DeleteOlderThanAsync(cutoff.Value, ct);
        if (deleted > 0)
            _log.LogInformation("Readings retention ({Months} months): deleted {Rows} readings older than {Cutoff:yyyy-MM-dd HH:mm} UTC",
                months, deleted, cutoff.Value);
        return deleted;
    }
}
