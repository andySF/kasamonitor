using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Tapo;
using KasaMonitor.Web.Components.Devices;
using KasaMonitor.Web.Components.Settings;
using KasaMonitor.Workers;

namespace KasaMonitor.Web.Ui;

/// <summary>How an integration is doing, at a glance (chip on /dispozitive, header of its /settings tab).</summary>
public enum IntegrationLevel
{
    Ok,
    /// <summary>„Atenție”: works partly (a device does not answer, a source reports a problem).</summary>
    Warning,
    Error,
    /// <summary>Switched off on purpose.</summary>
    Off,
    /// <summary>Missing what it needs to start (Client ID, key, coordinates, connection).</summary>
    NotConfigured,
}

/// <summary>
/// The state of one integration, projected from the states that already exist (<c>PlugDiscoveryService.LastScan</c>,
/// the plugs' live states, <see cref="ClimateState"/>, <see cref="HeatingState"/>, <see cref="MieleState"/>); nothing new
/// is measured or stored.
/// </summary>
public sealed record IntegrationStatus(
    IntegrationLevel Level,
    string Text,
    DateTimeOffset? LastSuccessUtc = null,
    int? CallsToday = null,
    int? DailyLimit = null)
{
    public static string LevelName(IntegrationLevel l) => l switch
    {
        IntegrationLevel.Ok => "OK",
        IntegrationLevel.Warning => "Atenție",
        IntegrationLevel.Error => "Eroare",
        IntegrationLevel.Off => "Oprit",
        IntegrationLevel.NotConfigured => "Neconfigurat",
        _ => l.ToString(),
    };

    /// <summary>Bootstrap badge classes per level (the same colours as the other status badges of the app).</summary>
    public static string Badge(IntegrationLevel l) => l switch
    {
        IntegrationLevel.Ok => "text-bg-success",
        IntegrationLevel.Warning => "text-bg-warning",
        IntegrationLevel.Error => "text-bg-danger",
        _ => "text-bg-secondary",
    };

    public static string Icon(IntegrationLevel l) => l switch
    {
        IntegrationLevel.Ok => "bi-check-circle-fill",
        IntegrationLevel.Warning => "bi-exclamation-triangle-fill",
        IntegrationLevel.Error => "bi-x-circle-fill",
        IntegrationLevel.Off => "bi-pause-circle",
        _ => "bi-dash-circle",
    };
}

/// <summary>
/// One integration (how the app talks to a vendor's devices): its key (in code, addresses and filters), the name shown
/// (the vendor), the device categories it brings, its /settings tab, its „+ Adaugă” flow on /dispozitive, the page with
/// its data and how its state is computed.
/// </summary>
public sealed record IntegrationInfo(
    string Key,
    string Name,
    string Icon,
    IReadOnlyList<DeviceCategory> Categories,
    Type SettingsComponent,
    Type? AddComponent,
    string? DataPage,
    Func<IServiceProvider, DateTimeOffset, CancellationToken, Task<IntegrationStatus>> Status)
{
    public string SettingsUrl => SettingsTabs.Href(Key);
}

/// <summary>
/// The registry of the integrations: the single list behind the /settings tabs, the „Firmă” filter and the state chips
/// of /dispozitive and the „+ Adaugă” menu. A new integration (e.g. <c>tuya</c> for Hama) is one entry here plus its
/// settings tab, its add component and its rows in <c>DeviceCatalog</c>.
/// </summary>
public static class Integrations
{
    public static readonly IReadOnlyList<IntegrationInfo> All =
    [
        new(IntegrationKeys.TpLink, "TP-Link (Kasa, Tapo)", "bi-outlet", [DeviceCategory.Plug],
            typeof(TpLinkSettingsTab), typeof(LanAddPanel), "/prize", TpLinkAsync),
        new(IntegrationKeys.Shelly, "Shelly", "bi-speedometer", [DeviceCategory.Meter, DeviceCategory.Plug, DeviceCategory.ClimateSensor],
            typeof(ShellySettingsTab), typeof(LanAddPanel), "/contor", ShellyAsync),
        new(IntegrationKeys.Viessmann, "Viessmann", "bi-fire", [DeviceCategory.Boiler, DeviceCategory.ClimateSensor],
            typeof(ViessmannSettingsTab), typeof(CloudConnectPanel), "/centrala", ViessmannAsync),
        new(IntegrationKeys.Miele, "Miele", "bi-water", [DeviceCategory.Appliance],
            typeof(MieleSettingsTab), typeof(CloudConnectPanel), "/aparate", MieleAsync),
        new(IntegrationKeys.Govee, "Govee", "bi-thermometer-half", [DeviceCategory.ClimateSensor],
            typeof(GoveeSettingsTab), typeof(GoveeAddPanel), "/clima", GoveeAsync),
        new(IntegrationKeys.OpenMeteo, "Open-Meteo", "bi-cloud-sun", [DeviceCategory.Weather],
            typeof(OpenMeteoSettingsTab), typeof(OpenMeteoAddPanel), "/clima", OpenMeteoAsync),
        // 0.11: devices that describe themselves on a broker (Zigbee2MQTT, ESPHome); read only.
        new(IntegrationKeys.Mqtt, "MQTT", "bi-diagram-2", [DeviceCategory.Meter, DeviceCategory.Relay, DeviceCategory.ClimateSensor, DeviceCategory.Generic],
            typeof(MqttSettingsTab), typeof(MqttAddPanel), DevicePages.Href(IntegrationKeys.Mqtt), MqttAsync),
    ];

