using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using KasaMonitor.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.DataMigration;

/// <summary>What to do with a target that already has rows.</summary>
public enum TargetMode
{
    /// <summary>Refuse to start when any table of the target has rows (the default: the copy is idempotent by refusal).</summary>
    RefuseNonEmpty,
    /// <summary>Empty every table of the target first, then copy everything.</summary>
    Truncate,
    /// <summary>Keep the tables already copied and verified (same count, same sums); empty and copy again the others.</summary>
    Resume,
}

/// <param name="SourcePath">The SQLite file (a consistent copy, e.g. the nightly backup; opened read-only).</param>
/// <param name="TargetConnectionString">SQL Server, the KasaMonitor database (created with deploy/sql/create-database.sql).</param>
/// <param name="ApplyMigrations">Brings the target schema up to date first (the same migrations the app applies at start).</param>
public sealed record MigrationOptions(
    string SourcePath,
    string TargetConnectionString,
    TargetMode Mode = TargetMode.RefuseNonEmpty,
    int BatchSize = DataMigrator.DefaultBatchSize,
    bool ApplyMigrations = true);

/// <summary>Sum of one numeric column on both sides.</summary>
public sealed record ColumnSum(string Column, double Source, double Target)
{
    /// <summary>Equal within a relative 1e-9 (sums of floating values in another order).</summary>
    public bool Ok => Math.Abs(Source - Target) <= 1e-9 * Math.Max(Math.Abs(Source), Math.Abs(Target)) + 1e-6;
}

/// <summary>Outcome of one table.</summary>
public sealed record TableResult(string Table, string Action, long SourceRows, long TargetRows, TimeSpan Duration,
    IReadOnlyList<ColumnSum> Sums, string? Error = null)
{
    public bool Ok => Error is null && SourceRows == TargetRows && Sums.All(s => s.Ok);
}

/// <summary>The final report (Markdown with <see cref="ToMarkdown"/>).</summary>
public sealed record MigrationReport(string SourcePath, long SourceBytes, TimeSpan Duration, IReadOnlyList<TableResult> Tables, IReadOnlyList<string> Notes)
{
    public bool Ok => Tables.All(t => t.Ok);
    public long CopiedRows => Tables.Where(t => t.Action == DataMigrator.ActionCopied).Sum(t => t.TargetRows);

    public string ToMarkdown()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# KasaMonitor: SQLite → SQL Server data migration");
        sb.AppendLine();
        sb.AppendLine(inv, $"- Source: `{Path.GetFileName(SourcePath)}`, {SourceBytes / 1048576.0:0.0} MB");
        sb.AppendLine(inv, $"- Result: **{(Ok ? "OK" : "FAILED")}**, {Tables.Count} tables, {CopiedRows:N0} rows copied in {Duration.TotalSeconds:0.0} s");
        var copySeconds = Tables.Where(t => t.Action == DataMigrator.ActionCopied).Sum(t => t.Duration.TotalSeconds);
        if (copySeconds > 0)
            sb.AppendLine(inv, $"- Throughput: {CopiedRows / copySeconds:N0} rows/s, {SourceBytes / 1048576.0 / Duration.TotalSeconds:0.0} MB/s of source file");
        sb.AppendLine();
        sb.AppendLine("| Table | Action | Source rows | Target rows | Seconds | Columns checked | Status |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---|");
        foreach (var t in Tables)
            sb.AppendLine(inv, $"| {t.Table} | {t.Action} | {t.SourceRows:N0} | {t.TargetRows:N0} | {t.Duration.TotalSeconds:0.00} | {t.Sums.Count} | {(t.Ok ? "ok" : "**" + (t.Error ?? "mismatch") + "**")} |");
        var bad = Tables.SelectMany(t => t.Sums.Where(s => !s.Ok).Select(s => (t.Table, s))).ToList();
        if (bad.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Checks that differ");
            foreach (var (table, s) in bad)
                sb.AppendLine(inv, $"- {table}.{s.Column}: source {s.Source:R}, target {s.Target:R}");
        }
        if (Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Notes");
            foreach (var n in Notes) sb.AppendLine("- " + n);
        }
        return sb.ToString();
    }
}

