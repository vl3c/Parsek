using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug fix-refly-abandon-and-fork-persist §Bug1 defenses-in-depth:
    /// the closure walk's NotCommitted skips in
    /// <see cref="EffectiveState.EnqueueChainSiblings"/> +
    /// <see cref="EffectiveState.EnqueuePidPeerSiblings"/>, and the row-write
    /// guard in <see cref="SupersedeCommit.AppendRelations"/>. These layers
    /// fire only when the primary fix
    /// (<see cref="RewindInvoker.ReapPriorProvisionalsForRp"/>) failed to
    /// remove an orphan — without these tests the defenses could be silently
    /// dropped by a future refactor and only the primary would have to fail
    /// for the bug to recur.
    /// </summary>
    [Collection("Sequential")]
    public class AppendRelationsNotCommittedGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public AppendRelationsNotCommittedGuardTests()
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

        private static Recording Rec(string id, string treeId,
            MergeState state = MergeState.Immutable,
            string chainId = null, int chainIndex = 0, int chainBranch = 0,
            uint vesselPid = 0, double startUT = 0.0,
            TerminalState? terminal = null,
            string sessionId = null,
            string supersedeTargetId = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = chainBranch,
                VesselPersistentId = vesselPid,
                TerminalStateValue = terminal,
                CreatingSessionId = sessionId,
                SupersedeTargetId = supersedeTargetId,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            return rec;
        }

        private static void InstallTree(string treeId, List<Recording> recordings)
        {
            var tree = new RecordingTree { Id = treeId, TreeName = "Test_" + treeId };
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

        [Fact]
        public void EnqueuePidPeerSiblings_SkipsNotCommittedPeer_LogsWarn()
        {
            // The pre-rewind launch HEAD is the closure root (origin). The
            // post-rewind TIP shares its vesselPersistentId so the pid-peer
            // walk would normally enqueue it. We add a NotCommitted ZOMBIE
            // peer with the same PID that the primary reap failed to remove
            // — the closure walk must skip it with a Warn.
            //
            // pid=42; both TIP and zombie are session-post-rewind peers.
            const uint pid = 42u;
            var origin = Rec("rec_origin", "tree_1",
                vesselPid: pid, startUT: 0.0, terminal: TerminalState.Destroyed);
            var tip = Rec("rec_tip", "tree_1",
                vesselPid: pid, startUT: 100.0, terminal: TerminalState.Destroyed);
            var zombie = Rec("rec_zombie", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 100.0,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_abandoned",
                supersedeTargetId: "rec_origin");
            InstallTree("tree_1", new List<Recording> { origin, tip, zombie });
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_new",
                TreeId = "tree_1",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                ActiveReFlyRecordingId = "rec_provisional",
                InvokedUT = 100.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 100.0,
                terminal: TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            // No supersede row points FROM the NotCommitted zombie.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_zombie");
            // The pid-peer walk's NotCommitted-skip Warn fired.
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Supersede]") &&
                l.Contains("EnqueuePidPeerSiblings: skipped NotCommitted peer") &&
                l.Contains("rec=rec_zombie") &&
                l.Contains("sess=sess_abandoned"));
        }

        [Fact]
        public void EnqueueChainSiblings_SkipsNotCommittedSibling_LogsWarn()
        {
            // Chain-id-based sibling walk: origin lives in a chain with one
            // additional sibling at ChainIndex=1. The sibling is NotCommitted
            // (zombie shape) and must be skipped with a Warn.
            const string chain = "chain_a";
            var origin = Rec("rec_origin", "tree_1",
                chainId: chain, chainIndex: 0, terminal: TerminalState.Destroyed);
            var zombieSibling = Rec("rec_zombie", "tree_1",
                state: MergeState.NotCommitted,
                chainId: chain, chainIndex: 1,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_abandoned",
                supersedeTargetId: "rec_origin");
            InstallTree("tree_1", new List<Recording> { origin, zombieSibling });
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_new",
                TreeId = "tree_1",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                ActiveReFlyRecordingId = "rec_provisional",
                InvokedUT = 0.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                chainId: chain, chainIndex: 2,
                terminal: TerminalState.Destroyed,
                supersedeTargetId: "rec_origin");
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            // No supersede row points FROM the NotCommitted sibling.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_zombie");
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Supersede]") &&
                l.Contains("EnqueueChainSiblings: skipped NotCommitted peer") &&
                l.Contains("rec=rec_zombie") &&
                l.Contains($"chain={chain}") &&
                l.Contains("sess=sess_abandoned"));
        }

        // ---------------------------------------------------------------
        // CL-3 false-positive regression (harness runs 2026-08-03_2147 /
        // 2026-08-04_1327 / _1447, all three logging exactly one warn whose
        // rec= and sess= equalled the run's OWN `ReFlySession Started`
        // provisional= and sess=). The in-place-continuation fork inherits the
        // origin's pid via RewindInvoker.CopyInheritedIdentityForFork, so it
        // satisfies every EnqueuePidPeerSiblings gate when the walk dequeues
        // the origin, and was reported as a reap leak on every single re-fly.
        // It is not one: ReapPriorProvisionalsForRp deliberately skips
        // same-session victims and LoadTimeSweep keeps live-marker sessions.
        // ---------------------------------------------------------------

        [Fact]
        public void EnqueuePidPeerSiblings_SkipsActiveSessionProvisional_WithoutWarn()
        {
            // Exactly the CL-3 shape: ONE session, no abandoned predecessor.
            // The provisional shares the origin's pid (in-place continuation)
            // and starts at the rewind UT, so it reaches the NotCommitted
            // guard — but it is this session's own fork, so no Warn may fire.
            const uint pid = 1917208454u;
            var origin = Rec("rec_origin", "tree_1",
                vesselPid: pid, startUT: 0.0, terminal: TerminalState.Destroyed);
            InstallTree("tree_1", new List<Recording> { origin });
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_live",
                TreeId = "tree_1",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                ActiveReFlyRecordingId = "rec_provisional",
                InvokedUT = 100.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 100.0,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_live",
                supersedeTargetId: "rec_origin");
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            // The false-positive Warn must be gone entirely.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][WARN][Supersede]") &&
                l.Contains("EnqueuePidPeerSiblings: skipped NotCommitted peer"));
            // The expected-case skip is still observable at Verbose.
            Assert.Contains(logLines, l =>
                l.Contains("EnqueuePidPeerSiblings: skipped active-session provisional") &&
                l.Contains("rec=rec_provisional"));
            // And the fork never becomes a supersede source (no self-row).
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_provisional");
        }

        [Fact]
        public void EnqueueChainSiblings_SkipsActiveSessionProvisional_WithoutWarn()
        {
            // Chain-walk symmetry: a same-session provisional that shares the
            // origin's ChainId must also skip silently rather than reporting a
            // reap leak. (The current in-place fork does not inherit ChainId,
            // but chain promotion during the re-fly can give it one.)
            const string chain = "chain_a";
            var origin = Rec("rec_origin", "tree_1",
                chainId: chain, chainIndex: 0, terminal: TerminalState.Destroyed);
            InstallTree("tree_1", new List<Recording> { origin });
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_live",
                TreeId = "tree_1",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                ActiveReFlyRecordingId = "rec_provisional",
                InvokedUT = 0.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                chainId: chain, chainIndex: 1,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_live",
                supersedeTargetId: "rec_origin");
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][WARN][Supersede]") &&
                l.Contains("EnqueueChainSiblings: skipped NotCommitted peer"));
            Assert.Contains(logLines, l =>
                l.Contains("EnqueueChainSiblings: skipped active-session provisional") &&
                l.Contains("rec=rec_provisional"));
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_provisional");
        }

        [Fact]
        public void EnqueuePidPeerSiblings_SkipsOptimizerSplitTailOfTheLiveSessionsFork()
        {
            // Pins the SECOND disjunct of IsActiveSessionProvisional
            // (CreatingSessionId == marker.SessionId) on the population that is
            // the only reason it exists.
            //
            // RecordingStore.Optimization's CopySplitIdentityFields gives the
            // second half of a split a FRESH RecordingId (Guid.NewGuid) while
            // copying CreatingSessionId, ProvisionalForRpId AND
            // VesselPersistentId from the original. Split a session-tagged
            // provisional and the tail therefore reaches this guard with the
            // fork's pid but an id that is NOT marker.ActiveReFlyRecordingId —
            // so the id disjunct misses it and only the session disjunct spares
            // it. MergeJournalOrchestrator documents the same population for the
            // same reason ("sweep every recording tagged with this session, not
            // just the ActiveReFlyRecordingId").
            //
            // Without this test, deleting the session disjunct leaves the whole
            // suite green while every optimizer-split re-fly resumes emitting the
            // false-positive "investigate" Warn this fix exists to close.
            const uint pid = 42u;
            var origin = Rec("rec_origin", "tree_1",
                vesselPid: pid, startUT: 0.0, terminal: TerminalState.Destroyed);
            // The split tail: same session, same pid, DIFFERENT recording id.
            var splitTail = Rec("rec_split_tail", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 120.0,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_live",
                supersedeTargetId: "rec_origin");
            InstallTree("tree_1", new List<Recording> { origin, splitTail });
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_live",
                TreeId = "tree_1",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                ActiveReFlyRecordingId = "rec_provisional",
                InvokedUT = 100.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 100.0,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_live",
                supersedeTargetId: "rec_origin");
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            // The tail is NOT an orphan: no reap-leak Warn may name it.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][WARN][Supersede]") &&
                l.Contains("EnqueuePidPeerSiblings: skipped NotCommitted peer") &&
                l.Contains("rec=rec_split_tail"));
            // It was reached and spared by the session disjunct specifically —
            // this line cannot appear via the ActiveReFlyRecordingId disjunct,
            // because rec_split_tail is not that id.
            Assert.Contains(logLines, l =>
                l.Contains("EnqueuePidPeerSiblings: skipped active-session provisional") &&
                l.Contains("rec=rec_split_tail"));
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_split_tail");
        }

        [Fact]
        public void IsActiveSessionProvisional_SessionDisjunctIsIndependentOfTheIdDisjunct()
        {
            // Direct unit cover on the predicate, so the two disjuncts cannot both
            // be satisfied by one fixture and mask each other's removal.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_live",
                ActiveReFlyRecordingId = "rec_provisional",
            };
            // id matches, session does NOT
            Assert.True(EffectiveState.IsActiveSessionProvisional(
                Rec("rec_provisional", "tree_1", sessionId: "sess_other"), marker));
            // session matches, id does NOT (the optimizer-split tail)
            Assert.True(EffectiveState.IsActiveSessionProvisional(
                Rec("rec_split_tail", "tree_1", sessionId: "sess_live"), marker));
            // neither matches -> a genuine foreign-session orphan
            Assert.False(EffectiveState.IsActiveSessionProvisional(
                Rec("rec_zombie", "tree_1", sessionId: "sess_abandoned"), marker));
            // an untagged recording is never spared by the session disjunct
            Assert.False(EffectiveState.IsActiveSessionProvisional(
                Rec("rec_untagged", "tree_1"), marker));
            // a marker with no session id must not spare every untagged recording
            Assert.False(EffectiveState.IsActiveSessionProvisional(
                Rec("rec_untagged", "tree_1"),
                new ReFlySessionMarker { SessionId = null, ActiveReFlyRecordingId = "x" }));
        }

        [Fact]
        public void EnqueuePidPeerSiblings_StillWarnsForForeignSessionOrphan()
        {
            // The §Bug1 signal must survive the false-positive fix: a genuine
            // orphan from an ABANDONED session (different CreatingSessionId,
            // not the marker's ActiveReFlyRecordingId) still warns, and the
            // live session's own fork alongside it still does not.
            const uint pid = 42u;
            var origin = Rec("rec_origin", "tree_1",
                vesselPid: pid, startUT: 0.0, terminal: TerminalState.Destroyed);
            var zombie = Rec("rec_zombie", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 100.0,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_abandoned",
                supersedeTargetId: "rec_origin");
            InstallTree("tree_1", new List<Recording> { origin, zombie });
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_live",
                TreeId = "tree_1",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                ActiveReFlyRecordingId = "rec_provisional",
                InvokedUT = 100.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 100.0,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_live",
                supersedeTargetId: "rec_origin");
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            // Genuine orphan: warn fires, naming the abandoned session.
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Supersede]") &&
                l.Contains("EnqueuePidPeerSiblings: skipped NotCommitted peer") &&
                l.Contains("rec=rec_zombie") &&
                l.Contains("sess=sess_abandoned"));
            // Live fork: no warn names it.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Parsek][WARN][Supersede]") &&
                l.Contains("EnqueuePidPeerSiblings: skipped NotCommitted peer") &&
                l.Contains("rec=rec_provisional"));
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_zombie");
        }

        [Fact]
        public void AppendRelations_RefusesRowWriteWhenOldIsNotCommitted()
        {
            // Construct a scenario where the closure root itself is a
            // NotCommitted recording. The row-write guard at the bottom
            // of the closure loop is the last line of defense.
            //
            // Debug builds throw InvalidOperationException; Release builds
            // log the Warn and skip the row. This test runs under whatever
            // configuration the test assembly is built with — assert both
            // behaviors and let the compile-time #if pick.
            const uint pid = 99u;
            // Origin is itself NotCommitted: a session-suppressed closure
            // would normally include origin via the PID-peer walk. The
            // origin is the closure root, so the closure includes it; the
            // row-write guard must refuse to write a row with origin's id
            // as oldRecordingId.
            var notCommittedOrigin = Rec("rec_origin", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 0.0,
                terminal: TerminalState.Destroyed,
                sessionId: "sess_orphan",
                supersedeTargetId: null);
            InstallTree("tree_1", new List<Recording> { notCommittedOrigin });
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_new",
                TreeId = "tree_1",
                OriginChildRecordingId = "rec_origin",
                SupersedeTargetId = "rec_origin",
                ActiveReFlyRecordingId = "rec_provisional",
                InvokedUT = 0.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted, vesselPid: pid, startUT: 100.0,
                terminal: TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            // Origin is the subtree root and is NotCommitted, so the closure
            // walk includes its id. None of the upstream defenses fire here
            // (origin is the closure root, not a chain-peer or PID-peer
            // candidate to be enqueued), so the row-write guard is the last
            // line of defense.
#if DEBUG
            // Debug build: row-write guard throws InvalidOperationException
            // and the merge aborts. A developer build crashes loudly so the
            // upstream invariant violation is impossible to miss.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.AppendRelations(marker, provisional, scenario));
            Assert.Contains("refusing row old=rec_origin", ex.Message);
            Assert.Contains("because old is NotCommitted", ex.Message);
            Assert.Contains("sess=sess_orphan", ex.Message);
#else
            // Release build: row-write guard warn-and-skips. No exception;
            // no invalid row written; Warn line logged.
            SupersedeCommit.AppendRelations(marker, provisional, scenario);
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin");
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][Supersede]") &&
                l.Contains("refusing row old=rec_origin") &&
                l.Contains("because old is NotCommitted") &&
                l.Contains("sess=sess_orphan"));
#endif
        }
    }
}
