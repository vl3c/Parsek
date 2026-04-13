using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostVisualFrameTests
    {
        [Fact]
        public void ComputeSnapshotVisualRootLocalOffset_DebrisUsesNegativeSnapshotCom()
        {
            var snapshot = BuildSnapshot("1.25,-2.5,3.75");
            var traj = new MockTrajectory { IsDebris = true };

            Vector3 offset = GhostVisualBuilder.ComputeSnapshotVisualRootLocalOffset(traj, snapshot);

            AssertVector3Close(new Vector3(-1.25f, 2.5f, -3.75f), offset);
        }

        [Fact]
        public void ComputeSnapshotVisualRootLocalOffset_NonDebrisLeavesVisualsUnshifted()
        {
            var snapshot = BuildSnapshot("1.25,-2.5,3.75");
            var traj = new MockTrajectory { IsDebris = false };

            Vector3 offset = GhostVisualBuilder.ComputeSnapshotVisualRootLocalOffset(traj, snapshot);

            AssertVector3Close(Vector3.zero, offset);
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
        public void TryGetSnapshotCenterOfMass_MissingValue_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");

            bool parsed = GhostVisualBuilder.TryGetSnapshotCenterOfMass(snapshot, out Vector3 centerOfMass);

            Assert.False(parsed);
            AssertVector3Close(Vector3.zero, centerOfMass);
        }

        private static ConfigNode BuildSnapshot(string coM)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("root", "0");
            snapshot.AddValue("CoM", coM);

            var part = snapshot.AddNode("PART");
            part.AddValue("name", "mk1pod.v2");
            part.AddValue("persistentId", "1");
            part.AddValue("parent", "0");
            part.AddValue("position", "0,0,0");
            part.AddValue("rotation", "0,0,0,1");

            return snapshot;
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
