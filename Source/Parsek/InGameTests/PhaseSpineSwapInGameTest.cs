using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 3 (migration plan §5, the spine swap) - the in-game gate that drives the REAL wired
    // ShadowRenderDriver.RunFrame over a live faithful ghost with the typed PhaseChain spine ON vs OFF and
    // asserts the two are byte-identical AND both report ZERO parity-drift:
    //
    //  - FLAG OFF (default): RunFrame samples the legacy ChainAssembler-built GhostRenderChain and stamps
    //    the StockConic seed the icon-drive patch reads. The ghost stays on its recorded orbit -> the
    //    probe's ComputeFaithfulOrbitParity reports zero drift (unchanged from today).
    //  - FLAG ON (ForceSpineDriveForTesting): RunFrame samples the typed PhaseChain (PhaseFactory output,
    //    byte-matching the assembler from Phase 2) and stamps the SAME seed. The ghost renders identically
    //    -> zero drift, and the stamped seed elements match the flag-OFF seed exactly.
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
            Description = "Phase 3 spine-swap (FLAG ON): RunFrame driving the typed PhaseChain spine over a "
                + "live faithful ghost stamps a StockConic seed and reports ZERO parity-drift, identical to "
                + "the legacy assembler spine")]
        public void SpineSwap_FlagOn_DrivesPhaseChain_ZeroDrift_MatchesAssemblerSeed()
        {
            RunSpineSwapParity(forceSpineOn: true);
        }

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 3 spine-swap (FLAG OFF): RunFrame driving the legacy assembler spine over a "
                + "live faithful ghost reports ZERO parity-drift (unchanged baseline) - the flag-off path is "
                + "byte-identical to pre-Phase-3 behavior")]
        public void SpineSwap_FlagOff_DrivesAssembler_ZeroDrift()
        {
            // CLAIM SCOPE: this exercises the FLAG-OFF-WITH-TRACING-ON path. RunSpineSwapParity force-enables
            // MapRenderTrace so the factory PhaseChain is always built (the parity sink + EmitStructural are
            // live), which means the spine is built-but-not-consumed here, not fully inert. So this proves
            // "flag-off renders identically to the assembler baseline" — NOT "the spine path is wholly
            // elided." The TRACING-OFF full-inertness of flag-off normal play (the C# const-fold + the
            // null-PhaseChain assembler fallback) is locked at the source/unit layer
            // (ShadowRenderDriverTests.PhaseSpineDriveActive_*, RunFrame_FlagOffPath_*), not here.
            RunSpineSwapParity(forceSpineOn: false);
        }

        // Build the live ghost once, then drive RunFrame with the spine forced OFF and ON, reading back the
        // stamped StockConic seed each time + the parity result. Asserts: both stamp a seed, both report
        // zero drift, and the flag-ON seed elements byte-match the flag-OFF seed (the spine produced the
        // same drive).
        private static void RunSpineSwapParity(bool forceSpineOn)
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

            bool prevForceSpine = ShadowRenderDriver.ForceSpineDriveForTesting;
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

                // Drive the spine the requested way and read back the stamped seed.
                ShadowRenderDriver.ForceSpineDriveForTesting = forceSpineOn;
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                bool stamped = ShadowRenderDriver.TryGetFreshStockConicSeed(
                    pid, Time.frameCount, out OrbitSegment drivenSeed, out string drivenBody);

                // Also drive the OPPOSITE flag state so we can assert the two spines stamp the SAME seed
                // (the byte-identical-drive proof). Build a fresh scene/seed each pass.
                ShadowRenderDriver.ForceSpineDriveForTesting = !forceSpineOn;
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                bool otherStamped = ShadowRenderDriver.TryGetFreshStockConicSeed(
                    pid, Time.frameCount, out OrbitSegment otherSeed, out string otherBody);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "SpineSwap parity: forceSpineOn={0} stamped={1} body={2} sma={3:F0} ecc={4:F4} | "
                    + "other stamped={5} body={6} sma={7:F0} ecc={8:F4}",
                    forceSpineOn, stamped, drivenBody ?? "?", drivenSeed.semiMajorAxis, drivenSeed.eccentricity,
                    otherStamped, otherBody ?? "?", otherSeed.semiMajorAxis, otherSeed.eccentricity));

                InGameAssert.IsTrue(stamped,
                    "RunFrame must stamp a StockConic seed for a faithful orbital ghost (the drive the "
                    + "icon-drive patch reads); none stamped for forceSpineOn=" + forceSpineOn);
                InGameAssert.IsTrue(otherStamped,
                    "the opposite-flag RunFrame must ALSO stamp a seed (both spines drive the same ghost)");

                // The two spines must stamp the SAME conic drive (byte-identical seed): same body + elements.
                InGameAssert.AreEqual(drivenBody, otherBody,
                    "the two spines must stamp the same seed body");
                InGameAssert.ApproxEqual(drivenSeed.semiMajorAxis, otherSeed.semiMajorAxis, 1e-6,
                    "the two spines must stamp the same seed semiMajorAxis (byte-identical drive)");
                InGameAssert.ApproxEqual(drivenSeed.eccentricity, otherSeed.eccentricity, 1e-9,
                    "the two spines must stamp the same seed eccentricity");
                InGameAssert.ApproxEqual(drivenSeed.startUT, otherSeed.startUT, 1e-6,
                    "the two spines must stamp the same seed startUT");
                InGameAssert.ApproxEqual(drivenSeed.endUT, otherSeed.endUT, 1e-6,
                    "the two spines must stamp the same seed endUT");

                // Restore the requested flag state, drive once more so the live orbit reflects it, then run
                // the production parity seam and assert ZERO drift (the ghost stays on its recorded orbit
                // regardless of which spine drove it).
                ShadowRenderDriver.ForceSpineDriveForTesting = forceSpineOn;
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);

                Orbit renderedOrbit = ghost.orbitDriver.orbit;
                Vector3d iconBodyRel = ghost.GetWorldPos3D() - kerbin.position;
                MapRenderProbe.FaithfulParitySample sample = MapRenderProbe.ComputeFaithfulOrbitParity(
                    renderedOrbit, kerbin, iconBodyRel, liveUT, 0.0, liveUT, rec.RecordingId);
                RenderParityOracle.ParityResult result = sample.Result;

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "SpineSwap parity-drift: forceSpineOn={0} sampled={1} skip={2} hasMeas={3} maxDev={4:F1}m "
                    + "tol={5:F1}m over={6}",
                    forceSpineOn, sample.Sampled, sample.SkipReason ?? "(none)", result.HasMeasurement,
                    result.MaxDeviationMeters, result.ToleranceMeters, result.OverTolerance));

                InGameAssert.IsTrue(sample.Sampled,
                    "spine-swap parity must SAMPLE (not skip); skipReason=" + (sample.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(result.HasMeasurement,
                    "spine-swap parity must yield a MEASUREMENT (else the gate is blind)");
                InGameAssert.IsFalse(result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "spine-swap (forceSpineOn={0}) must report ZERO drift; maxDev={1:F1}m exceeded "
                        + "tol={2:F1}m - the swapped spine is rendering a different orbit than the assembler.",
                        forceSpineOn, result.MaxDeviationMeters, result.ToleranceMeters));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("spine-swap-cleanup");
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
