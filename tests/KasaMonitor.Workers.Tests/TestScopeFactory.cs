using KasaMonitor.Core.Abstractions;
using KasaMonitor.Data;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers.Tests;

internal sealed class TestScopeFactory : IServiceScopeFactory
{
    private readonly Func<KasaDbContext> _ctxFactory;

    public TestScopeFactory(Func<KasaDbContext> ctxFactory)
    {
        _ctxFactory = ctxFactory;
    }

    public IServiceScope CreateScope() => new TestScope(_ctxFactory);

    private sealed class TestScope : IServiceScope, IServiceProvider
    {
        private readonly KasaDbContext _ctx;
        public TestScope(Func<KasaDbContext> ctxFactory) { _ctx = ctxFactory(); }
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(KasaDbContext)) return _ctx;
            if (serviceType == typeof(IPlugRepository)) return new PlugRepository(_ctx);
            if (serviceType == typeof(ISampleRepository)) return new SampleRepository(_ctx);
            if (serviceType == typeof(IHistoryRepository)) return new HistoryRepository(_ctx);
            if (serviceType == typeof(ISettingsRepository)) return new SettingsRepository(_ctx);
            if (serviceType == typeof(ILiveRepository)) return new LiveRepository(_ctx);
            if (serviceType == typeof(IHeatingRepository)) return new HeatingRepository(_ctx);
            if (serviceType == typeof(INilmRepository)) return new NilmRepository(_ctx);
            if (serviceType == typeof(IMieleRepository)) return new MieleRepository(_ctx);
            return null;
        }

        public void Dispose() => _ctx.Dispose();
    }
}
