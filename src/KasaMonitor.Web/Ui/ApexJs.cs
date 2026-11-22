namespace KasaMonitor.Web.Ui;

/// <summary>JavaScript values for ApexCharts options typed as <c>object</c> (e.g. <c>YAxis.Min</c>/<c>Max</c>).</summary>
public static class ApexJs
{
    /// <summary>
    /// A JavaScript function for an <c>object</c> option. Blazor-ApexCharts evaluates only <c>{"@eval": "…"}</c> on the JS
    /// side; a <c>CustomFunction</c> assigned to an <c>object</c> property is serialized as a plain string and ignored by
    /// ApexCharts (measured with 8.0.0: the padded axis of 0.15.1 had no effect).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Function(string js) => new Dictionary<string, string> { ["@eval"] = js };
}
