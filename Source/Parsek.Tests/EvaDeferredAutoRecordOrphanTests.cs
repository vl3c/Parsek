using Xunit;

namespace Parsek.Tests
{
    public class EvaDeferredAutoRecordOrphanTests
    {
        private static Recording MakeRecording(string id, uint pid)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = "tree-eva",
                VesselName = id,
                VesselPersistentId = pid,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 20.0
            };
        }

        private static RecordingTree MakeTree(string activeRecordingId)
        {
            var tree = new RecordingTree
            {
                Id = "tree-eva",
                TreeName = "EVA Tree",
                RootRecordingId = "rec-active",
                ActiveRecordingId = activeRecordingId
            };
            return tree;
        }

        [Fact]
        public void CanStartRecorderWithActiveTreeHead_NullTree_AllowsFreshTreeStart()
        {
            bool result = ParsekFlight.CanStartRecorderWithActiveTreeHead(
                null,
                activeVesselPid: 123,
                out string reason);

            Assert.True(result);
            Assert.Null(reason);
        }

        [Fact]
        public void CanStartRecorderWithActiveTreeHead_MatchingActivePid_AllowsStart()
        {
            var tree = MakeTree("rec-active");
            tree.Recordings["rec-active"] = MakeRecording("rec-active", 123);

            bool result = ParsekFlight.CanStartRecorderWithActiveTreeHead(
                tree,
                activeVesselPid: 123,
                out string reason);

            Assert.True(result);
            Assert.Null(reason);
        }

        [Fact]
        public void CanStartRecorderWithActiveTreeHead_UsesSpawnedPidForLiveRestores()
        {
            var tree = MakeTree("rec-active");
            var rec = MakeRecording("rec-active", 123);
            rec.SpawnedVesselPersistentId = 456;
            tree.Recordings["rec-active"] = rec;

            bool result = ParsekFlight.CanStartRecorderWithActiveTreeHead(
                tree,
                activeVesselPid: 456,
                out string reason);

            Assert.True(result);
            Assert.Null(reason);
        }

        [Fact]
        public void CanStartRecorderWithActiveTreeHead_MissingActiveRecordingId_RejectsStart()
        {
            var tree = MakeTree(null);

            bool result = ParsekFlight.CanStartRecorderWithActiveTreeHead(
                tree,
                activeVesselPid: 123,
                out string reason);

            Assert.False(result);
            Assert.Equal("active-recording-id-missing", reason);
        }

        [Fact]
        public void CanStartRecorderWithActiveTreeHead_MissingRecording_RejectsStart()
        {
            var tree = MakeTree("rec-missing");

            bool result = ParsekFlight.CanStartRecorderWithActiveTreeHead(
                tree,
                activeVesselPid: 123,
                out string reason);

            Assert.False(result);
            Assert.Equal("active-recording-not-found:rec-missing", reason);
        }

        [Fact]
        public void CanStartRecorderWithActiveTreeHead_ActivePidMismatch_RejectsStart()
        {
            var tree = MakeTree("rec-active");
            tree.Recordings["rec-active"] = MakeRecording("rec-active", 123);

            bool result = ParsekFlight.CanStartRecorderWithActiveTreeHead(
                tree,
                activeVesselPid: 456,
                out string reason);

            Assert.False(result);
            Assert.Equal("active-recording-pid-mismatch:123!=456", reason);
        }

        [Fact]
        public void CanStartRecorderWithActiveTreeHead_ZeroActivePid_RejectsStart()
        {
            var tree = MakeTree("rec-active");
            tree.Recordings["rec-active"] = MakeRecording("rec-active", 123);

            bool result = ParsekFlight.CanStartRecorderWithActiveTreeHead(
                tree,
                activeVesselPid: 0,
                out string reason);

            Assert.False(result);
            Assert.Equal("active-vessel-pid-missing", reason);
        }

        [Fact]
        public void CanStartRecorderWithActiveTreeHead_ZeroRecordingPid_RejectsStart()
        {
            var tree = MakeTree("rec-active");
            tree.Recordings["rec-active"] = MakeRecording("rec-active", 0);

            bool result = ParsekFlight.CanStartRecorderWithActiveTreeHead(
                tree,
                activeVesselPid: 123,
                out string reason);

            Assert.False(result);
            Assert.Equal("active-recording-pid-missing:rec-active", reason);
        }

        [Fact]
        public void TryResolveTrackedBackgroundParentRecording_DirectBackgroundMapHit_ReturnsRecordingId()
        {
            var tree = MakeTree(null);
            tree.BackgroundMap[200] = "rec-parent";

            bool result = ParsekFlight.TryResolveTrackedBackgroundParentRecording(
                tree,
                sourceVesselPid: 200,
                out string recordingId,
                out string diagnostic);

            Assert.True(result);
            Assert.Equal("rec-parent", recordingId);
            Assert.Equal("background-map-hit", diagnostic);
        }

        [Fact]
        public void TryResolveTrackedBackgroundParentRecording_ActiveHeadPresent_DoesNotRebuildMap()
        {
            var tree = MakeTree("rec-active");
            tree.Recordings["rec-active"] = MakeRecording("rec-active", 100);
            tree.Recordings["rec-parent"] = MakeRecording("rec-parent", 200);

            bool result = ParsekFlight.TryResolveTrackedBackgroundParentRecording(
                tree,
                sourceVesselPid: 200,
                out string recordingId,
                out string diagnostic);

            Assert.False(result);
            Assert.Null(recordingId);
            Assert.Equal("background-map-miss-active-head-present", diagnostic);
            Assert.Empty(tree.BackgroundMap);
        }

        [Fact]
        public void TryResolveTrackedBackgroundParentRecording_NullActiveHead_RebuildsMapOnce()
        {
            var tree = MakeTree(null);
            tree.Recordings["rec-parent"] = MakeRecording("rec-parent", 200);

            bool result = ParsekFlight.TryResolveTrackedBackgroundParentRecording(
                tree,
                sourceVesselPid: 200,
                out string recordingId,
                out string diagnostic);

            Assert.True(result);
            Assert.Equal("rec-parent", recordingId);
            Assert.Equal("background-map-hit-after-rebuild", diagnostic);
            Assert.Equal("rec-parent", tree.BackgroundMap[200]);
        }

        [Fact]
        public void RestoreBackgroundMapEntries_RestoresPreviousEntriesAndRemovesNewEntries()
        {
            var tree = MakeTree(null);
            tree.BackgroundMap[100] = "rec-parent";
            var snapshot = ParsekFlight.CaptureBackgroundMapEntries(tree, 100, 200, 100);

            tree.BackgroundMap[100] = "rec-new";
            tree.BackgroundMap[200] = "rec-background-child";

            ParsekFlight.RestoreBackgroundMapEntries(tree, snapshot);

            Assert.Equal("rec-parent", tree.BackgroundMap[100]);
            Assert.False(tree.BackgroundMap.ContainsKey(200));
            Assert.Equal(2, snapshot.Count);
        }

        [Fact]
        public void RollbackBackgroundParentBranchMutation_RestoresTreeHeadParentAndBackgroundMap()
        {
            var tree = MakeTree(null);
            var parent = MakeRecording("rec-parent", 100);
            tree.Recordings["rec-parent"] = parent;
            tree.BackgroundMap[100] = "rec-parent";
            var snapshot = ParsekFlight.CaptureBackgroundMapEntries(tree, 100, 200);

            var (bp, activeChild, backgroundChild) = ParsekFlight.BuildSplitBranchData(
                "rec-parent",
                tree.Id,
                branchUT: 50.0,
                BranchPointType.EVA,
                activeVesselPid: 200,
                activeVesselName: "Bob Kerman",
                backgroundVesselPid: 100,
                backgroundVesselName: "Capsule",
                evaCrewName: "Bob Kerman",
                evaVesselPid: 200,
                parentGeneration: 0);

            parent.ChildBranchPointId = bp.Id;
            tree.BranchPoints.Add(bp);
            tree.AddOrReplaceRecording(activeChild);
            tree.AddOrReplaceRecording(backgroundChild);
            tree.ActiveRecordingId = activeChild.RecordingId;
            tree.BackgroundMap.Remove(100);
            tree.BackgroundMap[100] = backgroundChild.RecordingId;

            ParsekFlight.RollbackBackgroundParentBranchMutation(
                tree,
                previousActiveRecordingId: null,
                parentRecordingId: "rec-parent",
                previousParentChildBranchPointId: null,
                bp,
                activeChild,
                backgroundChild,
                snapshot);

            Assert.Null(tree.ActiveRecordingId);
            Assert.Null(parent.ChildBranchPointId);
            Assert.Empty(tree.BranchPoints);
            Assert.False(tree.Recordings.ContainsKey(activeChild.RecordingId));
            Assert.False(tree.Recordings.ContainsKey(backgroundChild.RecordingId));
            Assert.Equal("rec-parent", tree.BackgroundMap[100]);
            Assert.False(tree.BackgroundMap.ContainsKey(200));
        }

        [Fact]
        public void BuildInvalidActiveTreeHeadRateLimitKey_VariesByCallerTreeHeadPidAndReason()
        {
            var tree = MakeTree("rec-active");

            string first = ParsekFlight.BuildInvalidActiveTreeHeadRateLimitKey(
                "start-recording",
                tree,
                activeVesselPid: 100,
                reason: "active-recording-id-missing");
            string second = ParsekFlight.BuildInvalidActiveTreeHeadRateLimitKey(
                "deferred-eva",
                tree,
                activeVesselPid: 200,
                reason: "active-recording-pid-mismatch:123!=200");

            Assert.NotEqual(first, second);
            Assert.Contains("start-recording", first);
            Assert.Contains("deferred-eva", second);
            Assert.Contains("activePid=100", first);
            Assert.Contains("activePid=200", second);
            Assert.Contains("activeRec=rec-active", first);
        }

        [Fact]
        public void FormatRecorderDropDiagnostics_IncludesBufferedDataCounts()
        {
            var tree = MakeTree(null);
            var recorder = new FlightRecorder();
            recorder.Recording.Add(new TrajectoryPoint());
            recorder.OrbitSegments.Add(new OrbitSegment());
            recorder.PartEvents.Add(new PartEvent());
            recorder.FlagEvents.Add(new FlagEvent());
            recorder.SegmentEvents.Add(new SegmentEvent());
            recorder.TrackSections.Add(new TrackSection());

            string diagnostic = ParsekFlight.FormatRecorderDropDiagnostics(
                recorder,
                tree,
                attemptedRecordingId: null);

            Assert.Contains("tree=tree-eva", diagnostic);
            Assert.Contains("activeRec=null", diagnostic);
            Assert.Contains("points=1", diagnostic);
            Assert.Contains("orbitSegments=1", diagnostic);
            Assert.Contains("partEvents=1", diagnostic);
            Assert.Contains("flagEvents=1", diagnostic);
            Assert.Contains("segmentEvents=1", diagnostic);
            Assert.Contains("trackSections=1", diagnostic);
        }
    }
}
