using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class InvestmentGroup3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "Investment");

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "InvestmentGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "InvestmentGroups");

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "Investment",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
