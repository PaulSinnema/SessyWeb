using SessyController.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Pins down the accounting rules of the FIFO cost basis. Two of them were wrong before
    /// v1.0.37 and are the reason this file exists:
    ///
    ///   - the free part of a charge is what did NOT cross the meter, not total solar
    ///     production — solar the house ate itself was never available to the battery;
    ///   - a layer holds stored (DC) energy at cost per stored kWh, so the charge efficiency
    ///     is in it, and the figure comparable with a selling price is stored / dischargeEff.
    ///
    /// Everything here runs against the pure statics, so no database or clock is involved.
    /// </summary>
    public class ChargeCostBasisTests
    {
        private const double ChargeEff = 0.92;
        private const double DischargeEff = 0.92;

        private static readonly DateTime Start = new(2026, 8, 4, 12, 0, 0);

        private static ChargeCostBasisService.ProjectionStep Step(
            int index, double socWh, double surplusWh, double buy) =>
            new(Start.AddMinutes(15 * index), socWh, surplusWh, buy);

        private static IReadOnlyList<ChargeCostBasisService.CostBasisLayerInfo> NoLayers() => [];

        private static ChargeCostBasisService.CostBasisProjection Run(
            IReadOnlyList<ChargeCostBasisService.ProjectionStep> steps,
            double startSocWh,
            IReadOnlyList<ChargeCostBasisService.CostBasisLayerInfo>? seed = null) =>
            ChargeCostBasisService.Project(
                seed ?? NoLayers(), steps, startSocWh, ChargeEff, DischargeEff, Start);

        // ── The grid/free split ──────────────────────────────────────────────

        [Fact]
        public void SplitByMeter_ChargesOnlyWhatCrossedTheMeter()
        {
            // 2 kWh charged while only 0.5 kWh was imported: the other 1.5 kWh was surplus.
            var (freeWh, gridWh) = ChargeCostBasisService.SplitChargeByMeter(2000.0, 500.0);

            Assert.Equal(500.0, gridWh, 6);
            Assert.Equal(1500.0, freeWh, 6);
        }

        [Fact]
        public void SplitByMeter_ImportBeyondTheChargeIsHouseLoad_NotBatteryCost()
        {
            // The house was importing 3 kWh while the battery took 1 kWh. Only the 1 kWh that
            // ended up in the battery may be charged to the battery.
            var (freeWh, gridWh) = ChargeCostBasisService.SplitChargeByMeter(1000.0, 3000.0);

            Assert.Equal(1000.0, gridWh, 6);
            Assert.Equal(0.0, freeWh, 6);
        }

        [Fact]
        public void SplitBySurplus_MirrorsTheMeterSplitOnTheForecastSide()
        {
            var (freeWh, gridWh) = ChargeCostBasisService.SplitChargeBySurplus(2000.0, 1500.0);

            Assert.Equal(1500.0, freeWh, 6);
            Assert.Equal(500.0, gridWh, 6);
        }

        // ── Efficiency conversions ───────────────────────────────────────────

        [Fact]
        public void Stored_DividesTheBuyingPriceByTheChargeEfficiency()
        {
            Assert.Equal(0.20 / ChargeEff, ChargeCostBasisService.Stored(0.20, ChargeEff), 6);
        }

        [Fact]
        public void Delivered_DividesAgainByTheDischargeEfficiency()
        {
            double stored = ChargeCostBasisService.Stored(0.20, ChargeEff);

            // Round trip: what a kWh bought at 0.20 has to fetch back to break even.
            Assert.Equal(0.20 / (ChargeEff * DischargeEff),
                ChargeCostBasisService.Delivered(stored, DischargeEff), 6);
        }

        [Fact]
        public void StoredDrainFor_TakesMoreOutOfStorageThanItDelivers()
        {
            Assert.Equal(1000.0 / DischargeEff,
                ChargeCostBasisService.StoredDrainFor(1000.0, DischargeEff), 6);
        }

        // ── Projection ───────────────────────────────────────────────────────

        [Fact]
        public void Project_ChargeWithoutSurplus_BooksTheGridPriceIncludingChargeLoss()
        {
            // SOC rises by 920 Wh, which took 1000 Wh off the meter at 0.20 EUR/kWh.
            var projection = Run([Step(0, 920.0, surplusWh: 0.0, buy: 0.20)], startSocWh: 0.0);

            var layer = Assert.Single(projection.EndOfHorizonLayers);
            Assert.False(layer.IsSolar);
            Assert.Equal(920.0, layer.Wh, 6);
            Assert.Equal(0.20 / ChargeEff, layer.CostEurPerKWh, 6);
            Assert.Equal(0.20 / (ChargeEff * DischargeEff), layer.CostEurPerKWhDelivered, 6);

            Assert.Equal(1.0, projection.PlannedGridChargeKWh, 6);
            Assert.Equal(0.20, projection.PlannedGridChargeCostEur, 6);
            Assert.Equal(0.20, projection.PlannedAverageBuyEurPerKWh, 6);
        }

        [Fact]
        public void Project_ChargeCoveredBySurplus_IsFreeAndCostsNothingOnTheMeter()
        {
            var projection = Run([Step(0, 920.0, surplusWh: 2000.0, buy: 0.20)], startSocWh: 0.0);

            var layer = Assert.Single(projection.EndOfHorizonLayers);
            Assert.True(layer.IsSolar);
            Assert.Equal(0.0, layer.CostEurPerKWh, 6);
            Assert.Equal(0.0, projection.PlannedGridChargeKWh, 6);
            Assert.Equal(0.0, projection.EndOfHorizonStoredEurPerKWh, 6);
        }

        [Fact]
        public void Project_PartialSurplus_SplitsTheQuarterIntoAFreeAndAPaidLayer()
        {
            // 1000 Wh AC needed to store 920 Wh; 400 Wh of it was surplus.
            var projection = Run([Step(0, 920.0, surplusWh: 400.0, buy: 0.20)], startSocWh: 0.0);

            Assert.Equal(2, projection.EndOfHorizonLayers.Count);

            var free = projection.EndOfHorizonLayers[0];
            var grid = projection.EndOfHorizonLayers[1];

            Assert.True(free.IsSolar);
            Assert.Equal(400.0 * ChargeEff, free.Wh, 6);
            Assert.False(grid.IsSolar);
            Assert.Equal(600.0 * ChargeEff, grid.Wh, 6);

            Assert.Equal(0.6, projection.PlannedGridChargeKWh, 6);
            Assert.Equal(0.6 * 0.20, projection.PlannedGridChargeCostEur, 6);

            // Average over the whole stack: only the paid part carries cost.
            double expectedStored = 0.6 * 0.20 / (920.0 / 1000.0);
            Assert.Equal(expectedStored, projection.EndOfHorizonStoredEurPerKWh, 6);
            Assert.Equal(expectedStored / DischargeEff,
                projection.EndOfHorizonDeliveredEurPerKWh, 6);
        }

        [Fact]
        public void Project_DischargeRemovesTheOldestEnergyFirst()
        {
            // Two charges at different prices, then a discharge that eats exactly the first.
            var projection = Run(
            [
                Step(0, 1000.0, surplusWh: 0.0, buy: 0.10),
                Step(1, 2000.0, surplusWh: 0.0, buy: 0.30),
                Step(2, 1000.0, surplusWh: 0.0, buy: 0.30)
            ], startSocWh: 0.0);

            var layer = Assert.Single(projection.EndOfHorizonLayers);
            Assert.Equal(1000.0, layer.Wh, 6);

            // The cheap layer is gone, so only the 0.30 layer is left.
            Assert.Equal(0.30 / ChargeEff, layer.CostEurPerKWh, 6);
            Assert.Equal(0.30 / ChargeEff, projection.EndOfHorizonStoredEurPerKWh, 6);
        }

        [Fact]
        public void Project_ReportsOneQuarterPerStepInOrder()
        {
            var steps = new[]
            {
                Step(0, 1000.0, 0.0, 0.10),
                Step(1, 2000.0, 0.0, 0.10),
                Step(2, 1500.0, 0.0, 0.10)
            };

            var projection = Run(steps, startSocWh: 0.0);

            Assert.Equal(steps.Length, projection.Quarters.Count);
            for (int i = 0; i < steps.Length; i++)
                Assert.Equal(steps[i].Time, projection.Quarters[i].Time);

            Assert.Equal(1000.0, projection.Quarters[0].TrackedWh, 6);
            Assert.Equal(2000.0, projection.Quarters[1].TrackedWh, 6);
            Assert.Equal(1500.0, projection.Quarters[2].TrackedWh, 6);
        }

        [Fact]
        public void Project_SeedsFromTheMeasuredLayersAndSpendsThemFirst()
        {
            var seed = new[]
            {
                new ChargeCostBasisService.CostBasisLayerInfo(0, 500.0, 0.0, 0.0, true),
                new ChargeCostBasisService.CostBasisLayerInfo(1, 500.0, 0.25, 0.25 / DischargeEff, false)
            };

            // Drain 500 Wh: the free layer is oldest, so the paid one survives.
            var projection = Run([Step(0, 500.0, 0.0, 0.20)], startSocWh: 1000.0, seed: seed);

            var layer = Assert.Single(projection.EndOfHorizonLayers);
            Assert.False(layer.IsSolar);
            Assert.Equal(0.25, layer.CostEurPerKWh, 6);
            Assert.Equal(0.0, projection.PlannedGridChargeKWh, 6);
        }

        [Fact]
        public void Project_DrainingMoreThanIsStored_EmptiesTheStackWithoutThrowing()
        {
            var seed = new[]
            {
                new ChargeCostBasisService.CostBasisLayerInfo(0, 500.0, 0.25, 0.25 / DischargeEff, false)
            };

            var projection = Run([Step(0, 0.0, 0.0, 0.20)], startSocWh: 5000.0, seed: seed);

            Assert.Empty(projection.EndOfHorizonLayers);
            Assert.Equal(0.0, projection.EndOfHorizonStoredEurPerKWh, 6);
            Assert.Equal(0.0, projection.EndOfHorizonDeliveredEurPerKWh, 6);
        }

        [Fact]
        public void Project_FlatSoc_ChangesNothing()
        {
            var seed = new[]
            {
                new ChargeCostBasisService.CostBasisLayerInfo(0, 1000.0, 0.25, 0.25 / DischargeEff, false)
            };

            var projection = Run(
            [
                Step(0, 1000.0, 0.0, 0.20),
                Step(1, 1000.0, 0.0, 0.20)
            ], startSocWh: 1000.0, seed: seed);

            var layer = Assert.Single(projection.EndOfHorizonLayers);
            Assert.Equal(1000.0, layer.Wh, 6);
            Assert.Equal(0.25, projection.EndOfHorizonStoredEurPerKWh, 6);
            Assert.Equal(0.0, projection.PlannedGridChargeKWh, 6);
        }
    }
}
