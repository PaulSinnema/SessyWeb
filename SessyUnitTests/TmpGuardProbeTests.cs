using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using SessyCommon.Enums;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    public class TmpGuardProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";

        [Fact]
        public void Guard_probe()
        {
            var rows = new List<(DateTime Time, double Buy, double Sell, double NetLoadWh, double MinSocWh)>();
            var measurements = new List<QuarterlyMeasurement>();
            using (var conn = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly"))
            {
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT Time, BuyingPriceEurKWh, SellingPriceEurKWh, NetLoadWh, MinSocWh
                                        FROM PlannedQuarters
                                        WHERE Time >= '2026-09-02 10:45:00' AND Time < '2026-09-04 00:00:00'
                                        ORDER BY Time;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        rows.Add((DateTime.Parse(r.GetString(0)), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)));
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT Time, BatteryMode, BatteryPowerWatts, BatteryStateOfChargeWh, IsReliable
                                        FROM QuarterlyMeasurements
                                        WHERE Time >= '2026-08-02' AND Time <= '2026-09-02'
                                        ORDER BY Time;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        measurements.Add(new QuarterlyMeasurement
                        {
                            Time = DateTime.Parse(r.GetString(0)),
                            BatteryMode = (Modes)r.GetInt32(1),
                            BatteryPowerWatts = r.GetDouble(2),
                            BatteryStateOfChargeWh = r.GetDouble(3),
                            IsReliable = r.GetInt32(4) != 0
                        });
                }
            }

            var flat = EfficiencyCurve.Flat(0.9297, 0.9297);
            var curve = BatteryEfficiencyService.Fit(measurements, flat);

            var points = rows.Select(x => new PricePoint(
                x.Time, x.Buy, x.Sell, NetLoadWh: x.NetLoadWh, SolarSurplusWh: 0.0,
                MaxChargeKW: 6.6, MaxDischargeKW: 4.16,
                TemperatureC: null, Temperature48hC: null,
                ReserveOnly: x.Time >= new DateTime(2026, 9, 3))).ToList();

            var bounds = rows.Select(x => new SocBound(x.Time, x.MinSocWh / 1000.0, 16.2)).ToList();

            var spec = new BatterySpec(
                CapacityKWh: 16.2,
                MaxChargeKW: 6.6,
                MaxDischargeKW: 4.16,
                ChargeEfficiency: 0.9297,
                DischargeEfficiency: 0.9297,
                InitialSocKWh: 4.917,
                Efficiency: curve,
                ChargeTaper: null,
                ChargeFloor: null,
                DischargeCapability: null);

            var opt = new SessyOptions(
                QuarterMinutes: 15,
                CycleCostEurPerKWh: 0.08622,
                FutureValueDiscountPerHour: 0.0,
                ReplacementCostEurPerKWh: 0.1299,
                AllowCarryForward: true);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Curve: chCeil={curve.ChargeCeiling:F4} chOv={curve.ChargeOverheadKW:F4} disCeil={curve.DischargeCeiling:F4} disOv={curve.DischargeOverheadKW:F4} samples={curve.Samples}");
            sb.AppendLine($"ChargeAt(6.6)={curve.ChargeAt(6.6):F4} DischargeAt(4.16)={curve.DischargeAt(4.16):F4} roundTrip={curve.ChargeAt(6.6)*curve.DischargeAt(4.16):F4}");

            // Manual Candidate B check for the 13:30 -> 19:30 pair.
            var i = rows.FindIndex(x => x.Time == new DateTime(2026, 9, 2, 13, 30, 0));
            var j = rows.FindIndex(x => x.Time == new DateTime(2026, 9, 2, 19, 30, 0));
            sb.AppendLine($"i={i} (13:30) buy={rows[i].Buy:F4} sell={rows[i].Sell:F4} netload={rows[i].NetLoadWh:F0}");
            sb.AppendLine($"j={j} (19:30) buy={rows[j].Buy:F4} sell={rows[j].Sell:F4} netload={rows[j].NetLoadWh:F0}");

            double costI = rows[i].Buy;
            double valueJ = rows[j].Buy;   // assume avoiding import
            double pairRT = curve.ChargeAt(6.6) * curve.DischargeAt(4.16);
            double profit = valueJ - costI / pairRT - 0.08622;
            sb.AppendLine($"Candidate B profit (valueJ=buy {valueJ:F4}): {profit:F4}");
            double profitSell = rows[j].Sell - costI / pairRT - 0.08622;
            sb.AppendLine($"Candidate B profit (valueJ=sell {rows[j].Sell:F4}): {profitSell:F4}");

            System.IO.File.WriteAllText(@"C:\Projects\Sessy\tmp_guard_probe_output.txt", sb.ToString());
        }
    }
}
