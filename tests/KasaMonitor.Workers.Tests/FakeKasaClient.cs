using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using System.Net;
using System.Runtime.CompilerServices;

namespace KasaMonitor.Workers.Tests;

internal sealed class FakeKasaClient : IKasaClient
{
    public Func<IPAddress, Task<PlugInfo>>? OnGetSysInfo = null;
    public Func<IPAddress, Task<EmeterReading>>? OnGetRealtime = null;
    public Func<IPAddress, int, int, Task<IReadOnlyList<DailyKwh>>>? OnGetDayStat = null;
    public Func<IPAddress, int, Task<IReadOnlyList<MonthlyKwh>>>? OnGetMonthStat = null;
    public List<DiscoveredPlug> DiscoveredPlugs { get; } = new();
    public Func<IPAddress, Task<DeviceDetails>>? OnGetDeviceDetails = null;

    public Task<DeviceDetails> GetDeviceDetailsAsync(IPAddress ip, CancellationToken ct) =>
        OnGetDeviceDetails?.Invoke(ip) ?? throw new InvalidOperationException("OnGetDeviceDetails not set");

    public Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct) =>
        OnGetSysInfo?.Invoke(ip) ?? throw new InvalidOperationException("OnGetSysInfo not set");

    public Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct) =>
        OnGetRealtime?.Invoke(ip) ?? throw new InvalidOperationException("OnGetRealtime not set");

    public Task<IReadOnlyList<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct) =>
        OnGetDayStat?.Invoke(ip, year, month) ?? throw new InvalidOperationException("OnGetDayStat not set");

    public Task<IReadOnlyList<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct) =>
        OnGetMonthStat?.Invoke(ip, year) ?? throw new InvalidOperationException("OnGetMonthStat not set");

    public async IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(
        IReadOnlyList<IPAddress> broadcastAddresses, TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var p in DiscoveredPlugs) yield return p;
        await Task.CompletedTask;
    }
}
