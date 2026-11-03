using KasaMonitor.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KasaMonitor.Web.ApiTests;

/// <summary>
/// The page tests run the real <c>Program</c> on a temporary SQLite file instead of SQL Server: the EF model is the same,
/// the schema comes from <c>EnsureCreated</c> (the migrations are SQL Server's). The SQL Server path itself — migrations,
/// queries, start-up — is covered by <c>KasaMonitor.SqlServer.Tests</c>.
/// </summary>
internal static class TestDatabase
{
    /// <summary>Required by <c>Program</c>, never opened here.</summary>
    public const string UnusedConnectionString = "Server=unused.invalid;Database=kasamonitor;User Id=kasamonitor;Password=unused;TrustServerCertificate=True";

    public static IWebHostBuilder UseSqliteTestDatabase(this IWebHostBuilder builder, string dir, string? connectionString = null)
    {
        builder.UseSetting("ConnectionStrings:" + KasaDatabase.ConnectionStringName, connectionString ?? UnusedConnectionString);
        builder.UseSetting("KasaMonitor:DataDir", dir);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<KasaDbContext>>();
            s.RemoveAll<IDbContextOptionsConfiguration<KasaDbContext>>();
            s.AddDbContext<KasaDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir, "kasa.db")}"));
            s.RemoveAll<IDatabaseInitializer>();
            s.AddSingleton<IDatabaseInitializer>(sp => new EnsureCreatedInitializer(sp.GetRequiredService<IServiceScopeFactory>()));
        });
        return builder;
    }

    private sealed class EnsureCreatedInitializer(IServiceScopeFactory scopes) : IDatabaseInitializer
    {
        public async Task InitializeAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<KasaDbContext>().Database.EnsureCreatedAsync(ct);
        }
    }
}
