using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class InvestmentGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvestmentGroup",
                table: "Investment",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvestmentGroup",
                table: "Investment");
        }
    }
}
