using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The framing rule (<see cref="PartParityFramingRule"/>, design 4.2 step 1): the shared box
    /// is the ghost box, the PAIRED real renderers and every UNPAIRED real renderer inside twice
    /// the paired box's extent; the rest are dropped and named (the P0 ReStock LV-T45, whose 18
    /// extra real renderers blew a 2 m part's box to 10 m).
    /// </summary>
    public class PartParityFramingRuleTests
    {
        private static PartParityRendererRecord Rec(string path, double[] min, double[] max, bool rendered = true, string mesh = "m")
        {
            return new PartParityRendererRecord
            {
                Path = path, Kind = "MeshRenderer", Mesh = mesh, VertexCount = 10,
                Materials = new[] { "mat" }, Shaders = new[] { "s" }, MainTextures = new[] { "t" },
                Rendered = rendered,
                LocalPosition = new[] { 0.0, 0.0, 0.0 }, LocalRotation = new[] { 0.0, 0.0, 0.0, 1.0 },
                LocalScale = new[] { 1.0, 1.0, 1.0 },
                BoundsMin = min, BoundsMax = max,
            };
        }

        private static double[] V(double x, double y, double z) => new[] { x, y, z };

        [Fact]
        public void FarUnpairedRealRenderer_IsDroppedAndNamed()
        {
            // A 2 m engine both sides have, plus a 10 m plume mesh only the real side has.
            var real = new List<PartParityRendererRecord>
            {
                Rec("model/engine", V(-0.6, -1, -0.6), V(0.6, 1, 0.6)),
                Rec("model/plume", V(-5, -10, -5), V(5, 0, 5), mesh: "plume"),
            };
            var ghost = new List<PartParityRendererRecord> { Rec("model/engine", V(-0.6, -1, -0.6), V(0.6, 1, 0.6)) };
            PartParityFramingDecision d = PartParityFramingRule.Decide(real, ghost);
            Assert.Single(d.Dropped);
            Assert.Equal("model/plume", d.Dropped[0].Key);
            Assert.Equal(V(-0.6, -1, -0.6), d.Shared.Min);
            Assert.Equal(V(0.6, 1, 0.6), d.Shared.Max);
            Assert.Single(d.Structure.MissingOnGhost); // the diff still names it
            Assert.False(d.GhostOutsideReal);
        }

        [Fact]
        public void NearUnpairedRealRenderer_IsKept()
        {
            // A shroud only the real side draws, inside twice the paired box: it frames.
            var real = new List<PartParityRendererRecord>
            {
                Rec("model/engine", V(-0.6, -1, -0.6), V(0.6, 1, 0.6)),
                Rec("model/shroud", V(-0.9, -1.5, -0.9), V(0.9, 0, 0.9), mesh: "shroud"),
            };
            var ghost = new List<PartParityRendererRecord> { Rec("model/engine", V(-0.6, -1, -0.6), V(0.6, 1, 0.6)) };
            PartParityFramingDecision d = PartParityFramingRule.Decide(real, ghost);
            Assert.Empty(d.Dropped);
            Assert.Equal(V(-0.9, -1.5, -0.9), d.Shared.Min);
        }

        [Fact]
        public void GhostOnlyMesh_JoinsTheSharedBox_AndIsFlaggedOutside()
        {
            var real = new List<PartParityRendererRecord> { Rec("model/tank", V(-1, -1, -1), V(1, 1, 1)) };
            var ghost = new List<PartParityRendererRecord>
            {
                Rec("model/tank", V(-1, -1, -1), V(1, 1, 1)),
                Rec("fairing_panels/p0", V(-1, 1, -1), V(1, 3, 1)),
            };
            PartParityFramingDecision d = PartParityFramingRule.Decide(real, ghost);
            Assert.Equal(3.0, d.Shared.Max[1]);
            Assert.True(d.GhostOutsideReal);
        }

        [Fact]
        public void NothingPaired_UsesTheGhostBoxAsReference()
        {
            var real = new List<PartParityRendererRecord>
            {
                Rec("model/a", V(-1, -1, -1), V(1, 1, 1)),
                Rec("model/far", V(50, 50, 50), V(51, 51, 51)),
            };
            var ghost = new List<PartParityRendererRecord> { Rec("other/b", V(-1, -1, -1), V(1, 1, 1)) };
            PartParityFramingDecision d = PartParityFramingRule.Decide(real, ghost);
            Assert.True(d.PairedReal.IsEmpty);
            Assert.Single(d.Dropped);
            Assert.Equal("model/far", d.Dropped[0].Key);
        }

        [Fact]
        public void NoGhostAtAll_KeepsEveryRealRenderer()
        {
            var real = new List<PartParityRendererRecord>
            {
                Rec("model/a", V(-1, -1, -1), V(1, 1, 1)),
                Rec("model/far", V(50, 50, 50), V(51, 51, 51)),
            };
            PartParityFramingDecision d = PartParityFramingRule.Decide(real, new List<PartParityRendererRecord>());
            Assert.Empty(d.Dropped);
            Assert.Equal(51.0, d.Shared.Max[0]);
        }

        [Fact]
        public void NotRenderedOrBoundlessRecords_NeverFrame()
        {
            var real = new List<PartParityRendererRecord>
            {
                Rec("model/a", V(-1, -1, -1), V(1, 1, 1)),
                Rec("model/hidden", V(-9, -9, -9), V(9, 9, 9), rendered: false),
                Rec("model/nobounds", null, null),
            };
            PartParityFramingDecision d = PartParityFramingRule.Decide(real, real);
            Assert.Equal(V(-1, -1, -1), d.Shared.Min);
            Assert.Empty(d.Dropped);
        }

        [Fact]
        public void FlatReference_StillKeepsAMeshJustOffItsPlane()
        {
            // A flat plate (zero thickness): 2x of a zero extent would drop everything off the
            // plane, so each half-extent is at least the reference's largest.
            var real = new List<PartParityRendererRecord>
            {
                Rec("model/plate", V(-1, 0, -1), V(1, 0, 1)),
                Rec("model/bolt", V(-0.1, 0.1, -0.1), V(0.1, 0.3, 0.1), mesh: "bolt"),
            };
            var ghost = new List<PartParityRendererRecord> { Rec("model/plate", V(-1, 0, -1), V(1, 0, 1)) };
            Assert.Empty(PartParityFramingRule.Decide(real, ghost).Dropped);
        }

        [Fact]
        public void GhostOutside_ToleratesTwoPercent()
        {
            var real = PartParityAabb.FromMinMax(V(-1, -1, -1), V(1, 1, 1));
            Assert.False(PartParityFramingRule.IsGhostOutside(real, PartParityAabb.FromMinMax(V(-1.03, -1, -1), V(1, 1, 1))));
            Assert.True(PartParityFramingRule.IsGhostOutside(real, PartParityAabb.FromMinMax(V(-1.05, -1, -1), V(1, 1, 1))));
            Assert.False(PartParityFramingRule.IsGhostOutside(real, new PartParityAabb()));
            Assert.True(PartParityFramingRule.IsGhostOutside(new PartParityAabb(), real));
        }

        [Fact]
        public void DroppedFlag_IsInvariantCulture()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string flag = PartParityFramingRule.DroppedFlag(new PartParityDroppedRenderer { Key = "model/plume#2", Size = V(10, 9.5, 0.25) });
                Assert.Equal("framing-dropped:model/plume#2=10.000x9.500x0.250", flag);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [Fact]
        public void Aabb_RejectsNonFiniteCorners()
        {
            Assert.True(PartParityAabb.FromMinMax(V(double.NaN, 0, 0), V(1, 1, 1)).IsEmpty);
            Assert.True(PartParityAabb.FromMinMax(null, V(1, 1, 1)).IsEmpty);
            var b = PartParityAabb.FromMinMax(V(1, 1, 1), V(-1, -1, -1));
            Assert.Equal(V(-1, -1, -1), b.Min);
        }
    }
}
