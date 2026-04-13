using Xunit;

namespace Parsek.Tests
{
    public class PositionFormatTests
    {
        // ── Start position ──

        [Fact]
        public void Start_LaunchSiteAndBody()
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
        public void Start_LaunchSiteOnly()
        {
            var rec = new Recording { LaunchSiteName = "Runway" };
            Assert.Equal("Runway", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void Start_Eva_WithParentVessel()
        {
            var rec = new Recording { EvaCrewName = "Jeb" };
            Assert.Equal("EVA from Mun Lander", RecordingsTableUI.FormatStartPosition(rec, "Mun Lander"));
        }

        [Fact]
        public void Start_Eva_NoParent_FallsBackToLocation()
        {
            var rec = new Recording
            {
                EvaCrewName = "Jeb",
                StartBiome = "Highlands",
                StartBodyName = "Mun"
            };
            Assert.Equal("EVA, Highlands, Mun", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void Start_Eva_NoParent_BodyOnly()
        {
            var rec = new Recording
            {
                EvaCrewName = "Jeb",
                StartBodyName = "Kerbin"
            };
            Assert.Equal("EVA, Kerbin", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void Start_SituationBiomeBody()
        {
            var rec = new Recording
            {
                StartSituation = "Flying",
                StartBiome = "Shores",
                StartBodyName = "Kerbin"
            };
            Assert.Equal("Flying, Shores, Kerbin", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void Start_SituationAndBody_NoBiome()
        {
            var rec = new Recording
            {
                StartSituation = "Orbiting",
                StartBodyName = "Mun"
            };
            Assert.Equal("Orbiting, Mun", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void Start_BiomeAndBody_NoSituation()
        {
            var rec = new Recording
            {
                StartBiome = "Highlands",
                StartBodyName = "Mun"
            };
            Assert.Equal("Highlands, Mun", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void Start_BodyOnly()
        {
            var rec = new Recording { StartBodyName = "Kerbin" };
            Assert.Equal("Kerbin", RecordingsTableUI.FormatStartPosition(rec));
        }

        [Fact]
        public void Start_Empty_ReturnsDash()
        {
            Assert.Equal("-", RecordingsTableUI.FormatStartPosition(new Recording()));
        }

        // ── End position ──

        [Fact]
        public void End_Orbiting()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Mun"
            };
            Assert.Equal("Orbiting Mun", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_Orbiting_NoBody()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Orbiting };
            Assert.Equal("Orbiting", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_Docked()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Docked,
                TerminalOrbitBody = "Kerbin"
            };
            Assert.Equal("Docked, Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_Landed_BiomeAndBody()
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
        public void End_Landed_FallsBackToStartBody()
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
        public void End_Splashed_BodyOnly()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Splashed,
                StartBodyName = "Kerbin"
            };
            Assert.Equal("Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_Destroyed()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Destroyed,
                StartBodyName = "Kerbin"
            };
            Assert.Equal("Destroyed, Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_SubOrbital_NoBody()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.SubOrbital };
            Assert.Equal("SubOrbital", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_Boarded_WithParentVessel()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Boarded };
            Assert.Equal("Boarded Mun Lander", RecordingsTableUI.FormatEndPosition(rec, "Mun Lander"));
        }

        [Fact]
        public void End_Boarded_NoParent()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Boarded };
            Assert.Equal("Boarded", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_NoTerminalState_WithSegmentPhase_ShowsBodyPhase()
        {
            var rec = new Recording
            {
                SegmentPhase = "exo",
                SegmentBodyName = "Kerbin"
            };
            Assert.Equal("Kerbin exo", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_NoTerminalState_MixedEvaAtmoSurface_ShowsBodyOnly()
        {
            var rec = new Recording
            {
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
                StartBodyName = "Kerbin",
                EvaCrewName = "Bill Kerman"
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 100,
                endUT = 110,
                frames = new System.Collections.Generic.List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                startUT = 110,
                endUT = 120,
                frames = new System.Collections.Generic.List<TrajectoryPoint>()
            });

            Assert.Equal("Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_NoTerminalState_WithLastPoint_ShowsBody()
        {
            var rec = new Recording();
            rec.Points = new System.Collections.Generic.List<TrajectoryPoint>
            {
                new TrajectoryPoint { bodyName = "Mun" }
            };
            Assert.Equal("Mun", RecordingsTableUI.FormatEndPosition(rec));
        }

        [Fact]
        public void End_NoTerminalState_NoData_ReturnsDash()
        {
            Assert.Equal("-", RecordingsTableUI.FormatEndPosition(new Recording()));
        }

        [Fact]
        public void End_Recovered()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Recovered,
                StartBodyName = "Kerbin"
            };
            Assert.Equal("Recovered, Kerbin", RecordingsTableUI.FormatEndPosition(rec));
        }
    }
}
