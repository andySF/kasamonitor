using FluentAssertions;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class MieleHistorySyncTests
{
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.f'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Run(DateTimeOffset start, DateTimeOffset end, double? kwh, double? water, string program = "Cottons", string? profile = null) =>
        "{\"programId\":{\"value_raw\":1,\"value_localized\":\"" + program + "\",\"key_localized\":\"Program name\"},"
        + "\"startAt\":\"" + Iso(start) + "\",\"endAt\":\"" + Iso(end) + "\","
        + "\"waterConsumption\":" + (water is { } w ? "{\"unit\":\"l\",\"value\":" + w.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" : "null") + ","
        + "\"energyConsumption\":" + (kwh is { } k ? "{\"unit\":\"kWh\",\"value\":" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" : "null") + ","
        + "\"energyMonitoring\":" + (profile ?? "null") + "}";

    [Fact]
    public async Task FirstRead_ThenAfterACycle_MergesAndAdds_NoDuplicatesAtTheNextRead_MetricsCountEachRunOnce()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync();
        var plug = Guid.NewGuid();
        var t = MieleRig.T0;
        using (var ctx = rig.Db.CreateContext())
        {
            // The plug measured 300 W (5 Wh per minute) yesterday 20:00–21:00, the time of a run KasaMonitor did not see.
            var minutes = Enumerable.Range(0, 60).Select(i => new LiveMinuteAggregate(plug, t.AddHours(-13).AddMinutes(i), 300, 300, 300, null, 5, 60, 60)).ToList();
            await new LiveRepository(ctx).SaveBatchAsync(Array.Empty<LiveSample>(), minutes, CancellationToken.None);
        }
        var history = rig.History();
        rig.Client.Devices = () => MieleRig.Json(4, 256);
        rig.Client.LastUsed = (_, _) => "[]";
        await rig.Worker().FullReadAsync(CancellationToken.None);
        await rig.Repo(r => r.SetPlugAsync(MieleRig.Device, plug, CancellationToken.None).ContinueWith(_ => 0));
        await rig.Recorder.ReloadDeviceAsync(MieleRig.Device, CancellationToken.None);
        // An archived appliance is never asked for.
        await rig.Repo(async r =>
        {
            await r.UpsertDeviceAsync("000999888777", new MieleIdent(2, "Tumble dryer", "Old dryer", "T1"), t, CancellationToken.None);
            await r.SetArchivedAsync("000999888777", true, CancellationToken.None);
            return 0;
        });

        history.Schedule.IsDue(rig.Now).Should().BeTrue("the first read of the appliances after start");
        (await history.RunDueAsync(CancellationToken.None)).Should().BeTrue();
        rig.Client.Calls.Should().Equal("devices", "lastUsed " + MieleRig.Device + " em");
        history.Schedule.NextDue.Should().Be(rig.Now.AddHours(6));
        rig.State.HistorySyncUtc.Should().Be(rig.Now);

        // A cycle observed 09:10–10:10; it ends → the history is read 2 minutes later.
        await rig.Recorder.ProcessAsync(MieleRig.Json(5, 260, 60, 0.4, 20), t.AddMinutes(10), CancellationToken.None);
        await rig.Recorder.ProcessAsync(MieleRig.Json(7, 268, 0, 0.7, 41), t.AddMinutes(70), CancellationToken.None);
        history.Schedule.NextDue.Should().Be(t.AddMinutes(72));

        var profile = "{\"unit\":\"kWh\",\"interval\":\"15min\",\"data\":[[\"" + Iso(t) + "\",0.2],[\"" + Iso(t.AddMinutes(15)) + "\",0.3]]}";
        var answer = "[" + Run(t.AddMinutes(11).AddSeconds(30), t.AddMinutes(71), 0.82, 44.5, profile: profile) + ","
                   + Run(t.AddHours(-13), t.AddHours(-12), 0.31, 39, "ECO") + "]";
        rig.Client.LastUsed = (_, _) => answer;
        rig.Client.Calls.Clear();
        rig.Now = t.AddMinutes(72);
        (await history.RunDueAsync(CancellationToken.None)).Should().BeTrue();
        rig.Client.Calls.Should().Equal("lastUsed " + MieleRig.Device + " em");

        var cycles = await rig.Repo(r => r.ListCyclesAsync(MieleRig.Device, 10, CancellationToken.None));
        cycles.Should().HaveCount(2);
        var merged = cycles[0];
        merged.Source.Should().Be(MieleCycleSources.Merged);
        merged.StartUtc.Should().Be(t.AddMinutes(10));
        merged.ApiStartUtc.Should().Be(t.AddMinutes(11).AddSeconds(30));
        merged.ApiEnergyKwh.Should().Be(0.82);
        merged.ApiWaterL.Should().Be(44.5);
        merged.EnergyKwh.Should().Be(0.7, "the ecoFeedback maximum seen live stays");
        merged.WaterL.Should().Be(41);
        merged.Phases.Should().NotBeEmpty();
        merged.EnergyProfile.Should().HaveCount(2);
        var api = cycles[1];
        api.Source.Should().Be(MieleCycleSources.Api);
        api.Program.Should().Be("ECO");
        api.Phases.Should().BeEmpty();
        api.ApiEnergyKwh.Should().Be(0.31);
        api.EnergyKwh.Should().BeNull();
        api.PlugEnergyWh.Should().BeApproximately(300, 1e-6, "the linked plug's minutes over the run");

        var live = rig.State.Appliances.Should().ContainSingle().Subject;
        live.CyclesTotal.Should().Be(2, "the API run counts, the merged one once");
        live.LastCycle!.MieleEnergyKwh.Should().Be(0.82, "miele_last_cycle_* use the API when it has the run");
        live.LastCycle.MieleWaterL.Should().Be(44.5);

        // 6 hours later the same five runs come again: nothing is duplicated.
        rig.Now = t.AddMinutes(72).AddHours(6);
        (await history.RunDueAsync(CancellationToken.None)).Should().BeTrue();
        (await rig.Repo(r => r.ListCyclesAsync(MieleRig.Device, 10, CancellationToken.None))).Should().HaveCount(2);
        rig.State.Appliances.Single().CyclesTotal.Should().Be(2);
        rig.Client.Calls.Should().NotContain(c => c.Contains("000999888777"));
    }

    [Fact]
    public async Task EnergyMonitoringNotAccepted_LoggedOnce_ReadWithoutIt_FromThenOn()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync();
        var history = rig.History();
        await rig.Recorder.ProcessAsync(MieleRig.Json(4, 256), rig.Now, CancellationToken.None);
        rig.Client.LastUsed = (_, em) => em ? throw new MieleApiException(400, "lastUsedPrograms HTTP 400: Malformed request") : "[]";

        await history.SyncAsync(CancellationToken.None);
        rig.Client.Calls.Should().Equal("lastUsed " + MieleRig.Device + " em", "lastUsed " + MieleRig.Device);
        history.WithoutEnergyMonitoring.Should().Equal(MieleRig.Device);

        rig.Client.Calls.Clear();
        await history.SyncAsync(CancellationToken.None);
        rig.Client.Calls.Should().Equal("lastUsed " + MieleRig.Device);
        rig.State.HistoryMessage.Should().BeNull();
    }

    [Fact]
    public async Task Limited429_StopsTheRead_NoCallBeforeRetryAfter_ElseOneHour()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync();
        var history = rig.History();
        await rig.Recorder.ProcessAsync(MieleRig.Json(4, 256), rig.Now, CancellationToken.None);
        await rig.Repo(r => r.UpsertDeviceAsync("000111222333", new MieleIdent(2, "Tumble dryer", "Dryer", "T1"), rig.Now, CancellationToken.None));
        rig.Client.LastUsed = (_, _) => throw new MieleApiException(429, "lastUsedPrograms HTTP 429", TimeSpan.FromMinutes(20));

        (await history.RunDueAsync(CancellationToken.None)).Should().BeTrue();
        rig.Client.Calls.Should().HaveCount(1, "nothing else is called after a 429");
        history.Schedule.NextDue.Should().Be(rig.Now.AddMinutes(20));
        rig.State.HistoryMessage.Should().Contain("limitat").And.Contain("20 min");

        rig.Now += TimeSpan.FromMinutes(19);
        (await history.RunDueAsync(CancellationToken.None)).Should().BeFalse();
        rig.Client.Calls.Should().HaveCount(1);

        rig.Now += TimeSpan.FromMinutes(1);
        rig.Client.LastUsed = (_, _) => throw new MieleApiException(503, "lastUsedPrograms HTTP 503: unavailable");
        (await history.RunDueAsync(CancellationToken.None)).Should().BeTrue();
        history.Schedule.NextDue.Should().Be(rig.Now.AddHours(1), "no Retry-After: one hour");
    }

    [Fact]
    public async Task MissingConsent403_OtherAppliancesStillRead_MessageShown()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync();
        var history = rig.History();
        await rig.Recorder.ProcessAsync(MieleRig.Json(4, 256), rig.Now, CancellationToken.None);
        await rig.Repo(r => r.UpsertDeviceAsync("000111222333", new MieleIdent(2, "Tumble dryer", "Dryer", "T1"), rig.Now, CancellationToken.None));
        rig.Client.LastUsed = (id, _) => id == "000111222333"
            ? throw new MieleApiException(403, "lastUsedPrograms HTTP 403: Appliance statistics consent is required")
            : "[]";

        await history.SyncAsync(CancellationToken.None);
        rig.Client.Calls.Should().HaveCount(2);
        rig.State.HistoryMessage.Should().Contain("Dryer").And.Contain("statistici aparat");
        history.Schedule.LastSyncUtc.Should().Be(rig.Now, "the read itself finished");
    }

    [Fact]
    public async Task NotConnected_NoCall_WaitsForTheNextConnectedRead()
    {
        using var rig = new MieleRig();
        var history = rig.History();
        await rig.Recorder.ProcessAsync(MieleRig.Json(4, 256), rig.Now, CancellationToken.None);
        history.Schedule.IsDue(rig.Now).Should().BeTrue();

        (await history.RunDueAsync(CancellationToken.None)).Should().BeTrue();
        rig.Client.Calls.Should().BeEmpty();
        history.Schedule.NextDue.Should().BeNull();
        rig.State.HistoryMessage.Should().Be("Contul Miele nu e conectat.");

        await rig.ConnectAsync();
        await rig.Recorder.ProcessAsync(MieleRig.Json(4, 256), rig.Now.AddMinutes(1), CancellationToken.None);
        history.Schedule.NextDue.Should().Be(rig.Now.AddMinutes(1));
    }
}
