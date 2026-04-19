using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="ParsekUI.BuildGroupTreeData"/> — pure data computation
    /// that builds the group tree structures for the recordings list UI.
    /// </summary>
    [Collection("Sequential")]
    public class GroupTreeDataTests : IDisposable
    {
        public GroupTreeDataTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        // Helper: create a recording with optional groups and chainId
        private static Recording MakeRec(string name,
            List<string> groups = null, string chainId = null, int chainIndex = -1)
        {
            return new Recording
            {
                VesselName = name,
                RecordingGroups = groups,
                ChainId = chainId,
                ChainIndex = chainIndex
            };
        }

        // Helper: identity sorted indices (0,1,2,...,n-1)
        private static int[] IdentityIndices(int count)
        {
            var indices = new int[count];
            for (int i = 0; i < count; i++) indices[i] = i;
            return indices;
        }

        // ────────────────────────────────────────────────────────────
        //  Empty input
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void EmptyInput_ProducesEmptyOutputs()
        {
            // Bug caught: null-reference or crash when no recordings exist
            var committed = new List<Recording>();
            var sorted = new int[0];
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Empty(grpToRecs);
            Assert.Empty(chainToRecs);
            Assert.Empty(grpChildren);
            Assert.Empty(rootGrps);
            Assert.Empty(rootChainIds);
        }

        // ────────────────────────────────────────────────────────────
        //  Single recording, no group
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SingleRecording_NoGroup_NotInAnyGroup()
        {
            // Bug caught: a recording with null RecordingGroups must not appear in grpToRecs
            var committed = new List<Recording> { MakeRec("Solo") };
            var sorted = IdentityIndices(1);
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Empty(grpToRecs);
            Assert.Empty(rootGrps);
        }

        // ────────────────────────────────────────────────────────────
        //  Recordings in different groups
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void TwoRecordings_DifferentGroups_EachGroupHasOne()
        {
            // Bug caught: groups must be keyed correctly; recordings must
            // appear under their declared group, not all under the same key
            var committed = new List<Recording>
            {
                MakeRec("Alpha", groups: new List<string> { "GroupA" }),
                MakeRec("Beta",  groups: new List<string> { "GroupB" })
            };
            var sorted = IdentityIndices(2);
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.True(grpToRecs.ContainsKey("GroupA"));
            Assert.True(grpToRecs.ContainsKey("GroupB"));
            Assert.Single(grpToRecs["GroupA"]);
            Assert.Single(grpToRecs["GroupB"]);
            Assert.Contains(0, grpToRecs["GroupA"]); // Alpha is index 0
            Assert.Contains(1, grpToRecs["GroupB"]); // Beta is index 1

            // Both should be root groups (no hierarchy set up)
            Assert.Contains("GroupA", rootGrps);
            Assert.Contains("GroupB", rootGrps);
        }

        [Fact]
        public void MultiGroupMembership_RecordingAppearsInBothGroups()
        {
            // Bug caught: a recording with multiple groups must appear in ALL of them,
            // not just the first one
            var committed = new List<Recording>
            {
                MakeRec("Multi", groups: new List<string> { "A", "B" })
            };
            var sorted = IdentityIndices(1);
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.True(grpToRecs.ContainsKey("A"));
            Assert.True(grpToRecs.ContainsKey("B"));
            Assert.Contains(0, grpToRecs["A"]);
            Assert.Contains(0, grpToRecs["B"]);
        }

        // ────────────────────────────────────────────────────────────
        //  Chain recordings
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ChainRecordings_GroupedByChainId()
        {
            // Bug caught: chain members must be collected under their chainId
            var committed = new List<Recording>
            {
                MakeRec("Seg1", chainId: "chain-abc", chainIndex: 0),
                MakeRec("Seg2", chainId: "chain-abc", chainIndex: 1),
                MakeRec("Other", chainId: "chain-xyz", chainIndex: 0)
            };
            var sorted = IdentityIndices(3);
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.True(chainToRecs.ContainsKey("chain-abc"));
            Assert.Equal(2, chainToRecs["chain-abc"].Count);
            Assert.Contains(0, chainToRecs["chain-abc"]);
            Assert.Contains(1, chainToRecs["chain-abc"]);

            Assert.True(chainToRecs.ContainsKey("chain-xyz"));
            Assert.Single(chainToRecs["chain-xyz"]);
        }

        [Fact]
        public void ChainWithNoGroups_IsRootChain()
        {
            // Bug caught: chains where no member has RecordingGroups must appear
            // in rootChainIds — otherwise they won't be rendered at root level
            var committed = new List<Recording>
            {
                MakeRec("Seg1", chainId: "chain-root", chainIndex: 0),
                MakeRec("Seg2", chainId: "chain-root", chainIndex: 1)
            };
            var sorted = IdentityIndices(2);
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Contains("chain-root", rootChainIds);
        }

        [Fact]
        public void ChainWithGroupMember_IsNotRootChain()
        {
            // Bug caught: if ANY member of a chain has RecordingGroups, the chain
            // is rendered inside that group, not at root level
            var committed = new List<Recording>
            {
                MakeRec("Seg1", groups: new List<string> { "MyGroup" },
                    chainId: "chain-grp", chainIndex: 0),
                MakeRec("Seg2", chainId: "chain-grp", chainIndex: 1)
            };
            var sorted = IdentityIndices(2);
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.DoesNotContain("chain-grp", rootChainIds);
        }

        // ────────────────────────────────────────────────────────────
        //  Nested group hierarchy (parent -> child)
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void NestedHierarchy_ChildAppearsUnderParent()
        {
            // Bug caught: grpChildren must reflect the groupParents mapping;
            // if the parent->child relationship is inverted, the tree renders wrong
            GroupHierarchyStore.groupParents["SubGroup"] = "TopGroup";

            var committed = new List<Recording>
            {
                MakeRec("Rec1", groups: new List<string> { "SubGroup" })
            };
            var sorted = IdentityIndices(1);
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            // grpChildren: TopGroup → [SubGroup]
            Assert.True(grpChildren.ContainsKey("TopGroup"));
            Assert.Contains("SubGroup", grpChildren["TopGroup"]);

            // SubGroup is a child → NOT in rootGrps
            Assert.DoesNotContain("SubGroup", rootGrps);

            // TopGroup IS a root group (it has no parent)
            Assert.Contains("TopGroup", rootGrps);
        }

        [Fact]
        public void RootGroups_SortedCaseInsensitive()
        {
            // Bug caught: unsorted root groups cause inconsistent UI ordering
            var committed = new List<Recording>
            {
                MakeRec("R1", groups: new List<string> { "Zebra" }),
                MakeRec("R2", groups: new List<string> { "alpha" }),
                MakeRec("R3", groups: new List<string> { "Beta" })
            };
            var sorted = IdentityIndices(3);
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            // Case-insensitive sort: alpha, Beta, Zebra
            Assert.Equal(3, rootGrps.Count);
            Assert.Equal("alpha", rootGrps[0]);
            Assert.Equal("Beta", rootGrps[1]);
            Assert.Equal("Zebra", rootGrps[2]);
        }

        // ────────────────────────────────────────────────────────────
        //  Known empty groups
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void KnownEmptyGroups_AppearInRootGroups()
        {
            // Bug caught: empty groups (created by user but no recordings yet) must
            // still appear in rootGrps so the user can see them in the UI
            var committed = new List<Recording>();
            var sorted = new int[0];
            var emptyGroups = new List<string> { "EmptyGroupA", "EmptyGroupB" };

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Contains("EmptyGroupA", rootGrps);
            Assert.Contains("EmptyGroupB", rootGrps);
        }

        [Fact]
        public void KnownEmptyGroup_WithParent_NotRootGroup()
        {
            // Bug caught: an empty group that is a child in groupParents must NOT
            // appear as a root group — it should only appear under its parent
            GroupHierarchyStore.groupParents["EmptyChild"] = "ParentGroup";

            var committed = new List<Recording>
            {
                MakeRec("R1", groups: new List<string> { "ParentGroup" })
            };
            var sorted = IdentityIndices(1);
            var emptyGroups = new List<string> { "EmptyChild" };

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.DoesNotContain("EmptyChild", rootGrps);
            Assert.Contains("ParentGroup", rootGrps);
            Assert.True(grpChildren.ContainsKey("ParentGroup"));
            Assert.Contains("EmptyChild", grpChildren["ParentGroup"]);
        }

        // ────────────────────────────────────────────────────────────
        //  sortedIndices remapping
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SortedIndices_RemapsCorrectly()
        {
            // Bug caught: using row index instead of sortedIndices[row] would
            // assign recordings to wrong groups when sort order differs from list order
            var committed = new List<Recording>
            {
                MakeRec("Second", groups: new List<string> { "G" }),
                MakeRec("First",  groups: new List<string> { "G" })
            };
            // Reverse order: show index 1 first, then index 0
            var sorted = new int[] { 1, 0 };
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            // Both indices should be in the group regardless of sort order
            Assert.True(grpToRecs.ContainsKey("G"));
            Assert.Contains(0, grpToRecs["G"]);
            Assert.Contains(1, grpToRecs["G"]);
        }

        [Fact]
        public void DuplicateGroupMembership_NoDuplicateIndices()
        {
            // Bug caught: if sortedIndices contains same index multiple times
            // (shouldn't happen, but defensive), grpToRecs must not have duplicates
            var committed = new List<Recording>
            {
                MakeRec("Rec", groups: new List<string> { "G" })
            };
            // Duplicate index (defensive)
            var sorted = new int[] { 0, 0 };
            var emptyGroups = new List<string>();

            ParsekUI.BuildGroupTreeData(committed, sorted, emptyGroups,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            // The code does "if (!list.Contains(ri)) list.Add(ri)" — so no dups
            Assert.Single(grpToRecs["G"]);
        }

        [Fact]
        public void PermanentRootGroup_IgnoresStoredParentAndStaysRoot()
        {
            GroupHierarchyStore.groupParents[RecordingStore.GloopsGroupName] = "Folder";

            var committed = new List<Recording>
            {
                MakeRec("Ghost", groups: new List<string> { RecordingStore.GloopsGroupName })
            };

            ParsekUI.BuildGroupTreeData(committed, IdentityIndices(1), new List<string>(),
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Contains(RecordingStore.GloopsGroupName, rootGrps);
            Assert.False(GroupHierarchyStore.HasGroupParent(RecordingStore.GloopsGroupName));
            Assert.False(grpChildren.ContainsKey("Folder")
                && grpChildren["Folder"].Contains(RecordingStore.GloopsGroupName));
        }
    }
}
