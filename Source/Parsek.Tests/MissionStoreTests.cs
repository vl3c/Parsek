using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MissionStoreTests : IDisposable
    {
        public MissionStoreTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStore.SuppressLogging = true;
            MissionStore.ResetForTesting();
        }

        public void Dispose()
        {
            MissionStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree Tree(string id, string name)
            => new RecordingTree { Id = id, TreeName = name };

        // A tree with a single continuous-vessel run, so MissionThroughLineBuilder
        // produces exactly one through-line whose head id == the root recording id.
        private static RecordingTree TreeWithThroughLineHead(
            string treeId, string headRecordingId)
        {
            var rec = new Recording
            {
                RecordingId = headRecordingId,
                VesselName = "V",
                ChainId = "C",
                ChainIndex = 0,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = 100,
                ExplicitEndUT = 200
            };
            var tree = new RecordingTree { Id = treeId, RootRecordingId = headRecordingId };
            tree.Recordings[headRecordingId] = rec;
            return tree;
        }

        private static Mission First() => new List<Mission>(MissionStore.Missions)[0];

        [Fact]
        public void EnsureDefaults_CreatesOnePerTree_AllIncluded_AndIsIdempotent()
        {
            var trees = new List<RecordingTree> { Tree("t1", "Kerbal X"), Tree("t2", "Mun Lander") };

            Assert.Equal(2, MissionStore.EnsureDefaultsForTrees(trees));
            Assert.Equal(2, MissionStore.Missions.Count);
            Assert.Equal(1, MissionStore.CountForTree("t1"));
            foreach (var m in MissionStore.Missions)
                Assert.Empty(m.ExcludedThroughLineHeadIds); // default = everything included

            Assert.Equal(0, MissionStore.EnsureDefaultsForTrees(trees)); // idempotent
            Assert.Equal(2, MissionStore.Missions.Count);
        }

        [Fact]
        public void Clone_CopiesSelection_IntoAnIndependentMission()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission original = First();
            original.ExcludedThroughLineHeadIds.Add("legA");
            original.LoopPlayback = true;
            original.LoopIntervalSeconds = 42.5;
            original.LoopTimeUnit = LoopTimeUnit.Min;
            original.LoopAnchorUT = 9876.5;

            Mission clone = MissionStore.Clone(original);

            Assert.Equal("t1", clone.TreeId);
            Assert.Equal(original.Name + " copy", clone.Name);
            Assert.NotEqual(original.Id, clone.Id);
            Assert.Contains("legA", clone.ExcludedThroughLineHeadIds);
            Assert.Equal(2, MissionStore.CountForTree("t1"));

            // Clone copies the loop fields, including the phase anchor.
            Assert.True(clone.LoopPlayback);
            Assert.Equal(42.5, clone.LoopIntervalSeconds);
            Assert.Equal(LoopTimeUnit.Min, clone.LoopTimeUnit);
            Assert.Equal(9876.5, clone.LoopAnchorUT);

            clone.ExcludedThroughLineHeadIds.Add("legB");
            Assert.DoesNotContain("legB", original.ExcludedThroughLineHeadIds); // independent sets
        }

        [Fact]
        public void Clone_InsertsCopyDirectlyAfterSource()
        {
            // Two trees so there is a mission after the source to displace; the clone must land
            // between the source and the next mission, not at the end of the list.
            MissionStore.EnsureDefaultsForTrees(
                new List<RecordingTree> { Tree("t1", "Kerbal X"), Tree("t2", "Mun Lander") });
            var before = new List<Mission>(MissionStore.Missions);
            Mission source = before.Find(m => m.TreeId == "t1");

            Mission clone = MissionStore.Clone(source);

            var after = new List<Mission>(MissionStore.Missions);
            int srcPos = after.IndexOf(source);
            Assert.Equal(srcPos + 1, after.IndexOf(clone)); // directly after the source
            Assert.Equal(before.Count + 1, after.Count);
        }

        [Fact]
        public void Delete_BlockedOnOriginal_AllowedOnCopy()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission only = First();

            Assert.False(MissionStore.CanDelete(only));
            Assert.False(MissionStore.Delete(only));
            Assert.Equal(1, MissionStore.CountForTree("t1"));

            Mission clone = MissionStore.Clone(only);
            Assert.True(MissionStore.CanDelete(clone));
            Assert.True(MissionStore.Delete(clone));
            Assert.Equal(1, MissionStore.CountForTree("t1"));
        }

        [Fact]
        public void Delete_OriginalNeverDeletable_EvenWithCopies()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission original = First();
            Mission copy = MissionStore.Clone(original);
            Mission copy2 = MissionStore.Clone(original);

            // The original (first mission of the tree) is never deletable, even with copies present;
            // only the copies are.
            Assert.False(MissionStore.CanDelete(original));
            Assert.True(MissionStore.CanDelete(copy));
            Assert.True(MissionStore.CanDelete(copy2));
            Assert.False(MissionStore.Delete(original));
            Assert.Equal(3, MissionStore.CountForTree("t1"));
        }

        [Fact]
        public void PruneOrphans_RemovesMissionsForMissingTrees()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X"), Tree("t2", "Y") });

            int removed = MissionStore.PruneOrphans(new List<RecordingTree> { Tree("t1", "X") });

            Assert.Equal(1, removed);
            Assert.Single(MissionStore.Missions);
            Assert.Equal(0, MissionStore.CountForTree("t2"));
        }

        [Fact]
        public void SaveLoad_RoundTripsNameTreeAndSelection()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission m = First();
            m.ExcludedThroughLineHeadIds.Add("legA");
            m.ExcludedThroughLineHeadIds.Add("legB");
            m.LoopPlayback = true;
            m.LoopIntervalSeconds = 123.75;
            m.LoopTimeUnit = LoopTimeUnit.Hour;
            m.LoopAnchorUT = 54321.25;
            string savedId = m.Id;

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);

            MissionStore.ResetForTesting();
            Assert.Empty(MissionStore.Missions);

            MissionStore.Load(node);

            Assert.Single(MissionStore.Missions);
            Mission loaded = First();
            Assert.Equal(savedId, loaded.Id);
            Assert.Equal("t1", loaded.TreeId);
            Assert.Equal(2, loaded.ExcludedThroughLineHeadIds.Count);
            Assert.Contains("legA", loaded.ExcludedThroughLineHeadIds);
            Assert.Contains("legB", loaded.ExcludedThroughLineHeadIds);

            // Loop fields round-trip too, including the phase anchor.
            Assert.True(loaded.LoopPlayback);
            Assert.Equal(123.75, loaded.LoopIntervalSeconds);
            Assert.Equal(LoopTimeUnit.Hour, loaded.LoopTimeUnit);
            Assert.Equal(54321.25, loaded.LoopAnchorUT);
        }

        [Fact]
        public void SaveLoad_RoundTripsArchivedFlag_AndHideArchivedToggle()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            First().Archived = true;
            MissionStore.HideArchived = true;

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            MissionStore.ResetForTesting();
            Assert.False(MissionStore.HideArchived); // reset cleared it

            MissionStore.Load(node);
            Assert.True(First().Archived);
            Assert.True(MissionStore.HideArchived);
        }

        [Fact]
        public void Load_MissingArchiveValues_DefaultToNotArchived()
        {
            // Older saves (before the Archive column) carry no archive fields: the mission must
            // load as not-archived and the global toggle off, not throw or stay stale.
            MissionStore.HideArchived = true; // pre-existing stale state
            var node = new ConfigNode("PARSEK");
            ConfigNode mNode = node.AddNode("MISSION");
            mNode.AddValue("id", "m1");
            mNode.AddValue("treeId", "t1");
            mNode.AddValue("name", "Kerbal X");

            MissionStore.Load(node);
            Assert.False(MissionStore.HideArchived);
            Assert.False(First().Archived);
        }

        [Fact]
        public void SaveLoad_RoundTripsWindowSize_AndUnsetStaysNaN()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            MissionStore.WindowWidth = 912.5f;
            MissionStore.WindowHeight = 480f;

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            MissionStore.ResetForTesting();
            Assert.True(float.IsNaN(MissionStore.WindowWidth)); // reset cleared it

            MissionStore.Load(node);
            Assert.Equal(912.5f, MissionStore.WindowWidth);
            Assert.Equal(480f, MissionStore.WindowHeight);

            // An unset (NaN) size is not written, and loads back as NaN (use the default).
            MissionStore.WindowWidth = float.NaN;
            MissionStore.WindowHeight = float.NaN;
            var node2 = new ConfigNode("PARSEK");
            MissionStore.Save(node2);
            Assert.False(node2.HasValue("missionWindowWidth"));
            MissionStore.WindowWidth = 123f; // dirty it
            MissionStore.Load(node2);
            Assert.True(float.IsNaN(MissionStore.WindowWidth));
        }

        [Fact]
        public void SaveLoad_AndClone_RoundTripExcludedIntervalKeys()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission m = First();
            m.ExcludedIntervalKeys.Add("L");        // dropped launch interval
            m.ExcludedIntervalKeys.Add("L/seg1");   // dropped survivor too

            // Clone copies the interval-trim selection independently.
            Mission copy = m.Clone("m-copy");
            Assert.Equal(2, copy.ExcludedIntervalKeys.Count);
            Assert.Contains("L", copy.ExcludedIntervalKeys);
            copy.ExcludedIntervalKeys.Add("extra");
            Assert.DoesNotContain("extra", m.ExcludedIntervalKeys); // independent set

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            MissionStore.ResetForTesting();
            MissionStore.Load(node);

            Mission loaded = First();
            Assert.Equal(2, loaded.ExcludedIntervalKeys.Count);
            Assert.Contains("L", loaded.ExcludedIntervalKeys);
            Assert.Contains("L/seg1", loaded.ExcludedIntervalKeys);
        }

        [Fact]
        public void Clone_CopiesArchivedFlag()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            First().Archived = true;
            MissionStore.Clone(First());

            var all = new List<Mission>(MissionStore.Missions);
            Assert.Equal(2, all.Count);
            Assert.True(all[1].Archived); // the copy (inserted right after the source) carries it
        }

        [Fact]
        public void SaveLoad_UnsetAnchor_RoundTripsAsNaN()
        {
            // An unset anchor (NaN) must survive save/load as NaN, not silently become 0 (which would
            // anchor the span clock at UT 0 instead of falling back to spanStart in the adapter).
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "Kerbal X") });
            Mission m = First();
            Assert.True(double.IsNaN(m.LoopAnchorUT)); // default

            var node = new ConfigNode("PARSEK");
            MissionStore.Save(node);
            MissionStore.ResetForTesting();
            MissionStore.Load(node);

            Assert.True(double.IsNaN(First().LoopAnchorUT));
        }

        [Fact]
        public void SetLoopEnabled_On_TurnsTargetOn_AndSameTreeOthersOff()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X") });
            Mission a = First();
            Mission b = MissionStore.Clone(a); // same tree t1
            Mission c = MissionStore.Clone(a); // same tree t1

            // Pre-set two same-tree variants on to prove one-loop-per-tree clears them.
            b.LoopPlayback = true;
            c.LoopPlayback = true;

            MissionStore.SetLoopEnabled(a, true, 1000.0);

            Assert.True(a.LoopPlayback);
            Assert.False(b.LoopPlayback);
            Assert.False(c.LoopPlayback);

            // Turning a different variant of the SAME tree on clears the previous selection.
            MissionStore.SetLoopEnabled(b, true, 1000.0);
            Assert.False(a.LoopPlayback);
            Assert.True(b.LoopPlayback);
            Assert.False(c.LoopPlayback);
        }

        [Fact]
        public void SetLoopEnabled_On_KeepsLoopingMissionsOnOtherTrees()
        {
            MissionStore.EnsureDefaultsForTrees(
                new List<RecordingTree> { Tree("t1", "Kerbal X"), Tree("t2", "Mun Lander") });
            var all = new List<Mission>(MissionStore.Missions);
            Mission m1 = all.Find(m => m.TreeId == "t1");
            Mission m2 = all.Find(m => m.TreeId == "t2");

            // Loop the first mission, then loop the second: concurrent across distinct trees.
            MissionStore.SetLoopEnabled(m1, true, 1000.0);
            MissionStore.SetLoopEnabled(m2, true, 1100.0);

            Assert.True(m1.LoopPlayback);  // not cleared - different tree
            Assert.True(m2.LoopPlayback);
            Assert.Equal(1000.0, m1.LoopAnchorUT);
            Assert.Equal(1100.0, m2.LoopAnchorUT);
        }

        [Fact]
        public void SetLoopEnabled_Off_TurnsOnlyTargetOff()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X") });
            Mission a = First();
            Mission b = MissionStore.Clone(a);
            a.LoopPlayback = true;
            b.LoopPlayback = true;

            MissionStore.SetLoopEnabled(a, false, 1000.0);

            Assert.False(a.LoopPlayback);
            Assert.True(b.LoopPlayback); // unaffected
        }

        [Fact]
        public void SetLoopEnabled_On_StampsAnchorUT_AndReEnableReAnchors()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X") });
            Mission a = First();
            Assert.True(double.IsNaN(a.LoopAnchorUT)); // unset before any enable

            // First enable stamps the anchor to the supplied UT.
            MissionStore.SetLoopEnabled(a, true, 1234.5);
            Assert.True(a.LoopPlayback);
            Assert.Equal(1234.5, a.LoopAnchorUT);

            // Disable then re-enable at a later UT re-anchors: the span clock will restart from the
            // recording start at the new anchor instead of resuming mid-mission.
            MissionStore.SetLoopEnabled(a, false, 2000.0);
            Assert.Equal(1234.5, a.LoopAnchorUT); // disable leaves the stale anchor in place

            MissionStore.SetLoopEnabled(a, true, 5555.5);
            Assert.True(a.LoopPlayback);
            Assert.Equal(5555.5, a.LoopAnchorUT); // re-enable overwrites it
        }

        [Fact]
        public void NormalizeOneLoopPerTree_ClearsExtraSameTreeLoops_KeepsFirstPerTree()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X") });
            Mission a = First();
            Mission b = MissionStore.Clone(a); // same tree t1
            Mission c = MissionStore.Clone(a); // same tree t1
            // Simulate a hand-edited save where several same-tree missions loop at once.
            a.LoopPlayback = true;
            b.LoopPlayback = true;
            c.LoopPlayback = true;

            int cleared = MissionStore.NormalizeOneLoopPerTree();

            Assert.Equal(2, cleared);
            Assert.True(a.LoopPlayback);   // first in list order for the tree is kept
            Assert.False(b.LoopPlayback);
            Assert.False(c.LoopPlayback);

            // Idempotent: a second pass clears nothing.
            Assert.Equal(0, MissionStore.NormalizeOneLoopPerTree());
        }

        [Fact]
        public void NormalizeOneLoopPerTree_KeepsOneLoopPerDistinctTree()
        {
            MissionStore.EnsureDefaultsForTrees(
                new List<RecordingTree> { Tree("t1", "Kerbal X"), Tree("t2", "Mun Lander") });
            var all = new List<Mission>(MissionStore.Missions);
            Mission m1 = all.Find(m => m.TreeId == "t1");
            Mission m2 = all.Find(m => m.TreeId == "t2");
            // Two looping missions on DISTINCT trees: both are valid concurrent loops.
            m1.LoopPlayback = true;
            m2.LoopPlayback = true;

            int cleared = MissionStore.NormalizeOneLoopPerTree();

            Assert.Equal(0, cleared);     // nothing to clear - one per tree already
            Assert.True(m1.LoopPlayback);
            Assert.True(m2.LoopPlayback);
        }

        [Fact]
        public void NormalizeOneLoopPerTree_NullTreeIdMissions_CollapseToOneSlot()
        {
            // A corrupt save could carry missions with no tree id. NormalizeOneLoopPerTree maps a
            // null TreeId to "" so all null-tree missions share ONE logical loop slot, while a
            // real-tree mission stays independent.
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X") });
            Mission real = First();                // tree t1
            Mission n1 = MissionStore.Clone(real); // inserted after its source
            Mission n2 = MissionStore.Clone(real); // inserted after its source
            n1.TreeId = null;
            n2.TreeId = null;
            real.LoopPlayback = true;
            n1.LoopPlayback = true;
            n2.LoopPlayback = true;

            int cleared = MissionStore.NormalizeOneLoopPerTree();

            // The real-tree mission is independent; the two null-tree missions collapse to one
            // logical "" slot, so exactly one of them is cleared (which one depends on list
            // order, so assert the invariant, not a specific survivor).
            Assert.Equal(1, cleared);
            Assert.True(real.LoopPlayback);                       // distinct (real) tree - kept
            Assert.True(n1.LoopPlayback ^ n2.LoopPlayback);       // exactly one null-tree survivor
        }

        [Fact]
        public void ReconcileSelections_RemovesBogusHead_AndWarns_KeepsValidHead()
        {
            // Capture log output to assert on the warn.
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStore.SuppressLogging = false;

            const string headId = "headLeg";
            var tree = TreeWithThroughLineHead("t1", headId);
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { tree });
            Mission m = First();
            m.ExcludedThroughLineHeadIds.Add(headId);     // valid: a current through-line head
            m.ExcludedThroughLineHeadIds.Add("bogusHead"); // stale: not a head anymore

            int removed = MissionStore.ReconcileSelections(new List<RecordingTree> { tree });

            Assert.Equal(1, removed);
            Assert.DoesNotContain("bogusHead", m.ExcludedThroughLineHeadIds); // dropped
            Assert.Contains(headId, m.ExcludedThroughLineHeadIds);            // survives
            Assert.Contains(logLines,
                l => l.Contains("[Mission]") && l.Contains("ReconcileSelections")
                  && l.Contains("removed 1"));
        }

        [Fact]
        public void ReconcileSelections_AllValidHeads_RemovesNothing_AndDoesNotWarn()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStore.SuppressLogging = false;

            const string headId = "headLeg";
            var tree = TreeWithThroughLineHead("t1", headId);
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { tree });
            Mission m = First();
            m.ExcludedThroughLineHeadIds.Add(headId);

            int removed = MissionStore.ReconcileSelections(new List<RecordingTree> { tree });

            Assert.Equal(0, removed);
            Assert.Contains(headId, m.ExcludedThroughLineHeadIds);
            Assert.DoesNotContain(logLines, l => l.Contains("ReconcileSelections"));
        }
    }
}
