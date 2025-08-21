using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class ExtendPerformanceTable4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MarketPrice",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SmoothedBuyingPrice",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SmoothedSellingPrice",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.Sql(
                "DELETE FROM Performance");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarketPrice",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "SmoothedBuyingPrice",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "SmoothedSellingPrice",
                table: "Performance");
        }
    }
}
