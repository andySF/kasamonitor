using System.Diagnostics;
using System.Text.RegularExpressions;
using KasaMonitor.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace KasaMonitor.SqlServer.Tests;

/// <summary>
/// One SQL Server container for the whole run (Testcontainers, removed by its reaper at the end), with the database and
/// the login of deploy/sql/create-database.sql. The tests run one after another (no parallelization in this assembly).
/// </summary>
public static partial class MsSql
{
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-latest";
    /// <summary>Another image for the whole suite, e.g. <c>mcr.microsoft.com/mssql/server:2019-CU22-ubuntu-20.04</c> (the production version).</summary>
    public const string ImageVariable = "KASA_SQLSERVER_IMAGE";
    public static string Image => Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } image ? image : DefaultImage;
    public const string Collation = "Latin1_General_100_CS_AS_SC";
    /// <summary>Password of the kasamonitor login inside the throw-away container (not a real secret).</summary>
    public const string AppPassword = "Kasa-Test-Login-2026!";
    /// <summary>Set to <c>off</c> to skip this suite even with Docker running.</summary>
    public const string SwitchVariable = "KASA_SQLSERVER_TESTS";

    private static readonly Lazy<string?> Unavailable = new(ProbeDocker);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MsSqlContainer? _container;
    private static string? _app;

    /// <summary>Why the suite is skipped, or null when Docker answers.</summary>
    public static string? SkipReason => Unavailable.Value;

    /// <summary>sa on master.</summary>
    public static string AdminConnectionString => _container?.GetConnectionString() ?? throw new InvalidOperationException("SQL Server not started");

    /// <summary>The kasamonitor login on its database, migrated (InitialCreate applied once).</summary>
    public static async Task<string> AppConnectionStringAsync()
    {
        await StartAsync();
        return _app!;
    }

    /// <summary>A new empty database with the collation of create-database.sql (sa connection string).</summary>
    public static async Task<string> NewDatabaseAsync(string prefix)
    {
        await StartAsync();
        var name = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 33, 100)];
        await ExecAsync(AdminConnectionString, $"CREATE DATABASE [{name}] COLLATE {Collation}");
        return new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    public static async Task StartAsync()
    {
        if (_app is not null) return;
        await Gate.WaitAsync();
        try
        {
            if (_app is not null) return;
            if (SkipReason is { } why) throw new InvalidOperationException("SQL Server tests need Docker: " + why);
            var container = new MsSqlBuilder(Image).Build();
            await container.StartAsync();
            _container = container;
            await RunScriptAsync(AdminConnectionString, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sql", "create-database.sql")),
                new Dictionary<string, string> { ["PASSWORD"] = AppPassword });
            var app = new SqlConnectionStringBuilder(AdminConnectionString)
            {
                InitialCatalog = "kasamonitor",
                UserID = "kasamonitor",
                Password = AppPassword,
            }.ConnectionString;
            await using (var ctx = Context(app)) await ctx.Database.MigrateAsync();
            _app = app;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static KasaDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<KasaDbContext>().UseKasaSqlServer(connectionString).Options);

    /// <summary>Empties every table of the app database (TRUNCATE: identities start again at 1).</summary>
    public static async Task ResetAsync()
    {
        var cs = await AppConnectionStringAsync();
        await using var ctx = Context(cs);
        var tables = ctx.Model.GetEntityTypes().Select(e => e.GetTableName()!).Distinct();
        await ExecAsync(cs, string.Join(";\n", tables.Select(t => $"TRUNCATE TABLE [{t}]")));
    }

    public static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A sqlcmd script: batches split on GO, <c>$(NAME)</c> variables replaced.</summary>
    public static async Task RunScriptAsync(string connectionString, string script, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (k, v) in variables) script = script.Replace("$(" + k + ")", v.Replace("'", "''"));
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var batch in GoLine().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    private static string? ProbeDocker()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(SwitchVariable), "off", StringComparison.OrdinalIgnoreCase))
            return SwitchVariable + "=off";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            if (!p.WaitForExit(20_000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return "docker info did not answer in 20 s";
            }
            return p.ExitCode == 0 ? null : "docker info failed (exit " + p.ExitCode + ")";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "docker is not installed (" + ex.Message + ")";
        }
    }
}

/// <summary>A test on the SQL Server container, skipped when Docker is not available.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (MsSql.SkipReason is { } why) Skip = "SQL Server tests skipped: " + why;
    }
}
