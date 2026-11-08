using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the phase helpers <see cref="GhostMapPresence.ResolveMapPresenceGhostSource"/>
    /// is composed of. The end-to-end decision contract is pinned by
    /// <see cref="MapPresenceSourceResolveCharacterizationTests"/>; these cells pin each phase's
    /// own outputs, including the values a phase must leave alone.
    /// </summary>
    [Collection("Sequential")]
    public class MapPresenceSourcePhaseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;
        private readonly CelestialBody mun;

        public MapPresenceSourcePhaseTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            mun = TestBodyRegistry.CreateBody("Mun", 200000.0, 6.5138398e10);
            GhostMapPresence.FindBodyByNameForTesting = name =>
                string.Equals(name, "Mun", StringComparison.Ordinal) ? mun
                : string.Equals(name, "Kerbin", StringComparison.Ordinal) ? kerbin
                : null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording Orbiting(string id, double startUT = 100, double endUT = 200)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Phase " + id,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitInclination = 0.3,
                TerminalOrbitEpoch = 100,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
        }

        private static OrbitSegment Seg(double startUT, double endUT, string body = "Kerbin")
        {
            return new OrbitSegment
            {
                startUT = startUT, endUT = endUT, bodyName = body,
                semiMajorAxis = 694160, eccentricity = 0.05, inclination = 0.3, epoch = startUT
            };
        }

        private static TrajectoryPoint Sentinel()
        {
            return new TrajectoryPoint { ut = -7, bodyName = "Sentinel", altitude = 42 };
        }

        // ---------- TryDecideMapPresenceInputGate -------------------------------

        [Fact]
        public void InputGate_NullTrajectory_DecidesNull()
        {
            Assert.True(GhostMapPresence.TryDecideMapPresenceInputGate(
                null, false, out string reason, out string detail));
            Assert.Equal("null", reason);
            Assert.Equal("null trajectory", detail);
        }

        [Fact]
        public void InputGate_Debris_DecidesBeforeSuppression()
        {
            var rec = Orbiting("debris");
            rec.IsDebris = true;
            Assert.True(GhostMapPresence.TryDecideMapPresenceInputGate(
                rec, true, out string reason, out string detail));
            Assert.Equal("debris", reason);
            Assert.Equal("isDebris=True", detail);
        }

        [Fact]
        public void InputGate_PlaybackToggleOff_DecidesAndLogsTheSuppression()
        {
            var rec = Orbiting("toggle-off");
            rec.PlaybackEnabled = false;
            Assert.True(GhostMapPresence.TryDecideMapPresenceInputGate(
                rec, true, out string reason, out string detail));
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipPlaybackDisabled, reason);
            Assert.Equal("playbackEnabled=False", detail);
            Assert.Contains(logLines, l => l.Contains("toggle-off"));
        }

        [Fact]
        public void InputGate_Suppressed_Decides()
        {
            Assert.True(GhostMapPresence.TryDecideMapPresenceInputGate(
                Orbiting("suppressed"), true, out string reason, out string detail));
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipSuppressed, reason);
            Assert.Equal("isSuppressed=True", detail);
        }

        [Fact]
        public void InputGate_OrdinaryRecording_PassesWithNoReason()
        {
            Assert.False(GhostMapPresence.TryDecideMapPresenceInputGate(
                Orbiting("ok"), false, out string reason, out string detail));
            Assert.Null(reason);
            Assert.Null(detail);
            Assert.Empty(logLines);
        }

        // ---------- TryDecideMapPresenceMaterializedGate ------------------------

        [Fact]
        public void MaterializedGate_NotMaterialized_PassesSilently()
        {
            Assert.False(GhostMapPresence.TryDecideMapPresenceMaterializedGate(
                Orbiting("a"), "a", false, true, true, out string reason, out string detail));
            Assert.Null(reason);
            Assert.Null(detail);
            Assert.Empty(logLines);
        }

        [Fact]
        public void MaterializedGate_MaterializedOutsideLoop_AlreadySpawned()
        {
            Assert.True(GhostMapPresence.TryDecideMapPresenceMaterializedGate(
                Orbiting("a"), "a", true, false, false, out string reason, out string detail));
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipAlreadySpawned, reason);
            Assert.Equal("already spawned", detail);
            Assert.Empty(logLines);
        }

        [Fact]
        public void MaterializedGate_LiveBoundAnchorInLoop_LiveAnchorDoubleWithPid()
        {
            var rec = Orbiting("anchor");
            rec.VesselPersistentId = 4242u;
            Assert.True(GhostMapPresence.TryDecideMapPresenceMaterializedGate(
                rec, "anchor", true, true, true, out string reason, out string detail));
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipLiveAnchorDouble, reason);
            Assert.Equal("live-anchor double suppressed", detail);
            Assert.Contains(logLines, l => l.Contains("anchor-double-suppressed (live-bind event): rec=anchor anchorPid=4242"));
        }

        [Fact]
        public void MaterializedGate_LoopMemberInWindow_DrawsAlongsideAndLogs()
        {
            Assert.False(GhostMapPresence.TryDecideMapPresenceMaterializedGate(
                Orbiting("loop"), "loop", true, true, false, out string reason, out string detail));
            Assert.Null(reason);
            Assert.Null(detail);
            Assert.Contains(logLines, l => l.Contains(
                "ResolveMapPresenceGhostSource: loop member rec=loop drawing map ghost alongside persisted real vessel"));
        }

        // ---------- TryDecideMapPresenceTerminalGate ----------------------------

        [Fact]
        public void TerminalGate_LandedTerminal_DecidesWithDefaultsForTheLaterInputs()
        {
            var rec = Orbiting("landed");
            rec.TerminalStateValue = TerminalState.Landed;
            Assert.True(GhostMapPresence.TryDecideMapPresenceTerminalGate(
                rec, 150, false, out TerminalState? terminal, out double activationStartUT,
                out bool region, out string reason, out string detail));
            Assert.Equal(TerminalState.Landed, terminal);
            Assert.Equal(0.0, activationStartUT);
            Assert.False(region);
            Assert.Equal("terminal-Landed", reason);
            Assert.Equal("terminal=Landed", detail);
        }

        [Fact]
        public void TerminalGate_DescentContinuation_DecidesAfterResolvingActivation()
        {
            var rec = Orbiting("descent");
            Assert.True(GhostMapPresence.TryDecideMapPresenceTerminalGate(
                rec, 150, true, out TerminalState? terminal, out double activationStartUT,
                out bool _, out string reason, out string detail));
            Assert.Equal(TerminalState.Orbiting, terminal);
            Assert.Equal(PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec), activationStartUT);
            Assert.Equal("transfer-member-descent-continuation", reason);
            Assert.StartsWith("descent-trigger transfer member handed off / landed: retire cleanly; ", detail);
        }

        [Fact]
        public void TerminalGate_EligibleTerminal_PassesWithActivationAndRegion()
        {
            var rec = Orbiting("eligible");
            Assert.False(GhostMapPresence.TryDecideMapPresenceTerminalGate(
                rec, 150, false, out TerminalState? terminal, out double activationStartUT,
                out bool region, out string reason, out string detail));
            Assert.Equal(TerminalState.Orbiting, terminal);
            Assert.Equal(PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec), activationStartUT);
            Assert.Equal(GhostMapPresence.IsTerminalMapPresenceRegion(rec, 150), region);
            Assert.Null(reason);
            Assert.Null(detail);
        }

        // ---------- ResolveMapPresenceChainSegmentsLogged -----------------------

        [Fact]
        public void ChainSegmentsLogged_OwnSegments_ReturnedWithoutTheChainTipLine()
        {
            var rec = Orbiting("own");
            rec.OrbitSegments = new List<OrbitSegment> { Seg(100, 200) };
            List<OrbitSegment> segments = GhostMapPresence.ResolveMapPresenceChainSegmentsLogged(rec, "own");
            Assert.Single(segments);
            Assert.DoesNotContain(logLines, l => l.Contains("map-presence chain-tip segments:"));
        }

        [Fact]
        public void ChainSegmentsLogged_SplitHead_BorrowsTheTipSegmentsAndLogsOnce()
        {
            PredictedTailMapPresenceTests.RegisterSplitChain(out Recording head, out Recording tip);
            List<OrbitSegment> segments = GhostMapPresence.ResolveMapPresenceChainSegmentsLogged(head, head.RecordingId);
            Assert.Equal(tip.OrbitSegments.Count, segments.Count);
            GhostMapPresence.ResolveMapPresenceChainSegmentsLogged(head, head.RecordingId);
            Assert.Single(logLines.FindAll(l => l.Contains(
                "map-presence chain-tip segments: head=" + head.RecordingId + " tip=" + tip.RecordingId)));
        }

        // ---------- TryResolveCoveringSegmentSource -----------------------------

        [Fact]
        public void CoveringSegment_NoSegments_FalseWithDefaults()
        {
            int idx = -1;
            Assert.False(GhostMapPresence.TryResolveCoveringSegmentSource(
                Orbiting("none"), 150, null, false, false, null, false, ref idx,
                out OrbitSegment segment, out GhostMapPresence.TrackingStationGhostSource source,
                out string detail));
            Assert.Null(segment.bodyName);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Null(detail);
        }

        [Fact]
        public void CoveringSegment_CoveringSegment_SegmentSourceWithBodyAndWindow()
        {
            var rec = Orbiting("cover", 100, 30000);
            int idx = -1;
            Assert.True(GhostMapPresence.TryResolveCoveringSegmentSource(
                rec, 500, new List<OrbitSegment> { Seg(400, 600) }, false, false, null, false, ref idx,
                out OrbitSegment segment, out GhostMapPresence.TrackingStationGhostSource source,
                out string detail));
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Equal("Kerbin", segment.bodyName);
            Assert.Equal(400.0, segment.startUT);
            Assert.Equal("segmentBody=Kerbin segmentUT=400.0-600.0", detail);
        }

        // ---------- FormatCoveringSegmentCheckpointDetail -----------------------

        [Fact]
        public void CoveringCheckpointDetail_NoCheckpoint_Null()
        {
            int idx = -1;
            Assert.Null(GhostMapPresence.FormatCoveringSegmentCheckpointDetail(
                Orbiting("plain"), 150, true, "Mun", ref idx));
        }

        [Fact]
        public void CoveringCheckpointDetail_Checkpoint_RejectedAsSaferSegment()
        {
            var rec = GhostMapSoiGapStateVectorTests.MakeSegmentAvailableRecording("seg-cp", "Mun");
            int idx = -1;
            string detail = GhostMapPresence.FormatCoveringSegmentCheckpointDetail(
                rec, 160, true, "Mun", ref idx);
            Assert.StartsWith(
                "stateVectorSource=OrbitalCheckpoint sectionUT=150.0-190.0 pointUT=150.0 stateVectorBody=Mun ", detail);
            Assert.Contains("orbitalCheckpointFallback=reject", detail);
            Assert.Contains("fallbackReason=" + GhostMapPresence.OrbitalCheckpointStateVectorRejectSaferSegment, detail);
            Assert.Equal(0, idx);
        }

        // ---------- TryAcceptCheckpointSoiGapStateVector ------------------------

        [Fact]
        public void CheckpointSoiGap_RecoveryAllowed_AcceptsAndSetsTheSkipReason()
        {
            var rec = GhostMapSoiGapStateVectorTests.MakeSoiGapRecording("soi", "Mun", "Mun");
            int idx = -1;
            string skipReason = null;
            Assert.True(GhostMapPresence.TryAcceptCheckpointSoiGapStateVector(
                rec, 160, true, "Mun", ref idx, out TrajectoryPoint point, ref skipReason,
                out string rejectReason, out string rejectDetail, out string detail));
            Assert.Equal(GhostMapPresence.SoiGapStateVectorFallbackReason, skipReason);
            Assert.Equal("Mun", point.bodyName);
            Assert.Null(rejectReason);
            Assert.Null(rejectDetail);
            Assert.Contains("orbitalCheckpointFallback=accept", detail);
        }

        [Fact]
        public void CheckpointSoiGap_RecoveryNotAllowed_RejectsAndKeepsTheSkipReason()
        {
            var rec = GhostMapSoiGapStateVectorTests.MakeSoiGapRecording("soi", "Mun", "Mun");
            int idx = -1;
            string skipReason = "kept";
            Assert.False(GhostMapPresence.TryAcceptCheckpointSoiGapStateVector(
                rec, 160, false, "Mun", ref idx, out TrajectoryPoint point, ref skipReason,
                out string rejectReason, out string rejectDetail, out string detail));
            Assert.Equal("kept", skipReason);
            Assert.Equal("Mun", point.bodyName);
            Assert.Equal(GhostMapPresence.OrbitalCheckpointStateVectorRejectNotSoiGap, rejectReason);
            Assert.Equal(detail, rejectDetail);
            Assert.Contains("orbitalCheckpointFallback=reject", detail);
        }

        [Fact]
        public void CheckpointSoiGap_NoCheckpoint_FalseWithNoRejection()
        {
            int idx = -1;
            string skipReason = "kept";
            Assert.False(GhostMapPresence.TryAcceptCheckpointSoiGapStateVector(
                Orbiting("plain"), 150, true, "Mun", ref idx, out TrajectoryPoint _, ref skipReason,
                out string rejectReason, out string rejectDetail, out string detail));
            Assert.Equal("kept", skipReason);
            Assert.Null(rejectReason);
            Assert.Null(rejectDetail);
            Assert.Null(detail);
        }

        // ---------- IsRelativeStateVectorDeferredForSegmentGap ------------------

        private static Recording WithSegments(params OrbitSegment[] segments)
        {
            var rec = Orbiting("rel");
            rec.OrbitSegments = new List<OrbitSegment>(segments);
            return rec;
        }

        [Fact]
        public void RelativeDefer_PastAndFutureSegmentsInRelativeFrame_True()
        {
            Assert.True(GhostMapPresence.IsRelativeStateVectorDeferredForSegmentGap(
                WithSegments(Seg(0, 50), Seg(300, 400)), 200, true));
        }

        [Fact]
        public void RelativeDefer_NotInRelativeFrame_False()
        {
            Assert.False(GhostMapPresence.IsRelativeStateVectorDeferredForSegmentGap(
                WithSegments(Seg(0, 50), Seg(300, 400)), 200, false));
        }

        [Fact]
        public void RelativeDefer_OnlyPastOrOnlyFutureSegment_False()
        {
            Assert.False(GhostMapPresence.IsRelativeStateVectorDeferredForSegmentGap(
                WithSegments(Seg(0, 50)), 200, true));
            Assert.False(GhostMapPresence.IsRelativeStateVectorDeferredForSegmentGap(
                WithSegments(Seg(300, 400)), 200, true));
        }

        [Fact]
        public void RelativeDefer_SegmentCoveringTheUT_NotPastNorFuture_False()
        {
            Assert.False(GhostMapPresence.IsRelativeStateVectorDeferredForSegmentGap(
                WithSegments(Seg(100, 300)), 200, true));
        }

        [Fact]
        public void RelativeDefer_NoSegments_False()
        {
            Assert.False(GhostMapPresence.IsRelativeStateVectorDeferredForSegmentGap(
                Orbiting("none"), 200, true));
        }

        // ---------- TryResolveMapPresenceStateVectorSource ----------------------

        // Reaches a body that builds a Quaternion through a Unity native (measured on mono).
        [UnityEntryFact]
        public void StateVector_PointOnlyRecording_ResolvesWithDetail()
        {
            var rec = new Recording
            {
                RecordingId = "sv",
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Mun", altitude = 3000, velocity = new Vector3(0, 100, 0) },
                    new TrajectoryPoint { ut = 200, bodyName = "Mun", altitude = 3200, velocity = new Vector3(0, 110, 0) }
                }
            };
            int idx = -1;
            TrajectoryPoint point = Sentinel();
            Assert.True(GhostMapPresence.TryResolveMapPresenceStateVectorSource(
                rec, 150, ref idx, ref point, out bool consider, out bool defer,
                out string svSkip, out string gapDetail, out string detail));
            Assert.True(consider);
            Assert.False(defer);
            Assert.Null(gapDetail);
            Assert.Equal("Mun", point.bodyName);
            Assert.StartsWith("stateVectorBody=Mun alt=", detail);
        }

        [Fact]
        public void StateVector_SegmentRecordingOutsideRelativeFrame_NotConsideredAndPointUntouched()
        {
            var rec = WithSegments(Seg(400, 600));
            int idx = -1;
            TrajectoryPoint point = Sentinel();
            Assert.False(GhostMapPresence.TryResolveMapPresenceStateVectorSource(
                rec, 500, ref idx, ref point, out bool consider, out bool defer,
                out string svSkip, out string gapDetail, out string detail));
            Assert.False(consider);
            Assert.False(defer);
            Assert.Null(svSkip);
            Assert.Null(gapDetail);
            Assert.Null(detail);
            Assert.Equal("Sentinel", point.bodyName);
            Assert.Equal(-7.0, point.ut);
            Assert.Equal(-1, idx);
        }

        [Fact]
        public void StateVector_RelativeSegmentGap_DeferredWithReasonAndDetail()
        {
            var rec = GhostMapPresenceTests.BuildRelativeFrameRecordingBetweenOrbitSegments();
            int idx = -1;
            TrajectoryPoint point = Sentinel();
            Assert.False(GhostMapPresence.TryResolveMapPresenceStateVectorSource(
                rec, 200, ref idx, ref point, out bool consider, out bool defer,
                out string svSkip, out string gapDetail, out string detail));
            Assert.False(consider);
            Assert.True(defer);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipRelativeStateVectorSegmentGap, svSkip);
            Assert.Equal("relativeFrame=True orbitSegmentGap=True", gapDetail);
            Assert.Null(detail);
            Assert.Equal("Sentinel", point.bodyName);
        }

        // ---------- TryDecideTerminalFallbackGate --------------------------------

        private static bool Gate(
            Recording rec, double currentUT, bool allowFallback, string rejectReason, string rejectDetail,
            out string reason, out string detail,
            bool consider = false, bool defer = false, string svSkip = null, string gapDetail = null)
        {
            return GhostMapPresence.TryDecideTerminalFallbackGate(
                rec, currentUT, rec.TerminalStateValue,
                PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec),
                allowFallback, consider, defer, svSkip, gapDetail, rejectReason, rejectDetail,
                out reason, out detail);
        }

        [Fact]
        public void FallbackGate_NoFallbackAllowed_CheckpointRejectWinsAndDetailsJoinInOrder()
        {
            Assert.True(Gate(Orbiting("nf"), 500, false, "cp-reject", "cp-detail",
                out string reason, out string detail, defer: true, gapDetail: "gap-detail"));
            Assert.Equal("cp-reject", reason);
            Assert.Equal("terminalFallback=False hasOrbitSegments=False gap-detail cp-detail", detail);
        }

        [Fact]
        public void FallbackGate_TerminalNotAllowedForTerminalOrbit_Decides()
        {
            var rec = Orbiting("sub");
            rec.TerminalStateValue = TerminalState.SubOrbital;
            Assert.True(Gate(rec, 500, true, null, "cp-detail", out string reason, out string detail));
            Assert.Equal("terminal-SubOrbital", reason);
            Assert.Equal("terminal=SubOrbital terminalOrbitFallback=True cp-detail", detail);
        }

        [Fact]
        public void FallbackGate_BeforeActivation_Decides()
        {
            Assert.True(Gate(Orbiting("early", 1000, 2000), 500, true, null, null,
                out string reason, out string detail));
            Assert.Equal("before-activation", reason);
            Assert.Equal("activationStartUT=1000.0", detail);
        }

        [Fact]
        public void FallbackGate_AfterTheRecording_Passes()
        {
            Assert.False(Gate(Orbiting("done"), 500, true, null, null,
                out string reason, out string detail));
            Assert.Null(reason);
            Assert.Null(detail);
        }

        // ---------- ResolveTerminalOrbitMapPresenceSource / the fallback wrapper --

        [Fact]
        public void TerminalOrbit_SeedsFromTheTerminalOrbitAndKeepsTheSkipReason()
        {
            var rec = Orbiting("orbit");
            string skipReason = "kept";
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTerminalOrbitMapPresenceSource(
                    rec, 500, 100, false, false, "cp-detail", ref skipReason,
                    out OrbitSegment segment, out string detail);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.TerminalOrbit, source);
            Assert.Equal("kept", skipReason);
            Assert.Equal("Kerbin", segment.bodyName);
            Assert.Equal(100.0, segment.startUT);
            Assert.Equal(200.0, segment.endUT);
            Assert.Equal(700000.0, segment.semiMajorAxis);
            Assert.StartsWith("terminalBody=Kerbin endUT=200.0 seedBody=Kerbin ", detail);
            Assert.EndsWith(" cp-detail", detail);
        }

        [Fact]
        public void TerminalOrbit_EndpointConflict_NoneWithTheConflictReason()
        {
            var rec = new Recording
            {
                RecordingId = "conflict", TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000, TerminalStateValue = TerminalState.Orbiting,
                ExplicitEndUT = 200,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 200, bodyName = "Mun", latitude = 1, longitude = 2, altitude = 3000 }
                }
            };
            string skipReason = null;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTerminalOrbitMapPresenceSource(
                    rec, 1000, 200, false, false, null, ref skipReason,
                    out OrbitSegment segment, out string detail);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipEndpointConflict, skipReason);
            Assert.Null(segment.bodyName);
            Assert.StartsWith("terminalBody=Kerbin endUT=200.0 seedFailure=endpoint-conflict endpointBody=Mun ", detail);
        }

        [Fact]
        public void TerminalFallback_GateDecides_NoneWithDefaultSegment()
        {
            var rec = Orbiting("early", 1000, 2000);
            string skipReason = null;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTerminalFallbackMapPresenceSource(
                    rec, 500, rec.TerminalStateValue, 1000, false, true, false,
                    false, false, null, null, null, null,
                    ref skipReason, out OrbitSegment segment, out string detail);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal("before-activation", skipReason);
            Assert.Null(segment.bodyName);
            Assert.Equal("activationStartUT=1000.0", detail);
        }

        [Fact]
        public void TerminalFallback_GatePasses_ReachesTheTerminalOrbit()
        {
            var rec = Orbiting("orbit");
            string skipReason = null;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveTerminalFallbackMapPresenceSource(
                    rec, 500, rec.TerminalStateValue, 100, false, true, false,
                    false, false, null, null, null, null,
                    ref skipReason, out OrbitSegment segment, out string _);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.TerminalOrbit, source);
            Assert.Null(skipReason);
            Assert.Equal("Kerbin", segment.bodyName);
        }

        // ---------- EmitMapPresenceSourceDecision --------------------------------

        [Fact]
        public void EmitDecision_NoOperationName_ReturnsTheSourceSilently()
        {
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment,
                GhostMapPresence.EmitMapPresenceSourceDecision(
                    Orbiting("silent"), "silent", 2, 500, null,
                    GhostMapPresence.TrackingStationGhostSource.Segment, null, "d",
                    Seg(400, 600), default(TrajectoryPoint)));
            Assert.Empty(logLines);
        }

        [Fact]
        public void EmitDecision_OperationName_WritesTheDecisionThenTheStructuredLine()
        {
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None,
                GhostMapPresence.EmitMapPresenceSourceDecision(
                    Orbiting("emit"), "emit", 2, 500, "phase-op",
                    GhostMapPresence.TrackingStationGhostSource.None, "why", "the-detail",
                    default(OrbitSegment), default(TrajectoryPoint)));
            Assert.Equal(2, logLines.Count);
            Assert.Contains("phase-op: rec=emit currentUT=500.0 source=None reason=why the-detail", logLines[0]);
            Assert.Contains("source-resolve: rec=emit idx=2", logLines[1]);
            Assert.Contains("reason=why", logLines[1]);
        }

        [Fact]
        public void EmitDecision_SameDecisionTwice_DecisionLineOnChangeOnly()
        {
            for (int i = 0; i < 2; i++)
                GhostMapPresence.EmitMapPresenceSourceDecision(
                    Orbiting("emit"), "emit", 2, 500, "phase-op",
                    GhostMapPresence.TrackingStationGhostSource.None, "why", null,
                    default(OrbitSegment), default(TrajectoryPoint));
            Assert.Single(logLines.FindAll(l => l.Contains("phase-op: rec=emit")));
        }
    }
}
