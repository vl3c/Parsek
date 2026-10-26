using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECOVERY-BURST-CONTRACT-SCIENCE-RECONCILE: a recovery that completes a contract pays
    /// subject science (VesselRecovery), the contract's science (ContractReward) and more
    /// subject science inside one 0.1 s window. The shapes below are CS-3's measured burst
    /// (cs3-cpr-KSP.log, ut=346.38): crewReport 3.5, ContractReward 1 + 1, then mysteryGoo
    /// 3 + 7 and recovery 5, all untagged events.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryBurstReconcileTests : IDisposable
    {
        private const double BurstUt = 346.38;
        private const string FleaRecording = "rec_flea_burst";
        private readonly List<string> logLines = new List<string>();

        public RecoveryBurstReconcileTests()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
        }

        public void Dispose()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(null, null, null);
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void AddResource(
            GameStateEventType type, string key, double before, double after, string tag = "")
        {
            var e = new GameStateEvent
            {
                ut = BurstUt,
                eventType = type,
                key = key,
                valueBefore = before,
                valueAfter = after,
                recordingId = tag
            };
            GameStateStore.AddEvent(ref e);
        }

        // The science side of CS-3's burst, in stock's order, plus the contract's own
        // completion events (the contract rows' live source anchors).
        private static void AddScienceBurst()
        {
            var parameterDone = new GameStateEvent
            {
                ut = BurstUt,
                eventType = GameStateEventType.ContractParameterCompleted,
                key = "c-science"
            };
            GameStateStore.AddEvent(ref parameterDone);
            var contractDone = new GameStateEvent
            {
                ut = BurstUt,
                eventType = GameStateEventType.ContractCompleted,
                key = "c-science"
            };
            GameStateStore.AddEvent(ref contractDone);
            AddResource(GameStateEventType.ScienceChanged, "VesselRecovery", 8.0, 11.5);   // crewReport
            AddResource(GameStateEventType.ScienceChanged, "ContractReward", 11.5, 12.5);  // parameter
            AddResource(GameStateEventType.ScienceChanged, "ContractReward", 12.5, 13.5);  // contract
            AddResource(GameStateEventType.ScienceChanged, "VesselRecovery", 13.5, 16.5);  // goo pad
            AddResource(GameStateEventType.ScienceChanged, "VesselRecovery", 16.5, 23.5);  // goo flying
            AddResource(GameStateEventType.ScienceChanged, "VesselRecovery", 23.5, 28.5);  // recovery
        }

        private static GameAction RecoveredSubject(string subjectId, float science)
        {
            return new GameAction
            {
                UT = BurstUt,
                Type = GameActionType.ScienceEarning,
                RecordingId = FleaRecording,
                SubjectId = subjectId,
                Method = ScienceMethod.Recovered,
                Effective = true,
                ScienceAwarded = science,
                EffectiveScience = science,
                StartUT = (float)BurstUt,
                EndUT = (float)BurstUt
            };
        }

        private static List<GameAction> BurstRows(bool includeCrewReport = true)
        {
            var rows = new List<GameAction>();
            if (includeCrewReport)
                rows.Add(RecoveredSubject("crewReport@KerbinFlyingLowShores", 3.5f));
            rows.Add(RecoveredSubject("mysteryGoo@KerbinSrfLandedLaunchPad", 3f));
            rows.Add(RecoveredSubject("mysteryGoo@KerbinFlyingLow", 7f));
            rows.Add(RecoveredSubject("recovery@KerbinFlew", 5f));
            rows.Add(new GameAction
            {
                UT = BurstUt,
                Type = GameActionType.ContractParameterComplete,
                ContractId = "c-science",
                ContractParameterId = "0",
                Effective = true,
                TransformedScienceReward = 1f
            });
            rows.Add(new GameAction
            {
                UT = BurstUt,
                Type = GameActionType.ContractComplete,
                ContractId = "c-science",
                Effective = true,
                TransformedScienceReward = 1f
            });
            return rows;
        }

        private static double SumDeltas(GameStateEventType type, string key)
        {
            double sum = 0.0;
            var events = GameStateStore.Events;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].eventType != type) continue;
                if (key != null && !string.Equals(events[i].key, key, StringComparison.Ordinal)) continue;
                sum += events[i].valueAfter - events[i].valueBefore;
            }
            return sum;
        }

        [Fact]
        public void Store_InterleavedScienceBurst_EveryDeltaCountedOnce()
        {
            AddScienceBurst();

            // VesselRecovery 3.5 | ContractReward 1+1 | VesselRecovery 3+7+5: the second
            // VesselRecovery run must not merge back across the ContractReward event, which
            // would make it 8.0 -> 28.5 and count the contract's 2.0 a second time.
            Assert.Equal(5, GameStateStore.EventCount);
            Assert.Equal(18.5, SumDeltas(GameStateEventType.ScienceChanged, "VesselRecovery"), 6);
            Assert.Equal(2.0, SumDeltas(GameStateEventType.ScienceChanged, "ContractReward"), 6);
            Assert.Equal(20.5, SumDeltas(GameStateEventType.ScienceChanged, null), 6);
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateStore]") && l.Contains("Not coalescing ScienceChanged key='VesselRecovery'")
                && l.Contains("interleaved key='ContractReward'"));
        }

        [Fact]
        public void Store_InterleavedFundsBurst_MirrorsScience()
        {
            // CS-3's funds side of the same burst: Progression (FirstCrewToSurvive),
            // ContractReward 6400 + 4160, Progression (Kerbin/Science). Merged across the
            // contract the Progression event read +15360 against 4800 expected.
            AddResource(GameStateEventType.FundsChanged, "Progression", 93108, 94708);
            AddResource(GameStateEventType.FundsChanged, "ContractReward", 94708, 101108);
            AddResource(GameStateEventType.FundsChanged, "ContractReward", 101108, 105268);
            AddResource(GameStateEventType.FundsChanged, "Progression", 105268, 108468);

            Assert.Equal(3, GameStateStore.EventCount);
            Assert.Equal(4800.0, SumDeltas(GameStateEventType.FundsChanged, "Progression"), 6);
            Assert.Equal(10560.0, SumDeltas(GameStateEventType.FundsChanged, "ContractReward"), 6);
        }

        [Fact]
        public void Store_InterleavedOtherTag_DoesNotCoalesceAcross()
        {
            AddResource(GameStateEventType.ScienceChanged, "ScienceTransmission", 0, 2, "rec-a");
            AddResource(GameStateEventType.ScienceChanged, "ScienceTransmission", 2, 5, "rec-b");
            AddResource(GameStateEventType.ScienceChanged, "ScienceTransmission", 5, 6, "rec-a");

            Assert.Equal(3, GameStateStore.EventCount);
            Assert.Equal(6.0, SumDeltas(GameStateEventType.ScienceChanged, null), 6);
        }

        [Fact]
        public void Store_OtherPoolInBetween_StillCoalesces()
        {
            // A funds or reputation event between two same-key science credits is a
            // different pool: it cannot inflate the science delta, so the run still merges.
            AddResource(GameStateEventType.ScienceChanged, "VesselRecovery", 0, 3);
            AddResource(GameStateEventType.FundsChanged, "ContractReward", 100, 200);
            AddResource(GameStateEventType.ScienceChanged, "VesselRecovery", 3, 7);

            Assert.Equal(2, GameStateStore.EventCount);
            Assert.Equal(7.0, SumDeltas(GameStateEventType.ScienceChanged, "VesselRecovery"), 6);
        }

        [Fact]
        public void PostWalk_RecoveryBurstCompletingContract_NoScienceDump()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = FleaRecording,
                VesselName = "Jumping Flea"
            });
            AddScienceBurst();

            LedgerOrchestrator.ReconcilePostWalk(GameStateStore.Events, BurstRows(), utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Science reconcile dump"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));
            Assert.Contains(logLines, l =>
                l.Contains("Post-walk match: ScienceEarning sci") &&
                l.Contains("expected=18.5, observed=18.5") &&
                l.Contains("keyed 'VesselRecovery'"));
            Assert.Contains(logLines, l =>
                l.Contains("Post-walk match: ContractParameterComplete sci") &&
                l.Contains("expected=2.0, observed=2.0") &&
                l.Contains("keyed 'ContractReward'"));
        }

        [Fact]
        public void PostWalk_RecoveryBurst_UnexplainedSubjectCredit_StillDumps()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = FleaRecording,
                VesselName = "Jumping Flea"
            });
            AddScienceBurst();
            // A further 2.0 of VesselRecovery science that no row explains.
            AddResource(GameStateEventType.ScienceChanged, "VesselRecovery", 28.5, 30.5);

            LedgerOrchestrator.ReconcilePostWalk(GameStateStore.Events, BurstRows(), utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN][LedgerOrchestrator]") &&
                l.Contains("Earnings reconciliation (post-walk, sci)") &&
                l.Contains("expected=18.5, observed=20.5"));
            Assert.Contains(logLines, l =>
                l.Contains("[ERROR][LedgerOrchestrator]") &&
                l.Contains("Science reconcile dump (post-walk)") &&
                l.Contains("reason='VesselRecovery'"));
        }

        [Fact]
        public void PendingKscScienceCredit_RecoveryBurst_NoPhantomFromContractReward()
        {
            AddScienceBurst();

            // Every subject rowed: nothing pending. Before the contiguity rule the merged
            // VesselRecovery event read 20.5 and the patch held a phantom 2.0 forward.
            Assert.Equal(0.0, LedgerOrchestrator.ComputePendingRecentKscScienceCredit(
                GameStateStore.Events, BurstRows(), BurstUt), 6);

            // The crewReport row not landed yet: exactly its 3.5 is pending.
            Assert.Equal(3.5, LedgerOrchestrator.ComputePendingRecentKscScienceCredit(
                GameStateStore.Events, BurstRows(includeCrewReport: false), BurstUt), 4);
        }

        [Fact]
        public void NextScienceChangeCapture_ContractRewardBetweenCreditAndCallback_KeepsCapture()
        {
            var capture = GameStateRecorder.NextScienceChangeCapture(
                default(GameStateRecorder.RecentScienceChangeCapture),
                3.5, "VesselRecovery", BurstUt, "");
            Assert.True(capture.Valid);

            // The SCIENCE contract completes on the received data and pays ContractReward
            // science before Parsek's onScienceReceived handler runs.
            capture = GameStateRecorder.NextScienceChangeCapture(capture, 1.0, "ContractReward", BurstUt, null);
            capture = GameStateRecorder.NextScienceChangeCapture(capture, 1.0, "ContractReward", BurstUt, null);
            capture = GameStateRecorder.NextScienceChangeCapture(capture, 3.0, "Progression", BurstUt, null);

            Assert.True(capture.Valid);
            Assert.Equal("VesselRecovery", capture.ReasonKey);
            Assert.True(GameStateRecorder.ShouldUseRecentScienceChangeCapture(capture, 3.5f, BurstUt, ""));
        }

        [Fact]
        public void NextScienceChangeCapture_NegativeMoveClears_SubjectCreditReplaces()
        {
            var capture = GameStateRecorder.NextScienceChangeCapture(
                default(GameStateRecorder.RecentScienceChangeCapture),
                3.5, "VesselRecovery", BurstUt, "rec-a");

            var cleared = GameStateRecorder.NextScienceChangeCapture(capture, -2.0, "StrategyInput", BurstUt, null);
            Assert.False(cleared.Valid);

            var replaced = GameStateRecorder.NextScienceChangeCapture(capture, 4.0, "ScienceTransmission", BurstUt + 1.0, "rec-b");
            Assert.True(replaced.Valid);
            Assert.Equal("ScienceTransmission", replaced.ReasonKey);
            Assert.Equal(4.0f, replaced.Delta);
            Assert.Equal("rec-b", replaced.RecordingId);

            // A non-subject credit with no capture to keep stays empty.
            var none = GameStateRecorder.NextScienceChangeCapture(
                default(GameStateRecorder.RecentScienceChangeCapture), 1.0, "ContractReward", BurstUt, null);
            Assert.False(none.Valid);
        }
    }
}
