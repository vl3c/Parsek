using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Outcome of the M2 transport-gain admission check
    /// (<see cref="RouteHarvestAnalysis.CheckTransportGains"/>).
    /// </summary>
    internal enum HarvestGainOutcome
    {
        /// <summary>
        /// The presence gate is unsatisfied (a lineage leg lacks a COMPLETE
        /// run manifest - absent, voided, or start-only) or the lineage could
        /// not be resolved unambiguously. The analysis must run the LEGACY
        /// path, exactly as it did before M2.
        /// </summary>
        LegacyFallback = 0,
        /// <summary>Every positive transport gain is covered by witnessed harvest.</summary>
        Covered = 1,
        /// <summary>At least one positive gain has no witnessed source (fail closed).</summary>
        UntrackedGain = 2,
    }

    /// <summary>
    /// Result of <see cref="RouteHarvestAnalysis.CheckTransportGains"/>. On
    /// <see cref="HarvestGainOutcome.UntrackedGain"/> the reject fields name
    /// the FIRST offending resource (ordinal order, deterministic) and the
    /// formatted detail string; on <see cref="HarvestGainOutcome.Covered"/>
    /// the harvested manifest carries the witnessed per-resource totals over
    /// the checked span (windows + bridged boundary deltas) for the D8 debit
    /// reduction and the refined undocked-start gate.
    /// </summary>
    internal sealed class HarvestGainCheckResult
    {
        public HarvestGainOutcome Outcome;
        /// <summary>Why the check degraded to legacy (LegacyFallback only).</summary>
        public string LegacyReason;
        public string RejectResource;
        public double RejectGained;
        public double RejectHarvested;
        /// <summary>Formatted reject detail, e.g. "Ore: 120.0 gained, 100.0 harvested".</summary>
        public string RejectDetail;
        /// <summary>Witnessed harvested totals per routable resource within the checked span.</summary>
        public Dictionary<string, double> HarvestedManifest;
        /// <summary>The gain-anchor lineage leg (diagnostics + logging).</summary>
        public Recording AnchorLeg;
        /// <summary>
        /// Earliest in-span harvest window by StartUT - the D7 harvest-origin
        /// display endpoint source (open-time location). Null when no window
        /// fell inside the checked span.
        /// </summary>
        public RouteHarvestWindow FirstHarvestWindow;

        internal static HarvestGainCheckResult Legacy(string reason)
        {
            return new HarvestGainCheckResult
            {
                Outcome = HarvestGainOutcome.LegacyFallback,
                LegacyReason = reason
            };
        }
    }

    /// <summary>
    /// Pure gain-side flow-closure check for Supply Run analysis (M2 / plan
    /// D6): every positive between-window transport gain must be covered by
    /// witnessed harvest windows (plus the D5 bridged boundary deltas), else
    /// the recording is rejected naming the unaccounted quantity. ENGAGED only
    /// when every lineage leg carries a COMPLETE <see cref="RouteRunCargoManifest"/>
    /// (start half non-null AND <c>EndCaptured</c>); any absent / voided /
    /// start-only manifest degrades the whole tree to the legacy analysis.
    ///
    /// <para><b>Lineage</b> walks parents from the window-carrying source
    /// recording via <c>ParentBranchPointId</c> / <c>BranchPoint.ParentRecordingIds</c>
    /// (falling back to the chain-segment <c>ParentRecordingId</c> link when no
    /// branch point exists), disambiguating multi-parent merge points by
    /// part-pid overlap with the window's <c>TransportPartPersistentIds</c> -
    /// the depot's background leg never enters. PersistentIds are craft-baked,
    /// not launch-unique, which is exactly why scope ambiguity falls back to
    /// legacy with a log and NEVER picks the larger overlap.</para>
    ///
    /// <para><b>Anchor</b> (round-2 correction 2): NOT the tree root. The
    /// backward walk starts at the latest PRE-DOCK lineage leg (the transport's
    /// arrival leg; the dock-merge child starts AT the dock and is never an
    /// anchor candidate - its start manifest is scoped to the combined
    /// transport+endpoint stack) and extends EARLIER across a seam only while
    /// the earlier leg's run-manifest pid scope MATCHES the window scope, or
    /// is a superset AND the seam is a chain/continuation boundary - never
    /// across an Undock/Dock/Board branch point, and never across a Launch /
    /// Terminal boundary (a Launch BP means a NEW flight started; craft-baked
    /// pids would otherwise extend the anchor into the previous launch). In
    /// practice: a start-docked depot run anchors at the undock-split child
    /// (the walk stops at the Undock BP, so depot tanks never mask gains); a
    /// KSC launch anchors at the root (scope superset covers jettisoned
    /// boosters; <c>max(0, ...)</c> clamps booster-fuel losses).</para>
    ///
    /// <para><b>Harvested</b> sums only windows with
    /// <c>StartUT &gt;= anchorLeg.StartUT</c> and <c>&lt;= window.DockUT</c>
    /// (pre-anchor windows must not inflate the total - fail-open mask), plus
    /// the D5 bridged boundary deltas: a positive leg-N+1-start minus
    /// leg-N-end delta counts as harvested iff (a) the seam lies STRICTLY
    /// before the dock (the dock-merge child's start manifest is NEVER a
    /// bridge operand - bridging across the dock seam would credit the
    /// endpoint's entire inventory as harvested), (b) BOTH legs' pid scopes
    /// MATCH, and (c) a window was open at the boundary
    /// (<c>ClosedAtRecordingStop</c> on N or <c>OpenedAtRecordingStart</c> on
    /// N+1). Undefined-name positive gains count as UNACCOUNTED (plan D2:
    /// admission-direction outputs exclude undefined names, so harvested[r]
    /// is 0 for them and the check fails closed).</para>
    /// </summary>
    internal static class RouteHarvestAnalysis
    {
        private const string Tag = "Route";

        /// <summary>
        /// Gain/harvest comparison epsilon. Wider than the engine's per-window
        /// 1e-9 because harvested totals SUM positive deltas across several
        /// windows and bridges, accumulating double rounding well above 1e-9
        /// at tank-scale magnitudes; 1e-6 units is still far below any
        /// meaningful cargo quantity.
        /// </summary>
        internal const double GainEpsilon = 1e-6;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// One lineage leg plus the seam connecting it to its PARENT (the
        /// previous element in oldest-first order). The root-most leg has
        /// <see cref="HasParentSeam"/> false.
        /// </summary>
        internal struct LineageLeg
        {
            public Recording Rec;
            public bool HasParentSeam;
            /// <summary>True when the parent link is the chain-segment ParentRecordingId (no branch point).</summary>
            public bool ChainSeam;
            /// <summary>Branch-point type of the parent seam (valid when HasParentSeam and not ChainSeam).</summary>
            public BranchPointType SeamType;
        }

        /// <summary>
        /// The full check: lineage, presence gate, anchor, gains vs harvested.
        /// Pure except for logging. <paramref name="tree"/> may be null
        /// (legacy single-recording analysis) - the lineage is then the source
        /// recording alone, and any parent link forces the legacy fallback.
        ///
        /// <para>M3 composition (plan D3 / item 7): <paramref name="loadedManifest"/>
        /// is the window's pickup LOAD manifest
        /// (<see cref="RouteAnalysisEngine.BuildResourceLoadManifest"/>). A pickup
        /// window LEGITIMATELY raises the dock transport above the anchor start, so
        /// without this term the gain check would false-reject a pickup window as
        /// <see cref="HarvestGainOutcome.UntrackedGain"/> BEFORE the M3 flow
        /// closure ever runs. The loaded term is folded into the COVERED totals
        /// (a third witnessed source alongside harvest windows + boundary
        /// bridges); it does NOT inflate the harvested-only manifest the D8 debit
        /// reduction / refined undocked-start gate read
        /// (<see cref="HarvestGainCheckResult.HarvestedManifest"/> stays
        /// harvest-only). Pass null (or the legacy two-arg overload) for a
        /// delivery-only window.</para>
        /// </summary>
        internal static HarvestGainCheckResult CheckTransportGains(
            RecordingTree tree,
            Recording source,
            RouteConnectionWindow window,
            RouteAnalysisLogMode logMode)
        {
            return CheckTransportGains(tree, source, window, logMode, null);
        }

        /// <summary>
        /// M3 overload: <paramref name="loadedManifest"/> is the window's pickup
        /// LOAD manifest, folded into the COVERED totals by the verdict phase so a
        /// pickup window is not false-rejected as an untracked gain. Pass null for
        /// a delivery-only window (the four-arg overload does this).
        /// </summary>
        internal static HarvestGainCheckResult CheckTransportGains(
            RecordingTree tree,
            Recording source,
            RouteConnectionWindow window,
            RouteAnalysisLogMode logMode,
            Dictionary<string, double> loadedManifest)
        {
            if (!TryResolveHarvestLineage(tree, source, window, logMode,
                    out HarvestLineageResolution resolution, out HarvestGainCheckResult fallback))
                return fallback;

            List<LineageLeg> lineage = resolution.Lineage;
            int anchorIdx = resolution.AnchorIdx;
            int arrivalIdx = resolution.ArrivalIdx;
            Recording anchorLeg = resolution.AnchorLeg;
            double dockUT = resolution.DockUT;

            Dictionary<string, double> harvested = ComputeHarvestedTotals(
                lineage, anchorIdx, arrivalIdx, anchorLeg.StartUT, dockUT,
                out RouteHarvestWindow firstWindow);

            Dictionary<string, double> gains =
                ComputeFullRunGains(anchorLeg.RouteRunManifest, window);

            return EvaluateGainVerdict(
                source, logMode, lineage, anchorLeg, harvested, gains, firstWindow,
                loadedManifest);
        }

        /// <summary>
        /// Resolves the harvest lineage: validation, lineage walk, presence gate,
        /// dock UT, arrival leg, arrival scope, and gain anchor. On a fall-back
        /// outcome returns false with the legacy result in <paramref name="fallback"/>;
        /// on success returns true with the resolved lineage state. Extracted verbatim
        /// from CheckTransportGains (no logic change).
        /// </summary>
        private static bool TryResolveHarvestLineage(
            RecordingTree tree,
            Recording source,
            RouteConnectionWindow window,
            RouteAnalysisLogMode logMode,
            out HarvestLineageResolution resolution,
            out HarvestGainCheckResult fallback)
        {
            resolution = default;

            if (source == null || window == null)
            {
                fallback = LegacyWithLog(logMode, "null-source-or-window", source);
                return false;
            }

            HashSet<uint> windowScope = ToScopeSet(window.TransportPartPersistentIds);
            if (windowScope == null || windowScope.Count == 0)
            {
                fallback = LegacyWithLog(logMode, "window-scope-missing", source);
                return false;
            }

            List<LineageLeg> lineage = CollectTransportLineage(
                tree, source, windowScope, out string lineageFallback);
            if (lineage == null)
            {
                fallback = LegacyWithLog(logMode, lineageFallback, source);
                return false;
            }

            // Presence gate (plan D6 + round-2 correction 5): EVERY lineage leg
            // must carry a COMPLETE manifest - start half non-null AND
            // EndCaptured. Absent (pre-M2), voided (BG transit / optimizer
            // split), and start-only (ForceStop) all degrade to legacy.
            for (int i = 0; i < lineage.Count; i++)
            {
                RouteRunCargoManifest m = lineage[i].Rec?.RouteRunManifest;
                if (m == null)
                {
                    fallback = LegacyWithLog(logMode,
                        $"manifest-missing leg={lineage[i].Rec?.RecordingId ?? "<none>"}", source);
                    return false;
                }
                if (!m.IsComplete)
                {
                    fallback = LegacyWithLog(logMode,
                        $"manifest-incomplete leg={lineage[i].Rec.RecordingId ?? "<none>"}", source);
                    return false;
                }
            }

            double dockUT = window.DockUT;
            if (double.IsNaN(dockUT) || double.IsInfinity(dockUT))
            {
                fallback = LegacyWithLog(logMode, "dock-ut-invalid", source);
                return false;
            }

            // Arrival leg: the latest lineage leg that starts STRICTLY before
            // the dock. The dock-merge child starts AT the dock (its
            // ExplicitStartUT is the merge UT), so it is excluded here - its
            // combined-stack start manifest must never become a gain baseline
            // or a bridge operand.
            int arrivalIdx = -1;
            for (int i = lineage.Count - 1; i >= 0; i--)
            {
                if (lineage[i].Rec.StartUT < dockUT)
                {
                    arrivalIdx = i;
                    break;
                }
            }
            if (arrivalIdx < 0)
            {
                fallback = LegacyWithLog(logMode, "no-pre-dock-leg", source);
                return false;
            }

            HashSet<uint> arrivalScope =
                ToScopeSet(lineage[arrivalIdx].Rec.RouteRunManifest.TransportPartPersistentIds);
            if (!ScopeCovers(arrivalScope, windowScope))
            {
                fallback = LegacyWithLog(logMode,
                    $"arrival-scope-mismatch leg={lineage[arrivalIdx].Rec.RecordingId ?? "<none>"}",
                    source);
                return false;
            }

            int anchorIdx = FindGainAnchorIndex(lineage, arrivalIdx, windowScope);
            Recording anchorLeg = lineage[anchorIdx].Rec;

            resolution = new HarvestLineageResolution
            {
                Lineage = lineage,
                AnchorIdx = anchorIdx,
                ArrivalIdx = arrivalIdx,
                AnchorLeg = anchorLeg,
                DockUT = dockUT,
            };
            fallback = default;
            return true;
        }

        /// <summary>
        /// Deterministic verdict: scan resource names in ordinal order and reject on
        /// the FIRST uncovered positive gain, else emit the covered diag and return
        /// Covered. Extracted from CheckTransportGains (no logic change).
        ///
        /// <para>M3 composition (plan D3 / item 7): <paramref name="loadedManifest"/>
        /// is the window's pickup LOAD term, folded into the COVERED totals so a
        /// pickup window is not false-rejected as an untracked gain. The returned
        /// HarvestedManifest stays harvest-only.</para>
        /// </summary>
        private static HarvestGainCheckResult EvaluateGainVerdict(
            Recording source,
            RouteAnalysisLogMode logMode,
            List<LineageLeg> lineage,
            Recording anchorLeg,
            Dictionary<string, double> harvested,
            Dictionary<string, double> gains,
            RouteHarvestWindow firstWindow,
            Dictionary<string, double> loadedManifest)
        {
            // M3 composition (plan D3 / item 7): the covered totals are the
            // witnessed HARVEST plus the window's pickup LOAD term. A pickup
            // window legitimately raises dock transport above the anchor start;
            // crediting the loaded term here stops it from false-rejecting as an
            // untracked gain. covered is a verdict-only working dict; the
            // returned HarvestedManifest stays harvest-only (the D8 debit
            // reduction / refined undocked-start gate must not see the loaded
            // term as harvest).
            Dictionary<string, double> covered =
                new Dictionary<string, double>(harvested, StringComparer.Ordinal);
            if (loadedManifest != null)
            {
                foreach (KeyValuePair<string, double> kvp in loadedManifest)
                {
                    if (kvp.Value > 0.0)
                        AddTo(covered, kvp.Key, kvp.Value);
                }
            }

            // Deterministic verdict: scan resource names in ordinal order and
            // reject on the FIRST uncovered positive gain. Undefined names are
            // checked like any other (their covered total is 0 because the
            // window/bridge/load sums are admission-direction outputs - D2 fail
            // closed).
            var names = new List<string>(gains.Keys);
            names.Sort(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                double gained = gains[name];
                if (gained <= GainEpsilon)
                    continue;

                covered.TryGetValue(name, out double coveredAmount);
                if (gained - coveredAmount > GainEpsilon)
                {
                    harvested.TryGetValue(name, out double harvestedAmount);
                    return new HarvestGainCheckResult
                    {
                        Outcome = HarvestGainOutcome.UntrackedGain,
                        RejectResource = name,
                        RejectGained = gained,
                        RejectHarvested = harvestedAmount,
                        RejectDetail = FormatGainDetail(name, gained, harvestedAmount),
                        HarvestedManifest = harvested,
                        AnchorLeg = anchorLeg,
                        FirstHarvestWindow = firstWindow
                    };
                }
            }

            double harvestedTotal = 0.0;
            foreach (double amount in harvested.Values)
                harvestedTotal += amount;
            Diag(logMode, "harvest-gain-covered",
                $"RouteAnalysis harvest: gains covered source={source.RecordingId ?? "<none>"} " +
                $"anchor={anchorLeg.RecordingId ?? "<none>"} lineage={lineage.Count} " +
                $"resources={harvested.Count} " +
                $"harvestedTotal={harvestedTotal.ToString("R", IC)}");

            return new HarvestGainCheckResult
            {
                Outcome = HarvestGainOutcome.Covered,
                HarvestedManifest = harvested,
                AnchorLeg = anchorLeg,
                FirstHarvestWindow = firstWindow
            };
        }

        /// <summary>
        /// Resolved lineage state handed from <see cref="TryResolveHarvestLineage"/>
        /// to the gain-compute and verdict phases of <see cref="CheckTransportGains"/>.
        /// </summary>
        private struct HarvestLineageResolution
        {
            public List<LineageLeg> Lineage;
            public int AnchorIdx;
            public int ArrivalIdx;
            public Recording AnchorLeg;
            public double DockUT;
        }

        /// <summary>
        /// The reject detail shape shown to the player, e.g.
        /// <c>"Ore: 120.0 gained, 100.0 harvested"</c> (InvariantCulture).
        /// </summary>
        internal static string FormatGainDetail(string name, double gained, double harvested)
        {
            return string.Format(IC, "{0}: {1:F1} gained, {2:F1} harvested",
                string.IsNullOrEmpty(name) ? "unknown" : name, gained, harvested);
        }

        /// <summary>
        /// Walks parents from the window-carrying source recording to the
        /// root-most reachable leg. Returns the lineage OLDEST-FIRST (last
        /// element = <paramref name="source"/>), or null with a fallback
        /// reason when the walk cannot resolve unambiguously. Multi-parent
        /// merge points are disambiguated by run-manifest pid overlap with
        /// <paramref name="windowScope"/>: exactly one overlapping parent
        /// proceeds; zero or several fall back (never picks-larger-overlap -
        /// pids are craft-baked, not launch-unique).
        /// </summary>
        internal static List<LineageLeg> CollectTransportLineage(
            RecordingTree tree,
            Recording source,
            HashSet<uint> windowScope,
            out string fallbackReason)
        {
            fallbackReason = null;
            var lineage = new List<LineageLeg>();
            var visited = new HashSet<string>(StringComparer.Ordinal);

            Dictionary<string, BranchPoint> bpById = null;
            if (tree?.BranchPoints != null)
            {
                bpById = new Dictionary<string, BranchPoint>(StringComparer.Ordinal);
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = tree.BranchPoints[i];
                    if (bp != null && !string.IsNullOrEmpty(bp.Id))
                        bpById[bp.Id] = bp;
                }
            }

            Recording current = source;
            while (true)
            {
                if (current == null)
                {
                    fallbackReason = "lineage-null-leg";
                    return null;
                }
                if (!string.IsNullOrEmpty(current.RecordingId)
                    && !visited.Add(current.RecordingId))
                {
                    fallbackReason = $"lineage-cycle leg={current.RecordingId}";
                    return null;
                }

                // Added without a seam first; once the parent link resolves,
                // the seam fields are stamped back onto THIS element (each
                // leg carries the seam to its OWN parent).
                lineage.Add(new LineageLeg { Rec = current, HasParentSeam = false });
                int currentIdx = lineage.Count - 1;

                // Resolve the parent link: branch point first, then the
                // chain-segment ParentRecordingId link (chain seams carry no
                // branch point).
                if (!string.IsNullOrEmpty(current.ParentBranchPointId))
                {
                    if (tree?.Recordings == null || bpById == null
                        || !bpById.TryGetValue(current.ParentBranchPointId, out BranchPoint bp)
                        || bp == null)
                    {
                        fallbackReason =
                            $"parent-bp-unresolvable leg={current.RecordingId ?? "<none>"}";
                        return null;
                    }

                    Recording parent = ResolveTransportParent(
                        tree, bp, windowScope, out string parentFallback);
                    if (parent == null)
                    {
                        fallbackReason = parentFallback;
                        return null;
                    }

                    lineage[currentIdx] = new LineageLeg
                    {
                        Rec = current,
                        HasParentSeam = true,
                        ChainSeam = false,
                        SeamType = bp.Type
                    };
                    current = parent;
                    continue;
                }

                if (!string.IsNullOrEmpty(current.ParentRecordingId))
                {
                    if (tree?.Recordings == null
                        || !tree.Recordings.TryGetValue(current.ParentRecordingId, out Recording chainParent)
                        || chainParent == null)
                    {
                        // A chain parent outside this tree (legacy chains span
                        // CommittedRecordings, not the tree) cannot be walked
                        // purely - degrade to legacy rather than guess.
                        fallbackReason =
                            $"chain-parent-unresolvable leg={current.RecordingId ?? "<none>"}";
                        return null;
                    }

                    lineage[currentIdx] = new LineageLeg
                    {
                        Rec = current,
                        HasParentSeam = true,
                        ChainSeam = true
                    };
                    current = chainParent;
                    continue;
                }

                break; // root-most leg reached
            }

            lineage.Reverse(); // oldest-first
            return lineage;
        }

        // Picks the transport-side parent at a branch point. Single parent
        // resolves directly; multi-parent merge points resolve by run-manifest
        // pid overlap with the window scope - exactly one overlapping parent
        // wins, anything else falls back to legacy.
        private static Recording ResolveTransportParent(
            RecordingTree tree,
            BranchPoint bp,
            HashSet<uint> windowScope,
            out string fallbackReason)
        {
            fallbackReason = null;
            var parents = new List<Recording>();
            if (bp.ParentRecordingIds != null)
            {
                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                {
                    string pid = bp.ParentRecordingIds[i];
                    if (string.IsNullOrEmpty(pid))
                        continue;
                    if (tree.Recordings.TryGetValue(pid, out Recording rec) && rec != null)
                        parents.Add(rec);
                }
            }

            if (parents.Count == 0)
            {
                fallbackReason = $"parent-missing bp={bp.Id ?? "<none>"}";
                return null;
            }
            if (parents.Count == 1)
                return parents[0];

            Recording match = null;
            int overlapping = 0;
            for (int i = 0; i < parents.Count; i++)
            {
                HashSet<uint> scope =
                    ToScopeSet(parents[i].RouteRunManifest?.TransportPartPersistentIds);
                if (ScopeOverlaps(scope, windowScope))
                {
                    overlapping++;
                    match = parents[i];
                }
            }

            if (overlapping == 1)
                return match;

            fallbackReason =
                $"ambiguous-merge-parents bp={bp.Id ?? "<none>"} overlapping={overlapping}";
            return null;
        }

        /// <summary>
        /// Backward anchor walk (plan D6 / round-2 correction 2). Starts at
        /// the arrival leg and extends earlier across a seam only while the
        /// earlier leg's scope MATCHES the window scope, or is a SUPERSET and
        /// the seam is a chain/continuation boundary. Never across
        /// Undock/Dock/Board (vessel-composition changes), never across
        /// Launch/Terminal (new-flight boundaries; craft-baked pids would
        /// otherwise extend into a previous launch of the same craft).
        /// </summary>
        internal static int FindGainAnchorIndex(
            List<LineageLeg> lineage,
            int arrivalIdx,
            HashSet<uint> windowScope)
        {
            int anchorIdx = arrivalIdx;
            while (anchorIdx > 0)
            {
                LineageLeg child = lineage[anchorIdx];
                if (!child.HasParentSeam)
                    break;

                bool seamIsChainOrContinuation = child.ChainSeam
                    || child.SeamType == BranchPointType.VesselSwitchContinuation;
                bool seamBlocksExtension = !child.ChainSeam
                    && (child.SeamType == BranchPointType.Undock
                        || child.SeamType == BranchPointType.Dock
                        || child.SeamType == BranchPointType.Board
                        || child.SeamType == BranchPointType.Launch
                        || child.SeamType == BranchPointType.Terminal);
                if (seamBlocksExtension)
                    break;

                HashSet<uint> earlierScope = ToScopeSet(
                    lineage[anchorIdx - 1].Rec?.RouteRunManifest?.TransportPartPersistentIds);
                if (ScopeEquals(earlierScope, windowScope))
                {
                    anchorIdx--;
                    continue;
                }
                if (seamIsChainOrContinuation && ScopeCovers(earlierScope, windowScope))
                {
                    anchorIdx--;
                    continue;
                }
                break;
            }
            return anchorIdx;
        }

        /// <summary>
        /// Per-resource full-run gain: <c>max(0, dock - anchorStart)</c> over
        /// the union of dock-manifest and anchor-start names, skipping the
        /// always-ignored environmental resources (EC/IntakeAir). Undefined
        /// names stay IN (rejection direction - they must fail closed).
        /// </summary>
        internal static Dictionary<string, double> ComputeFullRunGains(
            RouteRunCargoManifest anchorManifest,
            RouteConnectionWindow window)
        {
            var gains = new Dictionary<string, double>(StringComparer.Ordinal);
            if (window == null)
                return gains;

            var names = new HashSet<string>(StringComparer.Ordinal);
            AddNames(names, window.DockTransportResources);
            AddNames(names, anchorManifest?.StartTransportResources);

            foreach (string name in names)
            {
                if (ResourceTransferability.IsAlwaysIgnored(name))
                    continue;

                double dock = GetAmount(window.DockTransportResources, name);
                double start = GetAmount(anchorManifest?.StartTransportResources, name);
                double gain = dock - start;
                gains[name] = gain > 0.0 ? gain : 0.0;
            }

            return gains;
        }

        /// <summary>
        /// Witnessed harvested totals over the checked span: positive window
        /// deltas (routable names only, via
        /// <see cref="RouteHarvestCapture.ComputeWindowHarvestedManifest"/>)
        /// for windows with <c>anchorStartUT &lt;= StartUT &lt;= dockUT</c>,
        /// plus the D5 bridged boundary deltas between consecutive in-span
        /// legs. Also surfaces the earliest in-span window (D7 endpoint).
        /// </summary>
        internal static Dictionary<string, double> ComputeHarvestedTotals(
            List<LineageLeg> lineage,
            int anchorIdx,
            int arrivalIdx,
            double anchorStartUT,
            double dockUT,
            out RouteHarvestWindow firstWindow)
        {
            var harvested = new Dictionary<string, double>(StringComparer.Ordinal);
            firstWindow = null;

            // Window deltas. Windows live on lineage legs; the span filter is
            // by window StartUT, so pre-anchor windows (drilling while docked
            // at the origin - an M3 limitation) and post-dock windows
            // (drilling while docked at the destination) never inflate the
            // total. The leg scan stops AT the arrival leg: the span bound is
            // inclusive (<= dockUT), so a dock-merge child's window opened at
            // exactly DockUT (reachable via the mergeUT Planetarium fallback +
            // a same-frame birth) would otherwise credit post-dock
            // combined-stack production as harvested.
            for (int i = 0; i <= arrivalIdx; i++)
            {
                List<RouteHarvestWindow> windows = lineage[i].Rec?.RouteHarvestWindows;
                if (windows == null)
                    continue;

                for (int w = 0; w < windows.Count; w++)
                {
                    RouteHarvestWindow hw = windows[w];
                    if (hw == null || double.IsNaN(hw.StartUT))
                        continue;
                    if (hw.StartUT < anchorStartUT || hw.StartUT > dockUT)
                        continue;

                    if (firstWindow == null || hw.StartUT < firstWindow.StartUT)
                        firstWindow = hw;

                    Dictionary<string, double> windowHarvest =
                        RouteHarvestCapture.ComputeWindowHarvestedManifest(hw);
                    foreach (KeyValuePair<string, double> kvp in windowHarvest)
                        AddTo(harvested, kvp.Key, kvp.Value);
                }
            }

            // Bridged boundary deltas (plan D5, review BLOCKER 1 scope): only
            // seams strictly inside the anchor..arrival span - the dock seam
            // (arrival -> merge child) is never iterated, so the merge child's
            // combined-stack start manifest is never an operand.
            for (int i = anchorIdx; i < arrivalIdx; i++)
            {
                Recording legN = lineage[i].Rec;
                Recording legN1 = lineage[i + 1].Rec;
                RouteRunCargoManifest mN = legN?.RouteRunManifest;
                RouteRunCargoManifest mN1 = legN1?.RouteRunManifest;
                if (mN == null || mN1 == null)
                    continue;

                // (a) seam strictly before the dock.
                if (!(legN1.StartUT < dockUT))
                    continue;

                // (b) both legs' pid scopes MATCH (same-scope manifests; a
                // merge/board seam with differing scopes is unbridgeable and
                // any positive delta there stays unaccounted - fails closed).
                if (!ScopeEquals(
                        ToScopeSet(mN.TransportPartPersistentIds),
                        ToScopeSet(mN1.TransportPartPersistentIds)))
                    continue;

                // (c) a converter was witnessed active at the boundary.
                if (!WindowOpenAtBoundary(legN, legN1))
                    continue;

                // Positive deltas, routable names only (admission direction:
                // an undefined-name boundary delta must NOT count as
                // harvested).
                if (mN1.StartTransportResources == null)
                    continue;
                foreach (KeyValuePair<string, ResourceAmount> kvp in mN1.StartTransportResources)
                {
                    if (!ResourceTransferability.IsRoutableResource(kvp.Key, out _))
                        continue;
                    double delta = kvp.Value.amount - GetAmount(mN.EndTransportResources, kvp.Key);
                    if (delta > 0.0)
                        AddTo(harvested, kvp.Key, delta);
                }
            }

            return harvested;
        }

        // D5 bridge condition (c): leg N's LAST window closed at the recording
        // stop, or leg N+1's FIRST window opened at the recording start.
        private static bool WindowOpenAtBoundary(Recording legN, Recording legN1)
        {
            RouteHarvestWindow last = LastWindowByStartUT(legN?.RouteHarvestWindows);
            if (last != null && last.ClosedAtRecordingStop)
                return true;

            RouteHarvestWindow first = FirstWindowByStartUT(legN1?.RouteHarvestWindows);
            return first != null && first.OpenedAtRecordingStart;
        }

        private static RouteHarvestWindow FirstWindowByStartUT(List<RouteHarvestWindow> windows)
        {
            RouteHarvestWindow best = null;
            if (windows == null)
                return null;
            for (int i = 0; i < windows.Count; i++)
            {
                RouteHarvestWindow w = windows[i];
                if (w == null || double.IsNaN(w.StartUT))
                    continue;
                if (best == null || w.StartUT < best.StartUT)
                    best = w;
            }
            return best;
        }

        private static RouteHarvestWindow LastWindowByStartUT(List<RouteHarvestWindow> windows)
        {
            RouteHarvestWindow best = null;
            if (windows == null)
                return null;
            for (int i = 0; i < windows.Count; i++)
            {
                RouteHarvestWindow w = windows[i];
                if (w == null || double.IsNaN(w.StartUT))
                    continue;
                if (best == null || w.StartUT > best.StartUT)
                    best = w;
            }
            return best;
        }

        // ------------------------------------------------------------------
        // Scope-set helpers (order-insensitive pid-set comparisons)
        // ------------------------------------------------------------------

        internal static HashSet<uint> ToScopeSet(List<uint> pids)
        {
            if (pids == null)
                return null;
            return new HashSet<uint>(pids);
        }

        internal static bool ScopeEquals(HashSet<uint> a, HashSet<uint> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0)
                return false;
            return a.Count == b.Count && a.IsSupersetOf(b);
        }

        /// <summary>True when <paramref name="outer"/> covers every pid in <paramref name="inner"/> (match or superset).</summary>
        internal static bool ScopeCovers(HashSet<uint> outer, HashSet<uint> inner)
        {
            if (outer == null || inner == null || outer.Count == 0 || inner.Count == 0)
                return false;
            return outer.IsSupersetOf(inner);
        }

        internal static bool ScopeOverlaps(HashSet<uint> a, HashSet<uint> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0)
                return false;
            return a.Overlaps(b);
        }

        // ------------------------------------------------------------------
        // Small utilities
        // ------------------------------------------------------------------

        private static void AddNames(HashSet<string> names, Dictionary<string, ResourceAmount> manifest)
        {
            if (manifest == null)
                return;
            foreach (string name in manifest.Keys)
            {
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }

        private static double GetAmount(Dictionary<string, ResourceAmount> manifest, string name)
        {
            return manifest != null && manifest.TryGetValue(name, out ResourceAmount ra)
                ? ra.amount
                : 0.0;
        }

        private static void AddTo(Dictionary<string, double> manifest, string name, double amount)
        {
            manifest.TryGetValue(name, out double existing);
            manifest[name] = existing + amount;
        }

        // M2 logging plan: the legacy fallback is logged once per analysis in
        // Diagnostic mode (the one-shot Create Route / commit-time callers);
        // the ~1/second Quiet sweep folds into one shared rate-limited key so
        // the poll cannot spam.
        private static HarvestGainCheckResult LegacyWithLog(
            RouteAnalysisLogMode logMode, string reason, Recording source)
        {
            Diag(logMode, "harvest-legacy-fallback",
                $"RouteAnalysis harvest: lineage unresolved/manifest missing -> legacy analysis " +
                $"(reason={reason}) source={source?.RecordingId ?? "<none>"}");
            return HarvestGainCheckResult.Legacy(reason);
        }

        private static void Diag(RouteAnalysisLogMode logMode, string rateKey, string message)
        {
            if (logMode == RouteAnalysisLogMode.Diagnostic)
                ParsekLog.Info(Tag, message);
            else
                ParsekLog.VerboseRateLimited(Tag, rateKey, message);
        }
    }
}
