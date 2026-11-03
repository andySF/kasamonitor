using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers;

/// <summary>
/// The state of a device in the aggregated list of /dispozitive. Each integration keeps its own „online” rule (they feed
/// the <c>*_online</c> metrics and the Grafana alerts); this is only their common projection.
/// </summary>
public enum DeviceHealth
{
    Online,
    /// <summary>„Întârziat”: answers, but the last value is older than its source usually delivers (climate: Late; boiler: stale).</summary>
    Late,
    Offline,
    /// <summary>Found but cannot be read (e.g. a Tapo plug without the TP-Link account).</summary>
    Unsupported,
    /// <summary>„Retras”: withdrawn by the user (archived plug / sensor / Miele appliance, disabled boiler).</summary>
    Retired,
}

public static class DeviceHealths
{
    public static readonly IReadOnlyList<DeviceHealth> All = Enum.GetValues<DeviceHealth>();

    public static string Name(DeviceHealth h) => h switch
    {
        DeviceHealth.Online => "Online",
        DeviceHealth.Late => "Întârziat",
        DeviceHealth.Offline => "Offline",
        DeviceHealth.Unsupported => "Nesuportat",
        DeviceHealth.Retired => "Retras",
        _ => h.ToString(),
    };

    /// <summary>The value in the address (<c>?stare=</c>).</summary>
    public static string Slug(DeviceHealth h) => h switch
    {
        DeviceHealth.Late => "intarziat",
        DeviceHealth.Unsupported => "nesuportat",
        DeviceHealth.Retired => "retras",
        _ => h.ToString().ToLowerInvariant(),
    };

