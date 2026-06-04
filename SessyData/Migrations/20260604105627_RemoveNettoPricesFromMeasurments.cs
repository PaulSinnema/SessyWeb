using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveNettoPricesFromMeasurments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuyingPriceEur",
                table: "QuarterlyMeasurements");

            migrationBuilder.DropColumn(
                name: "SellingPriceEur",
                table: "QuarterlyMeasurements");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BuyingPriceEur",
                table: "QuarterlyMeasurements",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SellingPriceEur",
                table: "QuarterlyMeasurements",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
