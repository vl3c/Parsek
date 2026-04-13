using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CompoundPartPlaybackTests
    {
        [Fact]
        public void BuildSnapshotPartIdSet_CollectsAllPersistentIds()
        {
            var snapshot = new ConfigNode("VESSEL");
            AddSnapshotPart(snapshot, "root", "100");
            AddSnapshotPart(snapshot, "tank", "101");
            AddSnapshotPart(snapshot, "strut", "0");
            AddSnapshotPart(snapshot, "engine", "not-a-number");
            AddSnapshotPart(snapshot, "capsule", null);

            HashSet<uint> partIds = GhostVisualBuilder.BuildSnapshotPartIdSet(snapshot);

            Assert.Equal(2, partIds.Count);
            Assert.Contains(100u, partIds);
            Assert.Contains(101u, partIds);
        }

        [Fact]
        public void ShouldHideCompoundPart_HidesWhenTargetMissingFromSnapshot()
        {
            bool result = GhostPlaybackLogic.ShouldHideCompoundPart(
                200,
                new HashSet<uint> { 100 },
                targetVisualExists: false,
                targetVisualActive: false);

            Assert.True(result);
        }

        [Fact]
        public void ShouldHideCompoundPart_DoesNotHideWhenTargetIdUnknown()
        {
            bool result = GhostPlaybackLogic.ShouldHideCompoundPart(
                0,
                new HashSet<uint> { 100 },
                targetVisualExists: false,
                targetVisualActive: false);

            Assert.False(result);
        }

        [Fact]
        public void ShouldHideCompoundPart_DoesNotHideWhenTargetExistsLogicallyWithoutVisualRoot()
        {
            bool result = GhostPlaybackLogic.ShouldHideCompoundPart(
                200,
                new HashSet<uint> { 100, 200 },
                targetVisualExists: false,
                targetVisualActive: false);

            Assert.False(result);
        }

        [Fact]
        public void ShouldHideCompoundPart_HidesWhenTargetVisualIsInactive()
        {
            bool result = GhostPlaybackLogic.ShouldHideCompoundPart(
                200,
                new HashSet<uint> { 100, 200 },
                targetVisualExists: true,
                targetVisualActive: false);

            Assert.True(result);
        }

        private static void AddSnapshotPart(ConfigNode snapshot, string name, string persistentId)
        {
            var part = snapshot.AddNode("PART");
            part.AddValue("name", name);
            if (persistentId != null)
                part.AddValue("persistentId", persistentId);
            part.AddValue("parent", "0");
        }
    }
}
