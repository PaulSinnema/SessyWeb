using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderNameToSolarInverterData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderName",
                table: "SolarInverterData",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE SolarInverterData SET ProviderName = 'SolarEdge' WHERE ProviderName IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderName",
                table: "SolarInverterData");
        }
    }
}
