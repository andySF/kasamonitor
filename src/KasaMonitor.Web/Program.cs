using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Protocol.Tapo;
using KasaMonitor.Data;
using KasaMonitor.Web;
using KasaMonitor.Web.Api;
using KasaMonitor.Web.Components;
using KasaMonitor.Web.Integration;
using KasaMonitor.Workers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using System.Text;

if (args is ["--healthcheck"])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        var resp = await http.GetAsync("http://localhost:8910/health");
        Environment.Exit(resp.IsSuccessStatusCode ? 0 : 1);
    }
    catch
    {
        Environment.Exit(1);
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dbPath = builder.Configuration["KasaMonitor:DbPath"] ?? "kasa.db";
builder.Services.AddDbContext<KasaDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath};Cache=Shared"));

builder.Services.AddSingleton<IKasaClient, KasaClient>();
builder.Services.AddSingleton<ISmartTransport>(_ => new KlapTransport(TimeSpan.FromSeconds(5)));
builder.Services.AddSingleton<ISmartTransport>(_ => new AesTransport(TimeSpan.FromSeconds(5)));
builder.Services.AddSingleton<ITapoClient>(sp => new TapoClient(sp.GetServices<ISmartTransport>()));
// Shelly Gen2/Gen3 JSON-RPC over HTTP: reads, plus Switch.Set (relay, UI only); 3 s per request (polling), 1 s for discovery probes.
builder.Services.AddSingleton<IShellyClient>(_ => new ShellyClient(TimeSpan.FromSeconds(3)));
// Live stream (1 s main meter, 1-10 s plugs): one persistent WebSocket per streamed Shelly device.
builder.Services.AddSingleton<IShellyRpcSocketFactory, ShellyRpcSocketFactory>();
builder.Services.AddKasaWorkers();

// DataProtection protects the TP-Link password stored in the Settings table. The key ring lives next to the
// database (/data/dp-keys in the container) so it survives image updates; losing it only means re-entering the password.
var dataDir = Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".";
builder.Services.AddDataProtection()
    .SetApplicationName("KasaMonitor")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "dp-keys")));
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

var tariff = builder.Configuration.GetValue("KasaMonitor:TariffRonPerKwh", 1.30m);
builder.Services.AddSingleton(new KasaMonitor.Core.Costing.TariffCalculator(tariff));

// Environment/appsettings values are the initial values; whatever is saved in /settings (database) wins.
builder.Services.AddSingleton(new KasaSettingsDefaults(
    AutoDiscoveryEnabled: builder.Configuration.GetValue("KasaMonitor:AutoDiscoveryEnabled", true),
    AutoDiscoveryIntervalMinutes: builder.Configuration.GetValue("KasaMonitor:AutoDiscoveryIntervalMinutes", 5),
    BroadcastNetworks: builder.Configuration["KasaMonitor:BroadcastNetworks"] ?? "",
    AutoAddDiscoveredPlugs: builder.Configuration.GetValue("KasaMonitor:AutoAddDiscoveredPlugs", true),
    TapoUsername: builder.Configuration["KasaMonitor:TapoUsername"],
    TapoPassword: builder.Configuration["KasaMonitor:TapoPassword"],
    ReadingsRetentionMonths: builder.Configuration.GetValue("KasaMonitor:ReadingsRetentionMonths", 0),
    TariffRonPerKwh: tariff,
    ShellyDiscoveryEnabled: builder.Configuration.GetValue("KasaMonitor:ShellyDiscoveryEnabled", true),
    ShellyPassword: builder.Configuration["KasaMonitor:ShellyPassword"],
    ShellyProbeHosts: builder.Configuration["KasaMonitor:ShellyProbeHosts"] ?? "",
    LiveStreamEnabled: builder.Configuration.GetValue("KasaMonitor:LiveStreamEnabled", true),
    LiveStreamIntervalSeconds: builder.Configuration.GetValue("KasaMonitor:LiveStreamIntervalSeconds", 1),
    LiveRawRetentionDays: builder.Configuration.GetValue("KasaMonitor:LiveRawRetentionDays", 30),
    AllowRelayControl: builder.Configuration.GetValue("KasaMonitor:AllowRelayControl", true),
    LiveBurstTriggerW: builder.Configuration.GetValue("KasaMonitor:LiveBurstTriggerW", KasaSettingsDefaults.DefaultLiveBurstTriggerW),
    RemoteWriteEnabled: builder.Configuration.GetValue("KasaMonitor:RemoteWriteEnabled", false),
    RemoteWriteUrl: builder.Configuration["KasaMonitor:RemoteWriteUrl"] ?? "",
    RemoteWriteUser: builder.Configuration["KasaMonitor:RemoteWriteUser"],
    RemoteWritePassword: builder.Configuration["KasaMonitor:RemoteWritePassword"],
    NilmEnabled: builder.Configuration.GetValue("KasaMonitor:NilmEnabled", true),
    MinEventDeltaW: builder.Configuration.GetValue("KasaMonitor:MinEventDeltaW", KasaSettingsDefaults.DefaultMinEventDeltaW),
    ClusterWindowDays: builder.Configuration.GetValue("KasaMonitor:ClusterWindowDays", KasaSettingsDefaults.DefaultClusterWindowDays),
    MlEnabled: builder.Configuration.GetValue("KasaMonitor:MlEnabled", true),
    GrafanaUrl: builder.Configuration["KasaMonitor:GrafanaUrl"] ?? ""));

