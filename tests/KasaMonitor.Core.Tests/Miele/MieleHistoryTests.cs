using FluentAssertions;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using Xunit;

namespace KasaMonitor.Core.Tests.Miele;

public class MieleLastUsedProgramsParserTests
{
    /// <summary>The spec's examples getLastUsedProgramsWithEnergyMonitoring + getLastUsedProgramsWithoutEcoFeedback (swagger.json v1.1.0).</summary>
    public const string SpecExample = """
        [
          {"programId":{"value_raw":13,"value_localized":"QuickPowerWash","key_localized":"Program name"},
           "startAt":"2026-04-18T08:03:40.3Z","endAt":"2026-04-18T10:05:40.3Z",
           "waterConsumption":{"unit":"l","value":46.0},"energyConsumption":{"unit":"kWh","value":0.29},
           "energyMonitoring":{"unit":"kWh","interval":"15min","data":[
             ["2026-04-18T08:00:00Z",0.009],["2026-04-18T08:15:00Z",0.13],["2026-04-18T08:30:00Z",0.045],
             ["2026-04-18T08:45:00Z",0.02],["2026-04-18T09:00:00Z",0.018],["2026-04-18T09:15:00Z",0.016],
             ["2026-04-18T09:30:00Z",0.015],["2026-04-18T09:45:00Z",0.019],["2026-04-18T10:00:00Z",0.016]]}},
          {"programId":{"value_raw":1,"value_localized":"ECO","key_localized":"Program name"},
           "startAt":"2026-04-17T18:10:00Z","endAt":"2026-04-17T21:25:00Z",
           "waterConsumption":null,"energyConsumption":null,"energyMonitoring":null}
        ]
        """;

    [Fact]
    public void SpecExamples_WithEnergyMonitoring_AndWithoutEcoFeedback_NullsStayNull()
    {
        var runs = MieleLastUsedProgramsParser.Parse(SpecExample);
        runs.Should().HaveCount(2);

        var r = runs[0];
        r.ProgramRaw.Should().Be(13);
        r.Program.Should().Be("QuickPowerWash");
        r.StartUtc.Should().Be(new DateTimeOffset(2026, 4, 18, 8, 3, 40, 300, TimeSpan.Zero));
        r.EndUtc.Should().Be(new DateTimeOffset(2026, 4, 18, 10, 5, 40, 300, TimeSpan.Zero));
        r.EnergyKwh.Should().Be(0.29);
        r.WaterL.Should().Be(46);
        r.EnergyProfile.Should().HaveCount(9);
        r.EnergyProfile![1].Should().Be(new MieleEnergySlot(new DateTimeOffset(2026, 4, 18, 8, 15, 0, TimeSpan.Zero), 0.13));
        r.EnergyProfile.Sum(s => s.Kwh).Should().BeApproximately(0.288, 1e-9);

        var eco = runs[1];
        eco.Program.Should().Be("ECO");
        eco.EnergyKwh.Should().BeNull();
        eco.WaterL.Should().BeNull();
        eco.EnergyProfile.Should().BeNull();
    }

    [Fact]
    public void EmptyHistory_NotAnArray_AndBrokenRuns_AreSkippedOrNull()
    {
        MieleLastUsedProgramsParser.Parse("[]").Should().BeEmpty();
        MieleLastUsedProgramsParser.Parse("{\"code\":404}").Should().BeEmpty();
        var runs = MieleLastUsedProgramsParser.Parse("""
            [ {"startAt":null,"endAt":"2026-04-18T10:05:40Z"},
              {"programId":null,"startAt":"2026-04-18T08:00:00Z","endAt":"2026-04-18T07:00:00Z",
               "energyConsumption":{"unit":"kWh","value":-32768},"waterConsumption":{"unit":"l"},
               "energyMonitoring":{"unit":"kWh","interval":"15min","data":[["x",1],["2026-04-18T08:00:00Z",null],["2026-04-18T08:00:00Z",0.1]]}},
              {"startAt":"2026-04-18T12:00:00+02:00","endAt":"2026-04-18T13:00:00+02:00","energyConsumption":{"unit":"Wh","value":450}} ]
            """);
        runs.Should().HaveCount(2, "a run without start is skipped");
        runs[0].Program.Should().BeNull();
        runs[0].EndUtc.Should().Be(runs[0].StartUtc, "an end before the start is clamped");
        runs[0].EnergyKwh.Should().BeNull("-32768 = no value");
        runs[0].WaterL.Should().BeNull();
        runs[0].EnergyProfile.Should().ContainSingle().Which.Kwh.Should().Be(0.1);
        runs[1].StartUtc.Should().Be(new DateTimeOffset(2026, 4, 18, 10, 0, 0, TimeSpan.Zero));
        runs[1].EnergyKwh.Should().Be(0.45, "Wh are converted to kWh");
    }

