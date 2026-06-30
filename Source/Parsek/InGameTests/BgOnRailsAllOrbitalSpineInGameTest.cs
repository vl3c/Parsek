using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 11 / N5 (test-automation coverage follow-up) - the BG-ON-RAILS ALL-ORBITAL spine test. A
    // background / on-rails member emits NO env-classified per-frame TrackSections and no atmospheric Points; it
    // is orbit-bridge-only (see the on-rails BG invariant in CLAUDE.md). The render pipeline must build an
    // all-orbital chain for such a member - Loiter/Transfer conics with NO Descent/Surface phase - and never
    // assert on the absent SegmentPhase data (PhaseFactory design §11.3).
    //
    // It combines the two proven patterns:
    //   - P2-pure: build an all-OrbitSegment recording (NO atmospheric Points), run the REAL
    //     PhaseFactory.BuildPhaseChain over it, and assert the chain has ZERO DescentPhase / SurfacePhase /
    //     AscentPhase (only conic phases) and at least one conic phase. A regression that synthesized a
    //     traced Descent/Surface phase for an orbit-only member (e.g. by reading absent track sections as
    //     surface) would fail here.
    //   - P1-ghost: create the live faithful ghost on the same orbit and assert
    //     MapRenderProbe.ComputeFaithfulOrbitParity reports ZERO drift (the rendered conic == the recorded
    //     conic).
    //
    // ARCHITECTURAL TRUTH respected + honest caveat: this asserts the factory's all-orbital CLASSIFICATION (no
    // traced phases for an orbit-only member) + the live faithful parity, NOT any 5b pixel. The factory
    // classification is unit-tested headlessly; this runs the SAME PhaseFactory.BuildPhaseChain over a real
    // orbit-only recording and pairs it with a live ghost.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); the parity half builds a live ghost
    // ProtoVessel so it cannot run headless. FLIGHT only; career-independent; self-contained.
    public class BgOnRailsAllOrbitalSpineInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double Sma = 900000.0;   // well above Kerbin's 600km surface (a high circular park)
        private const double Ecc = 0.0;
        private const double Inc = 0.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 11 N5 BG-on-rails all-orbital (spine): PhaseFactory.BuildPhaseChain over an "
                + "all-OrbitSegment recording (no atmospheric Points / track sections) yields ONLY conic phases "
                + "(zero Descent/Surface/Ascent) and the live faithful ghost on that orbit reports ZERO parity-"
                + "drift. Asserts the factory all-orbital classification + live parity, not any 5b pixel.")]
        public void AllOrbitalRecording_FactoryYieldsConicOnly_AndLiveGhostZeroDrift()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double startUT = liveUT - 1800.0;
            double endUT = startUT + 3600.0;
            OrbitSegment seg = BuildSegment(startUT);

            // An ALL-ORBITAL recording: ONE above-surface OrbitSegment, NO atmospheric Points, NO TrackSections
            // (the on-rails BG shape). The factory must build a conic-only chain.
            Recording rec = BuildAllOrbitalRecording(startUT, endUT, seg);

            // --- (P2-pure) THE FACTORY ALL-ORBITAL CLASSIFICATION: conic phases only, no traced phases. ---
            // surface=null (the default): IsOrbitSegmentBelowSurface returns false for a null provider, so the
            // above-surface orbit stays a StockConic (it is genuinely above Kerbin's radius anyway). With no
            // Points, the assembler produces no TracedPath runs, so the factory classifies ONLY conic phases.
            PhaseChain chain = PhaseFactory.BuildPhaseChain(
                rec, committedIndex: 0, instanceKey: 0, windowStartUT: startUT, windowEndUT: endUT);

            int conicPhases = 0;
            int descentPhases = 0;
            int surfacePhases = 0;
            int ascentPhases = 0;
            for (int i = 0; i < chain.Phases.Count; i++)
            {
                TrajectoryPhase p = chain.Phases[i];
                if (p is DescentPhase) descentPhases++;
                else if (p is SurfacePhase) surfacePhases++;
                else if (p is AscentPhase) ascentPhases++;
                else if (p is ConicPhase || p is DualTreatmentConicPhase) conicPhases++;
            }

            ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                "BgOnRailsAllOrbital: phases={0} conic={1} descent={2} surface={3} ascent={4} "
                + "window=[{5:F1},{6:F1}]",
                chain.Phases.Count, conicPhases, descentPhases, surfacePhases, ascentPhases, startUT, endUT));

            InGameAssert.AreEqual(0, descentPhases,
                "an all-orbital (orbit-bridge-only) recording must produce NO DescentPhase - the factory must "
                + "not synthesize a traced descent for an orbit-only member (design §11.3)");
            InGameAssert.AreEqual(0, surfacePhases,
                "an all-orbital recording must produce NO SurfacePhase (no recorded surface/atmospheric run)");
            InGameAssert.AreEqual(0, ascentPhases,
                "an all-orbital recording must produce NO AscentPhase (no recorded ascent/atmospheric run)");
            InGameAssert.IsTrue(conicPhases >= 1,
                string.Format(CultureInfo.InvariantCulture,
                    "an all-orbital recording must produce at least one CONIC phase (saw {0}); a conic-free "
                    + "chain here would mean the above-surface orbit was wrongly dropped", conicPhases));

            // --- (P1-ghost) THE LIVE FAITHFUL PARITY: the ghost on this orbit reports zero drift. ---
            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = 0;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("bgonrails-start");

            uint pid = 0u;
            try
            {
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex, rec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg, default(TrajectoryPoint), startUT);

                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("all-orbital faithful ghost did not create in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;

                Orbit renderedOrbit = ghost.orbitDriver.orbit;
                Vector3d iconBodyRel = ghost.GetWorldPos3D() - kerbin.position;

                MapRenderProbe.FaithfulParitySample sample = MapRenderProbe.ComputeFaithfulOrbitParity(
                    renderedOrbit, kerbin, iconBodyRel, liveUT, 0.0, liveUT, rec.RecordingId);
                Parsek.MapRender.RenderParityOracle.ParityResult result = sample.Result;

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "BgOnRailsAllOrbital parity: pid={0} sampled={1} skip={2} hasMeas={3} maxDev={4:F1}m "
                    + "tol={5:F1}m over={6}",
                    pid, sample.Sampled, sample.SkipReason ?? "(none)", result.HasMeasurement,
                    result.MaxDeviationMeters, result.ToleranceMeters, result.OverTolerance));

                InGameAssert.IsTrue(sample.Sampled,
                    "the all-orbital faithful ghost must SAMPLE its parity (not skip); skipReason="
                    + (sample.SkipReason ?? "(none)"));
                InGameAssert.IsTrue(result.HasMeasurement,
                    "the all-orbital faithful ghost must yield a parity measurement (else the gate is blind)");
                InGameAssert.IsFalse(result.OverTolerance,
                    string.Format(CultureInfo.InvariantCulture,
                        "the all-orbital faithful ghost must report ZERO drift; maxDev={0:F1}m exceeded "
                        + "tol={1:F1}m", result.MaxDeviationMeters, result.ToleranceMeters));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("bgonrails-cleanup");
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

        // An ALL-ORBITAL recording (the on-rails BG shape): ONE above-surface OrbitSegment, NO atmospheric
        // Points, NO TrackSections. The assembler produces no traced runs (no Points), so the factory classifies
        // only conic phases.
        private static Recording BuildAllOrbitalRecording(double startUT, double endUT, OrbitSegment seg)
        {
            var rec = new Recording
            {
                RecordingId = "bgonrails-allorbital-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek BG OnRails AllOrbital",
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
            // NO Points, NO TrackSections (orbit-bridge-only). One above-surface conic.
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }
    }
}
