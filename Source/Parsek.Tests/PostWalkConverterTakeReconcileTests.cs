using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// POSTWALK-SCIENCE-RECONCILE-IGNORES-CONVERTER-TAKE (found by harness lane LAB-12, run
    /// 2026-10-18_0050). Under a science-converting strategy (Patents Licensing) stock's
    /// CurrencyConverter mutates the CurrencyModifierQuery in place, so the ScienceChanged a
    /// lab payout fires is NET of the take (11.875 of a 12.5 credit) while the post-walk
    /// expected side is the GROSS ScienceAwarded and the take is its own converter-sourced
    /// StrategyScienceDebit row:
    ///   "ScienceLabEarning id=(none) expected=12.5, observed=11.9 across 1 event(s) keyed
    ///    'ScienceTransmission' within 0.1s of ut=510.0 -- post-walk delta mismatch"
    /// plus an ERROR reconcile dump on every lab payout. The KSC door already grossed the
    /// observed side back up through the in-window converter legs; the post-walk compare now
    /// does the same, for every science leg (subject ScienceEarning rows had the same gap).
    /// </summary>
    [Collection("Sequential")]
    public class PostWalkConverterTakeReconcileTests : IDisposable
    {
        private const double LabUt = 510.006082343961;
        private readonly List<string> logLines = new List<string>();

        public PostWalkConverterTakeReconcileTests()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
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

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static GameStateEvent ScienceEvent(
            double ut, double before, double after, string reason, string recordingId = "")
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ScienceChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after,
                recordingId = recordingId
            };
        }

        private static GameAction LabCredit(double ut, float credit, ScienceMethod method)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceLabEarning,
                ScienceAwarded = credit,
                Method = method
            };
        }

        private static GameAction KscSubjectScience(double ut, float effective, ScienceMethod method)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceEarning,
                SubjectId = "recovery@KerbinOrbited",
                Method = method,
                Effective = true,
                ScienceAwarded = effective,
                EffectiveScience = effective,
                StartUT = (float)ut,
                EndUT = (float)ut
            };
        }

        private static GameAction ConverterTake(double ut, float take, string recordingId = null,
            StrategyConversionSource source = StrategyConversionSource.Converter)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyScienceDebit,
                RecordingId = recordingId,
                Cost = take,
                ConversionSource = source
            };
        }

        private static GameAction ConverterYield(double ut, float yield)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyScienceCredit,
                ScienceAwarded = yield
            };
        }

        private string SummaryLine()
        {
            string found = null;
            foreach (var l in logLines)
            {
                if (l.Contains("Post-walk reconcile: actions="))
                    found = l;
            }
            Assert.NotNull(found);
            return found;
        }

        private void AssertNoWarnOrError()
        {
            Assert.DoesNotContain(logLines, l => l.Contains("][WARN]") || l.Contains("][ERROR]"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.DoesNotContain(logLines, l => l.Contains("Science reconcile dump (post-walk)"));
        }

        private void AssertLabMismatchReported()
        {
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("Earnings reconciliation (post-walk, sci): ScienceLabEarning")
                && l.Contains("expected=12.5, observed=11.9")
                && l.Contains("post-walk delta mismatch"));
            Assert.Contains("mismatches(funds/rep/sci)=0/0/1", SummaryLine());
        }

        // ------------------------------------------------------------------
        // The LAB-12 shape
        // ------------------------------------------------------------------

        [Fact]
        public void LabPayoutUnderPatentsLicensing_GrossesTheNetEventUpThroughTheTake_Matches()
        {
            var events = new List<GameStateEvent>
            {
                ScienceEvent(LabUt, 136.75, 148.625, "ScienceTransmission")
            };
            var actions = new List<GameAction>
            {
                ConverterTake(LabUt, 0.625f),
                LabCredit(LabUt, 12.5f, ScienceMethod.Transmitted)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            AssertNoWarnOrError();
            Assert.Contains("mismatches(funds/rep/sci)=0/0/0", SummaryLine());
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("grossed the observed NET credit 11.875 keyed 'ScienceTransmission'")
                && l.Contains("take=0.625")
                && l.Contains("-> 12.5"));
        }

        [Fact]
        public void LabPayoutWithNoTakeRow_GenuineShortfallStillReports()
        {
            // Negative control: without a take row nothing excuses the 0.625 gap.
            var events = new List<GameStateEvent>
            {
                ScienceEvent(LabUt, 136.75, 148.625, "ScienceTransmission")
            };
            var actions = new List<GameAction> { LabCredit(LabUt, 12.5f, ScienceMethod.Transmitted) };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            AssertLabMismatchReported();
            Assert.Contains(logLines, l => l.Contains("[ERROR]") && l.Contains("Science reconcile dump (post-walk)"));
        }

        [Fact]
        public void ExchangerSourcedDebit_IsNotATake_GapStillReports()
        {
            // An exchanger moves science under its own StrategyInput key; it never sits on a
            // reward event's observed side, so it must not be folded in.
            var events = new List<GameStateEvent>
            {
                ScienceEvent(LabUt, 136.75, 148.625, "ScienceTransmission")
            };
            var actions = new List<GameAction>
            {
                ConverterTake(LabUt, 0.625f, source: StrategyConversionSource.Exchanger),
                LabCredit(LabUt, 12.5f, ScienceMethod.Transmitted)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            AssertLabMismatchReported();
        }

        [Fact]
        public void TakeOutsideTheWindow_IsNotFolded()
        {
            var events = new List<GameStateEvent>
            {
                ScienceEvent(LabUt, 136.75, 148.625, "ScienceTransmission")
            };
            var actions = new List<GameAction>
            {
                ConverterTake(LabUt + 1.0, 0.625f),
                LabCredit(LabUt, 12.5f, ScienceMethod.Transmitted)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            AssertLabMismatchReported();
        }

        [Fact]
        public void TakeAfterTheCutoff_IsNotFolded()
        {
            // Mirrors the action filter: a row past the recalc cutoff is not in this walk.
            var events = new List<GameStateEvent>
            {
                ScienceEvent(LabUt, 136.75, 148.625, "ScienceTransmission")
            };
            var actions = new List<GameAction>
            {
                ConverterTake(LabUt + 0.05, 0.625f),
                LabCredit(LabUt, 12.5f, ScienceMethod.Transmitted)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, LabUt + 0.01);

            AssertLabMismatchReported();
        }

        [Fact]
        public void TakeTaggedToAnotherRecording_IsNotFolded()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec-a",
                VesselName = "LABX Orbital Lab",
                MergeState = MergeState.Immutable
            });
            var events = new List<GameStateEvent>
            {
                ScienceEvent(LabUt, 136.75, 148.625, "ScienceTransmission", "rec-a")
            };
            var actions = new List<GameAction>
            {
                ConverterTake(LabUt, 0.625f, recordingId: "rec-b"),
                LabCredit(LabUt, 12.5f, ScienceMethod.Transmitted)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            Assert.Contains(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci): ScienceLabEarning")
                && l.Contains("expected=12.5, observed=11.9"));
        }

        // ------------------------------------------------------------------
        // ScienceEarning legs had the same gap
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(ScienceMethod.Recovered, "VesselRecovery")]
        [InlineData(ScienceMethod.Transmitted, "ScienceTransmission")]
        public void SubjectScienceUnderAConverter_GrossesUpToo_Matches(ScienceMethod method, string key)
        {
            // 10 gross subject science, 5% taken: KSP fires +9.5.
            var events = new List<GameStateEvent> { ScienceEvent(LabUt, 100.0, 109.5, key) };
            var actions = new List<GameAction>
            {
                KscSubjectScience(LabUt, 10f, method),
                ConverterTake(LabUt, 0.5f)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            AssertNoWarnOrError();
            Assert.Contains("mismatches(funds/rep/sci)=0/0/0", SummaryLine());
        }

        [Fact]
        public void SubjectScienceUnderAConverter_WithoutTheTakeRow_StillReports()
        {
            var events = new List<GameStateEvent> { ScienceEvent(LabUt, 100.0, 109.5, "VesselRecovery") };
            var actions = new List<GameAction> { KscSubjectScience(LabUt, 10f, ScienceMethod.Recovered) };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            Assert.Contains(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci): ScienceEarning")
                && l.Contains("expected=10.0, observed=9.5"));
        }

        [Fact]
        public void CoalescedRecoveryAndLabCredit_UnderAConverter_OneTakeFoldsOnce_Matches()
        {
            // PR #2138's coalesced shape (10 subject + 12.5 lab under VesselRecovery) with a
            // 5% take on the combined credit: +21.375 observed, one 1.125 take row. The lab
            // credit stays absorbed into the subject comparison and the take folds once.
            var events = new List<GameStateEvent> { ScienceEvent(372.15, 100.0, 121.375, "VesselRecovery") };
            var actions = new List<GameAction>
            {
                KscSubjectScience(372.15, 10f, ScienceMethod.Recovered),
                LabCredit(372.15, 12.5f, ScienceMethod.Recovered),
                ConverterTake(372.15, 1.125f)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            AssertNoWarnOrError();
            string summary = SummaryLine();
            Assert.Contains("mismatches(funds/rep/sci)=0/0/0", summary);
            Assert.Contains("labCovered=1", summary);
        }

        [Fact]
        public void ConverterYieldRidingInOnACredit_IsNettedOut_Matches()
        {
            // A converter yielding INTO science under the transaction's own reason: the
            // event carries the yield that no expected leg owns.
            var events = new List<GameStateEvent> { ScienceEvent(LabUt, 100.0, 112.0, "ScienceTransmission") };
            var actions = new List<GameAction>
            {
                KscSubjectScience(LabUt, 10f, ScienceMethod.Transmitted),
                ConverterYield(LabUt, 2f)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            AssertNoWarnOrError();
            Assert.Contains("mismatches(funds/rep/sci)=0/0/0", SummaryLine());
        }

        // ------------------------------------------------------------------
        // Attribution: one take never grosses up two comparisons
        // ------------------------------------------------------------------

        [Fact]
        public void TakeSharingItsUtWithCreditsUnderTwoReasons_IsAmbiguous_FoldsIntoNeither()
        {
            // A lab transmission and a recovery credit at the same UT, one untagged take: the
            // row cannot say which credit it came from, so neither comparison claims it. The
            // recovery comparison stays a clean match (no phantom take), the lab one keeps its
            // honest WARN.
            var events = new List<GameStateEvent>
            {
                ScienceEvent(LabUt, 136.75, 148.625, "ScienceTransmission"),
                ScienceEvent(LabUt, 148.625, 158.625, "VesselRecovery")
            };
            var actions = new List<GameAction>
            {
                ConverterTake(LabUt, 0.625f),
                LabCredit(LabUt, 12.5f, ScienceMethod.Transmitted),
                KscSubjectScience(LabUt, 10f, ScienceMethod.Recovered)
            };

            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);

            Assert.Contains(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci): ScienceLabEarning")
                && l.Contains("expected=12.5, observed=11.9"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci): ScienceEarning"));
            Assert.Contains(logLines, l => l.Contains("left 1 in-window strategy converter leg(s) out"));
            Assert.Contains("mismatches(funds/rep/sci)=0/0/1", SummaryLine());
        }

        [Fact]
        public void ComputeConverterScienceFold_NonScienceLeg_IsEmpty()
        {
            var owner = LabCredit(LabUt, 12.5f, ScienceMethod.Transmitted);
            var leg = new PostWalkActionReconciler.PostWalkLeg
            {
                Applies = true,
                Expected = 12.5,
                ReasonKey = "ScienceTransmission",
                EventType = GameStateEventType.FundsChanged
            };
            var fold = PostWalkActionReconciler.ComputeConverterScienceFold(
                owner, leg, LabUt - 0.1, LabUt + 0.1,
                new List<GameAction> { ConverterTake(LabUt, 0.625f) },
                new List<GameStateEvent> { ScienceEvent(LabUt, 0, 11.875, "ScienceTransmission") },
                null, double.NegativeInfinity, hiddenTimeline: false);

            Assert.Equal(0.0, fold.Take);
            Assert.Equal(0, fold.Rows);
        }
    }
}
