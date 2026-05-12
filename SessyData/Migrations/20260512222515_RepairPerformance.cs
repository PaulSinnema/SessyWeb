using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RepairPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReliable",
                table: "Performance",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Explicitly set all existing rows to reliable (1) first.
            // SQLite may not apply the column default to existing rows depending
            // on the EF Core / SQLite version, so we set it explicitly.
            migrationBuilder.Sql("UPDATE Performance SET IsReliable = 1;");

            // ── One-time data repair ────────────────────────────────────────────
            // Mark records from the overheating period as unreliable.
            // During 2025-10-27 to 2026-02-20 the Sessy batteries shut down mid-cycle
            // due to overheating (max charge/discharge set too high). This caused
            // charged energy to be recorded but discharged energy to be cut short,
            // resulting in artificially low round-trip efficiency figures.
            // The Sessy firmware now throttles instead of shutting down, so records
            // after 2026-02-20 are reliable again.
            //
            // REMOVE THIS BLOCK once the database has been repaired.
            migrationBuilder.Sql(@"
                UPDATE Performance
                SET IsReliable = 0
                WHERE Time >= '2025-10-27 00:00:00'
                AND Time <= '2026-02-20 14:00:00';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsReliable",
                table: "Performance");
        }
    }
}
