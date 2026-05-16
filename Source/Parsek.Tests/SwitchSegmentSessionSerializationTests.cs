using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase A.3 round-trip + staleness coverage for the new switch/Fly
    /// auto-record scenario state: <see cref="StockActionIntentMarker"/>,
    /// <see cref="SwitchSegmentSession"/>, and the
    /// <see cref="ParsekScenario"/> arm/clear log shape. Consume sites
    /// land later (Phase C); this guard is for data-shape only.
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentSessionSerializationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentSessionSerializationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            // Each test starts on a fresh "process" so cross-run-orphan
            // tests can drive the predicate deterministically.
            ParsekProcess.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekProcess.ResetForTesting();
        }

        // -----------------------------------------------------------------
        // Round-trip: every field preserved through WriteTo / TryLoadFrom.
        // -----------------------------------------------------------------

        // Fails if: a new StockActionIntentMarker field is added but the
        // codec is not updated in SaveInto / TryLoadFrom.
        [Fact]
        public void StockActionIntentMarker_AllFields_RoundTrip()
        {
            var marker = new StockActionIntentMarker
            {
                IntentId = Guid.NewGuid(),
                Action = StockActionType.KscMarkerFly,
                TargetVesselPersistentId = 3087746488u,
                SourceScene = StockActionSourceScene.SpaceCenter,
                CapturedRealtime = 12345.5f,
                CapturedUT = 1742810.25,
                ProcessSessionId = Guid.NewGuid(),
            };

            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);
            var node = parent.GetNode(StockActionIntentMarker.NodeName);
            Assert.NotNull(node);

            StockActionIntentMarker restored;
            Assert.True(StockActionIntentMarker.TryLoadFrom(node, out restored));

            Assert.Equal(marker.IntentId, restored.IntentId);
            Assert.Equal(marker.Action, restored.Action);
            Assert.Equal(marker.TargetVesselPersistentId, restored.TargetVesselPersistentId);
            Assert.Equal(marker.SourceScene, restored.SourceScene);
            Assert.Equal(marker.CapturedRealtime, restored.CapturedRealtime);
            Assert.Equal(marker.CapturedUT, restored.CapturedUT);
            Assert.Equal(marker.ProcessSessionId, restored.ProcessSessionId);
        }

        // Fails if: a new SwitchSegmentSession field is added but the
        // codec is not updated in SaveInto / TryLoadFrom.
        [Fact]
        public void SwitchSegmentSession_AllFields_RoundTrip()
        {
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                TreeId = "tree_abc",
                ParentRecordingId = "rec_parent",
                ActiveSegmentRecordingId = "rec_seg",
                SourceVesselPersistentId = 1111u,
                FocusedVesselPersistentId = 2222u,
                SwitchUT = 9999.75,
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                IntentId = Guid.NewGuid(),
                PreSessionBranchPointIds = new List<string> { "bp_pre_1", "bp_pre_2" },
                CommittedTreeId = "tree_committed",
            };

            var parent = new ConfigNode("PARSEK");
            session.SaveInto(parent);
            var node = parent.GetNode(SwitchSegmentSession.NodeName);
            Assert.NotNull(node);

            SwitchSegmentSession restored;
            Assert.True(SwitchSegmentSession.TryLoadFrom(node, out restored));

            Assert.Equal(session.SessionId, restored.SessionId);
            Assert.Equal(session.TreeId, restored.TreeId);
            Assert.Equal(session.ParentRecordingId, restored.ParentRecordingId);
            Assert.Equal(session.ActiveSegmentRecordingId, restored.ActiveSegmentRecordingId);
            Assert.Equal(session.SourceVesselPersistentId, restored.SourceVesselPersistentId);
            Assert.Equal(session.FocusedVesselPersistentId, restored.FocusedVesselPersistentId);
            Assert.Equal(session.SwitchUT, restored.SwitchUT);
            Assert.Equal(session.EntryReason, restored.EntryReason);
            Assert.Equal(session.IntentId, restored.IntentId);
            Assert.Equal(session.CommittedTreeId, restored.CommittedTreeId);
            Assert.Equal(session.PreSessionBranchPointIds, restored.PreSessionBranchPointIds);
        }

        // Fails if: PreSessionBranchPointIds is null-defaulted on
        // construction. Callers rely on the list always being non-null so
        // segment-creation code can append without a null check.
        [Fact]
        public void SwitchSegmentSession_PreSessionBranchPointIds_DefaultsToEmptyList()
        {
            var session = new SwitchSegmentSession();
            Assert.NotNull(session.PreSessionBranchPointIds);
            Assert.Empty(session.PreSessionBranchPointIds);
        }

        // -----------------------------------------------------------------
        // Staleness predicate: every branch covered without driving the
        // live Unity Time / Planetarium clock.
        // -----------------------------------------------------------------

        private static StockActionIntentMarker BuildMarker(
            StockActionType action,
            Guid processSessionId,
            float capturedRealtime = 100.0f,
            double capturedUT = 1000.0)
        {
            return new StockActionIntentMarker
            {
                IntentId = Guid.NewGuid(),
                Action = action,
                TargetVesselPersistentId = 42u,
                SourceScene = StockActionSourceScene.TrackingStation,
                CapturedRealtime = capturedRealtime,
                CapturedUT = capturedUT,
                ProcessSessionId = processSessionId,
            };
        }

        // Fails if: cross-run-orphan detection is removed or routed around
        // ProcessSessionId. This is the deterministic gate the wall-clock
        // TTL cannot provide.
        [Fact]
        public void EvaluateStaleness_DifferentProcessSessionId_ReturnsStaleCrossRun()
        {
            var marker = BuildMarker(StockActionType.TrackingStationFly, Guid.NewGuid());
            var classification = StockActionIntentMarker.EvaluateStaleness(
                marker, currentProcessSessionId: Guid.NewGuid(),
                currentRealtime: 105.0f, currentUT: 1001.0);
            Assert.Equal(StockActionIntentStaleness.StaleCrossRun, classification);
        }

        // Fails if: TS Fly TTL drifts from the 10 s the plan documents
        // for slow installs. >10 s after arm with same process => stale.
        [Fact]
        public void EvaluateStaleness_TrackingStationFly_PastTtl_ReturnsStaleIntentTtlExpired()
        {
            var procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly, procId,
                capturedRealtime: 100.0f);
            // 11 s elapsed > 10 s TTL.
            var classification = StockActionIntentMarker.EvaluateStaleness(
                marker, procId, currentRealtime: 111.0f, currentUT: 1001.0);
            Assert.Equal(StockActionIntentStaleness.StaleIntentTtlExpired, classification);
        }

        // Fails if: Map Switch-To TTL inherits the longer TS Fly TTL.
        // 3 s elapsed > 2 s Map TTL.
        [Fact]
        public void EvaluateStaleness_MapSwitchTo_PastTtl_ReturnsStaleIntentTtlExpired()
        {
            var procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.MapSwitchTo, procId,
                capturedRealtime: 100.0f);
            // 3 s elapsed > 2 s TTL.
            var classification = StockActionIntentMarker.EvaluateStaleness(
                marker, procId, currentRealtime: 103.0f, currentUT: 1001.0);
            Assert.Equal(StockActionIntentStaleness.StaleIntentTtlExpired, classification);
        }

        // Fails if: UT-regressed (quickload between arm and consume) is
        // silently treated as fresh. The plan requires this to be cleared.
        [Fact]
        public void EvaluateStaleness_UtRegression_ReturnsStaleIntentUtRegressed()
        {
            var procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly, procId,
                capturedRealtime: 100.0f, capturedUT: 5000.0);
            var classification = StockActionIntentMarker.EvaluateStaleness(
                marker, procId, currentRealtime: 105.0f, currentUT: 4500.0);
            Assert.Equal(StockActionIntentStaleness.StaleIntentUtRegressed, classification);
        }

        // Fails if: same-process F5 with a recent marker would be cleared
        // by the staleness predicate. This is the canonical
        // "marker survives F5/F9" case that must stay armed.
        [Fact]
        public void EvaluateStaleness_SameProcessRecentMarker_ReturnsFresh()
        {
            var procId = Guid.NewGuid();
            var marker = BuildMarker(StockActionType.TrackingStationFly, procId,
                capturedRealtime: 100.0f, capturedUT: 1000.0);
            // 2 s elapsed, well under 10 s TTL; UT advanced normally.
            var classification = StockActionIntentMarker.EvaluateStaleness(
                marker, procId, currentRealtime: 102.0f, currentUT: 1002.0);
            Assert.Equal(StockActionIntentStaleness.Fresh, classification);
        }

        // Fails if: GetTtlSeconds drifts from the plan's documented
        // constants. Pinned with the named constants from the type so a
        // future bump must update both sides.
        [Fact]
        public void GetTtlSeconds_MatchesPlanConstants()
        {
            Assert.Equal(
                StockActionIntentMarker.TrackingStationOrKscFlyTtlSeconds,
                StockActionIntentMarker.GetTtlSeconds(StockActionType.TrackingStationFly));
            Assert.Equal(
                StockActionIntentMarker.TrackingStationOrKscFlyTtlSeconds,
                StockActionIntentMarker.GetTtlSeconds(StockActionType.KscMarkerFly));
            Assert.Equal(
                StockActionIntentMarker.MapSwitchToTtlSeconds,
                StockActionIntentMarker.GetTtlSeconds(StockActionType.MapSwitchTo));
            Assert.Equal(10.0, StockActionIntentMarker.TrackingStationOrKscFlyTtlSeconds);
            Assert.Equal(2.0, StockActionIntentMarker.MapSwitchToTtlSeconds);
        }

        // -----------------------------------------------------------------
        // Arm / Clear log shape — directly drive ParsekScenario instance
        // methods. These touch only the private field + ParsekLog, no
        // Planetarium / Unity event hookup needed.
        // -----------------------------------------------------------------

        // Fails if: ArmStockActionIntent stops emitting a [SwitchIntent]
        // line with the IntentId. Consume-site debugging in KSP.log
        // depends on this single grep-able line.
        [Fact]
        public void ArmStockActionIntent_EmitsInfoLog_WithSubsystemAndIntentId()
        {
            var scenario = new ParsekScenario();
            var marker = BuildMarker(StockActionType.TrackingStationFly, ParsekProcess.ProcessSessionId);

            scenario.ArmStockActionIntent(marker);

            Assert.Same(marker, scenario.CurrentStockActionIntent);
            Assert.Contains(logLines,
                l => l.Contains("[SwitchIntent]")
                    && l.Contains("armed")
                    && l.Contains(marker.IntentId.ToString("D")));
        }

        // Fails if: re-arming overwrites silently. Plan's Prefix-on-Prefix
        // race requires a logged stale-intent-superseded line.
        [Fact]
        public void ArmStockActionIntent_OverwriteArmed_LogsSuperseded()
        {
            var scenario = new ParsekScenario();
            var first = BuildMarker(StockActionType.TrackingStationFly, ParsekProcess.ProcessSessionId);
            var second = BuildMarker(StockActionType.MapSwitchTo, ParsekProcess.ProcessSessionId);

            scenario.ArmStockActionIntent(first);
            logLines.Clear();
            scenario.ArmStockActionIntent(second);

            Assert.Same(second, scenario.CurrentStockActionIntent);
            Assert.Contains(logLines,
                l => l.Contains("[SwitchIntent]")
                    && l.Contains("stale-intent-superseded")
                    && l.Contains(first.IntentId.ToString("D")));
        }

        // Fails if: ClearStockActionIntent omits the reason or
        // accidentally crashes on the idempotent no-marker path.
        [Fact]
        public void ClearStockActionIntent_ArmedThenClear_EmitsClearedLog()
        {
            var scenario = new ParsekScenario();
            var marker = BuildMarker(StockActionType.TrackingStationFly, ParsekProcess.ProcessSessionId);
            scenario.ArmStockActionIntent(marker);
            logLines.Clear();

            scenario.ClearStockActionIntent("stale-cross-run");

            Assert.Null(scenario.CurrentStockActionIntent);
            Assert.Contains(logLines,
                l => l.Contains("[SwitchIntent]")
                    && l.Contains("cleared")
                    && l.Contains("stale-cross-run")
                    && l.Contains(marker.IntentId.ToString("D")));
        }

        // Fails if: Clear with no marker armed throws or logs at Info.
        // Idempotent contract = Verbose-only on no-op.
        [Fact]
        public void ClearStockActionIntent_NoMarker_LogsVerboseNoOp()
        {
            var scenario = new ParsekScenario();

            scenario.ClearStockActionIntent("end-of-scene");

            Assert.Null(scenario.CurrentStockActionIntent);
            // No "cleared" Info line emitted on no-op (only the verbose
            // no-op line). Asserting absence of a "cleared:" Info line
            // catches accidental Info emission on the idle path.
            Assert.DoesNotContain(logLines,
                l => l.Contains("[SwitchIntent]") && l.Contains("cleared:"));
        }

        // Fails if: ArmSwitchSegmentSession stops emitting a session line
        // with the SessionId + IntentId pair. Cross-linking the session
        // back to its triggering UI click is a load-bearing log invariant.
        [Fact]
        public void ArmSwitchSegmentSession_EmitsInfoLog_WithSessionAndIntentId()
        {
            var scenario = new ParsekScenario();
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.KscMarkerFly,
                TreeId = "tree",
                ActiveSegmentRecordingId = "rec_seg",
                SwitchUT = 1000.0,
            };

            scenario.ArmSwitchSegmentSession(session);

            Assert.Same(session, scenario.ActiveSwitchSegmentSession);
            Assert.Contains(logLines,
                l => l.Contains("[SwitchSegment]")
                    && l.Contains("armed")
                    && l.Contains(session.SessionId.ToString("D"))
                    && l.Contains(session.IntentId.ToString("D")));
        }

        // Fails if: ClearSwitchSegmentSession omits the reason or doesn't
        // null out the field.
        [Fact]
        public void ClearSwitchSegmentSession_ArmedThenClear_EmitsClearedLog()
        {
            var scenario = new ParsekScenario();
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.TrackingStationFly,
            };
            scenario.ArmSwitchSegmentSession(session);
            logLines.Clear();

            scenario.ClearSwitchSegmentSession("merge-accepted");

            Assert.Null(scenario.ActiveSwitchSegmentSession);
            Assert.Contains(logLines,
                l => l.Contains("[SwitchSegment]")
                    && l.Contains("cleared")
                    && l.Contains("merge-accepted")
                    && l.Contains(session.SessionId.ToString("D")));
        }

        // -----------------------------------------------------------------
        // ParsekProcess.ProcessSessionId lifetime: a single field
        // initializer per AppDomain. Two reads on the same process must
        // return the same GUID; ResetForTesting must produce a new one.
        // -----------------------------------------------------------------

        // Fails if: ProcessSessionId becomes a property that returns a
        // fresh Guid each call, or a MonoBehaviour-owned field that
        // regenerates on scene reload. Either change defeats the
        // cross-run-orphan mechanism.
        [Fact]
        public void ProcessSessionId_IsStableWithinSameProcess()
        {
            ParsekProcess.ResetForTesting();
            var a = ParsekProcess.ProcessSessionId;
            var b = ParsekProcess.ProcessSessionId;
            Assert.Equal(a, b);
        }

        // Fails if: ResetForTesting doesn't actually regenerate. Tests
        // that simulate cross-run-orphan rely on this.
        [Fact]
        public void ProcessSessionId_ResetForTesting_GeneratesNewGuid()
        {
            ParsekProcess.ResetForTesting();
            var before = ParsekProcess.ProcessSessionId;
            ParsekProcess.ResetForTesting();
            var after = ParsekProcess.ProcessSessionId;
            Assert.NotEqual(before, after);
        }

        // -----------------------------------------------------------------
        // Legacy / missing fields: TryLoadFrom returns false on malformed
        // payloads (rather than partially loading). Callers treat false
        // as "no marker armed", which is the safe default for the consume
        // site.
        // -----------------------------------------------------------------

        // Fails if: a missing required field silently loads a default
        // (zero IntentId / unspecified action). That would let a stale or
        // corrupted save start an unauthorized segment after load.
        [Fact]
        public void StockActionIntentMarker_TryLoadFrom_MissingIntentId_ReturnsFalse()
        {
            var node = new ConfigNode(StockActionIntentMarker.NodeName);
            node.AddValue("action", StockActionType.TrackingStationFly.ToString());
            node.AddValue("targetVesselPersistentId", "1");
            node.AddValue("sourceScene", StockActionSourceScene.TrackingStation.ToString());
            node.AddValue("capturedRealtime", "100");
            node.AddValue("capturedUT", "1000");
            node.AddValue("processSessionId", Guid.NewGuid().ToString("D"));

            StockActionIntentMarker marker;
            bool ok = StockActionIntentMarker.TryLoadFrom(node, out marker);

            Assert.False(ok);
            Assert.Null(marker);
        }

        // Fails if: SwitchSegmentSession.TryLoadFrom silently builds a
        // partial session on a malformed payload (missing sessionId).
        [Fact]
        public void SwitchSegmentSession_TryLoadFrom_MissingSessionId_ReturnsFalse()
        {
            var node = new ConfigNode(SwitchSegmentSession.NodeName);
            node.AddValue("entryReason", SwitchSegmentEntryReason.MapSwitchTo.ToString());
            node.AddValue("intentId", Guid.NewGuid().ToString("D"));

            SwitchSegmentSession session;
            bool ok = SwitchSegmentSession.TryLoadFrom(node, out session);

            Assert.False(ok);
            Assert.Null(session);
        }
    }
}
