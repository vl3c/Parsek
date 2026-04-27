using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    public class Bug616GhostMapReFlyLookaheadTests
    {
        private const string TreeId = "tree-616";
        private const string PendingTreeId = "tree-616-pending";
        private const string ParentBpId = "bp-616-decouple";
        private const uint ActivePid = 2676381515u;

        [Fact]
        public void AbsolutePrefixCreateLookahead_SuppressesFutureRelativeAnchorToActiveReFlyTarget()
        {
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", 2708531065u),
                ("rec-booster", ActivePid));
            var trees = new List<RecordingTree>
            {
                TreeWithDecouple("rec-capsule", "rec-booster", ActivePid)
            };
            Recording traj = TrajectoryWithAbsolutePrefixThenRelative(
                "rec-capsule",
                ActivePid);

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFlyAtCreateTime(
                marker,
                resolutionBranch: "absolute",
                resolutionAnchorPid: 0u,
                traj: traj,
                currentUT: 120.0,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.True(suppressed);
            Assert.Contains(
                GhostMapPresence.TrackingStationGhostSkipActiveReFlyRelativeLookahead,
                reason);
            Assert.Contains("currentBranch=absolute", reason);
            Assert.Contains("not-suppressed-not-relative-frame", reason);
        }

        [Fact]
        public void UpdateTimeRelativeFlip_RemovesWhenCurrentAnchorIsActiveReFlyTarget()
        {
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", 2708531065u),
                ("rec-booster", ActivePid));
            var trees = new List<RecordingTree>
            {
                TreeWithDecouple("rec-capsule", "rec-booster", ActivePid)
            };

            bool remove = GhostMapPresence.ShouldRemoveStateVectorProtoVesselForActiveReFlyOnUpdate(
                marker,
                resolutionBranch: "relative",
                resolutionAnchorPid: ActivePid,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.True(remove);
            Assert.Contains(
                GhostMapPresence.TrackingStationGhostSkipActiveReFlyRelativeUpdate,
                reason);
            Assert.Contains("relationship=parent", reason);
        }

        [Fact]
        public void UnrelatedRelativeAnchorAllowed_EvenWhenAnchorIsActiveReFlyTarget()
        {
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(
                ("rec-capsule", 2708531065u),
                ("rec-booster", ActivePid),
                ("rec-station", 9999999u));
            RecordingTree tree = TreeWithDecouple(
                "rec-capsule",
                "rec-booster",
                ActivePid);
            tree.Recordings["rec-station"] = new Recording
            {
                RecordingId = "rec-station",
                TreeId = TreeId,
                VesselPersistentId = 9999999u
            };
            var trees = new List<RecordingTree> { tree };
            Recording traj = TrajectoryWithAbsolutePrefixThenRelative(
                "rec-station",
                ActivePid);

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFlyAtCreateTime(
                marker,
                resolutionBranch: "absolute",
                resolutionAnchorPid: 0u,
                traj: traj,
                currentUT: 120.0,
                victimRecordingId: "rec-station",
                committedRecordings: committed,
                committedTrees: trees,
                out string reason);

            Assert.False(suppressed);
            Assert.Contains("not-suppressed-not-parent-of-refly-target", reason);
        }

        [Fact]
        public void PendingTreeActiveReFlySearch_WorksForCreateLookahead()
        {
            var marker = InPlaceMarker("rec-booster");
            var committed = CommittedWith(("rec-capsule", 2708531065u));
            RecordingTree pendingTree = TreeWithDecouple(
                "rec-capsule",
                "rec-booster",
                ActivePid,
                PendingTreeId);
            IReadOnlyList<RecordingTree> searchTrees =
                GhostMapPresence.ComposeSearchTreesForReFlySuppression(
                    new List<RecordingTree>(),
                    pendingTree);
            Recording traj = TrajectoryWithAbsolutePrefixThenRelative(
                "rec-capsule",
                ActivePid);

            bool suppressed = GhostMapPresence.ShouldSuppressStateVectorProtoVesselForActiveReFlyAtCreateTime(
                marker,
                resolutionBranch: "absolute",
                resolutionAnchorPid: 0u,
                traj: traj,
                currentUT: 120.0,
                victimRecordingId: "rec-capsule",
                committedRecordings: committed,
                committedTrees: searchTrees,
                out string reason);

            Assert.True(suppressed);
            Assert.Contains("activePidSource=search-tree:" + PendingTreeId, reason);
        }

        private static ReFlySessionMarker InPlaceMarker(string activeAndOriginRecId)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_616",
                TreeId = TreeId,
                ActiveReFlyRecordingId = activeAndOriginRecId,
                OriginChildRecordingId = activeAndOriginRecId,
                InvokedUT = 119.0
            };
        }

        private static List<Recording> CommittedWith(params (string id, uint pid)[] recs)
        {
            var list = new List<Recording>();
            for (int i = 0; i < recs.Length; i++)
            {
                list.Add(new Recording
                {
                    RecordingId = recs[i].id,
                    VesselPersistentId = recs[i].pid
                });
            }
            return list;
        }

        private static RecordingTree TreeWithDecouple(
            string parentId,
            string activeId,
            uint activePid,
            string treeId = TreeId)
        {
            var tree = new RecordingTree { Id = treeId };
            tree.Recordings[parentId] = new Recording
            {
                RecordingId = parentId,
                TreeId = treeId,
                ChildBranchPointId = ParentBpId
            };
            tree.Recordings[activeId] = new Recording
            {
                RecordingId = activeId,
                TreeId = treeId,
                ParentBranchPointId = ParentBpId,
                VesselPersistentId = activePid
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = ParentBpId,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { parentId },
                ChildRecordingIds = new List<string> { activeId }
            });
            return tree;
        }

        private static Recording TrajectoryWithAbsolutePrefixThenRelative(
            string recordingId,
            uint relativeAnchorPid)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = recordingId,
                RecordingFormatVersion = RecordingStore.RelativeLocalFrameFormatVersion
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 100.0,
                endUT = 150.0,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Relative,
                source = TrackSectionSource.Active,
                startUT = 150.0,
                endUT = 220.0,
                anchorVesselId = relativeAnchorPid,
                frames = new List<TrajectoryPoint>()
            });
            return rec;
        }
    }
}
