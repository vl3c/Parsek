using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 10 / B-row2 (cutover regression harness) - the RE-AIMED interplanetary-loop live proof: the
    // payoff of the Phase-9 SYNTHESIZED lens. A re-aimed member's rendered conic is aimed at the target's
    // CURRENT position, so it DIFFERS from the recorded segment - which means the FAITHFUL oracle (rendered
    // vs RECORDED) cannot validate it (it flags drift / is skipped in production by ShouldSkipReaimSegment).
    // The SYNTHESIZED oracle (rendered vs the PRODUCER'S INTENDED re-aimed seed) is the ONLY lens that can
    // confirm a re-aimed draw is correct. This test gives that proof LIVE:
    //
    //  - The ghost's live OrbitDriver.orbit is driven from a RE-AIMED segment (a recorded shape rotated in
    //    LAN, modelling "the target moved, so the transfer is re-aimed"). This is the rendered conic.
    //  - The recording on disk still stores the RECORDED segment (the un-re-aimed original).
    //  - SYNTHESIZED oracle (rendered re-aimed conic vs the re-aimed INTENDED seed): reads ~0 (the draw is
    //    faithful to the intended re-aimed arc). THE PROOF.
    //  - FAITHFUL oracle (rendered re-aimed conic vs the RECORDED segment): FLAGS drift. This is the negative
    //    control that proves the faithful-only oracle could NOT have given the synthesized proof - the
    //    re-aimed draw is correct yet the faithful lens reads it as drifted (which is exactly why production
    //    skips faithful parity for re-aimed members and needs the synthesized lens).
    //
    // ARCHITECTURAL TRUTH respected: this asserts DRAW-fidelity (rendered == the intended re-aimed seed), NOT
    // solve-correctness (whether the Lambert re-aim solver picked the physically right target position - the
    // solver owns that). It exercises the live capture path (OrbitDriver.orbit, GetWorldPos3D) the headless
    // synthesized regression scenarios cannot reach.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (builds a live ghost
    // ProtoVessel from a re-aimed segment and reads its OrbitDriver). FLIGHT only; career-independent.
    public class ReaimedLoopSynthesizedOracleInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double Sma = 1_200_000.0;   // a wider arc so the LAN rotation moves the conic clearly
        private const double Ecc = 0.05;
        private const double Inc = 12.0;
        private const double RecordedLAN = 0.0;
        private const double ReaimedLAN = 70.0;   // the target "moved": the transfer is re-aimed by 70 deg
        private const double KerbinRadiusFallback = 600000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 10 B-row2 re-aimed loop (live): the SYNTHESIZED oracle reads ~0 on a re-aimed "
                + "draw (rendered conic == intended re-aimed seed) while the FAITHFUL oracle FLAGS it (rendered "
                + "!= recorded) - the live proof the faithful-only oracle could not give")]
        public void ReaimedLoop_SynthesizedOracleReadsZero_FaithfulOracleFlagsIt()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double startUT = liveUT - 1800.0;
            // The RECORDED segment (what the recording stores) and the RE-AIMED segment (what the ghost is
            // actually driven from - the producer's intended re-aimed arc). Same body + sma + ecc + inc, but
            // a rotated LAN: the target moved, so the transfer is re-aimed.
            OrbitSegment recordedSeg = BuildSegment(startUT, RecordedLAN);
            OrbitSegment reaimedSeg = BuildSegment(startUT, ReaimedLAN);

            bool prevForceSpine = ShadowRenderDriver.ForceSpineDriveForTesting;
            bool prevForceTrace = MapRenderTrace.ForceEnabledForTesting;
            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            // The recording stores the RECORDED (un-re-aimed) segment - the faithful reference.
            Recording rec = BuildRecording(startUT, recordedSeg);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("reaim-synth-start");
            ShadowRenderDriver.Reset();

            uint pid = 0u;
            try
            {
                MapRenderTrace.ForceEnabledForTesting = true;
                ShadowRenderDriver.ForceSpineDriveForTesting = true; // spine ON (flag-ON scenario)

                // Drive the live ghost from the RE-AIMED segment: OrbitDriver.orbit is the re-aimed conic.
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex, rec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    reaimedSeg, default(TrajectoryPoint), startUT, loopEpochShiftSeconds: 0.0);

                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("Re-aimed ghost did not create in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;
                Orbit renderedOrbit = ghost.orbitDriver.orbit;
                Vector3d iconBodyRel = ghost.GetWorldPos3D() - kerbin.position;
                if (iconBodyRel.magnitude < 1.0)
                {
                    InGameAssert.Skip("Ghost world position not resolved on the creation frame");
                    return;
                }

                // --- THE PROOF: SYNTHESIZED oracle (rendered re-aimed conic vs the re-aimed INTENDED seed) ---
                // The rendered orbit IS the re-aimed seed (the ghost was driven from it), so the synthesized
                // lens reads ~0: the re-aimed draw is faithful to its intended arc. loopShift 0.0 (non-loop
                // here; the loop-shift epoch bake is covered by the A3 loop-shifted arm + the existing
                // SynthesizedParity_LoopShiftedGhost test).
                MapRenderProbe.SynthesizedConicParitySample synth =
                    MapRenderProbe.ComputeSynthesizedConicParity(
                        renderedOrbit, kerbin, reaimedSeg, KerbinBodyName, 0.0, liveUT, liveUT);
                InGameAssert.IsTrue(synth.Sampled,
                    "the synthesized oracle must SAMPLE the rendered-vs-re-aimed-seed diff; skipReason="
                    + (synth.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(synth.Result.HasMeasurement,
                    "the synthesized oracle must yield a measurement (else the proof is blind)");
                InGameAssert.IsFalse(synth.Result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "the SYNTHESIZED oracle must read ZERO drift on the re-aimed draw (rendered conic == "
                        + "intended re-aimed seed); maxDev={0:F0}m tol={1:F0}m. A re-aim DRAW regression "
                        + "(rendered conic diverging from the seed it was driven from) would flag here.",
                        synth.Result.MaxDeviationMeters, synth.Result.ToleranceMeters));

                // --- THE NEGATIVE CONTROL: FAITHFUL oracle (rendered re-aimed conic vs the RECORDED segment) ---
                // The re-aimed orbit DIFFERS from the recorded segment (LAN rotated 70 deg), so the faithful
                // lens FLAGS drift on this CORRECT re-aimed draw. This is precisely why the faithful-only
                // oracle could not give the synthesized proof - it cannot tell a correct re-aim from a wrong
                // one. (Production skips faithful parity for re-aimed members via ShouldSkipReaimSegment for
                // exactly this reason; here we exercise it directly to prove the lens distinction is real.)
                MapRenderProbe.FaithfulParitySample faithful = MapRenderProbe.ComputeFaithfulOrbitParity(
                    renderedOrbit, kerbin, 0.0, liveUT, rec.RecordingId);
                InGameAssert.IsTrue(faithful.Sampled,
                    "the faithful oracle must SAMPLE here (same body, covering recorded segment) so the "
                    + "lens-distinction control is non-vacuous; skipReason=" + (faithful.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(faithful.Result.HasMeasurement,
                    "the faithful control must yield a measurement");
                InGameAssert.IsTrue(faithful.Result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "the FAITHFUL oracle MUST flag the re-aimed draw as drifted (rendered LAN={0:F0} != "
                        + "recorded LAN={1:F0}); maxDev={2:F0}m tol={3:F0}m. If this reads ~0 the LAN rotation "
                        + "is not actually moving the conic (the proof would be tautological).",
                        ReaimedLAN, RecordedLAN, faithful.Result.MaxDeviationMeters,
                        faithful.Result.ToleranceMeters));

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "ReaimedLoop_SynthOracle: pid={0} recordedLAN={1:F0} reaimedLAN={2:F0} | "
                    + "synthDev={3:F0}m synthTol={4:F0}m (ZERO) | faithfulDev={5:F0}m faithfulTol={6:F0}m "
                    + "(FLAGGED)",
                    pid, RecordedLAN, ReaimedLAN, synth.Result.MaxDeviationMeters,
                    synth.Result.ToleranceMeters, faithful.Result.MaxDeviationMeters,
                    faithful.Result.ToleranceMeters));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("reaim-synth-cleanup");
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

        private static OrbitSegment BuildSegment(double startUT, double lan)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = startUT + 3600.0,
                inclination = Inc,
                eccentricity = Ecc,
                semiMajorAxis = Sma,
                longitudeOfAscendingNode = lan,
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
                RecordingId = "reaim-synth-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Reaim Synth Loop",
                TerminalStateValue = null,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = KerbinBodyName,
                TerminalOrbitBody = KerbinBodyName,
                TerminalOrbitSemiMajorAxis = Sma,
                TerminalOrbitEccentricity = Ecc,
                TerminalOrbitInclination = Inc,
                TerminalOrbitLAN = seg.longitudeOfAscendingNode,
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
                rotation = Quaternion.identity, velocity = new Vector3(0f, 2200f, 0f), bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0.0, longitude = 5.0, altitude = Sma - KerbinRadiusFallback,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 2200f, 0f), bodyName = KerbinBodyName,
            });
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }
    }
}
