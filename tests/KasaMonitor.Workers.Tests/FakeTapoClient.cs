using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Tapo;
using System.Net;
using System.Runtime.CompilerServices;

namespace KasaMonitor.Workers.Tests;

internal sealed class FakeTapoClient : ITapoClient
{
    public List<TapoDiscoveryResponse> Responses { get; } = new();
    public Func<IPAddress, TapoCredentials, Task<PlugInfo>>? OnGetDeviceInfo = null;
    public Func<IPAddress, TapoCredentials, Task<EmeterReading>>? OnGetEnergyUsage = null;
    public int EnergyCalls;

    public async IAsyncEnumerable<TapoDiscoveryResponse> DiscoverAsync(
        IReadOnlyList<IPAddress> addresses, TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var r in Responses) yield return r;
        await Task.CompletedTask;
    }

    public Task<PlugInfo> GetDeviceInfoAsync(IPAddress ip, string protocol, TapoCredentials credentials, CancellationToken ct) =>
        OnGetDeviceInfo?.Invoke(ip, credentials) ?? throw new InvalidOperationException("OnGetDeviceInfo not set");

    public Task<EmeterReading> GetEnergyUsageAsync(IPAddress ip, string protocol, TapoCredentials credentials, CancellationToken ct)
    {
        Interlocked.Increment(ref EnergyCalls);
        return OnGetEnergyUsage?.Invoke(ip, credentials) ?? throw new InvalidOperationException("OnGetEnergyUsage not set");
    }

    public Func<DateOnly, DateOnly, Task<IReadOnlyList<DailyKwh>>>? OnGetDailyEnergy = null;
    public List<(DateOnly First, DateOnly Last)> DailyEnergyRequests { get; } = new();

    public Task<IReadOnlyList<DailyKwh>> GetDailyEnergyAsync(IPAddress ip, string protocol, TapoCredentials credentials,
        DateOnly firstDay, DateOnly lastDay, DateOnly today, TimeZoneInfo tz, CancellationToken ct)
    {
        lock (DailyEnergyRequests) DailyEnergyRequests.Add((firstDay, lastDay));
        return OnGetDailyEnergy?.Invoke(firstDay, lastDay) ?? throw new InvalidOperationException("OnGetDailyEnergy not set");
    }

    public Func<IPAddress, TapoCredentials, Task<DeviceDetails>>? OnGetDeviceDetails = null;

    public Task<DeviceDetails> GetDeviceDetailsAsync(IPAddress ip, string protocol, TapoCredentials credentials, CancellationToken ct) =>
        OnGetDeviceDetails?.Invoke(ip, credentials) ?? throw new InvalidOperationException("OnGetDeviceDetails not set");

    public bool SupportsProtocol(string protocol) => protocol is PlugProtocols.Klap or PlugProtocols.Aes;
}
