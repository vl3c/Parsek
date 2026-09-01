using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// OPTIMIZER-PASS-STALE-INDEX-CACHES (2026-09-01): <c>RecordingStore.RunOptimizationPass</c>
    /// removes merge-absorbed recordings and inserts split second halves mid-list without
    /// bumping <c>RecordingStore.StateVersion</c> or telling any index-keyed live consumer.
    /// These cells pin the two halves of the fix: the StateVersion bump (so the ERS cache
    /// cannot serve a pre-pass set) and the three structural notifications plus the
    /// insert-side reindex mirrors the FLIGHT controller applies from them.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizationPassInvalidationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OptimizationPassInvalidationTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        private void EnableLogCapture()
        {
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        // Three consecutive exo segments of one chain: RunOptimizationMergePass collapses
        // them into one recording (same shape as RecordingOptimizerTests.MakeChainSegment,
        // plus a RecordingId so EffectiveState.IsVisible admits them into the ERS).
        private static Recording MakeMergeableChainSegment(string id, string chainId, int chainIndex,
            double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Chain",
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                SegmentPhase = "exo",
                SegmentBodyName = "Mun",
                LoopPlayback = false,
                PlaybackEnabled = true,
                Hidden = false,
                LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel,
                LoopAnchorVesselId = 0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 50000, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 50000, bodyName = "Mun" });
            return rec;
        }

        // One recording spanning an exo -> atmo boundary: RunOptimizationSplitPass cuts it
        // at section 1 and inserts the second half right after it (same shape as
        // RecordingStoreTests.RunOptimizationPass_SplitsTreeRecording).
        private static Recording MakeSplittableRecording(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Rocket",
                VesselPersistentId = 42,
                TerminalStateValue = TerminalState.Destroyed,
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                RecordingFormatVersion = 0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 17000, altitude = 80000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17029, altitude = 40000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17030, altitude = 30000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17060, altitude = 100, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 17000, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17030, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });
            return rec;
        }

        // A single-section recording the pass has nothing to do with.
        private static Recording MakeInertRecording(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Inert-" + id,
                VesselPersistentId = 7,
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                RecordingFormatVersion = 0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 1000, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 1100, altitude = 100, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                startUT = 1000, endUT = 1100,
                frames = new List<TrajectoryPoint>()
            });
            return rec;
        }

        private static void AddThreeMergeableSegments()
        {
            RecordingStore.AddRecordingWithTreeForTesting(
                MakeMergeableChainSegment("seg-a", "chain1", 0, 17000, 17030));
            RecordingStore.AddRecordingWithTreeForTesting(
                MakeMergeableChainSegment("seg-b", "chain1", 1, 17030, 17060));
            RecordingStore.AddRecordingWithTreeForTesting(
                MakeMergeableChainSegment("seg-c", "chain1", 2, 17060, 17090));
        }

        #region StateVersion / ERS cache

        [Fact]
        public void Split_BumpsStateVersion_SoTheErsCacheRebuilds()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeSplittableRecording("rec-split"));

            // Prime the ERS cache on the pre-pass list.
            var before = EffectiveState.ComputeERS();
            Assert.Single(before);
            int versionBefore = RecordingStore.StateVersion;

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            // MUTATION NOTE: deleting the BumpStateVersion() call in RunOptimizationPass
            // makes ComputeERS return the cached single-entry list here.
            Assert.NotEqual(versionBefore, RecordingStore.StateVersion);
            var after = EffectiveState.ComputeERS();
            Assert.Equal(2, after.Count);
            Assert.Contains(after, r => r.RecordingId == "rec-split");
            Assert.Contains(after, r => r.RecordingId != "rec-split" && r.ChainId == before[0].ChainId);
        }

        [Fact]
        public void Merge_BumpsStateVersion_SoTheErsCacheRebuilds()
        {
            AddThreeMergeableSegments();

            var before = EffectiveState.ComputeERS();
            Assert.Equal(3, before.Count);
            int versionBefore = RecordingStore.StateVersion;

            RecordingStore.RunOptimizationPass();

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.NotEqual(versionBefore, RecordingStore.StateVersion);
            var after = EffectiveState.ComputeERS();
            var merged = Assert.Single(after);
            Assert.Equal("seg-a", merged.RecordingId);
        }

        [Fact]
        public void NoStructuralChange_DoesNotBumpStateVersion()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("inert"));
            int versionBefore = RecordingStore.StateVersion;

            RecordingStore.RunOptimizationPass();

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal(versionBefore, RecordingStore.StateVersion);
        }

        #endregion

        #region Structural notifications

        [Fact]
        public void Merge_RaisesRemovingOnTheUnshiftedList_ThenRemovedOnTheShiftedList()
        {
            AddThreeMergeableSegments();
            var sequence = new List<string>();
            Exception handlerFailure = null;

            RecordingStore.OptimizationRecordingRemoving += (index, removed) =>
            {
                try
                {
                    var committed = RecordingStore.CommittedRecordings;
                    // Pre-removal: the index still addresses the absorbed recording.
                    Assert.Same(removed, committed[index]);
                    sequence.Add($"removing:{index}:{removed.RecordingId}:count={committed.Count}");
                }
                catch (Exception ex) { handlerFailure = ex; }
            };
            RecordingStore.OptimizationRecordingRemoved += (index, removed) =>
            {
                try
                {
                    var committed = RecordingStore.CommittedRecordings;
                    // Post-removal: the absorbed recording is gone and the list shrank by one.
                    Assert.DoesNotContain(committed, r => ReferenceEquals(r, removed));
                    sequence.Add($"removed:{index}:{removed.RecordingId}:count={committed.Count}");
                }
                catch (Exception ex) { handlerFailure = ex; }
            };

            RecordingStore.RunOptimizationPass();

            Assert.Null(handlerFailure);
            Assert.Equal(new[]
            {
                "removing:1:seg-b:count=3",
                "removed:1:seg-b:count=2",
                "removing:1:seg-c:count=2",
                "removed:1:seg-c:count=1",
            }, sequence);
        }

        [Fact]
        public void Split_RaisesInserted_AtTheMidListSecondHalfIndex()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("first"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeSplittableRecording("rec-split"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("last"));
            var inserted = new List<int>();
            Exception handlerFailure = null;

            // The pass contains subscriber exceptions (see the throwing-subscriber cell), so an
            // assertion failing inside the handler must be re-raised after the pass returns.
            RecordingStore.OptimizationRecordingInserted += index =>
            {
                try
                {
                    var committed = RecordingStore.CommittedRecordings;
                    // Post-insert: index-1 is the truncated first half, index is the second half,
                    // and everything that used to sit at index has moved up by one.
                    Assert.Equal("rec-split", committed[index - 1].RecordingId);
                    Assert.Equal(committed[index - 1].ChainId, committed[index].ChainId);
                    Assert.True(committed[index].StartUT >= committed[index - 1].EndUT,
                        $"second half starts at {committed[index].StartUT} before first half ends at {committed[index - 1].EndUT}");
                    Assert.Equal("last", committed[index + 1].RecordingId);
                    inserted.Add(index);
                }
                catch (Exception ex)
                {
                    handlerFailure = ex;
                }
            };

            RecordingStore.RunOptimizationPass();

            Assert.Null(handlerFailure);
            Assert.Equal(new[] { 2 }, inserted);
            Assert.Equal(4, RecordingStore.CommittedRecordings.Count);
        }

        [Fact]
        public void ThrowingSubscriber_IsLoggedAsError_AndThePassStillCompletes()
        {
            EnableLogCapture();
            RecordingStore.AddRecordingWithTreeForTesting(MakeSplittableRecording("rec-split"));
            RecordingStore.OptimizationRecordingInserted += index =>
                throw new InvalidOperationException("subscriber boom");

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[ERROR]") && l.Contains("[RecordingStore]")
                && l.Contains("OptimizationRecordingInserted subscriber threw")
                && l.Contains("subscriber boom"));
        }

        [Fact]
        public void ResetForTesting_ClearsSubscribers()
        {
            int calls = 0;
            RecordingStore.OptimizationRecordingInserted += index => calls++;
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(MakeSplittableRecording("rec-split"));

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Equal(0, calls);
        }

        #endregion

        #region Engine reindex mirrors

        [Fact]
        public void Engine_ReindexAfterInsert_ShiftsKeysAtOrAboveTheInsert()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var s0 = new GhostPlaybackState { vesselName = "s0" };
            var s1 = new GhostPlaybackState { vesselName = "s1" };
            var s3 = new GhostPlaybackState { vesselName = "s3" };
            engine.ghostStates[0] = s0;
            engine.ghostStates[1] = s1;
            engine.ghostStates[3] = s3;
            engine.loopPhaseOffsets[1] = 11.0;
            engine.loopPhaseOffsets[3] = 33.0;
            engine.overlapGhosts[3] = new List<GhostPlaybackState> { s3 };
            engine.loggedGhostEnter.UnionWith(new[] { 0, 1, 3 });
            engine.loggedReshow.UnionWith(new[] { 1 });

            engine.ReindexAfterInsert(1);

            Assert.Same(s0, engine.ghostStates[0]);
            Assert.False(engine.ghostStates.ContainsKey(1));
            Assert.Same(s1, engine.ghostStates[2]);
            Assert.Same(s3, engine.ghostStates[4]);
            Assert.Equal(3, engine.ghostStates.Count);
            Assert.Equal(11.0, engine.loopPhaseOffsets[2]);
            Assert.Equal(33.0, engine.loopPhaseOffsets[4]);
            Assert.Same(s3, engine.overlapGhosts[4][0]);
            Assert.Equal(new HashSet<int> { 0, 2, 4 }, engine.loggedGhostEnter);
            Assert.Equal(new HashSet<int> { 2 }, engine.loggedReshow);
        }

        [Fact]
        public void Engine_ReindexAfterInsert_AdjacentKeysDoNotOverwriteEachOther()
        {
            // Ascending iteration would move key 1 into 2 before 2 moved into 3, losing s2.
            var engine = new GhostPlaybackEngine(positioner: null);
            var s1 = new GhostPlaybackState { vesselName = "s1" };
            var s2 = new GhostPlaybackState { vesselName = "s2" };
            var s3 = new GhostPlaybackState { vesselName = "s3" };
            engine.ghostStates[1] = s1;
            engine.ghostStates[2] = s2;
            engine.ghostStates[3] = s3;

            engine.ReindexAfterInsert(1);

            Assert.Equal(3, engine.ghostStates.Count);
            Assert.Same(s1, engine.ghostStates[2]);
            Assert.Same(s2, engine.ghostStates[3]);
            Assert.Same(s3, engine.ghostStates[4]);
        }

        [Fact]
        public void Engine_InsertThenDeleteAtTheSameIndex_RoundTrips()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var s0 = new GhostPlaybackState { vesselName = "s0" };
            var s2 = new GhostPlaybackState { vesselName = "s2" };
            var s5 = new GhostPlaybackState { vesselName = "s5" };
            engine.ghostStates[0] = s0;
            engine.ghostStates[2] = s2;
            engine.ghostStates[5] = s5;
            engine.loggedGhostEnter.UnionWith(new[] { 0, 2, 5 });

            engine.ReindexAfterInsert(2);
            engine.ReindexAfterDelete(2);

            Assert.Same(s0, engine.ghostStates[0]);
            Assert.Same(s2, engine.ghostStates[2]);
            Assert.Same(s5, engine.ghostStates[5]);
            Assert.Equal(3, engine.ghostStates.Count);
            Assert.Equal(new HashSet<int> { 0, 2, 5 }, engine.loggedGhostEnter);
        }

        #endregion

        #region Watch-mode and continuation index mirrors

        private static List<Recording> MakeRecordings(params string[] ids)
        {
            var list = new List<Recording>();
            foreach (var id in ids)
                list.Add(new Recording { RecordingId = id, VesselName = "Vessel_" + id });
            return list;
        }

        [Fact]
        public void ComputeWatchIndexAfterInsert_BelowTheInsert_IsUnchanged()
        {
            // Watching #1 ("b"); inserted "x" at #3 -> [a, b, c, x, d]
            var recordings = MakeRecordings("a", "b", "c", "x", "d");
            var result = WatchModeController.ComputeWatchIndexAfterInsert(1, "b", 3, recordings);
            Assert.Equal((1, "b"), result);
        }

        [Fact]
        public void ComputeWatchIndexAfterInsert_AtOrAboveTheInsert_ShiftsUp()
        {
            // Watching #2 ("c"); inserted "x" at #2 -> [a, b, x, c, d]
            var recordings = MakeRecordings("a", "b", "x", "c", "d");
            Assert.Equal((3, "c"), WatchModeController.ComputeWatchIndexAfterInsert(2, "c", 2, recordings));
            Assert.Equal((4, "d"), WatchModeController.ComputeWatchIndexAfterInsert(3, "d", 2, recordings));
        }

        [Fact]
        public void ComputeWatchIndexAfterInsert_IdMismatch_ScansForTheId()
        {
            // The arithmetic says #3 but the list moved differently; the id scan wins.
            var recordings = MakeRecordings("c", "a", "b", "x", "d");
            Assert.Equal((0, "c"), WatchModeController.ComputeWatchIndexAfterInsert(2, "c", 2, recordings));
        }

        [Fact]
        public void ComputeWatchIndexAfterInsert_IdGone_ReturnsExit()
        {
            var recordings = MakeRecordings("a", "b", "x");
            Assert.Equal((-1, (string)null), WatchModeController.ComputeWatchIndexAfterInsert(1, "zzz", 1, recordings));
        }

        [Fact]
        public void ShiftTrackedIndex_RemovalAndInsertMirrors()
        {
            Assert.Equal(-1, ChainSegmentManager.ShiftTrackedIndexAfterRemoval(-1, 2));
            Assert.Equal(1, ChainSegmentManager.ShiftTrackedIndexAfterRemoval(1, 2));
            Assert.Equal(2, ChainSegmentManager.ShiftTrackedIndexAfterRemoval(2, 2)); // id guard reports it
            Assert.Equal(4, ChainSegmentManager.ShiftTrackedIndexAfterRemoval(5, 2));

            Assert.Equal(-1, ChainSegmentManager.ShiftTrackedIndexAfterInsert(-1, 2));
            Assert.Equal(1, ChainSegmentManager.ShiftTrackedIndexAfterInsert(1, 2));
            Assert.Equal(3, ChainSegmentManager.ShiftTrackedIndexAfterInsert(2, 2));
            Assert.Equal(6, ChainSegmentManager.ShiftTrackedIndexAfterInsert(5, 2));
        }

        [Fact]
        public void ChainSegmentManager_OnCommittedRecordingInserted_ShiftsBothContinuationIndices()
        {
            var manager = new ChainSegmentManager
            {
                ContinuationRecordingIdx = 4,
                UndockContinuationRecIdx = 1,
            };

            manager.OnCommittedRecordingInserted(2);
            Assert.Equal(5, manager.ContinuationRecordingIdx);
            Assert.Equal(1, manager.UndockContinuationRecIdx);

            manager.OnCommittedRecordingRemoved(0);
            Assert.Equal(4, manager.ContinuationRecordingIdx);
            Assert.Equal(0, manager.UndockContinuationRecIdx);
        }

        #endregion

        #region Recordings table sort cache

        [Fact]
        public void IsSortedIndicesCurrent_SameCountDifferentStateVersion_IsStale()
        {
            // A merge + split nets zero on the count; only the StateVersion sees it.
            Assert.False(RecordingsTableUI.IsSortedIndicesCurrent(
                hasSortedIndices: true, lastCount: 5, lastStateVersion: 10,
                committedCount: 5, stateVersion: 11));
        }

        [Fact]
        public void IsSortedIndicesCurrent_UnchangedCountAndVersion_IsCurrent()
        {
            Assert.True(RecordingsTableUI.IsSortedIndicesCurrent(
                hasSortedIndices: true, lastCount: 5, lastStateVersion: 10,
                committedCount: 5, stateVersion: 10));
        }

        [Fact]
        public void IsSortedIndicesCurrent_NoCacheOrCountChange_IsStale()
        {
            Assert.False(RecordingsTableUI.IsSortedIndicesCurrent(
                hasSortedIndices: false, lastCount: 5, lastStateVersion: 10,
                committedCount: 5, stateVersion: 10));
            Assert.False(RecordingsTableUI.IsSortedIndicesCurrent(
                hasSortedIndices: true, lastCount: 5, lastStateVersion: 10,
                committedCount: 6, stateVersion: 10));
        }

        #endregion
    }
}
