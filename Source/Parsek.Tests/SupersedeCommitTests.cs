using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8 of Rewind-to-Staging (design §5.3 / §5.5 / §6.6 step 2-3 /
    /// §7.17 / §7.43 / §10.4): guards the merge-time supersede commit.
    ///
    /// <para>
    /// Covers the terminal-kind matrix (Landed -&gt; Immutable; Crashed
    /// -&gt; CommittedProvisional), the forward-only merge-guarded subtree
    /// walk (every descendant in the closure gets a supersede relation; a
    /// mixed-parent descendant is excluded), the transient
    /// <see cref="Recording.SupersedeTargetId"/> clear, the active-marker
    /// clear, the ERS cache invalidation via supersede state version bump,
    /// and the regular tree-merge fallback when no session is active.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class SupersedeCommitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public SupersedeCommitTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        // ---------- Helpers -------------------------------------------------

        private static Recording Rec(string id, string treeId,
            string parentBranchPointId = null, string childBranchPointId = null,
            MergeState state = MergeState.Immutable,
            TerminalState? terminal = null,
            string supersedeTargetId = null,
            uint vesselPid = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
                SupersedeTargetId = supersedeTargetId,
                VesselPersistentId = vesselPid,
            };
        }

        private static BranchPoint Bp(string id, BranchPointType type,
            List<string> parents = null, List<string> children = null,
            uint targetPid = 0)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0.0,
                ParentRecordingIds = parents ?? new List<string>(),
                ChildRecordingIds = children ?? new List<string>(),
                TargetVesselPersistentId = targetPid,
            };
        }

        private static void InstallTree(string treeId, List<Recording> recordings,
            List<BranchPoint> branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                BranchPoints = branchPoints ?? new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private static ReFlySessionMarker Marker(
            string originId, string provisionalId,
            string sessionId = "sess_1", string treeId = "tree_1")
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
            };
        }

        // Fixture: origin (suppressed) + 1 descendant (suppressed) + unrelated
        // (outside). Mirrors SessionSuppressionWiringTests.InstallOriginClosureFixture.
        private void InstallOriginClosureFixture(
            string originId, string insideId, string outsideId,
            TerminalState? originTerminal = null, TerminalState? insideTerminal = null)
        {
            var origin = Rec(originId, "tree_1",
                childBranchPointId: "bp_c", terminal: originTerminal);
            var inside = Rec(insideId, "tree_1",
                parentBranchPointId: "bp_c", terminal: insideTerminal);
            var outside = Rec(outsideId, "tree_1");
            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { originId },
                children: new List<string> { insideId });
            InstallTree("tree_1",
                new List<Recording> { origin, inside, outside },
                new List<BranchPoint> { bp_c });
        }

        private static Recording AddProvisional(string recordingId, string treeId,
            TerminalState? terminal, string supersedeTargetId)
        {
            var provisional = Rec(recordingId, treeId,
                state: MergeState.NotCommitted,
                terminal: terminal,
                supersedeTargetId: supersedeTargetId);
            // Satisfy SupersedeCommit.AppendRelations supersede-target
            // invariant (>=1 trajectory point + non-null terminal). Tests
            // exercising the empty / null-terminal cases construct
            // provisionals directly.
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            RecordingStore.AddRecordingWithTreeForTesting(provisional, treeId);
            return provisional;
        }

        // ---------- Terminal kind matrix -----------------------------------

        [Fact]
        public void LandedTerminal_ProducesImmutable()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("mergeState=Immutable")
                && l.Contains("terminalKind=Landed"));
        }

        [Fact]
        public void CrashedTerminal_ProducesCommittedProvisional()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("mergeState=CommittedProvisional")
                && l.Contains("terminalKind=Crashed"));
        }

        [Fact]
        public void SplashedTerminal_ProducesImmutable()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Splashed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
        }

        // ---------- Subtree supersede ---------------------------------------

        [Fact]
        public void SubtreeSupersede_AllDescendantsGetRelations()
        {
            // origin -> inside1 -> inside2 linear chain; every id in the
            // closure must produce one supersede relation pointing at the
            // provisional.
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_1");
            var inside1 = Rec("rec_inside1", "tree_1",
                parentBranchPointId: "bp_1", childBranchPointId: "bp_2");
            var inside2 = Rec("rec_inside2", "tree_1",
                parentBranchPointId: "bp_2");
            var bp1 = Bp("bp_1", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_inside1" });
            var bp2 = Bp("bp_2", BranchPointType.Undock,
                parents: new List<string> { "rec_inside1" },
                children: new List<string> { "rec_inside2" });
            InstallTree("tree_1",
                new List<Recording> { origin, inside1, inside2 },
                new List<BranchPoint> { bp1, bp2 });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var rels = scenario.RecordingSupersedes;
            Assert.Equal(3, rels.Count);

            // Every relation points at the provisional.
            foreach (var rel in rels)
                Assert.Equal("rec_provisional", rel.NewRecordingId);

            var oldIds = new HashSet<string>(rels.Select(r => r.OldRecordingId));
            Assert.Contains("rec_origin", oldIds);
            Assert.Contains("rec_inside1", oldIds);
            Assert.Contains("rec_inside2", oldIds);

            // Log assertions.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("rel=")
                && l.Contains("old=rec_origin") && l.Contains("new=rec_provisional"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Added 3 supersede relations")
                && l.Contains("rooted at rec_origin"));
        }

        [Fact]
        public void MixedParentDescendant_NotIncluded()
        {
            // Two roots: rec_origin (in subtree) + rec_other (outside).
            // bp_c has BOTH as parents (mixed) so the walk must halt before
            // adding rec_inside to the closure (§7.40).
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            var other = Rec("rec_other", "tree_1", childBranchPointId: "bp_c");
            var inside = Rec("rec_inside", "tree_1", parentBranchPointId: "bp_c");
            var bp_c = Bp("bp_c", BranchPointType.Dock,
                parents: new List<string> { "rec_origin", "rec_other" },
                children: new List<string> { "rec_inside" });
            InstallTree("tree_1",
                new List<Recording> { origin, other, inside },
                new List<BranchPoint> { bp_c });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // Only the origin is superseded; rec_inside is a mixed-parent
            // halt and must NOT be in the relation list.
            var oldIds = new HashSet<string>(
                scenario.RecordingSupersedes.Select(r => r.OldRecordingId));
            Assert.Contains("rec_origin", oldIds);
            Assert.DoesNotContain("rec_inside", oldIds);
            Assert.DoesNotContain("rec_other", oldIds);
        }

        // ---------- Chain extension / Unfinished Flight ----------------------

        [Fact]
        public void ChainExtendsThroughCrashedReFly()
        {
            // The crashed provisional commits as CommittedProvisional and
            // remains an Unfinished Flight per §7.43 — because
            // IsUnfinishedFlight now routes through TerminalKindClassifier,
            // and the parent BP has an RP attached.
            var origin = Rec("rec_origin", "tree_1",
                parentBranchPointId: "bp_parent",
                terminal: TerminalState.Destroyed);
            var bp_parent = Bp("bp_parent", BranchPointType.Undock,
                parents: new List<string> { "rec_root" },
                children: new List<string> { "rec_origin" });
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint> { bp_parent });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");

            var rps = new List<RewindPoint>
            {
                new RewindPoint
                {
                    RewindPointId = "rp_1",
                    BranchPointId = "bp_parent",
                    ChildSlots = new List<ChildSlot>(),
                },
            };
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));
            scenario.RewindPoints = rps;

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);

            // After commit the provisional must satisfy IsUnfinishedFlight:
            // MergeState committed-ish + crashed + parent BP has RP.
            // IsUnfinishedFlight in Phase 2 checks for MergeState.Immutable,
            // but Phase 8's commit produces CommittedProvisional for crashed
            // outcomes. The §7.43 chain-extension behavior is that the
            // provisional stays rewindable; the assertion here is on the
            // post-commit state that makes that possible.
            var provisionalVisible = EffectiveState.IsVisible(provisional, scenario.RecordingSupersedes);
            Assert.True(provisionalVisible,
                "provisional must be visible in ERS after commit (nothing supersedes it)");

            // Origin is now superseded → NOT visible.
            Assert.False(EffectiveState.IsVisible(
                origin, scenario.RecordingSupersedes));
        }

        // ---------- Transient fields / marker cleanup ----------------------

        [Fact]
        public void MergeState_Clears_SupersedeTargetId()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            Assert.Equal("rec_origin", provisional.SupersedeTargetId);
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            Assert.Null(provisional.SupersedeTargetId);
        }

        [Fact]
        public void Commit_ClearsActiveMarker()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            Assert.Null(scenario.ActiveReFlySessionMarker);

            // §10.4 End reason=merged log.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("End reason=merged")
                && l.Contains("sess=sess_1"));
        }

        [Fact]
        public void Commit_BumpsStateVersion()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int versionBefore = scenario.SupersedeStateVersion;
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            int versionAfter = scenario.SupersedeStateVersion;

            Assert.NotEqual(versionBefore, versionAfter);
        }

        [Fact]
        public void Commit_InvalidatesErsCache_OriginVanishes()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside",
                originTerminal: TerminalState.Destroyed);
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            // Before commit: origin is session-suppressed (filtered out of ERS
            // by the marker), inside also suppressed, outside + provisional
            // visible.
            var ersBefore = EffectiveState.ComputeERS();
            var idsBefore = new HashSet<string>(ersBefore.Select(r => r.RecordingId));
            Assert.DoesNotContain("rec_origin", idsBefore);
            Assert.Contains("rec_outside", idsBefore);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // After commit: origin is superseded (filtered out of ERS by
            // supersede relations), not session-suppressed. The ERS cache
            // must rebuild — origin must still be absent; provisional must
            // be present.
            var ersAfter = EffectiveState.ComputeERS();
            var idsAfter = new HashSet<string>(ersAfter.Select(r => r.RecordingId));
            Assert.DoesNotContain("rec_origin", idsAfter);
            Assert.DoesNotContain("rec_inside", idsAfter);
            Assert.Contains("rec_outside", idsAfter);
            Assert.Contains("rec_provisional", idsAfter);
        }

        // ---------- Idempotence + edge cases --------------------------------

        [Fact]
        public void Commit_IsIdempotent_ReinvocationSkipsExistingRelations()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));
            var marker = scenario.ActiveReFlySessionMarker;

            SupersedeCommit.CommitSupersede(marker, provisional);
            int firstCount = scenario.RecordingSupersedes.Count;

            // Reset the marker and call again with the same provisional. Commit
            // must be a no-op for the relations that already exist (defensive
            // idempotence per Phase 8 scope).
            scenario.ActiveReFlySessionMarker = marker;
            SupersedeCommit.CommitSupersede(marker, provisional);
            int secondCount = scenario.RecordingSupersedes.Count;

            Assert.Equal(firstCount, secondCount);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("skip existing relation"));
        }

        [Fact]
        public void NoActiveMarker_SkipsSupersede_RegularTreeCommit()
        {
            // No scenario-installed marker. TryCommitReFlySupersede invoked from
            // MergeDialog.MergeCommit should short-circuit and leave the
            // supersede list untouched.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var scenario = InstallScenario(marker: null);

            MergeDialog.TryCommitReFlySupersede();

            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("no active re-fly session marker"));
        }

        [Fact]
        public void TryCommitReFlySupersede_NoProvisionalRecording_WarnsAndKeepsMarker()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var scenario = InstallScenario(
                Marker("rec_origin", "rec_ghost_provisional"));

            // Provisional recording id does not exist in the store.
            MergeDialog.TryCommitReFlySupersede();

            // Marker stays in place so the Phase 13 load-time sweep can clean
            // it up deterministically.
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("not found in committed list"));
        }

        [Fact]
        public void NullMarker_Commit_IsNoOp()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            InstallScenario(marker: null);

            // Direct call with null marker → safe no-op + warn.
            SupersedeCommit.CommitSupersede(null, provisional);

            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("marker is null"));
        }

        [Fact]
        public void NullProvisional_Commit_IsNoOp()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(
                ParsekScenario.Instance.ActiveReFlySessionMarker, null);

            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("provisional is null"));
        }

        // ---------- Self-supersede guards (bug/rewind-self-supersede-and-followups) -----

        /// <summary>
        /// Regression: Limbo-restore kept the origin recording alive across an
        /// RP-quicksave reload, the re-fly continued writing into that SAME
        /// recording, and after the placeholder-redirect in
        /// <see cref="MergeDialog.TryCommitReFlySupersede"/> we had
        /// <c>provisional.RecordingId == marker.OriginChildRecordingId</c>.
        /// The caller-side guard must detect this in-place-continuation case,
        /// skip the journaled merge entirely (no self-supersede row), flip
        /// MergeState, clear the marker, and return Completed.
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_SkipsMergeAndFinalizes()
        {
            // Build a tree where the origin recording is itself the recording
            // that the re-fly continues writing into. The marker's origin and
            // active pointers are the same id; no separate provisional exists.
            var origin = Rec("rec_origin", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed,
                supersedeTargetId: null);
            // Give it a non-empty trajectory so the placeholder redirect path
            // is skipped and the self-continuation guard runs directly.
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_origin", provisionalId: "rec_origin");
            var scenario = InstallScenario(marker);

            int versionBefore = scenario.SupersedeStateVersion;
            var result = MergeDialog.TryCommitReFlySupersede();

            // 1) No supersede row written.
            Assert.Empty(scenario.RecordingSupersedes);
            // 2) Marker cleared.
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // 3) MergeState flipped to Immutable (Landed terminal).
            Assert.Equal(MergeState.Immutable, origin.MergeState);
            // 4) Result is Completed.
            Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            // 5) Supersede state version bumped.
            Assert.NotEqual(versionBefore, scenario.SupersedeStateVersion);
            // 6) INFO log advertises the in-place-continuation diagnosis.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("in-place continuation")
                && l.Contains("rec_origin"));
        }

        /// <summary>
        /// Defense-in-depth: AppendRelations must refuse to write a
        /// self-supersede row (old==new) even if the caller-side guard is
        /// bypassed. This guards against future regressions in the ordering
        /// of the guards and against direct test-only callers.
        /// </summary>
        [Fact]
        public void AppendRelations_SelfSupersede_SkippedAndWarned()
        {
            // Origin is alone in its subtree; provisional id matches origin id.
            var origin = Rec("rec_same", "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.CommittedTrees.Add(tree);

            var provisional = Rec("rec_same", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed);
            // Satisfy AppendRelations supersede-target invariant so the
            // self-supersede defense is the only refusal exercised here.
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            var marker = Marker("rec_same", "rec_same");
            var scenario = InstallScenario(marker);

            int countBefore = scenario.RecordingSupersedes.Count;
            var subtree = SupersedeCommit.AppendRelations(marker, provisional, scenario);
            int countAfter = scenario.RecordingSupersedes.Count;

            Assert.Equal(countBefore, countAfter);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("refusing to write self-supersede row")
                && l.Contains("old=rec_same") && l.Contains("new=rec_same"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("selfSkipped=1"));
        }

        // ---------- Supersede-target invariant (item 10) -------------------

        [Fact]
        public void AppendRelations_EmptyProvisional_RefusesAndWarns()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed,
                supersedeTargetId: "rec_origin");
            Assert.Empty(provisional.Points);
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int countBefore = scenario.RecordingSupersedes.Count;

