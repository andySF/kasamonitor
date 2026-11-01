using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
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
    ReadingsRetentionMonths: builder.Configuration.GetValue("KasaMonitor:ReadingsRetentionMonths", 0)));

var pollIntervalSec = builder.Configuration.GetValue("KasaMonitor:PollIntervalSeconds", 10);

builder.Services.AddSingleton(sp => new PlugDiscoveryService(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<ITapoClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    sp.GetRequiredService<IPlugDiscoveredChannel>(),
    TimeSpan.FromSeconds(2.5),
    sp.GetRequiredService<ILogger<PlugDiscoveryService>>()));

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
    sp.GetRequiredService<ILogger<PollingWorker>>()));

builder.Services.AddHostedService(sp => new HistorySyncWorker(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    () => DateTime.UtcNow,
    sp.GetRequiredService<ILogger<HistorySyncWorker>>(),
    sp.GetRequiredService<ITapoClient>(),
    sp.GetRequiredService<IKasaSettingsService>(),
    discovered: sp.GetRequiredService<IPlugDiscoveredChannel>()));

builder.Services.AddHostedService(sp => new RollupWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<RollupWorker>>(),
    sp.GetRequiredService<ReadingsRetentionService>(),
    sp.GetRequiredService<IKasaSettingsService>()));

builder.Services.AddHostedService<KasaMetrics>();

builder.Services.AddSingleton(sp => new DeviceDetailsService(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<ITapoClient>(),
    sp.GetRequiredService<IKasaSettingsService>()));
builder.Services.AddSingleton<MetricsScrapeLog>();
builder.Services.AddSingleton<KasaMonitor.Web.Diagnostics.ClientErrorEndpoint>();
builder.Services.AddSingleton<PrometheusTargetsChecker>();

var app = builder.Build();

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
