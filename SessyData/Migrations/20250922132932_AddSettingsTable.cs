using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ManualOverride = table.Column<bool>(type: "INTEGER", nullable: false),
                    Hours = table.Column<string>(type: "TEXT", nullable: true),
                    TimeZone = table.Column<string>(type: "TEXT", nullable: true),
                    CycleCost = table.Column<double>(type: "REAL", nullable: true),
                    RequiredHomeEnergy = table.Column<string>(type: "TEXT", nullable: true),
                    NetZeroHomeMinProfit = table.Column<double>(type: "REAL", nullable: true),
                    SolarCorrection = table.Column<double>(type: "REAL", nullable: true),
                    DatabaseBackupDirectory = table.Column<string>(type: "TEXT", nullable: true),
                    SolarSystemShutsDownDuringNegativePrices = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            // Insert default row
            migrationBuilder.InsertData(
                table: "Settings",
                columns: new[]
                {
                    "Id",
                    "ManualOverride",
                    "Hours",
                    "TimeZone",
                    "CycleCost",
                    "RequiredHomeEnergy",
                    "NetZeroHomeMinProfit",
                    "SolarCorrection",
                    "DatabaseBackupDirectory",
                    "SolarSystemShutsDownDuringNegativePrices"
                },
                values: new object[]
                {
                    1,                  // Id (fixed since it's autoincrement, but we want a known row)
                    false,              // ManualOverride
                    null,               // Hours
                    "Europe/Amsterdam", // TimeZone
                    0.08,               // CycleCost
                    null,               // RequiredHomeEnergy
                    0.00,               // NetZeroHomeMinProfit
                    0.0,                // SolarCorrection
                    "/data/backups",    // DatabaseBackupDirectory
                    false               // SolarSystemShutsDownDuringNegativePrices
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
