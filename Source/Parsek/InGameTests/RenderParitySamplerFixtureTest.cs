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

        // A1 cutover-instrument: the SYNTHESIZED-mode conic parity capture-harness, the sibling guard to
        // ParitySampler_CapturesHandComputedOrbitGeometry above. Exercises the REAL production orchestration
        // (MapRenderProbe.ComputeSynthesizedConicParity: BuildOrbitFromSegment of the producer's intended
        // seed, OrbitRelativePositionYup sampling of BOTH the rendered orbit and the intended reference, the
        // scale-derived tolerance, the oracle diff in ParityMode.Synthesized) against a live ghost. Two
        // arms: (1) intended seed == the rendered orbit's own elements -> the synthesized diff reads ~0
        // (rendered IS the intended arc, no anomaly); (2) intended seed with the SAME shape ROTATED in LAN
        // (a re-aim DRAW regression: the rendered orbit drew a different orientation than the seed it was
        // driven from) -> the diff flags OverTolerance. This proves the synthesized capture path is not
        // "green but blind". Career-independent; FLIGHT on a stock Kerbin pack.
        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "A1 synthesized-mode parity capture-harness: ComputeSynthesizedConicParity diffs a "
                + "live ghost's rendered conic against the producer's intended seed (~0 when seed==rendered, "
                + "flags drift when the seed is rotated off the rendered orbit)")]
        public void SynthesizedParity_CapturesRenderedVsIntendedConic()
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
            GhostMapPresence.RemoveAllGhostVessels("synth-parity-fixture-start");

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
                    InGameAssert.Skip("Ghost did not create in this context (no proto)");
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

                // Arm 1: intended seed == the rendered orbit's own elements (a faithful StockConic draw:
                // rendered IS the intended arc). The synthesized diff must measure and report ZERO drift.
                // loopShift 0.0: this non-loop ghost was created with loopEpochShiftSeconds 0.0, so the
                // phase-matched reference equals the raw-epoch reference (BuildOrbitFromSegment verbatim).
                MapRenderProbe.SynthesizedConicParitySample matched =
                    MapRenderProbe.ComputeSynthesizedConicParity(
                        renderedOrbit, kerbin, iconBodyRel, seg, KerbinBodyName, 0.0, liveUT, liveUT);
                InGameAssert.IsTrue(matched.Sampled,
                    "A matching intended seed must yield a synthesized parity measurement (skipReason="
                    + (matched.SkipReason ?? "(none)") + ")");
                InGameAssert.IsTrue(matched.Result.HasMeasurement,
                    "The synthesized faithful arm must produce a usable measurement");
                InGameAssert.IsFalse(matched.Result.OverTolerance,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "rendered == intended seed must read ~0 synthesized drift; maxDev={0:F0} m tol={1:F0} m",
                        matched.Result.MaxDeviationMeters, matched.Result.ToleranceMeters));

                // Arm 2: intended seed ROTATED 90 deg in LAN (same shape, drawn at a different orientation).
                // This is the re-aim DRAW regression the synthesized lens exists to catch: the rendered orbit
                // (LAN=0) diverges from the intended seed (LAN=90). The diff must flag OverTolerance.
                OrbitSegment rotatedSeed = seg;
                rotatedSeed.longitudeOfAscendingNode = 90.0;
                MapRenderProbe.SynthesizedConicParitySample rotated =
                    MapRenderProbe.ComputeSynthesizedConicParity(
                        renderedOrbit, kerbin, iconBodyRel, rotatedSeed, KerbinBodyName, 0.0, liveUT, liveUT);
                InGameAssert.IsTrue(rotated.Sampled,
                    "A rotated (still-on-Kerbin) intended seed must still yield a measurement, not skip");
                InGameAssert.IsTrue(rotated.Result.HasMeasurement,
                    "The synthesized rotated-seed arm must produce a usable measurement");
                InGameAssert.IsTrue(rotated.Result.OverTolerance,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "A rendered orbit drawn 90 deg off its intended seed LAN must flag synthesized "
                        + "parity drift; maxDev={0:F0} m tol={1:F0} m",
                        rotated.Result.MaxDeviationMeters, rotated.Result.ToleranceMeters));

                // Body-mismatch arm: a seed naming a different frame body is a stale-seed / SOI race and must
                // SKIP cleanly (no false anomaly).
                MapRenderProbe.SynthesizedConicParitySample wrongBody =
                    MapRenderProbe.ComputeSynthesizedConicParity(
                        renderedOrbit, kerbin, iconBodyRel, seg, "Mun", 0.0, liveUT, liveUT);
                InGameAssert.IsFalse(wrongBody.Sampled,
                    "A seed on a different frame body must skip cleanly (no synthesized diff)");

                ParsekLog.Info("TestRunner",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "SynthesizedParity_CapturesRenderedVsIntendedConic: pid={0} matchedDev={1:F0}m "
                        + "rotatedDev={2:F0}m tol={3:F0}m wrongBodySkip={4}",
                        pid, matched.Result.MaxDeviationMeters, rotated.Result.MaxDeviationMeters,
                        matched.Result.ToleranceMeters, wrongBody.SkipReason ?? "(none)"));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("synth-parity-fixture-cleanup");
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
            }
        }

        // A1 cutover-instrument (BLOCKER fix): the LOOPED synthesized-mode parity arm. The non-loop fixture
        // above (loopEpochShiftSeconds 0.0) never exercises the loop-shift epoch bake, so it was BLIND to the
        // false-drift blocker (the synthesized reference was built at the RAW intendedSeg.epoch while the
        // rendered conic is driven at seg.epoch + loopShift, so every LOOPED member traced a different mean-
        // anomaly half-arc and false-fired on a CORRECT draw). This arm drives a ghost whose live orbit epoch
        // IS baked with a NON-ZERO loop shift (read back from the production map via GetGhostOrbitEpochShift)
        // and asserts: (1) the PHASE-MATCHED synthesized diff (loopShift threaded in -> the reference epoch is
        // baked with the SAME shift) reads ~0 for a faithful loop draw (rendered == intended seed); and (2)
        // the fix is LOAD-BEARING - a deliberately phase-MISMATCHED reference (loopShift 0.0, the raw epoch,
        // i.e. the pre-fix behavior) WOULD flag drift on the SAME correct draw. Mirrors the faithful path's
        // loop-shifted in-game arm (RenderParityBaselineTest.ParityBaseline_LoopShiftedFaithfulGhost_*).
        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "A1 synthesized-mode parity (LOOP-SHIFTED): a ghost whose live orbit epoch is baked "
                + "with a NON-ZERO loop shift reads ~0 synthesized drift when the intended reference is phase-"
                + "matched (loopShift threaded in), and the raw-epoch (phase-MISMATCHED) reference WOULD drift "
                + "- the proof the false-drift blocker fix is load-bearing")]
        public void SynthesizedParity_LoopShiftedGhost_PhaseMatched_ZeroDrift()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            // The looped ghost replays a PAST recorded segment whose effUT = liveUT - shift. Author the
            // segment around that effUT so the seed is a real recorded arc, mirroring a real loop replay.
            double effUT = liveUT - LoopEpochShiftSeconds;
            double startUT = effUT - 1800.0;
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
            GhostMapPresence.RemoveAllGhostVessels("synth-parity-loop-fixture-start");

            uint pid = 0u;
            try
            {
                // Create the live ghost with the NON-ZERO loop epoch shift baked in: this seeds the rendered
                // OrbitDriver.orbit epoch to seg.epoch + shift (StockConicTreatment.SeedAndDriveLive) and
                // records the shift via GetGhostOrbitEpochShift(pid) - the EXACT orchestration the probe reads.
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
                    InGameAssert.Skip("Loop ghost did not create in this context (no proto)");
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

                // Read the loop shift the live orbit was ACTUALLY baked with from the production map (NOT a
                // local constant): proves the create path stored it and the probe reads back the same value
                // the rendered orbit was driven with.
                double loopShift = GhostMapPresence.GetGhostOrbitEpochShift(pid);
                InGameAssert.ApproxEqual(LoopEpochShiftSeconds, loopShift, 1.0,
                    "Loop ghost must record the loop epoch shift the production probe threads into the "
                    + "synthesized reference");

                // PHASE-MATCHED arm: intended seed == the rendered orbit's elements, reference built with the
                // SAME loopShift the rendered conic was driven with. A faithful loop draw must read ~0 drift.
                MapRenderProbe.SynthesizedConicParitySample phaseMatched =
                    MapRenderProbe.ComputeSynthesizedConicParity(
                        renderedOrbit, kerbin, iconBodyRel, seg, KerbinBodyName, loopShift, liveUT, effUT);
                InGameAssert.IsTrue(phaseMatched.Sampled,
                    "The phase-matched loop arm must yield a synthesized parity measurement (skipReason="
                    + (phaseMatched.SkipReason ?? "(none)") + ")");
                InGameAssert.IsTrue(phaseMatched.Result.HasMeasurement,
                    "The phase-matched loop arm must produce a usable measurement");
                InGameAssert.IsFalse(phaseMatched.Result.OverTolerance,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "A loop-shifted ghost drawing its intended seed must read ~0 synthesized drift when "
                        + "the reference is PHASE-MATCHED (loopShift={0:F1}); maxDev={1:F0} m tol={2:F0} m. A "
                        + "drift here is the false-drift blocker re-appearing.",
                        loopShift, phaseMatched.Result.MaxDeviationMeters,
                        phaseMatched.Result.ToleranceMeters));

                // LOAD-BEARING negative control: build the reference with loopShift 0.0 (the raw epoch, the
                // PRE-FIX behavior) against the SAME correct rendered loop draw. The phase no longer cancels,
                // so the two orbits trace different mean-anomaly arcs and the diff MUST flag OverTolerance.
                // Without this the phase-match could be a no-op (e.g. a tautological circle-vs-itself) and the
                // arm would pass green-but-blind.
                MapRenderProbe.SynthesizedConicParitySample phaseMismatched =
                    MapRenderProbe.ComputeSynthesizedConicParity(
                        renderedOrbit, kerbin, iconBodyRel, seg, KerbinBodyName, 0.0, liveUT, effUT);
                InGameAssert.IsTrue(phaseMismatched.Sampled,
                    "The phase-mismatched control arm must still SAMPLE (it is a real diff, not a skip)");
                InGameAssert.IsTrue(phaseMismatched.Result.HasMeasurement,
                    "The phase-mismatched control arm must produce a usable measurement");
                InGameAssert.IsTrue(phaseMismatched.Result.OverTolerance,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "A raw-epoch (phase-MISMATCHED, loopShift=0) reference against a loop-shifted rendered "
                        + "draw MUST flag synthesized drift - proving the phase-match is load-bearing; "
                        + "maxDev={0:F0} m tol={1:F0} m. If this reads ~0 the loopShift is not actually moving "
                        + "the reference phase (the test is tautological).",
                        phaseMismatched.Result.MaxDeviationMeters,
                        phaseMismatched.Result.ToleranceMeters));

                ParsekLog.Info("TestRunner",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "SynthesizedParity_LoopShiftedGhost_PhaseMatched_ZeroDrift: pid={0} loopShift={1:F1} "
                        + "matchedDev={2:F0}m mismatchedDev={3:F0}m tol={4:F0}m",
                        pid, loopShift, phaseMatched.Result.MaxDeviationMeters,
                        phaseMismatched.Result.MaxDeviationMeters,
                        phaseMatched.Result.ToleranceMeters));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("synth-parity-loop-fixture-cleanup");
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
            }
        }

        // A deliberately non-zero loop epoch shift (seconds) for the LOOP-SHIFTED synthesized arm: ~18 min, a
        // sizeable fraction of the orbit period at Sma, so the rendered conic sits well around the orbit from
        // the raw recorded phase. With the BLOCKER bug present (raw-epoch reference) the phase-matched and
        // raw-epoch references diverge by ~orbit-diameter at this shift; the fix collapses the phase-matched
        // case to ~0 while the raw-epoch control still drifts.
        private const double LoopEpochShiftSeconds = 1100.0;

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
