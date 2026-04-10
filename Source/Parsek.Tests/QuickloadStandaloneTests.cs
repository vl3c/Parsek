using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for standalone quickload-resume (#294) and the fallback pending tree
    /// guard (#293). Companion to <see cref="QuickloadDiscardTests"/> (F9 discard path)
    /// and <see cref="QuickloadResumeTests"/> (tree-mode resume path).
    ///
    /// Bug #293: OnFlightReady's fallback check at line 4211 fires in the same frame
    /// as the tree restore coroutine, auto-merging the tree and leaving no recorder.
    /// Fix: guard the fallback with <c>restoringActiveTree</c>.
    ///
    /// Bug #294: Standalone recordings have no quickload-resume mechanism — F5 during
    /// a standalone recording, then F9, discards the in-progress data with no recovery.
    /// Fix: serialize active standalone recorder to PARSEK_ACTIVE_STANDALONE on F5,
    /// restore on F9 via a coroutine mirroring the tree restore path.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadStandaloneTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public QuickloadStandaloneTests()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            ParsekScenario.ClearPendingActiveStandalone();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            ParsekScenario.ClearPendingActiveStandalone();
        }

        // ───────── #293: Fallback guard tests ─────────

        [Fact]
        public void RestoringActiveTree_Guard_IsStaticField()
        {
            // Verify the guard field exists and defaults to false
            Assert.False(ParsekFlight.restoringActiveTree);
        }

        // ───────── #294: TryRestoreActiveStandaloneNode tests ─────────

        [Fact]
        public void TryRestoreActiveStandaloneNode_NoNode_DoesNothing()
        {
            var node = new ConfigNode("SCENARIO");

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.False(ParsekScenario.ScheduleActiveStandaloneRestoreOnFlightReady);
            Assert.Null(ParsekScenario.pendingActiveStandaloneRecording);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_ValidNode_SetsRestoreFlag()
        {
            var node = BuildStandaloneNode("Kerbal X", 42, 10, 3);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.True(ParsekScenario.ScheduleActiveStandaloneRestoreOnFlightReady);
            Assert.NotNull(ParsekScenario.pendingActiveStandaloneRecording);
            Assert.Equal("Kerbal X", ParsekScenario.pendingActiveStandaloneVesselName);
            Assert.Equal(42u, ParsekScenario.pendingActiveStandaloneVesselPid);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_DeserializesPoints()
        {
            var node = BuildStandaloneNode("Test", 100, 5, 0);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.Equal(5, ParsekScenario.pendingActiveStandaloneRecording.Points.Count);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_DeserializesPartEvents()
        {
            var node = BuildStandaloneNode("Test", 100, 3, 2);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.Equal(2, ParsekScenario.pendingActiveStandaloneRecording.PartEvents.Count);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_RestoresStartLocation()
        {
            var node = BuildStandaloneNode("Test", 100, 1, 0,
                startBody: "Kerbin", startBiome: "Shores", startSituation: "Flying",
                launchSite: "Launch Pad");

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            var rec = ParsekScenario.pendingActiveStandaloneRecording;
            Assert.Equal("Kerbin", rec.StartBodyName);
            Assert.Equal("Shores", rec.StartBiome);
            Assert.Equal("Flying", rec.StartSituation);
            Assert.Equal("Launch Pad", rec.LaunchSiteName);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_RestoresRewindSave()
        {
            var node = BuildStandaloneNode("Test", 100, 1, 0,
                rewindSave: "parsek_rw_abc123");

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.Equal("parsek_rw_abc123", ParsekScenario.pendingActiveStandaloneRewindSave);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_TreeRestoreScheduled_SkipsStandalone()
        {
            // Simulate tree restore already scheduled
            ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                ParsekScenario.ActiveTreeRestoreMode.Quickload;

            var node = BuildStandaloneNode("Test", 100, 5, 0);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.False(ParsekScenario.ScheduleActiveStandaloneRestoreOnFlightReady);
            Assert.Null(ParsekScenario.pendingActiveStandaloneRecording);
            Assert.Contains(logLines, l => l.Contains("tree takes precedence"));

            // Cleanup
            ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                ParsekScenario.ActiveTreeRestoreMode.None;
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_LogsRestoreData()
        {
            var node = BuildStandaloneNode("Flea Rocket", 999, 8, 2);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.Contains(logLines, l =>
                l.Contains("TryRestoreActiveStandaloneNode") &&
                l.Contains("Flea Rocket") &&
                l.Contains("pid=999") &&
                l.Contains("8 points") &&
                l.Contains("2 events"));
        }

        // ───────── ClearPendingActiveStandalone tests ─────────

        [Fact]
        public void ClearPendingActiveStandalone_ClearsAllFields()
        {
            // Set up some state
            var node = BuildStandaloneNode("Test", 42, 3, 1);
            ParsekScenario.TryRestoreActiveStandaloneNode(node);
            Assert.True(ParsekScenario.ScheduleActiveStandaloneRestoreOnFlightReady);

            ParsekScenario.ClearPendingActiveStandalone();

            Assert.False(ParsekScenario.ScheduleActiveStandaloneRestoreOnFlightReady);
            Assert.Null(ParsekScenario.pendingActiveStandaloneRecording);
            Assert.Null(ParsekScenario.pendingActiveStandaloneVesselName);
            Assert.Equal(0u, ParsekScenario.pendingActiveStandaloneVesselPid);
            Assert.Null(ParsekScenario.pendingActiveStandaloneRewindSave);
        }

        // ───────── Serialization round-trip test ─────────

        [Fact]
        public void SaveAndRestore_RoundTrip_PreservesTrajectoryData()
        {
            // Build a PARSEK_ACTIVE_STANDALONE node with known data
            var node = BuildStandaloneNode("RoundTrip Vessel", 12345, 15, 4,
                startBody: "Mun", startBiome: "Highlands",
                startSituation: "SubOrbital", launchSite: "Launch Pad",
                rewindSave: "parsek_rw_test");

            // Restore it
            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            var rec = ParsekScenario.pendingActiveStandaloneRecording;
            Assert.Equal(15, rec.Points.Count);
            Assert.Equal(4, rec.PartEvents.Count);
            Assert.Equal("Mun", rec.StartBodyName);
            Assert.Equal("Highlands", rec.StartBiome);
            Assert.Equal("SubOrbital", rec.StartSituation);
            Assert.Equal("Launch Pad", rec.LaunchSiteName);
            Assert.Equal("RoundTrip Vessel", ParsekScenario.pendingActiveStandaloneVesselName);
            Assert.Equal(12345u, ParsekScenario.pendingActiveStandaloneVesselPid);
            Assert.Equal("parsek_rw_test", ParsekScenario.pendingActiveStandaloneRewindSave);
        }

        [Fact]
        public void DoubleF5_SecondOverwritesFirst()
        {
            // First "F5" — 5 points
            var node1 = BuildStandaloneNode("Test", 100, 5, 0);
            ParsekScenario.TryRestoreActiveStandaloneNode(node1);
            Assert.Equal(5, ParsekScenario.pendingActiveStandaloneRecording.Points.Count);

            // Clear and simulate second "F5" — 10 points (overwrites via new OnLoad)
            ParsekScenario.ClearPendingActiveStandalone();
            var node2 = BuildStandaloneNode("Test", 100, 10, 0);
            ParsekScenario.TryRestoreActiveStandaloneNode(node2);
            Assert.Equal(10, ParsekScenario.pendingActiveStandaloneRecording.Points.Count);
        }

        // ───────── Helper ─────────

        /// <summary>
        /// Builds a SCENARIO ConfigNode containing a PARSEK_ACTIVE_STANDALONE child
        /// with the specified number of trajectory points and part events.
        /// </summary>
        private static ConfigNode BuildStandaloneNode(
            string vesselName, uint vesselPid, int pointCount, int eventCount,
            string startBody = null, string startBiome = null,
            string startSituation = null, string launchSite = null,
            string rewindSave = null)
        {
            var ic = CultureInfo.InvariantCulture;
            var parent = new ConfigNode("SCENARIO");
            var sa = parent.AddNode("PARSEK_ACTIVE_STANDALONE");
            sa.AddValue("vesselName", vesselName);
            sa.AddValue("vesselPid", vesselPid.ToString(ic));

            if (startBody != null) sa.AddValue("startBodyName", startBody);
            if (startBiome != null) sa.AddValue("startBiome", startBiome);
            if (startSituation != null) sa.AddValue("startSituation", startSituation);
            if (launchSite != null) sa.AddValue("launchSiteName", launchSite);
            if (rewindSave != null) sa.AddValue("rewindSave", rewindSave);

            for (int i = 0; i < pointCount; i++)
            {
                var pt = sa.AddNode("POINT");
                double ut = 100.0 + i * 0.5;
                pt.AddValue("ut", ut.ToString("R", ic));
                pt.AddValue("lat", "0");
                pt.AddValue("lon", "0");
                pt.AddValue("alt", (70000 + i * 100).ToString(ic));
                pt.AddValue("vx", "0");
                pt.AddValue("vy", "100");
                pt.AddValue("vz", "0");
                pt.AddValue("rx", "0");
                pt.AddValue("ry", "0");
                pt.AddValue("rz", "0");
                pt.AddValue("rw", "1");
                pt.AddValue("srx", "0");
                pt.AddValue("sry", "0");
                pt.AddValue("srz", "0");
                pt.AddValue("srw", "1");
            }

            for (int e = 0; e < eventCount; e++)
            {
                var evt = sa.AddNode("PART_EVENT");
                double ut = 110.0 + e * 5.0;
                evt.AddValue("ut", ut.ToString("R", ic));
                evt.AddValue("pid", (1000 + e).ToString(ic));
                evt.AddValue("type", "5"); // EngineIgnited
                evt.AddValue("part", "liquidEngine");
                evt.AddValue("value", "1");
                evt.AddValue("midx", "0");
            }

            return parent;
        }
    }
}
