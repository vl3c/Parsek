using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase C coverage for the stock-action intent consume site. Tests the
    /// pure decision predicate <see cref="StockActionIntentConsumeDecision.Evaluate"/>,
    /// the per-source setting lookup, the action→entry-reason mapping, and
    /// the routes produced for each refusal classification. The live-side
    /// branch routing (committed clone, BG-member, standalone tree mutation)
    /// requires a live <see cref="Vessel"/> and BackgroundRecorder; that
    /// integration coverage lives in Phase F's in-game tests.
    ///
    /// Tests map to plan §"Tests" entries in
    /// <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>.
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentConsumeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentConsumeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------- helpers ----------

        private static StockActionIntentMarker BuildMarker(
            StockActionType action,
            uint targetPid,
            Guid processSessionId,
            float capturedRealtime = 100f,
            double capturedUT = 1000.0)
        {
            return new StockActionIntentMarker
            {
                IntentId = Guid.NewGuid(),
                Action = action,
                TargetVesselPersistentId = targetPid,
                SourceScene = StockActionSourceScene.Flight,
                CapturedRealtime = capturedRealtime,
                CapturedUT = capturedUT,
                ProcessSessionId = processSessionId,
            };
        }

        // -----------------------------------------------------------------
        // Pure decision predicate — covers every Outcome branch.
        // -----------------------------------------------------------------

        // Fails if: a null marker is treated as anything other than the
        // common no-op. Covers plan test #11 (generic vessel switches
        // without a fresh stock-action intent marker do not immediate-start).
        [Fact]
        public void Evaluate_NullMarker_ReturnsNoIntent()
        {
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker: null,
                newVesselPersistentId: 42u,
                currentProcessSessionId: Guid.NewGuid(),
                currentRealtime: 0f,
                currentUT: 0.0,
                missedSwitchRecoveryInProgress: false,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.NoIntent, outcome);
            Assert.Equal(SwitchSegmentEntryRoute.NoIntent,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: a marker armed in a different AppDomain is consumed
        // anyway. Covers plan test #17b (cross-run-orphan clears on OnLoad
        // with stale-cross-run).
        [Fact]
        public void Evaluate_StaleCrossRun_ReturnsCrossRunOutcome()
        {
            Guid armProcess = Guid.NewGuid();
            Guid consumeProcess = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 99u, processSessionId: armProcess);
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, 99u, consumeProcess, 100f, 1000.0,
                missedSwitchRecoveryInProgress: false,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.StaleCrossRun, outcome);
            Assert.Equal("stale-cross-run",
                StockActionIntentConsumeDecision.ClearReasonFor(outcome));
            Assert.Equal(SwitchSegmentEntryRoute.Refused_StaleCrossRun,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: a TS Fly marker armed > 10s ago is still consumed.
        // Plan TTL contract: 10s for TS/KSC Fly.
        [Fact]
        public void Evaluate_StaleTtlExpired_TsFly_ReturnsTtlExpired()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 99u, processSessionId: procId,
                capturedRealtime: 100f, capturedUT: 1000.0);
            // Elapsed = 200 - 100 = 100s, TTL = 10s → expired.
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, 99u, procId, 200f, 1000.0,
                missedSwitchRecoveryInProgress: false,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.StaleIntentTtlExpired,
                outcome);
            Assert.Equal("stale-intent-ttl",
                StockActionIntentConsumeDecision.ClearReasonFor(outcome));
            Assert.Equal(SwitchSegmentEntryRoute.Refused_StaleIntent,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: a Map Switch-To marker survives past its 2s TTL window
        // (consume happens same-frame in stock — anything longer is stuck).
        [Fact]
        public void Evaluate_StaleTtlExpired_MapSwitchTo_ReturnsTtlExpired()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.MapSwitchTo,
                targetPid: 99u, processSessionId: procId,
                capturedRealtime: 100f, capturedUT: 1000.0);
            // Elapsed = 105 - 100 = 5s, MapSwitchTo TTL = 2s → expired.
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, 99u, procId, 105f, 1000.0, false, 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.StaleIntentTtlExpired,
                outcome);
            Assert.Equal(SwitchSegmentEntryRoute.Refused_StaleIntent,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: a quickload that regressed UT past the slack threshold
        // is mis-classified as fresh.
        [Fact]
        public void Evaluate_UtRegressed_ReturnsUtRegressed()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 99u, processSessionId: procId,
                capturedRealtime: 100f, capturedUT: 1000.0);
            // Within TTL but UT regressed by more than UtRegressionToleranceSeconds.
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, 99u, procId, 101f, 950.0,
                missedSwitchRecoveryInProgress: false,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.StaleIntentUtRegressed,
                outcome);
            Assert.Equal("stale-intent-ut-regressed",
                StockActionIntentConsumeDecision.ClearReasonFor(outcome));
            Assert.Equal(SwitchSegmentEntryRoute.Refused_StaleIntent,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: a marker targeting a different vessel than the one that
        // became active is consumed. Plan §"Double-fire diagnostic" — marker
        // PID mismatch must clear with stale-target-mismatch.
        [Fact]
        public void Evaluate_TargetMismatch_ReturnsTargetMismatch()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 100u, processSessionId: procId);
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, newVesselPersistentId: 200u,
                currentProcessSessionId: procId,
                currentRealtime: 101f, currentUT: 1001.0,
                missedSwitchRecoveryInProgress: false,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.TargetMismatch, outcome);
            Assert.Equal("stale-target-mismatch",
                StockActionIntentConsumeDecision.ClearReasonFor(outcome));
            Assert.Equal(SwitchSegmentEntryRoute.Refused_TargetMismatch,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: a rapid double-click on the same Switch-To button starts
        // a second session instead of no-opping. Plan §"Two rapid switches
        // semantics → Same target vessel".
        [Fact]
        public void Evaluate_DuplicateSameTarget_ReturnsDuplicateSameTarget()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.MapSwitchTo,
                targetPid: 77u, processSessionId: procId);
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, newVesselPersistentId: 77u,
                currentProcessSessionId: procId,
                currentRealtime: 100.1f, currentUT: 1000.1,
                missedSwitchRecoveryInProgress: false,
                activeSessionFocusedPid: 77u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.DuplicateSameTarget, outcome);
            Assert.Equal("duplicate-intent-same-target",
                StockActionIntentConsumeDecision.ClearReasonFor(outcome));
            Assert.Equal(SwitchSegmentEntryRoute.Refused_DuplicateSameTarget,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: rapid Switch-To from vessel A to vessel B to vessel C is
        // mis-classified as duplicate-same-target. Plan §"Two rapid switches
        // semantics → Different target vessel".
        [Fact]
        public void Evaluate_DifferentTargetWithActiveSession_AuthorizesNewConsume()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.MapSwitchTo,
                targetPid: 200u, processSessionId: procId);
            // Active session is focused on vessel A (pid 100); new intent
            // targets vessel B (pid 200); new active vessel matches new intent.
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, newVesselPersistentId: 200u,
                currentProcessSessionId: procId,
                currentRealtime: 100.1f, currentUT: 1000.1,
                missedSwitchRecoveryInProgress: false,
                activeSessionFocusedPid: 100u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.Authorized, outcome);
        }

        // Fails if: staleness checks run AFTER MissedSwitchRecovery, so a
        // stale-cross-run marker arriving inside the recovery path is
        // misclassified as MissedSwitchRecovery instead of StaleCrossRun.
        // MED 8 (PR #876 review): staleness must dominate the recovery
        // short-circuit so the diagnostic information is preserved.
        [Fact]
        public void Evaluate_StaleCrossRun_DuringMissedSwitchRecovery_PrefersStaleCrossRun()
        {
            Guid armProcess = Guid.NewGuid();
            Guid consumeProcess = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 99u, processSessionId: armProcess);
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, 99u, consumeProcess, 100f, 1000.0,
                missedSwitchRecoveryInProgress: true,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.StaleCrossRun,
                outcome);
            Assert.Equal("stale-cross-run",
                StockActionIntentConsumeDecision.ClearReasonFor(outcome));
            Assert.Equal(SwitchSegmentEntryRoute.Refused_StaleCrossRun,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: a TTL-expired marker arriving inside the recovery path
        // is misclassified as MissedSwitchRecovery. Same MED 8 reorder.
        [Fact]
        public void Evaluate_StaleTtl_DuringMissedSwitchRecovery_PrefersTtlExpired()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 99u, processSessionId: procId,
                capturedRealtime: 100f, capturedUT: 1000.0);
            // Elapsed = 200 - 100 = 100s, TTL = 10s → expired.
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, 99u, procId, 200f, 1000.0,
                missedSwitchRecoveryInProgress: true,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.StaleIntentTtlExpired,
                outcome);
        }

        // Fails if: the missed-switch recovery replay path consumes a still-
        // armed serialized intent. Plan §"Missed-switch recovery" requires
        // clear with stale-cross-run instead.
        [Fact]
        public void Evaluate_MissedSwitchRecovery_ReturnsRecoveryRefusal()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 11u, processSessionId: procId);
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, newVesselPersistentId: 11u,
                currentProcessSessionId: procId,
                currentRealtime: 100.1f, currentUT: 1000.1,
                missedSwitchRecoveryInProgress: true,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.MissedSwitchRecovery,
                outcome);
            Assert.Equal("stale-cross-run",
                StockActionIntentConsumeDecision.ClearReasonFor(outcome));
            Assert.Equal(SwitchSegmentEntryRoute.Refused_MissedSwitchRecovery,
                StockActionIntentConsumeDecision.RouteForRefusal(outcome));
        }

        // Fails if: a fresh marker matching the target vessel is refused.
        // Authorized must mean "caller proceeds to branch routing".
        [Fact]
        public void Evaluate_FreshAuthorized_ReturnsAuthorized()
        {
            Guid procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 99u, processSessionId: procId,
                capturedRealtime: 100f, capturedUT: 1000.0);
            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker, newVesselPersistentId: 99u,
                currentProcessSessionId: procId,
                currentRealtime: 101f, currentUT: 1001.0,
                missedSwitchRecoveryInProgress: false,
                activeSessionFocusedPid: 0u);
            Assert.Equal(StockActionIntentConsumeDecision.Outcome.Authorized, outcome);
            Assert.Null(StockActionIntentConsumeDecision.ClearReasonFor(outcome));
        }

        // -----------------------------------------------------------------
        // Static helpers
        // -----------------------------------------------------------------

        // Fails if: the action-to-entry-reason mapping ever drifts out of
        // 1:1 alignment. Plan keeps the two enums separate by design;
        // production has exactly one mapping function.
        [Fact]
        public void MapIntentActionToEntryReason_MapsOneToOne()
        {
            Assert.Equal(SwitchSegmentEntryReason.TrackingStationFly,
                ParsekFlight.MapIntentActionToEntryReason(StockActionType.TrackingStationFly));
            Assert.Equal(SwitchSegmentEntryReason.KscMarkerFly,
                ParsekFlight.MapIntentActionToEntryReason(StockActionType.KscMarkerFly));
            Assert.Equal(SwitchSegmentEntryReason.MapSwitchTo,
                ParsekFlight.MapIntentActionToEntryReason(StockActionType.MapSwitchTo));
        }

        // Fails if: BuildSwitchSegmentBoundaryPoint throws on a null vessel.
        // The consume site must degrade gracefully, not crash, when the
        // FlightGlobals state is partial during scene load.
        [Fact]
        public void BuildSwitchSegmentBoundaryPoint_NullVessel_ReturnsStubPoint()
        {
            TrajectoryPoint pt = ParsekFlight.BuildSwitchSegmentBoundaryPoint(null, 1234.5);
            Assert.Equal(1234.5, pt.ut);
            Assert.Equal("Unknown", pt.bodyName);
            Assert.True(double.IsNaN(pt.recordedGroundClearance));
        }

        // -----------------------------------------------------------------
        // Refusal log format — pure renderer
        // -----------------------------------------------------------------

        // Fails if: the refusal log line drops the intent id, action, or
        // PID fields needed to diagnose stuck markers in KSP.log.
        [Fact]
        public void FormatRefusalDiagnostic_IncludesAllFields()
        {
            var marker = BuildMarker(StockActionType.KscMarkerFly,
                targetPid: 555u, processSessionId: Guid.NewGuid());
            string line = StockActionIntentConsumeDecision.FormatRefusalDiagnostic(
                StockActionIntentConsumeDecision.Outcome.TargetMismatch,
                marker, newVesselPersistentId: 777u);
            Assert.Contains("reason=stale-target-mismatch", line);
            Assert.Contains("intentId=" + marker.IntentId.ToString("D"), line);
            Assert.Contains("action=KscMarkerFly", line);
            Assert.Contains("markerTargetPid=555", line);
            Assert.Contains("newVesselPid=777", line);
        }

        // Fails if: the no-marker formatting variant trips on the null
        // marker reference instead of producing a clean diagnostic.
        [Fact]
        public void FormatRefusalDiagnostic_NullMarker_DoesNotThrow()
        {
            string line = StockActionIntentConsumeDecision.FormatRefusalDiagnostic(
                StockActionIntentConsumeDecision.Outcome.NoIntent,
                marker: null, newVesselPersistentId: 1u);
            Assert.Contains("refused-no-marker", line);
            Assert.Contains("newVesselPid=1", line);
        }

        // -----------------------------------------------------------------
        // Scenario-side clear path on refusal — ParsekScenario integration
        // -----------------------------------------------------------------

        // Fails if: a successful clear-on-refusal does not actually clear
        // the marker from ParsekScenario state (e.g. a future caller calls
        // ClearReasonFor without also calling ClearStockActionIntent).
        [Fact]
        public void ClearStockActionIntent_ClearsMarker_AfterStaleClassification()
        {
            ParsekProcess.ResetForTesting();
            var scenario = new ParsekScenario();
            var marker = BuildMarker(StockActionType.TrackingStationFly,
                targetPid: 1u, processSessionId: Guid.NewGuid());
            scenario.ArmStockActionIntent(marker);
            Assert.NotNull(scenario.CurrentStockActionIntent);

            // Simulate the consume site discovering staleness.
            string reason = StockActionIntentConsumeDecision.ClearReasonFor(
                StockActionIntentConsumeDecision.Outcome.StaleCrossRun);
            scenario.ClearStockActionIntent(reason);

            Assert.Null(scenario.CurrentStockActionIntent);
            Assert.Contains(logLines,
                l => l.Contains("[SwitchIntent]") && l.Contains("stale-cross-run"));
        }

        // Fails if: a successful Authorized classification leaves the
        // marker armed after the consume site clears with "consumed-into-segment".
        // Covers plan test #10: "A successful immediate switch/Fly segment
        // start consumes the intent marker and leaves no first-modification
        // watcher armed for that same switch."
        [Fact]
        public void Consume_AuthorizedThenClear_LeavesNoArmedMarker()
        {
            var scenario = new ParsekScenario();
            var marker = BuildMarker(StockActionType.MapSwitchTo,
                targetPid: 42u, processSessionId: ParsekProcess.ProcessSessionId);
            scenario.ArmStockActionIntent(marker);

            // Authorized branch: caller clears with consumed-into-segment.
            scenario.ClearStockActionIntent("consumed-into-segment");

            Assert.Null(scenario.CurrentStockActionIntent);
            Assert.Contains(logLines,
                l => l.Contains("[SwitchIntent]")
                    && l.Contains("cleared")
                    && l.Contains("consumed-into-segment")
                    && l.Contains(marker.IntentId.ToString("D")));
        }

        // Phase C.1: source-text gate test. The three Started* branches in
        // ParsekFlight.TryConsumeStockActionIntent (committed-spawned-clone,
        // bg-member-continuation, standalone / bg-lookup-failed-standalone)
        // each create a new continuation segment via
        // SwitchSegmentBuilder.CreateSwitchContinuationSegment and then MUST
        // invoke BindLiveRecorderToSwitchSegment so the live FlightRecorder
        // is bound to the new recording id and trajectory samples flow into
        // the new segment immediately. We can't drive the live ParsekFlight
        // path from xUnit (Vessel + FlightGlobals + Unity GameEvents are
        // unguarded — see reference_parsek_scenario_xunit.md), so this is a
        // source-text gate: each Created==true site must call
        // BindLiveRecorderToSwitchSegment within a few lines, and the bind
        // helper must emit the recorder-bound Info log line that the consume
        // ledger relies on.
        //
        // Fails if: a future refactor of TryConsumeStockActionIntent /
        // Start* drops the recorder-bind call after segment creation,
        // silently leaving the new segment empty of trajectory samples.
        [Fact]
        public void Consume_StartedRoute_BindsRecorderToNewRecordingId()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string flightPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekFlight.cs");
            Assert.True(File.Exists(flightPath),
                $"ParsekFlight.cs not found at {flightPath}");
            string source = File.ReadAllText(flightPath);

            // Gate 1: the helper itself must exist.
            Assert.Contains("BindLiveRecorderToSwitchSegment", source);
            Assert.Contains("recorder-bound route=", source);
            Assert.Contains("new-recording-id=", source);

            // Gate 2: each Started* branch must call BindLiveRecorderToSwitchSegment
            // within ~15 lines of the matching successful CreateSwitchContinuationSegment
            // result, identified by the route tag passed to the helper. Use
            // multiline regex so .* spans the few intervening lines.
            //
            // committed-spawned-clone branch:
            Assert.Matches(new Regex(
                @"creation\.Created[\s\S]{0,2000}?BindLiveRecorderToSwitchSegment[\s\S]{0,300}?committed-spawned-clone",
                RegexOptions.Multiline),
                source);
            // bg-member-continuation branch:
            Assert.Matches(new Regex(
                @"BackgroundMap\.Remove\(newPid\)[\s\S]{0,400}?BindLiveRecorderToSwitchSegment[\s\S]{0,300}?bg-member-continuation",
                RegexOptions.Multiline),
                source);
            // standalone / bg-lookup-failed-standalone branch:
            Assert.Matches(new Regex(
                @"StartedStandalone[\s\S]{0,400}?BindLiveRecorderToSwitchSegment[\s\S]{0,300}?bg-lookup-failed-standalone",
                RegexOptions.Multiline),
                source);

            // Gate 3: the helper must mirror the canonical promote-from-background
            // bind pattern (new FlightRecorder, ActiveTree assigned,
            // StartRecording(isPromotion: true), PrepareSessionStateForRecorderStart).
            Assert.Matches(new Regex(
                @"recorder = new FlightRecorder\(\);[\s\S]{0,200}?recorder\.ActiveTree = activeTree;[\s\S]{0,400}?recorder\.StartRecording\(isPromotion: true\);[\s\S]{0,400}?PrepareSessionStateForRecorderStart\(",
                RegexOptions.Multiline),
                source);
        }
    }
}
