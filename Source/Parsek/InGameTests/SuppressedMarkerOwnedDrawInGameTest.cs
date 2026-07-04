using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 4b (migration plan §6.6b - re-home the IMGUI marker draw to the spine intent): the in-game
    // gate that drives the REAL wired ShadowRenderDriver.RunFrame over a live BELOW-ATMOSPHERE / no-bounds
    // (non-orbital) ghost with the typed PhaseChain spine ON vs OFF and asserts the marker-draw contract
    // both marker call sites route through (GhostMapPresence.ShouldDrawNonProtoMarkerForGhost):
    //
    //  - NEVER A BLANK ICON: a below-atmosphere / no-bounds ghost (no faithful conic to seed) must draw
    //    OUR non-proto marker - the marker decision is true - in BOTH flag states. The icon floor
    //    (ghostsWithSuppressedIcon / IsIconSuppressed) is the KEPT no-conic fallback and is untouched here;
    //    this test proves re-sourcing the TracedPath disjunct did not open a blank-icon gap.
    //  - NO DOUBLE-MARKER, NO GAP: the marker decision's TracedPath disjunct source
    //    (IsTracedPathOwnedThisFrame, the flag-aware selector 4a routes the polyline Driver on) must AGREE
    //    with the proto/marker consumer signal in GhostOrbitLinePatch (IsDirectorTracedPathActive) for the
    //    ghost pid. Because RunFrame stamps the legacy side-channel (tracedPathByPid) and the intent sibling
    //    (tracedPathIntentByPid) from the SAME intent in the SAME pass, the marker site agrees with the
    //    proto-line consumer in BOTH flag states - exactly one of {proto icon, our marker} per ghost.
    //  - FLAG ON sources the disjunct from the intent (IsDirectorTracedPathActiveFromIntent); FLAG OFF
    //    falls through to the legacy side-channel - byte-identical to today.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (drives RunFrame
    // against a live MapViewScene over a live ghost ProtoVessel + reads the Unity-coupled marker wrapper).
    // FLIGHT only; career-independent. The pure flag-aware selection + the pure marker decision are locked
    // headlessly in ShadowRenderDriverTests + MarkerDrawDecisionTests; this exercises the Unity-coupled
    // RunFrame -> stamp -> marker-wrapper path those cannot reach.
    public class SuppressedMarkerOwnedDrawInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double KerbinRadiusFallback = 600000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 4b marker draw (FLAG ON): RunFrame over a live below-atmosphere ghost draws "
                + "our non-proto marker (never a blank icon), sources the TracedPath disjunct from the intent, "
                + "and stays in sync with the proto-line consumer - no double-marker, no gap")]
        public void MarkerDraw_FlagOn_SourcedFromIntent_NeverBlankNoDouble()
        {
            RunMarkerOwnership(forceSpineOn: true);
        }

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 4b marker draw (FLAG OFF): RunFrame over a live below-atmosphere ghost draws "
                + "our non-proto marker on the legacy side-channel - byte-identical to today")]
        public void MarkerDraw_FlagOff_LegacySideChannel_Unchanged()
        {
            RunMarkerOwnership(forceSpineOn: false);
        }

        // Build a live below-atmosphere (non-orbital) ghost, drive RunFrame with the spine forced the
        // requested way, and assert the marker-draw contract. Shared invariants in BOTH flag states:
        //  (1) the marker decision (ShouldDrawNonProtoMarkerForGhost) draws our marker - never a blank icon;
        //  (2) the marker's TracedPath disjunct source (IsTracedPathOwnedThisFrame) equals the proto-line
        //      consumer signal (IsDirectorTracedPathActive) - exactly one painter, no double, no gap.
        // Additionally: flag-OFF reads the legacy side-channel (intent stamp absent off the flag); flag-ON
        // reads the intent source.
        private static void RunMarkerOwnership(bool forceSpineOn)
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double startUT = liveUT - 60.0;
            double endUT = liveUT + 60.0;

            bool prevForceSpine = ShadowRenderDriver.ForceSpineDriveForTesting;
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

                uint resolvedPid = GhostMapPresence.GetGhostVesselPidForRecording(recordingIndex);
                Vessel ghost = null;
                if (resolvedPid != 0u)
                    FlightGlobals.FindVessel(resolvedPid, out ghost);
                if (ghost == null)
                {
                    InGameAssert.Skip("non-orbital recording produced no ghost proto in this context");
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

                ShadowRenderDriver.ForceSpineDriveForTesting = forceSpineOn;
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                int frame = Time.frameCount;

                bool legacyActive = ShadowRenderDriver.IsDirectorTracedPathActive(pid, frame);
                bool intentActive = ShadowRenderDriver.IsDirectorTracedPathActiveFromIntent(pid, frame);
                bool ownedRouting = ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, frame);

                // The full marker decision (the SAME wrapper both marker call sites route through).
                bool shouldDraw = GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(
                    pid, out bool decDirectorTraced, out bool decPolylineOwning, out bool decIconSuppressed);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "SuppressedMarkerOwned: forceSpineOn={0} pid={1} legacyActive={2} intentActive={3} "
                    + "ownedRouting={4} shouldDraw={5} decDirectorTraced={6} decPolylineOwning={7} "
                    + "decIconSuppressed={8}",
                    forceSpineOn, pid, legacyActive, intentActive, ownedRouting, shouldDraw,
                    decDirectorTraced, decPolylineOwning, decIconSuppressed));

                // The marker site's TracedPath disjunct (decDirectorTraced) IS the flag-aware selector
                // output - the Phase-4b re-home. Assert that wiring directly.
                InGameAssert.AreEqual(ownedRouting, decDirectorTraced,
                    "the marker site's directorTracedPathActive disjunct must be the flag-aware selector "
                    + "(IsTracedPathOwnedThisFrame) output - the Phase-4b re-home");

                // EXACTLY-ONE-PAINTER / NO-GAP across the marker<->proto-line boundary: the marker's
                // TracedPath disjunct source must AGREE with the proto-line consumer signal
                // (IsDirectorTracedPathActive). True in BOTH flag states because the two stamps come from the
                // same intent in the same pass. A disagreement would either double-draw (marker + proto) or
                // gap (neither).
                InGameAssert.AreEqual(legacyActive, ownedRouting,
                    "marker TracedPath disjunct source (IsTracedPathOwnedThisFrame) must agree with the "
                    + "proto-line consumer signal (IsDirectorTracedPathActive) - no double-marker, no gap");

                // NEVER A BLANK ICON: a below-atmosphere / no-bounds ghost has no faithful conic to seed, so
                // the marker decision MUST draw our non-proto marker. The decision is a SUPERSET (TracedPath
                // OR polyline-owns OR the kept icon floor), so as long as ANY of those is set the marker
                // draws. We assert the decision is true; if it were false the ghost would show a blank icon.
                InGameAssert.IsTrue(shouldDraw,
                    "a below-atmosphere / no-bounds ghost must draw our non-proto marker (never a blank "
                    + "icon) - the marker decision is the dual of 'proto icon hidden'");

                if (forceSpineOn)
                {
                    // FLAG ON: the marker's TracedPath disjunct is SOURCED FROM THE INTENT. When the spine
                    // decided TracedPath this frame (legacyActive), the intent stamp is present and the
                    // disjunct equals it.
                    if (legacyActive)
                    {
                        InGameAssert.IsTrue(intentActive,
                            "FLAG ON: the intent-sourced stamp must be present (RunFrame stamps it from the "
                            + "same intent as the legacy side-channel)");
                        InGameAssert.AreEqual(intentActive, decDirectorTraced,
                            "FLAG ON: the marker TracedPath disjunct must equal the intent-sourced signal "
                            + "(re-homed)");
                    }
                }
                else
                {
                    // FLAG OFF: the intent stamp is NEVER written, so the marker disjunct reads the legacy
                    // side-channel - byte-identical to today.
                    InGameAssert.IsFalse(intentActive,
                        "FLAG OFF: the intent-sourced stamp must NOT be written (flag-gated), so the marker "
                        + "disjunct falls through to the legacy side-channel - byte-identical to today");
                    InGameAssert.AreEqual(legacyActive, decDirectorTraced,
                        "FLAG OFF: the marker TracedPath disjunct must equal the legacy side-channel signal");
                }
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("suppressedmarker-own-cleanup");
                ShadowRenderDriver.ForceSpineDriveForTesting = prevForceSpine;
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
            double radius = kerbin != null ? kerbin.Radius : KerbinRadiusFallback;
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
