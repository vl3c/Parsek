using Xunit;

namespace Parsek.Tests
{
    public class MergeDialogFormatTests
    {
        // ============================================================
        // B1: FormatDuration — all branches
        // ============================================================

        [Theory]
        [InlineData(double.NaN, "0s")]
        [InlineData(double.PositiveInfinity, "0s")]
        [InlineData(-5, "0s")]
        [InlineData(0, "0s")]
        [InlineData(45, "45s")]
        [InlineData(60, "1m")]
        [InlineData(61, "1m 1s")]
        [InlineData(3600, "1h")]
        [InlineData(3661, "1h 1m")]
        [InlineData(86400, "24h")]
        public void FormatDuration_AllBranches(double input, string expected)
        {
            string result = MergeDialog.FormatDuration(input);
            Assert.Equal(expected, result);
        }

    }
}