var pollIntervalSec = builder.Configuration.GetValue("KasaMonitor:PollIntervalSeconds", 10);

builder.Services.AddSingleton(sp => new PlugDiscoveryService(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<ITapoClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<IPlugDiscoveredChannel>(),
    TimeSpan.FromSeconds(2.5),
    sp.GetRequiredService<ILogger<PlugDiscoveryService>>(),
    sp.GetRequiredService<IShellyClient>()));

builder.Services.AddHostedService(sp => new DiscoveryWorker(
    sp.GetRequiredService<PlugDiscoveryService>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<ILogger<DiscoveryWorker>>()));

builder.Services.AddHostedService(sp => new PollingWorker(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<ITapoClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IPlugStateBroadcaster>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    TimeSpan.FromSeconds(pollIntervalSec),
    sp.GetRequiredService<ILogger<PollingWorker>>(),
    shelly: sp.GetRequiredService<IShellyClient>()));

builder.Services.AddHostedService(sp => new HistorySyncWorker(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    () => DateTime.UtcNow,
    sp.GetRequiredService<ILogger<HistorySyncWorker>>(),
    sp.GetRequiredService<ITapoClient>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    discovered: sp.GetRequiredService<IPlugDiscoveredChannel>(),
    shelly: sp.GetRequiredService<IShellyClient>()));

builder.Services.AddHostedService(sp => new RollupWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<RollupWorker>>(),
    sp.GetRequiredService<ReadingsRetentionService>(),
    sp.GetRequiredService<IKasaSettingsService>()));

builder.Services.AddSingleton(sp => new LiveStreamWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<ILiveStreamBroadcaster>(),
    sp.GetRequiredService<IShellyRpcSocketFactory>(),
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<ITapoClient>(),
    sp.GetRequiredService<ILogger<LiveStreamWorker>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveStreamWorker>());

