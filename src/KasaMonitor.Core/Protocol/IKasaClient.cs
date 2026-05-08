using KasaMonitor.Core.Models;
using System.Net;

namespace KasaMonitor.Core.Protocol;

public interface IKasaClient
{
    Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct);
    Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct);
    Task<IReadOnlyList<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct);
    Task<IReadOnlyList<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct);
    IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(IReadOnlyList<IPAddress> broadcastAddresses, TimeSpan timeout, CancellationToken ct);
}
