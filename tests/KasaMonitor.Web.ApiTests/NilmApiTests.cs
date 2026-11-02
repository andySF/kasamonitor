using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Data;
using KasaMonitor.Web.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>
/// The app on a temporary database, without its background workers (no discovery, polling or NILM loop). The caller's
/// address is simulated by a test-only middleware that sets <c>Connection.RemoteIpAddress</c> from
/// <see cref="RemoteIpHeader"/> (a LAN address when absent); the API itself never reads a header for that.
/// </summary>
public sealed class NilmApiFactory : WebApplicationFactory<Program>
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public const string LanIp = "192.168.1.50";
    public const string PublicIp = "203.0.113.7";   // TEST-NET-3 (documentation range)

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-nilm-api-{Guid.NewGuid():N}");
    public string? Token { get; init; }
    public LogSink Logs { get; } = new();
    public DateTimeOffset Start { get; } = DateTimeOffset.UtcNow.AddMinutes(-5);
    private int _slot;

    public NilmApiFactory() => Directory.CreateDirectory(Dir);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("KasaMonitor:DbPath", Path.Combine(Dir, "kasa.db"));
        if (Token is not null) builder.UseSetting(NilmApi.TokenSetting, Token);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IHostedService>();
            s.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            s.AddSingleton<ILoggerProvider>(Logs);
        });
    }

    /// <summary>A time no other seeded event uses (events of the same meter within ±2 s would be skipped as duplicates).</summary>
    public DateTimeOffset NextTime() => Start.AddMinutes(-2 * Interlocked.Increment(ref _slot));

    public HttpClient Client(string ip = LanIp, string? apiKey = null)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add(RemoteIpHeader, ip);
        if (apiKey is not null) c.DefaultRequestHeaders.Add(NilmApi.TokenHeader, apiKey);
        return c;
    }

    public async Task<T> Repo<T>(Func<INilmRepository, IServiceProvider, Task<T>> f)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KasaDbContext>().Database.GetConnectionString();
        db.Should().Contain(Dir, "the tests never touch a real database");
        return await f(scope.ServiceProvider.GetRequiredService<INilmRepository>(), scope.ServiceProvider);
    }

    private Guid? _main;

    /// <summary>The main meter (fake MAC) the seeded events belong to.</summary>
    public async Task<Guid> MainAsync()
    {
        if (_main is { } id) return id;
        _main = await Repo((_, sp) => sp.GetRequiredService<IPlugRepository>().UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.168.1.141"),
            new PlugInfo("aabbccddee01", "Casa", "S3EM-002CXCEU", "1", "1", "x", true, -50, TimeSpan.Zero), PlugProtocols.ShellyRpc, null, 0,
            PlugRole.MainMeter), DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));
        return _main.Value;
    }

    public Task<ApplianceSignature> AddSignatureAsync(string name, double deltaP, int count = 5,
        SignatureLabelSource source = SignatureLabelSource.Cluster, bool hidden = false) =>
        Repo((r, _) => r.AddSignatureAsync(new ApplianceSignature(0, name, "bi-question-circle", ApplianceCategories.Other, deltaP, 0, 1, count,
            DateTimeOffset.UtcNow.AddHours(-1), source, null, hidden), CancellationToken.None));

    public async Task<IReadOnlyList<MeterEvent>> AddEventsAsync(long? signatureId, params double[] deltas)
    {
        var main = await MainAsync();
        var events = deltas.Select(d => new MeterEvent(0, main, NextTime(), d, 0, 0, 1, 0, 300, signatureId)).ToList();
        return await Repo((r, _) => r.AddEventsAsync(events, CancellationToken.None));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMw) =>
            {
                var ip = ctx.Request.Headers[RemoteIpHeader].ToString();
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.Length > 0 ? ip : LanIp);
                return nextMw(ctx);
            });
            next(app);
        };
    }
}

/// <summary>Collects the app's log lines.</summary>
public sealed class LogSink : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Lines.Enqueue((category, logLevel, formatter(state, exception)));
    }
}

public class NilmApiTests(NilmApiFactory app) : IClassFixture<NilmApiFactory>
{
    private const string Api = NilmApi.Prefix;

    private Task<ApplianceSignature?> SignatureAsync(long id) => app.Repo((r, _) => r.GetSignatureAsync(id, CancellationToken.None));

