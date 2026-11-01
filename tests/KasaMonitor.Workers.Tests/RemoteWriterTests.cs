using System.Net;
using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Prometheus;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Snappier;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>Fake remote write endpoint: records requests, answers with the queued status codes (200 when empty).</summary>
internal sealed class FakeRemoteWriteHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = new();
    public Queue<HttpStatusCode> Answers { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsByteArrayAsync(ct);
        lock (Requests) Requests.Add((request, body));
        return new HttpResponseMessage(Answers.Count > 0 ? Answers.Dequeue() : HttpStatusCode.NoContent) { Content = new StringContent("err") };
    }
}

/// <summary>Minimal protobuf reader for prometheus.WriteRequest (test side, independent of the encoder).</summary>
internal static class WriteRequestReader
{
    public sealed record Series(Dictionary<string, string> Labels, List<(double Value, long Ts)> Samples);

    public static List<Series> Read(byte[] snappyBody)
    {
        var raw = Snappy.DecompressToArray(snappyBody);
        var result = new List<Series>();
        foreach (var (field, ts) in Fields(raw))
        {
            if (field != 1) continue;
            var s = new Series(new(), new());
            foreach (var (f, part) in Fields(ts))
            {
                if (f == 1)
                {
                    var kv = Fields(part).ToDictionary(x => x.Field, x => Encoding.UTF8.GetString(x.Bytes));
                    s.Labels[kv[1]] = kv[2];
                }
                else if (f == 2)
                {
                    double v = 0; long t = 0; var i = 0;
                    while (i < part.Length)
                    {
                        var key = part[i++];
                        if (key == 0x09) { v = BitConverter.ToDouble(part, i); i += 8; }
                        else if (key == 0x10) { t = (long)Varint(part, ref i); }
                        else throw new InvalidDataException($"unexpected key {key}");
                    }
                    s.Samples.Add((v, t));
                }
            }
            result.Add(s);
        }
        return result;
    }

