using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingOptimizerTests : System.IDisposable
    {
        public RecordingOptimizerTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        #region Helpers

        private static Recording MakeChainSegment(string chainId, int chainIndex,
            string phase = "exo", string body = "Mun", double startUT = 17000, double endUT = 17060)
        {
            var rec = new Recording
            {
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                SegmentPhase = phase,
                SegmentBodyName = body,
                LoopPlayback = false,
                PlaybackEnabled = true,
                Hidden = false,
                LoopIntervalSeconds = 10.0,
                LoopAnchorVesselId = 0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 50000 });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 50000 });
            return rec;
        }

        private static Recording MakeRecordingWithSections(double startUT, double midUT, double endUT,
            SegmentEnvironment env1, SegmentEnvironment env2)
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 80000 });
            rec.Points.Add(new TrajectoryPoint { ut = midUT - 1, altitude = 40000 });
            rec.Points.Add(new TrajectoryPoint { ut = midUT, altitude = 30000 });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 100 });

            rec.TrackSections.Add(new TrackSection
            {
                environment = env1,
                startUT = startUT,
                endUT = midUT,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = env2,
                startUT = midUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>()
            });
            return rec;
        }

        #endregion

        #region CanAutoMerge

        [Fact]
        public void CanAutoMerge_SamePhase_DefaultSettings_ReturnsTrue()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_DifferentPhase_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0, phase: "exo");
            var b = MakeChainSegment("chain1", 1, phase: "approach");
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_DifferentBody_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0, body: "Mun");
            var b = MakeChainSegment("chain1", 1, body: "Minmus");
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_LoopEnabled_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            a.LoopPlayback = true;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_PlaybackDisabled_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            b.PlaybackEnabled = false;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_Hidden_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            a.Hidden = true;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_CustomLoopInterval_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            b.LoopIntervalSeconds = 30.0;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_AnchorSet_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            a.LoopAnchorVesselId = 12345;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_DifferentGroups_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            a.RecordingGroups = new List<string> { "GroupA" };
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_SameGroups_ReturnsTrue()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            a.RecordingGroups = new List<string> { "GroupA" };
            b.RecordingGroups = new List<string> { "GroupA" };
            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_BranchPointBetween_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            a.ChildBranchPointId = "bp_001";
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_HasGhostingTriggerEvents_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            a.PartEvents.Add(new PartEvent { eventType = PartEventType.FairingJettisoned, ut = 17010 });
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_DifferentChain_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain2", 1);
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_NonConsecutiveIndex_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 2); // gap
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_NonZeroBranch_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            b.ChainBranch = 1;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_NullInputs_ReturnsFalse()
        {
            Assert.False(RecordingOptimizer.CanAutoMerge(null, new Recording()));
            Assert.False(RecordingOptimizer.CanAutoMerge(new Recording(), null));
        }

        #endregion

        #region CanAutoSplit

        [Fact]
        public void CanAutoSplit_NoGhostingTriggers_ReturnsTrue()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            Assert.True(RecordingOptimizer.CanAutoSplit(rec, 1));
        }

        [Fact]
        public void CanAutoSplit_HasGhostingTriggers_ReturnsFalse()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.Decoupled, ut = 17010 });
            Assert.False(RecordingOptimizer.CanAutoSplit(rec, 1));
        }

        [Fact]
        public void CanAutoSplit_HalfTooShort_ReturnsFalse()
        {
            // Second half only 3s
            var rec = MakeRecordingWithSections(17000, 17057, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            Assert.False(RecordingOptimizer.CanAutoSplit(rec, 1));
        }

        [Fact]
        public void CanAutoSplit_SingleSection_ReturnsFalse()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 17000 });
            rec.Points.Add(new TrajectoryPoint { ut = 17060 });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17000, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });
            Assert.False(RecordingOptimizer.CanAutoSplit(rec, 1));
        }

        [Fact]
        public void CanAutoSplit_IndexOutOfRange_ReturnsFalse()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            Assert.False(RecordingOptimizer.CanAutoSplit(rec, 0)); // can't split before first
            Assert.False(RecordingOptimizer.CanAutoSplit(rec, 2)); // out of range
        }

        [Fact]
        public void CanAutoSplit_NullRecording_ReturnsFalse()
        {
            Assert.False(RecordingOptimizer.CanAutoSplit(null, 1));
        }

        #endregion

        #region FindMergeCandidates

        [Fact]
        public void FindMergeCandidates_ThreeExoSegments_ReturnsTwoPairs()
        {
            var committed = new List<Recording>
            {
                MakeChainSegment("chain1", 0, startUT: 17000, endUT: 17060),
                MakeChainSegment("chain1", 1, startUT: 17060, endUT: 17120),
                MakeChainSegment("chain1", 2, startUT: 17120, endUT: 17180),
            };

            var candidates = RecordingOptimizer.FindMergeCandidates(committed);
            Assert.Equal(2, candidates.Count);
            Assert.Equal((0, 1), candidates[0]);
            Assert.Equal((1, 2), candidates[1]);
        }

        [Fact]
        public void FindMergeCandidates_MixedPhases_OnlyMatchingPairs()
        {
            var committed = new List<Recording>
            {
                MakeChainSegment("chain1", 0, phase: "exo", startUT: 17000, endUT: 17060),
                MakeChainSegment("chain1", 1, phase: "approach", startUT: 17060, endUT: 17120),
                MakeChainSegment("chain1", 2, phase: "approach", startUT: 17120, endUT: 17180),
            };

            var candidates = RecordingOptimizer.FindMergeCandidates(committed);
            Assert.Single(candidates);
            Assert.Equal((1, 2), candidates[0]);
        }

        [Fact]
        public void FindMergeCandidates_EmptyList_ReturnsEmpty()
        {
            Assert.Empty(RecordingOptimizer.FindMergeCandidates(new List<Recording>()));
        }

        [Fact]
        public void FindMergeCandidates_NoChain_ReturnsEmpty()
        {
            var committed = new List<Recording>
            {
                new Recording { SegmentPhase = "exo" },
                new Recording { SegmentPhase = "exo" },
            };
            Assert.Empty(RecordingOptimizer.FindMergeCandidates(committed));
        }

        #endregion

        #region FindSplitCandidates

        [Fact]
        public void FindSplitCandidates_EnvironmentChange_ReturnsSplit()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            var committed = new List<Recording> { rec };

            var candidates = RecordingOptimizer.FindSplitCandidates(committed);
            Assert.Single(candidates);
            Assert.Equal((0, 1), candidates[0]);
        }

        [Fact]
        public void FindSplitCandidates_SameEnvironment_ReturnsEmpty()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoBallistic);
            var committed = new List<Recording> { rec };

            Assert.Empty(RecordingOptimizer.FindSplitCandidates(committed));
        }

        #endregion

        #region MergeInto

        [Fact]
        public void MergeInto_ConcatenatesPoints()
        {
            var a = MakeChainSegment("c1", 0, startUT: 17000, endUT: 17030);
            var b = MakeChainSegment("c1", 1, startUT: 17030, endUT: 17060);
            RecordingOptimizer.MergeInto(a, b);
            Assert.Equal(4, a.Points.Count); // 2 + 2
            Assert.Equal(17000, a.Points[0].ut);
            Assert.Equal(17060, a.Points[3].ut);
        }

        [Fact]
        public void MergeInto_MergesPartEvents_Sorted()
        {
            var a = MakeChainSegment("c1", 0, startUT: 17000, endUT: 17030);
            var b = MakeChainSegment("c1", 1, startUT: 17030, endUT: 17060);
            a.PartEvents.Add(new PartEvent { ut = 17020, eventType = PartEventType.LightOn });
            b.PartEvents.Add(new PartEvent { ut = 17010, eventType = PartEventType.LightOff });
            RecordingOptimizer.MergeInto(a, b);
            Assert.Equal(2, a.PartEvents.Count);
            Assert.True(a.PartEvents[0].ut <= a.PartEvents[1].ut);
        }

        [Fact]
        public void MergeInto_InheritsVesselSnapshot_WhenAbsorbedIsChainTip()
        {
            var a = MakeChainSegment("c1", 0);
            var b = MakeChainSegment("c1", 1);
            a.VesselSnapshot = null; // mid-chain
            b.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingOptimizer.MergeInto(a, b);
            Assert.NotNull(a.VesselSnapshot);
        }

        [Fact]
        public void MergeInto_KeepsNullSnapshot_WhenAbsorbedIsMidChain()
        {
            var a = MakeChainSegment("c1", 0);
            var b = MakeChainSegment("c1", 1);
            a.VesselSnapshot = null;
            b.VesselSnapshot = null;
            RecordingOptimizer.MergeInto(a, b);
            Assert.Null(a.VesselSnapshot);
        }

        [Fact]
        public void MergeInto_InheritsTerminalState()
        {
            var a = MakeChainSegment("c1", 0);
            var b = MakeChainSegment("c1", 1);
            b.TerminalStateValue = TerminalState.Landed;
            RecordingOptimizer.MergeInto(a, b);
            Assert.Equal(TerminalState.Landed, a.TerminalStateValue);
        }

        [Fact]
        public void MergeInto_InvalidatesGhostGeometry()
        {
            var a = MakeChainSegment("c1", 0);
            a.GhostGeometryAvailable = true;
            a.GhostGeometryRelativePath = "old/path.pcrf";
            var b = MakeChainSegment("c1", 1);
            RecordingOptimizer.MergeInto(a, b);
            Assert.False(a.GhostGeometryAvailable);
            Assert.Null(a.GhostGeometryRelativePath);
        }

        [Fact]
        public void MergeInto_ClearsExplicitUTRanges()
        {
            var a = MakeChainSegment("c1", 0);
            a.ExplicitStartUT = 17000;
            a.ExplicitEndUT = 17030;
            var b = MakeChainSegment("c1", 1);
            RecordingOptimizer.MergeInto(a, b);
            Assert.True(double.IsNaN(a.ExplicitStartUT));
            Assert.True(double.IsNaN(a.ExplicitEndUT));
        }

        #endregion

        #region SplitAtSection

        [Fact]
        public void SplitAtSection_PartitionsPoints()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            int originalCount = rec.Points.Count;
            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.True(rec.Points.Count > 0);
            Assert.True(second.Points.Count > 0);
            Assert.Equal(originalCount, rec.Points.Count + second.Points.Count);
            Assert.True(rec.Points[rec.Points.Count - 1].ut < second.Points[0].ut);
        }

        [Fact]
        public void SplitAtSection_PartitionsSections()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Single(rec.TrackSections);
            Assert.Single(second.TrackSections);
            Assert.Equal(SegmentEnvironment.ExoBallistic, rec.TrackSections[0].environment);
            Assert.Equal(SegmentEnvironment.ExoPropulsive, second.TrackSections[0].environment);
        }

        [Fact]
        public void SplitAtSection_ClonesSnapshot()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            rec.GhostVisualSnapshot = new ConfigNode("VESSEL");
            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.NotNull(second.GhostVisualSnapshot);
            // Should be separate instances
            Assert.NotSame(rec.GhostVisualSnapshot, second.GhostVisualSnapshot);
        }

        [Fact]
        public void SplitAtSection_TagsPhaseFromEnvironment()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.SegmentBodyName = "Kerbin";
            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal("exo", rec.SegmentPhase);
            Assert.Equal("atmo", second.SegmentPhase);
            Assert.Equal("Kerbin", second.SegmentBodyName);
        }

        [Fact]
        public void SplitAtSection_BothHalvesHaveValidUTRanges()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.True(rec.StartUT < rec.EndUT);
            Assert.True(second.StartUT < second.EndUT);
            Assert.True(rec.EndUT <= second.StartUT);
        }

        #endregion

        #region ReindexChain

        [Fact]
        public void ReindexChain_AfterMerge_IndicesSequential()
        {
            var committed = new List<Recording>
            {
                MakeChainSegment("c1", 0, startUT: 17000, endUT: 17030),
                // index 1 was merged into 0, so index 2 is now at position 1
                MakeChainSegment("c1", 2, startUT: 17060, endUT: 17090),
            };
            RecordingOptimizer.ReindexChain(committed, "c1");
            Assert.Equal(0, committed[0].ChainIndex);
            Assert.Equal(1, committed[1].ChainIndex);
        }

        [Fact]
        public void ReindexChain_PreservesChainBranch()
        {
            var committed = new List<Recording>
            {
                MakeChainSegment("c1", 0, startUT: 17000, endUT: 17030),
                MakeChainSegment("c1", 5, startUT: 17060, endUT: 17090), // gap
            };
            // Add a branch-1 recording — should not be re-indexed
            var branch = MakeChainSegment("c1", 0, startUT: 17030, endUT: 17060);
            branch.ChainBranch = 1;
            committed.Add(branch);

            RecordingOptimizer.ReindexChain(committed, "c1");
            Assert.Equal(0, committed[0].ChainIndex);
            Assert.Equal(1, committed[1].ChainIndex);
            Assert.Equal(0, committed[2].ChainIndex); // branch-1 untouched
        }

        #endregion

        #region RunOptimizationPass integration

        [Fact]
        public void RunOptimizationPass_MergesThreeExoSegments()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            // Commit 3 consecutive exo segments in the same chain
            var a = MakeChainSegment("chain1", 0, startUT: 17000, endUT: 17030);
            var b = MakeChainSegment("chain1", 1, startUT: 17030, endUT: 17060);
            var c = MakeChainSegment("chain1", 2, startUT: 17060, endUT: 17090);

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(a);
            recordings.Add(b);
            recordings.Add(c);

            RecordingStore.RunOptimizationPass();

            // Should be merged into 1 recording
            Assert.Single(recordings);
            Assert.Equal(0, recordings[0].ChainIndex);
            Assert.Equal(6, recordings[0].Points.Count); // 2+2+2
            Assert.Equal(17000, recordings[0].StartUT);
            Assert.Equal(17090, recordings[0].EndUT);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SkipsUserModifiedSegments()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var a = MakeChainSegment("chain1", 0, startUT: 17000, endUT: 17030);
            var b = MakeChainSegment("chain1", 1, startUT: 17030, endUT: 17060);
            b.LoopPlayback = true; // user enabled loop — blocks merge
            var c = MakeChainSegment("chain1", 2, startUT: 17060, endUT: 17090);

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(a);
            recordings.Add(b);
            recordings.Add(c);

            RecordingStore.RunOptimizationPass();

            // No merges should occur (b blocks both pairs)
            Assert.Equal(3, recordings.Count);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_MergesDifferentChainsSeparately()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var a1 = MakeChainSegment("chain1", 0, startUT: 17000, endUT: 17030);
            var a2 = MakeChainSegment("chain1", 1, startUT: 17030, endUT: 17060);
            var b1 = MakeChainSegment("chain2", 0, phase: "approach", startUT: 17000, endUT: 17020);
            var b2 = MakeChainSegment("chain2", 1, phase: "approach", startUT: 17020, endUT: 17040);

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(a1);
            recordings.Add(a2);
            recordings.Add(b1);
            recordings.Add(b2);

            RecordingStore.RunOptimizationPass();

            // Both chains should be merged independently → 2 recordings
            Assert.Equal(2, recordings.Count);

            RecordingStore.ResetForTesting();
        }

        #endregion
    }
}
