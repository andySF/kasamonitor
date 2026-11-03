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
    string? RetireWarning = null,
    IReadOnlyList<string>? Capabilities = null)
{
    public const string PlugPrefix = "plug:";
    public const string MielePrefix = "miele:";
    public const string HeatingPrefix = "heating:";
    public const string ClimatePrefix = "climate:";
    /// <summary>A generic device of a self-describing integration (0.11, MQTT Discovery): <c>dev:{GenericDevices.Id}</c>.</summary>
    public const string GenericPrefix = "dev:";

    public static string PlugKey(Guid id) => PlugPrefix + id;
    public static string MieleKey(string id) => MielePrefix + id;
    public static string HeatingKey(Guid id) => HeatingPrefix + id;
    public static string ClimateKey(Guid id) => ClimatePrefix + id;
    public static string GenericKey(long id) => GenericPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// R1 (0.11): who owns a row, from the prefix of its key — the single dispatch of the expanded row's editor, of „Retrage”
    /// and of the generated page. Never from the category (a generic device classed „Contor” is not a plug).
    /// </summary>
    public static DeviceRowOwner OwnerOf(string? key) => key switch
    {
        null => DeviceRowOwner.Unknown,
        _ when key.StartsWith(PlugPrefix, StringComparison.Ordinal) => DeviceRowOwner.Plug,
        _ when key.StartsWith(MielePrefix, StringComparison.Ordinal) => DeviceRowOwner.Miele,
        _ when key.StartsWith(HeatingPrefix, StringComparison.Ordinal) => DeviceRowOwner.Heating,
        _ when key.StartsWith(ClimatePrefix, StringComparison.Ordinal) => DeviceRowOwner.Climate,
        _ when key.StartsWith(GenericPrefix, StringComparison.Ordinal) => DeviceRowOwner.Generic,
        _ => DeviceRowOwner.Unknown,
    };

    /// <summary>The id after the prefix as a Guid (plug, boiler, climate sensor); false for any other key or a malformed id.</summary>
    public static bool TryGuid(string? key, string prefix, out Guid id)
    {
        id = Guid.Empty;
        return key is not null && key.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(key.AsSpan(prefix.Length), out id);
    }

    /// <summary>The generic device id after the prefix (a positive integer); false otherwise.</summary>
    public static bool TryGeneric(string? key, out long id)
    {
        id = 0;
        return key is not null && key.StartsWith(GenericPrefix, StringComparison.Ordinal)
               && long.TryParse(key.AsSpan(GenericPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>The Miele device id after the prefix (digits only, as the API gives it); false otherwise.</summary>
    public static bool TryMiele(string? key, out string id)
    {
        id = key is not null && key.StartsWith(MielePrefix, StringComparison.Ordinal) ? key[MielePrefix.Length..] : "";
        return id.Length > 0 && id.All(char.IsAsciiLetterOrDigit);
    }
}

/// <summary>The owner of a /dispozitive row, from its key prefix (<see cref="DeviceRow.OwnerOf"/>).</summary>
public enum DeviceRowOwner
{
    Unknown,
    Plug,
    Miele,
    Heating,
    Climate,
    Generic,
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

/// <summary>A generic device for the catalog: the stored device, its classification, whether it answers, its last value.</summary>
public sealed record GenericCatalogItem(GenericDevice Device, KasaMonitor.Core.Devices.Generic.GenericClassification Classification, bool Available,
    DateTimeOffset? LastReadUtc, bool Late = false);

/// <summary>The projection of the existing tables and in-memory states into <see cref="DeviceRow"/> (pure; tested).</summary>
public static class DeviceCatalogRules
{
    public static IReadOnlyList<DeviceRow> Build(
        IReadOnlyList<Plug> plugs, IReadOnlyDictionary<Guid, PlugLiveState> live,
        IReadOnlyList<MieleDevice> miele, MieleState? mieleState,
        IReadOnlyList<HeatingDevice> heating, HeatingState? heatingState,
        IReadOnlyList<ClimateSensor> sensors, ClimateState? climate,
        DateTimeOffset now, IReadOnlyList<GenericCatalogItem>? generic = null)
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
                PlugDataUrl(p),
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

        // One row per physical device (0.11): the climate sensor of a generic device is a badge of its row, not a row.
        var ownedClimate = (generic ?? []).Select(g => g.Device.ExternalId).ToHashSet(StringComparer.Ordinal);
        foreach (var s in sensors)
        {
            if (s.Source == ClimateSources.Mqtt && ownedClimate.Contains(s.ExternalId)) continue;
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

        foreach (var g in generic ?? [])
        {
            var d = g.Device;
            var health = d.IsArchived ? DeviceHealth.Retired : !g.Available ? DeviceHealth.Offline : g.Late ? DeviceHealth.Late : DeviceHealth.Online;
            var model = string.Join(" · ", new[] { d.Manufacturer, d.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var key = DeviceRow.GenericKey(d.Id);
            rows.Add(new DeviceRow(key, d.Integration, d.Category, d.Name, d.NameIsCustom, d.Room.Length > 0 ? d.Room : null,
                model.Length > 0 ? model : g.Classification.Label, null, health, g.LastReadUtc ?? d.LastSeenUtc, d.IsArchived,
                d.CategoryOverride is null && d.Category == DeviceCategory.Generic ? g.Classification.Label : null,
                "/dispozitive/" + Uri.EscapeDataString(key), null, null, g.Classification.Capabilities));
        }

        return rows
            .OrderBy(r => r.Health == DeviceHealth.Retired)
            .ThenBy(r => r.Category)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The page with a plug's data: /contor for the main meter, its electricity history otherwise.</summary>
    public static string PlugDataUrl(Plug p) => p.Role == PlugRole.MainMeter ? "/contor" : $"/history?plug={p.Id}#curent";

    /// <summary>The room, with „afară” for an outdoor sensor unless the room already says it.</summary>
    public static string? Room(ClimateSensor s)
    {
        if (!s.IsOutdoor) return s.Room.Length > 0 ? s.Room : null;
        if (s.Room.Length == 0 || string.Equals(s.Room, "afară", StringComparison.CurrentCultureIgnoreCase)) return "Afară";
        return s.Room + " · afară";
    }

    public static string Model(Plug p)
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
    private readonly Mqtt.GenericRecorder? _generic;
    private readonly Mqtt.MqttState? _mqtt;

    public DeviceCatalog(IServiceScopeFactory scopes, IPlugStateBroadcaster plugs, ClimateState? climate = null, HeatingState? heating = null,
        MieleState? miele = null, ClimateRecorder? climateRecorder = null, MieleRecorder? mieleRecorder = null,
        IClimateSettingsService? climateSettings = null, Action? wakeHeating = null, Action? wakeClimate = null,
        Mqtt.GenericRecorder? generic = null, Mqtt.MqttState? mqtt = null)
    {
        _generic = generic;
        _mqtt = mqtt;
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
        return DeviceCatalogRules.Build(plugs, _plugs.Snapshot(), miele, _miele, heating, _heating, sensors, _climate, now, await GenericItemsAsync(now, ct));
    }

    /// <summary>The generic devices with their classification and whether they answer (the MQTT registry; offline while it does not know them).</summary>
    private async Task<IReadOnlyList<GenericCatalogItem>> GenericItemsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_generic is null) return [];
        await _generic.LoadAsync(ct);
        return _generic.Devices.Select(d =>
        {
            var snap = _mqtt?.Registry.Get(d.ExternalId, now);
            return new GenericCatalogItem(d, _generic.Classification(d.Id), snap?.Available ?? false, snap?.LastValueUtc, snap?.Late ?? false);
        }).ToList();
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
        // R2 (0.11): one dispatch on the key's prefix (DeviceRow.OwnerOf), each branch validating its id.
        switch (DeviceRow.OwnerOf(key))
        {
            case DeviceRowOwner.Plug when DeviceRow.TryGuid(key, DeviceRow.PlugPrefix, out var plugId):
                await sp.GetRequiredService<IPlugRepository>().SetArchivedAsync(plugId, retired, ct);
                return retired ? "Priza nu mai e interogată și nu mai apare în /metrics." : "Priza e interogată din nou.";
            case DeviceRowOwner.Miele when DeviceRow.TryMiele(key, out var id):
            {
                await sp.GetRequiredService<IMieleRepository>().SetArchivedAsync(id, retired, ct);
                if (_mieleRecorder is not null) await _mieleRecorder.ReloadDeviceAsync(id, ct);
                return retired ? "Aparatul e ascuns în Aparate și Acasă (istoricul rămâne)." : "Aparatul apare din nou.";
            }
            case DeviceRowOwner.Heating when DeviceRow.TryGuid(key, DeviceRow.HeatingPrefix, out var heatingId):
            {
                var repo = sp.GetRequiredService<IHeatingRepository>();
                if (!await repo.SetDeviceEnabledAsync(heatingId, !retired, ct)) return "Centrala nu mai există.";
                if ((await repo.ListDevicesAsync(ct)).FirstOrDefault(d => d.Id == heatingId) is { } device) _heating?.UpdateDevice(device);
                _wakeHeating?.Invoke();
                return retired ? "Centrala nu mai e citită (fără apeluri la Viessmann)." : "Centrala e citită din nou.";
            }
            case DeviceRowOwner.Climate when DeviceRow.TryGuid(key, DeviceRow.ClimatePrefix, out var sensorId):
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
            case DeviceRowOwner.Generic when DeviceRow.TryGeneric(key, out var genericId) && _generic is not null:
            {
                // R2: the device and its routed climate sensor, through one owner.
                if (await _generic.SetArchivedAsync(genericId, retired, ct) is null) return "Dispozitivul nu mai există.";
                return retired ? "Dispozitivul nu mai e salvat și nu mai apare în /metrics și Climă (istoricul rămâne)." : "Dispozitivul e citit din nou.";
            }
        }
        return "Dispozitiv necunoscut.";
    }
}