    public static IntegrationInfo? Get(string? key) =>
        All.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string NameOf(string key) => Get(key)?.Name ?? key;

    private static async Task<IntegrationStatus> TpLinkAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var s = await sp.GetRequiredService<IKasaSettingsService>().GetAsync(ct);
        var scan = sp.GetService<PlugDiscoveryService>()?.LastScan;
        var live = sp.GetRequiredService<IPlugStateBroadcaster>().Snapshot().Values;
        return IntegrationStatusRules.TpLink(s.AutoDiscoveryEnabled, s.TapoCredentials is not null, scan,
            live.Where(p => PlugProtocols.IsTpLink(p.Protocol)).ToList(), now);
    }

    private static async Task<IntegrationStatus> ShellyAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var s = await sp.GetRequiredService<IKasaSettingsService>().GetAsync(ct);
        var live = sp.GetRequiredService<IPlugStateBroadcaster>().Snapshot().Values.Where(p => p.Protocol == PlugProtocols.ShellyRpc).ToList();
        var climate = sp.GetRequiredService<ClimateState>();
        var sensors = climate.Sensors.Where(x => x.Sensor.Source == ClimateSources.Shelly).ToList();
        return IntegrationStatusRules.Shelly(s.ShellyDiscoveryEnabled, live, sensors.Count, sensors.Count(x => climate.IsOnline(x, now)),
            climate.SourceStatus(ClimateSources.Shelly), sp.GetService<PlugDiscoveryService>()?.LastScan?.AtUtc);
    }

    private static async Task<IntegrationStatus> ViessmannAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var s = await sp.GetRequiredService<IHeatingSettingsService>().GetAsync(ct);
        return IntegrationStatusRules.Viessmann(s, sp.GetRequiredService<HeatingState>(), now);
    }

    private static async Task<IntegrationStatus> MieleAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var s = await sp.GetRequiredService<IMieleSettingsService>().GetAsync(ct);
        return IntegrationStatusRules.Miele(s, sp.GetRequiredService<MieleState>(), now);
    }

    private static async Task<IntegrationStatus> GoveeAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var s = await sp.GetRequiredService<IClimateSettingsService>().GetAsync(ct);
        var climate = sp.GetRequiredService<ClimateState>();
        var sensors = climate.Sensors.Where(x => x.Sensor.Source == ClimateSources.Govee).ToList();
        return IntegrationStatusRules.Govee(s.HasGoveeKey, s.GoveeKeyProblem, s.GoveeHomeDevices.Count,
            sensors.Count(x => climate.IsOnline(x, now)), climate.SourceStatus(ClimateSources.Govee), climate.GoveeCalls.Today,
            sensors.Select(x => x.ReadUtc).Max());
    }

    private static async Task<IntegrationStatus> MqttAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var s = await sp.GetRequiredService<KasaMonitor.Workers.Mqtt.IMqttSettingsService>().GetAsync(ct);
        var state = sp.GetRequiredService<KasaMonitor.Workers.Mqtt.MqttState>();
        var recorder = sp.GetRequiredService<KasaMonitor.Workers.Mqtt.GenericRecorder>();
        await recorder.LoadAsync(ct);
        var discovered = state.Registry.Devices(now);
        var added = recorder.Devices.Where(d => !d.IsArchived).ToList();
        var online = added.Count(d => discovered.FirstOrDefault(x => x.ExternalId == d.ExternalId) is { Available: true });
        var problems = state.Registry.Problems.Count + discovered.SelectMany(d => d.Entities).Count(e => e.Spec.Problem is not null || e.ReadErrors > 0);
        return IntegrationStatusRules.Mqtt(s.Enabled, s.IsConfigured, state.Connected, s.Broker, state.Status, added.Count, online,
            discovered.Count(d => added.All(a => a.ExternalId != d.ExternalId)), problems, state.LastMessageUtc);
    }

    private static async Task<IntegrationStatus> OpenMeteoAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var s = await sp.GetRequiredService<IClimateSettingsService>().GetAsync(ct);
        var climate = sp.GetRequiredService<ClimateState>();
        var outdoor = climate.Sensors.FirstOrDefault(x => x.Sensor.Source == ClimateSources.OpenMeteo);
        return IntegrationStatusRules.OpenMeteo(s.HasLocation, climate.SourceStatus(ClimateSources.OpenMeteo),
            outdoor?.ReadUtc, outdoor is not null && climate.IsOnline(outdoor, now), climate.OpenMeteoCalls.Today);
    }
}

