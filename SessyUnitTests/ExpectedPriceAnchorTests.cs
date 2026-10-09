using System;
using System.Collections.Generic;
using System.Linq;
using SessyController.Services;
using Xunit;

namespace SessyTests.Services
{
    public class ExpectedPriceAnchorTests
    {
        private static List<double> Flat(double price) => Enumerable.Repeat(price, 96).ToList();

        [Fact]
        public void FirstQuarter_StartsAtLastKnownPrice()
        {
            var result = ExpectedPriceService.AnchorToLastKnown(Flat(0.17), 0.04, 12.0);

            Assert.Equal(0.04, result[0], 6);
        }

        [Fact]
        public void Gap_FadesWithDecayTime()
        {
            var result = ExpectedPriceService.AnchorToLastKnown(Flat(0.17), 0.04, 12.0);

            // After 12 h (quarter 48) one e-fold of the -0.13 gap remains.
            Assert.Equal(0.17 - 0.13 * Math.Exp(-1.0), result[48], 6);
            Assert.True(result[95] > result[48] && result[95] < 0.17);
        }

        [Fact]
        public void ProfileShape_IsKept()
        {
            var profile = Enumerable.Range(0, 96).Select(i => 0.10 + i * 0.001).ToList();

            var result = ExpectedPriceService.AnchorToLastKnown(profile, 0.10, 12.0);

            // Gap is zero at the start, so the profile is returned unchanged.
            for (int i = 0; i < 96; i++)
                Assert.Equal(profile[i], result[i], 9);
        }

        [Fact]
        public void NoLastKnownPrice_ReturnsProfile()
        {
            var result = ExpectedPriceService.AnchorToLastKnown(Flat(0.17), null, 12.0);

            Assert.All(result, p => Assert.Equal(0.17, p, 9));
        }
    }
}
