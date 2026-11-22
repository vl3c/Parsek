using System;
using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The sweep's own JSON reader (<see cref="PartParityJsonReader"/>): plain values, and a
    /// sidecar written by <see cref="PartParityJson.WriteCase"/> read back with the fields the
    /// manifest and the control judge need.
    /// </summary>
    public class PartParityJsonReaderTests
    {
        [Fact]
        public void Parse_ReadsEveryValueKind()
        {
            object o = PartParityJsonReader.Parse("{\"a\":1.5,\"b\":[true,false,null],\"c\":\"x\\\"y\\u0041\\n\",\"d\":{},\"e\":-2e3}");
            Assert.Equal(1.5, PartParityJsonReader.Num(o, "a"));
            List<object> b = PartParityJsonReader.Arr(o, "b");
            Assert.Equal(new object[] { true, false, null }, b.ToArray());
            Assert.Equal("x\"yA\n", PartParityJsonReader.Str(o, "c"));
            Assert.NotNull(PartParityJsonReader.Obj(o, "d"));
            Assert.Equal(-2000.0, PartParityJsonReader.Num(o, "e"));
            Assert.Null(PartParityJsonReader.Num(o, "missing"));
            Assert.Null(PartParityJsonReader.Str(o, "a"));
        }

        [Theory]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("[1,2")]
        [InlineData("\"open")]
        [InlineData("{} x")]
        [InlineData("")]
        public void Parse_RejectsMalformedText(string text)
        {
            Assert.Throws<FormatException>(() => PartParityJsonReader.Parse(text));
        }

        private static PartParityCaseSidecar Sample()
        {
            var s = new PartParityCaseSidecar
            {
                CaseId = "fueltank--default--0a1b2c3d",
                Part = "fuelTank",
                Title = "FL-T400 \"Fuel\" Tank",
                Source = "Squad",
                State = "stowed",
                Context = "alone",
                Skin = new PartParitySkinProof { Module = "ModulePartVariants", Requested = "White", Real = "White", Snapshot = "White" },
                Setup = new PartParitySetup { Status = "still-animating", Detail = "cap" },
                Structure = new PartParityStructureDiff { RealRenderers = 3, GhostRenderers = 2, Pairs = 2 },
            };
            s.Flags.Add("seed-events:4");
            s.Structure.MissingOnGhost.Add(new PartParityRendererRef { Key = "model/shroud", Mesh = "shroud", Rendered = true });
            s.Structure.ExtraOnGhost.Add(new PartParityRendererRef { Key = "fairing_panels/p0", Mesh = "p", Rendered = false });
            s.Views.Add(new PartParityViewRecord
            {
                View = "side-a",
                Measures = new PartParityViewMeasures
                {
                    Width = 512, Height = 512, RealPx = 1000, GhostPx = 900, AndPx = 880, OrPx = 1020,
                    RealOnlyPx = 120, GhostOnlyPx = 20, RealOnlyBlobPx = 77, GhostOnlyBlobPx = 3,
                    RealOnlyBlobPct = 0.5, GhostOnlyBlobPct = null, ColorOverPx = 9, ColorBlobPx = 5,
                    ColorP99 = 12.25, ColorBlockMax = 3.5, Iou = 0.8627, TouchesBorderGhost = true,
                },
            });
            return s;
        }

        [Fact]
        public void ReadCaseSidecar_RoundTripsWhatTheManifestReads()
        {
            PartParityCaseSidecar back = PartParityJsonReader.ReadCaseSidecar(PartParityJson.WriteCase(Sample()));
            Assert.Equal("fueltank--default--0a1b2c3d", back.CaseId);
            Assert.Equal("FL-T400 \"Fuel\" Tank", back.Title);
            Assert.Equal("Squad", back.Source);
            Assert.Equal("White", back.Skin.Requested);
            Assert.Equal("still-animating", back.Setup.Status);
            Assert.Equal(new[] { "seed-events:4" }, back.Flags.ToArray());
            Assert.Equal(3, back.Structure.RealRenderers);
            Assert.Equal("model/shroud", back.Structure.MissingOnGhost[0].Key);
            Assert.True(back.Structure.MissingOnGhost[0].Rendered);
            Assert.False(back.Structure.ExtraOnGhost[0].Rendered);
            PartParityViewMeasures m = back.Views[0].Measures;
            Assert.Equal("side-a", back.Views[0].View);
            Assert.Equal(77, m.RealOnlyBlobPx);
            Assert.Equal(0.5, m.RealOnlyBlobPct);
            Assert.Null(m.GhostOnlyBlobPct);
            Assert.Equal(12.25, m.ColorP99);
            Assert.Equal(0.8627, m.Iou);
            Assert.True(m.TouchesBorderGhost);
            Assert.False(m.TouchesBorderReal);
        }

        [Fact]
        public void ReadCaseSidecar_ReadsTheCrashedSidecar()
        {
            var key = new PartParityCaseKey { Part = "bad.part", Skin = "default" };
            string text = PartParityJson.WriteCase(PartParityJson.BuildCrashedSidecar(key, "bad.part--default--12345678", "died"));
            PartParityCaseSidecar back = PartParityJsonReader.ReadCaseSidecar(text);
            Assert.Equal(TestCommandPartParitySweep.SetupCrashed, back.Setup.Status);
            Assert.Empty(back.Views);
        }

        [Fact]
        public void ReadCaseSidecar_RejectsAnotherSchema()
        {
            Assert.Throws<FormatException>(() => PartParityJsonReader.ReadCaseSidecar("{\"schema\":\"parsek-part-parity/1\"}"));
        }
    }
}
