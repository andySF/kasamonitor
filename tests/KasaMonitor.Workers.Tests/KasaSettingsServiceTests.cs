using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class KasaSettingsServiceTests
{
    [Fact]
    public async Task EmptyDatabase_UsesConfigurationDefaults()
    {
        using var f = new TestDbFactory();
        var svc = TestSettings.Create(new TestScopeFactory(f.CreateContext), new KasaSettingsDefaults(
            AutoDiscoveryEnabled: true, AutoDiscoveryIntervalMinutes: 7, BroadcastNetworks: "192.168.254.255",
            AutoAddDiscoveredPlugs: false, TapoUsername: "u@example.com", TapoPassword: "pw"));

        var s = await svc.GetAsync(CancellationToken.None);

        s.AutoDiscoveryEnabled.Should().BeTrue();
        s.AutoDiscoveryIntervalMinutes.Should().Be(7);
        s.BroadcastAddresses.Should().Equal(IPAddress.Parse("192.168.254.255"));
        s.AutoAddDiscoveredPlugs.Should().BeFalse();
        s.TapoCredentials.Should().Be(new TapoCredentials("u@example.com", "pw"));
    }

    [Fact]
    public async Task GrafanaUrl_DefaultFromConfiguration_SavedValueWins_EmptyMeansNoLink_InvalidRefused()
    {
        using var f = new TestDbFactory();
        var svc = TestSettings.Create(new TestScopeFactory(f.CreateContext), new KasaSettingsDefaults(GrafanaUrl: "http://grafana.local:3000/d/x"));
        (await svc.GetAsync(CancellationToken.None)).GrafanaUrl.Should().Be("http://grafana.local:3000/d/x");

        var keep = new KasaSettingsUpdate(true, 5, "", true, "", null, false);
        await svc.SaveAsync(keep with { GrafanaUrl = " https://g.example/d/kasa " }, CancellationToken.None);
        (await svc.GetAsync(CancellationToken.None)).GrafanaUrl.Should().Be("https://g.example/d/kasa");

        await svc.SaveAsync(keep, CancellationToken.None);
        (await svc.GetAsync(CancellationToken.None)).GrafanaUrl.Should().Be("https://g.example/d/kasa", "null keeps the stored value");

        await svc.SaveAsync(keep with { GrafanaUrl = "" }, CancellationToken.None);
        (await svc.GetAsync(CancellationToken.None)).GrafanaUrl.Should().BeEmpty("empty = no link");

        var bad = () => svc.SaveAsync(keep with { GrafanaUrl = "grafana:3000" }, CancellationToken.None);
        await bad.Should().ThrowAsync<FormatException>();
        KasaSettingsService.ValidateGrafanaUrl("javascript:alert(1)").Should().NotBeNull();
    }

    [Fact]
    public async Task SavedValues_WinOverDefaults_AndPasswordIsStoredProtected()
    {
        using var f = new TestDbFactory();
        var scopes = new TestScopeFactory(f.CreateContext);
        var svc = TestSettings.Create(scopes, new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255"));

        await svc.SaveAsync(new KasaSettingsUpdate(false, 15, "10.0.0.255, 192.168.1.255", true, "me@example.com", "s3cret", false),
            CancellationToken.None);
        var s = await svc.GetAsync(CancellationToken.None);

        s.AutoDiscoveryEnabled.Should().BeFalse();
        s.AutoDiscoveryIntervalMinutes.Should().Be(15);
        s.BroadcastAddresses.Should().Equal(IPAddress.Parse("10.0.0.255"), IPAddress.Parse("192.168.1.255"));
        s.TapoCredentials.Should().Be(new TapoCredentials("me@example.com", "s3cret"));

        using var ctx = f.CreateContext();
        var raw = await new SettingsRepository(ctx).GetAsync(SettingKeys.TapoPassword, CancellationToken.None);
        raw.Should().NotBeNullOrEmpty().And.NotContain("s3cret");
    }

    [Fact]
    public async Task EmptyNewPassword_KeepsStored_ClearRemovesIt_WithoutEnvFallback()
    {
        using var f = new TestDbFactory();
        var svc = TestSettings.Create(new TestScopeFactory(f.CreateContext), new KasaSettingsDefaults(TapoPassword: "from-env"));

        await svc.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "me@example.com", "s3cret", false), CancellationToken.None);
        await svc.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "me@example.com", "", false), CancellationToken.None);
        (await svc.GetAsync(CancellationToken.None)).TapoCredentials!.Password.Should().Be("s3cret");

        await svc.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "me@example.com", null, ClearTapoPassword: true), CancellationToken.None);
        var s = await svc.GetAsync(CancellationToken.None);
        s.HasTapoPassword.Should().BeFalse();
        s.TapoCredentials.Should().BeNull();
    }

    [Fact]
    public async Task EmptyBroadcastInDatabase_FallsBackToConfiguration_ThenToGlobalBroadcast()
    {
        using var f = new TestDbFactory();
        var scopes = new TestScopeFactory(f.CreateContext);
        using (var ctx = f.CreateContext())
            await new SettingsRepository(ctx).SetAsync(SettingKeys.BroadcastNetworks, "", CancellationToken.None);

        (await TestSettings.Create(scopes, new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255")).GetAsync(CancellationToken.None))
            .BroadcastAddresses.Should().Equal(IPAddress.Parse("192.168.254.255"));
        (await TestSettings.Create(scopes, new KasaSettingsDefaults()).GetAsync(CancellationToken.None))
            .BroadcastAddresses.Should().Equal(IPAddress.Broadcast);
    }

    [Fact]
    public async Task InvalidBroadcastAddress_IsRejectedOnSave()
    {
        using var f = new TestDbFactory();
        var svc = TestSettings.Create(new TestScopeFactory(f.CreateContext));

        var act = () => svc.SaveAsync(new KasaSettingsUpdate(true, 5, "192.168.254.255, nope", true, "", null, false), CancellationToken.None);

        await act.Should().ThrowAsync<FormatException>().WithMessage("*nope*");
    }
}
