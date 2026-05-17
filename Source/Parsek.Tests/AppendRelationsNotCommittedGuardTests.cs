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
