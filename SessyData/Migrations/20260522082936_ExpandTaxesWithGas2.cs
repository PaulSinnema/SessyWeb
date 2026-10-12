using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class ExpandTaxesWithGas2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "GasSupplierMarkupEurPerM3",
                table: "Taxes",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            // Seed: the author's Frank Energie contract, excl. VAT (see README). Return compensation is
            // negative; CapacityTransportFee holds the supplier's fixed fee (5.79/month).
            migrationBuilder.InsertData(
                table: "Taxes",
                columns: new[]
                {
                    "Time", "EnergyTax", "ValueAddedTax", "PurchaseCompensation", "ReturnDeliveryCompensation",
                    "TaxReduction", "NetManagementCost", "FixedTransportFee", "CapacityTransportFee", "Netting",
                    "GasEnergyTaxEurPerM3", "GasValueAddedTaxPct", "GasSupplierMarkupEurPerM3"
                },
                values: new object[,]
                {
                    { "2025-01-01", 0.10154, 21.0, 0.01504, -0.0105, 524.95, 376.6435, 17.9945, 69.48, true, 0.0, 21.0, 0.085 },   // 2025 gas tax not set
                    { "2026-01-01", 0.09161, 21.0, 0.0182, -0.0182, 519.80, 376.6435, 17.9945, 69.48, true, 0.6007, 21.0, 0.085 },
                    { "2027-01-01", 0.09161, 21.0, 0.0182, -0.0182, 519.80, 376.6435, 17.9945, 69.48, false, 0.6007, 21.0, 0.085 }   // 2026 values until the official ones are known
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GasSupplierMarkupEurPerM3",
                table: "Taxes");
        }
    }
}