/// <summary>The copy cannot start or cannot go on (wrong source, schema mismatch, non-empty target, value too long).</summary>
public sealed class MigrationException(string message) : Exception(message);

/// <summary>
/// Copies every table of a KasaMonitor SQLite database into the SQL Server database: the target schema comes from the EF
/// migrations; tables in foreign-key order; rows in batches through <see cref="SqlBulkCopy"/> with the identities kept
/// (<see cref="SqlBulkCopyOptions.KeepIdentity"/>, the bulk form of <c>SET IDENTITY_INSERT ON</c>) and the identity seeds
/// moved past the copied ids; every table checked by <c>COUNT(*)</c> and by the sum of each numeric column.
/// </summary>
public sealed class DataMigrator
{
    public const int DefaultBatchSize = 5000;
    public const string ActionCopied = "copied";
    public const string ActionKept = "kept (already copied)";
    public const string ActionMissing = "not in source";

    /// <summary>EF's own tables: the target has its migration history, the lock table is transient.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "__EFMigrationsHistory", "__EFMigrationsLock" };

    private readonly Action<string> _log;

    public DataMigrator(Action<string>? log = null) => _log = log ?? (_ => { });

    private sealed record TargetColumn(string Name, string Type, int MaxChars, bool Nullable, bool HasDefault, bool Identity);

    public async Task<MigrationReport> RunAsync(MigrationOptions o, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        if (!File.Exists(o.SourcePath)) throw new MigrationException($"The source {o.SourcePath} does not exist.");
        if (o.BatchSize < 1) throw new MigrationException("The batch size must be positive.");
        var notes = new List<string>();

        if (o.ApplyMigrations)
        {
            _log("Applying the migrations to the target …");
            await using var ctx = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseKasaSqlServer(o.TargetConnectionString).Options);
            await ctx.Database.MigrateAsync(ct);
        }

        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = o.SourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await source.OpenAsync(ct);
        await using var target = new SqlConnection(o.TargetConnectionString);
        await target.OpenAsync(ct);

        var sourceTables = await SourceTablesAsync(source, ct);
        var targetTables = await TargetTablesAsync(target, ct);
        var order = await TableOrderAsync(target, targetTables.Keys, ct);

        // The plan is checked before any row moves: nothing of the source may be left behind.
        var problems = new List<string>();
        foreach (var t in sourceTables.Keys.Where(t => !targetTables.ContainsKey(t)).OrderBy(t => t, StringComparer.Ordinal))
            problems.Add($"table {t} is not in the target (a newer source than this version?)");
        foreach (var (table, cols) in targetTables)
        {
            if (!sourceTables.TryGetValue(table, out var srcCols)) continue;
            foreach (var c in srcCols.Where(c => !cols.Any(t => t.Name.Equals(c, StringComparison.OrdinalIgnoreCase))))
                problems.Add($"column {table}.{c} is not in the target");
            foreach (var c in cols.Where(c => !c.Nullable && !c.HasDefault && !c.Identity && !srcCols.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
                problems.Add($"column {table}.{c.Name} is required in the target and missing in the source");
        }
        if (problems.Count > 0) throw new MigrationException("The source does not fit the target: " + string.Join("; ", problems) + ".");

        var targetCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in order) targetCounts[t] = await TargetCountAsync(target, t, ct);
        var nonEmpty = targetCounts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} ({kv.Value:N0})").ToList();
        if (nonEmpty.Count > 0 && o.Mode == TargetMode.RefuseNonEmpty)
            throw new MigrationException("The target is not empty: " + string.Join(", ", nonEmpty) +
                                         ". Use --truncate to replace everything or --resume to continue an interrupted copy.");
        if (o.Mode == TargetMode.Truncate)
            foreach (var t in Enumerable.Reverse(order).Where(t => targetCounts[t] > 0))
                await EmptyAsync(target, t, ct);

