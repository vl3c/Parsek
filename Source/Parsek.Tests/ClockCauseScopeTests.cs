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
            ResourceScannerScopePatch.ResetForTesting();
        }

        public void Dispose()
        {
            ResourceScannerScopePatch.ResetForTesting();
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

        private static ProtoVessel Source(uint pid, string name)
        {
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.persistentId = pid;
            pv.vesselName = name;
            return pv;
        }

        private static bool InvokeSubmitPrefix(ScienceSubject subject, ProtoVessel source = null)
        {
            var prefix = typeof(DeployedScienceSubmitScopePatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { subject, 1f, source, false };
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
    
        // ================================================================
        // Mod background sources (LEDGER-EVENTS-OF-MOD-BACKGROUND-SOURCES-TAGGED-TO-THE-FLIGHT,
        // owner ruling 2026-10-25: unit tests only)
        // ================================================================

        // catches: a vessel event filed untagged while the flight itself caused it, inside a
        // recovery (whose scope names the owner), with no recorder to claim it at all, or on a
        // LOADED vessel outside the tree (row 3: the flight's EVA kerbal runs and transmits
        // that vessel's experiment, still the flight's doing).
        [Theory]
        [InlineData(true, false, false, false, true)]
        [InlineData(true, false, false, true, false)]
        [InlineData(true, false, true, false, false)]
        [InlineData(true, true, false, false, false)]
        [InlineData(false, false, false, false, false)]
        public void IsVesselEventOutsideTheFlight_TruthTable(
            bool hasLiveRecorder, bool recoveryScopeOpen, bool vesselLoaded, bool vesselInFlight, bool expected)
        {
            Assert.Equal(expected, GameStateRecorder.IsVesselEventOutsideTheFlight(
                hasLiveRecorder, recoveryScopeOpen, vesselLoaded, vesselInFlight));
        }

        // catches: Kerbalism-style background science from a vessel the flight does not fly
        // (and the body milestone it reaches) tagged to the live recording.
        [Fact]
        public void SubmitPrefix_UnloadedSourceVesselOutsideTheFlight_OpensTheScope()
        {
            InFlightWithALiveRecorder();
            GameStateRecorder.LiveFlightVesselProviderForTesting = pid => pid == 1001u;
            bool scoped = InvokeSubmitPrefix(Subject("temperatureScan@MunInSpaceHigh"), Source(2002u, "Relay Probe"));
            Assert.True(scoped);
            Assert.True(GameStateRecorder.InClockCauseScope);
            Assert.Null(GameStateRecorder.GroundStationSubmissionReasonKey);
            InvokeSubmitFinalizer(scoped);
            Assert.False(GameStateRecorder.InClockCauseScope);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("Clock-caused transition begins: science temperatureScan@MunInSpaceHigh from unloaded vessel 'Relay Probe' pid=2002, which the live flight does not record"));
        }

        // catches: the flight's own transmission (active or a background member of its tree)
        // losing its tag, and a transmission with no live recorder opening a scope.
        [Fact]
        public void SubmitPrefix_SourceVesselInTheFlight_OrNoRecorder_LeavesTheScopeClosed()
        {
            InFlightWithALiveRecorder();
            GameStateRecorder.LiveFlightVesselProviderForTesting = pid => pid == 1001u;
            Assert.False(InvokeSubmitPrefix(Subject("temperatureScan@MunInSpaceHigh"), Source(1001u, "Mun Lander")));
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            Assert.False(InvokeSubmitPrefix(Subject("temperatureScan@MunInSpaceHigh"), Source(2002u, "Relay Probe")));
            Assert.False(GameStateRecorder.InClockCauseScope);
        }

        // catches: a background vessel's award parked for the live flight's commit, so a
        // discard drops science stock already paid.
        [Fact]
        public void VesselSubject_InsideTheScope_IsUntaggedAndInTheLedgerAtOnce()
        {
            InFlightWithALiveRecorder();
            var recorder = new GameStateRecorder();
            GameStateRecorder.EnterClockCauseScope("test background vessel");
            try
            {
                recorder.SetLatestScienceChangeCaptureForTesting(new GameStateRecorder.RecentScienceChangeCapture
                {
                    Ut = 600.0,
                    ReasonKey = "ScienceTransmission",
                    Delta = 4f,
                    RecordingId = "",
                    Valid = true
                });
                recorder.CaptureScienceSubject(4f, "temperatureScan@MunInSpaceHigh", 4f, 20f, 1f, 600.0,
                    vesselName: "Relay Probe", launchGuid: null);
            }
            finally
            {
                GameStateRecorder.ExitClockCauseScope("test background vessel");
            }

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l => l.Contains("Science subject captured: temperatureScan@MunInSpaceHigh")
                && l.Contains("tag=''") && l.Contains("directLedger=True"));
            var row = Ledger.Actions.Single(a => a.Type == GameActionType.ScienceEarning
                && a.SubjectId == "temperatureScan@MunInSpaceHigh");
            Assert.True(string.IsNullOrEmpty(row.RecordingId));
        }

        // catches: a Contract Configurator auto-accept contract accepted by a mid-flight
        // refresh filed under the live flight, and a player's accept losing its tag.
        [Theory]
        [InlineData(true, Contracts.Contract.State.Active, Contracts.Contract.State.Offered, true)]
        [InlineData(false, Contracts.Contract.State.Active, Contracts.Contract.State.Offered, false)]
        [InlineData(true, Contracts.Contract.State.Completed, Contracts.Contract.State.Active, false)]
        [InlineData(true, Contracts.Contract.State.Active, Contracts.Contract.State.Active, false)]
        public void IsClockCausedAccept_OnlyAnAutoAcceptContractsAccept(
            bool autoAccept, Contracts.Contract.State newState, Contracts.Contract.State current, bool expected)
        {
            Assert.Equal(expected, ContractEarlyFailPatch.IsClockCausedAccept(newState, current, autoAccept));
        }

        // catches: a stock scanner signature drift leaving every stock scan to read as a mod's.
        [Fact]
        public void ScannerTargets_ResolveAgainstStock()
        {
            var targets = ResourceScannerScopePatch.ResolveTargetMethodsForTesting();
            Assert.Equal(2, targets.Count);
            Assert.Contains(targets, m => m.Name == "CompleteSurvey" && m.DeclaringType == typeof(ModuleOrbitalSurveyor));
            Assert.Contains(targets, m => m.Name == "RunAnalysis" && m.DeclaringType == typeof(ModuleBiomeScanner));
        }

        [Theory]
        [InlineData(true, false, false, true)]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, false, false)]
        [InlineData(false, false, false, false)]
        public void IsUnlockByNoFlight_TruthTable(bool hasLiveRecorder, bool recoveryScopeOpen, bool stockScannerOpen, bool expected)
        {
            Assert.Equal(expected, ResourceScannerScopePatch.IsUnlockByNoFlight(hasLiveRecorder, recoveryScopeOpen, stockScannerOpen));
        }

        // catches: SCANsat's background coverage unlocking a body while a flight records,
        // filed under that flight (a discard drops the unlock, a Re-Fly reads it as the
        // attempt's).
        [Fact]
        public void ScanUnlock_WithNoStockScanner_IsUntaggedAndInTheLedgerAtOnce()
        {
            InFlightWithALiveRecorder();
            ResourceScannerScopePatch.CaptureUnlock(700.0, "Minmus", null);

            var stored = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.ResourceScanCompleted);
            Assert.Equal("", stored.recordingId ?? "");
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.ResourceScanUnlocked && string.IsNullOrEmpty(a.RecordingId));
            Assert.False(GameStateRecorder.InClockCauseScope);
            Assert.Contains(logLines, l => l.Contains("Clock-caused transition begins: resource scan of Minmus with no stock scanner (a mod's scan)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Emit drift"));
        }

        // catches: the flight's own surveyor losing its tag, and (review of this fix) the
        // flight's EVA kerbal running "Perform Analysis" on a loaded lander outside the tree
        // (an EVA auto-record tree holds only the kerbal) filed untagged: a stock scan stays
        // the flight's whichever vessel carries the scanner.
        [Fact]
        public void ScanUnlock_InsideAStockScanner_KeepsTheFlightTag()
        {
            InFlightWithALiveRecorder();
            GameStateRecorder.LiveFlightVesselProviderForTesting = pid => pid == 1001u;
            ResourceScannerScopePatch.EnterScannerCall();
            try
            {
                Assert.False(GameStateRecorder.InClockCauseScope);
                ResourceScannerScopePatch.CaptureUnlock(700.0, "Mun", "Midlands");
            }
            finally
            {
                ResourceScannerScopePatch.ExitScannerCall();
            }

            var stored = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.ResourceScanCompleted);
            Assert.Equal("rec-live", stored.recordingId);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.ResourceScanUnlocked);
            Assert.Equal(0, ResourceScannerScopePatch.OpenScannerCalls);
            Assert.DoesNotContain(logLines, l => l.Contains("Clock-caused transition begins: resource scan"));
        }

        [Fact]
        public void ScannerPrefix_MarksTheCall_FinalizerClearsIt()
        {
            var prefix = typeof(ResourceScannerScopePatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            var finalizer = typeof(ResourceScannerScopePatch).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { false };
            prefix.Invoke(null, args);
            Assert.True((bool)args[0]);
            Assert.Equal(1, ResourceScannerScopePatch.OpenScannerCalls);
            finalizer.Invoke(null, new object[] { null, true });
            Assert.Equal(0, ResourceScannerScopePatch.OpenScannerCalls);
        }

        // catches: the KSC / no-recorder behaviour moving (an ownerless scan is already
        // untagged and forwarded there).
        [Fact]
        public void ScanUnlock_WithNoLiveRecorder_OpensNoScope()
        {
            ResourceScannerScopePatch.CaptureUnlock(700.0, "Duna", null);
            Assert.DoesNotContain(logLines, l => l.Contains("Clock-caused transition begins: resource scan"));
            Assert.Single(GameStateStore.Events.Where(e => e.eventType == GameStateEventType.ResourceScanCompleted));
        }
    }
}
