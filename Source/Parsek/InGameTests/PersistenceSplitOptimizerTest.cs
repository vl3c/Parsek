using System.Collections.Generic;
using System.Linq;
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
    ///
    /// <para>Scene = SPACECENTER (deliberate deviation from plan §9.4 which specified
    /// FLIGHT). The synthetic shape doesn't require a flight scene to construct — it
    /// goes straight into <see cref="RecordingStore"/>. SPACECENTER is the cheapest scene
    /// the in-game test runner can launch into, so it minimises the cost of the smoke
    /// run without losing coverage of the production wiring.</para>
    ///
    /// <para><b>Non-destructive contract.</b> The test runner shares static
    /// <see cref="RecordingStore"/> state with the player's live save. This test wraps
    /// its synthetic-recording mutation in <see cref="RecordingStoreTestSnapshot"/> so a
    /// real career save's recordings survive the run. Older versions of this file used
    /// <c>RecordingStore.ResetForTesting()</c>, which deleted the player's recordings
    /// from memory; the resulting OnSave then wrote zero RECORDING_TREE nodes over the
    /// .sfs while sidecars stayed on disk (production bug 2026-05-01). The current
    /// guard in <c>RecordingStore.ResetForTesting()</c> hard-fails any future regression
    /// of this pattern.</para>
    ///
    /// <para><b>Live-data skip.</b> <see cref="RecordingStore.RunOptimizationPass"/>
    /// walks the GLOBAL <c>committedRecordings</c> list — every player recording plus the
    /// synthetic one this fixture injects. The split / merge / boring-tail-trim passes
    /// mutate <c>Recording</c> instances in place (<c>ChainId</c>,
    /// <c>SegmentBodyName</c>, <c>FilesDirty</c>), update <c>committedTrees</c>'
    /// BackgroundMap and BranchPoint linkage, and fan out to disk via
    /// <c>FlushDirtyFiles</c> + <c>DeleteRecordingFiles</c> for any recording the
    /// optimizer touches. <see cref="RecordingStoreTestSnapshot"/> can only undo list
    /// membership and ordering — it cannot roll back per-instance field mutations on
    /// shared <c>Recording</c> references, and it cannot undo sidecar writes / deletes.
    /// So if a player's save already holds committed recordings, both methods refuse to
    /// run rather than risk corrupting them. The <c>RecordingOptimizerTests</c> xUnit
    /// suite covers the predicate exhaustively in isolation; the in-game smoke variant
    /// only adds value on a fresh save where the global pass is safely a no-op outside
    /// the synthetic fixture.</para>
    /// </summary>
    public class PersistenceSplitOptimizerTest
    {
        [InGameTest(Category = "Optimizer", Scene = GameScenes.SPACECENTER,
            Description = "Persistence predicate produces per-phase chain segments for an ascent + reentry recording (plan §9.4)")]
        public void RealAscentReentry_ProducesPerPhaseChain_InGame()
        {
            RunWithRecordingStoreLoggingSuppressed(RealAscentReentryBody);
        }

        private static void RealAscentReentryBody()
        {
            // Capture pre-test ID set BEFORE snapshot.Capture so we can compute the
            // delta after RunOptimizationPass and clean up the orphan sidecars the
            // optimizer's flush leaves behind for both the explicit synthetic id and
            // any random split-half ids the optimizer invents.
            var preIds = new HashSet<string>(
                RecordingStore.CommittedRecordings.Select(r => r.RecordingId));
            var snapshot = RecordingStoreTestSnapshot.Capture();
            int baselineCount = RecordingStore.CommittedRecordings.Count;
            // RunOptimizationPass walks the global committedRecordings list and mutates
            // Recording instances + sidecar files in place. Snapshot/restore is
            // reference-shallow and disk-blind, so it cannot undo those side effects on
            // the player's live recordings. Refuse to run when the store is non-empty;
            // RecordingOptimizerTests covers the predicate in xUnit. Run from a fresh save
            // (no committed recordings) to exercise the in-game path.
            if (baselineCount > 0)
            {
                InGameAssert.Skip(
                    $"Skipped: {baselineCount} live committed recording(s) present. " +
                    "RunOptimizationPass would mutate them in place and snapshot/restore " +
                    "cannot undo per-instance field mutations or sidecar disk I/O. Run " +
                    "from a fresh save, or rely on RecordingOptimizerTests xUnit coverage.");
            }

            HashSet<string> sidecarsToCleanup = null;
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

                // Compute synthetic-id delta IMMEDIATELY after the optimizer pass so a
                // failed assertion below still cleans up sidecars in finally.
                sidecarsToCleanup = new HashSet<string>(
                    RecordingStore.CommittedRecordings.Select(r => r.RecordingId));
                sidecarsToCleanup.ExceptWith(preIds);

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
                snapshot.Restore();
                InGameAssert.AreEqual(baselineCount, RecordingStore.CommittedRecordings.Count,
                    "Snapshot/restore should reinstate the player's live committed-recording count " +
                    "after the synthetic test recording is removed.");
                // Reference-shallow snapshot.Restore cannot undo the sidecar files the
                // optimizer flushed for synthetic + split-half recordings. Clean them up
                // explicitly so the player's saves/<save>/Parsek/Recordings/ doesn't
                // accumulate orphan .prec / .pann / *.craft / *.txt files per test run.
                if (sidecarsToCleanup != null && sidecarsToCleanup.Count > 0)
                {
                    InGameTestSidecarReaper.DeleteSidecarsForIds(
                        sidecarsToCleanup, "PersistenceSplitOptimizerTest");
                }
            }
        }

        [InGameTest(Category = "Optimizer", Scene = GameScenes.SPACECENTER,
            Description = "Persistence predicate suppresses every boundary in an eccentric grazing recording (plan §9.4)")]
        public void EccentricGrazing_StaysOneSegment_InGame()
        {
            RunWithRecordingStoreLoggingSuppressed(EccentricGrazingBody);
        }

        private static void EccentricGrazingBody()
        {
            // Capture pre-test ID set BEFORE snapshot.Capture so we can compute the
            // delta after RunOptimizationPass and clean up the orphan sidecars the
            // optimizer's flush leaves behind. (Even when this shape produces no chain
            // growth, the synthetic recording itself still gets flushed.)
            var preIds = new HashSet<string>(
                RecordingStore.CommittedRecordings.Select(r => r.RecordingId));
            var snapshot = RecordingStoreTestSnapshot.Capture();
            int baselineCount = RecordingStore.CommittedRecordings.Count;
            // See RealAscentReentry_ProducesPerPhaseChain_InGame above and the class-level
            // <b>Live-data skip</b> doc — RunOptimizationPass cannot be safely run over
            // live recordings, and snapshot/restore cannot undo the in-place mutation.
            if (baselineCount > 0)
            {
                InGameAssert.Skip(
                    $"Skipped: {baselineCount} live committed recording(s) present. " +
                    "RunOptimizationPass would mutate them in place and snapshot/restore " +
                    "cannot undo per-instance field mutations or sidecar disk I/O. Run " +
                    "from a fresh save, or rely on RecordingOptimizerTests xUnit coverage.");
            }

            HashSet<string> sidecarsToCleanup = null;
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

                // Compute synthetic-id delta IMMEDIATELY after the optimizer pass so a
                // failed assertion below still cleans up sidecars in finally.
                sidecarsToCleanup = new HashSet<string>(
                    RecordingStore.CommittedRecordings.Select(r => r.RecordingId));
                sidecarsToCleanup.ExceptWith(preIds);

                InGameAssert.AreEqual(initialCount, finalCount,
                    $"Eccentric grazing recording produced unexpected chain expansion " +
                    $"(initial={initialCount}, final={finalCount}). The persistence predicate " +
                    $"should suppress every Atmo↔Exo boundary in this shape.");
            }
            finally
            {
                snapshot.Restore();
                InGameAssert.AreEqual(baselineCount, RecordingStore.CommittedRecordings.Count,
                    "Snapshot/restore should reinstate the player's live committed-recording count " +
                    "after the synthetic test recording is removed.");
                // Reference-shallow snapshot.Restore cannot undo the sidecar files the
                // optimizer flushed for synthetic + split-half recordings. Clean them up
                // explicitly so the player's saves/<save>/Parsek/Recordings/ doesn't
                // accumulate orphan .prec / .pann / *.craft / *.txt files per test run.
                if (sidecarsToCleanup != null && sidecarsToCleanup.Count > 0)
                {
                    InGameTestSidecarReaper.DeleteSidecarsForIds(
                        sidecarsToCleanup, "PersistenceSplitOptimizerTest");
                }
            }
        }

        [InGameTest(Category = "Optimizer", Scene = GameScenes.SPACECENTER,
            Description = "A production loaded->on-rails boundary seam survives RunOptimizationPass unsplit (optimizer step 1)")]
        public void OnRailsBoundarySeam_SuppressesSplit_InGame()
        {
            RunWithRecordingStoreLoggingSuppressed(OnRailsBoundarySeamBody);
        }

        /// <summary>
        /// D3 `boundary-seam`. Drives the PRODUCTION seam producer
        /// (<c>BackgroundRecorder.FlushLoadedStateForOnRailsTransition</c>, through its
        /// testing wrapper, which also runs the rest of the on-rails transition tail) for a
        /// loaded background vessel that goes on rails mid-descent with no playable on-rails
        /// payload. The flush persists a one-frame SurfaceStationary boundary section flagged
        /// <c>isBoundarySeam</c> and logs `Persisted no-payload on-rails boundary section:
        /// ... (seam=1)`. The recording is then committed and the production
        /// <c>RecordingStore.RunOptimizationPass</c> runs over it: the Atmospheric -> Surface
        /// boundary would split at step 5 (Surface short-circuit), but step 1 skips it, so
        /// the pass logs `Split summary: ... seamSkipped=1` and adds no segment.
        /// The witness line prints only after every assertion held. The loaded state and
        /// its frames are injected (<c>OnVesselGoOnRails</c> and live sampling do not run);
        /// the flush, the seam flag, the optimizer pass and both production log lines are
        /// the shipped code.
        /// </summary>
        private static void OnRailsBoundarySeamBody()
        {
            var preIds = new HashSet<string>(
                RecordingStore.CommittedRecordings.Select(r => r.RecordingId));
            var snapshot = RecordingStoreTestSnapshot.Capture();
            int baselineCount = RecordingStore.CommittedRecordings.Count;
            // Same live-data guard as the two cells above: RunOptimizationPass walks the
            // whole committed list and snapshot/restore cannot undo its in-place mutation.
            if (baselineCount > 0)
            {
                InGameAssert.Skip(
                    $"Skipped: {baselineCount} live committed recording(s) present. " +
                    "RunOptimizationPass would mutate them in place and snapshot/restore " +
                    "cannot undo per-instance field mutations or sidecar disk I/O. Run " +
                    "from a fresh save, or rely on RecordingOptimizerTests xUnit coverage.");
            }

            HashSet<string> sidecarsToCleanup = null;
            try
            {
                const uint pid = 7700003u;
                const string recId = "rec_boundary_seam_smoke";
                const string treeId = "tree_boundary_seam_smoke";
                const double t0 = 17000.0;
                const double railsUT = t0 + 10.0;

                var tree = new RecordingTree
                {
                    Id = treeId,
                    TreeName = "Boundary seam smoke tree",
                    RootRecordingId = recId
                };
                var rec = new Recording
                {
                    RecordingId = recId,
                    TreeId = treeId,
                    VesselName = "Boundary Seam Smoke Probe",
                    VesselPersistentId = pid,
                    ChainId = "chain_boundary_seam_smoke",
                    ChainIndex = 0,
                    ChainBranch = 0,
                    MergeState = MergeState.Immutable
                };
                tree.Recordings[recId] = rec;

                // A background vessel on its last seconds of descent, loaded and sampled
                // every 2 s (inside the recorder's sparse-sampling threshold, so the cell
                // raises no sampling WARN), that goes on rails at railsUT.
                var bgRecorder = new BackgroundRecorder(tree);
                bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                    pid, recId, SegmentEnvironment.Atmospheric, t0);
                for (int i = 0; i < 5; i++)
                {
                    bgRecorder.InjectCurrentTrackSectionFrameForTesting(
                        pid, SeamPoint(t0 + 2.0 * i, 100.0 - 20.0 * i, -10f));
                }

                bgRecorder.FlushLoadedStateForOnRailsTransitionForTesting(
                    pid,
                    SegmentEnvironment.SurfaceStationary,
                    willHavePlayableOnRailsPayload: false,
                    boundaryPoint: SeamPoint(railsUT, 0.0, 0f),
                    ut: railsUT);

                InGameAssert.AreEqual(2, rec.TrackSections.Count,
                    "The on-rails flush should leave the Atmospheric section plus one boundary section.");
                InGameAssert.IsTrue(rec.TrackSections[1].isBoundarySeam,
                    "FlushLoadedStateForOnRailsTransition should flag the no-payload boundary section " +
                    "isBoundarySeam=true (producer C).");
                InGameAssert.AreEqual(SegmentEnvironment.SurfaceStationary, rec.TrackSections[1].environment,
                    "The boundary section should carry the next (on-rails) environment.");

                RecordingStore.AddRecordingWithTreeForTesting(rec);
                int initialCount = RecordingStore.CommittedRecordings.Count;
                RecordingStore.RunOptimizationPass();
                int finalCount = RecordingStore.CommittedRecordings.Count;

                // Computed IMMEDIATELY after the pass so a failed assertion below still
                // cleans up sidecars in finally.
                sidecarsToCleanup = new HashSet<string>(
                    RecordingStore.CommittedRecordings.Select(r => r.RecordingId));
                sidecarsToCleanup.ExceptWith(preIds);

                InGameAssert.AreEqual(initialCount, finalCount,
                    $"The seam-flanked Atmospheric->Surface boundary must not split " +
                    $"(initial={initialCount}, final={finalCount}); optimizer step 1 should skip it.");
                InGameAssert.AreEqual(2, rec.TrackSections.Count,
                    "RunOptimizationPass should leave both sections on the recording.");
                InGameAssert.IsTrue(rec.TrackSections[1].isBoundarySeam,
                    "The seam flag should survive the optimizer pass.");

                RecordingOptimizer.SplitBoundaryReason seamReason;
                bool seamSplittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out seamReason);
                InGameAssert.IsTrue(!seamSplittable,
                    "The boundary next to the seam section must not be splittable.");
                InGameAssert.AreEqual(RecordingOptimizer.SplitBoundaryReason.SuppressedBoundarySeam, seamReason,
                    "The boundary should be suppressed by the seam short-circuit (step 1), which is what " +
                    "the optimizer's seamSkipped counter tallies.");

                // Counterfactual on a copy: without the flag the same boundary is a Surface
                // short-circuit split, so step 1 (not some other predicate) is what held it.
                var counterfactual = new Recording { RecordingId = recId + "_counterfactual" };
                counterfactual.TrackSections.Add(rec.TrackSections[0]);
                TrackSection unflagged = rec.TrackSections[1];
                unflagged.isBoundarySeam = false;
                counterfactual.TrackSections.Add(unflagged);
                RecordingOptimizer.SplitBoundaryReason counterfactualReason;
                bool counterfactualSplittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(
                    counterfactual, 1, out counterfactualReason);
                InGameAssert.IsTrue(counterfactualSplittable,
                    $"Without isBoundarySeam the Atmospheric->Surface boundary should be splittable " +
                    $"(reason={counterfactualReason}); otherwise this cell does not prove step 1.");

                // Harness witness token: must stay after the last assertion so it prints only when every assert held.
                ParsekLog.Info("TestRunner", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "BoundarySeamOptimizerWitness: seam section survived RunOptimizationPass unsplit rec={0} sections={1} splitsAdded={2} seamReason={3} counterfactualReason={4}",
                    recId, rec.TrackSections.Count, finalCount - initialCount, seamReason, counterfactualReason));
            }
            finally
            {
                snapshot.Restore();
                InGameAssert.AreEqual(baselineCount, RecordingStore.CommittedRecordings.Count,
                    "Snapshot/restore should reinstate the player's live committed-recording count " +
                    "after the synthetic test recording is removed.");
                if (sidecarsToCleanup != null && sidecarsToCleanup.Count > 0)
                {
                    InGameTestSidecarReaper.DeleteSidecarsForIds(
                        sidecarsToCleanup, "PersistenceSplitOptimizerTest");
                }
            }
        }

        /// <summary>
        /// Each Optimizer cell silences RecordingStore.Log for its own body and restores the
        /// value it found, so later cells and categories in the same process keep their lines.
        /// </summary>
        private static void RunWithRecordingStoreLoggingSuppressed(System.Action body)
        {
            bool previousSuppressLogging = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = true;
            try
            {
                body();
            }
            finally
            {
                RecordingStore.SuppressLogging = previousSuppressLogging;
            }
        }

        private static TrajectoryPoint SeamPoint(double ut, double altitude, float verticalSpeed)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = -0.1,
                longitude = -74.6,
                altitude = altitude,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, verticalSpeed, 0f)
            };
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
