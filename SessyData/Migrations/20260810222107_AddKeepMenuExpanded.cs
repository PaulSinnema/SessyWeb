using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddKeepMenuExpanded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // true, not the scaffolded false: SQLite fills existing rows with this default, and an
            // installation that upgrades should get the new behaviour rather than silently keeping
            // the old one. Switching it off is a deliberate choice on the Settings page.
            migrationBuilder.AddColumn<bool>(
                name: "KeepMenuExpanded",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepMenuExpanded",
                table: "Settings");
        }
    }
}
