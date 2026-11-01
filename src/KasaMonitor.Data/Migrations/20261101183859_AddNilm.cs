using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNilm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NilmIgnored",
                table: "Plugs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ApplianceRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SignatureId = table.Column<long>(type: "INTEGER", nullable: false),
                    StartTs = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTs = table.Column<long>(type: "INTEGER", nullable: true),
                    PowerW = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedWh = table.Column<double>(type: "REAL", nullable: true),
                    StartEventId = table.Column<long>(type: "INTEGER", nullable: false),
                    EndEventId = table.Column<long>(type: "INTEGER", nullable: true),
                    HouseBeforeW = table.Column<double>(type: "REAL", nullable: false),
                    EndReason = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplianceRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplianceSignatures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    MeanDeltaP = table.Column<double>(type: "REAL", nullable: false),
                    MeanDeltaQ = table.Column<double>(type: "REAL", nullable: false),
                    MeanTransient = table.Column<double>(type: "REAL", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeen = table.Column<long>(type: "INTEGER", nullable: true),
                    LabelSource = table.Column<int>(type: "INTEGER", nullable: false),
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplianceSignatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MeterEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    DeltaP = table.Column<double>(type: "REAL", nullable: false),
                    DeltaQ = table.Column<double>(type: "REAL", nullable: false),
                    DeltaPf = table.Column<double>(type: "REAL", nullable: false),
                    TransientRatio = table.Column<double>(type: "REAL", nullable: false),
                    RiseSeconds = table.Column<double>(type: "REAL", nullable: false),
                    PowerBeforeW = table.Column<double>(type: "REAL", nullable: false),
                    SignatureId = table.Column<long>(type: "INTEGER", nullable: true),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    LabelPlugId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplianceRuns_EndTs",
                table: "ApplianceRuns",
                column: "EndTs");

            migrationBuilder.CreateIndex(
                name: "IX_ApplianceRuns_SignatureId",
                table: "ApplianceRuns",
                column: "SignatureId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplianceRuns_StartTs",
                table: "ApplianceRuns",
                column: "StartTs");

            migrationBuilder.CreateIndex(
                name: "IX_MeterEvents_PlugId_Ts",
                table: "MeterEvents",
                columns: new[] { "PlugId", "Ts" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterEvents_SignatureId_Ts",
                table: "MeterEvents",
                columns: new[] { "SignatureId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_MeterEvents_Ts",
                table: "MeterEvents",
                column: "Ts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplianceRuns");

            migrationBuilder.DropTable(
                name: "ApplianceSignatures");

            migrationBuilder.DropTable(
                name: "MeterEvents");

            migrationBuilder.DropColumn(
                name: "NilmIgnored",
                table: "Plugs");
        }
    }
}
