using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #432 — Gloops ghost-only recordings must contribute zero actions to the ledger.
    /// Covers the two action-creation guards in <see cref="LedgerOrchestrator"/>
    /// (<c>CreateKerbalAssignmentActions</c>, <c>CreateVesselCostActions</c>), the
    /// belt-and-braces <c>FilterOutGhostOnlyActions</c> pre-pass, the walk-integration
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
        // 4. FilterOutGhostOnlyActions — drops ghost-only, keeps normal + empty-tag
        // ================================================================

        [Fact]
        public void FilterOutGhostOnlyActions_DropsGhostOnlyAndKeepsOthers()
        {
            // Seed two recordings: one ghost-only, one normal.
            var ghostOnly = new Recording { RecordingId = "gloops-A", IsGhostOnly = true };
            var normal = new Recording { RecordingId = "normal-B", IsGhostOnly = false };
            RecordingStore.AddRecordingWithTreeForTesting(ghostOnly);
            RecordingStore.AddRecordingWithTreeForTesting(normal);

            // Three actions: one ghost-only-tagged, one normal-tagged, one empty-tagged
            // (legitimate empty-RecordingId cases include InitialFunds/Science/Reputation
            //  seed actions and KSC-spending-forwarded rows — those must survive the filter).
            var actions = new List<GameAction>
            {
                new GameAction { RecordingId = "gloops-A", Type = GameActionType.KerbalAssignment },
                new GameAction { RecordingId = "normal-B", Type = GameActionType.KerbalAssignment },
                new GameAction { RecordingId = "", Type = GameActionType.FundsInitial },
            };

            var filtered = LedgerOrchestrator.FilterOutGhostOnlyActions(actions);

            Assert.Equal(2, filtered.Count);
            Assert.DoesNotContain(filtered, a => a.RecordingId == "gloops-A");
            Assert.Contains(filtered, a => a.RecordingId == "normal-B");
            Assert.Contains(filtered, a => string.IsNullOrEmpty(a.RecordingId));
        }

        [Fact]
        public void FilterOutGhostOnlyActions_PreservesMigratedOldSaveActionsWithEmptyTag()
        {
            // MigrateOldSaveEvents (LedgerOrchestrator.cs:803) converts pre-ledger
            // save events into GameActions with null/empty RecordingId — documented
            // intentional tradeoff: we can't reliably map them to specific recordings.
            // The filter must keep these alongside the legitimate seed-action empty tags.
            var ghostOnly = new Recording { RecordingId = "gloops-old", IsGhostOnly = true };
            RecordingStore.AddRecordingWithTreeForTesting(ghostOnly);

            var actions = new List<GameAction>
            {
                // Simulated MigrateOldSaveEvents output: a FundsSpending with null tag.
                new GameAction { RecordingId = null, Type = GameActionType.FundsSpending, FundsSpent = 1000f },
                // Simulated InitialFunds seed.
                new GameAction { RecordingId = "", Type = GameActionType.FundsInitial },
                // The actually-ghost-only-tagged row that must be dropped.
                new GameAction { RecordingId = "gloops-old", Type = GameActionType.KerbalAssignment },
            };

            var filtered = LedgerOrchestrator.FilterOutGhostOnlyActions(actions);

            Assert.Equal(2, filtered.Count);
            Assert.Contains(filtered, a => a.Type == GameActionType.FundsSpending && a.RecordingId == null);
            Assert.Contains(filtered, a => a.Type == GameActionType.FundsInitial);
            Assert.DoesNotContain(filtered, a => a.RecordingId == "gloops-old");
        }

        [Fact]
        public void FilterOutGhostOnlyActions_NoGhostOnlyRecordings_ReturnsInputUnchanged()
        {
            var normal = new Recording { RecordingId = "normal-only", IsGhostOnly = false };
            RecordingStore.AddRecordingWithTreeForTesting(normal);

            var actions = new List<GameAction>
            {
                new GameAction { RecordingId = "normal-only", Type = GameActionType.KerbalAssignment },
                new GameAction { RecordingId = "", Type = GameActionType.FundsInitial },
            };

            var filtered = LedgerOrchestrator.FilterOutGhostOnlyActions(actions);

            // When no ghost-only recordings exist, the filter short-circuits to the input.
            Assert.Equal(2, filtered.Count);
        }

        // ================================================================
        // 5. Active-recording tag never returns a Gloops id (invariant guard)
        //    + forced Gloops-tagged event can be purged (mechanism check)
        // ================================================================

        [Fact]
        public void GetActiveRecordingIdForTagging_WithGloopsAndNormalCommitted_NeverReturnsGloopsId()
        {
            // Seed BOTH a committed Gloops recording and a committed normal recording.
            // The resolver must never return the Gloops id — proving that empty return
            // is due to the resolver honoring the active tree (empty in tests, no
            // ParsekFlight.Instance), not due to "the Gloops recording happened to be
            // skipped because no recording was committed at all."
            var gloops = new Recording
            {
                RecordingId = "gloops-B",
                VesselName = "Air Show",
                IsGhostOnly = true,
            };
            gloops.Points.Add(new TrajectoryPoint { ut = 10.0 });
            gloops.Points.Add(new TrajectoryPoint { ut = 20.0 });
            RecordingStore.CommitGloopsRecording(gloops);

            var normal = new Recording { RecordingId = "normal-A", IsGhostOnly = false };
            RecordingStore.AddRecordingWithTreeForTesting(normal);

            // Call the resolver many times to rule out any nondeterministic path.
            for (int i = 0; i < 10; i++)
            {
                string tag = ParsekFlight.GetActiveRecordingIdForTagging();
                Assert.NotEqual("gloops-B", tag);
                // May return "" or "normal-A" depending on activeTree wiring; never the Gloops id.
                Assert.True(tag == "" || tag == "normal-A",
                    $"Unexpected tag '{tag}' — resolver must return the active tree's id or empty");
            }
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
        // 6-7. RecalculateAndPatch integration — log fires when filtering happens
        // ================================================================

        [Fact]
        public void RecalculateAndPatch_FilterLogFires_WhenGhostOnlyActionPresent()
        {
            // Seed a ghost-only recording and a stale ledger row tagged with its id
            // (simulating a pre-fix save that leaked into Ledger.Actions).
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

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("filtered 1 action(s) tagged with ghost-only recordings"));

            // Filter fires on every RecalculateAndPatch as long as the stale row sits in
            // Ledger.Actions. The self-heal path (via MigrateKerbalAssignments in
            // OnKspLoad) is what actually rewrites the stale row — the walk filter itself
            // does not mutate Ledger.Actions. Verify this: a second call produces another
            // filter line with the same count.
            logLines.Clear();
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("filtered 1 action(s) tagged with ghost-only recordings"));
        }

        [Fact]
        public void RecalculateAndPatch_NoFilterLog_WhenNoGhostOnlyRecordings()
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
                && l.Contains("filtered") && l.Contains("ghost-only"));
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
