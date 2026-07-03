using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 10 / A3 (cutover regression harness) - the FLAG-ON parity-BASELINE in-game test, the positive
    // gate proving that with the typed PhaseChain spine DRIVING (ForceSpineDriveForTesting), a known-good
    // faithful ghost rendered live reports ZERO parity-drift across the FAITHFUL + SYNTHESIZED Phase-9
    // oracle modes, plus a POLYLINE ORACLE ZERO-CONTRACT SANITY check (NOT live polyline capture coverage;
    // see the mode-3 note below).
    //
    // WHY THIS DID NOT EXIST: the existing RenderParityBaselineTest captures the zero-drift baseline with the
    // spine flag OFF (the legacy assembler spine drives the orbit; the flag only swaps the DECISION source).
    // PhaseSpineSwapInGameTest proves flag-ON and flag-OFF stamp the SAME seed and read zero FAITHFUL drift,
    // but it exercises only the faithful oracle. This test closes the gap the 5-lens audit found for the two
    // LIVE lenses: the KNOWN-GOOD baseline run with the spine ON, asserted through the faithful AND
    // synthesized lenses at once, so a flag-ON regression that diverged in either (faithful
    // rendered-vs-recorded, synthesized rendered-vs-intended) lights up here.
    //
    // ARCHITECTURAL TRUTH respected: flag-ON only swaps the decision SOURCE - the legacy code still DRAWS the
    // pixels (GhostOrbitLinePatch / the autonomous polyline Driver). So this asserts the CURRENT contract:
    //  (i)  the spine's DECISION matches what flag-OFF would decide for a faithful member (the seed the icon-
    //       drive reads is byte-identical across the flag), and
    //  (ii) the live Phase-9 oracle stays GREEN (zero drift) on the correct draw, in the faithful +
    //       synthesized modes.
    // It does NOT assert any geometry change that only Phase 5b delivers.
    //
    // The oracle modes exercised:
    //  - FAITHFUL  : MapRenderProbe.ComputeFaithfulOrbitParity on the live OrbitDriver.orbit vs the recorded
    //                segment, PHASE-MATCHED. (the rendered-vs-recorded lens; LIVE flag-ON coverage)
    //  - SYNTHESIZED: MapRenderProbe.ComputeSynthesizedConicParity of the live orbit vs the Director's fresh
    //                StockConic seed (ShadowRenderDriver.TryGetFreshStockConicSeed) - for a faithful StockConic
    //                member the rendered orbit IS the seed, so it reads ~0. (the rendered-vs-intended lens;
    //                LIVE flag-ON coverage)
    //  - POLYLINE  : ORACLE ZERO-CONTRACT SANITY ONLY (rendered == recorded input yields zero drift): the
    //                leg-track lens (RenderParityOracle.ComputeDriftScaleDerived, ParityMode.Synthesized) on
    //                a real body-framed recorded leg arc diffed against ITSELF. This is NOT live polyline
    //                capture coverage - the live CaptureRenderedVsRecordedLegGeometry walk needs a populated
    //                polylineCache from a real map render, which this harness cannot produce; that live walk
    //                is validated by tracing-on play sessions. The arc-vs-itself diff mirrors
    //                FailClosedFaithfulInGameTest's in-game polyline-lens pattern (a real Unity-framed leg
    //                arc through the live body's world surface positions).
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (builds a live ghost
    // ProtoVessel, reads its OrbitDriver, drives RunFrame against a live MapViewScene, frames a leg arc
    // through Kerbin's world surface positions). FLIGHT only; career-independent.
    public class FlagOnParityBaselineInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double Sma = 850000.0;
        private const double Ecc = 0.0;
        private const double Inc = 0.0;
        private const double KerbinRadiusFallback = 600000.0;

        // A non-zero loop epoch shift (~18 min) for the loop-shifted arm, exercising the loop-shift epoch
        // bake (the false-drift blocker path) end-to-end through the real seam under the SPINE-ON flag.
        private const double LoopEpochShiftSeconds = 1100.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 10 A3 flag-ON parity baseline: with the typed PhaseChain spine DRIVING, a "
                + "known-good faithful ghost rendered live reports ZERO parity-drift across the faithful + "
                + "synthesized oracle modes, plus a polyline-oracle zero-contract sanity check (NOT live "
                + "polyline capture coverage)")]
        public void FlagOnBaseline_KnownGoodGhost_ZeroDrift_AllThreeOracleModes()
        {
            RunFlagOnAllModeBaseline(LoopEpochShiftSeconds: 0.0);
        }

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 10 A3 flag-ON parity baseline (LOOP-SHIFTED): with the spine DRIVING, a "
                + "ghost whose live orbit epoch is baked with a NON-ZERO loop shift reports ZERO parity-drift "
                + "across the faithful + synthesized oracle modes plus the polyline-oracle sanity check - the "
                + "spine-ON proof the loop-shift epoch bake stays correct")]
        public void FlagOnBaseline_LoopShiftedGhost_ZeroDrift_AllThreeOracleModes()
        {
            RunFlagOnAllModeBaseline(LoopEpochShiftSeconds);
        }

        private static void RunFlagOnAllModeBaseline(double LoopEpochShiftSeconds)
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            // A loop-shifted ghost replays a PAST recorded segment whose effUT = liveUT - shift; author the
            // segment around that effUT so the covering-segment lookup at the drive clock resolves.
            double effUT = liveUT - LoopEpochShiftSeconds;
            double startUT = effUT - 1800.0;
            OrbitSegment seg = BuildSegment(startUT);

            bool prevForceSpine = ShadowRenderDriver.ForceSpineDriveForTesting;
            bool prevForceTrace = MapRenderTrace.ForceEnabledForTesting;
            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            var capturedLines = new List<string>();
            System.Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool prevSuppress = ParsekLog.SuppressLogging;
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
            GhostMapPresence.RemoveAllGhostVessels("flagon-baseline-start");
            ShadowRenderDriver.Reset();

            uint pid = 0u;
            try
            {
                MapRenderTrace.ForceEnabledForTesting = true;   // the parity sink + anomaly raises are live
                ShadowRenderDriver.ForceSpineDriveForTesting = true; // THE SPINE DRIVES (flag ON)
                ParsekLog.SuppressLogging = false;
                ParsekLog.TestSinkForTesting = line => capturedLines.Add(line);

                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex, rec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg, default(TrajectoryPoint), startUT, loopEpochShiftSeconds: LoopEpochShiftSeconds);

                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("Faithful ghost did not create in this context (no proto)");
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

                // Drive the REAL wired RunFrame with the SPINE ON so the typed PhaseChain decides the intent
                // and stamps the StockConic seed (the drive the icon-drive patch reads + the synthesized
                // oracle diffs against). Reset before so the per-pid prior intent / chain cache is clean.
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);

                // S6 false-green guard: GetOrBuildChain swallows a factory throw into a cached null
                // PhaseChain and the spine then falls back to the legacy assembler chain, so zero drift
                // alone cannot prove the SPINE drove. A non-null cached PhaseChain is the proof.
                InGameAssert.IsTrue(ShadowRenderDriver.HasCachedPhaseChainForTesting(pid),
                    "the spine must have BUILT a PhaseChain for this ghost - a null cache means the factory "
                    + "threw and the legacy fallback drove (the flag-ON gate would otherwise pass on a false "
                    + "green)");

                Orbit renderedOrbit = ghost.orbitDriver.orbit;
                Vector3d iconBodyRel = ghost.GetWorldPos3D() - kerbin.position;
                if (iconBodyRel.magnitude < 1.0)
                {
                    InGameAssert.Skip("Ghost world position not resolved on the creation frame");
                    return;
                }

                // The loop shift the live orbit was ACTUALLY baked with (read from the production map, not a
                // local constant): the same value both oracle lenses thread into the phase-matched reference.
                double loopShift = GhostMapPresence.GetGhostOrbitEpochShift(pid);
                InGameAssert.ApproxEqual(LoopEpochShiftSeconds, loopShift, 1.0,
                    "the spine-ON ghost must record the loop epoch shift the production probe reads back");

                // --- ORACLE MODE 1: FAITHFUL (rendered orbit vs recorded segment, PHASE-MATCHED) ---
                MapRenderProbe.FaithfulParitySample faithful = MapRenderProbe.ComputeFaithfulOrbitParity(
                    renderedOrbit, kerbin, loopShift, liveUT, rec.RecordingId);
                InGameAssert.IsTrue(faithful.Sampled,
                    "FAITHFUL oracle must SAMPLE (not skip) the spine-ON faithful ghost; skipReason="
                    + (faithful.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(faithful.Result.HasMeasurement,
                    "FAITHFUL oracle must yield a measurement (else the lens is blind)");
                InGameAssert.IsFalse(faithful.Result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "FAITHFUL oracle must read ZERO drift on the spine-ON known-good ghost; maxDev={0:F1}m "
                        + "tol={1:F1}m. The spine is driving a different orbit than the recorded segment.",
                        faithful.Result.MaxDeviationMeters, faithful.Result.ToleranceMeters));

                // --- ORACLE MODE 2: SYNTHESIZED (rendered orbit vs the Director's fresh StockConic seed) ---
                // The spine stamped the seed in RunFrame above; the synthesized lens diffs the rendered orbit
                // against it. For a faithful StockConic member the rendered orbit IS the seed, so ~0.
                bool hasSeed = ShadowRenderDriver.TryGetFreshStockConicSeed(
                    pid, Time.frameCount, out OrbitSegment seed, out string seedBody);
                InGameAssert.IsTrue(hasSeed,
                    "the spine-ON RunFrame must stamp a fresh StockConic seed for the synthesized oracle to "
                    + "diff against (none stamped)");
                MapRenderProbe.SynthesizedConicParitySample synth =
                    MapRenderProbe.ComputeSynthesizedConicParity(
                        renderedOrbit, kerbin, seed, seedBody, loopShift, liveUT, effUT);
                InGameAssert.IsTrue(synth.Sampled,
                    "SYNTHESIZED oracle must SAMPLE the rendered-vs-seed diff; skipReason="
                    + (synth.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(synth.Result.HasMeasurement,
                    "SYNTHESIZED oracle must yield a measurement");
                InGameAssert.IsFalse(synth.Result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "SYNTHESIZED oracle must read ZERO drift (the rendered orbit IS the spine's seed for a "
                        + "faithful StockConic member); maxDev={0:F1}m tol={1:F1}m.",
                        synth.Result.MaxDeviationMeters, synth.Result.ToleranceMeters));

                // --- ORACLE MODE 3: POLYLINE ORACLE ZERO-CONTRACT SANITY (NOT live polyline capture) ---
                // HONEST SCOPE: this diffs a real Unity-framed body-fixed leg arc (Kerbin world surface
                // positions) against ITSELF in the polyline lens (ParityMode.Synthesized, the same mode the
                // live polyline-leg capture uses). It pins the oracle's zero-contract (rendered == recorded
                // input yields zero drift) on live Unity-framed geometry; it is NOT live polyline capture
                // coverage - the live CaptureRenderedVsRecordedLegGeometry walk needs a populated
                // polylineCache from a real map render, which this harness cannot produce, and is validated
                // by tracing-on play sessions instead.
                double[] recordedLegArc = BuildKerbinFramedLegArc(kerbin);
                RenderParityOracle.ParityResult polyline = RenderParityOracle.ComputeDriftScaleDerived(
                    RenderParityOracle.ParityMode.Synthesized, recordedLegArc, recordedLegArc);
                InGameAssert.AreEqual(RenderParityOracle.ParityMode.Synthesized, polyline.Mode,
                    "the polyline lens runs in Synthesized mode (rendered leg vs recorded leg track)");
                InGameAssert.IsTrue(polyline.HasMeasurement,
                    "POLYLINE oracle zero-contract sanity must yield a measurement (else the sanity check "
                    + "is blind)");
                InGameAssert.IsFalse(polyline.OverTolerance,
                    "POLYLINE oracle zero-contract sanity (rendered == recorded input) must read ZERO drift; "
                    + "NOT live polyline capture coverage - the live leg-capture walk is validated by "
                    + "tracing-on play sessions");

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "FlagOnBaseline_AllThreeModes: pid={0} loopShift={1:F1} | faithfulDev={2:F1}m "
                    + "synthDev={3:F1}m polylineDev={4:F2}m | faithfulTol={5:F1}m synthTol={6:F1}m "
                    + "polylineTol={7:F2}m",
                    pid, loopShift, faithful.Result.MaxDeviationMeters, synth.Result.MaxDeviationMeters,
                    polyline.MaxDeviationMeters, faithful.Result.ToleranceMeters,
                    synth.Result.ToleranceMeters, polyline.ToleranceMeters));

                // Mirror the production emit decision: a known-good spine-ON ghost emits NO parity-drift
                // anomaly. The RunFrame pass above already ran with the trace sink live; assert no line fired.
                foreach (string line in capturedLines)
                    InGameAssert.IsFalse(
                        line.Contains("[MapRenderTrace]")
                        && line.Contains("reason=" + MapRenderTrace.AnomalyParityDrift),
                        "the spine-ON known-good baseline must emit NO parity-drift anomaly; saw: " + line);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.SuppressLogging = prevSuppress;
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("flagon-baseline-cleanup");
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

        // A real Unity-framed body-fixed leg arc (Kerbin's own world surface positions, body-relative so the
        // absolute frame cancels). The exact geometry is not load-bearing - it must be a finite multi-point
        // arc the polyline lens can diff against itself (rendered == recorded => zero drift). Mirrors
        // FailClosedFaithfulInGameTest.BuildJoolFramedArc.
        private static double[] BuildKerbinFramedLegArc(CelestialBody kerbin)
        {
            const int n = 8;
            var flat = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                double lon = i * 9.0;        // a spread of longitudes
                double alt = 40000.0;        // an atmospheric / descent-band altitude
                Vector3d p = kerbin.GetWorldSurfacePosition(0.0, lon, alt) - kerbin.position;
                flat[i * 3] = p.x;
                flat[i * 3 + 1] = p.y;
                flat[i * 3 + 2] = p.z;
            }
            return flat;
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
                RecordingId = "flagon-baseline-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek FlagOn Baseline",
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
