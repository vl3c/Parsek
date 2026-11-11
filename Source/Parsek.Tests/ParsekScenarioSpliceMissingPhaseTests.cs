using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the phases extracted from
    /// <c>ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree</c>
    /// (<c>SpliceMissingCommittedRecordingsPhase*</c>). Their place inside the method is pinned
    /// by <see cref="ParsekScenarioSpliceMissingCharacterizationTests"/>; these cells pin what
    /// each one does. Touches shared static state (ParsekLog, RecordingStore), hence
    /// Sequential.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekScenarioSpliceMissingPhaseTests : System.IDisposable
    {
        public ParsekScenarioSpliceMissingPhaseTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree TreeOf(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "T1" };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.Recordings[r.RecordingId] = r;
            }
            return tree;
        }

        private static Recording Rec(string id, int points)
        {
            var rec = new Recording { RecordingId = id, VesselName = "V-" + id };
            for (int i = 0; i < points; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 100 + i });
            return rec;
        }

        // ---- recordings ----

        [Fact]
        public void Recordings_ClonesTheMissingOnesAndRefreshesTheDivergedOnes_AddingToTheCounts()
        {
            // committed: a (missing from loaded), b (active, diverged), c (diverged), d (in sync)
            var committed = TreeOf(Rec("a", 2), Rec("b", 2), Rec("c", 2), Rec("d", 3));
            var loadedB = Rec("b", 5);
            var loadedC = Rec("c", 5);
            var loadedD = Rec("d", 3);
            RecordingSidecarStore.MarkSidecarLoadFailure(loadedB, "trajectory-missing");
            RecordingSidecarStore.MarkSidecarLoadFailure(loadedC, "trajectory-missing");
            var loaded = TreeOf(loadedB, loadedC, loadedD);
            RecordingSidecarStore.MarkSidecarLoadFailure(committed.Recordings["a"], "trajectory-missing");

            var splicedIds = new List<string> { "earlier" };
            var refreshedIds = new List<string>();
            int spliced = 10, refreshed = 20, full = 30, preserved = 40;

            ParsekScenario.SpliceMissingCommittedRecordingsPhaseSpliceAndRefreshRecordings(
                loaded, committed, "b", splicedIds, refreshedIds,
                ref spliced, ref refreshed, ref full, ref preserved);

            Assert.Equal(11, spliced);
            Assert.Equal(22, refreshed);
            Assert.Equal(31, full);
            Assert.Equal(41, preserved);
            Assert.Equal(new[] { "earlier", "a" }, splicedIds);
            Assert.Equal(new[] { "b", "c" }, refreshedIds);

            // The splice is a clone, marked dirty, with the sidecar failure cleared.
            Recording a = loaded.Recordings["a"];
            Assert.NotSame(committed.Recordings["a"], a);
            Assert.True(a.FilesDirty);
            Assert.False(a.SidecarLoadFailed);
            Assert.True(committed.Recordings["a"].SidecarLoadFailed);

            // Both diverged copies took the committed shape; only the active one kept its
            // recorder-owned sidecar failure.
            Assert.Equal(2, loadedB.Points.Count);
            Assert.Equal(2, loadedC.Points.Count);
            Assert.True(loadedB.SidecarLoadFailed);
            Assert.False(loadedC.SidecarLoadFailed);
            Assert.False(loadedD.FilesDirty);
        }

        [Fact]
        public void Recordings_NoActiveId_RefreshesEveryDivergedOneInFull()
        {
            var committed = TreeOf(Rec("b", 2));
            var loadedB = Rec("b", 5);
            RecordingSidecarStore.MarkSidecarLoadFailure(loadedB, "trajectory-missing");
            var loaded = TreeOf(loadedB);
            var splicedIds = new List<string>();
            var refreshedIds = new List<string>();
            int spliced = 0, refreshed = 0, full = 0, preserved = 0;

            ParsekScenario.SpliceMissingCommittedRecordingsPhaseSpliceAndRefreshRecordings(
                loaded, committed, null, splicedIds, refreshedIds,
                ref spliced, ref refreshed, ref full, ref preserved);

            Assert.Equal(0, spliced);
            Assert.Equal(1, refreshed);
            Assert.Equal(1, full);
            Assert.Equal(0, preserved);
            Assert.False(loadedB.SidecarLoadFailed);
        }

        [Fact]
        public void Recordings_AnEmptyCommittedTree_ChangesNothing()
        {
            var loaded = TreeOf(Rec("b", 5));
            var splicedIds = new List<string>();
            var refreshedIds = new List<string>();
            int spliced = 3, refreshed = 4, full = 5, preserved = 6;

            ParsekScenario.SpliceMissingCommittedRecordingsPhaseSpliceAndRefreshRecordings(
                loaded, TreeOf(), "b", splicedIds, refreshedIds,
                ref spliced, ref refreshed, ref full, ref preserved);

            Assert.Equal(3, spliced);
            Assert.Equal(4, refreshed);
            Assert.Equal(5, full);
            Assert.Equal(6, preserved);
            Assert.Empty(splicedIds);
            Assert.Empty(refreshedIds);
            Assert.Single(loaded.Recordings);
        }

        // ---- branch points ----

        private static BranchPoint Bp(string id, string[] parents, string[] children)
        {
            return new BranchPoint
            {
                Id = id,
                UT = 50,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };
        }

        [Fact]
        public void BranchPoints_ClonesTheMissingOnesAndOverwritesDivergedLists_AddingToTheCounts()
        {
            var committed = TreeOf();
            committed.BranchPoints = new List<BranchPoint>
            {
                Bp("new", new[] { "p" }, new[] { "c1" }),
                Bp("moved", new[] { "half2" }, new[] { "c2" }),
                Bp("same", new[] { "x" }, new[] { "y" }),
            };
            var loaded = TreeOf();
            var loadedMoved = Bp("moved", new[] { "whole" }, new[] { "c2" });
            var loadedSame = Bp("same", new[] { "x" }, new[] { "y" });
            List<string> sameParents = loadedSame.ParentRecordingIds;
            loaded.BranchPoints = new List<BranchPoint> { loadedMoved, loadedSame };
            int splicedBps = 7, updatedBps = 8;

            ParsekScenario.SpliceMissingCommittedRecordingsPhaseMergeBranchPoints(
                loaded, committed, ref splicedBps, ref updatedBps);

            Assert.Equal(8, splicedBps);
            Assert.Equal(9, updatedBps);
            Assert.Equal(3, loaded.BranchPoints.Count);
            BranchPoint added = loaded.BranchPoints[2];
            Assert.Equal("new", added.Id);
            Assert.NotSame(committed.BranchPoints[0], added);
            Assert.Equal(new[] { "p" }, added.ParentRecordingIds);

            Assert.Equal(new[] { "half2" }, loadedMoved.ParentRecordingIds);
            Assert.Equal(new[] { "c2" }, loadedMoved.ChildRecordingIds);
            Assert.NotSame(committed.BranchPoints[1].ParentRecordingIds, loadedMoved.ParentRecordingIds);
            Assert.Same(sameParents, loadedSame.ParentRecordingIds);
        }

        [Fact]
        public void BranchPoints_ANullLoadedList_IsCreatedToTakeTheClones()
        {
            var committed = TreeOf();
            committed.BranchPoints = new List<BranchPoint> { Bp("new", new[] { "p" }, new string[0]) };
            var loaded = TreeOf();
            loaded.BranchPoints = null;
            int splicedBps = 0, updatedBps = 0;

            ParsekScenario.SpliceMissingCommittedRecordingsPhaseMergeBranchPoints(
                loaded, committed, ref splicedBps, ref updatedBps);

            Assert.Equal(1, splicedBps);
            Assert.Equal(0, updatedBps);
            Assert.Single(loaded.BranchPoints);
        }

        [Fact]
        public void BranchPoints_NoCommittedBranchPoints_LeavesANullLoadedListNull()
        {
            var committed = TreeOf();
            committed.BranchPoints = new List<BranchPoint>();
            var loaded = TreeOf();
            loaded.BranchPoints = null;
            int splicedBps = 0, updatedBps = 0;

            ParsekScenario.SpliceMissingCommittedRecordingsPhaseMergeBranchPoints(
                loaded, committed, ref splicedBps, ref updatedBps);

            Assert.Null(loaded.BranchPoints);
            Assert.Equal(0, splicedBps);
            Assert.Equal(0, updatedBps);
        }
    }
}
