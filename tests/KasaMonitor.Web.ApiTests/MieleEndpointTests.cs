using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Miele;
using KasaMonitor.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>The app on a temporary database, without background workers, with a fake Miele client secret from the environment setting.</summary>
public sealed class MieleFactory : WebApplicationFactory<Program>
{
    public const string FakeSecret = "test-secret-value-123";
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-miele-{Guid.NewGuid():N}");

    public MieleFactory() => Directory.CreateDirectory(Dir);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSqliteTestDatabase(Dir);
        builder.UseSetting("KasaMonitor:MieleClientSecret", FakeSecret);
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }

    public HttpClient NoRedirect() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class MieleEndpointTests : IClassFixture<MieleFactory>
{
    private readonly MieleFactory _f;
    public MieleEndpointTests(MieleFactory f) => _f = f;

    [Fact]
    public async Task Connect_WithoutClientId_BackToAparate_WithClientId_RedirectsToMiele_WithStateCookie_CallbackChecksIt()
    {
        var c = _f.NoRedirect();
        using (var scope = _f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().SetAsync(MieleSettingKeys.MieleClientId, "", CancellationToken.None);
        var r = await c.GetAsync(MieleApi.ConnectPath);
        r.StatusCode.Should().Be(HttpStatusCode.Redirect);
        r.Headers.Location!.ToString().Should().Be("/aparate?auth=notconfigured");

        await _f.Services.GetRequiredService<IMieleSettingsService>().SaveClientIdAsync("client-abc", CancellationToken.None);
        r = await c.GetAsync(MieleApi.ConnectPath);
        r.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = r.Headers.Location!.OriginalString;
        location.Should().StartWith(MieleApi.AuthorizeUrl + "?response_type=code&client_id=client-abc&redirect_uri=http%3A%2F%2Flocalhost%2Fmiele%2Fcallback");
        location.Should().Contain("scope=openid%20mcs_thirdparty_read").And.Contain("&state=").And.NotContain(MieleFactory.FakeSecret);
        r.Headers.GetValues("Set-Cookie").Should().Contain(x => x.StartsWith(MieleAuthService.StateCookieName + "=") && x.Contains("path=/miele") && x.Contains("httponly"));

        var forged = await c.GetAsync(MieleApi.CallbackPath + "?state=forged&code=x");
        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest, "no cookie of this browser");
    }

    [Fact]
    public async Task Pages_And_Metrics_RenderWithoutTheSecret()
    {
        var c = _f.CreateClient();
        var aparate = await c.GetStringAsync("/aparate");
        aparate.Should().Contain("Aparate Miele").And.Contain("Istoric cicluri").And.Contain("Miele oferă ultimele 5 rulări; de la conectare încolo KasaMonitor păstrează tot.");
        var settings = await c.GetStringAsync("/settings/miele");
        settings.Should().Contain("Miele (mașină de spălat, uscător)").And.Contain("încărcat").And.NotContain(MieleFactory.FakeSecret);
        (await c.GetStringAsync("/integration")).Should().Contain("miele_program_phase_info");
        (await c.GetStringAsync("/metrics")).Should().Contain("miele_connected 0");
    }
}
