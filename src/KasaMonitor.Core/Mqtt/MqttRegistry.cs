namespace KasaMonitor.Core.Mqtt;

/// <summary>
/// One entity as the registry knows it now: its spec, last value (canonical unit), when it was read live (<see cref="Ts"/>;
/// null for a value known only from a retained message), available / late, attributes, and the template errors of the last
/// messages (<see cref="ReadErrors"/>, in memory only, cleared by the next good message).
/// </summary>
public sealed record MqttEntitySnapshot(MqttEntitySpec Spec, double? Number, string? Text, DateTimeOffset? Ts, bool Available, string? Attributes,
    bool Late = false, int ReadErrors = 0, string? LastReadError = null, DateTimeOffset? LastReadErrorUtc = null)
{
    public bool HasValue => Number is not null || Text is not null;
}

/// <summary>One device as discovered: identity, the <c>device</c> block, its entities (unadopted devices live only here).</summary>
public sealed record MqttDeviceSnapshot(
    string ExternalId,
    MqttDeviceInfo? Info,
    IReadOnlyList<MqttEntitySnapshot> Entities,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    bool Available,
    bool Late = false)
{
    /// <summary>The device's name: its <c>device.name</c>, else its first entity's, else its identity.</summary>
    public string Name => Info?.Name ?? Entities.FirstOrDefault()?.Spec.Name ?? ExternalId;

    public DateTimeOffset? LastValueUtc => Entities.Select(e => e.Ts).Max();
}

/// <summary>A discovery message the registry could not use (no unique_id, invalid JSON, too large): shown in the MQTT tab.</summary>
public sealed record MqttProblem(string Topic, string Reason, DateTimeOffset AtUtc);

/// <summary>A value change produced by a live state message (the recorder decides what to store).</summary>
public sealed record MqttValueUpdate(string DeviceExternalId, MqttEntitySpec Spec, MqttValue Value, DateTimeOffset TsUtc);

/// <summary>What a discovery message changed.</summary>
public sealed record MqttDiscoveryChange(IReadOnlyList<string> ChangedDevices, IReadOnlyList<(string Device, string UniqueId)> Removed, IReadOnlyList<string> NewTopics);

/// <summary>
/// The in-memory picture of the broker (0.11, pure, thread-safe): the devices and entities discovered (retained
/// configurations come back at every connection: the upsert is idempotent), the index topic → entities, the last state of
/// every availability topic and the last value of every entity. No I/O: the worker feeds messages, the recorder stores.
/// Round 2: a retained state is not a new reading (R3), an unknown availability payload is ignored (R12), the number of
/// devices is bounded (R7) and a component that fails to map is a listed problem, never an exception (R6).
/// </summary>
public sealed class MqttRegistry
{
    private sealed class LiveEntity
    {
        public required MqttEntitySpec Spec;
        public double? Number;
        public string? Text;
        public DateTimeOffset? Ts;
        public string? Attributes;
        public readonly List<DateTimeOffset> Arrivals = new();
        public int ReadErrors;
        public string? LastReadError;
        public DateTimeOffset? LastReadErrorUtc;

        public TimeSpan? Cadence => MqttAvailabilityRule.Cadence(Arrivals);
    }

    private sealed class LiveDevice
    {
        public required string ExternalId;
        public MqttDeviceInfo? Info;
        public readonly Dictionary<string, LiveEntity> Entities = new(StringComparer.Ordinal);
        public DateTimeOffset FirstSeen;
        public DateTimeOffset LastSeen;
    }

