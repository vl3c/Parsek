using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug fix-refly-abandon-and-fork-persist §Bug2a: unit tests for
    /// <see cref="RecordingStore.CommitTree"/>'s new active-Re-Fly union
    /// path. The strict <see cref="ShouldReplaceCommittedTree"/> gate
    /// rejects an incoming tree as "incoming-missing-existing-ids" when
    /// the active session pruned BPs via trim-scope=ActiveRecOnly; the
    /// union path merges incoming-only ids into the existing committed
    /// tree instead of dropping the commit.
    /// </summary>
    [Collection("Sequential")]
    public class CommitTreeUnionActiveReFlyTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public CommitTreeUnionActiveReFlyTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording Rec(string id, string treeId,
            MergeState state = MergeState.Immutable)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
            };
        }

        private static BranchPoint Bp(string id)
        {
            return new BranchPoint { Id = id, Type = BranchPointType.Undock };
        }

        [Fact]
        public void UnionsActiveReFlyIncomingWithFewerBps_PreservesFork()
        {
            // Existing committed tree: 2 recordings + 1 BP.
            var head = Rec("rec_head", "tree_1", MergeState.CommittedProvisional);
            var debris = Rec("rec_debris", "tree_1");
            var existing = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Tree1",
                RootRecordingId = "rec_head",
                ActiveRecordingId = "rec_head",
                BranchPoints = new List<BranchPoint> { Bp("bp_pre_rewind") },
            };
            existing.AddOrReplaceRecording(head);
            existing.AddOrReplaceRecording(debris);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedInternal(debris);
            RecordingStore.AddCommittedTreeForTesting(existing);

            // Incoming active tree: trim-scope=ActiveRecOnly pruned debris
            // and the pre-rewind BP, but added the fork.
            var fork = Rec("rec_fork", "tree_1", MergeState.NotCommitted);
            var incoming = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Tree1",
                RootRecordingId = "rec_head",
                ActiveRecordingId = "rec_fork",
                BranchPoints = new List<BranchPoint>(),
            };
            incoming.AddOrReplaceRecording(head);
            incoming.AddOrReplaceRecording(fork);

            // Live Re-Fly marker keys the union path.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_1",
                TreeId = "tree_1",
                ActiveReFlyRecordingId = "rec_fork",
                InPlaceContinuation = true,
            };
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = marker,
            });

            RecordingStore.CommitTree(incoming);

            // After commit, the canonical tree is `existing` (we reassign
            // tree=committedTrees[i] inside CommitTree). It should now
            // contain the fork AND the pre-existing debris AND the BP.
            var canonical = RecordingStore.CommittedTrees[0];
            Assert.Equal("tree_1", canonical.Id);
            Assert.True(canonical.Recordings.ContainsKey("rec_head"));
            Assert.True(canonical.Recordings.ContainsKey("rec_debris"),
                "existing-only debris must be preserved");
            Assert.True(canonical.Recordings.ContainsKey("rec_fork"),
                "incoming-only fork must be added");
            Assert.Single(canonical.BranchPoints);
            Assert.Equal("bp_pre_rewind", canonical.BranchPoints[0].Id);
            // ActiveRecordingId promoted to fork per the union helper's
            // "prefer incoming when marker is present" rule.
            Assert.Equal("rec_fork", canonical.ActiveRecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][RecordingStore]") &&
                l.Contains("CommitTree: unioned active-Re-Fly incoming tree") &&
                l.Contains("addedRecordings=1") &&
                l.Contains("activeRecordingIdSwapped=True"));
        }

        [Fact]
        public void RejectsIncomingMissingBpsWithoutActiveReFlyMarker()
        {
            // Regression guard: with NO live marker, the strict gate must
            // still reject "incoming-missing-existing-ids" — preserving the
            // legitimate duplicate-skip behavior outside the Re-Fly path.
            var head = Rec("rec_head", "tree_1");
            var existing = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Tree1",
                BranchPoints = new List<BranchPoint> { Bp("bp_keep") },
            };
            existing.AddOrReplaceRecording(head);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedTreeForTesting(existing);

            var incoming = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Tree1",
                BranchPoints = new List<BranchPoint>(),
            };
            incoming.AddOrReplaceRecording(Rec("rec_head", "tree_1"));

            // No marker.
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            int existingTreesBefore = RecordingStore.CommittedTrees.Count;
            RecordingStore.CommitTree(incoming);

            // Tree count unchanged. The existing tree is untouched.
            Assert.Equal(existingTreesBefore, RecordingStore.CommittedTrees.Count);
            Assert.Single(existing.BranchPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][RecordingStore]") &&
                l.Contains("CommitTree: duplicate tree id='tree_1' skipped"));
        }

        [Fact]
        public void DoesNotUnion_WhenMarkerNamesDifferentTree()
        {
            // Marker references a DIFFERENT tree id. The union path must
            // NOT engage — fallback to strict-duplicate-skip.
            var head = Rec("rec_head", "tree_1");
            var existing = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Tree1",
                BranchPoints = new List<BranchPoint> { Bp("bp_a") },
            };
            existing.AddOrReplaceRecording(head);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedTreeForTesting(existing);

            var incoming = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Tree1",
                BranchPoints = new List<BranchPoint>(),
            };
            incoming.AddOrReplaceRecording(Rec("rec_head", "tree_1"));

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_2",
                TreeId = "tree_OTHER",
                InPlaceContinuation = true,
            };
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = marker,
            });

            RecordingStore.CommitTree(incoming);

            // Strict gate keeps existing tree intact.
            Assert.Single(existing.BranchPoints);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("CommitTree: unioned active-Re-Fly"));
        }
    }
}
