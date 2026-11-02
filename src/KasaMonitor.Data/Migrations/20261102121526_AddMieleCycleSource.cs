using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMieleCycleSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ApiEndTs",
                table: "MieleCycles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ApiEnergyKwh",
                table: "MieleCycles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ApiStartTs",
                table: "MieleCycles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ApiWaterL",
                table: "MieleCycles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnergyProfileJson",
                table: "MieleCycles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "MieleCycles",
                type: "TEXT",
                nullable: false,
                // The cycles stored by 0.8.5 were all seen live.
                defaultValue: "observed");

            migrationBuilder.CreateIndex(
                name: "IX_MieleCycles_DeviceId_ApiStartTs",
                table: "MieleCycles",
                columns: new[] { "DeviceId", "ApiStartTs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MieleCycles_DeviceId_ApiStartTs",
                table: "MieleCycles");

            migrationBuilder.DropColumn(
                name: "ApiEndTs",
                table: "MieleCycles");

            migrationBuilder.DropColumn(
                name: "ApiEnergyKwh",
                table: "MieleCycles");

            migrationBuilder.DropColumn(
                name: "ApiStartTs",
                table: "MieleCycles");

            migrationBuilder.DropColumn(
                name: "ApiWaterL",
                table: "MieleCycles");

            migrationBuilder.DropColumn(
                name: "EnergyProfileJson",
                table: "MieleCycles");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "MieleCycles");
        }
    }
}
