using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the PARSEK_ACTIVE_STANDALONE migration shim (#271 unification).
    /// Old saves may contain a PARSEK_ACTIVE_STANDALONE node from pre-always-tree versions.
    /// TryRestoreActiveStandaloneNode now converts these to a single-node RecordingTree
    /// and routes through the tree restore path.
    ///
    /// Also verifies the fallback pending tree guard (#293).
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
            ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                ParsekScenario.ActiveTreeRestoreMode.None;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                ParsekScenario.ActiveTreeRestoreMode.None;
        }

        // --------- #293: Fallback guard tests ---------

        [Fact]
        public void RestoringActiveTree_Guard_IsStaticField()
        {
            // Verify the guard field exists and defaults to false
            Assert.False(ParsekFlight.restoringActiveTree);
        }

        // --------- Migration shim: TryRestoreActiveStandaloneNode ---------

        [Fact]
        public void TryRestoreActiveStandaloneNode_NoNode_DoesNothing()
        {
            var node = new ConfigNode("SCENARIO");

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.Equal(ParsekScenario.ActiveTreeRestoreMode.None,
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady);
            Assert.Null(RecordingStore.PendingTree);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_ValidNode_CreatesPendingTree()
        {
            var node = BuildStandaloneNode("Kerbal X", 42, 10, 3);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.Equal(ParsekScenario.ActiveTreeRestoreMode.Quickload,
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady);
            Assert.NotNull(RecordingStore.PendingTree);
            Assert.Equal("Kerbal X", RecordingStore.PendingTree.TreeName);
            Assert.Equal(1, RecordingStore.PendingTree.Recordings.Count);

            // The root recording should have the vessel data
            var rootId = RecordingStore.PendingTree.RootRecordingId;
            Assert.NotNull(rootId);
            var rootRec = RecordingStore.PendingTree.Recordings[rootId];
            Assert.Equal(42u, rootRec.VesselPersistentId);
            Assert.Equal("Kerbal X", rootRec.VesselName);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_DeserializesPoints()
        {
            var node = BuildStandaloneNode("Test", 100, 5, 0);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            var rootId = RecordingStore.PendingTree.RootRecordingId;
            Assert.Equal(5, RecordingStore.PendingTree.Recordings[rootId].Points.Count);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_DeserializesPartEvents()
        {
            var node = BuildStandaloneNode("Test", 100, 3, 2);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            var rootId = RecordingStore.PendingTree.RootRecordingId;
            Assert.Equal(2, RecordingStore.PendingTree.Recordings[rootId].PartEvents.Count);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_RestoresStartLocation()
        {
            var node = BuildStandaloneNode("Test", 100, 1, 0,
                startBody: "Kerbin", startBiome: "Shores", startSituation: "Flying",
                launchSite: "Launch Pad");

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            var rootId = RecordingStore.PendingTree.RootRecordingId;
            var rec = RecordingStore.PendingTree.Recordings[rootId];
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

            Assert.Equal("parsek_rw_abc123", ParsekScenario.pendingActiveTreeResumeRewindSave);
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_TreeRestoreScheduled_SkipsStandalone()
        {
            // Simulate tree restore already scheduled
            ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                ParsekScenario.ActiveTreeRestoreMode.Quickload;

            var node = BuildStandaloneNode("Test", 100, 5, 0);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            // Should not create a pending tree (tree restore takes precedence)
            Assert.Null(RecordingStore.PendingTree);
            Assert.Contains(logLines, l => l.Contains("tree takes precedence"));

            // Cleanup
            ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                ParsekScenario.ActiveTreeRestoreMode.None;
        }

        [Fact]
        public void TryRestoreActiveStandaloneNode_LogsMigration()
        {
            var node = BuildStandaloneNode("Flea Rocket", 999, 8, 2);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            Assert.Contains(logLines, l =>
                l.Contains("TryRestoreActiveStandaloneNode") &&
                l.Contains("migrated") &&
                l.Contains("Flea Rocket") &&
                l.Contains("pid=999") &&
                l.Contains("8 points") &&
                l.Contains("2 events"));
        }

        // --------- Serialization round-trip test ---------

        [Fact]
        public void SaveAndRestore_RoundTrip_PreservesTrajectoryData()
        {
            var node = BuildStandaloneNode("RoundTrip Vessel", 12345, 15, 4,
                startBody: "Mun", startBiome: "Highlands",
                startSituation: "SubOrbital", launchSite: "Launch Pad",
                rewindSave: "parsek_rw_test");

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            var tree = RecordingStore.PendingTree;
            Assert.NotNull(tree);
            var rootId = tree.RootRecordingId;
            var rec = tree.Recordings[rootId];
            Assert.Equal(15, rec.Points.Count);
            Assert.Equal(4, rec.PartEvents.Count);
            Assert.Equal("Mun", rec.StartBodyName);
            Assert.Equal("Highlands", rec.StartBiome);
            Assert.Equal("SubOrbital", rec.StartSituation);
            Assert.Equal("Launch Pad", rec.LaunchSiteName);
            Assert.Equal("RoundTrip Vessel", rec.VesselName);
            Assert.Equal(12345u, rec.VesselPersistentId);
            Assert.Equal("parsek_rw_test", ParsekScenario.pendingActiveTreeResumeRewindSave);
        }

        [Fact]
        public void MigratedTree_HasCorrectActiveRecordingId()
        {
            var node = BuildStandaloneNode("Test", 100, 5, 0);

            ParsekScenario.TryRestoreActiveStandaloneNode(node);

            var tree = RecordingStore.PendingTree;
            Assert.NotNull(tree);
            Assert.Equal(tree.RootRecordingId, tree.ActiveRecordingId);
        }

        // --------- Helper ---------

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
