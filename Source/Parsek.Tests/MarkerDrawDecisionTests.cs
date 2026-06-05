using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8c: unit coverage for the pure marker-draw / proto-icon-suppression decision
    /// (<see cref="GhostMapPresence.ResolveMarkerDrawDecision"/>). This is the decision both
    /// marker call sites (flight-map <c>ParsekUI.DrawMapMarkers</c> + TS
    /// <c>ParsekTrackingStation.ClassifyAtmosphericMarkerSkip</c>) route through. The
    /// Unity-coupled wrapper <c>ShouldDrawNonProtoMarkerForGhost</c> (frame count + static
    /// reads) is covered by the in-game test (project rule: Unity-coupled -> in-game).
    ///
    /// "Draw our non-proto marker" is the DUAL of "proto icon hidden", so each case also
    /// asserts the no-double-marker / no-gap invariant: exactly one of {proto icon, our marker}
    /// per ghost per frame. Mirrors the 8b.2 <c>ResolveNonOrbitalLegOwnership</c> pure-dispatch
    /// tests in <c>GhostTrajectoryPolylineBuildTests</c>.
    /// </summary>
    public class MarkerDrawDecisionTests
    {
        // --- Gate OFF: byte-identical to the pre-8c legacy predicate ---

        [Fact]
        public void GateOff_LegacyPredicateOnly_DirectorDecisionIgnored()
        {
            // Gate OFF: the decision is exactly the legacy IsIconSuppressed || IsPolylineOwning
            // predicate. The Director TracedPath DECISION input is never consulted, so toggling it
            // changes nothing - this is the byte-identical gate-off fallback the phase must preserve.

            // Legacy icon-suppressed only -> draw our marker.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorDriveGateOn: false, directorTracedPathActive: false,
                polylineOwning: false, iconSuppressedLegacy: true));
            // Legacy polyline-owning only -> draw our marker.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorDriveGateOn: false, directorTracedPathActive: false,
                polylineOwning: true, iconSuppressedLegacy: false));
            // Neither legacy signal -> proto icon is the indicator, skip our marker.
            Assert.False(GhostMapPresence.ResolveMarkerDrawDecision(
                directorDriveGateOn: false, directorTracedPathActive: false,
                polylineOwning: false, iconSuppressedLegacy: false));
            // Director DECISION true but BOTH legacy signals false -> still skip gate-off (the
            // Director input is not consulted): proves gate-off ignores the repointed source.
            Assert.False(GhostMapPresence.ResolveMarkerDrawDecision(
                directorDriveGateOn: false, directorTracedPathActive: true,
                polylineOwning: false, iconSuppressedLegacy: false));
        }

        // --- Gate ON: the Director is the authoritative source for the owned (TracedPath) leg ---

        [Fact]
        public void GateOn_DirectorTracedPathDecisionIsAuthoritative()
        {
            // Gate ON: the Director's TracedPath DECISION owns the leg, so the proto icon is
            // hidden and our marker draws - even with NO legacy icon-suppressed flag set. This is
            // the 8c repoint of the context-(a) suppression off the legacy ghostsWithSuppressedIcon
            // set onto the Director-sourced decision.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorDriveGateOn: true, directorTracedPathActive: true,
                polylineOwning: false, iconSuppressedLegacy: false));
        }

        [Fact]
        public void GateOn_PolylineOwningIsAuthoritative()
        {
            // Gate ON: polyline-owns (already Director-sourced via the 8b.2 actual-draw set) hides
            // the proto icon, so our marker draws even with no legacy icon-suppressed flag.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorDriveGateOn: true, directorTracedPathActive: false,
                polylineOwning: true, iconSuppressedLegacy: false));
        }

        [Fact]
        public void GateOn_LegacyIconSuppressedKeptAsFallback()
        {
            // Gate ON: the legacy icon-suppressed flag is KEPT as the fallback (NOT retired/deleted -
            // that is 8e). It covers the signals the Director does NOT own yet: the no-bounds
            // transient (context b), below-atmosphere, and the off-arc clamp. Without this disjunct
            // those frames would hide the proto icon (drawIcons=NONE) with no marker drawn - a gap.
            Assert.True(GhostMapPresence.ResolveMarkerDrawDecision(
                directorDriveGateOn: true, directorTracedPathActive: false,
                polylineOwning: false, iconSuppressedLegacy: true));
        }

        [Fact]
        public void GateOn_NoSignal_ProtoIconIsTheIndicator_NoDoubleMarker()
        {
            // Gate ON: no Director decision, no polyline-owns, no legacy suppression -> the proto
            // icon is visible, so our marker is SKIPPED. This is the no-double-marker side: our
            // marker draws ONLY when the proto icon is hidden.
            Assert.False(GhostMapPresence.ResolveMarkerDrawDecision(
                directorDriveGateOn: true, directorTracedPathActive: false,
                polylineOwning: false, iconSuppressedLegacy: false));
        }

        [Fact]
        public void GateOn_IsSupersetOfLegacy_NoMarkerGap()
        {
            // The no-marker-gap proof, exhaustively: for EVERY combination of (polylineOwning,
            // iconSuppressedLegacy), the gate-ON decision is >= the gate-OFF (legacy) decision -
            // i.e. whenever the legacy path would draw the marker (proto hidden), the gate-ON path
            // also draws it. Adding the directorTracedPathActive disjunct can only ADD draws, never
            // remove one, so there is no frame where the proto is hidden but our marker is skipped.
            foreach (bool poly in new[] { false, true })
            foreach (bool legacy in new[] { false, true })
            foreach (bool director in new[] { false, true })
            {
                bool gateOff = GhostMapPresence.ResolveMarkerDrawDecision(
                    directorDriveGateOn: false, directorTracedPathActive: director,
                    polylineOwning: poly, iconSuppressedLegacy: legacy);
                bool gateOn = GhostMapPresence.ResolveMarkerDrawDecision(
                    directorDriveGateOn: true, directorTracedPathActive: director,
                    polylineOwning: poly, iconSuppressedLegacy: legacy);
                // gateOn implies-> covers gateOff (superset): if legacy draws, gate-on draws.
                Assert.True(!gateOff || gateOn);
            }
        }
    }
}
