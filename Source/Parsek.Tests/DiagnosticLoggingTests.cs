using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for diagnostic logging added to the playback lifecycle.
    /// Covers: LogSpawnContext null safety, multi-part crew extraction,
    /// and orbit segment gap detection in synthetic recordings.
    /// </summary>
    [Collection("Sequential")]
    public class DiagnosticLoggingTests
    {
        public DiagnosticLoggingTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region LogSpawnContext Null Safety (Regression: Issue 3)

        [Fact]
        public void LogSpawnContext_NullSnapshot_DoesNotThrow()
        {
            var rec = new RecordingStore.Recording
            {
                VesselName = "Ghost Ship",
                VesselSnapshot = null
            };

            // Should not throw NullReferenceException
            VesselSpawner.LogSpawnContext(rec, double.MaxValue);
        }

        [Fact]
        public void LogSpawnContext_EmptySnapshot_DoesNotThrow()
        {
            var node = new ConfigNode("VESSEL");
            // No PART nodes at all
            var rec = new RecordingStore.Recording
            {
                VesselName = "Empty Pod",
                VesselSnapshot = node
            };

            VesselSpawner.LogSpawnContext(rec, 500.0);
        }

        [Fact]
        public void LogSpawnContext_SnapshotWithNoParts_DoesNotThrow()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("sit", "ORBITING");
            // Has other nodes but no PART
            node.AddNode("ACTIONGROUPS");

            var rec = new RecordingStore.Recording
            {
                VesselName = "No Parts",
                VesselSnapshot = node
            };

            VesselSpawner.LogSpawnContext(rec, 1000.0);
        }

        #endregion

        #region LogSpawnContext Multi-Part Crew Extraction (Regression: Issue 2)

        [Fact]
        public void LogSpawnContext_SinglePartWithCrew_DoesNotThrow()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("sit", "ORBITING");
            var part = node.AddNode("PART");
            part.AddValue("crew", "Jeb Kerman");

            var rec = new RecordingStore.Recording
            {
                VesselName = "Jeb's Rocket",
                VesselSnapshot = node
            };

            VesselSpawner.LogSpawnContext(rec, 250.0);
        }

        [Fact]
        public void LogSpawnContext_MultiPartWithCrew_DoesNotThrow()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("sit", "ORBITING");

            var pod = node.AddNode("PART");
            pod.AddValue("crew", "Jeb Kerman");
            pod.AddValue("crew", "Bill Kerman");

            var lab = node.AddNode("PART");
            lab.AddValue("crew", "Bob Kerman");

            var rec = new RecordingStore.Recording
            {
                VesselName = "Station",
                VesselSnapshot = node
            };

            VesselSpawner.LogSpawnContext(rec, 100.0);
        }

        [Fact]
        public void LogSpawnContext_PartWithNoCrew_DoesNotThrow()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("sit", "LANDED");

            var part = node.AddNode("PART");
            // Probe core — no crew values

            var rec = new RecordingStore.Recording
            {
                VesselName = "Probe",
                VesselSnapshot = node
            };

            VesselSpawner.LogSpawnContext(rec, double.MaxValue);
        }

        #endregion

        #region Orbit Segment Gap Detection (Regression: synthetic recording data)

        [Fact]
        public void Orbit1_AscendPointsCoverGapToOrbitSegment()
        {
            // Regression: Orbit-1 had a 320s gap between last ascent point (UT 4680)
            // and orbit segment start (UT 5000), causing ghost to exit before orbit
            // segment activates. Verify there's no gap.
            var node = SyntheticRecordingTests.Orbit1().Build();

            // Find the last POINT UT
            var points = node.GetNodes("POINT");
            Assert.True(points.Length > 0);
            var lastPointNode = points[points.Length - 1];
            double lastPointUT = double.Parse(lastPointNode.GetValue("ut"),
                System.Globalization.CultureInfo.InvariantCulture);

            // Find the orbit segment start UT
            var segments = node.GetNodes("ORBIT_SEGMENT");
            Assert.Single(segments);
            double segStartUT = double.Parse(segments[0].GetValue("startUT"),
                System.Globalization.CultureInfo.InvariantCulture);

            // The last sampled point should be at or after the orbit segment start,
            // or the gap must be small enough that the ghost doesn't despawn.
            // EndUT is computed from the last point, so if segStartUT > lastPointUT,
            // the ghost exits at lastPointUT and never reaches the orbit segment.
            Assert.True(lastPointUT >= segStartUT,
                $"Gap detected: last point UT={lastPointUT}, orbit segment starts at UT={segStartUT}. " +
                $"Ghost will despawn before orbit segment activates.");
        }

        [Fact]
        public void KscHopper_HasNoOrbitSegmentGap()
        {
            // Ghost-only recording — no orbit segments, no gap possible
            var node = SyntheticRecordingTests.KscHopper().Build();
            Assert.Empty(node.GetNodes("ORBIT_SEGMENT"));
        }

        [Fact]
        public void SuborbitalArc_HasNoOrbitSegmentGap()
        {
            // Ghost-only recording — no orbit segments, no gap possible
            var node = SyntheticRecordingTests.SuborbitalArc().Build();
            Assert.Empty(node.GetNodes("ORBIT_SEGMENT"));
        }

        #endregion
    }
}
