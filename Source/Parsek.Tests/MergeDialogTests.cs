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

        // ============================================================
        // B2: GetLeafSituationText — all 8 terminal states + fallbacks
        // ============================================================

        [Fact]
        public void GetLeafSituationText_Orbiting_WithBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin"
            };
            Assert.Equal("Orbiting Kerbin", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_Orbiting_NullBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = null
            };
            Assert.Equal("Orbiting unknown", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_Landed_WithPosition()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition { body = "Mun" }
            };
            Assert.Equal("Landed on Mun", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_Landed_NoPosition()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = null
            };
            Assert.Equal("Landed on unknown", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_Splashed_WithPosition()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Splashed,
                TerminalPosition = new SurfacePosition { body = "Kerbin" }
            };
            Assert.Equal("Splashed on Kerbin", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_SubOrbital_WithBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.SubOrbital,
                TerminalOrbitBody = "Kerbin"
            };
            Assert.Equal("Sub-orbital, Kerbin", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_SubOrbital_NullBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.SubOrbital,
                TerminalOrbitBody = null
            };
            Assert.Equal("Sub-orbital, unknown", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_Destroyed()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Destroyed
            };
            Assert.Equal("Destroyed", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_Recovered()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Recovered
            };
            Assert.Equal("Recovered", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_Docked()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Docked
            };
            Assert.Equal("Docked", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_Boarded()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Boarded
            };
            Assert.Equal("Boarded", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_NullTerminal_WithVesselSituation()
        {
            var rec = new Recording
            {
                TerminalStateValue = null,
                VesselSituation = "FLYING"
            };
            Assert.Equal("FLYING", MergeDialog.GetLeafSituationText(rec));
        }

        [Fact]
        public void GetLeafSituationText_NullTerminal_NullVesselSituation()
        {
            var rec = new Recording
            {
                TerminalStateValue = null,
                VesselSituation = null
            };
            Assert.Equal("Unknown", MergeDialog.GetLeafSituationText(rec));
        }
    }
}