    [Fact]
    public void Url_WithAndWithoutTheEnergyProfile()
    {
        MieleApi.LastUsedProgramsUrl("000148508574", true).Should().Be("https://api.mcs3.miele.com/v1/devices/000148508574/lastUsedPrograms?calculateEnergyMonitoring=true");
        MieleApi.LastUsedProgramsUrl("000148508574", false).Should().Be("https://api.mcs3.miele.com/v1/devices/000148508574/lastUsedPrograms");
    }
}

public class MieleHistoryMergeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private const string Dev = "dev";

    private static MieleCycle Observed(long id, DateTimeOffset start, DateTimeOffset? end, string? program = "Cottons", double? kwh = 0.8, double? water = 40) =>
        new(id, Dev, start, end, end ?? start.AddMinutes(30), 1, program,
            new[] { new MielePhaseSpan(260, "Spălare principală", start, end) }, kwh, water, 950, end is null ? null : MieleEndReasons.End);

    private static MieleProgramRun Run(DateTimeOffset start, DateTimeOffset end, double? kwh = 0.9, double? water = 46, string? program = "Cottons API",
        IReadOnlyList<MieleEnergySlot>? profile = null) =>
        new(5, program, start, end, kwh, water, profile);

    [Fact]
    public void StartWithin10Minutes_MergesIntoTheObservedCycle_ThreeSeparatePairs_EcoFeedbackPlugAndPhasesStay()
    {
        var c = Observed(7, T0, T0.AddHours(2), program: null);
        var profile = new[] { new MieleEnergySlot(T0, 0.3), new MieleEnergySlot(T0.AddMinutes(15), 0.2) };
        var plan = MieleHistoryMerge.Plan(Dev, new[] { c }, new[] { Run(T0.AddMinutes(9).AddSeconds(59.7), T0.AddHours(2).AddMinutes(5), profile: profile) });

        plan.Inserts.Should().BeEmpty();
        var m = plan.Updates.Should().ContainSingle().Subject;
        m.Id.Should().Be(7);
        m.Source.Should().Be(MieleCycleSources.Merged);
        m.ApiStartUtc.Should().Be(T0.AddMinutes(9).AddSeconds(59), "whole seconds, the dedupe key");
        m.ApiEndUtc.Should().Be(T0.AddHours(2).AddMinutes(5));
        m.StartUtc.Should().Be(T0, "the observed times stay");
        m.EndUtc.Should().Be(T0.AddHours(2));
        m.ApiEnergyKwh.Should().Be(0.9);
        m.ApiWaterL.Should().Be(46);
        m.EnergyKwh.Should().Be(0.8, "the ecoFeedback value seen live is never overwritten");
        m.WaterL.Should().Be(40);
        m.PlugEnergyWh.Should().Be(950);
        m.MieleEnergyKwh.Should().Be(0.9, "Miele's best value = the API history's");
        m.Phases.Should().HaveCount(1);
        m.Program.Should().Be("Cottons API", "the observed cycle had no program");
        m.EnergyProfile.Should().BeEquivalentTo(profile);
    }

    [Fact]
    public void ObservedProgramKept_ApiNulls_MieleValueFallsBackToEcoFeedback()
    {
        var plan = MieleHistoryMerge.Plan(Dev, new[] { Observed(1, T0, T0.AddHours(1)) }, new[] { Run(T0.AddMinutes(-3), T0.AddHours(1), kwh: null, water: null) });
        var m = plan.Updates.Should().ContainSingle().Subject;
        m.Program.Should().Be("Cottons");
        m.ApiEnergyKwh.Should().BeNull();
        m.ApiWaterL.Should().BeNull();
        m.EnergyKwh.Should().Be(0.8);
        m.MieleEnergyKwh.Should().Be(0.8);
        m.MieleWaterL.Should().Be(40);
        m.Source.Should().Be(MieleCycleSources.Merged);
    }

    [Fact]
    public void StartsFarApart_ButOverlapAtLeastHalf_Merges_LessThanHalf_DoesNot()
    {
        // Observed 09:00–11:00 (seen late, e.g. after a reconnect); API 08:40–10:40: 100 of 120 min overlap.
        var c = Observed(3, T0, T0.AddHours(2));
        var plan = MieleHistoryMerge.Plan(Dev, new[] { c }, new[] { Run(T0.AddMinutes(-20), T0.AddMinutes(100)) });
        plan.Updates.Should().ContainSingle().Which.Id.Should().Be(3);
        plan.Inserts.Should().BeEmpty();

        // API 10:30–12:30 overlaps 30 of 120 min: another run.
        MieleHistoryMerge.Matches(c, Run(T0.AddMinutes(90), T0.AddMinutes(210))).Should().BeFalse();
        MieleHistoryMerge.Matches(c, Run(T0.AddMinutes(60), T0.AddMinutes(180))).Should().BeTrue("60 of 120 min = 50 %");
    }

    [Fact]
    public void RunWithoutPair_BecomesAnApiCycle_WithoutPhases()
    {
        var plan = MieleHistoryMerge.Plan(Dev, new[] { Observed(1, T0, T0.AddHours(2)) }, new[] { Run(T0.AddDays(-1), T0.AddDays(-1).AddHours(2)) });
        plan.Updates.Should().BeEmpty();
        var a = plan.Inserts.Should().ContainSingle().Subject;
        a.Id.Should().Be(0);
        a.DeviceId.Should().Be(Dev);
        a.Source.Should().Be(MieleCycleSources.Api);
        a.StartUtc.Should().Be(T0.AddDays(-1));
        a.EndUtc.Should().Be(T0.AddDays(-1).AddHours(2));
        a.ApiStartUtc.Should().Be(a.StartUtc);
        a.Phases.Should().BeEmpty();
        a.ApiEnergyKwh.Should().Be(0.9);
        a.ApiWaterL.Should().Be(46);
        a.EnergyKwh.Should().BeNull("no ecoFeedback for a run KasaMonitor did not see");
        a.WaterL.Should().BeNull();
        a.PlugEnergyWh.Should().BeNull();
        a.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void SecondSync_SameRuns_NoDuplicates_OnlyChangedValuesUpdated()
    {
        var observed = Observed(1, T0, T0.AddHours(2));
        var runs = new[] { Run(T0.AddMinutes(2), T0.AddHours(2)), Run(T0.AddDays(-2), T0.AddDays(-2).AddHours(1)) };
        var first = MieleHistoryMerge.Plan(Dev, new[] { observed }, runs);
        first.Updates.Should().HaveCount(1);
        first.Inserts.Should().HaveCount(1);

        // What is stored after the first read (the insert got id 2).
        var stored = new[] { first.Updates[0], first.Inserts[0] with { Id = 2 } };
        var second = MieleHistoryMerge.Plan(Dev, stored, runs);
        second.Inserts.Should().BeEmpty("(DeviceId, ApiStartTs) is already stored");
        second.Updates.Should().BeEmpty();
        second.Unchanged.Should().Be(2);

        // The API completed a value meanwhile: only that run is updated, still no insert.
        var third = MieleHistoryMerge.Plan(Dev, stored, new[] { runs[0], runs[1] with { WaterL = 51 } });
        third.Inserts.Should().BeEmpty();
        third.Updates.Should().ContainSingle().Which.ApiWaterL.Should().Be(51);

        // The same run twice in one answer is taken once.
        MieleHistoryMerge.Plan(Dev, Array.Empty<MieleCycle>(), new[] { runs[1], runs[1] }).Inserts.Should().ContainSingle();
    }

    [Fact]
    public void RunMatchingAnOpenCycle_WaitsForTheNextRead_NeverInsertedAsDuplicate()
    {
        var open = Observed(4, T0, null);
        var plan = MieleHistoryMerge.Plan(Dev, new[] { open }, new[] { Run(T0.AddMinutes(1), T0.AddHours(2)) });
        plan.Updates.Should().BeEmpty();
        plan.Inserts.Should().BeEmpty();
        plan.Deferred.Should().Be(1);
    }

    [Fact]
    public void TwoRuns_TwoCycles_EachRunTakesItsClosestCycle()
    {
        var a = Observed(1, T0, T0.AddMinutes(60));
        var b = Observed(2, T0.AddMinutes(70), T0.AddMinutes(130));
        var plan = MieleHistoryMerge.Plan(Dev, new[] { a, b }, new[] { Run(T0.AddMinutes(72), T0.AddMinutes(131)), Run(T0.AddMinutes(1), T0.AddMinutes(61)) });
        plan.Inserts.Should().BeEmpty();
        plan.Updates.Select(u => (u.Id, u.ApiStartUtc)).Should().BeEquivalentTo(new[] { (1L, (DateTimeOffset?)T0.AddMinutes(1)), (2L, (DateTimeOffset?)T0.AddMinutes(72)) });
    }
}

