using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// QUICKLOAD-UNTAGGED-RECOVERY-MILESTONE-SURVIVES-F9 (lane QL-2, run 2026-10-07_2344): stock
    /// completes <c>FirstCrewToSurvive</c> inside the <c>onVesselRecovered</c> dispatch of a
    /// recovery, before Parsek's own recovery handler runs, at the Space Center where no
    /// recorder is live. The milestone was recorded untagged, so the quickload reconcile kept
    /// it while it retired the recovery payout (tagged to the recovered recording), and the
    /// ledger claimed 800 funds KSP did not have. Owner decision: a progress node completed
    /// during a recovery belongs to the recovered recording (<see cref="RecoveryDispatchScope"/>).
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryScopedMilestoneTagTests : IDisposable
    {
        private const string FleaGuid = "f77e42072e3d4c59b04581daba628b55";
        private const uint FleaPid = 2905720181u;
        private const string FleaName = "Jumping Flea";
        private const string Milestone = "FirstCrewToSurvive";

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public RecoveryScopedMilestoneTagTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            ResetAll();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ResetAll();
            KspStatePatcher.ResetForTesting();
        }

        private static void ResetAll()
        {
            GameStateRecorder.ResetForTesting();
            RecoveryDispatchScope.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            FlightRecorder.QuickloadResumeUTProviderForTesting = null;
        }

        // ============================================================
        // The QL-2 shape, end to end
        // ============================================================

        [Fact]
        public void QL2_RecoveryMilestoneAfterQuicksave_TaggedToRecoveredRecording_RetiredByQuickload()
        {
            // The Flea hopped, was quicksaved on the ground at 352.2, recovered at 353.8
            // (the scene-exit commit ends the recording there) and stock completed
            // FirstCrewToSurvive inside the recovery dispatch at 353.94.
            var committed = MakeFleaTree("ql2", 353.8, TerminalState.Recovered);
            CommitInMemory(committed);
            string rootId = committed.RootRecordingId;

            RecoverWithMilestone(353.94, Milestone, funds: 800f);

            GameStateEvent evt = GameStateStore.Events.Single(e =>
                e.eventType == GameStateEventType.MilestoneAchieved && e.key == Milestone);
            Assert.Equal(rootId, evt.recordingId);
            GameAction row = Ledger.Actions.Single(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == Milestone);
            Assert.Equal(rootId, row.RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[RecoveryScope]")
                && l.Contains("Recovery-scoped progress event owner:")
                && l.Contains("owner=CommittedRecording")
                && l.Contains("recordingId=" + rootId)
                && l.Contains("reason=recovered-recording"));

            // F9 from the Space Center into the quicksave taken at 352.2.
            var resumed = RestoreQuicksaveOf(committed, 352.2);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);
            logLines.Clear();
            RunResumePrep(resumed, 352.48, LoadKind.QuickloadFlight);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == Milestone);
            Assert.DoesNotContain(GameStateStore.Events, e =>
                e.eventType == GameStateEventType.MilestoneAchieved && e.key == Milestone);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains("ledgerRowsRetired=1")
                && l.Contains("afterCutoff=1"));
        }

        [Fact]
        public void RecoveryMilestone_BeforeQuicksave_KeptByReconcile()
        {
            // Committed history: the recovery (and its milestone) came BEFORE the quicksave
            // the player loads, so the tag must not make it retireable.
            var committed = MakeFleaTree("ql2_history", 160.0, TerminalState.Recovered);
            CommitInMemory(committed);
            string rootId = committed.RootRecordingId;

            RecoverWithMilestone(160.5, Milestone, funds: 800f);

            var resumed = RestoreQuicksaveOf(committed, 150.0);
            RunResumePrep(resumed, 200.0, LoadKind.QuickloadFlight);

            GameAction row = Ledger.Actions.Single(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == Milestone);
            Assert.Equal(rootId, row.RecordingId);
            Assert.Contains(GameStateStore.Events, e =>
                e.eventType == GameStateEventType.MilestoneAchieved && e.key == Milestone);
        }

        // ============================================================
        // Paid exactly once
        // ============================================================

        [Fact]
        public void RecoveryMilestone_CommittedOwner_OneTaggedRow_ReCommitDoesNotDuplicate()
        {
            var committed = MakeFleaTree("once", 353.8, TerminalState.Recovered);
            CommitInMemory(committed);
            string rootId = committed.RootRecordingId;

            RecoverWithMilestone(353.94, Milestone, funds: 800f);
            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.MilestoneAchievement);

            // A later re-commit of the same recording reads the tagged event (owned by tag
            // past the window end) and the type + UT + key dedup drops it.
            bool science = false;
            LedgerOrchestrator.OnRecordingCommitted(rootId, 100.0, 353.8, null, ref science);

            GameAction row = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement);
            Assert.Equal(rootId, row.RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]")
                && l.Contains("Recovery-scoped milestone 'FirstCrewToSurvive' written to the ledger")
                && l.Contains("recordingId=" + rootId));
        }

        [Fact]
        public void RecoveryMilestone_PendingOwner_TaggedThenBookedOnceAtCommit()
        {
            // The recovered vessel's recording is still in a pending tree (the payout also
            // waits for that commit): the event is tagged to it, nothing is written at once,
            // and the commit books exactly one tagged row.
            var pending = MakeFleaTree("pending", 353.8, TerminalState.Recovered);
            RecordingStore.StashPendingTree(pending, PendingTreeState.Finalized);
            string rootId = pending.RootRecordingId;

            RecoverWithMilestone(353.94, Milestone, funds: 800f);

            GameStateEvent evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.MilestoneAchieved);
            Assert.Equal(rootId, evt.recordingId);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.MilestoneAchievement);
            Assert.Contains(logLines, l =>
                l.Contains("owner=PendingRecording") && l.Contains("reason=pending-tree-owner"));

            bool science = false;
            LedgerOrchestrator.OnRecordingCommitted(rootId, 100.0, 353.8, null, ref science);
            GameAction row = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement);
            Assert.Equal(rootId, row.RecordingId);
        }

        [Fact]
        public void RecoveryMilestone_PendingOwnerAmbiguous_StaysUntaggedKscRow()
        {
            var pending = MakeFleaTree("ambiguous", 353.8, TerminalState.Recovered);
            var twin = new Recording
            {
                RecordingId = "twin_ambiguous", TreeId = pending.Id, VesselName = FleaName,
                VesselPersistentId = FleaPid, RecordedVesselGuid = FleaGuid,
            };
            SetPoints(twin, 110.0, 353.8);
            pending.AddOrReplaceRecording(twin);
            RecordingStore.StashPendingTree(pending, PendingTreeState.Finalized);

            RecoverWithMilestone(353.94, Milestone, funds: 800f);

            AssertUntaggedKscRow();
            Assert.Contains(logLines, l => l.Contains("reason=pending-owner-ambiguous"));
        }

        // ============================================================
        // Mirror directions that must not change
        // ============================================================

        [Fact]
        public void MilestoneOutsideRecoveryScope_StaysUntaggedKscRow()
        {
            var committed = MakeFleaTree("noscope", 353.8, TerminalState.Recovered);
            CommitInMemory(committed);

            GameStateRecorder.RegisterPendingMilestoneEvent(Milestone, 353.94);

            AssertUntaggedKscRow();
            Assert.DoesNotContain(logLines, l => l.Contains("Recovery-scoped progress event owner:"));
        }

        [Fact]
        public void RecoveryOfUnrecordedVessel_MilestoneStaysUntaggedKscRow()
        {
            // A committed Flea exists, but the recovered vessel is another craft.
            CommitInMemory(MakeFleaTree("other", 353.8, TerminalState.Recovered));

            RecoveryDispatchScope.Open(
                RecoveredVesselIdentity.FromRawName("Stranger", "0123456789ab4cdef0123456789abcde", 42u),
                42u, "test");
            GameStateRecorder.RegisterPendingMilestoneEvent(Milestone, 353.94);
            RecoveryDispatchScope.Close("test");

            AssertUntaggedKscRow();
            Assert.Contains(logLines, l => l.Contains("owner=None") && l.Contains("reason=no-recording"));
        }

        [Fact]
        public void RecoveryOfSameNameDifferentLaunch_MilestoneStaysUntaggedKscRow()
        {
            // persistentId is craft-baked: a later launch of the same craft shares name and
            // pid. Its fresh launch guid conclusively differs, so it is not this recording.
            CommitInMemory(MakeFleaTree("relaunch", 353.8, TerminalState.Recovered));

            RecoveryDispatchScope.Open(
                RecoveredVesselIdentity.FromRawName(FleaName, "99999999999949999999999999999999", FleaPid),
                FleaPid, "test");
            GameStateRecorder.RegisterPendingMilestoneEvent(Milestone, 353.94);
            RecoveryDispatchScope.Close("test");

            AssertUntaggedKscRow();
        }

        [Fact]
        public void RecoveryMilestone_LiveRecorderTagWins()
        {
            CommitInMemory(MakeFleaTree("live", 353.8, TerminalState.Recovered));
            GameStateRecorder.TagResolverForTesting = () => "live_rec";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;

            RecoveryDispatchScope.Open(FleaIdentity(), FleaPid, "test");
            GameStateRecorder.RegisterPendingMilestoneEvent(Milestone, 353.94);
            RecoveryDispatchScope.Close("test");

            GameStateEvent evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.MilestoneAchieved);
            Assert.Equal("live_rec", evt.recordingId);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.MilestoneAchievement);
            Assert.DoesNotContain(logLines, l => l.Contains("Recovery-scoped progress event owner:"));
        }

        [Fact]
        public void RecoveryScope_TagsProgressionLegs_NotThePayoutOrCrewEvents()
        {
            var committed = MakeFleaTree("legs", 353.8, TerminalState.Recovered);
            CommitInMemory(committed);
            string rootId = committed.RootRecordingId;

            RecoveryDispatchScope.Open(FleaIdentity(), FleaPid, "test");
            GameStateRecorder.RegisterPendingMilestoneEvent(Milestone, 353.94);
            EmitResource(GameStateEventType.FundsChanged, "Progression", 353.94);
            EmitResource(GameStateEventType.FundsChanged, LedgerOrchestrator.VesselRecoveryReasonKey, 353.94);
            EmitResource(GameStateEventType.ScienceChanged, LedgerOrchestrator.VesselRecoveryReasonKey, 353.94);
            var crew = new GameStateEvent
            {
                ut = 353.94, eventType = GameStateEventType.CrewStatusChanged, key = "Jebediah Kerman",
            };
            GameStateRecorder.Emit(ref crew, "test");
            RecoveryDispatchScope.Close("test");

            Assert.Equal(rootId, Find(GameStateEventType.FundsChanged, "Progression").recordingId);
            Assert.Equal("", Find(GameStateEventType.FundsChanged, LedgerOrchestrator.VesselRecoveryReasonKey).recordingId ?? "");
            Assert.Equal("", Find(GameStateEventType.ScienceChanged, LedgerOrchestrator.VesselRecoveryReasonKey).recordingId ?? "");
            Assert.Equal("", Find(GameStateEventType.CrewStatusChanged, "Jebediah Kerman").recordingId ?? "");
            Assert.Contains(logLines, l =>
                l.Contains("Recovery scope closed:") && l.Contains("taggedEvents=2"));
        }

        // The post-walk reconciler pairs a recording-tagged milestone row only with
        // Progression events tagged to the same recording, so the reward's own funds leg must
        // carry the tag too or every walk warns "missing earning channel". The untagged-leg
        // half shows the leg tag is load-bearing.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RecoveryMilestone_PostWalkPairsTheRowWithItsTaggedProgressionLeg(bool legInsideScope)
        {
            var committed = MakeFleaTree("postwalk", 353.8, TerminalState.Recovered);
            CommitInMemory(committed);

            RecoveryDispatchScope.Open(FleaIdentity(), FleaPid, "test");
            GameStateRecorder.RegisterPendingMilestoneEvent(Milestone, 353.94);
            if (legInsideScope)
                EmitFunds("Progression", 353.94, 530400.0, 531200.0);
            RecoveryDispatchScope.Close("test");
            if (!legInsideScope)
                EmitFunds("Progression", 353.94, 530400.0, 531200.0);

            GameAction row = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement);
            row.MilestoneFundsAwarded = 800f;
            row.Effective = true;
            logLines.Clear();
            LedgerOrchestrator.ReconcilePostWalk(GameStateStore.Events, Ledger.Actions, utCutoff: null);

            bool warned = logLines.Any(l => l.Contains("Earnings reconciliation (post-walk, funds)"));
            Assert.Equal(!legInsideScope, warned);
        }

        [Fact]
        public void ProgressionLegAfterScopeClosed_StaysUntagged()
        {
            CommitInMemory(MakeFleaTree("closed", 353.8, TerminalState.Recovered));
            RecoveryDispatchScope.Open(FleaIdentity(), FleaPid, "test");
            RecoveryDispatchScope.Close("test");

            EmitResource(GameStateEventType.FundsChanged, "Progression", 353.94);

            Assert.Equal("", Find(GameStateEventType.FundsChanged, "Progression").recordingId ?? "");
        }

        // ============================================================
        // Pure decisions and the scope stack
        // ============================================================

        [Fact]
        public void DecideOwner_Table()
        {
            var pending = RecoveryDispatchScope.DecideOwner(true, 1, "p", "c");
            Assert.Equal(RecoveryEventOwnerKind.PendingRecording, pending.Kind);
            Assert.Equal("p", pending.RecordingId);

            var ambiguous = RecoveryDispatchScope.DecideOwner(true, 2, "p", "c");
            Assert.False(ambiguous.HasOwner);
            Assert.Equal("pending-owner-ambiguous", ambiguous.Reason);

            var noTarget = RecoveryDispatchScope.DecideOwner(true, 0, null, "c");
            Assert.False(noTarget.HasOwner);
            Assert.Equal("pending-owner-no-target", noTarget.Reason);

            var committed = RecoveryDispatchScope.DecideOwner(false, 0, null, "c");
            Assert.Equal(RecoveryEventOwnerKind.CommittedRecording, committed.Kind);
            Assert.Equal("c", committed.RecordingId);

            var none = RecoveryDispatchScope.DecideOwner(false, 0, null, null);
            Assert.False(none.HasOwner);
            Assert.Equal("no-recording", none.Reason);
        }

        [Fact]
        public void ShouldWriteRecoveryOwnedMilestone_Table()
        {
            var none = RecoveryEventOwner.NoOwner("no-recovery-scope");
            var committed = RecoveryDispatchScope.DecideOwner(false, 0, null, "c");
            var pending = RecoveryDispatchScope.DecideOwner(true, 1, "p", null);

            Assert.True(GameStateRecorder.ShouldWriteRecoveryOwnedMilestone("c", committed));
            // The owner applies only to the event it tagged: a live tag stays a commit row.
            Assert.False(GameStateRecorder.ShouldWriteRecoveryOwnedMilestone("live", committed));
            // A pending owner is booked by that recording's commit.
            Assert.False(GameStateRecorder.ShouldWriteRecoveryOwnedMilestone("p", pending));
            Assert.False(GameStateRecorder.ShouldWriteRecoveryOwnedMilestone("", none));
            Assert.False(GameStateRecorder.ShouldWriteRecoveryOwnedMilestone("c", none));
        }

        [Fact]
        public void DecideOpenSkipReason_Table()
        {
            Assert.Null(RecoveryDispatchScope.DecideOpenSkipReason(true, true, false, false, false));
            Assert.Equal("no-vessel", RecoveryDispatchScope.DecideOpenSkipReason(false, true, false, false, false));
            Assert.Equal("ghost-map-vessel", RecoveryDispatchScope.DecideOpenSkipReason(true, true, true, false, false));
            Assert.Equal("rewinding", RecoveryDispatchScope.DecideOpenSkipReason(true, true, false, true, false));
            Assert.Equal("programmatic-recovery", RecoveryDispatchScope.DecideOpenSkipReason(true, true, false, false, true));
            Assert.Equal("no-name", RecoveryDispatchScope.DecideOpenSkipReason(true, false, false, false, false));
        }

        [Fact]
        public void IsProgressEvent_Table()
        {
            Assert.True(RecoveryDispatchScope.IsProgressEvent(GameStateEventType.MilestoneAchieved, "any"));
            Assert.True(RecoveryDispatchScope.IsProgressEvent(GameStateEventType.FundsChanged, "Progression"));
            Assert.True(RecoveryDispatchScope.IsProgressEvent(GameStateEventType.ReputationChanged, "Progression"));
            Assert.True(RecoveryDispatchScope.IsProgressEvent(GameStateEventType.ScienceChanged, "Progression"));
            Assert.False(RecoveryDispatchScope.IsProgressEvent(GameStateEventType.FundsChanged, "VesselRecovery"));
            Assert.False(RecoveryDispatchScope.IsProgressEvent(GameStateEventType.ContractCompleted, "Progression"));
            Assert.False(RecoveryDispatchScope.IsProgressEvent(GameStateEventType.CrewStatusChanged, "Progression"));
        }

        [Fact]
        public void ResolveOwnerForEvent_NoScope_NoOwner()
        {
            var owner = RecoveryDispatchScope.ResolveOwnerForEvent(GameStateEventType.MilestoneAchieved, Milestone, 1.0);
            Assert.False(owner.HasOwner);
            Assert.Equal("no-recovery-scope", owner.Reason);
        }

        [Fact]
        public void ScopeStack_NestsAndClosesInnermost_CloseWithNoneOpenIsANoOp()
        {
            RecoveryDispatchScope.Close("nothing");
            Assert.Equal(0, RecoveryDispatchScope.Depth);
            Assert.Contains(logLines, l => l.Contains("Recovery scope close with none open: route=nothing"));

            RecoveryDispatchScope.Open(FleaIdentity(), FleaPid, "outer");
            RecoveryDispatchScope.Open(RecoveredVesselIdentity.FromRawName("Inner", null, 7u), 7u, "inner");
            Assert.Equal(2, RecoveryDispatchScope.Depth);
            RecoveryDispatchScope.Close("inner");
            Assert.Contains(logLines, l => l.Contains("Recovery scope closed: route=inner") && l.Contains("pid=7"));
            RecoveryDispatchScope.Close("outer");
            Assert.False(RecoveryDispatchScope.IsOpen);
        }

        [Fact]
        public void PatchTargets_ResolveAgainstStock()
        {
            Assert.NotNull(AccessTools.Method(typeof(VesselRetrieval), "recoverVessel", new[] { typeof(Vessel) }));
            Assert.NotNull(AccessTools.Method(typeof(SpaceTracking), "OnRecoverConfirm", Type.EmptyTypes));
            Assert.NotNull(AccessTools.Method(typeof(ShipConstruction), "RecoverVesselFromFlight",
                new[] { typeof(ProtoVessel), typeof(FlightState), typeof(bool) }));
            Assert.NotNull(AccessTools.Method(typeof(ProtoVessel), "Clean", new[] { typeof(string) }));

            var patchTypes = new[]
            {
                typeof(Parsek.Patches.RecoveryScopeVesselRetrievalPatch),
                typeof(Parsek.Patches.RecoveryScopeTrackingStationPatch),
                typeof(Parsek.Patches.RecoveryScopeRecoverVesselFromFlightPatch),
                typeof(Parsek.Patches.RecoveryScopeProtoVesselCleanPatch),
            };
            foreach (var t in patchTypes)
            {
                Assert.NotEmpty(t.GetCustomAttributes(typeof(HarmonyPatch), false));
                MethodInfo prefix = t.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = t.GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(prefix);
                Assert.NotNull(finalizer);
                Assert.Contains(prefix.GetParameters(), p => p.Name == "__state" && p.IsOut);
                Assert.Equal("__state", finalizer.GetParameters().Single().Name);
            }
        }

        // ============================================================
        // helpers
        // ============================================================

        private static RecoveredVesselIdentity FleaIdentity()
        {
            return RecoveredVesselIdentity.FromRawName(FleaName, FleaGuid, FleaPid);
        }

        // One recovery dispatch: the scope the Harmony prefix opens, the node's completion,
        // the reward enrichment the AwardProgress postfix applies, the finalizer's close.
        private static void RecoverWithMilestone(double ut, string milestoneId, float funds)
        {
            RecoveryDispatchScope.Open(FleaIdentity(), FleaPid, "test");
            try
            {
                GameStateRecorder.RegisterPendingMilestoneEvent(milestoneId, ut);
                foreach (var a in Ledger.Actions)
                {
                    if (a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == milestoneId)
                        a.MilestoneFundsAwarded = funds;
                }
            }
            finally
            {
                RecoveryDispatchScope.Close("test");
            }
        }

        private void AssertUntaggedKscRow()
        {
            GameStateEvent evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.MilestoneAchieved);
            Assert.True(string.IsNullOrEmpty(evt.recordingId));
            GameAction row = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement);
            Assert.Null(row.RecordingId);
        }

        private static void EmitResource(GameStateEventType type, string key, double ut)
        {
            var e = new GameStateEvent { ut = ut, eventType = type, key = key, valueBefore = 1.0, valueAfter = 2.0 };
            GameStateRecorder.Emit(ref e, "test");
        }

        private static void EmitFunds(string key, double ut, double before, double after)
        {
            var e = new GameStateEvent
            {
                ut = ut, eventType = GameStateEventType.FundsChanged, key = key,
                valueBefore = before, valueAfter = after,
            };
            GameStateRecorder.Emit(ref e, "test");
        }

        private static GameStateEvent Find(GameStateEventType type, string key)
        {
            return GameStateStore.Events.Single(e => e.eventType == type && e.key == key);
        }

        private static RecordingTree MakeFleaTree(string id, double endUt, TerminalState? terminal)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = FleaName,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            var root = new Recording
            {
                RecordingId = "root_" + id,
                TreeId = id,
                VesselName = FleaName,
                VesselPersistentId = FleaPid,
                RecordedVesselGuid = FleaGuid,
                TerminalStateValue = terminal,
            };
            SetPoints(root, 100.0, Math.Min(150.0, endUt - 1.0), endUt);
            tree.AddOrReplaceRecording(root);
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static void CommitInMemory(RecordingTree tree)
        {
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // F9 into the quicksave taken at quicksaveUT: the save carries the tree as it was then
        // (isActive); the restore splices the committed copy and detaches it.
        private static RecordingTree RestoreQuicksaveOf(RecordingTree committed, double quicksaveUT)
        {
            var quicksaved = MakeFleaTree(committed.Id, quicksaveUT, null);
            var node = new ConfigNode("PARSEK_SCENARIO");
            var treeNode = node.AddNode("RECORDING_TREE");
            quicksaved.Save(treeNode);
            treeNode.AddValue("isActive", "True");
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            return RecordingStore.PopPendingTree();
        }

        private static void SetPoints(Recording rec, params double[] uts)
        {
            rec.Points.Clear();
            rec.TrackSections.Clear();
            foreach (double ut in uts)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut, altitude = 70.0, bodyName = "Kerbin",
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                });
            }
            rec.ExplicitStartUT = uts[0];
            rec.ExplicitEndUT = uts[uts.Length - 1];
        }

        private static void RunResumePrep(RecordingTree tree, double resumeUT, LoadKind kind)
        {
            var recorder = new FlightRecorder { ActiveTree = tree };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => resumeUT;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree, kind);
            MethodInfo method = typeof(FlightRecorder).GetMethod(
                "PrepareQuickloadResumeStateIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(recorder, null);
        }
    }
}
