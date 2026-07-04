using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 0 / migration §2 - the parity-BASELINE in-game test: the POSITIVE gate complementing the
    // sampler's drift-DETECTION (slice 2's TrySampleAndEmitFaithfulOrbitParity + the headless drifted
    // scenarios). It runs a KNOWN-GOOD scenario end-to-end through the REAL wired probe orchestration
    // (MapRenderProbe.ComputeFaithfulOrbitParity: the live OrbitDriver.orbit, the recorded reference rebuilt
    // PHASE-MATCHED via BuildPhaseMatchedReferenceOrbit, both sampled through OrbitRelativePositionYup,
    // diffed by RenderParityOracle) and asserts ZERO parity-drift - both directly on the ParityResult AND on
    // the gated MapRenderTrace anomaly sink (no reason=parity-drift line emitted for the known-good ghost).
    //
    // CRITICAL (the "green but blind" guard): this test calls the SAME internal seam the production probe
    // calls (MapRenderProbe.ComputeFaithfulOrbitParity), NOT an inlined copy of the diff. An earlier version
    // re-implemented the diff inline with shift=0, so it never exercised the loop-shift epoch-bake
    // orchestration where the BLOCKER lived (the reference was built from the raw segment epoch while the
    // live orbit was baked to seg.epoch + loopShift, tracing opposite arcs and reporting a FALSE drift on
    // every faithful loop ghost). The LOOP-SHIFTED variant below drives a ghost whose live orbit epoch IS
    // baked with a non-zero loop shift and asserts ZERO drift end-to-end - the in-game proof the blocker
    // fix (phase-matched reference epoch) works.
    //
    // Why it complements the others: the diff MATH is unit-tested headless (RenderParityOracleTests), the
    // pure reframing/flattening is unit-tested headless (RenderGeometrySamplerTests), the Unity CAPTURE
    // path is validated against a hand-computed orbit (RenderParitySamplerFixtureTest), and the headless
    // regression set asserts the oracle's VERDICT per §11.5 row (RenderParityRegressionScenarioTests).
    // This test is the end-to-end "a real faithful ghost, rendered live, reports zero drift" baseline the
    // migration plan gates every later phase on - captured on KNOWN-GOOD scenarios ONLY (the documented-
    // buggy scenarios are tracked as expected-to-change, NOT baselined; see the traceability doc).
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (it builds a live
    // ghost ProtoVessel and reads its OrbitDriver). Career-independent; FLIGHT + TRACKSTATION variants.
    public class RenderParityBaselineTest
    {
        private const string KerbinBodyName = "Kerbin";

        // A clean, unambiguously-orbital, fully-above-atmosphere CIRCULAR equatorial Kerbin orbit - the
        // same known-good shape the capture-harness fixture uses, so the baseline shares its
        // hand-verifiable geometry. A faithful ghost on this orbit MUST report zero drift.
        private const double Sma = 850000.0;
        private const double Ecc = 0.0;
        private const double Inc = 0.0;

        // A deliberately non-zero loop epoch shift (seconds) for the loop-shifted variant: about 18 minutes,
        // a sizeable fraction of the ~1.4 h orbit period at Sma, so the icon sits well around the orbit from
        // the raw recorded phase. With the BLOCKER bug present (raw-epoch reference + offShift~0 remap) the
        // rendered and reference orbits would trace opposite arcs at this shift and report a ~orbit-diameter
        // false drift; with the fix (phase-matched reference epoch) the diff is ~0.
        private const double LoopEpochShiftSeconds = 1100.0;

        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "Phase 0 parity-baseline (FLIGHT): a known-good faithful ghost rendered live "
                + "reports ZERO parity-drift through the REAL wired probe seam (positive gate "
                + "complementing the drift-detection sampler)")]
        public void ParityBaseline_KnownGoodFaithfulGhost_ZeroDrift_Flight()
        {
            AssertKnownGoodGhostReportsZeroDrift("FLIGHT", LoopEpochShiftSeconds: 0.0);
        }

        [InGameTest(Category = "GhostMap", Scene = GameScenes.TRACKSTATION,
            Description = "Phase 0 parity-baseline (TRACKSTATION): a known-good faithful ghost rendered "
                + "live reports ZERO parity-drift through the REAL wired probe seam (flight-map<->TS "
                + "render parity is a v1 requirement)")]
        public void ParityBaseline_KnownGoodFaithfulGhost_ZeroDrift_TrackingStation()
        {
            AssertKnownGoodGhostReportsZeroDrift("TRACKSTATION", LoopEpochShiftSeconds: 0.0);
        }

        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "Phase 0 parity-baseline (FLIGHT, LOOP-SHIFTED): a faithful ghost whose live "
                + "orbit epoch is baked with a NON-ZERO loop shift reports ZERO parity-drift through the "
                + "REAL wired probe seam - the in-game proof the false-drift blocker fix (phase-matched "
                + "reference epoch) works on the buggy loop-shift orchestration path")]
        public void ParityBaseline_LoopShiftedFaithfulGhost_ZeroDrift_Flight()
        {
            AssertKnownGoodGhostReportsZeroDrift("FLIGHT-loop", LoopEpochShiftSeconds);
        }

        private static void AssertKnownGoodGhostReportsZeroDrift(string scene, double LoopEpochShiftSeconds)
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            // The recording is authored in the RAW recorded clock: a loop-shifted ghost replays a PAST
            // recorded segment whose effUT = liveUT - shift. Author the segment around that effUT so the
            // covering-segment lookup at offEffUT resolves, mirroring a real loop replay.
            double effUT = liveUT - LoopEpochShiftSeconds;
            double startUT = effUT - 1800.0;
            OrbitSegment seg = BuildSegment(startUT);

            // Force the tracer ON for this test so the anomaly sink is live (the gate routes everything
            // through MapRenderTrace.IsEnabled). Capture the emitted lines via the ParsekLog test sink so
            // we can assert NO reason=parity-drift line fires for the known-good ghost.
            bool prevForceEnabled = MapRenderTrace.ForceEnabledForTesting;
            var capturedLines = new List<string>();
            System.Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool prevSuppress = ParsekLog.SuppressLogging;

            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            Recording rec = BuildRecording(startUT, seg);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("parity-baseline-start");

            uint pid = 0u;
            try
            {
                MapRenderTrace.ForceEnabledForTesting = true;
                ParsekLog.SuppressLogging = false;
                ParsekLog.TestSinkForTesting = line => capturedLines.Add(line);

                // Create the live ghost with the loop epoch shift baked in (shift 0 for the non-loop
                // variants). For a non-zero shift this seeds the rendered OrbitDriver.orbit epoch to
                // seg.epoch + shift (BuildAndLoadGhostProtoVessel + StockConicTreatment.SeedAndDriveLive)
                // and records the shift via GhostMapPresence.GetGhostOrbitEpochShift(pid) - the EXACT
                // orchestration the production probe reads. The probe's ComputeFaithfulOrbitParity then
                // rebuilds the reference PHASE-MATCHED to that baked epoch, so a correct fix reads ~0.
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex,
                    rec,
                    GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg,
                    default(TrajectoryPoint),
                    startUT,
                    loopEpochShiftSeconds: LoopEpochShiftSeconds);

                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("Faithful ghost did not create in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;

                Orbit renderedOrbit = ghost.orbitDriver.orbit;

                // Read the loop shift the live orbit was ACTUALLY baked with from the production map (NOT a
                // local constant): proves the create path stored it and the probe reads the same value.
                double loopShift = GhostMapPresence.GetGhostOrbitEpochShift(pid);
                InGameAssert.ApproxEqual(LoopEpochShiftSeconds, loopShift, 1.0,
                    "Live ghost must record the loop epoch shift the production probe reads back");

                // The icon point exactly as the production sampler anchors the rendered set.
                Vector3d iconBodyRel = ghost.GetWorldPos3D() - kerbin.position;

                // *** Drive the REAL production seam (not an inlined diff) ***. This exercises the recorded-
                // segment lookup, the loop-shift epoch bake (BuildPhaseMatchedReferenceOrbit), the
                // OrbitRelativePositionYup sampling of BOTH orbits at the SAME UTs, the scale-derived
                // tolerance, and the oracle diff - the same call the probe makes each frame.
                MapRenderProbe.FaithfulParitySample sample = MapRenderProbe.ComputeFaithfulOrbitParity(
                    renderedOrbit, kerbin, iconBodyRel, effUT, loopShift, liveUT, rec.RecordingId);

                Parsek.MapRender.RenderParityOracle.ParityResult result = sample.Result;

                ParsekLog.Info("TestRunner",
                    string.Format(CultureInfo.InvariantCulture,
                        "ParityBaseline_KnownGood ({0}): pid={1} sma={2:F0} ecc={3:F2} loopShift={4:F1} "
                        + "sampled={5} skip={6} hasMeas={7} maxDev={8:F1}m tol={9:F1}m scale={10:F0}m over={11}",
                        scene, pid, Sma, Ecc, loopShift, sample.Sampled, sample.SkipReason ?? "(none)",
                        result.HasMeasurement, result.MaxDeviationMeters, result.ToleranceMeters,
                        sample.Scale, result.OverTolerance));

                // The KNOWN-GOOD baseline: a faithful ghost drawing its own recorded orbit MUST sample (a
                // skip here means the covering segment / body guard mis-fired) and MUST measure within
                // tolerance (zero drift). A no-sample / no-measurement here is a sampler-blindness bug (the
                // positive gate that would otherwise pass silently), so assert both.
                InGameAssert.IsTrue(sample.Sampled,
                    "Known-good faithful ghost must SAMPLE (not skip) the parity diff; skipReason="
                    + (sample.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(result.HasMeasurement,
                    "Known-good faithful ghost must yield a parity MEASUREMENT (else the positive gate is "
                    + "blind); HasMeasurement was false.");
                InGameAssert.IsFalse(result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "Known-good faithful ghost (loopShift={0:F1}) must report ZERO drift; maxDev={1:F1}m "
                        + "exceeded tol={2:F1}m (scale={3:F0}m). The wired parity path is flagging a faithful "
                        + "baseline as drifted - a positive-gate regression (the loop-shift false-drift "
                        + "blocker if loopShift != 0).",
                        loopShift, result.MaxDeviationMeters, result.ToleranceMeters, sample.Scale));

                // Mirror the production emit decision: a known-good ghost would NOT emit a parity-drift
                // anomaly. Run the same EmitAnomaly-on-over-tolerance gate and assert nothing fired.
                if (result.HasMeasurement && result.OverTolerance)
                    MapRenderTrace.EmitAnomaly(
                        MapRenderTrace.RenderSurface.ProtoOrbitLine, pid.ToString(CultureInfo.InvariantCulture),
                        liveUT, effUT, MapRenderTrace.AnomalyParityDrift,
                        "mode=faithful (baseline test emit)");

                foreach (string line in capturedLines)
                    InGameAssert.IsFalse(
                        line.Contains("reason=" + MapRenderTrace.AnomalyParityDrift),
                        "Known-good baseline must emit NO parity-drift anomaly; saw: " + line);
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("parity-baseline-cleanup");
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
                MapRenderTrace.ForceEnabledForTesting = prevForceEnabled;
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.SuppressLogging = prevSuppress;
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

        private static Recording BuildRecording(double startUT, OrbitSegment seg)
        {
            double endUT = startUT + 3600.0;
            var rec = new Recording
            {
                RecordingId = "parity-baseline-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Parity Baseline",
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
                ut = startUT,
                latitude = 0.0,
                longitude = 0.0,
                altitude = Sma - KerbinRadiusFallback,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2200f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                latitude = 0.0,
                longitude = 5.0,
                altitude = Sma - KerbinRadiusFallback,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2200f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }

        // Only used to author plausible TrajectoryPoint altitudes; the geometry under test is driven by
        // the OrbitSegment elements, so the exact value is not load-bearing.
        private const double KerbinRadiusFallback = 600000.0;
    }
}
