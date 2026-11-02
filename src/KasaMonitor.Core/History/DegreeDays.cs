namespace KasaMonitor.Core.History;

/// <summary>
/// Heating degree days of one day from its mean outdoor temperature (Eurostat method, the default): <c>Base − Tmean</c>
/// when <c>Tmean ≤ Threshold</c>, otherwise 0. Eurostat uses base 18 °C and threshold 15 °C; with another base
/// (setting <c>HeatingDegreeDaysBaseC</c>) the threshold stays 3 °C under it. The Romanian standard SR 4839 uses another
/// method (base 20 °C, threshold 12 °C), not this one.
/// </summary>
public sealed record DegreeDayRule(double BaseC, double ThresholdC)
{
    public const double EurostatBaseC = 18;
    public const double EurostatThresholdC = 15;

    /// <summary>Base and threshold differ by this much in the Eurostat method (18 / 15 °C).</summary>
    public const double ThresholdBelowBaseC = EurostatBaseC - EurostatThresholdC;

    public static readonly DegreeDayRule Eurostat = new(EurostatBaseC, EurostatThresholdC);

    /// <summary>The rule for a base temperature (threshold = base − 3 °C, as Eurostat's 18 / 15).</summary>
    public static DegreeDayRule ForBase(double baseC) => new(baseC, baseC - ThresholdBelowBaseC);

    /// <summary>Degree days of a day with this mean outdoor temperature (°C·day), Eurostat: 0 above the threshold.</summary>
    public double Of(double meanOutdoorC) => meanOutdoorC <= ThresholdC ? BaseC - meanOutdoorC : 0;

    /// <summary>
    /// The heating demand of a day without the threshold: max(0, base − Tmean). The x of the energy signature: between the
    /// threshold and the base the boiler still heats, and a jump to 0 there would bend the line (0.9.2, review R3).
    /// </summary>
    public double Linear(double meanOutdoorC) => Math.Max(0, BaseC - meanOutdoorC);
}

/// <summary>A least-squares line <c>y = Intercept + Slope · x</c>, its R² and the number of points.</summary>
public sealed record LinearFit(double Intercept, double Slope, double RSquared, int Count)
{
    public double At(double x) => Intercept + Slope * x;
}

public static class LinearRegression
{
    /// <summary>
    /// Ordinary least squares over the points; null with fewer than two points or when every x is the same (no line).
    /// R² = 1 − SS_res / SS_tot (1 when every y is the same and the line passes through them).
    /// </summary>
    public static LinearFit? Fit(IReadOnlyList<(double X, double Y)> points)
    {
        var n = points.Count;
        if (n < 2) return null;
        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var (x, y) in points)
        {
            sxx += (x - mx) * (x - mx);
            sxy += (x - mx) * (y - my);
            syy += (y - my) * (y - my);
        }
        if (sxx < 1e-12) return null;
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        double ssRes = 0;
        foreach (var (x, y) in points)
        {
            var e = y - (intercept + slope * x);
            ssRes += e * e;
        }
        var r2 = syy < 1e-12 ? 1 : 1 - ssRes / syy;
        return new LinearFit(intercept, slope, r2, n);
    }
}

/// <summary>
/// One day of the energy signature: its x = max(0, base − Tmean) (<see cref="DegreeDayRule.Linear"/>, no threshold), the
/// gas used for heating (kWh) and the mean outdoor temperature.
/// </summary>
public sealed record SignaturePoint(DateOnly Day, double DegreeDays, double GasHeatingKwh, double OutdoorMeanC);

/// <summary>
/// The house's energy signature over a set of days: heating gas (kWh/day) against heating degree days (per day), with the
/// regression line when there are at least <see cref="EnergySignatureRule.MinDays"/> days with both values.
/// <see cref="FirstDay"/>: the first day with both values (the data start there); null without any.
/// </summary>
public sealed record EnergySignature(IReadOnlyList<SignaturePoint> Points, LinearFit? Fit, DateOnly? FirstDay, DegreeDayRule Rule)
{
    public bool Enough => Points.Count >= EnergySignatureRule.MinDays && Fit is not null;
}

public static class EnergySignatureRule
{
    /// <summary>Fewer days than this with both values: no line (the message „insuficiente date”).</summary>
    public const int MinDays = 7;

    /// <summary>
    /// The days that have both a mean outdoor temperature and a heating gas value, in day order, each with its x =
    /// max(0, base − Tmean) (<see cref="DegreeDayRule.Linear"/>: no Eurostat threshold); the line of gas ~ x when there are at
    /// least <see cref="MinDays"/> of them.
    /// </summary>
    public static EnergySignature Build(IReadOnlyDictionary<DateOnly, double> outdoorMeanC, IReadOnlyDictionary<DateOnly, double> gasHeatingKwh,
        DegreeDayRule rule)
    {
        var points = outdoorMeanC
            .Where(kv => gasHeatingKwh.ContainsKey(kv.Key))
            .OrderBy(kv => kv.Key)
            .Select(kv => new SignaturePoint(kv.Key, rule.Linear(kv.Value), gasHeatingKwh[kv.Key], kv.Value))
            .ToList();
        var fit = points.Count >= MinDays ? LinearRegression.Fit(points.Select(p => (p.DegreeDays, p.GasHeatingKwh)).ToList()) : null;
        return new EnergySignature(points, fit, points.Count > 0 ? points[0].Day : null, rule);
    }
}
