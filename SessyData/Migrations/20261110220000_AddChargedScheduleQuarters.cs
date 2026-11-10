using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>
    /// Adds ChargedScheduleQuarters (Charged's schedule history, changed quarters only) and the
    /// retention setting. Hand-written migration; the target model lives in ModelContextModelSnapshot.
    /// </summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20261110220000_AddChargedScheduleQuarters")]
    public partial class AddChargedScheduleQuarters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChargedScheduleQuarters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PowerW = table.Column<double>(type: "REAL", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargedScheduleQuarters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChargedScheduleQuarters_Time_FetchedAt",
                table: "ChargedScheduleQuarters",
                columns: new[] { "Time", "FetchedAt" });

            migrationBuilder.AddColumn<int>(
                name: "ChargedScheduleKeepDays",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChargedScheduleQuarters");

            migrationBuilder.DropColumn(
                name: "ChargedScheduleKeepDays",
                table: "Settings");
        }
    }
}