// Prometheus remote write of the live samples (kasa_live_*), off by default (/settings/export).
builder.Services.AddSingleton(sp => new PrometheusRemoteWriter(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<ILiveStreamBroadcaster>(),
    sp.GetRequiredService<ILogger<PrometheusRemoteWriter>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrometheusRemoteWriter>());

builder.Services.AddHostedService<KasaMetrics>();

// Gas boiler (Viessmann ViCare cloud API): OAuth PKCE connection from /centrala, one bulk features call per interval.
builder.Services.AddSingleton(new HeatingSettingsDefaults(
    HeatingEnabled: builder.Configuration.GetValue("KasaMonitor:HeatingEnabled", true),
    ViessmannClientId: builder.Configuration["KasaMonitor:ViessmannClientId"] ?? "",
    ViessmannPollIntervalSeconds: builder.Configuration.GetValue("KasaMonitor:ViessmannPollIntervalSeconds", KasaMonitor.Core.Heating.ViessmannApi.DefaultPollIntervalSeconds),
    GasPricePerKwh: builder.Configuration.GetValue("KasaMonitor:GasPricePerKwh", 0.31m),
    GasUnit: builder.Configuration["KasaMonitor:GasUnit"] ?? KasaMonitor.Core.Heating.HeatingKinds.UnitM3,
    GasKwhPerM3: builder.Configuration.GetValue("KasaMonitor:GasKwhPerM3", KasaMonitor.Core.Heating.ViessmannApi.DefaultKwhPerM3),
    HeatingReadingsRetentionDays: builder.Configuration.GetValue("KasaMonitor:HeatingReadingsRetentionDays", 0),
    BoilerTimeZone: builder.Configuration["KasaMonitor:BoilerTimeZone"] ?? KasaMonitor.Core.Heating.BoilerClock.DefaultTimeZoneId));
builder.Services.AddSingleton<IHeatingSettingsService, HeatingSettingsService>();
builder.Services.AddSingleton<KasaMonitor.Core.Heating.IViessmannClient>(_ => new KasaMonitor.Core.Heating.ViessmannClient(TimeSpan.FromSeconds(20)));
builder.Services.AddSingleton<HeatingState>();
builder.Services.AddSingleton(sp => new ViessmannAuthService(
    sp.GetRequiredService<IHeatingSettingsService>(),
    sp.GetRequiredService<KasaMonitor.Core.Heating.IViessmannClient>(),
    sp.GetRequiredService<ILogger<ViessmannAuthService>>(),
    calls: sp.GetRequiredService<HeatingState>().Calls));
builder.Services.AddSingleton(sp => new HeatingPollWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IHeatingSettingsService>(),
    sp.GetRequiredService<ViessmannAuthService>(),
    sp.GetRequiredService<KasaMonitor.Core.Heating.IViessmannClient>(),
    sp.GetRequiredService<HeatingState>(),
    sp.GetRequiredService<ILogger<HeatingPollWorker>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<HeatingPollWorker>());

// Miele appliances (Miele 3rd Party API): OAuth from /aparate, SSE stream of all appliances, read-only. The client
// secret comes from KasaMonitor__MieleClientSecret or the file KasaMonitor__MieleClientSecretFile (read once here).
builder.Services.AddSingleton(new MieleSettingsDefaults(
    MieleClientId: builder.Configuration["KasaMonitor:MieleClientId"] ?? "",
    MieleClientSecretFile: builder.Configuration["KasaMonitor:MieleClientSecretFile"] ?? KasaMonitor.Core.Miele.MieleApi.DefaultClientSecretFile,
    MieleClientSecret: builder.Configuration["KasaMonitor:MieleClientSecret"]));
builder.Services.AddSingleton<IMieleSettingsService, MieleSettingsService>();
builder.Services.AddSingleton<KasaMonitor.Core.Miele.IMieleClient>(_ => new KasaMonitor.Core.Miele.MieleClient(TimeSpan.FromSeconds(20)));
builder.Services.AddSingleton<MieleState>();
builder.Services.AddSingleton(sp => new MieleAuthService(
    sp.GetRequiredService<IMieleSettingsService>(),
    sp.GetRequiredService<KasaMonitor.Core.Miele.IMieleClient>(),
    sp.GetRequiredService<ILogger<MieleAuthService>>()));
builder.Services.AddSingleton(sp => new MieleRecorder(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<MieleState>(),
    sp.GetRequiredService<ILogger<MieleRecorder>>()));
builder.Services.AddSingleton(sp => new MieleStreamWorker(
    sp.GetRequiredService<IMieleSettingsService>(),
    sp.GetRequiredService<MieleAuthService>(),
    sp.GetRequiredService<KasaMonitor.Core.Miele.IMieleClient>(),
    sp.GetRequiredService<MieleRecorder>(),
    sp.GetRequiredService<MieleState>(),
    sp.GetRequiredService<ILogger<MieleStreamWorker>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<MieleStreamWorker>());
// Program history (lastUsedPrograms, last 5 runs per appliance): at connection, every 6 h and 2 min after a cycle ended.
builder.Services.AddSingleton(sp => new MieleHistorySync(
    sp.GetRequiredService<MieleAuthService>(),
    sp.GetRequiredService<KasaMonitor.Core.Miele.IMieleClient>(),
    sp.GetRequiredService<MieleRecorder>(),
    sp.GetRequiredService<MieleState>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<MieleHistorySync>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<MieleHistorySync>());
builder.Services.AddSingleton(sp => new MieleGroundTruth(sp.GetRequiredService<MieleState>()));

// Climate (/clima): temperature and humidity per room from the boiler's readings, Govee (cloud, key from a mounted file),
// Shelly H&T / Add-on (local, mDNS) and Open-Meteo (outdoor, when latitude/longitude are set). The Govee key comes from
// KasaMonitor__GoveeApiKey or the file KasaMonitor__GoveeApiKeyFile / GOVEE_API_KEY_FILE (read once here).
builder.Services.AddSingleton(new ClimateSettingsDefaults(
    Latitude: ClimateSettingsService.ParseCoordinate(builder.Configuration["KasaMonitor:ClimateLatitude"]),
    Longitude: ClimateSettingsService.ParseCoordinate(builder.Configuration["KasaMonitor:ClimateLongitude"]),
    GoveeApiKeyFile: builder.Configuration["KasaMonitor:GoveeApiKeyFile"]
                     ?? Environment.GetEnvironmentVariable(KasaMonitor.Core.Climate.GoveeApi.KeyFileEnvironmentVariable)
                     ?? KasaMonitor.Core.Climate.GoveeApi.DefaultKeyFile,
    GoveeApiKey: builder.Configuration["KasaMonitor:GoveeApiKey"],
    HeatingDegreeDaysBaseC: ClimateSettingsService.ParseCoordinate(builder.Configuration["KasaMonitor:HeatingDegreeDaysBaseC"]) is { } hdd
        && hdd >= ClimateSettingsService.MinDegreeDaysBaseC && hdd <= ClimateSettingsService.MaxDegreeDaysBaseC
        ? hdd : KasaMonitor.Core.History.DegreeDayRule.EurostatBaseC));
builder.Services.AddSingleton<IClimateSettingsService, ClimateSettingsService>();
builder.Services.AddSingleton<KasaMonitor.Core.Climate.IGoveeClient>(_ => new KasaMonitor.Core.Climate.GoveeClient(TimeSpan.FromSeconds(20)));
builder.Services.AddSingleton<KasaMonitor.Core.Climate.IOpenMeteoClient>(_ => new KasaMonitor.Core.Climate.OpenMeteoClient(TimeSpan.FromSeconds(20)));
builder.Services.AddSingleton(sp => new ClimateState(() => sp.GetRequiredService<HeatingState>().PollIntervalSeconds));
builder.Services.AddSingleton(sp => new ClimateRecorder(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ClimateState>(),
    sp.GetRequiredService<ILogger<ClimateRecorder>>()));
builder.Services.AddSingleton(sp => new ClimateWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IClimateSettingsService>(),
    sp.GetRequiredService<ClimateRecorder>(),
    sp.GetRequiredService<ClimateState>(),
    sp.GetRequiredService<KasaMonitor.Core.Climate.IGoveeClient>(),
    sp.GetRequiredService<KasaMonitor.Core.Climate.IOpenMeteoClient>(),
    sp.GetRequiredService<ILogger<ClimateWorker>>(),
    kasa: sp.GetRequiredService<IKasaSettingsService>(),
    shelly: sp.GetRequiredService<IShellyClient>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClimateWorker>());

// Appliance detection (NILM) from the main meter's 1 s stream; the classifier is saved next to the database.
builder.Services.AddSingleton(sp => new KasaMonitor.Workers.Nilm.NilmService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<ILiveStreamBroadcaster>(),
    sp.GetRequiredService<IPlugStateBroadcaster>(),
    sp.GetRequiredService<ILogger<KasaMonitor.Workers.Nilm.NilmService>>(),
    Path.Combine(dataDir, "nilm-model.zip"),
    groundTruth: sp.GetRequiredService<MieleGroundTruth>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<KasaMonitor.Workers.Nilm.NilmService>());

// /history: the sections other than electricity (gas and boiler, Miele, detected appliances, climate, events), cached per
// period like the home page; climate days from ClimateDaily (filled at startup and after each midnight; today and days not
// stored yet from the raw readings).
builder.Services.AddSingleton<KasaMonitor.Core.Abstractions.IClimateDailyStats>(sp => new ClimateDailyStore(sp.GetRequiredService<IServiceScopeFactory>()));
builder.Services.AddSingleton(sp => new ClimateDailyWorker(sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<EnergyReportService>().TimeZone, sp.GetRequiredService<ILogger<ClimateDailyWorker>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClimateDailyWorker>());
builder.Services.AddSingleton(sp => new HistoryService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<EnergyReportService>(),
    sp.GetRequiredService<KasaMonitor.Core.Abstractions.IClimateDailyStats>(),
    sp.GetRequiredService<IClimateSettingsService>(),
    sp.GetRequiredService<IHeatingSettingsService>(),
    sp.GetRequiredService<HeatingState>(),
    sp.GetRequiredService<KasaMonitor.Workers.Nilm.NilmService>().GetDetectedRunsAsync,
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<ILogger<HistoryService>>()));

// Home page „Acasă” (/): period aggregates cached per minute, standby floor and bill estimate (also on /metrics).
builder.Services.AddSingleton(sp => new HomeService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<EnergyReportService>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<HeatingState>(),
    sp.GetRequiredService<IHeatingSettingsService>(),
    sp.GetRequiredService<KasaMonitor.Workers.Nilm.NilmService>(),
    sp.GetRequiredService<ILogger<HomeService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<HomeService>());
// „Climă” card of the home page: the climate service (Afară = Open-Meteo, rooms = the other sensors).
builder.Services.AddSingleton<KasaMonitor.Core.Home.IClimateOverview>(sp => new ClimateOverview(sp.GetRequiredService<ClimateState>()));

// The only write to a device: Shelly Switch.Set, after a confirmation in the UI (setting AllowRelayControl).
builder.Services.AddSingleton(sp => new RelayControlService(
    sp.GetRequiredService<IShellyClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<ILiveStreamBroadcaster>(),
    sp.GetRequiredService<IPlugStateBroadcaster>(),
    sp.GetRequiredService<ILogger<RelayControlService>>()));

builder.Services.AddSingleton(sp => new DeviceDetailsService(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<ITapoClient>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    shelly: sp.GetRequiredService<IShellyClient>()));
// MQTT Discovery (0.11, read only): devices that describe themselves on a broker (Zigbee2MQTT, ESPHome). The broker and the
// user come from the MQTT tab (or KasaMonitor__MqttHost / __MqttPort / __MqttUser while the database has none); the password
// from KasaMonitor__MqttPassword or the file KasaMonitor__MqttPasswordFile (read once here, never stored).
builder.Services.AddSingleton(new KasaMonitor.Workers.Mqtt.MqttSettingsDefaults(
    Host: builder.Configuration["KasaMonitor:MqttHost"] ?? "",
    Port: int.TryParse(builder.Configuration["KasaMonitor:MqttPort"], out var mqttPort) ? mqttPort : KasaMonitor.Workers.Mqtt.MqttSettingsService.DefaultPort,
    User: builder.Configuration["KasaMonitor:MqttUser"] ?? "",
    PasswordFile: builder.Configuration["KasaMonitor:MqttPasswordFile"] ?? KasaMonitor.Workers.Mqtt.MqttSettingsService.DefaultPasswordFile,
    Password: builder.Configuration["KasaMonitor:MqttPassword"]));
builder.Services.AddSingleton<KasaMonitor.Workers.Mqtt.IMqttSettingsService, KasaMonitor.Workers.Mqtt.MqttSettingsService>();
builder.Services.AddSingleton<KasaMonitor.Workers.Mqtt.MqttState>();
builder.Services.AddSingleton<KasaMonitor.Workers.Mqtt.IMqttTransportFactory, KasaMonitor.Workers.Mqtt.MqttNetTransportFactory>();
builder.Services.AddSingleton(sp => new KasaMonitor.Workers.Mqtt.GenericRecorder(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<KasaMonitor.Workers.Mqtt.MqttState>(),
    sp.GetRequiredService<ClimateRecorder>(),
    sp.GetRequiredService<ILogger<KasaMonitor.Workers.Mqtt.GenericRecorder>>()));
builder.Services.AddSingleton(sp => new KasaMonitor.Workers.Mqtt.MqttDiscoveryWorker(
    sp.GetRequiredService<KasaMonitor.Workers.Mqtt.IMqttSettingsService>(),
    sp.GetRequiredService<KasaMonitor.Workers.Mqtt.IMqttTransportFactory>(),
    sp.GetRequiredService<KasaMonitor.Workers.Mqtt.MqttState>(),
    sp.GetRequiredService<KasaMonitor.Workers.Mqtt.GenericRecorder>(),
    sp.GetRequiredService<ILogger<KasaMonitor.Workers.Mqtt.MqttDiscoveryWorker>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<KasaMonitor.Workers.Mqtt.MqttDiscoveryWorker>());
builder.Services.AddSingleton(sp => new KasaMonitor.Workers.Mqtt.GenericHistoryService(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<KasaMonitor.Workers.Mqtt.GenericRecorder>()));

// /dispozitive: every device of every integration in one list (a projection, no table of its own) and the uniform
// „Retrage / Readu”, which writes through each kind's owner.
builder.Services.AddSingleton(sp => new DeviceCatalog(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IPlugStateBroadcaster>(),
    sp.GetRequiredService<ClimateState>(),
    sp.GetRequiredService<HeatingState>(),
    sp.GetRequiredService<MieleState>(),
    sp.GetRequiredService<ClimateRecorder>(),
    sp.GetRequiredService<MieleRecorder>(),
    sp.GetRequiredService<IClimateSettingsService>(),
    wakeHeating: () => sp.GetRequiredService<HeatingPollWorker>().Wake(),
    wakeClimate: () => sp.GetRequiredService<ClimateWorker>().Wake(),
    generic: sp.GetRequiredService<KasaMonitor.Workers.Mqtt.GenericRecorder>(),
    mqtt: sp.GetRequiredService<KasaMonitor.Workers.Mqtt.MqttState>()));
// /dispozitive/{key} (0.11): every device of every integration projected into entities (no migration of the existing
// tables), one source per row-key prefix, and the per-entity preferences.
builder.Services.AddSingleton<KasaMonitor.Core.Devices.Entities.IEntitySource>(sp => new KasaMonitor.Workers.Entities.PlugEntitySource(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IPlugStateBroadcaster>()));
builder.Services.AddSingleton<KasaMonitor.Core.Devices.Entities.IEntitySource>(sp => new KasaMonitor.Workers.Entities.ClimateEntitySource(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ClimateState>()));
builder.Services.AddSingleton<KasaMonitor.Core.Devices.Entities.IEntitySource>(sp => new KasaMonitor.Workers.Entities.HeatingEntitySource(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<HeatingState>()));
builder.Services.AddSingleton<KasaMonitor.Core.Devices.Entities.IEntitySource>(sp => new KasaMonitor.Workers.Entities.MieleEntitySource(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<MieleState>()));
builder.Services.AddSingleton<KasaMonitor.Core.Devices.Entities.IEntitySource>(sp => new KasaMonitor.Workers.Entities.GenericEntitySource(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<KasaMonitor.Workers.Mqtt.GenericRecorder>(),
    sp.GetRequiredService<KasaMonitor.Workers.Mqtt.MqttState>()));
builder.Services.AddSingleton(sp => new KasaMonitor.Workers.Entities.DeviceEntityService(
    sp.GetServices<KasaMonitor.Core.Devices.Entities.IEntitySource>(), sp.GetRequiredService<IServiceScopeFactory>()));
builder.Services.AddSingleton<MetricsScrapeLog>();
builder.Services.AddSingleton<KasaMonitor.Web.Diagnostics.ClientErrorEndpoint>();
builder.Services.AddSingleton<PrometheusTargetsChecker>();

var app = builder.Build();
RemoteWriteMetrics.Register(app.Services.GetRequiredService<PrometheusRemoteWriter>());
HeatingMetrics.Register(app.Services.GetRequiredService<HeatingState>());
NilmMetrics.Register(app.Services.GetRequiredService<KasaMonitor.Workers.Nilm.NilmService>());
MieleMetrics.Register(app.Services.GetRequiredService<MieleState>());
ClimateMetrics.Register(app.Services.GetRequiredService<ClimateState>());
HomeMetrics.Register(app.Services.GetRequiredService<HomeService>());
GenericMetrics.Register(app.Services.GetRequiredService<KasaMonitor.Workers.Mqtt.MqttState>(), app.Services.GetRequiredService<KasaMonitor.Workers.Mqtt.GenericRecorder>(),
    app.Services.GetRequiredService<KasaMonitor.Workers.Mqtt.IMqttSettingsService>(), app.Services.GetRequiredService<IServiceScopeFactory>(),
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("KasaMonitor.Web.GenericMetrics"));

using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
    ctx.Database.Migrate();
    ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    ctx.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
}

app.UseMiddleware<MetricsScrapeLogMiddleware>();
// Static files (app.css, js) change with every release and are linked without a fingerprint: ask the browser to
// revalidate them on every load (ETag answers 304 when unchanged) instead of the heuristic caching that showed the
// previous release's CSS after an update (0.10.1).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseRouting();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// /plugs became /dispozitive in 0.10.0 (every device of every integration); old bookmarks are sent there for good.
app.MapGet("/plugs", () => Results.Redirect(KasaMonitor.Web.Ui.DevicePages.Path, permanent: true));

app.MapGet("/health", async (KasaDbContext ctx) =>
{
    try { await ctx.Plugs.AnyAsync(); return Results.Ok("healthy"); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 503); }
});

app.MapMetrics();

app.MapPost("/api/client-error", (KasaMonitor.Web.Diagnostics.ClientErrorReport report, HttpContext ctx,
        KasaMonitor.Web.Diagnostics.ClientErrorEndpoint endpoint) => endpoint.Handle(report, ctx))
    .DisableAntiforgery();

// NILM administration over HTTP (/api/nilm/*): the same actions as /consumatori; writes only from the LAN.
app.MapNilmApi();

// ViCare OAuth: /centrala/connect redirects to Viessmann (PKCE + state); Viessmann redirects back to /centrala/callback.
// The redirect URI is this app's own address + /centrala/callback and must be registered on the developer portal.
// A cookie with the hash of the state ties the callback to the browser that started the flow.
app.MapGet("/centrala/connect", async (HttpContext ctx, ViessmannAuthService auth) =>
{
    if (await auth.BeginAuthorizeAsync(CallbackUri(ctx.Request), ctx.RequestAborted) is not { } start)
        return Results.Redirect("/centrala?auth=noclient");
    ctx.Response.Cookies.Append(ViessmannAuthService.StateCookieName, ViessmannAuthService.StateCookieValue(start.State), new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/centrala",
        MaxAge = ViessmannAuthService.PendingLifetime,
        Secure = ctx.Request.IsHttps,
    });
    return Results.Redirect(start.Url);
});
app.MapGet(KasaMonitor.Core.Heating.ViessmannApi.CallbackPath, async (HttpContext ctx, ViessmannAuthService auth, HeatingState heating) =>
{
    var q = ctx.Request.Query;
    var state = q["state"].FirstOrDefault();
    var cookie = ctx.Request.Cookies[ViessmannAuthService.StateCookieName];
    ctx.Response.Cookies.Delete(ViessmannAuthService.StateCookieName, new CookieOptions { Path = "/centrala" });
    if (!ViessmannAuthService.CookieMatches(state, cookie))
        return Results.Text("Cererea de autorizare nu vine din acest browser (cookie lipsă sau diferit). Reia conectarea din /centrala.", "text/plain; charset=utf-8", statusCode: 400);
    var (result, detail) = await auth.CompleteAsync(state, q["code"].FirstOrDefault(), q["error"].FirstOrDefault(), ctx.RequestAborted);
    if (result is ViessmannCallbackResult.ExchangeFailed or ViessmannCallbackResult.Denied && detail is not null)
        heating.SetStatus("Conectarea ViCare a eșuat: " + detail);
    return result == ViessmannCallbackResult.InvalidState
        ? Results.Text("Cerere de autorizare necunoscută sau expirată (state invalid). Reia conectarea din /centrala.", "text/plain; charset=utf-8", statusCode: 400)
        : Results.Redirect("/centrala?auth=" + result.ToString().ToLowerInvariant());
});

// Miele OAuth: /miele/connect redirects to Miele (state + cookie with its hash); Miele redirects back to /miele/callback.
// The redirect URI is this app's own address + /miele/callback (open KasaMonitor at the address used for it).
app.MapGet(KasaMonitor.Core.Miele.MieleApi.ConnectPath, async (HttpContext ctx, MieleAuthService auth) =>
{
    if (await auth.BeginAuthorizeAsync(MieleCallbackUri(ctx.Request), ctx.RequestAborted) is not { } start)
        return Results.Redirect("/aparate?auth=notconfigured");
    ctx.Response.Cookies.Append(MieleAuthService.StateCookieName, ViessmannAuthService.StateCookieValue(start.State), new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/miele",
        MaxAge = MieleAuthService.PendingLifetime,
        Secure = ctx.Request.IsHttps,
    });
    return Results.Redirect(start.Url);
});
app.MapGet(KasaMonitor.Core.Miele.MieleApi.CallbackPath, async (HttpContext ctx, MieleAuthService auth, MieleState miele) =>
{
    var q = ctx.Request.Query;
    var state = q["state"].FirstOrDefault();
    var cookie = ctx.Request.Cookies[MieleAuthService.StateCookieName];
    ctx.Response.Cookies.Delete(MieleAuthService.StateCookieName, new CookieOptions { Path = "/miele" });
    if (!ViessmannAuthService.CookieMatches(state, cookie))
        return Results.Text("Cererea de autorizare nu vine din acest browser (cookie lipsă sau diferit). Reia conectarea din /aparate.", "text/plain; charset=utf-8", statusCode: 400);
    var (result, detail) = await auth.CompleteAsync(state, q["code"].FirstOrDefault(), q["error"].FirstOrDefault(), ctx.RequestAborted);
    if (result is MieleCallbackResult.ExchangeFailed or MieleCallbackResult.Denied && detail is not null)
        miele.SetError("Conectarea Miele a eșuat: " + detail, DateTimeOffset.UtcNow);
    return result == MieleCallbackResult.InvalidState
        ? Results.Text("Cerere de autorizare necunoscută sau expirată (state invalid). Reia conectarea din /aparate.", "text/plain; charset=utf-8", statusCode: 400)
        : Results.Redirect("/aparate?auth=" + result.ToString().ToLowerInvariant());
});

