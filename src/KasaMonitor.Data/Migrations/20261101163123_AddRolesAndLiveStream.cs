using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRolesAndLiveStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "Plugs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "StreamEnabled",
                table: "Plugs",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // Existing Shelly meter channels (EM Gen3 "S3EM-…", Pro EM / Pro 3EM "SPEM-…", or their app names) become
            // sub-meters (role 2) without the live stream; everything else stays a plug (0) with the stream on.
            // The main meter is chosen by the user in /plugs. New devices get their role from the component type.
            migrationBuilder.Sql(
                "UPDATE \"Plugs\" SET \"Role\" = 2, \"StreamEnabled\" = 0 WHERE \"Protocol\" = 'shelly-rpc' " +
                "AND (\"Model\" LIKE 'S3EM%' OR \"Model\" LIKE 'SPEM%' OR \"HwVer\" IN ('EMG3', 'ProEM', 'Pro3EM'));");

            migrationBuilder.CreateTable(
                name: "LiveMinuteAggregates",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MinuteTs = table.Column<long>(type: "INTEGER", nullable: false),
                    MinW = table.Column<double>(type: "REAL", nullable: false),
                    AvgW = table.Column<double>(type: "REAL", nullable: false),
                    MaxW = table.Column<double>(type: "REAL", nullable: false),
                    AvgVoltageV = table.Column<double>(type: "REAL", nullable: true),
                    EnergyWh = table.Column<double>(type: "REAL", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CoveredSeconds = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveMinuteAggregates", x => new { x.PlugId, x.MinuteTs });
                });

            migrationBuilder.CreateTable(
                name: "LiveSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    PowerW = table.Column<double>(type: "REAL", nullable: false),
                    ApparentVa = table.Column<double>(type: "REAL", nullable: true),
                    VoltageV = table.Column<double>(type: "REAL", nullable: true),
                    CurrentA = table.Column<double>(type: "REAL", nullable: true),
                    Pf = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RelayCommands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    On = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayCommands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Plugs_SingleMainMeter",
                table: "Plugs",
                column: "Role",
                unique: true,
                filter: "\"Role\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSamples_PlugId_Ts",
                table: "LiveSamples",
                columns: new[] { "PlugId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_RelayCommands_PlugId_Ts",
                table: "RelayCommands",
                columns: new[] { "PlugId", "Ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveMinuteAggregates");

            migrationBuilder.DropTable(
                name: "LiveSamples");

            migrationBuilder.DropTable(
                name: "RelayCommands");

            migrationBuilder.DropIndex(
                name: "IX_Plugs_SingleMainMeter",
                table: "Plugs");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Plugs");

            migrationBuilder.DropColumn(
                name: "StreamEnabled",
                table: "Plugs");
        }
    }
}
