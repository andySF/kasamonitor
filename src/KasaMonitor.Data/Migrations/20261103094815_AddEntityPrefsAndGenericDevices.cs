using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityPrefsAndGenericDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EntityPrefs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Visible = table.Column<bool>(type: "INTEGER", nullable: true),
                    InHistory = table.Column<bool>(type: "INTEGER", nullable: true),
                    InMetrics = table.Column<bool>(type: "INTEGER", nullable: true),
                    NameOverride = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PrecisionOverride = table.Column<int>(type: "INTEGER", nullable: true),
                    ClassOverride = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UnitOverride = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CommandsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityPrefs", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "GenericDevices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Integration = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameIsCustom = table.Column<bool>(type: "INTEGER", nullable: false),
                    Room = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Firmware = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ViaDevice = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AutoCategory = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CategoryOverride = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenericDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GenericEntities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Integration = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Component = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DataType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Class = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClassRaw = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    StateClass = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Precision = table.Column<int>(type: "INTEGER", nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Writable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StateClassInferred = table.Column<bool>(type: "INTEGER", nullable: false),
                    DiscoveryJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsRemoved = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastNumber = table.Column<double>(type: "REAL", nullable: true),
                    LastText = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    LastTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenericEntities", x => x.Id);
                });

            // R6: integer key, WITHOUT ROWID (the table is its primary-key index; EF Core cannot declare it).
            migrationBuilder.Sql(
                "CREATE TABLE \"GenericHourly\" (" +
                "\"EntityId\" INTEGER NOT NULL, \"HourTs\" INTEGER NOT NULL, \"Min\" REAL NULL, \"Avg\" REAL NULL, \"Max\" REAL NULL, " +
                "\"Last\" REAL NULL, \"Count\" INTEGER NOT NULL, \"Delta\" REAL NULL, " +
                "CONSTRAINT \"PK_GenericHourly\" PRIMARY KEY (\"EntityId\", \"HourTs\")) WITHOUT ROWID;");

            migrationBuilder.Sql(
                "CREATE TABLE \"GenericReadings\" (" +
                "\"EntityId\" INTEGER NOT NULL, \"Ts\" INTEGER NOT NULL, \"Value\" REAL NULL, \"Text\" TEXT NULL, " +
                "CONSTRAINT \"PK_GenericReadings\" PRIMARY KEY (\"EntityId\", \"Ts\")) WITHOUT ROWID;");

            migrationBuilder.CreateIndex(
                name: "IX_GenericDevices_Integration_ExternalId",
                table: "GenericDevices",
                columns: new[] { "Integration", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenericEntities_DeviceId",
                table: "GenericEntities",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_GenericEntities_Integration_ExternalId",
                table: "GenericEntities",
                columns: new[] { "Integration", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntityPrefs");

            migrationBuilder.DropTable(
                name: "GenericDevices");

            migrationBuilder.DropTable(
                name: "GenericEntities");

            migrationBuilder.DropTable(
                name: "GenericHourly");

            migrationBuilder.DropTable(
                name: "GenericReadings");
        }
    }
}
