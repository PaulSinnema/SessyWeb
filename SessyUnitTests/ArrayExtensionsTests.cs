using SessyCommon.Extensions;

namespace SessyUnitTests
{
    public class ArrayExtensionsTests
    {
        [Theory]
        // Already on a boundary
        [InlineData("[1.0, 2.0, 3.0]", new double[] {1.0, 2.0, 3.0})]
        [InlineData("[1.5, 2.5, 3.5]", new double[] { 1.5, 2.5, 3.5 })]
        [InlineData("[1.54321, 2.54321, 3.54321]", new double[] { 1.54321, 2.54321, 3.54321 })]
        public void DoubleArrayExpected(string input, double[] expected)
        {
            // Arrange

            // Act
            var actual = input.StringToArray<double>();

            // Assert
            Assert.Equal(expected, actual);
        }

        [Theory]
        // Already on a boundary
        [InlineData(new double[] { 1.0, 2.0, 3.0 }, "[1,2,3]")]
        [InlineData(new double[] { 1.5, 2.5, 3.5 }, "[1.5,2.5,3.5]")]
        [InlineData(new double[] { 1.54321, 2.54321, 3.54321 }, "[1.54321,2.54321,3.54321]")]
        public void StringExpected(double[] input, string expected)
        {
            // Arrange

            // Act
            var actual = input.StringFromArray();

            // Assert
            Assert.Equal(expected, actual);
        }
    }
}
