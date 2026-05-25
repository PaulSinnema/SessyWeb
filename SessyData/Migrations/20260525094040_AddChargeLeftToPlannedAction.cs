using Microsoft.EntityFrameworkCore.Migrations;
using System.Numerics;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeLeftToPlannedAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove all existing planned action rows — they have no ChargeLeftWh value
            // and would be used as a false reference (0.0) for SOC deviation checks.
            // A fresh plan will be generated on the next cycle.
            migrationBuilder.Sql("DELETE FROM PlannedActions;");

            migrationBuilder.AddColumn<double>(
                name: "ChargeLeftWh",
                table: "PlannedActions",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargeLeftWh",
                table: "PlannedActions");
        }
    }
}
