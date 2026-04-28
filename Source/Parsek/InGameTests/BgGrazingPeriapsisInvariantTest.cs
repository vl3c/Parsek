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
    /// cycle the recording's `TrackSections` list must remain empty — even though
    /// `EnvironmentDetector.Classify` would call the periapsis side `Atmospheric`, that
    /// classification has no path into a TrackSection while the vessel is on rails.
    ///
    /// If a future refactor accidentally lets the on-rails path emit env-class TrackSections
    /// (via a moved field, removed packed-gate, etc.), this test trips before the change
    /// reaches a player save.
    /// </summary>
    public class BgGrazingPeriapsisInvariantTest
    {
        [InGameTest(Category = "Recording", Scene = GameScenes.SPACECENTER,
            Description = "On-rails BG vessel with grazing periapsis emits zero TrackSections across 50 simulated orbits (S16 invariant)")]
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

                InGameAssert.AreEqual(0, rec.TrackSections.Count,
                    $"On-rails BG vessel emitted a TrackSection at orbit {i} — eccentric-orbit invariant broken (S16). " +
                    $"Check BackgroundOnRailsState fields and the packed early-return in OnBackgroundPhysicsFrame.");
            }

            InGameAssert.AreEqual(0, rec.TrackSections.Count,
                "After 50 simulated orbits, TrackSections must still be empty.");
            InGameAssert.IsTrue(rec.OrbitSegments.Count > 0,
                "OrbitSegments should have accumulated (sanity check on the simulation).");
        }
    }
}
