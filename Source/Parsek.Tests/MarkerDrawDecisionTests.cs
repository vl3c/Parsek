using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8c / 8e S4: unit coverage for the pure marker-draw / proto-icon-suppression decision
    /// (<see cref="GhostMapPresence.ResolveMarkerDrawDecision"/>). This is the decision both
    /// marker call sites (flight-map <c>ParsekUI.DrawMapMarkers</c> + TS
    /// <c>ParsekTrackingStation.ClassifyAtmosphericMarkerSkip</c>) route through. The
    /// Unity-coupled wrapper <c>ShouldDrawNonProtoMarkerForGhost</c> (frame count + static
    /// reads) is covered by the in-game test (project rule: Unity-coupled -> in-game).
    ///
    /// "Draw our non-proto marker" is the DUAL of "proto icon hidden", so each case also
    /// asserts the no-double-marker / no-gap invariant: exactly one of {proto icon, our marker}
    /// per ghost per frame. 8e S4 dropped the director-drive gate, so the decision is now the
    /// unconditional <c>directorTracedPathActive || polylineOwning || iconSuppressedLegacy</c>.
    /// Mirrors the 8b.2 <c>ResolveNonOrbitalLegOwnership</c> pure-dispatch tests in
    /// <c>GhostTrajectoryPolylineBuildTests</c>.
    /// </summary>
    public class MarkerDrawDecisionTests
    {
        // --- The Director is the authoritative source (8e S4: unconditional, no gate) ---

        [Fact]
        public void DirectorTracedPathDecisionIsAuthoritative()
        {
            // The Director's TracedPath DECISION owns the leg, so the proto icon is hidden and our
            // marker draws - even with NO legacy icon-suppressed flag set. This is the 8c repoint of
            // the context-(a) suppression off the legacy ghostsWithSuppressedIcon set onto the
            // Director-sourced decision.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorTracedPathActive: true,
                polylineOwning: false, iconSuppressedLegacy: false));
        }

        [Fact]
        public void PolylineOwningIsAuthoritative()
        {
            // Polyline-owns (already Director-sourced via the 8b.2 actual-draw set) hides the proto
            // icon, so our marker draws even with no legacy icon-suppressed flag.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorTracedPathActive: false,
                polylineOwning: true, iconSuppressedLegacy: false));
        }

        [Fact]
        public void LegacyIconSuppressedKeptAsFallback()
        {
            // The legacy icon-suppressed flag is KEPT as the fallback (NOT retired/deleted - that is
            // 8f). It covers the signals the Director does NOT own yet: the no-bounds transient
            // (context b), below-atmosphere, and the off-arc clamp. Without this disjunct those frames
            // would hide the proto icon (drawIcons=NONE) with no marker drawn - a gap.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorTracedPathActive: false,
                polylineOwning: false, iconSuppressedLegacy: true));
        }

        [Fact]
        public void NoSignal_ProtoIconIsTheIndicator_NoDoubleMarker()
        {
            // No Director decision, no polyline-owns, no legacy suppression -> the proto icon is
            // visible, so our marker is SKIPPED. This is the no-double-marker side: our marker draws
            // ONLY when the proto icon is hidden.
            Assert.False(GhostMapPresence.ResolveMarkerDrawDecision(
                directorTracedPathActive: false,
                polylineOwning: false, iconSuppressedLegacy: false));
        }

        [Fact]
        public void IsSupersetOfLegacy_NoMarkerGap()
        {
            // The no-marker-gap proof, exhaustively: for EVERY combination of (polylineOwning,
            // iconSuppressedLegacy), the decision is >= the legacy IsIconSuppressed || IsPolylineOwning
            // predicate - i.e. whenever the legacy path would draw the marker (proto hidden), the
            // current decision also draws it. Adding the directorTracedPathActive disjunct can only ADD
            // draws, never remove one, so there is no frame where the proto is hidden but our marker is
            // skipped.
            foreach (bool poly in new[] { false, true })
            foreach (bool legacy in new[] { false, true })
            foreach (bool director in new[] { false, true })
            {
                bool legacyDecision = legacy || poly;
                bool decision = GhostMapPresence.ResolveMarkerDrawDecision(
                    directorTracedPathActive: director,
                    polylineOwning: poly, iconSuppressedLegacy: legacy);
                // decision covers the legacy predicate (superset): if legacy draws, decision draws.
                Assert.True(!legacyDecision || decision);
            }
        }

        // --- Marker-decision tracer: TS skip-reason -> shared MarkerOutcome mapping ---

        [Fact]
        public void MapSkipReasonToMarkerOutcome_NativeIcon_MapsToProtoIcon()
        {
            Assert.Equal(MapRenderTrace.MarkerOutcome.DrawnProtoIcon,
                ParsekTrackingStation.MapSkipReasonToMarkerOutcome(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.NativeIconActive));
        }

        [Fact]
        public void MapSkipReasonToMarkerOutcome_Debris_MapsToSkippedDebris()
        {
            Assert.Equal(MapRenderTrace.MarkerOutcome.SkippedDebris,
                ParsekTrackingStation.MapSkipReasonToMarkerOutcome(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.Debris));
        }

        [Fact]
        public void MapSkipReasonToMarkerOutcome_None_MapsToDrawnNonProto()
        {
            // None = eligible to draw; the caller upgrades to SkippedPositionFail only if the
            // subsequent position resolve fails.
            Assert.Equal(MapRenderTrace.MarkerOutcome.DrawnNonProto,
                ParsekTrackingStation.MapSkipReasonToMarkerOutcome(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.None));
        }

        [Fact]
        public void MapSkipReasonToMarkerOutcome_DecisionSkips_MapToSkippedDecisionFalse()
        {
            // NullRecording / NoTrajectoryPoints / OutsideTimeRange / SuppressedByChainFilter /
            // OrbitSegmentActive all collapse to "the decision said do not draw this marker".
            // (A [Theory] with InlineData would expose the internal enum on a public method
            // signature -> CS0051, so these are asserted inline in one Fact.)
            var skips = new[]
            {
                ParsekTrackingStation.AtmosphericMarkerSkipReason.NullRecording,
                ParsekTrackingStation.AtmosphericMarkerSkipReason.NoTrajectoryPoints,
                ParsekTrackingStation.AtmosphericMarkerSkipReason.OutsideTimeRange,
                ParsekTrackingStation.AtmosphericMarkerSkipReason.SuppressedByChainFilter,
                ParsekTrackingStation.AtmosphericMarkerSkipReason.OrbitSegmentActive,
            };
            foreach (var reason in skips)
                Assert.Equal(MapRenderTrace.MarkerOutcome.SkippedDecisionFalse,
                    ParsekTrackingStation.MapSkipReasonToMarkerOutcome(reason));
        }

        // --- GAP-1: TS overlap instance can now carry a REAL ride field on its decision line ---

        [Fact]
        public void BuildMarkerDecisionSignature_TsOverlapInstance_CarriesRealRideAndPolylineSource()
        {
            // GAP-1 proof: the TS overlap path (DrawOneTsOverlapInstanceMarker) rode a leg and threads
            // the real rideReason/legIndex from the diagnostic TryAnchorMarkerToPolyline overload into
            // its per-instance decision line. Before the fix the TS path hardcoded ride=not-attempted
            // posSource=traj, which LIED for an instance that actually rode the shared polyline.
            string sig = MapRenderTrace.BuildMarkerDecisionSignature(
                recordingIndex: 5,
                vesselName: "Hopper",
                directorTracedPathActive: false,
                polylineOwning: true,
                iconSuppressed: false,
                shouldDrawNonProto: true,
                outcome: MapRenderTrace.MarkerOutcome.DrawnNonProto,
                rideReason: MapRenderTrace.MarkerRideReason.RodeLeg,
                legIndex: 3,
                posSource: "polyline");

            Assert.Contains("ride=rode-leg3", sig);
            Assert.Contains("posSource=polyline", sig);
            // No tsSkip on a drawn marker (the param defaulted to null).
            Assert.DoesNotContain("tsSkip=", sig);
        }

        // --- C-1: optional tsSkip= field appends only when set; flight stays byte-identical ---

        [Fact]
        public void BuildMarkerDecisionSignature_TsSkipParam_AppendsTokenWhenSet()
        {
            // A skipped TS marker carries the raw finer reason as a trailing tsSkip= field so the
            // taxonomy the shared MarkerOutcome folds away survives on the per-ghost line.
            string sig = MapRenderTrace.BuildMarkerDecisionSignature(
                recordingIndex: 1,
                vesselName: "Probe",
                directorTracedPathActive: false,
                polylineOwning: false,
                iconSuppressed: false,
                shouldDrawNonProto: false,
                outcome: MapRenderTrace.MarkerOutcome.SkippedDecisionFalse,
                rideReason: MapRenderTrace.MarkerRideReason.NotAttempted,
                legIndex: -1,
                posSource: "traj",
                tsSkipReason: "outside-time-range");

            Assert.Contains("outcome=skipped-decision-false", sig);
            Assert.Contains("tsSkip=outside-time-range", sig);
            // The new field is appended LAST so it never shifts the existing field order.
            Assert.EndsWith("tsSkip=outside-time-range", sig);
        }

        [Fact]
        public void BuildMarkerDecisionSignature_TsSkipParam_OmittedWhenNullOrEmpty_FlightByteIdentical()
        {
            // FLIGHT passes nothing for tsSkipReason (the default). The produced signature must be
            // BYTE-IDENTICAL to the same call without the new param - so flight signatures (and the
            // existing flight tests) are unchanged. Both null and empty omit the field.
            string baseline = MapRenderTrace.BuildMarkerDecisionSignature(
                4, "Munar Probe", true, false, false, true,
                MapRenderTrace.MarkerOutcome.DrawnNonProto,
                MapRenderTrace.MarkerRideReason.RodeLeg, 2, "polyline");

            string withNull = MapRenderTrace.BuildMarkerDecisionSignature(
                4, "Munar Probe", true, false, false, true,
                MapRenderTrace.MarkerOutcome.DrawnNonProto,
                MapRenderTrace.MarkerRideReason.RodeLeg, 2, "polyline",
                tsSkipReason: null);

            string withEmpty = MapRenderTrace.BuildMarkerDecisionSignature(
                4, "Munar Probe", true, false, false, true,
                MapRenderTrace.MarkerOutcome.DrawnNonProto,
                MapRenderTrace.MarkerRideReason.RodeLeg, 2, "polyline",
                tsSkipReason: "");

            Assert.Equal(baseline, withNull);
            Assert.Equal(baseline, withEmpty);
            Assert.DoesNotContain("tsSkip=", baseline);
        }

        // --- C-1: AtmosphericMarkerSkipReasonToken maps each finer reason to a stable token ---

        [Fact]
        public void AtmosphericMarkerSkipReasonToken_MapsEveryReason()
        {
            Assert.Equal("none",
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.None));
            Assert.Equal("native-icon-active",
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.NativeIconActive));
            Assert.Equal("null-recording",
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.NullRecording));
            Assert.Equal("debris",
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.Debris));
            Assert.Equal("no-trajectory-points",
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.NoTrajectoryPoints));
            Assert.Equal("outside-time-range",
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.OutsideTimeRange));
            Assert.Equal("suppressed-by-chain-filter",
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.SuppressedByChainFilter));
            Assert.Equal("orbit-segment-active",
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.OrbitSegmentActive));
        }
    }
}
