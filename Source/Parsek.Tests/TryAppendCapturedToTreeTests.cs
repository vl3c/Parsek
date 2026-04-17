using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="ParsekFlight.TryAppendCapturedToTree"/>.
    /// Bug #297: FallbackCommitSplitRecorder was tree-mode-unaware and orphaned
    /// continuation trajectory data as ungrouped standalone recordings instead of
    /// appending to the active tree recording.
    /// </summary>
    [Collection("Sequential")]
    public class TryAppendCapturedToTreeTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TryAppendCapturedToTreeTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static TrajectoryPoint MakePoint(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 100.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        private static Recording MakeCaptured(double startUT, double endUT, bool destroyed = false, double maxDist = 500.0)
        {
            var rec = new Recording();
            rec.Points.Add(MakePoint(startUT));
            rec.Points.Add(MakePoint((startUT + endUT) / 2));
            rec.Points.Add(MakePoint(endUT));
            rec.VesselDestroyed = destroyed;
            rec.MaxDistanceFromLaunch = maxDist;
            rec.EndBiome = "Grasslands";
            return rec;
        }

        private static RecordingTree MakeTree(string rootId, string activeId = null)
        {
            var tree = new RecordingTree
            {
                Id = "tree-001",
                TreeName = "Test Vessel",
                RootRecordingId = rootId,
                ActiveRecordingId = activeId ?? rootId,
            };
            var rootRec = new Recording { RecordingId = rootId, VesselName = "Test Vessel" };
            rootRec.Points.Add(MakePoint(100.0));
            rootRec.Points.Add(MakePoint(110.0));
            rootRec.EndBiome = "Shores";
            rootRec.MaxDistanceFromLaunch = 200.0;
            tree.Recordings[rootId] = rootRec;
            return tree;
        }

        [Fact]
        public void TreeMode_AppendsToActiveRecording()
        {
            var tree = MakeTree("root-001");
            var captured = MakeCaptured(120.0, 180.0, destroyed: true, maxDist: 5000.0);

            bool result = ParsekFlight.TryAppendCapturedToTree(tree, captured);

            Assert.True(result);
            var rootRec = tree.Recordings["root-001"];
            // Original 2 points + 3 captured points = 5
            Assert.Equal(5, rootRec.Points.Count);
            Assert.Equal(TerminalState.Destroyed, rootRec.TerminalStateValue);
            Assert.True(rootRec.VesselDestroyed);
            Assert.Equal(5000.0, rootRec.MaxDistanceFromLaunch);
            Assert.Equal("Grasslands", rootRec.EndBiome);
            Assert.Equal(180.0, rootRec.ExplicitEndUT);
            Assert.Contains(logLines, l => l.Contains("[Flight]") && l.Contains("TryAppendCapturedToTree: appended 3 points"));
        }

        [Fact]
        public void TreeMode_FallsBackToRoot_WhenActiveIdNull()
        {
            var tree = MakeTree("root-001", activeId: null);
            tree.ActiveRecordingId = null;
            var captured = MakeCaptured(120.0, 180.0);

            bool result = ParsekFlight.TryAppendCapturedToTree(tree, captured);

            Assert.True(result);
            Assert.Equal(5, tree.Recordings["root-001"].Points.Count);
            Assert.Contains(logLines, l => l.Contains("appended 3 points to tree recording 'root-001'"));
        }

        [Fact]
        public void TreeMode_ReturnsFalse_WhenRecordingNotFound()
        {
            var tree = MakeTree("root-001");
            tree.ActiveRecordingId = "nonexistent-id";
            var captured = MakeCaptured(120.0, 180.0);

            bool result = ParsekFlight.TryAppendCapturedToTree(tree, captured);

            Assert.False(result);
            // Root recording should be untouched
            Assert.Equal(2, tree.Recordings["root-001"].Points.Count);
            Assert.Contains(logLines, l => l.Contains("not found in tree"));
        }

        [Fact]
        public void TreeMode_ReturnsFalse_WhenTreeNull()
        {
            var captured = MakeCaptured(120.0, 180.0);

            bool result = ParsekFlight.TryAppendCapturedToTree(null, captured);

            Assert.False(result);
        }

        [Fact]
        public void TreeMode_ReturnsFalse_WhenCapturedTooShort()
        {
            var tree = MakeTree("root-001");
            var captured = new Recording();
            captured.Points.Add(MakePoint(120.0)); // Only 1 point

            bool result = ParsekFlight.TryAppendCapturedToTree(tree, captured);

            Assert.False(result);
            Assert.Equal(2, tree.Recordings["root-001"].Points.Count);
        }

        [Fact]
        public void TreeMode_DoesNotOverwriteSnapshots()
        {
            var tree = MakeTree("root-001");
            var rootRec = tree.Recordings["root-001"];
            var originalVesselSnap = new ConfigNode("VESSEL");
            originalVesselSnap.AddValue("name", "original");
            rootRec.VesselSnapshot = originalVesselSnap;
            var originalGhostSnap = new ConfigNode("GHOST");
            originalGhostSnap.AddValue("name", "original-ghost");
            rootRec.GhostVisualSnapshot = originalGhostSnap;

            var captured = MakeCaptured(120.0, 180.0);
            var capturedSnap = new ConfigNode("VESSEL");
            capturedSnap.AddValue("name", "captured");
            captured.VesselSnapshot = capturedSnap;
            captured.GhostVisualSnapshot = new ConfigNode("GHOST");

            ParsekFlight.TryAppendCapturedToTree(tree, captured);

            // Snapshots must not be overwritten — pre-breakup originals are better for ghost visuals
            Assert.Same(originalVesselSnap, rootRec.VesselSnapshot);
            Assert.Same(originalGhostSnap, rootRec.GhostVisualSnapshot);
        }

        [Fact]
        public void TreeMode_NonDestroyedVessel_DoesNotSetTerminalState()
        {
            var tree = MakeTree("root-001");
            var captured = MakeCaptured(120.0, 180.0, destroyed: false);

            bool result = ParsekFlight.TryAppendCapturedToTree(tree, captured);

            Assert.True(result);
            var rootRec = tree.Recordings["root-001"];
            Assert.Null(rootRec.TerminalStateValue);
            Assert.False(rootRec.VesselDestroyed);
            Assert.Equal(5, rootRec.Points.Count);
        }

        [Fact]
        public void TreeMode_AppendsPartEventsAndOrbitSegments()
        {
            var tree = MakeTree("root-001");
            tree.Recordings["root-001"].PartEvents.Add(new PartEvent
            {
                ut = 105.0, partPersistentId = 100, eventType = PartEventType.EngineIgnited, partName = "engine",
            });

            var captured = MakeCaptured(120.0, 180.0);
            captured.PartEvents.Add(new PartEvent
            {
                ut = 150.0, partPersistentId = 200, eventType = PartEventType.EngineShutdown, partName = "engine",
            });
            captured.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130.0, endUT = 170.0, bodyName = "Kerbin",
            });

            bool result = ParsekFlight.TryAppendCapturedToTree(tree, captured);

            Assert.True(result);
            var rootRec = tree.Recordings["root-001"];
            Assert.Equal(2, rootRec.PartEvents.Count);
            Assert.Equal(105.0, rootRec.PartEvents[0].ut); // sorted
            Assert.Equal(150.0, rootRec.PartEvents[1].ut);
            Assert.Single(rootRec.OrbitSegments);
        }

        [Fact]
        public void TreeMode_PreservesMaxDist_WhenCapturedIsSmaller()
        {
            var tree = MakeTree("root-001");
            tree.Recordings["root-001"].MaxDistanceFromLaunch = 10000.0;
            var captured = MakeCaptured(120.0, 180.0, maxDist: 500.0);

            ParsekFlight.TryAppendCapturedToTree(tree, captured);

            // Should keep the larger existing value
            Assert.Equal(10000.0, tree.Recordings["root-001"].MaxDistanceFromLaunch);
        }

        [Fact]
        public void TreeMode_LiftsCapturedRewindSaveOntoRoot()
        {
            // #416 R-button: on a joint-break crash, the main recorder is moved to
            // pendingSplitRecorder (this.recorder becomes null), so FinalizeTreeRecordings'
            // own CopyRewindSaveToRoot no-ops. TryAppendCapturedToTree is the remaining
            // bridge — the captured rewind save + reserved/pre-launch budget must land on
            // the root recording or the committed crash recording has no R button despite
            // the parsek_rw_*.sfs file existing on disk.
            var tree = MakeTree("root-001");
            var rootRec = tree.Recordings["root-001"];
            Assert.True(string.IsNullOrEmpty(rootRec.RewindSaveFileName));

            var captured = MakeCaptured(120.0, 180.0, destroyed: true);
            captured.RewindSaveFileName = "parsek_rw_abc123";
            captured.RewindReservedFunds = 4200.0;
            captured.RewindReservedScience = 7.5;
            captured.RewindReservedRep = 1.25f;
            captured.PreLaunchFunds = 25000.0;
            captured.PreLaunchScience = 12.0;
            captured.PreLaunchReputation = 3.0f;

            ParsekFlight.TryAppendCapturedToTree(tree, captured);

            Assert.Equal("parsek_rw_abc123", rootRec.RewindSaveFileName);
            Assert.Equal(4200.0, rootRec.RewindReservedFunds);
            Assert.Equal(7.5, rootRec.RewindReservedScience);
            Assert.Equal(1.25f, rootRec.RewindReservedRep);
            Assert.Equal(25000.0, rootRec.PreLaunchFunds);
            Assert.Equal(12.0, rootRec.PreLaunchScience);
            Assert.Equal(3.0f, rootRec.PreLaunchReputation);
            Assert.Contains(logLines, l =>
                l.Contains("[Flight]")
                && l.Contains("TryAppendCapturedToTree: copied rewind save 'parsek_rw_abc123'"));
        }

        [Fact]
        public void TreeMode_DoesNotOverwriteRootRewindSave_FirstWins()
        {
            // #416 defensive: if the root already has a rewind save (normal path where
            // CopyRewindSaveToRoot already ran earlier in the session), the crash-path
            // bridge call must not clobber it. CopyRewindSaveToRoot enforces first-wins
            // on RewindSaveFileName and on the reserved/pre-launch budget fields.
            var tree = MakeTree("root-001");
            var rootRec = tree.Recordings["root-001"];
            rootRec.RewindSaveFileName = "parsek_rw_existing";
            rootRec.PreLaunchFunds = 50000.0;
            rootRec.RewindReservedFunds = 999.0;

            var captured = MakeCaptured(120.0, 180.0, destroyed: true);
            captured.RewindSaveFileName = "parsek_rw_newer";
            captured.RewindReservedFunds = 123.0;
            captured.PreLaunchFunds = 111.0;

            ParsekFlight.TryAppendCapturedToTree(tree, captured);

            Assert.Equal("parsek_rw_existing", rootRec.RewindSaveFileName);
            Assert.Equal(50000.0, rootRec.PreLaunchFunds);
            Assert.Equal(999.0, rootRec.RewindReservedFunds);
        }
    }
}
