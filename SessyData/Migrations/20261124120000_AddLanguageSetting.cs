using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>Adds the language of the user interface (Settings.Language), English by default.</summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20261124120000_AddLanguageSetting")]
    public partial class AddLanguageSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Language", table: "Settings", type: "TEXT", nullable: false, defaultValue: "en");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Language", table: "Settings");
        }
    }
}
