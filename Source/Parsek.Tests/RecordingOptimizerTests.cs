using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
                LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel,
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

        private static TrajectoryPoint PointAt(double ut, double altitude = 0)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                altitude = altitude,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
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
        public void CanAutoMerge_ContinuousEvaAtmoToSurface_ReturnsTrue()
        {
            var a = MakeChainSegment("chain1", 0, phase: "atmo", body: "Kerbin", startUT: 17000, endUT: 17030);
            var b = MakeChainSegment("chain1", 1, phase: "surface", body: "Kerbin", startUT: 17029.5, endUT: 17060);
            a.EvaCrewName = "Bill Kerman";
            b.EvaCrewName = "Bill Kerman";
            a.ParentRecordingId = "parent-1";
            b.ParentRecordingId = "parent-1";
            a.VesselPersistentId = 1001;
            b.VesselPersistentId = 1001;

            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_GappedEvaAtmoToSurface_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0, phase: "atmo", body: "Kerbin", startUT: 17000, endUT: 17030);
            var b = MakeChainSegment("chain1", 1, phase: "surface", body: "Kerbin", startUT: 17033.1, endUT: 17060);
            a.EvaCrewName = "Bill Kerman";
            b.EvaCrewName = "Bill Kerman";
            a.ParentRecordingId = "parent-1";
            b.ParentRecordingId = "parent-1";

            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_NonEvaAtmoToSurface_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0, phase: "atmo", body: "Kerbin", startUT: 17000, endUT: 17030);
            var b = MakeChainSegment("chain1", 1, phase: "surface", body: "Kerbin", startUT: 17029.5, endUT: 17060);

            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_EvaAtmoToSurface_UnknownBody_ReturnsFalse()
        {
            var a = MakeChainSegment("chain1", 0, phase: "atmo", body: "Kerbin", startUT: 17000, endUT: 17030);
            var b = MakeChainSegment("chain1", 1, phase: "surface", body: "Kerbin", startUT: 17029.5, endUT: 17060);
            a.EvaCrewName = "Bill Kerman";
            b.EvaCrewName = "Bill Kerman";
            a.ParentRecordingId = "parent-1";
            b.ParentRecordingId = "parent-1";
            a.SegmentBodyName = null;

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
            b.LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel + 15.0;
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_UntouchedRecordingFieldInit_StaysInLockstepWithSentinel()
        {
            // Regression guard for a prior bug: DefaultLoopIntervalSeconds was bumped
            // 10 -> 30 and RecordingOptimizer.CanAutoMerge used it as the "untouched"
            // sentinel, but Recording.LoopIntervalSeconds field init stayed at 10. Fresh
            // captures and legacy saves then failed the guard (10 != 30) and stopped
            // auto-merging. This test pins the invariant: the field init and the sentinel
            // MUST be equal.
            var fresh = new Recording();
            Assert.Equal(LoopTiming.UntouchedLoopIntervalSentinel, fresh.LoopIntervalSeconds);
        }

        [Fact]
        public void CanAutoMerge_TwoUntouchedRecordings_ReturnsTrue()
        {
            // Same regression: two recordings at the untouched sentinel must be mergeable
            // regardless of the user-facing DefaultLoopIntervalSeconds value.
            var a = MakeChainSegment("chain1", 0);
            var b = MakeChainSegment("chain1", 1);
            Assert.Equal(LoopTiming.UntouchedLoopIntervalSentinel, a.LoopIntervalSeconds);
            Assert.Equal(LoopTiming.UntouchedLoopIntervalSentinel, b.LoopIntervalSeconds);
            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));
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

        [Fact]
        public void FindSplitCandidates_SkipsContinuousEvaAtmosphereSurfaceBoundary()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.EvaCrewName = "Bill Kerman";
            rec.ParentRecordingId = "parent-1";
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

        [Fact]
        public void MergeInto_TransfersBranchAndTerminalMetadata()
        {
            var a = MakeChainSegment("c1", 0);
            var b = MakeChainSegment("c1", 1);
            b.ChildBranchPointId = "bp_001";
            b.TerminalStateValue = TerminalState.Landed;
            b.TerminalOrbitInclination = 28.5;
            b.TerminalOrbitEccentricity = 0.01;
            b.TerminalOrbitSemiMajorAxis = 700000;
            b.TerminalOrbitLAN = 90.0;
            b.TerminalOrbitArgumentOfPeriapsis = 45.0;
            b.TerminalOrbitMeanAnomalyAtEpoch = 1.23;
            b.TerminalOrbitEpoch = 17060;
            b.TerminalOrbitBody = "Kerbin";
            b.TerminalPosition = new SurfacePosition { body = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0 };
            b.TerrainHeightAtEnd = 123.4;
            b.SurfacePos = new SurfacePosition { body = "Kerbin", latitude = 4.0, longitude = 5.0, altitude = 6.0 };

            RecordingOptimizer.MergeInto(a, b);

            Assert.Equal("bp_001", a.ChildBranchPointId);
            Assert.Equal(TerminalState.Landed, a.TerminalStateValue);
            Assert.Equal("Kerbin", a.TerminalOrbitBody);
            Assert.Equal(123.4, a.TerrainHeightAtEnd);
            Assert.True(a.TerminalPosition.HasValue);
            Assert.True(a.SurfacePos.HasValue);
        }

        [Fact]
        public void MergeInto_RefreshesEndpointDecisionFromMergedTerminalState()
        {
            var a = MakeChainSegment("c1", 0);
            var b = MakeChainSegment("c1", 1);
            var firstPoint = a.Points[0];
            firstPoint.bodyName = "Kerbin";
            a.Points[0] = firstPoint;
            var secondPoint = a.Points[1];
            secondPoint.bodyName = "Kerbin";
            a.Points[1] = secondPoint;
            a.EndpointPhase = RecordingEndpointPhase.TrajectoryPoint;
            a.EndpointBodyName = "Kerbin";
            b.TerminalStateValue = TerminalState.Landed;
            b.TerminalPosition = new SurfacePosition
            {
                body = "Mun",
                latitude = 1.0,
                longitude = 2.0,
                altitude = 3.0
            };

            RecordingOptimizer.MergeInto(a, b);

            Assert.Equal(RecordingEndpointPhase.TerminalPosition, a.EndpointPhase);
            Assert.Equal("Mun", a.EndpointBodyName);
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

        [Fact]
        public void SplitAtSection_WithConcreteTrackFrames_RebuildsFlatTrajectoryPerHalf()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 10, latitude = 10, longitude = 10, altitude = 10, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 20, latitude = 20, longitude = 20, altitude = 20, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 30, latitude = 30, longitude = 30, altitude = 30, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 40, latitude = 40, longitude = 40, altitude = 40, bodyName = "Kerbin" });

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 10,
                endUT = 20,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 10, latitude = 1, longitude = 1, altitude = 100, bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero },
                    new TrajectoryPoint { ut = 20, latitude = 2, longitude = 2, altitude = 200, bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero }
                }
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 30,
                endUT = 40,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 30, latitude = 3, longitude = 3, altitude = 300, bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero },
                    new TrajectoryPoint { ut = 40, latitude = 4, longitude = 4, altitude = 400, bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero }
                }
            });

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal(new[] { 10.0, 20.0 }, rec.Points.Select(p => p.ut).ToArray());
            Assert.Equal(new[] { 30.0, 40.0 }, second.Points.Select(p => p.ut).ToArray());
            Assert.Equal(100, rec.Points[0].altitude);
            Assert.Equal(400, second.Points[1].altitude);
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
            RecordingStore.AddRecordingWithTreeForTesting(a);
            RecordingStore.AddRecordingWithTreeForTesting(b);
            RecordingStore.AddRecordingWithTreeForTesting(c);

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
        public void RunOptimizationPass_MergeUpdatesTreeMembershipAndBranchParentLinks()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var first = MakeChainSegment("merge_tree", 0);
            first.RecordingId = "seg0";
            first.TreeId = "tree_merge";

            var second = MakeChainSegment("merge_tree", 1);
            second.RecordingId = "seg1";
            second.TreeId = "tree_merge";
            second.ChildBranchPointId = "bp_merge";
            second.TerminalStateValue = TerminalState.Landed;
            second.TerminalPosition = new SurfacePosition
            {
                body = "Kerbin",
                latitude = 1,
                longitude = 2,
                altitude = 3
            };

            var bp = new BranchPoint
            {
                Id = "bp_merge",
                ParentRecordingIds = new List<string> { "seg1" }
            };

            var tree = new RecordingTree
            {
                Id = "tree_merge",
                RootRecordingId = "seg0",
                ActiveRecordingId = "seg1",
                BranchPoints = new List<BranchPoint> { bp },
                Recordings = new Dictionary<string, Recording>
                {
                    { "seg0", first },
                    { "seg1", second }
                }
            };

            RecordingStore.CommittedTrees.Add(tree);
            RecordingStore.AddRecordingWithTreeForTesting(first);
            RecordingStore.AddRecordingWithTreeForTesting(second);

            RecordingStore.RunOptimizationPass();

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Single(tree.Recordings);
            Assert.Contains("seg0", tree.Recordings.Keys);
            Assert.DoesNotContain("seg1", tree.Recordings.Keys);
            Assert.Equal("seg0", tree.ActiveRecordingId);
            Assert.Equal("bp_merge", first.ChildBranchPointId);
            Assert.Contains("seg0", bp.ParentRecordingIds);
            Assert.DoesNotContain("seg1", bp.ParentRecordingIds);

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
            RecordingStore.AddRecordingWithTreeForTesting(a);
            RecordingStore.AddRecordingWithTreeForTesting(b);
            RecordingStore.AddRecordingWithTreeForTesting(c);

            RecordingStore.RunOptimizationPass();

            // No merges should occur (b blocks both pairs)
            Assert.Equal(3, recordings.Count);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_ContinuousEvaAtmosphereSurfaceRemainsSingleRecording()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.RecordingId = "eva_continuous";
            rec.VesselName = "Bill Kerman";
            rec.VesselPersistentId = 1001;
            rec.EvaCrewName = "Bill Kerman";
            rec.ParentRecordingId = "parent-1";

            var recordings = RecordingStore.CommittedRecordings;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Single(recordings);
            Assert.Equal("eva_continuous", recordings[0].RecordingId);
            Assert.True(string.IsNullOrEmpty(recordings[0].ChainId));

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_EvaBoundarySurfaceBridgeWithLaterSurfaceGapRemainsSingleRecording()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = new Recording
            {
                RecordingId = "eva_surface_bridge",
                VesselName = "Jeb Kerman",
                VesselPersistentId = 1002,
                EvaCrewName = "Jeb Kerman",
                ParentRecordingId = "parent-1",
                StartBodyName = "Kerbin"
            };

            var atmoFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 17000, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 17010, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 17019.5, bodyName = "Kerbin" },
            };
            var bridgeFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 17020, bodyName = "Kerbin" },
            };
            var laterSurfaceFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 17080, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 17100, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 17120, bodyName = "Kerbin" },
            };

            rec.Points.AddRange(atmoFrames);
            rec.Points.AddRange(bridgeFrames);
            rec.Points.AddRange(laterSurfaceFrames);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000,
                endUT = 17020,
                frames = new List<TrajectoryPoint>(atmoFrames)
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17020,
                endUT = 17020,
                frames = new List<TrajectoryPoint>(bridgeFrames)
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17080,
                endUT = 17120,
                frames = new List<TrajectoryPoint>(laterSurfaceFrames)
            });

            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(new List<Recording> { rec }));

            var recordings = RecordingStore.CommittedRecordings;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Single(recordings);
            Assert.Equal("eva_surface_bridge", recordings[0].RecordingId);
            Assert.True(recordings[0].TrackSections.Count >= 2);
            Assert.Equal(SegmentEnvironment.Atmospheric, recordings[0].TrackSections[0].environment);
            Assert.Equal(SegmentEnvironment.SurfaceStationary, recordings[0].TrackSections[1].environment);
            Assert.Equal(17020, recordings[0].TrackSections[1].startUT);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_HealsSplitEvaAtmosphereSurfacePairWithoutNonMonotonicPoints()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var first = MakeChainSegment("eva_chain", 0, phase: "atmo", body: "Kerbin", startUT: 17000, endUT: 17020);
            first.RecordingId = "eva_first";
            first.VesselName = "Bill Kerman";
            first.VesselPersistentId = 1001;
            first.EvaCrewName = "Bill Kerman";
            first.ParentRecordingId = "parent-1";
            first.StartBodyName = "Kerbin";
            first.Points.Clear();
            first.TrackSections.Clear();
            var firstFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 17000, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 17010, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 17020, bodyName = "Kerbin" },
            };
            first.Points.AddRange(firstFrames);
            first.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000,
                endUT = 17020,
                frames = new List<TrajectoryPoint>(firstFrames)
            });

            var second = MakeChainSegment("eva_chain", 1, phase: "surface", body: "Kerbin", startUT: 17019.5, endUT: 17040);
            second.RecordingId = "eva_second";
            second.VesselName = "Bill Kerman";
            second.VesselPersistentId = 1001;
            second.EvaCrewName = "Bill Kerman";
            second.ParentRecordingId = "parent-1";
            second.StartBodyName = "Kerbin";
            second.Points.Clear();
            second.TrackSections.Clear();
            var secondFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 17019.5, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 17021, bodyName = "Kerbin" },
                new TrajectoryPoint { ut = 17040, bodyName = "Kerbin" },
            };
            second.Points.AddRange(secondFrames);
            second.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17019.5,
                endUT = 17040,
                frames = new List<TrajectoryPoint>(secondFrames)
            });

            var recordings = RecordingStore.CommittedRecordings;
            RecordingStore.AddRecordingWithTreeForTesting(first);
            RecordingStore.AddRecordingWithTreeForTesting(second);

            RecordingStore.RunOptimizationPass();

            Assert.Single(recordings);
            Assert.Equal(0, recordings[0].ChainIndex);
            Assert.Equal("Bill Kerman", recordings[0].EvaCrewName);
            Assert.Equal(new[] { 17000.0, 17010.0, 17020.0, 17021.0, 17040.0 },
                recordings[0].Points.Select(p => p.ut).ToArray());
            for (int i = 1; i < recordings[0].Points.Count; i++)
                Assert.True(recordings[0].Points[i - 1].ut <= recordings[0].Points[i].ut);

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
            RecordingStore.AddRecordingWithTreeForTesting(a1);
            RecordingStore.AddRecordingWithTreeForTesting(a2);
            RecordingStore.AddRecordingWithTreeForTesting(b1);
            RecordingStore.AddRecordingWithTreeForTesting(b2);

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
        public void CanAutoSplitIgnoringGhostTriggers_AllowsTreeRecordings()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.TreeId = "tree-001";

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
        public void FindSplitCandidatesForOptimizer_FindsTreeRecordings()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.TreeId = "tree-001";
            var committed = new List<Recording> { rec };

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

        [Fact]
        public void FindSplitCandidatesForOptimizer_SkipsContinuousEvaAtmosphereSurfaceBoundary()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.EvaCrewName = "Bill Kerman";
            rec.ParentRecordingId = "parent-1";
            var committed = new List<Recording> { rec };

            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(committed));
        }

        [Fact]
        public void FindSplitCandidatesForOptimizer_NonEvaAtmosphereSurfaceStillSplits()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            var committed = new List<Recording> { rec };

            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(committed);
            Assert.Single(candidates);
            Assert.Equal((0, 1), candidates[0]);
        }

        [Fact]
        public void FindSplitCandidatesForOptimizer_SkipsContinuousEvaSurfaceAtmoSurface()
        {
            var rec = MakeRecordingWith3Sections(
                17000, 17020, 17040, 17060,
                SegmentEnvironment.SurfaceMobile, SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary,
                body1: "Kerbin", body2: "Kerbin", body3: "Kerbin");
            rec.EvaCrewName = "Bill Kerman";
            rec.ParentRecordingId = "parent-1";
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
        public void SplitAtSection_RecomputesEndpointDecisionForBothHalves()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric,
                body1: "Kerbin", body2: "Mun");
            // Seed stale endpoint metadata that matches neither split half's final endpoint.
            // Both halves now recompute their endpoint decision from the split payload, so
            // the assertions below would fail if the split-time refresh were removed.
            rec.EndpointPhase = RecordingEndpointPhase.OrbitSegment;
            rec.EndpointBodyName = "Minmus";

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal(RecordingEndpointPhase.TrajectoryPoint, rec.EndpointPhase);
            Assert.Equal("Kerbin", rec.EndpointBodyName);
            Assert.Equal(RecordingEndpointPhase.TrajectoryPoint, second.EndpointPhase);
            Assert.Equal("Mun", second.EndpointBodyName);
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

        [Fact]
        public void SplitAtSection_CopiesGeneration()
        {
            // Both halves are the same logical vessel and must share Generation
            // so the cascade-depth cap (#284) treats them identically.
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.ExoBallistic, SegmentEnvironment.Atmospheric);
            rec.Generation = 1;

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal(1, rec.Generation);
            Assert.Equal(1, second.Generation);
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.NotEmpty(recordings[0].ChainId);
            Assert.Equal(recordings[0].ChainId, recordings[1].ChainId);
            Assert.Equal(0, recordings[0].ChainIndex);
            Assert.Equal(1, recordings[1].ChainIndex);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitsTreeRecordingsAtEnvBoundary()
        {
            // T56: tree recordings are now split at environment boundaries.
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            // Tree recording should be split into 2 chain segments
            Assert.Equal(2, recordings.Count);
            Assert.Equal("rec_in_tree", recordings[0].RecordingId);
            Assert.Equal("tree_001", recordings[0].TreeId);
            Assert.Equal("tree_001", recordings[1].TreeId);
            Assert.Equal(2, tree.Recordings.Count);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.Equal("Kerbin", recordings[0].SegmentBodyName);
            Assert.Equal("Mun", recordings[1].SegmentBodyName);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitsTreeRecordingAndUpdatesBranchPoint()
        {
            // T56: tree recordings are now split at environment boundaries.
            // The ChildBranchPointId moves to the second half, and the
            // BranchPoint.ParentRecordingIds is updated to reference it.
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            // Tree recording should be split into 2
            Assert.Equal(2, recordings.Count);
            Assert.Equal(2, tree.Recordings.Count);

            // First half keeps original ID, no ChildBranchPointId
            Assert.Equal("parent_rec", recordings[0].RecordingId);
            Assert.Null(recordings[0].ChildBranchPointId);

            // Second half gets ChildBranchPointId
            var secondHalf = recordings[1];
            Assert.Equal("bp_001", secondHalf.ChildBranchPointId);
            Assert.Equal("tree_bp", secondHalf.TreeId);

            // BP.ParentRecordingIds updated to reference second half
            Assert.Contains(secondHalf.RecordingId, bp.ParentRecordingIds);
            Assert.DoesNotContain("parent_rec", bp.ParentRecordingIds);

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RunOptimizationPass_SplitKeepsBranchPointOnFirstHalfWhenBranchUTPrecedesSplit()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            var rec = MakeRecordingWithSections(100, 170, 240,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic);
            rec.RecordingId = "upper_parent";
            rec.TreeId = "tree_bp_early";
            rec.ChildBranchPointId = "bp_early";

            var bp = new BranchPoint
            {
                Id = "bp_early",
                UT = 116.711,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "upper_parent" }
            };
            var tree = new RecordingTree
            {
                Id = "tree_bp_early",
                BranchPoints = new List<BranchPoint> { bp },
                Recordings = new System.Collections.Generic.Dictionary<string, Recording>
                {
                    { "upper_parent", rec }
                }
            };
            RecordingStore.CommittedTrees.Add(tree);

            var recordings = RecordingStore.CommittedRecordings;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, recordings.Count);
            Assert.Equal("bp_early", recordings[0].ChildBranchPointId);
            Assert.Null(recordings[1].ChildBranchPointId);
            Assert.Contains("upper_parent", bp.ParentRecordingIds);
            Assert.DoesNotContain(recordings[1].RecordingId, bp.ParentRecordingIds);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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

        [Fact]
        public void Issue316_PopulateLoopSync_LateDebrisAfterWatchedSegment_GetsMinusOne()
        {
            var chain0 = new Recording
            {
                RecordingId = "8582d3de9ee74681856352edc49563c3",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 0
            };
            chain0.Points.Add(new TrajectoryPoint { ut = 60.1 });
            chain0.Points.Add(new TrajectoryPoint { ut = 193.6 });

            var chain1 = new Recording
            {
                RecordingId = "06efb0cf37ac493ca3e3fa72c8c2d0d0",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 1
            };
            chain1.Points.Add(new TrajectoryPoint { ut = 193.6 });
            chain1.Points.Add(new TrajectoryPoint { ut = 784.2 });

            var watched = new Recording
            {
                RecordingId = "707490bbcabe495895eecadabed34c2b",
                TreeId = "tree-316",
                VesselPersistentId = 100,
                ChainId = "chain-316",
                ChainIndex = 2
            };
            watched.Points.Add(new TrajectoryPoint { ut = 784.2 });
            watched.Points.Add(new TrajectoryPoint { ut = 817.5 });

            var debrisDuringHold = new Recording
            {
                RecordingId = "aea21e7f06914584be43bebc00ce52f2",
                TreeId = "tree-316",
                VesselPersistentId = 200,
                IsDebris = true
            };
            debrisDuringHold.Points.Add(new TrajectoryPoint { ut = 817.66795427392969 });
            debrisDuringHold.Points.Add(new TrajectoryPoint { ut = 850.44795427389988 });

            var debrisAfterHold = new Recording
            {
                RecordingId = "62e4c90147454cc8aec091d2950e6056",
                TreeId = "tree-316",
                VesselPersistentId = 201,
                IsDebris = true
            };
            debrisAfterHold.Points.Add(new TrajectoryPoint { ut = 921.89877580711743 });
            debrisAfterHold.Points.Add(new TrajectoryPoint { ut = 932.71452898094788 });

            var recordings = new List<Recording>
            {
                chain0,
                chain1,
                watched,
                debrisDuringHold,
                debrisAfterHold
            };

            RecordingStore.PopulateLoopSyncParentIndices(recordings);

            Assert.Equal(-1, debrisDuringHold.LoopSyncParentIdx);
            Assert.Equal(-1, debrisAfterHold.LoopSyncParentIdx);
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
        public void FindLastInterestingUT_ZeroThrottleEngineSeedInBoringTail_Ignored()
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.ExoBallistic, startUT = 200, endUT = 800 });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 790,
                eventType = PartEventType.EngineIgnited,
                value = 0f,
                partPersistentId = 2485666303,
                partName = "liquidEngineMainsail.v2",
                moduleIndex = 0
            });

            Assert.Equal(200, RecordingOptimizer.FindLastInterestingUT(rec));
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
        public void TrimBoringTail_ClampsExplicitEndUTToTrimmedBounds()
        {
            // Regression: commit sets rec.ExplicitEndUT to the scene-exit UT
            // (e.g. 17630 for a full 630-second recording), but the trim only
            // physically removes Points and TrackSections past trimUT. Without
            // clearing ExplicitEndUT, Recording.EndUT keeps returning the stale
            // finalize-time value because the getter prefers ExplicitEndUT over
            // the actual trajectory bounds, so the player sees the full original
            // duration in the Recordings table and the ghost plays past the end
            // of the trimmed trajectory. Post-trim, ExplicitEndUT must be NaN or
            // clamped to the new authoritative bounds.
            var rec = MakeRecordingWithBoringTail(17000, 17030, 17630,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.ExplicitEndUT = 17630; // simulate scene-exit-time finalize value
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.True(double.IsNaN(rec.ExplicitEndUT),
                "ExplicitEndUT must be cleared after trim so Recording.EndUT reflects the trimmed trajectory.");
            // EndUT should now be close to trimUT (17030 + 10 = 17040), not 17630.
            Assert.True(rec.EndUT <= 17030 + RecordingOptimizer.DefaultTailBufferSeconds + 1,
                $"Post-trim EndUT should be trimmed, got {rec.EndUT}");
            Assert.True(rec.EndUT < 17630,
                "Post-trim EndUT must not fall back to the stale pre-trim ExplicitEndUT.");
        }

        [Fact]
        public void TrimBoringTail_LandedStableTerminalState_StillTrims()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            var rec = new Recording
            {
                RecordingId = "trim-landed-identity",
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 1.0,
                    longitude = 2.0,
                    altitude = 3.0,
                    rotation = Quaternion.identity,
                    situation = SurfaceSituation.Landed
                }
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin", latitude = 0.1, longitude = 0.2, altitude = 100, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin", latitude = 0.3, longitude = 0.4, altitude = 60, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 130, bodyName = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 140, bodyName = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 150, bodyName = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0, rotation = Quaternion.identity });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 120 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 120, endUT = 150 });

            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(130, rec.EndUT);
            Assert.Contains(logLines, l =>
                l.Contains("TryGetTerminalSurfaceReference")
                && l.Contains("trim-landed-identity")
                && l.Contains("ignoring identity terminal rotation"));
        }

        [Fact]
        public void TryGetTerminalSurfaceReference_LastPointIdentityRotation_DoesNotMarkRotationRecorded()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                bodyName = "Kerbin",
                latitude = 1.0,
                longitude = 2.0,
                altitude = 3.0,
                rotation = Quaternion.identity
            });

            MethodInfo method = typeof(RecordingOptimizer).GetMethod(
                "TryGetTerminalSurfaceReference",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            object[] args =
            {
                rec,
                null,
                0.0,
                0.0,
                0.0,
                Quaternion.identity,
                false
            };

            bool found = (bool)method.Invoke(null, args);

            Assert.True(found);
            Assert.False((bool)args[6]);
        }

        [Fact]
        public void TrimBoringTail_LandedIdleTailWithFloatJitter_StillTrims()
        {
            // Regression: TailMatchesTerminalSurfaceState used to compare
            // lat/lon/alt/rotation with exact equality. A landed rover at rest
            // accumulates tiny float/double drift every physics frame, so the
            // tail never matched the captured terminal pose byte-for-byte and
            // the trim was silently skipped on every real recording. After the
            // fix, the comparison uses epsilon tolerances sized to absorb
            // physics jitter while still rejecting real movement.
            // Quaternion.Euler is a Unity engine call and cannot be invoked in
            // headless xUnit; use raw quaternions instead. A ~0.05° rotation
            // change is represented by tiny jitter in the y component.
            var baseRot = new Quaternion(0f, 0.1045285f, 0f, 0.9945219f); // ~12° yaw
            var jitterRotA = new Quaternion(0f, 0.1046f, 0f, 0.9945f);    // ~12.01°
            var jitterRotB = new Quaternion(0f, 0.1044f, 0f, 0.9945f);    // ~11.98°

            var rec = new Recording
            {
                RecordingId = "trim-landed-jitter",
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 1.0,
                    longitude = 2.0,
                    altitude = 3.0,
                    rotation = baseRot,
                    rotationRecorded = true,
                    situation = SurfaceSituation.Landed
                }
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin", latitude = 0.1, longitude = 0.2, altitude = 100, rotation = baseRot });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin", latitude = 0.3, longitude = 0.4, altitude = 60, rotation = baseRot });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0, rotation = baseRot });
            rec.Points.Add(new TrajectoryPoint { ut = 130, bodyName = "Kerbin", latitude = 1.0 + 1e-10, longitude = 2.0 - 1e-10, altitude = 3.0 + 0.02, rotation = jitterRotA });
            rec.Points.Add(new TrajectoryPoint { ut = 140, bodyName = "Kerbin", latitude = 1.0 - 2e-10, longitude = 2.0 + 3e-10, altitude = 3.0 - 0.03, rotation = jitterRotB });
            rec.Points.Add(new TrajectoryPoint { ut = 150, bodyName = "Kerbin", latitude = 1.0 + 5e-10, longitude = 2.0 - 1e-10, altitude = 3.0 + 0.01, rotation = jitterRotA });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 120 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 120, endUT = 150 });

            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings),
                "Sub-meter / sub-degree jitter in the idle tail must not block the trim.");
            Assert.True(rec.EndUT <= 120 + RecordingOptimizer.DefaultTailBufferSeconds + 1);
            Assert.True(rec.EndUT >= 120);
        }

        [Fact]
        public void TrimBoringTail_LandedTerminalStateChangesLater_DoesNotTrim()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 1.0,
                    longitude = 2.0,
                    altitude = 3.0,
                    rotation = Quaternion.identity,
                    situation = SurfaceSituation.Landed
                }
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin", latitude = 0.1, longitude = 0.2, altitude = 100, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 110, bodyName = "Kerbin", latitude = 0.3, longitude = 0.4, altitude = 60, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 120, bodyName = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 130, bodyName = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0, rotation = Quaternion.identity });
            // Mid-tail jump representing real vessel movement (≈55 m). The tail match
            // tolerances are sized to absorb physics jitter but reject movement this
            // large — a vessel that drove even briefly would exceed this threshold.
            rec.Points.Add(new TrajectoryPoint { ut = 140, bodyName = "Kerbin", latitude = 1.0005, longitude = 2.0, altitude = 3.0, rotation = Quaternion.identity });
            rec.Points.Add(new TrajectoryPoint { ut = 150, bodyName = "Kerbin", latitude = 1.0, longitude = 2.0, altitude = 3.0, rotation = Quaternion.identity });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 120 });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = 120, endUT = 150 });

            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(150, rec.EndUT);
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
        public void TrimBoringTail_TrimsSectionFramesAndFlatSyncDoesNotRegrowTail()
        {
            var rec = new Recording
            {
                VesselName = "TrimFrames",
                RecordingId = "trim_frames"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 17000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17020, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17040, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17050, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17150, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17250, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17350, bodyName = "Kerbin" });

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000,
                endUT = 17050,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 17000, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17020, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17040, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17050, bodyName = "Kerbin" }
                }
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17050,
                endUT = 17350,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 17050, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17150, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17250, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17350, bodyName = "Kerbin" }
                }
            });

            var recordings = new List<Recording> { rec };
            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(17050, rec.EndUT);
            Assert.Single(rec.TrackSections[1].frames);
            Assert.Equal(17050, rec.TrackSections[1].frames[0].ut);

            Assert.True(RecordingStore.TrySyncFlatTrajectoryFromTrackSections(rec));
            Assert.Equal(17050, rec.EndUT);
            Assert.Equal(17050, rec.Points[rec.Points.Count - 1].ut);
        }

        [Fact]
        public void TrimBoringTail_TrimsCheckpointSectionsAndFlatSyncDoesNotRegrowTail()
        {
            var rec = new Recording
            {
                VesselName = "TrimCheckpoints",
                RecordingId = "trim_checkpoints"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 17000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17020, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17040, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17050, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17150, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17250, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17350, bodyName = "Kerbin" });

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000,
                endUT = 17050,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 17000, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17020, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17040, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 17050, bodyName = "Kerbin" }
                }
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 17050,
                endUT = 17350,
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 17050, endUT = 17150, bodyName = "Kerbin" },
                    new OrbitSegment { startUT = 17150, endUT = 17250, bodyName = "Kerbin" },
                    new OrbitSegment { startUT = 17250, endUT = 17350, bodyName = "Kerbin" }
                }
            });
            rec.OrbitSegments.AddRange(rec.TrackSections[1].checkpoints);

            var recordings = new List<Recording> { rec };
            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(17060, rec.EndUT);
            Assert.Single(rec.TrackSections[1].checkpoints);
            Assert.Equal(17060, rec.TrackSections[1].checkpoints[0].endUT);

            Assert.True(RecordingStore.TrySyncFlatTrajectoryFromTrackSections(rec));
            Assert.Equal(17060, rec.EndUT);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(17060, rec.OrbitSegments[0].endUT);
        }

        [Fact]
        public void SplitAtSection_RelativeSectionsUseFlatSync()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 150, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 200, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 250, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 300, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100,
                endUT = 200,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 125, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 150, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" }
                }
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 200,
                endUT = 300,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 200, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 240, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 300, bodyName = "Kerbin" }
                }
            });

            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal(4, rec.Points.Count);
            Assert.Equal(100, rec.Points[0].ut);
            Assert.Equal(200, rec.Points[rec.Points.Count - 1].ut);
            Assert.Equal(3, second.Points.Count);
            Assert.Equal(200, second.Points[0].ut);
            Assert.Equal(300, second.Points[second.Points.Count - 1].ut);
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

        [Fact]
        public void TrimBoringTail_StableOrbitLateZeroThrottleEngineSeed_StillTrims()
        {
            var rec = MakeRecordingWithBoringTail(2000, 2042.84, 5000,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 6, boringPointCount: 40);
            rec.PartEvents.Add(new PartEvent
            {
                ut = 2042.84,
                eventType = PartEventType.EngineThrottle,
                value = 0f,
                partPersistentId = 2485666303,
                partName = "liquidEngineMainsail.v2",
                moduleIndex = 0
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 2042.84,
                eventType = PartEventType.EngineShutdown,
                value = 0f,
                partPersistentId = 2485666303,
                partName = "unknown",
                moduleIndex = 0
            });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 4995,
                eventType = PartEventType.EngineIgnited,
                value = 0f,
                partPersistentId = 2485666303,
                partName = "liquidEngineMainsail.v2",
                moduleIndex = 0
            });
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.True(rec.EndUT >= 2042.84);
            Assert.True(rec.EndUT <= 2042.84 + RecordingOptimizer.DefaultTailBufferSeconds + 1,
                $"Expected stable orbit to trim near entry, got EndUT={rec.EndUT}");
            Assert.DoesNotContain(rec.PartEvents,
                e => e.eventType == PartEventType.EngineIgnited && e.value <= 0f && e.ut > rec.EndUT);
        }

        [Fact]
        public void TrimBoringTail_StableOrbitingTerminalShape_StillTrims()
        {
            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 1.25;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 800000.0;
            rec.TerminalOrbitLAN = 120.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 45.0;
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 1.25,
                eccentricity = 0.01,
                semiMajorAxis = 800000.0,
                longitudeOfAscendingNode = 120.0,
                argumentOfPeriapsis = 45.0
            });
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.True(rec.EndUT <= 140);
        }

        [Fact]
        public void TrimBoringTail_OrbitChangesAfterTrimPoint_DoesNotTrim()
        {
            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 3.5;
            rec.TerminalOrbitEccentricity = 0.2;
            rec.TerminalOrbitSemiMajorAxis = 1200000.0;
            rec.TerminalOrbitLAN = 200.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 75.0;
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 175,
                bodyName = "Kerbin",
                inclination = 3.5,
                eccentricity = 0.2,
                semiMajorAxis = 1200000.0,
                longitudeOfAscendingNode = 200.0,
                argumentOfPeriapsis = 75.0
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 175,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 3.5,
                eccentricity = 0.2,
                // Delta = 100 km, well above the SMA epsilon (max(10m, 1e-3 * 1.2Mm) = 1200m).
                // A real circularization burn at 1.2 Mm shifts SMA by tens of km, so this
                // is a realistic "tail still maneuvering" scenario the guard must reject.
                semiMajorAxis = 1300000.0,
                longitudeOfAscendingNode = 200.0,
                argumentOfPeriapsis = 75.0
            });
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(220, rec.EndUT);
        }

        [Fact]
        public void TrimBoringTail_SubOrbitalTerminalUsesOrbitGuard()
        {
            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.TerminalStateValue = TerminalState.SubOrbital;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 1.25;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 800000.0;
            rec.TerminalOrbitLAN = 120.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 45.0;
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 1.25,
                // Eccentricity delta = 0.04, above the absolute epsilon (1e-3). A coast
                // segment whose eccentricity drifted by 0.04 is genuinely a different
                // orbit (the recorded terminal SubOrbital is not yet stable); the guard
                // must keep this recording un-trimmed.
                eccentricity = 0.05,
                semiMajorAxis = 800000.0,
                longitudeOfAscendingNode = 120.0,
                argumentOfPeriapsis = 45.0
            });
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(220, rec.EndUT);
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

        // Regression #612: a stable on-rails orbit accumulates jitter from rails /
        // pack-unpack / conic prediction during the boring tail. The previous exact
        // float-equality check on SMA / ecc / inc / LAN / argP rejected the trim on
        // every real recording. With epsilon comparisons the trim now succeeds for
        // jitter inside tolerance and still rejects deltas above tolerance.
        [Fact]
        public void TrimBoringTail_StableOrbitWithRailsJitter_StillTrims()
        {
            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 1.25;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 800000.0;
            rec.TerminalOrbitLAN = 120.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 45.0;
            // Tail OrbitSegment matches the terminal shape with realistic stable-orbit
            // jitter: SMA shifted by a few metres, eccentricity by 5e-4, angles by
            // ~0.005 deg. All within the new epsilons (10 m / 1e-3 / 0.01 deg).
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 1.255,
                eccentricity = 0.0105,
                semiMajorAxis = 800003.5,
                longitudeOfAscendingNode = 120.005,
                argumentOfPeriapsis = 45.005,
            });
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings),
                $"Expected jittered stable orbit to pass epsilon check, got endUT={rec.EndUT}");
            Assert.True(rec.EndUT <= 140);
        }

        // Regression #612 negative case: a real maneuver shifts SMA / ecc / angles
        // by amounts well above the epsilons, and the guard must still reject the trim.
        [Fact]
        public void TrimBoringTail_StableOrbitWithRealManeuver_DoesNotTrim()
        {
            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 1.25;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 800000.0;
            rec.TerminalOrbitLAN = 120.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 45.0;
            // Eccentricity delta = 0.05 (50x epsilon) — represents a real circularization
            // burn the recorder hasn't finalized yet. Trim must be skipped.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 1.25,
                eccentricity = 0.06,
                semiMajorAxis = 800000.0,
                longitudeOfAscendingNode = 120.0,
                argumentOfPeriapsis = 45.0,
            });
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(220, rec.EndUT);
        }

        // Regression #612 follow-up (wraparound). LAN and argP are angular and
        // routinely cross the 0/360 boundary on a stable orbit. Raw Math.Abs(a-b)
        // produced a ~360 deg false mismatch; the OrbitShapeMatchesTerminal helper
        // now uses TrajectoryMath.AngularDeltaDegrees so a tail at LAN=0.002 still
        // matches a terminal at LAN=359.997 (true delta 0.005, well under the
        // 0.01 deg epsilon). Companion test below verifies the helper still rejects
        // a real 1 deg cross-boundary delta.
        [Fact]
        public void TrimBoringTail_LanWrapsAroundZeroBoundary_StillTrims()
        {
            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 1.25;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 800000.0;
            rec.TerminalOrbitLAN = 359.997;
            rec.TerminalOrbitArgumentOfPeriapsis = 45.0;
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 1.25,
                eccentricity = 0.01,
                semiMajorAxis = 800000.0,
                // True wrapped delta = 0.005 deg, below the 0.01 deg angle epsilon.
                // Raw |a-b| = 359.995 would have rejected the trim before the fix.
                longitudeOfAscendingNode = 0.002,
                argumentOfPeriapsis = 45.0,
            });
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings),
                $"Expected wraparound LAN to pass shortest-angle check, got endUT={rec.EndUT}");
            Assert.True(rec.EndUT <= 140);
        }

        [Fact]
        public void TrimBoringTail_ArgPWrapsAroundZeroBoundary_StillTrims()
        {
            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 1.25;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 800000.0;
            rec.TerminalOrbitLAN = 120.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 0.001;
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 1.25,
                eccentricity = 0.01,
                semiMajorAxis = 800000.0,
                longitudeOfAscendingNode = 120.0,
                // True wrapped delta = 0.003 deg, below epsilon. Raw |a-b| = 359.997.
                argumentOfPeriapsis = 359.998,
            });
            var recordings = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, recordings),
                $"Expected wraparound argP to pass shortest-angle check, got endUT={rec.EndUT}");
            Assert.True(rec.EndUT <= 140);
        }

        [Fact]
        public void TrimBoringTail_LanCrossBoundaryRealManeuver_DoesNotTrim()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.RecordingId = "lan-cross-boundary";
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 1.25;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 800000.0;
            rec.TerminalOrbitLAN = 359.5;
            rec.TerminalOrbitArgumentOfPeriapsis = 45.0;
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 1.25,
                eccentricity = 0.01,
                semiMajorAxis = 800000.0,
                // True wrapped delta = 1.0 deg, well above the 0.01 deg epsilon —
                // the guard must reject this even though both ends sit near the
                // 0/360 boundary. Pins the wraparound helper against false-pass.
                longitudeOfAscendingNode = 0.5,
                argumentOfPeriapsis = 45.0,
            });
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Equal(220, rec.EndUT);
            // The divergence log line now reports the wrapped delta, not the raw
            // Abs(a-b) (which would have been 359.0).
            Assert.Contains(logLines, l =>
                l.Contains("[Optimizer]")
                && l.Contains("OrbitShapeMatchesTerminal")
                && l.Contains("LAN wrapped delta")
                && l.Contains("lan-cross-boundary"));
        }

        // Regression #612: TrimBoringTail's entry guards used to silently return
        // false; the visible-from-KSP.log skip-reason logging now records WHICH
        // guard rejected.
        [Fact]
        public void TrimBoringTail_NotLeaf_LogsSkipReason()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Two-segment chain — rec1 is mid-chain (has a successor), so IsLeafRecording
            // returns false and TrimBoringTail must skip with reason "not-leaf".
            var rec1 = MakeRecordingWithBoringTail(17000, 17050, 17650,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec1.RecordingId = "trim-not-leaf-rec1";
            rec1.ChainId = "chain-trim-test";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            var rec2 = new Recording
            {
                RecordingId = "trim-not-leaf-rec2",
                ChainId = "chain-trim-test",
                ChainIndex = 1,
                ChainBranch = 0,
            };
            rec2.Points.Add(new TrajectoryPoint { ut = 17650 });
            rec2.Points.Add(new TrajectoryPoint { ut = 17700 });
            var recordings = new List<Recording> { rec1, rec2 };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec1, recordings));
            Assert.Contains(logLines, l =>
                l.Contains("[Optimizer]")
                && l.Contains("TrimBoringTail: skipped (not-leaf)")
                && l.Contains("trim-not-leaf-rec1"));
        }

        [Fact]
        public void TrimBoringTail_TooShort_LogsSkipReason()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // 25s recording — under the 30s minimum, must skip with "too-short".
            var rec = MakeRecordingWithBoringTail(17000, 17010, 17025,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.SurfaceStationary);
            rec.RecordingId = "trim-too-short";
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            Assert.Contains(logLines, l =>
                l.Contains("[Optimizer]")
                && l.Contains("TrimBoringTail: skipped (too-short)")
                && l.Contains("trim-too-short"));
        }

        [Fact]
        public void TrimBoringTail_TerminalMismatch_LogsSkipReasonWithDelta()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            var rec = MakeRecordingWithBoringTail(100, 130, 220,
                SegmentEnvironment.Atmospheric, SegmentEnvironment.ExoBallistic,
                activePointCount: 4, boringPointCount: 6);
            rec.RecordingId = "trim-terminal-mismatch";
            rec.VesselName = "TerminalMismatchProbe";
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitInclination = 1.25;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitSemiMajorAxis = 800000.0;
            rec.TerminalOrbitLAN = 120.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 45.0;
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130,
                endUT = 220,
                bodyName = "Kerbin",
                inclination = 1.25,
                eccentricity = 0.06, // delta 0.05, well above 1e-3 epsilon
                semiMajorAxis = 800000.0,
                longitudeOfAscendingNode = 120.0,
                argumentOfPeriapsis = 45.0,
            });
            var recordings = new List<Recording> { rec };

            Assert.False(RecordingOptimizer.TrimBoringTail(rec, recordings));
            // The TrimBoringTail "terminal-mismatch" skip reason fires AND the
            // OrbitShapeMatchesTerminal helper records which field diverged.
            Assert.Contains(logLines, l =>
                l.Contains("[Optimizer]")
                && l.Contains("TrimBoringTail: skipped (terminal-mismatch)")
                && l.Contains("trim-terminal-mismatch"));
            Assert.Contains(logLines, l =>
                l.Contains("[Optimizer]")
                && l.Contains("OrbitShapeMatchesTerminal")
                && l.Contains("ecc delta"));
        }

        #endregion

        #region RunOptimizationPass -- tree with branch points

        /// <summary>
        /// Simulates a Mun mission: launch (atmo), orbit+transfer (exo), approach, surface.
        /// The main recording has a staging branch point at the end.
        /// After optimizer splits, verifies:
        /// - 4 chain segments from main (atmo, exo, approach, surface) + 1 debris = 5 total
        /// - ChildBranchPointId ends up on the last chain segment (surface)
        /// - BranchPoint.ParentRecordingIds correctly references the last segment
        /// - Chain indices are sequential
        /// - All chain segments are in the tree's Recordings dict
        /// - Debris recording is untouched
        /// </summary>
        [Fact]
        public void RunOptimizationPass_TreeMultiSection_SplitsIntoChainSegments()
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

            main.ChildBranchPointId = "bp_staging";

            var bp = new BranchPoint
            {
                Id = "bp_staging",
                UT = 17700,
                Type = BranchPointType.JointBreak,
                ParentRecordingIds = new List<string> { "main_rec" },
                ChildRecordingIds = new List<string> { "main_continuation", "debris_rec" }
            };

            // Debris recording (booster) -- single environment, won't be split
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
            RecordingStore.AddRecordingWithTreeForTesting(main);
            RecordingStore.AddRecordingWithTreeForTesting(debris);

            RecordingStore.RunOptimizationPass();

            // 4 chain segments from main + 1 debris = 5 recordings
            Assert.Equal(5, recordings.Count);
            Assert.Equal(5, tree.Recordings.Count);

            // All chain segments should have TreeId set and share a ChainId
            var chainMembers = recordings.Where(r => r.RecordingId == "main_rec" || r.ChainId == main.ChainId).ToList();
            Assert.Equal(4, chainMembers.Count);
            chainMembers.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));
            for (int i = 0; i < chainMembers.Count; i++)
            {
                Assert.Equal("tree_mun", chainMembers[i].TreeId);
                Assert.Equal(i, chainMembers[i].ChainIndex);
            }

            // ChildBranchPointId should be on the last chain segment only
            for (int i = 0; i < chainMembers.Count - 1; i++)
                Assert.Null(chainMembers[i].ChildBranchPointId);
            Assert.Equal("bp_staging", chainMembers[chainMembers.Count - 1].ChildBranchPointId);

            // BP.ParentRecordingIds should reference the last chain segment
            Assert.Contains(chainMembers[chainMembers.Count - 1].RecordingId, bp.ParentRecordingIds);

            // Debris recording unchanged
            var debrisResult = recordings.FirstOrDefault(r => r.RecordingId == "debris_rec");
            Assert.NotNull(debrisResult);
            Assert.True(debrisResult.IsDebris);
            Assert.Equal("bp_staging", debrisResult.ParentBranchPointId);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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

        #region SplitAtSection — EVA field propagation

        [Fact]
        public void SplitAtSection_PropagatesEvaCrewName()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.SurfaceMobile, SegmentEnvironment.Atmospheric);
            rec.EvaCrewName = "Bill Kerman";
            rec.ParentRecordingId = "parent-abc";
            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Equal("Bill Kerman", rec.EvaCrewName);
            Assert.Equal("Bill Kerman", second.EvaCrewName);
            Assert.Equal("parent-abc", rec.ParentRecordingId);
            Assert.Equal("parent-abc", second.ParentRecordingId);
        }

        [Fact]
        public void SplitAtSection_NullEvaFieldsStayNull()
        {
            var rec = MakeRecordingWithSections(17000, 17030, 17060,
                SegmentEnvironment.SurfaceMobile, SegmentEnvironment.Atmospheric);
            var second = RecordingOptimizer.SplitAtSection(rec, 1);

            Assert.Null(rec.EvaCrewName);
            Assert.Null(second.EvaCrewName);
            Assert.Null(rec.ParentRecordingId);
            Assert.Null(second.ParentRecordingId);
        }

        #endregion

        #region Relative Absolute Shadow Optimizer Trims

        [Fact]
        public void TrimBoringTailPayload_RelativeSection_TrimsAbsoluteShadowFrames()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 10,
                endUT = 40,
                frames = new List<TrajectoryPoint>
                {
                    PointAt(10),
                    PointAt(20),
                    PointAt(30),
                    PointAt(40),
                },
                absoluteFrames = new List<TrajectoryPoint>
                {
                    PointAt(10, 100),
                    PointAt(20, 200),
                    PointAt(30, 300),
                    PointAt(40, 400),
                },
            };
            MethodInfo method = typeof(RecordingOptimizer).GetMethod(
                "TryTrimTrackSectionPayload",
                BindingFlags.NonPublic | BindingFlags.Static);

            object[] args = { section, 25.0 };
            bool trimmed = (bool)method.Invoke(null, args);
            section = (TrackSection)args[0];

            Assert.True(trimmed);
            Assert.Equal(new[] { 10.0, 20.0 }, section.frames.Select(p => p.ut).ToArray());
            Assert.Equal(new[] { 10.0, 20.0 }, section.absoluteFrames.Select(p => p.ut).ToArray());
            Assert.Equal(20, section.endUT);
        }

        [Fact]
        public void TrimOverlappingSectionFrames_RelativeSection_TrimsAbsoluteShadowFrames()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    environment = SegmentEnvironment.Atmospheric,
                    startUT = 10,
                    endUT = 20,
                    frames = new List<TrajectoryPoint>
                    {
                        PointAt(10),
                        PointAt(20),
                    },
                },
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    environment = SegmentEnvironment.ExoBallistic,
                    startUT = 15,
                    endUT = 30,
                    frames = new List<TrajectoryPoint>
                    {
                        PointAt(15),
                        PointAt(21),
                        PointAt(30),
                    },
                    absoluteFrames = new List<TrajectoryPoint>
                    {
                        PointAt(15, 150),
                        PointAt(21, 210),
                        PointAt(30, 300),
                    },
                },
            };
            MethodInfo method = typeof(RecordingOptimizer).GetMethod(
                "TrimOverlappingSectionFrames",
                BindingFlags.NonPublic | BindingFlags.Static);

            method.Invoke(null, new object[] { sections });

            Assert.Equal(new[] { 21.0, 30.0 }, sections[1].frames.Select(p => p.ut).ToArray());
            Assert.Equal(new[] { 21.0, 30.0 }, sections[1].absoluteFrames.Select(p => p.ut).ToArray());
            Assert.Equal(21, sections[1].startUT);
            Assert.Equal(30, sections[1].endUT);
        }

        #endregion
    }
}
