using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// SPLIT-BRANCHPOINT-PARENT (todo MISSION-LOG-REWORK "look into"): a recording split at UT t
    /// left every branch point after the cut, and every child linked to the recording after the
    /// cut, naming the HEAD (pre-cut) segment. These cells pin the shared rule
    /// (<see cref="SplitParentLinks"/>) on each split path (the optimizer pass, the merge that
    /// reverses it; the Re-Fly splitter's cell lives in RecordingTreeSplitterTests), the
    /// load-time repair for trees written before the fix, and the mission interval keys over
    /// every recorded fixture tree.
    /// </summary>
    [Collection("Sequential")]
    public class SplitParentLinksTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly List<string> logLines = new List<string>();

        public SplitParentLinksTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            MissionStructureBuilder.SuppressLogging = false;
            MissionCompositionBuilder.SuppressLogging = false;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ------------------------------------------------------------ shapes

        private static void AddSection(Recording rec, SegmentEnvironment env, double start, double end,
            double altStart, double altEnd)
        {
            rec.Points.Add(new TrajectoryPoint { ut = start, altitude = altStart, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = (start + end) / 2, altitude = (altStart + altEnd) / 2, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = env, startUT = start, endUT = end,
                frames = new List<TrajectoryPoint>(),
            });
        }

        // One vessel flying exo [1000,1200] -> atmo [1200,1400] -> surface [1400,1600]: the
        // optimizer cuts it at 1200 and at 1400 (both runs outlast the graze window).
        private static Recording ThreeEnvironmentRecording(string id, string treeId)
        {
            var rec = new Recording
            {
                RecordingId = id, VesselName = "Stack", TreeId = treeId, VesselPersistentId = 42,
                TerminalStateValue = TerminalState.Landed,
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
            };
            AddSection(rec, SegmentEnvironment.ExoBallistic, 1000, 1200, 90000, 70000);
            AddSection(rec, SegmentEnvironment.Atmospheric, 1200, 1400, 69000, 100);
            AddSection(rec, SegmentEnvironment.SurfaceStationary, 1400, 1600, 80, 80);
            rec.Points.Add(new TrajectoryPoint { ut = 1600, altitude = 80, bodyName = "Kerbin" });
            return rec;
        }

        private static BranchPoint BP(string id, BranchPointType type, double ut, params string[] parents)
            => new BranchPoint
            {
                Id = id, Type = type, UT = ut,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(),
            };

        private static Recording Child(string id, string treeId, string parentRecId, string parentBpId,
            double start, double end, string crew = null)
        {
            var rec = new Recording
            {
                RecordingId = id, VesselName = crew ?? id, TreeId = treeId, EvaCrewName = crew,
                ParentRecordingId = parentRecId, ParentBranchPointId = parentBpId,
            };
            rec.Points.Add(new TrajectoryPoint { ut = start, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = end, altitude = 100, bodyName = "Kerbin" });
            return rec;
        }

        private static List<Recording> Chain(RecordingTree tree, string chainId)
            => tree.Recordings.Values.Where(r => r.ChainId == chainId).OrderBy(r => r.ChainIndex).ToList();

        // ------------------------------------------------------------ optimizer split

        // catches: the defect itself. A decouple, an EVA and a dock after the optimizer's cuts
        // kept naming the head; each now names the segment that holds its UT, an event exactly
        // at a cut belongs to the later segment, one a moment before it stays, and the dock's
        // second parent (a recording that was not split) keeps its id and position.
        [Fact]
        public void OptimizerSplit_RepointsEveryLaterBranchPointAndChild_ToTheSegmentThatHoldsIt()
        {
            const string treeId = "tree-split";
            var tree = new RecordingTree { Id = treeId, TreeName = "Split" };
            Recording rec = ThreeEnvironmentRecording("rec", treeId);
            tree.AddOrReplaceRecording(rec);
            tree.RootRecordingId = rec.RecordingId;

            var early = BP("bp-early", BranchPointType.JointBreak, 1100, "rec");
            var justBefore = BP("bp-before-cut", BranchPointType.JointBreak, 1199.99, "rec");
            var atCut = BP("bp-at-cut", BranchPointType.JointBreak, 1200, "rec");
            var eva = BP("bp-eva", BranchPointType.EVA, 1300, "rec");
            eva.ChildRecordingIds.Add("kerbal");
            var dock = BP("bp-dock", BranchPointType.Dock, 1500, "other", "rec");
            dock.ChildRecordingIds.Add("docked");
            tree.BranchPoints.AddRange(new[] { early, justBefore, atCut, eva, dock });
            rec.ChildBranchPointId = dock.Id;
            Recording kerbal = Child("kerbal", treeId, "rec", "bp-eva", 1300, 1350, "Jeb");
            Recording earlyKerbal = Child("kerbal-early", treeId, "rec", "bp-early", 1100, 1150, "Bill");
            tree.AddOrReplaceRecording(kerbal);
            tree.AddOrReplaceRecording(earlyKerbal);

            RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.RunOptimizationPass();

            List<Recording> segs = Chain(tree, rec.ChainId);
            Assert.Equal(3, segs.Count);
            Assert.Same(rec, segs[0]);
            Assert.Equal(new[] { segs[0].RecordingId }, early.ParentRecordingIds);
            Assert.Equal(new[] { segs[0].RecordingId }, justBefore.ParentRecordingIds);
            Assert.Equal(new[] { segs[1].RecordingId }, atCut.ParentRecordingIds);
            Assert.Equal(new[] { segs[1].RecordingId }, eva.ParentRecordingIds);
            Assert.Equal(new[] { "other", segs[2].RecordingId }, dock.ParentRecordingIds);
            Assert.Equal(dock.Id, segs[2].ChildBranchPointId);
            Assert.Null(segs[0].ChildBranchPointId);
            Assert.Equal(segs[1].RecordingId, kerbal.ParentRecordingId);
            Assert.Equal(segs[0].RecordingId, earlyKerbal.ParentRecordingId);
            Assert.Contains(logLines, l => l.Contains("[SplitParentLinks]")
                && l.Contains("Optimization split") && l.Contains("repointed"));
            // The repaired-on-load shape is exactly what the live split wrote.
            Assert.Equal(0, SplitParentLinks.RepairStaleParents(tree, "test"));
        }

        // catches: the merge that reverses a split leaving a branch point or an EVA child on
        // the absorbed segment's id, which no longer exists after the merge.
        [Fact]
        public void OptimizerMerge_PointsTheAbsorbedSegmentsLinksAtTheSurvivor()
        {
            const string treeId = "tree-merge";
            var tree = new RecordingTree { Id = treeId, TreeName = "Merge" };
            Recording a = MergeableSegment("seg-a", treeId, 0, 17000, 17030);
            Recording b = MergeableSegment("seg-b", treeId, 1, 17030, 17060);
            tree.AddOrReplaceRecording(a);
            tree.AddOrReplaceRecording(b);
            tree.RootRecordingId = a.RecordingId;
            var decouple = BP("bp-decouple", BranchPointType.JointBreak, 17045, "seg-b");
            tree.BranchPoints.Add(decouple);
            Recording kerbal = Child("kerbal", treeId, "seg-b", null, 17050, 17055, "Val");
            tree.AddOrReplaceRecording(kerbal);

            RecordingStore.AddRecordingWithTreeForTesting(a);
            RecordingStore.AddRecordingWithTreeForTesting(b);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.RunOptimizationPass();

            Assert.False(tree.Recordings.ContainsKey("seg-b"));
            Assert.Equal(new[] { "seg-a" }, decouple.ParentRecordingIds);
            Assert.Equal("seg-a", kerbal.ParentRecordingId);
            Assert.Contains(logLines, l => l.Contains("[SplitParentLinks]")
                && l.Contains("Optimization merge") && l.Contains("survivor=seg-a"));
        }

        private static Recording MergeableSegment(string id, string treeId, int index, double start, double end)
        {
            var rec = new Recording
            {
                RecordingId = id, VesselName = "Chain", TreeId = treeId, ChainId = "chain-m",
                ChainIndex = index, ChainBranch = 0, SegmentPhase = "exo", SegmentBodyName = "Mun",
                PlaybackEnabled = true, LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel,
            };
            rec.Points.Add(new TrajectoryPoint { ut = start, altitude = 50000, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = end, altitude = 50000, bodyName = "Mun" });
            return rec;
        }

        // catches: the split and the merge disagreeing (the mirror direction): a split followed
        // by the merge that reverses it restores every link exactly, two-parent branch point
        // included, and a parent list that already holds the survivor is not doubled.
        [Fact]
        public void SplitThenMerge_RestoresEveryLink()
        {
            var tree = new RecordingTree { Id = "t", TreeName = "t" };
            var head = new Recording { RecordingId = "head", TreeId = "t", ChainId = "c", ChainIndex = 0 };
            var tail = new Recording { RecordingId = "tail", TreeId = "t", ChainId = "c", ChainIndex = 1 };
            tree.AddOrReplaceRecording(head);
            var before = BP("before", BranchPointType.JointBreak, 50, "head");
            var after = BP("after", BranchPointType.Dock, 150, "other", "head");
            tree.BranchPoints.AddRange(new[] { before, after });
            Recording kerbal = Child("k", "t", "head", null, 160, 170, "Jeb");
            tree.AddOrReplaceRecording(kerbal);

            var changes = new List<SplitParentLinks.LinkChange>();
            Assert.Equal(2, SplitParentLinks.RepointToTail(tree, "head", tail, 100, changes, "test"));
            Assert.Equal(new[] { "other", "tail" }, after.ParentRecordingIds);
            Assert.Equal("tail", kerbal.ParentRecordingId);

            tree.AddOrReplaceRecording(tail);
            Assert.Equal(2, SplitParentLinks.RepointToSurvivor(tree, "tail", "head", "test"));
            Assert.Equal(new[] { "head" }, before.ParentRecordingIds);
            Assert.Equal(new[] { "other", "head" }, after.ParentRecordingIds);
            Assert.Equal("head", kerbal.ParentRecordingId);

            // A list already naming the survivor drops the absorbed entry instead of doubling it.
            var both = BP("both", BranchPointType.Dock, 200, "head", "tail");
            tree.BranchPoints.Add(both);
            SplitParentLinks.RepointToSurvivor(tree, "tail", "head", "test");
            Assert.Equal(new[] { "head" }, both.ParentRecordingIds);

            // Undo reverses each recorded rewrite.
            SplitParentLinks.RepointToTail(tree, "head", tail, 100, changes = new List<SplitParentLinks.LinkChange>(), "test");
            for (int i = changes.Count - 1; i >= 0; i--) SplitParentLinks.Undo(changes[i]);
            Assert.Equal(new[] { "other", "head" }, after.ParentRecordingIds);
            Assert.Equal("head", kerbal.ParentRecordingId);
        }

        [Theory]
        [InlineData(100.0, 100.0, true)]      // exactly at the cut: the tail's first sample
        [InlineData(100.00005, 100.0, true)]
        [InlineData(99.99995, 100.0, true)]   // inside the 0.1 ms tolerance
        [InlineData(99.999, 100.0, false)]
        [InlineData(double.NaN, 100.0, false)]
        [InlineData(100.0, double.NaN, false)]
        [InlineData(double.PositiveInfinity, 100.0, false)]
        public void BelongsToTail_Boundary(double eventUT, double tailStart, bool expected)
            => Assert.Equal(expected, SplitParentLinks.BelongsToTail(eventUT, tailStart));

        // ------------------------------------------------------------ readers that stay put

        // catches: the predicate the composition and the spawn safety net read drifting: only
        // a later chain segment, only a branch point at or after its start, never the one it
        // ends at (ChildBranchPointId or at its end UT).
        [Fact]
        public void IsFlownPastOnLaterSegment_OnlyMovedBranchPointsTheVesselFlewPast()
        {
            Recording head = Segment("head", 0, 0, 100);
            Recording tail = Segment("tail", 1, 100, 200);
            Recording unsplit = Child("solo", "stale", null, null, 0, 200);
            var mid = BP("mid", BranchPointType.JointBreak, 150, "tail");
            var atStart = BP("start", BranchPointType.JointBreak, 100, "tail");
            var atEnd = BP("end", BranchPointType.Dock, 199.5, "tail");
            var before = BP("before", BranchPointType.JointBreak, 50, "tail");

            Assert.True(SplitParentLinks.IsFlownPastOnLaterSegment(tail, mid));
            Assert.True(SplitParentLinks.IsFlownPastOnLaterSegment(tail, atStart));
            Assert.False(SplitParentLinks.IsFlownPastOnLaterSegment(tail, atEnd));
            Assert.False(SplitParentLinks.IsFlownPastOnLaterSegment(tail, before));
            Assert.False(SplitParentLinks.IsFlownPastOnLaterSegment(head, mid));
            Assert.False(SplitParentLinks.IsFlownPastOnLaterSegment(unsplit, mid));
            tail.ChildBranchPointId = "mid";
            Assert.False(SplitParentLinks.IsFlownPastOnLaterSegment(tail, mid));
        }

        // catches: the corrected parent turning a chain's last segment into a "non-leaf" in
        // the spawn safety net (it now parents the decouple it flew past), which would stop
        // the vessel spawning at its recording end; an unsplit recording reads as before.
        [Fact]
        public void SpawnLeafSafetyNet_IgnoresABranchPointTheLastSegmentFlewPast()
        {
            var tree = new RecordingTree { Id = "leaf", TreeName = "leaf" };
            Recording head = Segment("head", 0, 0, 100);
            Recording tail = Segment("tail", 1, 100, 200);
            head.TreeId = tail.TreeId = "leaf";
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tail);
            tree.BranchPoints.Add(BP("decouple", BranchPointType.JointBreak, 150, "tail"));
            Assert.False(GhostPlaybackLogic.IsNonLeafInTree(tail, tree));

            var flat = new RecordingTree { Id = "flat", TreeName = "flat" };
            Recording solo = Child("solo", "flat", null, null, 0, 200);
            flat.AddOrReplaceRecording(solo);
            flat.BranchPoints.Add(BP("decouple", BranchPointType.JointBreak, 150, "solo"));
            Assert.True(GhostPlaybackLogic.IsNonLeafInTree(solo, flat));
        }

        // catches: the composition following a post-switch Launch child (another vessel) as
        // the ship's continuation once the Launch branch point names the later segment; it
        // stays a peel of the run, as it was when the branch point sat on the first segment.
        [Fact]
        public void Composition_LaunchChildOnALaterSegment_StaysAPeel()
        {
            var tree = new RecordingTree { Id = "c", TreeName = "c" };
            Recording head = Segment("head", 0, 0, 100);
            Recording tail = Segment("tail", 1, 100, 300);
            head.TreeId = tail.TreeId = "c";
            Recording other = Child("other", "c", null, "launch", 140, 260);
            other.VesselName = "Other";
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tail);
            tree.AddOrReplaceRecording(other);
            var launch = BP("launch", BranchPointType.Launch, 150, "tail");
            launch.ChildRecordingIds.Add("other");
            tree.BranchPoints.Add(launch);
            tree.RootRecordingId = "head";

            MissionStructure s = MissionStructureBuilder.Build(tree);
            Assert.Null(MissionThroughLineBuilder.ContinuationSuccessor(s, s.LegsById["tail"]));
            Assert.False(s.LegsById["tail"].ContinuesAsVessel);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(s);
            Assert.Equal(new[] { "head", "tail" }, view.ByHeadId["head"].MemberLegIds.ToArray());
            Assert.True(view.ByHeadId.ContainsKey("other"));

            // The same shape on an unsplit leg reads as it always has.
            var flat = new RecordingTree { Id = "f", TreeName = "f" };
            Recording solo = Child("solo", "f", null, null, 0, 300);
            Recording other2 = Child("other", "f", null, "launch", 140, 260);
            flat.AddOrReplaceRecording(solo);
            flat.AddOrReplaceRecording(other2);
            var launch2 = BP("launch", BranchPointType.Launch, 150, "solo");
            launch2.ChildRecordingIds.Add("other");
            flat.BranchPoints.Add(launch2);
            MissionStructure fs = MissionStructureBuilder.Build(flat);
            Assert.Equal("other", MissionThroughLineBuilder.ContinuationSuccessor(fs, fs.LegsById["solo"]));
        }

        // ------------------------------------------------------------ load-time repair

        // A tree as the old split wrote it: head [0,100] -> tail [100,200] -> tail2 [200,300],
        // every later link still on the head.
        private static RecordingTree StaleThreeSegmentTree()
        {
            var tree = new RecordingTree { Id = "stale", TreeName = "Stale" };
            tree.AddOrReplaceRecording(Segment("head", 0, 0, 100));
            tree.AddOrReplaceRecording(Segment("tail", 1, 100, 200));
            tree.AddOrReplaceRecording(Segment("tail2", 2, 200, 300));
            tree.RootRecordingId = "head";
            tree.BranchPoints.Add(BP("b50", BranchPointType.JointBreak, 50, "head"));
            tree.BranchPoints.Add(BP("b100", BranchPointType.JointBreak, 100, "head"));
            var b150 = BP("b150", BranchPointType.EVA, 150, "head");
            b150.ChildRecordingIds.Add("k0");
            tree.BranchPoints.Add(b150);
            tree.BranchPoints.Add(BP("b160", BranchPointType.Dock, 160, "other", "head"));
            tree.BranchPoints.Add(BP("b250", BranchPointType.JointBreak, 250, "head"));
            tree.BranchPoints.Add(BP("b260", BranchPointType.JointBreak, 260, "tail"));
            // An EVA kerbal split into two chain segments of its own: both follow the EVA.
            Recording k0 = Child("k0", "stale", "head", "b150", 150, 180, "Jeb");
            k0.ChainId = "kc"; k0.ChainIndex = 0;
            Recording k1 = Child("k1", "stale", "head", null, 180, 240, "Jeb");
            k1.ChainId = "kc"; k1.ChainIndex = 1;
            tree.AddOrReplaceRecording(k0);
            tree.AddOrReplaceRecording(k1);
            tree.AddOrReplaceRecording(Child("other", "stale", null, null, 0, 300));
            return tree;
        }

        private static Recording Segment(string id, int index, double start, double end)
        {
            var rec = new Recording { RecordingId = id, VesselName = "Stack", TreeId = "stale", ChainId = "c", ChainIndex = index };
            rec.Points.Add(new TrajectoryPoint { ut = start, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = end, altitude = 100, bodyName = "Kerbin" });
            return rec;
        }

        private static string Parents(RecordingTree tree, string bpId)
            => string.Join(",", tree.BranchPoints.Single(b => b.Id == bpId).ParentRecordingIds);

        // catches: an old save keeping its stale parents (no repair), a repair that moves a
        // link backwards or past the segment holding it, a split child whose two segments
        // disagree, and a repair that is not idempotent or logs on a clean tree.
        [Fact]
        public void LoadRepair_MovesStaleLinksForward_Once_AndIsIdempotent()
        {
            RecordingTree tree = StaleThreeSegmentTree();

            int repaired = SplitParentLinks.RepairStaleParents(tree, "LoadRecordingTrees");

            Assert.Equal(7, repaired);
            Assert.Equal("head", Parents(tree, "b50"));
            Assert.Equal("tail", Parents(tree, "b100"));
            Assert.Equal("tail", Parents(tree, "b150"));
            Assert.Equal("other,tail", Parents(tree, "b160"));
            Assert.Equal("tail2", Parents(tree, "b250"));
            Assert.Equal("tail2", Parents(tree, "b260"));
            Assert.Equal("tail", tree.Recordings["k0"].ParentRecordingId);
            Assert.Equal("tail", tree.Recordings["k1"].ParentRecordingId);
            Assert.Null(tree.Recordings["other"].ParentRecordingId);
            Assert.Single(logLines, l => l.Contains("[INFO][SplitParentLinks]")
                && l.Contains("RepairStaleParents: context=LoadRecordingTrees tree=stale")
                && l.Contains("bpParents=5") && l.Contains("childParents=2"));

            logLines.Clear();
            Assert.Equal(0, SplitParentLinks.RepairStaleParents(tree, "LoadRecordingTrees"));
            Assert.DoesNotContain(logLines, l => l.Contains("[SplitParentLinks]"));
        }

        // catches: the repair touching a tree whose links are already right (every tree the
        // fixed split writes), or one with no chains at all.
        [Fact]
        public void LoadRepair_LeavesACorrectTreeUntouchedAndSilent()
        {
            RecordingTree tree = StaleThreeSegmentTree();
            SplitParentLinks.RepairStaleParents(tree, "first");
            string snapshot = Snapshot(tree);
            logLines.Clear();

            Assert.Equal(0, SplitParentLinks.RepairStaleParents(tree, "second"));
            Assert.Equal(snapshot, Snapshot(tree));
            Assert.Empty(logLines.Where(l => l.Contains("[SplitParentLinks]")));

            var flat = new RecordingTree { Id = "flat", TreeName = "flat" };
            flat.AddOrReplaceRecording(Child("root", "flat", null, null, 0, 100));
            flat.BranchPoints.Add(BP("b", BranchPointType.JointBreak, 50, "root"));
            Assert.Equal(0, SplitParentLinks.RepairStaleParents(flat, "flat"));
            Assert.Equal("root", Parents(flat, "b"));
        }

        // catches: the repair guessing. A successor the branch point itself created (a real
        // child, not a later segment), an ambiguous successor (two segments at the next
        // index), and a successor with no trajectory (no usable start) all stop the walk.
        [Fact]
        public void LoadRepair_StopsOnOwnChild_AmbiguousOrDatalessSuccessor()
        {
            var own = new RecordingTree { Id = "own", TreeName = "own" };
            own.AddOrReplaceRecording(Segment("head", 0, 0, 100));
            Recording next = Segment("next", 1, 100, 200);
            next.ParentBranchPointId = "b";
            own.AddOrReplaceRecording(next);
            var b = BP("b", BranchPointType.Undock, 100, "head");
            b.ChildRecordingIds.Add("next");
            own.BranchPoints.Add(b);
            Assert.Equal(0, SplitParentLinks.RepairStaleParents(own, "own"));
            Assert.Equal("head", Parents(own, "b"));

            var ambiguous = new RecordingTree { Id = "amb", TreeName = "amb" };
            ambiguous.AddOrReplaceRecording(Segment("head", 0, 0, 100));
            ambiguous.AddOrReplaceRecording(Segment("x", 1, 100, 200));
            ambiguous.AddOrReplaceRecording(Segment("y", 1, 100, 200));
            ambiguous.BranchPoints.Add(BP("b", BranchPointType.JointBreak, 150, "head"));
            Assert.Equal(0, SplitParentLinks.RepairStaleParents(ambiguous, "amb"));
            Assert.Equal("head", Parents(ambiguous, "b"));

            var dataless = new RecordingTree { Id = "dl", TreeName = "dl" };
            dataless.AddOrReplaceRecording(Segment("head", 0, 0, 100));
            dataless.AddOrReplaceRecording(new Recording { RecordingId = "empty", TreeId = "dl", ChainId = "c", ChainIndex = 1 });
            dataless.BranchPoints.Add(BP("b", BranchPointType.JointBreak, 150, "head"));
            Assert.Equal(0, SplitParentLinks.RepairStaleParents(dataless, "dl"));
            Assert.Equal("head", Parents(dataless, "b"));
        }

        private static string Snapshot(RecordingTree tree)
        {
            var parts = new List<string>();
            foreach (BranchPoint bp in tree.BranchPoints)
                parts.Add(bp.Id + "=" + string.Join(",", bp.ParentRecordingIds));
            foreach (Recording r in tree.Recordings.Values.OrderBy(r => r.RecordingId, StringComparer.Ordinal))
                parts.Add(r.RecordingId + "->" + (r.ParentRecordingId ?? "-"));
            return string.Join(";", parts);
        }

        // ------------------------------------------------------------ fixture key diff

        public static IEnumerable<object[]> RecordedFixtures()
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "harness", "fixtures", "saves"));
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string sfs = Path.Combine(dir, "persistent.sfs");
                if (!File.Exists(sfs)) continue;
                string text = File.ReadAllText(sfs);
                if (text.Contains("RECORDING_TREE") && text.Contains("BRANCH_POINT"))
                    yield return new object[] { Path.GetFileName(dir) };
            }
        }

        private static List<RecordingTree> LoadAllTrees(string fixture)
        {
            string saveDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "harness", "fixtures", "saves", fixture));
            ConfigNode root = ConfigNode.Load(Path.Combine(saveDir, "persistent.sfs"));
            ConfigNode game = root.GetNode("GAME") ?? root;
            var trees = new List<RecordingTree>();
            foreach (ConfigNode scen in game.GetNodes("SCENARIO"))
            {
                if (scen.GetValue("name") != "ParsekScenario") continue;
                foreach (ConfigNode tn in scen.GetNodes("RECORDING_TREE"))
                {
                    RecordingTree tree = RecordingTree.Load(tn);
                    foreach (Recording rec in tree.Recordings.Values)
                    {
                        string precTxt = Path.Combine(saveDir, "Parsek", "Recordings", rec.RecordingId + ".prec.txt");
                        if (!File.Exists(precTxt)) continue;
                        ConfigNode node = ConfigNode.Load(precTxt);
                        if (node != null) TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec);
                    }
                    trees.Add(tree);
                }
            }
            return trees;
        }

        // Selectable interval key -> its span: the physical interval a stored key selects.
        private static SortedDictionary<string, string> Keys(RecordingTree tree)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var stack = new Stack<MissionCompositionNode>(
                MissionCompositionBuilder.Build(MissionStructureBuilder.Build(tree)));
            while (stack.Count > 0)
            {
                MissionCompositionNode n = stack.Pop();
                if (n.IsSelectable)
                    map[n.HeadLegId] = n.StartUT.ToString("R", CultureInfo.InvariantCulture)
                        + ".." + n.EndUT.ToString("R", CultureInfo.InvariantCulture);
                foreach (MissionCompositionNode c in n.Children) stack.Push(c);
            }
            return map;
        }

        // Everything the Missions tab and the Log draw for one tree, as text: every
        // composition node (key, span, events, labels, owner), every vessel row and every
        // Log row. Two trees that differ only in which segment a link names must agree.
        private static List<string> Behaviour(List<RecordingTree> trees, RecordingTree tree)
        {
            var ic = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            MissionStructure s = MissionStructureBuilder.Build(tree);
            List<MissionCompositionNode> roots = MissionCompositionBuilder.Build(s);
            var stack = new Stack<KeyValuePair<int, MissionCompositionNode>>();
            for (int i = roots.Count - 1; i >= 0; i--) stack.Push(new KeyValuePair<int, MissionCompositionNode>(0, roots[i]));
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                MissionCompositionNode n = e.Value;
                lines.Add($"node{e.Key} {n.HeadLegId} {n.StartUT.ToString("R", ic)}..{n.EndUT.ToString("R", ic)} " +
                    $"{n.StartEvent}|{n.EndEvent}|{n.CompositionLabel}|{n.VesselName}|{n.OwnerHeadId}|sel={n.IsSelectable}");
                for (int i = n.Children.Count - 1; i >= 0; i--)
                    stack.Push(new KeyValuePair<int, MissionCompositionNode>(e.Key + 1, n.Children[i]));
            }
            var partners = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> names = MissionVesselNaming.Build(tree, s,
                MissionVesselNaming.LaunchIndex.Build(trees), t => "m-" + t,
                out MissionVesselNaming.Tally _, null, partners);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(s);
            MissionUndockSides sides = MissionVesselRowBuilder.ResolveUndockSides(s, partners);
            Func<string, double, string> dock = (head, ut) =>
                MissionPresentation.ResolveSameTreeDockPartnerVesselName(s, view, head, ut, names);
            Func<string, double, string> terminal = (head, ut) =>
                MissionPresentation.ResolveTerminalDockPartnerVesselName(s, head, ut, names);
            var rowStack = new Stack<KeyValuePair<int, MissionVesselRow>>();
            List<MissionVesselRow> rows = MissionVesselRowBuilder.Build(roots, dock, names, partners, terminal, sides);
            for (int i = rows.Count - 1; i >= 0; i--) rowStack.Push(new KeyValuePair<int, MissionVesselRow>(0, rows[i]));
            while (rowStack.Count > 0)
            {
                var e = rowStack.Pop();
                MissionVesselRow r = e.Value;
                lines.Add($"row{e.Key} {r.OwnerHeadId} {r.VesselName} {r.StartEvent}|{r.EndEvent}|{r.EventPhrase} " +
                    "keys=" + string.Join(",", MissionVesselRowBuilder.IntervalKeys(r)));
                for (int i = r.Children.Count - 1; i >= 0; i--)
                    rowStack.Push(new KeyValuePair<int, MissionVesselRow>(e.Key + 1, r.Children[i]));
            }
            // Which recording ends its vessel's flight (the spawn and crew-retirement gate),
            // read through the branch-point parent safety net.
            foreach (Recording rec in tree.Recordings.Values.OrderBy(r => r.RecordingId, StringComparer.Ordinal))
                lines.Add($"final {rec.RecordingId} {GhostPlaybackLogic.IsFinalSpawnSegment(rec, tree)}");
            foreach (StructureStep step in MissionStructureListBuilder.Build(tree, s, null, (bp, viewer) => null,
                         null, names, partners))
                lines.Add($"log {step.UT.ToString("R", ic)} {step.Label}|{step.Location}|{step.VesselName}");
            return lines;
        }

        // The one visible change the corrected parent makes on a recorded fixture: the Log's
        // Location for a staging row is read from the branch point's parent at its UT, and the
        // stale head ended at Kerbin long before the stage dropped at Duna.
        private static readonly string[][] KnownCorrections =
        {
            new[]
            {
                "log 2570542384.1110182 Staged: 1 piece (Decoupler.2)|Kerbin|Kerbal X",
                "log 2570542384.1110182 Staged: 1 piece (Decoupler.2)|Duna|Kerbal X",
            },
        };

        private void AssertSameBehaviour(string stage, List<string> staleSide, List<string> fixedSide,
            List<string> corrections)
        {
            var expected = new List<string>(staleSide);
            var actual = new List<string>(fixedSide);
            foreach (string[] pair in KnownCorrections)
            {
                int i = expected.IndexOf(pair[0]);
                int j = actual.IndexOf(pair[1]);
                if (i < 0 || j < 0) continue;
                expected[i] = pair[1];
                corrections.Add(pair[0] + " -> " + pair[1]);
            }
            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            foreach (string m in missing) output.WriteLine(stage + " - " + m);
            foreach (string x in extra) output.WriteLine(stage + " + " + x);
            Assert.True(missing.Count == 0 && extra.Count == 0 && expected.Count == actual.Count,
                stage + ": " + string.Join(" | ", missing.Select(m => "-" + m).Concat(extra.Select(x => "+" + x))));
        }

        private void AssertSameKeys(string stage, SortedDictionary<string, string> expected,
            SortedDictionary<string, string> actual)
        {
            var diff = new List<string>();
            foreach (var kv in expected)
                if (!actual.TryGetValue(kv.Key, out string span)) diff.Add("missing " + kv.Key + " " + kv.Value);
                else if (span != kv.Value) diff.Add("moved " + kv.Key + " " + kv.Value + " -> " + span);
            foreach (var kv in actual)
                if (!expected.ContainsKey(kv.Key)) diff.Add("extra " + kv.Key + " " + kv.Value);
            foreach (string d in diff) output.WriteLine(stage + ": " + d);
            Assert.True(diff.Count == 0, stage + ": " + string.Join(" | ", diff));
        }

        // What the old split left behind: every link on a later chain segment names the
        // chain's first segment, except the branch point a segment ends at (its
        // ChildBranchPointId), which the old split did move.
        private static int MakeStale(RecordingTree tree)
        {
            int staled = 0;
            Recording First(Recording r)
                => string.IsNullOrEmpty(r.ChainId) ? r
                   : tree.Recordings.Values.Where(x => x.ChainId == r.ChainId && x.ChainBranch == r.ChainBranch && x.ChainIndex >= 0)
                       .OrderBy(x => x.ChainIndex).First();
            foreach (BranchPoint bp in tree.BranchPoints)
                for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                {
                    if (!tree.Recordings.TryGetValue(bp.ParentRecordingIds[p], out Recording named)) continue;
                    if (named.ChainIndex <= 0 || named.ChildBranchPointId == bp.Id) continue;
                    bp.ParentRecordingIds[p] = First(named).RecordingId;
                    staled++;
                }
            foreach (Recording child in tree.Recordings.Values)
            {
                if (child.ParentRecordingId == null
                    || !tree.Recordings.TryGetValue(child.ParentRecordingId, out Recording named)) continue;
                if (named.ChainIndex <= 0 || named.ChainId == child.ChainId && child.ChainId != null) continue;
                child.ParentRecordingId = First(named).RecordingId;
                staled++;
            }
            return staled;
        }

        // catches: the fix (or the repair) moving a stored mission interval key, or naming a
        // different span with it, on any recorded fixture tree; and the load-time repair
        // disagreeing with what the live split writes. Per fixture: the trees as on disk, then
        // the optimizer pass as a load runs it (fixed rule), then the same trees with the old
        // stale links put back. Keys and spans must match across all three, and the repair of
        // the stale copy must give back exactly the live split's links.
        [Theory]
        [MemberData(nameof(RecordedFixtures))]
        public void EveryRecordedFixture_KeysUnmoved_RepairEqualsTheLiveSplit(string fixture)
        {
            List<RecordingTree> trees = LoadAllTrees(fixture);
            var corrections = new List<string>();
            int staleOnDisk = 0;
            foreach (RecordingTree tree in trees)
            {
                SortedDictionary<string, string> raw = Keys(tree);
                List<string> rawBehaviour = Behaviour(trees, tree);
                staleOnDisk += SplitParentLinks.RepairStaleParents(tree, "disk");
                AssertSameKeys("disk-repair " + tree.Id, raw, Keys(tree));
                AssertSameBehaviour("disk-repair " + tree.Id, rawBehaviour, Behaviour(trees, tree), corrections);
            }

            foreach (RecordingTree tree in trees)
            {
                RecordingStore.AddCommittedTreeForTesting(tree);
                foreach (Recording rec in tree.Recordings.Values)
                    RecordingStore.AddCommittedInternal(rec);
            }
            logLines.Clear();
            RecordingStore.RunOptimizationPass();
            int repointedLive = logLines.Count(l => l.Contains("[VERBOSE][SplitParentLinks]")
                && l.Contains("Optimization split"));

            int staled = 0, repaired = 0;
            foreach (RecordingTree tree in trees)
            {
                SortedDictionary<string, string> live = Keys(tree);
                List<string> liveBehaviour = Behaviour(trees, tree);
                string liveLinks = Snapshot(tree);
                staled += MakeStale(tree);
                AssertSameKeys("stale " + tree.Id, live, Keys(tree));
                AssertSameBehaviour("stale " + tree.Id, Behaviour(trees, tree), liveBehaviour, corrections);
                repaired += SplitParentLinks.RepairStaleParents(tree, "stale");
                Assert.Equal(liveLinks, Snapshot(tree));
                Assert.Equal(0, SplitParentLinks.RepairStaleParents(tree, "again"));
                Assert.Equal(live, Keys(tree));
            }
            Assert.Equal(staled, repaired);
            output.WriteLine($"{fixture}: trees={trees.Count} staleOnDisk={staleOnDisk} " +
                $"liveRepoints={repointedLive} staledThenRepaired={repaired} " +
                $"corrections={corrections.Count}");
            foreach (string c in corrections) output.WriteLine("  corrected: " + c);
            Assert.Equal(fixture == "duna-park-recorded" ? 2 : 0, corrections.Count);
        }
    }
}