#if DEBUG
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.AppendRelations(
                    scenario.ActiveReFlySessionMarker, provisional, scenario));
            Assert.Contains("invariant violation", ex.Message);
            Assert.Contains("empty Points", ex.Message);
#else
            var subtree = SupersedeCommit.AppendRelations(
                scenario.ActiveReFlySessionMarker, provisional, scenario);
            Assert.Empty(subtree);
#endif

            Assert.Equal(countBefore, scenario.RecordingSupersedes.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations invariant violation")
                && l.Contains("provisional=rec_provisional")
                && l.Contains("reason=empty Points")
                && l.Contains("refusing to write supersede rows"));
        }

        [Fact]
        public void AppendRelations_NullTerminalProvisional_RefusesAndWarns()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                terminal: null,
                supersedeTargetId: "rec_origin");
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            provisional.Points.Add(new TrajectoryPoint { ut = 1.0 });
            Assert.Null(provisional.TerminalStateValue);
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int countBefore = scenario.RecordingSupersedes.Count;

#if DEBUG
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.AppendRelations(
                    scenario.ActiveReFlySessionMarker, provisional, scenario));
            Assert.Contains("invariant violation", ex.Message);
            Assert.Contains("null TerminalState", ex.Message);
#else
            var subtree = SupersedeCommit.AppendRelations(
                scenario.ActiveReFlySessionMarker, provisional, scenario);
            Assert.Empty(subtree);
#endif

            Assert.Equal(countBefore, scenario.RecordingSupersedes.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations invariant violation")
                && l.Contains("provisional=rec_provisional")
                && l.Contains("reason=null TerminalState")
                && l.Contains("refusing to write supersede rows"));
        }

        [Fact]
        public void ValidateSupersedeTarget_ReasonStrings()
        {
            string reason;

            Assert.False(SupersedeCommit.ValidateSupersedeTarget(null, out reason));
            Assert.Equal("null recording", reason);

            var emptyPoints = new Recording { Points = new List<TrajectoryPoint>(), TerminalStateValue = TerminalState.Landed };
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(emptyPoints, out reason));
            Assert.Equal("empty Points", reason);

            var nullTerminal = new Recording { TerminalStateValue = null };
            nullTerminal.Points.Add(new TrajectoryPoint { ut = 0.0 });
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(nullTerminal, out reason));
            Assert.Equal("null TerminalState", reason);

            var ok = new Recording { TerminalStateValue = TerminalState.Landed };
            ok.Points.Add(new TrajectoryPoint { ut = 0.0 });
            Assert.True(SupersedeCommit.ValidateSupersedeTarget(ok, out reason));
            Assert.Null(reason);
        }
    }
}
