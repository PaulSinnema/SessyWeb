using SessyController.Services;
using SessyData.Model;

namespace SessyTests.Services
{
    /// <summary>
    /// The meter delta used to exist four times: in EnergyStatisticsService, in
    /// ChargeCostBasisService, in GridPower and once more for month boundaries. Two clamped a
    /// negative delta, one did not, and none of them separated the meters. This is the one
    /// implementation they all use now.
    /// </summary>
    public class QuarterlyFactsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 13, 12, 0, 0);

        private static EnergyHistory Reading(DateTime time, double consumed, double produced, string meter = "1") =>
            new EnergyHistory
            {
                Time = time,
                MeterId = meter,
                ConsumedTariff1 = consumed,
                ConsumedTariff2 = 0.0,
                ProducedTariff1 = produced,
                ProducedTariff2 = 0.0
            };

        // ── GridDelta ────────────────────────────────────────────────────────

        [Fact]
        public void Delta_is_the_difference_between_two_readings()
        {
            var (importWh, exportWh) = QuarterlyFactsService.GridDelta(
                Reading(T0, 1000.0, 200.0),
                Reading(T0.AddMinutes(15), 1500.0, 250.0));

            Assert.Equal(500.0, importWh);
            Assert.Equal(50.0, exportWh);
        }

        [Fact]
        public void Both_tariffs_count_towards_the_same_total()
        {
            var previous = new EnergyHistory { Time = T0, ConsumedTariff1 = 100, ConsumedTariff2 = 200, ProducedTariff1 = 10, ProducedTariff2 = 20 };
            var current = new EnergyHistory { Time = T0.AddMinutes(15), ConsumedTariff1 = 150, ConsumedTariff2 = 260, ProducedTariff1 = 15, ProducedTariff2 = 25 };

            var (importWh, exportWh) = QuarterlyFactsService.GridDelta(previous, current);

            Assert.Equal(110.0, importWh);
            Assert.Equal(10.0, exportWh);
        }

        [Fact]
        public void A_meter_reset_is_not_energy_flowing_backwards()
        {
            // The registers only count up, so a negative delta is a reset or a gap. GridPower used
            // to skip this clamp, which turned a reset into revenue on the financial page.
            var (importWh, exportWh) = QuarterlyFactsService.GridDelta(
                Reading(T0, 5000.0, 900.0),
                Reading(T0.AddMinutes(15), 0.0, 0.0));

            Assert.Equal(0.0, importWh);
            Assert.Equal(0.0, exportWh);
        }

        // ── GridDeltas ───────────────────────────────────────────────────────

        [Fact]
        public void Deltas_are_keyed_on_the_end_of_the_interval()
        {
            // That is the quarter the energy belongs to, and the quarter the measurement carries.
            var deltas = QuarterlyFactsService.GridDeltas(new[]
            {
                Reading(T0, 1000.0, 0.0),
                Reading(T0.AddMinutes(15), 1400.0, 0.0)
            });

            Assert.True(deltas.ContainsKey(T0.AddMinutes(15)));
            Assert.False(deltas.ContainsKey(T0));
            Assert.Equal(400.0, deltas[T0.AddMinutes(15)].importWh);
        }

        [Fact]
        public void The_first_reading_yields_no_delta()
        {
            var deltas = QuarterlyFactsService.GridDeltas(new[] { Reading(T0, 1000.0, 0.0) });

            Assert.Empty(deltas);
        }

        [Fact]
        public void Readings_out_of_order_are_sorted_first()
        {
            var deltas = QuarterlyFactsService.GridDeltas(new[]
            {
                Reading(T0.AddMinutes(15), 1400.0, 0.0),
                Reading(T0, 1000.0, 0.0)
            });

            Assert.Equal(400.0, deltas[T0.AddMinutes(15)].importWh);
        }

        [Fact]
        public void Two_meters_are_differenced_separately_and_then_summed()
        {
            // A second P1 meter has its own register run. Differencing across the two would
            // subtract one meter's total from the other's — the reason this groups by MeterId.
            var deltas = QuarterlyFactsService.GridDeltas(new[]
            {
                Reading(T0, 1000.0, 0.0, "1"),
                Reading(T0.AddMinutes(15), 1400.0, 0.0, "1"),
                Reading(T0, 50_000.0, 0.0, "2"),
                Reading(T0.AddMinutes(15), 50_100.0, 0.0, "2")
            });

            Assert.Single(deltas);
            Assert.Equal(500.0, deltas[T0.AddMinutes(15)].importWh);
        }

        [Fact]
        public void Readings_a_second_apart_still_difference_correctly()
        {
            // EnergyMonitorService writes just after the quarter boundary, so the stored times are
            // not exactly 15 minutes apart. Nothing here assumes they are.
            var deltas = QuarterlyFactsService.GridDeltas(new[]
            {
                Reading(T0.AddSeconds(5), 1000.0, 0.0),
                Reading(T0.AddMinutes(15).AddSeconds(7), 1250.0, 0.0)
            });

            Assert.Equal(250.0, deltas[T0.AddMinutes(15).AddSeconds(7)].importWh);
        }

        [Fact]
        public void A_gap_in_the_history_still_produces_one_interval()
        {
            // Two readings an hour apart: the delta belongs to the later quarter, whole. Nothing
            // here spreads it back over the quarters that were never recorded.
            var deltas = QuarterlyFactsService.GridDeltas(new[]
            {
                Reading(T0, 1000.0, 0.0),
                Reading(T0.AddHours(1), 2000.0, 0.0)
            });

            Assert.Single(deltas);
            Assert.Equal(1000.0, deltas[T0.AddHours(1)].importWh);
        }
    }
}
