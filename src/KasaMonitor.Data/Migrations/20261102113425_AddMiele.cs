using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMiele : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MieleCycles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    StartTs = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunningTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ProgramRaw = table.Column<int>(type: "INTEGER", nullable: true),
                    Program = table.Column<string>(type: "TEXT", nullable: true),
                    PhasesJson = table.Column<string>(type: "TEXT", nullable: false),
                    EnergyKwh = table.Column<double>(type: "REAL", nullable: true),
                    WaterL = table.Column<double>(type: "REAL", nullable: true),
                    PlugEnergyWh = table.Column<double>(type: "REAL", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MieleCycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MieleDevices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TypeRaw = table.Column<int>(type: "INTEGER", nullable: true),
                    TypeText = table.Column<string>(type: "TEXT", nullable: false),
                    TechType = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", nullable: false),
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MieleDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MieleStateSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    StatusRaw = table.Column<int>(type: "INTEGER", nullable: true),
                    StatusText = table.Column<string>(type: "TEXT", nullable: true),
                    ProgramTypeRaw = table.Column<int>(type: "INTEGER", nullable: true),
                    ProgramTypeText = table.Column<string>(type: "TEXT", nullable: true),
                    ProgramIdRaw = table.Column<int>(type: "INTEGER", nullable: true),
                    ProgramIdText = table.Column<string>(type: "TEXT", nullable: true),
                    PhaseRaw = table.Column<int>(type: "INTEGER", nullable: true),
                    PhaseText = table.Column<string>(type: "TEXT", nullable: true),
                    RemainingSec = table.Column<int>(type: "INTEGER", nullable: true),
                    ElapsedSec = table.Column<int>(type: "INTEGER", nullable: true),
                    StartTimeSec = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetTempC = table.Column<double>(type: "REAL", nullable: true),
                    SpinRpm = table.Column<int>(type: "INTEGER", nullable: true),
                    DryingStepRaw = table.Column<int>(type: "INTEGER", nullable: true),
                    DoorOpen = table.Column<bool>(type: "INTEGER", nullable: true),
                    SignalFailure = table.Column<bool>(type: "INTEGER", nullable: true),
                    SignalInfo = table.Column<bool>(type: "INTEGER", nullable: true),
                    EcoEnergyKwh = table.Column<double>(type: "REAL", nullable: true),
                    EcoWaterL = table.Column<double>(type: "REAL", nullable: true),
                    EcoEnergyForecast = table.Column<double>(type: "REAL", nullable: true),
                    EcoWaterForecast = table.Column<double>(type: "REAL", nullable: true),
                    RemoteFullControl = table.Column<bool>(type: "INTEGER", nullable: true),
                    RemoteSmartGrid = table.Column<bool>(type: "INTEGER", nullable: true),
                    RemoteMobileStart = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MieleStateSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MieleCycles_DeviceId_StartTs",
                table: "MieleCycles",
                columns: new[] { "DeviceId", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_MieleCycles_EndTs",
                table: "MieleCycles",
                column: "EndTs");

            migrationBuilder.CreateIndex(
                name: "IX_MieleStateSnapshots_DeviceId_Ts",
                table: "MieleStateSnapshots",
                columns: new[] { "DeviceId", "Ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MieleCycles");

            migrationBuilder.DropTable(
                name: "MieleDevices");

            migrationBuilder.DropTable(
                name: "MieleStateSnapshots");
        }
    }
}