/// <summary>The rules of <see cref="IntegrationStatus"/>, pure (inputs = the existing states), tested in Web.Tests.</summary>
public static class IntegrationStatusRules
{
    private static string Devices(int online, int total, string one, string many) =>
        $"{online}/{total} {(total == 1 ? one : many)} răspund";

    /// <summary>
    /// TP-Link works without the account (Kasa legacy), so Tapo plugs that need it make the integration „Atenție”, not
    /// „Neconfigurat”; „Oprit” only when nothing is polled and the automatic discovery is off.
    /// </summary>
    public static IntegrationStatus TpLink(bool autoDiscovery, bool hasAccount, DiscoveryScan? scan,
        IReadOnlyList<PlugLiveState> plugs, DateTimeOffset now)
    {
        var needAccount = scan?.Items.Count(i => i.Plug.Protocol is PlugProtocols.Klap or PlugProtocols.Aes
                                                  && i.Plug.UnsupportedReason is TapoSupport.MissingCredentialsReason or TapoSupport.AuthFailedReason) ?? 0;
        var online = plugs.Count(p => p.IsOnline);
        var last = plugs.Where(p => p.IsOnline).Select(p => (DateTimeOffset?)p.LastSampleUtc).Max();
        var scanText = scan is null ? (autoDiscovery ? "nicio scanare încă" : "descoperirea automată e oprită")
            : $"ultima scanare {UiFormat.RelativeRo(scan.AtUtc, now)}";
        if (needAccount > 0)
            return new(IntegrationLevel.Warning,
                $"{needAccount} priză(e) Tapo {(hasAccount ? "refuză contul TP-Link" : "au nevoie de contul TP-Link")} · {scanText}", last);
        if (plugs.Count == 0)
            return new(autoDiscovery ? IntegrationLevel.Ok : IntegrationLevel.Off, $"nicio priză interogată · {scanText}", last);
        var level = online == plugs.Count ? IntegrationLevel.Ok : online == 0 ? IntegrationLevel.Error : IntegrationLevel.Warning;
        return new(level, $"{Devices(online, plugs.Count, "priză", "prize")} · {scanText}", last);
    }

    public static IntegrationStatus Shelly(bool discovery, IReadOnlyList<PlugLiveState> channels, int sensors, int sensorsOnline,
        string? sourceStatus, DateTimeOffset? lastScanUtc)
    {
        var online = channels.Count(p => p.IsOnline);
        var total = channels.Count + sensors;
        var answering = online + sensorsOnline;
        var last = channels.Where(p => p.IsOnline).Select(p => (DateTimeOffset?)p.LastSampleUtc).Max();
        var parts = new List<string>();
        if (channels.Count > 0) parts.Add(Devices(online, channels.Count, "canal", "canale"));
        if (sensors > 0) parts.Add(Devices(sensorsOnline, sensors, "senzor", "senzori"));
        if (!discovery) parts.Add("descoperirea mDNS e oprită");
        if (sourceStatus is not null) parts.Add(sourceStatus);
        if (total == 0)
            return new(discovery ? IntegrationLevel.Ok : IntegrationLevel.Off,
                string.Join(" · ", parts.Prepend("niciun dispozitiv Shelly")), lastScanUtc);
        var level = answering == total && sourceStatus is null ? IntegrationLevel.Ok
            : answering == 0 ? IntegrationLevel.Error : IntegrationLevel.Warning;
        return new(level, string.Join(" · ", parts), last);
    }