    private const int MaxProblems = 50;
    /// <summary>R7: the most devices kept from discovery (a hostile or broken publisher cannot fill the memory).</summary>
    public const int MaxDevices = 500;
    /// <summary>The longest device identity accepted (stored in an indexed column).</summary>
    public const int MaxExternalId = 200;
    private const int ArrivalsKept = 6;
    private readonly object _sync = new();
    private readonly Dictionary<string, LiveDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool? State, DateTimeOffset Ts)> _availability = new(StringComparer.Ordinal);
    private readonly List<MqttProblem> _problems = new();

    /// <summary>Applies one message under the discovery prefix; returns what changed (devices, removed entities, topics to subscribe).</summary>
    public MqttDiscoveryChange ApplyDiscovery(MqttDiscoveryResult r, DateTimeOffset now)
    {
        lock (_sync)
        {
            var topicsBefore = TopicsCore();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var removed = new List<(string, string)>();
            switch (r.Kind)
            {
                case MqttDiscoveryKind.Removed:
                    foreach (var d in _devices.Values)
                        foreach (var e in d.Entities.Values.Where(e => e.Spec.ConfigTopic == r.Topic).ToList())
                        {
                            d.Entities.Remove(e.Spec.UniqueId);
                            removed.Add((d.ExternalId, e.Spec.UniqueId));
                            changed.Add(d.ExternalId);
                        }
                    foreach (var empty in _devices.Values.Where(d => d.Entities.Count == 0).Select(d => d.ExternalId).ToList()) _devices.Remove(empty);
                    _problems.RemoveAll(p => p.Topic == r.Topic);
                    break;
                case MqttDiscoveryKind.Invalid:
                    Problem(new MqttProblem(r.Topic, r.Error ?? "payload invalid", now));
                    break;
                case MqttDiscoveryKind.Config:
                    _problems.RemoveAll(p => p.Topic == r.Topic);
                    // Map every component first: a component that fails is a problem and changes nothing (no half-applied message).
                    var mapped = new List<(MqttDiscoveredComponent C, MqttEntitySpec? Spec)>();
                    foreach (var c in r.Components)
                    {
                        try { mapped.Add((c, MqttEntityMapper.Map(c))); }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            Problem(new MqttProblem(r.Topic, $"{c.Component}/{c.ObjectId}: descriere neacceptată ({ex.GetType().Name})", now));
                        }
                    }
                    // A device-based message replaces its whole component list: entities of that topic no longer listed are removed.
                    var listed = mapped.Where(x => x.Spec is not null).Select(x => x.Spec!.UniqueId).ToHashSet(StringComparer.Ordinal);
                    foreach (var d in _devices.Values)
                        foreach (var e in d.Entities.Values.Where(e => e.Spec.ConfigTopic == r.Topic && !listed.Contains(e.Spec.UniqueId)).ToList())
                        {
                            d.Entities.Remove(e.Spec.UniqueId);
                            removed.Add((d.ExternalId, e.Spec.UniqueId));
                            changed.Add(d.ExternalId);
                        }
                    foreach (var (c, spec) in mapped)
                    {
                        if (spec is null || c.DeviceExternalId is not { } extId)
                        {
                            Problem(new MqttProblem(r.Topic, $"{c.Component}/{c.ObjectId}: fără unique_id (sau mai lung de {MqttEntityMapper.MaxUniqueId}), nu poate fi adăugat", now));
                            continue;
                        }
                        if (extId.Length > MaxExternalId)
                        {
                            Problem(new MqttProblem(r.Topic, $"{c.Component}/{c.ObjectId}: identificatorul dispozitivului e mai lung de {MaxExternalId}", now));
                            continue;
                        }
                        // The same unique id seen under another device (the publisher moved it): it moves.
                        foreach (var other in _devices.Values.Where(d => d.ExternalId != extId && d.Entities.ContainsKey(spec.UniqueId)))
                        {
                            other.Entities.Remove(spec.UniqueId);
                            changed.Add(other.ExternalId);
                        }
                        if (!_devices.TryGetValue(extId, out var dev))
                        {
                            if (_devices.Count >= MaxDevices)
                            {
                                Problem(new MqttProblem(r.Topic, $"prea multe dispozitive descoperite (peste {MaxDevices}): ignorat", now));
                                continue;
                            }
                            _devices[extId] = dev = new LiveDevice { ExternalId = extId, FirstSeen = now };
                        }
                        dev.Info = c.Device ?? dev.Info;
                        dev.LastSeen = now;
                        if (dev.Entities.TryGetValue(spec.UniqueId, out var existing))
                        {
                            if (existing.Spec.DiscoveryJson != spec.DiscoveryJson) changed.Add(extId);
                            existing.Spec = spec;
                        }
                        else
                        {
                            dev.Entities[spec.UniqueId] = new LiveEntity { Spec = spec };
                            changed.Add(extId);
                        }
                    }
                    foreach (var empty in _devices.Values.Where(d => d.Entities.Count == 0).Select(d => d.ExternalId).ToList()) _devices.Remove(empty);
                    break;
            }
            var newTopics = TopicsCore().Except(topicsBefore, StringComparer.Ordinal).ToList();
            return new MqttDiscoveryChange(changed.ToList(), removed, newTopics);
        }
    }

    /// <summary>
    /// Applies a state / availability / attributes message; returns the value updates it produced. R3 (round 2): a
    /// <paramref name="retained"/> state is what the broker kept, not a reading made now: its value is remembered (shown
    /// without a time), its time stays the last live one (or none) and no update is produced, so nothing is stored.
    /// Availability messages count retained or not (a retained LWT „offline” is the device's state).
    /// </summary>
    public IReadOnlyList<MqttValueUpdate> ApplyMessage(string topic, string payload, DateTimeOffset now, bool retained = false)
    {
        var updates = new List<MqttValueUpdate>();
        lock (_sync)
        {
            foreach (var d in _devices.Values)
            {
                foreach (var e in d.Entities.Values)
                {
                    foreach (var a in e.Spec.Availability.Where(a => a.Topic == topic))
                    {
                        // R12: a payload that is neither „available” nor „not available” is ignored, as in Home Assistant.
                        if (MqttValueParser.Availability(a, payload) is { } state) _availability[topic] = (state, now);
                    }
                    if (e.Spec.JsonAttributesTopic == topic) e.Attributes = payload.Length > 4000 ? payload[..4000] : payload;
                    if (e.Spec.StateTopic != topic) continue;
                    var v = MqttValueParser.Parse(e.Spec, payload);
                    if (v.Error is { } error)
                    {
                        e.ReadErrors++;
                        e.LastReadError = error.Length > 200 ? error[..200] : error;
                        e.LastReadErrorUtc = now;
                        continue;
                    }
                    if (!v.HasValue) continue;
                    e.ReadErrors = 0;
                    e.LastReadError = null;
                    e.LastReadErrorUtc = null;
                    if (retained)
                    {
                        if (e.Number != v.Number || e.Text != v.Text)
                        {
                            e.Number = v.Number;
                            e.Text = v.Text;
                        }
                        continue;
                    }
                    e.Number = v.Number;
                    e.Text = v.Text;
                    e.Ts = now;
                    e.Arrivals.Add(now);
                    if (e.Arrivals.Count > ArrivalsKept) e.Arrivals.RemoveAt(0);
                    d.LastSeen = now;
                    updates.Add(new MqttValueUpdate(d.ExternalId, e.Spec, v, now));
                }
            }
        }
        return updates;
    }

    /// <summary>Every state, availability and attributes topic of the discovered entities (subscribed after each connection).</summary>
    public IReadOnlyCollection<string> Topics()
    {
        lock (_sync) return TopicsCore();
    }

    private HashSet<string> TopicsCore()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _devices.Values.SelectMany(d => d.Entities.Values))
        {
            if (e.Spec.StateTopic is { } s && e.Spec.Problem is null && !MqttEntityMapper.HasWildcard(s)) set.Add(s);
            if (e.Spec.JsonAttributesTopic is { } j) set.Add(j);
            foreach (var a in e.Spec.Availability) set.Add(a.Topic);
        }
        return set;
    }

    public IReadOnlyList<MqttDeviceSnapshot> Devices(DateTimeOffset now)
    {
        lock (_sync) return _devices.Values.Select(d => Snapshot(d, now)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public MqttDeviceSnapshot? Get(string externalId, DateTimeOffset now)
    {
        lock (_sync) return _devices.TryGetValue(externalId, out var d) ? Snapshot(d, now) : null;
    }

    public IReadOnlyList<MqttProblem> Problems
    {
        get { lock (_sync) return _problems.ToList(); }
    }

    /// <summary>A problem seen by the worker (a message over the size limit …), listed with the discovery problems.</summary>
    public void AddProblem(string topic, string reason, DateTimeOffset now)
    {
        lock (_sync) Problem(new MqttProblem(topic, reason, now));
    }

    private MqttDeviceSnapshot Snapshot(LiveDevice d, DateTimeOffset now)
    {
        var entities = d.Entities.Values.OrderBy(e => e.Spec.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new MqttEntitySnapshot(e.Spec, e.Number, e.Text, e.Ts,
                MqttAvailabilityRule.IsAvailable(e.Spec, _availability, e.Ts, now, e.Cadence), e.Attributes,
                MqttAvailabilityRule.IsLate(e.Spec, e.Ts, now, e.Cadence), e.ReadErrors, e.LastReadError, e.LastReadErrorUtc))
            .ToList();
        var readable = entities.Where(e => e.Spec.Problem is null).ToList();
        var available = readable.Any(e => e.Available);
        // „Întârziat”: answers, but every available value whose age decides is late (none fresh, none confirmed by availability).
        var late = available && readable.Where(e => e.Available).All(e => e.Late);
        return new MqttDeviceSnapshot(d.ExternalId, d.Info, entities, d.FirstSeen, d.LastSeen, available, late);
    }

    private void Problem(MqttProblem p)
    {
        _problems.RemoveAll(x => x.Topic == p.Topic && x.Reason == p.Reason);
        _problems.Add(p);
        if (_problems.Count > MaxProblems) _problems.RemoveAt(0);
    }
}
