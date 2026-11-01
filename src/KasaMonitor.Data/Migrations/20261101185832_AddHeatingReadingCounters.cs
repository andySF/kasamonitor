using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHeatingReadingCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "GasDhwTodayM3",
                table: "HeatingReadings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GasHeatingTodayM3",
                table: "HeatingReadings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HeatTodayKwh",
                table: "HeatingReadings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PowerTodayKwh",
                table: "HeatingReadings",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GasDhwTodayM3",
                table: "HeatingReadings");

            migrationBuilder.DropColumn(
                name: "GasHeatingTodayM3",
                table: "HeatingReadings");

            migrationBuilder.DropColumn(
                name: "HeatTodayKwh",
                table: "HeatingReadings");

            migrationBuilder.DropColumn(
                name: "PowerTodayKwh",
                table: "HeatingReadings");
        }
    }
}