    public static IntegrationStatus Viessmann(HeatingRuntimeSettings s, HeatingState state, DateTimeOffset now)
    {
        var calls = state.Calls.Today;
        var last = state.Current?.LastSuccessUtc;
        if (!s.HeatingEnabled) return new(IntegrationLevel.Off, "citirea centralei e oprită", last, calls, ViessmannApi.DailyCallLimit);
        if (!s.HasClientId) return new(IntegrationLevel.NotConfigured, "lipsește Client ID-ul", last, calls, ViessmannApi.DailyCallLimit);
        if (!s.IsConnected)
            return new(s.AuthError is null ? IntegrationLevel.NotConfigured : IntegrationLevel.Error,
                s.AuthError is { } e ? $"neconectat: {e}" : "neconectat: apasă „Conectează ViCare”", last, calls, ViessmannApi.DailyCallLimit);
        if (state.IsOnline(now))
            return new(IntegrationLevel.Ok, $"conectat · ultima citire {UiFormat.RelativeRo(last!.Value, now)}", last, calls, ViessmannApi.DailyCallLimit);
        var why = state.Status ?? state.Current?.LastError ?? "fără citire recentă";
        return new(IntegrationLevel.Warning, $"conectat, dar {why}", last, calls, ViessmannApi.DailyCallLimit);
    }

    public static IntegrationStatus Miele(MieleRuntimeSettings s, MieleState state, DateTimeOffset now)
    {
        var last = state.LastReadUtc ?? state.LastEventUtc;
        if (!s.HasClientId) return new(IntegrationLevel.NotConfigured, "lipsește Client ID-ul", last);
        if (!s.HasSecret) return new(IntegrationLevel.NotConfigured, "lipsește secretul clientului (fișierul montat)", last);
        if (s.Token is null)
            return new(s.AuthError is null ? IntegrationLevel.NotConfigured : IntegrationLevel.Error,
                s.AuthError is { } e ? $"neconectat: {e}" : "neconectat: apasă „Conectează Miele”", last);
        if (s.AuthError is { } refused) return new(IntegrationLevel.Error, $"reconectare necesară: {refused}", last);
        var appliances = state.Appliances;
        var online = appliances.Count(a => state.IsOnline(a, now));
        var count = appliances.Count == 0 ? "niciun aparat" : $"{online}/{appliances.Count} {(appliances.Count == 1 ? "aparat" : "aparate")} conectate la Miele";
        if (state.StreamConnected)
            return new(IntegrationLevel.Ok, $"flux {(state.Mode == MieleStreamMode.Polling ? "citit la 60 s" : "conectat")} · {count}", last);
        var why = state.Status ?? state.LastError ?? "fluxul nu e conectat";
        return new(IntegrationLevel.Warning, $"{why} · {count}", last);
    }

    public static IntegrationStatus Govee(bool hasKey, string? keyProblem, int ticked, int online, string? sourceStatus, int callsToday,
        DateTimeOffset? lastReadUtc)
    {
        if (!hasKey)
            return new(IntegrationLevel.NotConfigured, $"lipsește cheia API{(keyProblem is null ? "" : $": {keyProblem}")}", lastReadUtc, callsToday, GoveeApi.DailyRequestLimit);
        if (sourceStatus is not null)
            return new(IntegrationLevel.Warning, sourceStatus, lastReadUtc, callsToday, GoveeApi.DailyRequestLimit);
        if (ticked == 0)
            return new(IntegrationLevel.Ok, "cheie încărcată · niciun termometru adăugat", lastReadUtc, callsToday, GoveeApi.DailyRequestLimit);
        var level = online == ticked ? IntegrationLevel.Ok : online == 0 ? IntegrationLevel.Error : IntegrationLevel.Warning;
        return new(level, Devices(online, ticked, "termometru", "termometre"), lastReadUtc, callsToday, GoveeApi.DailyRequestLimit);
    }

