using System.Globalization;
using KasaMonitor.Core.Reports;
using KasaMonitor.Workers;

namespace KasaMonitor.Web.Ui;

/// <summary>
/// The texts and the axis of the „Putere” chart of /history (0.14.0): the labels under the axis (at most
/// <see cref="MaxTicks"/>, on whole hours, local midnights or months), the time of a point, the time of a maximum, the
/// words of the comparison, the colours of the series (the palette and the order of /contor).
/// </summary>
public static class PowerChart
{
    public const int MaxTicks = 12;

    /// <summary>A label under the axis: the grid index it stands at and its text.</summary>
    public sealed record Tick(int Index, string Label);

    private static readonly int[] HourSteps = { 1, 2, 3, 4, 6, 12, 24 };
    private static readonly int[] DaySteps = { 1, 2, 3, 5, 7, 14, 28 };
    private static readonly int[] MonthSteps = { 1, 2, 3, 6, 12 };

    private static DateTime Local(DateTimeOffset utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(utc, tz).DateTime;

    private static int Step(int[] steps, double count) => steps.FirstOrDefault(s => Math.Ceiling(count / s) <= MaxTicks, steps[^1]);

    private static string Hm(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string DayShort(DateTime t) => $"{RoNames.DaysShort[(int)t.DayOfWeek]} {t.Day}";

    private static string DayMonth(DateTime t) => $"{t.Day} {RoNames.MonthShort(t.Month)}";

    /// <summary>
    /// The labels of a grid: minutes → whole local hours every 1–24 h („14:00”; a local midnight of a multi-day grid: „12 nov”);
    /// hours → local midnights every 1–28 days („lun 3” for up to a week, else „3 nov”); days → the same, or the first of
    /// every 1–12 months („nov”, January and the first label with the year) beyond 62 days. A wall time seen twice (the
    /// repeated 03:00 of the autumn DST day) is labelled once.
    /// </summary>
    public static IReadOnlyList<Tick> Ticks(IReadOnlyList<DateTimeOffset> grid, PowerResolution resolution, TimeZoneInfo tz)
    {
        var ticks = new List<Tick>();
        if (grid.Count == 0) return ticks;
        var local = grid.Select(t => Local(t, tz)).ToList();
        var firstDay = local[0].Date;
        var days = (local[^1].Date - firstDay).Days + 1;
        var seen = new HashSet<DateTime>();
        switch (resolution)
        {
            case PowerResolution.Minute:
            {
                var step = Step(HourSteps, days * 24.0);
                for (var i = 0; i < local.Count; i++)
                {
                    var t = local[i];
                    if (t.Minute != 0 || t.Hour % step != 0 || !seen.Add(t)) continue;
                    ticks.Add(new Tick(i, days > 1 && t.Hour == 0 ? DayMonth(t) : Hm(t)));
                }
                break;
            }
            case PowerResolution.Day when days > 62:
            {
                var months = (local[^1].Year - local[0].Year) * 12 + local[^1].Month - local[0].Month + 1;
                var step = Step(MonthSteps, months);
                for (var i = 0; i < local.Count; i++)
                {
                    var t = local[i];
                    var index = (t.Year - local[0].Year) * 12 + t.Month - local[0].Month;
                    if (i > 0 && t.Day != 1 || index % step != 0) continue;
                    var name = RoNames.MonthShort(t.Month);
                    ticks.Add(new Tick(i, ticks.Count == 0 || t.Month == 1 ? $"{name} {t.Year}" : name));
                }
                break;
            }
            default:
            {
                var step = Step(DaySteps, days);
                for (var i = 0; i < local.Count; i++)
                {
                    var t = local[i];
                    if (t.TimeOfDay != TimeSpan.Zero && resolution == PowerResolution.Hour || (t.Date - firstDay).Days % step != 0 || !seen.Add(t.Date)) continue;
                    ticks.Add(new Tick(i, days <= 7 ? DayShort(t) : DayMonth(t)));
                }
                break;
            }
        }
        return ticks;
    }

    /// <summary>The time of a point: „mar 12 nov, 14:37” (minute), „mar 12 nov, 14:00–15:00” (hour), „mar 12 nov 2026” (day).</summary>
    public static string PointTitle(DateTimeOffset startUtc, PowerResolution resolution, TimeZoneInfo tz)
    {
        var t = Local(startUtc, tz);
        var day = $"{RoNames.DaysShort[(int)t.DayOfWeek]} {DayMonth(t)}";
        return resolution switch
        {
            PowerResolution.Minute => $"{day}, {Hm(t)}",
            PowerResolution.Hour => $"{day}, {Hm(t)}–{Hm(Local(startUtc.AddHours(1), tz))}",
            _ => $"{day} {t.Year}",
        };
    }

    /// <summary>
    /// The time of a run of buckets [start, end) (a hover zone of a dense chart): „mar 12 nov, 14:00–14:08”, „12 nov 22:00 –
    /// 13 nov 04:00”, „12–15 nov 2026”, „28 nov – 2 dec 2026”; a single bucket as <see cref="PointTitle"/>.
    /// </summary>
    public static string RangeTitle(DateTimeOffset startUtc, DateTimeOffset endUtc, int buckets, PowerResolution resolution, TimeZoneInfo tz)
    {
        if (buckets <= 1) return PointTitle(startUtc, resolution, tz);
        var a = Local(startUtc, tz);
        var b = Local(endUtc, tz);
        if (resolution == PowerResolution.Day)
        {
            var last = b.AddDays(-1);
            return a.Year == last.Year && a.Month == last.Month
                ? $"{a.Day}–{last.Day} {RoNames.MonthShort(a.Month)} {a.Year}"
                : $"{DayMonth(a)} – {DayMonth(last)} {last.Year}";
        }
        return a.Date == b.Date || b.TimeOfDay == TimeSpan.Zero && b.Date == a.Date.AddDays(1)
            ? $"{RoNames.DaysShort[(int)a.DayOfWeek]} {DayMonth(a)}, {Hm(a)}–{Hm(b)}"
            : $"{DayMonth(a)} {Hm(a)} – {DayMonth(b)} {Hm(b)}";
    }

    /// <summary>When a maximum was: its minute („12 nov, 14:37”) or, from hour rows, its hour („12 nov 2026, 14:00–15:00”).</summary>
    public static string When(DateTimeOffset maxAtUtc, PowerResolution resolution, TimeZoneInfo tz)
    {
        var t = Local(maxAtUtc, tz);
        return resolution == PowerResolution.Minute
            ? $"{DayMonth(t)}, {Hm(t)}"
            : $"{DayMonth(t)} {t.Year}, {Hm(t)}–{Hm(Local(maxAtUtc.AddHours(1), tz))}";
    }

    /// <summary>„pe minut” / „pe oră” / „pe zi”.</summary>
    public static string ResolutionText(PowerResolution resolution) => resolution switch
    {
        PowerResolution.Minute => "pe minut",
        PowerResolution.Hour => "pe oră",
        _ => "pe zi",
    };

    /// <summary>The option of the page's comparison control.</summary>
    public const string LastYearOption = "Același interval anul trecut";

    /// <summary>
    /// The words of the comparison of the section: its name (legend, „fără date pentru …”) and the „față de …” of the Δ.
    /// The previous period as on the rest of the page (today: „ieri”, „la aceeași oră”); last year with the period's title.
    /// A period in progress is compared with the same part of the comparison.
    /// </summary>
    public static (string Name, string Versus) ComparisonWords(HistoryPower p)
    {
        var inProgress = p.NowIndex is not null;
        if (p.Compare == CompareMode.LastYear && p.ComparisonPeriod is { } cp)
        {
            var title = ReportFormat.Title(cp);
            return ($"anul trecut ({title})", inProgress ? "față de aceeași parte din " + title : "față de " + title);
        }
        var names = PeriodNames.For(p.Period.Kind);
        if (inProgress && p.Period.Kind == ReportGranularity.Day) return ("ieri", "față de ieri la aceeași oră");
        return (names.Previous, inProgress ? "față de aceeași parte din " + names.Previous : names.Versus);
    }

    /// <summary>
    /// The colour of every channel, in the palette of /contor and stable whatever else is shown: the main meter its own, the
    /// other channels the overlay colours by their place in the list (repeated beyond the palette).
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Colors(HistoryPower p)
    {
        var colors = new Dictionary<Guid, string>();
        var i = 0;
        foreach (var c in p.Channels)
            colors[c.PlugId] = c.IsMain ? LiveChartColors.Main : LiveChartColors.Overlays[i++ % LiveChartColors.Overlays.Count];
        return colors;
    }

    /// <summary>The alias of a series („?” for an id that is no longer a channel).</summary>
    public static string Name(HistoryPower p, Guid id) => p.Channels.FirstOrDefault(c => c.PlugId == id)?.Alias ?? "?";
}
