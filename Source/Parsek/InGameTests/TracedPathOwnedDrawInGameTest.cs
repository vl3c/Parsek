using System.Collections.Generic;
using System.Globalization;
using Parsek.Display;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 4a (migration plan §6.6a - re-home the TracedPath polyline draw to the intent): the in-game
    // gate that drives the REAL wired ShadowRenderDriver.RunFrame over a live NON-ORBITAL (TracedPath)
    // ghost with the typed PhaseChain spine ON vs OFF and asserts the EXACTLY-ONE-PAINTER contract for the
    // TracedPath polyline leg:
    //
    //  - FLAG ON (ForceSpineDriveForTesting): the owned-draw routing
    //    (ShadowRenderDriver.IsTracedPathOwnedThisFrame) is SOURCED FROM THE INTENT
    //    (IsDirectorTracedPathActiveFromIntent). Because RunFrame stamps the legacy side-channel
    //    (tracedPathByPid) and the intent sibling (tracedPathIntentByPid) from the SAME intent in the SAME
    //    pass, the owned-draw routing AGREES with the proto/marker consumers (which read
    //    IsDirectorTracedPathActive): exactly one painter (the owned treatment), the autonomous Driver-
    //    direct draw stands down for that leg, no double-draw, no gap.
    //  - FLAG OFF (default): the owned-draw routing returns the LEGACY side-channel
    //    (IsDirectorTracedPathActive) - BYTE-IDENTICAL to today; the intent stamp is never written, so the
    //    routing is the prior-rewrite behavior unchanged.
    //
    // It asserts the FLAG-AWARE selector against the live RunFrame stamps - not an inlined copy - so it
    // catches a regression that hard-wired either source or desynced the owned routing from the consumers.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (drives RunFrame
    // against a live MapViewScene over a live ghost ProtoVessel). FLIGHT only; career-independent. The pure
    // flag-aware selection + the flag-gated stamp are locked headlessly in ShadowRenderDriverTests; this
    // exercises the Unity-coupled RunFrame -> stamp -> selector path those cannot reach.
    public class TracedPathOwnedDrawInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double KerbinRadiusFallback = 600000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 4a TracedPath owned-draw (FLAG ON): RunFrame over a live non-orbital ghost "
                + "sources the owned-draw decision from the intent and agrees with the proto/marker consumers "
                + "- exactly one polyline painter, no double-draw, no gap")]
        public void TracedPathOwnedDraw_FlagOn_SourcedFromIntent_ExactlyOnePainter()
        {
            RunTracedPathOwnership(forceSpineOn: true);
        }

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 4a TracedPath owned-draw (FLAG OFF): RunFrame over a live non-orbital ghost "
                + "routes the owned-draw decision on the legacy side-channel - byte-identical to today")]
        public void TracedPathOwnedDraw_FlagOff_LegacySideChannel_Unchanged()
        {
            RunTracedPathOwnership(forceSpineOn: false);
        }

        // Build a live non-orbital ghost (SELF-CREATED via GhostMapPresence.CreateGhostVesselFromSource -
        // the RenderParityBaselineTest pattern - so the test needs ZERO live mission / pre-existing ghost
        // and ASSERTS instead of passing-as-skip on a cold pad launch), drive RunFrame with the spine
        // forced the requested way, and assert the flag-aware owned-draw signal contract. The shared
        // invariant in BOTH flag states: the owned-draw routing (IsTracedPathOwnedThisFrame) equals the
        // proto/marker consumer signal (IsDirectorTracedPathActive) for the ghost pid, so exactly one
        // painter owns the leg (no double, no gap). Additionally: flag-OFF must read the legacy
        // side-channel (intent stamp absent off the flag); flag-ON must read the intent source.
        private static void RunTracedPathOwnership(bool forceSpineOn)
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            // Author the non-orbital TracedPath leg AROUND liveUT so the leg deterministically covers liveUT
            // and the chain's active segment at liveUT classifies TracedPath + in-coverage - the leg ASSERTS
            // instead of skipping.
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
            GhostMapPresence.RemoveAllGhostVessels("tracedpath-own-start");
            ShadowRenderDriver.Reset();

            uint pid = 0u;
            try
            {
                MapRenderTrace.ForceEnabledForTesting = true; // so the spine + intent path are live

                // SELF-CREATE the ghost from the recording's own first body-relative state vector (the
                // RenderParityBaselineTest.cs:134-141 pattern). A non-orbital leg has no conic to seed, so
                // create through the StateVector source (a flat low-altitude TrajectoryPoint), which
                // registers the pid in GhostMapPresence.ghostMapVesselPids + the pid->recording maps the
                // MapViewScene resolves through. This makes the test SELF-CONTAINED (no live mission /
                // pre-existing ghost) and DETERMINISTIC (the leg classifies TracedPath + in-coverage at
                // liveUT), so it ASSERTS rather than skipping on a cold pad launch.
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
                    // the TracedPath decision: it means no ghost could be created AT ALL in this context.
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

                ShadowRenderDriver.ForceSpineDriveForTesting = forceSpineOn;
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                int frame = Time.frameCount;

                bool legacyActive = ShadowRenderDriver.IsDirectorTracedPathActive(pid, frame);
                bool intentActive = ShadowRenderDriver.IsDirectorTracedPathActiveFromIntent(pid, frame);
                bool ownedRouting = ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, frame);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "TracedPathOwned: forceSpineOn={0} pid={1} legacyActive={2} intentActive={3} "
                    + "ownedRouting={4}",
                    forceSpineOn, pid, legacyActive, intentActive, ownedRouting));

                // DETERMINISTIC TracedPath: the self-created ghost's recording is a flat non-orbital leg
                // (no OrbitSegment) covering liveUT, so RunFrame's chain classifies its active segment at
                // liveUT as TracedPath, sets a visible-InSegment intent, and stamps the legacy side-channel.
                // ASSERT it decided TracedPath (the prior Skip("spine did not decide TracedPath") was a
                // pass-as-skip on a cold pad launch; the self-created in-coverage leg removes that path).
                InGameAssert.IsTrue(legacyActive,
                    "the self-created non-orbital ghost's leg must classify TracedPath in-coverage at liveUT "
                    + "(the legacy side-channel must be active) - if not, the chain mis-classified a flat "
                    + "non-orbital leg covering liveUT");

                // EXACTLY-ONE-PAINTER: the owned-draw routing must AGREE with the proto/marker consumers
                // (which read IsDirectorTracedPathActive). If they disagreed, either the owned treatment AND
                // the autonomous direct draw would both run (double-draw), or neither would (gap). True in
                // BOTH flag states because the two stamps come from the same intent in the same pass.
                InGameAssert.AreEqual(legacyActive, ownedRouting,
                    "owned-draw routing (IsTracedPathOwnedThisFrame) must agree with the proto/marker "
                    + "consumer signal (IsDirectorTracedPathActive) - exactly one painter, no double, no gap");

                if (forceSpineOn)
                {
                    // FLAG ON: the routing is SOURCED FROM THE INTENT, and the intent stamp is present (it
                    // matches the legacy stamp by construction).
                    InGameAssert.IsTrue(intentActive,
                        "FLAG ON: the intent-sourced stamp must be present (RunFrame stamps it from the same "
                        + "intent as the legacy side-channel)");
                    InGameAssert.AreEqual(intentActive, ownedRouting,
                        "FLAG ON: the owned-draw routing must equal the intent-sourced signal (re-homed)");
                }
                else
                {
                    // FLAG OFF: the intent stamp is NEVER written, so the routing reads the legacy side-
                    // channel - byte-identical to today.
                    InGameAssert.IsFalse(intentActive,
                        "FLAG OFF: the intent-sourced stamp must NOT be written (flag-gated), so the routing "
                        + "falls through to the legacy side-channel - byte-identical to today");
                    InGameAssert.AreEqual(legacyActive, ownedRouting,
                        "FLAG OFF: the owned-draw routing must equal the legacy side-channel signal");
                }
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("tracedpath-own-cleanup");
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

        // A short below-orbit (atmospheric / non-orbital) recording: flat Points only, NO OrbitSegment, so
        // the spine classifies it as a TracedPath leg rather than a StockConic.
        private static Recording BuildNonOrbitalRecording(double startUT, double endUT, CelestialBody kerbin)
        {
            double radius = kerbin != null ? kerbin.Radius : KerbinRadiusFallback;
            var rec = new Recording
            {
                RecordingId = "tracedpath-own-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek TracedPath Own",
                EndpointPhase = RecordingEndpointPhase.SurfacePosition,
                EndpointBodyName = KerbinBodyName,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                PlaybackEnabled = true,
            };
            // Two low-altitude points (an ascent stretch); no OrbitSegment -> non-orbital TracedPath leg.
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
