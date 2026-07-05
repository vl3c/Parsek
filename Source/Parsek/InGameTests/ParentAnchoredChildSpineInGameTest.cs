using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 10 / B-row4 (cutover regression harness) - the PARENT-ANCHORED controlled-child spine test. A
    // controlled-decoupled child (a lander / probe / capsule off a parent through a decoupler:
    // IsDebris=false, ParentAnchorRecordingId set) renders through its own body-relative arc while close to
    // its parent.
    //
    // HONEST SCOPE (S11): AnchorFrameResolver.ResolveParentAnchoredChild is DEFINE-ONLY in v1 - it has ZERO
    // production callers (the Phase-5b re-scope retained the walk fence for the parent-anchored
    // population; a later phase wires the spine's routing through it). So arm (a)
    // exercises the defined-but-unwired resolver's decision matrix (a contract pin for the future wiring, not
    // proof the spine routes through it today), and arm (b) is a CRASH-SMOKE + PLUMBING check (a
    // parent-anchored child ghost creates, RunFrame runs over it without throwing, and the faithful oracle
    // reads the arc the ghost was created from) - NOT spine-decision coverage of parent-anchored routing.
    //
    //  (a) THE DUAL-SURFACE ROUTING DECISION (the define-only ResolveParentAnchoredChild): against a
    //      LIVE-body-scaled UT range,
    //        - >= 2 body-fixed samples AND the UT in-range  -> BodyFixedPrimary (render via bodyFixedFrames),
    //        - the body-fixed primary unusable but an anchor-local frames surface covers the UT (loop-anchored
    //          chain fallback)                               -> AnchorLocalSecondary,
    //        - the UT out-of-range / too few samples         -> RETIRE (never clamp to a stale child offset -
    //          the documented "stale ghost" bug it prevents).
    //      The pure decision matrix is locked headlessly in AnchorFrameTests; here we exercise it against a
    //      LIVE-body UT scale (the same body the rendered child arc is framed through) so the contract the
    //      future wiring will consume holds on real clock magnitudes, not just synthetic small numbers.
    //
    //  (b) CRASH-SMOKE + PLUMBING: a live parent-anchored child ghost on a body-relative orbit arc, driven
    //      through the REAL wired RunFrame with the spine ON, reports ZERO faithful parity-drift. The oracle
    //      arm compares the ghost against the SAME segment it was created from (green-by-construction for
    //      the routing question), so it proves the parent-anchored recording shape flows through
    //      creation/RunFrame/oracle without throwing or skipping - not that the spine made a
    //      parent-anchored routing decision.
    //
    // ARCHITECTURAL TRUTH respected: it does NOT assert any geometry change or routing wiring that only
    // a future wiring phase delivers.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (builds a live child
    // ghost ProtoVessel, reads its OrbitDriver, drives RunFrame, scales the dual-surface UT range off the live
    // body). FLIGHT only; career-independent.
    public class ParentAnchoredChildSpineInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double Sma = 750000.0;   // a body-relative parking arc around the parent's body
        private const double Ecc = 0.0;
        private const double Inc = 6.0;
        private const double KerbinRadiusFallback = 600000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 10 B-row4 parent-anchored child: the DEFINE-ONLY dual-surface resolver "
                + "(zero production callers; the 5b re-scope kept the walk fence, so the wiring is a later phase) decides correctly (>=2-sample in-range -> "
                + "body-fixed primary; out-of-range -> RETIRE, never clamp) on a live-body UT scale, plus a "
                + "crash-smoke/plumbing pass of a controlled-decoupled child ghost through RunFrame (spine "
                + "ON) with the faithful oracle green - NOT spine parent-anchored routing coverage")]
        public void ParentAnchoredChild_FlagOn_DualSurfaceRoutingCorrect_OracleGreen()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double startUT = liveUT - 1800.0;
            OrbitSegment seg = BuildSegment(startUT);


            bool prevForceTrace = MapRenderTrace.ForceEnabledForTesting;
            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            Recording rec = BuildControlledChildRecording(startUT, seg);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("parent-child-start");
            ShadowRenderDriver.Reset();

            uint pid = 0u;
            try
            {
                MapRenderTrace.ForceEnabledForTesting = true;

                // --- (a) THE DUAL-SURFACE ROUTING DECISION (the DEFINE-ONLY resolver, live-body UT scale) ---
                // ResolveParentAnchoredChild has zero production callers (the 5b re-scope retained the walk
                // fence instead of wiring it; a later phase consumes it); this pins the
                // contract the future wiring will consume. Model a body-fixed window [winStart, winEnd] around
                // the live drive clock. The body-fixed primary needs >=2 samples AND the UT inside the
                // range. Use the live UT magnitudes so the decision holds on real clock scales, not just
                // synthetic small numbers.
                double winStart = liveUT - 300.0;
                double winEnd = liveUT + 300.0;
                double loopFramesStart = liveUT - 1000.0;   // a wider loop-anchored frames window
                double loopFramesEnd = liveUT + 1000.0;

                // IN-RANGE, >=2 samples -> BodyFixedPrimary.
                AnchorFrameResolver.ParentChildSurface inRange =
                    AnchorFrameResolver.ResolveParentAnchoredChild(
                        liveUT, bodyFixedSampleCount: 3, bodyFixedStartUt: winStart, bodyFixedEndUt: winEnd,
                        hasAnchorLocalFrames: true, anchorLocalStartUt: loopFramesStart,
                        anchorLocalEndUt: loopFramesEnd);
                InGameAssert.AreEqual(AnchorFrameResolver.ParentChildSurface.BodyFixedPrimary, inRange,
                    "the DEFINE-ONLY resolver (still unwired post-5b): a parent-anchored child with >=2 body-fixed "
                    + "samples and the UT in-range must route to the body-fixed PRIMARY surface (the contract "
                    + "the future wiring consumes)");

                // OUT-OF-RANGE (past the body-fixed window) with NO loop-frames cover -> RETIRE (never clamp).
                double pastEndUT = winEnd + 5000.0;
                AnchorFrameResolver.ParentChildSurface outOfRange =
                    AnchorFrameResolver.ResolveParentAnchoredChild(
                        pastEndUT, bodyFixedSampleCount: 3, bodyFixedStartUt: winStart, bodyFixedEndUt: winEnd,
                        hasAnchorLocalFrames: false, anchorLocalStartUt: double.NaN,
                        anchorLocalEndUt: double.NaN);
                InGameAssert.AreEqual(AnchorFrameResolver.ParentChildSurface.Retire, outOfRange,
                    "a parent-anchored child whose UT is past the body-fixed window with no loop-frames cover "
                    + "must RETIRE - never clamp to a stale child offset (the documented stale-ghost bug)");

                // TOO FEW samples (1) but a loop-anchored frames surface covers the UT -> AnchorLocalSecondary.
                AnchorFrameResolver.ParentChildSurface secondary =
                    AnchorFrameResolver.ResolveParentAnchoredChild(
                        liveUT, bodyFixedSampleCount: 1, bodyFixedStartUt: winStart, bodyFixedEndUt: winEnd,
                        hasAnchorLocalFrames: true, anchorLocalStartUt: loopFramesStart,
                        anchorLocalEndUt: loopFramesEnd);
                InGameAssert.AreEqual(AnchorFrameResolver.ParentChildSurface.AnchorLocalSecondary, secondary,
                    "a parent-anchored child with too few body-fixed samples but a covering loop-anchored "
                    + "frames surface must fall back to the anchor-local SECONDARY surface");

                // --- (b) CRASH-SMOKE + PLUMBING: a live child ghost on its body-relative arc, RunFrame
                // spine ON. Green-by-construction for the routing question (the oracle compares the ghost
                // against the segment it was created from); it proves the parent-anchored recording shape
                // flows through creation/RunFrame/oracle without throwing or skipping - NOT that the spine
                // made a parent-anchored routing decision (a later phase wires that). ---
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex, rec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg, default(TrajectoryPoint), startUT, loopEpochShiftSeconds: 0.0);

                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("Parent-anchored child ghost did not create in this context (no proto)");
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

                // Drive the REAL wired RunFrame with the spine ON over the live child ghost.
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);

                Orbit renderedOrbit = ghost.orbitDriver.orbit;
                Vector3d iconBodyRel = ghost.GetWorldPos3D() - kerbin.position;
                if (iconBodyRel.magnitude < 1.0)
                {
                    InGameAssert.Skip("Child ghost world position not resolved on the creation frame");
                    return;
                }

                MapRenderProbe.FaithfulParitySample faithful = MapRenderProbe.ComputeFaithfulOrbitParity(
                    renderedOrbit, kerbin, 0.0, liveUT, rec.RecordingId);
                InGameAssert.IsTrue(faithful.Sampled,
                    "crash-smoke/plumbing arm: the parent-anchored recording shape must flow through the "
                    + "faithful oracle (SAMPLE, not skip - a skip means the shape broke the plumbing); "
                    + "skipReason=" + (faithful.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(faithful.Result.HasMeasurement,
                    "crash-smoke/plumbing arm: the controlled-child faithful oracle must yield a measurement");
                InGameAssert.IsFalse(faithful.Result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "crash-smoke/plumbing arm (green-by-construction for routing: the ghost is compared "
                        + "against the segment it was created from): a controlled-decoupled child driven "
                        + "spine-ON must report ZERO faithful drift; maxDev={0:F1}m tol={1:F1}m",
                        faithful.Result.MaxDeviationMeters, faithful.Result.ToleranceMeters));

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "ParentAnchoredChild_Spine: pid={0} isDebris={1} parentAnchor={2} | routing inRange={3} "
                    + "outOfRange={4} secondary={5} | faithfulDev={6:F1}m tol={7:F1}m",
                    pid, rec.IsDebris, rec.ParentAnchorRecordingId ?? "(null)", inRange, outOfRange, secondary,
                    faithful.Result.MaxDeviationMeters, faithful.Result.ToleranceMeters));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("parent-child-cleanup");
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

        private static OrbitSegment BuildSegment(double startUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = startUT + 3600.0,
                inclination = Inc,
                eccentricity = Ecc,
                semiMajorAxis = Sma,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                bodyName = KerbinBodyName,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            };
        }

        // A CONTROLLED-DECOUPLED CHILD recording: IsDebris=false, ParentAnchorRecordingId set (a lander /
        // probe off a parent through a decoupler). It records its own body-relative orbit arc; the rendered
        // geometry under test is the OrbitSegment elements.
        private static Recording BuildControlledChildRecording(double startUT, OrbitSegment seg)
        {
            double endUT = startUT + 3600.0;
            var rec = new Recording
            {
                RecordingId = "parent-child-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Controlled Child",
                IsDebris = false,                                  // controlled-decoupled, NOT debris
                ParentAnchorRecordingId = "parent-" + System.Guid.NewGuid().ToString("N"),
                TerminalStateValue = null,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = KerbinBodyName,
                TerminalOrbitBody = KerbinBodyName,
                TerminalOrbitSemiMajorAxis = Sma,
                TerminalOrbitEccentricity = Ecc,
                TerminalOrbitInclination = Inc,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.0,
                TerminalOrbitEpoch = startUT,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                PlaybackEnabled = true,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0.0, longitude = 0.0, altitude = Sma - KerbinRadiusFallback,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 2400f, 0f), bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0.0, longitude = 5.0, altitude = Sma - KerbinRadiusFallback,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 2400f, 0f), bodyName = KerbinBodyName,
            });
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }
    }
}
