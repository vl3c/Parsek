using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the renderer-level structural diff (<see cref="PartParityStructure"/>):
    /// the ghost-name mapping against the names <c>GhostVisualBuilder</c> really builds, the
    /// pairing by path (duplicates included), and every diff list with its tolerance.
    /// </summary>
    public class PartParityStructureTests
    {
        private static PartParityRendererRecord Rec(string path, string mesh = "m", bool rendered = true,
            double[] pos = null, double[] rot = null, double[] scale = null,
            string material = "KSP/Bumped Specular mat", string shader = "KSP/Bumped Specular",
            string texture = "tex", int vertices = 100, string kind = "MeshRenderer")
        {
            return new PartParityRendererRecord
            {
                Path = path,
                Kind = kind,
                Mesh = mesh,
                VertexCount = vertices,
                Materials = new[] { material },
                Shaders = new[] { shader },
                MainTextures = new[] { texture },
                Rendered = rendered,
                LocalPosition = pos ?? new[] { 0.0, 0.0, 0.0 },
                LocalRotation = rot ?? new[] { 0.0, 0.0, 0.0, 1.0 },
                LocalScale = scale ?? new[] { 1.0, 1.0, 1.0 },
            };
        }

        // ----- naming -----

        [Theory]
        // The co-located ghost under the real part: <ghostRoot>/ghost_visuals/ghost_part_<pid>/<model>/...
        [InlineData("PartParity mk1pod--default/ghost_visuals/ghost_part_100000/model/mk1pod(Clone)/Hatch",
            "model/mk1pod/Hatch")]
        [InlineData("ghost_visuals/ghost_part_4123456789/model/Squad-Parts(Clone)/obj_base", "model/Squad-Parts/obj_base")]
        // The pid label falls back to the part name when the pid is 0.
        [InlineData("root/ghost_visuals/ghost_part_mk1pod.v2/model/a/b", "model/a/b")]
        // EVA model root.
        [InlineData("ghost_part_7/model01/body01", "model01/body01")]
        // A real path is only stripped of its clone suffixes.
        [InlineData("model/mk1pod(Clone)/Hatch", "model/mk1pod/Hatch")]
        [InlineData("model/x (Clone)(Clone)/y", "model/x/y")]
        [InlineData("model//a/", "model/a")]
        [InlineData("", "")]
        public void NormalizePath_MapsGhostNamesBackToThePartPath(string raw, string expected)
        {
            Assert.Equal(expected, PartParityStructure.NormalizePath(raw));
        }

        [Fact]
        public void GhostAndRealPathsOfOneRenderer_NormalizeAlike()
        {
            string real = "model/fuelTankSmallFlat(Clone)/Tank";
            string ghost = "PartParity fueltank/ghost_visuals/ghost_part_100000/model/fuelTankSmallFlat(Clone)/Tank";
            Assert.Equal(PartParityStructure.NormalizePath(real), PartParityStructure.NormalizePath(ghost));
        }

        [Theory]
        [InlineData("PartParity x/ghost_visuals/ghost_part_1/model/a", true)]
        [InlineData("ghost_part_1/model", true)]
        [InlineData("x/ghost_visuals", true)]
        [InlineData("model/a/b", false)]
        [InlineData("model/ghostly_part/b", false)]
        [InlineData("", false)]
        public void IsGhostSubtreePath(string raw, bool expected)
        {
            Assert.Equal(expected, PartParityStructure.IsGhostSubtreePath(raw));
        }

        [Theory]
        [InlineData("KSP/Bumped Specular (Instance)", "KSP/Bumped Specular")]
        [InlineData("mat (Instance) (Instance)", "mat")]
        [InlineData("mat(Clone) (Instance)", "mat")]
        [InlineData("mat", "mat")]
        [InlineData(null, "")]
        public void NormalizeAssetName(string raw, string expected)
        {
            Assert.Equal(expected, PartParityStructure.NormalizeAssetName(raw));
        }

        // ----- pairing -----

        [Fact]
        public void IdenticalSides_PairEverythingAndListNothing()
        {
            var real = new List<PartParityRendererRecord> { Rec("model/a"), Rec("model/b") };
            var ghost = new List<PartParityRendererRecord> { Rec("model/a"), Rec("model/b") };
            var d = PartParityStructure.Diff(real, ghost);
            Assert.Equal(2, d.Pairs);
            Assert.Equal(2, d.RealRenderers);
            Assert.Equal(2, d.GhostRenderers);
            Assert.Empty(d.MissingOnGhost);
            Assert.Empty(d.ExtraOnGhost);
            Assert.Empty(d.TransformDiffers);
            Assert.Empty(d.MaterialDiffers);
            Assert.Empty(d.MeshDiffers);
            Assert.Empty(d.RenderedDiffers);
        }

        [Fact]
        public void MissingAndExtra_AreNamedWithMeshAndRenderedFlag()
        {
            var real = new List<PartParityRendererRecord> { Rec("model/a"), Rec("model/skirt", "skirtMesh") };
            var ghost = new List<PartParityRendererRecord> { Rec("model/a"), Rec("fairing_panels/p0", "panel", rendered: false) };
            var d = PartParityStructure.Diff(real, ghost);
            Assert.Equal(1, d.Pairs);
            var missing = Assert.Single(d.MissingOnGhost);
            Assert.Equal("model/skirt", missing.Key);
            Assert.Equal("skirtMesh", missing.Mesh);
            Assert.True(missing.Rendered);
            var extra = Assert.Single(d.ExtraOnGhost);
            Assert.Equal("fairing_panels/p0", extra.Key);
            Assert.False(extra.Rendered);
        }

        [Fact]
        public void DuplicatePaths_PairInOrderAndKeyTheRest()
        {
            var real = new List<PartParityRendererRecord> { Rec("model/Cylinder", "c0"), Rec("model/Cylinder", "c1") };
            var ghost = new List<PartParityRendererRecord> { Rec("model/Cylinder", "c0") };
            var d = PartParityStructure.Diff(real, ghost);
            Assert.Equal(1, d.Pairs);
            Assert.Equal("model/Cylinder#1", Assert.Single(d.MissingOnGhost).Key);
            Assert.Empty(d.MeshDiffers);
        }

        [Fact]
        public void NullListsAndNullRecords_AreEmpty()
        {
            var d = PartParityStructure.Diff(null, new List<PartParityRendererRecord> { null });
            Assert.Equal(0, d.Pairs);
            Assert.Equal(0, d.GhostRenderers);
            Assert.Empty(d.ExtraOnGhost);
        }

        // ----- transform -----

        [Theory]
        [InlineData(0.0005, 0.0, 0.0, false)]   // 0.5 mm
        [InlineData(0.002, 0.0, 0.0, true)]     // 2 mm
        [InlineData(0.0, 0.4, 0.0, false)]      // 0.4 deg
        [InlineData(0.0, 1.0, 0.0, true)]       // 1 deg
        [InlineData(0.0, 0.0, 0.005, false)]    // 0.5 %
        [InlineData(0.0, 0.0, 0.02, true)]      // 2 %
        public void TransformTolerances(double dPos, double dRotDeg, double dScale, bool differs)
        {
            double half = dRotDeg * System.Math.PI / 360.0;
            var real = new List<PartParityRendererRecord> { Rec("model/a") };
            var ghost = new List<PartParityRendererRecord>
            {
                Rec("model/a",
                    pos: new[] { dPos, 0.0, 0.0 },
                    rot: new[] { 0.0, System.Math.Sin(half), 0.0, System.Math.Cos(half) },
                    scale: new[] { 1.0, 1.0 + dScale, 1.0 }),
            };
            var d = PartParityStructure.Diff(real, ghost);
            Assert.Equal(differs, d.TransformDiffers.Count == 1);
            if (differs)
            {
                var t = d.TransformDiffers[0];
                Assert.Equal(dPos, t.PositionDeltaM, 9);
                Assert.Equal(dRotDeg, t.RotationDeltaDeg, 6);
                Assert.Equal(dScale * 100.0, t.ScaleDeltaPct, 6);
            }
        }

        [Fact]
        public void QuaternionAndItsNegation_AreTheSameRotation()
        {
            Assert.Equal(0.0, PartParityStructure.QuaternionAngleDeg(
                new[] { 0.0, 0.0, 0.0, 1.0 }, new[] { 0.0, 0.0, 0.0, -1.0 }), 9);
            double s = System.Math.Sqrt(0.5);
            Assert.Equal(90.0, PartParityStructure.QuaternionAngleDeg(
                new[] { 0.0, 0.0, 0.0, 1.0 }, new[] { 0.0, s, 0.0, s }), 6);
        }

        [Fact]
        public void MissingPose_CountsAsDiffering()
        {
            var real = new List<PartParityRendererRecord> { Rec("model/a") };
            var g = Rec("model/a");
            g.LocalRotation = null;
            var d = PartParityStructure.Diff(real, new List<PartParityRendererRecord> { g });
            var t = Assert.Single(d.TransformDiffers);
            Assert.True(double.IsNaN(t.RotationDeltaDeg));
        }

        [Fact]
        public void ZeroRealScale_ComparesAbsolutely()
        {
            Assert.Equal(0.0, PartParityStructure.ScaleDeltaPct(new[] { 0.0, 1.0, 1.0 }, new[] { 0.0, 1.0, 1.0 }), 9);
            Assert.Equal(50.0, PartParityStructure.ScaleDeltaPct(new[] { 0.0, 1.0, 1.0 }, new[] { 0.5, 1.0, 1.0 }), 9);
        }

        // ----- material, mesh, rendered -----

        [Fact]
        public void InstanceMaterial_MatchesTheSharedOne()
        {
            var real = new List<PartParityRendererRecord> { Rec("model/a", material: "tankMat (Instance)") };
            var ghost = new List<PartParityRendererRecord> { Rec("model/a", material: "tankMat") };
            Assert.Empty(PartParityStructure.Diff(real, ghost).MaterialDiffers);
        }

        [Fact]
        public void ShaderAndTextureDifferences_AreListedPerField()
        {
            var real = new List<PartParityRendererRecord> { Rec("model/a", shader: "KSP/Bumped", texture: "white") };
            var ghost = new List<PartParityRendererRecord> { Rec("model/a", shader: "KSP/Diffuse", texture: "black") };
            var d = PartParityStructure.Diff(real, ghost);
            Assert.Equal(new[] { "shaders", "mainTextures" }, d.MaterialDiffers.Select(f => f.Field).ToArray());
            Assert.Equal("KSP/Bumped", d.MaterialDiffers[0].Real);
            Assert.Equal("KSP/Diffuse", d.MaterialDiffers[0].Ghost);
        }

        [Fact]
        public void MeshKindAndVertexDifferences_AreListed()
        {
            var real = new List<PartParityRendererRecord> { Rec("model/a", mesh: "m1", vertices: 100) };
            var ghost = new List<PartParityRendererRecord>
                { Rec("model/a", mesh: "m2", vertices: 120, kind: "SkinnedMeshRenderer") };
            var d = PartParityStructure.Diff(real, ghost);
            Assert.Equal(new[] { "kind", "mesh", "vertexCount" }, d.MeshDiffers.Select(f => f.Field).ToArray());
            Assert.Equal("100", d.MeshDiffers[2].Real);
            Assert.Equal("120", d.MeshDiffers[2].Ghost);
        }

        [Fact]
        public void RealHiddenGhostShown_IsARenderedDiff()
        {
            // The P0 finding: stock hides the shroud of an engine with nothing below, the
            // ghost draws it. The renderer exists on both sides; only "rendered" differs.
            var real = new List<PartParityRendererRecord> { Rec("model/shroud", rendered: false) };
            var ghost = new List<PartParityRendererRecord> { Rec("model/shroud", rendered: true) };
            var d = PartParityStructure.Diff(real, ghost);
            var r = Assert.Single(d.RenderedDiffers);
            Assert.Equal("model/shroud", r.Key);
            Assert.Equal("false", r.Real);
            Assert.Equal("true", r.Ghost);
            Assert.Empty(d.MissingOnGhost);
        }
    }
}
