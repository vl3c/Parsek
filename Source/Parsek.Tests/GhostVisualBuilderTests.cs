using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class GhostVisualBuilderTests
    {
        [Fact]
        public void GetGhostSnapshot_PrefersGhostVisualOverVesselSnapshot()
        {
            var rec = new Recording();
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("name", "EndSnapshot");
            var ghost = new ConfigNode("VESSEL");
            ghost.AddValue("name", "StartSnapshot");
            rec.VesselSnapshot = vessel;
            rec.GhostVisualSnapshot = ghost;

            ConfigNode selected = GhostVisualBuilder.GetGhostSnapshot(rec);

            Assert.Equal("StartSnapshot", selected.GetValue("name"));
        }

        [Fact]
        public void GetGhostSnapshot_ReturnsGhostWhenVesselSnapshotIsNull()
        {
            var rec = new Recording();
            var ghost = new ConfigNode("VESSEL");
            ghost.AddValue("name", "StartSnapshot");
            rec.GhostVisualSnapshot = ghost;

            ConfigNode selected = GhostVisualBuilder.GetGhostSnapshot(rec);

            Assert.NotNull(selected);
            Assert.Equal("StartSnapshot", selected.GetValue("name"));
        }

        [Fact]
        public void GetGhostSnapshot_BothNull_ReturnsNull()
        {
            var rec = new Recording();

            Assert.Null(GhostVisualBuilder.GetGhostSnapshot(rec));
        }

        [Fact]
        public void GetGhostSnapshot_NullRecording_ReturnsNull()
        {
            Assert.Null(GhostVisualBuilder.GetGhostSnapshot(null));
        }

        [Fact]
        public void GetGhostSnapshot_OnlyVesselSnapshot_ReturnsFallback()
        {
            var rec = new Recording();
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("name", "VesselFallback");
            rec.VesselSnapshot = vessel;

            ConfigNode selected = GhostVisualBuilder.GetGhostSnapshot(rec);

            Assert.NotNull(selected);
            Assert.Equal("VesselFallback", selected.GetValue("name"));
        }

        [Fact]
        public void GroupColorChangersByPartId_NullList_ReturnsEmptyDictionary()
        {
            Dictionary<uint, List<ColorChangerGhostInfo>> grouped =
                GhostVisualBuilder.GroupColorChangersByPartId(null);

            Assert.NotNull(grouped);
            Assert.Empty(grouped);
        }

        [Fact]
        public void GroupColorChangersByPartId_GroupsByPersistentId_AndPreservesEncounterOrder()
        {
            var first = new ColorChangerGhostInfo { partPersistentId = 100u, shaderProperty = "_EmissiveColor" };
            var second = new ColorChangerGhostInfo { partPersistentId = 200u, shaderProperty = "_BurnColor" };
            var third = new ColorChangerGhostInfo { partPersistentId = 100u, shaderProperty = "_Color" };

            Dictionary<uint, List<ColorChangerGhostInfo>> grouped =
                GhostVisualBuilder.GroupColorChangersByPartId(new List<ColorChangerGhostInfo> { first, second, third });

            Assert.Equal(2, grouped.Count);
            Assert.Same(first, grouped[100u][0]);
            Assert.Same(third, grouped[100u][1]);
            Assert.Same(second, grouped[200u][0]);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData(" fx_exhaustFlame_blue(Clone) ", "fx_exhaustFlame_blue")]
        [InlineData("fx_smokeTrail_light", "fx_smokeTrail_light")]
        [InlineData("fx_test(Clone)_extra", "fx_test(Clone)_extra")]
        public void NormalizeFxPrefabName_TrimsAndStripsOnlyTrailingCloneSuffix(string rawName, string expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.NormalizeFxPrefabName(rawName));
        }

        [Fact]
        public void TryGetSnapshotRootPartInfo_UsesSnapshotRootIndex()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("root", "1");
            snapshot.AddValue("CoM", "0,0,0");

            var nonRoot = snapshot.AddNode("PART");
            nonRoot.AddValue("name", "probeCoreSphere");
            nonRoot.AddValue("persistentId", "10");
            nonRoot.AddValue("parent", "0");
            nonRoot.AddValue("position", "0,0,0");
            nonRoot.AddValue("rotation", "0,0,0,1");

            var root = snapshot.AddNode("PART");
            root.AddValue("name", "fuelTank");
            root.AddValue("persistentId", "42");
            root.AddValue("parent", "0");
            root.AddValue("position", "1,2,3");
            root.AddValue("rotation", "0.1,0.2,0.3,0.9");

            bool parsed = GhostVisualBuilder.TryGetSnapshotRootPartInfo(
                snapshot,
                out string partName,
                out uint persistentId,
                out Vector3 localPosition,
                out Quaternion localRotation);

            Assert.True(parsed);
            Assert.Equal("fuelTank", partName);
            Assert.Equal((uint)42, persistentId);
            AssertVector3Close(new Vector3(1f, 2f, 3f), localPosition);
            AssertQuaternionClose(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), localRotation);
        }

        [Fact]
        public void TryGetSnapshotRootPartInfo_InvalidRootFallsBackToFirstPartAndSupportsPartAlias()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("root", "99");

            var root = snapshot.AddNode("PART");
            root.AddValue("part", "mk1pod.v2_12345");
            root.AddValue("persistentId", "77");
            root.AddValue("pos", "4,5,6");
            root.AddValue("rot", "0,0,0,1");

            snapshot.AddNode("PART").AddValue("name", "ignoredPart");

            bool parsed = GhostVisualBuilder.TryGetSnapshotRootPartInfo(
                snapshot,
                out string partName,
                out uint persistentId,
                out Vector3 localPosition,
                out Quaternion localRotation);

            Assert.True(parsed);
            Assert.Equal("mk1pod.v2", partName);
            Assert.Equal((uint)77, persistentId);
            AssertVector3Close(new Vector3(4f, 5f, 6f), localPosition);
            AssertQuaternionClose(Quaternion.identity, localRotation);
        }

        [Fact]
        public void TryGetSnapshotCenterOfMass_MissingValue_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");

            bool parsed = GhostVisualBuilder.TryGetSnapshotCenterOfMass(snapshot, out Vector3 centerOfMass);

            Assert.False(parsed);
            AssertVector3Close(Vector3.zero, centerOfMass);
        }

        [Fact]
        public void TryGetSnapshotCenterOfMass_ParsesCoMValue()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("CoM", "1,2,3");

            bool parsed = GhostVisualBuilder.TryGetSnapshotCenterOfMass(snapshot, out Vector3 centerOfMass);

            Assert.True(parsed);
            AssertVector3Close(new Vector3(1f, 2f, 3f), centerOfMass);
        }

        [Fact]
        public void TryTriggerStockExplosionFx_ReturnsFalseWhenFxMongerIsUnavailable()
        {
            bool explodeCalled = false;

            bool queued = GhostVisualBuilder.TryTriggerStockExplosionFx(
                Vector3.zero,
                0.25,
                out string failureReason,
                isFxMongerAvailable: () => false,
                explode: (pos, power) => explodeCalled = true);

            Assert.False(queued);
            Assert.False(explodeCalled);
            Assert.Equal("no live FXMonger instance", failureReason);
        }

        [Fact]
        public void TryTriggerStockExplosionFx_ReturnsFalseWhenStockExplodeThrows()
        {
            bool explodeCalled = false;

            bool queued = GhostVisualBuilder.TryTriggerStockExplosionFx(
                Vector3.one,
                0.5,
                out string failureReason,
                isFxMongerAvailable: () => true,
                explode: (pos, power) =>
                {
                    explodeCalled = true;
                    throw new System.InvalidOperationException("boom");
                });

            Assert.False(queued);
            Assert.True(explodeCalled);
            Assert.Equal("boom", failureReason);
        }

        [Fact]
        public void TryTriggerStockExplosionFx_ReturnsTrueWhenStockExplodeRuns()
        {
            Vector3 capturedPosition = Vector3.zero;
            double capturedPower = -1;

            bool queued = GhostVisualBuilder.TryTriggerStockExplosionFx(
                new Vector3(1f, 2f, 3f),
                0.75,
                out string failureReason,
                isFxMongerAvailable: () => true,
                explode: (pos, power) =>
                {
                    capturedPosition = pos;
                    capturedPower = power;
                });

            Assert.True(queued);
            Assert.Null(failureReason);
            Assert.Equal(new Vector3(1f, 2f, 3f), capturedPosition);
            Assert.Equal(0.75, capturedPower, 6);
        }

        private static void AssertVector3Close(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
        {
            Assert.InRange(Mathf.Abs(expected.x - actual.x), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.y - actual.y), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.z - actual.z), 0f, epsilon);
        }

        private static void AssertQuaternionClose(Quaternion expected, Quaternion actual, float epsilon = 1e-4f)
        {
            Assert.InRange(Mathf.Abs(expected.x - actual.x), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.y - actual.y), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.z - actual.z), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.w - actual.w), 0f, epsilon);
        }
    }
}
