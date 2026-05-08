using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Data;
using KasaMonitor.Web.Components;
using KasaMonitor.Workers;
using Microsoft.EntityFrameworkCore;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dbPath = builder.Configuration["KasaMonitor:DbPath"] ?? "kasa.db";
builder.Services.AddDbContext<KasaDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath};Cache=Shared"));

builder.Services.AddSingleton<IKasaClient, KasaClient>();
builder.Services.AddKasaWorkers();

var tariff = builder.Configuration.GetValue("KasaMonitor:TariffRonPerKwh", 1.30m);
builder.Services.AddSingleton(new KasaMonitor.Core.Costing.TariffCalculator(tariff));

var broadcastAddrs = (builder.Configuration["KasaMonitor:BroadcastNetworks"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(IPAddress.Parse)
    .ToList();
if (broadcastAddrs.Count == 0) broadcastAddrs.Add(IPAddress.Broadcast);

var pollIntervalSec = builder.Configuration.GetValue("KasaMonitor:PollIntervalSeconds", 10);

builder.Services.AddHostedService(sp => new DiscoveryWorker(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IPlugDiscoveredChannel>(),
    broadcastAddrs,
    TimeSpan.FromSeconds(2.5),
    TimeSpan.FromMinutes(5),
    sp.GetRequiredService<ILogger<DiscoveryWorker>>()));

builder.Services.AddHostedService(sp => new PollingWorker(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IPlugStateBroadcaster>(),
    TimeSpan.FromSeconds(pollIntervalSec),
    sp.GetRequiredService<ILogger<PollingWorker>>()));

builder.Services.AddHostedService(sp => new HistorySyncWorker(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    () => DateTime.UtcNow,
    sp.GetRequiredService<ILogger<HistorySyncWorker>>()));

builder.Services.AddHostedService(sp => new RollupWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<RollupWorker>>()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
    ctx.Database.Migrate();
    ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    ctx.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/health", async (KasaDbContext ctx) =>
{
    try { await ctx.Plugs.AnyAsync(); return Results.Ok("healthy"); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 503); }
});

app.Run();
