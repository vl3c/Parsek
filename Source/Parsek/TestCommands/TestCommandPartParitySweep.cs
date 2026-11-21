using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>The <c>op=</c> of a sweep step.</summary>
    internal enum PartParitySweepOp
    {
        /// <summary>Clear the label directory (the lane's first step).</summary>
        Reset,

        /// <summary>Process cases in the stable order until the time box ends (resumes).</summary>
        Run,
    }

    /// <summary>The <c>skins=</c> selection.</summary>
    internal enum PartParitySkinSelection
    {
        /// <summary>The base skin of each part only.</summary>
        Default,

        /// <summary>Every skin a part offers.</summary>
        All,
    }

    /// <summary>What the resume rule decides for one case at the start of its turn (design 5.3).</summary>
    internal enum PartParityCaseResume
    {
        /// <summary>No complete sidecar and no crash record: capture it.</summary>
        Run,

        /// <summary>A complete sidecar exists: skip it.</summary>
        SkipComplete,

        /// <summary>A stale <c>inflight.txt</c> names it: a previous process died on it.
        /// Record it crashed (write the crashed sidecar) and skip it.</summary>
        RecordCrashed,
    }

    /// <summary>The parsed args of one <c>PartParitySweep</c> step.</summary>
    internal sealed class PartParitySweepArgs
    {
        internal string Label;
        internal PartParitySweepOp Op = PartParitySweepOp.Run;
        internal string Filter = TestCommandPartParitySweep.DefaultFilter;

        /// <summary>The <c>source=</c> csv, or null for every source.</summary>
        internal List<string> Sources;

        internal PartParitySkinSelection Skins = PartParitySkinSelection.Default;

        /// <summary>The views, in canonical order, deduplicated.</summary>
        internal List<string> Views = new List<string>(TestCommandPartParitySweep.RequiredViews);

        internal int Tile = TestCommandPartParitySweep.DefaultTile;
        internal int ShardK = 1;
        internal int ShardN = 1;

        /// <summary>The <c>limit=</c>, or 0 for no limit.</summary>
        internal int Limit;

        internal bool Controls = true;
        internal int MaxSeconds = TestCommandPartParitySweep.DefaultMaxSeconds;
    }

    /// <summary>The identity of one case before it has an id: part, skin, state, context
    /// (design section 3), plus what ordering and filtering read.</summary>
    internal sealed class PartParityCaseKey
    {
        internal string Part;

        /// <summary>The <c>source</c> attribution (first model url segment).</summary>
        internal string Source;

        internal string Skin = TestCommandPartParitySweep.DefaultSkinName;

        /// <summary>The skin's position among the part's skins, 0 for the base skin.</summary>
        internal int SkinOrdinal;

        internal string State = TestCommandPartParitySweep.DefaultState;
        internal string Context = TestCommandPartParitySweep.DefaultContext;
    }

    /// <summary>One case a step will consider, with its stable index and id.</summary>
    internal sealed class PartParityPlannedCase
    {
        /// <summary>The case's index in the stable order of the selected cases (the index the
        /// shard rule reads).</summary>
        internal int Index;

        internal PartParityCaseKey Key;
        internal string CaseId;
    }

    /// <summary>
    /// Pure half of the automation-only <c>PartParitySweep</c> seam verb
    /// (design-part-ghost-parity.md section 7): the arg grammar with its defaults and
    /// refusals, the stable case order, sharding, case ids, the resume decision, the time-box
    /// decision and the OK payload. The capture itself, the files and the wave coroutine
    /// belong to the applier.
    ///
    /// <para>Grammar: <c>PartParitySweep label=&lt;label&gt; [op=reset|run]
    /// [filter=&lt;glob&gt;] [source=&lt;csv&gt;] [skins=default|all]
    /// [views=required|all|&lt;csv&gt;] [tile=256..1024] [shard=&lt;k&gt;/&lt;n&gt;]
    /// [limit=&lt;n&gt;] [controls=true|false] [maxSeconds=60..480]</c>. Every present arg
    /// that does not parse is REJECTED with its own reason, never clamped or defaulted: a
    /// silently widened sweep would run past its step budget, a silently narrowed one would
    /// read as complete.</para>
    /// </summary>
    internal static class TestCommandPartParitySweep
    {
        internal const string VerbName = "PartParitySweep";

        // ----- files -----

        /// <summary>The sweep root under the KSP root; each label is a directory below it.</summary>
        internal const string RootDirName = "parsek-partparity";
        internal const string ManifestFileName = "manifest.json";
        internal const string InflightFileName = "inflight.txt";
        internal const string SidecarSuffix = ".part.json";
        internal const string AtlasSuffix = ".png";
        internal const string ThumbSuffix = ".thumb.png";

        internal static string SidecarFileName(string caseId) => (caseId ?? string.Empty) + SidecarSuffix;
        internal static string AtlasFileName(string caseId) => (caseId ?? string.Empty) + AtlasSuffix;
        internal static string ThumbFileName(string caseId) => (caseId ?? string.Empty) + ThumbSuffix;

        /// <summary>The label directory relative to the KSP root, forward slashes.</summary>
        internal static string RelativeLabelDir(string label) => RootDirName + "/" + (label ?? string.Empty);

        // ----- views -----

        internal const string ViewSideA = "side-a";
        internal const string ViewSideB = "side-b";
        internal const string ViewTop = "top";
        internal const string ViewBottom = "bottom";
        internal const string ViewSideC = "side-c";
        internal const string ViewSideD = "side-d";
        internal const string ViewHero = "hero";

        internal const string ViewsRequiredToken = "required";
        internal const string ViewsAllToken = "all";

        /// <summary>The four orthographic views every sweep captures.</summary>
        internal static readonly string[] RequiredViews = { ViewSideA, ViewSideB, ViewTop, ViewBottom };

        /// <summary><c>views=all</c>: the required views plus the two remaining side faces.
        /// The perspective <c>hero</c> view is opt-in by name only.</summary>
        internal static readonly string[] AllViews =
            { ViewSideA, ViewSideB, ViewTop, ViewBottom, ViewSideC, ViewSideD };

        /// <summary>Every view name, in canonical order (a csv is re-ordered to this).</summary>
        internal static readonly string[] KnownViews =
            { ViewSideA, ViewSideB, ViewTop, ViewBottom, ViewSideC, ViewSideD, ViewHero };

        // ----- defaults and ranges -----

        internal const string DefaultFilter = "*";
        internal const int DefaultTile = 512;
        internal const int MinTile = 256;
        internal const int MaxTile = 1024;
        internal const int DefaultMaxSeconds = 480;
        internal const int MinMaxSeconds = 60;
        internal const int MaxMaxSeconds = 480;
        internal const int MaxShards = 64;
        internal const int MaxLimit = 100000;
        internal const int MaxFilterLength = 128;

        internal const string DefaultSkinName = "default";
        internal const string DefaultState = "stowed";
        internal const string DefaultContext = "alone";

        // ----- case ids -----

        internal const int MaxCaseIdLength = TestCommandCaptureScreenshot.MaxLabelLength;
        internal const string CaseIdSeparator = "--";
        internal const int CaseIdHashLength = 8;

        // ----- setup statuses (sidecar setup.status) -----

        internal const string SetupOk = "ok";
        internal const string SetupSkinNotApplied = "skin-not-applied";
        internal const string SetupSpawnTimeout = "spawn-timeout";
        internal const string SetupStillAnimating = "still-animating";
        internal const string SetupDiedSettling = "died-settling";
        internal const string SetupCrashed = "crashed";

        internal static readonly string[] SetupStatuses =
        {
            SetupOk, SetupSkinNotApplied, SetupSpawnTimeout, SetupStillAnimating,
            SetupDiedSettling, SetupCrashed,
        };

        /// <summary>True for a status that is a setup fault: anything but <c>ok</c> and
        /// <c>still-animating</c>. A still-animating case is captured and measured anyway
        /// (design 4.1), so it is a tag on a real measurement, not a missing one.</summary>
        internal static bool IsSetupFault(string status)
            => !string.Equals(status, SetupOk, StringComparison.Ordinal)
               && !string.Equals(status, SetupStillAnimating, StringComparison.Ordinal);

        // ----- controls (design 4.5) -----

        internal const string ControlEmpty = "ctl-empty";
        internal const string ControlGhostAbsent = "ctl-ghost-absent";
        internal const string ControlRealVsReal = "ctl-real-vs-real";
        internal const string ControlGhostScale90 = "ctl-ghost-scale90";
        internal const string ControlGhostRot90 = "ctl-ghost-rot90";
        internal const string ControlGhostFlip = "ctl-ghost-flip";
        internal const string ControlGhostMissingMesh = "ctl-ghost-missing-mesh";
        internal const string ControlWrongSkin = "ctl-wrong-skin";

        /// <summary>The control cases, in the order they run (before every other case).</summary>
        internal static readonly string[] ControlIds =
        {
            ControlEmpty, ControlGhostAbsent, ControlRealVsReal, ControlGhostScale90,
            ControlGhostRot90, ControlGhostFlip, ControlGhostMissingMesh, ControlWrongSkin,
        };

        internal const string ControlsOk = "ok";
        internal const string ControlsBad = "bad";
        internal const string ControlsPending = "pending";

        /// <summary><c>controls=false</c>: the step ran no controls (a dev run only; a lane
        /// whose controls never ran cannot pass).</summary>
        internal const string ControlsOff = "off";

        // ----- refusals -----

        internal const string LabelArgMissingReason = TestCommandCaptureScreenshot.LabelArgMissingReason;
        internal const string LabelArgInvalidReason = TestCommandCaptureScreenshot.LabelArgInvalidReason;
        internal const string OpArgInvalidReason = "op-arg-invalid";
        internal const string FilterArgInvalidReason = "filter-arg-invalid";
        internal const string SourceArgInvalidReason = "source-arg-invalid";
        internal const string SkinsArgInvalidReason = "skins-arg-invalid";
        internal const string ViewsArgInvalidReason = "views-arg-invalid";
        internal const string TileArgInvalidReason = "tile-arg-invalid";
        internal const string ShardArgInvalidReason = "shard-arg-invalid";
        internal const string LimitArgInvalidReason = "limit-arg-invalid";
        internal const string ControlsArgInvalidReason = "controls-arg-invalid";
        internal const string MaxSecondsArgInvalidReason = "maxseconds-arg-invalid";

        /// <summary>REJECTED: FLIGHT with no active vessel.</summary>
        internal const string NoActiveVesselReason = "no-active-vessel";

        /// <summary>REJECTED: the active vessel is not in a stable orbit (landed, splashed,
        /// suborbital, escaping): the co-orbital spawns need an orbiting host.</summary>
        internal const string HostNotOrbitingReason = "host-not-orbiting";

        /// <summary>The REJECTED vocabulary, in parse-then-admission order.</summary>
        internal static readonly string[] Reasons =
        {
            LabelArgMissingReason, LabelArgInvalidReason, OpArgInvalidReason,
            FilterArgInvalidReason, SourceArgInvalidReason, SkinsArgInvalidReason,
            ViewsArgInvalidReason, TileArgInvalidReason, ShardArgInvalidReason,
            LimitArgInvalidReason, ControlsArgInvalidReason, MaxSecondsArgInvalidReason,
            NoActiveVesselReason, HostNotOrbitingReason,
        };

        /// <summary>Every arg key the verb reads.</summary>
        internal static readonly string[] ArgKeys =
        {
            "label", "op", "filter", "source", "skins", "views", "tile", "shard", "limit",
            "controls", "maxSeconds",
        };

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // ----- arg parse -----

        private static string Get(IDictionary<string, string> args, string key)
            => args != null && args.TryGetValue(key, out string v) ? v : null;

        /// <summary>
        /// Parses every arg of one step. On a refusal <paramref name="rejectReason"/> is the
        /// reason token and <paramref name="rejectDetail"/> the offending <c>key=value</c>.
        /// </summary>
        internal static bool TryParseArgs(
            IDictionary<string, string> args, out PartParitySweepArgs parsed,
            out string rejectReason, out string rejectDetail)
        {
            parsed = null;
            rejectDetail = null;
            var a = new PartParitySweepArgs();

            string raw = Get(args, "label");
            if (!TestCommandCaptureScreenshot.TryParseLabel(raw, out string label, out rejectReason))
                return Reject("label", raw, out rejectDetail);
            a.Label = label;

            raw = Get(args, "op");
            if (raw != null)
            {
                if (raw == "reset") a.Op = PartParitySweepOp.Reset;
                else if (raw == "run") a.Op = PartParitySweepOp.Run;
                else { rejectReason = OpArgInvalidReason; return Reject("op", raw, out rejectDetail); }
            }

            raw = Get(args, "filter");
            if (raw != null)
            {
                if (!IsValidFilter(raw)) { rejectReason = FilterArgInvalidReason; return Reject("filter", raw, out rejectDetail); }
                a.Filter = raw;
            }

            raw = Get(args, "source");
            if (raw != null)
            {
                if (!TryParseSourceCsv(raw, out List<string> sources))
                { rejectReason = SourceArgInvalidReason; return Reject("source", raw, out rejectDetail); }
                a.Sources = sources;
            }

            raw = Get(args, "skins");
            if (raw != null)
            {
                if (raw == "default") a.Skins = PartParitySkinSelection.Default;
                else if (raw == "all") a.Skins = PartParitySkinSelection.All;
                else { rejectReason = SkinsArgInvalidReason; return Reject("skins", raw, out rejectDetail); }
            }

            raw = Get(args, "views");
            if (raw != null)
            {
                if (!TryParseViews(raw, out List<string> views))
                { rejectReason = ViewsArgInvalidReason; return Reject("views", raw, out rejectDetail); }
                a.Views = views;
            }

            raw = Get(args, "tile");
            if (raw != null)
            {
                if (!TryParseIntInRange(raw, MinTile, MaxTile, out int tile))
                { rejectReason = TileArgInvalidReason; return Reject("tile", raw, out rejectDetail); }
                a.Tile = tile;
            }

            raw = Get(args, "shard");
            if (raw != null)
            {
                if (!TryParseShard(raw, out int k, out int n))
                { rejectReason = ShardArgInvalidReason; return Reject("shard", raw, out rejectDetail); }
                a.ShardK = k;
                a.ShardN = n;
            }

            raw = Get(args, "limit");
            if (raw != null)
            {
                if (!TryParseIntInRange(raw, 1, MaxLimit, out int limit))
                { rejectReason = LimitArgInvalidReason; return Reject("limit", raw, out rejectDetail); }
                a.Limit = limit;
            }

            raw = Get(args, "controls");
            if (raw != null)
            {
                if (raw == "true") a.Controls = true;
                else if (raw == "false") a.Controls = false;
                else { rejectReason = ControlsArgInvalidReason; return Reject("controls", raw, out rejectDetail); }
            }

            raw = Get(args, "maxSeconds");
            if (raw != null)
            {
                if (!TryParseIntInRange(raw, MinMaxSeconds, MaxMaxSeconds, out int maxSeconds))
                { rejectReason = MaxSecondsArgInvalidReason; return Reject("maxSeconds", raw, out rejectDetail); }
                a.MaxSeconds = maxSeconds;
            }

            parsed = a;
            rejectReason = null;
            return true;
        }

        private static bool Reject(string key, string raw, out string detail)
        {
            detail = key + "=" + (raw ?? string.Empty);
            return false;
        }

        /// <summary>A decimal integer in [min, max]; no sign, no whitespace, no other culture.</summary>
        internal static bool TryParseIntInRange(string raw, int min, int max, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            if (!int.TryParse(raw, NumberStyles.None, Ic, out int v)) return false;
            if (v < min || v > max) return false;
            value = v;
            return true;
        }

        /// <summary><c>k/n</c> with 1 &lt;= n &lt;= <see cref="MaxShards"/> and 1 &lt;= k &lt;= n.</summary>
        internal static bool TryParseShard(string raw, out int k, out int n)
        {
            k = 1;
            n = 1;
            if (string.IsNullOrEmpty(raw)) return false;
            int slash = raw.IndexOf('/');
            if (slash <= 0 || slash != raw.LastIndexOf('/')) return false;
            if (!TryParseIntInRange(raw.Substring(slash + 1), 1, MaxShards, out int nn)) return false;
            if (!TryParseIntInRange(raw.Substring(0, slash), 1, nn, out int kk)) return false;
            k = kk;
            n = nn;
            return true;
        }

        /// <summary>A glob over part names: 1..<see cref="MaxFilterLength"/> chars of
        /// <c>[A-Za-z0-9._-]</c> plus the wildcards <c>*</c> and <c>?</c>.</summary>
        internal static bool IsValidFilter(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > MaxFilterLength) return false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (!(IsIdChar(c) || c == '*' || c == '?')) return false;
            }
            return true;
        }

        /// <summary>A non-empty csv of source names (<c>[A-Za-z0-9._-]</c>, no empty item);
        /// duplicates are dropped, order kept.</summary>
        internal static bool TryParseSourceCsv(string raw, out List<string> sources)
        {
            sources = null;
            if (string.IsNullOrEmpty(raw)) return false;
            var list = new List<string>();
            foreach (string item in raw.Split(','))
            {
                if (item.Length == 0) return false;
                for (int i = 0; i < item.Length; i++)
                    if (!IsIdChar(item[i])) return false;
                bool dup = false;
                foreach (string s in list)
                    if (string.Equals(s, item, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) list.Add(item);
            }
            sources = list;
            return true;
        }

        /// <summary><c>required</c>, <c>all</c>, or a csv of <see cref="KnownViews"/> names
        /// (re-ordered to the canonical order, duplicates dropped).</summary>
        internal static bool TryParseViews(string raw, out List<string> views)
        {
            views = null;
            if (raw == ViewsRequiredToken) { views = new List<string>(RequiredViews); return true; }
            if (raw == ViewsAllToken) { views = new List<string>(AllViews); return true; }
            if (string.IsNullOrEmpty(raw)) return false;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string item in raw.Split(','))
            {
                if (Array.IndexOf(KnownViews, item) < 0) return false;
                wanted.Add(item);
            }
            views = new List<string>();
            foreach (string v in KnownViews)
                if (wanted.Contains(v)) views.Add(v);
            return true;
        }

        private static bool IsIdChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
               || c == '.' || c == '_' || c == '-';

        /// <summary>Glob match, <c>*</c> any run and <c>?</c> one char, case-insensitive
        /// (ordinal, invariant upper-casing).</summary>
        internal static bool GlobMatch(string pattern, string text)
        {
            if (pattern == null || text == null) return false;
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || CharEq(pattern[p], text[t])))
                {
                    p++;
                    t++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = t;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++mark;
                }
                else
                {
                    return false;
                }
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private static bool CharEq(char a, char b)
            => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

        // ----- order, selection, sharding -----

        /// <summary>The stable case order: part name (ordinal), skin ordinal, skin name, state,
        /// context (all ordinal). Independent of enumeration order, so two runs of one install
        /// number the cases the same.</summary>
        internal static int CompareCases(PartParityCaseKey a, PartParityCaseKey b)
        {
            int c = string.CompareOrdinal(a.Part, b.Part);
            if (c != 0) return c;
            c = a.SkinOrdinal.CompareTo(b.SkinOrdinal);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Skin, b.Skin);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.State, b.State);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Context, b.Context);
        }

        /// <summary>
        /// The cases one step considers, in stable order: every case that passes
        /// <c>filter</c> (part name), <c>source</c> (case-insensitive) and <c>skins</c>, indexed
        /// 0.. in the stable order; then the shard (index mod n == k - 1); then the first
        /// <c>limit</c> of those. The index is taken BEFORE the shard, so shards of one sweep
        /// partition the same numbering.
        /// </summary>
        internal static List<PartParityPlannedCase> SelectCases(
            IEnumerable<PartParityCaseKey> cases, PartParitySweepArgs args)
        {
            if (args == null) args = new PartParitySweepArgs();
            var kept = new List<PartParityCaseKey>();
            if (cases != null)
            {
                foreach (var c in cases)
                {
                    if (c == null) continue;
                    if (!GlobMatch(args.Filter ?? DefaultFilter, c.Part ?? string.Empty)) continue;
                    if (args.Sources != null && !SourceSelected(args.Sources, c.Source)) continue;
                    if (args.Skins == PartParitySkinSelection.Default && c.SkinOrdinal != 0) continue;
                    kept.Add(c);
                }
            }
            // List.Sort is not stable, so the order must not lean on input order:
            // CompareCases is total over the tuple and exact duplicates are interchangeable.
            kept.Sort(CompareCases);

            var selected = new List<PartParityPlannedCase>();
            int n = args.ShardN < 1 ? 1 : args.ShardN;
            int k = args.ShardK < 1 ? 1 : args.ShardK;
            for (int i = 0; i < kept.Count; i++)
            {
                if (i % n != k - 1) continue;
                if (args.Limit > 0 && selected.Count >= args.Limit) break;
                selected.Add(new PartParityPlannedCase { Index = i, Key = kept[i], CaseId = CaseId(kept[i]) });
            }
            return selected;
        }

        private static bool SourceSelected(List<string> sources, string source)
        {
            if (source == null) return false;
            foreach (string s in sources)
                if (string.Equals(s, source, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ----- case ids (design 5.2) -----

        /// <summary>Maps one id component: chars outside <c>[A-Za-z0-9._-]</c> become
        /// <c>-</c>, letters are lower-cased.</summary>
        internal static string MapIdComponent(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
                else if (IsIdChar(c)) sb.Append(c);
                else sb.Append('-');
            }
            return sb.ToString();
        }

        /// <summary>FNV-1a 32-bit over the UTF-8 bytes of <paramref name="s"/>, as 8 lower
        /// hex digits.</summary>
        internal static string Fnv1a32Hex(string s)
        {
            uint h = 2166136261u;
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 16777619u;
            }
            return h.ToString("x8", Ic);
        }

        /// <summary>The unmapped tuple the case-id hash is taken over: the four components
        /// joined by U+001F (a character no part, skin, state or context name holds).</summary>
        internal static string CaseTuple(PartParityCaseKey key)
            => (key.Part ?? string.Empty) + "\u001f" + (key.Skin ?? string.Empty) + "\u001f"
               + (key.State ?? string.Empty) + "\u001f" + (key.Context ?? string.Empty);

        /// <summary>
        /// The case id: <c>&lt;part&gt;--&lt;skin&gt;[--&lt;state&gt;][--&lt;context&gt;]--&lt;hash&gt;</c>,
        /// each component mapped by <see cref="MapIdComponent"/>. The state is written when it
        /// or the context is not the default (so a context never sits in the state's place),
        /// the context when it is not the default. The 8-hex hash of the UNMAPPED tuple keeps
        /// two names that map alike (case, punctuation) apart on a case-insensitive file
        /// system. The readable head is cut so the whole id fits
        /// <see cref="MaxCaseIdLength"/>, a head that does not start alphanumeric gains an
        /// <c>x</c>, and the result always passes <c>TestCommandCaptureScreenshot.IsValidLabel</c>.
        /// </summary>
        internal static string CaseId(PartParityCaseKey key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            bool contextNonDefault = !string.Equals(key.Context ?? DefaultContext, DefaultContext, StringComparison.Ordinal);
            bool stateNonDefault = !string.Equals(key.State ?? DefaultState, DefaultState, StringComparison.Ordinal);

            var head = new StringBuilder();
            head.Append(MapIdComponent(key.Part));
            head.Append(CaseIdSeparator).Append(MapIdComponent(key.Skin));
            if (stateNonDefault || contextNonDefault)
                head.Append(CaseIdSeparator).Append(MapIdComponent(key.State));
            if (contextNonDefault)
                head.Append(CaseIdSeparator).Append(MapIdComponent(key.Context));

            string h = head.ToString();
            if (h.Length == 0 || !IsAlnum(h[0])) h = "x" + h;
            string tail = CaseIdSeparator + Fnv1a32Hex(CaseTuple(key));
            int maxHead = MaxCaseIdLength - tail.Length;
            if (h.Length > maxHead) h = h.Substring(0, maxHead);
            return h + tail;
        }

        private static bool IsAlnum(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        // ----- resume (design 5.3) -----

        /// <summary>The case id an <c>inflight.txt</c> names: its first line, trimmed; null
        /// when empty or not a valid case id (a torn write names nothing).</summary>
        internal static string ParseInflight(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int nl = text.IndexOfAny(new[] { '\r', '\n' });
            string first = (nl >= 0 ? text.Substring(0, nl) : text).Trim();
            if (first.Length == 0) return null;
            return TestCommandCaptureScreenshot.IsValidLabel(first) ? first : null;
        }

        /// <summary>The <c>inflight.txt</c> text written before a case starts.</summary>
        internal static string FormatInflight(string caseId) => (caseId ?? string.Empty) + "\n";

        /// <summary>
        /// The resume rule for one case. <paramref name="staleInflightCaseId"/> is what an
        /// <c>inflight.txt</c> found at step start names (null when there was none): the
        /// applier deletes the file after each case, so one found at step start is a
        /// previous process that died mid-case. A complete sidecar wins over a crash record:
        /// a process that died between the sidecar write and the inflight delete finished
        /// the case.
        /// </summary>
        internal static PartParityCaseResume DecideCaseResume(
            string caseId, bool completeSidecarExists, string staleInflightCaseId)
        {
            if (completeSidecarExists) return PartParityCaseResume.SkipComplete;
            if (staleInflightCaseId != null
                && string.Equals(caseId, staleInflightCaseId, StringComparison.Ordinal))
                return PartParityCaseResume.RecordCrashed;
            return PartParityCaseResume.Run;
        }

        /// <summary>
        /// Whether a step may start another wave: only when the time spent so far plus the
        /// previous wave's wall time stays inside the box, so a step ends before its budget
        /// rather than after. The first wave of a step always starts.
        /// </summary>
        internal static bool ShouldStartWave(double elapsedSeconds, int maxSeconds, double lastWaveSeconds)
        {
            if (elapsedSeconds <= 0.0) return true;
            double estimate = lastWaveSeconds > 0.0 ? lastWaveSeconds : 0.0;
            return elapsedSeconds + estimate <= maxSeconds;
        }

        /// <summary>The <c>controls=</c> payload token from the outcomes so far: <c>bad</c>
        /// when any control failed, else <c>pending</c> while any has not run, else <c>ok</c>;
        /// <c>off</c> when the step ran without controls.</summary>
        internal static string ControlsState(bool controlsEnabled, IList<string> outcomes)
        {
            if (!controlsEnabled) return ControlsOff;
            int ok = 0;
            if (outcomes != null)
            {
                foreach (string o in outcomes)
                {
                    if (o == ControlsBad) return ControlsBad;
                    if (o == ControlsOk) ok++;
                }
            }
            return ok >= ControlIds.Length ? ControlsOk : ControlsPending;
        }

        // ----- admission and payload -----

        /// <summary>Null when the host may run a sweep, else the refusal reason.</summary>
        internal static string DecideHostAdmission(bool hasActiveVessel, bool activeVesselOrbiting)
        {
            if (!hasActiveVessel) return NoActiveVesselReason;
            if (!activeVesselOrbiting) return HostNotOrbitingReason;
            return null;
        }

        /// <summary>
        /// OK payload (design 7): <c>label done next cases complete crashed setupFaults
        /// controls wall</c>, in that order. <c>next</c> is the position in the step's
        /// selection the next step resumes at (= <c>cases</c> when done); <c>wall</c> is the
        /// step's wall time in seconds, one decimal.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            string label, bool done, int next, int cases, int complete, int crashed,
            int setupFaults, string controls, double wallSeconds)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("label", label ?? string.Empty),
                new KeyValuePair<string, string>("done", done ? "true" : "false"),
                new KeyValuePair<string, string>("next", next.ToString(Ic)),
                new KeyValuePair<string, string>("cases", cases.ToString(Ic)),
                new KeyValuePair<string, string>("complete", complete.ToString(Ic)),
                new KeyValuePair<string, string>("crashed", crashed.ToString(Ic)),
                new KeyValuePair<string, string>("setupFaults", setupFaults.ToString(Ic)),
                new KeyValuePair<string, string>("controls", controls ?? ControlsPending),
                new KeyValuePair<string, string>("wall",
                    (double.IsNaN(wallSeconds) || double.IsInfinity(wallSeconds) ? 0.0 : wallSeconds)
                        .ToString("0.0", Ic)),
            };
        }

        /// <summary>The payload as one <c>key=value</c> line, space-separated (the form a log
        /// line and a spec regex read).</summary>
        internal static string FormatPayload(List<KeyValuePair<string, string>> payload)
        {
            var sb = new StringBuilder();
            if (payload == null) return string.Empty;
            for (int i = 0; i < payload.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(payload[i].Key).Append('=').Append(payload[i].Value);
            }
            return sb.ToString();
        }
    }
}
