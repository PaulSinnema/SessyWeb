using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsToDB : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "NetZeroHomeMinProfit",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "CycleCost",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChargedInControl",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "ManualChargingHours",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualDischargingHours",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualNetZeroHomeHours",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SolarAnnualProductionKWh",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatisticsFromDate",
                table: "Settings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargedInControl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ManualChargingHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ManualDischargingHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ManualNetZeroHomeHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SolarAnnualProductionKWh",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "StatisticsFromDate",
                table: "Settings");

            migrationBuilder.AlterColumn<double>(
                name: "NetZeroHomeMinProfit",
                table: "Settings",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<double>(
                name: "CycleCost",
                table: "Settings",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL");
        }
    }
}
