using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LAB-RECOVERY-RECONCILE-COALESCED-FALSE-MISMATCH (found by harness lane LAB-14,
    /// run 2026-10-17_2203). Stock pays a recovered science lab's stored science through a
    /// bare AddScience under the SAME reason key as the vessel's subject recovery science
    /// (VesselRecovery), and a lab transmission under ScienceTransmission, the key of an
    /// experiment transmission. GameStateStore coalesces same-UT deltas, so the one
    /// observed ScienceChanged carries both payouts while the post-walk reconciler used to
    /// expect only the ScienceEarning share:
    ///   "ScienceEarning id=recovery@KerbinOrbited expected=10.0, observed=22.5 across 1
    ///    event(s) keyed 'VesselRecovery' ... -- post-walk delta mismatch"
    /// The ScienceLabEarning row now sits on the expected side of whichever comparison
    /// sums its event, and a genuine shortfall still reports.
    /// </summary>
    [Collection("Sequential")]
    public class PostWalkLabCreditReconcileTests : IDisposable
    {
        private const string LabRecordingId = "labx-orbital-lab";
        private readonly List<string> logLines = new List<string>();

        public PostWalkLabCreditReconcileTests()
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

        private static void CommitLabRecording()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = LabRecordingId,
                VesselName = "LABX Orbital Lab",
                MergeState = MergeState.Immutable
            });
        }

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

        /// <summary>A subject ScienceEarning with a persisted [startUt, endUt] span.</summary>
        private static GameAction SubjectScience(
            double ut, float startUt, float endUt, float effective, ScienceMethod method,
            string subjectId, string recordingId)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceEarning,
                RecordingId = recordingId,
                SubjectId = subjectId,
                Method = method,
                Effective = true,
                ScienceAwarded = effective,
                EffectiveScience = effective,
                StartUT = startUt,
                EndUT = endUt
            };
        }

        private static GameAction LabCredit(
            double ut, float credit, ScienceMethod method, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceLabEarning,
                RecordingId = recordingId,
                ScienceAwarded = credit,
                Method = method
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

        // ------------------------------------------------------------------
        // Classifier
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(ScienceMethod.Recovered, "VesselRecovery")]
        [InlineData(ScienceMethod.Transmitted, "ScienceTransmission")]
        public void ClassifyPostWalk_ScienceLabEarning_KeysTheLabPayoutReason(
            ScienceMethod method, string expectedKey)
        {
            var exp = PostWalkActionReconciler.ClassifyPostWalk(LabCredit(372.15, 12.5f, method));

            Assert.True(exp.Reconcile);
            Assert.True(exp.Sci.Applies);
            Assert.Equal(12.5, exp.Sci.Expected, 4);
            Assert.Equal(expectedKey, exp.Sci.ReasonKey);
            Assert.Equal(GameStateEventType.ScienceChanged, exp.Sci.EventType);
            Assert.False(exp.Funds.Applies);
            Assert.False(exp.Rep.Applies);
        }

        [Fact]
        public void ClassifyPostWalk_ScienceLabEarning_NonPositiveCredit_NotReconciled()
        {
            Assert.False(PostWalkActionReconciler.ClassifyPostWalk(
                LabCredit(372.15, 0f, ScienceMethod.Recovered)).Reconcile);
        }

        [Fact]
        public void GetPostWalkPrimaryRank_SubjectRowOutranksLabCredit_RecordingScopeFirst()
        {
            var labUntagged = LabCredit(1, 1f, ScienceMethod.Recovered);
            var labTagged = LabCredit(1, 1f, ScienceMethod.Recovered, LabRecordingId);
            var scienceUntagged = SubjectScience(1, 1f, 1f, 1f, ScienceMethod.Recovered, "s", null);
            var scienceTagged = SubjectScience(1, 1f, 1f, 1f, ScienceMethod.Recovered, "s", LabRecordingId);

            Assert.True(PostWalkActionReconciler.GetPostWalkPrimaryRank(scienceUntagged) >
                        PostWalkActionReconciler.GetPostWalkPrimaryRank(labUntagged));
            Assert.True(PostWalkActionReconciler.GetPostWalkPrimaryRank(scienceTagged) >
                        PostWalkActionReconciler.GetPostWalkPrimaryRank(labTagged));
            // The historical rule is unchanged: recording scope beats an untagged row.
            Assert.True(PostWalkActionReconciler.GetPostWalkPrimaryRank(labTagged) >
                        PostWalkActionReconciler.GetPostWalkPrimaryRank(scienceUntagged));
        }

        // ------------------------------------------------------------------
        // (a) recovery science + lab recovery credit, coalesced into one event
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(true)]   // recovery ScienceEarning tagged to the lab vessel's recording
        [InlineData(false)]  // recovery ScienceEarning untagged (KSC-side capture)
        public void ReconcilePostWalk_RecoveryScienceAndLabCreditCoalesced_Matches(bool scienceTagged)
        {
            // The LAB-14 shape: +10.0 subject recovery science and +12.5 lab science, one
            // coalesced ScienceChanged(VesselRecovery) of +22.5, untagged, at ut=372.15.
            if (scienceTagged) CommitLabRecording();
            var events = new List<GameStateEvent>
            {
                ScienceEvent(372.15, 100.0, 122.5, "VesselRecovery")
            };
            var actions = new List<GameAction>
            {
                SubjectScience(372.15, 372.15f, 372.15f, 10f, ScienceMethod.Recovered,
                    "recovery@KerbinOrbited", scienceTagged ? LabRecordingId : null),
                LabCredit(372.15, 12.5f, ScienceMethod.Recovered)
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            AssertNoWarnOrError();
            string summary = SummaryLine();
            Assert.Contains("compared=1", summary);
            Assert.Contains("matches=1", summary);
            Assert.Contains("mismatches(funds/rep/sci)=0/0/0", summary);
        }

        [Fact]
        public void ReconcilePostWalk_RecoveryScienceAndLabCredit_BothTagged_Matches()
        {
            CommitLabRecording();
            var events = new List<GameStateEvent>
            {
                ScienceEvent(372.15, 100.0, 122.5, "VesselRecovery", LabRecordingId)
            };
            var actions = new List<GameAction>
            {
                // Lab first in list order: the subject row must still own the comparison.
                LabCredit(372.15, 12.5f, ScienceMethod.Recovered, LabRecordingId),
                SubjectScience(372.15, 372.15f, 372.15f, 10f, ScienceMethod.Recovered,
                    "recovery@KerbinOrbited", LabRecordingId)
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            AssertNoWarnOrError();
            Assert.Contains("compared=1", SummaryLine());
            Assert.Contains("matches=1", SummaryLine());
        }

        // ------------------------------------------------------------------
        // (b) lab credit alone
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(ScienceMethod.Recovered, "VesselRecovery")]
        [InlineData(ScienceMethod.Transmitted, "ScienceTransmission")]
        public void ReconcilePostWalk_LabCreditAlone_Matches(ScienceMethod method, string reason)
        {
            var events = new List<GameStateEvent> { ScienceEvent(500.0, 40.0, 52.5, reason) };
            var actions = new List<GameAction> { LabCredit(500.0, 12.5f, method) };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            AssertNoWarnOrError();
            string summary = SummaryLine();
            Assert.Contains("actions=1", summary);
            Assert.Contains("compared=1", summary);
            Assert.Contains("matches=1", summary);
        }

        // ------------------------------------------------------------------
        // (c) a genuine shortfall still reports
        // ------------------------------------------------------------------

        [Fact]
        public void ReconcilePostWalk_ObservedExceedsScienceplusLab_StillMismatches()
        {
            var events = new List<GameStateEvent>
            {
                ScienceEvent(372.15, 100.0, 122.5, "VesselRecovery")
            };
            var actions = new List<GameAction>
            {
                SubjectScience(372.15, 372.15f, 372.15f, 10f, ScienceMethod.Recovered,
                    "recovery@KerbinOrbited", null),
                LabCredit(372.15, 5f, ScienceMethod.Recovered)
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            int warns = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("Earnings reconciliation (post-walk, sci)") &&
                    l.Contains("expected=15.0") &&
                    l.Contains("observed=22.5") &&
                    l.Contains("keyed 'VesselRecovery'"))
                    warns++;
            }
            // Exactly one report: the lab row is reconciled inside the subject row's
            // comparison and must not anchor a second one of its own.
            Assert.Equal(1, warns);
            Assert.Contains("mismatches(funds/rep/sci)=0/0/1", SummaryLine());
        }

        [Fact]
        public void ReconcilePostWalk_LabCreditAloneButObservedLarger_StillMismatches()
        {
            var events = new List<GameStateEvent>
            {
                ScienceEvent(500.0, 40.0, 62.5, "VesselRecovery")
            };
            var actions = new List<GameAction> { LabCredit(500.0, 12.5f, ScienceMethod.Recovered) };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, sci)") &&
                l.Contains("ScienceLabEarning") &&
                l.Contains("expected=12.5") &&
                l.Contains("observed=22.5"));
            Assert.Contains("mismatches(funds/rep/sci)=0/0/1", SummaryLine());
        }

        // ------------------------------------------------------------------
        // (d) transmit path: experiment transmission + lab transmission credit
        // ------------------------------------------------------------------

        [Fact]
        public void ReconcilePostWalk_ExperimentTransmissionAndLabTransmitCoalesced_Matches()
        {
            var events = new List<GameStateEvent>
            {
                ScienceEvent(640.0, 30.0, 40.0, "ScienceTransmission")
            };
            var actions = new List<GameAction>
            {
                SubjectScience(640.0, 640f, 640f, 6f, ScienceMethod.Transmitted,
                    "temperatureScan@KerbinInSpaceLow", null),
                LabCredit(640.0, 4f, ScienceMethod.Transmitted)
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            AssertNoWarnOrError();
            Assert.Contains("compared=1", SummaryLine());
            Assert.Contains("matches=1", SummaryLine());
        }

        [Fact]
        public void ReconcilePostWalk_InFlightLabTransmitInsideEndAnchoredSpan_Matches()
        {
            // In-flight shape: the experiment's ScienceEarning is end-anchored at the
            // recording end (ut=800) with its capture span [790,800], while the lab
            // credit row sits at its own transmission UT (790), outside the same-UT
            // window. The coalesced +10 event at 790 is on the span's observed side,
            // so the lab's share must be on its expected side.
            CommitLabRecording();
            var events = new List<GameStateEvent>
            {
                ScienceEvent(790.0, 30.0, 40.0, "ScienceTransmission", LabRecordingId)
            };
            var actions = new List<GameAction>
            {
                SubjectScience(800.0, 790f, 800f, 6f, ScienceMethod.Transmitted,
                    "temperatureScan@KerbinInSpaceLow", LabRecordingId),
                LabCredit(790.0, 4f, ScienceMethod.Transmitted, LabRecordingId)
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            AssertNoWarnOrError();
            string summary = SummaryLine();
            Assert.Contains("compared=1", summary);
            Assert.Contains("matches=1", summary);
        }

        [Fact]
        public void ReconcilePostWalk_DifferentReasonKeys_LabCreditNotAbsorbed()
        {
            // A lab RECOVERY credit is not on a TRANSMISSION comparison's expected side:
            // each pairs with its own key, so both match on their own.
            var events = new List<GameStateEvent>
            {
                ScienceEvent(640.0, 30.0, 36.0, "ScienceTransmission"),
                ScienceEvent(640.0, 36.0, 48.5, "VesselRecovery")
            };
            var actions = new List<GameAction>
            {
                SubjectScience(640.0, 640f, 640f, 6f, ScienceMethod.Transmitted,
                    "temperatureScan@KerbinInSpaceLow", null),
                LabCredit(640.0, 12.5f, ScienceMethod.Recovered)
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            AssertNoWarnOrError();
            string summary = SummaryLine();
            Assert.Contains("compared=2", summary);
            Assert.Contains("matches=2", summary);
        }
    }
}
