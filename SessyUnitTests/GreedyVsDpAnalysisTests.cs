using System.Text;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    // TIJDELIJK — vergelijkt greedy vs DP op een opgenomen solve-input. Mag weg na gebruik.
    public class GreedyVsDpAnalysisTests
    {
        private readonly ITestOutputHelper _output;
        public GreedyVsDpAnalysisTests(ITestOutputHelper output) => _output = output;

        private const string InputPath = @"C:\data\exports\solve-input-20261003-201726.json";
        private const string ReportPath = @"C:\data\exports\greedy_vs_dp.txt";

        [Fact]
        public void Compare_greedy_and_dp()
        {
            var input = SolveInputRecorder.Read(InputPath);
            Assert.NotNull(input);

            var greedy = BatteryGreedyPlanner.Solve(input!.PricePoints, input.Spec, input.Options, input.SocBounds);
            var dp = BatteryDpPlanner.Solve(input.PricePoints, input.Spec, input.Options, input.SocBounds);
            Assert.NotNull(greedy);
            Assert.NotNull(dp);

            var sb = new StringBuilder();
            sb.AppendLine($"Greedy obj = {greedy!.ObjectiveEur:F4}   DP obj = {dp!.ObjectiveEur:F4}   delta = {dp.ObjectiveEur - greedy.ObjectiveEur:F4}");

            double gCh = greedy.Plan.Sum(p => p.ChargeKW * 0.25);
            double gDis = greedy.Plan.Sum(p => p.DischargeKW * 0.25);
            double dCh = dp.Plan.Sum(p => p.ChargeKW * 0.25);
            double dDis = dp.Plan.Sum(p => p.DischargeKW * 0.25);
            sb.AppendLine($"Greedy totals: charge {gCh:F2} kWh, discharge {gDis:F2} kWh");
            sb.AppendLine($"DP     totals: charge {dCh:F2} kWh, discharge {dDis:F2} kWh");
            sb.AppendLine();
            sb.AppendLine("idx time        buy   sell  | G.mode        Gch   Gdis  GsocEnd | D.mode        Dch   Ddis  DsocEnd | diff");

            for (int i = 0; i < greedy.Plan.Count; i++)
            {
                var g = greedy.Plan[i];
                var d = dp.Plan[i];
                var pp = input.PricePoints[i];
                bool diff = g.Mode != d.Mode
                    || System.Math.Abs(g.ChargeKW - d.ChargeKW) > 0.05
                    || System.Math.Abs(g.DischargeKW - d.DischargeKW) > 0.05;

                sb.AppendLine(string.Format(
                    "{0,3} {1} {2:F3} {3:F3} | {4,-12} {5,5:F2} {6,5:F2} {7,6:F2} | {8,-12} {9,5:F2} {10,5:F2} {11,6:F2} | {12}",
                    i, g.Start.ToString("MM-dd HH:mm"), pp.BuyEurPerKWh, pp.SellEurPerKWh,
                    g.Mode, g.ChargeKW, g.DischargeKW, g.SocEndKWh,
                    d.Mode, d.ChargeKW, d.DischargeKW, d.SocEndKWh,
                    diff ? "<<<" : ""));
            }

            System.IO.File.WriteAllText(ReportPath, sb.ToString());
            _output.WriteLine(sb.ToString());
        }
    }
}
