using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for small pure decisions of <see cref="GhostPlaybackLogic"/> that
    /// no test called directly: the committed-id lookup and supersede redirect, the
    /// continued-past-child-branch probe, the surface-stationary check, the engine power sum,
    /// the one-shot duration normalizer and the tree-local vessel membership check.
    /// </summary>
    [Collection("Sequential")]
    public class GhostPlaybackLogicCharacterizationTests : IDisposable
    {
        public GhostPlaybackLogicCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        private static Recording Rec(string id, uint pid = 0, double start = 0.0, double end = 100.0)
        {
            var rec = new Recording { RecordingId = id, VesselName = id, VesselPersistentId = pid };
            rec.Points.Add(new TrajectoryPoint { ut = start, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = end, bodyName = "Kerbin" });
            return rec;
        }

        // ---------- FindRecordingIndexById ---------------------------------------

        [Fact]
        public void FindRecordingIndexById_GuardsAndFirstMatch()
        {
            var list = new List<Recording> { null, Rec("a"), Rec("b"), Rec("b") };
            Assert.Equal(-1, GhostPlaybackLogic.FindRecordingIndexById(null, "a"));
            Assert.Equal(-1, GhostPlaybackLogic.FindRecordingIndexById(list, null));
            Assert.Equal(-1, GhostPlaybackLogic.FindRecordingIndexById(list, ""));
            Assert.Equal(1, GhostPlaybackLogic.FindRecordingIndexById(list, "a"));
            Assert.Equal(2, GhostPlaybackLogic.FindRecordingIndexById(list, "b"));
            Assert.Equal(-1, GhostPlaybackLogic.FindRecordingIndexById(list, "c"));
        }

        // ---------- ResolveSupersedeIndex ----------------------------------------

        private static RecordingSupersedeRelation Supersede(string oldId, string newId) =>
            new RecordingSupersedeRelation { OldRecordingId = oldId, NewRecordingId = newId };

        [Fact]
        public void ResolveSupersedeIndex_FollowsChainToCommittedSuccessor()
        {
            var committed = new List<Recording> { Rec("origin"), Rec("mid"), Rec("tip") };
            var supersedes = new List<RecordingSupersedeRelation>
            {
                Supersede("origin", "mid"),
                Supersede("mid", "tip")
            };
            Assert.Equal(2, GhostPlaybackLogic.ResolveSupersedeIndex(0, committed, supersedes));
            Assert.Equal(2, GhostPlaybackLogic.ResolveSupersedeIndex(1, committed, supersedes));
            Assert.Equal(2, GhostPlaybackLogic.ResolveSupersedeIndex(2, committed, supersedes));
        }

        [Fact]
        public void ResolveSupersedeIndex_PassThroughCases()
        {
            var committed = new List<Recording> { Rec("origin"), null, new Recording { RecordingId = null } };
            var supersedes = new List<RecordingSupersedeRelation> { Supersede("origin", "gone") };

            // Out of range, null list, no relations, null / id-less candidate.
            Assert.Equal(-1, GhostPlaybackLogic.ResolveSupersedeIndex(-1, committed, supersedes));
            Assert.Equal(7, GhostPlaybackLogic.ResolveSupersedeIndex(7, committed, supersedes));
            Assert.Equal(0, GhostPlaybackLogic.ResolveSupersedeIndex(0, null, supersedes));
            Assert.Equal(0, GhostPlaybackLogic.ResolveSupersedeIndex(0, committed, null));
            Assert.Equal(0, GhostPlaybackLogic.ResolveSupersedeIndex(
                0, committed, new List<RecordingSupersedeRelation>()));
            Assert.Equal(1, GhostPlaybackLogic.ResolveSupersedeIndex(1, committed, supersedes));
            Assert.Equal(2, GhostPlaybackLogic.ResolveSupersedeIndex(2, committed, supersedes));
            // The successor is not in the committed list: keep the candidate.
            Assert.Equal(0, GhostPlaybackLogic.ResolveSupersedeIndex(0, committed, supersedes));
        }

        // ---------- TryGetContinuedPastChildBranch -------------------------------

        private static RecordingTree TreeWith(Recording parent, BranchPoint bp, params Recording[] children)
        {
            var tree = new RecordingTree { Id = "tree", TreeName = "tree" };
            tree.AddOrReplaceRecording(parent);
            foreach (var child in children)
                tree.AddOrReplaceRecording(child);
            tree.BranchPoints.Add(bp);
            return tree;
        }

        [Fact]
        public void TryGetContinuedPastChildBranch_BreakupWithForeignChildrenAndLaterEnd_True()
        {
            var parent = Rec("parent", pid: 10, end: 100.0);
            parent.ChildBranchPointId = "bp";
            var debris = Rec("debris", pid: 20);
            var bp = new BranchPoint
            {
                Id = "bp",
                UT = 50.0,
                Type = BranchPointType.Breakup,
                ChildRecordingIds = new List<string> { "debris" }
            };
            var tree = TreeWith(parent, bp, debris);

            Assert.True(GhostPlaybackLogic.TryGetContinuedPastChildBranch(parent, tree, out BranchPoint found));
            Assert.Same(bp, found);
        }

        [Fact]
        public void TryGetContinuedPastChildBranch_ChildSharesPid_False()
        {
            var parent = Rec("parent", pid: 10, end: 100.0);
            parent.ChildBranchPointId = "bp";
            var continuation = Rec("cont", pid: 10);
            var bp = new BranchPoint
            {
                Id = "bp",
                UT = 50.0,
                Type = BranchPointType.JointBreak,
                ChildRecordingIds = new List<string> { "cont" }
            };
            Assert.False(GhostPlaybackLogic.TryGetContinuedPastChildBranch(
                parent, TreeWith(parent, bp, continuation), out BranchPoint found));
            Assert.Null(found);
        }

        [Fact]
        public void TryGetContinuedPastChildBranch_TypeThatCannotLeaveParent_False()
        {
            var parent = Rec("parent", pid: 10, end: 100.0);
            parent.ChildBranchPointId = "bp";
            var bp = new BranchPoint { Id = "bp", UT = 50.0, Type = BranchPointType.Undock };
            Assert.False(GhostPlaybackLogic.TryGetContinuedPastChildBranch(
                parent, TreeWith(parent, bp), out _));
        }

        [Fact]
        public void TryGetContinuedPastChildBranch_EndWithinSlackOfBranch_False()
        {
            // End must be past bp.UT + CoalesceWindow + WatchSplitEndSlackSeconds (1 s).
            var parent = Rec("parent", pid: 10, end: 51.5);
            parent.ChildBranchPointId = "bp";
            var bp = new BranchPoint
            {
                Id = "bp", UT = 50.0, CoalesceWindow = 0.5, Type = BranchPointType.Launch
            };
            Assert.False(GhostPlaybackLogic.TryGetContinuedPastChildBranch(
                parent, TreeWith(parent, bp), out _));

            var later = Rec("later", pid: 10, end: 51.6);
            later.ChildBranchPointId = "bp";
            Assert.True(GhostPlaybackLogic.TryGetContinuedPastChildBranch(
                later, TreeWith(later, bp), out _));
        }

        [Fact]
        public void TryGetContinuedPastChildBranch_Guards_False()
        {
            var parent = Rec("parent", pid: 10);
            var bp = new BranchPoint { Id = "bp", UT = 50.0, Type = BranchPointType.Breakup };
            var tree = TreeWith(parent, bp);

            // No child branch id.
            Assert.False(GhostPlaybackLogic.TryGetContinuedPastChildBranch(parent, tree, out _));
            // Branch id not in the tree.
            parent.ChildBranchPointId = "missing";
            Assert.False(GhostPlaybackLogic.TryGetContinuedPastChildBranch(parent, tree, out _));
            // Zero pid.
            var noPid = Rec("nopid", pid: 0);
            noPid.ChildBranchPointId = "bp";
            Assert.False(GhostPlaybackLogic.TryGetContinuedPastChildBranch(noPid, tree, out _));
            Assert.False(GhostPlaybackLogic.TryGetContinuedPastChildBranch(null, tree, out _));
            Assert.False(GhostPlaybackLogic.TryGetContinuedPastChildBranch(parent, null, out _));
        }

        // ---------- IsSurfaceStationaryAtPlaybackUT ------------------------------

        [Fact]
        public void IsSurfaceStationaryAtPlaybackUT_UsesCoveringSectionEnvironment()
        {
            var rec = Rec("r");
            rec.TrackSections.Add(new TrackSection
            {
                startUT = 0.0, endUT = 50.0, environment = SegmentEnvironment.SurfaceStationary
            });
            rec.TrackSections.Add(new TrackSection
            {
                startUT = 50.0, endUT = 100.0, environment = SegmentEnvironment.Atmospheric
            });
            Assert.True(GhostPlaybackLogic.IsSurfaceStationaryAtPlaybackUT(rec, 10.0));
            Assert.False(GhostPlaybackLogic.IsSurfaceStationaryAtPlaybackUT(rec, 60.0));
            Assert.False(GhostPlaybackLogic.IsSurfaceStationaryAtPlaybackUT(null, 10.0));
        }

        [Fact]
        public void IsSurfaceStationaryAtPlaybackUT_NoSectionFallsBackToSurfaceOnlyShape()
        {
            var surfaceOnly = new Recording
            {
                RecordingId = "s",
                SurfacePos = new SurfacePosition { body = "Kerbin" }
            };
            Assert.True(GhostPlaybackLogic.IsSurfaceStationaryAtPlaybackUT(surfaceOnly, 10.0));

            var withPoints = Rec("p");
            withPoints.SurfacePos = new SurfacePosition { body = "Kerbin" };
            Assert.False(GhostPlaybackLogic.IsSurfaceStationaryAtPlaybackUT(withPoints, 500.0));

            var withOrbit = new Recording
            {
                RecordingId = "o",
                SurfacePos = new SurfacePosition { body = "Kerbin" }
            };
            withOrbit.OrbitSegments.Add(new OrbitSegment { startUT = 0.0, endUT = 10.0, bodyName = "Kerbin" });
            Assert.False(GhostPlaybackLogic.IsSurfaceStationaryAtPlaybackUT(withOrbit, 500.0));

            Assert.False(GhostPlaybackLogic.IsSurfaceStationaryAtPlaybackUT(
                new Recording { RecordingId = "empty" }, 10.0));
        }

        // ---------- SumEnginePower ----------------------------------------------

        [Fact]
        public void SumEnginePower_SumsOnlyPositivePower()
        {
            Assert.Equal(0f, GhostPlaybackLogic.SumEnginePower(null));
            Assert.Equal(0f, GhostPlaybackLogic.SumEnginePower(new GhostPlaybackState()));

            var state = new GhostPlaybackState
            {
                engineInfos = new Dictionary<ulong, EngineGhostInfo>
                {
                    [1] = new EngineGhostInfo { currentPower = 0.5f },
                    [2] = new EngineGhostInfo { currentPower = 0f },
                    [3] = new EngineGhostInfo { currentPower = -2f },
                    [4] = null,
                    [5] = new EngineGhostInfo { currentPower = 0.25f },
                }
            };
            Assert.Equal(0.75f, GhostPlaybackLogic.SumEnginePower(state));
        }

        // ---------- NormalizeOneShotDurationSeconds -----------------------------

        [Fact]
        public void NormalizeOneShotDurationSeconds_NonPositiveOrNonFiniteUsesFallback()
        {
            float fallback = GhostAudioPresets.ExplosionOneShotFallbackDurationSeconds;
            Assert.Equal(fallback, GhostPlaybackLogic.NormalizeOneShotDurationSeconds(0f));
            Assert.Equal(fallback, GhostPlaybackLogic.NormalizeOneShotDurationSeconds(-1f));
            Assert.Equal(fallback, GhostPlaybackLogic.NormalizeOneShotDurationSeconds(float.NaN));
            Assert.Equal(fallback, GhostPlaybackLogic.NormalizeOneShotDurationSeconds(float.PositiveInfinity));
            Assert.Equal(0.01f, GhostPlaybackLogic.NormalizeOneShotDurationSeconds(0.01f));
            Assert.Equal(7.5f, GhostPlaybackLogic.NormalizeOneShotDurationSeconds(7.5f));
        }

        // ---------- IsVesselRecordedByTree --------------------------------------

        [Fact]
        public void IsVesselRecordedByTree_ReadsTheNamedCommittedTreeOnly()
        {
            var treeA = new RecordingTree { Id = "tree_a", TreeName = "A" };
            treeA.RecordedVesselPids.Add(111u);
            var treeB = new RecordingTree { Id = "tree_b", TreeName = "B" };
            treeB.RecordedVesselPids.Add(222u);
            RecordingStore.AddCommittedTreeForTesting(treeA);
            RecordingStore.AddCommittedTreeForTesting(treeB);

            Assert.True(GhostPlaybackLogic.IsVesselRecordedByTree("tree_a", 111u));
            Assert.False(GhostPlaybackLogic.IsVesselRecordedByTree("tree_a", 222u));
            Assert.True(GhostPlaybackLogic.IsVesselRecordedByTree("tree_b", 222u));
            Assert.False(GhostPlaybackLogic.IsVesselRecordedByTree("tree_c", 111u));
            Assert.False(GhostPlaybackLogic.IsVesselRecordedByTree("tree_a", 0u));
            Assert.False(GhostPlaybackLogic.IsVesselRecordedByTree(null, 111u));
        }
    }
}
