using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClimateDaily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClimateDaily",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<string>(type: "TEXT", nullable: false),
                    MinC = table.Column<double>(type: "REAL", nullable: true),
                    AvgC = table.Column<double>(type: "REAL", nullable: true),
                    MaxC = table.Column<double>(type: "REAL", nullable: true),
                    MinHumidity = table.Column<double>(type: "REAL", nullable: true),
                    AvgHumidity = table.Column<double>(type: "REAL", nullable: true),
                    MaxHumidity = table.Column<double>(type: "REAL", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClimateDaily", x => new { x.SensorId, x.Day });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClimateDaily");
        }
    }
}
