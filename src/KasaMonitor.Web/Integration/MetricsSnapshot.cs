using System.Globalization;
using Prometheus;

namespace KasaMonitor.Web.Integration;

public sealed record MetricSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value);

public sealed record MetricFamilySummary(KasaMetrics.MetricInfo Info, int SeriesCount, MetricSample? Example);

/// <summary>Reads what /metrics would return right now (in process, so it is not counted as a scrape).</summary>
public static class MetricsSnapshot
{
    public static async Task<IReadOnlyList<MetricFamilySummary>> KasaFamiliesAsync(CollectorRegistry? registry = null, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await (registry ?? Metrics.DefaultRegistry).CollectAndExportAsTextAsync(ms, ct);
        var samples = ParseSamples(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        return KasaMetrics.Catalog.Select(info =>
        {
            var series = samples.Where(s => s.Name == info.Name)
                .OrderBy(s => s.Labels.GetValueOrDefault("alias"), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return new MetricFamilySummary(info, series.Count, series.FirstOrDefault());
        }).ToList();
    }

    /// <summary>Parses sample lines of the Prometheus text exposition format (comments and malformed lines are skipped).</summary>
    public static IReadOnlyList<MetricSample> ParseSamples(string text)
    {
        var result = new List<MetricSample>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            string name;
            string rest;
            var brace = line.IndexOf('{');
            var space = line.IndexOf(' ');
            if (brace >= 0 && (space < 0 || brace < space))
            {
                name = line[..brace];
                var end = ParseLabels(line, brace + 1, labels);
                if (end < 0) continue;
                rest = line[(end + 1)..].Trim();
            }
            else
            {
                if (space < 0) continue;
                name = line[..space];
                rest = line[(space + 1)..].Trim();
            }

            var valueText = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (valueText is null || !double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            result.Add(new MetricSample(name, labels, value));
        }
        return result;
    }

    /// <returns>Index of the closing brace, or -1.</returns>
    private static int ParseLabels(string line, int i, Dictionary<string, string> labels)
    {
        while (i < line.Length)
        {
            if (line[i] == '}') return i;
            if (line[i] == ',' || line[i] == ' ') { i++; continue; }
            var eq = line.IndexOf('=', i);
            if (eq < 0 || eq + 1 >= line.Length || line[eq + 1] != '"') return -1;
            var key = line[i..eq];
            var sb = new System.Text.StringBuilder();
            var j = eq + 2;
            for (; j < line.Length; j++)
            {
                var c = line[j];
                if (c == '\\' && j + 1 < line.Length)
                {
                    var n = line[++j];
                    sb.Append(n == 'n' ? '\n' : n);
                }
                else if (c == '"') break;
                else sb.Append(c);
            }
            if (j >= line.Length) return -1;
            labels[key] = sb.ToString();
            i = j + 1;
        }
        return -1;
    }
}