    private static async Task ShouldBe(HttpResponseMessage resp, HttpStatusCode status)
    {
        if (resp.StatusCode != status) resp.StatusCode.Should().Be(status, await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.254.10", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("2001:db8::1", false)]
    public void IsPrivate_OnlyTheLanRangesAndLoopback(string ip, bool expected)
    {
        NilmApi.IsPrivate(IPAddress.Parse(ip)).Should().Be(expected);
    }

    [Fact]
    public void IsPrivate_NoAddress_IsRefused()
    {
        NilmApi.IsPrivate(null).Should().BeFalse();
    }

    [Fact]
    public async Task Signatures_ByDefault_ThePageFilter_AllIncludesRareAndHidden()
    {
        var frequent = await app.AddSignatureAsync("", 1500, count: 5);
        var rare = await app.AddSignatureAsync("", 1600, count: 1);
        var hidden = await app.AddSignatureAsync("", 1700, count: 5, hidden: true);
        var named = await app.AddSignatureAsync("Cuptor test", 1800, count: 1, source: SignatureLabelSource.User);
        using var client = app.Client();

        var list = await client.GetFromJsonAsync<List<NilmApi.SignatureDto>>($"{Api}/signatures");
        var all = (await client.GetFromJsonAsync<List<NilmApi.SignatureDto>>($"{Api}/signatures?all=true"))!;

        list!.Select(s => s.Id).Should().Contain(new[] { frequent.Id, named.Id }).And.NotContain(new[] { rare.Id, hidden.Id });
        all.Select(s => s.Id).Should().Contain(new[] { frequent.Id, named.Id, rare.Id, hidden.Id });
        var dto = all.Single(s => s.Id == named.Id);
        dto.Name.Should().Be("Cuptor test");
        dto.LabelSource.Should().Be("User");
        dto.MeanDeltaP.Should().Be(1800);
        dto.Count.Should().Be(1);
        all.Single(s => s.Id == hidden.Id).Hidden.Should().BeTrue();
    }

    [Fact]
    public async Task Signature_Sheet_ProfileAndNewestEventsFirst_404ForAMissingId()
    {
        var sig = await app.AddSignatureAsync("", 2000);
        var events = await app.AddEventsAsync(sig.Id, 2000, -1990, 2010);
        using var client = app.Client();

        var resp = await client.GetAsync($"{Api}/signatures/{sig.Id}");
        await ShouldBe(resp, HttpStatusCode.OK);
        var sheet = await resp.Content.ReadFromJsonAsync<NilmApi.SheetDto>();

        sheet!.Signature.Id.Should().Be(sig.Id);
        sheet.Profile.Events.Should().Be(3);
        sheet.Profile.OnEvents.Should().Be(2);
        sheet.Events.Select(e => e.Id).Should().Equal(events.OrderByDescending(e => e.TsUtc).Select(e => e.Id));
        sheet.Events.Should().OnlyContain(e => e.SignatureId == sig.Id && e.Source == "None");
        (await client.GetAsync($"{Api}/signatures/987654")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Events_RangeAndMinimumStep()
    {
        var events = await app.AddEventsAsync(null, 40, 600, -45, -610);
        var from = events.Min(e => e.TsUtc).AddSeconds(-1);
        var to = events.Max(e => e.TsUtc).AddSeconds(1);
        using var client = app.Client();

        var url = $"{Api}/events?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var every = await client.GetFromJsonAsync<List<NilmApi.EventDto>>(url);
        var big = await client.GetFromJsonAsync<List<NilmApi.EventDto>>(url + "&minAbsDeltaP=100");

        every!.Select(e => e.Id).Should().BeEquivalentTo(events.Select(e => e.Id));
        big!.Select(e => e.DeltaP).Should().BeEquivalentTo(new[] { 600.0, -610.0 });
        (await client.GetAsync($"{Api}/events?from={Uri.EscapeDataString(to.ToString("O"))}&to={Uri.EscapeDataString(from.ToString("O"))}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Label_NameAndCategory_AsTheUserLabel_404_400_And403FromOutsideTheLan()
    {
        var sig = await app.AddSignatureAsync("", 2100);
        using var client = app.Client();

        var resp = await client.PostAsJsonAsync($"{Api}/signatures/{sig.Id}/label", new { name = "  Mașina   de spălat ", category = "washer" });
        await ShouldBe(resp, HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<NilmApi.SignatureDto>();

        dto!.Name.Should().Be("Mașina de spălat");
        dto.Category.Should().Be("washer");
        dto.LabelSource.Should().Be("User");
        var stored = await SignatureAsync(sig.Id);
        stored!.Icon.Should().Be(ApplianceCategories.Get("washer").Icon);
        app.Logs.Lines.Should().Contain(l => l.Level == LogLevel.Information && l.Category == typeof(NilmApi).FullName
            && l.Message.Contains($"signature {sig.Id} labelled") && l.Message.Contains(NilmApiFactory.LanIp));

        (await client.PostAsJsonAsync($"{Api}/signatures/987654/label", new { name = "X" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"{Api}/signatures/{sig.Id}/label", new { category = "rocket" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"{Api}/signatures/{sig.Id}/label", new { name = "   " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var outside = app.Client(NilmApiFactory.PublicIp);
        (await outside.PostAsJsonAsync($"{Api}/signatures/{sig.Id}/label", new { name = "Altceva" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SignatureAsync(sig.Id))!.Name.Should().Be("Mașina de spălat");
        (await outside.GetAsync($"{Api}/signatures/{sig.Id}")).StatusCode.Should().Be(HttpStatusCode.OK, "reading is allowed from anywhere");
    }

    [Fact]
    public async Task Label_OnlyTheCategory_KeepsTheName()
    {
        var sig = await app.AddSignatureAsync("", 2150);
        using var client = app.Client();

        var resp = await client.PostAsJsonAsync($"{Api}/signatures/{sig.Id}/label", new { category = "boiler" });
        await ShouldBe(resp, HttpStatusCode.OK);

        var stored = await SignatureAsync(sig.Id);
        stored!.Category.Should().Be("boiler");
        stored.Name.Should().Be(sig.Name);
        stored.LabelSource.Should().Be(SignatureLabelSource.Cluster, "as in the UI, a category alone is not a name");
    }

    [Fact]
    public async Task MergeInto_TheSurvivorRuleOfTheUi_SaysWhoStays_404_And403()
    {
        var named = await app.AddSignatureAsync("Fierbător test", 2200, count: 2, source: SignatureLabelSource.User);
        var anon = await app.AddSignatureAsync("", 2210, count: 8);
        await app.AddEventsAsync(anon.Id, 2210, -2205);
        using var client = app.Client();

        using (var outside = app.Client(NilmApiFactory.PublicIp))
            (await outside.PostAsync($"{Api}/signatures/{anon.Id}/merge-into/{named.Id}", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SignatureAsync(anon.Id)).Should().NotBeNull();

        var resp = await client.PostAsync($"{Api}/signatures/{anon.Id}/merge-into/{named.Id}", null);
        await ShouldBe(resp, HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<NilmApi.MergeDto>();

        dto!.SurvivorId.Should().Be(named.Id, "the user's label survives");
        dto.AbsorbedId.Should().Be(anon.Id);
        dto.Survivor!.Name.Should().Be("Fierbător test");
        (await SignatureAsync(anon.Id)).Should().BeNull();
        (await client.PostAsync($"{Api}/signatures/{anon.Id}/merge-into/{named.Id}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"{Api}/signatures/{named.Id}/merge-into/{named.Id}", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddTo_MergesACloseComponent_OnlyNamesADistantOne_404_And403()
    {
        var target = await app.AddSignatureAsync("Mașină test", 2400, source: SignatureLabelSource.User);
        var close = await app.AddSignatureAsync("", 2410);
        var distant = await app.AddSignatureAsync("", 280);
        using var client = app.Client();

        using (var outside = app.Client(NilmApiFactory.PublicIp))
            (await outside.PostAsync($"{Api}/signatures/{close.Id}/add-to/{target.Id}", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var merged = await (await client.PostAsync($"{Api}/signatures/{close.Id}/add-to/{target.Id}", null)).Content.ReadFromJsonAsync<NilmApi.AddToDto>();
        var named = await (await client.PostAsync($"{Api}/signatures/{distant.Id}/add-to/{target.Id}", null)).Content.ReadFromJsonAsync<NilmApi.AddToDto>();

        merged!.Outcome.Should().Be("merged");
        merged.Result!.Id.Should().Be(target.Id);
        (await SignatureAsync(close.Id)).Should().BeNull();
        named!.Outcome.Should().Be("named");
        named.Result!.Id.Should().Be(distant.Id);
        named.Result.Name.Should().Be("Mașină test");
        (await SignatureAsync(distant.Id))!.MeanDeltaP.Should().Be(280, "a distant component keeps its events");
        (await client.PostAsync($"{Api}/signatures/987654/add-to/{target.Id}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HideAndUnhide_404_And403()
    {
        var sig = await app.AddSignatureAsync("", 2500);
        using var client = app.Client();

        var hidden = await client.PostAsync($"{Api}/signatures/{sig.Id}/hide", null);
        await ShouldBe(hidden, HttpStatusCode.OK);
        (await hidden.Content.ReadFromJsonAsync<NilmApi.SignatureDto>())!.Hidden.Should().BeTrue();
        (await SignatureAsync(sig.Id))!.Hidden.Should().BeTrue();

        using (var outside = app.Client(NilmApiFactory.PublicIp))
            (await outside.PostAsync($"{Api}/signatures/{sig.Id}/unhide", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SignatureAsync(sig.Id))!.Hidden.Should().BeTrue();

        await ShouldBe(await client.PostAsync($"{Api}/signatures/{sig.Id}/unhide", null), HttpStatusCode.OK);
        (await SignatureAsync(sig.Id))!.Hidden.Should().BeFalse();
        (await client.PostAsync($"{Api}/signatures/987654/hide", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"{Api}/signatures/987654/unhide", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Regroup_ReturnsTheResultOfTheUi_And403()
    {
        await app.AddEventsAsync(null, 900, -905, 910, -895, 902);
        using var client = app.Client();

        using (var outside = app.Client(NilmApiFactory.PublicIp))
            (await outside.PostAsync($"{Api}/regroup", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var resp = await client.PostAsync($"{Api}/regroup", null);
        await ShouldBe(resp, HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<NilmApi.RegroupDto>();

        dto!.Events.Should().BeGreaterThanOrEqualTo(5);
        dto.Signatures.Should().BeGreaterThan(0);
        dto.Merges.Should().NotBeNull();
        app.Logs.Lines.Should().Contain(l => l.Level == LogLevel.Information && l.Message.StartsWith("NILM API: regroup from " + NilmApiFactory.LanIp));
    }

    [Fact]
    public async Task Plugs_ListAndSingleAppliance_200_400_404_And403()
    {
        var plug = await app.Repo((_, sp) => sp.GetRequiredService<IPlugRepository>().UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.168.1.142"),
            new PlugInfo("aabbccddee21", "Priza bucatarie", "KP115(EU)", "1", "1", "x", true, -50, TimeSpan.Zero), PlugProtocols.Legacy, null, 0,
            PlugRole.Plug), DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));
        using var client = app.Client();

        var before = (await client.GetFromJsonAsync<List<NilmApi.PlugDto>>($"{Api}/plugs"))!.Single(p => p.Id == plug);
        before.Alias.Should().Be("Priza bucatarie");
        before.Single.Should().BeFalse();
        before.NilmState.Should().Be("Auto");

        var url = $"{Api}/plugs/{plug}/appliance";
        using (var outside = app.Client(NilmApiFactory.PublicIp))
            (await outside.PostAsJsonAsync(url, new { name = "Frigider", category = "fridge", single = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await ShouldBe(await client.PostAsJsonAsync(url, new { name = "Frigider", category = "frigider" }), HttpStatusCode.BadRequest);
        await ShouldBe(await client.PostAsJsonAsync($"{Api}/plugs/{Guid.NewGuid()}/appliance", new { name = "X" }), HttpStatusCode.NotFound);
        await ShouldBe(await client.PostAsJsonAsync($"{Api}/plugs/{await app.MainAsync()}/appliance", new { name = "Casa" }), HttpStatusCode.NotFound);

        var resp = await client.PostAsJsonAsync(url, new { name = "Frigider", category = "fridge", single = true });
        await ShouldBe(resp, HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<NilmApi.PlugDto>())!;
        dto.Single.Should().BeTrue();
        dto.ApplianceName.Should().Be("Frigider");
        dto.Category.Should().Be("fridge");
        dto.Alias.Should().Be("Priza bucatarie", "the alias stays");
        (await client.GetFromJsonAsync<List<NilmApi.PlugDto>>($"{Api}/plugs"))!.Single(p => p.Id == plug).Should().Be(dto);
        app.Logs.Lines.Should().Contain(l => l.Level == LogLevel.Information && l.Message.Contains("NILM API: plug Priza bucatarie single appliance True")
            && l.Message.Contains(NilmApiFactory.LanIp));

        var cleared = (await (await client.PostAsJsonAsync(url, new { single = false })).Content.ReadFromJsonAsync<NilmApi.PlugDto>())!;
        cleared.Single.Should().BeFalse();
        cleared.Category.Should().Be("fridge", "no category: unchanged");
    }

    [Fact]
    public async Task Train_WithTooFewLabels_SaysWhatIsMissing_And403()
    {
        using var client = app.Client();

        using (var outside = app.Client(NilmApiFactory.PublicIp))
            (await outside.PostAsync($"{Api}/train", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var resp = await client.PostAsync($"{Api}/train", null);
        await ShouldBe(resp, HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<NilmApi.TrainDto>();

        dto!.Trained.Should().BeFalse();
        dto.Classes.Should().BeEmpty();
        dto.Accuracy.Should().BeNull();
    }

    [Fact]
    public async Task Mark_TheNearestLargeEventWithin90s_IsLabelledByTheUser_ItsAnonymousSignatureTakesNameAndCategory()
    {
        var sig = await app.AddSignatureAsync("", 1950);
        var main = await app.MainAsync();
        var t = app.NextTime();
        var stored = await app.Repo((r, _) => r.AddEventsAsync(new[]
        {
            new MeterEvent(0, main, t.AddSeconds(-60), 1980, 0, 0, 1, 0, 300, sig.Id),   // 80 s from the mark
            new MeterEvent(0, main, t.AddSeconds(5), -60, 0, 0, 1, 0, 300, null),        // nearest, but under 100 W
        }, CancellationToken.None));
        using var client = app.Client();

        using (var outside = app.Client(NilmApiFactory.PublicIp))
            (await outside.PostAsJsonAsync($"{Api}/mark", new { name = "Fierbător API", at = t.AddSeconds(20) })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync($"{Api}/mark", new { name = " " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"{Api}/mark", new { name = "X", category = "rocket" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var resp = await client.PostAsJsonAsync($"{Api}/mark", new { name = "Fierbător API", category = "cooking", at = t.AddSeconds(20) });
        await ShouldBe(resp, HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<NilmApi.MarkDto>();

        dto!.Outcome.Should().Be("matched");
        dto.PendingUntil.Should().BeNull();
        dto.Event!.Id.Should().Be(stored[0].Id);
        dto.Event.Label.Should().Be("Fierbător API");
        dto.Event.Source.Should().Be("User");
        dto.Signature!.Id.Should().Be(sig.Id, "an anonymous signature takes the name");
        dto.Signature.Name.Should().Be("Fierbător API");
        dto.Signature.Category.Should().Be("cooking");
        dto.Signature.LabelSource.Should().Be("User");
        (await app.Repo((r, _) => r.GetEventAsync(stored[1].Id, CancellationToken.None)))!.Source.Should().Be(EventLabelSource.None);
        app.Logs.Lines.Should().Contain(l => l.Level == LogLevel.Information && l.Message.StartsWith("NILM API: mark Fierbător API")
            && l.Message.Contains(NilmApiFactory.LanIp) && l.Message.Contains("Matched"));
    }

    [Fact]
    public async Task Mark_NoEventYet_IsPending_ATimeLongPast_404()
    {
        using var client = app.Client();

        var resp = await client.PostAsJsonAsync($"{Api}/mark", new { name = "Aspirator" });
        await ShouldBe(resp, HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<NilmApi.MarkDto>();

        dto!.Outcome.Should().Be("pending");
        dto.Event.Should().BeNull();
        dto.PendingUntil.Should().BeCloseTo(DateTimeOffset.UtcNow + KasaMonitor.Workers.Nilm.NilmService.MarkPendingFor, TimeSpan.FromSeconds(30));
        (await client.PostAsJsonAsync($"{Api}/mark", new { name = "Aspirator", at = DateTimeOffset.UtcNow.AddDays(-3) }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithAToken_EveryRouteNeedsTheHeader_WritesStillOnlyFromTheLan()
    {
        using var secured = new NilmApiFactory { Token = "test-token-1" };
        var sig = await secured.AddSignatureAsync("", 2600);

        using (var noKey = secured.Client())
        {
            (await noKey.GetAsync($"{Api}/signatures")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await noKey.PostAsync($"{Api}/signatures/{sig.Id}/hide", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using (var wrongKey = secured.Client(apiKey: "test-token-2"))
            (await wrongKey.GetAsync($"{Api}/signatures")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using (var withKey = secured.Client(apiKey: "test-token-1"))
        {
            (await withKey.GetAsync($"{Api}/signatures")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await withKey.PostAsync($"{Api}/signatures/{sig.Id}/hide", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using (var outside = secured.Client(NilmApiFactory.PublicIp, apiKey: "test-token-1"))
            (await outside.PostAsync($"{Api}/signatures/{sig.Id}/unhide", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
