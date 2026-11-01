using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KasaMonitor.Core.Protocol.Shelly;

namespace KasaMonitor.Workers.Tests;

/// <summary>One fake Shelly device per IP; defaults to the captured EM Gen3 answers.</summary>
internal sealed class FakeShellyClient : IShellyClient
{
    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    public static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public sealed class Device
    {
        public string DeviceInfo { get; set; } = Fixture("shelly_emg3_deviceinfo.json");
        public string Status { get; set; } = Fixture("shelly_emg3_status.json");
        public string Config { get; set; } = Fixture("shelly_emg3_config.json");
        public string? Password { get; set; }
        public Exception? StatusError { get; set; }
        /// <summary>Windows (from ts) for which GetData reports more records than it can page through.</summary>
        public HashSet<long> IncompleteWindows { get; } = new();
        /// <summary>EM1Data rows by channel.</summary>
        public Dictionary<int, List<ShellyEnergyRow>> Energy { get; } = new();
    }

    public Dictionary<string, Device> Devices { get; } = new();
    public List<ShellyMdnsAnswer> MdnsAnswers { get; } = new();
    public int StatusCalls;
    public int DeviceInfoCalls;
    public List<(int Id, long From, long To)> DataRequests { get; } = new();

    public Device Add(string ip, Device? device = null) => Devices[ip] = device ?? new Device();

    private Device Get(IPAddress ip) =>
        Devices.TryGetValue(ip.ToString(), out var d) ? d : throw new TimeoutException($"{ip}: no answer");

    private static void CheckAuth(Device d, string? password)
    {
        if (d.Password is null) return;
        if (string.IsNullOrEmpty(password)) throw new ShellyAuthenticationException("password required", passwordMissing: true);
        if (password != d.Password) throw new ShellyAuthenticationException("password rejected", passwordMissing: false);
    }

    public Task<ShellyDeviceInfo> GetDeviceInfoAsync(IPAddress ip, CancellationToken ct, TimeSpan? timeout = null)
    {
        Interlocked.Increment(ref DeviceInfoCalls);
        var d = Get(ip);
        var info = ShellyDeviceInfo.Parse(Json(d.DeviceInfo));
        return Task.FromResult(d.Password is null ? info : info with { AuthEnabled = true });
    }

    public Task<JsonElement> GetStatusAsync(IPAddress ip, string? password, CancellationToken ct)
    {
        Interlocked.Increment(ref StatusCalls);
        var d = Get(ip);
        if (d.StatusError is not null) throw d.StatusError;
        CheckAuth(d, password);
        return Task.FromResult(Json(d.Status));
    }

    public Task<JsonElement> GetConfigAsync(IPAddress ip, string? password, CancellationToken ct)
    {
        var d = Get(ip);
        CheckAuth(d, password);
        return Task.FromResult(Json(d.Config));
    }

    public Task<IReadOnlyList<ShellyDataBlock>> GetEm1RecordsAsync(IPAddress ip, string? password, int id, CancellationToken ct)
    {
        var d = Get(ip);
        CheckAuth(d, password);
        var rows = d.Energy.GetValueOrDefault(id) ?? new List<ShellyEnergyRow>();
        IReadOnlyList<ShellyDataBlock> blocks = rows.Select(r => new ShellyDataBlock(r.Ts, 60, 1)).ToList();
        return Task.FromResult(blocks);
    }

    public Task<IReadOnlyList<ShellyEnergyRow>> GetEm1DataAsync(IPAddress ip, string? password, int id, long fromTs, long toTs, CancellationToken ct)
    {
        var d = Get(ip);
        CheckAuth(d, password);
        lock (DataRequests) DataRequests.Add((id, fromTs, toTs));
        if (d.IncompleteWindows.Contains(fromTs)) throw new ShellyIncompleteDataException("page limit reached");
        IReadOnlyList<ShellyEnergyRow> rows = (d.Energy.GetValueOrDefault(id) ?? new()).Where(r => r.Ts >= fromTs && r.Ts <= toTs).ToList();
        return Task.FromResult(rows);
    }

    public async IAsyncEnumerable<ShellyMdnsAnswer> DiscoverAsync(TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var a in MdnsAnswers) yield return a;
        await Task.CompletedTask;
    }
}
