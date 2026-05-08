using KasaMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

    public void Dispose() => _conn.Dispose();
}
