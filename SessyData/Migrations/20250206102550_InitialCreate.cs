using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessyStatusHistory",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    StatusDetails = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessyStatusHistory", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "SolarHistory",
                columns: table => new
                {
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GlobalRadiation = table.Column<double>(type: "REAL", nullable: false),
                    GeneratedPower = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolarHistory", x => x.Time);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessyStatusHistory");

            migrationBuilder.DropTable(
                name: "SolarHistory");
        }
    }
}
