using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FairingVisualTests : System.IDisposable
    {
        public FairingVisualTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ResolveFairingVisualState_DeployedSnapshotWithShowMesh_ParsesBothFlags()
        {
            var partNode = CreateFairingPartNode("st_flight_deployed", true);

            var state = GhostVisualBuilder.ResolveFairingVisualState(partNode);

            Assert.True(state.isDeployed);
            Assert.True(state.showMesh);
        }

        [Fact]
        public void ResolveFairingVisualState_IntactSnapshotWithShowMeshDisabled_ParsesBothFlags()
        {
            var partNode = CreateFairingPartNode("st_idle", false);

            var state = GhostVisualBuilder.ResolveFairingVisualState(partNode);

            Assert.False(state.isDeployed);
            Assert.False(state.showMesh);
        }

        [Fact]
        public void GetFairingStructureSegmentCount_UsesConfigRootObjects()
        {
            var partConfig = CreateFairingPartConfig(capCount: 4, trussCount: 4);

            int segmentCount = GhostVisualBuilder.GetFairingStructureSegmentCount(partConfig);

            Assert.Equal(4, segmentCount);
        }

        [Fact]
        public void GenerateFairingTrussMesh_RepresentativeShape_ProducesFiniteIndexedGeometry()
        {
            var sections = new List<(float h, float r)>
            {
                (0f, 0.9375f),
                (1.28653526f, 0.9375f),
                (2.78717613f, 0.2f)
            };

            var geometry = GhostVisualBuilder.GenerateFairingTrussGeometry(
                sections, 6, Vector3.zero, Vector3.up);

            Assert.True(geometry.vertices.Count > 0);
            Assert.Equal(geometry.vertices.Count, geometry.uvs.Count);
            Assert.NotEmpty(geometry.triangles);
            foreach (int triangleIndex in geometry.triangles)
                Assert.InRange(triangleIndex, 0, geometry.vertices.Count - 1);
            foreach (Vector3 vertex in geometry.vertices)
                Assert.True(IsFinite(vertex));
        }

        [Fact]
        public void TransformAlongAxis_UpAxis_PreservesIdentityOrientation()
        {
            var transformed = GhostVisualBuilder.TransformAlongAxis(
                new Vector3(1f, 2f, 3f), Vector3.up, Vector3.zero);

            AssertVectorApproximatelyEqual(new Vector3(1f, 2f, 3f), transformed);
        }

        [Fact]
        public void TransformAlongAxis_ForwardAxis_MapsUpToAxisWithoutReflection()
        {
            Vector3 right = GhostVisualBuilder.TransformAlongAxis(Vector3.right, Vector3.forward, Vector3.zero);
            Vector3 up = GhostVisualBuilder.TransformAlongAxis(Vector3.up, Vector3.forward, Vector3.zero);
            Vector3 forward = GhostVisualBuilder.TransformAlongAxis(Vector3.forward, Vector3.forward, Vector3.zero);

            AssertVectorApproximatelyEqual(Vector3.forward, up);
            Assert.True(Dot(Cross(right, up), forward) > 0.99f);
        }

        [Fact]
        public void ComputeFairingVisualActivation_Deployed_HidesShellAndShowsTruss()
        {
            var visibility = GhostVisualBuilder.ComputeFairingVisualActivation(deployed: true);

            Assert.False(visibility.shellVisible);
            Assert.True(visibility.trussVisible);
        }

        [Fact]
        public void ComputeFairingVisualActivation_Intact_ShowsShellAndHidesTruss()
        {
            var visibility = GhostVisualBuilder.ComputeFairingVisualActivation(deployed: false);

            Assert.True(visibility.shellVisible);
            Assert.False(visibility.trussVisible);
        }

        private static ConfigNode CreateFairingPartNode(string fsmState, bool showMesh)
        {
            var partNode = new ConfigNode("PART");
            var fairingModule = partNode.AddNode("MODULE");
            fairingModule.AddValue("name", "ModuleProceduralFairing");
            fairingModule.AddValue("fsm", fsmState);

            var toggleModule = partNode.AddNode("MODULE");
            toggleModule.AddValue("name", "ModuleStructuralNodeToggle");
            toggleModule.AddValue("showMesh", showMesh);
            return partNode;
        }

        private static ConfigNode CreateFairingPartConfig(int capCount, int trussCount)
        {
            var partConfig = new ConfigNode("PART");
            for (int i = 1; i <= capCount; i++)
            {
                var module = partConfig.AddNode("MODULE");
                module.AddValue("name", "ModuleStructuralNode");
                module.AddValue("rootObject", $"Cap{i}");
            }

            for (int i = 1; i <= trussCount; i++)
            {
                var module = partConfig.AddNode("MODULE");
                module.AddValue("name", "ModuleStructuralNode");
                module.AddValue("rootObject", $"Truss{i}");
            }

            return partConfig;
        }

        private static bool IsFinite(Vector3 vector)
        {
            return !float.IsNaN(vector.x) && !float.IsInfinity(vector.x) &&
                   !float.IsNaN(vector.y) && !float.IsInfinity(vector.y) &&
                   !float.IsNaN(vector.z) && !float.IsInfinity(vector.z);
        }

        private static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);
        }

        private static float Dot(Vector3 a, Vector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        private static void AssertVectorApproximatelyEqual(Vector3 expected, Vector3 actual, float tolerance = 1e-4f)
        {
            Assert.InRange(actual.x, expected.x - tolerance, expected.x + tolerance);
            Assert.InRange(actual.y, expected.y - tolerance, expected.y + tolerance);
            Assert.InRange(actual.z, expected.z - tolerance, expected.z + tolerance);
        }
    }
}
