using System.Text;

namespace KasaMonitor.Core.History;

/// <summary>
/// The table of one history section, as text cells (single owner: the page shows it and the CSV export writes it, so both
/// always hold the same rows). <see cref="Links"/>: per row, the address of the period it drills down to (null = none).
/// <see cref="CsvKeys"/>: per row, the first cell as the CSV writes it — the time in ISO (<c>yyyy-MM-dd HH:00</c>,
/// <c>yyyy-MM-dd</c>, <c>yyyy-MM</c>) instead of the page's text („lun 2 nov”), so a spreadsheet sorts and filters it as a date.
/// </summary>
public sealed record HistoryTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<string?>? Links = null,
    IReadOnlyList<string>? CsvKeys = null)
{
    public static readonly HistoryTable Empty = new(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
}

/// <summary>
/// CSV for the spreadsheet the house uses (Excel / LibreOffice in Romanian): <c>;</c> between fields (the decimal comma
/// stays inside the numbers), CRLF between records, quotes around a field holding <c>;</c>, a quote or a line break
/// (doubled inside), UTF-8 with BOM so the diacritics open correctly.
/// </summary>
public static class HistoryCsv
{
    public const char Separator = ';';

    public static string Write(HistoryTable table)
    {
        var sb = new StringBuilder();
        Line(sb, table.Headers);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            if (table.CsvKeys is { } keys && r < keys.Count && row.Count > 0) row = row.Skip(1).Prepend(keys[r]).ToList();
            Line(sb, row);
        }
        return sb.ToString();
    }

    public static byte[] Bytes(HistoryTable table) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Write(table))).ToArray();

    public static string Field(string value) =>
        value.IndexOfAny(new[] { Separator, '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static void Line(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(Separator);
            sb.Append(Field(fields[i]));
        }
        sb.Append("\r\n");
    }
}
