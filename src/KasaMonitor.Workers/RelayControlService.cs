using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>Outcome of a relay command (the logged row and whether the device accepted it).</summary>
public sealed record RelayCommandResult(bool Success, string Message, RelayCommand? Logged);

/// <summary>
/// The only write path to a device: Shelly <c>Switch.Set {id, on}</c> over HTTP RPC (digest when a password is set).
/// Allowed only when <see cref="SettingKeys.AllowRelayControl"/> is on and only for Shelly channels that have a relay
/// (<see cref="HasRelay"/>); every attempt is logged in <c>RelayCommands</c>. Kasa/Tapo plugs have no command here.
/// </summary>
public sealed class RelayControlService
{
    public const string DisabledMessage = "Comanda releului e dezactivată (Setări → Permite comanda releului).";
    public const string NoRelayMessage = "Canalul nu are releu comandabil (doar dispozitivele Shelly cu switch).";

    private readonly IShellyClient _shelly;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKasaSettingsService _settings;
    private readonly ILiveStreamBroadcaster _broadcaster;
    private readonly IPlugStateBroadcaster _states;
    private readonly ILogger<RelayControlService> _log;
    private readonly Func<DateTimeOffset> _clock;

    public RelayControlService(IShellyClient shelly, IServiceScopeFactory scopeFactory, IKasaSettingsService settings,
        ILiveStreamBroadcaster broadcaster, IPlugStateBroadcaster states, ILogger<RelayControlService> log, Func<DateTimeOffset>? clock = null)
    {
        _states = states;
        _shelly = shelly;
        _scopeFactory = scopeFactory;
        _settings = settings;
        _broadcaster = broadcaster;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A Shelly channel with a relay: channel 0 of a meter with <c>switch:0</c> (EM Gen3, Pro EM) or a <c>switch:N</c>
    /// plug (Plus Plug S, 1PM, ...). The relay's switch id is the channel number. Known from the last reading
    /// (<see cref="EmeterReading.RelayOn"/> is set only where the device reports a relay for this channel).
    /// </summary>
    public static bool HasRelay(PlugLiveState? live) =>
        live is { Protocol: PlugProtocols.ShellyRpc, LastReading.RelayOn: not null };

    /// <summary>The newest known relay state: the 10 s poll or the stream/command, whichever is more recent.</summary>
    public static bool? CurrentState(PlugLiveState? live, RelayState? streamed)
    {
        var polled = live?.LastReading?.RelayOn;
        if (streamed is null) return polled;
        if (polled is null) return streamed.On;
        return streamed.AtUtc >= live!.LastSampleUtc ? streamed.On : polled;
    }

    public async Task<bool> IsAllowedAsync(CancellationToken ct) => (await _settings.GetAsync(ct)).AllowRelayControl;

    /// <summary>Last commands, newest first (all plugs when <paramref name="plugId"/> is null).</summary>
    public async Task<IReadOnlyList<RelayCommand>> RecentAsync(Guid? plugId, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ILiveRepository>().ListRelayCommandsAsync(plugId, take, ct);
    }

    /// <summary>Sends <c>Switch.Set</c> after the user confirmed it; the result (ok or the error) is logged in RelayCommands.</summary>
    public async Task<RelayCommandResult> SetAsync(Guid plugId, bool on, string source, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        if (!settings.AllowRelayControl) return new RelayCommandResult(false, DisabledMessage, null);

        Plug? plug;
        using (var scope = _scopeFactory.CreateScope())
            plug = (await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct)).FirstOrDefault(p => p.Id == plugId);
        // Same rule as the UI: only a Shelly channel whose last reading reports a relay (switch:N) can be commanded.
        if (plug is null || !plug.IsPolled || plug.Protocol != PlugProtocols.ShellyRpc
            || !HasRelay(_states.Snapshot().GetValueOrDefault(plug.Id)))
            return new RelayCommandResult(false, NoRelayMessage, null);

        string result;
        try
        {
            await _shelly.SetSwitchAsync(plug.IpAddress, settings.ShellyCredentials?.Password, plug.Channel, on, ct);
            result = RelayCommand.Ok;
            _broadcaster.PublishRelay(new RelayState(plug.Id, on, _clock()));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            result = ex is ShellyAuthenticationException auth
                ? (auth.PasswordMissing ? ShellySupport.MissingPasswordReason : ShellySupport.AuthFailedReason)
                : ex.Message;
        }

        _log.LogInformation("Relay command {State} for {Alias} ({Ip}, switch:{Switch}) from {Source}: {Result}",
            on ? "ON" : "OFF", plug.Alias, plug.IpAddress, plug.Channel, source, result);
        RelayCommand logged;
        using (var scope = _scopeFactory.CreateScope())
            logged = await scope.ServiceProvider.GetRequiredService<ILiveRepository>()
                .AddRelayCommandAsync(new RelayCommand(0, _clock(), plug.Id, on, source, result), ct);
        return result == RelayCommand.Ok
            ? new RelayCommandResult(true, $"Releu {(on ? "pornit" : "oprit")}.", logged)
            : new RelayCommandResult(false, $"Comanda a eșuat: {result}", logged);
    }
}
