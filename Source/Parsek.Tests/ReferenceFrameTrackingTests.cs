using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for reference frame tracking in FlightRecorder.
    /// Verifies that physics periods produce ABSOLUTE TrackSections,
    /// on-rails transitions produce ORBITAL_CHECKPOINT TrackSections,
    /// and orbit segments are correctly added to checkpoint lists.
    /// </summary>
    [Collection("Sequential")]
    public class ReferenceFrameTrackingTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly FlightRecorder recorder;

        public ReferenceFrameTrackingTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            recorder = new FlightRecorder();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region Physics period produces ABSOLUTE TrackSection

        [Fact]
        public void PhysicsPeriod_TrackSection_HasAbsoluteReferenceFrame()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 1000.0);
            recorder.CloseCurrentTrackSection(1050.0);

            Assert.Single(recorder.TrackSections);
            Assert.Equal(ReferenceFrame.Absolute, recorder.TrackSections[0].referenceFrame);
        }

        [Fact]
        public void PhysicsPeriod_TrackSection_HasNonNullFramesList()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 1000.0);
            recorder.CloseCurrentTrackSection(1050.0);

            Assert.NotNull(recorder.TrackSections[0].frames);
        }

        [Fact]
        public void PhysicsPeriod_TrackSection_HasEmptyCheckpointsList()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 1000.0);
            recorder.CloseCurrentTrackSection(1050.0);

            Assert.NotNull(recorder.TrackSections[0].checkpoints);
            Assert.Empty(recorder.TrackSections[0].checkpoints);
        }

        #endregion

        #region On-rails transition closes ABSOLUTE, opens ORBITAL_CHECKPOINT

        [Fact]
        public void OnRailsTransition_ClosesAbsoluteSection_OpensOrbitalCheckpointSection()
        {
            // Start physics (ABSOLUTE) section
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, 2000.0);

            // Simulate going on rails: close ABSOLUTE, start ORBITAL_CHECKPOINT
            recorder.CloseCurrentTrackSection(2050.0);
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 2050.0);
            recorder.CloseCurrentTrackSection(2200.0);

            Assert.Equal(2, recorder.TrackSections.Count);

            // First section: ABSOLUTE (physics)
            Assert.Equal(ReferenceFrame.Absolute, recorder.TrackSections[0].referenceFrame);
            Assert.Equal(2000.0, recorder.TrackSections[0].startUT);
            Assert.Equal(2050.0, recorder.TrackSections[0].endUT);

            // Second section: ORBITAL_CHECKPOINT (on rails)
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, recorder.TrackSections[1].referenceFrame);
            Assert.Equal(2050.0, recorder.TrackSections[1].startUT);
            Assert.Equal(2200.0, recorder.TrackSections[1].endUT);
        }

        [Fact]
        public void OnRailsTransition_OrbitalCheckpointSection_PreservesEnvironment()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, 3000.0);
            recorder.CloseCurrentTrackSection(3050.0);
            recorder.StartNewTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.OrbitalCheckpoint, 3050.0);
            recorder.CloseCurrentTrackSection(3100.0);

            Assert.Equal(SegmentEnvironment.ExoPropulsive, recorder.TrackSections[1].environment);
        }

        [Fact]
        public void OnRailsTransition_SectionsAreContiguous()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, 4000.0);
            recorder.CloseCurrentTrackSection(4100.0);
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 4100.0);
            recorder.CloseCurrentTrackSection(4500.0);

            // End of first == start of second
            Assert.Equal(recorder.TrackSections[0].endUT, recorder.TrackSections[1].startUT);
        }

        #endregion

        #region Off-rails transition closes ORBITAL_CHECKPOINT, opens ABSOLUTE

        [Fact]
        public void OffRailsTransition_ClosesOrbitalCheckpoint_OpensAbsolute()
        {
            // Start on-rails section
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 5000.0);

            // Simulate going off rails: close ORBITAL_CHECKPOINT, start ABSOLUTE
            recorder.CloseCurrentTrackSection(5300.0);
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 5300.0);
            recorder.CloseCurrentTrackSection(5400.0);

            Assert.Equal(2, recorder.TrackSections.Count);

            // First section: ORBITAL_CHECKPOINT
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, recorder.TrackSections[0].referenceFrame);
            Assert.Equal(5000.0, recorder.TrackSections[0].startUT);
            Assert.Equal(5300.0, recorder.TrackSections[0].endUT);

            // Second section: ABSOLUTE
            Assert.Equal(ReferenceFrame.Absolute, recorder.TrackSections[1].referenceFrame);
            Assert.Equal(5300.0, recorder.TrackSections[1].startUT);
            Assert.Equal(5400.0, recorder.TrackSections[1].endUT);
        }

        [Fact]
        public void OffRailsTransition_NewAbsoluteSection_HasFramesNotCheckpoints()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 6000.0);
            recorder.CloseCurrentTrackSection(6300.0);
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 6300.0);
            recorder.CloseCurrentTrackSection(6400.0);

            var absSection = recorder.TrackSections[1];
            Assert.NotNull(absSection.frames);
            Assert.NotNull(absSection.checkpoints);
            Assert.Empty(absSection.checkpoints);
        }

        #endregion

        #region Full on/off rails cycle

        [Fact]
        public void FullCycle_Physics_OnRails_Physics_ProducesThreeSections()
        {
            // Phase 1: Physics (ABSOLUTE)
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 10000.0);
            recorder.CloseCurrentTrackSection(10100.0);

            // Phase 2: On rails (ORBITAL_CHECKPOINT)
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 10100.0);
            recorder.CloseCurrentTrackSection(10500.0);

            // Phase 3: Physics again (ABSOLUTE)
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 10500.0);
            recorder.CloseCurrentTrackSection(10600.0);

            Assert.Equal(3, recorder.TrackSections.Count);
            Assert.Equal(ReferenceFrame.Absolute, recorder.TrackSections[0].referenceFrame);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, recorder.TrackSections[1].referenceFrame);
            Assert.Equal(ReferenceFrame.Absolute, recorder.TrackSections[2].referenceFrame);
        }

        [Fact]
        public void FullCycle_AllSections_AreContiguous()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 20000.0);
            recorder.CloseCurrentTrackSection(20100.0);
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 20100.0);
            recorder.CloseCurrentTrackSection(20500.0);
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 20500.0);
            recorder.CloseCurrentTrackSection(20600.0);

            for (int i = 0; i < recorder.TrackSections.Count - 1; i++)
            {
                Assert.Equal(recorder.TrackSections[i].endUT, recorder.TrackSections[i + 1].startUT);
            }
        }

        #endregion

        #region Orbit segment added to ORBITAL_CHECKPOINT checkpoints

        [Fact]
        public void AddOrbitSegmentToCurrentTrackSection_OrbitalCheckpoint_AddsToCheckpoints()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 7000.0);

            var segment = new OrbitSegment
            {
                startUT = 7000.0,
                endUT = 7500.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 0.01
            };

            recorder.AddOrbitSegmentToCurrentTrackSection(segment);
            recorder.CloseCurrentTrackSection(7500.0);

            Assert.Single(recorder.TrackSections);
            Assert.Single(recorder.TrackSections[0].checkpoints);
            Assert.Equal("Kerbin", recorder.TrackSections[0].checkpoints[0].bodyName);
            Assert.Equal(7000.0, recorder.TrackSections[0].checkpoints[0].startUT);
            Assert.Equal(7500.0, recorder.TrackSections[0].checkpoints[0].endUT);
        }

        [Fact]
        public void AddOrbitSegmentToCurrentTrackSection_MultipleSegments_AllAdded()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 8000.0);

            var seg1 = new OrbitSegment { startUT = 8000.0, endUT = 8200.0, bodyName = "Kerbin" };
            var seg2 = new OrbitSegment { startUT = 8200.0, endUT = 8500.0, bodyName = "Mun" };

            recorder.AddOrbitSegmentToCurrentTrackSection(seg1);
            recorder.AddOrbitSegmentToCurrentTrackSection(seg2);
            recorder.CloseCurrentTrackSection(8500.0);

            Assert.Equal(2, recorder.TrackSections[0].checkpoints.Count);
            Assert.Equal("Kerbin", recorder.TrackSections[0].checkpoints[0].bodyName);
            Assert.Equal("Mun", recorder.TrackSections[0].checkpoints[1].bodyName);
        }

        #endregion

        #region Orbit segment NOT added to ABSOLUTE section

        [Fact]
        public void AddOrbitSegmentToCurrentTrackSection_Absolute_DoesNotAdd()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 9000.0);

            var segment = new OrbitSegment
            {
                startUT = 9000.0,
                endUT = 9100.0,
                bodyName = "Kerbin"
            };

            recorder.AddOrbitSegmentToCurrentTrackSection(segment);
            recorder.CloseCurrentTrackSection(9100.0);

            Assert.Single(recorder.TrackSections);
            Assert.Empty(recorder.TrackSections[0].checkpoints);
        }

        [Fact]
        public void AddOrbitSegmentToCurrentTrackSection_NoActiveSection_DoesNotThrow()
        {
            // No section started — should be a no-op
            var segment = new OrbitSegment
            {
                startUT = 1000.0,
                endUT = 1100.0,
                bodyName = "Kerbin"
            };

            // Should not throw
            recorder.AddOrbitSegmentToCurrentTrackSection(segment);

            Assert.Empty(recorder.TrackSections);
        }

        #endregion

        #region Log assertions for reference frame transitions

        [Fact]
        public void ReferenceFrameTransition_OnRails_LogsTransition()
        {
            logLines.Clear();

            // Simulate on-rails transition: close ABSOLUTE, start ORBITAL_CHECKPOINT
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, 11000.0);
            recorder.CloseCurrentTrackSection(11050.0);
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 11050.0);

            // Verify the OrbitalCheckpoint section start was logged
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Recorder]") &&
                l.Contains("TrackSection started") &&
                l.Contains("ref=OrbitalCheckpoint"));
        }

        [Fact]
        public void ReferenceFrameTransition_OffRails_LogsTransition()
        {
            logLines.Clear();

            // Simulate off-rails transition: close ORBITAL_CHECKPOINT, start ABSOLUTE
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 12000.0);
            recorder.CloseCurrentTrackSection(12300.0);
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 12300.0);

            // Verify the Absolute section start was logged
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Recorder]") &&
                l.Contains("TrackSection started") &&
                l.Contains("ref=Absolute"));
        }

        [Fact]
        public void ReferenceFrameTransition_CloseLog_IncludesReferenceFrame()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 13000.0);
            logLines.Clear();
            recorder.CloseCurrentTrackSection(13500.0);

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Recorder]") &&
                l.Contains("TrackSection closed") &&
                l.Contains("ref=OrbitalCheckpoint"));
        }

        [Fact]
        public void ReferenceFrameTransition_CloseLog_IncludesCheckpointCount()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 14000.0);

            var seg = new OrbitSegment { startUT = 14000.0, endUT = 14200.0, bodyName = "Kerbin" };
            recorder.AddOrbitSegmentToCurrentTrackSection(seg);

            logLines.Clear();
            recorder.CloseCurrentTrackSection(14200.0);

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") &&
                l.Contains("[Recorder]") &&
                l.Contains("TrackSection closed") &&
                l.Contains("checkpoints=1"));
        }

        [Fact]
        public void AddOrbitSegmentToCurrentTrackSection_OrbitalCheckpoint_LogsAddition()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, 15000.0);

            logLines.Clear();
            var seg = new OrbitSegment { startUT = 15000.0, endUT = 15500.0, bodyName = "Kerbin" };
            recorder.AddOrbitSegmentToCurrentTrackSection(seg);

            Assert.Contains(logLines, l =>
                l.Contains("[Recorder]") &&
                l.Contains("Orbit segment added to TrackSection checkpoints") &&
                l.Contains("Kerbin"));
        }

        [Fact]
        public void AddOrbitSegmentToCurrentTrackSection_Absolute_DoesNotLog()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 16000.0);

            logLines.Clear();
            var seg = new OrbitSegment { startUT = 16000.0, endUT = 16100.0, bodyName = "Kerbin" };
            recorder.AddOrbitSegmentToCurrentTrackSection(seg);

            Assert.DoesNotContain(logLines, l => l.Contains("Orbit segment added to TrackSection checkpoints"));
        }

        #endregion
    }
}
