using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;
using KasaMonitor.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KasaMonitor.Data.Tests;

/// <summary>0.11, F2: the generic tables — devices, entities, readings (integer key, clustered on (EntityId, Ts) on SQL Server), hourly rollups, retention.</summary>
public class GenericRepositoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private static GenericEntityUpsert Entity(string uid, DeviceClass cls = DeviceClass.Temperature, string? problem = null) =>
        new(uid, "sensor", new DeviceEntity("", uid, EntityKind.Sensor, EntityDataType.Number, cls, cls.ToString().ToLowerInvariant(), "°C",
            StateClass.Measurement, 1, false, EntityCategory.Primary, null, problem), "{\"unique_id\":\"" + uid + "\"}");

    [Fact]
    public async Task Devices_AddIsIdempotent_InfoFollowsTheDeviceUntilRenamed_AndTheOverrideIsChecked()
    {
        using var db = new TestDbFactory();
        var repo = new GenericRepository(db.CreateContext());
        var d = await repo.AddDeviceAsync(IntegrationKeys.Mqtt, "zigbee2mqtt_0xaabbccddee000001", "Dormitor senzor", "Aqara", "WSDCGQ11LM", null, null, "", DeviceCategory.ClimateSensor, T0, Ct);
        (await repo.AddDeviceAsync(IntegrationKeys.Mqtt, "zigbee2mqtt_0xaabbccddee000001", "alt nume", null, null, null, null, "", DeviceCategory.Generic, T0, Ct)).Id.Should().Be(d.Id);
        await repo.UpdateDeviceInfoAsync(d.Id, "Senzor nou", null, null, "3000-0002", null, DeviceCategory.ClimateSensor, T0.AddMinutes(1), Ct);
        (await repo.GetDeviceAsync(d.Id, Ct))!.Should().Match<GenericDevice>(x => x.Name == "Senzor nou" && x.Firmware == "3000-0002" && x.Manufacturer == "Aqara");
        var renamed = await repo.UpdateDeviceAsync(d.Id, "Dormitor", "Dormitor", DeviceCategory.Generic, Ct);
        (renamed.NameIsCustom, renamed.Category).Should().Be((true, DeviceCategory.Generic));
        await repo.UpdateDeviceInfoAsync(d.Id, "Senzor nou", null, null, null, null, DeviceCategory.ClimateSensor, T0.AddMinutes(2), Ct);
        (await repo.GetDeviceAsync(d.Id, Ct))!.Name.Should().Be("Dormitor", "a name set by the user stays");
        var act = () => repo.UpdateDeviceAsync(d.Id, "", "", DeviceCategory.Plug, Ct);
        await act.Should().ThrowAsync<ArgumentException>();
        (await repo.UpdateDeviceAsync(d.Id, "", "", null, Ct)).Should().Match<GenericDevice>(x => !x.NameIsCustom && x.CategoryOverride == null);
        (await repo.SetArchivedAsync(d.Id, true, Ct)).IsArchived.Should().BeTrue();
        (await repo.ListDevicesAsync(false, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Entities_UpsertRewritesTheDescription_RemovedKeepsTheRow_AndReadingsSetTheLastValue()
    {
        using var db = new TestDbFactory();
        var repo = new GenericRepository(db.CreateContext());
        var d = await repo.AddDeviceAsync(IntegrationKeys.Mqtt, "dev-a", "A", null, null, null, null, "", DeviceCategory.Generic, T0, Ct);
        var rows = await repo.UpsertEntitiesAsync(d.Id, IntegrationKeys.Mqtt, [Entity("a_t"), Entity("a_h", DeviceClass.Humidity)], Ct);
        rows.Should().HaveCount(2);
        await repo.UpsertEntitiesAsync(d.Id, IntegrationKeys.Mqtt, [Entity("a_t", problem: "șablon neacceptat: x")], Ct);
        var list = await repo.ListEntitiesAsync(d.Id, Ct);
        list.Single(e => e.ExternalId == "a_t").Description.Problem.Should().Be("șablon neacceptat: x");
        list.Single(e => e.ExternalId == "a_h").Description.Class.Should().Be(DeviceClass.Humidity);
        await repo.MarkEntitiesRemovedAsync(d.Id, ["a_h"], Ct);
        (await repo.ListEntitiesAsync(d.Id, Ct)).Single(e => e.ExternalId == "a_h").IsRemoved.Should().BeTrue();

        var t = list.Single(e => e.ExternalId == "a_t").Id;
        await repo.AddReadingsAsync([new GenericReading(t, T0, 21, null), new GenericReading(t, T0.AddSeconds(0.4), 21.5, null), new GenericReading(t, T0.AddMinutes(1), 22, null)], Ct);
        await repo.AddReadingsAsync([new GenericReading(t, T0, 99, null)], Ct);
        var readings = await repo.ListReadingsAsync(t, T0, T0.AddHours(1), Ct);
        readings.Select(r => r.Number).Should().Equal(21.5, 22);
        (await repo.ListEntitiesAsync(d.Id, Ct)).Single(e => e.Id == t).Should().Match<GenericEntityRow>(e => e.LastNumber == 22 && e.LastTsUtc == T0.AddMinutes(1));
        (await repo.LastReadingBeforeAsync(t, T0.AddMinutes(1), Ct))!.Number.Should().Be(21.5);
    }

    [Fact]
    public async Task Hourly_UpsertListLast_AndTheRetentionDeletesOnlyOldRawRows()
    {
        using var db = new TestDbFactory();
        var repo = new GenericRepository(db.CreateContext());
        var d = await repo.AddDeviceAsync(IntegrationKeys.Mqtt, "dev-b", "B", null, null, null, null, "", DeviceCategory.Generic, T0, Ct);
        var e = (await repo.UpsertEntitiesAsync(d.Id, IntegrationKeys.Mqtt, [Entity("b_p", DeviceClass.Power)], Ct)).Single().Id;
        await repo.UpsertHourlyAsync(e, [new GenericHour(T0, 1, 2, 3, 3, 4, null), new GenericHour(T0.AddHours(1), 2, 3, 4, 4, 1, null)], Ct);
        await repo.UpsertHourlyAsync(e, [new GenericHour(T0.AddHours(1), 2, 3.5, 5, 5, 2, null)], Ct);
        var hours = await repo.ListHourlyAsync([e], T0, T0.AddHours(2), Ct);
        hours.Select(h => h.Hour.Avg).Should().Equal(2, 3.5);
        (await repo.LastHourlyAsync(Ct))[e].Should().Be(T0.AddHours(1));

        await repo.AddReadingsAsync(Enumerable.Range(0, 30).Select(i => new GenericReading(e, T0.AddDays(-100).AddMinutes(i), i, null))
            .Append(new GenericReading(e, T0, 1, null)).ToList(), Ct);
        (await repo.DeleteReadingsBeforeAsync(T0.AddDays(-90), batchSize: 7, Ct)).Should().Be(30);
        (await repo.ListReadingsAsync(e, T0.AddDays(-200), T0.AddDays(1), Ct)).Should().ContainSingle();
        (await repo.ListHourlyAsync([e], T0, T0.AddHours(2), Ct)).Should().HaveCount(2, "the hourly rows are kept");
    }

    [Fact]
    public async Task R10_OnlyFiniteNumbersAndBoundedTextsAreWritten()
    {
        using var db = new TestDbFactory();
        var repo = new GenericRepository(db.CreateContext());
        var d = await repo.AddDeviceAsync(IntegrationKeys.Mqtt, "dev-f", new string('N', 500), new string('M', 500), null, null, null, new string('R', 300),
            DeviceCategory.Generic, T0, Ct);
        (d.Name.Length, d.Room.Length, d.Manufacturer!.Length).Should().Be((200, 100, 200));
        var e = (await repo.UpsertEntitiesAsync(d.Id, IntegrationKeys.Mqtt, [Entity("f_t")], Ct)).Single();
        await repo.AddReadingsAsync([new GenericReading(e.Id, T0, double.NaN, null), new GenericReading(e.Id, T0.AddMinutes(1), double.PositiveInfinity, new string('x', 1000))], Ct);
        var rows = await repo.ListReadingsAsync(e.Id, T0, T0.AddHours(1), Ct);
        rows.Select(r => r.Number).Should().Equal(new double?[] { null, null });
        rows[1].Text!.Length.Should().Be(255);
        await repo.UpsertHourlyAsync(e.Id, [new GenericHour(T0, double.NegativeInfinity, double.NaN, 3, 3, 1, double.PositiveInfinity)], Ct);
        (await repo.ListHourlyAsync([e.Id], T0, T0.AddHours(1), Ct)).Single().Hour.Should().Be(new GenericHour(T0, null, null, 3, 3, 1, null));
    }

}
