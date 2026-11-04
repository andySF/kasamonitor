using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurationBands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxRunSeconds",
                table: "ApplianceSignatures",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinRunSeconds",
                table: "ApplianceSignatures",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SplitFromId",
                table: "ApplianceSignatures",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxRunSeconds",
                table: "ApplianceSignatures");

            migrationBuilder.DropColumn(
                name: "MinRunSeconds",
                table: "ApplianceSignatures");

            migrationBuilder.DropColumn(
                name: "SplitFromId",
                table: "ApplianceSignatures");
        }
    }
}