// CSV of one /history section for the period of the address (the page's „CSV” buttons): the same data and table as the page.
app.MapGet("/history/export.csv", async (HttpContext ctx, HomeService home, HistoryService history, IKasaSettingsService kasa,
    IHeatingSettingsService heating, KasaMonitor.Workers.Mqtt.GenericHistoryService generic) =>
{
    var q = ctx.Request.Query;
    var section = q["section"].FirstOrDefault() ?? "";
    var period = KasaMonitor.Core.Reports.PeriodQuery.Parse(q["p"].FirstOrDefault(), q["d"].FirstOrDefault(), q["de"].FirstOrDefault(),
        q["pana"].FirstOrDefault(), history.Today(), KasaMonitor.Core.Reports.ReportGranularity.Month);
    var plug = KasaMonitor.Core.Reports.PeriodQuery.Plug(q["plug"].FirstOrDefault());
    var table = await KasaMonitor.Web.Ui.HistoryExport.TableAsync(section, period, KasaMonitor.Core.Reports.PeriodQuery.Compare(q["cmp"].FirstOrDefault()),
        plug, home, history, kasa, heating, DateTimeOffset.UtcNow, ctx.RequestAborted, generic);
    return table is null
        ? Results.Text("Secțiune necunoscută. Valori: " + string.Join(", ", KasaMonitor.Web.Ui.HistorySections.All), "text/plain; charset=utf-8", statusCode: 400)
        : Results.File(KasaMonitor.Core.History.HistoryCsv.Bytes(table), "text/csv; charset=utf-8", KasaMonitor.Web.Ui.HistoryExport.FileName(section, period));
});

