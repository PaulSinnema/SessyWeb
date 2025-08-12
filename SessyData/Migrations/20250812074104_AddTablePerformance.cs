using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddTablePerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Performance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BuyingPrice = table.Column<double>(type: "REAL", nullable: false),
                    SellingPrice = table.Column<double>(type: "REAL", nullable: false),
                    Profit = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedConsumptionPerQuarterHour = table.Column<double>(type: "REAL", nullable: false),
                    ChargeLeft = table.Column<double>(type: "REAL", nullable: false),
                    ChargeNeeded = table.Column<double>(type: "REAL", nullable: false),
                    Charging = table.Column<bool>(type: "INTEGER", nullable: false),
                    Discharging = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Performance", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Performance");
        }
    }
}
