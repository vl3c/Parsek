using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    // The models of the part parity sweep's two files (design-part-ghost-parity.md 5.1, 5.2).
    // Plain data: the live capture fills them, the pure writer below serializes them. Every
    // key of the key tables is ALWAYS written; a value that was not measured is JSON null, so
    // a reader never has to tell "absent" from "older build".

    /// <summary>The <c>skin</c> block: the skin proof (design 3.2).</summary>
    internal sealed class PartParitySkinProof
    {
        internal string Module;
        internal string ModuleId;
        internal string Requested;
        internal string Real;
        internal string Snapshot;
        internal string Ghost;
    }

    /// <summary>A framing box in part space: centre and half-extents, metres.</summary>
    internal sealed class PartParityBox
    {
        internal double[] Center;
        internal double[] Extents;
    }

    /// <summary>The <c>framing</c> block.</summary>
    internal sealed class PartParityFraming
    {
        internal PartParityBox Real;
        internal PartParityBox Ghost;
        internal PartParityBox Shared;
        internal double? OrthoSize;
    }

    /// <summary>The <c>atlas</c> block.</summary>
    internal sealed class PartParityAtlas
    {
        internal string File;
        internal string Thumb;
        internal int Tile;
        internal string[] Rows;
        internal string[] Cols;
    }

    /// <summary>One <c>views[]</c> entry.</summary>
    internal sealed class PartParityViewRecord
    {
        internal string View;
        internal PartParityViewMeasures Measures;
        internal PartParityMatteStats? RealMatte;
        internal PartParityMatteStats? GhostMatte;
    }

    /// <summary>The <c>setup</c> block.</summary>
    internal sealed class PartParitySetup
    {
        internal string Status;
        internal string Detail;
    }

    /// <summary>One case sidecar, <c>&lt;caseId&gt;.part.json</c>.</summary>
    internal sealed class PartParityCaseSidecar
    {
        internal string CaseId;
        internal string Part;
        internal string Title;
        internal string Source;
        internal string PartUrl;
        internal List<string> ModelUrls = new List<string>();
        internal string Category;
        internal List<string> Tags = new List<string>();
        internal PartParitySkinProof Skin;
        internal string State;
        internal string Context;
        internal PartParityAtlas Atlas;
        internal PartParityFraming Framing;
        internal double? GhostRootOffsetM;
        internal int[] LayersReal;
        internal int[] LayersGhost;
        internal List<string> Flags = new List<string>();
        internal List<PartParityViewRecord> Views = new List<PartParityViewRecord>();
        internal PartParityStructureDiff Structure;
        internal PartParitySetup Setup;
        internal List<string> Log = new List<string>();
    }

    /// <summary>One <c>controls[]</c> entry of the manifest.</summary>
    internal sealed class PartParityControlRecord
    {
        internal string Id;
        internal string Subject;
        internal string Required;

        /// <summary><c>ok</c>, <c>bad</c> or <c>pending</c>.</summary>
        internal string Outcome;
        internal string Detail;
    }

    /// <summary>One <c>cases[]</c> entry of the manifest: the raw summary the grid sorts by.</summary>
    internal sealed class PartParityCaseSummary
    {
        internal int Index;
        internal string CaseId;
        internal string Part;
        internal string Title;
        internal string Source;
        internal string Skin;
        internal string State;
        internal string Context;
        internal string Sidecar;
        internal string SetupStatus;
        internal int? MaxShapeBlobPx;
        internal int? MaxColorBlobPx;
        internal double? MinIou;
        internal int? MissingRendered;
        internal int? ExtraRendered;
    }

    /// <summary>One <c>exclusions[]</c> entry: a part the sweep did not capture, and why.</summary>
    internal sealed class PartParityExclusion
    {
        internal string Part;
        internal string Reason;
    }

    /// <summary>The <c>progress</c> block of the manifest.</summary>
    internal sealed class PartParityProgress
    {
        internal bool Complete;
        internal int Cases;
        internal int CompleteCases;
        internal int SetupFaults;
        internal string Controls;
        internal List<string> Crashed = new List<string>();

        /// <summary>The host-integrity guard stopped the sweep (design 4.1): the remaining
        /// cases were left unrun, so the sweep is an instrument problem, never part findings.</summary>
        internal bool HostDamaged;

        /// <summary><c>gone|parts|situation</c> when <see cref="HostDamaged"/>, else null.</summary>
        internal string HostDamage;
        internal string HostDamageDetail;
    }

    /// <summary>The sweep manifest, <c>manifest.json</c>.</summary>
    internal sealed class PartParityManifest
    {
        internal string Label;
        internal string Profile;
        internal List<string> GameDataMods = new List<string>();
        internal string DllHash;
        internal int Tile;
        internal List<string> Views = new List<string>();
        internal List<KeyValuePair<string, string>> Settings = new List<KeyValuePair<string, string>>();
        internal List<PartParityControlRecord> Controls = new List<PartParityControlRecord>();
        internal List<PartParityCaseSummary> Cases = new List<PartParityCaseSummary>();
        internal List<PartParityExclusion> Exclusions = new List<PartParityExclusion>();
        internal PartParityProgress Progress;
    }

    /// <summary>
    /// Schema ids, key tables and the writer of the part parity sweep's files. The writer
    /// emits keys in the order of the key tables (a test parses the output and compares), so
    /// the tables are what a source-sync cell on the harness side pins.
    ///
    /// <para>Numbers are written <c>ToString("R", CultureInfo.InvariantCulture)</c>; a NaN or
    /// an infinity is written as <c>null</c> (never the bare tokens <c>NaN</c> /
    /// <c>Infinity</c>, which are not JSON). Strings are escaped by
    /// <c>GuiTreeJson.Str</c>. Layout: one top-level key per line and one <c>views[]</c>
    /// entry per line; everything nested is compact. The output ends with a newline.</para>
    /// </summary>
    internal static class PartParityJson
    {
        internal const string ManifestSchemaId = "parsek-part-parity/1";
        internal const string CaseSchemaId = "parsek-part-parity-case/1";

        internal static readonly string[] CaseKeys =
        {
            "schema", "caseId", "part", "title", "source", "partUrl", "modelUrls", "category",
            "tags", "skin", "state", "context", "measure", "atlas", "framing", "ghostRootOffsetM",
            "layers", "flags", "views", "structure", "setup", "log",
        };

        internal static readonly string[] SkinKeys =
        {
            "module", "moduleId", "requested", "real", "snapshot", "ghost",
        };

        /// <summary>The fixed measuring parameters, written into every sidecar and the manifest
        /// header so a reader never assumes them.</summary>
        internal static readonly string[] MeasureKeys =
        {
            "coverageAlpha", "tolerancePx", "blobConnectivity", "colorMetric", "colorThreshold",
            "colorBlockGrid", "minBlockOverlapFraction",
        };

        internal static readonly string[] AtlasKeys = { "file", "thumb", "tile", "rows", "cols" };

        internal static readonly string[] FramingKeys = { "real", "ghost", "shared", "orthoSize" };

        internal static readonly string[] BoxKeys = { "center", "extents" };

        internal static readonly string[] ViewKeys =
        {
            "view", "width", "height",
            "realPx", "ghostPx", "andPx", "orPx", "realOnlyPx", "ghostOnlyPx",
            "realOnlyTolPx", "ghostOnlyTolPx", "realOnlyBlobPx", "ghostOnlyBlobPx",
            "realOnlyBlobPct", "ghostOnlyBlobPct",
            "bbox", "centroid", "touchesBorder",
            "colorOverlapPx", "colorOverPx", "colorBlobPx", "colorP99", "colorBlockMax",
            "iou", "matte",
        };

        internal static readonly string[] MatteKeys = { "opaque", "edge", "inverted" };

        internal static readonly string[] StructureKeys =
        {
            "realRenderers", "ghostRenderers", "pairs", "missingOnGhost", "extraOnGhost",
            "transformDiffers", "materialDiffers", "meshDiffers", "renderedDiffers",
        };

        internal static readonly string[] RendererRefKeys = { "key", "mesh", "rendered", "nonVisual" };

        internal static readonly string[] TransformDiffKeys =
        {
            "key", "positionDeltaM", "rotationDeltaDeg", "scaleDeltaPct",
        };

        internal static readonly string[] FieldDiffKeys = { "key", "field", "real", "ghost" };

        internal static readonly string[] SetupKeys = { "status", "detail" };

        internal static readonly string[] ManifestKeys =
        {
            "schema", "label", "profile", "gameDataMods", "dllHash", "tile", "views", "measure",
            "settings", "controls", "cases", "exclusions", "progress",
        };

        internal static readonly string[] ControlKeys = { "id", "subject", "required", "outcome", "detail" };

        internal static readonly string[] CaseSummaryKeys =
        {
            "index", "caseId", "part", "title", "source", "skin", "state", "context", "sidecar",
            "setupStatus", "maxShapeBlobPx", "maxColorBlobPx", "minIou", "missingRendered",
            "extraRendered",
        };

        internal static readonly string[] ExclusionKeys = { "part", "reason" };

        internal static readonly string[] ProgressKeys =
        {
            "complete", "cases", "completeCases", "setupFaults", "controls", "crashed",
            "hostDamaged", "hostDamage", "hostDamageDetail",
        };

        /// <summary>The <c>colorMetric</c> token: Euclidean RGB distance after premultiply.</summary>
        internal const string ColorMetricToken = "euclidean-rgb-premultiplied";

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // ----- scalar fragments -----

        internal static string Str(string s) => GuiTreeJson.Str(s);

        internal static string Num(double v)
            => double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", Ic);

        internal static string Num(double? v) => v.HasValue ? Num(v.Value) : "null";

        internal static string Int(long v) => v.ToString(Ic);

        internal static string IntOrNull(int? v) => v.HasValue ? v.Value.ToString(Ic) : "null";

        internal static string Bool(bool v) => v ? "true" : "false";

        internal static string StrArray(IList<string> items)
        {
            if (items == null) return "null";
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Str(items[i]));
            }
            return sb.Append(']').ToString();
        }

        internal static string IntArray(IList<int> items)
        {
            if (items == null) return "null";
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(items[i].ToString(Ic));
            }
            return sb.Append(']').ToString();
        }

        internal static string NumArray(IList<double> items)
        {
            if (items == null) return "null";
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Num(items[i]));
            }
            return sb.Append(']').ToString();
        }

        /// <summary>A compact object from (key, already-serialized value) pairs, in order.</summary>
        internal static string Obj(params KeyValuePair<string, string>[] members)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < members.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Str(members[i].Key)).Append(':').Append(members[i].Value);
            }
            return sb.Append('}').ToString();
        }

        private static string Arr(IList<string> serializedItems)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < serializedItems.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(serializedItems[i]);
            }
            return sb.Append(']').ToString();
        }

        private static KeyValuePair<string, string> M(string key, string value)
            => new KeyValuePair<string, string>(key, value);

        /// <summary>The top-level layout: one member per line, two-space indent.</summary>
        private static string TopLevel(List<KeyValuePair<string, string>> members)
        {
            var sb = new StringBuilder("{\n");
            for (int i = 0; i < members.Count; i++)
            {
                sb.Append("  ").Append(Str(members[i].Key)).Append(": ").Append(members[i].Value);
                sb.Append(i < members.Count - 1 ? ",\n" : "\n");
            }
            return sb.Append("}\n").ToString();
        }

        /// <summary>An array of objects laid out one element per line under a top-level key.</summary>
        private static string LinedArray(IList<string> serializedItems)
        {
            if (serializedItems.Count == 0) return "[]";
            var sb = new StringBuilder("[\n");
            for (int i = 0; i < serializedItems.Count; i++)
            {
                sb.Append("    ").Append(serializedItems[i]);
                sb.Append(i < serializedItems.Count - 1 ? ",\n" : "\n");
            }
            return sb.Append("  ]").ToString();
        }

        // ----- blocks -----

        internal static string MeasureBlock()
        {
            return Obj(
                M("coverageAlpha", Num(PartParityMetrics.CoverageAlpha)),
                M("tolerancePx", Int(PartParityMetrics.TolerancePx)),
                M("blobConnectivity", Int(PartParityMetrics.BlobConnectivity)),
                M("colorMetric", Str(ColorMetricToken)),
                M("colorThreshold", Int(PartParityMetrics.DefaultColorThreshold)),
                M("colorBlockGrid", Int(PartParityMetrics.ColorBlockGrid)),
                M("minBlockOverlapFraction", Num(PartParityMetrics.MinBlockOverlapFraction)));
        }

        internal static string SkinBlock(PartParitySkinProof s)
        {
            if (s == null) return "null";
            return Obj(
                M("module", Str(s.Module)), M("moduleId", Str(s.ModuleId)),
                M("requested", Str(s.Requested)), M("real", Str(s.Real)),
                M("snapshot", Str(s.Snapshot)), M("ghost", Str(s.Ghost)));
        }

        internal static string BoxBlock(PartParityBox b)
        {
            if (b == null) return "null";
            return Obj(M("center", NumArray(b.Center)), M("extents", NumArray(b.Extents)));
        }

        internal static string FramingBlock(PartParityFraming f)
        {
            if (f == null) return "null";
            return Obj(
                M("real", BoxBlock(f.Real)), M("ghost", BoxBlock(f.Ghost)),
                M("shared", BoxBlock(f.Shared)), M("orthoSize", Num(f.OrthoSize)));
        }

        internal static string AtlasBlock(PartParityAtlas a)
        {
            if (a == null) return "null";
            return Obj(
                M("file", Str(a.File)), M("thumb", Str(a.Thumb)), M("tile", Int(a.Tile)),
                M("rows", StrArray(a.Rows)), M("cols", StrArray(a.Cols)));
        }

        internal static string MatteBlock(PartParityMatteStats? s)
        {
            if (!s.HasValue) return "null";
            return Obj(
                M("opaque", Int(s.Value.Opaque)), M("edge", Int(s.Value.Edge)),
                M("inverted", Int(s.Value.Inverted)));
        }

        private static string Pair(string real, string ghost)
            => Obj(M("real", real), M("ghost", ghost));

        internal static string ViewBlock(PartParityViewRecord v)
        {
            if (v == null) return "null";
            PartParityViewMeasures m = v.Measures ?? new PartParityViewMeasures();
            return Obj(
                M("view", Str(v.View)),
                M("width", Int(m.Width)), M("height", Int(m.Height)),
                M("realPx", Int(m.RealPx)), M("ghostPx", Int(m.GhostPx)),
                M("andPx", Int(m.AndPx)), M("orPx", Int(m.OrPx)),
                M("realOnlyPx", Int(m.RealOnlyPx)), M("ghostOnlyPx", Int(m.GhostOnlyPx)),
                M("realOnlyTolPx", Int(m.RealOnlyTolPx)), M("ghostOnlyTolPx", Int(m.GhostOnlyTolPx)),
                M("realOnlyBlobPx", Int(m.RealOnlyBlobPx)), M("ghostOnlyBlobPx", Int(m.GhostOnlyBlobPx)),
                M("realOnlyBlobPct", Num(m.RealOnlyBlobPct)), M("ghostOnlyBlobPct", Num(m.GhostOnlyBlobPct)),
                M("bbox", Pair(IntArray(m.BboxReal), IntArray(m.BboxGhost))),
                M("centroid", Pair(NumArray(m.CentroidReal), NumArray(m.CentroidGhost))),
                M("touchesBorder", Pair(Bool(m.TouchesBorderReal), Bool(m.TouchesBorderGhost))),
                M("colorOverlapPx", Int(m.ColorOverlapPx)), M("colorOverPx", Int(m.ColorOverPx)),
                M("colorBlobPx", Int(m.ColorBlobPx)), M("colorP99", Num(m.ColorP99)),
                M("colorBlockMax", Num(m.ColorBlockMax)),
                M("iou", Num(m.Iou)),
                M("matte", Pair(MatteBlock(v.RealMatte), MatteBlock(v.GhostMatte))));
        }

        internal static string StructureBlock(PartParityStructureDiff d)
        {
            if (d == null) return "null";
            var missing = new List<string>();
            foreach (var r in d.MissingOnGhost) missing.Add(RendererRef(r));
            var extra = new List<string>();
            foreach (var r in d.ExtraOnGhost) extra.Add(RendererRef(r));
            var transform = new List<string>();
            foreach (var t in d.TransformDiffers)
                transform.Add(Obj(
                    M("key", Str(t.Key)), M("positionDeltaM", Num(t.PositionDeltaM)),
                    M("rotationDeltaDeg", Num(t.RotationDeltaDeg)), M("scaleDeltaPct", Num(t.ScaleDeltaPct))));
            return Obj(
                M("realRenderers", Int(d.RealRenderers)), M("ghostRenderers", Int(d.GhostRenderers)),
                M("pairs", Int(d.Pairs)),
                M("missingOnGhost", Arr(missing)), M("extraOnGhost", Arr(extra)),
                M("transformDiffers", Arr(transform)),
                M("materialDiffers", FieldDiffs(d.MaterialDiffers)),
                M("meshDiffers", FieldDiffs(d.MeshDiffers)),
                M("renderedDiffers", FieldDiffs(d.RenderedDiffers)));
        }

        private static string RendererRef(PartParityRendererRef r)
            => Obj(M("key", Str(r.Key)), M("mesh", Str(r.Mesh)), M("rendered", Bool(r.Rendered)),
                   M("nonVisual", Str(r.NonVisual)));

        private static string FieldDiffs(List<PartParityFieldDiff> list)
        {
            var items = new List<string>();
            if (list != null)
                foreach (var f in list)
                    items.Add(Obj(M("key", Str(f.Key)), M("field", Str(f.Field)),
                        M("real", Str(f.Real)), M("ghost", Str(f.Ghost))));
            return Arr(items);
        }

        internal static string SetupBlock(PartParitySetup s)
        {
            if (s == null) return "null";
            return Obj(M("status", Str(s.Status)), M("detail", Str(s.Detail)));
        }

        // ----- documents -----

        /// <summary>Serializes one case sidecar.</summary>
        internal static string WriteCase(PartParityCaseSidecar c)
        {
            if (c == null) c = new PartParityCaseSidecar();
            var views = new List<string>();
            foreach (var v in c.Views) views.Add(ViewBlock(v));
            var members = new List<KeyValuePair<string, string>>
            {
                M("schema", Str(CaseSchemaId)),
                M("caseId", Str(c.CaseId)),
                M("part", Str(c.Part)),
                M("title", Str(c.Title)),
                M("source", Str(c.Source)),
                M("partUrl", Str(c.PartUrl)),
                M("modelUrls", StrArray(c.ModelUrls)),
                M("category", Str(c.Category)),
                M("tags", StrArray(c.Tags)),
                M("skin", SkinBlock(c.Skin)),
                M("state", Str(c.State)),
                M("context", Str(c.Context)),
                M("measure", MeasureBlock()),
                M("atlas", AtlasBlock(c.Atlas)),
                M("framing", FramingBlock(c.Framing)),
                M("ghostRootOffsetM", Num(c.GhostRootOffsetM)),
                M("layers", Pair(IntArray(c.LayersReal), IntArray(c.LayersGhost))),
                M("flags", StrArray(c.Flags)),
                M("views", LinedArray(views)),
                M("structure", StructureBlock(c.Structure)),
                M("setup", SetupBlock(c.Setup)),
                M("log", StrArray(c.Log)),
            };
            return TopLevel(members);
        }

        /// <summary>
        /// The sidecar of a case a previous process crashed on (design 5.3): identity and
        /// <c>setup.status = crashed</c>, no pictures, no measures. Writing it makes the case
        /// complete, so the resume rule skips it from then on.
        /// </summary>
        internal static PartParityCaseSidecar BuildCrashedSidecar(
            PartParityCaseKey key, string caseId, string detail)
        {
            return new PartParityCaseSidecar
            {
                CaseId = caseId,
                Part = key.Part,
                Skin = PartParitySkinRules.RequestOnlyProof(key),
                State = key.State,
                Context = key.Context,
                Setup = new PartParitySetup
                {
                    Status = TestCommandPartParitySweep.SetupCrashed,
                    Detail = detail,
                },
            };
        }

        /// <summary>Serializes the manifest.</summary>
        internal static string WriteManifest(PartParityManifest mf)
        {
            if (mf == null) mf = new PartParityManifest();
            var settings = new StringBuilder("{");
            for (int i = 0; i < mf.Settings.Count; i++)
            {
                if (i > 0) settings.Append(',');
                settings.Append(Str(mf.Settings[i].Key)).Append(':').Append(Str(mf.Settings[i].Value));
            }
            settings.Append('}');

            var controls = new List<string>();
            foreach (var c in mf.Controls)
                controls.Add(Obj(M("id", Str(c.Id)), M("subject", Str(c.Subject)),
                    M("required", Str(c.Required)), M("outcome", Str(c.Outcome)),
                    M("detail", Str(c.Detail))));
            var cases = new List<string>();
            foreach (var s in mf.Cases)
                cases.Add(Obj(
                    M("index", Int(s.Index)), M("caseId", Str(s.CaseId)), M("part", Str(s.Part)),
                    M("title", Str(s.Title)), M("source", Str(s.Source)), M("skin", Str(s.Skin)),
                    M("state", Str(s.State)), M("context", Str(s.Context)),
                    M("sidecar", Str(s.Sidecar)), M("setupStatus", Str(s.SetupStatus)),
                    M("maxShapeBlobPx", IntOrNull(s.MaxShapeBlobPx)), M("maxColorBlobPx", IntOrNull(s.MaxColorBlobPx)),
                    M("minIou", Num(s.MinIou)), M("missingRendered", IntOrNull(s.MissingRendered)),
                    M("extraRendered", IntOrNull(s.ExtraRendered))));
            var exclusions = new List<string>();
            foreach (var e in mf.Exclusions)
                exclusions.Add(Obj(M("part", Str(e.Part)), M("reason", Str(e.Reason))));

            string progress = "null";
            if (mf.Progress != null)
            {
                PartParityProgress p = mf.Progress;
                progress = Obj(
                    M("complete", Bool(p.Complete)), M("cases", Int(p.Cases)),
                    M("completeCases", Int(p.CompleteCases)), M("setupFaults", Int(p.SetupFaults)),
                    M("controls", Str(p.Controls)), M("crashed", StrArray(p.Crashed)),
                    M("hostDamaged", Bool(p.HostDamaged)), M("hostDamage", Str(p.HostDamage)),
                    M("hostDamageDetail", Str(p.HostDamageDetail)));
            }

            var members = new List<KeyValuePair<string, string>>
            {
                M("schema", Str(ManifestSchemaId)),
                M("label", Str(mf.Label)),
                M("profile", Str(mf.Profile)),
                M("gameDataMods", StrArray(mf.GameDataMods)),
                M("dllHash", Str(mf.DllHash)),
                M("tile", Int(mf.Tile)),
                M("views", StrArray(mf.Views)),
                M("measure", MeasureBlock()),
                M("settings", settings.ToString()),
                M("controls", LinedArray(controls)),
                M("cases", LinedArray(cases)),
                M("exclusions", LinedArray(exclusions)),
                M("progress", progress),
            };
            return TopLevel(members);
        }
    }
}
