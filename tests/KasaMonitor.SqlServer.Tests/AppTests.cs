using System.Net;
using FluentAssertions;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace KasaMonitor.SqlServer.Tests;

/// <summary>The real Program on SQL Server: ConnectionStrings__Kasa, KasaMonitor__DataDir, no background workers.</summary>
public sealed class SqlServerAppFactory(string connectionString) : WebApplicationFactory<Program>
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-sqlapp-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Dir);
        builder.UseSetting("ConnectionStrings:" + KasaDatabase.ConnectionStringName, connectionString);
        builder.UseSetting("KasaMonitor:DataDir", Dir);
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class AppTests
{
    [SqlServerFact]
    public async Task OnAnEmptyDatabase_TheAppMigratesAtStart_StoresItsKeysInTheDataDir_AndEveryPageRenders()
    {
        var cs = await MsSql.NewDatabaseAsync("kasa_app");
        using var app = new SqlServerAppFactory(cs);
        var client = app.CreateClient();

        (await client.GetStringAsync("/health")).Should().Contain("healthy");
        await using (var ctx = MsSql.Context(cs))
            (await ctx.Database.GetAppliedMigrationsAsync()).Should().BeEquivalentTo(ctx.Database.GetMigrations());
        // The key ring is written at the first protected value (a password saved in /settings).
        var protector = app.Services.GetRequiredService<KasaMonitor.Core.Abstractions.ISecretProtector>();
        protector.Unprotect(protector.Protect("x")).Should().Be("x");
        Directory.EnumerateFiles(Path.Combine(app.Dir, "dp-keys"), "key-*.xml").Should().NotBeEmpty("the DataProtection keys live in KasaMonitor__DataDir");

        await SeedAsync(cs);
        foreach (var url in new[]
                 {
                     "/", "/history?p=zi&d=2026-11-02", "/history?p=luna&d=2026-11", "/history?p=an&d=2026", "/centrala", "/clima", "/aparate",
                     "/consumatori", "/contor", "/prize", "/dispozitive", "/settings/aplicatie", "/history/export.csv?section=costuri&p=luna&d=2026-11",
                 })
        {
            var r = await client.GetAsync(url);
            r.StatusCode.Should().Be(HttpStatusCode.OK, url);
            (await r.Content.ReadAsStringAsync()).Should().NotContain("An unhandled exception", url);
        }
    }

    [SqlServerFact]
    public async Task Health_IsDegradedWhileTheDatabaseIsOffline_AndHealthyAgainAfter_WithoutStopping()
    {
        var cs = await MsSql.NewDatabaseAsync("kasa_health");
        var name = new SqlConnectionStringBuilder(cs).InitialCatalog;
        using var app = new SqlServerAppFactory(cs);
        var client = app.CreateClient();
        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);

        await MsSql.ExecAsync(MsSql.AdminConnectionString, $"ALTER DATABASE [{name}] SET OFFLINE WITH ROLLBACK IMMEDIATE");
        var down = await client.GetAsync("/health");
        down.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await down.Content.ReadAsStringAsync()).Should().StartWith("degraded");

        await MsSql.ExecAsync(MsSql.AdminConnectionString, $"ALTER DATABASE [{name}] SET ONLINE");
        HttpStatusCode back = default;
        for (var i = 0; i < 10 && back != HttpStatusCode.OK; i++)
        {
            back = (await client.GetAsync("/health")).StatusCode;
            if (back != HttpStatusCode.OK) await Task.Delay(500);
        }
        back.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>A little of everything the pages read: two plugs (one main meter), samples, days, live minutes, NILM, boiler, climate.</summary>
    private static async Task SeedAsync(string cs)
    {
        await using var ctx = MsSql.Context(cs);
        var now = DateTimeOffset.UtcNow;
        var main = Guid.NewGuid();
        var plug = Guid.NewGuid();
        PlugEntity Plug(Guid id, string mac, int role) => new()
        {
            Id = id, Mac = mac, IpAddress = "192.168.254.10", Alias = role == 1 ? "Contor general" : "Frigider", Model = "S3EM-002CXCEU", HwVer = "EMG3",
            SwVer = "1.4.4", OemId = "X", FirstSeenUtc = now.AddDays(-40), LastSeenUtc = now, Protocol = "shelly-rpc", Role = role, StreamEnabled = true,
        };
        ctx.Plugs.AddRange(Plug(main, "aabbccddee00", 1), Plug(plug, "aabbccddee01", 0));
        for (var i = 0; i < 300; i++)
            ctx.Samples.Add(new SampleEntity { PlugId = plug, TimestampUtc = now.AddMinutes(-10 * i), Online = true, PowerMw = 80_000 + i, TotalWh = 10_000 + i });
        for (var d = 1; d <= 40; d++)
        {
            var day = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-d).ToString("yyyy-MM-dd");
            ctx.DailyEnergies.Add(new DailyEnergyEntity { PlugId = main, Date = day, EnergyWh = 9000 + d });
            ctx.DailyEnergies.Add(new DailyEnergyEntity { PlugId = plug, Date = day, EnergyWh = 1200 + d });
        }
        var minute = now.ToUnixTimeSeconds() / 60 * 60;
        for (var m = 1; m <= 120; m++)
            ctx.LiveMinuteAggregates.Add(new LiveMinuteAggregateEntity { PlugId = main, MinuteTs = minute - 60 * m, MinW = 200, AvgW = 350, MaxW = 900,
                AvgVoltageV = 231, EnergyWh = 5.8, SampleCount = 60, CoveredSeconds = 60 });
        for (var s = 0; s < 60; s++)
            ctx.LiveSamples.Add(new LiveSampleEntity { PlugId = main, Ts = now.ToUnixTimeSeconds() - s * 10, PowerW = 300 + s, VoltageV = 231 });
        var sig = new ApplianceSignatureEntity { Name = "Fierbător", Icon = "kettle", Category = "kitchen", MeanDeltaP = 2000, MeanDeltaQ = 10, MeanTransient = 1, Count = 2 };
        ctx.ApplianceSignatures.Add(sig);
        await ctx.SaveChangesAsync();
        ctx.MeterEvents.AddRange(
            new MeterEventEntity { PlugId = main, Ts = now.ToUnixTimeSeconds() - 3600, DeltaP = 2000, DeltaQ = 10, TransientRatio = 1, SignatureId = sig.Id },
            new MeterEventEntity { PlugId = main, Ts = now.ToUnixTimeSeconds() - 3400, DeltaP = -2000, DeltaQ = -10, TransientRatio = 1, SignatureId = sig.Id });
        var boiler = Guid.NewGuid();
        ctx.HeatingDevices.Add(new HeatingDeviceEntity { Id = boiler, Provider = "viessmann", InstallationId = "1000001", GatewaySerial = "1111222233334444",
            DeviceId = "0", ModelId = "E3_Vitodens_050_0822", Name = "Vitodens 050", LastSeenUtc = now.ToUnixTimeSeconds() });
        for (var i = 0; i < 30; i++)
            ctx.HeatingReadings.Add(new HeatingReadingEntity { DeviceId = boiler, Ts = now.ToUnixTimeSeconds() - 120 * i, BoilerTempC = 45, BurnerActive = i % 3 == 0,
                BurnerStarts = 1000 + i / 3, BurnerHours = 500 + i * 0.01, ActiveError = i == 10 ? "F.4" : null, GasHeatingTodayM3 = 1.2, PowerTodayKwh = 0.3 });
        for (var d = 0; d < 20; d++)
            ctx.HeatingEnergyDaily.Add(new HeatingEnergyDailyEntity { DeviceId = boiler, Day = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-d).ToString("yyyy-MM-dd"),
                GasHeatingMaxM3 = 2.5, GasHeatingLastM3 = 2.4, GasDhwMaxM3 = 0.4, HeatHeatingKwh = 20, PowerHeatingKwh = 0.6, Source = "readings",
                UpdatedTs = now.ToUnixTimeSeconds() });
        var sensor = Guid.NewGuid();
        ctx.ClimateSensors.Add(new ClimateSensorEntity { Id = sensor, Source = "govee", ExternalId = "AA:BB:CC:DD:EE:FF:00:31", Name = "Baie", Room = "Baie",
            LastSeenUtc = now.ToUnixTimeSeconds() });
        for (var i = 0; i < 48; i++)
            ctx.ClimateReadings.Add(new ClimateReadingEntity { SensorId = sensor, Ts = now.ToUnixTimeSeconds() - 1800 * i, TemperatureC = 21 + i % 3, HumidityPercent = 50 });
        await ctx.SaveChangesAsync();
    }
}
