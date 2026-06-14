using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Bug A (loop-member ghost map icon renders far off its own orbit line at high warp).
    //
    // The defect: a looped transfer ghost's state-vector gap-glide reseed
    // (GhostMapPresence.UpdateGhostOrbitFromStateVectors) runs in a Parsek per-frame pass AFTER the
    // frame's VesselPrecalculate.FixedUpdate. It reseeds the orbit elements and calls
    // updateFromParameters() (which propagates orbitDriver.pos to the live clock and SetPositions the
    // TRANSFORM), but the value the map icon actually renders at - Vessel.CoMD, returned by
    // GetWorldPos3D - is only refreshed by VesselPrecalculate.CalculatePhysicsStats, which already ran
    // this frame BEFORE the reseed. So on the reseed frame the cached CoMD still holds the PRE-reseed
    // phase while the orbit LINE is drawn from the freshly-reseeded conic; at high warp the
    // (currentUT - reseed-epoch) gap leaves the icon up to ~168 deg off its own line for one frame
    // until the next FixedUpdate (the loopShift=0.0 icon-off-orbit / icon-teleport anomaly family,
    // rec 04177 idx 43). GetWorldPos3D self-recomputes CoMD only while !firstStatsRunComplete (true
    // only at creation) - which is exactly why ForceImmediateIconDrive fixes the CREATION frame but a
    // steady-state re-drive does not.
    //
    // THE FIX it guards: after the reseed, GhostMapPresence.UpdateGhostOrbitFromStateVectors forces a
    // VesselPrecalculate.CalculatePhysicsStats() (gated to the conic-change seam) so CoMD is re-snapped
    // onto the just-reseeded conic THIS frame (for a packed orbiting ghost it sets
    // CoMD = mainBody.position + orbitDriver.pos). A second updateFromParameters() would NOT fix it -
    // it re-moves the transform but never touches the cached CoMD GetWorldPos3D returns (the no-op
    // trap this test refutes at runtime).
    //
    // WHAT THIS ASSERTS: it reproduces the production reseed sequence on a real packed ghost
    // (OrbitReseed - the same helper the production path uses - then updateFromParameters), proves the
    // cached CoMD is STALE (the icon sits >20 deg off the new conic), then applies the fix's exact
    // operation (CalculatePhysicsStats) and proves the icon re-snaps onto the conic (<5 deg). It runs
    // CalculatePhysicsStats once up front so firstStatsRunComplete is set and GetWorldPos3D returns the
    // CACHED CoMD (the real steady-state contract), otherwise the cache self-heals and the stale frame
    // cannot be reproduced.
    //
    // WHY this does NOT drive the full per-frame loop: the genuine high-warp FixedUpdate/LateUpdate/
    // exec-order-10000 interleave across a real multi-segment transfer with the live shadow director is
    // not reproducible in an isolated in-game test (same limitation the creation-frame test documents).
    // The production CALL SITE + the seam gate are covered by the xUnit LoopIconWarpLagTests
    // (GhostOrbitElementsChanged / IconVsOrbitAngleDeg) plus the "Seam CoMD re-snap summary"
    // proof-of-fire grep (refreshed>0, maxOffDeg<1) on a manual warp playtest.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); not in the headless xUnit suite.
    public class LoopBoundaryIconWarpLagTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double Sma = 800000.0;
        private const double Ecc = 0.05;
        private const double LoopShiftSeconds = 240000.0;

        // The reseed advances the conic phase to this fraction of a period later, so the stale icon and
        // the new conic@now are unambiguously apart (not a degenerate near-0 / near-360 wrap).
        private const double ReseedPhaseFraction = 0.37;

        // The stale (pre-refresh) icon must be at least this far off the new conic for the assertion to
        // distinguish the fix from a no-op.
        private const double MinStaleOffOrbitDeg = 20.0;

        // After the CalculatePhysicsStats re-snap the icon must sit on its conic.
        private const double MaxOnOrbitDeg = 5.0;

        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "Bug A: after a state-vector reseed changes a packed ghost's conic, forcing CalculatePhysicsStats re-snaps the icon CoMD (GetWorldPos3D) onto the new conic the same frame; SetPosition alone leaves the cached CoMD stale (the high-warp icon-off-orbit anomaly)")]
        public void StateVectorReseed_CalculatePhysicsStatsResnapsIconOntoConic()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double effUT = liveUT - LoopShiftSeconds;
            OrbitSegment seg = BuildSegment(effUT);

            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            Recording rec = BuildLoopShiftedRecording(effUT, seg);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("loop-boundary-icon-warp-test-start");

            uint pid = 0u;
            try
            {
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex,
                    rec,
                    GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg,
                    default(TrajectoryPoint),
                    effUT,
                    loopEpochShiftSeconds: LoopShiftSeconds);

                if (ghost == null || ghost.orbitDriver == null
                    || ghost.orbitDriver.orbit == null || ghost.precalc == null)
                {
                    InGameAssert.Skip("Loop ghost / precalc did not resolve in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;
                InGameAssert.IsTrue(GhostMapPresence.IsGhostMapVessel(pid),
                    "Freshly created loop ghost must be registered as a ghost map vessel");

                Orbit orb = ghost.orbitDriver.orbit;
                double now = Planetarium.GetUniversalTime();

                // 1. Settle the CoMD cache (firstStatsRunComplete=true) so a later GetWorldPos3D returns
                //    the CACHED value, the real steady-state contract. Without this the cache self-heals
                //    on read and the stale frame cannot be reproduced.
                ghost.precalc.CalculatePhysicsStats();
                double period = orb.period;
                if (double.IsNaN(period) || period <= 0.0)
                {
                    InGameAssert.Skip("Degenerate ghost orbit period; cannot phase-shift the reseed");
                    return;
                }

                // 2. Reproduce the production reseed sequence: OrbitReseed (the same helper
                //    UpdateGhostOrbitFromStateVectors uses) rebuilds the SAME conic at a clearly
                //    different phase (a fraction of a period later), then updateFromParameters() (the
                //    production :8381 drive) propagates orbitDriver.pos + the transform to the live
                //    clock. Neither call touches the cached CoMD.
                double sampleUT = now + period * ReseedPhaseFraction;
                Vector3d relZup = orb.getRelativePositionAtUT(sampleUT);
                Vector3d worldYup = (Vector3d)relZup.xzy + kerbin.position;
                Vector3d velZup = orb.getOrbitalVelocityAtUT(sampleUT);
                OrbitReseed.FromWorldPosAndZupVelocity(orb, kerbin, worldYup, velZup, now);
                ghost.orbitDriver.updateFromParameters();

                // The conic the line + the end-of-frame probe expect the icon on, at 'now'.
                Vector3d conicNow = orb.getRelativePositionAtUT(now);
                conicNow.Swizzle();

                // 3. Without the CoMD refresh, GetWorldPos3D returns the STALE cached CoMD (still the
                //    pre-reseed phase) - the icon sits far off the new conic. Prove the bug is live.
                Vector3d iconStale = ghost.GetWorldPos3D() - kerbin.position;
                if (iconStale.magnitude < 1.0)
                {
                    InGameAssert.Skip("Ghost world position not resolved this frame");
                    return;
                }
                double staleOffDeg = GhostMapPresence.IconVsOrbitAngleDeg(iconStale, conicNow);
                if (double.IsNaN(staleOffDeg) || staleOffDeg < MinStaleOffOrbitDeg)
                {
                    InGameAssert.Skip(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Reseed moved the icon only {0:F1} deg; cannot distinguish the re-snap from a no-op. "
                        + "Pick a different phase fraction.", staleOffDeg));
                    return;
                }

                // 4. THE FIX's exact operation: CalculatePhysicsStats re-snaps CoMD onto the new conic
                //    (CoMD = mainBody.position + orbitDriver.pos for a packed orbiting ghost), so
                //    GetWorldPos3D now lands on the line.
                ghost.precalc.CalculatePhysicsStats();
                Vector3d iconFixed = ghost.GetWorldPos3D() - kerbin.position;
                double fixedOffDeg = GhostMapPresence.IconVsOrbitAngleDeg(iconFixed, conicNow);
                // Cross-check the production proof-metric helper agrees with the direct read.
                double helperOffDeg = GhostMapPresence.SeamIconOffOrbitAngleDeg(ghost, orb, now);

                ParsekLog.Info("TestRunner",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "StateVectorReseed_CalculatePhysicsStatsResnapsIconOntoConic: pid={0} period={1:F0}s "
                        + "phaseFrac={2:F2} staleOffDeg={3:F2} fixedOffDeg={4:F2} helperOffDeg={5:F2} "
                        + "(stale>={6:F0} fixed<{7:F0})",
                        pid, period, ReseedPhaseFraction, staleOffDeg, fixedOffDeg, helperOffDeg,
                        MinStaleOffOrbitDeg, MaxOnOrbitDeg));

                InGameAssert.IsGreaterThan(staleOffDeg, MinStaleOffOrbitDeg,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Before the CoMD refresh the packed ghost icon (cached CoMD / GetWorldPos3D) must be "
                        + "stale - off the just-reseeded conic by a clear margin. It is only {0:F1} deg off, so "
                        + "the cache did not go stale and the test cannot prove the fix is not a no-op.",
                        staleOffDeg));

                InGameAssert.IsLessThan(fixedOffDeg, MaxOnOrbitDeg,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "After the CalculatePhysicsStats re-snap (the fix's operation) the ghost icon must sit "
                        + "on its conic at the live clock. It is {0:F1} deg off (was {1:F1} deg off stale). A "
                        + "large value means CalculatePhysicsStats did NOT refresh the cached CoMD onto "
                        + "orbitDriver.pos - the fix's load-bearing assumption is broken (e.g. a second "
                        + "updateFromParameters() was substituted, the no-op trap).",
                        fixedOffDeg, staleOffDeg));

                InGameAssert.IsLessThan(helperOffDeg, MaxOnOrbitDeg,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "SeamIconOffOrbitAngleDeg (the proof-of-fire metric) must agree with the direct read "
                        + "({0:F1} deg vs {1:F1} deg). A mismatch means the metric's getRelativePositionAtUT + "
                        + "Swizzle frame diverged from the probe's icon-off-orbit math.",
                        helperOffDeg, fixedOffDeg));

                // 5. Steady-state efficiency contract: with the conic unchanged the seam gate must NOT
                //    request another refresh (else CalculatePhysicsStats would run every frame for every
                //    ghost - the visual-efficiency invariant). Validate the gate decision against the
                //    LIVE orbit's own elements (runtime values, vs the synthetic xUnit cases), and
                //    exercise the test-reset hook so it is not dead code.
                GhostMapPresence.ResetSeamComdCountersForTesting();
                InGameAssert.IsTrue(GhostMapPresence.seamComdRefreshCount == 0
                        && GhostMapPresence.seamComdSteadyCount == 0,
                    "ResetSeamComdCountersForTesting must zero the seam counters");
                Orbit cur = ghost.orbitDriver.orbit;
                InGameAssert.IsFalse(
                    GhostMapPresence.GhostOrbitElementsChanged(
                        true, cur.semiMajorAxis, cur.eccentricity, cur.epoch,
                        cur.semiMajorAxis, cur.eccentricity, cur.epoch),
                    "The seam gate must skip an unchanged conic (the steady-state efficiency contract): "
                    + "re-applying the same recorded point must not trip GhostOrbitElementsChanged, so "
                    + "CalculatePhysicsStats does not run every frame.");
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("loop-boundary-icon-warp-test-cleanup");
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
            }
        }

        // Bug A director-path facet (validated 2026-06-14_1642): the surviving high-warp
        // icon-off-orbit / icon-teleport anomalies sit on DIRECTOR-DRIVE (and legacy-bounded) ghosts,
        // not the state-vector gap-glide the test above covers. Same root mechanism: GetWorldPos3D
        // reads the cached Vessel.CoMD, which VesselPrecalculate.Update computes (CalculatePhysicsStats)
        // BEFORE re-propagating the orbit (UpdateOrbit -> the icon-drive Prefix) for a packed ghost, so
        // CoMD trails orbitDriver.pos by one Update every frame. The fix re-snaps CoMD onto
        // refBody.position + orbitDriver.pos inside the Prefix (after SetPosition). This test stages a
        // deliberately-wrong (stale) CoMD on a real bounded ghost, drives it through the production
        // Prefix (updateFromParameters), and asserts the Prefix corrected the cached CoMD onto the
        // driver's pos - the no-op refutation (SetPosition alone would leave the stale value).
        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "Bug A director-path: the per-frame icon-drive Prefix re-snaps the cached Vessel.CoMD (GetWorldPos3D) onto refBody.position + orbitDriver.pos, so a packed driven ghost icon rides its line the same frame instead of trailing CoMD by one Update (the surviving high-warp icon-off-orbit)")]
        public void IconDrivePrefix_ResnapsCoMDOntoDriverPos()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double effUT = liveUT - LoopShiftSeconds;
            OrbitSegment seg = BuildSegment(effUT);

            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            Recording rec = BuildLoopShiftedRecording(effUT, seg);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("director-comd-test-start");

            uint pid = 0u;
            try
            {
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex, rec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg, default(TrajectoryPoint), effUT, loopEpochShiftSeconds: LoopShiftSeconds);
                if (ghost == null || ghost.orbitDriver == null
                    || ghost.orbitDriver.orbit == null || ghost.precalc == null)
                {
                    InGameAssert.Skip("Ghost / precalc did not resolve in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;

                // Settle the CoMD cache (firstStatsRunComplete=true) so GetWorldPos3D returns the
                // CACHED value, the real steady-state contract that lets the director CoMD lag exist.
                ghost.precalc.CalculatePhysicsStats();

                // Stage a deliberately-wrong (stale) CoMD 5000 km off in +x.
                Vector3d corruption = new Vector3d(5.0e6, 0.0, 0.0);
                ghost.CoMD = kerbin.position + corruption;
                ghost.CoM = ghost.CoMD;
                Vector3d staleRel = ghost.GetWorldPos3D() - kerbin.position;
                if ((staleRel - corruption).magnitude > 1.0)
                {
                    InGameAssert.Skip("CoMD cache self-refreshed (firstStatsRunComplete not set); "
                        + "cannot stage the stale frame");
                    return;
                }

                // Drive the icon the way VesselPrecalculate.Update does (UpdateOrbit ->
                // updateFromParameters): the Prefix's bounded branch propagates orbitDriver.pos +
                // SetPositions the transform, then - with the fix - re-snaps CoMD onto
                // refBody.position + orbitDriver.pos.
                Parsek.Patches.GhostOrbitIconDrivePatch.ResetDirectorIconCoMDRefreshCountForTesting();
                ghost.orbitDriver.updateFromParameters();

                if (Parsek.Patches.GhostOrbitIconDrivePatch.directorIconCoMDRefreshCount == 0L)
                {
                    InGameAssert.Skip("Ghost did not take the bounded icon-drive path (no orbit bounds / "
                        + "deferred to stock); cannot exercise the director CoMD re-snap");
                    return;
                }

                Vector3d driverPos = ghost.orbitDriver.pos;          // the Yup pos the Prefix just wrote
                Vector3d fixedRel = ghost.GetWorldPos3D() - kerbin.position;

                ParsekLog.Info("TestRunner",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "IconDrivePrefix_ResnapsCoMDOntoDriverPos: pid={0} refreshed={1} staleRel|{2:F0}| "
                        + "fixedRel|{3:F0}| driverPos|{4:F0}| offFromDriver|{5:F2}| offFromStale|{6:F0}|",
                        pid, Parsek.Patches.GhostOrbitIconDrivePatch.directorIconCoMDRefreshCount,
                        staleRel.magnitude, fixedRel.magnitude, driverPos.magnitude,
                        (fixedRel - driverPos).magnitude, (fixedRel - corruption).magnitude));

                // After the drive, GetWorldPos3D (CoMD - refBody.position) must equal the driver's pos -
                // the icon is exactly on the conic it was just propagated to.
                InGameAssert.IsLessThan((fixedRel - driverPos).magnitude, 1.0,
                    "After the icon-drive Prefix, the cached CoMD must equal refBody.position + "
                    + "orbitDriver.pos (GetWorldPos3D on the conic). A large gap means the Prefix did NOT "
                    + "re-snap CoMD - SetPosition alone leaves the cached CoMD stale (the no-op the "
                    + "director re-snap fixes; a second updateFromParameters() would not help).");

                // ... and it must have moved OFF the staged stale value (proves the drive corrected it,
                // not a coincidental match).
                InGameAssert.IsGreaterThan((fixedRel - corruption).magnitude, 1000.0,
                    "The drive must have corrected the staged stale CoMD (5000 km off); the icon must no "
                    + "longer sit at the corrupted position.");
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("director-comd-test-cleanup");
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
            }
        }

        private static OrbitSegment BuildSegment(double effUT)
        {
            return new OrbitSegment
            {
                startUT = effUT,
                endUT = effUT + 600.0,
                inclination = 0.0,
                eccentricity = Ecc,
                semiMajorAxis = Sma,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = effUT,
                bodyName = KerbinBodyName,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            };
        }

        private static Recording BuildLoopShiftedRecording(double effUT, OrbitSegment seg)
        {
            double startUT = effUT;
            double endUT = effUT + 600.0;
            var rec = new Recording
            {
                RecordingId = "loop-boundary-icon-warp-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Loop Boundary Icon Warp Test",
                TerminalStateValue = null,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = KerbinBodyName,
                TerminalOrbitBody = KerbinBodyName,
                TerminalOrbitSemiMajorAxis = Sma,
                TerminalOrbitEccentricity = Ecc,
                TerminalOrbitInclination = 0.0,
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
                altitude = 160000.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2200f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                latitude = 0.0,
                longitude = 5.0,
                altitude = 160000.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2200f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }
    }
}
