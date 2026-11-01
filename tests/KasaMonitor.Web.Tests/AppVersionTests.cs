using FluentAssertions;
using KasaMonitor.Web.Components.Layout;
using KasaMonitor.Web.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class AppVersionTests
{
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    [Fact]
    public async Task NavMenu_RendersTheVersion_RightAligned_WithATooltip()
    {
        var services = new ServiceCollection()
            .AddSingleton<NavigationManager, TestNavigationManager>()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);

        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NavMenu>()).ToHtmlString());

        AppVersion.Short.Should().MatchRegex(@"^v\d+\.\d+\.\d+");
        html.Should().Contain(">" + AppVersion.Short + "</span>");
        html.Should().Contain("ms-auto").And.Contain("app-version");
        html.Should().Contain("title=\"KasaMonitor ");
    }

    [Fact]
    public void Format_SplitsTheCommit_AndAddsTheBuildTime()
    {
        var (shortText, title) = AppVersion.Format("0.4.0+66dbb9a1234567890", new DateTimeOffset(2026, 11, 1, 16, 5, 0, TimeSpan.Zero));
        shortText.Should().Be("v0.4.0");
        title.Should().Be("KasaMonitor 0.4.0 · commit 66dbb9a · build 2026-11-01 16:05 UTC");

        AppVersion.Format("0.4.0", null).Should().Be(("v0.4.0", "KasaMonitor 0.4.0"));
    }
}
