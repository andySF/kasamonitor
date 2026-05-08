using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KasaMonitor.Data;

internal sealed class DesignTimeKasaDbContextFactory : IDesignTimeDbContextFactory<KasaDbContext>
{
    public KasaDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<KasaDbContext>()
            .UseSqlite("Filename=:memory:")
            .Options;
        return new KasaDbContext(opts);
    }
}
