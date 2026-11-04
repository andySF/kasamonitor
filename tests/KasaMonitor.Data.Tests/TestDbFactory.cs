using KasaMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KasaMonitor.Data.Tests;

internal sealed class TestDbFactory : IDisposable
{
    private readonly SqliteConnection _conn;

    public TestDbFactory()
    {
        _conn = new SqliteConnection("Filename=:memory:");
        _conn.Open();
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
    }

    public KasaDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<KasaDbContext>()
            .UseSqlite(_conn)
            .Options;
        return new KasaDbContext(opts);
    }

    /// <summary>A context on the same database with an interceptor (a failure simulated in the middle of a transaction).</summary>
    public KasaDbContext CreateContext(IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(_conn).AddInterceptors(interceptor).Options);

    public void Dispose() => _conn.Dispose();
}
