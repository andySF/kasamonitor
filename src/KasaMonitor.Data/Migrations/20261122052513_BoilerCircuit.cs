using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class BoilerCircuit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CircuitPumpOn",
                table: "HeatingReadings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EcoActive",
                table: "HeatingReadings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EcoReason",
                table: "HeatingReadings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RoomTargetC",
                table: "HeatingReadings",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SupplyTargetC",
                table: "HeatingReadings",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CircuitPumpOn",
                table: "HeatingReadings");

            migrationBuilder.DropColumn(
                name: "EcoActive",
                table: "HeatingReadings");

            migrationBuilder.DropColumn(
                name: "EcoReason",
                table: "HeatingReadings");

            migrationBuilder.DropColumn(
                name: "RoomTargetC",
                table: "HeatingReadings");

            migrationBuilder.DropColumn(
                name: "SupplyTargetC",
                table: "HeatingReadings");
        }
    }
}
