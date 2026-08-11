using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REFLY-CONCLUSION-SKIPS-APPENDRELATIONS (found 2026-08-11 by S4.2's first
    /// flight): a Re-Fly session that ends without flying reached the merge with
    /// its provisional deleted out from under the durable marker, so
    /// <c>MergeDialog.TryCommitReFlySupersede</c> bailed one step ABOVE
    /// <c>SupersedeCommit.AppendRelations</c> and the supersede / tombstone
    /// machinery never ran; the session retired through the next load's zombie
    /// sweep instead of through the named refusal.
    ///
    /// <para>
    /// These cells pin both halves of the fix: the prune hands the retired
    /// provisional over (and refuses to delete one the merge would accept), and
    /// the conclusion classifies the absence and concludes IN-SESSION through the
    /// same <c>outcome=refused-unflown-provisional</c> branch.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyConclusionRouteTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ReFlyConclusionRouteTests()
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
            ReFlyProvisionalRetirement.ResetForTesting();
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
            ReFlyProvisionalRetirement.ResetForTesting();
        }

        // ---------- Helpers -------------------------------------------------

        private static ReFlySessionMarker Marker(
            string provisionalId = "rec_provisional",
            string originId = "rec_origin",
            string sessionId = "sess_1",
            string treeId = "tree_1")
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
                PreSessionBranchPointIds = new List<string>(),
            };
        }

        private static Recording Rec(
            string id,
            string treeId = "tree_1",
            MergeState state = MergeState.Immutable,
            TerminalState? terminal = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                TerminalStateValue = terminal,
            };
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

        private static RecordingTree TreeWith(params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
                tree.AddOrReplaceRecording(rec);
            if (recordings.Length > 0)
                tree.RootRecordingId = recordings[0].RecordingId;
            return tree;
        }

        // ---------- Pure classifier: the conclusion side --------------------

        [Fact]
        public void Classify_NoMarker_LeavesForSweep()
        {
            var decision = ReFlyConclusionRoute.Classify(
                null, "rec_provisional", Rec("rec_provisional"), false);

            Assert.Equal(ReFlyConclusionRouteKind.LeaveForSweep, decision.Kind);
            Assert.Equal("no-marker", decision.Reason);
        }

        [Fact]
        public void Classify_NothingRetired_LeavesForSweep()
        {
            // The pre-fix behaviour is preserved for a genuinely unexplained
            // absence: nothing in this session accounts for the missing
            // provisional, so the marker survives and the load-time sweep owns it.
            var decision = ReFlyConclusionRoute.Classify(
                Marker(), "rec_provisional", null, false);

            Assert.Equal(ReFlyConclusionRouteKind.LeaveForSweep, decision.Kind);
            Assert.Equal("no-retired-provisional", decision.Reason);
        }

        [Fact]
        public void Classify_RetiredIdDoesNotMatchTheMissingId_LeavesForSweep()
        {
            var decision = ReFlyConclusionRoute.Classify(
                Marker(), "rec_provisional", Rec("rec_someone_else"), false);

            Assert.Equal(ReFlyConclusionRouteKind.LeaveForSweep, decision.Kind);
            Assert.Equal("retired-provisional-id-mismatch", decision.Reason);
        }

        [Fact]
        public void Classify_RetiredProvisionalThatWouldValidate_LeavesForSweep()
        {
            // A retired recording the merge WOULD accept is real payload the
            // session owes a supersede for. Concluding it as a no-op would
            // silently drop a legitimate merge, so it must not take this route.
            var decision = ReFlyConclusionRoute.Classify(
                Marker(), "rec_provisional", Rec("rec_provisional"),
                retiredValidatesAsSupersedeTarget: true);

            Assert.Equal(ReFlyConclusionRouteKind.LeaveForSweep, decision.Kind);
            Assert.Equal("retired-provisional-owes-supersede", decision.Reason);
        }

        [Fact]
        public void Classify_RetiredEmptyProvisional_TakesTheNoOpConclusion()
        {
            var decision = ReFlyConclusionRoute.Classify(
                Marker(), "rec_provisional", Rec("rec_provisional"), false);

            Assert.Equal(ReFlyConclusionRouteKind.NoOpConclusion, decision.Kind);
            Assert.Equal("retired-empty-provisional", decision.Reason);
        }

        // ---------- Pure classifier: the prune side -------------------------

        // The expected action travels as a string because ReFlyProvisionalPruneAction
        // is internal and xUnit test methods must be public.
        [Theory]
        [InlineData("rec_other", false, "NotTheProvisional")]
        [InlineData("rec_other", true, "NotTheProvisional")]
        [InlineData("rec_provisional", false, "RetireEmpty")]
        [InlineData("rec_provisional", true, "KeepOwesSupersede")]
        public void ClassifyProvisionalPrune_SplitsOnIdentityThenPayload(
            string prunedId, bool validates, string expected)
        {
            Assert.Equal(expected,
                ReFlyConclusionRoute.ClassifyProvisionalPrune(Marker(), prunedId, validates)
                    .ToString());
        }

        [Fact]
        public void ClassifyProvisionalPrune_NoMarker_IsNeverTheProvisional()
        {
            Assert.Equal("NotTheProvisional",
                ReFlyConclusionRoute.ClassifyProvisionalPrune(null, "rec_provisional", false)
                    .ToString());
        }

        // ---------- The hand-over slot --------------------------------------

        [Fact]
        public void Retirement_TryTake_IsSingleShotAndSessionScoped()
        {
            var marker = Marker();
            var retired = Rec("rec_provisional");
            ReFlyProvisionalRetirement.Note(marker, retired, "PruneZeroPointLeaves");

            // A different session must never adopt this note as its conclusion.
            Recording taken;
            string reason;
            Assert.False(ReFlyProvisionalRetirement.TryTake(
                Marker(sessionId: "sess_other"), out taken, out reason));
            Assert.Null(taken);

            Assert.True(ReFlyProvisionalRetirement.TryTake(marker, out taken, out reason));
            Assert.Same(retired, taken);
            Assert.Equal("PruneZeroPointLeaves", reason);

            // Single-shot: the slot is cleared by the take.
            Assert.False(ReFlyProvisionalRetirement.TryTake(marker, out taken, out reason));
            Assert.Null(taken);
        }

        // ---------- Prune integration ---------------------------------------

        [Fact]
        public void PruneZeroPointLeaves_RetiresTheSessionProvisionalAndHandsItOver()
        {
            var marker = Marker();
            InstallScenario(marker);

            var provisional = Rec("rec_provisional",
                state: MergeState.NotCommitted, terminal: TerminalState.Landed);
            var tree = TreeWith(Rec("rec_origin", terminal: TerminalState.Destroyed), provisional);
            tree.Recordings["rec_origin"].Points.Add(new TrajectoryPoint { ut = 0.0 });

            ParsekFlight.PruneZeroPointLeaves(tree);

            // Pruned exactly as before — the tree keeps no zero-point junk...
            Assert.False(tree.Recordings.ContainsKey("rec_provisional"));
            // ...but the conclusion can now name what happened to it.
            Recording taken;
            string reason;
            Assert.True(ReFlyProvisionalRetirement.TryTake(marker, out taken, out reason));
            Assert.Same(provisional, taken);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("outcome=retired-empty-provisional")
                && l.Contains("rec=rec_provisional")
                && l.Contains("sess=sess_1"));
            Assert.Contains(logLines, l =>
                l.Contains("PruneZeroPointLeaves: removed 1")
                && l.Contains("reFlyProvisionalRetired=1"));
        }

        [Fact]
        public void PruneZeroPointLeaves_KeepsAProvisionalTheMergeWouldAccept()
        {
            // IsZeroPointLeaf (Points + OrbitSegments + SurfacePos) and
            // ValidateSupersedeTarget (Points + OrbitSegments + playable
            // TrackSections) are NOT the same predicate: a section-authoritative
            // provisional is zero-point AND a valid supersede target. The hygiene
            // pass must defer to the merge's test, never delete real payload.
            var marker = Marker();
            InstallScenario(marker);

            var provisional = Rec("rec_provisional",
                state: MergeState.NotCommitted, terminal: TerminalState.Landed);
            var section = new TrackSection
            {
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 0.0 },
                    new TrajectoryPoint { ut = 1.0 },
                },
            };
            provisional.TrackSections.Add(section);

            var tree = TreeWith(Rec("rec_origin", terminal: TerminalState.Destroyed), provisional);
            tree.Recordings["rec_origin"].Points.Add(new TrajectoryPoint { ut = 0.0 });

            ParsekFlight.PruneZeroPointLeaves(tree);

            Assert.True(tree.Recordings.ContainsKey("rec_provisional"));
            Recording taken;
            string reason;
            Assert.False(ReFlyProvisionalRetirement.TryTake(marker, out taken, out reason));
            Assert.Contains(logLines, l =>
                l.Contains("outcome=refly-provisional-prune-kept")
                && l.Contains("rec=rec_provisional"));
        }

        [Fact]
        public void PruneZeroPointLeaves_OrdinaryEmptyLeafIsUntouchedByTheGuard()
        {
            var marker = Marker();
            InstallScenario(marker);

            var tree = TreeWith(
                Rec("rec_origin", terminal: TerminalState.Destroyed),
                Rec("rec_debris", terminal: TerminalState.Destroyed));
            tree.Recordings["rec_origin"].Points.Add(new TrajectoryPoint { ut = 0.0 });

            ParsekFlight.PruneZeroPointLeaves(tree);

            Assert.False(tree.Recordings.ContainsKey("rec_debris"));
            Recording taken;
            string reason;
            Assert.False(ReFlyProvisionalRetirement.TryTake(marker, out taken, out reason));
        }

        // ---------- The named conclusion ------------------------------------

        [Fact]
        public void ConcludeRetiredProvisional_TakesTheNamedRefusalAndClearsTheSession()
        {
            var marker = Marker();
            var scenario = InstallScenario(marker);
            RecordingStore.AddRecordingWithTreeForTesting(
                Rec("rec_origin", terminal: TerminalState.Destroyed), "tree_1");

            var retired = Rec("rec_provisional",
                state: MergeState.NotCommitted, terminal: TerminalState.Landed);
            // The pre-Re-Fly anchor is a COPY of the origin's pre-rewind tail, not
            // the attempt's own flight. It must not read as "it flew".
            retired.PreReFlyAnchorPoints = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 0.0 },
                new TrajectoryPoint { ut = 1.0 },
                new TrajectoryPoint { ut = 2.0 },
                new TrajectoryPoint { ut = 3.0 },
            };

            Assert.True(SupersedeCommit.ConcludeRetiredProvisional(
                marker, retired, "PruneZeroPointLeaves"));

            // The supersede / tombstone machinery RAN and decided: 0 rows, 0
            // tombstones, origin stays effective.
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Empty(scenario.LedgerTombstones);
            Assert.Null(scenario.ActiveReFlySessionMarker);

            // The refusal is the SAME grep-stable token the in-tree route emits —
            // this route is not a second way of saying the same thing.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations outcome=refused-unflown-provisional")
                && l.Contains("provisional=rec_provisional")
                && l.Contains("reason=empty Points")
                && l.Contains("points=0")
                && l.Contains("preReFlyAnchorPoints=4"));
            Assert.Contains(logLines, l =>
                l.Contains("outcome=concluded-no-supersede")
                && l.Contains("rows=0")
                && l.Contains("tombstones=0"));
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=concluded-no-supersede")
                && l.Contains("sess=sess_1"));
        }

        [Fact]
        public void ConcludeRetiredProvisional_RefusesWhenTheRetiredRecordingWouldValidate()
        {
            var marker = Marker();
            var scenario = InstallScenario(marker);
            RecordingStore.AddRecordingWithTreeForTesting(
                Rec("rec_origin", terminal: TerminalState.Destroyed), "tree_1");

            var retired = Rec("rec_provisional",
                state: MergeState.NotCommitted, terminal: TerminalState.Landed);
            retired.Points.Add(new TrajectoryPoint { ut = 0.0 });

            Assert.False(SupersedeCommit.ConcludeRetiredProvisional(
                marker, retired, "PruneZeroPointLeaves"));

            // Nothing written, marker preserved for the load-time sweep.
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("ConcludeRetiredProvisional: refusing the no-op route"));
        }

        // ---------- End-to-end through the commit branch --------------------

        [Fact]
        public void TryCommitReFlySupersede_RetiredProvisional_ConcludesInSession()
        {
            var marker = Marker();
            var scenario = InstallScenario(marker);
            RecordingStore.AddRecordingWithTreeForTesting(
                Rec("rec_origin", terminal: TerminalState.Destroyed), "tree_1");

            var retired = Rec("rec_provisional",
                state: MergeState.NotCommitted, terminal: TerminalState.Landed);
            ReFlyProvisionalRetirement.Note(marker, retired, "PruneZeroPointLeaves");

            var result = MergeDialog.TryCommitReFlySupersede();

            Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("AppendRelations outcome=refused-unflown-provisional"));
            // The pre-fix cascade must be gone: no bail WARN, so no zombie marker
            // for the next load's sweep to clear.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("not found in committed list after tree commit"));
        }

        [Fact]
        public void TryCommitReFlySupersede_UnexplainedAbsence_StillLeavesTheMarkerForTheSweep()
        {
            var marker = Marker();
            var scenario = InstallScenario(marker);

            var result = MergeDialog.TryCommitReFlySupersede();

            Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Interrupted, result);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("not found in committed list after tree commit")
                && l.Contains("route=no-retired-provisional"));
        }

        // ---------- The predicate the finding asked about -------------------

        [Fact]
        public void DescribeSupersedePayload_SeparatesFlightPayloadFromTheReFlyAnchor()
        {
            // The exact misread the finding warned about: the provisional logged
            // `PRE_REFLY_ANCHOR written: points=4` while carrying zero trajectory
            // points of its own. Both numbers now print side by side so nobody has
            // to guess which surface "4" came from.
            var rec = Rec("rec_provisional", terminal: TerminalState.Landed);
            rec.PreReFlyAnchorPoints = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 0.0 },
                new TrajectoryPoint { ut = 1.0 },
            };

            string described = SupersedeCommit.DescribeSupersedePayload(rec);

            Assert.Contains("points=0", described);
            Assert.Contains("preReFlyAnchorPoints=2", described);
            Assert.Contains("playableSections=0", described);
            Assert.Contains("terminal=Landed", described);

            string reason;
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(rec, out reason));
            Assert.Equal("empty Points", reason);
        }
    }
}
