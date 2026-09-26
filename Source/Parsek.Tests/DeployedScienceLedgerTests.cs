using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SCIENCE-SUBJECT-RUNNING-TOTAL-OVER-CREDIT and the 2026-09-26 deployed-science
    /// ruling. Every cell drives the production capture core
    /// (<see cref="GameStateRecorder.CaptureScienceSubject"/>) with the values stock's
    /// <c>OnScienceRecieved</c> hands it: <c>amount</c> is the award, <c>subject.science</c>
    /// is the subject's RUNNING total after <c>SubmitScienceData</c> added the award.
    /// </summary>
    [Collection("Sequential")]
    public class DeployedScienceLedgerTests : System.IDisposable
    {
        private const string SeismicSubject = "deployedSeismicSensor@MunSrfLandedMidlands";
        private const string CrewReportSubject = "crewReport@KerbinSrfLandedLaunchPad";

        private readonly List<string> logLines = new List<string>();

        public DeployedScienceLedgerTests()
        {
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

        private static IEnumerable<GameAction> ScienceRows(string subjectId)
        {
            return Ledger.Actions.Where(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == subjectId);
        }

        // ---------- (A) running total vs increment ----------------------

        [Fact]
        public void Ledger_AddsEveryScienceEarningRow_SoRunningTotalRowsWouldOverCredit()
        {
            // Pins WHY the capture must store increments: the ledger ADDS rows per subject.
            // This is the exact row shape the pre-fix capture wrote (science = subject.science)
            // for three deployed sends of 8 each, and it credits 8 + 16 + 24 = 48 where stock
            // holds 24.
            SeedZeroScience();
            float[] runningTotals = { 8f, 16f, 24f };
            for (int i = 0; i < runningTotals.Length; i++)
            {
                LedgerOrchestrator.TryRecordKscScienceSubject(new PendingScienceSubject
                {
                    subjectId = SeismicSubject,
                    science = runningTotals[i],
                    subjectMaxValue = 80f,
                    captureUT = 100.0 + 60.0 * i,
                    reasonKey = "VesselRecovery",
                    recordingId = ""
                }, vesselName: null);
            }

            Assert.Equal(48.0, LedgerOrchestrator.Science.GetSubjectCredited(SeismicSubject), 3);
        }

        [Fact]
        public void Capture_ThreeDeployedSendsAtKsc_CreditsStockRunningTotal()
        {
            SeedZeroScience();
            NoRecorder();
            var recorder = new GameStateRecorder();
            float[] runningTotals = { 8f, 16f, 24f };
            for (int i = 0; i < runningTotals.Length; i++)
            {
                recorder.CaptureScienceSubject(
                    amount: 8f,
                    subjectId: SeismicSubject,
                    subjectScienceAfter: runningTotals[i],
                    subjectCap: 80f,
                    scienceGainMultiplier: 1f,
                    captureUt: 100.0 + 60.0 * i,
                    vesselName: null,
                    launchGuid: null);
            }

            var rows = ScienceRows(SeismicSubject).ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(8f, r.ScienceAwarded));
            Assert.Equal(24.0, LedgerOrchestrator.Science.GetSubjectCredited(SeismicSubject), 3);
            Assert.Equal(24.0, LedgerOrchestrator.Science.GetAvailableScience(), 3);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void Capture_RepeatedOrdinarySubject_CreditsStockRunningTotal()
        {
            // Scope: the over-credit was never deployed-only. Any subject submitted twice
            // (a second transmission, a recovery after a transmit) re-credited the first.
            SeedZeroScience();
            NoRecorder();
            var recorder = new GameStateRecorder();
            recorder.CaptureScienceSubject(1.5f, CrewReportSubject, 1.5f, 5f, 1f, 50.0, null, null);
            recorder.CaptureScienceSubject(0.9f, CrewReportSubject, 2.4f, 5f, 1f, 70.0, null, null);

            Assert.Equal(new[] { 1.5f, 0.9f },
                ScienceRows(CrewReportSubject).Select(r => r.ScienceAwarded).ToArray());
            Assert.Equal(2.4, LedgerOrchestrator.Science.GetSubjectCredited(CrewReportSubject), 3);
        }

        [Fact]
        public void Capture_SubjectCarryingInjectedCommittedScience_AddsOnlyTheNewAward()
        {
            // ScienceSubjectPatch raises subject.science to the committed credit before stock
            // adds the new award, so the running total includes science the ledger already
            // holds. Only the increment may be filed.
            SeedZeroScience();
            Ledger.AddAction(new GameAction
            {
                UT = 10.0,
                Type = GameActionType.ScienceEarning,
                SubjectId = CrewReportSubject,
                ScienceAwarded = 3f,
                SubjectMaxValue = 5f,
                Method = ScienceMethod.Recovered
            });
            NoRecorder();
            var recorder = new GameStateRecorder();
            recorder.CaptureScienceSubject(1.2f, CrewReportSubject, 4.2f, 5f, 1f, 90.0, null, null);

            Assert.Equal(4.2, LedgerOrchestrator.Science.GetSubjectCredited(CrewReportSubject), 3);
        }

        [Fact]
        public void Capture_InFlightTaggedSubject_PendsTheIncrement()
        {
            LiveRecorder("rec-live");
            var recorder = new GameStateRecorder();
            recorder.CaptureScienceSubject(1.5f, CrewReportSubject, 1.5f, 5f, 1f, 50.0, null, null);
            recorder.CaptureScienceSubject(0.9f, CrewReportSubject, 2.4f, 5f, 1f, 70.0, null, null);

            Assert.Empty(ScienceRows(CrewReportSubject));
            Assert.Equal(new[] { 1.5f, 0.9f },
                GameStateRecorder.PendingScienceSubjects.Select(s => s.science).ToArray());
            Assert.All(GameStateRecorder.PendingScienceSubjects,
                s => Assert.Equal("rec-live", s.recordingId));

            var actions = GameStateEventConverter.ConvertScienceSubjects(
                GameStateRecorder.PendingScienceSubjects, "rec-live", 40.0, 80.0);
            var module = new ScienceModule();
            foreach (var a in actions)
                module.ProcessEarning(a);
            Assert.Equal(2.4, module.GetSubjectCredited(CrewReportSubject), 3);
        }

        [Theory]
        [InlineData(8f, 1f, 24f, 8f)]
        [InlineData(3f, 1.5f, 2f, 2f)]      // stock scales the event by ScienceGainMultiplier
        [InlineData(3f, 0f, 9f, 3f)]        // no usable multiplier: the award stands
        [InlineData(3f, float.NaN, 9f, 3f)]
        [InlineData(6f, 1f, 4f, 4f)]        // never more than the running total
        [InlineData(6f, 1f, 0f, 6f)]        // unknown running total: no clamp
        [InlineData(0f, 1f, 4f, 0f)]
        [InlineData(-2f, 1f, 4f, 0f)]
        public void ComputeScienceSubjectIncrement_Cases(
            float amount, float multiplier, float runningTotal, float expected)
        {
            Assert.Equal((double)expected,
                (double)GameStateRecorder.ComputeScienceSubjectIncrement(amount, multiplier, runningTotal),
                3);
        }

        // ---------- (B) deployed science is always untagged -------------

        [Theory]
        [InlineData("deployedSeismicSensor@MunSrfLandedMidlands", true)]
        [InlineData("deployedWeatherReport@KerbinSrfLandedShores", true)]
        [InlineData("deployedGooObservation@MinmusSrfLandedFlats", true)]
        [InlineData("deployedIONCollector@DunaSrfLandedHighlands", true)]
        [InlineData("crewReport@KerbinSrfLandedLaunchPad", false)]
        [InlineData("seismicScan@MunSrfLandedMidlands", false)]
        [InlineData("DeployedSeismicSensor@Mun", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsDeployedScienceSubjectId_Cases(string subjectId, bool expected)
        {
            Assert.Equal(expected, GameStateRecorder.IsDeployedScienceSubjectId(subjectId));
        }

        [Fact]
        public void Capture_DeployedSubjectInFlight_WritesUntaggedLedgerRowAndPendsNothing()
        {
            SeedZeroScience();
            LiveRecorder("rec-live");
            var recorder = new GameStateRecorder();
            recorder.CaptureScienceSubject(8f, SeismicSubject, 8f, 80f, 1f, 300.0, null, null);

            var row = Assert.Single(ScienceRows(SeismicSubject));
            Assert.Null(row.RecordingId);
            Assert.Equal(8f, row.ScienceAwarded);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") &&
                l.Contains("routed untagged") &&
                l.Contains("live tag 'rec-live'"));
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") &&
                l.Contains("Science subject captured: " + SeismicSubject) &&
                l.Contains("tag=''") &&
                l.Contains("deployed=True directLedger=True"));
        }

        [Fact]
        public void Capture_DeployedSubjectMatchingTaggedScienceChange_UntagsThatEvent()
        {
            SeedZeroScience();
            LiveRecorder("rec-live");
            var evt = new GameStateEvent
            {
                ut = 300.0,
                eventType = GameStateEventType.ScienceChanged,
                key = "VesselRecovery",
                valueBefore = 0.0,
                valueAfter = 8.0,
                recordingId = "rec-live"
            };
            GameStateStore.AddEvent(ref evt);

            var recorder = new GameStateRecorder();
            recorder.SetLatestScienceChangeCaptureForTesting(new GameStateRecorder.RecentScienceChangeCapture
            {
                Ut = 300.0,
                ReasonKey = "VesselRecovery",
                Delta = 8f,
                RecordingId = "rec-live",
                Valid = true
            });
            recorder.CaptureScienceSubject(8f, SeismicSubject, 8f, 80f, 1f, 300.0, null, null);

            var stored = Assert.Single(GameStateStore.Events.Where(e =>
                e.eventType == GameStateEventType.ScienceChanged));
            Assert.Equal("", stored.recordingId ?? "");
            var row = Assert.Single(ScienceRows(SeismicSubject));
            Assert.Null(row.RecordingId);
            Assert.Equal(ScienceMethod.Recovered, row.Method);
        }

        [Fact]
        public void Capture_OrdinaryExperimentInFlight_StaysTaggedToTheRecording()
        {
            LiveRecorder("rec-live");
            var recorder = new GameStateRecorder();
            recorder.CaptureScienceSubject(1.5f, CrewReportSubject, 1.5f, 5f, 1f, 300.0, null, null);

            Assert.Empty(ScienceRows(CrewReportSubject));
            var pending = Assert.Single(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal("rec-live", pending.recordingId);
        }

        [Fact]
        public void TryRecordKscScienceSubject_DeployedRecoveryReasonWithNamedVessel_SkipsRecoveryPicker()
        {
            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = "rec-probe",
                VesselName = "Probe",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            });

            bool handled = LedgerOrchestrator.TryRecordKscScienceSubject(new PendingScienceSubject
            {
                subjectId = SeismicSubject,
                science = 8f,
                subjectMaxValue = 80f,
                captureUT = 250.0,
                reasonKey = LedgerOrchestrator.VesselRecoveryReasonKey,
                recordingId = ""
            }, "Probe");

            Assert.True(handled);
            Assert.Null(Assert.Single(ScienceRows(SeismicSubject)).RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("deployed-experiment subject='" + SeismicSubject + "'") &&
                l.Contains("recovery picker skipped"));
        }

        [Fact]
        public void IsRetryBlockingRecordingAction_DeployedScienceNeverSealsAReFlySlot()
        {
            var deployed = new GameAction
            {
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec-refly",
                SubjectId = SeismicSubject,
                ScienceAwarded = 8f,
                Method = ScienceMethod.Recovered
            };
            var ordinary = new GameAction
            {
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec-refly",
                SubjectId = CrewReportSubject,
                ScienceAwarded = 1.5f,
                Method = ScienceMethod.Transmitted
            };
            var actions = new List<GameAction> { deployed, ordinary };

            Assert.False(SupersedeCommit.IsRetryBlockingRecordingAction(deployed, actions));
            Assert.True(SupersedeCommit.IsRetryBlockingRecordingAction(ordinary, actions));
        }
    }
}
