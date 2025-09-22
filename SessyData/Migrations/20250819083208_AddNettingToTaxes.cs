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

            migrationBuilder.InsertData(
               table: "Taxes",
               columns: new[]
               {
                    "Time",
                    "EnergyTax",
                    "ValueAddedTax",
                    "PurchaseCompensation",
                    "ReturnDeliveryCompensation",
                    "TaxReduction",
                    "NetManagementCost",
                    "FixedTransportFee",
                    "CapacityTransportFee",
                    "Netting"
               },
               values: new object[]
              {
                    "2025-01-01",       // Time
                    0.10154,            // EnegyTax
                    21.0,               // ValueAddedTax
                    0.01504,            // PurchaseCompensation
                    0.0105,             // ReturnDeliveryCompensation
                    524.95,             // TaxReduction
                    376.6435,           // NetManagementCost
                    17.9945,            // FixedTransportFee
                    289.08,             // CapacityTransportFee,
                    true                // Netting
              }
           );

            migrationBuilder.InsertData(
               table: "Taxes",
               columns: new[]
               {
                    "Time",
                    "EnergyTax",
                    "ValueAddedTax",
                    "PurchaseCompensation",
                    "ReturnDeliveryCompensation",
                    "TaxReduction",
                    "NetManagementCost",
                    "FixedTransportFee",
                    "CapacityTransportFee",
                    "Netting"
               },
               values: new object[]
              {
                    "2026-01-01",       // Time
                    0.0916,            // EnegyTax
                    21.0,               // ValueAddedTax
                    0.01504,            // PurchaseCompensation
                    0.0105,             // ReturnDeliveryCompensation
                    519.80,             // TaxReduction
                    376.6435,           // NetManagementCost
                    17.9945,            // FixedTransportFee
                    289.08,             // CapacityTransportFee,
                    true                // Netting
              }
           );

            migrationBuilder.InsertData(
               table: "Taxes",
               columns: new[]
               {
                    "Time",
                    "EnergyTax",
                    "ValueAddedTax",
                    "PurchaseCompensation",
                    "ReturnDeliveryCompensation",
                    "TaxReduction",
                    "NetManagementCost",
                    "FixedTransportFee",
                    "CapacityTransportFee",
                    "Netting"
               },
               values: new object[]
              {
                    "2027-01-01",       // Time
                    0.0,                // EnegyTax (to be determined)
                    21.0,               // ValueAddedTax
                    0.01504,            // PurchaseCompensation
                    0.0105,             // ReturnDeliveryCompensation
                    0.0,                // TaxReduction (to be determined)
                    376.6435,           // NetManagementCost
                    17.9945,            // FixedTransportFee
                    289.08,             // CapacityTransportFee,
                    false               // Netting
              }
           );
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
