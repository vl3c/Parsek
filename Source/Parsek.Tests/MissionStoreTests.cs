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

            Mission clone = MissionStore.Clone(original);

            Assert.Equal("t1", clone.TreeId);
            Assert.Equal(original.Name + " copy", clone.Name);
            Assert.NotEqual(original.Id, clone.Id);
            Assert.Contains("legA", clone.ExcludedThroughLineHeadIds);
            Assert.Equal(2, MissionStore.CountForTree("t1"));

            // Clone copies the three loop fields.
            Assert.True(clone.LoopPlayback);
            Assert.Equal(42.5, clone.LoopIntervalSeconds);
            Assert.Equal(LoopTimeUnit.Min, clone.LoopTimeUnit);

            clone.ExcludedThroughLineHeadIds.Add("legB");
            Assert.DoesNotContain("legB", original.ExcludedThroughLineHeadIds); // independent sets
        }

        [Fact]
        public void Delete_BlockedOnLast_AllowedWhenMoreThanOne()
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

            // Loop fields round-trip too.
            Assert.True(loaded.LoopPlayback);
            Assert.Equal(123.75, loaded.LoopIntervalSeconds);
            Assert.Equal(LoopTimeUnit.Hour, loaded.LoopTimeUnit);
        }

        [Fact]
        public void SetLoopEnabled_On_TurnsTargetOn_AndAllOthersOff()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X") });
            Mission a = First();
            Mission b = MissionStore.Clone(a);
            Mission c = MissionStore.Clone(a);

            // Pre-set two others on to prove single-selection clears them.
            b.LoopPlayback = true;
            c.LoopPlayback = true;

            MissionStore.SetLoopEnabled(a, true);

            Assert.True(a.LoopPlayback);
            Assert.False(b.LoopPlayback);
            Assert.False(c.LoopPlayback);

            // Turning a different one on clears the previous selection.
            MissionStore.SetLoopEnabled(b, true);
            Assert.False(a.LoopPlayback);
            Assert.True(b.LoopPlayback);
            Assert.False(c.LoopPlayback);
        }

        [Fact]
        public void SetLoopEnabled_Off_TurnsOnlyTargetOff()
        {
            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { Tree("t1", "X") });
            Mission a = First();
            Mission b = MissionStore.Clone(a);
            a.LoopPlayback = true;
            b.LoopPlayback = true;

            MissionStore.SetLoopEnabled(a, false);

            Assert.False(a.LoopPlayback);
            Assert.True(b.LoopPlayback); // unaffected
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
