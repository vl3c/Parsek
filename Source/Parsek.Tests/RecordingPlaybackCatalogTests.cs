using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Truth table of <see cref="RecordingPlaybackCatalog"/> (the own-timeline
    /// <see cref="IPlaybackCatalog"/>), one block per variant, each row landing
    /// with the task that routes its first caller through the variant
    /// (multiplayer plan r0b-prep-playback, P2a-01). Rows here: the pending
    /// read (P1 to P3), the focus-tree lookup (L3 / L4), the
    /// trees-then-pending-then-list recording lookup (L5 / L6, L9's fallback),
    /// the committed-first containment tree lookup (L2) and the committed-list
    /// recording lookup (L7 / L8).
    ///
    /// Besides the named rows, an equivalence sweep drives the replaced
    /// lookups (kept below as verbatim oracles, with the guard their only
    /// caller applied) and the catalog over the same fixtures, and asserts the
    /// same answer and the same instance for every probe.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingPlaybackCatalogTests : IDisposable
    {
        private readonly IPlaybackCatalog catalog = RecordingPlaybackCatalog.Own;

        public RecordingPlaybackCatalogTests()
        {
            RecordingStore.ResetForTesting();
            InertRecordingTrees.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekScenario.SetInstanceForTesting(null);
            InertRecordingTrees.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // PendingTree (P1 to P3)
        // ==================================================================

        [Fact]
        public void PendingTree_EmptySlot_IsNull()
        {
            Assert.Null(catalog.PendingTree);
        }

        [Fact]
        public void PendingTree_StashedTree_IsThatTree()
        {
            RecordingTree pending = Tree("P", Rec("r1", "P"));
            PendingTreeSlot.StashPendingTree(pending);

            Assert.Same(pending, catalog.PendingTree);
        }

        [Fact]
        public void PendingTree_IgnoresCommittedTrees()
        {
            RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));

            Assert.Null(catalog.PendingTree);
        }

        // ==================================================================
        // TryFindFocusTree (L3 / L4)
        // ==================================================================

        [Fact]
        public void FocusTree_PendingOnly_FindsPending()
        {
            RecordingTree pending = Tree("P", Rec("r1", "P"));
            PendingTreeSlot.StashPendingTree(pending);

            Assert.True(catalog.TryFindFocusTree("r1", "P", out RecordingTree tree));
            Assert.Same(pending, tree);
        }

        [Fact]
        public void FocusTree_CommittedOnly_FindsCommitted()
        {
            RecordingTree committed = Tree("A", Rec("r1", "A"));
            RecordingStore.AddCommittedTreeForTesting(committed);

            Assert.True(catalog.TryFindFocusTree("r1", "A", out RecordingTree tree));
            Assert.Same(committed, tree);
        }

        [Fact]
        public void FocusTree_BothHoldTheId_CommittedWins()
        {
            RecordingTree committed = Tree("A", Rec("r1", "A"));
            RecordingTree pending = Tree("P", Rec("r1", "P"));
            RecordingStore.AddCommittedTreeForTesting(committed);
            PendingTreeSlot.StashPendingTree(pending);

            Assert.True(catalog.TryFindFocusTree("r1", null, out RecordingTree tree));
            Assert.Same(committed, tree);
        }

        [Fact]
        public void FocusTree_TwoCommittedTreesHoldTheId_FirstInListWins()
        {
            RecordingTree first = Tree("A", Rec("r1", "A"));
            RecordingTree second = Tree("B", Rec("r1", "B"));
            RecordingStore.AddCommittedTreeForTesting(first);
            RecordingStore.AddCommittedTreeForTesting(second);

            Assert.True(catalog.TryFindFocusTree("r1", null, out RecordingTree tree));
            Assert.Same(first, tree);
        }

        [Fact]
        public void FocusTree_TreeIdMatchWithoutContainment_Accepts()
        {
            RecordingTree committed = Tree("A", Rec("other", "A"));
            RecordingStore.AddCommittedTreeForTesting(committed);

            Assert.True(catalog.TryFindFocusTree("r1", "A", out RecordingTree tree));
            Assert.Same(committed, tree);
        }

        [Fact]
        public void FocusTree_PendingTreeIdMatchWithoutContainment_Accepts()
        {
            RecordingTree pending = Tree("P", Rec("other", "P"));
            PendingTreeSlot.StashPendingTree(pending);

            Assert.True(catalog.TryFindFocusTree("r1", "P", out RecordingTree tree));
            Assert.Same(pending, tree);
        }

        [Fact]
        public void FocusTree_EarlierTreeIdMatch_BeatsLaterContainment()
        {
            RecordingTree byId = Tree("A", Rec("other", "A"));
            RecordingTree byContainment = Tree("B", Rec("r1", "B"));
            RecordingStore.AddCommittedTreeForTesting(byId);
            RecordingStore.AddCommittedTreeForTesting(byContainment);

            Assert.True(catalog.TryFindFocusTree("r1", "A", out RecordingTree tree));
            Assert.Same(byId, tree);
        }

        [Fact]
        public void FocusTree_NullValueUnderTheId_StillContains()
        {
            RecordingTree committed = Tree("A");
            committed.Recordings["r1"] = null;
            RecordingStore.AddCommittedTreeForTesting(committed);

            Assert.True(catalog.TryFindFocusTree("r1", null, out RecordingTree tree));
            Assert.Same(committed, tree);
        }

        [Fact]
        public void FocusTree_NullRecordingsDictionary_IsSkippedEvenOnTreeIdMatch()
        {
            RecordingTree hollow = Tree("A");
            hollow.Recordings = null;
            RecordingTree pending = Tree("A", Rec("r1", "A"));
            RecordingStore.AddCommittedTreeForTesting(hollow);
            PendingTreeSlot.StashPendingTree(pending);

            Assert.True(catalog.TryFindFocusTree("r1", "A", out RecordingTree tree));
            Assert.Same(pending, tree);
        }

        [Fact]
        public void FocusTree_PendingWithNullRecordings_NeverMatches()
        {
            RecordingTree pending = Tree("P");
            PendingTreeSlot.StashPendingTree(pending);
            pending.Recordings = null;

            Assert.False(catalog.TryFindFocusTree("r1", "P", out RecordingTree tree));
            Assert.Null(tree);
        }

        [Fact]
        public void FocusTree_EmptyTreeId_MatchesByContainmentOnly()
        {
            RecordingTree blankId = Tree("");
            RecordingStore.AddCommittedTreeForTesting(blankId);

            Assert.False(catalog.TryFindFocusTree("r1", "", out RecordingTree tree));
            Assert.Null(tree);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FocusTree_NullOrEmptyRecordingId_IsFalse(string recordingId)
        {
            RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));

            Assert.False(catalog.TryFindFocusTree(recordingId, "A", out RecordingTree tree));
            Assert.Null(tree);
        }

        [Fact]
        public void FocusTree_UnknownIdAndTree_IsFalse()
        {
            RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));
            PendingTreeSlot.StashPendingTree(Tree("P", Rec("r2", "P")));

            Assert.False(catalog.TryFindFocusTree("missing", "none", out RecordingTree tree));
            Assert.Null(tree);
        }

        [Fact]
        public void FocusTree_FlatListOnly_IsNotATree()
        {
            RecordingStore.AddCommittedInternal(Rec("r9", "X"));

            Assert.False(catalog.TryFindFocusTree("r9", "X", out RecordingTree tree));
            Assert.Null(tree);
        }

        // ==================================================================
        // TryFindRecording, TreesThenPendingThenList (L5 / L6)
        // ==================================================================

        [Fact]
        public void Recording_PendingOnly_FindsPending()
        {
            Recording rec = Rec("r1", "P");
            PendingTreeSlot.StashPendingTree(Tree("P", rec));

            Assert.True(Find("r1", out Recording found));
            Assert.Same(rec, found);
        }

        [Fact]
        public void Recording_CommittedTreeOnly_FindsCommitted()
        {
            Recording rec = Rec("r1", "A");
            RecordingStore.AddCommittedTreeForTesting(Tree("A", rec));

            Assert.True(Find("r1", out Recording found));
            Assert.Same(rec, found);
        }

        [Fact]
        public void Recording_TreeAndPendingAndListHoldTheId_CommittedTreeWins()
        {
            Recording inTree = Rec("r1", "A");
            Recording inPending = Rec("r1", "P");
            Recording inList = Rec("r1", "L");
            RecordingStore.AddCommittedTreeForTesting(Tree("A", inTree));
            PendingTreeSlot.StashPendingTree(Tree("P", inPending));
            RecordingStore.AddCommittedInternal(inList);

            Assert.True(Find("r1", out Recording found));
            Assert.Same(inTree, found);
        }

        [Fact]
        public void Recording_PendingAndList_PendingWins()
        {
            Recording inPending = Rec("r1", "P");
            Recording inList = Rec("r1", "L");
            PendingTreeSlot.StashPendingTree(Tree("P", inPending));
            RecordingStore.AddCommittedInternal(inList);

            Assert.True(Find("r1", out Recording found));
            Assert.Same(inPending, found);
        }

        [Fact]
        public void Recording_NullValueInTree_FallsThroughToPendingThenList()
        {
            RecordingTree committed = Tree("A");
            committed.Recordings["r1"] = null;
            RecordingStore.AddCommittedTreeForTesting(committed);
            Recording inList = Rec("r1", "L");
            RecordingStore.AddCommittedInternal(inList);

            Assert.True(Find("r1", out Recording found));
            Assert.Same(inList, found);

            RecordingTree pending = Tree("P");
            pending.Recordings["r1"] = null;
            PendingTreeSlot.StashPendingTree(pending);
            Assert.True(Find("r1", out found));
            Assert.Same(inList, found);
        }

        [Fact]
        public void Recording_NullValueEverywhere_IsFalseWithNullOut()
        {
            RecordingTree committed = Tree("A");
            committed.Recordings["r1"] = null;
            RecordingStore.AddCommittedTreeForTesting(committed);
            RecordingStore.AddCommittedInternal(null);

            Assert.False(Find("r1", out Recording found));
            Assert.Null(found);
        }

        [Fact]
        public void Recording_NullRecordingsDictionary_IsSkipped()
        {
            RecordingTree hollow = Tree("A");
            hollow.Recordings = null;
            RecordingStore.AddCommittedTreeForTesting(hollow);
            RecordingStore.AddCommittedTreeForTesting(null);
            Recording rec = Rec("r1", "B");
            RecordingStore.AddCommittedTreeForTesting(Tree("B", rec));

            Assert.True(Find("r1", out Recording found));
            Assert.Same(rec, found);
        }

        [Fact]
        public void Recording_FlatListOnly_IsFound()
        {
            Recording rec = Rec("r9", "X");
            RecordingStore.AddCommittedInternal(rec);

            Assert.True(Find("r9", out Recording found));
            Assert.Same(rec, found);
        }

        [Fact]
        public void Recording_TreeIdIsNotARecordingId()
        {
            RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));

            Assert.False(Find("A", out Recording found));
            Assert.Null(found);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Recording_NullOrEmptyId_IsFalse(string id)
        {
            RecordingStore.AddCommittedInternal(Rec("", "X"));

            Assert.False(Find(id, out Recording found));
            Assert.Null(found);
        }

        [Fact]
        public void Recording_SupersededRecording_IsStillFound()
        {
            // The catalog reads the raw store, as the anchor sites did: a
            // superseded recording is still a valid anchor by exact id.
            Recording oldRec = Rec("old", "A");
            Recording newRec = Rec("new", "A");
            RecordingStore.AddCommittedTreeForTesting(Tree("A", oldRec, newRec));
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_old_new",
                        OldRecordingId = "old",
                        NewRecordingId = "new",
                    },
                },
            });

            Assert.True(Find("old", out Recording found));
            Assert.Same(oldRec, found);
            Assert.True(catalog.TryFindFocusTree("old", null, out RecordingTree tree));
            Assert.Equal("A", tree.Id);
        }

        [Fact]
        public void Recording_InertTreeRecording_IsNotFound()
        {
            // An inert (set-aside) tree is held as a ConfigNode outside the
            // committed trees; nothing playback resolves may name it.
            InertRecordingTrees.SetAside(InertTreeNode("I", "inert-rec"), "generation-newer");
            Assert.True(InertRecordingTrees.ContainsRecordingId("inert-rec"));

            Assert.False(Find("inert-rec", out Recording found));
            Assert.Null(found);
            Assert.False(catalog.TryFindFocusTree("inert-rec", "I", out RecordingTree tree));
            Assert.Null(tree);
        }

        [Fact]
        public void Recording_UndefinedScope_IsFalse()
        {
            RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));

            Assert.False(catalog.TryFindRecording("r1", (CatalogRecordingScope)99, out Recording found));
            Assert.Null(found);
        }

        // ==================================================================
        // TryFindTreeHolding, CommittedFirstContainsKey (L2)
        // ==================================================================

        [Fact]
        public void TreeHolding_PendingOnly_FindsPending()
        {
            RecordingTree pending = Tree("P", Rec("r1", "P"));
            PendingTreeSlot.StashPendingTree(pending);

            Assert.True(FindTree("r1", out RecordingTree tree));
            Assert.Same(pending, tree);
        }

        [Fact]
        public void TreeHolding_CommittedOnly_FindsCommitted()
        {
            RecordingTree committed = Tree("A", Rec("r1", "A"));
            RecordingStore.AddCommittedTreeForTesting(committed);

            Assert.True(FindTree("r1", out RecordingTree tree));
            Assert.Same(committed, tree);
        }

        [Fact]
        public void TreeHolding_BothHoldTheId_CommittedWins()
        {
            RecordingTree committed = Tree("A", Rec("r1", "A"));
            RecordingTree pending = Tree("P", Rec("r1", "P"));
            RecordingStore.AddCommittedTreeForTesting(committed);
            PendingTreeSlot.StashPendingTree(pending);

            Assert.True(FindTree("r1", out RecordingTree tree));
            Assert.Same(committed, tree);
        }

        [Fact]
        public void TreeHolding_TwoCommittedTreesHoldTheId_FirstInListWins()
        {
            RecordingTree first = Tree("A", Rec("r1", "A"));
            RecordingTree second = Tree("B", Rec("r1", "B"));
            RecordingStore.AddCommittedTreeForTesting(first);
            RecordingStore.AddCommittedTreeForTesting(second);

            Assert.True(FindTree("r1", out RecordingTree tree));
            Assert.Same(first, tree);
        }

        [Fact]
        public void TreeHolding_NullValueUnderTheId_Accepts()
        {
            // L2 matches by key; L1's non-null rule is a different order.
            RecordingTree committed = Tree("A");
            committed.Recordings["r1"] = null;
            RecordingStore.AddCommittedTreeForTesting(committed);

            Assert.True(FindTree("r1", out RecordingTree tree));
            Assert.Same(committed, tree);

            RecordingStore.ResetForTesting();
            RecordingTree pending = Tree("P");
            pending.Recordings["r1"] = null;
            PendingTreeSlot.StashPendingTree(pending);
            Assert.True(FindTree("r1", out tree));
            Assert.Same(pending, tree);
        }

        [Fact]
        public void TreeHolding_TreeIdMatchWithoutContainment_Rejects()
        {
            // L3 accepts a tree-id match; L2 matches by containment only.
            RecordingStore.AddCommittedTreeForTesting(Tree("r1", Rec("other", "r1")));
            PendingTreeSlot.StashPendingTree(Tree("r1", Rec("other2", "r1")));

            Assert.False(FindTree("r1", out RecordingTree tree));
            Assert.Null(tree);
        }

        [Fact]
        public void TreeHolding_NullRecordingsDictionaryAndNullTree_AreSkipped()
        {
            RecordingTree hollow = Tree("A");
            hollow.Recordings = null;
            RecordingStore.AddCommittedTreeForTesting(hollow);
            RecordingStore.AddCommittedTreeForTesting(null);
            RecordingTree pending = Tree("P", Rec("r1", "P"));
            PendingTreeSlot.StashPendingTree(pending);

            Assert.True(FindTree("r1", out RecordingTree tree));
            Assert.Same(pending, tree);
        }

        [Fact]
        public void TreeHolding_PendingWithNullRecordings_NeverMatches()
        {
            RecordingTree pending = Tree("P");
            PendingTreeSlot.StashPendingTree(pending);
            pending.Recordings = null;

            Assert.False(FindTree("r1", out RecordingTree tree));
            Assert.Null(tree);
        }

        [Fact]
        public void TreeHolding_FlatListOnly_IsNotATree()
        {
            RecordingStore.AddCommittedInternal(Rec("r9", "X"));

            Assert.False(FindTree("r9", out RecordingTree tree));
            Assert.Null(tree);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void TreeHolding_NullOrEmptyId_IsFalse(string id)
        {
            RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("", "A")));

            Assert.False(FindTree(id, out RecordingTree tree));
            Assert.Null(tree);
        }

        [Fact]
        public void TreeHolding_UndefinedOrder_IsFalse()
        {
            RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));

            Assert.False(catalog.TryFindTreeHolding("r1", (CatalogTreeOrder)99, out RecordingTree tree));
            Assert.Null(tree);
        }

        // ==================================================================
        // TryFindRecording, CommittedListOnly (L7 / L8)
        // ==================================================================

        [Fact]
        public void ListOnly_FlatListEntry_IsFound()
        {
            Recording rec = Rec("r9", "X");
            RecordingStore.AddCommittedInternal(rec);

            Assert.True(FindInList("r9", out Recording found));
            Assert.Same(rec, found);
        }

        [Fact]
        public void ListOnly_CommittedTreeOrPendingOnly_IsNotFound()
        {
            RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));
            PendingTreeSlot.StashPendingTree(Tree("P", Rec("r2", "P")));

            Assert.False(FindInList("r1", out Recording found));
            Assert.Null(found);
            Assert.False(FindInList("r2", out found));
            Assert.Null(found);
        }

        [Fact]
        public void ListOnly_TreeAndListHoldTheId_ListEntryIsReturned()
        {
            Recording inTree = Rec("r1", "A");
            Recording inList = Rec("r1", "L");
            RecordingStore.AddCommittedTreeForTesting(Tree("A", inTree));
            RecordingStore.AddCommittedInternal(inList);

            Assert.True(FindInList("r1", out Recording found));
            Assert.Same(inList, found);
        }

        [Fact]
        public void ListOnly_TwoEntriesShareTheId_FirstInListWins()
        {
            Recording first = Rec("r1", "A");
            Recording second = Rec("r1", "B");
            RecordingStore.AddCommittedInternal(first);
            RecordingStore.AddCommittedInternal(second);

            Assert.True(FindInList("r1", out Recording found));
            Assert.Same(first, found);
        }

        [Fact]
        public void ListOnly_NullEntries_AreSkipped()
        {
            RecordingStore.AddCommittedInternal(null);
            Recording rec = Rec("r1", "X");
            RecordingStore.AddCommittedInternal(rec);

            Assert.True(FindInList("r1", out Recording found));
            Assert.Same(rec, found);
        }

        [Fact]
        public void ListOnly_MatchIsOrdinal()
        {
            RecordingStore.AddCommittedInternal(Rec("R1", "X"));

            Assert.False(FindInList("r1", out Recording found));
            Assert.Null(found);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ListOnly_NullOrEmptyId_IsFalse(string id)
        {
            RecordingStore.AddCommittedInternal(Rec("", "X"));

            Assert.False(FindInList(id, out Recording found));
            Assert.Null(found);
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Sweep_TreeHolding_MatchesThePositionerFocusTreeLookup(int fixture)
        {
            string name = BuildFixture(fixture);
            foreach (string id in ProbeRecordingIds)
            {
                bool expected = LegacyTryFindRelativeAnchorFocusTree(id, out RecordingTree expectedTree);
                bool actual = FindTree(id, out RecordingTree actualTree);
                Assert.True(expected == actual,
                    $"fixture={name} id='{id}' legacy={expected} catalog={actual}");
                Assert.True(ReferenceEquals(expectedTree, actualTree),
                    $"fixture={name} id='{id}' tree differs");
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Sweep_ListOnly_MatchesTheCommittedListLookups(int fixture)
        {
            string name = BuildFixture(fixture);
            foreach (string id in ProbeRecordingIds)
            {
                Recording viaStore = RecordingStore.TryFindCommittedRecordingById(id);
                Recording viaTailLift = LegacyResolveRecordingById(id);
                bool actual = FindInList(id, out Recording actualRec);
                Assert.True((viaStore != null) == actual,
                    $"fixture={name} id='{id}' store={viaStore != null} catalog={actual}");
                Assert.True(ReferenceEquals(viaStore, actualRec),
                    $"fixture={name} id='{id}' store recording differs");
                Assert.True(ReferenceEquals(viaTailLift, actualRec),
                    $"fixture={name} id='{id}' tail-lift recording differs");
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Sweep_TailLiftLookup_MatchesTheRemovedCommittedListWalk(int fixture)
        {
            // L7 through the catalog (P2a-04): the production lookup, with a
            // null catalog and with Own, against the walk it replaced.
            string name = BuildFixture(fixture);
            foreach (string id in ProbeRecordingIds)
            {
                Recording expected = LegacyResolveRecordingById(id);
                Recording viaDefault = ParsekFlightGhostPositioner.ResolveRecordingById(id);
                Recording viaOwn = ParsekFlightGhostPositioner.ResolveRecordingById(
                    id, RecordingPlaybackCatalog.Own);
                Assert.True(ReferenceEquals(expected, viaDefault),
                    $"fixture={name} id='{id}' legacy={Describe(expected)} default={Describe(viaDefault)}");
                Assert.True(ReferenceEquals(expected, viaOwn),
                    $"fixture={name} id='{id}' legacy={Describe(expected)} own={Describe(viaOwn)}");
            }
        }

        private static string Describe(Recording rec)
        {
            return rec == null ? "null" : rec.RecordingId + "@" + rec.TreeId;
        }

        // ==================================================================
        // Equivalence sweep: the replaced resolver lookups vs the catalog
        // ==================================================================

        private static readonly string[] ProbeRecordingIds =
            { null, "", " ", "r1", "r2", "r9", "old", "inert-rec", "missing", "A", "P" };

        private static readonly string[] ProbeTreeIds =
            { null, "", "A", "B", "P", "I", "none" };

        public static IEnumerable<object[]> Fixtures()
        {
            for (int i = 0; i < FixtureCount; i++)
                yield return new object[] { i };
        }

        private const int FixtureCount = 16;

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Sweep_FocusTree_MatchesTheDeletedResolverLookup(int fixture)
        {
            string name = BuildFixture(fixture);
            int probes = 0;
            foreach (string recordingId in ProbeRecordingIds)
            {
                foreach (string treeId in ProbeTreeIds)
                {
                    Recording focus = new Recording { RecordingId = recordingId, TreeId = treeId };
                    bool expected = LegacyBuildContextFocusTree(focus, out RecordingTree expectedTree);
                    bool actual = catalog.TryFindFocusTree(recordingId, treeId, out RecordingTree actualTree);
                    Assert.True(expected == actual,
                        $"fixture={name} recordingId='{recordingId}' treeId='{treeId}' legacy={expected} catalog={actual}");
                    Assert.True(ReferenceEquals(expectedTree, actualTree),
                        $"fixture={name} recordingId='{recordingId}' treeId='{treeId}' tree differs");
                    probes++;
                }
            }
            Assert.Equal(ProbeRecordingIds.Length * ProbeTreeIds.Length, probes);
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Sweep_RecordingById_MatchesTheDeletedResolverLookup(int fixture)
        {
            string name = BuildFixture(fixture);
            foreach (string id in ProbeRecordingIds)
            {
                bool expected = LegacyTryFindRecordingById(id, out Recording expectedRec);
                bool actual = Find(id, out Recording actualRec);
                Assert.True(expected == actual,
                    $"fixture={name} id='{id}' legacy={expected} catalog={actual}");
                Assert.True(ReferenceEquals(expectedRec, actualRec),
                    $"fixture={name} id='{id}' recording differs");

                // L9: the resolver's public entry point (the cast first, then the catalog).
                bool viaResolver = RecordedRelativeAnchorPoseResolver.TryFindFocusRecording(
                    new MockTrajectory { RecordingId = id },
                    out Recording resolverRec);
                Assert.True(expected == viaResolver, $"fixture={name} id='{id}' resolver={viaResolver}");
                Assert.True(ReferenceEquals(expectedRec, resolverRec),
                    $"fixture={name} id='{id}' resolver recording differs");
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Sweep_PendingTree_MatchesTheDeletedResolverRead(int fixture)
        {
            string name = BuildFixture(fixture);
            RecordingTree expected = PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null;
            Assert.True(ReferenceEquals(expected, catalog.PendingTree), $"fixture={name}");
        }

        /// <summary>
        /// Store states covering every branch of the two lookups: empty,
        /// each source alone, overlaps, null values, null dictionaries, null
        /// list entries, tree-id-only matches, superseded and inert data.
        /// </summary>
        private string BuildFixture(int fixture)
        {
            switch (fixture)
            {
                case 0:
                    return "empty";
                case 1:
                    RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A"), Rec("r2", "A")));
                    return "committed-only";
                case 2:
                    PendingTreeSlot.StashPendingTree(Tree("P", Rec("r1", "P")));
                    return "pending-only";
                case 3:
                    RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));
                    PendingTreeSlot.StashPendingTree(Tree("P", Rec("r1", "P"), Rec("r2", "P")));
                    RecordingStore.AddCommittedInternal(Rec("r1", "L"));
                    return "all-three-hold-r1";
                case 4:
                    RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));
                    RecordingStore.AddCommittedTreeForTesting(Tree("B", Rec("r1", "B"), Rec("r2", "B")));
                    return "two-committed-trees";
                case 5:
                {
                    RecordingTree hollow = Tree("A");
                    hollow.Recordings = null;
                    RecordingStore.AddCommittedTreeForTesting(hollow);
                    PendingTreeSlot.StashPendingTree(Tree("A", Rec("r1", "A")));
                    return "hollow-committed-same-id-as-pending";
                }
                case 6:
                {
                    RecordingTree nulls = Tree("A");
                    nulls.Recordings["r1"] = null;
                    RecordingStore.AddCommittedTreeForTesting(nulls);
                    RecordingTree pendingNulls = Tree("P");
                    pendingNulls.Recordings["r1"] = null;
                    pendingNulls.Recordings["r2"] = null;
                    PendingTreeSlot.StashPendingTree(pendingNulls);
                    RecordingStore.AddCommittedInternal(Rec("r1", "L"));
                    return "null-values-then-list";
                }
                case 7:
                    RecordingStore.AddCommittedTreeForTesting(null);
                    RecordingStore.AddCommittedTreeForTesting(Tree("B", Rec("r2", "B")));
                    RecordingStore.AddCommittedInternal(null);
                    RecordingStore.AddCommittedInternal(Rec("r9", "X"));
                    return "null-tree-and-null-list-entry";
                case 8:
                    RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("other", "A")));
                    PendingTreeSlot.StashPendingTree(Tree("P", Rec("other2", "P")));
                    return "tree-id-only-matches";
                case 9:
                    RecordingStore.AddCommittedInternal(Rec("r9", "X"));
                    RecordingStore.AddCommittedInternal(Rec(" ", "X"));
                    return "flat-list-only";
                case 10:
                {
                    RecordingTree hollowPending = Tree("P");
                    PendingTreeSlot.StashPendingTree(hollowPending);
                    hollowPending.Recordings = null;
                    RecordingStore.AddCommittedInternal(Rec("r1", "L"));
                    return "hollow-pending";
                }
                case 11:
                    RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("old", "A"), Rec("r1", "A")));
                    RecordingStore.AddCommittedInternal(Rec("old", "A"));
                    ParsekScenario.SetInstanceForTesting(new ParsekScenario
                    {
                        RecordingSupersedes = new List<RecordingSupersedeRelation>
                        {
                            new RecordingSupersedeRelation
                            {
                                RelationId = "rsr_old_r1",
                                OldRecordingId = "old",
                                NewRecordingId = "r1",
                            },
                        },
                    });
                    return "superseded";
                case 12:
                    InertRecordingTrees.SetAside(InertTreeNode("I", "inert-rec"), "generation-newer");
                    RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));
                    return "inert-tree";
                case 13:
                    RecordingStore.AddCommittedTreeForTesting(Tree("", Rec("r2", "")));
                    PendingTreeSlot.StashPendingTree(Tree("", Rec("r1", "")));
                    return "blank-tree-ids";
                case 14:
                    RecordingStore.AddCommittedTreeForTesting(Tree("A", Rec("r1", "A")));
                    RecordingStore.AddCommittedInternal(Rec("r1", "L1"));
                    RecordingStore.AddCommittedInternal(null);
                    RecordingStore.AddCommittedInternal(Rec("r1", "L2"));
                    RecordingStore.AddCommittedInternal(Rec("r9", "X"));
                    return "duplicate-id-in-flat-list";
                case 15:
                    RecordingStore.AddCommittedInternal(Rec("R1", "X"));
                    RecordingStore.AddCommittedInternal(Rec("r9 ", "X"));
                    RecordingStore.AddCommittedInternal(Rec("r1", "L"));
                    return "case-and-space-variants-before-the-match";
                default:
                    throw new ArgumentOutOfRangeException(nameof(fixture));
            }
        }

        // ------------------------------------------------------------------
        // Oracles: the lookups RecordedRelativeAnchorPoseResolver carried
        // before it called the catalog, copied verbatim (L3 with the guard of
        // its only caller, TryBuildContext; L5 whole). Do not edit them to
        // follow the catalog: they are the behavior the catalog must keep.
        // ------------------------------------------------------------------

        private static bool LegacyBuildContextFocusTree(Recording focusRecording, out RecordingTree focusTree)
        {
            focusTree = null;
            if (focusRecording == null || string.IsNullOrEmpty(focusRecording.RecordingId))
                return false;
            return LegacyTryFindFocusTree(focusRecording, out focusTree);
        }

        private static bool LegacyTryFindFocusTree(
            Recording focusRecording,
            out RecordingTree focusTree)
        {
            focusTree = null;
            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings == null)
                        continue;
                    if ((!string.IsNullOrEmpty(focusRecording.TreeId)
                            && string.Equals(tree.Id, focusRecording.TreeId, StringComparison.Ordinal))
                        || tree.Recordings.ContainsKey(focusRecording.RecordingId))
                    {
                        focusTree = tree;
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTreeSlot.HasPendingTree
                ? PendingTreeSlot.PendingTree
                : null;
            if (pending?.Recordings != null
                && (pending.Recordings.ContainsKey(focusRecording.RecordingId)
                    || (!string.IsNullOrEmpty(focusRecording.TreeId)
                        && string.Equals(pending.Id, focusRecording.TreeId, StringComparison.Ordinal))))
            {
                focusTree = pending;
                return true;
            }

            return false;
        }

        private static bool LegacyTryFindRecordingById(
            string recordingId,
            out Recording recording)
        {
            recording = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.TryGetValue(recordingId, out recording)
                        && recording != null)
                    {
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTreeSlot.HasPendingTree
                ? PendingTreeSlot.PendingTree
                : null;
            if (pending?.Recordings != null
                && pending.Recordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                return true;
            }

            IReadOnlyList<Recording> committedRecordings = RecordingStore.CommittedRecordings;
            if (committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording candidate = committedRecordings[i];
                    if (candidate != null
                        && string.Equals(candidate.RecordingId, recordingId, StringComparison.Ordinal))
                    {
                        recording = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        // Oracle: ParsekFlightGhostPositioner.TryFindRelativeAnchorFocusTree
        // before it called the catalog (L2), copied verbatim.
        private static bool LegacyTryFindRelativeAnchorFocusTree(
            string recordingId,
            out RecordingTree focusTree)
        {
            focusTree = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.ContainsKey(recordingId))
                    {
                        focusTree = tree;
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTreeSlot.HasPendingTree
                ? PendingTreeSlot.PendingTree
                : null;
            if (pending?.Recordings != null && pending.Recordings.ContainsKey(recordingId))
            {
                focusTree = pending;
                return true;
            }

            return false;
        }

        // Oracle: ParsekFlightGhostPositioner.ResolveRecordingById (L7) before
        // it called the catalog, copied verbatim. L8 is
        // RecordingStore.TryFindCommittedRecordingById itself, which the sweep
        // calls directly.
        private static Recording LegacyResolveRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            try
            {
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed != null)
                {
                    for (int i = 0; i < committed.Count; i++)
                    {
                        Recording r = committed[i];
                        if (r == null) continue;
                        if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                            return r;
                    }
                }
            }
            catch
            {
                // Mid-load mutation; treat as missing.
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private bool Find(string id, out Recording rec)
        {
            return catalog.TryFindRecording(id, CatalogRecordingScope.TreesThenPendingThenList, out rec);
        }

        private bool FindInList(string id, out Recording rec)
        {
            return catalog.TryFindRecording(id, CatalogRecordingScope.CommittedListOnly, out rec);
        }

        private bool FindTree(string id, out RecordingTree tree)
        {
            return catalog.TryFindTreeHolding(id, CatalogTreeOrder.CommittedFirstContainsKey, out tree);
        }

        private static Recording Rec(string recordingId, string treeId)
        {
            return new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                TreeId = treeId,
                VesselName = recordingId,
            };
        }

        /// <summary>
        /// A tree holding the given recordings under their ids, written
        /// straight into the dictionary so a blank id is kept as a key.
        /// </summary>
        private static RecordingTree Tree(string id, params Recording[] recordings)
        {
            var tree = new RecordingTree { Id = id, TreeName = "tree-" + id };
            foreach (Recording rec in recordings)
                tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        private static ConfigNode InertTreeNode(string treeId, string recordingId)
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("id", treeId);
            node.AddValue("treeName", "inert-" + treeId);
            ConfigNode rec = node.AddNode("RECORDING");
            rec.AddValue("recordingId", recordingId);
            return node;
        }
    }
}
