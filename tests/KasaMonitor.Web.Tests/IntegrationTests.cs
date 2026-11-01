using FluentAssertions;
using KasaMonitor.Web.Integration;
using Microsoft.AspNetCore.Http;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class IntegrationConfigGeneratorTests
{
    [Fact]
    public void EmbeddedDeployFiles_ArePresent()
    {
        DeployFiles.All.Should().Contain(new[]
        {
            DeployFiles.MonitoringCompose, DeployFiles.PrometheusConfig, DeployFiles.Dashboard,
            DeployFiles.AlertRules, DeployFiles.DataSource, DeployFiles.DashboardProvider,
        });
        DeployFiles.Read(DeployFiles.PrometheusConfig).Should().Contain(IntegrationConfigGenerator.RepositoryTarget);
    }

    [Theory]
    [InlineData("192.168.1.10", "192.168.1.10:8910")]
    [InlineData("192.168.1.10:9000", "192.168.1.10:9000")]
    [InlineData(" http://nas.local:8910/ ", "nas.local:8910")]
    [InlineData("host.docker.internal:8910", "host.docker.internal:8910")]
    [InlineData("[fd00::5]:8910", "[fd00::5]:8910")]
    public void NormalizeTarget_Accepts(string input, string expected)
    {
        IntegrationConfigGenerator.NormalizeTarget(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.10:99999")]
    [InlineData("a\"]\n  - evil")]
    [InlineData("host/path")]
    [InlineData("host:port")]
    public void NormalizeTarget_Rejects(string input)
    {
        var act = () => IntegrationConfigGenerator.NormalizeTarget(input);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ScrapeSnippet_ContainsJobAndTarget()
    {
        var s = IntegrationConfigGenerator.ScrapeConfigSnippet("192.168.1.10:8910");

        s.Should().StartWith("scrape_configs:");
        s.Should().Contain("job_name: kasa");
        s.Should().Contain("- targets: [\"192.168.1.10:8910\"]");
        s.Should().Contain("metrics_path: /metrics");
    }

    [Fact]
    public void PrometheusYml_ReplacesOnlyTheTarget()
    {
        var original = DeployFiles.Read(DeployFiles.PrometheusConfig);

        var yml = IntegrationConfigGenerator.PrometheusYml("10.0.0.7:8910");

        yml.Should().Contain("- targets: [\"10.0.0.7:8910\"]");
        yml.Should().NotContain("[\"host.docker.internal:8910\"]");
        yml.Should().Contain("job_name: kasa");
        yml.Length.Should().Be(original.Length - IntegrationConfigGenerator.RepositoryTarget.Length + "10.0.0.7:8910".Length);
    }

    [Fact]
    public void DashboardJson_DefaultUid_IsTheRepositoryFile()
    {
        IntegrationConfigGenerator.DashboardJson().Should().Be(DeployFiles.Read(DeployFiles.Dashboard));
    }

    [Fact]
    public void DashboardJson_CustomUid_ReplacesEveryPrometheusDataSource()
    {
        var json = IntegrationConfigGenerator.DashboardJson("abc-123");

        using var doc = JsonDocument.Parse(json);
        var uids = new List<string>();
        Collect(doc.RootElement, uids);
        uids.Should().NotBeEmpty().And.OnlyContain(u => u == "abc-123");
        doc.RootElement.GetProperty("uid").GetString().Should().Be("kasamonitor"); // dashboard uid untouched
    }

    [Fact]
    public void AlertRules_CustomUid_Replaced()
    {
        var yml = IntegrationConfigGenerator.AlertRulesYml("abc-123");
        yml.Should().Contain("datasourceUid: abc-123").And.NotContain("datasourceUid: prometheus");
        yml.Should().Contain("datasourceUid: __expr__");
    }

    [Fact]
    public void DataSourceUid_Invalid_Rejected()
    {
        var act = () => IntegrationConfigGenerator.NormalizeDataSourceUid("x\" onload=");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Bundle_HasDeployLayout_WithTargetFilledIn()
    {
        var zipBytes = IntegrationConfigGenerator.MonitoringBundle("192.168.1.10:8910");

        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        names.Should().Contain(new[]
        {
            "docker-compose.monitoring.yml", "prometheus/prometheus.yml", "README.txt",
            "grafana/provisioning/datasources/prometheus.yml", "grafana/provisioning/dashboards/dashboards.yml",
            "grafana/provisioning/dashboards/kasamonitor.json", "grafana/provisioning/alerting/rules.yml",
        });
        using var r = new StreamReader(zip.GetEntry("prometheus/prometheus.yml")!.Open());
        r.ReadToEnd().Should().Contain("[\"192.168.1.10:8910\"]");
        using var c = new StreamReader(zip.GetEntry("docker-compose.monitoring.yml")!.Open());
        c.ReadToEnd().Should().Be(DeployFiles.Read(DeployFiles.MonitoringCompose));
    }

    [Fact]
    public void DefaultTarget_UsesBrowserHost_OrLanIpForLocalhost()
    {
        var lan = new[] { IPAddress.Parse("192.168.1.10") };
        HostAddresses.DefaultTarget(new Uri("http://192.168.1.20:8910/"), lan).Should().Be("192.168.1.20:8910");
        HostAddresses.DefaultTarget(new Uri("http://localhost:5000/"), lan).Should().Be("192.168.1.10:5000");
        HostAddresses.DefaultTarget(new Uri("http://127.0.0.1:8910/"), lan).Should().Be("192.168.1.10:8910");
        HostAddresses.DefaultTarget(new Uri("http://localhost:8910/"), Array.Empty<IPAddress>()).Should().Be("localhost:8910");
    }

    private static void Collect(JsonElement e, List<string> uids)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "prometheus"
                && e.TryGetProperty("uid", out var u))
                uids.Add(u.GetString()!);
            foreach (var p in e.EnumerateObject()) Collect(p.Value, uids);
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) Collect(x, uids);
        }
    }
}

