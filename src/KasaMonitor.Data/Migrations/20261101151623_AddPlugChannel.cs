using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KasaMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlugChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Plugs_Mac",
                table: "Plugs");

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Plugs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Plugs_Mac_Channel",
                table: "Plugs",
                columns: new[] { "Mac", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Plugs_Mac_Channel",
                table: "Plugs");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Plugs");

            migrationBuilder.CreateIndex(
                name: "IX_Plugs_Mac",
                table: "Plugs",
                column: "Mac",
                unique: true);
        }
    }
}
