using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 8 (map/TS render overhaul, migration plan section 10) - the in-game TRACER-COVERAGE MATRIX gate.
    //
    // The Phase-8 logging priority: a mapRenderTracing-on run over the regression set must emit the expected
    // Tier-A/B/C lines for EVERY map/TS render surface and every NEW phase/seam/lifecycle event. This test is
    // the standing "the tracer matrix is COMPLETE" assertion. Rather than wait for a live multi-ghost
    // regression flight to incidentally exercise each surface (non-deterministic, and a missing surface would
    // simply produce no line - a silent gap), it drives each surface + event class through its REAL emit
    // ENTRY POINT (the production builders + the gated MapRenderTrace / GhostRenderTrace sinks) with the
    // tracer forced on, captures every emitted line, and asserts a complete matrix:
    //
    //   surface=ProtoOrbitLine     appear/disappear: LineVisibilityChange (Tier-B)
    //                              Tier-A:           GhostCreated / GhostDestroyed (lifecycle), FirstPosition,
    //                                                PhaseChainAssembled, fail-closed-to-faithful
    //                              Tier-C:           icon-off-orbit / decision-vs-truth / parity-drift
    //   surface=ProtoIcon          Tier-B:           icon-suppressed
    //   surface=Polyline           appear/disappear: PolylineLegChange appear|disappear (Tier-A)
    //                              Tier-A:           DescentStitched
    //                              Tier-C:           polyline-orbit-overlap
    //   surface=PolylineForwardArc appear/disappear: PolylineLegChange appear|disappear (Tier-A)
    //   surface=ImguiLabeledMarker Tier-B:           MarkerDecision (flight-map marker)
    //   surface=AtmosphericMarker  Tier-B:           MarkerDecision (TS marker)
    //   flight-scene mesh          appear/disappear: MeshSpawned / MeshDestroyed (Tier-A, the GhostRenderTrace
    //                                                sibling instrument)
    //
    // NON-VACUOUS: every assertion drives a real production emit path (real builder + real Emit* sink). If a
    // surface ever loses its appear/disappear or Tier-A/B/C wiring (a helper renamed, the RenderSurface enum
    // entry dropped, an Emit* turned into a no-op, the gate inverted), the matching matrix line goes missing
    // and this test fails - the exact "a surface is no longer instrumented" regression Phase 8 must guard.
    //
    // The pure per-line schema is locked headlessly in MapRenderTraceTests / MapRenderEventCoverageTests and
    // the per-surface decision math in the surface tests; this in-game test is the integration assertion that
    // a mapRenderTracing-on session lights up EVERY surface end-to-end (the Unity-coupled
    // CurrentFrameCount() / Planetarium / live-sink path those cannot reach). FLIGHT only; career-independent.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics).
    public class MapRenderTracerCoverageInGameTest
    {
        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 8 tracer-coverage matrix: with mapRenderTracing on, EVERY map/TS render "
                + "surface (ProtoOrbitLine / ProtoIcon / Polyline / PolylineForwardArc / ImguiLabeledMarker / "
                + "AtmosphericMarker / flight mesh) emits its appear/disappear + Tier-A/B/C lines through the "
                + "real production emit paths - the tracer matrix is complete")]
        public void TracerMatrix_EverySurface_EmitsAppearDisappearAndTiers()
        {
            bool prevMapForce = MapRenderTrace.ForceEnabledForTesting;
            bool prevMeshForce = GhostRenderTrace.ForceEnabledForTesting;
            System.Func<int> prevMapFrame = MapRenderTrace.FrameCounterOverrideForTesting;
            System.Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool prevSuppress = ParsekLog.SuppressLogging;
            bool? prevVerbose = ParsekLog.VerboseOverrideForTesting;

            var lines = new List<string>();

            try
            {
                MapRenderTrace.Reset();
                MapRenderTrace.ForceEnabledForTesting = true;
                MapRenderTrace.FrameCounterOverrideForTesting = () => Time.frameCount;
                GhostRenderTrace.ForceEnabledForTesting = true;
                ParsekLog.SuppressLogging = false;
                ParsekLog.VerboseOverrideForTesting = true; // Tier-B lines route to Verbose
                ParsekLog.TestSinkForTesting = line => lines.Add(line);

                double ut = Planetarium.GetUniversalTime();
                if (ut <= 0.0)
                    ut = 1000.0; // cold-load UT=0 guard: a positive UT so the windows / formatters are real

                DriveAllSurfaces(ut);

                // ---- The matrix: each entry MUST be present (a missing line = an uninstrumented surface) ----

                // surface=ProtoOrbitLine -----------------------------------------------------------------
                AssertHasMapLine(lines, "LineVisibilityChange", "ProtoOrbitLine",
                    "ProtoOrbitLine appear/disappear (Tier-B LineVisibilityChange) missing");
                AssertHasMapPhase(lines, "GhostCreated", "ProtoOrbitLine",
                    "ProtoOrbitLine Tier-A lifecycle GhostCreated missing");
                AssertHasMapPhase(lines, "GhostDestroyed", "ProtoOrbitLine",
                    "ProtoOrbitLine Tier-A lifecycle GhostDestroyed missing");
                AssertHasMapPhase(lines, "FirstPosition", "ProtoOrbitLine",
                    "ProtoOrbitLine Tier-A FirstPosition missing");
                AssertHasMapPhase(lines, "PhaseChainAssembled", "ProtoOrbitLine",
                    "ProtoOrbitLine Tier-A PhaseChainAssembled (Phase 3) missing");
                AssertHasReason(lines, MapRenderTrace.EventFailClosedToFaithful, "ProtoOrbitLine",
                    "ProtoOrbitLine Tier-A fail-closed-to-faithful (Phase 7) missing");
                AssertHasReason(lines, "icon-off-orbit", "ProtoOrbitLine",
                    "ProtoOrbitLine Tier-C icon-off-orbit missing");
                AssertHasReason(lines, "decision-vs-truth", "ProtoOrbitLine",
                    "ProtoOrbitLine Tier-C decision-vs-truth missing");
                AssertHasReason(lines, MapRenderTrace.AnomalyParityDrift, "ProtoOrbitLine",
                    "ProtoOrbitLine Tier-C parity-drift missing");

                // surface=ProtoIcon ----------------------------------------------------------------------
                AssertHasMapLine(lines, "icon-suppressed", "ProtoIcon",
                    "ProtoIcon Tier-B icon-suppressed missing");

                // surface=Polyline -----------------------------------------------------------------------
                AssertHasMapLine(lines, "PolylineLegChange", "Polyline",
                    "Polyline appear/disappear (Tier-A PolylineLegChange) missing");
                AssertContainsAll(lines, "Polyline appear/disappear must carry both event tokens",
                    new[] { "surface=Polyline", "event=appear" });
                AssertContainsAll(lines, "Polyline appear/disappear must carry both event tokens",
                    new[] { "surface=Polyline", "event=disappear" });
                AssertHasMapPhase(lines, "DescentStitched", "Polyline",
                    "Polyline Tier-A DescentStitched (Phase 6) missing");
                AssertHasReason(lines, "polyline-orbit-overlap", "Polyline",
                    "Polyline Tier-C polyline-orbit-overlap missing");

                // surface=PolylineForwardArc -----------------------------------------------------------
                AssertHasMapLine(lines, "PolylineLegChange", "PolylineForwardArc",
                    "PolylineForwardArc appear/disappear (Tier-A PolylineLegChange) missing");

                // surface=ImguiLabeledMarker / AtmosphericMarker (Tier-B MarkerDecision) ----------------
                AssertHasMapLine(lines, "MarkerDecision", "ImguiLabeledMarker",
                    "ImguiLabeledMarker Tier-B MarkerDecision (flight-map marker) missing");
                AssertHasMapLine(lines, "MarkerDecision", "AtmosphericMarker",
                    "AtmosphericMarker Tier-B MarkerDecision (TS marker) missing");

                // flight-scene mesh (the GhostRenderTrace sibling instrument) ---------------------------
                AssertHasMeshPhase(lines, "MeshSpawned",
                    "flight-scene mesh Tier-A MeshSpawned (appear) missing");
                AssertHasMeshPhase(lines, "MeshDestroyed",
                    "flight-scene mesh Tier-A MeshDestroyed (disappear) missing");

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "TracerMatrix: complete - {0} captured lines cover all surfaces", lines.Count));
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevMapForce;
                MapRenderTrace.FrameCounterOverrideForTesting = prevMapFrame;
                GhostRenderTrace.ForceEnabledForTesting = prevMeshForce;
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.SuppressLogging = prevSuppress;
                ParsekLog.VerboseOverrideForTesting = prevVerbose;
                MapRenderTrace.Reset();
            }
        }

        // Drives every surface + event class through its REAL production emit entry point. Each call routes a
        // real builder through the gated sink, so a captured line proves the path is wired end-to-end.
        private static void DriveAllSurfaces(double ut)
        {
            const string recId = "tracer-matrix-rec";
            const uint pid = 424242u;
            string pidKey = pid.ToString(CultureInfo.InvariantCulture);

            // -- ProtoOrbitLine: Tier-B LineVisibilityChange (appear/disappear of the line decision) --
            MapRenderTrace.EmitLineVisibilityOnChange(
                pidKey, recId, ut, signature: "visible-body-frame",
                details: "lineActive=true drawIcons=ALL reason=visible-body-frame");

            // -- ProtoOrbitLine: Tier-A lifecycle (GhostCreated / GhostDestroyed) via the real builder --
            MapRenderTrace.EmitStructural(
                "GhostCreated", MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, ut, ut,
                MapRenderTrace.InitialWindowSeconds,
                MapRenderTrace.BuildLifecycleDetails(
                    "TracerMatrixGhost", "Kerbin", "FLIGHT", new Vector3d(1.0, 2.0, 3.0), "spawn"),
                recId);
            MapRenderTrace.EmitStructural(
                "GhostDestroyed", MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, ut, ut,
                MapRenderTrace.DestroyWindowSeconds,
                MapRenderTrace.BuildLifecycleDetails(
                    "TracerMatrixGhost", "Kerbin", "FLIGHT", null, "retire"),
                recId);

            // -- ProtoOrbitLine: Tier-A FirstPosition via the real builder --
            MapRenderTrace.EmitStructural(
                "FirstPosition", MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, ut, ut,
                MapRenderTrace.InitialWindowSeconds,
                MapRenderTrace.BuildFirstPositionDetails(
                    new Vector3d(4.0, 5.0, 6.0), "Kerbin", 700000.0, 0.01, "first-truth"),
                recId);

            // -- ProtoOrbitLine: Tier-A PhaseChainAssembled (Phase 3) via the REAL ShadowRenderDriver hook --
            // Build a real one-phase chain and drive the live emit so the Phase-3 structural wiring is proven.
            PhaseChain chain = BuildTracerMatrixChain(recId, ut);
            ShadowRenderDriver.EmitPhaseChainAssembledForTesting(pid, ut, chain);

            // -- ProtoOrbitLine: Tier-A fail-closed-to-faithful (Phase 7) via the REAL classifier emit --
            FailClosedClassifier.FailClosedDecision failClosed = FailClosedClassifier.Classify(
                new List<string> { "Kerbin", "Sun" }, hasLiveVesselArrivalAnchor: true,
                referenceBodyName: _ => null);
            FailClosedClassifier.EmitFailClosedToFaithful(recId, committedIndex: 0, ut, failClosed);

            // -- ProtoOrbitLine: Tier-C anomalies (icon-off-orbit / decision-vs-truth / parity-drift) --
            MapRenderTrace.EmitAnomaly(
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, ut, ut,
                "icon-off-orbit", "angleDeg=42.0", recId);
            MapRenderTrace.EmitAnomaly(
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, ut, ut,
                "decision-vs-truth",
                "drawIcons-changed-after-decision(intended=ALL,actual=NONE) intentReason=test", recId);
            MapRenderTrace.EmitAnomaly(
                MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, ut, ut,
                MapRenderTrace.AnomalyParityDrift, "maxDev=999.0m tol=10.0m", recId);

            // -- ProtoIcon: Tier-B icon-suppressed (on-change EVENT) --
            MapRenderTrace.EmitOnChange(
                "icon-suppressed", MapRenderTrace.RenderSurface.ProtoIcon, pidKey, ut, ut,
                "iconSuppressed=true reason=below-atmosphere", recId);

            // -- Polyline: appear/disappear (Tier-A PolylineLegChange) via the REAL polyline diff emitter --
            // Two passes over the SAME previous-set so the first call emits appear, the second emits disappear.
            var prev = new HashSet<string>();
            var cur = new HashSet<string> { recId };
            Parsek.Display.GhostTrajectoryPolylineRenderer.EmitPolylineDrawSetChangesForTesting(
                MapRenderTrace.RenderSurface.Polyline, prev, cur, ut); // prev {} -> cur {rec} => appear
            var curEmpty = new HashSet<string>();
            Parsek.Display.GhostTrajectoryPolylineRenderer.EmitPolylineDrawSetChangesForTesting(
                MapRenderTrace.RenderSurface.Polyline, prev, curEmpty, ut + 2.0); // {rec} -> {} => disappear

            // -- Polyline: Tier-A DescentStitched (Phase 6) via the REAL stitcher emit --
            CrossMemberSeamStitcher.EmitDescentStitched(
                pid, recId, ut, reAnchoredHead: ut + 5.0, committedIndex: 0,
                Parsek.Reaim.DescentTrigger.DescentHeadPhase.Descent);

            // -- Polyline: Tier-C polyline-orbit-overlap --
            MapRenderTrace.EmitAnomaly(
                MapRenderTrace.RenderSurface.Polyline, pidKey, ut, ut,
                "polyline-orbit-overlap",
                "orbit-line-active-while-polyline-owns icon-on-orbit sma=700000 ecc=0.0100", recId);

            // -- PolylineForwardArc: appear/disappear (Tier-A PolylineLegChange) via the REAL diff emitter --
            var prevArc = new HashSet<string>();
            var curArc = new HashSet<string> { recId };
            Parsek.Display.GhostTrajectoryPolylineRenderer.EmitPolylineDrawSetChangesForTesting(
                MapRenderTrace.RenderSurface.PolylineForwardArc, prevArc, curArc, ut);

            // -- ImguiLabeledMarker / AtmosphericMarker: Tier-B MarkerDecision via the REAL signature builder --
            // EmitMarkerDecisionOnChange is keyed per recordingId, carried in the prefix pid= slot.
            string flightSig = MapRenderTrace.BuildMarkerDecisionSignature(
                recordingIndex: 0, vesselName: "TracerMatrixGhost",
                directorTracedPathActive: false, polylineOwning: true, iconSuppressed: false,
                shouldDrawNonProto: true, MapRenderTrace.MarkerOutcome.DrawnNonProto,
                MapRenderTrace.MarkerRideReason.RodeLeg, legIndex: 1, posSource: "polyline");
            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, recId, ut, flightSig);

            string tsSig = MapRenderTrace.BuildMarkerDecisionSignature(
                recordingIndex: 0, vesselName: "TracerMatrixGhost",
                directorTracedPathActive: false, polylineOwning: true, iconSuppressed: false,
                shouldDrawNonProto: true, MapRenderTrace.MarkerOutcome.DrawnNonProto,
                MapRenderTrace.MarkerRideReason.RodeLeg, legIndex: 1, posSource: "polyline",
                tsSkipReason: "in-atmosphere");
            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.AtmosphericMarker, recId + "-ts", ut, tsSig);

            // -- flight-scene mesh: MeshSpawned / MeshDestroyed (the GhostRenderTrace sibling) --
            // important=true so it emits regardless of an open detailed window (matching EmitMeshLifecycleTrace).
            GhostRenderTrace.EmitPhase(
                recId, ghostIndex: 0, ut, ut, "MeshSpawned",
                "vessel=TracerMatrixGhost reason=ghost-created", important: true);
            GhostRenderTrace.EmitPhase(
                recId, ghostIndex: 0, ut, ut, "MeshDestroyed",
                "vessel=TracerMatrixGhost reason=retire", important: true);
        }

        // A minimal but REAL single-conic PhaseChain so the Phase-3 PhaseChainAssembled emit summarizes a
        // genuine chain (phases / window / kinds / provenance / seams), not a hand-faked detail string.
        private static PhaseChain BuildTracerMatrixChain(string recId, double ut)
        {
            var rec = new Recording
            {
                RecordingId = recId,
                VesselName = "TracerMatrixGhost",
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = "Kerbin",
                ExplicitStartUT = ut,
                ExplicitEndUT = ut + 1000.0,
                PlaybackEnabled = true,
            };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = ut, endUT = ut + 1000.0, bodyName = "Kerbin",
                semiMajorAxis = 700000.0, eccentricity = 0.01, epoch = ut,
            });
            rec.TrackSections.Add(new TrackSection
            {
                startUT = ut, endUT = ut + 1000.0, environment = SegmentEnvironment.ExoBallistic,
            });
            rec.MarkFilesDirty();

            return PhaseFactory.BuildPhaseChain(
                rec, committedIndex: 0, instanceKey: 0,
                windowStartUT: ut, windowEndUT: ut + 1000.0);
        }

        // ---- Matrix assertion helpers (each names the gap precisely) ----

        private static void AssertHasMapLine(
            List<string> lines, string phaseToken, string surfaceToken, string message)
        {
            foreach (string l in lines)
                if (l.Contains("[MapRenderTrace]")
                    && l.Contains("phase=" + phaseToken)
                    && l.Contains("surface=" + surfaceToken))
                    return;
            InGameAssert.Fail(message);
        }

        // GhostCreated / GhostDestroyed / FirstPosition / PhaseChainAssembled / DescentStitched are emitted
        // through EmitStructural, so the phase token IS the event name (no separate reason=). Same matcher as
        // AssertHasMapLine, kept distinct for readability of the matrix.
        private static void AssertHasMapPhase(
            List<string> lines, string phaseToken, string surfaceToken, string message)
            => AssertHasMapLine(lines, phaseToken, surfaceToken, message);

        // Anomalies + fail-closed-to-faithful are routed through EmitAnomaly / EmitStructural with the class in
        // reason= (anomalies) or as the phase= (fail-closed). Match on the reason token OR the phase token so
        // either routing is accepted.
        private static void AssertHasReason(
            List<string> lines, string reasonOrPhaseToken, string surfaceToken, string message)
        {
            foreach (string l in lines)
                if (l.Contains("[MapRenderTrace]")
                    && l.Contains("surface=" + surfaceToken)
                    && (l.Contains("reason=" + reasonOrPhaseToken)
                        || l.Contains("phase=" + reasonOrPhaseToken)))
                    return;
            InGameAssert.Fail(message);
        }

        private static void AssertHasMeshPhase(List<string> lines, string phaseToken, string message)
        {
            foreach (string l in lines)
                if (l.Contains("[GhostRenderTrace]") && l.Contains("phase=" + phaseToken))
                    return;
            InGameAssert.Fail(message);
        }

        private static void AssertContainsAll(List<string> lines, string message, string[] needles)
        {
            foreach (string l in lines)
            {
                bool all = true;
                foreach (string n in needles)
                    if (!l.Contains(n)) { all = false; break; }
                if (all)
                    return;
            }
            InGameAssert.Fail(message + " (needles: " + string.Join(", ", needles) + ")");
        }
    }
}