        var results = new List<TableResult>();
        foreach (var table in order)
        {
            ct.ThrowIfCancellationRequested();
            var cols = targetTables[table];
            if (!sourceTables.TryGetValue(table, out var srcCols))
            {
                notes.Add($"{table}: not in the source (added after the source's version), left empty.");
                results.Add(new TableResult(table, ActionMissing, 0, await TargetCountAsync(target, table, ct), TimeSpan.Zero, []));
                continue;
            }
            var copied = cols.Where(c => srcCols.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var checkedCols = copied;
            var sw = Stopwatch.StartNew();
            var sourceRows = await SourceCountAsync(source, table, ct);

            if (o.Mode == TargetMode.Resume && targetCounts[table] > 0)
            {
                var existing = await CheckAsync(source, target, table, checkedCols, ct);
                if (targetCounts[table] == sourceRows && existing.All(s => s.Ok))
                {
                    _log($"{table}: {sourceRows:N0} rows already copied and verified, kept");
                    results.Add(new TableResult(table, ActionKept, sourceRows, targetCounts[table], sw.Elapsed, existing));
                    continue;
                }
                _log($"{table}: {targetCounts[table]:N0} of {sourceRows:N0} rows from an interrupted copy, copied again");
                await EmptyAsync(target, table, ct);
            }

            try
            {
                await CopyAsync(source, target, table, copied, sourceRows, o.BatchSize, ct);
                if (copied.Any(c => c.Identity) && sourceRows > 0)
                    await ExecAsync(target, $"DBCC CHECKIDENT ('{Quote(table)}', RESEED) WITH NO_INFOMSGS", ct);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or FormatException or MigrationException)
            {
                results.Add(new TableResult(table, ActionCopied, sourceRows, await TargetCountAsync(target, table, ct), sw.Elapsed, [], ex.Message));
                _log($"{table}: FAILED: {ex.Message}");
                notes.Add($"Stopped at {table}; run again with --resume to continue from this table.");
                return new MigrationReport(o.SourcePath, new FileInfo(o.SourcePath).Length, total.Elapsed, results, notes);
            }
            var sums = await CheckAsync(source, target, table, checkedCols, ct);
            var result = new TableResult(table, ActionCopied, sourceRows, await TargetCountAsync(target, table, ct), sw.Elapsed, sums);
            results.Add(result);
            _log($"{table}: {result.TargetRows:N0} rows in {sw.Elapsed.TotalSeconds:0.0} s, {(result.Ok ? "verified" : "MISMATCH")}");
        }
        return new MigrationReport(o.SourcePath, new FileInfo(o.SourcePath).Length, total.Elapsed, results, notes);
    }

    private async Task CopyAsync(SqliteConnection source, SqlConnection target, string table, IReadOnlyList<TargetColumn> cols, long sourceRows,
        int batchSize, CancellationToken ct)
    {
        // No KeepNulls: a column absent from an older source gets its default (Plugs.Protocol = 'legacy'), as the plan assumes.
        using var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = Quote(table),
            BatchSize = batchSize,
            BulkCopyTimeout = 0,
        };
        var data = new DataTable(table);
        var convert = new Func<object, object>[cols.Count];
        for (var i = 0; i < cols.Count; i++)
        {
            data.Columns.Add(cols[i].Name, ClrType(cols[i].Type)).AllowDBNull = true;
            bulk.ColumnMappings.Add(cols[i].Name, cols[i].Name);
            convert[i] = Converter(table, cols[i]);
        }

