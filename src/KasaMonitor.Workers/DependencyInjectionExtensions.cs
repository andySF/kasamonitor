using KasaMonitor.Core.Abstractions;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddKasaWorkers(this IServiceCollection services)
    {
        services.AddScoped<IPlugRepository, PlugRepository>();
        services.AddScoped<ISampleRepository, SampleRepository>();
        services.AddScoped<IHistoryRepository, HistoryRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();

        services.AddSingleton<IPlugStateBroadcaster, PlugStateBroadcaster>();
        services.AddSingleton<IPlugDiscoveredChannel, PlugDiscoveredChannel>();
        return services;
    }
}
