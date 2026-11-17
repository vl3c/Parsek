using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.InGameTests.Spikes;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Spikes
{
    // Spike branch only (spike/mp-r0-s4a). Pure parts of spike S4a: value formatting,
    // the reflective field walk, the turn-back predicate, footprint extents, CSV rows.
    public class SpikeS4FormatTests : IDisposable
    {
        private readonly CultureInfo savedCulture;

        public SpikeS4FormatTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private enum Color3 { Red, Green }

        private class Base
        {
            public double baseValue = 2.5;
            private int basePrivate = 7;
            public int Touch() => basePrivate;
        }

        private class Sample : Base
        {
            public double lat = -0.0972;
            public float angle = 1.5f;
            public string name = "KSC";
            public bool flag = true;
            public Color3 colour = Color3.Green;
            public Vector3 position = new Vector3(1f, 2.5f, -3f);
            public Vector3d precise = new Vector3d(1.25, -2.0, 600000.5);
            public int[] list = { 1, 2, 3 };
            public object reference = new System.Text.StringBuilder();
            public object nothing = null;
            private double hidden = 4.0;
            public int Auto { get; set; } = 9;
            public double Touch2() => hidden;
        }

        [Fact]
        public void FormatValue_IsInvariantUnderACommaCulture()
        {
            Assert.Equal("1.5", SpikeS4Format.FormatValue(1.5));
            Assert.Equal("1.5", SpikeS4Format.FormatValue(1.5f));
            Assert.Equal("-0.0972", SpikeS4Format.FormatValue(-0.0972));
            Assert.Equal("1 2.5 -3", SpikeS4Format.FormatValue(new Vector3(1f, 2.5f, -3f)));
            Assert.Equal("1.25 -2 600000.5", SpikeS4Format.FormatValue(new Vector3d(1.25, -2.0, 600000.5)));
            Assert.Equal("0 0 0 1", SpikeS4Format.FormatValue(new Quaternion(0f, 0f, 0f, 1f)));
            Assert.Equal("0.5 1.5", SpikeS4Format.FormatValue(new Vector2d(0.5, 1.5)));
            Assert.Equal("12345", SpikeS4Format.FormatValue(12345));
        }

        [Fact]
        public void FormatValue_NamesNullBoolEnumArrayAndReferences()
        {
            Assert.Equal("null", SpikeS4Format.FormatValue(null));
            Assert.Equal("true", SpikeS4Format.FormatValue(true));
            Assert.Equal("false", SpikeS4Format.FormatValue(false));
            Assert.Equal("Green", SpikeS4Format.FormatValue(Color3.Green));
            Assert.Equal("len=3", SpikeS4Format.FormatValue(new[] { 1, 2, 3 }));
            Assert.Equal("KSC", SpikeS4Format.FormatValue("KSC"));
            Assert.Equal("<StringBuilder>", SpikeS4Format.FormatValue(new System.Text.StringBuilder()));
        }

        [Theory]
        [InlineData(typeof(double), true)]
        [InlineData(typeof(string), true)]
        [InlineData(typeof(Vector3), true)]
        [InlineData(typeof(Vector3d), true)]
        [InlineData(typeof(QuaternionD), true)]
        [InlineData(typeof(GameScenes), true)]
        [InlineData(typeof(object), false)]
        [InlineData(typeof(int[]), false)]
        [InlineData(null, false)]
        public void IsValueLike_CoversPrimitivesVectorsEnums(Type t, bool expected)
        {
            Assert.Equal(expected, SpikeS4Format.IsValueLike(t));
        }

        [Fact]
        public void DumpFields_PublicOnly_WalksBasesSkipsBackingFieldsAndDoesNotRecurse()
        {
            var fields = SpikeS4Format.DumpFields(new Sample(), includeNonPublic: false);
            var map = fields.ToDictionary(f => f.Name, f => f.Value);
            Assert.Equal("-0.0972", map["lat"]);
            Assert.Equal("1.5", map["angle"]);
            Assert.Equal("KSC", map["name"]);
            Assert.Equal("true", map["flag"]);
            Assert.Equal("Green", map["colour"]);
            Assert.Equal("1 2.5 -3", map["position"]);
            Assert.Equal("1.25 -2 600000.5", map["precise"]);
            Assert.Equal("len=3", map["list"]);
            Assert.Equal("<StringBuilder>", map["reference"]);
            Assert.Equal("null", map["nothing"]);
            Assert.Equal("2.5", map["baseValue"]);
            Assert.False(map.ContainsKey("hidden"));
            Assert.False(map.ContainsKey("basePrivate"));
            Assert.DoesNotContain(fields, f => f.Name.Contains("Auto") || f.Name.Contains("<"));
            // Declared fields first, base fields after.
            Assert.True(fields.FindIndex(f => f.Name == "lat") < fields.FindIndex(f => f.Name == "baseValue"));
        }

        [Fact]
        public void DumpFields_NonPublicAndStopAt()
        {
            var all = SpikeS4Format.DumpFields(new Sample(), includeNonPublic: true).ToDictionary(f => f.Name, f => f.Value);
            Assert.Equal("4", all["hidden"]);
            Assert.Equal("7", all["basePrivate"]);

            var stopped = SpikeS4Format.DumpFields(new Sample(), includeNonPublic: true, stopAt: typeof(Base));
            Assert.DoesNotContain(stopped, f => f.Name == "baseValue" || f.Name == "basePrivate");
            Assert.Empty(SpikeS4Format.DumpFields(null, true));
        }

        [Theory]
        [InlineData("My Career", "SPIKE-REFUSE-1", true)]   // a folder copy keeps the source's title
        [InlineData("SPIKE-REFUSE-1", "other", true)]
        [InlineData("x", "spike-refuse-2", true)]
        [InlineData("x", "  SPIKE-REFUSE", true)]
        [InlineData("x", "SPIKE-S4A-ON", false)]
        [InlineData("my SPIKE-REFUSE", "a SPIKE-REFUSE", false)]
        [InlineData(null, null, false)]
        [InlineData("", "", false)]
        public void ShouldTurnBack_MatchesFolderOrTitlePrefix(string title, string folder, bool expected)
        {
            Assert.Equal(expected, SpikeS4Format.ShouldTurnBack(title, folder));
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData(" 1 ", true)]
        [InlineData("true", false)]
        [InlineData("0", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void TurnBackSwitch_IsTheLiteralOne(string raw, bool expected)
        {
            Assert.Equal(expected, SpikeS4Format.IsTurnBackArmedValue(raw));
        }

        [Fact]
        public void ComputeExtents_BoundsAndLocalLengths()
        {
            var geo = new List<double[]>
            {
                new[] { -0.0972, -74.5575, 70.0 },
                new[] { -0.0960, -74.5590, 72.5 },
                new[] { double.NaN, 0.0, 0.0 },        // skipped
            };
            var local = new List<double[]>
            {
                new[] { -10.0, 0.0, -20.0 },
                new[] { 30.0, 5.0, 40.0 },
                new[] { 999.0, 999.0, 999.0 },
            };
            SpikeS4Extents e = SpikeS4Format.ComputeExtents(geo, local);
            Assert.Equal(2, e.Points);
            Assert.Equal(-0.0972, e.LatMin);
            Assert.Equal(-0.0960, e.LatMax);
            Assert.Equal(-74.5590, e.LonMin);
            Assert.Equal(-74.5575, e.LonMax);
            Assert.Equal(40.0, e.LengthX);
            Assert.Equal(5.0, e.LengthY);
            Assert.Equal(60.0, e.LengthZ);
            Assert.Equal((-0.0972 + -0.0960) / 2.0, e.CentreLat, 12);
        }

        [Fact]
        public void ComputeExtents_EmptyAndMismatch()
        {
            SpikeS4Extents e = SpikeS4Format.ComputeExtents(new List<double[]>(), new List<double[]>());
            Assert.Equal(0, e.Points);
            Assert.Equal(0.0, e.LengthX);
            Assert.True(double.IsNaN(e.CentreLat));
            Assert.Throws<ArgumentException>(() =>
                SpikeS4Format.ComputeExtents(new List<double[]> { new double[3] }, new List<double[]>()));
        }

        [Fact]
        public void BoxCorners_EightCornersSpanTheBox()
        {
            Vector3[] c = SpikeS4Format.BoxCorners(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f));
            Assert.Equal(8, c.Length);
            Assert.Equal(new Vector3(0f, 0f, 0f), c[0]);
            Assert.Equal(new Vector3(2f, 4f, 6f), c[7]);
            Assert.Equal(8, c.Distinct().Count());
        }

        [Fact]
        public void JoinTags_SortedDistinctNonEmpty()
        {
            Assert.Equal("LaunchPad|Untagged", SpikeS4Format.JoinTags(new[] { "Untagged", "LaunchPad", "", null, "Untagged" }));
            Assert.Equal("", SpikeS4Format.JoinTags(null));
        }

        [Fact]
        public void EventRowAndMessage_AreInvariant()
        {
            object[] row = SpikeS4Format.EventRow("20261117T000000Z", 3, 120, 1.25, "onGameStateLoad", "node",
                "SPACECENTER", "g2", "SPIKE, quoted", "SPIKE-S4A-ON");
            Assert.Equal(SpikeS4Format.EventHeader.Length, row.Length);
            Assert.Equal("20261117T000000Z,3,120,1.25,onGameStateLoad,node,SPACECENTER,g2,\"SPIKE, quoted\",SPIKE-S4A-ON",
                SpikeCsv.FormatRow(row));
            Assert.Equal("event seq=3 frame=120 ev=onGameStateLoad scene=SPACECENTER game=g2 title=t folder=f detail=d",
                SpikeS4Format.FormatEventMessage(3, 120, "onGameStateLoad", "d", "SPACECENTER", "g2", "t", "f"));
            Assert.Equal("event seq=1 frame=-1 ev=x scene= game=none title= folder= detail=",
                SpikeS4Format.FormatEventMessage(1, -1, "x", null, null, null, null, null));
        }

        [Fact]
        public void Headers_HaveNoDuplicateColumns()
        {
            foreach (var h in new[] { SpikeS4Format.KscStateHeader, SpikeS4Format.FootprintHeader,
                         SpikeS4Format.CornerHeader, SpikeS4Format.CircleHeader, SpikeS4Format.EventHeader })
                Assert.Equal(h.Length, h.Distinct().Count());
        }

        [Fact]
        public void FormatArg_QuotesStringsAndNamesReferences()
        {
            Assert.Equal("\"persistent\"", SpikeS4Format.FormatArg("persistent"));
            Assert.Equal("null", SpikeS4Format.FormatArg(null));
            Assert.Equal("2.5", SpikeS4Format.FormatArg(2.5));
            Assert.Equal("FLIGHT", SpikeS4Format.FormatArg(GameScenes.FLIGHT));
            Assert.Equal("<StringBuilder>", SpikeS4Format.FormatArg(new System.Text.StringBuilder()));
        }

        [Fact]
        public void MethodLabel_IsTypeDotName()
        {
            Assert.Equal("String.Concat", SpikeS4Format.MethodLabel(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })));
            Assert.Equal("?", SpikeS4Format.MethodLabel(null));
        }

        [Fact]
        public void ObjectIds_StablePerObjectDistinctAcrossObjects()
        {
            var ids = new SpikeObjectIds("g");
            object a = new object(), b = new object();
            Assert.Equal("g1", ids.IdOf(a));
            Assert.Equal("g2", ids.IdOf(b));
            Assert.Equal("g1", ids.IdOf(a));
            Assert.Equal("none", ids.IdOf(null));
        }

        [Fact]
        public void Registry_HasExactlyOneS4aLineWithPatches()
        {
            var lines = SpikeRegistry.All.Where(r => r.Id == SpikeS4Format.SpikeId).ToList();
            Assert.Single(lines);
            Assert.NotNull(lines[0].Apply);
        }
    }
}
