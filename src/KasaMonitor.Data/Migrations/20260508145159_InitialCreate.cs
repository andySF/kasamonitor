using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyEnergy",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    EnergyWh = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyEnergy", x => new { x.PlugId, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "MonthlyEnergy",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    EnergyWh = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyEnergy", x => new { x.PlugId, x.Year, x.Month });
                });

            migrationBuilder.CreateTable(
                name: "OneHourAggregates",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BucketStartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PAvgMw = table.Column<long>(type: "INTEGER", nullable: false),
                    PMaxMw = table.Column<long>(type: "INTEGER", nullable: false),
                    EnergyWh = table.Column<long>(type: "INTEGER", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneHourAggregates", x => new { x.PlugId, x.BucketStartUtc });
                });

            migrationBuilder.CreateTable(
                name: "OneMinuteAggregates",
                columns: table => new
                {
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BucketStartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PAvgMw = table.Column<long>(type: "INTEGER", nullable: false),
                    PMaxMw = table.Column<long>(type: "INTEGER", nullable: false),
                    PMinMw = table.Column<long>(type: "INTEGER", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OnlineCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneMinuteAggregates", x => new { x.PlugId, x.BucketStartUtc });
                });

            migrationBuilder.CreateTable(
                name: "Plugs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mac = table.Column<string>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", nullable: false),
                    AliasIsCustom = table.Column<bool>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    HwVer = table.Column<string>(type: "TEXT", nullable: false),
                    SwVer = table.Column<string>(type: "TEXT", nullable: false),
                    OemId = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plugs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Samples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Online = table.Column<bool>(type: "INTEGER", nullable: false),
                    PowerMw = table.Column<long>(type: "INTEGER", nullable: true),
                    VoltageMv = table.Column<long>(type: "INTEGER", nullable: true),
                    CurrentMa = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalWh = table.Column<long>(type: "INTEGER", nullable: true),
                    Rssi = table.Column<int>(type: "INTEGER", nullable: true),
                    RelayOn = table.Column<bool>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Samples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Plugs_Mac",
                table: "Plugs",
                column: "Mac",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_samples_plug_time",
                table: "Samples",
                columns: new[] { "PlugId", "TimestampUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyEnergy");

            migrationBuilder.DropTable(
                name: "MonthlyEnergy");

            migrationBuilder.DropTable(
                name: "OneHourAggregates");

            migrationBuilder.DropTable(
                name: "OneMinuteAggregates");

            migrationBuilder.DropTable(
                name: "Plugs");

            migrationBuilder.DropTable(
                name: "Samples");

            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
