using System.Collections.Generic;
using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// M-A7 Layer 1 PURE accumulation core for the render composition manifest
    /// (docs/dev/design-autotest-render-composition.md). Plain data + append methods + hard caps +
    /// the ConfigNode authoring, with ZERO Unity calls so the whole surface is xUnit-drivable.
    ///
    /// <para>The thin subscriber (<see cref="RenderCompositionRecorder"/>) owns one instance, reads
    /// the Unity-native values (UT / warp / frame / env) behind its own ECall-isolated readers, and
    /// hands them here. Nothing in this file touches KSP state beyond <c>ConfigNode</c> (which the
    /// xUnit suite already exercises headlessly through the sidecar / baseline codecs).</para>
    ///
    /// <para><b>No silent caps.</b> Every record family carries an explicit bound; a record dropped
    /// at the cap increments a per-(section, pid, kind) counter that is emitted as a
    /// <c>TRUNCATED</c> node at export, so a truncated section reads as a DEFINED unevaluable to the
    /// Python verifier rather than as a clean pass.</para>
    ///
    /// <para><b>Serialization contract.</b> Doubles are <c>ToString("R", InvariantCulture)</c>;
    /// bools are ConfigNode's own "True"/"False"; enums arrive already flattened to token strings by
    /// the recorder (this file never spells a render-window coverage enum member - that is a source
    /// gate, see <c>LineBlinkWindowExitExemptionTests</c>).</para>
    /// </summary>
    internal sealed class RenderCompositionManifest
    {
        internal const int SchemaVersion = 1;
        internal const string RootNodeName = "RENDER_MANIFEST";

        // ---- Hard record caps (SPEC "Caps"). A hit cap drops the NEW record and counts it. ----
        internal const int MaxDwellsPerPid = 512;
        internal const int MaxTransitionsPerPid = 1024;
        internal const int MaxLineBranchesPerPid = 2048;
        internal const int MaxSeamRecordsPerPid = 512;
        internal const int MaxClockEvents = 2048;
        internal const int MaxOwnershipChanges = 4096;
        internal const int MaxPlanUnits = 512;
        internal const int MaxChainBuilds = 1024;
        internal const int MaxRouteRecords = 1024;

        // ---- GLOBAL record caps. The per-pid bounds above bound ONE ghost; a scene holding twenty
        // ghosts multiplies every one of them, which is how a 512-per-pid dwell bound turns into a
        // multi-megabyte manifest nobody asked for. These bound the WHOLE export. Same drop-and-count
        // semantics: the record is dropped and a TRUNCATED node is emitted, so a truncated section
        // reads as a DEFINED unevaluable rather than as a clean pass. The section token carries
        // <see cref="GlobalSectionSuffix"/> so a consumer can tell a global truncation from a
        // per-pid one, and the pid is 0 (the whole-export convention every global section uses).
        internal const int MaxDwellsTotal = 8192;
        internal const int MaxTransitionsTotal = 16384;
        internal const int MaxLineBranchesTotal = 16384;
        internal const int MaxSeamRecordsTotal = 8192;
        internal const int MaxRatifiedSkipRecords = 4096;
        internal const int MaxAnomalyEchoRecords = 1024;

        // The last bound: total records of EVERY kind in one export. Every family above has its own
        // ceiling, but they sum - this is the one number that bounds the file no matter which mix of
        // families a pathological session produces.
        internal const int MaxTotalRecords = 65536;

        // Distinct TRUNCATED rows are themselves per-(section, pid, kind), so a session with hundreds
        // of pids could grow the marker list without bound. One reserved overflow row absorbs the
        // rest, so the count is never silently lost and the list never grows past this + 1.
        internal const int MaxTruncationRecords = 512;

        /// <summary>Appended to a section token when the GLOBAL cap dropped the record.</summary>
        internal const string GlobalSectionSuffix = ":global";

        /// <summary>Section token for a drop at the whole-export record ceiling.</summary>
        internal const string TotalCeilingSection = "ALL" + GlobalSectionSuffix;

        /// <summary>Section / kind of the reserved TRUNCATED row that absorbs distinct-key overflow.</summary>
        internal const string TruncationOverflowSection = "TRUNCATED" + GlobalSectionSuffix;
        internal const string TruncationOverflowKind = "distinct-key-overflow";

        // Bounded dedupe dictionary for the clock-event debounce. FAIL-CLOSED: when the table is full
        // the section STOPS accepting clock events entirely and every further attempt is counted under
        // one reserved TRUNCATED row. It is deliberately NOT cleared - a wipe silently restarts the
        // debounce, so the SAME (kind, owner, cycle, ordinal) event can be appended a second time and a
        // consumer counting "one engage per run" reads a duplicate as a second run.
        internal const int MaxClockEventDedupeKeys = 8192;

        /// <summary>Section of the reserved TRUNCATED row for a clock event dropped because the
        /// debounce table is exhausted (fail-closed; the section stops accepting events).</summary>
        internal const string ClockEventDedupeExhaustedSection = "CLOCK_EVENT:dedupe-exhausted";
        internal const string ClockEventDedupeExhaustedKind = "dedupe-exhausted";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // ---- Section storage (insertion-ordered so an export is byte-deterministic) ----
        private readonly List<PlanUnitRecord> planUnits = new List<PlanUnitRecord>();
        private readonly List<ChainBuildRecord> chainBuilds = new List<ChainBuildRecord>();
        private readonly List<DwellRecord> closedDwells = new List<DwellRecord>();
        private readonly Dictionary<uint, DwellRecord> openDwellByPid = new Dictionary<uint, DwellRecord>();
        private readonly List<uint> openDwellOrder = new List<uint>();
        private readonly Dictionary<uint, double> lastFrameUtByPid = new Dictionary<uint, double>();
        private readonly Dictionary<uint, int> dwellCountByPid = new Dictionary<uint, int>();
        private readonly List<TransitionRecord> transitions = new List<TransitionRecord>();
        private readonly Dictionary<uint, int> transitionCountByPid = new Dictionary<uint, int>();
        private readonly List<SeamTangentRecord> seamTangents = new List<SeamTangentRecord>();
        private readonly List<SeamEndpointRecord> seamEndpoints = new List<SeamEndpointRecord>();
        private readonly Dictionary<uint, int> seamCountByPid = new Dictionary<uint, int>();
        private readonly List<ClockEventRecord> clockEvents = new List<ClockEventRecord>();
        private readonly Dictionary<string, bool> clockEventSeen = new Dictionary<string, bool>();
        private readonly List<LineBranchRecord> lineBranches = new List<LineBranchRecord>();
        private readonly Dictionary<uint, int> lineBranchCountByPid = new Dictionary<uint, int>();
        private readonly Dictionary<uint, string> lineBranchStateByPid = new Dictionary<uint, string>();
        private readonly List<OwnershipChangeRecord> ownershipChanges = new List<OwnershipChangeRecord>();
        private readonly List<RatifiedSkipRecord> ratifiedSkips = new List<RatifiedSkipRecord>();
        private readonly List<AnomalyEchoRecord> anomalyEchoRecords = new List<AnomalyEchoRecord>();
        private readonly List<RouteLineBuildRecord> routeLineBuilds = new List<RouteLineBuildRecord>();
        private readonly List<RouteLegDeferRecord> routeLegDefers = new List<RouteLegDeferRecord>();
        private readonly List<RouteCoDrawViolationRecord> routeCoDrawViolations =
            new List<RouteCoDrawViolationRecord>();
        private readonly List<TruncationRecord> truncations = new List<TruncationRecord>();

        private bool haveClockDefer;
        private double clockDeferFirstUT;
        private double clockDeferLastUT;
        private int clockDeferCount;
        private int planSeq;
        private int totalRecords;

        // ---- Read-only counters the export payload reports ----
        internal int PlanUnitCount => planUnits.Count;
        internal int ChainBuildCount => chainBuilds.Count;
        internal int ClosedDwellCount => closedDwells.Count;
        internal int OpenDwellCount => openDwellByPid.Count;
        internal int TransitionCount => transitions.Count;
        internal int ClockEventCount => clockEvents.Count;
        internal int OwnershipChangeCount => ownershipChanges.Count;
        internal int LineBranchCount => lineBranches.Count;
        internal int SeamTangentCount => seamTangents.Count;
        internal int SeamEndpointCount => seamEndpoints.Count;
        internal int RouteLineBuildCount => routeLineBuilds.Count;
        internal int RouteCoDrawViolationCount => routeCoDrawViolations.Count;
        internal int AnomalyEchoRecordCount => anomalyEchoRecords.Count;
        internal int TruncationCount => truncations.Count;

        /// <summary>Every accumulated record of every kind (the whole-export ceiling's subject).</summary>
        internal int TotalRecordCount => totalRecords;

        internal void Reset()
        {
            planUnits.Clear();
            chainBuilds.Clear();
            closedDwells.Clear();
            openDwellByPid.Clear();
            openDwellOrder.Clear();
            lastFrameUtByPid.Clear();
            dwellCountByPid.Clear();
            transitions.Clear();
            transitionCountByPid.Clear();
            seamTangents.Clear();
            seamEndpoints.Clear();
            seamCountByPid.Clear();
            clockEvents.Clear();
            clockEventSeen.Clear();
            lineBranches.Clear();
            lineBranchCountByPid.Clear();
            lineBranchStateByPid.Clear();
            ownershipChanges.Clear();
            ratifiedSkips.Clear();
            anomalyEchoRecords.Clear();
            routeLineBuilds.Clear();
            routeLegDefers.Clear();
            routeCoDrawViolations.Clear();
            truncations.Clear();
            haveClockDefer = false;
            clockDeferFirstUT = 0.0;
            clockDeferLastUT = 0.0;
            clockDeferCount = 0;
            planSeq = 0;
            totalRecords = 0;
        }

        // =====================================================================================
        //  GLOBAL bounds (shared by every append site)
        // =====================================================================================

        /// <summary>The section token a GLOBAL cap records, distinct from the per-pid one.</summary>
        internal static string GlobalSection(string section)
            => (section ?? "?") + GlobalSectionSuffix;

        /// <summary>
        /// THE ONE admission sequence every append site runs, in one place so no family can drift into
        /// a different order or forget a bound. Three gates, always in this order:
        ///
        /// <list type="number">
        /// <item>the family's PER-PID cap (skipped when <paramref name="perPidCounts"/> is null,
        /// i.e. the family has no per-pid dimension) - counted against the PLAIN section token and the
        /// REAL pid, because that marker names the one ghost that overran;</item>
        /// <item>the family's WHOLE-EXPORT cap - counted against
        /// <see cref="GlobalSection"/> and pid 0, because the drop is a property of the export, not of
        /// any one ghost;</item>
        /// <item>the whole-export record ceiling across every family - counted against
        /// <see cref="TotalCeilingSection"/>, still naming the attempted kind.</item>
        /// </list>
        ///
        /// <para>On success it does the bookkeeping too - the per-pid counter bump and the single
        /// <c>totalRecords++</c> - so a caller can never append a record the ceiling did not count.</para>
        /// </summary>
        private bool TryAdmit(
            string section, uint pid, string kind,
            int familyCount, int familyCap,
            Dictionary<uint, int> perPidCounts, int perPidCap)
        {
            int perPid = 0;
            if (perPidCounts != null)
            {
                perPidCounts.TryGetValue(pid, out perPid);
                if (perPid >= perPidCap)
                {
                    CountTruncation(section ?? "?", pid, kind ?? "?");
                    return false;
                }
            }
            if (familyCount >= familyCap)
            {
                CountTruncation(GlobalSection(section), 0u, kind ?? "?");
                return false;
            }
            if (totalRecords >= MaxTotalRecords)
            {
                CountTruncation(TotalCeilingSection, 0u, kind ?? "?");
                return false;
            }
            if (perPidCounts != null)
                perPidCounts[pid] = perPid + 1;
            totalRecords++;
            return true;
        }

        // =====================================================================================
        //  PLAN
        // =====================================================================================

        /// <summary>Appends one plan unit; returns the assigned monotonic plan sequence, or -1 at cap.</summary>
        internal int AppendPlanUnit(PlanUnitRecord unit)
        {
            if (unit == null)
                return -1;
            if (!TryAdmit("PLAN", 0u, "unit", planUnits.Count, MaxPlanUnits, null, 0))
                return -1;
            unit.PlanSeq = planSeq++;
            planUnits.Add(unit);
            return unit.PlanSeq;
        }

        // =====================================================================================
        //  CHAIN
        // =====================================================================================

        internal void AppendChainBuild(ChainBuildRecord chain)
        {
            if (chain == null)
                return;
            if (!TryAdmit("CHAIN", 0u, "chain-build", chainBuilds.Count, MaxChainBuilds, null, 0))
                return;
            chainBuilds.Add(chain);
        }

        // =====================================================================================
        //  DWELLS + TRANSITIONS
        // =====================================================================================

        /// <summary>
        /// One armed frame of Director observation for one ghost. Opens a dwell on the first
        /// observation, closes + re-opens (emitting a TRANSITION) whenever the dwell key changes, and
        /// otherwise folds the frame into the open dwell's aggregates.
        /// </summary>
        internal void ObserveDwellFrame(in DwellSample sample)
        {
            openDwellByPid.TryGetValue(sample.Pid, out DwellRecord open);

            // FIELD-WISE identity compare FIRST: this runs once per armed ghost per frame, and on the
            // overwhelming majority of frames nothing changed. Building the key string only on an
            // actual change keeps the steady state allocation-free (the key is still the record's
            // stored identity, and BuildDwellKey stays the single definition of it).
            bool justOpened = false;
            if (open == null || DwellIdentityChanged(open, in sample))
            {
                string key = BuildDwellKey(sample);
                DwellRecord prior = open;
                if (prior != null)
                    CloseDwell(sample.Pid, prior, sample.CurrentUT);

                open = OpenDwell(sample, key);
                justOpened = true;
                if (prior != null && open != null)
                    AppendTransition(prior, open, sample.CurrentUT);
            }

            if (open != null)
            {
                open.Frames++;
                open.CloseUT = sample.CurrentUT;
                AccumulateWarpBucket(open, sample.WarpRate, sample.PhysicsWarp);
                AccumulateHeadUT(open, sample.HeadUT);
                open.MarkerDecision = sample.MarkerDecision;
                open.MarkerTracedPath = sample.MarkerTracedPath;
                open.MarkerPolyline = sample.MarkerPolyline;
                open.MarkerIconSuppressed = sample.MarkerIconSuppressed;
                if (sample.HasLoopUT)
                {
                    open.HasCloseLoopUT = true;
                    open.CloseLoopUT = sample.LoopUT;
                }
                if (sample.HasTruth)
                {
                    open.HasClosePos = true;
                    open.CloseBody = sample.TruthBody;
                    open.CloseX = sample.TruthX;
                    open.CloseY = sample.TruthY;
                    open.CloseZ = sample.TruthZ;
                }
            }

            if (!justOpened
                && open != null
                && lastFrameUtByPid.TryGetValue(sample.Pid, out double prevUT))
            {
                double step = sample.CurrentUT - prevUT;
                if (step > open.MaxUtStep)
                    open.MaxUtStep = step;
            }
            lastFrameUtByPid[sample.Pid] = sample.CurrentUT;
        }

        /// <summary>Closes any dwell still open for <paramref name="pid"/> (ghost retired / scene exit).</summary>
        internal void CloseOpenDwell(uint pid, double closeUT)
        {
            if (!openDwellByPid.TryGetValue(pid, out DwellRecord open) || open == null)
                return;
            CloseDwell(pid, open, closeUT);
        }

        /// <summary>Closes EVERY open dwell, marking each as open-at-export.
        /// <para>NOT on the export path any more: <c>BuildFileNode</c> snapshots open dwells
        /// non-destructively (a build or write that fails must not have already retired them).
        /// This stays the explicit "retire everything now" helper for a caller that really does
        /// want the accumulation closed - the fixture builders use it so their expected output is
        /// authored rather than inferred.</para></summary>
        internal void CloseAllOpenDwells(double closeUT)
        {
            if (openDwellOrder.Count == 0)
                return;
            var pids = new List<uint>(openDwellOrder);
            for (int i = 0; i < pids.Count; i++)
            {
                if (!openDwellByPid.TryGetValue(pids[i], out DwellRecord open) || open == null)
                    continue;
                open.OpenAtExport = true;
                CloseDwell(pids[i], open, closeUT);
            }
        }

        /// <summary>
        /// Folds one tracer anomaly raise into the OPEN dwell for <paramref name="pid"/> (reason +
        /// count). Raises with no open dwell are dropped by design: an echo is dwell-scoped context,
        /// and the anomaly itself is already the tracer's own record.
        /// </summary>
        internal void NoteAnomalyEcho(uint pid, string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return;
            if (!openDwellByPid.TryGetValue(pid, out DwellRecord open) || open == null)
                return;
            for (int i = 0; i < open.AnomalyEchoes.Count; i++)
            {
                if (string.Equals(open.AnomalyEchoes[i].Reason, reason, System.StringComparison.Ordinal))
                {
                    AnomalyEcho hit = open.AnomalyEchoes[i];
                    hit.Count++;
                    open.AnomalyEchoes[i] = hit;
                    return;
                }
            }
            open.AnomalyEchoes.Add(new AnomalyEcho { Reason = reason, Count = 1 });
        }

        private DwellRecord OpenDwell(in DwellSample sample, string key)
        {
            // A dwell is a record from the moment it OPENS (it is stored either way), so the family
            // bound counts the open + closed populations together.
            if (!TryAdmit("DWELL", sample.Pid, "dwell",
                    closedDwells.Count + openDwellByPid.Count, MaxDwellsTotal,
                    dwellCountByPid, MaxDwellsPerPid))
            {
                openDwellByPid.Remove(sample.Pid);
                return null;
            }

            var rec = new DwellRecord
            {
                Key = key,
                Pid = sample.Pid,
                RecId = sample.RecId,
                CommittedIndex = sample.CommittedIndex,
                ChainSignature = sample.ChainSignature,
                SegmentIndex = sample.SegmentIndex,
                PhaseKind = sample.PhaseKind,
                Treatment = sample.Treatment,
                Visible = sample.Visible,
                Coverage = sample.Coverage,
                FrameBody = sample.FrameBody,
                OpenUT = sample.CurrentUT,
                CloseUT = sample.CurrentUT,
                MinHeadUT = double.NaN,
                MaxHeadUT = double.NaN,
                MaxUtStep = 0.0,
                HasOwnerIndex = sample.HasOwnerIndex,
                OwnerIndex = sample.OwnerIndex,
            };
            if (sample.HasLoopUT)
            {
                rec.HasOpenLoopUT = true;
                rec.OpenLoopUT = sample.LoopUT;
            }
            if (sample.HasTruth)
            {
                rec.HasOpenPos = true;
                rec.OpenBody = sample.TruthBody;
                rec.OpenX = sample.TruthX;
                rec.OpenY = sample.TruthY;
                rec.OpenZ = sample.TruthZ;
            }

            if (!openDwellByPid.ContainsKey(sample.Pid))
                openDwellOrder.Add(sample.Pid);
            openDwellByPid[sample.Pid] = rec;
            return rec;
        }

        private void CloseDwell(uint pid, DwellRecord rec, double closeUT)
        {
            if (closeUT > rec.CloseUT)
                rec.CloseUT = closeUT;
            // ONE-FRAME MARKER RE-SAMPLE. The Director stamps a dwell's marker decision DURING its
            // own pass, before the frame's polyline / TracedPath ownership publishes land, so a dwell
            // whose marker turns on later in the same frame closes carrying `false`. Re-sampling once
            // at close - when this frame's stamps do exist - recovers that. Only a `false` decision is
            // re-read (a `true` was already positive evidence), and the whole triple is overwritten
            // together so the decision and its three disjuncts always describe ONE sample.
            if (!rec.MarkerDecision && MarkerResampler != null
                && MarkerResampler(pid, out bool decision, out bool tracedPath,
                    out bool polyline, out bool iconSuppressed))
            {
                rec.MarkerDecision = decision;
                rec.MarkerTracedPath = tracedPath;
                rec.MarkerPolyline = polyline;
                rec.MarkerIconSuppressed = iconSuppressed;
            }
            closedDwells.Add(rec);
            openDwellByPid.Remove(pid);
            openDwellOrder.Remove(pid);
        }

        /// <summary>
        /// Optional LATE marker re-read used by <see cref="CloseDwell"/>. The core stays Unity-free -
        /// the recorder owns the ECall-isolated implementation and installs it; a headless manifest
        /// leaves it null and closes dwells with the stamped triple verbatim. Returns false when no
        /// sample could be taken (then nothing is overwritten).
        /// </summary>
        internal delegate bool MarkerResampleDelegate(
            uint pid, out bool decision, out bool tracedPath, out bool polyline, out bool iconSuppressed);

        internal MarkerResampleDelegate MarkerResampler;

        private void AppendTransition(DwellRecord from, DwellRecord to, double ut)
        {
            if (!TryAdmit("TRANSITION", from.Pid, "transition",
                    transitions.Count, MaxTransitionsTotal, transitionCountByPid, MaxTransitionsPerPid))
                return;
            transitions.Add(new TransitionRecord
            {
                Pid = from.Pid,
                UT = ut,
                FromPhaseKind = from.PhaseKind,
                ToPhaseKind = to == null ? null : to.PhaseKind,
                FromTreatment = from.Treatment,
                ToTreatment = to == null ? null : to.Treatment,
                FromBody = from.FrameBody,
                ToBody = to == null ? null : to.FrameBody,
                FromSegmentIndex = from.SegmentIndex,
                ToSegmentIndex = to == null ? -1 : to.SegmentIndex,
                ChainSignature = to == null ? from.ChainSignature : to.ChainSignature,
            });
        }

        /// <summary>
        /// PURE field-wise mirror of <see cref="BuildDwellKey"/>: true when this frame's sample would
        /// build a DIFFERENT key from the open dwell's stored identity. Every component compared here
        /// must be a component of the key, and vice versa - the two are kept in step by
        /// <c>Dwell_FieldCompare_AgreesWithTheKeyOnEveryComponent</c>.
        /// </summary>
        private static bool DwellIdentityChanged(DwellRecord open, in DwellSample s)
        {
            return open.Visible != s.Visible
                || open.SegmentIndex != s.SegmentIndex
                || !SameKeyToken(open.Treatment, s.Treatment)
                || !SameKeyToken(open.Coverage, s.Coverage)
                || !SameKeyToken(open.FrameBody, s.FrameBody)
                || !SameKeyToken(open.PhaseKind, s.PhaseKind)
                || !SameKeyToken(open.ChainSignature, s.ChainSignature);
        }

        /// <summary>Ordinal token compare with the SAME null normalization the key builder applies,
        /// so a null and a literal "?" never read as a change the key would not have made.</summary>
        private static bool SameKeyToken(string a, string b)
            => string.Equals(a ?? "?", b ?? "?", System.StringComparison.Ordinal);

        /// <summary>PURE dwell identity: any change here opens a new dwell and emits a transition.</summary>
        internal static string BuildDwellKey(in DwellSample s)
        {
            return string.Concat(
                s.Visible ? "1" : "0", "|",
                s.Treatment ?? "?", "|",
                s.Coverage ?? "?", "|",
                s.FrameBody ?? "?", "|",
                s.SegmentIndex.ToString(IC), "|",
                s.PhaseKind ?? "?", "|",
                s.ChainSignature ?? "?");
        }

        /// <summary>
        /// PURE warp bucketing (SPEC): 0 = rate &lt;= 1 (and physics dt), 1 = physics warp above 1x,
        /// 2 = rails &lt;= 100x, 3 = rails &lt;= 1000x, 4 = rails above 1000x.
        /// </summary>
        internal static int ClassifyWarpBucket(double warpRate, bool physicsWarp)
        {
            if (double.IsNaN(warpRate) || warpRate <= 1.0)
                return 0;
            if (physicsWarp)
                return 1;
            if (warpRate <= 100.0)
                return 2;
            if (warpRate <= 1000.0)
                return 3;
            return 4;
        }

        private static void AccumulateWarpBucket(DwellRecord rec, double warpRate, bool physicsWarp)
        {
            switch (ClassifyWarpBucket(warpRate, physicsWarp))
            {
                case 0: rec.Warp1x++; break;
                case 1: rec.WarpPhys++; break;
                case 2: rec.Warp100++; break;
                case 3: rec.Warp1000++; break;
                default: rec.WarpHigh++; break;
            }
        }

        private static void AccumulateHeadUT(DwellRecord rec, double headUT)
        {
            if (double.IsNaN(headUT) || double.IsInfinity(headUT))
                return;
            if (double.IsNaN(rec.MinHeadUT) || headUT < rec.MinHeadUT)
                rec.MinHeadUT = headUT;
            if (double.IsNaN(rec.MaxHeadUT) || headUT > rec.MaxHeadUT)
                rec.MaxHeadUT = headUT;
        }

        // =====================================================================================
        //  SEAMS
        // =====================================================================================

        internal const string SeamTangentSection = "SEAM_TANGENT";
        internal const string SeamTangentKind = "seam-tangent";
        internal const string SeamEndpointSection = "SEAM_ENDPOINT";
        internal const string SeamEndpointKind = "seam-endpoint";

        /// <summary>
        /// TWO-PHASE seam admission, phase one: runs the whole cap preamble (per-pid, family, ceiling,
        /// including the TRUNCATED accounting) BEFORE the caller allocates the record object. Both seam
        /// families share one per-pid and one family bound - they are two halves of the same seam
        /// evidence. Pair every <c>true</c> with exactly one <see cref="AddAdmittedSeamTangent"/> /
        /// <see cref="AddAdmittedSeamEndpoint"/>; a <c>false</c> is already counted and must be dropped.
        /// </summary>
        internal bool TryAdmitSeamRecord(uint pid, string section, string kind)
            => TryAdmit(section, pid, kind, seamTangents.Count + seamEndpoints.Count,
                MaxSeamRecordsTotal, seamCountByPid, MaxSeamRecordsPerPid);

        /// <summary>Phase two of <see cref="TryAdmitSeamRecord"/>; never call without an admit.</summary>
        internal void AddAdmittedSeamTangent(SeamTangentRecord rec)
        {
            if (rec != null)
                seamTangents.Add(rec);
        }

        /// <summary>Phase two of <see cref="TryAdmitSeamRecord"/>; never call without an admit.</summary>
        internal void AddAdmittedSeamEndpoint(SeamEndpointRecord rec)
        {
            if (rec != null)
                seamEndpoints.Add(rec);
        }

        /// <summary>One-shot form (admit + add) for callers that already hold the record.</summary>
        internal void AppendSeamTangent(SeamTangentRecord rec)
        {
            if (rec == null)
                return;
            if (!TryAdmitSeamRecord(rec.Pid, SeamTangentSection, SeamTangentKind))
                return;
            AddAdmittedSeamTangent(rec);
        }

        /// <summary>One-shot form (admit + add) for callers that already hold the record.</summary>
        internal void AppendSeamEndpoint(SeamEndpointRecord rec)
        {
            if (rec == null)
                return;
            if (!TryAdmitSeamRecord(rec.Pid, SeamEndpointSection, SeamEndpointKind))
                return;
            AddAdmittedSeamEndpoint(rec);
        }

        // =====================================================================================
        //  CLOCK EVENTS
        // =====================================================================================

        internal const string ClockCycleRollover = "cycle-rollover";
        internal const string ClockInterCycleTail = "inter-cycle-tail";
        internal const string ClockBoundaryOverlapSecondary = "boundary-overlap-secondary";
        internal const string ClockDescentPhase = "descent-phase";
        internal const string ClockRouteDockCrossing = "route-dock-crossing";
        internal const string ClockReaimWindow = "reaim-window";

        /// <summary>
        /// The OBSERVATION-DERIVED hold pair. Emitted by the recorder's STALL-ACCUMULATION run
        /// detector, NOT by re-running the hold formula: a verifier leg that recomputed the same
        /// formula the product ran would be circular.
        ///
        /// <para><b>Detail convention (supersedes the wave-1 one).</b> BOTH events carry
        /// <c>detailA</c> = the run's ORDINAL within its (owner, cycle), 0-based, so a cycle holding
        /// more than once produces one distinguishable pair per stall instead of collapsing into the
        /// debounce. <c>cycleIndex</c> is the cycle at ENGAGE on both, so a hold straddling a rollover
        /// keeps one identity. <c>detailB</c> is the frozen (stall-start) loopUT on both.
        /// <c>detailC</c> is 0 on engage and the total accumulated LIVE seconds of the stall on
        /// release.</para>
        /// </summary>
        internal const string ClockHoldEngage = "hold-engage";
        internal const string ClockHoldRelease = "hold-release";

        /// <summary>
        /// PURE debounce key. One key per (kind, owner, cycle, detailA, detailS) covers every kind:
        /// cycle-rollover / inter-cycle-tail key on the cycle, boundary-overlap-secondary on the
        /// secondary cycle (detailA) + decision token, descent-phase on the phase token,
        /// route-dock-crossing on (routeId, dockCycleIndex), reaim-window on (memberId, windowIndex).
        /// </summary>
        internal static string BuildClockEventKey(
            string kind, int ownerIndex, long cycleIndex, double detailA, string detailS)
        {
            return string.Concat(
                kind ?? "?", "|",
                ownerIndex.ToString(IC), "|",
                cycleIndex.ToString(IC), "|",
                detailA.ToString("R", IC), "|",
                detailS ?? "");
        }

        /// <summary>
        /// Appends a clock event unless an identical (debounce-key) event was already recorded.
        /// Returns true when the event was actually appended.
        ///
        /// <para><paramref name="hasDetailD"/> gates the OPTIONAL fourth numeric slot (schema v1.1):
        /// it is written only when the caller actually measured one, so an event kind that carries no
        /// fourth value keeps the exact node shape it had before. <c>descent-phase</c> uses it for the
        /// resolved descent head UT.</para>
        /// </summary>
        internal bool AppendClockEventIfChanged(
            string kind, int ownerIndex, long cycleIndex, double ut,
            double detailA, double detailB, double detailC, string detailS,
            bool hasDetailD = false, double detailD = 0.0)
        {
            string key = BuildClockEventKey(kind, ownerIndex, cycleIndex, detailA, detailS);
            if (clockEventSeen.ContainsKey(key))
                return false;
            if (clockEventSeen.Count >= MaxClockEventDedupeKeys)
            {
                // FAIL CLOSED. Wiping the table would restart the debounce and let an already-recorded
                // (kind, owner, cycle, ordinal) land a SECOND time - a consumer counting one engage per
                // run would read that duplicate as a second run. So the section stops accepting clock
                // events instead, and every further attempt is counted under one reserved marker.
                CountTruncation(
                    ClockEventDedupeExhaustedSection, 0u, ClockEventDedupeExhaustedKind);
                return false;
            }
            clockEventSeen[key] = true;

            if (!TryAdmit("CLOCK_EVENT", 0u, kind ?? "?", clockEvents.Count, MaxClockEvents, null, 0))
                return false;
            clockEvents.Add(new ClockEventRecord
            {
                Kind = kind,
                OwnerIndex = ownerIndex,
                CycleIndex = cycleIndex,
                UT = ut,
                DetailA = detailA,
                DetailB = detailB,
                DetailC = detailC,
                DetailS = detailS,
                HasDetailD = hasDetailD,
                DetailD = detailD,
            });
            return true;
        }

        // =====================================================================================
        //  LINE BRANCH / OWNERSHIP / RATIFIED SKIP / CLOCK DEFER
        // =====================================================================================

        /// <summary>
        /// Change-debounced per pid on (reason, lineActive, coverage): the branch funnel runs every
        /// frame for every ghost, but only its STATE changes are composition evidence.
        /// </summary>
        internal bool AppendLineBranchIfChanged(LineBranchRecord rec)
        {
            if (rec == null)
                return false;
            string state = string.Concat(
                rec.Reason ?? "?", "|", rec.LineActive ? "1" : "0", "|", rec.Coverage ?? "?");
            if (lineBranchStateByPid.TryGetValue(rec.Pid, out string prev)
                && string.Equals(prev, state, System.StringComparison.Ordinal))
                return false;
            lineBranchStateByPid[rec.Pid] = state;

            if (!TryAdmit("LINE_BRANCH", rec.Pid, "line-branch",
                    lineBranches.Count, MaxLineBranchesTotal, lineBranchCountByPid, MaxLineBranchesPerPid))
                return false;
            lineBranches.Add(rec);
            return true;
        }

        internal void AppendOwnershipChange(string recId, double ut, bool appeared)
        {
            if (string.IsNullOrEmpty(recId))
                return;
            if (!TryAdmit("OWNERSHIP_CHANGE", 0u, appeared ? "appear" : "disappear",
                    ownershipChanges.Count, MaxOwnershipChanges, null, 0))
                return;
            ownershipChanges.Add(new OwnershipChangeRecord
            {
                RecId = recId,
                UT = ut,
                Event = appeared ? "appear" : "disappear",
            });
        }

        /// <summary>Aggregated per (pid, reason): first UT, last UT, count.</summary>
        internal void NoteRatifiedSkip(uint pid, double ut, string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return;
            for (int i = 0; i < ratifiedSkips.Count; i++)
            {
                RatifiedSkipRecord r = ratifiedSkips[i];
                if (r.Pid == pid && string.Equals(r.Reason, reason, System.StringComparison.Ordinal))
                {
                    r.LastUT = ut;
                    r.Count++;
                    ratifiedSkips[i] = r;
                    return;
                }
            }
            if (!TryAdmit("RATIFIED_SKIP", 0u, "ratified-skip",
                    ratifiedSkips.Count, MaxRatifiedSkipRecords, null, 0))
                return;
            ratifiedSkips.Add(new RatifiedSkipRecord
            {
                Pid = pid,
                Reason = reason,
                FirstUT = ut,
                LastUT = ut,
                Count = 1,
            });
        }

        /// <summary>
        /// Appends ONE standalone anomaly-echo record per tracer raise, keyed by the raise's pid key
        /// VERBATIM. The dwell-embedded aggregation (<see cref="NoteAnomalyEcho"/>) is kept alongside
        /// it, but it can only ever hold raises that (a) parse as a uint pid AND (b) land while a dwell
        /// for that pid is open - so on its own it silently loses every raise from a non-numeric key
        /// (the tracer's route / surface keys) and every raise outside a dwell. A verifier cannot tell
        /// "no anomaly was raised" from "the raise had nowhere to land"; this record family closes that
        /// hole with its own cap and TRUNCATED marker.
        /// </summary>
        internal void AppendAnomalyEchoRecord(string pidKey, string recId, string reason, double ut)
        {
            if (string.IsNullOrEmpty(reason))
                return;
            if (!TryAdmit("ANOMALY_ECHO", 0u, "anomaly-echo",
                    anomalyEchoRecords.Count, MaxAnomalyEchoRecords, null, 0))
                return;
            anomalyEchoRecords.Add(new AnomalyEchoRecord
            {
                PidKey = pidKey,
                RecId = recId,
                Reason = reason,
                UT = ut,
            });
        }

        internal void NoteClockDefer(double ut)
        {
            if (!haveClockDefer)
            {
                haveClockDefer = true;
                clockDeferFirstUT = ut;
            }
            clockDeferLastUT = ut;
            clockDeferCount++;
        }

        // =====================================================================================
        //  ROUTE OVERVIEW LINE
        // =====================================================================================

        internal void AppendRouteLineBuild(RouteLineBuildRecord rec)
        {
            if (rec == null)
                return;
            if (!TryAdmit("ROUTE_LINE_BUILD", 0u, "route-line-build",
                    routeLineBuilds.Count, MaxRouteRecords, null, 0))
                return;
            routeLineBuilds.Add(rec);
        }

        /// <summary>Aggregated per (routeId, recId).</summary>
        internal void NoteRouteLegDeferred(string routeId, string recId)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(recId))
                return;
            for (int i = 0; i < routeLegDefers.Count; i++)
            {
                RouteLegDeferRecord r = routeLegDefers[i];
                if (string.Equals(r.RouteId, routeId, System.StringComparison.Ordinal)
                    && string.Equals(r.RecId, recId, System.StringComparison.Ordinal))
                {
                    r.Count++;
                    routeLegDefers[i] = r;
                    return;
                }
            }
            if (!TryAdmit("ROUTE_LEG_DEFER", 0u, "route-leg-defer",
                    routeLegDefers.Count, MaxRouteRecords, null, 0))
                return;
            routeLegDefers.Add(new RouteLegDeferRecord { RouteId = routeId, RecId = recId, Count = 1 });
        }

        internal void AppendRouteCoDrawViolation(string routeId, string recId, double ut, int frame)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(recId))
                return;
            if (!TryAdmit("ROUTE_CODRAW_VIOLATION", 0u, "route-codraw-violation",
                    routeCoDrawViolations.Count, MaxRouteRecords, null, 0))
                return;
            routeCoDrawViolations.Add(new RouteCoDrawViolationRecord
            {
                RouteId = routeId,
                RecId = recId,
                UT = ut,
                Frame = frame,
            });
        }

        // =====================================================================================
        //  TRUNCATION accounting
        // =====================================================================================

        /// <summary>
        /// Test-only: drives the truncation accounting directly. Every production cap needs hundreds
        /// of records to trip, so the committed SAMPLE fixture (which must contain one record of every
        /// node kind) would otherwise have to be enormous just to carry a TRUNCATED node.
        /// </summary>
        internal void NoteTruncationForTesting(string section, uint pid, string kind)
            => CountTruncation(section, pid, kind);

        private void CountTruncation(string section, uint pid, string kind)
        {
            if (TryIncrementTruncation(section, pid, kind))
                return;

            // Distinct-key overflow. A session with hundreds of pids would otherwise grow this
            // marker list without bound, so past the cap every NEW key folds into one reserved
            // row: the drop is still counted (no silent cap), it just stops naming its pid.
            if (truncations.Count >= MaxTruncationRecords)
            {
                if (TryIncrementTruncation(TruncationOverflowSection, 0u, TruncationOverflowKind))
                    return;
                section = TruncationOverflowSection;
                pid = 0u;
                kind = TruncationOverflowKind;
            }

            truncations.Add(new TruncationRecord
            {
                Section = section,
                Pid = pid,
                Kind = kind,
                DroppedCount = 1,
            });
        }

        private bool TryIncrementTruncation(string section, uint pid, string kind)
        {
            for (int i = 0; i < truncations.Count; i++)
            {
                TruncationRecord t = truncations[i];
                if (t.Pid == pid
                    && string.Equals(t.Section, section, System.StringComparison.Ordinal)
                    && string.Equals(t.Kind, kind, System.StringComparison.Ordinal))
                {
                    t.DroppedCount++;
                    truncations[i] = t;
                    return true;
                }
            }
            return false;
        }

        // =====================================================================================
        //  CONSTANTS catalog (Layer 2 TRANSPORT - the Python table is the AUTHORITY)
        // =====================================================================================

        /// <summary>
        /// One exported tolerance / catalog constant: the C# code name (dotted) and its live value.
        /// The values are compile-time <c>const</c> references, so reading them never triggers a type
        /// initializer and the core stays Unity-free.
        /// </summary>
        internal readonly struct ExportedConstant
        {
            internal ExportedConstant(string name, double value) { Name = name; Value = value; }
            internal string Name { get; }
            internal double Value { get; }
        }

        /// <summary>
        /// Widens a <c>float</c> constant to the double the PROGRAMMER wrote, not the one the binary
        /// float widens to. <c>0.05f</c> widens to <c>0.0500000007450581</c>, which would be exported
        /// verbatim and force the Python ratified table to carry a binary artefact instead of the
        /// authored 0.05. Round-tripping through the float's own shortest representation recovers the
        /// authored value exactly.
        /// </summary>
        private static double FromSingle(float value)
            => double.Parse(value.ToString("R", CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture);

        private static readonly ExportedConstant[] constants =
        {
            new ExportedConstant("PhaseSeamClassifier.DefaultTangentToleranceRadians",
                PhaseSeamClassifier.DefaultTangentToleranceRadians),
            new ExportedConstant("CrossMemberSeamStitcher.TangentToleranceRadians",
                CrossMemberSeamStitcher.TangentToleranceRadians),
            new ExportedConstant("SeamEndpointOracle.DefaultRatioTolerance",
                SeamEndpointOracle.DefaultRatioTolerance),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.BridgeMergeSampleCount",
                Parsek.Display.GhostTrajectoryPolylineRenderer.BridgeMergeSampleCount),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians",
                Parsek.Display.GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians",
                Parsek.Display.GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.BridgeChordMinAngleRadians",
                Parsek.Display.GhostTrajectoryPolylineRenderer.BridgeChordMinAngleRadians),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.BridgeMaxSeamGapSeconds",
                Parsek.Display.GhostTrajectoryPolylineRenderer.BridgeMaxSeamGapSeconds),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds",
                Parsek.Display.GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.AnchorMaxResidualKm",
                FromSingle(Parsek.Display.GhostTrajectoryPolylineRenderer.AnchorMaxResidualKm)),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.AnchorMaxRelResidual",
                FromSingle(Parsek.Display.GhostTrajectoryPolylineRenderer.AnchorMaxRelResidual)),
            new ExportedConstant("ShadowRenderDriver.SeedFreshnessFrames",
                ShadowRenderDriver.SeedFreshnessFrames),
            new ExportedConstant("GhostOrbitLinePatch.PolylineReleaseGraceSeconds",
                Parsek.Patches.GhostOrbitLinePatch.PolylineReleaseGraceSeconds),
            new ExportedConstant("GhostTrajectoryPolylineRenderer.TangentSeamConicSampleDtSeconds",
                Parsek.Display.GhostTrajectoryPolylineRenderer.Driver.TangentSeamConicSampleDtSeconds),
            new ExportedConstant("DescentTrigger.DefaultSeamEpsSeconds",
                Parsek.Reaim.DescentTrigger.DefaultSeamEpsSeconds),
            new ExportedConstant("ReaimLoiterCompressor.DefaultKeepRevs",
                Parsek.Reaim.ReaimLoiterCompressor.DefaultKeepRevs),
            new ExportedConstant("ReaimLoiterCompressor.DefaultAStepRelThreshold",
                Parsek.Reaim.ReaimLoiterCompressor.DefaultAStepRelThreshold),
            new ExportedConstant("ReaimLoiterCompressor.DefaultContiguityEpsilonSeconds",
                Parsek.Reaim.ReaimLoiterCompressor.DefaultContiguityEpsilonSeconds),
            new ExportedConstant("ReaimLoiterCompressor.DefaultSameOrbitRelThreshold",
                Parsek.Reaim.ReaimLoiterCompressor.DefaultSameOrbitRelThreshold),
            new ExportedConstant("DestinationArrivalSolver.MaxJointHoldWholePeriods",
                Parsek.Reaim.DestinationArrivalSolver.MaxJointHoldWholePeriods),
        };

        /// <summary>The exported constant catalog, in export order (the Python RATIFIED table mirror).</summary>
        internal static IReadOnlyList<ExportedConstant> Constants => constants;

        // =====================================================================================
        //  ConfigNode authoring
        // =====================================================================================

        /// <summary>
        /// Builds the OUTER unnamed ConfigNode wrapping one <c>RENDER_MANIFEST</c> child.
        /// <c>ConfigNode.Save</c> drops the outer name, so the file's top-level node is
        /// <c>RENDER_MANIFEST</c> exactly (the shape <c>saveparse.parse_sfs</c> expects).
        /// </summary>
        internal ConfigNode BuildFileNode(in ManifestHeader header)
        {
            var file = new ConfigNode();
            ConfigNode root = file.AddNode(RootNodeName);

            root.AddValue("schemaVersion", SchemaVersion.ToString(IC));
            root.AddValue("exportUT", D(header.ExportUT));
            root.AddValue("exportReason", header.ExportReason ?? "");
            root.AddValue("scene", header.Scene ?? "");
            root.AddValue("saveName", header.SaveName ?? "");
            root.AddValue("envArmed", B(header.EnvArmed));
            root.AddValue("forceArmed", B(header.ForceArmed));
            root.AddValue("mapRenderTracingOn", B(header.MapRenderTracingOn));

            ConfigNode consts = root.AddNode("CONSTANTS");
            for (int i = 0; i < constants.Length; i++)
                consts.AddValue(constants[i].Name, D(constants[i].Value));

            WritePlan(root);
            WriteChain(root);
            WriteObserved(root, header.ExportUT);
            return file;
        }

        private void WritePlan(ConfigNode root)
        {
            ConfigNode plan = root.AddNode("PLAN");
            for (int i = 0; i < planUnits.Count; i++)
            {
                PlanUnitRecord u = planUnits[i];
                ConfigNode n = plan.AddNode("UNIT");
                n.AddValue("host", u.Host ?? "");
                n.AddValue("planSeq", u.PlanSeq.ToString(IC));
                n.AddValue("signatureHash", u.SignatureHash.ToString(IC));
                n.AddValue("ownerIndex", u.OwnerIndex.ToString(IC));
                n.AddValue("spanStartUT", D(u.SpanStartUT));
                n.AddValue("spanEndUT", D(u.SpanEndUT));
                n.AddValue("cadenceSeconds", D(u.CadenceSeconds));
                n.AddValue("overlapCadenceSeconds", D(u.OverlapCadenceSeconds));
                n.AddValue("phaseAnchorUT", D(u.PhaseAnchorUT));
                n.AddValue("isReaim", B(u.IsReaim));
                n.AddValue("hasRelaunchSchedule", B(u.HasRelaunchSchedule));

                for (int m = 0; m < u.Members.Count; m++)
                {
                    PlanMemberRecord mem = u.Members[m];
                    ConfigNode mn = n.AddNode("MEMBER");
                    mn.AddValue("index", mem.Index.ToString(IC));
                    mn.AddValue("recId", mem.RecId ?? "");
                    mn.AddValue("startUT", D(mem.StartUT));
                    mn.AddValue("endUT", D(mem.EndUT));
                }

                for (int c = 0; c < u.LoiterCuts.Count; c++)
                {
                    PlanCutRecord cut = u.LoiterCuts[c];
                    ConfigNode cn = n.AddNode("LOITER_CUT");
                    cn.AddValue("startUT", D(cut.StartUT));
                    cn.AddValue("lengthSeconds", D(cut.LengthSeconds));
                }

                n.AddValue("arrivalHoldSeconds", D(u.ArrivalHoldSeconds));
                n.AddValue("arrivalHoldAtUT", D(u.ArrivalHoldAtUT));
                n.AddValue("arrivalAlignPeriodSeconds", D(u.ArrivalAlignPeriodSeconds));
                n.AddValue("arrivalJointSecondaryPeriodSeconds", D(u.ArrivalJointSecondaryPeriodSeconds));
                n.AddValue("arrivalJointSecondaryToleranceSeconds", D(u.ArrivalJointSecondaryToleranceSeconds));
                n.AddValue("arrivalJointMaxWholeHoldPeriods", u.ArrivalJointMaxWholeHoldPeriods.ToString(IC));
                if (!string.IsNullOrEmpty(u.ArrivalAmberReason))
                    n.AddValue("arrivalAmberReason", u.ArrivalAmberReason);

                // The body NAMES behind the two rotation periods (schema v1.1, supervisor decision 1).
                // Present only for a re-aim unit, which is the only population whose ReaimPlan states
                // them; they turn the verifier's stock-table cross-check from "does this period match
                // SOME ratified body" into an exact row lookup.
                if (!string.IsNullOrEmpty(u.LaunchBodyName))
                    n.AddValue("launchBodyName", u.LaunchBodyName);
                if (!string.IsNullOrEmpty(u.DestinationBodyName))
                    n.AddValue("destinationBodyName", u.DestinationBodyName);
                n.AddValue("launchBodyRotationPeriodSeconds", D(u.LaunchBodyRotationPeriodSeconds));
                n.AddValue("launchHoldEngaged", B(u.LaunchHoldEngaged));
                n.AddValue("recordedSoiExitUT", D(u.RecordedSoiExitUT));

                if (!string.IsNullOrEmpty(u.DescentMemberIndices))
                    n.AddValue("descentMemberIndices", u.DescentMemberIndices);
                n.AddValue("recordedDeorbitUT", D(u.RecordedDeorbitUT));
                n.AddValue("descentEndUT", D(u.DescentEndUT));
                n.AddValue("destinationBodyRotationPeriodSeconds", D(u.DestinationBodyRotationPeriodSeconds));
                n.AddValue("loiterPeriodSeconds", D(u.LoiterPeriodSeconds));
                n.AddValue("captureShiftSeconds", D(u.CaptureShiftSeconds));
                n.AddValue("parkingConicEndUT", D(u.ParkingConicEndUT));
                n.AddValue("transferMemberIndex", u.TransferMemberIndex.ToString(IC));
                n.AddValue("firstDeorbitLegStartUT", D(u.FirstDeorbitLegStartUT));
                n.AddValue("transferMemberRecordingId", u.TransferMemberRecordingId ?? "");

                if (u.ReaimSchedule != null)
                {
                    ConfigNode rn = n.AddNode("REAIM_SCHEDULE");
                    rn.AddValue("firstDepartureUT", D(u.ReaimSchedule.FirstDepartureUT));
                    rn.AddValue("synodicPeriodSeconds", D(u.ReaimSchedule.SynodicPeriodSeconds));
                    rn.AddValue("tofSeconds", D(u.ReaimSchedule.TofSeconds));
                    rn.AddValue("phaseAnchorUT", D(u.ReaimSchedule.PhaseAnchorUT));
                    rn.AddValue("cadenceSeconds", D(u.ReaimSchedule.CadenceSeconds));
                    rn.AddValue("prograde", B(u.ReaimSchedule.Prograde));
                }

                if (u.Route != null)
                {
                    ConfigNode on = n.AddNode("ROUTE");
                    on.AddValue("routeId", u.Route.RouteId ?? "");
                    on.AddValue("backingMissionTreeId", u.Route.BackingMissionTreeId ?? "");
                    on.AddValue("recordedDockUT", D(u.Route.RecordedDockUT));
                    on.AddValue("recordedOriginUndockUT", D(u.Route.RecordedOriginUndockUT));
                    on.AddValue("dispatchWindowPeriod", D(u.Route.DispatchWindowPeriod));
                    on.AddValue("scope", u.Route.Scope ?? "");
                    on.AddValue("excludedIntervalKeys", u.Route.ExcludedIntervalKeys ?? "");
                }
            }
        }

        private void WriteChain(ConfigNode root)
        {
            ConfigNode chain = root.AddNode("CHAIN");
            for (int i = 0; i < chainBuilds.Count; i++)
            {
                ChainBuildRecord c = chainBuilds[i];
                ConfigNode n = chain.AddNode("CHAIN_BUILD");
                n.AddValue("pid", c.Pid.ToString(IC));
                n.AddValue("recId", c.RecId ?? "");
                n.AddValue("committedIndex", c.CommittedIndex.ToString(IC));
                n.AddValue("ut", D(c.UT));
                n.AddValue("signature", c.Signature ?? "");
                n.AddValue("windowIndex", c.WindowIndex.ToString(IC));
                n.AddValue("provenance", c.Provenance ?? "");
                n.AddValue("hasReaimedSegments", B(c.HasReaimedSegments));
                n.AddValue("seamSource", c.SeamSource ?? "assembler");
                for (int p = 0; p < c.Phases.Count; p++)
                {
                    ChainPhaseRecord ph = c.Phases[p];
                    ConfigNode pn = n.AddNode("PHASE");
                    pn.AddValue("kind", ph.Kind ?? "");
                    pn.AddValue("provenance", ph.Provenance ?? "");
                    pn.AddValue("body", ph.Body ?? "");
                    pn.AddValue("startUT", D(ph.StartUT));
                    pn.AddValue("endUT", D(ph.EndUT));
                }
                for (int s = 0; s < c.Seams.Count; s++)
                {
                    ChainSeamRecord sm = c.Seams[s];
                    ConfigNode sn = n.AddNode("SEAM");
                    sn.AddValue("boundaryIndex", sm.BoundaryIndex.ToString(IC));
                    sn.AddValue("kind", sm.Kind ?? "");
                }
            }
        }

        private void WriteObserved(ConfigNode root, double exportUT)
        {
            ConfigNode obs = root.AddNode("OBSERVED");

            for (int i = 0; i < closedDwells.Count; i++)
                WriteDwell(obs, closedDwells[i], closedDwells[i].OpenAtExport, closedDwells[i].CloseUT);

            // Dwells still OPEN at export are serialized as a read-only SNAPSHOT: `openAtExport=True`
            // with the close stamp advanced to the export instant, and NOTHING mutated. Closing them
            // here would destroy accumulation state before the write can fail, so a failed write would
            // leave the recorder poorer than it was and the eventual real close would land on a dwell
            // that had already been retired. The schema has always carried `openAtExport`, so this is
            // the same on-disk shape the destructive close produced.
            for (int i = 0; i < openDwellOrder.Count; i++)
            {
                if (!openDwellByPid.TryGetValue(openDwellOrder[i], out DwellRecord open) || open == null)
                    continue;
                // Same NaN-safe max as CloseAllOpenDwells: a NaN export instant (Planetarium
                // unavailable) leaves the last observed frame's stamp standing.
                WriteDwell(obs, open, true, exportUT > open.CloseUT ? exportUT : open.CloseUT);
            }

            for (int i = 0; i < transitions.Count; i++)
            {
                TransitionRecord t = transitions[i];
                ConfigNode n = obs.AddNode("TRANSITION");
                n.AddValue("pid", t.Pid.ToString(IC));
                n.AddValue("ut", D(t.UT));
                n.AddValue("fromPhaseKind", t.FromPhaseKind ?? "");
                n.AddValue("toPhaseKind", t.ToPhaseKind ?? "");
                n.AddValue("fromTreatment", t.FromTreatment ?? "");
                n.AddValue("toTreatment", t.ToTreatment ?? "");
                n.AddValue("fromBody", t.FromBody ?? "");
                n.AddValue("toBody", t.ToBody ?? "");
                n.AddValue("fromSegmentIndex", t.FromSegmentIndex.ToString(IC));
                n.AddValue("toSegmentIndex", t.ToSegmentIndex.ToString(IC));
                n.AddValue("chainSignature", t.ChainSignature ?? "");
            }

            for (int i = 0; i < seamTangents.Count; i++)
            {
                SeamTangentRecord s = seamTangents[i];
                ConfigNode n = obs.AddNode("SEAM_TANGENT");
                n.AddValue("pid", s.Pid.ToString(IC));
                n.AddValue("recId", s.RecId ?? "");
                n.AddValue("legIndex", s.LegIndex.ToString(IC));
                n.AddValue("ut", D(s.UT));
                n.AddValue("continuous", B(s.Continuous));
                n.AddValue("angleRad", D(s.AngleRadians));
                n.AddValue("toleranceRadians", D(s.ToleranceRadians));
            }

            for (int i = 0; i < seamEndpoints.Count; i++)
            {
                SeamEndpointRecord s = seamEndpoints[i];
                ConfigNode n = obs.AddNode("SEAM_ENDPOINT");
                n.AddValue("pid", s.Pid.ToString(IC));
                n.AddValue("recId", s.RecId ?? "");
                n.AddValue("ut", D(s.UT));
                n.AddValue("sampled", B(s.Sampled));
                n.AddValue("skipReason", s.SkipReason ?? "");
                n.AddValue("ratio", D(s.Ratio));
                n.AddValue("endpointDistanceMeters", D(s.EndpointDistanceMeters));
                n.AddValue("soiRadiusMeters", D(s.SoiRadiusMeters));
                n.AddValue("ratioTolerance", D(s.RatioTolerance));
                n.AddValue("outsideSoi", B(s.OutsideSoi));
                n.AddValue("fromBody", s.FromBody ?? "");
                n.AddValue("toBody", s.ToBody ?? "");
                n.AddValue("recordedSeamUT", D(s.RecordedSeamUT));
                n.AddValue("seamUT", D(s.SeamUT));
                n.AddValue("clockConvention", s.ClockConvention ?? "");
                n.AddValue("seedKind", s.SeedKind ?? "");
            }

            for (int i = 0; i < clockEvents.Count; i++)
            {
                ClockEventRecord c = clockEvents[i];
                ConfigNode n = obs.AddNode("CLOCK_EVENT");
                n.AddValue("kind", c.Kind ?? "");
                n.AddValue("ownerIndex", c.OwnerIndex.ToString(IC));
                n.AddValue("cycleIndex", c.CycleIndex.ToString(IC));
                n.AddValue("ut", D(c.UT));
                n.AddValue("detailA", D(c.DetailA));
                n.AddValue("detailB", D(c.DetailB));
                n.AddValue("detailC", D(c.DetailC));
                n.AddValue("detailS", c.DetailS ?? "");
                if (c.HasDetailD)
                    n.AddValue("detailD", D(c.DetailD));
            }

            for (int i = 0; i < lineBranches.Count; i++)
            {
                LineBranchRecord l = lineBranches[i];
                ConfigNode n = obs.AddNode("LINE_BRANCH");
                n.AddValue("pid", l.Pid.ToString(IC));
                n.AddValue("recId", l.RecId ?? "");
                n.AddValue("ut", D(l.UT));
                n.AddValue("reason", l.Reason ?? "");
                n.AddValue("lineActive", B(l.LineActive));
                n.AddValue("drawIcons", l.DrawIcons.ToString(IC));
                n.AddValue("iconSuppressed", B(l.IconSuppressed));
                n.AddValue("coverage", l.Coverage ?? "");
            }

            for (int i = 0; i < ownershipChanges.Count; i++)
            {
                OwnershipChangeRecord o = ownershipChanges[i];
                ConfigNode n = obs.AddNode("OWNERSHIP_CHANGE");
                n.AddValue("recId", o.RecId ?? "");
                n.AddValue("ut", D(o.UT));
                n.AddValue("event", o.Event ?? "");
            }

            for (int i = 0; i < ratifiedSkips.Count; i++)
            {
                RatifiedSkipRecord r = ratifiedSkips[i];
                ConfigNode n = obs.AddNode("RATIFIED_SKIP");
                n.AddValue("pid", r.Pid.ToString(IC));
                n.AddValue("reason", r.Reason ?? "");
                n.AddValue("firstUT", D(r.FirstUT));
                n.AddValue("lastUT", D(r.LastUT));
                n.AddValue("count", r.Count.ToString(IC));
            }

            // STANDALONE anomaly echoes: one row per tracer raise, pid key verbatim (it is NOT always a
            // uint - the tracer keys some surfaces by name), recId possibly empty. Distinct from the
            // DWELL-nested ANOMALY_ECHO aggregation by NESTING, not by node name.
            for (int i = 0; i < anomalyEchoRecords.Count; i++)
            {
                AnomalyEchoRecord a = anomalyEchoRecords[i];
                ConfigNode n = obs.AddNode("ANOMALY_ECHO");
                n.AddValue("pidKey", a.PidKey ?? "");
                n.AddValue("recId", a.RecId ?? "");
                n.AddValue("reason", a.Reason ?? "");
                n.AddValue("ut", D(a.UT));
            }

            if (haveClockDefer)
            {
                ConfigNode n = obs.AddNode("CLOCK_DEFER");
                n.AddValue("firstUT", D(clockDeferFirstUT));
                n.AddValue("lastUT", D(clockDeferLastUT));
                n.AddValue("count", clockDeferCount.ToString(IC));
            }

            for (int i = 0; i < routeLineBuilds.Count; i++)
            {
                RouteLineBuildRecord r = routeLineBuilds[i];
                ConfigNode n = obs.AddNode("ROUTE_LINE_BUILD");
                n.AddValue("routeId", r.RouteId ?? "");
                n.AddValue("signature", r.Signature.ToString(IC));
                n.AddValue("dockClipUT", D(r.DockClipUT));
                n.AddValue("dispatchWindowPeriod", D(r.DispatchWindowPeriod));
                n.AddValue("scope", r.Scope ?? "");
                n.AddValue("resolvableMembers", r.ResolvableMembers.ToString(IC));
                n.AddValue("groups", r.Groups.ToString(IC));
                n.AddValue("totalLegs", r.TotalLegs.ToString(IC));
                n.AddValue("transferLegsDropped", r.TransferLegsDropped.ToString(IC));
                n.AddValue("ut", D(r.UT));
            }

            for (int i = 0; i < routeLegDefers.Count; i++)
            {
                RouteLegDeferRecord r = routeLegDefers[i];
                ConfigNode n = obs.AddNode("ROUTE_LEG_DEFER");
                n.AddValue("routeId", r.RouteId ?? "");
                n.AddValue("recId", r.RecId ?? "");
                n.AddValue("count", r.Count.ToString(IC));
            }

            for (int i = 0; i < routeCoDrawViolations.Count; i++)
            {
                RouteCoDrawViolationRecord r = routeCoDrawViolations[i];
                ConfigNode n = obs.AddNode("ROUTE_CODRAW_VIOLATION");
                n.AddValue("routeId", r.RouteId ?? "");
                n.AddValue("recId", r.RecId ?? "");
                n.AddValue("ut", D(r.UT));
                n.AddValue("frame", r.Frame.ToString(IC));
            }

            for (int i = 0; i < truncations.Count; i++)
            {
                TruncationRecord t = truncations[i];
                ConfigNode n = obs.AddNode("TRUNCATED");
                n.AddValue("section", t.Section ?? "");
                n.AddValue("pid", t.Pid.ToString(IC));
                n.AddValue("kind", t.Kind ?? "");
                n.AddValue("droppedCount", t.DroppedCount.ToString(IC));
            }
        }

        /// <summary>
        /// Serializes one dwell. <paramref name="openAtExport"/> and <paramref name="closeUT"/> are
        /// passed in rather than read off the record so an OPEN dwell can be snapshotted at the export
        /// instant without mutating it (see <see cref="WriteObserved"/>).
        /// </summary>
        private static void WriteDwell(ConfigNode obs, DwellRecord d, bool openAtExport, double closeUT)
        {
            ConfigNode n = obs.AddNode("DWELL");
            n.AddValue("pid", d.Pid.ToString(IC));
            n.AddValue("recId", d.RecId ?? "");
            n.AddValue("committedIndex", d.CommittedIndex.ToString(IC));
            if (d.HasOwnerIndex)
                n.AddValue("ownerIndex", d.OwnerIndex.ToString(IC));
            n.AddValue("chainSignature", d.ChainSignature ?? "");
            n.AddValue("segmentIndex", d.SegmentIndex.ToString(IC));
            n.AddValue("phaseKind", d.PhaseKind ?? "");
            n.AddValue("treatment", d.Treatment ?? "");
            n.AddValue("visible", B(d.Visible));
            n.AddValue("coverage", d.Coverage ?? "");
            n.AddValue("frameBody", d.FrameBody ?? "");
            n.AddValue("openUT", D(d.OpenUT));
            n.AddValue("closeUT", D(closeUT));
            // The RECORDED clock at the dwell's endpoints, omitted entirely when the dwell could not
            // be mapped to a loop unit. Live openUT/closeUT can never answer "did a sample land inside
            // a compressed loiter cut" - the cut is an interval on the RECORDED clock.
            if (d.HasOpenLoopUT)
                n.AddValue("openLoopUT", D(d.OpenLoopUT));
            if (d.HasCloseLoopUT)
                n.AddValue("closeLoopUT", D(d.CloseLoopUT));
            n.AddValue("frames", d.Frames.ToString(IC));
            n.AddValue("warp1x", d.Warp1x.ToString(IC));
            n.AddValue("warpPhys", d.WarpPhys.ToString(IC));
            n.AddValue("warp100", d.Warp100.ToString(IC));
            n.AddValue("warp1000", d.Warp1000.ToString(IC));
            n.AddValue("warpHigh", d.WarpHigh.ToString(IC));
            n.AddValue("minHeadUT", D(d.MinHeadUT));
            n.AddValue("maxHeadUT", D(d.MaxHeadUT));
            n.AddValue("maxUtStep", D(d.MaxUtStep));
            if (d.HasOpenPos)
            {
                n.AddValue("openBody", d.OpenBody ?? "");
                n.AddValue("openX", D(d.OpenX));
                n.AddValue("openY", D(d.OpenY));
                n.AddValue("openZ", D(d.OpenZ));
            }
            if (d.HasClosePos)
            {
                n.AddValue("closeBody", d.CloseBody ?? "");
                n.AddValue("closeX", D(d.CloseX));
                n.AddValue("closeY", D(d.CloseY));
                n.AddValue("closeZ", D(d.CloseZ));
            }
            n.AddValue("markerDecision", B(d.MarkerDecision));
            n.AddValue("markerTracedPath", B(d.MarkerTracedPath));
            n.AddValue("markerPolyline", B(d.MarkerPolyline));
            n.AddValue("markerIconSuppressed", B(d.MarkerIconSuppressed));
            for (int a = 0; a < d.AnomalyEchoes.Count; a++)
            {
                ConfigNode an = n.AddNode("ANOMALY_ECHO");
                an.AddValue("reason", d.AnomalyEchoes[a].Reason ?? "");
                an.AddValue("count", d.AnomalyEchoes[a].Count.ToString(IC));
            }
            if (openAtExport)
                n.AddValue("openAtExport", "True");
        }

        /// <summary>Serializes one double with the repo-wide round-trip contract.</summary>
        internal static string D(double v) => v.ToString("R", IC);

        private static string B(bool v) => v ? "True" : "False";

        /// <summary>
        /// Deterministic 64-bit FNV-1a over a string. Used for <c>signatureHash</c>: the full builder
        /// signature is unbounded (it enumerates every mission + member), so the manifest carries a
        /// stable hash instead. Deliberately NOT <c>string.GetHashCode</c>, which is neither stable
        /// across runtimes nor documented.
        ///
        /// <para>This is the repo's CANONICAL standalone FNV-1a string hasher: the other FNV-1a sites
        /// hash structured inputs (ids, doubles, enum values) inside their own signature builders, so
        /// there is no shared helper to reuse - a caller that needs a stable hash OF A STRING calls
        /// this one rather than adding another copy.</para>
        /// </summary>
        internal static long StableHash(string s)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        h ^= s[i];
                        h *= 1099511628211UL;
                    }
                }
                return (long)(h & 0x7FFFFFFFFFFFFFFFUL);
            }
        }

        // =====================================================================================
        //  Record shapes (plain data; the recorder fills them, this file serializes them)
        // =====================================================================================

        /// <summary>Header values the recorder reads from Unity/KSP and hands in at export.</summary>
        internal readonly struct ManifestHeader
        {
            internal ManifestHeader(
                double exportUT, string exportReason, string scene, string saveName,
                bool envArmed, bool forceArmed, bool mapRenderTracingOn)
            {
                ExportUT = exportUT;
                ExportReason = exportReason;
                Scene = scene;
                SaveName = saveName;
                EnvArmed = envArmed;
                ForceArmed = forceArmed;
                MapRenderTracingOn = mapRenderTracingOn;
            }

            internal double ExportUT { get; }
            internal string ExportReason { get; }
            internal string Scene { get; }
            internal string SaveName { get; }
            internal bool EnvArmed { get; }
            internal bool ForceArmed { get; }
            internal bool MapRenderTracingOn { get; }
        }

        /// <summary>One armed frame of Director observation (the dwell driver's input).</summary>
        internal struct DwellSample
        {
            internal uint Pid;
            internal string RecId;
            internal int CommittedIndex;
            internal string ChainSignature;
            internal int SegmentIndex;
            internal string PhaseKind;
            internal string Treatment;
            internal bool Visible;
            internal string Coverage;
            internal string FrameBody;
            internal double CurrentUT;
            internal double HeadUT;
            internal double WarpRate;
            internal bool PhysicsWarp;
            internal bool MarkerDecision;
            internal bool MarkerTracedPath;
            internal bool MarkerPolyline;
            internal bool MarkerIconSuppressed;
            internal bool HasTruth;
            internal string TruthBody;
            internal double TruthX;
            internal double TruthY;
            internal double TruthZ;
            /// <summary>
            /// The RECORDED-clock instant this frame rendered (the unit's SpanLoopFrame loopUT),
            /// present only when the dwell's committed index resolved to a known loop unit
            /// (schema v1.1, supervisor decision 4). Dwell open/close UTs are LIVE; RC-CUT needs the
            /// recorded clock to ask whether a sample landed inside a compressed loiter cut.
            /// </summary>
            internal bool HasLoopUT;
            internal double LoopUT;
            /// <summary>The owning unit's owner index when resolved through the unit set's
            /// OwnerByIndex (schema v1.1, supervisor decision 5); absent otherwise.</summary>
            internal bool HasOwnerIndex;
            internal int OwnerIndex;
        }

        internal struct AnomalyEcho
        {
            internal string Reason;
            internal int Count;
        }

        /// <summary>One STANDALONE tracer-raise echo (OBSERVED.ANOMALY_ECHO), independent of whether a
        /// dwell was open and of whether the raise's pid key parses as a uint.</summary>
        internal struct AnomalyEchoRecord
        {
            /// <summary>The tracer's pid key VERBATIM - not necessarily a uint.</summary>
            internal string PidKey;
            internal string RecId;
            internal string Reason;
            internal double UT;
        }

        internal sealed class DwellRecord
        {
            internal string Key;
            internal uint Pid;
            internal string RecId;
            internal int CommittedIndex;
            internal string ChainSignature;
            internal int SegmentIndex;
            internal string PhaseKind;
            internal string Treatment;
            internal bool Visible;
            internal string Coverage;
            internal string FrameBody;
            internal double OpenUT;
            internal double CloseUT;
            internal int Frames;
            internal int Warp1x;
            internal int WarpPhys;
            internal int Warp100;
            internal int Warp1000;
            internal int WarpHigh;
            internal double MinHeadUT;
            internal double MaxHeadUT;
            internal double MaxUtStep;
            internal bool HasOpenPos;
            internal string OpenBody;
            internal double OpenX;
            internal double OpenY;
            internal double OpenZ;
            internal bool HasClosePos;
            internal string CloseBody;
            internal double CloseX;
            internal double CloseY;
            internal double CloseZ;
            /// <summary>
            /// The marker triple as of the dwell's LAST observed frame, with one late re-read at close
            /// when the last stamp was <c>false</c> (see <c>CloseDwell</c>). RESIDUAL SEMANTICS: the
            /// stamp is taken inside the Director's own pass, so a marker that turns on LATER in the
            /// same frame is invisible to it; the close-time re-read recovers exactly that case for
            /// the dwell's final frame and NOT for its interior frames. So a <c>true</c> means "the
            /// marker was drawn at some point in this dwell's last frame" and a <c>false</c> means
            /// "not at the Director's stamp and not at close" - never "not on any frame of the dwell".
            /// </summary>
            internal bool MarkerDecision;
            internal bool MarkerTracedPath;
            internal bool MarkerPolyline;
            internal bool MarkerIconSuppressed;
            internal bool OpenAtExport;
            internal bool HasOwnerIndex;
            internal int OwnerIndex;
            internal bool HasOpenLoopUT;
            internal double OpenLoopUT;
            internal bool HasCloseLoopUT;
            internal double CloseLoopUT;
            internal readonly List<AnomalyEcho> AnomalyEchoes = new List<AnomalyEcho>();
        }

        internal struct TransitionRecord
        {
            internal uint Pid;
            internal double UT;
            internal string FromPhaseKind;
            internal string ToPhaseKind;
            internal string FromTreatment;
            internal string ToTreatment;
            internal string FromBody;
            internal string ToBody;
            internal int FromSegmentIndex;
            internal int ToSegmentIndex;
            internal string ChainSignature;
        }

        internal sealed class SeamTangentRecord
        {
            internal uint Pid;
            internal string RecId;
            internal int LegIndex;
            internal double UT;
            internal bool Continuous;
            internal double AngleRadians;
            internal double ToleranceRadians;
        }

        internal sealed class SeamEndpointRecord
        {
            internal uint Pid;
            internal string RecId;
            internal double UT;
            internal bool Sampled;
            internal string SkipReason;
            internal double Ratio;
            internal double EndpointDistanceMeters;
            internal double SoiRadiusMeters;
            internal double RatioTolerance;
            internal bool OutsideSoi;
            internal string FromBody;
            internal string ToBody;
            internal double RecordedSeamUT;
            internal double SeamUT;
            internal string ClockConvention;
            internal string SeedKind;
        }

        internal struct ClockEventRecord
        {
            internal string Kind;
            internal int OwnerIndex;
            internal long CycleIndex;
            internal double UT;
            internal double DetailA;
            internal double DetailB;
            internal double DetailC;
            internal string DetailS;
            /// <summary>True when <see cref="DetailD"/> was measured (schema v1.1 optional slot).</summary>
            internal bool HasDetailD;
            internal double DetailD;
        }

        internal sealed class LineBranchRecord
        {
            internal uint Pid;
            internal string RecId;
            internal double UT;
            internal string Reason;
            internal bool LineActive;
            internal int DrawIcons;
            internal bool IconSuppressed;
            internal string Coverage;
        }

        internal struct OwnershipChangeRecord
        {
            internal string RecId;
            internal double UT;
            internal string Event;
        }

        internal struct RatifiedSkipRecord
        {
            internal uint Pid;
            internal string Reason;
            internal double FirstUT;
            internal double LastUT;
            internal int Count;
        }

        internal sealed class RouteLineBuildRecord
        {
            internal string RouteId;
            internal long Signature;
            internal double DockClipUT;
            internal double DispatchWindowPeriod;
            internal string Scope;
            internal int ResolvableMembers;
            internal int Groups;
            internal int TotalLegs;
            internal int TransferLegsDropped;
            internal double UT;
        }

        internal struct RouteLegDeferRecord
        {
            internal string RouteId;
            internal string RecId;
            internal int Count;
        }

        internal struct RouteCoDrawViolationRecord
        {
            internal string RouteId;
            internal string RecId;
            internal double UT;
            internal int Frame;
        }

        internal struct TruncationRecord
        {
            internal string Section;
            internal uint Pid;
            internal string Kind;
            internal int DroppedCount;
        }

        internal struct PlanMemberRecord
        {
            internal int Index;
            internal string RecId;
            internal double StartUT;
            internal double EndUT;
        }

        internal struct PlanCutRecord
        {
            internal double StartUT;
            internal double LengthSeconds;
        }

        internal sealed class PlanReaimScheduleRecord
        {
            internal double FirstDepartureUT;
            internal double SynodicPeriodSeconds;
            internal double TofSeconds;
            internal double PhaseAnchorUT;
            internal double CadenceSeconds;
            internal bool Prograde;
        }

        internal sealed class PlanRouteRecord
        {
            internal string RouteId;
            internal string BackingMissionTreeId;
            internal double RecordedDockUT;
            internal double RecordedOriginUndockUT;
            internal double DispatchWindowPeriod;
            internal string Scope;
            internal string ExcludedIntervalKeys;
        }

        internal sealed class PlanUnitRecord
        {
            internal string Host;
            internal int PlanSeq;
            internal long SignatureHash;
            internal int OwnerIndex;
            internal double SpanStartUT;
            internal double SpanEndUT;
            internal double CadenceSeconds;
            internal double OverlapCadenceSeconds;
            internal double PhaseAnchorUT;
            internal bool IsReaim;
            internal bool HasRelaunchSchedule;
            internal readonly List<PlanMemberRecord> Members = new List<PlanMemberRecord>();
            internal readonly List<PlanCutRecord> LoiterCuts = new List<PlanCutRecord>();
            internal double ArrivalHoldSeconds;
            internal double ArrivalHoldAtUT;
            internal double ArrivalAlignPeriodSeconds;
            internal double ArrivalJointSecondaryPeriodSeconds;
            internal double ArrivalJointSecondaryToleranceSeconds;
            internal int ArrivalJointMaxWholeHoldPeriods;
            internal string ArrivalAmberReason;
            /// <summary>ReaimPlan.LaunchBody for a re-aim unit; null/empty otherwise (schema v1.1).</summary>
            internal string LaunchBodyName;
            /// <summary>ReaimPlan.TargetBody for a re-aim unit; null/empty otherwise (schema v1.1).</summary>
            internal string DestinationBodyName;
            internal double LaunchBodyRotationPeriodSeconds;
            internal bool LaunchHoldEngaged;
            internal double RecordedSoiExitUT;
            internal string DescentMemberIndices;
            internal double RecordedDeorbitUT;
            internal double DescentEndUT;
            internal double DestinationBodyRotationPeriodSeconds;
            internal double LoiterPeriodSeconds;
            internal double CaptureShiftSeconds;
            internal double ParkingConicEndUT;
            internal int TransferMemberIndex;
            internal double FirstDeorbitLegStartUT;
            internal string TransferMemberRecordingId;
            internal PlanReaimScheduleRecord ReaimSchedule;
            internal PlanRouteRecord Route;
        }

        internal struct ChainPhaseRecord
        {
            internal string Kind;
            internal string Provenance;
            internal string Body;
            internal double StartUT;
            internal double EndUT;
        }

        internal struct ChainSeamRecord
        {
            internal int BoundaryIndex;
            internal string Kind;
        }

        internal sealed class ChainBuildRecord
        {
            internal uint Pid;
            internal string RecId;
            internal int CommittedIndex;
            internal double UT;
            internal string Signature;
            internal long WindowIndex;
            internal string Provenance;
            internal bool HasReaimedSegments;
            internal string SeamSource = "assembler";
            internal readonly List<ChainPhaseRecord> Phases = new List<ChainPhaseRecord>();
            internal readonly List<ChainSeamRecord> Seams = new List<ChainSeamRecord>();
        }
    }
}
