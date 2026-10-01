using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SCIENCE-SAME-SUBJECT-SAME-INSTANT-ROW-DROPPED: stock calls <c>SubmitScienceData</c>
    /// once per data item, so two canisters of one subject recovered together (or two
    /// deployed stations sending one subject in one frame) are two awards at the same
    /// instant. Each capture mints its own identity
    /// (<see cref="PendingScienceSubject.captureActionId"/>), the converted row adopts it as
    /// its <c>ActionId</c>, and <see cref="LedgerOrchestrator.DeduplicateAgainstLedger"/>
    /// drops a science row only when that SAME capture is presented again. Every cell
    /// drives the recorder's headless capture core and the real ledger paths, then recalcs.
    /// </summary>
    [Collection("Sequential")]
    public class ScienceSameInstantAwardTests : IDisposable
    {
        private const string Goo = "mysteryGoo@KerbinSrfLandedLaunchPad";
        private const string Thermo = "temperatureScan@KerbinSrfLandedLaunchPad";
        private const string CrewReport = "crewReport@KerbinSrfLandedLaunchPad";
        private const string Seismic = "deployedSeismicSensor@MunSrfLandedMidlands";

        private readonly List<string> logLines = new List<string>();

        public ScienceSameInstantAwardTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static void SeedZeroScience()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = 0f
            });
        }

        private static void NoRecorder()
        {
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => false;
        }

        private static void LiveRecorder(string tag)
        {
            GameStateRecorder.TagResolverForTesting = () => tag;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
        }

        /// <summary>One stock callback: amount (multiplier 1) and the running total after it.</summary>
        private static void Capture(
            GameStateRecorder recorder, string subjectId, float amount, float totalAfter,
            double ut, string vesselName = null)
        {
            recorder.CaptureScienceSubject(
                amount, subjectId, totalAfter, 40f, 1f, ut, vesselName, null);
        }

        /// <summary>
        /// One stock recovery submission: stock fires ScienceChanged(VesselRecovery) and then
        /// OnScienceReceived, so the recorder sees a matching capture before every callback.
        /// </summary>
        private static void CaptureRecovery(
            GameStateRecorder recorder, string subjectId, float amount, float totalAfter,
            double ut, string vesselName)
        {
            recorder.SetLatestScienceChangeCaptureForTesting(
                new GameStateRecorder.RecentScienceChangeCapture
                {
                    Ut = ut,
                    ReasonKey = LedgerOrchestrator.VesselRecoveryReasonKey,
                    Delta = amount,
                    RecordingId = "",
                    Valid = true
                });
            Capture(recorder, subjectId, amount, totalAfter, ut, vesselName);
        }

        /// <summary>Captures in flight and takes the parked subjects back out for a commit.</summary>
        private static List<PendingScienceSubject> TakePending()
        {
            var taken = new List<PendingScienceSubject>(GameStateRecorder.PendingScienceSubjects);
            GameStateRecorder.PendingScienceSubjects.Clear();
            return taken;
        }

        private static void Commit(string recordingId, double startUT, double endUT,
            List<PendingScienceSubject> subjects)
        {
            bool scienceAdded = false;
            LedgerOrchestrator.OnRecordingCommitted(
                recordingId, startUT, endUT, subjects, ref scienceAdded);
        }

        private static List<GameAction> ScienceRows(string subjectId)
        {
            return Ledger.Actions.Where(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == subjectId).ToList();
        }

        private static double Credited(string subjectId)
        {
            return LedgerOrchestrator.Science.GetSubjectCredited(subjectId);
        }

        // ================================================================
        // Second award at the same instant
        // ================================================================

        [Fact]
        public void TwoSameInstantKscAwardsOfOneSubject_CreditBoth()
        {
            // The bug: the second callback's row was dropped against the first by
            // subject + 0.1 s capture window, so the ledger credited 5 where stock has 10.
            SeedZeroScience();
            NoRecorder();
            var recorder = new GameStateRecorder();
            Capture(recorder, Goo, 5f, 5f, 88.7);
            Capture(recorder, Goo, 5f, 10f, 88.7);

            LedgerOrchestrator.RecalculateAndPatch();

            var rows = ScienceRows(Goo);
            Assert.Equal(2, rows.Count);
            Assert.NotEqual(rows[0].ActionId, rows[1].ActionId);
            Assert.Equal(10.0, Credited(Goo), 3);
            Assert.Equal(10.0, LedgerOrchestrator.Science.GetAvailableScience(), 3);
            Assert.DoesNotContain(logLines, l => l.Contains("duplicate direct ScienceEarning suppressed"));
        }

        [Fact]
        public void RecoveryBurst_SeveralSubjectsWithRepeats_CreditsEveryCallback()
        {
            SeedZeroScience();
            NoRecorder();
            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = "rec-recovered",
                VesselName = "Recovered Probe",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            });

            // One recovery: two goo canisters, two thermometers, one crew report, all at
            // the recovery instant.
            var recorder = new GameStateRecorder();
            CaptureRecovery(recorder, Goo, 4f, 4f, 250.0, "Recovered Probe");
            CaptureRecovery(recorder, Thermo, 3f, 3f, 250.0, "Recovered Probe");
            CaptureRecovery(recorder, Goo, 4f, 8f, 250.0, "Recovered Probe");
            CaptureRecovery(recorder, CrewReport, 2f, 2f, 250.0, "Recovered Probe");
            CaptureRecovery(recorder, Thermo, 3f, 6f, 250.0, "Recovered Probe");

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(2, ScienceRows(Goo).Count);
            Assert.Equal(2, ScienceRows(Thermo).Count);
            Assert.Single(ScienceRows(CrewReport));
            Assert.All(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning),
                a =>
                {
                    Assert.Equal("rec-recovered", a.RecordingId);
                    Assert.Equal(ScienceMethod.Recovered, a.Method);
                });
            Assert.Equal(8.0, Credited(Goo), 3);
            Assert.Equal(6.0, Credited(Thermo), 3);
            Assert.Equal(2.0, Credited(CrewReport), 3);
            Assert.Equal(16.0, LedgerOrchestrator.Science.GetAvailableScience(), 3);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void TwoDeployedStationsSendOneSubjectInOneFrame_DuringAFlight_BothUntaggedRowsCredit()
        {
            // The deployed-science ruling (PR #1883): written straight to the ledger
            // untagged even with a live recorder, so it reaches the same direct-path dedup.
            SeedZeroScience();
            LiveRecorder("rec-live");
            var recorder = new GameStateRecorder();
            Capture(recorder, Seismic, 6f, 6f, 300.0);
            Capture(recorder, Seismic, 6f, 12f, 300.0);

            LedgerOrchestrator.RecalculateAndPatch();

            var rows = ScienceRows(Seismic);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Null(r.RecordingId));
            Assert.Equal(12.0, Credited(Seismic), 3);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void TwoSameInstantAwardsInOneRecording_CommitCreditsBoth()
        {
            SeedZeroScience();
            LiveRecorder("rec-a");
            var recorder = new GameStateRecorder();
            Capture(recorder, Goo, 5f, 5f, 150.0);
            Capture(recorder, Goo, 5f, 10f, 150.0);
            var pending = TakePending();
            Assert.Equal(2, pending.Count);

            Commit("rec-a", 100.0, 200.0, pending);
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(2, ScienceRows(Goo).Count);
            Assert.Equal(10.0, Credited(Goo), 3);
        }

        [Fact]
        public void FreshCaptureBesideALegacyRowAtTheSameInstant_IsKept()
        {
            // A legacy row (older build, no capture identity) can never be a filing of a
            // capture this build made, so it does not absorb a new award of its subject.
            SeedZeroScience();
            Ledger.AddAction(new GameAction
            {
                UT = 88.7,
                Type = GameActionType.ScienceEarning,
                SubjectId = Goo,
                ScienceAwarded = 5f,
                SubjectMaxValue = 40f,
                StartUT = 88.7f,
                EndUT = 88.7f,
                Method = ScienceMethod.Recovered
            });
            NoRecorder();
            Capture(new GameStateRecorder(), Goo, 5f, 10f, 88.7);

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(2, ScienceRows(Goo).Count);
            Assert.Equal(10.0, Credited(Goo), 3);
        }

        // ================================================================
        // Re-files of a capture are still dropped
        // ================================================================

        [Fact]
        public void KscFiledCapture_ReFiledAtRecordingCommit_IsDropped()
        {
            SeedZeroScience();
            LiveRecorder("rec-a");
            Capture(new GameStateRecorder(), Goo, 5f, 5f, 150.0);
            var captured = Assert.Single(TakePending());

            // The capture is filed live as a direct row, then the same capture reaches
            // the recording commit.
            var direct = captured;
            direct.recordingId = "";
            Assert.True(LedgerOrchestrator.TryRecordKscScienceSubject(direct, vesselName: null));
            Commit("rec-a", 100.0, 200.0, new List<PendingScienceSubject> { captured });
            LedgerOrchestrator.RecalculateAndPatch();

            var row = Assert.Single(ScienceRows(Goo));
            Assert.Null(row.RecordingId);
            Assert.Equal(captured.captureActionId, row.ActionId);
            Assert.Equal(5.0, Credited(Goo), 3);
            Assert.Contains(logLines, l =>
                l.Contains("DeduplicateAgainstLedger: removed 1 duplicates") &&
                l.Contains("scienceCaptureIdMatches=1"));
        }

        [Fact]
        public void BothSameInstantRows_ReFiledAtCommit_AreBothDropped()
        {
            SeedZeroScience();
            LiveRecorder("rec-a");
            var recorder = new GameStateRecorder();
            Capture(recorder, Goo, 5f, 5f, 150.0);
            Capture(recorder, Goo, 5f, 10f, 150.0);
            var captured = TakePending();
            Assert.Equal(2, captured.Count);

            for (int i = 0; i < captured.Count; i++)
            {
                var direct = captured[i];
                direct.recordingId = "";
                Assert.True(LedgerOrchestrator.TryRecordKscScienceSubject(direct, vesselName: null));
            }
            Assert.Equal(2, ScienceRows(Goo).Count);

            Commit("rec-a", 100.0, 200.0, captured);
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(2, ScienceRows(Goo).Count);
            Assert.All(ScienceRows(Goo), r => Assert.Null(r.RecordingId));
            Assert.Equal(10.0, Credited(Goo), 3);
            Assert.Contains(logLines, l =>
                l.Contains("DeduplicateAgainstLedger: removed 2 duplicates") &&
                l.Contains("scienceCaptureIdMatches=2"));
        }

        [Fact]
        public void CommitRetry_ReFilesBothSameInstantRows_NoDoubleCount()
        {
            // A commit that reached the ledger and then failed keeps its pending subjects;
            // the retry converts the same captures again.
            SeedZeroScience();
            LiveRecorder("rec-a");
            var recorder = new GameStateRecorder();
            Capture(recorder, Goo, 5f, 5f, 150.0);
            Capture(recorder, Goo, 5f, 10f, 150.0);
            var captured = TakePending();

            Commit("rec-a", 100.0, 200.0, captured);
            Commit("rec-a", 100.0, 200.0, captured);
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(2, ScienceRows(Goo).Count);
            Assert.Equal(10.0, Credited(Goo), 3);
        }

        [Fact]
        public void DiscardReHome_DropsTheFiledCaptureButKeepsASecondAwardAtTheSameInstant()
        {
            SeedZeroScience();
            LiveRecorder("rec-discarded");
            var recorder = new GameStateRecorder();
            Capture(recorder, Goo, 5f, 5f, 150.0);
            Capture(recorder, Goo, 5f, 10f, 150.0);
            var captured = TakePending();

            // The first capture already has its row; both re-home off the discarded tree.
            var direct = captured[0];
            direct.recordingId = "";
            Assert.True(LedgerOrchestrator.TryRecordKscScienceSubject(direct, vesselName: null));
            GameStateRecorder.PendingScienceSubjects.AddRange(captured);

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-discarded" }, "test-discard");
            LedgerOrchestrator.RecalculateAndPatch();

            var rows = ScienceRows(Goo);
            Assert.Equal(2, rows.Count);
            Assert.Equal(
                new[] { captured[0].captureActionId, captured[1].captureActionId }.OrderBy(s => s),
                rows.Select(r => r.ActionId).OrderBy(s => s));
            Assert.Equal(10.0, Credited(Goo), 3);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void CaptureIdentity_SurvivesSaveLoad_SoAReFileAfterReloadIsStillDropped()
        {
            SeedZeroScience();
            LiveRecorder("rec-a");
            Capture(new GameStateRecorder(), Goo, 5f, 5f, 150.0);
            var captured = Assert.Single(TakePending());
            var direct = captured;
            direct.recordingId = "";
            Assert.True(LedgerOrchestrator.TryRecordKscScienceSubject(direct, vesselName: null));

            var filed = Assert.Single(ScienceRows(Goo));
            var parent = new ConfigNode("LEDGER");
            filed.SerializeInto(parent);
            var restored = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));
            Assert.Equal(captured.captureActionId, restored.ActionId);
            Assert.False(restored.HasScienceCaptureIdentity);

            Ledger.ResetForTesting();
            SeedZeroScience();
            Ledger.AddAction(restored);
            Commit("rec-a", 100.0, 200.0, new List<PendingScienceSubject> { captured });
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Single(ScienceRows(Goo));
            Assert.Equal(5.0, Credited(Goo), 3);
        }

        // ================================================================
        // The dedup rule itself
        // ================================================================

        private static GameAction ScienceCandidate(string actionId, bool captureIdentified)
        {
            return new GameAction
            {
                ActionId = actionId,
                HasScienceCaptureIdentity = captureIdentified,
                UT = 150.0,
                Type = GameActionType.ScienceEarning,
                SubjectId = Goo,
                ScienceAwarded = 5f,
                StartUT = 150f,
                EndUT = 150f
            };
        }

        [Fact]
        public void Dedup_SameCaptureTwiceInOneBatch_KeepsOne()
        {
            var survivors = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction>
            {
                ScienceCandidate("act_capture_one", true),
                ScienceCandidate("act_capture_one", true),
                ScienceCandidate("act_capture_two", true)
            });

            Assert.Equal(new[] { "act_capture_one", "act_capture_two" },
                survivors.Select(a => a.ActionId));
            Assert.Contains(logLines, l => l.Contains("scienceCaptureIdBatchRepeats=1"));
        }

        [Fact]
        public void Dedup_CandidateWithoutCaptureIdentity_KeepsTheLegacySubjectAndMomentMatch()
        {
            Ledger.AddAction(ScienceCandidate("act_filed", true));

            var survivors = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction>
            {
                ScienceCandidate("act_unidentified", false)
            });

            Assert.Empty(survivors);
        }

        [Fact]
        public void Dedup_CaptureIdentifiedCandidate_IgnoresOtherTypesSharingTheMoment()
        {
            // Mirror direction: the science branch does not change how other types match.
            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch"
            });
            var milestone = new GameAction
            {
                UT = 150.05,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch"
            };

            var survivors = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction>
            {
                ScienceCandidate("act_capture_fresh", true),
                milestone
            });

            var kept = Assert.Single(survivors);
            Assert.Equal("act_capture_fresh", kept.ActionId);
        }
    }
}
