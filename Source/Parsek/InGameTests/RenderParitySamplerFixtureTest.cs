using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 0 / design §14: the recorded-vs-rendered PARITY SAMPLER capture-harness (the review's
    // "green but blind" guard). The RenderParityOracle's diff MATH and the RenderGeometrySampler's pure
    // reframing/flattening are unit-tested headless; this in-game fixture is the SEPARATE guard that the
    // Unity CAPTURE PATH itself is correct - i.e. that the geometry MapRenderProbe reads off a live ghost
    // (the rendered OrbitDriver.orbit via OrbitRelativePositionYup, the icon point via GetWorldPos3D -
    // body.position, and the recorded reference via BuildOrbitFromSegment) lands where a HAND-COMPUTED
    // orbit at known elements says it should. Without this, a sampler that read the wrong frame / wrong
    // clock would still pass every diff-math unit test (both sides wrong the same way) yet be blind in
    // game.
    //
    // It deliberately does NOT touch the oracle: it asserts the captured Vector3d positions directly
    // against an independent KSP Orbit built from the SAME known elements. Career-independent; runs in
    // FLIGHT on a stock Kerbin pack. NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot
    // run headless.
    public class RenderParitySamplerFixtureTest
    {
        private const string KerbinBodyName = "Kerbin";

        // A clean, unambiguously-orbital, fully-above-atmosphere CIRCULAR equatorial Kerbin orbit: ecc=0
        // and inc=0 make the hand-computed reference trivial - every body-relative position has magnitude
        // == sma and lies in the equatorial (XZ, Y-up world) plane.
        private const double Sma = 800000.0;
        private const double Ecc = 0.0;
        private const double Inc = 0.0;

        // Capture-vs-hand-computed tolerances. The capture must land essentially exactly on the
        // independent Orbit (same elements, same body, same UT): a few metres of float slack on an
        // 800 km-radius orbit. The "icon lies on its own captured orbit line" check is the cross-surface
        // agreement: the icon point and the captured orbit curve are both rendered from the live
        // OrbitDriver, so they must coincide closely.
        private const double CapturePositionToleranceMeters = 50.0;
        private const double IconOnLineToleranceMeters = 2000.0;

        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "Phase 0 parity capture-harness: the MapRenderProbe Unity sampler captures a live "
                + "ghost's rendered orbit / icon geometry and the recorded reference at the SAME hand-"
                + "computed positions (guards the capture path, separate from the oracle diff-math tests)")]
        public void ParitySampler_CapturesHandComputedOrbitGeometry()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double startUT = liveUT;
            OrbitSegment seg = BuildSegment(startUT);

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
            GhostMapPresence.RemoveAllGhostVessels("parity-sampler-fixture-start");

            uint pid = 0u;
            try
            {
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex,
                    rec,
                    GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg,
                    default(TrajectoryPoint),
                    startUT,
                    loopEpochShiftSeconds: 0.0);

                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("Faithful ghost did not create in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;
                InGameAssert.IsTrue(GhostMapPresence.IsGhostMapVessel(pid),
                    "Freshly created faithful ghost must be registered as a ghost map vessel");

                Orbit renderedOrbit = ghost.orbitDriver.orbit;

                // --- Capture surface 1: the rendered orbit-line geometry (OrbitRelativePositionYup, the
                // EXACT production capture). A circular equatorial orbit's body-relative samples must each
                // have magnitude == sma and (near-)zero out-of-plane (Y, Y-up world) component. ---
                int n = 5;
                for (int i = 0; i < n; i++)
                {
                    double ut = startUT + i * 600.0; // five samples across ~50 min of the orbit
                    Vector3d capturedRel = MapRenderProbe.OrbitRelativePositionYup(renderedOrbit, ut);
                    double r = capturedRel.magnitude;
                    InGameAssert.ApproxEqual(Sma, r, CapturePositionToleranceMeters,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Captured rendered orbit sample {0} radius must equal the known sma {1:F0} m "
                            + "(circular), was {2:F0} m. The capture is reading the wrong frame/clock.",
                            i, Sma, r));
                    InGameAssert.IsLessThan(System.Math.Abs(capturedRel.y), CapturePositionToleranceMeters,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Captured rendered equatorial (inc=0) orbit sample {0} must lie in the Y-up "
                            + "equatorial plane (|y| ~ 0), was y={1:F1} m.",
                            i, capturedRel.y));
                }

                // --- Capture surface 2: the recorded reference reconstruction (BuildOrbitFromSegment, the
                // EXACT production construction) sampled through the SAME OrbitRelativePositionYup must match
                // the rendered orbit at the same UT (faithful: recorded == rendered). ---
                CelestialBody recordedBody = MapRenderProbe.ResolveBodyByName(seg.bodyName);
                InGameAssert.IsNotNull(recordedBody, "Recorded segment body must resolve");
                Orbit recordedOrbit = MapRenderProbe.BuildOrbitFromSegment(seg, recordedBody);
                InGameAssert.IsNotNull(recordedOrbit,
                    "BuildOrbitFromSegment must reconstruct a usable Orbit from the recorded segment");

                double refCheckUT = startUT + 1200.0;
                Vector3d renderedAt = MapRenderProbe.OrbitRelativePositionYup(renderedOrbit, refCheckUT);
                Vector3d recordedAt = MapRenderProbe.OrbitRelativePositionYup(recordedOrbit, refCheckUT);
                double refDelta = (renderedAt - recordedAt).magnitude;
                InGameAssert.IsLessThan(refDelta, CapturePositionToleranceMeters,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "For a FAITHFUL ghost the recorded reference orbit (BuildOrbitFromSegment) and the "
                        + "rendered OrbitDriver.orbit must coincide at the same UT; captured |delta|={0:F1} m "
                        + "exceeds {1:F0} m. rendered={2} recorded={3}",
                        refDelta, CapturePositionToleranceMeters, renderedAt, recordedAt));

                // --- Cross-surface agreement: the rendered ICON point (GetWorldPos3D - body.position, the
                // probe's bodyRelPos capture) must lie ON the captured rendered orbit curve at the icon's
                // resolved clock (here the live clock, shift 0). This proves the icon-point capture and the
                // orbit-curve capture are in the SAME frame - the precondition for the parity diff. ---
                Vector3d iconBodyRel = ghost.GetWorldPos3D() - kerbin.position;
                if (iconBodyRel.magnitude < 1.0)
                {
                    InGameAssert.Skip("Ghost world position not resolved on the creation frame");
                    return;
                }
                Vector3d orbitAtLive = MapRenderProbe.OrbitRelativePositionYup(renderedOrbit, liveUT);
                double iconOnLine = (iconBodyRel - orbitAtLive).magnitude;

                ParsekLog.Info("TestRunner",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "ParitySampler_CapturesHandComputedOrbitGeometry: pid={0} sma={1:F0} ecc={2:F2} "
                        + "iconR={3:F0} orbitAtLiveR={4:F0} iconOnLineDelta={5:F0}m refDelta={6:F1}m",
                        pid, Sma, Ecc, iconBodyRel.magnitude, orbitAtLive.magnitude, iconOnLine, refDelta));

                InGameAssert.IsLessThan(iconOnLine, IconOnLineToleranceMeters,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "The rendered icon point (GetWorldPos3D - body.position) must lie on the captured "
                        + "rendered orbit curve at the icon's clock; captured |delta|={0:F0} m exceeds "
                        + "{1:F0} m. The icon-point capture and the orbit-curve capture are in different "
                        + "frames, so the parity diff would be meaningless.",
                        iconOnLine, IconOnLineToleranceMeters));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("parity-sampler-fixture-cleanup");
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
                RecordingId = "parity-sampler-fixture-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Parity Sampler Fixture",
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
                altitude = Sma - kerbinRadiusFallback,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2200f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                latitude = 0.0,
                longitude = 5.0,
                altitude = Sma - kerbinRadiusFallback,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2200f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }

        // Only used to author plausible TrajectoryPoint altitudes; the orbit geometry under test is driven
        // entirely by the OrbitSegment elements, so the exact value is not load-bearing.
        private const double kerbinRadiusFallback = 600000.0;
    }
}
