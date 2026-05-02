using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KscScienceSubjectLedgerTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KscScienceSubjectLedgerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TryRecordKscScienceSubject_UntaggedTransmission_AddsNullOwnerScienceEarning()
        {
            var subject = new PendingScienceSubject
            {
                subjectId = "crewReport@KerbinSrfLandedLaunchPad",
                science = 1.5f,
                subjectMaxValue = 5.0f,
                captureUT = 29.4,
                reasonKey = "ScienceTransmission",
                recordingId = ""
            };

            bool handled = LedgerOrchestrator.TryRecordKscScienceSubject(subject, vesselName: null);

            Assert.True(handled);
            var action = Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.Null(action.RecordingId);
            Assert.Equal("crewReport@KerbinSrfLandedLaunchPad", action.SubjectId);
            Assert.Equal(1.5f, action.ScienceAwarded);
            Assert.Equal(29.4, action.UT, 3);
            Assert.Equal(29.4f, action.StartUT);
            Assert.Equal(29.4f, action.EndUT);
            Assert.Equal(ScienceMethod.Transmitted, action.Method);

            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                "crewReport@KerbinSrfLandedLaunchPad",
                out float committedScience));
            Assert.Equal(1.5f, committedScience);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("KSC science recorded") &&
                l.Contains("recordingId=(none)"));
        }

        [Fact]
        public void TryRecordKscScienceSubject_RecoveryWithMatchingRecording_UsesRecoveryOwner()
        {
            var rec = new Recording
            {
                RecordingId = "rec-recovered-science",
                VesselName = "Recovered Probe",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            RecordingStore.AddCommittedInternal(rec);

            var subject = new PendingScienceSubject
            {
                subjectId = "temperatureScan@KerbinSrfLandedLaunchPad",
                science = 1.2f,
                subjectMaxValue = 8.0f,
                captureUT = 250.0,
                reasonKey = LedgerOrchestrator.VesselRecoveryReasonKey,
                recordingId = ""
            };

            bool handled = LedgerOrchestrator.TryRecordKscScienceSubject(
                subject,
                "Recovered Probe");

            Assert.True(handled);
            var action = Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.Equal("rec-recovered-science", action.RecordingId);
            Assert.Equal("temperatureScan@KerbinSrfLandedLaunchPad", action.SubjectId);
            Assert.Equal(ScienceMethod.Recovered, action.Method);
            Assert.Equal(250.0, action.UT, 3);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("PickRecoveryRecordingId") &&
                l.Contains("tier=most-recent-ended") &&
                l.Contains("pick=rec-recovered-science"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("KSC science recorded") &&
                l.Contains("recordingId=rec-recovered-science"));
        }

        [Fact]
        public void TryRecordKscScienceSubject_DuplicateSameSubjectAndUt_IsHandledWithoutSecondLedgerRow()
        {
            var subject = new PendingScienceSubject
            {
                subjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                science = 3.6f,
                subjectMaxValue = 12.0f,
                captureUT = 88.7,
                reasonKey = LedgerOrchestrator.VesselRecoveryReasonKey,
                recordingId = ""
            };

            bool firstHandled = LedgerOrchestrator.TryRecordKscScienceSubject(subject, "Probe");
            bool secondHandled = LedgerOrchestrator.TryRecordKscScienceSubject(subject, "Probe");

            Assert.True(firstHandled);
            Assert.True(secondHandled);
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("duplicate direct ScienceEarning suppressed") &&
                l.Contains("mysteryGoo@KerbinSrfLandedLaunchPad"));
        }

        [Fact]
        public void TryRecordKscScienceSubject_DirectThenRecordingCommitSameSubjectAndUt_DeduplicatesAcrossPaths()
        {
            var subject = new PendingScienceSubject
            {
                subjectId = "crewReport@KerbinSrfLandedLaunchPad",
                science = 1.5f,
                subjectMaxValue = 5.0f,
                captureUT = 150.0,
                reasonKey = "ScienceTransmission",
                recordingId = ""
            };

            bool directHandled = LedgerOrchestrator.TryRecordKscScienceSubject(
                subject,
                vesselName: null);

            var rec = new Recording
            {
                RecordingId = "rec-cross-path-science",
                VesselName = "Science Probe",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
            RecordingStore.AddCommittedInternal(rec);

            var tree = new RecordingTree
            {
                Id = "tree-cross-path-science",
                TreeName = "Cross Path Science",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;

            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subject.subjectId,
                science = subject.science,
                subjectMaxValue = subject.subjectMaxValue,
                captureUT = subject.captureUT,
                reasonKey = subject.reasonKey,
                recordingId = rec.RecordingId
            });

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            Assert.True(directHandled);
            var action = Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.Null(action.RecordingId);
            Assert.Equal("crewReport@KerbinSrfLandedLaunchPad", action.SubjectId);
            Assert.Equal(150.0, action.UT, 3);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("DeduplicateAgainstLedger: removed 1 duplicates"));
        }
    }
}
