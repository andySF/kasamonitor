using FluentAssertions;
using KasaMonitor.Web.Diagnostics;
using KasaMonitor.Web.Ui;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class ClientErrorEndpointTests
{
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Category, string Message)> Entries = new();
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class L : ILogger
        {
            private readonly CapturingLoggerFactory _f;
            private readonly string _cat;
            public L(CapturingLoggerFactory f, string cat) { _f = f; _cat = cat; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_f.Entries) _f.Entries.Add((logLevel, _cat, formatter(state, exception)));
            }
        }
    }

    private static DefaultHttpContext Ctx()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = "Mozilla/5.0 Test";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.77");
        return ctx;
    }

    [Fact]
    public void Report_IsLoggedAsWarning_WithUrlAgentAndStack()
    {
        var loggers = new CapturingLoggerFactory();
        var ep = new ClientErrorEndpoint(loggers);

        var result = ep.Handle(new ClientErrorReport("blazor-error-ui", "TypeError: x is undefined", "at f (kasa.js:1)", "http://h:8910/plugs",
            new[] { "Error: There was an unhandled exception on the current circuit" }), Ctx());

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(204);
        var e = loggers.Entries.Single();
        e.Level.Should().Be(LogLevel.Warning);
        e.Category.Should().Be("KasaMonitor.ClientErrors");
        e.Message.Should().Contain("blazor-error-ui").And.Contain("http://h:8910/plugs").And.Contain("Mozilla/5.0 Test")
            .And.Contain("192.168.1.77").And.Contain("TypeError: x is undefined").And.Contain("kasa.js:1").And.Contain("unhandled exception");
    }

    [Fact]
    public void EmptyReport_IsRejected_AndNotLogged()
    {
        var loggers = new CapturingLoggerFactory();
        var ep = new ClientErrorEndpoint(loggers);

        ep.Handle(new ClientErrorReport("error", " ", null, null, null), Ctx())
            .Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        ep.Handle(null, Ctx()).Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        loggers.Entries.Should().BeEmpty();
    }

    [Fact]
    public void LongFields_AreTruncated_AndFloodIsLimited()
    {
        var loggers = new CapturingLoggerFactory();
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var ep = new ClientErrorEndpoint(loggers, () => now);
        var huge = new string('x', 10_000);

        for (int i = 0; i < ClientErrorEndpoint.MaxPerMinute + 5; i++)
            ep.Handle(new ClientErrorReport("error", huge, huge, "u", null), Ctx());

        loggers.Entries.Should().HaveCount(ClientErrorEndpoint.MaxPerMinute);
        loggers.Entries[0].Message.Length.Should().BeLessThan(5_000);
        ep.Handle(new ClientErrorReport("error", "late", null, "u", null), Ctx())
            .Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(429);

        now = now.AddMinutes(1);
        ep.Handle(new ClientErrorReport("error", "next minute", null, "u", null), Ctx())
            .Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(204);
    }
}

public class UiFormatTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

    [Theory]
    [InlineData(12, "acum 12 s")]
    [InlineData(180, "acum 3 min")]
    [InlineData(5 * 3600 + 10, "acum 5 h")]
    [InlineData(26 * 3600, "acum 1 zi")]
    [InlineData(3 * 86400, "acum 3 zile")]
    [InlineData(-90, "peste 1 min")]
    public void RelativeRo(int secondsAgo, string expected)
    {
        UiFormat.RelativeRo(Now.AddSeconds(-secondsAgo), Now).Should().Be(expected);
    }

    [Theory]
    [InlineData(45.4, "45 s")]
    [InlineData(750.0, "12 min")]
    [InlineData(3900.0, "1 h 05 min")]
    [InlineData(null, "—")]
    public void DurationRo(double? seconds, string expected)
    {
        UiFormat.DurationRo(seconds).Should().Be(expected);
    }

    [Fact]
    public void ProtocolBadges_AreDistinct()
    {
        new[] { "legacy", "klap", "aes", "unsupported" }.Select(UiFormat.ProtocolBadge).Should().OnlyHaveUniqueItems();
    }
}
