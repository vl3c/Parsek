using System.Collections.Generic;
using System.Globalization;
using Parsek.Display;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 4a origin (migration plan section 6.6a), collapsed at Phase 5b (the flag + the legacy side-channel
    // are gone): the in-game gate that drives the REAL wired ShadowRenderDriver.RunFrame over a live
    // NON-ORBITAL (TracedPath) ghost and asserts the EXACTLY-ONE-PAINTER contract for the TracedPath
    // polyline leg: the owned-draw routing (ShadowRenderDriver.IsTracedPathOwnedThisFrame) is SOURCED
    // FROM THE INTENT (IsDirectorTracedPathActiveFromIntent - the single stamp RunFrame writes), and the
    // proto/marker consumers read the SAME selector, so exactly one painter (the owned treatment) owns
    // the leg, the Driver-direct draw stands down for it, no double-draw, no gap.
    //
    // It asserts the selector against the live RunFrame stamp - not an inlined copy - so it catches a
    // regression that desynced the owned routing from the consumers.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (drives RunFrame
    // against a live MapViewScene over a live ghost ProtoVessel). FLIGHT only; career-independent. The
    // pure selection + stamp are locked headlessly in ShadowRenderDriverTests; this exercises the
    // Unity-coupled RunFrame -> stamp -> selector path those cannot reach.
    public class TracedPathOwnedDrawInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double KerbinRadiusFallback = 600000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 4a/5b TracedPath owned-draw: RunFrame over a live non-orbital ghost "
                + "sources the owned-draw decision from the single intent stamp and agrees with the "
                + "proto/marker consumers - exactly one polyline painter, no double-draw, no gap")]
        public void TracedPathOwnedDraw_SourcedFromIntent_ExactlyOnePainter()
        {
            RunTracedPathOwnership();
        }

        // Build a live non-orbital ghost (SELF-CREATED via GhostMapPresence.CreateGhostVesselFromSource -
        // the RenderParityBaselineTest pattern - so the test needs ZERO live mission / pre-existing ghost
        // and ASSERTS instead of passing-as-skip on a cold pad launch), drive RunFrame, and assert the
        // owned-draw signal contract: the owned-draw routing (IsTracedPathOwnedThisFrame) equals the
        // intent-sourced signal the proto/marker consumers read, so exactly one painter owns the leg.
        private static void RunTracedPathOwnership()
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

                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                int frame = Time.frameCount;

                bool intentActive = ShadowRenderDriver.IsDirectorTracedPathActiveFromIntent(pid, frame);
                bool ownedRouting = ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, frame);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "TracedPathOwned: pid={0} intentActive={1} ownedRouting={2}",
                    pid, intentActive, ownedRouting));

                // DETERMINISTIC TracedPath: the self-created ghost's recording is a flat non-orbital leg
                // (no OrbitSegment) covering liveUT, so RunFrame's chain classifies its active segment at
                // liveUT as TracedPath, sets a visible-InSegment intent, and stamps the intent signal.
                // ASSERT it decided TracedPath (the prior Skip("spine did not decide TracedPath") was a
                // pass-as-skip on a cold pad launch; the self-created in-coverage leg removes that path).
                InGameAssert.IsTrue(intentActive,
                    "the self-created non-orbital ghost's leg must classify TracedPath in-coverage at liveUT "
                    + "(the intent stamp must be active) - if not, the chain mis-classified a flat "
                    + "non-orbital leg covering liveUT");

                // EXACTLY-ONE-PAINTER (Phase 5b single-source collapse): the owned-draw routing IS the
                // intent-sourced signal the proto/marker consumers read - the same selector - so the
                // owned treatment and the Driver-direct draw can never both paint the leg (no double,
                // no gap). This pins the routing against the live RunFrame stamp.
                InGameAssert.AreEqual(intentActive, ownedRouting,
                    "owned-draw routing (IsTracedPathOwnedThisFrame) must equal the intent-sourced signal "
                    + "(IsDirectorTracedPathActiveFromIntent) - exactly one painter, no double, no gap");
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("tracedpath-own-cleanup");
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