public class MetricsScrapeLogTests
{
    private static DefaultHttpContext Request(string method, string path, string? ua, string ip = "10.0.0.5")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (ua is not null) ctx.Request.Headers.UserAgent = ua;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return ctx;
    }

    [Fact]
    public async Task RecordsGetMetrics_AndCallsNext()
    {
        var log = new MetricsScrapeLog();
        var nextCalled = 0;
        var at = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var mw = new MetricsScrapeLogMiddleware(_ => { nextCalled++; return Task.CompletedTask; }, log, () => at);

        await mw.InvokeAsync(Request("GET", "/metrics", "Prometheus/3.5.0"));

        nextCalled.Should().Be(1);
        var last = log.LastPrometheus();
        last.Should().NotBeNull();
        last!.AtUtc.Should().Be(at);
        last.RemoteIp.Should().Be("10.0.0.5");
        last.UserAgent.Should().Be("Prometheus/3.5.0");
    }

    [Fact]
    public async Task IgnoresOtherPathsAndMethods()
    {
        var log = new MetricsScrapeLog();
        var mw = new MetricsScrapeLogMiddleware(_ => Task.CompletedTask, log);

        await mw.InvokeAsync(Request("GET", "/plugs", "Prometheus/3.5.0"));
        await mw.InvokeAsync(Request("POST", "/metrics", "Prometheus/3.5.0"));
        await mw.InvokeAsync(Request("GET", "/metricsx", "Prometheus/3.5.0"));

        log.Recent().Should().BeEmpty();
        log.LastPrometheus().Should().BeNull();
    }

    [Fact]
    public async Task BrowserRequest_IsListedButIsNotAPrometheusScrape()
    {
        var log = new MetricsScrapeLog();
        var mw = new MetricsScrapeLogMiddleware(_ => Task.CompletedTask, log);

        await mw.InvokeAsync(Request("GET", "/metrics", "Mozilla/5.0"));

        log.Recent().Should().ContainSingle();
        log.LastPrometheus().Should().BeNull();
    }

    [Fact]
    public void KeepsOnlyTheLastN_NewestFirst()
    {
        var log = new MetricsScrapeLog(capacity: 3);
        for (int i = 0; i < 5; i++)
            log.Record(new ScrapeEntry(DateTimeOffset.UnixEpoch.AddSeconds(i), $"10.0.0.{i}", "Prometheus/3"));

        log.Recent().Select(e => e.RemoteIp).Should().Equal("10.0.0.4", "10.0.0.3", "10.0.0.2");
        log.LastPrometheus()!.RemoteIp.Should().Be("10.0.0.4");
    }
}

