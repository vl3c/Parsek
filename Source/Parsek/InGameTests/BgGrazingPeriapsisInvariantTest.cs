using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime guard for the eccentric-orbit-grazes-atmo invariant flagged in
    /// `docs/dev/research/extending-rewind-to-stable-leaves.md` §S16.
    ///
    /// The xUnit twin (`EccentricOrbitOptimizerInvariantTests`) covers the structural
    /// invariant via reflection. This in-game variant runs the same proof against the
    /// real runtime: a `BackgroundRecorder` is constructed inside KSP, an on-rails state
    /// is injected, and a periapsis-grazing eccentric orbit is simulated by alternating
    /// `InjectOpenOrbitSegmentForTesting` + `CheckpointAllVessels` 50 times. After every
    /// cycle the recording must carry zero per-frame environment-classified
    /// `TrackSection`s — even though `EnvironmentDetector.Classify` would call the
    /// periapsis side `Atmospheric`, that classification has no path into a per-frame
    /// section while the vessel is on rails. Packed/on-rails closes do append
    /// `OrbitalCheckpoint` bridge sections that wrap closed `OrbitSegment`s (orbit-only
    /// bridges, not splittable env toggles); those are expected and allowed.
    ///
    /// If a future refactor accidentally lets the on-rails path emit per-frame env-class
    /// TrackSections (via a moved field, removed packed-gate, etc.), this test trips
    /// before the change reaches a player save.
    /// </summary>
    public class BgGrazingPeriapsisInvariantTest
    {
        // OrbitalCheckpoint bridge sections are orbit-only and expected from packed
        // closes; any Absolute/Relative per-frame section is the S16 invariant breach.
        private static int CountPerFrameEnvSections(List<TrackSection> sections)
        {
            if (sections == null) return 0;
            int count = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].referenceFrame != ReferenceFrame.OrbitalCheckpoint)
                    count++;
            }
            return count;
        }

        [InGameTest(Category = "Recording", Scene = GameScenes.SPACECENTER,
            Description = "On-rails BG vessel with grazing periapsis emits zero per-frame env TrackSections across 50 simulated orbits (S16 invariant)")]
        public void OnRailsGrazingPeriapsis_ProducesNoTrackSections_Across_Many_Orbits()
        {
            const uint pid = 8888001u;
            const string recId = "rec_eccentric_grazing_runtime";
            const double orbitalPeriod = 3600.0;
            const int orbitCount = 50;

            var tree = new RecordingTree
            {
                Id = "tree_eccentric_grazing_runtime",
                TreeName = "S16 invariant runtime tree",
                ActiveRecordingId = recId
            };
            var rec = new Recording
            {
                RecordingId = recId,
                TreeId = tree.Id,
                VesselName = "Eccentric Grazing Probe (runtime)",
                VesselPersistentId = pid,
                ChainId = "chain_grazing_runtime",
                ChainIndex = 0,
                ChainBranch = 0
            };
            tree.Recordings[recId] = rec;
            tree.BackgroundMap[pid] = recId;

            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectOnRailsStateForTesting(pid, recId, ut: 100.0);

            for (int i = 0; i < orbitCount; i++)
            {
                double startUT = 100.0 + i * orbitalPeriod;

                // Simulate a fresh OrbitSegment opened at apoapsis after each warp checkpoint.
                // The orbit grazes atmosphere (Pe ~60 km on Kerbin, Ap ~300 km).
                bgRecorder.InjectOpenOrbitSegmentForTesting(pid, new OrbitSegment
                {
                    startUT = startUT,
                    endUT = startUT,
                    bodyName = "Kerbin",
                    semiMajorAxis = 800000.0,
                    eccentricity = 0.18,
                    inclination = 0.05,
                    longitudeOfAscendingNode = 0.0,
                    argumentOfPeriapsis = 0.0,
                    meanAnomalyAtEpoch = 0.0,
                    epoch = startUT
                });

                // CheckpointAllVessels closes the open segment at the warp boundary; the null
                // vessel finder mirrors the BG case where no live Vessel exists in the scene
                // (the path the bug claim relied on).
                bgRecorder.CheckpointAllVessels(startUT + orbitalPeriod, vesselFinder: _ => null);

                InGameAssert.AreEqual(0, CountPerFrameEnvSections(rec.TrackSections),
                    $"On-rails BG vessel emitted a per-frame env-classified TrackSection at orbit {i} — " +
                    $"eccentric-orbit invariant broken (S16). OrbitalCheckpoint bridge sections are allowed; " +
                    $"any Absolute/Relative section is the breach. Check BackgroundOnRailsState fields and " +
                    $"the packed early-return in OnBackgroundPhysicsFrame.");
            }

            InGameAssert.AreEqual(0, CountPerFrameEnvSections(rec.TrackSections),
                "After 50 simulated orbits, no per-frame env-classified TrackSections must exist " +
                "(OrbitalCheckpoint bridge sections are allowed).");
            InGameAssert.IsTrue(rec.OrbitSegments.Count > 0,
                "OrbitSegments should have accumulated (sanity check on the simulation).");
        }
    }
}
