using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Bug 3 creation-frame guard (branch fix-map-warp-icon-teleport / PR #1145).
    //
    // The existing RuntimeTests.DirectorDriveEpochBakePlacesIconOnRecordedPhase proves the epoch-bake
    // MATH (a baked Orbit resolved at the live clock lands on the recorded true anomaly) in isolation;
    // it deliberately does NOT touch a real ProtoVessel. This test fills that gap: it creates a REAL
    // loop-shifted map ghost and asserts the freshly-loaded packed icon sits on its RECORDED orbit
    // phase at creation, not at the raw-epoch-at-live phase.
    //
    // The defect: a loop-shifted map ghost was ProtoVessel.Load-ed carrying the RAW recorded orbit
    // epoch, so KSP placed its packed icon by propagating at the LIVE Planetarium clock against that
    // epoch - landing it shift-worth-of-mean-anomaly off the recorded phase at creation. At extreme
    // warp the proto is destroyed+recreated every frame, so that one-frame-wrong creation placement is
    // EVERY frame (the icon-off-orbit / icon-teleport anomaly family).
    //
    // THE FIX it guards (build-time epoch bake): BuildAndLoadGhostProtoVessel authors the proto's orbit
    // node with epoch = segment.epoch + loopShift, so ProtoVessel.Load's own live-clock propagation
    // places the icon on the recorded phase at creation, with no settle pass. Before the fix the icon
    // sat at the raw-epoch-at-live phase (the full phase offset off the line); after it the icon is on
    // the recorded phase (angle to the recorded phase ~0).
    //
    // WHY this asserts creation-frame placement directly (not "the icon does not move after creation"):
    // the steady-state per-frame drive that keeps a ghost on its line in real flight is the Director
    // drive (StockConicTreatment.SeedAndDriveLive), which needs a fresh ShadowRenderDriver seed written
    // by the flight loop's RunFrame. This isolated in-game test does not run that loop, so the Director
    // never engages and the legacy fallback drive drifts the synthetic ghost on later frames - a test-
    // context artifact, NOT real flight. So we read ONLY the creation frame (pv.Load's placement, which
    // is exactly what the bake controls) and compare it against the recorded phase. End-to-end steady-
    // state is validated separately by the warp playtest (icon-off-orbit anomaly counts).
    //
    // NOTE: this is an in-game test (runs inside KSP via Ctrl+Shift+T / Settings > Diagnostics); it
    // cannot run in the headless xUnit suite. Run it in the Flight scene on the launch pad.
    public class LoopGhostCreationFrameIconTest
    {
        private const string KerbinBodyName = "Kerbin";

        // An unambiguously orbital, fully-above-atmosphere elliptical Kerbin orbit (peri ~160 km).
        private const double Sma = 800000.0;
        private const double Ecc = 0.05;

        // A large loop shift (recorded clock far behind the live clock), matching the real mission
        // family. The exact value is not load-bearing: a runtime precondition Skips if it happens to
        // land the raw-epoch-at-live phase too close to the recorded phase (shift ~ an integer number of
        // periods), so the assertion is always meaningful when it runs.
        private const double LoopShiftSeconds = 240000.0;

        // The icon must sit within this of the recorded phase at creation. The bake lands it within ~1
        // deg; an un-baked load lands it the full phase separation away (~100+ deg on this orbit).
        private const double MaxOffOrbitDeg = 5.0;

        // The recorded phase and the raw-epoch-at-live phase must be at least this far apart for the
        // assertion to distinguish a correct bake from a broken one.
        private const double MinPhaseSeparationDeg = 10.0;

        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "Bug 3: a freshly-created loop-shifted ghost icon is placed on its RECORDED orbit phase at creation (guards the build-time epoch bake; un-baked load sat at the raw-epoch-at-live phase)")]
        public void FreshLoopGhostIcon_OnRecordedPhaseAtCreation()
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

            // Swap a single synthetic loop-shifted recording into the committed store so the create path
            // resolves it; restore everything in finally.
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
            GhostMapPresence.RemoveAllGhostVessels("loop-ghost-creation-frame-test-start");

            uint pid = 0u;
            try
            {
                // Create the ghost the way the flight deferred-create pass does: at the loop-mapped
                // effUT, with the live-frame epoch shift passed through. This loads a real packed
                // ProtoVessel - the subject under test.
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex,
                    rec,
                    GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg,
                    default(TrajectoryPoint),
                    effUT,
                    loopEpochShiftSeconds: LoopShiftSeconds);

                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("Loop-shifted ghost did not create in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;
                InGameAssert.IsTrue(GhostMapPresence.IsGhostMapVessel(pid),
                    "Freshly created loop ghost must be registered as a ghost map vessel");

                // Prerequisite guard: the shift must have registered for this pid (UpdateGhostOrbitFor
                // Recording, inside the create), proving the loop-shift threading reached the create path.
                double registeredShift = GhostMapPresence.GetGhostOrbitEpochShift(pid);
                InGameAssert.IsTrue(
                    System.Math.Abs(registeredShift - LoopShiftSeconds) < 1.0,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Loop shift must be registered for the freshly-created ghost (expected {0:F0}s, got {1:F0}s)",
                        LoopShiftSeconds, registeredShift));

                // The icon's actual rendered position at creation, body-relative in Y-up WORLD axes -
                // the SAME frame the MapRenderProbe icon-off-orbit check uses (GetWorldPos3D -
                // body.position). This is pv.Load's placement, which is exactly what the build-time bake
                // controls (the create-call's later raw re-seed + Update-phase drive do not refresh the
                // packed CoMD this frame).
                Vector3d iconBodyRel = ghost.GetWorldPos3D() - kerbin.position;
                if (iconBodyRel.magnitude < 1.0)
                {
                    InGameAssert.Skip("Ghost world position not resolved on the creation frame");
                    return;
                }

                // After the create call the ghost's live orbit carries the RAW recorded epoch (ApplyOrbit
                // ToVessel re-seeds raw), so its orbit at effUT IS the recorded phase and its orbit at
                // liveUT IS the buggy raw-epoch-at-live phase. Both in the probe's Y-up world frame.
                Orbit orb = ghost.orbitDriver.orbit;
                Vector3d recordedRel = orb.getRelativePositionAtUT(effUT);
                recordedRel.Swizzle();
                Vector3d liveRawRel = orb.getRelativePositionAtUT(liveUT);
                liveRawRel.Swizzle();

                double separationDeg = Vector3d.Angle(recordedRel, liveRawRel);
                if (separationDeg < MinPhaseSeparationDeg)
                {
                    InGameAssert.Skip(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Degenerate loop shift: recorded phase and raw-epoch-at-live phase only {0:F1} deg " +
                        "apart; cannot distinguish a correct bake. Pick a different shift.", separationDeg));
                    return;
                }

                double angleToRecordedDeg = Vector3d.Angle(iconBodyRel, recordedRel);
                double angleToRawDeg = Vector3d.Angle(iconBodyRel, liveRawRel);

                ParsekLog.Info("TestRunner",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "FreshLoopGhostIcon_OnRecordedPhaseAtCreation: pid={0} shift={1:F0}s " +
                        "angleToRecorded={2:F2}deg angleToRaw={3:F2}deg phaseSeparation={4:F2}deg " +
                        "threshold={5:F2}deg registeredShift={6:F0}s",
                        pid, LoopShiftSeconds, angleToRecordedDeg, angleToRawDeg, separationDeg,
                        MaxOffOrbitDeg, registeredShift));

                InGameAssert.IsLessThan(angleToRecordedDeg, MaxOffOrbitDeg,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Freshly-created loop ghost icon must sit on its RECORDED orbit phase at creation, " +
                        "not at the raw-epoch-at-live phase. Icon is {0:F1} deg off the recorded phase and " +
                        "{1:F1} deg off the raw-epoch-at-live phase (the two are {2:F1} deg apart). The " +
                        "build-time epoch bake (BuildAndLoadGhostProtoVessel authors the proto orbit epoch " +
                        "as segment.epoch + loopShift) makes ProtoVessel.Load place the icon on the recorded " +
                        "phase. An icon near the raw-epoch-at-live phase means the bake regressed or was " +
                        "undone before the icon resolved.",
                        angleToRecordedDeg, angleToRawDeg, separationDeg));
            }
            finally
            {
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("loop-ghost-creation-frame-test-cleanup");
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
                RecordingId = "loop-ghost-creation-frame-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Loop Ghost Creation Frame Test",
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