    private static IEnumerable<(int Field, byte[] Bytes)> Fields(byte[] data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var key = (int)Varint(data, ref i);
            if ((key & 7) != 2) throw new InvalidDataException("only length-delimited fields expected here");
            var len = (int)Varint(data, ref i);
            yield return (key >> 3, data[i..(i + len)]);
            i += len;
        }
    }

    private static ulong Varint(byte[] d, ref int i)
    {
        ulong v = 0; var shift = 0;
        while (true)
        {
            var b = d[i++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
    }
}

public class RemoteWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
    private const string Url = "http://192.168.254.4:9090/api/v1/write";

    private sealed class Rig : IDisposable
    {
        public TestDbFactory Db { get; } = new();
        public TestScopeFactory Scopes { get; }
        public KasaSettingsService Settings { get; }
        public LiveStreamBroadcaster Broadcaster { get; } = new();
        public FakeRemoteWriteHandler Http { get; } = new();
        public DateTimeOffset Now = T0;
        public PrometheusRemoteWriter Writer { get; }

        public Rig(bool enabled = true, string? user = null, string? password = null)
        {
            Scopes = new TestScopeFactory(Db.CreateContext);
            Settings = TestSettings.Create(Scopes, new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255",
                RemoteWriteEnabled: enabled, RemoteWriteUrl: Url, RemoteWriteUser: user, RemoteWritePassword: password));
            Writer = new PrometheusRemoteWriter(Scopes, Settings, Broadcaster, NullLogger<PrometheusRemoteWriter>.Instance, Http, () => Now);
        }

        public async Task<Guid> AddPlugAsync()
        {
            using var ctx = Db.CreateContext();
            return await new PlugRepository(ctx).UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.168.254.141"),
                new PlugInfo("d885ac0b743c", "Tablou", "S3EM-002CXCEU", "EMG3", "2.1.1", "gen3", true, -50, TimeSpan.Zero),
                PlugProtocols.ShellyRpc, null, 0, PlugRole.MainMeter), DateTimeOffset.UtcNow, CancellationToken.None);
        }

        public LiveStreamWorker Worker() => new(Scopes, Settings, Broadcaster, new FakeRpcSocketFactory(), new FakeKasaClient(), new FakeTapoClient(),
            NullLogger<LiveStreamWorker>.Instance, () => Now, TimeSpan.FromMilliseconds(5));

        public void Dispose()
        {
            Writer.Dispose();
            Db.Dispose();
        }
    }

    [Fact]
    public void Snappy_RoundTrip()
    {
        var payload = RemoteWriteEncoder.Encode(new[]
        {
            new RemoteSeries(new KeyValuePair<string, string>[] { new("__name__", "kasa_live_power_w"), new("alias", "Casă") },
                Enumerable.Range(0, 100).Select(i => new RemoteSample(400 + i, 1793551260000 + i * 1000L)).ToList()),
        });
        var compressed = Snappy.CompressToArray(payload);
        Snappy.DecompressToArray(compressed).Should().Equal(payload);
        compressed.Length.Should().BeLessThan(payload.Length);
    }

    [Fact]
    public async Task OnlyTheStoredSamples_AreSent_WithTheirOwnTimestamps_AndTheMetricLabels()
    {
        using var rig = new Rig();
        var plug = await rig.AddPlugAsync();
        await rig.Writer.RefreshSettingsAsync(CancellationToken.None);
        var worker = rig.Worker();

        for (var s = 0; s < 70; s++)   // constant power: stored = first + the 60 s witness
            worker.Accept(new LiveSample(plug, T0.AddSeconds(s), 500, null, 230.5, 2.2, 0.9));
        worker.SamplesReceived.Should().Be(70);
        rig.Writer.Status.Buffered.Should().Be(2);

        (await rig.Writer.FlushOnceAsync(CancellationToken.None)).Should().Be(8);   // 2 samples × 4 metrics

        var (req, body) = rig.Http.Requests.Single();
        req.RequestUri!.ToString().Should().Be(Url);
        req.Method.Should().Be(HttpMethod.Post);
        req.Content!.Headers.ContentEncoding.Should().Equal("snappy");
        req.Content.Headers.ContentType!.MediaType.Should().Be("application/x-protobuf");
        req.Headers.GetValues("X-Prometheus-Remote-Write-Version").Should().Equal("0.1.0");
        req.Headers.Authorization.Should().BeNull();
        var series = WriteRequestReader.Read(body);
        series.Select(s => s.Labels["__name__"]).Should().BeEquivalentTo(
            "kasa_live_power_w", "kasa_live_voltage_v", "kasa_live_current_a", "kasa_live_power_factor");
        var power = series.Single(s => s.Labels["__name__"] == "kasa_live_power_w");
        power.Labels.Should().Contain(new Dictionary<string, string>
            { ["alias"] = "Tablou", ["mac"] = "d885ac0b743c", ["model"] = "S3EM-002CXCEU", ["protocol"] = "shelly-rpc", ["role"] = "main" });
        power.Samples.Should().Equal((500, T0.ToUnixTimeMilliseconds()), (500, T0.AddSeconds(60).ToUnixTimeMilliseconds()));
        rig.Writer.Status.SentSamples.Should().Be(8);
        rig.Writer.Status.Buffered.Should().Be(0);
        rig.Writer.Status.LastSuccessUtc.Should().Be(T0);
    }

    [Fact]
    public async Task ServerError_IsRetriedWithBackoff_AndNothingIsLost()
    {
        using var rig = new Rig();
        var plug = await rig.AddPlugAsync();
        await rig.Writer.RefreshSettingsAsync(CancellationToken.None);
        rig.Http.Answers.Enqueue(HttpStatusCode.ServiceUnavailable);
        rig.Broadcaster.PublishStored(new LiveSample(plug, T0, 500));

        (await rig.Writer.FlushOnceAsync(CancellationToken.None)).Should().Be(0);
        rig.Writer.Status.Failures.Should().Be(1);
        rig.Writer.Status.Buffered.Should().Be(1);
        rig.Writer.Status.LastError.Should().Contain("503");

        (await rig.Writer.FlushOnceAsync(CancellationToken.None)).Should().Be(0);   // still inside the 1 s backoff: no request
        rig.Http.Requests.Should().HaveCount(1);

        rig.Now = T0.AddSeconds(2);
        (await rig.Writer.FlushOnceAsync(CancellationToken.None)).Should().Be(1);
        rig.Writer.Status.SentSamples.Should().Be(1);
        rig.Writer.Status.DroppedSamples.Should().Be(0);
        rig.Writer.Status.LastError.Should().BeNull();
    }

    [Fact]
    public async Task ClientError_DropsTheBatch_AndOldSamplesAreDroppedAfter5Minutes()
    {
        using var rig = new Rig();
        var plug = await rig.AddPlugAsync();
        await rig.Writer.RefreshSettingsAsync(CancellationToken.None);

        rig.Http.Answers.Enqueue(HttpStatusCode.BadRequest);   // e.g. out-of-order sample
        rig.Broadcaster.PublishStored(new LiveSample(plug, T0, 500));
        await rig.Writer.FlushOnceAsync(CancellationToken.None);
        rig.Writer.Status.Buffered.Should().Be(0);
        rig.Writer.Status.DroppedSamples.Should().Be(1);

        rig.Http.Answers.Enqueue(HttpStatusCode.ServiceUnavailable);
        rig.Now = T0.AddSeconds(10);
        rig.Broadcaster.PublishStored(new LiveSample(plug, rig.Now, 600));
        await rig.Writer.FlushOnceAsync(CancellationToken.None);               // fails, stays queued
        rig.Writer.Status.Buffered.Should().Be(1);

        rig.Now = T0.AddMinutes(6);                                            // endpoint down for more than 5 minutes
        await rig.Writer.FlushOnceAsync(CancellationToken.None);
        rig.Writer.Status.Buffered.Should().Be(0);
        rig.Writer.Status.DroppedSamples.Should().Be(2);
    }

    [Fact]
    public async Task Disabled_EnqueuesNothing_AndBasicAuthIsSentWhenSet()
    {
        using (var off = new Rig(enabled: false))
        {
            await off.Writer.RefreshSettingsAsync(CancellationToken.None);
            off.Broadcaster.PublishStored(new LiveSample(Guid.NewGuid(), T0, 1));
            off.Writer.Status.Buffered.Should().Be(0);
            (await off.Writer.FlushOnceAsync(CancellationToken.None)).Should().Be(0);
            off.Http.Requests.Should().BeEmpty();
        }

        using var rig = new Rig(user: "kasa", password: "pw");
        var plug = await rig.AddPlugAsync();
        await rig.Writer.RefreshSettingsAsync(CancellationToken.None);
        rig.Broadcaster.PublishStored(new LiveSample(plug, T0, 1));
        await rig.Writer.FlushOnceAsync(CancellationToken.None);
        var auth = rig.Http.Requests.Single().Request.Headers.Authorization!;
        auth.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)).Should().Be("kasa:pw");
    }

    [Fact]
    public async Task Settings_RemoteWriteUrlIsValidated_AndCredentialsAreStoredEncrypted()
    {
        using var rig = new Rig(enabled: false);
        var bad = () => rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "192.168.254.255", true, "", null, false,
            RemoteWriteEnabled: true, RemoteWriteUrl: "192.168.254.4:9090"), CancellationToken.None);
        await bad.Should().ThrowAsync<FormatException>();

        await rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "192.168.254.255", true, "", null, false,
            RemoteWriteEnabled: true, RemoteWriteUrl: Url, NewRemoteWriteUser: "kasa", NewRemoteWritePassword: "pw", LiveBurstTriggerW: 80), CancellationToken.None);
        var s = await rig.Settings.GetAsync(CancellationToken.None);
        s.RemoteWriteEnabled.Should().BeTrue();
        s.RemoteWriteCredentials.Should().Be(new RemoteWriteCredentials("kasa", "pw"));
        s.LiveBurstTriggerW.Should().Be(80);
        using var ctx = rig.Db.CreateContext();
        ctx.Settings.Where(x => x.Key == SettingKeys.RemoteWritePassword || x.Key == SettingKeys.RemoteWriteUser)
            .AsEnumerable().Should().OnlyContain(x => x.Value.StartsWith("enc:"));
        (await new Rig().Settings.GetAsync(CancellationToken.None)).LiveBurstTriggerW.Should().Be(50);   // default
    }
}
