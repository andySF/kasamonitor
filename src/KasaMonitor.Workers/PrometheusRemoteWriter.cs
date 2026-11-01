using System.Net;
using System.Net.Http.Headers;
using System.Text;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Prometheus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snappier;

namespace KasaMonitor.Workers;

/// <summary>State of the remote write shown in /settings and exported on /metrics.</summary>
public sealed record RemoteWriteStatus(bool Enabled, string Url, DateTimeOffset? LastSuccessUtc, long SentSamples, long DroppedSamples,
    long Failures, int Buffered, string? LastError);

/// <summary>
/// Prometheus remote write 1.0 of the live stream: every sample that passed the change filter (the same ones stored as
/// raw rows, <see cref="ILiveStreamBroadcaster.SampleStored"/>) becomes <c>kasa_live_power_w</c> and, when reported,
/// <c>kasa_live_voltage_v</c>, <c>kasa_live_current_a</c>, <c>kasa_live_power_factor</c>, with the sample's own
/// timestamp (ms) and the labels of /metrics (<see cref="PlugMetricLabels"/>). Samples wait in an in-memory queue,
/// sent every second or at 500 samples (snappy-compressed protobuf, <c>POST</c>, optional basic auth). A failed send is
/// retried with backoff 1 → 60 s; HTTP 4xx other than 429 is not retryable (the batch is dropped). Samples older than
/// <see cref="MaxBufferAge"/> are dropped and counted. Enqueueing never blocks the stream.
/// </summary>
public sealed class PrometheusRemoteWriter : BackgroundService
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBufferAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LabelRefresh = TimeSpan.FromSeconds(30);
    public const int FlushAtSamples = 500;
    public const int MaxBatchSamples = 5000;
    /// <summary>Hard cap of the queue (memory), whatever the age.</summary>
    public const int MaxBuffered = 200_000;

    public const string PowerMetric = "kasa_live_power_w";
    public const string VoltageMetric = "kasa_live_voltage_v";
    public const string CurrentMetric = "kasa_live_current_a";
    public const string PowerFactorMetric = "kasa_live_power_factor";

    private readonly record struct Item(LiveSample Sample, DateTimeOffset EnqueuedUtc);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKasaSettingsService _settings;
    private readonly ILiveStreamBroadcaster _broadcaster;
    private readonly ILogger<PrometheusRemoteWriter> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HttpClient _http;
    private readonly List<Item> _queue = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    private volatile bool _enabled;
    private string _url = "";
    private RemoteWriteCredentials? _creds;
    private Dictionary<Guid, string[]> _labels = new();
    private DateTimeOffset _labelsReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private TimeSpan _backoff = TimeSpan.Zero;
    private DateTimeOffset? _lastSuccess;
    private long _sent, _dropped, _failures;
    private string? _lastError;

    /// <param name="handler">Test hook (fake endpoint); production uses a default handler.</param>
    public PrometheusRemoteWriter(IServiceScopeFactory scopeFactory, IKasaSettingsService settings, ILiveStreamBroadcaster broadcaster,
        ILogger<PrometheusRemoteWriter> log, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _broadcaster = broadcaster;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _http = handler is null ? new HttpClient { Timeout = RequestTimeout } : new HttpClient(handler) { Timeout = RequestTimeout };
        _broadcaster.SampleStored += Enqueue;
    }

    public RemoteWriteStatus Status
    {
        get
        {
            lock (_sync)
                return new RemoteWriteStatus(_enabled, _url, _lastSuccess, Interlocked.Read(ref _sent), Interlocked.Read(ref _dropped),
                    Interlocked.Read(ref _failures), _queue.Count, _lastError);
        }
    }

    /// <summary>Reads the settings (enabled, URL, credentials); turning it off empties the queue.</summary>
    public async Task RefreshSettingsAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        lock (_sync)
        {
            _url = s.RemoteWriteUrl;
            _creds = s.RemoteWriteCredentials;
            _enabled = s.RemoteWriteEnabled && KasaSettingsService.ValidateRemoteWriteUrl(s.RemoteWriteUrl) is null;
            if (!_enabled) _queue.Clear();
        }
    }

    /// <summary>Called by the live stream for every stored sample; never blocks.</summary>
    public void Enqueue(LiveSample sample)
    {
        if (!_enabled) return;
        int count;
        lock (_sync)
        {
            if (_queue.Count >= MaxBuffered)
            {
                _queue.RemoveAt(0);
                Interlocked.Increment(ref _dropped);
            }
            _queue.Add(new Item(sample, _clock()));
            count = _queue.Count;
        }
        if (count >= FlushAtSamples && _wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>Sends one batch (if due). Returns the number of samples accepted by the endpoint.</summary>
    public async Task<int> FlushOnceAsync(CancellationToken ct)
    {
        await _flushLock.WaitAsync(ct);
        try
        {
            var now = _clock();
            List<Item> batch;
            string url;
            RemoteWriteCredentials? creds;
            lock (_sync)
            {
                var cutoff = now - MaxBufferAge;
                var old = _queue.FindIndex(i => i.EnqueuedUtc >= cutoff);
                var expired = old < 0 ? _queue.Count : old;
                if (expired > 0)
                {
                    _queue.RemoveRange(0, expired);
                    Interlocked.Add(ref _dropped, expired);
                    _log.LogWarning("Remote write: dropped {Count} samples older than {Age} (endpoint unavailable)", expired, MaxBufferAge);
                }
                if (!_enabled || _queue.Count == 0 || now < _retryAt) return 0;
                batch = _queue.Take(MaxBatchSamples).ToList();
                url = _url;
                creds = _creds;
            }

            var labels = await LabelsAsync(batch.Select(i => i.Sample.PlugId), now, ct);
            var series = BuildSeries(batch.Select(i => i.Sample), labels);
            var samples = series.Sum(s => s.Samples.Count);
            var (ok, retryable, error) = await SendAsync(url, creds, series, ct);
            lock (_sync)
            {
                if (ok || !retryable)
                {
                    // Sent, or rejected for good (4xx): either way these samples leave the queue.
                    _queue.RemoveRange(0, Math.Min(batch.Count, _queue.Count));
                }
                if (ok)
                {
                    Interlocked.Add(ref _sent, samples);
                    _lastSuccess = now;
                    _lastError = null;
                    _backoff = TimeSpan.Zero;
                    _retryAt = DateTimeOffset.MinValue;
                }
                else
                {
                    Interlocked.Increment(ref _failures);
                    _lastError = error;
                    if (!retryable) Interlocked.Add(ref _dropped, samples);
                    _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, _backoff.Ticks * 2));
                    _retryAt = now + _backoff;
                }
            }
            if (!ok)
                _log.LogWarning("Remote write to {Url} failed ({Retry}): {Error}", url, retryable ? $"retry in {_backoff.TotalSeconds:0} s" : "batch dropped", error);
            return ok ? samples : 0;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>
    /// One series per (metric, plug): <c>kasa_live_power_w</c> always, voltage/current/power factor when the sample has
    /// them; samples in time order. Samples of plugs without known labels (removed meanwhile) are skipped.
    /// </summary>
    public static IReadOnlyList<RemoteSeries> BuildSeries(IEnumerable<LiveSample> samples, IReadOnlyDictionary<Guid, string[]> labels)
    {
        var series = new Dictionary<(string Metric, Guid Plug), List<RemoteSample>>();
        void Add(string metric, Guid plug, double? value, DateTimeOffset ts)
        {
            if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v)) return;
            if (!series.TryGetValue((metric, plug), out var list)) series[(metric, plug)] = list = new List<RemoteSample>();
            list.Add(new RemoteSample(v, ts.ToUnixTimeMilliseconds()));
        }
        foreach (var s in samples.Where(s => labels.ContainsKey(s.PlugId)))
        {
            Add(PowerMetric, s.PlugId, s.PowerW, s.TsUtc);
            Add(VoltageMetric, s.PlugId, s.VoltageV, s.TsUtc);
            Add(CurrentMetric, s.PlugId, s.CurrentA, s.TsUtc);
            Add(PowerFactorMetric, s.PlugId, s.Pf, s.TsUtc);
        }
        return series.Select(kv =>
        {
            var values = labels[kv.Key.Plug];
            var l = new List<KeyValuePair<string, string>> { new("__name__", kv.Key.Metric) };
            for (var i = 0; i < PlugMetricLabels.Names.Length; i++) l.Add(new(PlugMetricLabels.Names[i], values[i]));
            return new RemoteSeries(l, kv.Value.OrderBy(x => x.TimestampMs).ToList());
        }).ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, string[]>> LabelsAsync(IEnumerable<Guid> needed, DateTimeOffset now, CancellationToken ct)
    {
        var missing = needed.Any(id => !_labels.ContainsKey(id));
        if (missing || now - _labelsReadAt >= LabelRefresh)
        {
            using var scope = _scopeFactory.CreateScope();
            var plugs = await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
            _labels = plugs.ToDictionary(p => p.Id, PlugMetricLabels.For);
            _labelsReadAt = now;
        }
        return _labels;
    }

    private async Task<(bool Ok, bool Retryable, string? Error)> SendAsync(string url, RemoteWriteCredentials? creds,
        IReadOnlyList<RemoteSeries> series, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent(Snappy.CompressToArray(RemoteWriteEncoder.Encode(series)));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            content.Headers.ContentEncoding.Add("snappy");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.Add("X-Prometheus-Remote-Write-Version", "0.1.0");
            req.Headers.UserAgent.ParseAdd("KasaMonitor");
            if (creds is not null)
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.User}:{creds.Password}")));
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return (true, false, null);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var code = (int)resp.StatusCode;
            var retryable = code >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
            return (false, retryable, $"HTTP {code}: {(body.Length > 300 ? body[..300] : body).Trim()}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, true, ex.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settingsReadAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_clock() - settingsReadAt >= TimeSpan.FromSeconds(10))
                {
                    await RefreshSettingsAsync(stoppingToken);
                    settingsReadAt = _clock();
                }
                await FlushOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "Remote write cycle failed"); }
            try { await _wake.WaitAsync(FlushInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override void Dispose()
    {
        _broadcaster.SampleStored -= Enqueue;
        _http.Dispose();
        base.Dispose();
    }
}
