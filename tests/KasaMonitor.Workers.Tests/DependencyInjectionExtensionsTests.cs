using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class DependencyInjectionExtensionsTests
{
    [Fact]
    public void AddKasaWorkers_RegistersAllAbstractions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        services.AddDbContext<KasaDbContext>(o => o.UseSqlite(conn));
        services.AddSingleton<IKasaClient, KasaClient>();
        services.AddKasaWorkers();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var sd = scope.ServiceProvider;

        sd.GetService<IPlugStateBroadcaster>().Should().NotBeNull();
        sd.GetService<IPlugDiscoveredChannel>().Should().NotBeNull();
        sd.GetService<IPlugRepository>().Should().NotBeNull();
        sd.GetService<ISampleRepository>().Should().NotBeNull();
        sd.GetService<IHistoryRepository>().Should().NotBeNull();
        sd.GetService<ISettingsRepository>().Should().NotBeNull();
    }
}
