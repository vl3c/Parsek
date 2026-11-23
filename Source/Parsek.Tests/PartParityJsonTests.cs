using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the part parity files' writer (<see cref="PartParityJson"/>): a
    /// golden sidecar, comma-decimal culture invariance, string escaping, the NaN / infinity
    /// rule, and that the key tables a harness source-sync cell will pin are exactly the
    /// keys the writer emits, in order.
    /// </summary>
    public class PartParityJsonTests
    {
        internal static PartParityCaseSidecar SmallSidecar()
        {
            var measures = new PartParityViewMeasures
            {
                Width = 4, Height = 4,
                RealPx = 4, GhostPx = 3, AndPx = 3, OrPx = 4,
                RealOnlyPx = 1, GhostOnlyPx = 0, RealOnlyTolPx = 0, GhostOnlyTolPx = 0,
                RealOnlyBlobPx = 0, GhostOnlyBlobPx = 0,
                RealOnlyBlobPct = 0.0, GhostOnlyBlobPct = null,
                BboxReal = new[] { 1, 1, 2, 2 }, BboxGhost = new[] { 1, 1, 2, 2 },
                CentroidReal = new[] { 2.0, 2.0 }, CentroidGhost = new[] { 1.8333333333333333, 2.0 },
                TouchesBorderReal = false, TouchesBorderGhost = true,
                ColorOverlapPx = 3, ColorThreshold = 24, ColorOverPx = 0, ColorBlobPx = 0,
                ColorP99 = 0.5, ColorBlockMax = 0.25, Iou = 0.75,
            };
            var diff = new PartParityStructureDiff { RealRenderers = 2, GhostRenderers = 1, Pairs = 1 };
            diff.MissingOnGhost.Add(new PartParityRendererRef { Key = "model/skirt", Mesh = "skirt", Rendered = true });
            diff.RenderedDiffers.Add(new PartParityFieldDiff { Key = "model/a", Field = "rendered", Real = "false", Ghost = "true" });
            return new PartParityCaseSidecar
            {
                CaseId = "mk1pod.v2--default--0a1b2c3d",
                Part = "mk1pod.v2",
                Title = "Mk1 \"Pod\"",
                Source = "Squad",
                PartUrl = "Squad/Parts/Command/mk1pod_v2/part/mk1pod_v2",
                ModelUrls = { "Squad/Parts/Command/mk1pod_v2/model" },
                Category = "Pods",
                Tags = { "hidden" },
                Skin = new PartParitySkinProof
                {
                    Module = "ModulePartVariants", ModuleId = null, Requested = "White",
                    Real = "White", Snapshot = "White", Ghost = "White",
                },
                State = "stowed",
                Context = "alone",
                Atlas = new PartParityAtlas
                {
                    File = "mk1pod.v2--default--0a1b2c3d.png", Thumb = "mk1pod.v2--default--0a1b2c3d.thumb.png",
                    Tile = 512, Rows = new[] { "real", "ghost" }, Cols = new[] { "side-a" },
                },
                Framing = new PartParityFraming
                {
                    Real = new PartParityBox { Center = new[] { 0.0, 0.5, 0.0 }, Extents = new[] { 0.625, 0.5, 0.625 } },
                    Shared = new PartParityBox { Center = new[] { 0.0, 0.5, 0.0 }, Extents = new[] { 0.625, 0.5, 0.625 } },
                    OrthoSize = 1.1,
                },
                GhostRootOffsetM = 0.0,
                LayersReal = new[] { 0 },
                LayersGhost = new[] { 0, 1 },
                Flags = { "ghost-layer-differs" },
                Views =
                {
                    new PartParityViewRecord
                    {
                        View = "side-a", Measures = measures,
                        RealMatte = new PartParityMatteStats { Opaque = 3, Edge = 1, Inverted = 0 },
                    },
                },
                Structure = diff,
                Setup = new PartParitySetup { Status = "ok", Detail = null },
                Log = { "[Parsek][INFO][GhostVisual] built" },
            };
        }

        private const string GoldenSidecar =
            "{\n" +
            "  \"schema\": \"parsek-part-parity-case/1\",\n" +
            "  \"caseId\": \"mk1pod.v2--default--0a1b2c3d\",\n" +
            "  \"part\": \"mk1pod.v2\",\n" +
            "  \"title\": \"Mk1 \\\"Pod\\\"\",\n" +
            "  \"source\": \"Squad\",\n" +
            "  \"partUrl\": \"Squad/Parts/Command/mk1pod_v2/part/mk1pod_v2\",\n" +
            "  \"modelUrls\": [\"Squad/Parts/Command/mk1pod_v2/model\"],\n" +
            "  \"category\": \"Pods\",\n" +
            "  \"tags\": [\"hidden\"],\n" +
            "  \"skin\": {\"module\":\"ModulePartVariants\",\"moduleId\":null,\"requested\":\"White\",\"real\":\"White\",\"snapshot\":\"White\",\"ghost\":\"White\"},\n" +
            "  \"state\": \"stowed\",\n" +
            "  \"context\": \"alone\",\n" +
            "  \"measure\": {\"coverageAlpha\":0.5,\"tolerancePx\":1,\"blobConnectivity\":4,\"colorMetric\":\"euclidean-rgb-premultiplied\",\"colorThreshold\":24,\"colorBlockGrid\":16,\"minBlockOverlapFraction\":0.25},\n" +
            "  \"atlas\": {\"file\":\"mk1pod.v2--default--0a1b2c3d.png\",\"thumb\":\"mk1pod.v2--default--0a1b2c3d.thumb.png\",\"tile\":512,\"rows\":[\"real\",\"ghost\"],\"cols\":[\"side-a\"]},\n" +
            "  \"framing\": {\"real\":{\"center\":[0,0.5,0],\"extents\":[0.625,0.5,0.625]},\"ghost\":null,\"shared\":{\"center\":[0,0.5,0],\"extents\":[0.625,0.5,0.625]},\"orthoSize\":1.1},\n" +
            "  \"ghostRootOffsetM\": 0,\n" +
            "  \"layers\": {\"real\":[0],\"ghost\":[0,1]},\n" +
            "  \"flags\": [\"ghost-layer-differs\"],\n" +
            "  \"views\": [\n" +
            "    {\"view\":\"side-a\",\"width\":4,\"height\":4,\"realPx\":4,\"ghostPx\":3,\"andPx\":3,\"orPx\":4,\"realOnlyPx\":1,\"ghostOnlyPx\":0,\"realOnlyTolPx\":0,\"ghostOnlyTolPx\":0,\"realOnlyBlobPx\":0,\"ghostOnlyBlobPx\":0,\"realOnlyBlobPct\":0,\"ghostOnlyBlobPct\":null,\"bbox\":{\"real\":[1,1,2,2],\"ghost\":[1,1,2,2]},\"centroid\":{\"real\":[2,2],\"ghost\":[1.8333333333333333,2]},\"touchesBorder\":{\"real\":false,\"ghost\":true},\"colorOverlapPx\":3,\"colorOverPx\":0,\"colorBlobPx\":0,\"colorP99\":0.5,\"colorBlockMax\":0.25,\"iou\":0.75,\"matte\":{\"real\":{\"opaque\":3,\"edge\":1,\"inverted\":0},\"ghost\":null},\"start\":null,\"stowedRef\":null}\n" +
            "  ],\n" +
            "  \"structure\": {\"realRenderers\":2,\"ghostRenderers\":1,\"pairs\":1,\"missingOnGhost\":[{\"key\":\"model/skirt\",\"mesh\":\"skirt\",\"rendered\":true,\"nonVisual\":null}],\"extraOnGhost\":[],\"transformDiffers\":[],\"materialDiffers\":[],\"meshDiffers\":[],\"renderedDiffers\":[{\"key\":\"model/a\",\"field\":\"rendered\",\"real\":\"false\",\"ghost\":\"true\"}]},\n" +
            "  \"setup\": {\"status\":\"ok\",\"detail\":null},\n" +
            "  \"log\": [\"[Parsek][INFO][GhostVisual] built\"],\n" +
            "  \"stateProof\": null,\n" +
            "  \"structureStart\": null,\n" +
            "  \"stowedCaseId\": null,\n" +
            "  \"fx\": null,\n" +
            "  \"fxAtlas\": null,\n" +
            "  \"strip\": null\n" +
            "}\n";

        [Fact]
        public void WriteCase_MatchesTheGoldenSidecar()
        {
            Assert.Equal(GoldenSidecar, PartParityJson.WriteCase(SmallSidecar()));
        }

        [Fact]
        public void WriteCase_IsTheSameUnderACommaDecimalCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string json = PartParityJson.WriteCase(SmallSidecar());
                Assert.Equal(GoldenSidecar, json);
                Assert.DoesNotContain("0,5", json);
                var mf = new PartParityManifest
                {
                    Label = "de", Tile = 1024,
                    Cases = { new PartParityCaseSummary { Index = 12345, MinIou = 0.125 } },
                };
                string manifest = PartParityJson.WriteManifest(mf);
                Assert.Contains("\"index\":12345", manifest);
                Assert.Contains("\"minIou\":0.125", manifest);
                Assert.Contains("\"tile\": 1024", manifest);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Strings_AreEscapedAndRoundTrip()
        {
            string nasty = "quote\" back\\ nl\n tab\t cr\r ctl\u0001 del\u007f end";
            var c = SmallSidecar();
            c.Title = nasty;
            c.Log.Add(nasty);
            string json = PartParityJson.WriteCase(c);
            Assert.Contains("\\u0001", json);
            Assert.Contains("\\u007f", json);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            Assert.Equal(nasty, root["title"]);
            Assert.Equal(nasty, ((List<object>)root["log"])[1]);
        }

        [Fact]
        public void NonFiniteNumbers_AreWrittenAsNull()
        {
            var c = SmallSidecar();
            c.GhostRootOffsetM = double.NaN;
            c.Views[0].Measures.ColorP99 = double.PositiveInfinity;
            c.Structure.TransformDiffers.Add(new PartParityTransformDiff
            {
                Key = "model/a", PositionDeltaM = 0.01, RotationDeltaDeg = double.NaN, ScaleDeltaPct = double.NegativeInfinity,
            });
            string json = PartParityJson.WriteCase(c);
            Assert.DoesNotContain("NaN", json);
            Assert.DoesNotContain("Infinity", json);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            Assert.Null(root["ghostRootOffsetM"]);
            var view = (Dictionary<string, object>)((List<object>)root["views"])[0];
            Assert.Null(view["colorP99"]);
            var structure = (Dictionary<string, object>)root["structure"];
            var td = (Dictionary<string, object>)((List<object>)structure["transformDiffers"])[0];
            Assert.Null(td["rotationDeltaDeg"]);
            Assert.Null(td["scaleDeltaPct"]);
            Assert.Equal(0.01, td["positionDeltaM"]);
        }

        // ----- key tables -----

        private static void AssertKeysInOrder(string[] table, Dictionary<string, object> parsed, string json)
        {
            Assert.Equal(table.OrderBy(k => k, System.StringComparer.Ordinal),
                parsed.Keys.OrderBy(k => k, System.StringComparer.Ordinal));
            int last = -1;
            foreach (string key in table)
            {
                int at = json.IndexOf("\"" + key + "\":", last + 1, System.StringComparison.Ordinal);
                Assert.True(at > last, "key '" + key + "' out of table order");
                last = at;
            }
        }

        [Fact]
        public void SidecarKeys_AreExactlyTheKeyTables()
        {
            var c = SmallSidecar();
            c.Framing.Ghost = c.Framing.Real;
            c.Views[0].GhostMatte = new PartParityMatteStats();
            c.Structure.TransformDiffers.Add(new PartParityTransformDiff { Key = "k" });
            c.Structure.MaterialDiffers.Add(new PartParityFieldDiff { Key = "k", Field = "shaders" });
            string json = PartParityJson.WriteCase(c);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);

            // Top-level keys sit at the start of a line, so the order check reads them there.
            var top = json.Split('\n').Where(l => l.StartsWith("  \"")).Select(l => l.Substring(3, l.IndexOf('"', 3) - 3)).ToArray();
            Assert.Equal(PartParityJson.CaseKeys, top);
            Assert.Equal(PartParityJson.CaseKeys.Length, root.Count);

            AssertKeysInOrder(PartParityJson.SkinKeys, (Dictionary<string, object>)root["skin"], json);
            AssertKeysInOrder(PartParityJson.MeasureKeys, (Dictionary<string, object>)root["measure"], json);
            AssertKeysInOrder(PartParityJson.AtlasKeys, (Dictionary<string, object>)root["atlas"], json);
            var framing = (Dictionary<string, object>)root["framing"];
            Assert.Equal(PartParityJson.FramingKeys.OrderBy(k => k), framing.Keys.OrderBy(k => k));
            Assert.Equal(PartParityJson.BoxKeys.OrderBy(k => k),
                ((Dictionary<string, object>)framing["real"]).Keys.OrderBy(k => k));
            var view = (Dictionary<string, object>)((List<object>)root["views"])[0];
            AssertKeysInOrder(PartParityJson.ViewKeys, view, json);
            var matte = (Dictionary<string, object>)view["matte"];
            Assert.Equal(PartParityJson.MatteKeys.OrderBy(k => k),
                ((Dictionary<string, object>)matte["real"]).Keys.OrderBy(k => k));
            var structure = (Dictionary<string, object>)root["structure"];
            AssertKeysInOrder(PartParityJson.StructureKeys, structure, json);
            Assert.Equal(PartParityJson.RendererRefKeys.OrderBy(k => k),
                ((Dictionary<string, object>)((List<object>)structure["missingOnGhost"])[0]).Keys.OrderBy(k => k));
            Assert.Equal(PartParityJson.TransformDiffKeys.OrderBy(k => k),
                ((Dictionary<string, object>)((List<object>)structure["transformDiffers"])[0]).Keys.OrderBy(k => k));
            Assert.Equal(PartParityJson.FieldDiffKeys.OrderBy(k => k),
                ((Dictionary<string, object>)((List<object>)structure["materialDiffers"])[0]).Keys.OrderBy(k => k));
            AssertKeysInOrder(PartParityJson.SetupKeys, (Dictionary<string, object>)root["setup"], json);
        }

        [Fact]
        public void ManifestKeys_AreExactlyTheKeyTables()
        {
            var mf = new PartParityManifest
            {
                Label = "pp-1", Profile = "stock-minimal", DllHash = "abc", Tile = 512,
                GameDataMods = { "Squad", "Parsek" },
                Views = { "side-a", "side-b", "top", "bottom" },
                Settings = { new KeyValuePair<string, string>("masterTextureLimit", "3") },
                Controls = { new PartParityControlRecord { Id = "ctl-empty", Subject = "both hidden", Required = "zero coverage", Outcome = "ok" } },
                Cases = { new PartParityCaseSummary { Index = 0, CaseId = "a--default--00000000", SetupStatus = "ok", MaxShapeBlobPx = 12, MinIou = 0.99 } },
                Exclusions = { new PartParityExclusion { Part = "flag", Reason = "excluded-flag" } },
                Progress = new PartParityProgress
                {
                    Complete = false, Cases = 10, CompleteCases = 4, Controls = "pending", Crashed = { "b--default--11111111" },
                    HostDamaged = true, HostDamage = "parts", HostDamageDetail = "pid=7 parts 13->9 at before wave 3",
                },
            };
            string json = PartParityJson.WriteManifest(mf);
            Assert.EndsWith("}\n", json);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            var top = json.Split('\n').Where(l => l.StartsWith("  \"")).Select(l => l.Substring(3, l.IndexOf('"', 3) - 3)).ToArray();
            Assert.Equal(PartParityJson.ManifestKeys, top);
            Assert.Equal(PartParityJson.ManifestSchemaId, root["schema"]);
            Assert.Equal("3", ((Dictionary<string, object>)root["settings"])["masterTextureLimit"]);
            AssertKeysInOrder(PartParityJson.ControlKeys,
                (Dictionary<string, object>)((List<object>)root["controls"])[0], json);
            AssertKeysInOrder(PartParityJson.CaseSummaryKeys,
                (Dictionary<string, object>)((List<object>)root["cases"])[0], json);
            AssertKeysInOrder(PartParityJson.ExclusionKeys,
                (Dictionary<string, object>)((List<object>)root["exclusions"])[0], json);
            var progress = (Dictionary<string, object>)root["progress"];
            AssertKeysInOrder(PartParityJson.ProgressKeys, progress, json);
            Assert.Equal(new List<object> { "b--default--11111111" }, progress["crashed"]);
            Assert.Equal(true, progress["hostDamaged"]);
            Assert.Equal("parts", progress["hostDamage"]);
            Assert.Equal("pid=7 parts 13->9 at before wave 3", progress["hostDamageDetail"]);
            var summary = (Dictionary<string, object>)((List<object>)root["cases"])[0];
            Assert.Null(summary["maxColorBlobPx"]);
            Assert.Equal(12.0, summary["maxShapeBlobPx"]);
        }

        [Fact]
        public void EmptyManifest_IsValidJsonWithNullProgress()
        {
            string json = PartParityJson.WriteManifest(new PartParityManifest());
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            Assert.Null(root["progress"]);
            Assert.Empty((List<object>)root["cases"]);
        }

        [Fact]
        public void CrashedSidecar_CarriesIdentityAndTheCrashedStatus()
        {
            var key = new PartParityCaseKey { Part = "badPart", Skin = "Gold", SkinOrdinal = 1 };
            string id = TestCommandPartParitySweep.CaseId(key);
            var c = PartParityJson.BuildCrashedSidecar(key, id, "inflight at step start");
            string json = PartParityJson.WriteCase(c);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            Assert.Equal(id, root["caseId"]);
            Assert.Equal("badPart", root["part"]);
            Assert.Equal("Gold", ((Dictionary<string, object>)root["skin"])["requested"]);
            var setup = (Dictionary<string, object>)root["setup"];
            Assert.Equal(TestCommandPartParitySweep.SetupCrashed, setup["status"]);
            Assert.Empty((List<object>)root["views"]);
            Assert.Null(root["structure"]);
        }
    }
}