// Downloads for /integration (generated from the embedded deploy/ files; no secrets involved).
var integration = app.MapGroup("/integration/files");
integration.MapGet("/prometheus.yml", (string? target) =>
    WithTarget(target, t => Results.File(Encoding.UTF8.GetBytes(IntegrationConfigGenerator.PrometheusYml(t)), "text/yaml", "prometheus.yml")));
integration.MapGet("/kasamonitor.json", (string? datasourceUid) =>
    WithUid(datasourceUid, u => Results.File(Encoding.UTF8.GetBytes(IntegrationConfigGenerator.DashboardJson(u)), "application/json", "kasamonitor.json")));
integration.MapGet("/rules.yml", (string? datasourceUid) =>
    WithUid(datasourceUid, u => Results.File(Encoding.UTF8.GetBytes(IntegrationConfigGenerator.AlertRulesYml(u)), "text/yaml", "rules.yml")));
integration.MapGet("/kasamonitor-monitoring.zip", (string? target) =>
    WithTarget(target, t => Results.File(IntegrationConfigGenerator.MonitoringBundle(t), "application/zip", "kasamonitor-monitoring.zip")));

app.Run();

static string MieleCallbackUri(HttpRequest r) => $"{r.Scheme}://{r.Host}{KasaMonitor.Core.Miele.MieleApi.CallbackPath}";

static string CallbackUri(HttpRequest r) => $"{r.Scheme}://{r.Host}{KasaMonitor.Core.Heating.ViessmannApi.CallbackPath}";

static IResult WithTarget(string? target, Func<string, IResult> make)
{
    try { return make(IntegrationConfigGenerator.NormalizeTarget(target)); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }
}

static IResult WithUid(string? uid, Func<string, IResult> make)
{
    try { return make(IntegrationConfigGenerator.NormalizeDataSourceUid(uid)); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }
}
