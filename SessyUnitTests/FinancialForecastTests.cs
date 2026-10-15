using System;
using System.Collections.Generic;
using SessyController.Services;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    public class FinancialForecastTests
    {
        private static Taxes Tariffs(bool netting) => new()
        {
            Time = new DateTime(2026, 1, 1),
            EnergyTax = 0.10,
            ValueAddedTax = 21.0,
            PurchaseCompensation = 0.02,
            ReturnDeliveryCompensation = 0.02,
            TaxReduction = 365.0,
            NetManagementCost = 365.0,
            FixedTransportFee = 0.0,
            CapacityTransportFee = 0.0,
            Netting = netting
        };

        private static readonly List<GridFlow> Flows =
        [
            new(new DateTime(2026, 1, 1, 12, 0, 0), 10.0, 0.0, 0.10),
            new(new DateTime(2026, 1, 1, 18, 0, 0), 0.0, 4.0, 0.30)
        ];

        private static FinancialForecast Run(bool netting)
            => FinancialForecastService.Calculate("t", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), Flows, _ => Tariffs(netting));

        [Fact]
        public void Netting_TaxOnNetAndVatOnExport()
        {
            var f = Run(netting: true);

            Assert.Equal(10.0 * 0.12 * 1.21, f.ImportCostEur, 6);
            Assert.Equal(4.0 * 0.28 * 1.21, f.ExportRevenueEur, 6);
            Assert.Equal(6.0 * 0.10 * 1.21, f.EnergyTaxEur, 6);
            Assert.Equal(1.0 * 1.21, f.FixedCostsEur, 6);
            Assert.Equal(1.0 * 1.21, f.TaxReductionEur, 6);
        }

        [Fact]
        public void ReturnDeliveryCosts_AreACostWhateverTheSign()
        {
            var positive = Run(netting: true);
            var negative = FinancialForecastService.Calculate("t", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), Flows,
                _ => { var t = Tariffs(true); t.ReturnDeliveryCompensation = -0.02; return t; });

            Assert.Equal(positive.ExportRevenueEur, negative.ExportRevenueEur, 6);
        }

        [Fact]
        public void NoNetting_TaxOnImportAndNoVatOnExport()
        {
            var f = Run(netting: false);

            Assert.Equal(4.0 * 0.28, f.ExportRevenueEur, 6);
            Assert.Equal(10.0 * 0.10 * 1.21, f.EnergyTaxEur, 6);
            Assert.True(f.TotalEur > Run(netting: true).TotalEur);
        }
    }
}
