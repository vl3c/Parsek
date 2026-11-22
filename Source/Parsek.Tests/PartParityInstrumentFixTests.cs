using System;
using System.Collections.Generic;
using System.IO;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The part parity sweep's instrument fixes of 2026-11-22 (design-part-ghost-parity.md
    /// D.3 / D.4): the canopy lever arms the structure writes for the air cap
    /// (PARTPARITY-CANOPY-CAP-LEVER-ARM) and the real-side colour-changer leak repair's pure
    /// rule (PARTPARITY-STACKED-SHROUD-TOP-COLOR), plus source gates on their live wiring.
    /// </summary>
    public class PartParityInstrumentFixTests
    {
        private static PartParityRendererRecord Rec(string path, double x, double y, double z)
            => new PartParityRendererRecord
            {
                Path = path, Kind = "MeshRenderer", Mesh = "m", Rendered = true,
                Materials = new[] { "mat" }, Shaders = new[] { "s" }, MainTextures = new[] { "t" },
                LocalPosition = new[] { x, y, z }, LocalRotation = new[] { 0.0, 0.0, 0.0, 1.0 },
                LocalScale = new[] { 1.0, 1.0, 1.0 },
            };

        [Fact]
        public void IsCanopyRendererKey_MatchesAPathSegment_NotAPrefix()
        {
            Assert.True(PartParityStructure.IsCanopyRendererKey("model/canopy", "canopy"));
            Assert.True(PartParityStructure.IsCanopyRendererKey("model/canopy/Canopy004#2", "canopy"));
            Assert.False(PartParityStructure.IsCanopyRendererKey("model/canopyCover", "canopy"));
            Assert.False(PartParityStructure.IsCanopyRendererKey("model/housing#canopy", "canopy"));
            Assert.False(PartParityStructure.IsCanopyRendererKey("model/a", ""));
            Assert.False(PartParityStructure.IsCanopyRendererKey(null, "canopy"));
        }

        [Fact]
        public void StampCanopyPivotDistances_WritesTheLeverArm_OnCanopyEntriesOnly()
        {
            // A ReStock-shaped canopy: the pivot at (0, 1, 0), its renderer 4 m above it, a
            // housing that is not under the canopy.
            var real = new List<PartParityRendererRecord>
            {
                Rec("model/canopy/Canopy004", 0.0, 5.0, 0.0),
                Rec("model/canopy/Canopy004", 3.0, 1.0, 4.0),
                Rec("model/housing", 0.0, 0.0, 0.0),
            };
            var ghost = new List<PartParityRendererRecord>
            {
                Rec("model/canopy/Canopy004", 0.1, 5.0, 0.0),
                Rec("model/canopy/Canopy004", 3.0, 1.2, 4.0),
                Rec("model/housing", 0.0, 0.0, 0.05),
            };
            PartParityStructureDiff d = PartParityStructure.Diff(real, ghost);
            Assert.Equal(3, d.TransformDiffers.Count);
            int n = PartParityStructure.StampCanopyPivotDistances(d, real, "canopy", new[] { 0.0, 1.0, 0.0 });
            Assert.Equal(2, n);
            Assert.Equal(4.0, d.TransformDiffers.Find(t => t.Key == "model/canopy/Canopy004").CanopyPivotDistanceM, 9);
            Assert.Equal(5.0, d.TransformDiffers.Find(t => t.Key == "model/canopy/Canopy004#1").CanopyPivotDistanceM, 9);
            Assert.True(double.IsNaN(d.TransformDiffers.Find(t => t.Key == "model/housing").CanopyPivotDistanceM));
            // No canopy transform or no pivot: nothing written.
            PartParityStructureDiff d2 = PartParityStructure.Diff(real, ghost);
            Assert.Equal(0, PartParityStructure.StampCanopyPivotDistances(d2, real, null, new[] { 0.0, 1.0, 0.0 }));
            Assert.Equal(0, PartParityStructure.StampCanopyPivotDistances(d2, real, "canopy", null));
            Assert.All(d2.TransformDiffers, t => Assert.True(double.IsNaN(t.CanopyPivotDistanceM)));
        }

        [Fact]
        public void TransformDiffKeys_CarryTheLeverArm_WrittenNullWhenUnstamped()
        {
            Assert.Contains("canopyPivotDistanceM", PartParityJson.TransformDiffKeys);
            var d = new PartParityStructureDiff();
            d.TransformDiffers.Add(new PartParityTransformDiff { Key = "a/canopy", PositionDeltaM = 0.2, RotationDeltaDeg = 1.0, ScaleDeltaPct = 0.0, CanopyPivotDistanceM = 4.5 });
            d.TransformDiffers.Add(new PartParityTransformDiff { Key = "a/b", PositionDeltaM = 0.2, RotationDeltaDeg = 1.0, ScaleDeltaPct = 0.0 });
            string json = PartParityJson.StructureBlock(d);
            Assert.Contains("\"canopyPivotDistanceM\":4.5", json);
            Assert.Contains("\"canopyPivotDistanceM\":null", json);
        }

        [Fact]
        public void ColourLeak_IsTheModuleColourOnARendererWhoseMaterialDiffers()
        {
            double[] module = { 0.85, 0.85, 0.85, 1.0 };
            double[] white = { 1.0, 1.0, 1.0, 1.0 };
            Assert.True(PartParityColorChangerLeakRepair.IsLeak(new[] { 0.85, 0.85, 0.85, 1.0 }, module, white));
            // Not the module's colour (another writer, or an unset block): no leak.
            Assert.False(PartParityColorChangerLeakRepair.IsLeak(new[] { 0.0, 0.0, 0.0, 0.0 }, module, white));
            Assert.False(PartParityColorChangerLeakRepair.IsLeak(new[] { 0.80, 0.85, 0.85, 1.0 }, module, white));
            // The module colour equals the material's own: the leak changes nothing, left alone.
            Assert.False(PartParityColorChangerLeakRepair.IsLeak(module, module, module));
            Assert.False(PartParityColorChangerLeakRepair.IsLeak(null, module, white));
            Assert.False(PartParityColorChangerLeakRepair.IsLeak(new[] { double.NaN, 0.85, 0.85, 1.0 }, module, white));
            Assert.Equal("colorchanger-leak-repaired:2", PartParityColorChangerLeakRepair.Flag(2));
        }

        private static string Src(string file)
            => File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "Source", "Parsek", "TestCommands", file));

        [Fact]
        public void LiveWiring_RepairsLeaksBeforeTheRecords_AndStampsBothRoutes()
        {
            string capture = Src("PartParityCaseCapture.cs");
            // Both Prepare branches repair before anything reads the real renderers.
            int stateRepair = capture.IndexOf("RepairColorChangerLeaks(wc, wc.Real.rootPart, wc.State.JettisonTransforms);", StringComparison.Ordinal);
            int stateDriver = capture.IndexOf("PartParityStateDriver.Prepare(ctx, wc);", StringComparison.Ordinal);
            Assert.True(stateRepair > 0 && stateRepair < stateDriver);
            int stowedRepair = capture.IndexOf("RepairColorChangerLeaks(wc, part, null);", StringComparison.Ordinal);
            int stowedRecords = capture.IndexOf("wc.RealRecords = Records(partT, realRenderers", StringComparison.Ordinal);
            Assert.True(stowedRepair > 0 && stowedRepair < stowedRecords);
            Assert.Contains("PartParityStateDriver.StampCanopy(wc, wc.Framing.Structure, \"event\");", capture);
            Assert.Contains("StampCanopy(wc, run.StructureStart, \"start\");", Src("PartParityStateDriver.cs"));
            Assert.Contains("wc.State.CanopyPivotLocal = new double[]", Src("PartParityAirContext.cs"));
        }
    }
}
