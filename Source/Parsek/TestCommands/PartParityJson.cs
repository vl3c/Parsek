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

        /// <summary>A state case's start route (real vs ghost-start), the full measure set of a
        /// view (its <see cref="View"/> is not written); null for a stowed case (design D.2).</summary>
        internal PartParityViewRecord Start;

        /// <summary>A state case's stowed reference (real vs ghost-stowed), written with the
        /// shape keys only (<see cref="PartParityJson.StowedRefKeys"/>); null for a stowed case.</summary>
        internal PartParityViewMeasures StowedRef;
    }

    /// <summary>One recorded part event of a state case's event route.</summary>
    internal sealed class PartParityStateEvent
    {
        internal string Type;
        internal double? Ut;
        internal uint? Pid;
        internal int? Midx;
    }

    /// <summary>One context filler: the attach node and the part put on it.</summary>
    internal sealed class PartParityContextNode
    {
        internal string Node;
        internal string Part;
    }

    /// <summary>The <c>stateProof</c> block of a state or context case (design D.2): what was
    /// pressed, whether the state was reached, what the event route recorded and how each
    /// ghost route read. Every key is always written; null when not measured.</summary>
    internal sealed class PartParityStateProof
    {
        internal string Family;
        internal string Module;
        internal int? ModuleIndex;
        internal string State;
        internal string Action;
        internal bool? EventVisible;

        /// <summary>One of <see cref="PartParityStateRules.ActionResults"/>.</summary>
        internal string ActionResult;
        internal bool? Reached;
        internal double? WaitS;

        /// <summary>The stock field the done signal read, and its value.</summary>
        internal string EndpointField;
        internal string EndpointValue;

        /// <summary>The event route's recorded events; null when not measured.</summary>
        internal List<PartParityStateEvent> Events;

        /// <summary>One of <see cref="PartParityStateRules.GhostOutcomes"/> per route.</summary>
        internal string GhostEvent;
        internal string GhostStart;

        /// <summary>The start route at its first frame (before any stepped prime).</summary>
        internal bool? StartFirstFrameAtTarget;
        internal double? StartFirstFrameTrackerDeg;

        /// <summary>A sun tracker's target angle, the event route's angle and the start
        /// route's first-frame angle, degrees.</summary>
        internal double? SunTargetDeg;
        internal double? SunEventDeg;
        internal double? SunStartFirstFrameDeg;

        /// <summary>An air case's canopy airflow tilt and wobble at capture, degrees.</summary>
        internal double? CanopyTiltDeg;
        internal double? CanopyWobbleDeg;

        /// <summary>An air case's canopy transform name (<c>ModuleParachute.canopyName</c>);
        /// the judge's canopy cap reads renderers at or under it (design D.4, D.6).</summary>
        internal string CanopyTransform;

        /// <summary>The context's filler parts by node; null when the context has none.</summary>
        internal List<PartParityContextNode> Nodes;
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

        /// <summary>The state proof (design D.2); null for a stowed case.</summary>
        internal PartParityStateProof StateProof;

        /// <summary>The structural diff of the start-route ghost; null for a stowed case.</summary>
        internal PartParityStructureDiff StructureStart;

        /// <summary>The id of the same part and skin at <c>stowed</c> / <c>alone</c>; null for a stowed case.</summary>
        internal string StowedCaseId;

        /// <summary>An FX case's <c>fx</c>, <c>fxAtlas</c> and <c>strip</c> blocks (design E.5) as
        /// JSON fragments per key of <see cref="PartParityJson.FxKeys"/>,
        /// <see cref="PartParityJson.FxAtlasKeys"/> and <see cref="PartParityJson.FxStripKeys"/>
        /// (<see cref="PartParityJson.KeyedBlock"/> writes every key, null when absent); null for
        /// every other case.</summary>
        internal Dictionary<string, string> Fx;
        internal Dictionary<string, string> FxAtlas;
        internal Dictionary<string, string> Strip;
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

        /// <summary>A state case's family, press result and whether it reached its state
        /// (the setup summary of design D.2); null for a stowed case.</summary>
        internal string Family;
        internal string ActionResult;
        internal bool? Reached;
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
            "stateProof", "structureStart", "stowedCaseId",
            "fx", "fxAtlas", "strip",
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
            "start", "stowedRef",
        };

        /// <summary>The keys of a view's <c>start</c> block: the full measure set of a view,
        /// without its name.</summary>
        internal static readonly string[] StartKeys =
        {
            "width", "height",
            "realPx", "ghostPx", "andPx", "orPx", "realOnlyPx", "ghostOnlyPx",
            "realOnlyTolPx", "ghostOnlyTolPx", "realOnlyBlobPx", "ghostOnlyBlobPx",
            "realOnlyBlobPct", "ghostOnlyBlobPct",
            "bbox", "centroid", "touchesBorder",
            "colorOverlapPx", "colorOverPx", "colorBlobPx", "colorP99", "colorBlockMax",
            "iou", "matte",
        };

        /// <summary>The keys of a view's <c>stowedRef</c> block, exactly the shape keys the
        /// recompute checks for it (design D.2, D.6): the masks' pixel counts, the one-sided
        /// counts, the tolerant mismatch counts and the two largest blobs with their percentages.</summary>
        internal static readonly string[] StowedRefKeys =
        {
            "realPx", "ghostPx", "realOnlyPx", "ghostOnlyPx", "realOnlyTolPx", "ghostOnlyTolPx",
            "realOnlyBlobPx", "ghostOnlyBlobPx", "realOnlyBlobPct", "ghostOnlyBlobPct",
        };

        internal static readonly string[] StateProofKeys =
        {
            "family", "module", "moduleIndex", "state", "action", "eventVisible", "actionResult",
            "reached", "waitS", "endpoint", "events", "ghostEvent", "ghostStart",
            "startFirstFrame", "sunTracker", "canopy", "nodes",
        };

        internal static readonly string[] EndpointKeys = { "field", "value" };

        internal static readonly string[] StateEventKeys = { "type", "ut", "pid", "midx" };

        internal static readonly string[] StartFirstFrameKeys = { "atTarget", "trackerDeg" };

        internal static readonly string[] SunTrackerKeys = { "targetDeg", "eventDeg", "startFirstFrameDeg" };

        internal static readonly string[] CanopyKeys = { "tiltDeg", "wobbleDeg", "transform" };

        internal static readonly string[] ContextNodeKeys = { "node", "part" };

        // ----- the FX blocks (design E.5), every key always written, null when not measured -----

        /// <summary>The keys of an FX case's <c>fx</c> block.</summary>
        internal static readonly string[] FxKeys =
        {
            "family", "module", "moduleIndex", "engineMode", "throttle", "drive", "warmupS", "plateau",
            "hold", "rcs", "samples", "framing", "layers", "cullingMask", "views", "particles",
            "structure", "inventory", "plate", "reference",
        };

        internal static readonly string[] FxDriveKeys = { "ignited", "finalThrust", "currentThrottle", "recordedPower", "cheat" };

        /// <summary><c>warmupS</c>, <c>plateau</c>, <c>particles</c>, <c>layers</c> and
        /// <c>inventory</c> are keyed by side (<c>ghostAll</c> on an rcs case only).</summary>
        internal static readonly string[] FxSideKeys = { "real", "ghost", "ghostStart", "ghostAll" };

        internal static readonly string[] FxPlateauKeys = { "signal", "settled", "waitS" };

        internal static readonly string[] FxHoldKeys = { "maxDriftM", "stepM", "steps" };

        internal static readonly string[] FxRcsKeys = { "input", "predicted", "fired", "nozzles", "ghostHidden", "powerRatio" };

        internal static readonly string[] FxFramingKeys = { "near", "far", "clamped" };

        /// <summary>One side's renderer keys per layer.</summary>
        internal static readonly string[] FxLayerKeys = { "body", "flame", "smoke" };

        /// <summary>One <c>fx.views[]</c> entry: a route, FX view and layer.</summary>
        internal static readonly string[] FxViewKeys =
            { "view", "route", "layer", "width", "height", "axis", "real", "ghost", "compare", "splitHalf" };

        internal static readonly string[] FxAxisKeys = { "originPx", "dirPx" };

        /// <summary>One side's measures of one tile, all from the 8-bit tile.</summary>
        internal static readonly string[] FxSideMeasureKeys =
        {
            "corePx", "envelopePx", "saturatedPx", "originPx", "lengthPx", "widthPx", "coreCentroid",
            "meanColor", "chromaticity", "luminance", "hueHistogram",
        };

        /// <summary>The ghost-minus-real comparison of one tile (the E.4 measures).</summary>
        internal static readonly string[] FxCompareKeys =
        {
            "originDeltaPx", "lateralOffsetPx", "lengthRatio", "widthRatio", "directionDeg",
            "crossSectionAreaRatio", "crossSectionOffset", "chromaDistance", "hueL1", "brightnessRatio",
            "coreIou",
        };

        internal static readonly string[] FxSplitHalfKeys = { "even", "odd" };

        internal static readonly string[] FxHalfKeys = { "samples", "real", "ghost", "compare" };

        internal static readonly string[] FxParticleKeys = { "count", "speed", "direction", "size", "alongP95", "acrossP95" };

        internal static readonly string[] FxStructureKeys = { "fxMissingOnGhost", "fxExtraOnGhost", "fxDiffers", "lightsDiffer" };

        internal static readonly string[] FxPlateKeys = { "litPct", "lightType", "range", "spotAngle", "cullingMask" };

        internal static readonly string[] FxAtlasKeys = { "file", "rows", "cols", "tile" };

        internal static readonly string[] FxStripKeys = { "file", "frames" };

        // ----- atlas rows (design D.2) -----

        /// <summary>A stowed case's atlas rows, top-down.</summary>
        internal static readonly string[] StowedAtlasRows = { "real", "ghost" };

        /// <summary>A state case's atlas rows, top-down: the real part, the event-route ghost,
        /// the start-route ghost, the stowed reference ghost.</summary>
        internal static readonly string[] StateAtlasRows = { "real", "ghost", "ghost-start", "ghost-stowed" };

        /// <summary>The atlas rows of a case (a fresh copy): four for a state case, two for a
        /// stowed one. The live capture reads its rows here, so it holds no literal of its own.</summary>
        internal static string[] AtlasRowsFor(bool isStateCase)
            => (string[])(isStateCase ? StateAtlasRows : StowedAtlasRows).Clone();

        /// <summary>An FX case's FX atlas rows, top-down (design E.5); its columns are
        /// <see cref="PartParityFxRules.Views"/>.</summary>
        internal static readonly string[] FxAtlasRows =
            { "real-flame", "ghost-flame", "ghost-start-flame", "real-smoke", "ghost-smoke", "ghost-start-smoke" };

        /// <summary>An rcs case's FX atlas rows: the six, then the all-nozzle ghost (R4).</summary>
        internal static readonly string[] RcsFxAtlasRows =
        {
            "real-flame", "ghost-flame", "ghost-start-flame", "real-smoke", "ghost-smoke", "ghost-start-smoke",
            "ghost-all-flame", "ghost-all-smoke",
        };

        /// <summary>The FX atlas rows of a case (a fresh copy); the live capture reads them here.</summary>
        internal static string[] FxAtlasRowsFor(bool isRcsCase)
            => (string[])(isRcsCase ? RcsFxAtlasRows : FxAtlasRows).Clone();

        /// <summary>The strip's rows (design E.5): the real part and the event-route ghost.</summary>
        internal static readonly string[] StripRows = { "real", "ghost" };

        internal static readonly string[] MatteKeys = { "opaque", "edge", "inverted" };

        internal static readonly string[] StructureKeys =
        {
            "realRenderers", "ghostRenderers", "pairs", "missingOnGhost", "extraOnGhost",
            "transformDiffers", "materialDiffers", "meshDiffers", "renderedDiffers",
        };

        internal static readonly string[] RendererRefKeys = { "key", "mesh", "rendered", "nonVisual" };

        internal static readonly string[] TransformDiffKeys =
        {
            "key", "positionDeltaM", "rotationDeltaDeg", "scaleDeltaPct", "canopyPivotDistanceM",
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
            "extraRendered", "family", "actionResult", "reached",
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
            var members = new List<KeyValuePair<string, string>> { M("view", Str(v.View)) };
            members.AddRange(MeasureMembers(v));
            members.Add(M("start", v.Start == null ? "null" : Obj(MeasureMembers(v.Start).ToArray())));
            members.Add(M("stowedRef", StowedRefBlock(v.StowedRef)));
            return Obj(members.ToArray());
        }

        /// <summary>A view's measure members, <c>width</c> to <c>matte</c> (<see cref="StartKeys"/>).</summary>
        private static List<KeyValuePair<string, string>> MeasureMembers(PartParityViewRecord v)
        {
            PartParityViewMeasures m = v.Measures ?? new PartParityViewMeasures();
            return new List<KeyValuePair<string, string>>
            {
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
                M("matte", Pair(MatteBlock(v.RealMatte), MatteBlock(v.GhostMatte))),
            };
        }

        /// <summary>The <c>stowedRef</c> block: only <see cref="StowedRefKeys"/>.</summary>
        internal static string StowedRefBlock(PartParityViewMeasures m)
        {
            if (m == null) return "null";
            return Obj(
                M("realPx", Int(m.RealPx)), M("ghostPx", Int(m.GhostPx)),
                M("realOnlyPx", Int(m.RealOnlyPx)), M("ghostOnlyPx", Int(m.GhostOnlyPx)),
                M("realOnlyTolPx", Int(m.RealOnlyTolPx)), M("ghostOnlyTolPx", Int(m.GhostOnlyTolPx)),
                M("realOnlyBlobPx", Int(m.RealOnlyBlobPx)), M("ghostOnlyBlobPx", Int(m.GhostOnlyBlobPx)),
                M("realOnlyBlobPct", Num(m.RealOnlyBlobPct)), M("ghostOnlyBlobPct", Num(m.GhostOnlyBlobPct)));
        }

        private static string BoolOrNull(bool? v) => v.HasValue ? Bool(v.Value) : "null";

        private static string UIntOrNull(uint? v) => v.HasValue ? v.Value.ToString(Ic) : "null";

        /// <summary>The <c>stateProof</c> block (<see cref="StateProofKeys"/>); null for a stowed case.</summary>
        internal static string StateProofBlock(PartParityStateProof p)
        {
            if (p == null) return "null";
            string events = "null";
            if (p.Events != null)
            {
                var items = new List<string>();
                foreach (var e in p.Events)
                    if (e != null)
                        items.Add(Obj(M("type", Str(e.Type)), M("ut", Num(e.Ut)),
                            M("pid", UIntOrNull(e.Pid)), M("midx", IntOrNull(e.Midx))));
                events = Arr(items);
            }
            string nodes = "null";
            if (p.Nodes != null)
            {
                var items = new List<string>();
                foreach (var n in p.Nodes)
                    if (n != null) items.Add(Obj(M("node", Str(n.Node)), M("part", Str(n.Part))));
                nodes = Arr(items);
            }
            return Obj(
                M("family", Str(p.Family)), M("module", Str(p.Module)),
                M("moduleIndex", IntOrNull(p.ModuleIndex)), M("state", Str(p.State)),
                M("action", Str(p.Action)), M("eventVisible", BoolOrNull(p.EventVisible)),
                M("actionResult", Str(p.ActionResult)), M("reached", BoolOrNull(p.Reached)),
                M("waitS", Num(p.WaitS)),
                M("endpoint", Obj(M("field", Str(p.EndpointField)), M("value", Str(p.EndpointValue)))),
                M("events", events),
                M("ghostEvent", Str(p.GhostEvent)), M("ghostStart", Str(p.GhostStart)),
                M("startFirstFrame", Obj(M("atTarget", BoolOrNull(p.StartFirstFrameAtTarget)),
                    M("trackerDeg", Num(p.StartFirstFrameTrackerDeg)))),
                M("sunTracker", Obj(M("targetDeg", Num(p.SunTargetDeg)), M("eventDeg", Num(p.SunEventDeg)),
                    M("startFirstFrameDeg", Num(p.SunStartFirstFrameDeg)))),
                M("canopy", Obj(M("tiltDeg", Num(p.CanopyTiltDeg)), M("wobbleDeg", Num(p.CanopyWobbleDeg)),
                    M("transform", Str(p.CanopyTransform)))),
                M("nodes", nodes));
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
                    M("rotationDeltaDeg", Num(t.RotationDeltaDeg)), M("scaleDeltaPct", Num(t.ScaleDeltaPct)),
                    M("canopyPivotDistanceM", Num(t.CanopyPivotDistanceM))));
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
                M("stateProof", StateProofBlock(c.StateProof)),
                M("structureStart", StructureBlock(c.StructureStart)),
                M("stowedCaseId", Str(c.StowedCaseId)),
                M("fx", KeyedBlock(FxKeys, c.Fx)),
                M("fxAtlas", KeyedBlock(FxAtlasKeys, c.FxAtlas)),
                M("strip", KeyedBlock(FxStripKeys, c.Strip)),
            };
            return TopLevel(members);
        }

        /// <summary>
        /// One block of a key table: <c>null</c> when <paramref name="fragments"/> is null, else
        /// an object with every key of <paramref name="keys"/> in table order, each the JSON
        /// fragment given for it or <c>null</c>. A fragment under a key the table does not hold
        /// is not written (<see cref="UnknownKeys"/> names them for a test).
        /// </summary>
        internal static string KeyedBlock(string[] keys, IDictionary<string, string> fragments)
        {
            if (fragments == null) return "null";
            var members = new KeyValuePair<string, string>[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string v = fragments.TryGetValue(keys[i], out string f) && !string.IsNullOrEmpty(f) ? f : "null";
                members[i] = M(keys[i], v);
            }
            return Obj(members);
        }

        /// <summary>The keys of <paramref name="fragments"/> that <paramref name="keys"/> lacks, in ordinal order.</summary>
        internal static List<string> UnknownKeys(string[] keys, IDictionary<string, string> fragments)
        {
            var unknown = new List<string>();
            if (fragments == null) return unknown;
            foreach (string k in fragments.Keys)
                if (System.Array.IndexOf(keys, k) < 0) unknown.Add(k);
            unknown.Sort(string.CompareOrdinal);
            return unknown;
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
                    M("extraRendered", IntOrNull(s.ExtraRendered)), M("family", Str(s.Family)),
                    M("actionResult", Str(s.ActionResult)), M("reached", BoolOrNull(s.Reached))));
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
