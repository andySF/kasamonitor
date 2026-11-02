using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlugAppliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplianceCategory",
                table: "Plugs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApplianceName",
                table: "Plugs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SingleAppliance",
                table: "Plugs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplianceCategory",
                table: "Plugs");

            migrationBuilder.DropColumn(
                name: "ApplianceName",
                table: "Plugs");

            migrationBuilder.DropColumn(
                name: "SingleAppliance",
                table: "Plugs");
        }
    }
}