public class MieleHistoryScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstReadAfterStart_ThenEvery6Hours_ReconnectionsDoNotRepeatIt()
    {
        var s = new MieleHistorySchedule();
        s.NextDue.Should().BeNull("nothing before the appliances were read");
        s.DevicesRead(T0);
        s.IsDue(T0).Should().BeTrue();
        s.Synced(T0);
        s.NextDue.Should().Be(T0.AddHours(6));
        s.DevicesRead(T0.AddMinutes(5));   // an SSE reconnection reads the appliances again
        s.NextDue.Should().Be(T0.AddHours(6), "only the first read after start or after connecting the account");
        s.IsDue(T0.AddHours(6).AddSeconds(-1)).Should().BeFalse();
        s.IsDue(T0.AddHours(6)).Should().BeTrue();

        s.Connected();   // the account was connected again
        s.DevicesRead(T0.AddHours(1));
        s.NextDue.Should().Be(T0.AddHours(1));
        s.LastSyncUtc.Should().Be(T0);
    }

    [Fact]
    public void CycleEnd_DueTwoMinutesLater_KeptWhenAnEarlierSyncHappens()
    {
        var s = new MieleHistorySchedule();
        s.DevicesRead(T0);
        s.Synced(T0);
        s.CycleEnded(T0.AddHours(1));
        s.NextDue.Should().Be(T0.AddHours(1).AddMinutes(2));
        s.Synced(T0.AddHours(1).AddMinutes(1));   // a read before the trigger does not consume it
        s.NextDue.Should().Be(T0.AddHours(1).AddMinutes(2));
        s.Synced(T0.AddHours(1).AddMinutes(2));
        s.NextDue.Should().Be(T0.AddHours(7).AddMinutes(2));
    }

    [Fact]
    public void Throttled_RetryAfterOrOneHour_DelaysEveryTrigger()
    {
        var s = new MieleHistorySchedule();
        s.DevicesRead(T0);
        s.Throttled(T0, TimeSpan.FromMinutes(17));
        s.NextDue.Should().Be(T0.AddMinutes(17));
        s.Throttled(T0, null);
        s.NextDue.Should().Be(T0.AddHours(1));
        s.CycleEnded(T0.AddMinutes(10));
        s.IsDue(T0.AddMinutes(30)).Should().BeFalse("a cycle end does not lift the limit");
        s.IsDue(T0.AddHours(1)).Should().BeTrue();
    }

    [Fact]
    public void WithoutAccount_WaitsForTheNextConnectedRead()
    {
        var s = new MieleHistorySchedule();
        s.DevicesRead(T0);
        s.CycleEnded(T0);
        s.WaitForConnection();
        s.NextDue.Should().BeNull();
        s.DevicesRead(T0.AddHours(3));
        s.NextDue.Should().Be(T0.AddHours(3));
    }
}

