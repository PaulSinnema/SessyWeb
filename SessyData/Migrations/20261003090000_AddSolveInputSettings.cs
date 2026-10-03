using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ModelContext))]
    [Migration("20261003090000_AddSolveInputSettings")]
    public partial class AddSolveInputSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RecordSolveInputs",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SolveInputKeepFiles",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecordSolveInputs",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SolveInputKeepFiles",
                table: "Settings");
        }
    }
}
