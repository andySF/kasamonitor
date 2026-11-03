using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Models;
using KasaMonitor.Workers.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>0.11, F1: the projections of the existing integrations (IEntitySource), the dispatch on the key prefix (R1, R2).</summary>
public class EntitySourcesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void EveryRowKeyPrefixOfTheCatalog_HasExactlyOneEntitySource()
    {
        var prefixes = typeof(DeviceRow).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Prefix", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).ToList();
        prefixes.Should().HaveCountGreaterThanOrEqualTo(4);
        // KeyPrefix is a constant expression: read it without running a constructor.
        var sources = typeof(DeviceEntityService).Assembly.GetTypes()
            .Where(t => typeof(IEntitySource).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => ((IEntitySource)RuntimeHelpers.GetUninitializedObject(t)).KeyPrefix).ToList();
        sources.Should().BeEquivalentTo(prefixes, "every kind of row of /dispozitive has its generated page, and only one source answers it");
    }

    [Theory]
    [InlineData("plug:3f2a8c1e-0000-0000-0000-000000000001", DeviceRowOwner.Plug)]
    [InlineData("miele:000111222333", DeviceRowOwner.Miele)]
    [InlineData("heating:3f2a8c1e-0000-0000-0000-000000000001", DeviceRowOwner.Heating)]
    [InlineData("climate:3f2a8c1e-0000-0000-0000-000000000001", DeviceRowOwner.Climate)]
    [InlineData("nimic:1", DeviceRowOwner.Unknown)]
    [InlineData("", DeviceRowOwner.Unknown)]
    public void OwnerOf_IsTheKeyPrefix_NeverTheCategory(string key, DeviceRowOwner owner) => DeviceRow.OwnerOf(key).Should().Be(owner);

    [Fact]
    public void TryGuid_AndTryMiele_RejectMalformedIds()
    {
        DeviceRow.TryGuid("plug:not-a-guid", DeviceRow.PlugPrefix, out _).Should().BeFalse();
        DeviceRow.TryGuid("climate:3f2a8c1e-0000-0000-0000-000000000001", DeviceRow.PlugPrefix, out _).Should().BeFalse("another owner's key");
        DeviceRow.TryMiele("miele:", out _).Should().BeFalse();
        DeviceRow.TryMiele("miele:../x", out _).Should().BeFalse();
        DeviceRow.TryMiele("miele:000111222333", out var id).Should().BeTrue();
        id.Should().Be("000111222333");
    }

    private sealed class Rig : IDisposable
    {
        private readonly TestDbFactory _db = new();
        public TestScopeFactory Scopes { get; }
        public PlugStateBroadcaster Plugs { get; } = new();
        public ClimateState Climate { get; } = new();
        public HeatingState Heating { get; } = new();
        public MieleState Miele { get; } = new();
        public ClimateRecorder ClimateRecorder { get; }
        public DeviceEntityService Service { get; }
        public DeviceCatalog Catalog { get; }

        public Rig()
        {
            Scopes = new TestScopeFactory(_db.CreateContext);
            ClimateRecorder = new ClimateRecorder(Scopes, Climate);
            Service = new DeviceEntityService(new IEntitySource[]
            {
                new PlugEntitySource(Scopes, Plugs), new ClimateEntitySource(Scopes, Climate),
                new HeatingEntitySource(Scopes, Heating), new MieleEntitySource(Scopes, Miele),
            }, Scopes);
            Catalog = new DeviceCatalog(Scopes, Plugs, Climate, Heating, Miele, ClimateRecorder, new MieleRecorder(Scopes, Miele, NullLogger<MieleRecorder>.Instance));
        }

        public T Repo<T>() where T : class => (T)Scopes.CreateScope().ServiceProvider.GetService(typeof(T))!;

        public Task<Guid> AddPlugAsync(string mac, string alias) => Repo<IPlugRepository>().UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.0.2.60"),
            new PlugInfo(mac, alias, "KP115(EU)", "1.0", "1.0.12 Build 1", "", true, -50, TimeSpan.Zero)), Now, Ct);

        public void Dispose() => _db.Dispose();
    }

    [Fact]
    public async Task R2_MalformedOrForeignKeys_AreUnknown_AndNothingIsWithdrawn()
    {
        using var rig = new Rig();
        var plugId = await rig.AddPlugAsync("aabbccddee60", "Frigider");
        foreach (var key in new[] { "plug:not-a-guid", "miele:", "miele:../x", "heating:x", "climate:", "dev:1" })
            (await rig.Catalog.SetRetiredAsync(key, true, Ct)).Should().Be("Dispozitiv necunoscut.", key);
        (await rig.Repo<IPlugRepository>().ListAllAsync(Ct)).Single(p => p.Id == plugId).IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task PlugSource_ProjectsTheLiveReading_AndThePowerHistoryComesFromTheMinuteAggregates()
    {
        using var rig = new Rig();
        var id = await rig.AddPlugAsync("aabbccddee61", "Frigider");
        rig.Plugs.Publish(new PlugLiveState(id, "aabbccddee61", "Frigider", true, new EmeterReading(85_000, 230_000, 400, 1_000, 10, 100), Now.AddSeconds(-5), "KP115(EU)"));
        var key = DeviceRow.PlugKey(id);

        var d = await rig.Service.GetAsync(key, Now, Ct);
        d.Should().NotBeNull();
        d!.Name.Should().Be("Frigider");
        d.Available.Should().BeTrue();
        d.RichPageUrl.Should().Be($"/history?plug={id}#curent");
        d.ValueOf(key + "/power")!.Number.Should().Be(85);

        var hour = new DateTimeOffset(Now.Year, Now.Month, Now.Day, Now.Hour, 0, 0, TimeSpan.Zero);
        await rig.Repo<ILiveRepository>().SaveBatchAsync(Array.Empty<LiveSample>(), new[]
        {
            new LiveMinuteAggregate(id, hour.AddHours(-2), 80, 90, 120, 231, 1.5, 60, 60),
            new LiveMinuteAggregate(id, hour.AddHours(-2).AddMinutes(1), 60, 70, 75, 229, 1.2, 60, 60),
        }, Ct);
        var h = await rig.Service.HistoryAsync(key, key + "/power", hour.AddHours(-3), hour, Ct);
        h.Should().NotBeNull().And.HaveCount(3);
        h![1].Min.Should().Be(60);
        h[1].Max.Should().Be(120);
        h[1].Avg.Should().Be(80);
        h[0].Avg.Should().BeNull();
        (await rig.Service.HistoryAsync(key, key + "/voltage", hour.AddHours(-3), hour, Ct))![1].Avg.Should().Be(230);
        (await rig.Service.HistoryAsync(key, key + "/energy", hour.AddHours(-3), hour, Ct)).Should().BeNull("no adapter for the counter in 0.11");
    }

    [Fact]
    public async Task ClimateSource_ProjectsTheSensor_AndTheHistoryComesFromTheClimateReadings()
    {
        using var rig = new Rig();
        await rig.ClimateRecorder.RecordAsync(new ClimateSample(ClimateSources.Shelly, "aabbccddee62:0", "Camera tehnică", Now.AddMinutes(-30), 21.0, 40), Ct);
        await rig.ClimateRecorder.RecordAsync(new ClimateSample(ClimateSources.Shelly, "aabbccddee62:0", "Camera tehnică", Now.AddMinutes(-1), 22.4, 48), Ct);
        var sensor = rig.Climate.Sensors.Single().Sensor;
        var key = DeviceRow.ClimateKey(sensor.Id);

        var d = await rig.Service.GetAsync(key, Now, Ct);
        d!.Integration.Should().Be(ClimateSources.Shelly);
        d.ValueOf(key + "/temperature")!.Number.Should().Be(22.4);
        d.ValueOf(key + "/humidity")!.Number.Should().Be(48);
        d.Available.Should().BeTrue();

        var h = await rig.Service.HistoryAsync(key, key + "/temperature", Now.AddHours(-1), Now.AddSeconds(1), Ct);
        h!.Select(b => b.Max).Where(v => v is not null).Should().ContainSingle().Which.Should().Be(22.4);
        h!.Select(b => b.Min).Where(v => v is not null).Single().Should().Be(21.0);
    }

    [Fact]
    public async Task Service_UnknownKeysAreNull_AndPreferencesNeverEnableCommands()
    {
        using var rig = new Rig();
        (await rig.Service.GetAsync("plug:" + Guid.NewGuid(), Now, Ct)).Should().BeNull();
        (await rig.Service.GetAsync("nimic:1", Now, Ct)).Should().BeNull();
        (await rig.Service.GetAsync("heating:" + Guid.NewGuid(), Now, Ct)).Should().BeNull();
        (await rig.Service.GetAsync("miele:000999", Now, Ct)).Should().BeNull();

        await rig.Service.SavePrefsAsync(new EntityPrefs("plug:x/relay", NameOverride: "Bec", CommandsEnabled: true), Ct);
        var prefs = await rig.Service.PrefsAsync("plug:x", Ct);
        prefs["plug:x/relay"].CommandsEnabled.Should().BeFalse("R8: „comenzi permise” stays off in 0.11");
        prefs["plug:x/relay"].NameOverride.Should().Be("Bec");
        (await rig.Service.PrefsAsync("plug:xy", Ct)).Should().BeEmpty("a device's preferences are its own key followed by '/'");
    }
}
