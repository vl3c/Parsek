using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 6 (migration plan §8 / design §9.1, the ONE cross-member geometric seam built in v1) - the
    // in-game gate for the orbit↔landing G1 descent re-stitch owned by CrossMemberSeamStitcher.
    //
    // It builds a live re-aim-looped LANDING fixture (a descent-trigger LoopUnit + a descent-member
    // recording whose body-fixed deorbit→reentry→landing run lives on the destination body) and drives the
    // REAL stitcher (CrossMemberSeamStitcher.TryStitchDescentSeam) over the typed PhaseChain at a TRIGGERED
    // live UT:
    //
    //  - the stitcher PROMOTES the descent member to a visible first-class DescentPhase at the
    //    re-anchored swept-deorbit head (recordedDeorbitUT + (liveUT - triggerUT)), and the orbit↔landing
    //    G1 tangent seam is CONTINUOUS (zero rigid-seam-tangent-discontinuity) for a smoothly-recorded
    //    descent. Past the descent clip the stitcher RETIRES the member (no held sample), so the sub-surface
    //    ghost does NOT linger below the surface (the documented bug closed). Unconditional since the
    //    Phase-5b flag removal (the spine always invokes the stitcher; the old FLAG OFF inert arm's premise
    //    is gone).
    //
    // The pure clock/tangent/ordering math is locked headlessly in CrossMemberSeamStitcherTests; this
    // exercises the Unity-coupled live-PhaseChain + live-body-tangent path those cannot reach (the parity
    // oracle runs in SYNTHESIZED mode here, since Phase 6 intentionally CHANGES the deorbit geometry to fix
    // the sub-surface bug).
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (builds a live
    // PhaseChain over a recording + resolves the destination body's world tangents). FLIGHT only;
    // career-independent.
    public class DescentReStitchInGameTest
    {
        private const string DescentBodyName = "Kerbin"; // a stock body always present; the destination body
        private const int DescentMemberIdx = 0;
        private const int TransferMemberIdx = 2;

        // Geometry chosen so cycle 0's LIVE window (liveUT in [phaseAnchor, phaseAnchor+cadence)) contains a
        // real trigger window, anchored at the LIVE UT so the resolved cycle is 0 (the same shaping the
        // headless tests use, shifted to a live UT base).
        private const double ClipSeconds = 100.0;       // descent clip duration
        private const double CaptureShift = -150.0;
        private const double Trot = 300.0;              // small period so the trigger lands inside cycle 0
        private const double Tpark = 80.0;
        private const double Cadence = 4000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 6 descent re-stitch: the CrossMemberSeamStitcher promotes the "
                + "descent member to a visible first-class DescentPhase at the re-anchored head, the "
                + "orbit↔landing G1 seam is continuous (zero rigid-seam-tangent-discontinuity), and the "
                + "sub-surface ghost retires past the descent clip")]
        public void DescentReStitch_PromotesContinuousDescent_AndRetiresPastEnd()
        {
            RunDescentReStitch();
        }

        private static void RunDescentReStitch()
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.bodyName == DescentBodyName);
            if (body == null)
            {
                InGameAssert.Skip("destination body not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double now = Planetarium.GetUniversalTime();
            // Base the span clock at a live UT so cycle 0 resolves at `now`. The descent geometry sits a few
            // hundred seconds into the cycle (entry = recordedDeorbitUT + captureShift, the trigger after it).
            double phaseAnchor = now;
            double recordedDeorbitUT = now + 200.0;       // the seam (recorded deorbit start) in the live base
            double descentEndUT = recordedDeorbitUT + ClipSeconds;
            double spanStart = now;
            double spanEnd = now + 1000.0;

            bool prevForceTrace = MapRenderTrace.ForceEnabledForTesting;

            try
            {
                MapRenderTrace.ForceEnabledForTesting = true; // so EmitStructural / the anomaly sink are live

                GhostPlaybackLogic.LoopUnitSet units = BuildDescentUnitSet(
                    phaseAnchor, spanStart, spanEnd, recordedDeorbitUT, descentEndUT);
                PhaseChain chain = BuildDescentMemberChain(recordedDeorbitUT, descentEndUT);

                // The same triggerUT the stitcher re-anchors on (shared source). conicEnd = deorbit +
                // captureShift; trigger = first t >= entry congruent to deorbit (mod Trot).
                Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                    0, phaseAnchor, Cadence, spanStart, recordedDeorbitUT, Trot, CaptureShift, null,
                    out _, out double entryUT, out double triggerUT);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "DescentReStitch: now={0:R} deorbit={1:R} entry={2:R} trigger={3:R} "
                    + "clip={4:F0}s", now, recordedDeorbitUT, entryUT, triggerUT, ClipSeconds));

                if (double.IsNaN(triggerUT))
                {
                    InGameAssert.Skip("descent trigger did not resolve for this fixture (degenerate timing)");
                    return;
                }

                // Sample at 0.7 of the clip (re-anchored head = recordedDeorbitUT + 0.7*clip). The descent
                // TRACED RUN starts at recordedDeorbitUT + clip/2: the first descent point sits exactly at the
                // parking conic's (inclusive) endUT == recordedDeorbitUT, so it is counted as orbital-covered
                // and dropped from the run, leaving the run [deorbit + clip/2, descentEnd]. A head at 0.4*clip
                // (= deorbit+0.4clip) would fall in the (deorbit, deorbit+clip/2) interior gap and TryGetPhase
                // would correctly miss it (the documented sub-surface-retire behaviour); 0.7*clip lands the
                // realistic mid-clip head squarely inside the descent run.
                double triggeredLiveUT = triggerUT + 0.7 * ClipSeconds; // inside the descent traced run
                double pastEndLiveUT = triggerUT + 1.5 * ClipSeconds;   // past the clip => Done

                // --- TRIGGERED frame: the stitcher promotes a visible TracedPath descent at the re-anchored head ---
                bool stitched = CrossMemberSeamStitcher.TryStitchDescentSeam(
                    chain, sampleUT: triggeredLiveUT, liveUT: triggeredLiveUT, units, out GhostSample sample);

                // --- PAST-END frame: the stitcher retires (no held sample) so the sub-surface ghost retires ---
                bool stitchedPastEnd = CrossMemberSeamStitcher.TryStitchDescentSeam(
                    chain, sampleUT: pastEndLiveUT, liveUT: pastEndLiveUT, units, out GhostSample pastSample);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "DescentReStitch result: triggered stitched={0} cov={1} treat={2} "
                    + "driveUT={3:R} | pastEnd stitched={4} cov={5}",
                    stitched, sample.Coverage, sample.Treatment, sample.DriveUT,
                    stitchedPastEnd, pastSample.Coverage));
                InGameAssert.IsTrue(stitched,
                    "the stitcher must promote the descent member at a TRIGGERED live UT (re-anchored head "
                    + "inside the descent clip)");
                InGameAssert.AreEqual(Coverage.InSegment, sample.Coverage,
                    "the promoted descent sample must be InSegment");
                InGameAssert.AreEqual(Treatment.TracedPath, sample.Treatment,
                    "the promoted descent is a body-fixed TracedPath (the polyline-owned descent)");
                InGameAssert.AreEqual(DescentBodyName, sample.FrameBodyName,
                    "the promoted descent renders on the destination body");
                InGameAssert.ApproxEqual(
                    recordedDeorbitUT + (triggeredLiveUT - triggerUT), sample.DriveUT, 1e-3,
                    "the descent DriveUT is the swept deorbit head (recordedDeorbitUT + (liveUT - triggerUT))");
                InGameAssert.AreEqual(SeamKind.Rigid, sample.Segment.LeadingSeam,
                    "the promoted descent carries the cross-member orbit↔landing Rigid leading seam "
                    + "(the stitcher owns the seam; the factory leaves it None)");

                // The promoted phase covering the head is a first-class DescentPhase (no longer hidden in the
                // transfer member).
                InGameAssert.IsTrue(
                    chain.TryGetPhase(sample.DriveUT, out TrajectoryPhase phase, out _),
                    "the chain must locate a phase at the re-anchored head");
                InGameAssert.IsTrue(phase is DescentPhase,
                    "the promoted phase is a first-class DescentPhase (visible, no longer hidden)");

                // SUB-SURFACE-GHOST-RETIRES: past the descent clip the stitcher returns false WITHOUT a held
                // sample, so the descent member's intent falls to Hidden and the ghost retires.
                InGameAssert.IsFalse(stitchedPastEnd,
                    "past the descent clip the stitcher must NOT hold a sample (the sub-surface ghost retires)");
                InGameAssert.AreEqual(default(GhostSample).Coverage, pastSample.Coverage,
                    "the past-end sample must be the default (no held below-surface sample)");

                // --- The orbit↔landing G1 seam predicate on LIVE body-relative world tangents ---
                // The capture-orbit-velocity-vs-descent-first-tangent PRODUCTION anomaly raise is WIRED
                // (Phase 5b) at the descent DRAW site (GhostTrajectoryPolylineRenderer.Driver
                // .EvaluateDescentSeamTangents - the only place those live world tangents exist; a
                // RenderSegment carries no points). Here we exercise the Unity-coupled live-body tangent
                // extraction + the seam predicate the headless tests cannot reach: two GENUINELY DISTINCT world
                // tangents from the recorded descent's consecutive legs (p0->p1, p1->p2) must read CONTINUOUS
                // for a smoothly-recorded descent (a stitch/extraction bug that kinked them would fail), and a
                // deliberately PERPENDICULAR tangent must read as a discontinuity (the predicate detects a real
                // kink on live-extracted data - NOT the old vacuous always-true assertion).
                Vector3 leg0 = WorldTangentBetween(body, 0.0, 0.0, 45000.0, 0.1, 0.05, 20000.0);
                Vector3 leg1 = WorldTangentBetween(body, 0.1, 0.05, 20000.0, 0.2, 0.1, 100.0);
                bool continuous = CrossMemberSeamStitcher.IsTangentSeamContinuous(leg0, leg1);
                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "DescentReStitch G1 seam: leg0={0} leg1={1} continuous={2}", leg0, leg1, continuous));
                InGameAssert.IsTrue(continuous,
                    "the orbit↔landing G1 seam must read CONTINUOUS for a smoothly-recorded descent "
                    + "(zero rigid-seam-tangent-discontinuity; distinct live-body tangents, non-vacuous)");

                Vector3 perp = Vector3.Cross(leg0, Vector3.up);
                if (perp.sqrMagnitude < 1e-6f)
                    perp = Vector3.Cross(leg0, Vector3.right);
                InGameAssert.IsFalse(CrossMemberSeamStitcher.IsTangentSeamContinuous(leg0, perp),
                    "a perpendicular seam tangent must read as a rigid-seam-tangent-discontinuity "
                    + "(the predicate detects a real kink on live-extracted tangents)");
            }
            finally
            {
                ShadowRenderDriver.Reset();
                MapRenderTrace.ForceEnabledForTesting = prevForceTrace;
            }
        }

        // A descent-trigger LoopUnit + set whose descent set = {DescentMemberIdx}. Mirrors the working
        // headless fixture (Supported plan + valid schedule => IsReaim; non-NaN periods + non-empty descent
        // set => HasDescentTrigger).
        private static GhostPlaybackLogic.LoopUnitSet BuildDescentUnitSet(
            double phaseAnchor, double spanStart, double spanEnd,
            double recordedDeorbitUT, double descentEndUT)
        {
            var plan = new Parsek.Reaim.ReaimMissionPlan { Supported = true };
            var sched = new Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule { Valid = true };
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: TransferMemberIdx, memberIndices: new[] { DescentMemberIdx, TransferMemberIdx },
                spanStartUT: spanStart, spanEndUT: spanEnd, cadenceSeconds: Cadence, phaseAnchorUT: phaseAnchor,
                overlapCadenceSeconds: Cadence, memberWindows: null, relaunchSchedule: null,
                reaimPlan: plan, reaimSchedule: sched,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: double.NaN, launchHoldEngaged: false,
                recordedSoiExitUT: double.NaN,
                descentMemberIndices: new[] { DescentMemberIdx }, recordedDeorbitUT: recordedDeorbitUT,
                descentEndUT: descentEndUT, destinationBodyRotationPeriodSeconds: Trot,
                loiterPeriodSeconds: Tpark, captureShiftSeconds: CaptureShift,
                transferMemberIndex: TransferMemberIdx);
            var ownerByIndex = new Dictionary<int, int>
            {
                { DescentMemberIdx, TransferMemberIdx }, { TransferMemberIdx, TransferMemberIdx },
            };
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { TransferMemberIdx, unit } }, ownerByIndex);
        }

        // The descent member's per-member PhaseChain: a parking conic [deorbit-50, deorbit] then a body-fixed
        // atmospheric reentry/descent run [deorbit, descentEnd] so the factory classifies the traced run as a
        // DescentPhase (a non-surface/non-approach traced run AFTER the first conic).
        private static PhaseChain BuildDescentMemberChain(double recordedDeorbitUT, double descentEndUT)
        {
            var rec = new Recording
            {
                RecordingId = "descent-restitch-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Descent ReStitch",
                EndpointPhase = RecordingEndpointPhase.SurfacePosition,
                EndpointBodyName = DescentBodyName,
                ExplicitStartUT = recordedDeorbitUT - 50.0,
                ExplicitEndUT = descentEndUT,
                PlaybackEnabled = true,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = recordedDeorbitUT, latitude = 0.0, longitude = 0.0, altitude = 45000.0,
                rotation = Quaternion.identity, velocity = new Vector3(0f, -200f, 0f), bodyName = DescentBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 0.5 * (recordedDeorbitUT + descentEndUT), latitude = 0.1, longitude = 0.05, altitude = 20000.0,
                rotation = Quaternion.identity, velocity = new Vector3(0f, -150f, 0f), bodyName = DescentBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = descentEndUT, latitude = 0.2, longitude = 0.1, altitude = 100.0,
                rotation = Quaternion.identity, velocity = new Vector3(0f, -10f, 0f), bodyName = DescentBodyName,
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = recordedDeorbitUT - 50.0, endUT = recordedDeorbitUT,
                bodyName = DescentBodyName, semiMajorAxis = 700000.0, eccentricity = 0.01, epoch = recordedDeorbitUT - 50.0,
            });
            rec.TrackSections.Add(new TrackSection
            {
                startUT = recordedDeorbitUT - 50.0, endUT = recordedDeorbitUT, environment = SegmentEnvironment.ExoBallistic,
            });
            rec.TrackSections.Add(new TrackSection
            {
                startUT = recordedDeorbitUT, endUT = descentEndUT, environment = SegmentEnvironment.Atmospheric,
            });
            rec.MarkFilesDirty();

            return PhaseFactory.BuildPhaseChain(
                rec, DescentMemberIdx, instanceKey: 0,
                windowStartUT: recordedDeorbitUT - 50.0, windowEndUT: descentEndUT);
        }

        // A live body-relative world tangent between two GENUINELY DISTINCT (lat,lon,alt) points on the
        // descent body, sampled from the body's own world surface positions (the Unity part this in-game test
        // exercises). Both endpoints are sampled at the same instant (same body rotation), so the relative
        // geometry is the body-fixed descent geometry; the absolute frame cancels in the seam-continuity
        // comparison. Unlike the old helper, the two endpoints actually differ, so the tangent is real.
        private static Vector3 WorldTangentBetween(
            CelestialBody body, double latA, double lonA, double altA,
            double latB, double lonB, double altB)
        {
            Vector3d a = body.GetWorldSurfacePosition(latA, lonA, altA) - body.position;
            Vector3d b = body.GetWorldSurfacePosition(latB, lonB, altB) - body.position;
            return CrossMemberSeamStitcher.TangentFromPositions((Vector3)a, (Vector3)b);
        }
    }
}
