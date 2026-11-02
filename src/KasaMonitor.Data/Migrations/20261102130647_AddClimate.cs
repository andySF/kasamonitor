using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClimate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClimateReadings",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    TemperatureC = table.Column<double>(type: "REAL", nullable: false),
                    HumidityPercent = table.Column<double>(type: "REAL", nullable: true),
                    BatteryPercent = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClimateReadings", x => new { x.SensorId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "ClimateSensors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Room = table.Column<string>(type: "TEXT", nullable: false),
                    IsOutdoor = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    BatteryPercent = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClimateSensors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClimateReadings_Ts",
                table: "ClimateReadings",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_ClimateSensors_Source_ExternalId",
                table: "ClimateSensors",
                columns: new[] { "Source", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClimateReadings");

            migrationBuilder.DropTable(
                name: "ClimateSensors");
        }
    }
}
