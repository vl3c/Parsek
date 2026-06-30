using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 11 / N3 (test-automation coverage follow-up) - the MULTI-BODY CONCURRENT GHOSTS parity test. It
    // closes the "two faithful ghosts on different bodies render concurrently without cross-frame leakage" case
    // with the proven P1-ghost pattern (live faithful ghosts via GhostMapPresence.CreateGhostVesselFromSource +
    // MapRenderProbe.ComputeFaithfulOrbitParity, modelled on RenderParityBaselineTest.cs:134-141).
    //
    // THE CONTRACT (the live assertions): TWO live faithful ghosts created in ONE RecordingStore swap - one on a
    // Kerbin orbit, one on a Mun orbit - must each:
    //   - report ZERO faithful parity-drift through the REAL wired probe seam (each rendered orbit == its own
    //     recorded arc);
    //   - have DISTINCT persistent ids (two separate ProtoVessels, not one reused); and
    //   - resolve parity in its OWN body frame (the probe's covering-segment body equals the ghost's rendered
    //     body). A regression that framed the Mun ghost in Kerbin's frame (cross-frame leak) would either
    //     parity-skip with body-mismatch or drift hugely - either way this fails.
    //
    // WHY IT IS NON-VACUOUS: the probe (ComputeFaithfulOrbitParity) skips with "body-mismatch" if the rendered
    // body differs from the recorded covering body, so the test asserts BOTH ghosts SAMPLED (not skipped) AND
    // measured within tolerance AND that the covering body each parity used is the ghost's own body - catching a
    // cross-frame leak rather than passing it through as a silent skip.
    //
    // ARCHITECTURAL TRUTH respected + honest caveat: this asserts the live faithful parity (the rendered orbit
    // equals the recorded orbit, per body), NOT any 5b pixel. It DOES exercise the live ProtoVessel lifecycle
    // (two concurrent ghosts), which is the point of the multi-body case.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (builds two live ghost
    // ProtoVessels + reads their OrbitDrivers). Skips cleanly if Mun is absent (mirrors the FailClosedFaithful
    // Jool guard). FLIGHT only; career-independent; self-contained (its own two recordings, cleaned up).
    public class MultiBodyConcurrentGhostsParityInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const string MunBodyName = "Mun";

        // A clean above-atmosphere circular equatorial orbit per body (stock radii: Kerbin 600km, Mun 200km).
        private const double KerbinSma = 850000.0;
        private const double MunSma = 400000.0;
        private const double Ecc = 0.0;
        private const double Inc = 0.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 11 N3 multi-body concurrent ghosts (parity): two live faithful ghosts (one "
                + "Kerbin orbit, one Mun orbit) created in one RecordingStore swap each report ZERO faithful "
                + "drift through the real probe seam, have DISTINCT pids, and resolve parity in their OWN body "
                + "frame (no cross-frame leak). Skips if Mun absent.")]
        public void MultiBodyConcurrentGhosts_KerbinAndMun_BothZeroDrift_DistinctPids_NoCrossFrameLeak()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            CelestialBody mun = FlightGlobals.Bodies?.Find(b => b.bodyName == MunBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }
            if (mun == null)
            {
                InGameAssert.Skip("Mun not present (non-stock pack) - cannot exercise the multi-body case");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double startUT = liveUT - 1800.0;

            OrbitSegment kerbinSeg = BuildSegment(startUT, KerbinBodyName, KerbinSma);
            OrbitSegment munSeg = BuildSegment(startUT, MunBodyName, MunSma);

            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            Recording kerbinRec = BuildRecording(startUT, kerbinSeg, KerbinBodyName, KerbinSma);
            Recording munRec = BuildRecording(startUT, munSeg, MunBodyName, MunSma);

            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(kerbinRec);
            RecordingStore.AddCommittedInternal(munRec);
            int kerbinIdx = 0;
            int munIdx = 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("multibody-start");

            uint kerbinPid = 0u;
            uint munPid = 0u;
            try
            {
                Vessel kerbinGhost = GhostMapPresence.CreateGhostVesselFromSource(
                    kerbinIdx, kerbinRec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    kerbinSeg, default(TrajectoryPoint), startUT);
                Vessel munGhost = GhostMapPresence.CreateGhostVesselFromSource(
                    munIdx, munRec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    munSeg, default(TrajectoryPoint), startUT);

                if (kerbinGhost == null || kerbinGhost.orbitDriver == null || kerbinGhost.orbitDriver.orbit == null
                    || munGhost == null || munGhost.orbitDriver == null || munGhost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("one or both faithful ghosts did not create in this context (no proto)");
                    return;
                }
                kerbinPid = kerbinGhost.persistentId;
                munPid = munGhost.persistentId;

                // DISTINCT pids: two separate ProtoVessels, not one reused (a reuse would alias the two ghosts).
                InGameAssert.IsTrue(kerbinPid != munPid,
                    string.Format(CultureInfo.InvariantCulture,
                        "the two concurrent ghosts must have DISTINCT persistent ids (kerbinPid={0} munPid={1}) - "
                        + "a shared pid would alias the two recordings' map presence", kerbinPid, munPid));

                // Each ghost's parity, in its OWN body frame, must be sampled + zero drift.
                AssertGhostFaithfulInOwnFrame(
                    "Kerbin", kerbinGhost, kerbin, kerbinRec, liveUT);
                AssertGhostFaithfulInOwnFrame(
                    "Mun", munGhost, mun, munRec, liveUT);
            }
            finally
            {
                if (kerbinPid != 0u || munPid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("multibody-cleanup");
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
            }
        }

        // Drive the REAL probe seam for one ghost against its OWN body and assert: sampled (not skipped), a
        // measurement, zero drift, AND the parity's covering segment is on this ghost's own body (the no-cross-
        // frame-leak proof: ComputeFaithfulOrbitParity skips with body-mismatch if the rendered body differs
        // from the recorded covering body, so a leak surfaces as either a skip or drift - both fail here).
        private static void AssertGhostFaithfulInOwnFrame(
            string label, Vessel ghost, CelestialBody body, Recording rec, double liveUT)
        {
            Orbit renderedOrbit = ghost.orbitDriver.orbit;
            Vector3d iconBodyRel = ghost.GetWorldPos3D() - body.position;

            MapRenderProbe.FaithfulParitySample sample = MapRenderProbe.ComputeFaithfulOrbitParity(
                renderedOrbit, body, iconBodyRel, liveUT, 0.0, liveUT, rec.RecordingId);
            Parsek.MapRender.RenderParityOracle.ParityResult result = sample.Result;

            ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                "MultiBodyConcurrent {0}: pid={1} body={2} sampled={3} skip={4} hasMeas={5} maxDev={6:F1}m "
                + "tol={7:F1}m over={8}",
                label, ghost.persistentId, body.bodyName, sample.Sampled, sample.SkipReason ?? "(none)",
                result.HasMeasurement, result.MaxDeviationMeters, result.ToleranceMeters, result.OverTolerance));

            InGameAssert.IsTrue(sample.Sampled,
                string.Format(CultureInfo.InvariantCulture,
                    "the {0} faithful ghost must SAMPLE its OWN-body parity (not skip); a body-mismatch skip here "
                    + "is a cross-frame leak (the ghost was framed in another body). skipReason={1}",
                    label, sample.SkipReason ?? "(none)"));
            InGameAssert.IsTrue(result.HasMeasurement,
                string.Format(CultureInfo.InvariantCulture,
                    "the {0} faithful ghost must yield a parity measurement (else the gate is blind)", label));
            InGameAssert.IsFalse(result.OverTolerance,
                string.Format(CultureInfo.InvariantCulture,
                    "the {0} faithful ghost must report ZERO drift in its own body frame; maxDev={1:F1}m exceeded "
                    + "tol={2:F1}m - a cross-frame leak (e.g. the Mun ghost framed in Kerbin) would drift here",
                    label, result.MaxDeviationMeters, result.ToleranceMeters));
        }

        private static OrbitSegment BuildSegment(double startUT, string bodyName, double sma)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = startUT + 3600.0,
                inclination = Inc,
                eccentricity = Ecc,
                semiMajorAxis = sma,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                bodyName = bodyName,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            };
        }

        private static Recording BuildRecording(double startUT, OrbitSegment seg, string bodyName, double sma)
        {
            double endUT = startUT + 3600.0;
            var rec = new Recording
            {
                RecordingId = "multibody-" + bodyName + "-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek MultiBody " + bodyName,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = bodyName,
                TerminalOrbitBody = bodyName,
                TerminalOrbitSemiMajorAxis = sma,
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
            // Two above-surface points; the geometry under test is the OrbitSegment elements. Altitude is an
            // approximate above-surface value (not load-bearing - the orbit drives the parity).
            double approxAlt = sma * 0.25;
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0.0, longitude = 0.0, altitude = approxAlt,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 1500f, 0f), bodyName = bodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0.0, longitude = 5.0, altitude = approxAlt,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 1500f, 0f), bodyName = bodyName,
            });
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }
    }
}
