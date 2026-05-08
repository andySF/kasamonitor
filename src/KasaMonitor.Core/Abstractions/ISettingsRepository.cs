namespace KasaMonitor.Core.Abstractions;

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct);
}
