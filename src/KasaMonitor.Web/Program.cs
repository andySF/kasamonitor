using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Protocol.Tapo;
using KasaMonitor.Data;
using KasaMonitor.Web;
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
    MlEnabled: builder.Configuration.GetValue("KasaMonitor:MlEnabled", true)));

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

// Prometheus remote write of the live samples (kasa_live_*), off by default (/settings → Integrare).
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
    HeatingReadingsRetentionDays: builder.Configuration.GetValue("KasaMonitor:HeatingReadingsRetentionDays", 0)));
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
// Appliance detection (NILM) from the main meter's 1 s stream; the classifier is saved next to the database.
builder.Services.AddSingleton(sp => new KasaMonitor.Workers.Nilm.NilmService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<ILiveStreamBroadcaster>(),
    sp.GetRequiredService<IPlugStateBroadcaster>(),
    sp.GetRequiredService<ILogger<KasaMonitor.Workers.Nilm.NilmService>>(),
    Path.Combine(dataDir, "nilm-model.zip")));
builder.Services.AddHostedService(sp => sp.GetRequiredService<KasaMonitor.Workers.Nilm.NilmService>());

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
builder.Services.AddSingleton<MetricsScrapeLog>();
builder.Services.AddSingleton<KasaMonitor.Web.Diagnostics.ClientErrorEndpoint>();
builder.Services.AddSingleton<PrometheusTargetsChecker>();

var app = builder.Build();
RemoteWriteMetrics.Register(app.Services.GetRequiredService<PrometheusRemoteWriter>());
HeatingMetrics.Register(app.Services.GetRequiredService<HeatingState>());
NilmMetrics.Register(app.Services.GetRequiredService<KasaMonitor.Workers.Nilm.NilmService>());

using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
    ctx.Database.Migrate();
    ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    ctx.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
}

app.UseMiddleware<MetricsScrapeLogMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/health", async (KasaDbContext ctx) =>
{
    try { await ctx.Plugs.AnyAsync(); return Results.Ok("healthy"); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 503); }
});

app.MapMetrics();

app.MapPost("/api/client-error", (KasaMonitor.Web.Diagnostics.ClientErrorReport report, HttpContext ctx,
        KasaMonitor.Web.Diagnostics.ClientErrorEndpoint endpoint) => endpoint.Handle(report, ctx))
    .DisableAntiforgery();

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