        await using var cmd = source.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(c => "\"" + c.Name + "\""))} FROM \"{table}\"";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        long done = 0;
        var values = new object[cols.Count];
        while (await reader.ReadAsync(ct))
        {
            for (var i = 0; i < cols.Count; i++)
            {
                var v = reader.GetValue(i);
                values[i] = v is DBNull ? DBNull.Value : convert[i](v);
            }
            data.Rows.Add(values);
            if (data.Rows.Count >= batchSize)
            {
                await bulk.WriteToServerAsync(data, ct);
                done += data.Rows.Count;
                data.Clear();
                if (done % (batchSize * 40) == 0) _log($"{table}: {done:N0} / {sourceRows:N0}");
            }
        }
        if (data.Rows.Count > 0) await bulk.WriteToServerAsync(data, ct);
    }

    /// <summary>
    /// One check per column, computed on both sides: numeric columns by their sum; texts by the sum of their lengths (UTF-16
    /// units, so a cut or a lost character shows); GUIDs by the number of distinct values (a wrong conversion merges or
    /// splits them); dates by the sum of their UTC instants in microseconds.
    /// </summary>
    private static async Task<List<ColumnSum>> CheckAsync(SqliteConnection source, SqlConnection target, string table, IReadOnlyList<TargetColumn> cols,
        CancellationToken ct)
    {
        var numeric = cols.Where(c => IsNumeric(c.Type)).ToList();
        var others = cols.Where(c => !IsNumeric(c.Type) && CheckKind(c.Type) is not null).ToList();
        var result = new List<ColumnSum>();
        if (numeric.Count > 0)
        {
            await using var s = source.CreateCommand();
            s.CommandText = $"SELECT {string.Join(", ", numeric.Select(c => $"TOTAL(CAST(\"{c.Name}\" AS REAL))"))} FROM \"{table}\"";
            var src = new double[numeric.Count];
            await using (var r = await s.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct))
                    for (var i = 0; i < numeric.Count; i++) src[i] = r.GetDouble(i);
            var dst = await TargetAggregatesAsync(target, table, numeric.Select(c => $"COALESCE(SUM(CAST({Quote(c.Name)} AS float)), 0)"), ct);
            result.AddRange(numeric.Select((c, i) => new ColumnSum(c.Name, src[i], dst[i])));
        }
        if (others.Count > 0)
        {
            // The source side in C#, through the same conversion as the copy.
            var convert = others.Select(c => Converter(table, c)).ToArray();
            var sums = new double[others.Count];
            var distinct = others.Select(_ => new HashSet<Guid>()).ToArray();
            await using (var s = source.CreateCommand())
            {
                s.CommandText = $"SELECT {string.Join(", ", others.Select(c => "\"" + c.Name + "\""))} FROM \"{table}\"";
                await using var r = await s.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    for (var i = 0; i < others.Count; i++)
                    {
                        if (r.IsDBNull(i)) continue;
                        switch (convert[i](r.GetValue(i)))
                        {
                            case string text: sums[i] += text.Length; break;
                            case Guid g: distinct[i].Add(g); break;
                            case DateTimeOffset d: sums[i] += d.UtcTicks / 10; break;
                            case DateTime d: sums[i] += d.Ticks / 10; break;
                        }
                    }
            }
            var dst = await TargetAggregatesAsync(target, table, others.Select(c => CheckKind(c.Type) switch
            {
                "len" => c.Type is "nvarchar" or "nchar" or "ntext"
                    ? $"COALESCE(SUM(CAST(DATALENGTH({Quote(c.Name)}) AS bigint)), 0) / 2"
                    : $"COALESCE(SUM(CAST(DATALENGTH({Quote(c.Name)}) AS bigint)), 0)",
                "distinct" => $"COUNT(DISTINCT {Quote(c.Name)})",
                _ => c.Type == "datetimeoffset"
                    ? $"COALESCE(SUM(CAST(DATEDIFF_BIG(microsecond, CAST('0001-01-01' AS datetime2), CAST(SWITCHOFFSET({Quote(c.Name)}, '+00:00') AS datetime2)) AS float)), 0)"
                    : $"COALESCE(SUM(CAST(DATEDIFF_BIG(microsecond, CAST('0001-01-01' AS datetime2), CAST({Quote(c.Name)} AS datetime2)) AS float)), 0)",
            }), ct);
            result.AddRange(others.Select((c, i) => new ColumnSum($"{CheckKind(c.Type)}({c.Name})",
                CheckKind(c.Type) == "distinct" ? distinct[i].Count : sums[i], dst[i])));
        }
        return result;
    }

    /// <summary>How a non-numeric column is checked: len (texts), distinct (GUIDs), time (dates); null = not checked (binary).</summary>
    private static string? CheckKind(string sqlType) => sqlType switch
    {
        "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" => "len",
        "uniqueidentifier" => "distinct",
        "datetimeoffset" or "datetime2" or "datetime" or "date" or "smalldatetime" => "time",
        _ => null,
    };

    private static async Task<double[]> TargetAggregatesAsync(SqlConnection target, string table, IEnumerable<string> expressions, CancellationToken ct)
    {
        var list = expressions.ToList();
        await using var t = target.CreateCommand();
        t.CommandText = $"SELECT {string.Join(", ", list)} FROM {Quote(table)}";
        t.CommandTimeout = 0;
        var values = new double[list.Count];
        await using var r = await t.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
            for (var i = 0; i < list.Count; i++) values[i] = Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        return values;
    }

    private static Func<object, object> Converter(string table, TargetColumn c)
    {
        var inv = CultureInfo.InvariantCulture;
        return c.Type switch
        {
            "uniqueidentifier" => v => v switch
            {
                string s => Guid.Parse(s),
                byte[] { Length: 16 } b => new Guid(b),
                _ => throw new MigrationException($"{table}.{c.Name}: not a GUID ({v.GetType().Name})"),
            },
            "datetimeoffset" => v => v switch
            {
                string s => DateTimeOffset.Parse(s, inv, DateTimeStyles.AssumeUniversal),
                _ => throw new MigrationException($"{table}.{c.Name}: not a date ({v.GetType().Name})"),
            },
            "datetime2" or "datetime" or "date" or "smalldatetime" => v => v switch
            {
                string s => DateTime.Parse(s, inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                _ => throw new MigrationException($"{table}.{c.Name}: not a date ({v.GetType().Name})"),
            },
            "bit" => v => Convert.ToInt64(v, inv) != 0,
            "bigint" => v => Convert.ToInt64(v, inv),
            "int" => v => Convert.ToInt32(v, inv),
            "smallint" => v => Convert.ToInt16(v, inv),
            "tinyint" => v => Convert.ToByte(v, inv),
            "float" => v => Convert.ToDouble(v, inv),
            "real" => v => Convert.ToSingle(v, inv),
            "decimal" or "numeric" or "money" or "smallmoney" => v => v is string s
                ? decimal.Parse(s, NumberStyles.Float, inv)
                : Convert.ToDecimal(v, inv),
            "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" => v =>
            {
                var s = v as string ?? Convert.ToString(v, inv) ?? "";
                if (c.MaxChars > 0 && s.Length > c.MaxChars)
                    throw new MigrationException($"{table}.{c.Name}: a value of {s.Length} characters does not fit nvarchar({c.MaxChars})");
                return s;
            },
            "varbinary" or "binary" => v => v as byte[] ?? Encoding.UTF8.GetBytes(Convert.ToString(v, inv) ?? ""),
            _ => throw new MigrationException($"{table}.{c.Name}: the target type {c.Type} is not supported by the migration tool"),
        };
    }

    private static Type ClrType(string sqlType) => sqlType switch
    {
        "uniqueidentifier" => typeof(Guid),
        "datetimeoffset" => typeof(DateTimeOffset),
        "datetime2" or "datetime" or "date" or "smalldatetime" => typeof(DateTime),
        "bit" => typeof(bool),
        "bigint" => typeof(long),
        "int" => typeof(int),
        "smallint" => typeof(short),
        "tinyint" => typeof(byte),
        "float" => typeof(double),
        "real" => typeof(float),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "varbinary" or "binary" => typeof(byte[]),
        _ => typeof(string),
    };

    private static bool IsNumeric(string sqlType) =>
        sqlType is "bit" or "bigint" or "int" or "smallint" or "tinyint" or "float" or "real" or "decimal" or "numeric" or "money" or "smallmoney";

    private static async Task<Dictionary<string, List<string>>> SourceTablesAsync(SqliteConnection source, CancellationToken ct)
    {
        var tables = new List<string>();
        await using (var cmd = source.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) tables.Add(r.GetString(0));
        }
        if (!tables.Contains("__EFMigrationsHistory", StringComparer.OrdinalIgnoreCase))
            throw new MigrationException("The source is not a KasaMonitor database (no __EFMigrationsHistory).");
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables.Where(t => !Ignored.Contains(t)))
        {
            var cols = new List<string>();
            await using var cmd = source.CreateCommand();
            cmd.CommandText = $"SELECT name FROM pragma_table_info('{t.Replace("'", "''")}')";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) cols.Add(r.GetString(0));
            result[t] = cols;
        }
        return result;
    }

    private static async Task<Dictionary<string, List<TargetColumn>>> TargetTablesAsync(SqlConnection target, CancellationToken ct)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandText = """
            SELECT t.name, c.name, ty.name, c.max_length, c.is_nullable, CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END, c.is_identity
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0 AND c.is_computed = 0 AND SCHEMA_NAME(t.schema_id) = 'dbo'
            ORDER BY t.name, c.column_id
            """;
        var result = new Dictionary<string, List<TargetColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var table = r.GetString(0);
            if (Ignored.Contains(table)) continue;
            var type = r.GetString(2);
            int maxBytes = r.GetInt16(3);
            var maxChars = maxBytes < 0 ? 0 : type is "nvarchar" or "nchar" ? maxBytes / 2 : type is "varchar" or "char" ? maxBytes : 0;
            if (!result.TryGetValue(table, out var cols)) result[table] = cols = [];
            cols.Add(new TargetColumn(r.GetString(1), type, maxChars, r.GetBoolean(4), r.GetInt32(5) == 1, r.GetBoolean(6)));
        }
        return result;
    }

    /// <summary>Referenced tables before the tables that reference them; otherwise by name.</summary>
    private static async Task<List<string>> TableOrderAsync(SqlConnection target, IEnumerable<string> tables, CancellationToken ct)
    {
        var deps = tables.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        await using (var cmd = target.CreateCommand())
        {
            cmd.CommandText = "SELECT OBJECT_NAME(parent_object_id), OBJECT_NAME(referenced_object_id) FROM sys.foreign_keys";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (deps.TryGetValue(r.GetString(0), out var d) && deps.ContainsKey(r.GetString(1)) && !r.GetString(0).Equals(r.GetString(1), StringComparison.OrdinalIgnoreCase))
                    d.Add(r.GetString(1));
        }
        var order = new List<string>();
        var left = new SortedSet<string>(deps.Keys, StringComparer.Ordinal);
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(t => deps[t].All(d => order.Contains(d, StringComparer.OrdinalIgnoreCase)))
                       ?? throw new MigrationException("The foreign keys of the target form a cycle.");
            order.Add(next);
            left.Remove(next);
        }
        return order;
    }

    private static async Task EmptyAsync(SqlConnection target, string table, CancellationToken ct)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(@t)";
        cmd.Parameters.AddWithValue("@t", Quote(table));
        var referenced = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        await ExecAsync(target, referenced ? $"DELETE FROM {Quote(table)}" : $"TRUNCATE TABLE {Quote(table)}", ct);
    }

    private static async Task<long> SourceCountAsync(SqliteConnection source, string table, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<long> TargetCountAsync(SqlConnection target, string table, CancellationToken ct)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandText = $"SELECT COUNT_BIG(*) FROM {Quote(table)}";
        cmd.CommandTimeout = 0;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task ExecAsync(SqlConnection target, string sql, CancellationToken ct)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
}
