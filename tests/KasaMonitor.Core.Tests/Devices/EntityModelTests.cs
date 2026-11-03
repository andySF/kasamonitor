using System.Reflection;
using FluentAssertions;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using Xunit;

namespace KasaMonitor.Core.Tests.Devices;

/// <summary>0.11, F1: the common capability model — classes and units, the rendering rules, the projections of the existing integrations.</summary>
public class EntityModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(EntityKind.Sensor, "temperature", DeviceClass.Temperature)]
    [InlineData(EntityKind.Sensor, "atmospheric_pressure", DeviceClass.Pressure)]
    [InlineData(EntityKind.Sensor, "battery", DeviceClass.Battery)]
    [InlineData(EntityKind.Binary, "battery", DeviceClass.BatteryLow)]
    [InlineData(EntityKind.Binary, "power", DeviceClass.PowerPresent)]
    [InlineData(EntityKind.Binary, "door", DeviceClass.Door)]
    [InlineData(EntityKind.Binary, "moisture", DeviceClass.Moisture)]
    [InlineData(EntityKind.Sensor, "", DeviceClass.None)]
    public void FromHa_MapsTheSensorAndTheBinaryLists_Separately(EntityKind kind, string ha, DeviceClass expected) =>
        DeviceClasses.FromHa(kind, ha).Class.Should().Be(expected);

    [Fact]
    public void FromHa_UnknownClass_IsGeneric_AndKeepsTheOriginalText()
    {
        var (cls, raw) = DeviceClasses.FromHa(EntityKind.Sensor, "sulphur_dioxide");
        cls.Should().Be(DeviceClass.None);
        raw.Should().Be("sulphur_dioxide");
    }

    [Theory]
    [InlineData(DeviceClass.Power, "kW", 1.5, 1500, "W")]
    [InlineData(DeviceClass.Power, "mW", 2500, 2.5, "W")]
    [InlineData(DeviceClass.Energy, "kWh", 12.345, 12345, "Wh")]
    [InlineData(DeviceClass.Temperature, "°F", 68, 20, "°C")]
    [InlineData(DeviceClass.Temperature, "K", 293.15, 20, "°C")]
    [InlineData(DeviceClass.Current, "mA", 450, 0.45, "A")]
    [InlineData(DeviceClass.Pressure, "kPa", 101.3, 1013, "hPa")]
    [InlineData(DeviceClass.Duration, "min", 2, 120, "s")]
    public void Normalize_ConvertsIntoTheCanonicalUnit(DeviceClass cls, string unit, double value, double expected, string canonical)
    {
        var (c, u, conv) = DeviceClasses.Normalize(cls, unit);
        c.Should().Be(cls);
        u.Should().Be(canonical);
        conv.Apply(value).Should().BeApproximately(expected, 1e-6);
    }

    [Fact]
    public void Normalize_AnUnknownUnit_PassesThroughAndTheClassBecomesGeneric()
    {
        var (c, u, conv) = DeviceClasses.Normalize(DeviceClass.Power, "hp");
        c.Should().Be(DeviceClass.None);
        u.Should().Be("hp");
        conv.IsIdentity.Should().BeTrue();
        // A percentage class without a unit is canonical; a temperature without one is not trusted.
        DeviceClasses.Normalize(DeviceClass.Battery, null).Unit.Should().Be("%");
        DeviceClasses.Normalize(DeviceClass.Temperature, null).Class.Should().Be(DeviceClass.None);
        // A class without a canonical unit keeps the unit given.
        DeviceClasses.Normalize(DeviceClass.Water, "L").Should().Be((DeviceClass.Water, "L", UnitConversion.Identity));
    }

    [Fact]
    public void ConvertPrecision_DropsTheDigitsTheFactorAdded()
    {
        DeviceClasses.ConvertPrecision(3, new UnitConversion(1000)).Should().Be(0);
        DeviceClasses.ConvertPrecision(2, new UnitConversion(0.001)).Should().Be(5);
        DeviceClasses.ConvertPrecision(1, new UnitConversion(5.0 / 9, -32 * 5.0 / 9)).Should().Be(1, "an offset conversion keeps the precision");
    }

    [Fact]
    public void BinaryText_IsRomanianPerClass()
    {
        DeviceClasses.BinaryText(DeviceClass.Door, true).Should().Be("deschis");
        DeviceClasses.BinaryText(DeviceClass.Door, false).Should().Be("închis");
        DeviceClasses.BinaryText(DeviceClass.Moisture, true).Should().Be("ud");
        DeviceClasses.BinaryText(DeviceClass.BatteryLow, true).Should().Be("descărcată");
        DeviceClasses.BinaryText(DeviceClass.Connectivity, false).Should().Be("deconectat");
        DeviceClasses.IsAlert(DeviceClass.BatteryLow, true).Should().BeTrue();
        DeviceClasses.IsAlert(DeviceClass.Door, true).Should().BeFalse();
    }

    private static DeviceEntity Sensor(StateClass? sc, DeviceClass cls = DeviceClass.Temperature, EntityCategory cat = EntityCategory.Primary,
        EntityDataType type = EntityDataType.Number) =>
        new("dev:1/x", "X", EntityKind.Sensor, type, cls, null, "°C", sc, 1, false, cat);

    [Fact]
    public void ViewRules_MeasurementIsAChartWithHistory_CounterIsAConsumption_PlainNumberHasNoChart()
    {
        EntityViewRules.For(Sensor(StateClass.Measurement), null).Should().Be(new EntityViewDecision(EntityView.Chart, EntitySection.Main, true, false));
        EntityViewRules.For(Sensor(StateClass.TotalIncreasing, DeviceClass.Energy), null).View.Should().Be(EntityView.Counter);
        EntityViewRules.For(Sensor(StateClass.Total, DeviceClass.Energy), null).History.Should().BeTrue();
        var plain = EntityViewRules.For(Sensor(null), null);
        plain.View.Should().Be(EntityView.Value);
        plain.History.Should().BeFalse("a number without a state class gets a chart only when ticked „Istoric”");
        EntityViewRules.For(Sensor(null), new EntityPrefs("dev:1/x", InHistory: true)).History.Should().BeTrue();
        EntityViewRules.For(Sensor(StateClass.Measurement), new EntityPrefs("dev:1/x", InHistory: false)).History.Should().BeFalse();
    }

    [Fact]
    public void ViewRules_TextBinaryCommandUnsupported_AndTheSections()
    {
        EntityViewRules.For(Sensor(null, DeviceClass.Enum, type: EntityDataType.Enum), null).View.Should().Be(EntityView.Text);
        var binary = new DeviceEntity("dev:1/door", "Ușă", EntityKind.Binary, EntityDataType.Boolean, DeviceClass.Door);
        EntityViewRules.For(binary, null).View.Should().Be(EntityView.Binary);
        var sw = new DeviceEntity("dev:1/sw", "Releu", EntityKind.Switch, EntityDataType.Boolean, Writable: true, Command: new EntityCommand("cmd/x"));
        var cmd = EntityViewRules.For(sw, new EntityPrefs(sw.Key, CommandsEnabled: true));
        cmd.View.Should().Be(EntityView.Command);
        cmd.CommandEnabled.Should().BeFalse("R8: no command from KasaMonitor in 0.11, whatever the preference");
        var climate = new DeviceEntity("dev:1/c", "Climate", EntityKind.Unsupported, EntityDataType.Text, Problem: "neacceptat (climate)");
        EntityViewRules.For(climate, null).Section.Should().Be(EntitySection.Unsupported);

        var diag = Sensor(StateClass.Measurement, DeviceClass.SignalStrength, EntityCategory.Diagnostic);
        EntityViewRules.For(diag, null).Section.Should().Be(EntitySection.Diagnostic, "diagnostics are folded by default");
        EntityViewRules.For(diag, new EntityPrefs(diag.Key, Visible: true)).Section.Should().Be(EntitySection.Main);
        EntityViewRules.For(Sensor(StateClass.Measurement), new EntityPrefs("dev:1/x", Visible: false)).Section.Should().Be(EntitySection.Hidden);
    }

    [Fact]
    public void Defaults_MetricsOnlyForTheMainMeasuredClassesOfPrimaryEntities()
    {
        EntityDefaults.InMetrics(Sensor(StateClass.Measurement)).Should().BeTrue();
        EntityDefaults.InMetrics(Sensor(StateClass.Measurement, cat: EntityCategory.Diagnostic)).Should().BeFalse();
        EntityDefaults.InMetrics(Sensor(StateClass.Measurement, DeviceClass.SignalStrength)).Should().BeFalse();
        EntityDefaults.InMetrics(Sensor(null, DeviceClass.Enum, type: EntityDataType.Enum)).Should().BeFalse();
        EntityDefaults.Decimals(Sensor(StateClass.Measurement), new EntityPrefs("dev:1/x", PrecisionOverride: 3)).Should().Be(3);
        EntityDefaults.Name(Sensor(StateClass.Measurement), new EntityPrefs("dev:1/x", NameOverride: " Dormitor ")).Should().Be("Dormitor");
    }

    [Fact]
    public void HistoryBuckets_MinMaxAndWeightedMean_AndAGapIsNotAZero()
    {
        var from = Now;
        var points = new[]
        {
            new EntityPoint(from.AddMinutes(1), 100, 200, 300, 60),
            new EntityPoint(from.AddMinutes(2), 50, 400, 500, 20),
            EntityPoint.Of(from.AddHours(2).AddMinutes(5), 7),
        };
        var b = EntityHistoryBuckets.Build(points, from, from.AddHours(3), TimeSpan.FromHours(1));
        b.Should().HaveCount(3);
        b[0].Min.Should().Be(50);
        b[0].Max.Should().Be(500);
        b[0].Avg.Should().BeApproximately((200 * 60 + 400 * 20) / 80.0, 1e-9);
        b[1].Avg.Should().BeNull();
        b[2].Avg.Should().Be(7);
    }

    [Fact]
    public void PlugProjection_TpLinkReading_HasPowerVoltageCurrentEnergy_ButNoShellyExtras()
    {
        var set = EntityProjections.Plug("plug:1", new EmeterReading(85_000, 230_100, 400, 1_000, 10, 100), Now, true);
        var keys = set.Entities.Select(e => e.Key).ToList();
        keys.Should().Contain(new[] { "plug:1/power", "plug:1/voltage", "plug:1/current", "plug:1/energy", "plug:1/energy_today", "plug:1/online" });
        keys.Should().NotContain("plug:1/power_factor").And.NotContain("plug:1/relay");
        set.Values["plug:1/power"].Number.Should().Be(85);
        set.Values["plug:1/voltage"].Number.Should().BeApproximately(230.1, 1e-9);
        set.Entities.Single(e => e.Key == "plug:1/energy").StateClass.Should().Be(StateClass.TotalIncreasing);
        set.Entities.Single(e => e.Key == "plug:1/energy_today").Category.Should().Be(EntityCategory.Diagnostic);
        // Offline without a reading: power without a value and „online” off.
        var off = EntityProjections.Plug("plug:1", null, null, false);
        off.Entities.Select(e => e.Key).Should().Equal("plug:1/power", "plug:1/online");
        off.Values["plug:1/online"].Number.Should().Be(0);
    }

    [Fact]
    public void ClimateProjection_TemperatureHumidityBatteryOnline()
    {
        var set = EntityProjections.Climate("climate:1", 22.4, 48, 80, Now, true);
        set.Entities.Select(e => e.Key).Should().Equal("climate:1/temperature", "climate:1/humidity", "climate:1/battery", "climate:1/online");
        set.Values["climate:1/temperature"].Number.Should().Be(22.4);
        EntityProjections.Climate("climate:1", null, null, null, null, false).Entities.Should().HaveCount(2);
    }

    // ---- Reflection: every public property of the projected models is projected or excluded on purpose (audit §2.2, risk 3). ----

    /// <summary>A value for a property type that differs from the default (null / 0 / false / empty).</summary>
    private static object? Distinct(Type t, string name)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(double)) return 12.5;
        if (u == typeof(long)) return 12_345L;
        if (u == typeof(int)) return 42;
        if (u == typeof(bool)) return true;
        if (u == typeof(string)) return "x-" + name;
        if (u == typeof(DateTimeOffset)) return Now;
        if (u == typeof(EnergySummary)) return new EnergySummary(1.5, 2, 3.5, 4, 5, 6, EnergyUnit.KilowattHour, Now);
        if (u == typeof(IReadOnlyDictionary<string, double>)) return new Dictionary<string, double> { ["normal"] = 21 };
        if (u == typeof(IReadOnlyList<HeatingScheduleSlot>)) return new[] { new HeatingScheduleSlot("mon", "normal", "06:00", "22:00") };
        if (u == typeof(IReadOnlyList<HeatingMessage>)) return new[] { new HeatingMessage("x", "S.6", Now, "status") };
        if (u == typeof(IReadOnlyList<HeatingFeatureRef>)) return new[] { new HeatingFeatureRef("f", null) };
        throw new InvalidOperationException($"No test value for {t} ({name}): extend the test together with the projection.");
    }

    private static bool Differs(EntitySet a, EntitySet b) =>
        a.Entities.Count != b.Entities.Count
        || a.Values.Any(kv => !b.Values.TryGetValue(kv.Key, out var v) || v.Number != kv.Value.Number || v.Text != kv.Value.Text);

    /// <summary>For a positional record: the instance with every parameter at its default except <paramref name="only"/>.</summary>
    private static T Record<T>(string? only)
    {
        var ctor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p =>
            string.Equals(p.Name, only, StringComparison.OrdinalIgnoreCase) ? Distinct(p.ParameterType, p.Name!)
            : p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        return (T)ctor.Invoke(args);
    }

    private static IEnumerable<PropertyInfo> Settable(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.SetMethod is not null || t.GetConstructors().Any(c => c.GetParameters().Any(a => string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase))));

    [Fact]
    public void EmeterReading_EveryPropertyIsProjected()
    {
        var baseline = EntityProjections.Plug("plug:1", Record<EmeterReading>(null), Now, true);
        foreach (var p in Settable(typeof(EmeterReading)).Where(p => !EntityProjections.ExcludedEmeter.Contains(p.Name)))
            Differs(EntityProjections.Plug("plug:1", Record<EmeterReading>(p.Name), Now, true), baseline)
                .Should().BeTrue($"EmeterReading.{p.Name} must be projected or listed in ExcludedEmeter");
    }

    [Fact]
    public void MieleStateValues_EveryPropertyIsProjected()
    {
        var baseline = EntityProjections.Miele("miele:1", Record<MieleStateValues>(null), Now, true);
        foreach (var p in Settable(typeof(MieleStateValues)).Where(p => !EntityProjections.ExcludedMiele.Contains(p.Name)))
            Differs(EntityProjections.Miele("miele:1", Record<MieleStateValues>(p.Name), Now, true), baseline)
                .Should().BeTrue($"MieleStateValues.{p.Name} must be projected or listed in ExcludedMiele");
        // The computed IsRunning is projected too (a running state shows „În funcțiune: pornit”).
        var running = Record<MieleStateValues>(null) with { StatusRaw = MieleTexts.StatusRunning };
        EntityProjections.Miele("miele:1", running, Now, true).Values["miele:1/running"].Number.Should().Be(1);
    }

    [Fact]
    public void HeatingSnapshot_EveryPropertyIsProjected()
    {
        var baseline = EntityProjections.Heating("heating:1", new HeatingSnapshot(), true);
        var props = typeof(HeatingSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is not null).ToList();
        props.Should().HaveCountGreaterThan(30);
        foreach (var p in props.Where(p => !EntityProjections.ExcludedHeating.Contains(p.Name)))
        {
            var snapshot = new HeatingSnapshot();
            p.SetValue(snapshot, Distinct(p.PropertyType, p.Name));
            Differs(EntityProjections.Heating("heating:1", snapshot, true), baseline)
                .Should().BeTrue($"HeatingSnapshot.{p.Name} must be projected or listed in ExcludedHeating");
        }
        EntityProjections.ExcludedHeating.Should().OnlyContain(n => props.Any(p => p.Name == n), "an exclusion names a real property");
    }
}
