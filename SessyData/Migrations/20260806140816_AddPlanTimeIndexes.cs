using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanTimeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PlannedQuarters_Time",
                table: "PlannedQuarters",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedActions_PlanId",
                table: "PlannedActions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedActions_SavedAt",
                table: "PlannedActions",
                column: "SavedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedActions_Time",
                table: "PlannedActions",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_ActualQuarters_Time",
                table: "ActualQuarters",
                column: "Time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlannedQuarters_Time",
                table: "PlannedQuarters");

            migrationBuilder.DropIndex(
                name: "IX_PlannedActions_PlanId",
                table: "PlannedActions");

            migrationBuilder.DropIndex(
                name: "IX_PlannedActions_SavedAt",
                table: "PlannedActions");

            migrationBuilder.DropIndex(
                name: "IX_PlannedActions_Time",
                table: "PlannedActions");

            migrationBuilder.DropIndex(
                name: "IX_ActualQuarters_Time",
                table: "ActualQuarters");
        }
    }
}
