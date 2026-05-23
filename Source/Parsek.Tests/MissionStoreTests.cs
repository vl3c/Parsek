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

            Mission clone = MissionStore.Clone(original);

            Assert.Equal("t1", clone.TreeId);
            Assert.Equal(original.Name + " copy", clone.Name);
            Assert.NotEqual(original.Id, clone.Id);
            Assert.Contains("legA", clone.ExcludedThroughLineHeadIds);
            Assert.Equal(2, MissionStore.CountForTree("t1"));

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
        }
    }
}
