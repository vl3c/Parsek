using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// In-game smoke test for the persistence-based split predicate
    /// (`docs/dev/plans/optimizer-persistence-split.md`, test #30).
    ///
    /// xUnit twins under `RecordingOptimizerTests` cover the predicate exhaustively in
    /// isolation. This in-game variant runs the real-flight chain shape through the
    /// production `RecordingStore.RunOptimizationPass()` path inside KSP — so that
    /// future refactors of `RecordingOptimizer`, `RecordingStore.RunOptimizationSplitPass`,
    /// or any wiring between them must keep producing per-phase chain segments for the
    /// canonical ascent + reentry shape that the feature exists to serve.
    ///
    /// The test does not automate a full real flight (engines, gravity turn, deorbit
    /// burn, parachute) — that would be a multi-minute simulation outside the in-game
    /// test runner's tractable scope. Instead it injects a synthetic recording whose
    /// `TrackSections` mirror what the recorder would produce for a real
    /// "pad → atmo ascent → orbit → atmo reentry → landing" mission, then runs the
    /// production optimizer pass and asserts the resulting chain.
    /// </summary>
    public class PersistenceSplitOptimizerTest
    {
        [InGameTest(Category = "Optimizer", Scene = GameScenes.SPACECENTER,
            Description = "Persistence predicate produces per-phase chain segments for an ascent + reentry recording (plan §9.4)")]
        public void RealAscentReentry_ProducesPerPhaseChain_InGame()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            try
            {
                const string recId = "rec_persistence_smoke_ascent_reentry";
                const string treeId = "tree_persistence_smoke";
                const double t0 = 17000.0;

                // Section shape:
                //   s=0  Pad / Surface     [30s]
                //   s=1  Atmo ascent       [300s]
                //   s=2  Orbit / ExoBallistic [1800s]
                //   s=3  Atmo reentry     [200s]
                //   s=4  Landing / Surface [60s]
                // Boundaries:
                //   s=1 Surface→Atmo  (Surface short-circuit)        → split
                //   s=2 Atmo→Exo      (persistence: both runs long)  → split
                //   s=3 Exo→Atmo      (persistence: both runs long;
                //                      forward neighbour is Surface
                //                      class 2, ≠ Exo class 1, no
                //                      bracket)                      → split
                //   s=4 Atmo→Surface  (Surface short-circuit)        → split
                var rec = new Recording
                {
                    RecordingId = recId,
                    TreeId = treeId,
                    VesselName = "Persistence Smoke Probe",
                    VesselPersistentId = 7700001u,
                    ChainId = "chain_persistence_smoke",
                    ChainIndex = 0,
                    ChainBranch = 0,
                    MergeState = MergeState.Immutable
                };

                AddSection(rec, SegmentEnvironment.SurfaceStationary, t0, t0 + 30);
                AddSection(rec, SegmentEnvironment.Atmospheric, t0 + 30, t0 + 330);
                AddSection(rec, SegmentEnvironment.ExoBallistic, t0 + 330, t0 + 2130);
                AddSection(rec, SegmentEnvironment.Atmospheric, t0 + 2130, t0 + 2330);
                AddSection(rec, SegmentEnvironment.SurfaceMobile, t0 + 2330, t0 + 2390);

                RecordingStore.AddRecordingWithTreeForTesting(rec);

                int initialCount = RecordingStore.CommittedRecordings.Count;
                RecordingStore.RunOptimizationPass();
                int finalCount = RecordingStore.CommittedRecordings.Count;

                int chainGrowth = finalCount - initialCount;
                InGameAssert.IsTrue(chainGrowth >= 3,
                    $"Persistence predicate should produce at least 3 splits for the canonical " +
                    $"5-section ascent+reentry shape (Surface/Atmo/Exo/Atmo/Surface), giving " +
                    $"≥4 chain segments. Initial count={initialCount} final count={finalCount} " +
                    $"chainGrowth={chainGrowth}. If this fails, check that " +
                    $"FindSplitCandidatesForOptimizer is still wired to RunOptimizationSplitPass.");
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        [InGameTest(Category = "Optimizer", Scene = GameScenes.SPACECENTER,
            Description = "Persistence predicate suppresses every boundary in an eccentric grazing recording (plan §9.4)")]
        public void EccentricGrazing_StaysOneSegment_InGame()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            try
            {
                const string recId = "rec_persistence_smoke_grazing";
                const string treeId = "tree_persistence_smoke_grazing";
                const double t0 = 17000.0;

                var rec = new Recording
                {
                    RecordingId = recId,
                    TreeId = treeId,
                    VesselName = "Persistence Smoke Grazing Probe",
                    VesselPersistentId = 7700002u,
                    ChainId = "chain_persistence_grazing",
                    ChainIndex = 0,
                    ChainBranch = 0,
                    MergeState = MergeState.Immutable
                };

                // Eccentric grazing pattern — 4 oscillations Exo[1500] / Atmo[40] / Exo[1500] /
                // Atmo[40] / Exo[1500]. All four env-class boundaries should suppress.
                double cur = t0;
                AddSection(rec, SegmentEnvironment.ExoBallistic, cur, cur + 1500); cur += 1500;
                AddSection(rec, SegmentEnvironment.Atmospheric, cur, cur + 40);   cur += 40;
                AddSection(rec, SegmentEnvironment.ExoBallistic, cur, cur + 1500); cur += 1500;
                AddSection(rec, SegmentEnvironment.Atmospheric, cur, cur + 40);   cur += 40;
                AddSection(rec, SegmentEnvironment.ExoBallistic, cur, cur + 1500);

                RecordingStore.AddRecordingWithTreeForTesting(rec);
                int initialCount = RecordingStore.CommittedRecordings.Count;
                RecordingStore.RunOptimizationPass();
                int finalCount = RecordingStore.CommittedRecordings.Count;

                InGameAssert.AreEqual(initialCount, finalCount,
                    $"Eccentric grazing recording produced unexpected chain expansion " +
                    $"(initial={initialCount}, final={finalCount}). The persistence predicate " +
                    $"should suppress every Atmo↔Exo boundary in this shape.");
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        private static void AddSection(Recording rec, SegmentEnvironment env, double startUT, double endUT)
        {
            var p0 = new TrajectoryPoint
            {
                ut = startUT,
                latitude = 0,
                longitude = 0,
                altitude = 50000,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            var p1 = new TrajectoryPoint
            {
                ut = endUT,
                latitude = 0,
                longitude = 0,
                altitude = 50000,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            rec.Points.Add(p0);
            rec.Points.Add(p1);
            rec.TrackSections.Add(new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 10f,
                frames = new List<TrajectoryPoint> { p0, p1 },
                checkpoints = new List<OrbitSegment>()
            });
        }
    }
}