public class MieleEnergyComparisonTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Profile_AsAveragePower_PlugPerSlot_FromMinutesElsePolls()
    {
        var plug = Guid.NewGuid();
        var slots = new[] { new MieleEnergySlot(T0, 0.25), new MieleEnergySlot(T0.AddMinutes(15), 0.05) };
        MieleEnergyComparison.ProfilePower(slots).Select(p => ((p.TsUtc - T0).TotalMinutes, p.W)).Should().Equal((0, 1000), (15, 200), (30, 200));

        // 1200 W for the first 15 minutes (20 Wh per minute), nothing after.
        var minutes = Enumerable.Range(0, 15).Select(i => new LiveMinuteAggregate(plug, T0.AddMinutes(i), 1200, 1200, 1200, null, 20, 60, 60)).ToList();
        var cmp = MieleEnergyComparison.Slots(slots, minutes, Array.Empty<Sample>());
        cmp[0].MieleWh.Should().Be(250);
        cmp[0].PlugWh.Should().BeApproximately(300, 1e-9);
        cmp[1].PlugWh.Should().BeNull();
        MieleEnergyComparison.PlugPower(minutes, Array.Empty<Sample>(), T0, T0.AddMinutes(30)).Should().HaveCount(15).And.OnlyContain(p => p.W == 1200);

        var polls = new[] { new Sample(1, plug, T0, true, 500_000, null, null, null, null, null, null), new Sample(2, plug, T0.AddMinutes(1), false, null, null, null, null, null, null, null) };
        MieleEnergyComparison.PlugPower(Array.Empty<LiveMinuteAggregate>(), polls, T0, T0.AddMinutes(30)).Should().ContainSingle().Which.W.Should().Be(500);
        MieleEnergyComparison.ProfilePower(Array.Empty<MieleEnergySlot>()).Should().BeEmpty();
    }
}