    public static DeviceHealth? FromSlug(string? slug) =>
        All.Cast<DeviceHealth?>().FirstOrDefault(h => string.Equals(Slug(h!.Value), slug, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One row of /dispozitive. <see cref="Key"/> is „plug:{id}”, „miele:{id}”, „heating:{id}” or „climate:{id}”; the
/// writes (name, room, role, links, withdraw) stay with the owner of each kind. <see cref="RetireWarning"/>: what stops
/// when it is withdrawn (main meter, boiler), asked before „Retrage”.
/// </summary>
public sealed record DeviceRow(
    string Key,
    string Integration,
    DeviceCategory Category,
    string Name,
    bool NameIsCustom,
    string? Room,
    string Model,
    string? Address,
    DeviceHealth Health,
    DateTimeOffset? LastReadUtc,
    bool IsArchived,
    string? LinkedTo,
    string DataUrl,
    string? Note = null,
    string? RetireWarning = null)
{
    public const string PlugPrefix = "plug:";
    public const string MielePrefix = "miele:";
    public const string HeatingPrefix = "heating:";
    public const string ClimatePrefix = "climate:";

    public static string PlugKey(Guid id) => PlugPrefix + id;
    public static string MieleKey(string id) => MielePrefix + id;
    public static string HeatingKey(Guid id) => HeatingPrefix + id;
    public static string ClimateKey(Guid id) => ClimatePrefix + id;
}

/// <summary>The filters of /dispozitive (category, vendor / integration, state, „arată retrase”).</summary>
public sealed record DeviceFilter(DeviceCategory? Category = null, string? Integration = null, DeviceHealth? Health = null, bool ShowRetired = false)
{
    /// <summary>Retired rows only with „arată retrase” (or when the state filter asks for them).</summary>
    public IReadOnlyList<DeviceRow> Apply(IEnumerable<DeviceRow> rows) => rows
        .Where(r => ShowRetired || Health == DeviceHealth.Retired || r.Health != DeviceHealth.Retired)
        .Where(r => Category is null || r.Category == Category)
        .Where(r => Integration is null || string.Equals(r.Integration, Integration, StringComparison.OrdinalIgnoreCase))
        .Where(r => Health is null || r.Health == Health)
        .ToList();
}

/// <summary>The projection of the existing tables and in-memory states into <see cref="DeviceRow"/> (pure; tested).</summary>
public static class DeviceCatalogRules
{
    public static IReadOnlyList<DeviceRow> Build(
        IReadOnlyList<Plug> plugs, IReadOnlyDictionary<Guid, PlugLiveState> live,
        IReadOnlyList<MieleDevice> miele, MieleState? mieleState,
        IReadOnlyList<HeatingDevice> heating, HeatingState? heatingState,
        IReadOnlyList<ClimateSensor> sensors, ClimateState? climate,
        DateTimeOffset now)
    {
        var rows = new List<DeviceRow>();
        var plugNames = plugs.ToDictionary(p => p.Id, p => p.Alias);
        var mieleOnPlug = miele.Where(m => m is { PlugId: not null, IsArchived: false }).GroupBy(m => m.PlugId!.Value)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(m => m.Alias)));

        foreach (var p in plugs)
        {
            live.TryGetValue(p.Id, out var st);
            var health = p.IsArchived ? DeviceHealth.Retired
                : !p.IsSupported ? DeviceHealth.Unsupported
                : st is { IsOnline: true } ? DeviceHealth.Online : DeviceHealth.Offline;
            var linked = new List<string>();
            if (p.Appliance is { } a) linked.Add("aparat: " + a);
            if (mieleOnPlug.TryGetValue(p.Id, out var m)) linked.Add("alimentează " + m);
            var meter = p.Role != PlugRole.Plug;
            rows.Add(new DeviceRow(DeviceRow.PlugKey(p.Id), PlugProtocols.Integration(p.Protocol),
                meter ? DeviceCategory.Meter : DeviceCategory.Plug, p.Alias, p.AliasIsCustom, null,
                Model(p), p.IpAddress.ToString(), health,
                // R3: the last real reading (live state), not Plugs.LastSeenUtc (discovery only). A failed poll publishes
                // its time without a reading: that is no reading.
                st is { LastReading: not null } ? st.LastSampleUtc : null,
                p.IsArchived, linked.Count == 0 ? null : string.Join(" · ", linked),
                p.Role == PlugRole.MainMeter ? "/contor" : $"/history?plug={p.Id}#curent",
                p.IsSupported ? null : p.UnsupportedReason,
                p.Role == PlugRole.MainMeter
                    ? "E contorul general: pagina Contor, „Acum în casă” din Acasă și detecția consumatorilor rămân fără date."
                    : null));
        }

        foreach (var d in miele)
        {
            var st = mieleState?.Get(d.Id);
            var health = d.IsArchived ? DeviceHealth.Retired
                : st is not null && mieleState!.IsOnline(st, now) ? DeviceHealth.Online : DeviceHealth.Offline;
            var model = d.TechType.Length > 0 ? $"{d.TypeText} · {d.TechType}" : d.TypeText;
            rows.Add(new DeviceRow(DeviceRow.MieleKey(d.Id), IntegrationKeys.Miele, DeviceCategory.Appliance, d.Alias,
                !string.Equals(d.Alias, d.DeviceName, StringComparison.Ordinal) && d.DeviceName.Length > 0, null, model, null, health,
                st?.StateUtc ?? d.LastSeenUtc, d.IsArchived,
                d.PlugId is { } plug ? "priza " + (plugNames.TryGetValue(plug, out var n) ? n : "(ștearsă)") : null, "/aparate"));
        }

        foreach (var h in heating)
        {
            var current = heatingState?.Current is { } c && c.Device.Id == h.Id ? c : null;
            var health = !h.Enabled ? DeviceHealth.Retired
                : current is null || !heatingState!.IsOnline(now) ? DeviceHealth.Offline
                : heatingState.IsStale(now) ? DeviceHealth.Late : DeviceHealth.Online;
            rows.Add(new DeviceRow(DeviceRow.HeatingKey(h.Id), IntegrationKeys.Viessmann, DeviceCategory.Boiler, h.Name, false, null,
                h.ModelId.Length > 0 ? h.ModelId : "ViCare", null, health, current?.Snapshot?.ReadUtc ?? current?.LastSuccessUtc ?? h.LastSeenUtc,
                !h.Enabled, null, "/centrala", h.LastError,
                "Centrala nu mai e citită: fără gaz, temperaturi și cronologie noi (contul ViCare rămâne conectat)."));
        }

        foreach (var s in sensors)
        {
            var st = climate?.Get(s.Id);
            DeviceHealth health;
            if (s.IsArchived) health = DeviceHealth.Retired;
            else if (st is null || climate is null || !climate.IsOnline(st, now)) health = DeviceHealth.Offline;
            else health = climate.Freshness(st, now) == ClimateFreshnessLevel.Late ? DeviceHealth.Late : DeviceHealth.Online;
            var weather = s.Source == ClimateSources.OpenMeteo;
            rows.Add(new DeviceRow(DeviceRow.ClimateKey(s.Id), s.Source, weather ? DeviceCategory.Weather : DeviceCategory.ClimateSensor,
                s.Name, false, Room(s),
                ClimateSources.DisplayName(s.Source), s.Source == ClimateSources.Shelly ? s.ExternalId : null, health,
                st?.ReadUtc ?? s.LastSeenUtc, s.IsArchived, null, "/clima",
                s.BatteryPercent is { } b ? $"baterie {b} %" : null));
        }

        return rows
            .OrderBy(r => r.Health == DeviceHealth.Retired)
            .ThenBy(r => r.Category)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The room, with „afară” for an outdoor sensor unless the room already says it.</summary>
    private static string? Room(ClimateSensor s)
    {
        if (!s.IsOutdoor) return s.Room.Length > 0 ? s.Room : null;
        if (s.Room.Length == 0 || string.Equals(s.Room, "afară", StringComparison.CurrentCultureIgnoreCase)) return "Afară";
        return s.Room + " · afară";
    }

    private static string Model(Plug p)
    {
        var model = p.Protocol == PlugProtocols.ShellyRpc
            ? KasaMonitor.Core.Protocol.Shelly.ShellySupport.FriendlyModel(p.Model, p.HwVer)
            : PlugModelName.Short(p.Model);
        if (p.Protocol == PlugProtocols.ShellyRpc && p.Channel > 0) model += $" · ch{p.Channel}";
        return model.Length == 0 ? p.Protocol : model;
    }
}

/// <summary>
/// The aggregated device list of /dispozitive (no table of its own: a projection of Plugs, MieleDevices, HeatingDevices
/// and ClimateSensors with the in-memory states) and the uniform „Retrage / Readu”, which calls each kind's owner.
/// Cheap: the states are in memory, the database is read for the four lists only.
/// </summary>
public sealed class DeviceCatalog
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IPlugStateBroadcaster _plugs;
    private readonly ClimateState? _climate;
    private readonly HeatingState? _heating;
    private readonly MieleState? _miele;
    private readonly ClimateRecorder? _climateRecorder;
    private readonly MieleRecorder? _mieleRecorder;
    private readonly IClimateSettingsService? _climateSettings;
    private readonly Action? _wakeHeating;
    private readonly Action? _wakeClimate;

    public DeviceCatalog(IServiceScopeFactory scopes, IPlugStateBroadcaster plugs, ClimateState? climate = null, HeatingState? heating = null,
        MieleState? miele = null, ClimateRecorder? climateRecorder = null, MieleRecorder? mieleRecorder = null,
        IClimateSettingsService? climateSettings = null, Action? wakeHeating = null, Action? wakeClimate = null)
    {
        _scopes = scopes;
        _plugs = plugs;
        _climate = climate;
        _heating = heating;
        _miele = miele;
        _climateRecorder = climateRecorder;
        _mieleRecorder = mieleRecorder;
        _climateSettings = climateSettings;
        _wakeHeating = wakeHeating;
        _wakeClimate = wakeClimate;
    }

    public async Task<IReadOnlyList<DeviceRow>> ListAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var plugs = await sp.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        var miele = await sp.GetRequiredService<IMieleRepository>().ListDevicesAsync(includeArchived: true, ct);
        var heating = await sp.GetRequiredService<IHeatingRepository>().ListDevicesAsync(ct);
        var sensors = await sp.GetRequiredService<IClimateRepository>().ListSensorsAsync(includeArchived: true, ct);
        return DeviceCatalogRules.Build(plugs, _plugs.Snapshot(), miele, _miele, heating, _heating, sensors, _climate, now);
    }

