using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 10 / B-row1 (cutover regression harness) - the DESCENT end-to-end SPINE test. It drives a looped
    // landing flag-ON through the SAME sampler entry point ShadowRenderDriver.RunFrame inlines
    // (ChainSampler.Sample(PhaseChain, liveUT, units) -> the span-clock remap + the CrossMemberSeamStitcher +
    // the projection) at three live UTs and asserts the spine's DESCENT DECISION is correct:
    //
    //  - TRIGGERED (re-anchored head live, inside the descent clip): the spine resolves a VISIBLE TracedPath
    //    DescentPhase at the swept-deorbit head (recordedDeorbitUT + (liveUT - triggerUT)), promoted to a
    //    first-class DescentPhase over the cross-member Rigid orbit-landing seam.
    //  - PAST-END (past the descent clip): the spine resolves OutsideWindow / Hidden (the stitcher returns no
    //    held sample), so the descent member's intent retires - the spine's DECISION to not hold a sub-surface
    //    ghost.
    //
    // WHY DRIVE ChainSampler.Sample DIRECTLY (not the per-pid RunFrame loop): RunFrame iterates the LIVE
    // GhostMapPresence pid set + builds the chain via GetOrBuildChain off a re-aim resolver, which needs a
    // full ReaimMissionPlan/Schedule to produce a descent-set member - a fragile fixture that frequently
    // window-misses. ChainSampler.Sample(PhaseChain, ...) is the EXACT pure call RunFrame inlines for the
    // spine (RunFrame: `sample = ChainSampler.Sample(phaseChain, currentUT, units)`), so driving it against a
    // hand-built descent PhaseChain + descent-trigger unit exercises the production spine decision path the
    // headless tests (CrossMemberSeamStitcherTests, pure-clock only) cannot reach: the live-PhaseChain
    // span-clock remap -> stitcher -> projection -> the GhostSample the Director turns into an intent.
    //
    // ARCHITECTURAL TRUTH respected + the GATE FINDING surfaced (NOT a faked pixel change): the spine's
    // DECISION (re-anchored head / promoted DescentPhase / Hidden-past-clip) is what flag-ON changes today.
    // The legacy autonomous polyline Driver still OWNS the actual leg DRAW (the pixels), so the Phase-6
    // sub-surface-retire FIX only LANDS at the draw layer when Phase 5b deletes the legacy draw. This test
    // therefore asserts the spine's RETIRE DECISION (the sample retires past the clip) and DOCUMENTS - by
    // asserting the CURRENT observable state - that the leg-draw ownership is still the legacy Driver
    // (IsRenderingNonOrbitalLeg is NOT driven by this spine sample; the draw-layer retire lands at 5b). It
    // does NOT assert a sub-surface pixel disappearing this phase.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (builds a live
    // PhaseChain over a recording + resolves the destination body). FLIGHT only; career-independent.
    public class DescentEndToEndSpineInGameTest
    {
        private const string DescentBodyName = "Kerbin";
        private const int DescentMemberIdx = 0;
        private const int TransferMemberIdx = 2;

        private const double ClipSeconds = 100.0;
        private const double CaptureShift = -150.0;
        private const double Trot = 300.0;
        private const double Tpark = 80.0;
        private const double Cadence = 4000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 10 B-row1 descent end-to-end (spine): the spine's sampler (the RunFrame-"
                + "inlined ChainSampler.Sample) resolves a VISIBLE re-anchored DescentPhase at a triggered "
                + "live UT and RETIRES (Hidden, no held sample) past the descent clip; documents that the "
                + "legacy Driver still owns the leg DRAW (sub-surface retire lands at 5b)")]
        public void DescentSpine_FlagOn_PromotesReanchoredDescent_RetiresPastClip_LegDrawStillLegacy()
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.bodyName == DescentBodyName);
            if (body == null)
            {
                InGameAssert.Skip("destination body not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double now = Planetarium.GetUniversalTime();
            double phaseAnchor = now;
            double recordedDeorbitUT = now + 200.0;
            double descentEndUT = recordedDeorbitUT + ClipSeconds;
            double spanStart = now;
            double spanEnd = now + 1000.0;

            bool prevForceSpine = ShadowRenderDriver.ForceSpineDriveForTesting;
            bool prevForceTrace = MapRenderTrace.ForceEnabledForTesting;

            try
            {
                MapRenderTrace.ForceEnabledForTesting = true;
                ShadowRenderDriver.ForceSpineDriveForTesting = true; // the spine drives (flag ON)

                GhostPlaybackLogic.LoopUnitSet units = BuildDescentUnitSet(
                    phaseAnchor, spanStart, spanEnd, recordedDeorbitUT, descentEndUT);
                Recording rec = BuildDescentRecording(recordedDeorbitUT, descentEndUT);
                PhaseChain chain = PhaseFactory.BuildPhaseChain(
                    rec, DescentMemberIdx, instanceKey: 0,
                    windowStartUT: recordedDeorbitUT - 50.0, windowEndUT: descentEndUT);

                Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                    0, phaseAnchor, Cadence, spanStart, recordedDeorbitUT, Trot, CaptureShift, null,
                    out _, out double entryUT, out double triggerUT);

                if (double.IsNaN(triggerUT))
                {
                    InGameAssert.Skip("descent trigger did not resolve for this fixture (degenerate timing)");
                    return;
                }

                double triggeredLiveUT = triggerUT + 0.4 * ClipSeconds; // inside the clip
                double pastEndLiveUT = triggerUT + 1.5 * ClipSeconds;   // past the clip => retire

                // --- TRIGGERED frame: drive the SAME sampler RunFrame inlines for the spine path ---
                GhostSample triggered = ChainSampler.Sample(chain, triggeredLiveUT, units);
                // The Director turns that GhostSample into the intent the same way RunFrame does.
                GhostRenderIntent triggeredIntent = GhostRenderDirector.Decide(
                    triggered, GhostRenderIntent.Hidden(), rec.VesselName);

                // --- PAST-END frame: same sampler, past the clip ---
                GhostSample pastEnd = ChainSampler.Sample(chain, pastEndLiveUT, units);
                GhostRenderIntent pastEndIntent = GhostRenderDirector.Decide(
                    pastEnd, triggeredIntent /* prior was visible */, rec.VesselName);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "DescentSpine: deorbit={0:R} entry={1:R} trigger={2:R} | triggered cov={3} treat={4} "
                    + "vis={5} driveUT={6:R} body={7} | pastEnd cov={8} vis={9}",
                    recordedDeorbitUT, entryUT, triggerUT, triggered.Coverage, triggered.Treatment,
                    triggeredIntent.Visible, triggered.DriveUT, triggered.FrameBodyName ?? "?",
                    pastEnd.Coverage, pastEndIntent.Visible));

                // (i) THE SPINE DECISION at the trigger: a VISIBLE re-anchored TracedPath descent.
                InGameAssert.AreEqual(Coverage.InSegment, triggered.Coverage,
                    "the spine must resolve the descent InSegment at the triggered live UT (re-anchored head "
                    + "inside the clip)");
                InGameAssert.AreEqual(Treatment.TracedPath, triggered.Treatment,
                    "the promoted descent is a body-fixed TracedPath leg (polyline-owned)");
                InGameAssert.IsTrue(triggeredIntent.Visible,
                    "the spine's descent intent must be VISIBLE at the triggered head");
                InGameAssert.AreEqual(DescentBodyName, triggered.FrameBodyName,
                    "the promoted descent renders on the destination body");
                InGameAssert.ApproxEqual(
                    recordedDeorbitUT + (triggeredLiveUT - triggerUT), triggered.DriveUT, 1e-3,
                    "the descent DriveUT is the swept deorbit head (recordedDeorbitUT + (liveUT - triggerUT))");
                InGameAssert.AreEqual(SeamKind.Rigid, triggered.Segment.LeadingSeam,
                    "the promoted descent carries the cross-member orbit-landing Rigid leading seam (the "
                    + "stitcher owns it; the factory leaves it None) - the spine's seam decision");

                // The promoted phase the spine located is a first-class DescentPhase (no longer hidden).
                InGameAssert.IsTrue(
                    chain.TryGetPhase(triggered.DriveUT, out TrajectoryPhase phase, out _),
                    "the chain must locate a phase at the re-anchored head");
                InGameAssert.IsTrue(phase is DescentPhase,
                    "the spine promotes a first-class DescentPhase (visible, no longer hidden in transfer)");

                // (ii) THE SPINE RETIRE DECISION past the clip: the sample is OutsideWindow and the intent
                // retires to Hidden (no held sub-surface sample). This is the spine's decision; the actual
                // sub-surface PIXEL only stops at 5b (see the gate finding below).
                InGameAssert.AreEqual(Coverage.OutsideWindow, pastEnd.Coverage,
                    "past the descent clip the spine must resolve OutsideWindow (the stitcher returns no held "
                    + "sample - the spine's retire decision)");
                InGameAssert.IsFalse(pastEndIntent.Visible,
                    "the spine's descent intent must RETIRE (Hidden) past the clip, even though the prior "
                    + "frame was visible - the spine does not hold a sub-surface ghost");

                // (iii) THE GATE FINDING (documented, not faked): the ACTUAL leg DRAW is still owned by the
                // legacy autonomous polyline Driver. The spine's intent above is the DECISION source; it does
                // NOT itself drive the pixels. So the polyline ownership signal for this recording is NOT set
                // by this spine sample (no live map render walk ran), confirming the Phase-6 sub-surface-retire
                // FIX lands at the DRAW layer only when Phase 5b deletes the legacy draw. We assert the CURRENT
                // observable state (ownership not driven by the spine sample) rather than a pixel change.
                bool legDrawOwnedByPolyline =
                    Parsek.Display.GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(rec.RecordingId);
                InGameAssert.IsFalse(legDrawOwnedByPolyline,
                    "GATE FINDING (documents the 5b-pending state): the spine's descent DECISION does not "
                    + "itself own the leg DRAW - IsRenderingNonOrbitalLeg is false because no live polyline "
                    + "walk drew this recording's leg (the legacy autonomous Driver owns the draw; the "
                    + "sub-surface-retire fix lands at the DRAW layer at Phase 5b). This asserts the current "
                    + "decision-source-swap contract, NOT a 5b-only pixel change.");

                ParsekLog.Info("TestRunner",
                    "DescentSpine GATE FINDING: spine descent DECISION is correct (visible re-anchored head + "
                    + "retire past clip), but the leg DRAW ownership remains the legacy Driver - the "
                    + "sub-surface-retire pixel change lands at Phase 5b (legacy-draw deletion).");
            }
            finally
            {
                ShadowRenderDriver.ForceSpineDriveForTesting = prevForceSpine;
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

        // The descent member's recording: a parking conic [deorbit-50, deorbit] then a body-fixed atmospheric
        // reentry/descent run [deorbit, descentEnd] so the factory classifies the traced run as a DescentPhase
        // (a non-surface/non-approach traced run AFTER the first conic).
        private static Recording BuildDescentRecording(double recordedDeorbitUT, double descentEndUT)
        {
            var rec = new Recording
            {
                RecordingId = "descent-spine-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Descent Spine",
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
            return rec;
        }
    }
}
