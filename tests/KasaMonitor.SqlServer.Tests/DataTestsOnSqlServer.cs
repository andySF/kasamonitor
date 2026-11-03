using KasaMonitor.SqlServer.Tests;
using Microsoft.EntityFrameworkCore;

// The repository tests of KasaMonitor.Data.Tests are compiled into this assembly (see the .csproj) and find these two types
// first: their namespace's FactAttribute (skipped without Docker) and a TestDbFactory on SQL Server instead of SQLite.
namespace KasaMonitor.Data.Tests;

/// <summary>[Fact] of the linked repository tests: on SQL Server, skipped when Docker is not available.</summary>
internal sealed class FactAttribute : Xunit.FactAttribute
{
    public FactAttribute()
    {
        if (MsSql.SkipReason is { } why) Skip = "SQL Server tests skipped: " + why;
    }
}

/// <summary>
/// The same API as the SQLite TestDbFactory of KasaMonitor.Data.Tests: an empty database per test (the migrated
/// kasamonitor database of the container, every table truncated), contexts with the app's SQL Server options
/// (retrying execution strategy included).
/// </summary>
internal sealed class TestDbFactory : IDisposable
{
    private readonly string _cs;

    public TestDbFactory()
    {
        // Off the test's synchronization context: the constructor is synchronous in the linked tests.
        Task.Run(MsSql.ResetAsync).GetAwaiter().GetResult();
        _cs = Task.Run(MsSql.AppConnectionStringAsync).GetAwaiter().GetResult();
    }

    public KasaDbContext CreateContext() => new(new DbContextOptionsBuilder<KasaDbContext>().UseKasaSqlServer(_cs).Options);

    public void Dispose() { }
}
