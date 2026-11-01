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
        services.AddScoped<ILiveRepository, LiveRepository>();
        services.AddScoped<IHeatingRepository, HeatingRepository>();
        services.AddScoped<INilmRepository, NilmRepository>();

        services.AddSingleton<IPlugStateBroadcaster, PlugStateBroadcaster>();
        services.AddSingleton<ILiveStreamBroadcaster>(sp => new LiveStreamBroadcaster(
            sp.GetService<Microsoft.Extensions.Logging.ILogger<LiveStreamBroadcaster>>()));
        services.AddSingleton<IPlugDiscoveredChannel, PlugDiscoveredChannel>();
        services.AddSingleton<IKasaSettingsService, KasaSettingsService>();
        services.AddSingleton<ReadingsRetentionService>(sp => new ReadingsRetentionService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ReadingsRetentionService>>()));
        return services;
    }
}
