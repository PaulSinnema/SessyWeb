using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddNettingToTaxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Netting",
                table: "Taxes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Seed rows moved to ExpandTaxesWithGas2, where the Taxes table is complete.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Netting",
                table: "Taxes");
        }
    }
}
