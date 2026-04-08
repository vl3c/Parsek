using Xunit;

namespace Parsek.Tests
{
    public class PositionFormatTests
    {
        [Fact]
        public void FormatStartPosition_LaunchSiteAndBody()
        {
            var rec = new Recording
            {
                LaunchSiteName = "LaunchPad",
                StartBiome = "Shores",
                StartBodyName = "Kerbin"
            };
            Assert.Equal("LaunchPad, Kerbin", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void FormatStartPosition_LaunchSiteOnly_WhenNoBody()
        {
            var rec = new Recording { LaunchSiteName = "LaunchPad" };
            Assert.Equal("LaunchPad", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void FormatStartPosition_BiomeAndBody_WhenNoLaunchSite()
        {
            var rec = new Recording
            {
                StartBiome = "Highlands",
                StartBodyName = "Mun"
            };
            Assert.Equal("Highlands, Mun", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void FormatStartPosition_BodyOnly_WhenNoBiome()
        {
            var rec = new Recording { StartBodyName = "Kerbin" };
            Assert.Equal("Kerbin", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void FormatStartPosition_Dash_WhenEmpty()
        {
            var rec = new Recording();
            Assert.Equal("-", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_Orbiting_WithBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Mun"
            };
            Assert.Equal("Orbiting Mun", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_Docked_WithBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Docked,
                TerminalOrbitBody = "Kerbin"
            };
            Assert.Equal("Docked, Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_Landed_BiomeAndBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                EndBiome = "Midlands",
                TerminalOrbitBody = "Mun"
            };
            Assert.Equal("Midlands, Mun", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_Splashed_BodyOnly()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Splashed,
                StartBodyName = "Kerbin"
            };
            Assert.Equal("Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_Destroyed_WithBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Destroyed,
                StartBodyName = "Kerbin"
            };
            Assert.Equal("Destroyed, Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_SubOrbital_NoBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.SubOrbital
            };
            Assert.Equal("SubOrbital", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_NoTerminalState_ReturnsDash()
        {
            var rec = new Recording();
            Assert.Equal("-", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_Landed_FallsBackToStartBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                EndBiome = "Shores",
                StartBodyName = "Kerbin"
            };
            Assert.Equal("Shores, Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_Boarded_IgnoresBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Boarded,
                StartBodyName = "Kerbin"
            };
            Assert.Equal("Boarded", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void FormatEndPosition_Orbiting_NoBody()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting
            };
            Assert.Equal("Orbiting", RecordingsTableUI.FormatEndPosition(rec));
        }
    }
}
