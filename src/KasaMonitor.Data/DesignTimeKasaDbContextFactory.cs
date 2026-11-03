using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KasaMonitor.Data;

/// <summary>
/// For <c>dotnet ef migrations add / script</c>: SQL Server with a placeholder connection string (the tools do not connect
/// for these commands). <c>dotnet ef database update</c> needs a real one: set <c>ConnectionStrings__Kasa</c>.
/// </summary>
internal sealed class DesignTimeKasaDbContextFactory : IDesignTimeDbContextFactory<KasaDbContext>
{
    public KasaDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__" + KasaDatabase.ConnectionStringName)
                 ?? "Server=localhost;Database=kasamonitor;Integrated Security=false;TrustServerCertificate=True";
        var opts = new DbContextOptionsBuilder<KasaDbContext>()
            .UseKasaSqlServer(cs)
            .Options;
        return new KasaDbContext(opts);
    }
}
