using KasaMonitor.Data;
using KasaMonitor.DataMigration;

// kasa-migrate: copies a KasaMonitor SQLite database (kasa.db, up to 0.11) into the SQL Server database of 0.12.0.
// The connection string comes from --target or, better (it holds a password), from ConnectionStrings__Kasa.

const string Usage = """
    Usage: kasa-migrate --source <kasa.db> [--target <connection string>] [--truncate | --resume] [--batch <rows>] [--report <file.md>] [--no-migrate]

      --source      a consistent copy of kasa.db (the nightly backup, no -wal next to it); opened read-only
      --target      SQL Server connection string; default: the environment variable ConnectionStrings__Kasa
      --truncate    empty every table of the target first (the default refuses a target with rows)
      --resume      keep the tables already copied and verified, copy the others again (after a failed run)
      --batch       rows per bulk-copy batch (default 5000)
      --report      also write the report (Markdown) to this file
      --no-migrate  do not apply the EF migrations to the target first

    Exit code: 0 copied and verified, 1 a table differs or failed, 2 refused (usage, schema, non-empty target).
    """;

string? sourcePath = null, target = null, report = null;
var mode = TargetMode.RefuseNonEmpty;
var batch = DataMigrator.DefaultBatchSize;
var migrate = true;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " needs a value");
    try
    {
        switch (args[i])
        {
            case "--source": sourcePath = Next(); break;
            case "--target": target = Next(); break;
            case "--report": report = Next(); break;
            case "--batch": batch = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--truncate": mode = TargetMode.Truncate; break;
            case "--resume": mode = TargetMode.Resume; break;
            case "--no-migrate": migrate = false; break;
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            default: throw new ArgumentException("unknown option " + args[i]);
        }
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException)
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
target ??= Environment.GetEnvironmentVariable("ConnectionStrings__" + KasaDatabase.ConnectionStringName);
if (sourcePath is null || string.IsNullOrWhiteSpace(target))
{
    Console.Error.WriteLine("--source and a target (--target or ConnectionStrings__Kasa) are required.");
    Console.Error.WriteLine(Usage);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try
{
    var result = await new DataMigrator(line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"))
        .RunAsync(new MigrationOptions(sourcePath, target, mode, batch, migrate), cts.Token);
    var md = result.ToMarkdown();
    Console.WriteLine();
    Console.WriteLine(md);
    if (report is not null) await File.WriteAllTextAsync(report, md);
    return result.Ok ? 0 : 1;
}
catch (MigrationException ex)
{
    Console.Error.WriteLine("Refused: " + ex.Message);
    return 2;
}
