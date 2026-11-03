using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Data;

/// <summary>
/// The database is SQL Server (0.12.0): one connection string (<c>ConnectionStrings__Kasa</c>), transient errors retried
/// by EF Core, the schema brought up to date by the migrations at start-up.
/// </summary>
public static class KasaDatabase
{
    /// <summary>Name of the connection string (<c>ConnectionStrings:Kasa</c>, env <c>ConnectionStrings__Kasa</c>).</summary>
    public const string ConnectionStringName = "Kasa";

    /// <summary>Transient errors (failover, network blip, throttling) are retried this many times by EF Core.</summary>
    public const int MaxRetryCount = 6;

    /// <summary>Longest pause between two retries of the same command.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The oldest SQL Server the app supports is 2019 (compatibility level 150): EF must not generate SQL of a newer level
    /// (e.g. LEAST/GREATEST of 2022), whatever its own default.
    /// </summary>
    public const int CompatibilityLevel = 150;

    /// <summary>SQL Server with the retrying execution strategy. Explicit transactions must run inside
    /// <c>Database.CreateExecutionStrategy().ExecuteAsync(…)</c> (the repositories do).</summary>
    public static DbContextOptionsBuilder UseKasaSqlServer(this DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlServer(connectionString, sql => sql
            .UseCompatibilityLevel(CompatibilityLevel)
            .EnableRetryOnFailure(MaxRetryCount, MaxRetryDelay, errorNumbersToAdd: null));

    /// <summary>The same SQL Server options without the retrying strategy: one attempt per command (the start-up's own
    /// loop decides how often to try, so a missing server does not hold the start for minutes per attempt).</summary>
    public static DbContextOptionsBuilder<KasaDbContext> UseKasaSqlServerWithoutRetry(this DbContextOptionsBuilder<KasaDbContext> options,
        string connectionString) =>
        options.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(CompatibilityLevel));

    /// <inheritdoc cref="UseKasaSqlServer(DbContextOptionsBuilder, string)"/>
    public static DbContextOptionsBuilder<KasaDbContext> UseKasaSqlServer(this DbContextOptionsBuilder<KasaDbContext> options, string connectionString) =>
        (DbContextOptionsBuilder<KasaDbContext>)UseKasaSqlServer((DbContextOptionsBuilder)options, connectionString);

    /// <summary>
    /// Opens (and closes) the context's connection directly, without the retrying strategy, within <paramref name="timeout"/>.
    /// False when the server does not answer, refuses the login or the database is missing. Used at start-up and by /health.
    /// </summary>
    public static async Task<bool> CanOpenAsync(KasaDbContext ctx, TimeSpan timeout, CancellationToken ct) =>
        await ProbeAsync(ctx, timeout, ct) is null;

    /// <summary>Like <see cref="CanOpenAsync"/>: null when the database answered, otherwise why not (the exception's message:
    /// login failed, database missing, network, timeout …) for the start-up log.</summary>
    public static async Task<string?> ProbeAsync(KasaDbContext ctx, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // OpenConnection does not go through the execution strategy: one attempt, bounded by the timeout.
            await ctx.Database.OpenConnectionAsync(cts.Token);
            try
            {
                await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync(cts.Token);
            }
            finally
            {
                await ctx.Database.CloseConnectionAsync();
            }
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ex is OperationCanceledException ? $"no answer within {timeout.TotalSeconds:0} s" : ex.GetBaseException().Message;
        }
    }
}

/// <summary>Brings the database up at start-up: waits for the server, then applies the migrations.</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken ct);
}

/// <summary>
/// Start-up of the database: waits up to <see cref="WaitLimit"/> (120 s) for the server to accept a connection, with a
/// line in the log every <see cref="LogEvery"/> (10 s), then applies the migrations, retried <see cref="MigrateAttempts"/>
/// times. When the server is still missing the last attempt throws and the process stops (Docker restarts it).
/// </summary>
public sealed class DatabaseStartup : IDatabaseInitializer
{
    public static readonly TimeSpan DefaultWaitLimit = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan DefaultLogEvery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultProbeEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    public const int DefaultMigrateAttempts = 3;
    public static readonly TimeSpan DefaultMigrateRetryDelay = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<string?>> _probe;
    private readonly Func<CancellationToken, Task> _migrate;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public TimeSpan WaitLimit { get; init; } = DefaultWaitLimit;
    public TimeSpan LogEvery { get; init; } = DefaultLogEvery;
    public TimeSpan ProbeEvery { get; init; } = DefaultProbeEvery;
    public int MigrateAttempts { get; init; } = DefaultMigrateAttempts;
    public TimeSpan MigrateRetryDelay { get; init; } = DefaultMigrateRetryDelay;

    /// <param name="probe">One probe of the server: null = a connection opened, otherwise why not.</param>
    /// <param name="migrate">Applies the migrations.</param>
    /// <param name="delay">Test hook for the pauses (default <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>).</param>
    public DatabaseStartup(Func<CancellationToken, Task<string?>> probe, Func<CancellationToken, Task> migrate, ILogger log,
        TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _probe = probe;
        _migrate = migrate;
        _log = log;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
    }

    /// <summary>
    /// The production wiring: every probe and the migrations through a context of their own, without the retrying
    /// strategy (each attempt is one attempt; this class decides how many). Longest start with the server missing:
    /// <see cref="DefaultWaitLimit"/> + <see cref="DefaultMigrateAttempts"/> × (connect timeout 15 s + <see cref="DefaultMigrateRetryDelay"/>) ≈ 3 min.
    /// </summary>
    public static DatabaseStartup ForConnectionString(string connectionString, ILogger log)
    {
        KasaDbContext Create() => new(new DbContextOptionsBuilder<KasaDbContext>().UseKasaSqlServerWithoutRetry(connectionString).Options);
        return new DatabaseStartup(
            async ct =>
            {
                await using var ctx = Create();
                return await KasaDatabase.ProbeAsync(ctx, ProbeTimeout, ct);
            },
            async ct =>
            {
                await using var ctx = Create();
                await ctx.Database.MigrateAsync(ct);
            },
            log);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await WaitForServerAsync(ct);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _migrate(ct);
                _log.LogInformation("Database schema up to date");
                return;
            }
            catch (Exception ex) when (attempt < MigrateAttempts && !ct.IsCancellationRequested)
            {
                _log.LogWarning("Applying the database migrations failed (attempt {Attempt} of {Attempts}), retrying in {Delay} s: {Reason}",
                    attempt, MigrateAttempts, (int)MigrateRetryDelay.TotalSeconds, ex.Message);
                await _delay(MigrateRetryDelay, ct);
            }
        }
    }

    /// <summary>True when the server answered within <see cref="WaitLimit"/>.</summary>
    public async Task<bool> WaitForServerAsync(CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        var nextLog = TimeSpan.Zero;
        while (true)
        {
            var problem = await _probe(ct);
            if (problem is null) return true;
            var elapsed = _time.GetElapsedTime(start);
            if (elapsed >= WaitLimit)
            {
                _log.LogError("The database server did not answer within {Limit} s ({Reason}); applying the migrations anyway (the start fails if it is still missing)",
                    (int)WaitLimit.TotalSeconds, problem);
                return false;
            }
            if (elapsed >= nextLog)
            {
                _log.LogWarning("Waiting for the database server ({Elapsed} s of {Limit} s): {Reason}", (int)elapsed.TotalSeconds, (int)WaitLimit.TotalSeconds, problem);
                nextLog = elapsed + LogEvery;
            }
            await _delay(ProbeEvery, ct);
        }
    }
}
