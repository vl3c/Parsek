using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-10-24: an event no flight causes is filed under no flight. A Breaking
    /// Ground ground station's science submission and a Sentinel telescope's detection run on
    /// their own clock while any flight is live; stock pays the award, its strategy
    /// conversions and any contract they complete inside one call, which
    /// <see cref="DeployedScienceSubmitScopePatch"/> and <see cref="SentinelDetectionScopePatch"/>
    /// wrap in the recorder's clock-cause scope (todos
    /// CONTRACT-COMPLETED-BY-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT,
    /// STRATEGY-CONVERSION-OF-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT).
    /// </summary>
    [Collection("Sequential")]
    public class ClockCauseScopeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ClockCauseScopeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static readonly Guid G1 = new Guid("5c0e7a11-0000-4000-8000-000000000001");

        private static void InFlightWithALiveRecorder()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
        }

        private static ScienceSubject Subject(string id)
        {
            var subject = (ScienceSubject)FormatterServices.GetUninitializedObject(typeof(ScienceSubject));
            subject.id = id;
            return subject;
        }

        private static bool InvokeSubmitPrefix(ScienceSubject subject)
        {
            var prefix = typeof(DeployedScienceSubmitScopePatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { subject, 1f, null, false };
            prefix.Invoke(null, args);
            return (bool)args[3];
        }

        private static void InvokeSubmitFinalizer(bool scoped)
        {
            var finalizer = typeof(DeployedScienceSubmitScopePatch).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            finalizer.Invoke(null, new object[] { null, scoped });
        }

        // ================================================================
        // The patch targets and the ground-station predicate
        // ================================================================

        // catches: a stock signature drift silently dropping the scope (Harmony skips a null
        // target, and every deployed completion is tagged to the flight again).
        [Fact]
        public void Targets_ResolveAgainstStock()
        {
            Assert.NotNull(DeployedScienceSubmitScopePatch.ResolveTargetMethodForTesting());
            var sentinel = SentinelDetectionScopePatch.ResolveTargetMethodsForTesting();
            Assert.Equal(2, sentinel.Count);
            Assert.Contains(sentinel, m => m.Name == "SpawnComet" && m.DeclaringType == typeof(SentinelMission.SentinelScenario));
            Assert.Contains(sentinel, m => m.Name == "DiscoverAsteroid" && m.DeclaringType == typeof(SentinelMission.SentinelParameter));
        }

        // catches (second row): a ground station's data a kerbal carried aboard and the
        // vessel transmits or recovers losing its flight's tag, with any body science
        // milestone it completes.
        [Theory]
        [InlineData("deployedSeismicSensor@MunSrfLandedMidlands", false, true)]
        [InlineData("deployedSeismicSensor@MunSrfLandedMidlands", true, false)]
        [InlineData("deployedWeatherReport@DunaSrfLanded", false, true)]
        [InlineData("crewReport@KerbinSrfLandedLaunchPad", false, false)]
        [InlineData("crewReport@KerbinSrfLandedLaunchPad", true, false)]
        [InlineData("DeployedSeismicSensor@Mun", false, false)]
        [InlineData("", false, false)]
        [InlineData(null, false, false)]
        public void IsGroundStationSubmission_OnlyForADeployedSubjectNoVesselSubmits(
            string subjectId, bool hasSourceVessel, bool expected)
        {
            Assert.Equal(expected, DeployedScienceSubmitScopePatch.IsGroundStationSubmission(subjectId, hasSourceVessel));
        }

        [Fact]
        public void SubmitPrefix_DeployedSubject_OpensTheScope_FinalizerClosesIt()
        {
            bool scoped = InvokeSubmitPrefix(Subject("deployedGooObservation@MunSrfLanded"));
            Assert.True(scoped);
            Assert.True(GameStateRecorder.InClockCauseScope);
            InvokeSubmitFinalizer(scoped);
            Assert.False(GameStateRecorder.InClockCauseScope);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("Clock-caused transition begins: ground-station science deployedGooObservation@MunSrfLanded"));
        }

        [Theory]
        [InlineData(1f, "VesselRecovery")]
        [InlineData(0.5f, "ScienceTransmission")]
        public void SubmissionReasonKey_FollowsStocksXmitScalarRule(float xmitScalar, string expected)
        {
            Assert.Equal(expected, DeployedScienceSubmitScopePatch.SubmissionReasonKey(xmitScalar));
        }

        [Fact]
        public void SubmitPrefix_DeployedSubject_SetsTheSubmissionReason_FinalizerClearsIt()
        {
            bool scoped = InvokeSubmitPrefix(Subject("deployedGooObservation@MunSrfLanded"));
            Assert.Equal("VesselRecovery", GameStateRecorder.GroundStationSubmissionReasonKey);
            InvokeSubmitFinalizer(scoped);
            Assert.Null(GameStateRecorder.GroundStationSubmissionReasonKey);
        }

        // catches: CLK-1 2026-10-25_0945 - a contract completed by another subscriber of the
        // same OnScienceRecieved fire pays its science reward first, which clears the award's
        // ScienceChanged capture; the subject then filed with no reason, a Transmitted row the
        // post-walk reconcile could not pair with stock's VesselRecovery event (ERROR dump).
        [Fact]
        public void DeployedSubject_CaptureClearedByANestedReward_TakesTheSubmissionReason()
        {
            InFlightWithALiveRecorder();
            var recorder = new GameStateRecorder();
            GameStateRecorder.EnterClockCauseScope("test ground station");
            GameStateRecorder.GroundStationSubmissionReasonKey = "VesselRecovery";
            try
            {
                recorder.SetLatestScienceChangeCaptureForTesting(default(GameStateRecorder.RecentScienceChangeCapture));
                recorder.CaptureScienceSubject(0.3f, "deployedSeismicSensor@KerbinSrfLanded", 0.3f, 30f, 1f, 411.2,
                    vesselName: null, launchGuid: null);
            }
            finally
            {
                GameStateRecorder.GroundStationSubmissionReasonKey = null;
                GameStateRecorder.ExitClockCauseScope("test ground station");
            }

            Assert.Contains(logLines, l => l.Contains("Science subject captured: deployedSeismicSensor@KerbinSrfLanded")
                && l.Contains("reason='VesselRecovery'") && l.Contains("tag=''"));
            var row = Ledger.Actions.Single(a => a.Type == GameActionType.ScienceEarning
                && a.SubjectId == "deployedSeismicSensor@KerbinSrfLanded");
            Assert.Equal(ScienceMethod.Recovered, row.Method);
            Assert.Equal("VesselRecovery", LedgerOrchestrator.GetScienceChangedReasonKey(row));
        }

        // catches: a vessel's own transmission or recovery losing its flight's tag.
        [Fact]
        public void SubmitPrefix_VesselSubject_LeavesTheScopeClosed()
        {
            Assert.False(InvokeSubmitPrefix(Subject("crewReport@KerbinSrfLandedLaunchPad")));
            Assert.False(InvokeSubmitPrefix(null));
            Assert.False(GameStateRecorder.InClockCauseScope);
        }

        // ================================================================
        // Inside the scope: the completion is untagged and written at once
        // ================================================================

        // catches: CONTRACT-COMPLETED-BY-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT - a ground
        // station's completion filed under the live flight, so a discard drops a reward stock
        // already paid and a Re-Fly reads it as the attempt's gain.
        [Fact]
        public void ContractCompleted_InsideTheScope_IsUntaggedAndInTheLedgerAtOnce()
        {
            InFlightWithALiveRecorder();
            GameStateRecorder.EnterClockCauseScope("test ground station");
            try
            {
                new GameStateRecorder().CaptureContractCompleted(
                    G1.ToString(), "Collect seismic data on the Mun", 42000f, 12f, 0f, 500.0);
            }
            finally
            {
                GameStateRecorder.ExitClockCauseScope("test ground station");
            }

            var stored = GameStateStore.Events.Where(e => e.eventType == GameStateEventType.ContractCompleted).ToList();
            Assert.Single(stored);
            Assert.Equal("", stored[0].recordingId ?? "");
            var rows = Ledger.Actions.Where(a => a.Type == GameActionType.ContractComplete && a.ContractId == G1.ToString()).ToList();
            Assert.Single(rows);
            Assert.True(string.IsNullOrEmpty(rows[0].RecordingId));
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("ContractCompleted 'Collect seismic data on the Mun'")
                && l.Contains("completed by no flight, filed untagged"));
            // An untagged event in a clock-cause scope is the design, not tag drift.
            Assert.DoesNotContain(logLines, l => l.Contains("Emit drift") && l.Contains("ContractCompleted"));
        }

        // catches: the fix untagging a completion the flight itself makes.
        [Fact]
        public void ContractCompleted_OutsideTheScope_KeepsTheFlightTag_AndWaitsForTheCommit()
        {
            InFlightWithALiveRecorder();
            new GameStateRecorder().CaptureContractCompleted(G1.ToString(), "Orbit the Mun", 42000f, 0f, 0f, 500.0);

            var stored = GameStateStore.Events.Where(e => e.eventType == GameStateEventType.ContractCompleted).ToList();
            Assert.Single(stored);
            Assert.Equal("rec-live", stored[0].recordingId);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.ContractComplete);
        }

        [Fact]
        public void HasClaimingLiveRecorder_IsFalseInsideTheScope()
        {
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            Assert.True(GameStateRecorder.HasClaimingLiveRecorder());
            GameStateRecorder.EnterClockCauseScope("test");
            try
            {
                Assert.False(GameStateRecorder.HasClaimingLiveRecorder());
            }
            finally
            {
                GameStateRecorder.ExitClockCauseScope("test");
            }
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            Assert.False(GameStateRecorder.HasClaimingLiveRecorder());
        }

        // catches: STRATEGY-CONVERSION-OF-DEPLOYED-SCIENCE-TAGGED-TO-THE-FLIGHT - the strategy's
        // cut of a ground-station award taking the live tag while the award is untagged, so a
        // discard refunds the conversion and keeps the science.
        [Fact]
        public void StrategyConversionTag_InsideTheScope_IsNone()
        {
            InFlightWithALiveRecorder();
            Assert.Equal("rec-live", GameStateRecorder.ResolveStrategyConversionRecordingTag("ScienceTransmission", 500.0));
            GameStateRecorder.EnterClockCauseScope("test ground station");
            try
            {
                Assert.Null(GameStateRecorder.ResolveStrategyConversionRecordingTag("ScienceTransmission", 500.0));
            }
            finally
            {
                GameStateRecorder.ExitClockCauseScope("test ground station");
            }
        }

        // catches: the subject capture comparing the live tag against the scope's untagged
        // ScienceChanged capture, so the award lost its reason key; and a deployed subject
        // parked for the committing recording.
        [Fact]
        public void DeployedSubject_InsideTheScope_MatchesItsUntaggedScienceChange_AndIsInTheLedger()
        {
            InFlightWithALiveRecorder();
            var recorder = new GameStateRecorder();
            GameStateRecorder.EnterClockCauseScope("test ground station");
            try
            {
                recorder.SetLatestScienceChangeCaptureForTesting(new GameStateRecorder.RecentScienceChangeCapture
                {
                    Ut = 500.0,
                    ReasonKey = "ScienceTransmission",
                    Delta = 6f,
                    RecordingId = "",
                    Valid = true
                });
                recorder.CaptureScienceSubject(6f, "deployedSeismicSensor@MunSrfLanded", 6f, 30f, 1f, 500.0,
                    vesselName: null, launchGuid: null);
            }
            finally
            {
                GameStateRecorder.ExitClockCauseScope("test ground station");
            }

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l => l.Contains("Science subject captured: deployedSeismicSensor@MunSrfLanded")
                && l.Contains("reason='ScienceTransmission'") && l.Contains("tag=''") && l.Contains("deployed=True"));
        }
    }
}
