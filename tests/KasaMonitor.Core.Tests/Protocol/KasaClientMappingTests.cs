using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Net;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaClientMappingTests
{
    private static string WrapSysInfo(string innerJson) => $"{{\"system\":{{\"get_sysinfo\":{innerJson}}}}}";
    private static string WrapEmeter(string method, string innerJson) => $"{{\"emeter\":{{\"{method}\":{innerJson}}}}}";

    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public async Task GetSysInfoAsync_MapsDtoToDomainModel_NormalizingMac()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapSysInfo(LoadFixture("sysinfo_kp115_birou.json")));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var info = await client.GetSysInfoAsync(IPAddress.Loopback, CancellationToken.None);
            await serverTask;

            info.Mac.Should().Be("c006c3702bfc"); // lowercase, separators stripped
            info.Alias.Should().Be("Birou");
            info.Model.Should().StartWith("KP115");
            info.RelayOn.Should().BeTrue();        // wire was 1
            info.RssiDbm.Should().BeNegative();
            info.OnTime.Should().BeGreaterThan(TimeSpan.Zero);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetRealtimeAsync_MapsDtoToDomainModel()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapEmeter("get_realtime", LoadFixture("emeter_kp115.json")));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var reading = await client.GetRealtimeAsync(IPAddress.Loopback, CancellationToken.None);
            await serverTask;

            reading.PowerMw.Should().BeGreaterThan(0);
            reading.VoltageMv.Should().BeGreaterThan(200_000);
            reading.CurrentMa.Should().BeGreaterThan(0);
            reading.TotalWh.Should().BeGreaterOrEqualTo(0);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetRealtimeAsync_ThrowsKasaProtocolException_OnNonZeroErrCode()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapEmeter("get_realtime", "{\"voltage_mv\":0,\"current_ma\":0,\"power_mw\":0,\"total_wh\":0,\"err_code\":-1}"));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var act = async () => await client.GetRealtimeAsync(IPAddress.Loopback, CancellationToken.None);

            await act.Should().ThrowAsync<KasaProtocolException>().Where(e => e.ErrCode == -1);
            await serverTask;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetDayStatAsync_MapsAllEntries()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapEmeter("get_daystat", LoadFixture("daystat_kp115.json")));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var days = await client.GetDayStatAsync(IPAddress.Loopback, 2026, 5, CancellationToken.None);
            await serverTask;

            days.Should().NotBeEmpty();
            days.Should().AllSatisfy(d =>
            {
                d.Year.Should().BeGreaterThan(2020);
                d.Day.Should().BeInRange(1, 31);
                d.EnergyWh.Should().BeGreaterOrEqualTo(0);
            });
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetMonthStatAsync_MapsAllEntries()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapEmeter("get_monthstat", LoadFixture("monstat_kp115.json")));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var months = await client.GetMonthStatAsync(IPAddress.Loopback, 2026, CancellationToken.None);
            await serverTask;

            months.Should().NotBeEmpty();
            months.Should().AllSatisfy(m => m.Month.Should().BeInRange(1, 12));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetSysInfoAsync_RequestPayloadAndMacNormalization_AreCorrect()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            string? capturedRequest = null;
            var serverTask = Task.Run(async () =>
            {
                capturedRequest = await LoopbackTestServer.ReceiveRequestAsync(
                    listener, WrapSysInfo(LoadFixture("sysinfo_kp115_birou.json")));
            });

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            await client.GetSysInfoAsync(IPAddress.Loopback, CancellationToken.None);
            await serverTask;

            capturedRequest.Should().Contain("\"system\"").And.Contain("\"get_sysinfo\"");
        }
        finally { listener.Stop(); }
    }
}
