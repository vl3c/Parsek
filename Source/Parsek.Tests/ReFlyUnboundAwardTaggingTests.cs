using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REFLY-UNBOUND-PROVISIONAL-TAGS-AWARDS-TO-ORIGIN (owner ruling 2026-10-15, option A): a
    /// career award earned in FLIGHT while a Re-Fly session's provisional has no recorder bound
    /// is tagged to the provisional, so it survives the attempt's merge and is dropped only with
    /// a discarded attempt. Before the fix the tag was empty (the re-fly tree still pending in
    /// Limbo), which filed a permanent untagged ledger row a discard kept, or named the origin
    /// the merge supersedes, which let the attempt's own merge tombstone it.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyUnboundAwardTaggingTests : IDisposable
    {
        private const string TreeId = "tree_rf3";
        private const string OriginId = "rec_origin";
        private const string ProvisionalId = "rec_prov";
        private const string OtherId = "rec_upper_stage";
        private const string RpId = "rp_rf3";
        private const string SessionId = "sess_rf3";
        private const double RewindUT = 382.7;
        private const double BindUT = 500.0;

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public ReFlyUnboundAwardTaggingTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            ResetAll();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ResetAll();
            KspStatePatcher.ResetForTesting();
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ReFlyAwardTagging.ResetForTesting();
            ReFlyProvisionalRetirement.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
        }

        private class FakeProgressNode : ProgressNode
        {
            public FakeProgressNode(string id) : base(id, startReached: false) { }
        }

        private static ReFlySessionMarker Marker(string supersedeTargetId = OriginId)
        {
            return new ReFlySessionMarker
            {
                SessionId = SessionId,
                TreeId = TreeId,
                ActiveReFlyRecordingId = ProvisionalId,
                OriginChildRecordingId = OriginId,
                SupersedeTargetId = supersedeTargetId,
                RewindPointId = RpId,
                RewindPointUT = RewindUT,
                InvokedUT = RewindUT,
                InPlaceContinuation = true,
            };
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>
                {
                    new RewindPoint { RewindPointId = RpId, UT = RewindUT },
                },
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, altitude = 30000.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
        }

        private static Recording Rec(string id, MergeState state, params double[] uts)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = TreeId,
                MergeState = state,
                TerminalStateValue = TerminalState.Landed,
            };
            foreach (double ut in uts) rec.Points.Add(PointAt(ut));
            return rec;
        }

        // ============================================================
        // The pure decision
        // ============================================================

        [Fact]
        public void Resolve_NoSession_KeepsLiveTag()
        {
            var d = ReFlyAwardTagging.Resolve("", inFlight: true, marker: null);
            Assert.False(d.Substituted);
            Assert.Equal("", d.Tag);
            Assert.Equal(ReFlyAwardTagging.ReasonNoMarker, d.Reason);
        }

        [Fact]
        public void Resolve_NotInFlight_KeepsLiveTag()
        {
            // A KSC action is never the attempt's, live marker or not.
            var d = ReFlyAwardTagging.Resolve("", inFlight: false, marker: Marker());
            Assert.False(d.Substituted);
            Assert.Equal("", d.Tag);
            Assert.Equal(ReFlyAwardTagging.ReasonNotInFlight, d.Reason);
        }

        [Fact]
        public void Resolve_EmptyTagWhileUnbound_TagsTheAttempt()
        {
            var d = ReFlyAwardTagging.Resolve("", inFlight: true, marker: Marker());
            Assert.True(d.Substituted);
            Assert.Equal(ProvisionalId, d.Tag);
            Assert.Equal(ReFlyAwardTagging.ReasonUnboundEmpty, d.Reason);
        }

        [Fact]
        public void Resolve_OriginTagWhileUnbound_TagsTheAttempt()
        {
            var d = ReFlyAwardTagging.Resolve(OriginId, inFlight: true, marker: Marker());
            Assert.True(d.Substituted);
            Assert.Equal(ProvisionalId, d.Tag);
            Assert.Equal(ReFlyAwardTagging.ReasonUnboundOrigin, d.Reason);
        }

        [Fact]
        public void Resolve_SupersedeTargetTagWhileUnbound_TagsTheAttempt()
        {
            // A slot re-flown a second time supersedes the prior attempt, not the origin.
            var d = ReFlyAwardTagging.Resolve("rec_prior_tip", inFlight: true,
                marker: Marker(supersedeTargetId: "rec_prior_tip"));
            Assert.True(d.Substituted);
            Assert.Equal(ProvisionalId, d.Tag);
            Assert.Equal(ReFlyAwardTagging.ReasonUnboundSupersedeTarget, d.Reason);
        }

        [Fact]
        public void Resolve_BoundToProvisional_KeepsLiveTag()
        {
            var d = ReFlyAwardTagging.Resolve(ProvisionalId, inFlight: true, marker: Marker());
            Assert.False(d.Substituted);
            Assert.Equal(ProvisionalId, d.Tag);
            Assert.Equal(ReFlyAwardTagging.ReasonBound, d.Reason);
        }

        [Fact]
        public void Resolve_LiveTagNamesAnotherRecording_KeepsLiveTag()
        {
            // A child the attempt split off, or a vessel the player switched to, is a bound
            // recorder of its own.
            var d = ReFlyAwardTagging.Resolve(OtherId, inFlight: true, marker: Marker());
            Assert.False(d.Substituted);
            Assert.Equal(OtherId, d.Tag);
            Assert.Equal(ReFlyAwardTagging.ReasonOtherRecording, d.Reason);
        }

        [Fact]
        public void Resolve_MarkerWithoutProvisional_KeepsLiveTag()
        {
            var marker = Marker();
            marker.ActiveReFlyRecordingId = null;
            var d = ReFlyAwardTagging.Resolve("", inFlight: true, marker: marker);
            Assert.False(d.Substituted);
            Assert.Equal(ReFlyAwardTagging.ReasonNoProvisional, d.Reason);
        }

        // ============================================================
        // The production resolver
        // ============================================================

        [Fact]
        public void ResolveCurrentRecordingTag_UnboundSessionInFlight_ReturnsProvisionalAndLogs()
        {
            InstallScenario(Marker());
            ReFlyAwardTagging.InFlightProviderForTesting = () => true;

            Assert.Equal(ProvisionalId, GameStateRecorder.ResolveCurrentRecordingTag());
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]")
                && l.Contains("Re-Fly award tag: outcome=tagged-to-attempt")
                && l.Contains("reason=" + ReFlyAwardTagging.ReasonUnboundEmpty)
                && l.Contains("sess=" + SessionId)
                && l.Contains("provisional=" + ProvisionalId));
        }

        [Fact]
        public void ResolveCurrentRecordingTag_OutsideFlight_StaysEmpty()
        {
            InstallScenario(Marker());
            ReFlyAwardTagging.InFlightProviderForTesting = () => false;

            Assert.Equal("", GameStateRecorder.ResolveCurrentRecordingTag());
            Assert.DoesNotContain(logLines, l => l.Contains("outcome=tagged-to-attempt"));
        }

        [Fact]
        public void ResolveCurrentRecordingTag_NoSession_StaysEmpty()
        {
            ReFlyAwardTagging.InFlightProviderForTesting = () => true;
            Assert.Equal("", GameStateRecorder.ResolveCurrentRecordingTag());
        }

        // ============================================================
        // The attempt's commit window
        // ============================================================

        [Fact]
        public void AdjustStartUt_Provisional_OpensAtTheRewindPoint()
        {
            var rec = Rec(ProvisionalId, MergeState.NotCommitted, BindUT, 560.0);
            rec.ProvisionalForRpId = RpId;
            double adjusted = LedgerOrchestrator.AdjustStartUtForReFlyProvisional(
                rec, rec.StartUT, id => id == RpId ? RewindUT : double.NaN);
            Assert.Equal(RewindUT, adjusted);
            Assert.Contains(logLines, l =>
                l.Contains("Re-Fly attempt window: extended startUT")
                && l.Contains("rp=" + RpId));
        }

        [Fact]
        public void AdjustStartUt_NotAProvisional_Unchanged()
        {
            var rec = Rec(OtherId, MergeState.Immutable, BindUT, 560.0);
            Assert.Equal(BindUT, LedgerOrchestrator.AdjustStartUtForReFlyProvisional(
                rec, BindUT, _ => RewindUT));
        }

        [Fact]
        public void AdjustStartUt_ChainContinuation_Unchanged()
        {
            // An optimizer split's later half copies the RP back-pointer; the split retag
            // already moved its events onto it, so the chain rule owns its window.
            var rec = Rec(ProvisionalId + "_b", MergeState.Immutable, BindUT, 560.0);
            rec.ProvisionalForRpId = RpId;
            rec.ChainId = "chain_1";
            rec.ChainIndex = 1;
            Assert.Equal(BindUT, LedgerOrchestrator.AdjustStartUtForReFlyProvisional(
                rec, BindUT, _ => RewindUT));
        }

        [Fact]
        public void AdjustStartUt_UnknownOrLaterRewindPoint_Unchanged()
        {
            var rec = Rec(ProvisionalId, MergeState.NotCommitted, BindUT, 560.0);
            rec.ProvisionalForRpId = RpId;
            Assert.Equal(BindUT, LedgerOrchestrator.AdjustStartUtForReFlyProvisional(
                rec, BindUT, _ => double.NaN));
            Assert.Equal(BindUT, LedgerOrchestrator.AdjustStartUtForReFlyProvisional(
                rec, BindUT, _ => 600.0));
        }

        [Fact]
        public void ResolveRewindPointUt_ReadsTheScenarioThenTheMarker()
        {
            InstallScenario(Marker());
            Assert.Equal(RewindUT, LedgerOrchestrator.ResolveRewindPointUt(RpId));

            ParsekScenario.Instance.RewindPoints.Clear();
            Assert.Equal(RewindUT, LedgerOrchestrator.ResolveRewindPointUt(RpId));
            Assert.True(double.IsNaN(LedgerOrchestrator.ResolveRewindPointUt("rp_other")));
        }

        // ============================================================
        // The RF-3 shape, end to end
        // ============================================================

        /// <summary>
        /// The origin earned a record band after the rewind point; the re-fly re-earns the
        /// same band in the stretch before its recorder binds, then merges. The origin's
        /// band is tombstoned by the merge and the attempt's is kept: the band is paid once.
        /// </summary>
        [Fact]
        public void RF3Shape_UnboundReEarnedRecordBand_PaidOnceAfterMerge()
        {
            var scenario = InstallScenario(Marker());
            ReFlyAwardTagging.InFlightProviderForTesting = () => true;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            LedgerOrchestrator.Initialize();

            // Committed history: the origin flew past the rewind point and earned the band.
            var origin = Rec(OriginId, MergeState.Immutable, 300.0, 420.0, 450.0);
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = TreeId,
                RootRecordingId = OriginId,
                ActiveRecordingId = OriginId,
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedTreeForTesting(tree);
            var originBand = new GameAction
            {
                ActionId = "act_origin_band",
                Type = GameActionType.MilestoneAchievement,
                RecordingId = OriginId,
                MilestoneId = "RecordsAltitude",
                MilestoneFundsAwarded = 9600f,
                MilestoneRepAwarded = 1f,
                MilestoneRecordThresholds = 1,
                UT = 420.0,
            };
            Ledger.AddAction(originBand);

            // The unbound stretch: the re-fly re-earns the same band before any recorder binds.
            GameStateRecorder.EmitStandaloneProgressReward(
                new FakeProgressNode("RecordsAltitude"), 9600.0, 1f, 0.0, ut: 383.0);

            GameStateEvent evt = GameStateStore.Events.Single(e =>
                e.eventType == GameStateEventType.MilestoneAchieved && e.key == "RecordsAltitude");
            Assert.Equal(ProvisionalId, evt.recordingId);
            // Not filed as a permanent untagged row: it waits for the attempt's commit.
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.MilestoneAchievement && a.RecordingId != OriginId);

            // The recorder binds late; the attempt merges.
            var provisional = Rec(ProvisionalId, MergeState.NotCommitted, BindUT, 530.0, 560.0);
            provisional.ProvisionalForRpId = RpId;
            provisional.CreatingSessionId = SessionId;
            provisional.SupersedeTargetId = OriginId;
            tree.AddOrReplaceRecording(provisional);
            RecordingStore.AddCommittedInternal(provisional);

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == originBand.ActionId);
            var paidBands = EffectiveState.ComputeELS()
                .Where(a => a.Type == GameActionType.MilestoneAchievement
                    && a.MilestoneId == "RecordsAltitude"
                    && a.UT > RewindUT)
                .ToList();
            var kept = Assert.Single(paidBands);
            Assert.Equal(ProvisionalId, kept.RecordingId);
            Assert.Equal(9600f, kept.MilestoneFundsAwarded);
            Assert.Equal(1, kept.MilestoneRecordThresholds);
            Assert.Contains(logLines, l =>
                l.Contains("NotifyLedgerTreeCommitted")
                && l.Contains("reFlyWindowsExtended=1"));
        }

        /// <summary>
        /// The attempt never bound and carried nothing the merge could play, so the scene-exit
        /// prune retired its provisional out of the tree and the merge concludes with no
        /// supersede. The player merged, so the award it earned stays: the tree commit files it
        /// as an untagged row before its walk, and the conclusion keeps it.
        /// </summary>
        [Fact]
        public void RetiredProvisional_MergeKeepsTheUnboundAward()
        {
            var marker = Marker();
            var scenario = InstallScenario(marker);
            ReFlyAwardTagging.InFlightProviderForTesting = () => true;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            LedgerOrchestrator.Initialize();

            var origin = Rec(OriginId, MergeState.Immutable, 300.0, 420.0, 450.0);
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = TreeId,
                RootRecordingId = OriginId,
                ActiveRecordingId = OriginId,
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedTreeForTesting(tree);

            GameStateRecorder.EmitStandaloneProgressReward(
                new FakeProgressNode("RecordsAltitude"), 9600.0, 1f, 0.0, ut: 383.0);
            Assert.Equal(ProvisionalId, GameStateStore.Events.Single(e =>
                e.eventType == GameStateEventType.MilestoneAchieved).recordingId);

            // The prune retires the empty provisional out of the tree and notes it.
            var retired = Rec(ProvisionalId, MergeState.NotCommitted);
            retired.ProvisionalForRpId = RpId;
            retired.CreatingSessionId = SessionId;
            retired.SupersedeTargetId = OriginId;
            ReFlyProvisionalRetirement.Note(marker, retired, "test-prune");

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            var filed = Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsAltitude");
            Assert.True(string.IsNullOrEmpty(filed.RecordingId));
            Assert.Equal(9600f, filed.MilestoneFundsAwarded);
            Assert.DoesNotContain(GameStateStore.Events, e => e.recordingId == ProvisionalId);
            Assert.Contains(logLines, l =>
                l.Contains("outcome=filed-retired-provisional-rows")
                && l.Contains("provisional=" + ProvisionalId)
                && l.Contains("rows=1"));

            // The merge concludes with no supersede; the award stays effective.
            Assert.True(SupersedeCommit.ConcludeRetiredProvisional(marker, retired, "test-prune"));
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Single(EffectiveState.ComputeELS(), a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsAltitude");
        }

        [Fact]
        public void RetiredProvisional_OtherSessionNote_FilesNothing()
        {
            var marker = Marker();
            InstallScenario(marker);
            ReFlyAwardTagging.InFlightProviderForTesting = () => true;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            LedgerOrchestrator.Initialize();

            var origin = Rec(OriginId, MergeState.Immutable, 300.0, 420.0, 450.0);
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = TreeId,
                RootRecordingId = OriginId,
                ActiveRecordingId = OriginId,
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedTreeForTesting(tree);

            GameStateRecorder.EmitStandaloneProgressReward(
                new FakeProgressNode("RecordsAltitude"), 9600.0, 1f, 0.0, ut: 383.0);

            var otherMarker = Marker();
            otherMarker.SessionId = "sess_other";
            ReFlyProvisionalRetirement.Note(otherMarker,
                Rec(ProvisionalId, MergeState.NotCommitted), "test-prune");

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.MilestoneAchievement);
            Assert.Equal(ProvisionalId, GameStateStore.Events.Single(e =>
                e.eventType == GameStateEventType.MilestoneAchieved).recordingId);
        }

        [Fact]
        public void DiscardedAttempt_DropsTheUnboundAward()
        {
            InstallScenario(Marker());
            ReFlyAwardTagging.InFlightProviderForTesting = () => true;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            LedgerOrchestrator.Initialize();

            GameStateRecorder.EmitStandaloneProgressReward(
                new FakeProgressNode("RecordsSpeed"), 4800.0, 1f, 0.0, ut: 383.0);
            Assert.Equal(ProvisionalId, GameStateStore.Events.Single(e =>
                e.eventType == GameStateEventType.MilestoneAchieved).recordingId);

            // The Re-Fly discard purges the attempt's tagged events; nothing reached the ledger.
            GameStateStore.PurgeEventsForRecordings(new[] { ProvisionalId }, "test discard");

            Assert.DoesNotContain(GameStateStore.Events, e =>
                e.eventType == GameStateEventType.MilestoneAchieved);
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.MilestoneAchievement);
        }
    }
}
