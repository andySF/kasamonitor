using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class HeatingRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadingChanges_TheRawQuery_FirstRowGapsAndErrorChangesOnly()
    {
        // The only raw SQL of the app (window functions); portable since 0.12.0, checked here on SQLite and on SQL Server
        // in KasaMonitor.SqlServer.Tests.
        using var db = new TestDbFactory();
        var device = Guid.NewGuid();
        var other = Guid.NewGuid();
        using (var ctx = db.CreateContext())
        {
            var t0 = T0.ToUnixTimeSeconds();
            foreach (var (dev, ts, err) in new (Guid, long, string?)[]
                     { (device, t0, null), (device, t0 + 120, " "), (device, t0 + 240, "F.4 "), (device, t0 + 360, "F.4"), (device, t0 + 2000, "F.4"),
                       (device, t0 + 2120, null), (other, t0 + 60, "X") })
                ctx.HeatingReadings.Add(new KasaMonitor.Data.Entities.HeatingReadingEntity { DeviceId = dev, Ts = ts, ActiveError = err });
            await ctx.SaveChangesAsync();
        }

        var changes = await new HeatingRepository(db.CreateContext()).ListReadingChangesAsync(device, T0, T0.AddHours(1), TimeSpan.FromMinutes(10), CancellationToken.None);

        changes.Select(c => (long)(c.TsUtc - T0).TotalSeconds).Should().Equal(0, 240, 2000, 2120);
        changes[0].PreviousTsUtc.Should().BeNull();
        changes[1].ActiveError.Should().Be("F.4", "trimmed");
        changes[1].PreviousError.Should().BeNull("blank is no error");
        changes[2].PreviousTsUtc.Should().Be(T0.AddSeconds(360), "a gap longer than 10 minutes");
        changes[3].ActiveError.Should().BeNull();
    }

    [Fact]
    public async Task Device_UpsertIsByProviderInstallationGatewayAndDevice()
    {
        using var db = new TestDbFactory();
        var repo = new HeatingRepository(db.CreateContext());
        var d = await repo.UpsertDeviceAsync(new HeatingDevice(Guid.Empty, "viessmann", "1000001", "1111222233334444", "0", "E3_Vitodens_050_0822",
            "Vitodens 050", true, null, null), CancellationToken.None);
        var again = await new HeatingRepository(db.CreateContext()).UpsertDeviceAsync(d with { Id = Guid.NewGuid(), Name = "ignored" }, CancellationToken.None);

        again.Id.Should().Be(d.Id);
        again.Name.Should().Be("Vitodens 050");
        await repo.UpdateDeviceStatusAsync(d.Id, T0, "gateway offline", CancellationToken.None);
        (await new HeatingRepository(db.CreateContext()).ListDevicesAsync(CancellationToken.None)).Single()
            .Should().Match<HeatingDevice>(x => x.LastSeenUtc == T0 && x.LastError == "gateway offline");
    }

    [Fact]
    public async Task SetDailyGas_OverwritesOnePurpose_CreatesTheRowWhenMissing()
    {
        using var db = new TestDbFactory();
        var id = Guid.NewGuid();
        var d1 = new DateOnly(2026, 11, 1);
        var d2 = new DateOnly(2026, 11, 2);
        await new HeatingRepository(db.CreateContext()).UpsertDailyAsync(new[]
        {
            new HeatingEnergyDay(id, d1, 1.4, 0.1, 12, null, null, null, "summary", 0.7, 0.1),
        }, CancellationToken.None);

        await new HeatingRepository(db.CreateContext()).SetDailyGasAsync(id, d1, "heating", 0.7, 0.7, CancellationToken.None);
        await new HeatingRepository(db.CreateContext()).SetDailyGasAsync(id, d2, "dhw", 0.0, 0.0, CancellationToken.None);

        var days = await new HeatingRepository(db.CreateContext()).ListDailyAsync(id, d1, d2, CancellationToken.None);
        days.Select(d => (d.Day, d.GasHeatingMaxM3, d.GasHeatingLastM3, d.GasDhwMaxM3, d.GasDhwLastM3, d.HeatHeatingKwh)).Should().Equal(
            (d1, (double?)0.7, (double?)0.7, (double?)0.1, (double?)0.1, (double?)12),
            (d2, (double?)null, (double?)null, (double?)0.0, (double?)0.0, (double?)null));
        var act = () => new HeatingRepository(db.CreateContext()).SetDailyGasAsync(id, d1, "power", 1, 1, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SetDailyKwh_SetsOrClearsOneValue_MissingRowIsLeftAlone()
    {
        using var db = new TestDbFactory();
        var id = Guid.NewGuid();
        var d1 = new DateOnly(2026, 11, 1);
        await new HeatingRepository(db.CreateContext()).UpsertDailyAsync(new[]
        {
            new HeatingEnergyDay(id, d1, 1.4, 0.1, 14, 1, 0.3, 0.02, "summary", 1.4, 0.1),
        }, CancellationToken.None);

        await new HeatingRepository(db.CreateContext()).SetDailyKwhAsync(id, d1, "heat", "heating", null, CancellationToken.None);
        await new HeatingRepository(db.CreateContext()).SetDailyKwhAsync(id, d1, "power", "dhw", 0.05, CancellationToken.None);
        await new HeatingRepository(db.CreateContext()).SetDailyKwhAsync(id, d1.AddDays(1), "heat", "dhw", 3, CancellationToken.None);

        var days = await new HeatingRepository(db.CreateContext()).ListDailyAsync(id, d1, d1.AddDays(1), CancellationToken.None);
        days.Select(d => (d.Day, d.HeatHeatingKwh, d.HeatDhwKwh, d.PowerHeatingKwh, d.PowerDhwKwh, d.GasHeatingMaxM3)).Should().Equal(
            (d1, (double?)null, (double?)1, (double?)0.3, (double?)0.05, (double?)1.4));
        var act = () => new HeatingRepository(db.CreateContext()).SetDailyKwhAsync(id, d1, "gas", "heating", 1, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Daily_KeepsHighestAndLast_TotalUsesLastForTodayAndHighestForClosedDays()
    {
        using var db = new TestDbFactory();
        var repo = new HeatingRepository(db.CreateContext());
        var id = Guid.NewGuid();
        var d1 = new DateOnly(2026, 10, 31);
        var d2 = new DateOnly(2026, 11, 1);
        async Task<IReadOnlyList<HeatingDayDecrease>> Up(DateOnly day, double? gh, double? gd, double? hh = null) =>
            await new HeatingRepository(db.CreateContext()).UpsertDailyAsync(new[] { new HeatingEnergyDay(id, day, gh, gd, hh, null, null, null, "summary") }, CancellationToken.None);

        (await Up(d1, 3.0, 0.5, 30)).Should().BeEmpty();
        (await Up(d1, 2.0, null)).Should().ContainSingle().Which.Should().Be(new HeatingDayDecrease(id, d1, "heating", 3.0, 3.0, 2.0));
        (await Up(d2, 1.3, 0.1, 14)).Should().BeEmpty();
        (await Up(d2, 1.5, 0.1, 15)).Should().BeEmpty();
        (await Up(d2, 0.2, 0.1, 15)).Should().ContainSingle().Which.Should().Be(new HeatingDayDecrease(id, d2, "heating", 1.5, 1.5, 0.2));

        var days = await repo.ListDailyAsync(id, d1, d2, CancellationToken.None);
        days.Select(d => (d.GasHeatingMaxM3, d.GasHeatingLastM3)).Should().Equal((3.0, 2.0), (1.5, 0.2));
        days[0].GasDhwMaxM3.Should().Be(0.5, "a null value keeps the stored one");
        days[0].GasDhwLastM3.Should().Be(0.5);
        days[0].GasHeatingFor(d2).Should().Be(3.0, "a closed day counts with its highest value");
        days[1].GasHeatingFor(d2).Should().Be(0.2, "today counts with the last value");

        var totals = await repo.GetTotalsAsync(id, d2, CancellationToken.None);
        totals.GasHeatingM3.Should().BeApproximately(3.2, 1e-9);
        totals.GasDhwM3.Should().BeApproximately(0.6, 1e-9);
        totals.HeatHeatingKwh.Should().Be(45);
        // The next day d2 is closed too: its highest value counts.
        (await repo.GetTotalsAsync(id, d2.AddDays(1), CancellationToken.None)).GasHeatingM3.Should().BeApproximately(4.5, 1e-9);
    }

    [Fact]
    public async Task ReadingsRetention_DeletesOnlyOlderRows()
    {
        using var db = new TestDbFactory();
        var repo = new HeatingRepository(db.CreateContext());
        var id = Guid.NewGuid();
        var r = new HeatingReading(id, T0, null, 20, 36.2, null, null, null, null, null, false, 0, 0, null, 1, 1, null, null, null, null, null);
        await repo.SaveReadingAsync(r with { TsUtc = T0.AddDays(-40) }, CancellationToken.None);
        await repo.SaveReadingAsync(r, CancellationToken.None);

        (await repo.DeleteReadingsBeforeAsync(T0.AddDays(-30), CancellationToken.None)).Should().Be(1);
        (await repo.ListReadingsAsync(id, T0.AddDays(-50), T0, CancellationToken.None)).Should().ContainSingle().Which.TsUtc.Should().Be(T0);
    }

    [Fact]
    public async Task Periods_AndRawDump_AreOverwritten()
    {
        using var db = new TestDbFactory();
        var repo = new HeatingRepository(db.CreateContext());
        var id = Guid.NewGuid();
        await repo.UpsertPeriodsAsync(new[] { new HeatingEnergyPeriod(id, "gas", "dhw", "month", 0.1, "m3", T0) }, CancellationToken.None);
        await repo.UpsertPeriodsAsync(new[] { new HeatingEnergyPeriod(id, "gas", "dhw", "month", 0.4, "m3", T0.AddMinutes(2)) }, CancellationToken.None);
        await repo.SaveRawAsync(id, T0, "{\"data\":[1]}", CancellationToken.None);
        await repo.SaveRawAsync(id, T0.AddMinutes(2), "{\"data\":[2]}", CancellationToken.None);

        (await repo.ListPeriodsAsync(id, CancellationToken.None)).Single().Should().Be(new HeatingEnergyPeriod(id, "gas", "dhw", "month", 0.4, "m3", T0.AddMinutes(2)));
        (await repo.GetRawAsync(id, CancellationToken.None)).Should().Be((T0.AddMinutes(2), "{\"data\":[2]}"));
        using var ctx = db.CreateContext();
        ctx.HeatingRawFeatures.Count().Should().Be(1);
    }

    [Fact]
    public async Task Readings_RoundTrip()
    {
        using var db = new TestDbFactory();
        var repo = new HeatingRepository(db.CreateContext());
        var id = Guid.NewGuid();
        var r = new HeatingReading(id, T0, null, 20, 36.2, null, null, 23.7, 34.1, 45, false, 0, 0, null, 31089, 2891, "heating", "normal", "eco", null, "S.6");
        await repo.SaveReadingAsync(r, CancellationToken.None);
        await repo.SaveReadingAsync(r with { TsUtc = T0.AddMinutes(2), BurnerActive = true, ModulationPct = 40 }, CancellationToken.None);

        var list = await repo.ListReadingsAsync(id, T0, T0.AddMinutes(5), CancellationToken.None);
        list.Should().HaveCount(2);
        list[0].Should().Be(r);
        list[1].ModulationPct.Should().Be(40);
    }
}