    /// <summary>
    /// MQTT (0.11): „Oprit” when switched off, „Neconfigurat” without a broker, „Eroare” while not connected (the reason:
    /// refused authentication, unreachable), „Atenție” when an added device does not answer or an entity cannot be read.
    /// </summary>
    public static IntegrationStatus Mqtt(bool enabled, bool configured, bool connected, string broker, string? status, int added, int online,
        int notAdded, int problems, DateTimeOffset? lastMessageUtc)
    {
        if (!enabled) return new(IntegrationLevel.Off, "citirea MQTT e oprită", lastMessageUtc);
        if (!configured) return new(IntegrationLevel.NotConfigured, "lipsește brokerul", lastMessageUtc);
        if (!connected) return new(IntegrationLevel.Error, $"neconectat la {broker}{(status is null ? "" : $": {status}")}", lastMessageUtc);
        var parts = new List<string> { $"conectat la {broker}" };
        parts.Add(added == 0 ? "niciun dispozitiv adăugat" : Devices(online, added, "dispozitiv", "dispozitive"));
        if (notAdded > 0) parts.Add($"{notAdded} descoperite neadăugate");
        if (problems > 0) parts.Add($"{problems} {(problems == 1 ? "problemă" : "probleme")} (Setări → MQTT)");
        var level = online < added || problems > 0 ? IntegrationLevel.Warning : IntegrationLevel.Ok;
        return new(level, string.Join(" · ", parts), lastMessageUtc);
    }

    public static IntegrationStatus OpenMeteo(bool hasLocation, string? sourceStatus, DateTimeOffset? lastReadUtc, bool fresh, int callsToday)
    {
        if (!hasLocation) return new(IntegrationLevel.NotConfigured, "fără coordonate: nu se face nicio cerere", lastReadUtc, callsToday);
        if (sourceStatus is not null) return new(IntegrationLevel.Warning, sourceStatus, lastReadUtc, callsToday);
        return fresh
            ? new(IntegrationLevel.Ok, "„Afară” citit la 15 min", lastReadUtc, callsToday)
            : new(IntegrationLevel.Warning, lastReadUtc is null ? "nicio citire încă" : "fără citire recentă", lastReadUtc, callsToday);
    }
}

/// <summary>
/// The /settings tabs: „Aplicație” and „Export” (the app itself), then one tab per integration of <see cref="Integrations"/>.
/// <see cref="Href"/> is the single owner of the tab addresses (links from the other pages use it).
/// </summary>
public static class SettingsTabs
{
    public const string App = "aplicatie";
    public const string Export = "export";

    public sealed record Tab(string Key, string Name, string Icon, Type Component, IntegrationInfo? Integration);

    public static readonly IReadOnlyList<Tab> All =
    [
        new(App, "Aplicație", "bi-sliders", typeof(AppSettingsTab), null),
        new(Export, "Export", "bi-diagram-3", typeof(ExportSettingsTab), null),
        .. Integrations.All.Select(i => new Tab(i.Key, i.Name, i.Icon, i.SettingsComponent, i)),
    ];

    public static Tab? Get(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string Href(string key) => $"/settings/{key}";
}

/// <summary>Single owner of the address of the devices page (links from /settings and the data pages).</summary>
public static class DevicePages
{
    public const string Path = "/dispozitive";

    /// <summary>The devices page, optionally filtered on one integration (<c>?firma=</c>).</summary>
    public static string Href(string? integration = null) =>
        integration is null ? Path : $"{Path}?firma={Uri.EscapeDataString(integration)}";

    /// <summary>The devices page with one row open (<c>?d=plug:{id}</c>, <c>climate:{id}</c>, …): the ✎ links of the data pages.</summary>
    public static string Row(string key) => $"{Path}?d={Uri.EscapeDataString(key)}";

    /// <summary>The generated page of one device (0.11): every value, <c>/dispozitive/{key}</c> with the key escaped.</summary>
    public static string Device(string key) => $"{Path}/{Uri.EscapeDataString(key)}";

    /// <summary>The devices page with the „+ Adaugă” panel open on one integration (<c>?adauga=govee</c>).</summary>
    public static string Add(string integration) => $"{Path}?adauga={Uri.EscapeDataString(integration)}";
}