    /// <summary>
    /// R5: withdraws (<paramref name="retired"/> true) or brings back a device through its owner: plug → <c>Plugs.IsArchived</c>,
    /// Miele → <c>MieleDevices.IsArchived</c>, boiler → <c>HeatingDevices.Enabled</c>, climate sensor →
    /// <c>ClimateSensors.IsArchived</c> (a Govee thermometer through its „acasă” tick, which the climate worker follows).
    /// Returns a Romanian message.
    /// </summary>
    public async Task<string> SetRetiredAsync(string key, bool retired, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        if (key.StartsWith(DeviceRow.PlugPrefix, StringComparison.Ordinal) && Guid.TryParse(key[DeviceRow.PlugPrefix.Length..], out var plugId))
        {
            await sp.GetRequiredService<IPlugRepository>().SetArchivedAsync(plugId, retired, ct);
            return retired ? "Priza nu mai e interogată și nu mai apare în /metrics." : "Priza e interogată din nou.";
        }
        if (key.StartsWith(DeviceRow.MielePrefix, StringComparison.Ordinal))
        {
            var id = key[DeviceRow.MielePrefix.Length..];
            await sp.GetRequiredService<IMieleRepository>().SetArchivedAsync(id, retired, ct);
            if (_mieleRecorder is not null) await _mieleRecorder.ReloadDeviceAsync(id, ct);
            return retired ? "Aparatul e ascuns în Aparate și Acasă (istoricul rămâne)." : "Aparatul apare din nou.";
        }
        if (key.StartsWith(DeviceRow.HeatingPrefix, StringComparison.Ordinal) && Guid.TryParse(key[DeviceRow.HeatingPrefix.Length..], out var heatingId))
        {
            var repo = sp.GetRequiredService<IHeatingRepository>();
            if (!await repo.SetDeviceEnabledAsync(heatingId, !retired, ct)) return "Centrala nu mai există.";
            if ((await repo.ListDevicesAsync(ct)).FirstOrDefault(d => d.Id == heatingId) is { } device) _heating?.UpdateDevice(device);
            _wakeHeating?.Invoke();
            return retired ? "Centrala nu mai e citită (fără apeluri la Viessmann)." : "Centrala e citită din nou.";
        }
        if (key.StartsWith(DeviceRow.ClimatePrefix, StringComparison.Ordinal) && Guid.TryParse(key[DeviceRow.ClimatePrefix.Length..], out var sensorId))
        {
            var sensor = (await sp.GetRequiredService<IClimateRepository>().ListSensorsAsync(includeArchived: true, ct)).FirstOrDefault(s => s.Id == sensorId);
            if (sensor is null) return "Senzorul nu mai există.";
            if (sensor.Source == ClimateSources.Govee && _climateSettings is not null)
                await _climateSettings.SetGoveeHomeAsync(sensor.ExternalId, !retired, ct);
            if (_climateRecorder is not null) await _climateRecorder.SetArchivedAsync(sensorId, retired, ct);
            else await sp.GetRequiredService<IClimateRepository>().SetArchivedAsync(sensorId, retired, ct);
            _wakeClimate?.Invoke();
            return retired ? "Senzorul e ascuns în Climă și nu se mai salvează (istoricul rămâne)." : "Senzorul apare din nou în Climă.";
        }
        return "Dispozitiv necunoscut.";
    }
}
