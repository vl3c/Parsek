using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for collapse-seal-into-mergestate (plan section 8):
    /// open/closed is read solely from a slot's effective tip MergeState, and
    /// the first-commit guard in
    /// <see cref="RecordingStore.ApplyRewindProvisionalMergeStates"/> must never
    /// re-open a concluded (Immutable) tip on a later re-commit. Each test name
    /// states the hole it pins.
    /// </summary>
    [Collection("Sequential")]
    public class CollapseSealMergeStateRegressionTests : IDisposable
    {
        private readonly List<string> deletedRpIds = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public CollapseSealMergeStateRegressionTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindPointReaper.ResetTestOverrides();
            UnfinishedFlightSealHandler.ResetForTesting();
            UnfinishedFlightStashHandler.ResetForTesting();
            UnfinishedFlightSealHandler.SavePersistentForTesting = () => true;
            UnfinishedFlightSealHandler.UtcNowForTesting =
                () => new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
            UnfinishedFlightStashHandler.UtcNowForTesting =
                () => new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
            RewindPointReaper.DeleteQuicksaveForTesting = rpId =>
            {
                deletedRpIds.Add(rpId);
                return true;
            };
        }

        public void Dispose()
        {
            RewindPointReaper.ResetTestOverrides();
            UnfinishedFlightSealHandler.ResetForTesting();
            UnfinishedFlightStashHandler.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---------- Helpers -------------------------------------------------

        private static Recording Rec(string id, string treeId,
            TerminalState? terminal = null,
            string parentBranchPointId = null,
            string evaCrewName = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                EvaCrewName = evaCrewName,
            };
        }

        private static ChildSlot Slot(int index, string recId)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = recId,
                Controllable = true,
            };
        }

        private static RecordingTree MakeSplitTree(
            string treeId, string bpId, string focusId, string otherId,
            TerminalState focusTerminal, TerminalState otherTerminal,
            string otherEvaCrew = null)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                RootRecordingId = focusId,
                ActiveRecordingId = focusId,
            };
            tree.Recordings[focusId] = Rec(focusId, treeId, focusTerminal,
                parentBranchPointId: bpId);
            tree.Recordings[otherId] = Rec(otherId, treeId, otherTerminal,
                parentBranchPointId: bpId, evaCrewName: otherEvaCrew);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = bpId,
                UT = 100.0,
                Type = BranchPointType.Undock,
                RewindPointId = "rp_" + treeId,
                ParentRecordingIds = new List<string> { "parent_" + treeId },
                ChildRecordingIds = new List<string> { focusId, otherId },
            });
            return tree;
        }

        private static RewindPoint Rp(string treeId, string bpId,
            int focusSlotIndex, params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = "rp_" + treeId,
                BranchPointId = bpId,
                UT = 100.0,
                SessionProvisional = true,
                FocusSlotIndex = focusSlotIndex,
                ChildSlots = new List<ChildSlot>(slots),
            };
        }

        private static ParsekScenario InstallScenario(RewindPoint rp,
            List<RecordingSupersedeRelation> supersedes = null)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { rp },
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        // ---------- 1. Clobber guard: a sealed crash tip stays sealed --------

        [Fact]
        public void SealedCrashSlot_StaysImmutableWhilePeerOpen_ThenReapsWhenLastSlotSealed()
        {
            // Two crashed slots: seal ONE so its tip is Immutable while the
            // sibling stays open (CP). The sealed tip must NOT be re-opened by a
            // later promotion pass (first-commit guard), the RP survives because
            // the peer is still open, and reaping waits until the last open slot
            // is sealed.
            var tree = MakeSplitTree("clobber", "bp_c", "child_a", "child_b",
                TerminalState.Destroyed, TerminalState.Destroyed);
            // Neither slot is the focus -> both crashed children are open UFs.
            var rp = Rp("clobber", "bp_c", -1,
                Slot(0, "child_a"), Slot(1, "child_b"));
            var scenario = InstallScenario(rp);

            // First commit: both crashed children promote to CP (open).
            RecordingStore.CommitTree(tree);
            Assert.Equal(MergeState.CommittedProvisional, tree.Recordings["child_a"].MergeState);
            Assert.Equal(MergeState.CommittedProvisional, tree.Recordings["child_b"].MergeState);

            // Seal child_a -> tip Immutable. child_b stays open, so the RP is
            // NOT reaped by the seal.
            Assert.True(UnfinishedFlightSealHandler.TrySeal(tree.Recordings["child_a"], out _));
            Assert.Equal(MergeState.Immutable, tree.Recordings["child_a"].MergeState);
            Assert.Single(scenario.RewindPoints);

            // Run promotion again directly: the sealed tip (Immutable, in a
            // committed tree) must NOT be re-opened, and the still-open peer
            // stays CP. This is the clobber guard.
            RecordingStore.ApplyRewindProvisionalMergeStatesForTesting(tree);
            Assert.Equal(MergeState.Immutable, tree.Recordings["child_a"].MergeState);
            Assert.Equal(MergeState.CommittedProvisional, tree.Recordings["child_b"].MergeState);

            // Seal the last open slot -> all tips Immutable -> RP reaps.
            Assert.True(UnfinishedFlightSealHandler.TrySeal(tree.Recordings["child_b"], out _));
            Assert.Equal(MergeState.Immutable, tree.Recordings["child_b"].MergeState);
            Assert.Empty(scenario.RewindPoints);
        }

        // ---------- 2. Non-focus re-fly to Orbit fork stays Immutable --------

        [Fact]
        public void NonFocusOrbitFork_StaysImmutableOnRecommit()
        {
            // A non-focus re-fly that reached stable Orbit is sealed by
            // SupersedeCommit (the fork is Immutable AND a supersede fork). On a
            // later generic promotion pass the fork still matches the
            // static-focus stableLeafUnconcluded shape, so without the
            // supersede-fork-identity skip it would be re-demoted to CP and
            // un-sealed. The guard keeps it Immutable (canon preserved at the
            // parent-rewind / anchor sites, which are unchanged code).
            var tree = new RecordingTree
            {
                Id = "fork_tree",
                TreeName = "ForkTree",
                RootRecordingId = "origin",
                ActiveRecordingId = "fork",
            };
            // origin: the superseded original (HEAD). fork: the Immutable
            // Orbit conclusion. Both Orbiting non-focus shape.
            tree.Recordings["origin"] = Rec("origin", "fork_tree", TerminalState.Orbiting,
                parentBranchPointId: "bp_f");
            tree.Recordings["origin"].MergeState = MergeState.CommittedProvisional;
            tree.Recordings["fork"] = Rec("fork", "fork_tree", TerminalState.Orbiting,
                parentBranchPointId: "bp_f");
            tree.Recordings["fork"].MergeState = MergeState.Immutable;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_f",
                UT = 100.0,
                Type = BranchPointType.Undock,
                RewindPointId = "rp_fork_tree",
                ParentRecordingIds = new List<string> { "parent" },
                ChildRecordingIds = new List<string> { "origin", "fork" },
            });
            var rp = new RewindPoint
            {
                RewindPointId = "rp_fork_tree",
                BranchPointId = "bp_f",
                UT = 100.0,
                SessionProvisional = false,
                FocusSlotIndex = 1,
                ChildSlots = new List<ChildSlot> { Slot(0, "origin") },
            };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = "rsr_origin_fork",
                    OldRecordingId = "origin",
                    NewRecordingId = "fork",
                    UT = 100.0,
                },
            };
            // Pre-seed the committed tree so the first-commit tree-membership
            // snapshot does not flag the fork as a fresh first-commit tip; the
            // supersede-fork-identity skip is the load-bearing guard here.
            InstallScenario(rp, supersedes);

            RecordingStore.CommitTree(tree);

            // The fork is a supersede fork (NewRecordingId) -> promotion skips
            // it -> stays Immutable (canon).
            Assert.Equal(MergeState.Immutable, tree.Recordings["fork"].MergeState);
        }

        // ---------- 3. Stash then seal: not re-stashable, hidden from UF -----

        [Fact]
        public void StashThenSeal_NotReStashable_AndHiddenFromUf()
        {
            // Stash a stable Landed leaf -> tip CP (open). Seal it -> tip
            // Immutable (closed). The monotonic slot.Stashed bit blocks
            // re-stash (alreadyStashed), and the sealed tip hides the row from
            // the UF group.
            // A sibling crashed slot stays open (CP) so the RP survives the
            // seal, letting the re-stash rejection surface as alreadyStashed.
            var tree = MakeSplitTree("stashseal", "bp_s", "child_peer", "child_leaf",
                TerminalState.Destroyed, TerminalState.Landed);
            tree.Recordings["child_peer"].MergeState = MergeState.CommittedProvisional;
            // The leaf is born Immutable (a stable Landed leaf default-excluded
            // from UF). Stash opens it.
            tree.Recordings["child_leaf"].MergeState = MergeState.Immutable;
            var leafSlot = Slot(1, "child_leaf");
            var rp = Rp("stashseal", "bp_s", -1,
                Slot(0, "child_peer"), leafSlot);
            rp.SessionProvisional = false;
            var scenario = InstallScenario(rp);
            RecordingStore.AddRecordingWithTreeForTesting(
                tree.Recordings["child_peer"], "stashseal");
            RecordingStore.AddRecordingWithTreeForTesting(
                tree.Recordings["child_leaf"], "stashseal");
            EffectiveState.ResetCachesForTesting();

            // Stash: tip Immutable -> CommittedProvisional (open).
            Assert.True(UnfinishedFlightStashHandler.TryStash(
                tree.Recordings["child_leaf"], out _));
            Assert.Equal(MergeState.CommittedProvisional,
                tree.Recordings["child_leaf"].MergeState);
            Assert.True(leafSlot.Stashed);
            Assert.True(EffectiveState.IsUnfinishedFlight(tree.Recordings["child_leaf"]));

            // Seal: tip CommittedProvisional -> Immutable (closed).
            Assert.True(UnfinishedFlightSealHandler.TrySeal(
                tree.Recordings["child_leaf"], out _));
            Assert.Equal(MergeState.Immutable,
                tree.Recordings["child_leaf"].MergeState);

            // The monotonic Stashed bit blocks re-stash.
            Assert.False(
                UnfinishedFlightClassifier.TryResolveStashableRewindPointForRecording(
                    tree.Recordings["child_leaf"], out _, out _, out string reStashReason));
            Assert.Equal("alreadyStashed", reStashReason);

            // The sealed tip hides the row from the UF group.
            Assert.False(EffectiveState.IsUnfinishedFlight(tree.Recordings["child_leaf"]));
        }

        // ---------- 4. NB1: mid-flight CommitRecordingDirect crash tip -------

        [Fact]
        public void MidFlightCommittedCrashTip_NotSkippedByGuard_PromotesToCpAndKeepsRp()
        {
            // A split-branch crash tip committed mid-flight (flat list + active
            // tree, but NOT a committed tree and not a supersede fork) must NOT
            // be skipped by the first-commit guard on its tree's first commit;
            // it is demoted to CP (open) and its RP is NOT reaped. A flat-list-
            // keyed guard would skip it and the reaper would reap a re-flyable
            // RP (data loss). Keyed on committed-TREE membership avoids that.
            var tree = MakeSplitTree("nb1", "bp_n", "child_focus", "child_crash",
                TerminalState.Landed, TerminalState.Destroyed);
            var crashTip = tree.Recordings["child_crash"];
            // Born Immutable (CommitRecordingDirect children never touch
            // MergeState) and present in the flat committed list mid-flight.
            crashTip.MergeState = MergeState.Immutable;
            RecordingStore.AddCommittedInternal(crashTip);

            var rp = Rp("nb1", "bp_n", 0,
                Slot(0, "child_focus"), Slot(1, "child_crash"));
            var scenario = InstallScenario(rp);

            // First CommitTree of this tree: the crash tip is in the flat list
            // but NOT in any committed tree and not a fork -> promotion demotes
            // it to CP (open).
            RecordingStore.CommitTree(tree);
            Assert.Equal(MergeState.CommittedProvisional, crashTip.MergeState);

            // The crash slot is open (CP tip) -> RP is NOT reaped.
            int reaped = RewindPointReaper.ReapOrphanedRPs();
            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
        }

        // ---------- 5. Non-in-place fork never in committed tree ------------

        [Fact]
        public void NonInPlaceOrbitFork_NotInCommittedTree_StaysImmutableOnRecommit()
        {
            // A NON-IN-PLACE re-fly to stable Orbit produces an Immutable fork
            // that lives in its own tree and is never migrated into the
            // committed tree (MigrateActiveReFlyForkIntoCommittedTree
            // early-returns for !InPlaceContinuation). On a later re-commit of
            // the fork's tree, slot-driven promotion resolves the slot tip to
            // this Immutable Orbit fork, finds it absent from every committed
            // tree, and (without the supersede-fork-identity skip) would
            // re-demote it -> un-sealing canon. The fork-identity skip closes
            // this hole.
            var forkTree = new RecordingTree
            {
                Id = "nonip_fork",
                TreeName = "NonInPlaceFork",
                RootRecordingId = "fork_root",
                ActiveRecordingId = "fork_root",
            };
            // The fork is the slot ORIGIN (non-in-place: it lives in its own
            // tree, so the slot points directly at it rather than walking a
            // supersede edge from a migrated HEAD).
            forkTree.Recordings["fork_root"] = Rec("fork_root", "nonip_fork",
                TerminalState.Orbiting, parentBranchPointId: "bp_ni");
            forkTree.Recordings["fork_root"].MergeState = MergeState.Immutable;
            forkTree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_ni",
                UT = 100.0,
                Type = BranchPointType.Undock,
                RewindPointId = "rp_nonip_fork",
                ParentRecordingIds = new List<string> { "parent_ni" },
                ChildRecordingIds = new List<string> { "fork_root" },
            });
            var rp = new RewindPoint
            {
                RewindPointId = "rp_nonip_fork",
                BranchPointId = "bp_ni",
                UT = 100.0,
                SessionProvisional = false,
                FocusSlotIndex = -1,
                ChildSlots = new List<ChildSlot> { Slot(0, "fork_root") },
            };
            // The fork id appears as a supersede NewRecordingId even though it
            // is not in any committed tree (it superseded a sibling-slot origin
            // that lives elsewhere). A surviving sibling-slot RP keeps the RP
            // around for the re-commit.
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = "rsr_sibling_fork",
                    OldRecordingId = "sibling_origin",
                    NewRecordingId = "fork_root",
                    UT = 100.0,
                },
            };
            InstallScenario(rp, supersedes);

            RecordingStore.CommitTree(forkTree);

            // Caught by the supersede-fork-identity skip (NOT tree membership):
            // the fork stays Immutable, never re-demoted/un-sealed.
            Assert.Equal(MergeState.Immutable, forkTree.Recordings["fork_root"].MergeState);
        }

        // ---------- 6. Tree-only tip resolves via the tree fallback ----------

        [Fact]
        public void FindCommittedRecordingByIdRaw_ResolvesTreeOnlyTip_NotInFlatList()
        {
            // A recording that lives in a committed tree but is NOT mirrored into
            // the flat committed list must still resolve, via the tree fallback
            // in EffectiveState.FindCommittedRecordingByIdRaw. The Seal / Stash
            // handlers and LoadTimeSweep's missing-quicksave sweep all resolve a
            // slot's effective tip through this helper; a flat-list-only scan
            // would return null and silently skip the slot (leaving it
            // un-concluded / un-sealable). This pins the shared tree-aware
            // resolver the LoadTimeSweep fix depends on.
            var rec = new Recording
            {
                RecordingId = "tree_only_tip",
                VesselName = "Tree Only",
                TreeId = "treeonly",
                MergeState = MergeState.CommittedProvisional,
                TerminalStateValue = TerminalState.Orbiting,
            };
            var tree = new RecordingTree
            {
                Id = "treeonly",
                TreeName = "TreeOnly",
                RootRecordingId = "tree_only_tip",
                ActiveRecordingId = "tree_only_tip",
            };
            tree.Recordings["tree_only_tip"] = rec;
            // Adds the tree to committedTrees ONLY; does not touch the flat
            // committed list, so a flat-only scan cannot find this recording.
            RecordingStore.AddCommittedTreeForTesting(tree);
            EffectiveState.ResetCachesForTesting();

            var resolved = EffectiveState.FindCommittedRecordingByIdRaw("tree_only_tip");
            Assert.NotNull(resolved);
            Assert.Same(rec, resolved);
        }
    }
}
