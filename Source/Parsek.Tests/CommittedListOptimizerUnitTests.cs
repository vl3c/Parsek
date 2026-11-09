using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the units of <see cref="CommittedListOptimizer"/> that the existing
    /// optimizer suites reach only through a whole pass: the split identity copy, the
    /// merge's tree bookkeeping, the split's ledger retag cut and the boring-tail skip
    /// summary.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedListOptimizerUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public CommittedListOptimizerUnitTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
        }

        // ---- CopySplitIdentityFields ----

        private static Recording FullIdentity() => new Recording
        {
            RecordingId = "orig",
            ChainId = "chain-1",
            TreeId = "tree-1",
            VesselName = "Probe",
            VesselPersistentId = 42u,
            RecordedVesselGuid = "guid-1",
            PreLaunchFunds = 1000.5,
            PreLaunchScience = 12.25,
            PreLaunchReputation = 3.5f,
            RecordingGroups = new List<string> { "A", "B" },
            CreatingSessionId = "sess-1",
            ProvisionalForRpId = "rp-1",
            SupersedeTargetId = "sup-1",
            SwitchSegmentSessionId = "seg-1",
        };

        [Fact]
        public void SplitIdentity_FreshIdAndEveryLineageFieldCopied()
        {
            Recording original = FullIdentity();
            var second = new Recording { RecordingId = "orig" };

            CommittedListOptimizer.CopySplitIdentityFields(original, second);

            Assert.NotEqual("orig", second.RecordingId);
            Assert.Equal(32, second.RecordingId.Length);
            Assert.Equal("chain-1", original.ChainId);
            Assert.Equal("chain-1", second.ChainId);
            Assert.Equal("tree-1", second.TreeId);
            Assert.Equal("Probe", second.VesselName);
            Assert.Equal(42u, second.VesselPersistentId);
            Assert.Equal("guid-1", second.RecordedVesselGuid);
            Assert.Equal(1000.5, second.PreLaunchFunds);
            Assert.Equal(12.25, second.PreLaunchScience);
            Assert.Equal(3.5f, second.PreLaunchReputation);
            Assert.Equal(new[] { "A", "B" }, second.RecordingGroups);
            Assert.NotSame(original.RecordingGroups, second.RecordingGroups);
            Assert.Equal("sess-1", second.CreatingSessionId);
            Assert.Equal("rp-1", second.ProvisionalForRpId);
            Assert.Equal("sup-1", second.SupersedeTargetId);
            Assert.Equal("seg-1", second.SwitchSegmentSessionId);
        }

        [Fact]
        public void SplitIdentity_AnUnchainedOriginalGetsAChainIdBothHalvesShare()
        {
            var original = new Recording { RecordingId = "orig", ChainId = null, RecordingGroups = null };
            var second = new Recording { RecordingGroups = new List<string> { "stale" } };

            CommittedListOptimizer.CopySplitIdentityFields(original, second);

            Assert.False(string.IsNullOrEmpty(original.ChainId));
            Assert.Equal(original.ChainId, second.ChainId);
            Assert.Null(second.RecordingGroups);

            // An empty chain id is replaced too.
            var emptyChain = new Recording { ChainId = "" };
            var secondHalf = new Recording();
            CommittedListOptimizer.CopySplitIdentityFields(emptyChain, secondHalf);
            Assert.Equal(32, emptyChain.ChainId.Length);
            Assert.Equal(emptyChain.ChainId, secondHalf.ChainId);
        }

        // ---- UpdateTreeStateAfterOptimizationMerge ----

        private static RecordingTree CommitTree(string id, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = id };
            foreach (var r in recs)
            {
                r.TreeId = id;
                tree.AddOrReplaceRecording(r);
            }
            RecordingStore.CommittedTrees.Add(tree);
            return tree;
        }

        [Fact]
        public void MergeTreeState_RemovesTheAbsorbedAndRepointsRootAndActive()
        {
            var target = new Recording { RecordingId = "t" };
            var absorbed = new Recording { RecordingId = "a" };
            var other = new Recording { RecordingId = "o" };
            RecordingTree tree = CommitTree("tree-m", target, absorbed, other);
            tree.RootRecordingId = "a";
            tree.ActiveRecordingId = "a";
            RecordingTree untouched = CommitTree("tree-other", new Recording { RecordingId = "a2" });
            untouched.RootRecordingId = "a2";

            CommittedListOptimizer.UpdateTreeStateAfterOptimizationMerge(target, absorbed);

            Assert.False(tree.Recordings.ContainsKey("a"));
            Assert.Same(target, tree.Recordings["t"]);
            Assert.True(tree.Recordings.ContainsKey("o"));
            Assert.Equal("t", tree.RootRecordingId);
            Assert.Equal("t", tree.ActiveRecordingId);
            Assert.Equal("a2", untouched.RootRecordingId);
            Assert.Single(untouched.Recordings);
        }

        [Fact]
        public void MergeTreeState_OtherRootAndActiveStayAndATargetNotYetInTheTreeIsAdded()
        {
            var target = new Recording { RecordingId = "t", TreeId = "tree-n" };
            var absorbed = new Recording { RecordingId = "a" };
            RecordingTree tree = CommitTree("tree-n", absorbed, new Recording { RecordingId = "r" });
            tree.RootRecordingId = "r";
            tree.ActiveRecordingId = null;

            CommittedListOptimizer.UpdateTreeStateAfterOptimizationMerge(target, absorbed);

            Assert.Same(target, tree.Recordings["t"]);
            Assert.False(tree.Recordings.ContainsKey("a"));
            Assert.Equal("r", tree.RootRecordingId);
            Assert.Null(tree.ActiveRecordingId);
        }

        [Fact]
        public void MergeTreeState_TreeIdFromTheTargetElseTheAbsorbedElseNothing()
        {
            // The target's tree id wins: the absorbed's own tree is left alone.
            var absorbedInB = new Recording { RecordingId = "a" };
            RecordingTree treeB = CommitTree("tree-b", absorbedInB);
            var targetInA = new Recording { RecordingId = "t" };
            RecordingTree treeA = CommitTree("tree-a", targetInA, new Recording { RecordingId = "a" });
            absorbedInB.TreeId = "tree-b";

            CommittedListOptimizer.UpdateTreeStateAfterOptimizationMerge(targetInA, absorbedInB);
            Assert.True(treeB.Recordings.ContainsKey("a"));
            Assert.False(treeA.Recordings.ContainsKey("a"));

            // No target tree id: the absorbed's tree id is used, and a null target only removes.
            var lone = new Recording { RecordingId = "x" };
            RecordingTree treeC = CommitTree("tree-c", lone);
            treeC.RootRecordingId = "x";
            CommittedListOptimizer.UpdateTreeStateAfterOptimizationMerge(null, lone);
            Assert.Empty(treeC.Recordings);
            Assert.Equal("x", treeC.RootRecordingId);

            // No tree id anywhere, or a null absorbed: nothing changes.
            CommittedListOptimizer.UpdateTreeStateAfterOptimizationMerge(
                new Recording { RecordingId = "t2" }, new Recording { RecordingId = "a" });
            Assert.True(treeB.Recordings.ContainsKey("a"));
            CommittedListOptimizer.UpdateTreeStateAfterOptimizationMerge(targetInA, null);
            Assert.True(treeA.Recordings.ContainsKey("t"));
        }

        // ---- RetagLedgerActionsAfterOptimizationSplit ----

        private static GameAction Science(double ut, string recId) => new GameAction
        {
            UT = ut,
            Type = GameActionType.ScienceEarning,
            RecordingId = recId,
        };

        [Fact]
        public void SplitRetag_CutsAtTheSecondHalfsFirstSectionStart()
        {
            Ledger.AddAction(Science(120.0, "orig"));
            Ledger.AddAction(Science(180.0, "orig"));
            Ledger.AddAction(Science(250.0, "orig"));
            Ledger.AddAction(Science(260.0, "someone-else"));

            var original = new Recording { RecordingId = "orig" };
            var second = new Recording { RecordingId = "second" };
            second.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            second.Points.Add(new TrajectoryPoint { ut = 300.0, bodyName = "Kerbin" });
            second.TrackSections.Add(new TrackSection { startUT = 200.0, endUT = 300.0 });

            CommittedListOptimizer.RetagLedgerActionsAfterOptimizationSplit(original, second);

            var tags = Ledger.Actions.Select(a => a.UT + ":" + a.RecordingId).ToArray();
            Assert.Equal(new[] { "120:orig", "180:orig", "250:second", "260:someone-else" }, tags);
            Assert.Contains(logLines, l => l.Contains("[INFO][RecordingStore]")
                && l.Contains("Optimization split: retagged 1 ledger action(s) from 'orig' to second half 'second'"));
        }

        [Fact]
        public void SplitRetag_NoSectionsCutsAtTheSecondHalfsStartUT()
        {
            Ledger.AddAction(Science(120.0, "orig"));
            Ledger.AddAction(Science(180.0, "orig"));

            var original = new Recording { RecordingId = "orig" };
            var second = new Recording { RecordingId = "second" };
            second.Points.Add(new TrajectoryPoint { ut = 150.0, bodyName = "Kerbin" });
            second.Points.Add(new TrajectoryPoint { ut = 300.0, bodyName = "Kerbin" });

            CommittedListOptimizer.RetagLedgerActionsAfterOptimizationSplit(original, second);

            var tags = Ledger.Actions.Select(a => a.UT + ":" + a.RecordingId).ToArray();
            Assert.Equal(new[] { "120:orig", "180:second" }, tags);
        }

        [Fact]
        public void SplitRetag_NullHalfDoesNothingAndNoMovedRowLogsNoRetagLine()
        {
            Ledger.AddAction(Science(180.0, "orig"));
            CommittedListOptimizer.RetagLedgerActionsAfterOptimizationSplit(null, new Recording { RecordingId = "second" });
            CommittedListOptimizer.RetagLedgerActionsAfterOptimizationSplit(new Recording { RecordingId = "orig" }, null);
            Assert.Equal("orig", Ledger.Actions.Single().RecordingId);

            var second = new Recording { RecordingId = "second" };
            second.TrackSections.Add(new TrackSection { startUT = 500.0, endUT = 600.0 });
            CommittedListOptimizer.RetagLedgerActionsAfterOptimizationSplit(new Recording { RecordingId = "orig" }, second);
            Assert.Equal("orig", Ledger.Actions.Single().RecordingId);
            Assert.DoesNotContain(logLines, l => l.Contains("Optimization split: retagged"));
        }

        // ---- TrimBoringTailsForOptimization ----

        private static Recording ShortLeaf(string id)
        {
            var rec = new Recording { RecordingId = id };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 101.0, bodyName = "Kerbin" });
            return rec;
        }

        [Fact]
        public void TrimSkipSummary_OneLineCountsByCategoryDescendingThenByName()
        {
            var recordings = new List<Recording>
            {
                new Recording { RecordingId = "empty-1" },
                new Recording { RecordingId = "empty-2" },
                ShortLeaf("s1"),
                ShortLeaf("s2"),
                ShortLeaf("s3"),
            };

            CommittedListOptimizer.TrimBoringTailsForOptimization(recordings);

            var summary = Assert.Single(logLines, l => l.Contains("Optimization pass: TrimBoringTail skipped"));
            Assert.Contains("[VERBOSE][RecordingStore]", summary);
            Assert.EndsWith("TrimBoringTail skipped 5 recording(s) — too-short=3, rec-null-or-too-few-points=2", summary);
            Assert.DoesNotContain(logLines, l => l.Contains("trimmed boring tails"));
            Assert.DoesNotContain(logLines, l => l.Contains("TrimBoringTail: skipped ("));
        }

        [Fact]
        public void TrimSkipSummary_EqualCountsOrderByOrdinalName()
        {
            var recordings = new List<Recording>
            {
                ShortLeaf("s1"),
                new Recording { RecordingId = "empty" },
            };

            CommittedListOptimizer.TrimBoringTailsForOptimization(recordings);

            var summary = Assert.Single(logLines, l => l.Contains("Optimization pass: TrimBoringTail skipped"));
            Assert.EndsWith("skipped 2 recording(s) — rec-null-or-too-few-points=1, too-short=1", summary);
        }

        [Fact]
        public void TrimSkipSummary_NothingToSummarizeLogsNothing()
        {
            CommittedListOptimizer.TrimBoringTailsForOptimization(new List<Recording>());
            Assert.Empty(logLines);
        }
        // ---- extracted helpers ----

        [Fact]
        public void SplitSecondHalfUT_FirstSectionStartElseStartUT()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 150.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 300.0, bodyName = "Kerbin" });
            Assert.Equal(150.0, CommittedListOptimizer.ResolveSplitSecondHalfUT(rec));

            rec.TrackSections.Add(new TrackSection { startUT = 200.0, endUT = 250.0 });
            rec.TrackSections.Add(new TrackSection { startUT = 120.0, endUT = 130.0 });
            Assert.Equal(200.0, CommittedListOptimizer.ResolveSplitSecondHalfUT(rec));

            rec.TrackSections = null;
            Assert.Equal(150.0, CommittedListOptimizer.ResolveSplitSecondHalfUT(rec));
        }

        [Fact]
        public void TrimSkipCounts_DescendingCountThenOrdinalNameAndTheTotal()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["too-short"] = 2,
                ["not-leaf"] = 5,
                ["b"] = 2,
                ["B"] = 2,
            };
            Assert.Equal("not-leaf=5, B=2, b=2, too-short=2",
                CommittedListOptimizer.FormatTrimSkipCounts(counts, out int total));
            Assert.Equal(11, total);

            Assert.Equal("", CommittedListOptimizer.FormatTrimSkipCounts(
                new Dictionary<string, int>(), out total));
            Assert.Equal(0, total);
        }

        [Fact]
        public void SplitSegmentBodyNames_FromEachHalfsFirstPointElseUnchanged()
        {
            var original = new Recording { SegmentBodyName = "old-a" };
            original.Points.Add(new TrajectoryPoint { ut = 1, bodyName = "Kerbin" });
            original.Points.Add(new TrajectoryPoint { ut = 2, bodyName = "Mun" });
            var second = new Recording { SegmentBodyName = "old-b" };

            CommittedListOptimizer.DeriveSplitSegmentBodyNames(original, second);
            Assert.Equal("Kerbin", original.SegmentBodyName);
            Assert.Equal("old-b", second.SegmentBodyName);

            second.Points.Add(new TrajectoryPoint { ut = 3, bodyName = "Minmus" });
            second.Points.Add(new TrajectoryPoint { ut = 4, bodyName = "Kerbin" });
            original.Points = null;
            original.SegmentBodyName = "kept";
            CommittedListOptimizer.DeriveSplitSegmentBodyNames(original, second);
            Assert.Equal("kept", original.SegmentBodyName);
            Assert.Equal("Minmus", second.SegmentBodyName);
        }

        [Fact]
        public void SplitChildBranchPoint_MovesToTheSecondHalfOrTheSecondCarriesNone()
        {
            var original = new Recording { ChildBranchPointId = "bp-1" };
            var second = new Recording { ChildBranchPointId = "copied-by-split" };
            CommittedListOptimizer.AssignSplitChildBranchPoint(original, second, movesToSecond: true);
            Assert.Null(original.ChildBranchPointId);
            Assert.Equal("bp-1", second.ChildBranchPointId);

            original = new Recording { ChildBranchPointId = "bp-1" };
            second = new Recording { ChildBranchPointId = "copied-by-split" };
            CommittedListOptimizer.AssignSplitChildBranchPoint(original, second, movesToSecond: false);
            Assert.Equal("bp-1", original.ChildBranchPointId);
            Assert.Null(second.ChildBranchPointId);
        }

        [Fact]
        public void AddToCommittedTree_TheFirstTreeWithTheIdOnly()
        {
            RecordingTree other = CommitTree("tree-x");
            RecordingTree first = CommitTree("tree-y");
            RecordingTree duplicate = CommitTree("tree-y");
            var rec = new Recording { RecordingId = "half-2" };

            CommittedListOptimizer.AddToCommittedTree("tree-y", rec);
            Assert.Same(rec, first.Recordings["half-2"]);
            Assert.Empty(duplicate.Recordings);
            Assert.Empty(other.Recordings);

            // Replace, not duplicate.
            var replacement = new Recording { RecordingId = "half-2" };
            CommittedListOptimizer.AddToCommittedTree("tree-y", replacement);
            Assert.Same(replacement, Assert.Single(first.Recordings).Value);

            CommittedListOptimizer.AddToCommittedTree(null, new Recording { RecordingId = "n" });
            CommittedListOptimizer.AddToCommittedTree("", new Recording { RecordingId = "e" });
            CommittedListOptimizer.AddToCommittedTree("tree-none", new Recording { RecordingId = "z" });
            Assert.Single(first.Recordings);
            Assert.Empty(other.Recordings);
            Assert.Empty(duplicate.Recordings);
        }
    }
}
