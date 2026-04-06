using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MergeDialogFormatTests : System.IDisposable
    {
        public MergeDialogFormatTests()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekTimeFormat.ResetForTesting();
        }

        // ============================================================
        // B1: FormatDuration — all branches (now delegates to ParsekTimeFormat)
        // ============================================================

        [Theory]
        [InlineData(double.NaN, "0s")]
        [InlineData(double.PositiveInfinity, "0s")]
        [InlineData(-5, "0s")]
        [InlineData(0, "0s")]
        [InlineData(45, "45s")]
        [InlineData(60, "1m 0s")]
        [InlineData(61, "1m 1s")]
        [InlineData(3600, "1h 0m")]
        [InlineData(3661, "1h 1m")]
        [InlineData(86400, "4d")]       // Kerbin: 86400 / 21600 = 4 days
        public void FormatDuration_AllBranches(double input, string expected)
        {
            string result = MergeDialog.FormatDuration(input);
            Assert.Equal(expected, result);
        }

    }
}
