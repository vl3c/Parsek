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
    /// cannot serve a pre-pass set) and the store's structural notifications
    /// (<c>CommittedRecordingRemoving</c> / <c>Removed</c> / <c>Inserted</c>) plus the
    /// index-shift mirrors the FLIGHT controller applies from them.
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
            // MUTATION NOTE: deleting the BumpStateVersion() after the split Insert makes
            // ComputeERS return the cached single-entry list here.
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
        public void Merge_BumpsStateVersion_AtEachRemoval_NotOnlyAtTheEnd()
        {
            // A subscriber running inside the pass must already see a fresh ERS: the bump
            // sits at the mutation, not after both passes complete.
            AddThreeMergeableSegments();
            var ersSizesSeenByRemovedHandler = new List<int>();
            RecordingStore.CommittedRecordingRemoved += (index, removed, absorbedInto) =>
                ersSizesSeenByRemovedHandler.Add(EffectiveState.ComputeERS().Count);
            EffectiveState.ComputeERS();

            RecordingStore.RunOptimizationPass();

            Assert.Equal(new[] { 2, 1 }, ersSizesSeenByRemovedHandler);
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
        public void Merge_RaisesRemovingOnTheUnshiftedList_ThenRemovedWithTheMergeTarget()
        {
            AddThreeMergeableSegments();
            var sequence = new List<string>();
            Exception handlerFailure = null;

            // The pass contains subscriber exceptions (see the throwing-subscriber cell), so an
            // assertion failing inside a handler must be re-raised after the pass returns.
            RecordingStore.CommittedRecordingRemoving += (index, removed) =>
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
            RecordingStore.CommittedRecordingRemoved += (index, removed, absorbedInto) =>
            {
                try
                {
                    var committed = RecordingStore.CommittedRecordings;
                    // Post-removal: the absorbed recording is gone, the list shrank by one, and
                    // the merge target that now carries its trajectory is named.
                    Assert.DoesNotContain(committed, r => ReferenceEquals(r, removed));
                    Assert.NotNull(absorbedInto);
                    Assert.Contains(committed, r => ReferenceEquals(r, absorbedInto));
                    sequence.Add($"removed:{index}:{removed.RecordingId}->{absorbedInto.RecordingId}:count={committed.Count}");
                }
                catch (Exception ex) { handlerFailure = ex; }
            };

            RecordingStore.RunOptimizationPass();

            Assert.Null(handlerFailure);
            Assert.Equal(new[]
            {
                "removing:1:seg-b:count=3",
                "removed:1:seg-b->seg-a:count=2",
                "removing:1:seg-c:count=2",
                "removed:1:seg-c->seg-a:count=1",
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

            RecordingStore.CommittedRecordingInserted += index =>
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
        public void InsertCommittedAfter_RaisesInserted_AtTheInsertIndex()
        {
            // The Re-Fly origin splitter's TIP insert is the other mid-list producer.
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("a"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("b"));
            var inserted = new List<int>();
            RecordingStore.CommittedRecordingInserted += index => inserted.Add(index);

            RecordingStore.InsertCommittedAfter("a", MakeInertRecording("tip"));

            Assert.Equal(new[] { 1 }, inserted);
            Assert.Equal("tip", RecordingStore.CommittedRecordings[1].RecordingId);
            Assert.Equal("b", RecordingStore.CommittedRecordings[2].RecordingId);
        }

        [Fact]
        public void RemoveRecordingAt_RaisesRemovingThenRemoved_WithNoMergeTarget()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("a"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("b"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("c"));
            var sequence = new List<string>();
            RecordingStore.CommittedRecordingRemoving += (index, removed) =>
                sequence.Add($"removing:{index}:{removed.RecordingId}:count={RecordingStore.CommittedRecordings.Count}");
            RecordingStore.CommittedRecordingRemoved += (index, removed, absorbedInto) =>
                sequence.Add($"removed:{index}:{removed.RecordingId}:target={(absorbedInto == null ? "none" : absorbedInto.RecordingId)}:count={RecordingStore.CommittedRecordings.Count}");

            RecordingStore.RemoveRecordingAt(1);

            Assert.Equal(new[]
            {
                "removing:1:b:count=3",
                "removed:1:b:target=none:count=2",
            }, sequence);
            Assert.Equal("c", RecordingStore.CommittedRecordings[1].RecordingId);
        }

        [Fact]
        public void ThrowingSubscriber_IsLoggedAsError_AndThePassStillCompletes()
        {
            EnableLogCapture();
            RecordingStore.AddRecordingWithTreeForTesting(MakeSplittableRecording("rec-split"));
            RecordingStore.CommittedRecordingInserted += index =>
                throw new InvalidOperationException("subscriber boom");

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[ERROR]") && l.Contains("[RecordingStore]")
                && l.Contains("CommittedRecordingInserted subscriber threw")
                && l.Contains("subscriber boom"));
        }

        [Fact]
        public void ResetForTesting_ClearsSubscribers()
        {
            int calls = 0;
            RecordingStore.CommittedRecordingInserted += index => calls++;
            RecordingStore.ResetForTesting();
            RecordingStore.AddRecordingWithTreeForTesting(MakeSplittableRecording("rec-split"));

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Equal(0, calls);
        }

        #endregion

        #region IndexShift and the engine mirrors

        [Fact]
        public void IndexShift_DictAfterDelete_DropsTheRemovedKey_AndShiftsAbove()
        {
            var dict = new Dictionary<int, string> { [0] = "a", [2] = "c", [3] = "d", [5] = "f" };
            IndexShift.DictAfterDelete(dict, 2);
            Assert.Equal(new Dictionary<int, string> { [0] = "a", [2] = "d", [4] = "f" }, dict);
        }

        [Fact]
        public void IndexShift_DictAfterInsert_AdjacentKeysDoNotOverwriteEachOther()
        {
            // Ascending iteration would move key 1 into 2 before 2 moved into 3, losing "b".
            var dict = new Dictionary<int, string> { [1] = "a", [2] = "b", [3] = "c" };
            IndexShift.DictAfterInsert(dict, 1);
            Assert.Equal(new Dictionary<int, string> { [2] = "a", [3] = "b", [4] = "c" }, dict);
        }

        [Fact]
        public void IndexShift_SetMirrors()
        {
            var set = new HashSet<int> { 0, 2, 5 };
            IndexShift.SetAfterInsert(set, 2);
            Assert.Equal(new HashSet<int> { 0, 3, 6 }, set);
            IndexShift.SetAfterDelete(set, 3);
            Assert.Equal(new HashSet<int> { 0, 5 }, set);
        }

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

        [Fact]
        public void Engine_ReindexAfterDelete_DropsTheRemovedSlot()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            engine.overlapGhosts[2] = new List<GhostPlaybackState>();
            engine.overlapGhosts[3] = new List<GhostPlaybackState> { new GhostPlaybackState { vesselName = "s3" } };

            engine.ReindexAfterDelete(2);

            Assert.Single(engine.overlapGhosts);
            Assert.Equal("s3", engine.overlapGhosts[2][0].vesselName);
        }

        #endregion

        #region Held ghosts and map presence

        [Fact]
        public void MapPresence_FlightDicts_ShiftWithTheCommittedList()
        {
            try
            {
                GhostMapPresence.flightLastMapOrbitByIndex[1] = ("Kerbin", 700000, 0.01);
                GhostMapPresence.flightLastMapOrbitByIndex[3] = ("Mun", 300000, 0.02);
                GhostMapPresence.flightStateVectorCachedIndices[3] = 9;
                GhostMapPresence.flightSoiGapStateVectorExpectedBodies[4] = "Minmus";
                GhostMapPresence.flightChainMapOwner["chain-x"] = 3;
                GhostMapPresence.flightChainMapOwner["chain-y"] = 1;

                GhostMapPresence.ReindexPresenceAfterInsert(2);

                Assert.Equal("Kerbin", GhostMapPresence.flightLastMapOrbitByIndex[1].body);
                Assert.Equal("Mun", GhostMapPresence.flightLastMapOrbitByIndex[4].body);
                Assert.Equal(9, GhostMapPresence.flightStateVectorCachedIndices[4]);
                Assert.Equal("Minmus", GhostMapPresence.flightSoiGapStateVectorExpectedBodies[5]);
                Assert.Equal(4, GhostMapPresence.flightChainMapOwner["chain-x"]);
                Assert.Equal(1, GhostMapPresence.flightChainMapOwner["chain-y"]);

                GhostMapPresence.ReindexPresenceAfterDelete(1);

                Assert.False(GhostMapPresence.flightLastMapOrbitByIndex.ContainsKey(1));
                Assert.Equal("Mun", GhostMapPresence.flightLastMapOrbitByIndex[3].body);
                Assert.Equal(9, GhostMapPresence.flightStateVectorCachedIndices[3]);
                Assert.Equal("Minmus", GhostMapPresence.flightSoiGapStateVectorExpectedBodies[4]);
                Assert.Equal(3, GhostMapPresence.flightChainMapOwner["chain-x"]);
                // The removed slot's chain owner entry goes with it.
                Assert.False(GhostMapPresence.flightChainMapOwner.ContainsKey("chain-y"));
            }
            finally
            {
                GhostMapPresence.ClearFlightMapPresenceState();
            }
        }

        #endregion

        #region Watch-mode and continuation mirrors

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
        public void ComputeWatchIndexAfterDelete_StillExitsOnTheWatchedIndex_AndShiftsAbove()
        {
            var recordings = MakeRecordings("a", "c", "d");
            Assert.Equal((-1, (string)null), WatchModeController.ComputeWatchIndexAfterDelete(1, "b", 1, recordings));
            Assert.Equal((1, "c"), WatchModeController.ComputeWatchIndexAfterDelete(2, "c", 1, recordings));
        }

        [Fact]
        public void ResolveCommittedIndexThroughAbsorption_FollowsTheMergeChain()
        {
            var committed = MakeRecordings("p", "q", "r");
            var absorbed = new Dictionary<string, string>
            {
                ["seg-new"] = "seg-mid",
                ["seg-mid"] = "q",
            };
            Assert.Equal(1, ChainSegmentManager.ResolveCommittedIndexThroughAbsorption("seg-new", absorbed, committed));
            Assert.Equal(2, ChainSegmentManager.ResolveCommittedIndexThroughAbsorption("r", absorbed, committed));
            Assert.Equal(-1, ChainSegmentManager.ResolveCommittedIndexThroughAbsorption("gone", absorbed, committed));
            Assert.Equal(-1, ChainSegmentManager.ResolveCommittedIndexThroughAbsorption(null, absorbed, committed));
        }

        [Fact]
        public void ResolveCommittedIndexThroughAbsorption_IsCycleSafe()
        {
            var committed = MakeRecordings("p");
            var cycle = new Dictionary<string, string> { ["x"] = "y", ["y"] = "x" };
            Assert.Equal(-1, ChainSegmentManager.ResolveCommittedIndexThroughAbsorption("x", cycle, committed));
        }

        [Fact]
        public void ChainSegmentManager_Removed_RetargetsAnAbsorbedContinuation_ToTheMergeTarget()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("target"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("absorbed"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("above"));
            var manager = new ChainSegmentManager
            {
                ContinuationVesselPid = 77,
                ContinuationRecordingIdx = 1,
                ContinuationRecordingId = "absorbed",
                UndockContinuationPid = 88,
                UndockContinuationRecIdx = 2,
                UndockContinuationRecId = "above",
            };
            var target = RecordingStore.CommittedRecordings[0];
            var absorbed = RecordingStore.CommittedRecordings[1];

            RecordingStore.RemoveCommittedInternal(absorbed);
            manager.OnCommittedRecordingRemoved(1, absorbed, target);

            // The continuation follows its trajectory into the merge target; the undock
            // continuation above the removal is rebound by id one slot down.
            Assert.Equal(77u, manager.ContinuationVesselPid);
            Assert.Equal(0, manager.ContinuationRecordingIdx);
            Assert.Equal("target", manager.ContinuationRecordingId);
            Assert.Equal(88u, manager.UndockContinuationPid);
            Assert.Equal(1, manager.UndockContinuationRecIdx);
            Assert.Equal("above", manager.UndockContinuationRecId);
        }

        [Fact]
        public void ChainSegmentManager_Removed_StopsAContinuationWhoseRecordingWasDeleted()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("a"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("tracked"));
            var manager = new ChainSegmentManager
            {
                ContinuationVesselPid = 77,
                ContinuationRecordingIdx = 1,
                ContinuationRecordingId = "tracked",
            };
            var tracked = RecordingStore.CommittedRecordings[1];

            RecordingStore.RemoveCommittedInternal(tracked);
            manager.OnCommittedRecordingRemoved(1, tracked, null);

            Assert.Equal(0u, manager.ContinuationVesselPid);
            Assert.Equal(-1, manager.ContinuationRecordingIdx);
            Assert.Null(manager.ContinuationRecordingId);
        }

        [Fact]
        public void ChainSegmentManager_Inserted_RebindsBothContinuationsById()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("a"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("inserted"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("b"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeInertRecording("c"));
            // Indices as they were before "inserted" landed at #1.
            var manager = new ChainSegmentManager
            {
                ContinuationVesselPid = 77,
                ContinuationRecordingIdx = 1,
                ContinuationRecordingId = "b",
                UndockContinuationPid = 88,
                UndockContinuationRecIdx = 0,
                UndockContinuationRecId = "a",
            };

            manager.OnCommittedRecordingInserted(1);

            Assert.Equal(2, manager.ContinuationRecordingIdx);
            Assert.Equal(0, manager.UndockContinuationRecIdx);
        }

        #endregion
    }
}
