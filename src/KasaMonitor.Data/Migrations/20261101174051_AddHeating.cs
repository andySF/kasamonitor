using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHeating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HeatingDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<string>(type: "TEXT", nullable: false),
                    GatewaySerial = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HeatingEnergyDaily",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<string>(type: "TEXT", nullable: false),
                    GasHeatingMaxM3 = table.Column<double>(type: "REAL", nullable: true),
                    GasHeatingLastM3 = table.Column<double>(type: "REAL", nullable: true),
                    GasDhwMaxM3 = table.Column<double>(type: "REAL", nullable: true),
                    GasDhwLastM3 = table.Column<double>(type: "REAL", nullable: true),
                    HeatHeatingKwh = table.Column<double>(type: "REAL", nullable: true),
                    HeatDhwKwh = table.Column<double>(type: "REAL", nullable: true),
                    PowerHeatingKwh = table.Column<double>(type: "REAL", nullable: true),
                    PowerDhwKwh = table.Column<double>(type: "REAL", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingEnergyDaily", x => new { x.DeviceId, x.Day });
                });

            migrationBuilder.CreateTable(
                name: "HeatingEnergyPeriods",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    Period = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingEnergyPeriods", x => new { x.DeviceId, x.Kind, x.Purpose, x.Period });
                });

            migrationBuilder.CreateTable(
                name: "HeatingRawFeatures",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingRawFeatures", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "HeatingReadings",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    BoilerTempC = table.Column<double>(type: "REAL", nullable: true),
                    BoilerTargetC = table.Column<double>(type: "REAL", nullable: true),
                    SupplyTempC = table.Column<double>(type: "REAL", nullable: true),
                    ReturnTempC = table.Column<double>(type: "REAL", nullable: true),
                    OutsideTempC = table.Column<double>(type: "REAL", nullable: true),
                    RoomTempC = table.Column<double>(type: "REAL", nullable: true),
                    DhwTempC = table.Column<double>(type: "REAL", nullable: true),
                    DhwTargetC = table.Column<double>(type: "REAL", nullable: true),
                    BurnerActive = table.Column<bool>(type: "INTEGER", nullable: true),
                    ModulationPct = table.Column<double>(type: "REAL", nullable: true),
                    PumpPct = table.Column<double>(type: "REAL", nullable: true),
                    PressureBar = table.Column<double>(type: "REAL", nullable: true),
                    BurnerStarts = table.Column<long>(type: "INTEGER", nullable: true),
                    BurnerHours = table.Column<double>(type: "REAL", nullable: true),
                    OperatingMode = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveProgram = table.Column<string>(type: "TEXT", nullable: true),
                    DhwMode = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveError = table.Column<string>(type: "TEXT", nullable: true),
                    StatusCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeatingReadings", x => new { x.DeviceId, x.Ts });
                });

            migrationBuilder.CreateIndex(
                name: "IX_HeatingDevices_Provider_InstallationId_GatewaySerial_DeviceId",
                table: "HeatingDevices",
                columns: new[] { "Provider", "InstallationId", "GatewaySerial", "DeviceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HeatingDevices");

            migrationBuilder.DropTable(
                name: "HeatingEnergyDaily");

            migrationBuilder.DropTable(
                name: "HeatingEnergyPeriods");

            migrationBuilder.DropTable(
                name: "HeatingRawFeatures");

            migrationBuilder.DropTable(
                name: "HeatingReadings");
        }
    }
}
