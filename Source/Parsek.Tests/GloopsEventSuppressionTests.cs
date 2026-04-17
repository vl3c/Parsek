using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #432 — Gloops ghost-only recordings must contribute zero actions to the ledger.
    /// Covers the two action-creation guards in <see cref="LedgerOrchestrator"/>
    /// (<c>CreateKerbalAssignmentActions</c>, <c>CreateVesselCostActions</c>), the
    /// belt-and-braces <c>PurgeGhostOnlyActionsFromLedger</c> pre-pass, the walk-integration
    /// log signal from <c>RecalculateAndPatch</c>, the self-heal path through
    /// <c>MigrateKerbalAssignments</c> → <c>OnKspLoad</c>, and the invariant that
    /// events never get tagged with a Gloops recording's id in the first place.
    /// </summary>
    [Collection("Sequential")]
    public class GloopsEventSuppressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GloopsEventSuppressionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateRecorder.TagResolverForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording MakeCrewedRecording(string id, bool ghostOnly)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Jeb Kerman");
            snapshot.AddNode(part);

            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Test Ship",
                GhostVisualSnapshot = snapshot,
                IsGhostOnly = ghostOnly,
            };
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb Kerman", KerbalEndState.Aboard }
            };
            return rec;
        }

        // ================================================================
        // 1. CreateKerbalAssignmentActions — ghost-only returns empty
        // ================================================================

        [Fact]
        public void CreateKerbalAssignmentActions_GhostOnlyRecording_ReturnsEmpty()
        {
            var rec = MakeCrewedRecording("gloops-kerbal-empty", ghostOnly: true);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions(
                "gloops-kerbal-empty", 100.0, 200.0);

            Assert.Empty(actions);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("CreateKerbalAssignmentActions")
                && l.Contains("gloops-kerbal-empty")
                && l.Contains("ghost-only"));
        }

        // ================================================================
        // 2. CreateKerbalAssignmentActions — normal recording still produces rows
        // ================================================================

        [Fact]
        public void CreateKerbalAssignmentActions_NormalRecording_ProducesRow()
        {
            var rec = MakeCrewedRecording("normal-kerbal-row", ghostOnly: false);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateKerbalAssignmentActions(
                "normal-kerbal-row", 100.0, 200.0);

            Assert.Single(actions);
            Assert.Equal(GameActionType.KerbalAssignment, actions[0].Type);
            Assert.Equal("Jeb Kerman", actions[0].KerbalName);
            Assert.Equal("normal-kerbal-row", actions[0].RecordingId);
        }

        // ================================================================
        // 3. CreateVesselCostActions — ghost-only returns empty
        // ================================================================

        [Fact]
        public void CreateVesselCostActions_GhostOnlyRecording_ReturnsEmpty()
        {
            var rec = new Recording
            {
                RecordingId = "gloops-cost-empty",
                IsGhostOnly = true,
                // Values that would normally produce both build cost and recovery.
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 300.0, funds = 47000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "gloops-cost-empty", 100.0, 300.0);

            Assert.Empty(actions);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("CreateVesselCostActions")
                && l.Contains("gloops-cost-empty")
                && l.Contains("ghost-only"));
        }

        // ================================================================
        // 4. PurgeGhostOnlyActionsFromLedger — mutates Ledger.Actions in place
        // ================================================================

        [Fact]
        public void PurgeGhostOnlyActionsFromLedger_DropsGhostOnlyRowsAndMutatesLedger()
        {
            // Seed two committed recordings: one ghost-only, one normal.
            var ghostOnly = new Recording { RecordingId = "gloops-A", IsGhostOnly = true };
            var normal = new Recording { RecordingId = "normal-B", IsGhostOnly = false };
            RecordingStore.AddRecordingWithTreeForTesting(ghostOnly);
            RecordingStore.AddRecordingWithTreeForTesting(normal);

            // Pre-fix / regression scenario: three stale actions in Ledger.Actions —
            // one tagged to the ghost-only recording, one to the normal one, and a
            // legitimate empty-tag seed row (which must survive — empty-RecordingId
            // actions cover InitialFunds/Science/Reputation seeds, KSC-spending-
            // forwarded rows, and MigrateOldSaveEvents output).
            Ledger.AddAction(new GameAction { RecordingId = "gloops-A", Type = GameActionType.KerbalAssignment });
            Ledger.AddAction(new GameAction { RecordingId = "normal-B", Type = GameActionType.KerbalAssignment });
            Ledger.AddAction(new GameAction { RecordingId = "", Type = GameActionType.FundsInitial });

            Assert.Equal(3, Ledger.Actions.Count);

            int removed = LedgerOrchestrator.PurgeGhostOnlyActionsFromLedger();

            Assert.Equal(1, removed);
            Assert.Equal(2, Ledger.Actions.Count);
            // Timeline / career-state views read Ledger.Actions directly, so mutate
            // in place — not just filter a walk-time copy.
            Assert.DoesNotContain(Ledger.Actions, a => a.RecordingId == "gloops-A");
            Assert.Contains(Ledger.Actions, a => a.RecordingId == "normal-B");
            Assert.Contains(Ledger.Actions, a => string.IsNullOrEmpty(a.RecordingId));
        }

        [Fact]
        public void PurgeGhostOnlyActionsFromLedger_PreservesMigratedOldSaveActionsWithNullTag()
        {
            // MigrateOldSaveEvents (LedgerOrchestrator.cs:803) converts pre-ledger
            // save events into GameActions with null/empty RecordingId — documented
            // intentional tradeoff: we cannot reliably map them to specific recordings.
            // The purge must keep these alongside the legitimate seed-action empty tags.
            var ghostOnly = new Recording { RecordingId = "gloops-old", IsGhostOnly = true };
            RecordingStore.AddRecordingWithTreeForTesting(ghostOnly);

            Ledger.AddAction(new GameAction { RecordingId = null, Type = GameActionType.FundsSpending, FundsSpent = 1000f });
            Ledger.AddAction(new GameAction { RecordingId = "", Type = GameActionType.FundsInitial });
            Ledger.AddAction(new GameAction { RecordingId = "gloops-old", Type = GameActionType.KerbalAssignment });

            int removed = LedgerOrchestrator.PurgeGhostOnlyActionsFromLedger();

            Assert.Equal(1, removed);
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.FundsSpending && a.RecordingId == null);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.FundsInitial);
            Assert.DoesNotContain(Ledger.Actions, a => a.RecordingId == "gloops-old");
        }

        [Fact]
        public void PurgeGhostOnlyActionsFromLedger_NoGhostOnlyRecordings_Noop()
        {
            var normal = new Recording { RecordingId = "normal-only", IsGhostOnly = false };
            RecordingStore.AddRecordingWithTreeForTesting(normal);

            Ledger.AddAction(new GameAction { RecordingId = "normal-only", Type = GameActionType.KerbalAssignment });
            Ledger.AddAction(new GameAction { RecordingId = "", Type = GameActionType.FundsInitial });

            int removed = LedgerOrchestrator.PurgeGhostOnlyActionsFromLedger();

            Assert.Equal(0, removed);
            Assert.Equal(2, Ledger.Actions.Count);
        }

        [Fact]
        public void PurgeGhostOnlyActionsFromLedger_RemovesAllTypesForGhostOnlyRecording()
        {
            // A ghost-only recording may have multiple action types leaked in from
            // different migration / past-code paths — the purge must remove them all,
            // not just one type (as ReplaceActionsForRecording would for KerbalAssignment).
            var ghostOnly = new Recording { RecordingId = "gloops-multi", IsGhostOnly = true };
            RecordingStore.AddRecordingWithTreeForTesting(ghostOnly);

            Ledger.AddAction(new GameAction { RecordingId = "gloops-multi", Type = GameActionType.KerbalAssignment });
            Ledger.AddAction(new GameAction { RecordingId = "gloops-multi", Type = GameActionType.FundsSpending, FundsSpent = 5000f });
            Ledger.AddAction(new GameAction { RecordingId = "gloops-multi", Type = GameActionType.FundsEarning, FundsAwarded = 3000f });

            int removed = LedgerOrchestrator.PurgeGhostOnlyActionsFromLedger();

            Assert.Equal(3, removed);
            Assert.Empty(Ledger.Actions);
        }

        [Fact]
        public void PurgeEventsForRecordings_RemovesForcedGloopsTaggedEvent_LiveAndSnapshot()
        {
            // This test pins the purge *mechanism* across all three stores touched by
            // PurgeEventsForRecordings: live events, contract snapshots, and milestones.
            GameStateRecorder.TagResolverForTesting = () => "gloops-forced";

            // Branch 1: live events list.
            var evt = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-1",
                detail = ""
            };
            GameStateRecorder.Emit(evt, "test");

            // Branch 2: orphan contract snapshots (must be removed when the matching
            // ContractAccepted event is purged — PurgeOrphanedContractSnapshots).
            var contractNode = new ConfigNode("CONTRACT");
            contractNode.AddValue("guid", "contract-1");
            GameStateStore.AddContractSnapshot("contract-1", contractNode);

            int eventCountBefore = GameStateStore.EventCount;
            int snapshotCountBefore = GameStateStore.ContractSnapshots.Count;
            Assert.True(eventCountBefore >= 1);
            Assert.True(snapshotCountBefore >= 1);

            int removed = GameStateStore.PurgeEventsForRecordings(
                new[] { "gloops-forced" }, "test");

            Assert.Equal(1, removed);
            Assert.Equal(eventCountBefore - 1, GameStateStore.EventCount);
            // Snapshot's matching ContractAccepted event was purged, so the orphan
            // cleanup should have removed the snapshot too.
            Assert.Equal(snapshotCountBefore - 1, GameStateStore.ContractSnapshots.Count);
        }

        // ================================================================
        // RecalculateAndPatch integration — purge happens in-place, log fires once,
        // subsequent calls are no-ops because the ledger is already clean.
        // ================================================================

        [Fact]
        public void RecalculateAndPatch_PurgesGhostOnlyActions_AndSecondCallIsNoop()
        {
            var ghostOnly = new Recording { RecordingId = "gloops-stale", IsGhostOnly = true };
            RecordingStore.AddRecordingWithTreeForTesting(ghostOnly);

            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "gloops-stale",
                KerbalName = "Jeb Kerman",
                KerbalRole = "Pilot",
                StartUT = 100f,
                EndUT = 200f,
                KerbalEndStateField = KerbalEndState.Aboard,
            });

            LedgerOrchestrator.RecalculateAndPatch();

            // Purge log fired and the stale row is gone from Ledger.Actions itself.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("PurgeGhostOnlyActionsFromLedger")
                && l.Contains("removed 1 action(s)"));
            Assert.DoesNotContain(Ledger.Actions, a => a.RecordingId == "gloops-stale");

            // Second call is a no-op: nothing left to purge, so no purge log line fires.
            logLines.Clear();
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("PurgeGhostOnlyActionsFromLedger")
                && l.Contains("removed"));
        }

        [Fact]
        public void RecalculateAndPatch_NoPurgeLog_WhenNoGhostOnlyRecordings()
        {
            var normal = new Recording { RecordingId = "normal-only", IsGhostOnly = false };
            RecordingStore.AddRecordingWithTreeForTesting(normal);

            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "normal-only",
                KerbalName = "Jeb Kerman",
                KerbalRole = "Pilot",
                StartUT = 100f,
                EndUT = 200f,
                KerbalEndStateField = KerbalEndState.Aboard,
            });

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("PurgeGhostOnlyActionsFromLedger")
                && l.Contains("removed"));
            // Normal action survives.
            Assert.Contains(Ledger.Actions, a => a.RecordingId == "normal-only");
        }

        // ================================================================
        // 8. MigrateKerbalAssignments self-heal: stale pre-fix rows get replaced
        //    with the (now empty) guarded output of CreateKerbalAssignmentActions.
        // ================================================================

        [Fact]
        public void OnKspLoad_RewritesStaleGhostOnlyKerbalRowsToEmpty()
        {
            // Pre-fix scenario: a Gloops recording committed on an older build already
            // has a KerbalAssignment row in Ledger.Actions tagged with its id.
            var gloopsWithCrew = MakeCrewedRecording("gloops-stale-crew", ghostOnly: true);
            RecordingStore.AddRecordingWithTreeForTesting(gloopsWithCrew);

            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "gloops-stale-crew",
                KerbalName = "Jeb Kerman",
                KerbalRole = "Pilot",
                StartUT = 100f,
                EndUT = 200f,
                KerbalEndStateField = KerbalEndState.Aboard,
            });

            int before = Ledger.Actions.Count;
            Assert.Equal(1, before);

            var validIds = new HashSet<string> { "gloops-stale-crew" };
            LedgerOrchestrator.OnKspLoad(validIds, maxUT: 1000.0);

            // MigrateKerbalAssignments should have replaced the stale row with an empty
            // desired list (CreateKerbalAssignmentActions returns empty for ghost-only).
            int afterGhost = 0;
            for (int i = 0; i < Ledger.Actions.Count; i++)
            {
                var a = Ledger.Actions[i];
                if (a.Type == GameActionType.KerbalAssignment
                    && a.RecordingId == "gloops-stale-crew")
                    afterGhost++;
            }
            Assert.Equal(0, afterGhost);
        }
    }
}
