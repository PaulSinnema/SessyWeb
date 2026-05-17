using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class InvestmentGroup2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvestmentGroup",
                table: "Investment");

            migrationBuilder.AlterColumn<int>(
                name: "Category",
                table: "Investment",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "InvestmentGroupId",
                table: "Investment",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvestmentGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentGroups_Name",
                table: "InvestmentGroups",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvestmentGroups");

            migrationBuilder.DropColumn(
                name: "InvestmentGroupId",
                table: "Investment");

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "Investment",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "InvestmentGroup",
                table: "Investment",
                type: "TEXT",
                nullable: true);
        }
    }
}
