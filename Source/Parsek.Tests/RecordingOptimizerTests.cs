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
            SegmentEnvironment env1, SegmentEnvironment env2,
            string body1 = "Kerbin", string body2 = "Kerbin")
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 80000, bodyName = body1 });
            rec.Points.Add(new TrajectoryPoint { ut = midUT - 1, altitude = 40000, bodyName = body1 });
            rec.Points.Add(new TrajectoryPoint { ut = midUT, altitude = 30000, bodyName = body2 });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 100, bodyName = body2 });

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

        private static Recording MakeRecordingWith3Sections(
            double ut0, double ut1, double ut2, double ut3,
            SegmentEnvironment env1, SegmentEnvironment env2, SegmentEnvironment env3,
            string body1 = "Kerbin", string body2 = "Kerbin", string body3 = "Mun")
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = ut0, altitude = 80000, bodyName = body1 });
            rec.Points.Add(new TrajectoryPoint { ut = ut1 - 1, altitude = 40000, bodyName = body1 });
            rec.Points.Add(new TrajectoryPoint { ut = ut1, altitude = 30000, bodyName = body2 });
            rec.Points.Add(new TrajectoryPoint { ut = ut2 - 1, altitude = 20000, bodyName = body2 });
            rec.Points.Add(new TrajectoryPoint { ut = ut2, altitude = 10000, bodyName = body3 });
            rec.Points.Add(new TrajectoryPoint { ut = ut3, altitude = 100, bodyName = body3 });

            rec.TrackSections.Add(new TrackSection
            {
                environment = env1, startUT = ut0, endUT = ut1,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = env2, startUT = ut1, endUT = ut2,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = env3, startUT = ut2, endUT = ut3,
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
        public void CanAutoMerge_LoopStartUTSet_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            a.LoopStartUT = 500;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_LoopEndUTSet_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            b.LoopEndUT = 600;
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
        public void SplitAtSection_SurfaceTaggedAsSurface()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.SurfaceStationary);
            rec.SegmentBodyName = "Mun";
            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal("exo", rec.SegmentPhase);
            Assert.Equal("surface", second.SegmentPhase);
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

        #region CanAutoSplitIgnoringGhostTriggers

        [Fact]
        public void CanAutoSplitIgnoringGhostTriggers_AllowsEngineEvents()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.EngineIgnited, ut = 17010 });

            Assert.False(RecordingOptimizer.CanAutoSplit(rec, 1));
            Assert.True(RecordingOptimizer.CanAutoSplitIgnoringGhostTriggers(rec, 1));
        }

        [Fact]
        public void CanAutoSplitIgnoringGhostTriggers_StillChecksMinDuration()
        {
            var rec = MakeRecordingWithSections(17000, 17057, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            Assert.False(RecordingOptimizer.CanAutoSplitIgnoringGhostTriggers(rec, 1));
        }

        [Fact]
        public void CanAutoSplitIgnoringGhostTriggers_StillChecksSectionCount()
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
            Assert.False(RecordingOptimizer.CanAutoSplitIgnoringGhostTriggers(rec, 1));
        }

        #endregion

        #region FindSplitCandidatesForOptimizer

        [Fact]
        public void FindSplitCandidatesForOptimizer_FindsCandidatesWithGhostingEvents()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.EngineIgnited, ut = 17010 });
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.RCSActivated, ut = 17015 });
            var committed = new List<Recording> { rec };

            Assert.Empty(RecordingOptimizer.FindSplitCandidates(committed));
            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(committed);
            Assert.Single(candidates);
            Assert.Equal((0, 1), candidates[0]);
        }

        #endregion

        #region SplitAtSection field transfers

        [Fact]
        public void SplitAtSection_TransfersVesselSnapshotToSecondHalf()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.VesselSnapshot = new ConfigNode("VESSEL");

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Null(rec.VesselSnapshot);
            Assert.NotNull(second.VesselSnapshot);
        }

        [Fact]
        public void SplitAtSection_TransfersTerminalStateToSecondHalf()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitInclination = 28.5;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 700000;
            rec.TerminalOrbitLAN = 90.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 45.0;
            rec.TerminalOrbitMeanAnomalyAtEpoch = 1.23;
            rec.TerminalOrbitEpoch = 17050;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalPosition = new SurfacePosition { latitude = 1.0, longitude = 2.0 };
            rec.TerrainHeightAtEnd = 123.4;
            rec.SurfacePos = new SurfacePosition { latitude = 3.0, longitude = 4.0 };

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Null(rec.TerminalStateValue);
            Assert.Null(rec.TerminalOrbitBody);
            Assert.Null(rec.TerminalPosition);
            Assert.True(double.IsNaN(rec.TerrainHeightAtEnd));
            Assert.Null(rec.SurfacePos);

            Assert.Equal(TerminalState.Orbiting, second.TerminalStateValue);
            Assert.Equal(28.5, second.TerminalOrbitInclination);
            Assert.Equal(0.01, second.TerminalOrbitEccentricity);
            Assert.Equal(700000, second.TerminalOrbitSemiMajorAxis);
            Assert.Equal(90.0, second.TerminalOrbitLAN);
            Assert.Equal(45.0, second.TerminalOrbitArgumentOfPeriapsis);
            Assert.Equal(1.23, second.TerminalOrbitMeanAnomalyAtEpoch);
            Assert.Equal(17050, second.TerminalOrbitEpoch);
            Assert.Equal("Kerbin", second.TerminalOrbitBody);
            Assert.NotNull(second.TerminalPosition);
            Assert.Equal(123.4, second.TerrainHeightAtEnd);
            Assert.NotNull(second.SurfacePos);
        }

        [Fact]
        public void SplitAtSection_CopiesControllersToBoth()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.Controllers = new List<ControllerInfo>
            {
                new ControllerInfo { partPersistentId = 1, type = "ProbeCore" }
            };

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.NotNull(rec.Controllers);
            Assert.Single(rec.Controllers);
            Assert.NotNull(second.Controllers);
            Assert.Single(second.Controllers);
            Assert.NotSame(rec.Controllers, second.Controllers);
        }

        [Fact]
        public void SplitAtSection_CopiesAntennaSpecsToBoth()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.AntennaSpecs = new List<AntennaSpec>
            {
                new AntennaSpec { partName = "antenna1", antennaPower = 500000 }
            };

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.NotNull(rec.AntennaSpecs);
            Assert.Single(rec.AntennaSpecs);
            Assert.NotNull(second.AntennaSpecs);
            Assert.Single(second.AntennaSpecs);
            Assert.NotSame(rec.AntennaSpecs, second.AntennaSpecs);
        }

        [Fact]
        public void SplitAtSection_CopiesIsDebris()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.IsDebris = true;

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.True(rec.IsDebris);
            Assert.True(second.IsDebris);
        }

        [Fact]
        public void SplitAtSection_CopiesRecordingFormatVersion()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.RecordingFormatVersion = 7;

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal(7, second.RecordingFormatVersion);
        }

        #endregion

        #region RunOptimizationPass integration -- split

        [Fact]
        public void RunOptimizationPass_SplitsMultiEnvRecording()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWith3Sections(
                17000, 17030, 17060, 17090,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic, SegmentEnvironment.SurfaceStationary,
                body1: "Kerbin", body2: "Kerbin", body3: "Mun");
            rec.RecordingId = "original_id";
            rec.VesselName = "TestVessel";
            rec.VesselPersistentId = 12345;

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(3, recordings.Count);
            Assert.NotNull(recordings[0].ChainId);
            Assert.Equal(recordings[0].ChainId, recordings[1].ChainId);
            Assert.Equal(recordings[0].ChainId, recordings[2].ChainId);
            Assert.Equal("TestVessel", recordings[0].VesselName);
            Assert.Equal("TestVessel", recordings[1].VesselName);
            Assert.Equal("TestVessel", recordings[2].VesselName);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitAssignsChainIdAndIndexes()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "orig_id";

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.NotEmpty(recordings[0].ChainId);
            Assert.Equal(recordings[0].ChainId, recordings[1].ChainId);
            Assert.Equal(0, recordings[0].ChainIndex);
            Assert.Equal(1, recordings[1].ChainIndex);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitPreservesTreeId()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "rec_in_tree";
            rec.TreeId = "tree_001";

            var tree = new RecordingTree
            {
                Id = "tree_001",
                Recordings = new System.Collections.Generic.Dictionary<string, Recording>
                {
                    { "rec_in_tree", rec }
                }
            };
            RecordingStore.CommittedTrees.Add(tree);

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.Equal("tree_001", recordings[0].TreeId);
            Assert.Equal("tree_001", recordings[1].TreeId);
            Assert.True(tree.Recordings.ContainsKey(recordings[1].RecordingId));

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitDerivesSegmentBodyNameFromPoints()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                body1: "Kerbin", body2: "Mun");
            rec.RecordingId = "body_test";

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.Equal("Kerbin", recordings[0].SegmentBodyName);
            Assert.Equal("Mun", recordings[1].SegmentBodyName);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitUpdatesBranchPointParentRecordingIds()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "parent_rec";
            rec.TreeId = "tree_bp";
            rec.ChildBranchPointId = "bp_001";

            var bp = new BranchPoint
            {
                Id = "bp_001",
                UT = 17060,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "parent_rec" }
            };
            var tree = new RecordingTree
            {
                Id = "tree_bp",
                BranchPoints = new List<BranchPoint> { bp },
                Recordings = new System.Collections.Generic.Dictionary<string, Recording>
                {
                    { "parent_rec", rec }
                }
            };
            RecordingStore.CommittedTrees.Add(tree);

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.Null(recordings[0].ChildBranchPointId);
            Assert.Equal("bp_001", recordings[1].ChildBranchPointId);
            Assert.Single(bp.ParentRecordingIds);
            Assert.Equal(recordings[1].RecordingId, bp.ParentRecordingIds[0]);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SingleEnvNotSplit()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "single_env";

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Single(recordings);
            Assert.Equal("single_env", recordings[0].RecordingId);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitIsIdempotent()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "idem_test";

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();
            Assert.Equal(2, recordings.Count);
            string chainId = recordings[0].ChainId;
            string id0 = recordings[0].RecordingId;
            string id1 = recordings[1].RecordingId;

            RecordingStore.RunOptimizationPass();
            Assert.Equal(2, recordings.Count);
            Assert.Equal(chainId, recordings[0].ChainId);
            Assert.Equal(id0, recordings[0].RecordingId);
            Assert.Equal(id1, recordings[1].RecordingId);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitCopiesPreLaunchBudget()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "budget_test";
            rec.PreLaunchFunds = 100000;
            rec.PreLaunchScience = 50;
            rec.PreLaunchReputation = 25;

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.Equal(100000, recordings[1].PreLaunchFunds);
            Assert.Equal(50, recordings[1].PreLaunchScience);
            Assert.Equal(25, recordings[1].PreLaunchReputation);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitCopiesRecordingGroups()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "groups_test";
            rec.RecordingGroups = new List<string> { "Launches", "Mun Missions" };

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.NotNull(recordings[1].RecordingGroups);
            Assert.Equal(2, recordings[1].RecordingGroups.Count);
            Assert.Contains("Launches", recordings[1].RecordingGroups);
            Assert.Contains("Mun Missions", recordings[1].RecordingGroups);
            Assert.NotSame(recordings[0].RecordingGroups, recordings[1].RecordingGroups);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_DoesNotSetParentRecordingId()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "no_parent_test";

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            // MUST-FIX 1: ParentRecordingId should NOT be set on the second half
            Assert.Null(recordings[1].ParentRecordingId);

            RecordingStore.ResetForTesting();
        }

        #endregion

        #region SplitAtSection boundary interpolation

        [Fact]
        public void SplitAtSection_PointExactlyAtSplitUT_NoExtraPointCreated()
        {
            // MakeRecordingWithSections places a point at exactly midUT (17030),
            // so the interpolation path should NOT be triggered.
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            int originalPointCount = rec.Points.Count;

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            // Total points should equal original count (no interpolation added)
            Assert.Equal(originalPointCount, rec.Points.Count + second.Points.Count);

            // The split boundary point (UT=17030) should end up in the second half
            // (since splitPointIdx is the first point >= splitUT)
            Assert.True(second.Points[0].ut <= 17030.0);
        }

        [Fact]
        public void SplitAtSection_PointExactlyAtSplitUT_FirstHalfEndsBeforeBoundary()
        {
            // When a point exists at exactly splitUT, the first half should contain
            // points strictly before splitUT, and the second half starts at splitUT.
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            // First half's last point should be before splitUT
            Assert.True(rec.Points[rec.Points.Count - 1].ut < 17030.0);

            // Second half's first point should be at splitUT
            Assert.Equal(17030.0, second.Points[0].ut);
        }

        [Fact]
        public void SplitAtSection_ZeroPoints_HandlesGracefully()
        {
            // Recording with 0 points but valid track sections (degenerate case)
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17000, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 17030, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            // Both halves should have 0 points (no crash)
            Assert.Empty(rec.Points);
            Assert.Empty(second.Points);

            // Sections should still be partitioned correctly
            Assert.Single(rec.TrackSections);
            Assert.Single(second.TrackSections);
        }

        [Fact]
        public void SplitAtSection_ZeroPoints_PreservesEnvironmentTags()
        {
            // Even with zero points, section environment should be preserved
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17000, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                startUT = 17030, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
            Assert.Equal(SegmentEnvironment.SurfaceStationary, second.TrackSections[0].environment);
            Assert.Equal("atmo", rec.SegmentPhase);
            Assert.Equal("surface", second.SegmentPhase);
        }

        [Fact]
        public void SplitAtSection_AllPointsBeforeSplitUT_SecondHalfEmpty()
        {
            // All points are before splitUT — second half gets no points.
            // (No gap to interpolate because splitPointIdx ends up at Points.Count)
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 17010, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17020, bodyName = "Kerbin" });

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17010, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 17030, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            // All points stay in first half, second half is empty
            Assert.Equal(2, rec.Points.Count);
            Assert.Empty(second.Points);
        }

        // NOTE: The boundary interpolation path in SplitAtSection (lines 308-336 in
        // RecordingOptimizer.cs) — which creates a synthetic point via Quaternion.Slerp
        // and Vector3.Lerp when no trajectory point falls at exactly splitUT — cannot
        // be unit tested because Quaternion.Slerp and Vector3.Lerp are Unity native
        // methods that throw SecurityException outside the engine runtime.
        //
        // Integration test checklist for boundary interpolation:
        //   1. Record a trajectory with sparse sampling across a TrackSection boundary
        //      (e.g., points at UT=100 and UT=200, section boundary at UT=150)
        //   2. Trigger an optimization split at that boundary
        //   3. Verify: interpolated point appears at UT=150 in BOTH halves
        //   4. Verify: lat/lon/alt are linearly interpolated (t=0.5 in above example)
        //   5. Verify: rotation is slerped, velocity is lerped
        //   6. Verify: funds/science/reputation are interpolated

        #endregion
    }
}
