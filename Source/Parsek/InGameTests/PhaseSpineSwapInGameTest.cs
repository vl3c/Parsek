using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 3 origin (migration plan section 5, the spine swap), reworked at Phase 5b (the cutover flag +
    // its test seam were REMOVED - the typed PhaseChain spine is UNCONDITIONAL): the in-game gate that
    // drives the REAL wired ShadowRenderDriver.RunFrame over a live faithful ghost and asserts the spine
    // (1) actually BUILT + drove a typed PhaseChain (not the loud-warned assembler exception fallback),
    // (2) stamped the StockConic seed the icon-drive patch reads, and (3) reports ZERO parity-drift via
    // the production faithful oracle. The old flag-ON-vs-flag-OFF A/B seed comparison died with the flag
    // (there is no second spine to compare against; the assembler chain survives only as the exception
    // fallback for a factory throw, pinned by the HasCachedPhaseChainForTesting false-green guard here).
    //
    // This complements the headless PhaseSpineParityTests (which prove the sampler/director intent parity
    // pure) by exercising the Unity-coupled RunFrame -> seed-stamp -> probe path the pure tests cannot
    // reach. Runs the SAME internal seam production uses (RunFrame + TryGetFreshStockConicSeed +
    // ComputeFaithfulOrbitParity), so it is not a green-but-blind inlined copy.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (builds a live ghost
    // ProtoVessel + reads its OrbitDriver + drives RunFrame against a live MapViewScene). FLIGHT only;
    // career-independent.
    public class PhaseSpineSwapInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double Sma = 850000.0;
        private const double Ecc = 0.0;
        private const double Inc = 0.0;
        private const double KerbinRadiusFallback = 600000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 3/5b spine drive: RunFrame driving the unconditional typed PhaseChain "
                + "spine over a live faithful ghost BUILDS a PhaseChain (no exception fallback), stamps a "
                + "StockConic seed, and reports ZERO parity-drift")]
        public void SpineDrive_Unconditional_BuildsPhaseChain_StampsSeed_ZeroDrift()
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

            Recording rec = BuildRecording(startUT, seg);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("spine-swap-start");
            ShadowRenderDriver.Reset();

            uint pid = 0u;
            try
            {
                MapRenderTrace.ForceEnabledForTesting = true; // so EmitStructural / parity sink are live

                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex, rec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg, default(TrajectoryPoint), startUT, loopEpochShiftSeconds: 0.0);

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

                // Drive the unconditional spine and read back the stamped seed.
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                bool stamped = ShadowRenderDriver.TryGetFreshStockConicSeed(
                    pid, Time.frameCount, out OrbitSegment drivenSeed, out string drivenBody);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "SpineDrive: stamped={0} body={1} sma={2:F0} ecc={3:F4}",
                    stamped, drivenBody ?? "?", drivenSeed.semiMajorAxis, drivenSeed.eccentricity));

                InGameAssert.IsTrue(stamped,
                    "RunFrame must stamp a StockConic seed for a faithful orbital ghost (the drive the "
                    + "icon-drive patch reads)");

                // False-green guard: GetOrBuildChain swallows a factory throw into a cached null
                // PhaseChain and the spine then falls back to the loud-warned legacy assembler chain, so
                // a stamped seed + zero drift alone cannot prove the SPINE drove.
                InGameAssert.IsTrue(ShadowRenderDriver.HasCachedPhaseChainForTesting(pid),
                    "the spine must have BUILT a PhaseChain for this ghost - a null cache means the "
                    + "factory threw and the assembler exception fallback drove (this gate would "
                    + "otherwise pass on a false green)");

                // The seed must be the recorded segment's elements (the faithful drive).
                InGameAssert.AreEqual(KerbinBodyName, drivenBody,
                    "the spine must stamp the recorded segment's body");
                InGameAssert.ApproxEqual(Sma, drivenSeed.semiMajorAxis, 1e-6,
                    "the spine must stamp the recorded segment's semiMajorAxis");
                InGameAssert.ApproxEqual(Ecc, drivenSeed.eccentricity, 1e-9,
                    "the spine must stamp the recorded segment's eccentricity");

                Orbit renderedOrbit = ghost.orbitDriver.orbit;
                MapRenderProbe.FaithfulParitySample sample = MapRenderProbe.ComputeFaithfulOrbitParity(
                    renderedOrbit, kerbin, 0.0, liveUT, rec.RecordingId);
                RenderParityOracle.ParityResult result = sample.Result;

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "SpineDrive parity-drift: sampled={0} skip={1} hasMeas={2} maxDev={3:F1}m "
                    + "tol={4:F1}m over={5}",
                    sample.Sampled, sample.SkipReason ?? "(none)", result.HasMeasurement,
                    result.MaxDeviationMeters, result.ToleranceMeters, result.OverTolerance));

                InGameAssert.IsTrue(sample.Sampled,
                    "spine parity must SAMPLE (not skip); skipReason=" + (sample.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(result.HasMeasurement,
                    "spine parity must yield a MEASUREMENT (else the gate is blind)");
                InGameAssert.IsFalse(result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "the spine must report ZERO drift; maxDev={0:F1}m exceeded tol={1:F1}m - the "
                        + "spine is rendering a different orbit than the recorded segment.",
                        result.MaxDeviationMeters, result.ToleranceMeters));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("spine-swap-cleanup");
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

        private static Recording BuildRecording(double startUT, OrbitSegment seg)
        {
            double endUT = startUT + 3600.0;
            var rec = new Recording
            {
                RecordingId = "spine-swap-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Spine Swap",
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
