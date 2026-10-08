using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>
    /// The calculated night reserve is gone; FixedNightReservePct is now the minimum reserve.
    /// Whoever used the calculated reserve gets 0 (the planner covers the night itself); a fixed
    /// reserve set by hand is kept. Data only, no schema change.
    /// </summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20261008180000_MinimumReserveReplacesNightReserve")]
    public partial class MinimumReserveReplacesNightReserve : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Settings SET FixedNightReservePct = 0 WHERE UseCalculatedNightReserve = 1;");
            migrationBuilder.Sql("UPDATE Settings SET UseCalculatedNightReserve = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The learned reserve cannot be restored; switching back to it is enough.
            migrationBuilder.Sql("UPDATE Settings SET UseCalculatedNightReserve = 1 WHERE FixedNightReservePct = 0;");
        }
    }
}
