using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>
    /// Mode SolarOnly is renamed to HoldReserve (it mostly runs at night). Stored mode names and
    /// reasons follow; the numeric enum values are unchanged.
    /// </summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20261008120000_RenameSolarOnlyToHoldReserve")]
    public partial class RenameSolarOnlyToHoldReserve : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE PlannedQuarters SET PlannedMode = 'HoldReserve' WHERE PlannedMode = 'SolarOnly';");
            migrationBuilder.Sql("UPDATE PlannedActions SET Mode = 'HoldReserve' WHERE Mode = 'SolarOnly';");
            migrationBuilder.Sql("UPDATE ActualQuarters SET StateMachineReason = 'MILP: Hold reserve' WHERE StateMachineReason = 'MILP: Solar only';");
            migrationBuilder.Sql("UPDATE ActualQuarters SET ActualMode = 'HoldReserve' WHERE ActualMode = 'SolarOnly';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE PlannedQuarters SET PlannedMode = 'SolarOnly' WHERE PlannedMode = 'HoldReserve';");
            migrationBuilder.Sql("UPDATE PlannedActions SET Mode = 'SolarOnly' WHERE Mode = 'HoldReserve';");
            migrationBuilder.Sql("UPDATE ActualQuarters SET StateMachineReason = 'MILP: Solar only' WHERE StateMachineReason = 'MILP: Hold reserve';");
            migrationBuilder.Sql("UPDATE ActualQuarters SET ActualMode = 'SolarOnly' WHERE ActualMode = 'HoldReserve';");
        }
    }
}
