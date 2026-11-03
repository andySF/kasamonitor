using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KasaMonitor.Data.Tests;

/// <summary>Start-up of the database (0.12.0): wait for the server up to 120 s with a log line every 10 s, then the migrations, retried.</summary>
public class DatabaseStartupTests
{
    private sealed class ManualTime : TimeProvider
    {
        public TimeSpan Now { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    private static (DatabaseStartup Startup, ManualTime Time, ListLogger Log, List<TimeSpan> Delays) Rig(Func<TimeSpan, bool> serverUpAt,
        Func<int, Task> migrate)
    {
        var time = new ManualTime();
        var log = new ListLogger();
        var delays = new List<TimeSpan>();
        var calls = 0;
        var startup = new DatabaseStartup(_ => Task.FromResult(serverUpAt(time.Now) ? null : "Login failed for user 'kasamonitor'."),
            _ => migrate(++calls), log, time,
            (d, _) => { delays.Add(d); time.Now += d; return Task.CompletedTask; });
        return (startup, time, log, delays);
    }

    [Fact]
    public async Task TheServerComesUpAfter25s_ThreeWaitingLines10sApart_ThenOneMigration()
    {
        var migrations = 0;
        var (startup, time, log, _) = Rig(now => now >= TimeSpan.FromSeconds(25), _ => { migrations++; return Task.CompletedTask; });

        await startup.InitializeAsync(CancellationToken.None);

        time.Now.Should().Be(TimeSpan.FromSeconds(26), "probed every 2 s");
        log.Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).Should().Equal(
            "Waiting for the database server (0 s of 120 s): Login failed for user 'kasamonitor'.",
            "Waiting for the database server (10 s of 120 s): Login failed for user 'kasamonitor'.",
            "Waiting for the database server (20 s of 120 s): Login failed for user 'kasamonitor'.");
        migrations.Should().Be(1);
        log.Lines.Last().Text.Should().Be("Database schema up to date");
    }

    [Fact]
    public async Task NoServerFor120s_AnErrorLine_TheMigrationsAreTriedAnyway_AndTheLastFailureStopsTheStart()
    {
        var (startup, time, log, delays) = Rig(_ => false, _ => throw new InvalidOperationException("server not found"));

        var start = () => startup.InitializeAsync(CancellationToken.None);

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("server not found");
        log.Lines.Should().ContainSingle(l => l.Level == LogLevel.Error).Which.Text.Should().Contain("120 s");
        log.Lines.Count(l => l.Text.StartsWith("Waiting for the database server")).Should().Be(12, "one line every 10 s");
        log.Lines.Count(l => l.Text.StartsWith("Applying the database migrations failed")).Should().Be(DatabaseStartup.DefaultMigrateAttempts - 1);
        delays.Count(d => d == DatabaseStartup.DefaultMigrateRetryDelay).Should().Be(DatabaseStartup.DefaultMigrateAttempts - 1);
        time.Now.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task AMigrationFailingTwice_IsRetried_AndTheThirdAttemptWins()
    {
        var (startup, _, log, _) = Rig(_ => true, n => n <= 2 ? throw new TimeoutException("login timeout") : Task.CompletedTask);

        await startup.InitializeAsync(CancellationToken.None);

        log.Lines.Where(l => l.Level == LogLevel.Warning).Should().HaveCount(2).And.OnlyContain(l => l.Text.Contains("login timeout"));
        log.Lines.Last().Text.Should().Be("Database schema up to date");
    }

    [Fact]
    public async Task CanOpen_TrueOnAWorkingDatabase_FalseWhenTheFileCannotBeOpened()
    {
        using var db = new TestDbFactory();
        (await KasaDatabase.CanOpenAsync(db.CreateContext(), TimeSpan.FromSeconds(2), CancellationToken.None)).Should().BeTrue();

        var missing = Path.Combine(Path.GetTempPath(), $"kasa-missing-{Guid.NewGuid():N}", "kasa.db");
        await using var broken = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite($"Data Source={missing};Mode=ReadOnly").Options);
        (await KasaDatabase.CanOpenAsync(broken, TimeSpan.FromSeconds(2), CancellationToken.None)).Should().BeFalse();
        (await KasaDatabase.ProbeAsync(broken, TimeSpan.FromSeconds(2), CancellationToken.None)).Should().Contain("unable to open database file");
        SqliteConnection.ClearAllPools();
    }
}
