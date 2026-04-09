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
        public void FindSplitCandidates_CoarseEnvironmentChange_ReturnsSplit()
        {
            // Atmospheric → ExoBallistic is a real environment class change
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            var committed = new List<Recording> { rec };

            var candidates = RecordingOptimizer.FindSplitCandidates(committed);
            Assert.Single(candidates);
            Assert.Equal((0, 1), candidates[0]);
        }

        [Fact]
        public void FindSplitCandidates_ExoToExo_ReturnsEmpty()
        {
            // ExoBallistic → ExoPropulsive is the same coarse class — no split
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoPropulsive);
            var committed = new List<Recording> { rec };

            Assert.Empty(RecordingOptimizer.FindSplitCandidates(committed));
        }

        [Fact]
        public void FindSplitCandidates_SameEnvironment_ReturnsEmpty()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.ExoBallistic);
            var committed = new List<Recording> { rec };

            Assert.Empty(RecordingOptimizer.FindSplitCandidates(committed));
        }

        [Fact]
        public void FindSplitCandidates_SurfaceMobileToStationary_ReturnsEmpty()
        {
            // SurfaceMobile → SurfaceStationary is the same coarse class
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.SurfaceMobile, SegmentEnvironment.SurfaceStationary);
            var committed = new List<Recording> { rec };

            Assert.Empty(RecordingOptimizer.FindSplitCandidates(committed));
        }

        [Fact]
        public void FindSplitCandidates_ExoToSurface_ReturnsSplit()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.SurfaceMobile);
            var committed = new List<Recording> { rec };

            var candidates = RecordingOptimizer.FindSplitCandidates(committed);
            Assert.Single(candidates);
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

        [Fact]
        public void FindSplitCandidatesForOptimizer_SkipsExoToExo()
        {
            // ExoPropulsive → ExoBallistic in same coarse class — no split even for optimizer
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoPropulsive, SegmentEnvironment.ExoBallistic);
            var committed = new List<Recording> { rec };

            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(committed));
        }

        #endregion

        #region SplitEnvironmentClass

        [Fact]
        public void SplitEnvironmentClass_ExoTypesAreSameClass()
        {
            Assert.Equal(
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.ExoPropulsive),
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.ExoBallistic));
        }

        [Fact]
        public void SplitEnvironmentClass_SurfaceTypesAreSameClass()
        {
            Assert.Equal(
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.SurfaceMobile),
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.SurfaceStationary));
        }

        [Fact]
        public void SplitEnvironmentClass_AtmoAndExoAreDifferent()
        {
            Assert.NotEqual(
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.Atmospheric),
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.ExoBallistic));
        }

        [Fact]
        public void SplitEnvironmentClass_ExoAndSurfaceAreDifferent()
        {
            Assert.NotEqual(
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.ExoBallistic),
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.SurfaceMobile));
        }

        [Fact]
        public void SplitEnvironmentClass_ApproachIsDistinctFromExo()
        {
            Assert.NotEqual(
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.Approach),
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.ExoBallistic));
        }

        [Fact]
        public void SplitEnvironmentClass_ApproachIsDistinctFromSurface()
        {
            Assert.NotEqual(
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.Approach),
                RecordingOptimizer.SplitEnvironmentClass(SegmentEnvironment.SurfaceMobile));
        }

        [Fact]
        public void FindSplitCandidates_ExoToApproach_ReturnsSplit()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Approach);
            var committed = new List<Recording> { rec };

            var candidates = RecordingOptimizer.FindSplitCandidates(committed);
            Assert.Single(candidates);
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
            rec.RecordingFormatVersion = 0;

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal(0, second.RecordingFormatVersion);
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

        #region PopulateLoopSyncParentIndices

        [Fact]
        public void PopulateLoopSync_LinksDebrisToParent()
        {
            var parent = new Recording
            {
                RecordingId = "parent1",
                TreeId = "tree1",
                VesselPersistentId = 100,
                IsDebris = false
            };
            parent.Points.Add(new TrajectoryPoint { ut = 10 });
            parent.Points.Add(new TrajectoryPoint { ut = 50 });

            var debris = new Recording
            {
                RecordingId = "debris1",
                TreeId = "tree1",
                VesselPersistentId = 200, // different vessel (detached part)
                IsDebris = true
            };
            debris.Points.Add(new TrajectoryPoint { ut = 30 });
            debris.Points.Add(new TrajectoryPoint { ut = 35 });

            var recordings = new List<Recording> { parent, debris };
            RecordingStore.PopulateLoopSyncParentIndices(recordings);

            Assert.Equal(0, debris.LoopSyncParentIdx);
            Assert.Equal(-1, parent.LoopSyncParentIdx);
        }

        [Fact]
        public void PopulateLoopSync_NonDebrisGetsMinusOne()
        {
            var rec = new Recording
            {
                RecordingId = "rec1",
                TreeId = "tree1",
                VesselPersistentId = 100,
                IsDebris = false
            };
            rec.Points.Add(new TrajectoryPoint { ut = 10 });
            rec.Points.Add(new TrajectoryPoint { ut = 50 });

            var recordings = new List<Recording> { rec };
            RecordingStore.PopulateLoopSyncParentIndices(recordings);

            Assert.Equal(-1, rec.LoopSyncParentIdx);
        }

        [Fact]
        public void PopulateLoopSync_DebrisWithoutMatchingParent_GetsMinusOne()
        {
            var debris = new Recording
            {
                RecordingId = "debris1",
                TreeId = "tree1",
                VesselPersistentId = 200,
                IsDebris = true
            };
            debris.Points.Add(new TrajectoryPoint { ut = 30 });
            debris.Points.Add(new TrajectoryPoint { ut = 35 });

            // No parent recording in the list
            var recordings = new List<Recording> { debris };
            RecordingStore.PopulateLoopSyncParentIndices(recordings);

            Assert.Equal(-1, debris.LoopSyncParentIdx);
        }

        [Fact]
        public void PopulateLoopSync_DebrisWithoutTreeId_GetsMinusOne()
        {
            var debris = new Recording
            {
                RecordingId = "debris1",
                TreeId = null,
                VesselPersistentId = 200,
                IsDebris = true
            };
            debris.Points.Add(new TrajectoryPoint { ut = 30 });
            debris.Points.Add(new TrajectoryPoint { ut = 35 });

            var recordings = new List<Recording> { debris };
            RecordingStore.PopulateLoopSyncParentIndices(recordings);

            Assert.Equal(-1, debris.LoopSyncParentIdx);
        }

        [Fact]
        public void PopulateLoopSync_MultipleDebris_LinkToCorrectParents()
        {
            var parent1 = new Recording
            {
                RecordingId = "p1", TreeId = "tree1", VesselPersistentId = 100, IsDebris = false
            };
            parent1.Points.Add(new TrajectoryPoint { ut = 10 });
            parent1.Points.Add(new TrajectoryPoint { ut = 50 });

            var parent2 = new Recording
            {
                RecordingId = "p2", TreeId = "tree1", VesselPersistentId = 100, IsDebris = false
            };
            parent2.Points.Add(new TrajectoryPoint { ut = 50 });
            parent2.Points.Add(new TrajectoryPoint { ut = 100 });

            var debris1 = new Recording
            {
                RecordingId = "d1", TreeId = "tree1", VesselPersistentId = 300, IsDebris = true
            };
            debris1.Points.Add(new TrajectoryPoint { ut = 30 });
            debris1.Points.Add(new TrajectoryPoint { ut = 35 });

            var debris2 = new Recording
            {
                RecordingId = "d2", TreeId = "tree1", VesselPersistentId = 400, IsDebris = true
            };
            debris2.Points.Add(new TrajectoryPoint { ut = 70 });
            debris2.Points.Add(new TrajectoryPoint { ut = 75 });

            var recordings = new List<Recording> { parent1, parent2, debris1, debris2 };
            RecordingStore.PopulateLoopSyncParentIndices(recordings);

            Assert.Equal(0, debris1.LoopSyncParentIdx); // parent1 covers UT 30
            Assert.Equal(1, debris2.LoopSyncParentIdx); // parent2 covers UT 70
        }

        #endregion

        #region FindLastInterestingUT

        [Fact]
        public void FindLastInterestingUT_LastNonBoringSectionEnd()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 200, endUT = 800 });

            Assert.Equal(200, RecordingOptimizer.FindLastInterestingUT(rec));
        }

        [Fact]
        public void FindLastInterestingUT_PartEventAfterBoringSectionStart()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 200, endUT = 800 });
            rec.PartEvents.Add(new PartEvent { ut = 205, eventType = PartEventType.ParachuteDeployed });

            Assert.Equal(205, RecordingOptimizer.FindLastInterestingUT(rec));
        }

        [Fact]
        public void FindLastInterestingUT_SegmentEventTakesMaximum()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceMobile, startUT = 100, endUT = 150 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 150, endUT = 800 });
            rec.SegmentEvents.Add(new SegmentEvent { ut = 160 });

            Assert.Equal(160, RecordingOptimizer.FindLastInterestingUT(rec));
        }

        [Fact]
        public void FindLastInterestingUT_FlagEventTakesMaximum()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.ExoBallistic, startUT = 200, endUT = 800 });
            rec.FlagEvents.Add(new FlagEvent { ut = 250 });

            Assert.Equal(250, RecordingOptimizer.FindLastInterestingUT(rec));
        }

        [Fact]
        public void FindLastInterestingUT_AllBoring_ReturnsNaN()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 100, endUT = 800 });

            Assert.True(double.IsNaN(RecordingOptimizer.FindLastInterestingUT(rec)));
        }

        [Fact]
        public void FindLastInterestingUT_NoSections_ReturnsNaN()
        {
            var rec = new Recording();
            Assert.True(double.IsNaN(RecordingOptimizer.FindLastInterestingUT(rec)));
        }

        [Fact]
        public void FindLastInterestingUT_NoBoringTail_ReturnsLastSectionEnd()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceMobile, startUT = 200, endUT = 300 });

            Assert.Equal(300, RecordingOptimizer.FindLastInterestingUT(rec));
        }

        #endregion

        #region IsLeafRecording

        [Fact]
        public void IsLeafRecording_Standalone_ReturnsTrue()
        {
            var rec = new Recording();
            Assert.True(RecordingOptimizer.IsLeafRecording(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void IsLeafRecording_HasChildBranch_ReturnsFalse()
        {
            var rec = new Recording { ChildBranchPointId = "bp1" };
            Assert.False(RecordingOptimizer.IsLeafRecording(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void IsLeafRecording_MidChain_ReturnsFalse()
        {
            var rec1 = new Recording { ChainId = "c1", ChainIndex = 0, ChainBranch = 0 };
            var rec2 = new Recording { ChainId = "c1", ChainIndex = 1, ChainBranch = 0 };
            Assert.False(RecordingOptimizer.IsLeafRecording(rec1, new List<Recording> { rec1, rec2 }));
        }

        [Fact]
        public void IsLeafRecording_LastInChain_ReturnsTrue()
        {
            var rec1 = new Recording { ChainId = "c1", ChainIndex = 0, ChainBranch = 0 };
            var rec2 = new Recording { ChainId = "c1", ChainIndex = 1, ChainBranch = 0 };
            Assert.True(RecordingOptimizer.IsLeafRecording(rec2, new List<Recording> { rec1, rec2 }));
        }

        [Fact]
        public void IsLeafRecording_BranchNonZero_ReturnsTrue()
        {
            // Branch > 0 recordings are ghost-only parallel paths — no successor
            var rec1 = new Recording { ChainId = "c1", ChainIndex = 0, ChainBranch = 0 };
            var rec2 = new Recording { ChainId = "c1", ChainIndex = 1, ChainBranch = 1 };
            // rec1 has a branch-0 successor (rec2 is branch 1, doesn't count)
            // but actually rec2 is branch 1 so it does NOT make rec1 a non-leaf
            // Wait: the check looks for ChainBranch == 0 && higher index — rec2 is branch 1
            Assert.True(RecordingOptimizer.IsLeafRecording(rec1, new List<Recording> { rec1, rec2 }));
        }

        #endregion

        #region TrimBoringTail

        private static Recording MakeRecordingWithBoringTail(
            double startUT, double midUT, double endUT,
            SegmentEnvironment activeEnv, SegmentEnvironment boringEnv,
            int activePointCount = 5, int boringPointCount = 20)
        {
            var rec = new Recording();
            // Active phase points
            for (int i = 0; i < activePointCount; i++)
            {
                double t = startUT + (midUT - startUT) * i / (activePointCount - 1);
                rec.Points.Add(new TrajectoryPoint { ut = t });
            }
            // Boring tail points
            for (int i = 0; i < boringPointCount; i++)
            {
                double t = midUT + (endUT - midUT) * (i + 1) / boringPointCount;
                rec.Points.Add(new TrajectoryPoint { ut = t });
            }
            rec.TrackSections.Add(new TrackSection
                { environment = activeEnv, startUT = startUT, endUT = midUT });
            rec.TrackSections.Add(new TrackSection
                { environment = boringEnv, startUT = midUT, endUT = endUT });
            return rec;
        }

        [Fact]
        public void TrimBoringTail_TrimsLongSurfaceStationaryTail()
        {
            // 30s atmospheric + 600s SurfaceStationary → should trim to ~10s past midUT
            var rec = MakeRecordingWithBoringTail(17000, 17030, 17630,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            // EndUT should be ~10s past last interesting (17030)
            Assert.True(rec.EndUT <= 17030 + RecordingOptimizer.DefaultTailBufferSeconds + 1);
            Assert.True(rec.EndUT >= 17030);
        }

        [Fact]
        public void TrimBoringTail_TrimsExoBallisticTail()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17060, 17660,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.True(rec.EndUT <= 17060 + RecordingOptimizer.DefaultTailBufferSeconds + 1);
        }

        [Fact]
        public void TrimBoringTail_PreservesBufferSeconds()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.SurfaceMobile, SegmentEnvironment.SurfaceStationary,
                activePointCount: 5, boringPointCount: 60);
            var recordings = new List<Recording> { rec };

            RecordingOptimizer.TrimBoringTail(rec, recordings);
            // Last point should be at or just before trimUT = 17050 + 10 = 17060
            Assert.True(rec.EndUT >= 17050);
            Assert.True(rec.EndUT <= 17060);
        }

        [Fact]
        public void TrimBoringTail_NoBoringTail_ReturnsFalse()
        {
            var rec = new Recording();
            for (int i = 0; i < 10; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 17000 + i * 10 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 17000, endUT = 17090 });
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
        }

        [Fact]
        public void TrimBoringTail_ShortRecording_ReturnsFalse()
        {
            // Duration < MinDurationForTrimSeconds → skip
            var rec = MakeRecordingWithBoringTail(17000, 17010, 17025,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary,
                activePointCount: 3, boringPointCount: 3);
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
        }

        [Fact]
        public void TrimBoringTail_Idempotent()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            double endAfterFirst = rec.EndUT;
            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(endAfterFirst, rec.EndUT);
        }

        [Fact]
        public void TrimBoringTail_HasChildBranch_ReturnsFalse()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.ChildBranchPointId = "bp1";
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
        }

        [Fact]
        public void TrimBoringTail_MidChain_ReturnsFalse()
        {
            var rec1 = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec1.ChainId = "c1";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            var rec2 = new Recording { ChainId = "c1", ChainIndex = 1, ChainBranch = 0 };
            rec2.Points.Add(new TrajectoryPoint { ut = 17650 });
            rec2.Points.Add(new TrajectoryPoint { ut = 17700 });
            var recordings = new List<Recording> { rec1, rec2 };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec1, recordings));
        }

        [Fact]
        public void TrimBoringTail_TrimsOrbitSegments()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 17050, endUT = 17650 });
            var recordings = new List<Recording> { rec };

            RecordingOptimizer.TrimBoringTail(rec, recordings);
            Assert.Single(rec.OrbitSegments);
            Assert.True(rec.OrbitSegments[0].endUT <= 17060 + 1);
        }

        [Fact]
        public void TrimBoringTail_ShortensMidSpanTrackSection()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            var recordings = new List<Recording> { rec };

            RecordingOptimizer.TrimBoringTail(rec, recordings);
            // The boring section should be shortened, not removed (trimUT falls within it)
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.True(rec.TrackSections[1].endUT <= 17060 + 1);
        }

        [Fact]
        public void TrimBoringTail_PreservesEventsBeforeTrimUT()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.PartEvents.Add(new PartEvent { ut = 17020, eventType = PartEventType.Decoupled });
            rec.SegmentEvents.Add(new SegmentEvent { ut = 17025 });
            rec.FlagEvents.Add(new FlagEvent { ut = 17030 });
            var recordings = new List<Recording> { rec };

            RecordingOptimizer.TrimBoringTail(rec, recordings);
            Assert.Single(rec.PartEvents);
            Assert.Single(rec.SegmentEvents);
            Assert.Single(rec.FlagEvents);
        }

        [Fact]
        public void TrimBoringTail_StripsEventsPastNewEndUT()
        {
            // Sparse boring points (every 100s) with an event between them.
            // The event at 17080 defines lastInterestingUT → trimUT=17090,
            // but the last kept point is at 17050 (next point 17150 is past trimUT).
            // So newEndUT=17050 and the event at 17080 > 17050 gets stripped.
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary,
                activePointCount: 5, boringPointCount: 6); // sparse: ~100s spacing
            rec.PartEvents.Add(new PartEvent { ut = 17020, eventType = PartEventType.Decoupled });
            rec.PartEvents.Add(new PartEvent { ut = 17080, eventType = PartEventType.LightOn });
            var recordings = new List<Recording> { rec };

            RecordingOptimizer.TrimBoringTail(rec, recordings);
            // Event at 17020 survives (before newEndUT), event at 17080 stripped
            Assert.Single(rec.PartEvents);
            Assert.Equal(17020, rec.PartEvents[0].ut);
        }

        [Fact]
        public void TrimBoringTail_AllBoringSections_TrimsToMinimalWindow()
        {
            var rec = new Recording();
            for (int i = 0; i < 10; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 17000 + i * 100 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.ExoBallistic, startUT = 17000, endUT = 17900 });
            var recordings = new List<Recording> { rec };

            // All-boring leaf: trims to Points[1].ut + buffer.
            // Points[1] = 17100, buffer = 10, trimUT = 17110 → keeps 2 points (17000, 17100)
            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(2, rec.Points.Count);
            Assert.True(rec.EndUT <= 17100 + RecordingOptimizer.DefaultTailBufferSeconds + 1);
        }

        [Fact]
        public void TrimBoringTail_AllBoringSections_TooFewPoints_ReturnsFalse()
        {
            // Only 2 points — not enough to trim (need >= 3 for trimming to make sense)
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 17000 });
            rec.Points.Add(new TrajectoryPoint { ut = 17900 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.ExoBallistic, startUT = 17000, endUT = 17900 });
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(2, rec.Points.Count);
        }

        [Fact]
        public void TrimBoringTail_InvalidatesCachedStats()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.CachedStats = new RecordingStats();
            rec.CachedStatsPointCount = 25;
            var recordings = new List<Recording> { rec };

            RecordingOptimizer.TrimBoringTail(rec, recordings);
            Assert.Null(rec.CachedStats);
            Assert.Equal(0, rec.CachedStatsPointCount);
        }

        [Fact]
        public void TrimBoringTail_PartEventInBoringTail_ExtendsBuffer()
        {
            // Part event at UT 200 (in boring section) pushes last-interesting past section boundary
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.PartEvents.Add(new PartEvent { ut = 17200, eventType = PartEventType.ParachuteDeployed });
            var recordings = new List<Recording> { rec };

            RecordingOptimizer.TrimBoringTail(rec, recordings);
            // Should trim based on event at 17200, not section end at 17050
            Assert.True(rec.EndUT >= 17200);
            Assert.True(rec.EndUT <= 17210 + 1);
        }

        /// <summary>
        /// Helper: builds a recording mirroring the real Butterfly Rover playtest scenario.
        /// 30s drive at UT 155-185 (15 dense points), then 30 game-minutes of landed idle
        /// (60 sparse points) ending at UT 2070. Two TrackSections (SurfaceMobile then
        /// SurfaceStationary). The specific PartEvent injection differs per test.
        /// </summary>
        private static Recording MakeRoverScenarioRecording()
        {
            var rec = new Recording();
            // Active drive: 15 points from 155 to 183 (2s apart)
            for (int i = 0; i < 15; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 155 + i * 2 });
            // Boring tail: 60 points from 215 to 1985 (30s apart)
            for (int i = 1; i <= 60; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 185 + i * 30 });
            // Final point at UT 2070 (simulates the recording's true end)
            rec.Points.Add(new TrajectoryPoint { ut = 2070 });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile, startUT = 155, endUT = 185,
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary, startUT = 185, endUT = 2070,
            });
            return rec;
        }

        [Fact]
        public void TrimBoringTail_RoverScenario_OnlyStartSeedEvents_TrimsCorrectly()
        {
            // Reproduces the Butterfly Rover bug A scenario: a rover recording that
            // starts with seed events (DeployableExtended for solar panels at recording
            // start), then the rover sits boring for 30+ game-minutes. Without the
            // FlightRecorder/BackgroundRecorder fix that suppresses spurious seed events
            // on chain promotion, a duplicate DeployableExtended event would fire at the
            // end of the boring tail, poisoning FindLastInterestingUT and preventing trim.
            //
            // This test verifies the GOOD state: when all PartEvents live at the start,
            // TrimBoringTail correctly trims the long boring tail. The fix in
            // FlightRecorder.ResetPartEventTrackingState (skipping emitSeedEvents on
            // promotion) and BackgroundRecorder.InitializeLoadedState (skipping emission
            // when the tree recording already has events) ensures this state is the
            // runtime reality for chain-continued recordings.
            var rec = MakeRoverScenarioRecording();
            int originalPointCount = rec.Points.Count;
            double originalEndUT = rec.EndUT;
            // Seed events at start only (DeployableExtended for both solar panels)
            rec.PartEvents.Add(new PartEvent
            {
                ut = 155, eventType = PartEventType.DeployableExtended, partPersistentId = 873017503,
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 155, eventType = PartEventType.DeployableExtended, partPersistentId = 955579157,
            });
            var recordings = new List<Recording> { rec };

            Assert.Equal(2070, originalEndUT);
            Assert.Equal(76, originalPointCount);

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            // Last interesting = 185 (SurfaceMobile endUT), trimUT = 195
            // All points with UT > 195 trimmed → keeps only the 15 active points
            Assert.Equal(15, rec.Points.Count);
            Assert.True(rec.EndUT <= 195, $"Expected EndUT <= 195, got {rec.EndUT}");
            Assert.True(rec.EndUT >= 155, $"Expected EndUT >= 155, got {rec.EndUT}");
        }

        [Fact]
        public void TrimBoringTail_RoverScenario_StaleLateSeedEvent_BlocksTrim()
        {
            // Reproduces the PRE-FIX bug state: same rover scenario but with a bogus
            // DeployableExtended event at UT 2068.78 (near the end), simulating what
            // happened when the backgrounded rover was re-loaded and the seeder
            // re-emitted events at the current UT. This stale event pushes
            // FindLastInterestingUT to 2068, and trimUT = 2078 > EndUT = 2070,
            // so TrimBoringTail returns false and the recording is NOT trimmed.
            //
            // Documents the symptom so regressions are caught: the fix lives in
            // FlightRecorder / BackgroundRecorder, not TrimBoringTail itself. If this
            // test ever starts returning true (trim succeeds), something changed in
            // TrimBoringTail that may mask the seed-event fix in the recorders.
            var rec = MakeRoverScenarioRecording();
            rec.PartEvents.Add(new PartEvent
            {
                ut = 155, eventType = PartEventType.DeployableExtended, partPersistentId = 873017503,
            });
            // Bug A symptom: spurious duplicate at UT 2068.78 from chain-promotion re-seed
            rec.PartEvents.Add(new PartEvent
            {
                ut = 2068.78, eventType = PartEventType.DeployableExtended, partPersistentId = 955579157,
            });
            var recordings = new List<Recording> { rec };

            // Documents pre-fix behavior: trim SKIPPED because trimUT=2078.78 > EndUT=2070
            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(2070, rec.EndUT);
            Assert.Equal(76, rec.Points.Count);
        }

        [Fact]
        public void TrimBoringTail_MultipleTrailingBoringSections()
        {
            // Atmospheric 100-200, SurfaceStationary 200-400, ExoBallistic 400-800
            var rec = new Recording();
            for (int i = 0; i < 5; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 17000 + i * 50 }); // 17000-17200
            for (int i = 1; i <= 30; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 17200 + i * 20 }); // 17220-17800
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 17000, endUT = 17200 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 17200, endUT = 17400 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.ExoBallistic, startUT = 17400, endUT = 17800 });
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            // Last interesting = 17200 (Atmospheric endUT), trim at 17210
            Assert.True(rec.EndUT <= 17210 + 1);
            // ExoBallistic section entirely removed, SurfaceStationary shortened
            Assert.Equal(2, rec.TrackSections.Count);
        }

        [Fact]
        public void TrimBoringTail_CustomBufferSeconds()
        {
            var rec = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary,
                activePointCount: 5, boringPointCount: 60);
            var recordings = new List<Recording> { rec };

            RecordingOptimizer.TrimBoringTail(rec, recordings, bufferSeconds: 5.0);
            // Buffer is 5s instead of 10s, so trim at 17055
            Assert.True(rec.EndUT >= 17050);
            Assert.True(rec.EndUT <= 17055 + 1);
        }

        [Fact]
        public void TrimBoringTail_AllPointsPastTrimUT_ReturnsFalse()
        {
            // Construct a recording where the non-boring section has no points
            // but the boring section has all the points — trimUT falls before first point
            var rec = new Recording();
            // All points are in the boring tail, well past the section boundary
            for (int i = 0; i < 10; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 17500 + i * 50 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 17000, endUT = 17050 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 17050, endUT = 17950 });
            var recordings = new List<Recording> { rec };

            // lastInteresting=17050, trimUT=17060, but all points are at 17500+
            // keepCount would be 0 (< 2), so returns false
            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(10, rec.Points.Count); // untouched
        }

        #endregion

        #region RunOptimizationPass -- tree with branch points

        /// <summary>
        /// Simulates a Mun mission: launch (atmo), orbit+transfer (exo), approach, surface.
        /// The main recording has a staging branch point during exo phase.
        /// After optimizer splits, verifies:
        /// - 4 chain segments (atmo, exo, approach, surface)
        /// - ChildBranchPointId ends up on the exo segment (where the staging happened)
        /// - BranchPoint.ParentRecordingIds correctly references the exo segment
        /// - Chain indices are sequential
        /// - Debris recording is untouched
        /// </summary>
        [Fact]
        public void RunOptimizationPass_TreeWithBranchPoint_SplitsCorrectly()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            // Main recording: Atmo [17000,17100] -> Exo [17100,17500] -> Approach [17500,17600] -> Surface [17600,17700]
            var main = new Recording { RecordingId = "main_rec", TreeId = "tree_mun" };
            main.Points.Add(new TrajectoryPoint { ut = 17000, altitude = 100, bodyName = "Kerbin" });
            main.Points.Add(new TrajectoryPoint { ut = 17050, altitude = 30000, bodyName = "Kerbin" });
            main.Points.Add(new TrajectoryPoint { ut = 17099, altitude = 69000, bodyName = "Kerbin" });
            main.Points.Add(new TrajectoryPoint { ut = 17100, altitude = 71000, bodyName = "Kerbin" });
            main.Points.Add(new TrajectoryPoint { ut = 17200, altitude = 100000, bodyName = "Kerbin" });
            main.Points.Add(new TrajectoryPoint { ut = 17300, altitude = 200000, bodyName = "Mun" });
            main.Points.Add(new TrajectoryPoint { ut = 17400, altitude = 50000, bodyName = "Mun" });
            main.Points.Add(new TrajectoryPoint { ut = 17499, altitude = 31000, bodyName = "Mun" });
            main.Points.Add(new TrajectoryPoint { ut = 17500, altitude = 29000, bodyName = "Mun" });
            main.Points.Add(new TrajectoryPoint { ut = 17550, altitude = 10000, bodyName = "Mun" });
            main.Points.Add(new TrajectoryPoint { ut = 17599, altitude = 100, bodyName = "Mun" });
            main.Points.Add(new TrajectoryPoint { ut = 17600, altitude = 10, bodyName = "Mun" });
            main.Points.Add(new TrajectoryPoint { ut = 17650, altitude = 5, bodyName = "Mun" });
            main.Points.Add(new TrajectoryPoint { ut = 17700, altitude = 5, bodyName = "Mun" });

            main.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric, startUT = 17000, endUT = 17100,
                frames = new List<TrajectoryPoint>()
            });
            main.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic, startUT = 17100, endUT = 17500,
                frames = new List<TrajectoryPoint>()
            });
            main.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Approach, startUT = 17500, endUT = 17600,
                frames = new List<TrajectoryPoint>()
            });
            main.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary, startUT = 17600, endUT = 17700,
                frames = new List<TrajectoryPoint>()
            });

            // Branch point at recording's temporal end (UT=17700), as in real tree mode
            // where ChildBranchPointId is set at branchUT during CreateSplitBranch.
            main.ChildBranchPointId = "bp_staging";

            var bp = new BranchPoint
            {
                Id = "bp_staging",
                UT = 17700, // at recording's end (as in real tree mode)
                Type = BranchPointType.JointBreak,
                ParentRecordingIds = new List<string> { "main_rec" },
                ChildRecordingIds = new List<string> { "main_continuation", "debris_rec" }
            };

            // Debris recording (booster) — single environment, won't be split
            var debris = new Recording
            {
                RecordingId = "debris_rec",
                TreeId = "tree_mun",
                IsDebris = true,
                ParentBranchPointId = "bp_staging",
                VesselName = "Booster"
            };
            debris.Points.Add(new TrajectoryPoint { ut = 17200, altitude = 100000, bodyName = "Kerbin" });
            debris.Points.Add(new TrajectoryPoint { ut = 17300, altitude = 50000, bodyName = "Kerbin" });
            debris.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic, startUT = 17200, endUT = 17300,
                frames = new List<TrajectoryPoint>()
            });

            var tree = new RecordingTree
            {
                Id = "tree_mun",
                TreeName = "Mun Mission",
                RootRecordingId = "main_rec",
                BranchPoints = new List<BranchPoint> { bp },
                Recordings = new System.Collections.Generic.Dictionary<string, Recording>
                {
                    { "main_rec", main },
                    { "debris_rec", debris }
                }
            };
            RecordingStore.CommittedTrees.Add(tree);

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(main);
            recordings.Add(debris);

            RecordingStore.RunOptimizationPass();

            // Should have 5 recordings: 4 from main split + 1 debris
            // (surface leaf may be trimmed to minimal window but still exists)
            Assert.True(recordings.Count >= 4, $"Expected at least 4 recordings, got {recordings.Count}");

            // Find the chain segments (exclude debris)
            var chain = new List<Recording>();
            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].RecordingId != "debris_rec")
                    chain.Add(recordings[i]);
            }
            Assert.Equal(4, chain.Count);

            // Verify chain indices are sequential
            chain.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));
            for (int i = 0; i < chain.Count; i++)
                Assert.Equal(i, chain[i].ChainIndex);

            // All chain segments share the same ChainId
            string chainId = chain[0].ChainId;
            Assert.False(string.IsNullOrEmpty(chainId));
            for (int i = 1; i < chain.Count; i++)
                Assert.Equal(chainId, chain[i].ChainId);

            // Verify segment phases
            Assert.Equal("atmo", chain[0].SegmentPhase);
            Assert.Equal("exo", chain[1].SegmentPhase);
            Assert.Equal("approach", chain[2].SegmentPhase);
            Assert.Equal("surface", chain[3].SegmentPhase);

            // ChildBranchPointId should be on the LAST chain segment (temporal end)
            Assert.Null(chain[0].ChildBranchPointId);
            Assert.Null(chain[1].ChildBranchPointId);
            Assert.Null(chain[2].ChildBranchPointId);
            Assert.Equal("bp_staging", chain[3].ChildBranchPointId);

            // BranchPoint.ParentRecordingIds should reference the last chain segment
            Assert.Single(bp.ParentRecordingIds);
            Assert.Equal(chain[3].RecordingId, bp.ParentRecordingIds[0]);

            // All chain segments have correct TreeId
            for (int i = 0; i < chain.Count; i++)
                Assert.Equal("tree_mun", chain[i].TreeId);

            // Debris recording unchanged
            var debrisResult = recordings.Find(r => r.RecordingId == "debris_rec");
            Assert.NotNull(debrisResult);
            Assert.True(debrisResult.IsDebris);
            Assert.Equal("bp_staging", debrisResult.ParentBranchPointId);

            // Tree dict updated with new recordings
            Assert.True(tree.Recordings.Count >= 5,
                $"Tree should have at least 5 recordings, got {tree.Recordings.Count}");

            RecordingStore.ResetForTesting();
        }

        /// <summary>
        /// Verifies that the all-boring leaf trim works end-to-end through RunOptimizationPass.
        /// After splitting Approach from Surface, the Surface leaf (all SurfaceStationary)
        /// should be trimmed to a minimal window.
        /// </summary>
        [Fact]
        public void RunOptimizationPass_AllBoringLeaf_TrimmedToMinimalWindow()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            // Recording: Approach [17000,17030] -> SurfaceStationary [17030,17200]
            var rec = new Recording { RecordingId = "approach_surface" };
            rec.Points.Add(new TrajectoryPoint { ut = 17000, altitude = 20000, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = 17015, altitude = 10000, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = 17029, altitude = 100, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = 17030, altitude = 5, bodyName = "Mun" });
            // Long boring tail: 170 seconds of sitting on the surface
            for (int i = 1; i <= 17; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 17030 + i * 10, altitude = 5, bodyName = "Mun" });

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Approach, startUT = 17000, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary, startUT = 17030, endUT = 17200,
                frames = new List<TrajectoryPoint>()
            });

            var recordings = RecordingStore.CommittedRecordings;
            recordings.Add(rec);

            RecordingStore.RunOptimizationPass();

            // Should split into 2: Approach + Surface
            Assert.Equal(2, recordings.Count);
            var approach = recordings[0];
            var surface = recordings[1];
            Assert.Equal("approach", approach.SegmentPhase);
            Assert.Equal("surface", surface.SegmentPhase);

            // Surface leaf is all-boring -> trimmed to Points[1].ut + buffer.
            // After split, surface points start at 17030 with 10s spacing,
            // so Points[1]=17040, trimUT=17050. Significantly shorter than original 170s.
            Assert.True(surface.EndUT < 17100,
                $"Surface EndUT {surface.EndUT} should be trimmed well below original 17200");

            RecordingStore.ResetForTesting();
        }

        #endregion
    }
}
