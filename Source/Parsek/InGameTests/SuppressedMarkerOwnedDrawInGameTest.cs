using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 4b origin (migration plan section 6.6b), collapsed at Phase 5b (the flag + the legacy side-channel
    // are gone): the in-game gate that drives the REAL wired ShadowRenderDriver.RunFrame over a live
    // BELOW-ATMOSPHERE / no-bounds (non-orbital) ghost and asserts the marker-draw contract both marker
    // call sites route through (GhostMapPresence.ShouldDrawNonProtoMarkerForGhost):
    //
    //  - NEVER A BLANK ICON: a below-atmosphere / no-bounds ghost (no faithful conic to seed) must draw
    //    OUR non-proto marker - the marker decision is true. The icon floor (ghostsWithSuppressedIcon /
    //    IsIconSuppressed) is the KEPT no-conic fallback and is untouched here.
    //  - NO DOUBLE-MARKER, NO GAP: the marker decision's TracedPath disjunct and the proto-line
    //    suppress in GhostOrbitLinePatch read the SAME single intent-sourced selector
    //    (IsTracedPathOwnedThisFrame == IsDirectorTracedPathActiveFromIntent), so exactly one of
    //    {proto icon, our marker} paints per ghost.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (drives RunFrame
    // against a live MapViewScene over a live ghost ProtoVessel + reads the Unity-coupled marker wrapper).
    // FLIGHT only; career-independent. The pure selection + the pure marker decision are locked
    // headlessly in ShadowRenderDriverTests + MarkerDrawDecisionTests; this exercises the Unity-coupled
    // RunFrame -> stamp -> marker-wrapper path those cannot reach.
    public class SuppressedMarkerOwnedDrawInGameTest
    {
        private const string KerbinBodyName = "Kerbin";

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 4b/5b marker draw: RunFrame over a live below-atmosphere ghost draws our "
                + "non-proto marker (never a blank icon), sources the TracedPath disjunct from the single "
                + "intent stamp, and stays in sync with the proto-line consumer - no double-marker, no gap")]
        public void MarkerDraw_SourcedFromIntent_NeverBlankNoDouble()
        {
            RunMarkerOwnership();
        }

        // Build a live below-atmosphere (non-orbital) ghost (SELF-CREATED via
        // GhostMapPresence.CreateGhostVesselFromSource - the RenderParityBaselineTest pattern - so the test
        // needs ZERO live mission / pre-existing ghost and ASSERTS instead of passing-as-skip on a cold pad
        // launch), drive RunFrame, and assert the marker-draw contract:
        //  (1) the marker decision (ShouldDrawNonProtoMarkerForGhost) draws our marker - never a blank icon;
        //  (2) the marker's TracedPath disjunct source (IsTracedPathOwnedThisFrame) equals the single
        //      intent-sourced signal the proto-line consumer reads - exactly one painter, no double, no gap.
        private static void RunMarkerOwnership()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            // Author the below-atmosphere TracedPath leg AROUND liveUT so the leg deterministically covers
            // liveUT and the chain's active segment at liveUT classifies TracedPath + in-coverage.
            double startUT = liveUT - 60.0;
            double endUT = liveUT + 60.0;

            bool prevForceTrace = MapRenderTrace.ForceEnabledForTesting;
            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            Recording rec = BuildNonOrbitalRecording(startUT, endUT, kerbin);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("suppressedmarker-own-start");
            ShadowRenderDriver.Reset();

            uint pid = 0u;
            try
            {
                MapRenderTrace.ForceEnabledForTesting = true; // so the spine + intent path are live

                // SELF-CREATE the ghost from the recording's own first body-relative state vector (the
                // RenderParityBaselineTest.cs:134-141 pattern). A below-atmosphere / no-bounds leg has no
                // conic to seed, so create through the StateVector source (a flat low-altitude
                // TrajectoryPoint), which registers the pid in GhostMapPresence.ghostMapVesselPids + the
                // pid->recording maps the MapViewScene + the marker decision resolve through. SELF-CONTAINED
                // (no live mission / pre-existing ghost) + DETERMINISTIC (the leg classifies TracedPath +
                // in-coverage at liveUT), so the marker-draw + ownership ASSERT rather than skip on a cold
                // pad launch.
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex,
                    rec,
                    GhostMapPresence.TrackingStationGhostSource.StateVector,
                    default(OrbitSegment),
                    rec.Points[0],
                    liveUT);
                if (ghost == null)
                {
                    // A ghost-create miss here is environmental (the state-vector create resolves the body
                    // by name and needs a live PartLoader/Vessel context). Keep this a Skip - the body-
                    // not-found guard's sibling - rather than a false assert. It is NOT a pass-as-skip on
                    // the marker decision: it means no ghost could be created AT ALL in this context.
                    InGameAssert.Skip("ghost ProtoVessel did not create in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;

                var scene = new MapViewScene();
                scene.SetFrameInputs(GhostPlaybackLogic.LoopUnitSet.Empty, liveUT);
                if (!scene.IsActive)
                {
                    InGameAssert.Skip("MapViewScene not active (not in FLIGHT)");
                    return;
                }

                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                int frame = Time.frameCount;

                bool intentActive = ShadowRenderDriver.IsDirectorTracedPathActiveFromIntent(pid, frame);
                bool ownedRouting = ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, frame);

                // The full marker decision (the SAME wrapper both marker call sites route through).
                bool shouldDraw = GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(
                    pid, out bool decDirectorTraced, out bool decPolylineOwning, out bool decIconSuppressed);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "SuppressedMarkerOwned: pid={0} intentActive={1} "
                    + "ownedRouting={2} shouldDraw={3} decDirectorTraced={4} decPolylineOwning={5} "
                    + "decIconSuppressed={6}",
                    pid, intentActive, ownedRouting, shouldDraw,
                    decDirectorTraced, decPolylineOwning, decIconSuppressed));

                // DETERMINISTIC TracedPath: the self-created ghost's recording is a flat below-atmosphere
                // leg (no OrbitSegment) covering liveUT, so RunFrame's chain classifies its active segment
                // at liveUT as TracedPath and stamps the intent signal. ASSERT it (no pass-as-skip on
                // a cold pad launch - the self-created in-coverage leg makes this deterministic).
                InGameAssert.IsTrue(intentActive,
                    "the self-created below-atmosphere ghost's leg must classify TracedPath in-coverage at "
                    + "liveUT (the intent stamp must be active)");

                // The marker site's TracedPath disjunct (decDirectorTraced) IS the shared selector
                // output - the Phase-4b re-home, single-source since 5b. Assert that wiring directly.
                InGameAssert.AreEqual(ownedRouting, decDirectorTraced,
                    "the marker site's directorTracedPathActive disjunct must be the shared selector "
                    + "(IsTracedPathOwnedThisFrame) output - the Phase-4b re-home");

                // EXACTLY-ONE-PAINTER / NO-GAP across the marker<->proto-line boundary (Phase 5b
                // single-source collapse): the marker's TracedPath disjunct and the proto-line consumer
                // read the SAME intent-sourced selector, so a disagreement (double-draw or gap) is
                // structurally impossible; pin the wiring against the live stamp.
                InGameAssert.AreEqual(intentActive, ownedRouting,
                    "marker TracedPath disjunct source (IsTracedPathOwnedThisFrame) must equal the "
                    + "intent-sourced signal the proto-line consumer reads - no double-marker, no gap");

                // NEVER A BLANK ICON: a below-atmosphere / no-bounds ghost has no faithful conic to seed, so
                // the marker decision MUST draw our non-proto marker. The decision is a SUPERSET (TracedPath
                // OR polyline-owns OR the kept icon floor), so as long as ANY of those is set the marker
                // draws. We assert the decision is true; if it were false the ghost would show a blank icon.
                InGameAssert.IsTrue(shouldDraw,
                    "a below-atmosphere / no-bounds ghost must draw our non-proto marker (never a blank "
                    + "icon) - the marker decision is the dual of 'proto icon hidden'");
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("suppressedmarker-own-cleanup");
                ShadowRenderDriver.Reset();
                MapRenderTrace.ForceEnabledForTesting = prevForceTrace;
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
            }
        }

        // A short below-orbit (atmospheric / non-orbital) recording: flat low-altitude Points only, NO
        // OrbitSegment, so the spine classifies it as a TracedPath leg rather than a StockConic - the
        // below-atmosphere / no-bounds case whose marker the icon floor would otherwise carry.
        private static Recording BuildNonOrbitalRecording(double startUT, double endUT, CelestialBody kerbin)
        {
            var rec = new Recording
            {
                RecordingId = "suppressedmarker-own-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek SuppressedMarker Own",
                EndpointPhase = RecordingEndpointPhase.SurfacePosition,
                EndpointBodyName = KerbinBodyName,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                PlaybackEnabled = true,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0.0, longitude = 0.0, altitude = 5000.0,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 300f, 0f), bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0.5, longitude = 0.5, altitude = 25000.0,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 800f, 0f), bodyName = KerbinBodyName,
            });
            rec.MarkFilesDirty();
            return rec;
        }
    }
}