public class MetricsSnapshotTests
{
    [Fact]
    public void ParseSamples_ReadsLabelsAndValues()
    {
        const string text = "# HELP kasa_power_load Instantaneous active power (watts).\n" +
                            "# TYPE kasa_power_load gauge\n" +
                            "kasa_power_load{alias=\"Birou \\\"PC\\\"\",mac=\"aa\",model=\"KP115\",protocol=\"legacy\"} 12.5\n" +
                            "kasa_online 1\n" +
                            "garbage line\n";

        var s = MetricsSnapshot.ParseSamples(text);

        s.Should().HaveCount(2);
        s[0].Name.Should().Be("kasa_power_load");
        s[0].Labels["alias"].Should().Be("Birou \"PC\"");
        s[0].Labels["protocol"].Should().Be("legacy");
        s[0].Value.Should().Be(12.5);
        s[1].Name.Should().Be("kasa_online");
    }

    [Fact]
    public async Task KasaFamilies_CountsSeriesFromRegistry()
    {
        var bc = new KasaMonitor.Workers.PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);
        bc.Publish(new KasaMonitor.Core.Models.PlugLiveState(Guid.NewGuid(), "snap00000001", "AAA snapshot", true,
            new KasaMonitor.Core.Models.EmeterReading(5_000, 230_000, 20, 7), DateTimeOffset.UtcNow, "KP115"));

        var families = await MetricsSnapshot.KasaFamiliesAsync();

        families.Select(f => f.Info.Name).Should().Equal(KasaMetrics.Catalog.Select(c => c.Name));
        var power = families.Single(f => f.Info.Name == "kasa_power_load");
        power.SeriesCount.Should().BeGreaterThan(0);
        power.Info.Unit.Should().Be("W");
        await metrics.StopAsync(CancellationToken.None);
    }
}

public class PrometheusTargetsCheckerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public Uri? LastUri;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(_f(request));
        }
    }

    private const string TargetsJson = """
        {"status":"success","data":{"activeTargets":[
          {"labels":{"job":"kasa","instance":"kasamonitor"},"scrapeUrl":"http://192.168.1.10:8910/metrics","health":"up","lastError":"","lastScrape":"2026-11-01T12:00:00Z"},
          {"labels":{"job":"node"},"scrapeUrl":"http://x:9100/metrics","health":"up","lastError":""}
        ],"droppedTargets":[]}}
        """;

    [Fact]
    public async Task KasaJobUp_IsReported()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TargetsJson, Encoding.UTF8, "application/json") });
        var c = new PrometheusTargetsChecker(h);

        var r = await c.CheckAsync("prom.local:9090", CancellationToken.None);

        h.LastUri!.ToString().Should().Be("http://prom.local:9090/api/v1/targets?state=active");
        r.Reachable.Should().BeTrue();
        r.KasaUp.Should().BeTrue();
        r.Targets.Should().ContainSingle().Which.ScrapeUrl.Should().Be("http://192.168.1.10:8910/metrics");
    }

    [Fact]
    public async Task MissingJob_IsExplained()
    {
        var json = TargetsJson.Replace("\"job\":\"kasa\"", "\"job\":\"other\"");
        var c = new PrometheusTargetsChecker(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));

        var r = await c.CheckAsync("localhost:9090", CancellationToken.None);

        r.Reachable.Should().BeTrue();
        r.KasaUp.Should().BeFalse();
        r.Message.Should().Contain("nu are job-ul");
    }

    [Fact]
    public async Task Unreachable_IsReported()
    {
        var c = new PrometheusTargetsChecker(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var r = await c.CheckAsync("localhost:9090", CancellationToken.None);

        r.Reachable.Should().BeFalse();
        r.Message.Should().Contain("connection refused");
    }

    [Theory]
    [InlineData("localhost:9090", "http://localhost:9090/")]
    [InlineData("https://prom.example.com", "https://prom.example.com/")]
    public void BaseUri_Normalizes(string input, string expected)
    {
        PrometheusTargetsChecker.BaseUri(input).ToString().Should().Be(expected);
    }

    [Fact]
    public void BaseUri_RejectsPaths()
    {
        var act = () => PrometheusTargetsChecker.BaseUri("http://evil/some/path?x=1");
        act.Should().Throw<FormatException>();
    }
}
