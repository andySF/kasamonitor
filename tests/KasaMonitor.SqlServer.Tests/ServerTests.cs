using FluentAssertions;
using Xunit.Abstractions;

namespace KasaMonitor.SqlServer.Tests;

public class ServerTests(ITestOutputHelper output)
{
    [SqlServerFact]
    public async Task TheServer_IsAtLeast2019_AndTheLoginConnectsWithEncryption()
    {
        var cs = await MsSql.AppConnectionStringAsync();
        var version = await MsSql.ScalarAsync<string>(cs, "SELECT CONVERT(nvarchar(4000), @@VERSION)");
        output.WriteLine($"{MsSql.Image}: {version}");
        (await MsSql.ScalarAsync<int>(cs, "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))")).Should().BeGreaterThanOrEqualTo(15, "SQL Server 2019 or newer");
        // Encrypt is on by default in Microsoft.Data.SqlClient (the container's certificate is self-signed: TrustServerCertificate=True,
        // as in the stack's connection string). Seen from the server, with the app's login connected.
        await using var app = new Microsoft.Data.SqlClient.SqlConnection(cs);
        await app.OpenAsync();
        (await MsSql.ScalarAsync<string>(MsSql.AdminConnectionString, """
            SELECT TOP 1 CONVERT(nvarchar(10), c.encrypt_option) FROM sys.dm_exec_connections c
            JOIN sys.dm_exec_sessions s ON s.session_id = c.session_id WHERE s.login_name = 'kasamonitor'
            """)).Should().Be("TRUE");
    }
}
