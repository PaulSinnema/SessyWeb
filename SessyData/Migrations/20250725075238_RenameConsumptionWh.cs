using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RenameConsumptionWh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ConsumptionKWh",
                table: "Consumption",
                newName: "ConsumptionWh");

            migrationBuilder.Sql("UPDATE Consumption SET ConsumptionWh = 0 WHERE Time > '2025-07-23 17:24:26'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ConsumptionWh",
                table: "Consumption",
                newName: "ConsumptionKWh");
        }
    }
}
