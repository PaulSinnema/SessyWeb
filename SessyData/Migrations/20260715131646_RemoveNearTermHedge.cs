using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveNearTermHedge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NearTermHedgeFraction",
                table: "Settings");

            migrationBuilder.RenameColumn(
                name: "NearTermHedgeHours",
                table: "Settings",
                newName: "FutureValueDiscountPerHour");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FutureValueDiscountPerHour",
                table: "Settings",
                newName: "NearTermHedgeHours");

            migrationBuilder.AddColumn<double>(
                name: "NearTermHedgeFraction",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
