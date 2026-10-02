using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Logistics window: a Supply Routes management surface with three sections
    /// driven by route enablement.
    /// <list type="bullet">
    ///   <item><b>Active Routes</b> - running on their schedule (including the
    ///   held states and the hard-broken states, distinguished by the merged Status
    ///   cell's word and colour). Interact: Pause. A held run still flies the ghost
    ///   (the world looks busy) but transfers/charges nothing; the Status cell names
    ///   the reason.</item>
    ///   <item><b>Paused Routes</b> - stored but not running on a schedule, plus a
    ///   route armed by Send making its one run (it stays in this table before,
    ///   during and after that run). Interact: Activate, Send (one run, then stay
    ///   Paused). Delete lives in the expanded detail block beside Rename.</item>
    ///   <item><b>Candidates</b> - derived (not stored) from fully-sealed,
    ///   eligible Supply Run trees that are not already promoted. Action: Create
    ///   Route (promotes to a Paused route).</item>
    /// </list>
    /// Rows use the Recordings-window caret style (click the name to expand a
    /// detail panel). Available in both FLIGHT and SPACECENTER. Mirrors the
    /// ClickThruBlocker / resize / input-lock pattern from <see cref="SpawnControlUI"/>.
    /// <para>Basic / Advanced (<see cref="UiSurface.LogisticsRouteTuning"/>): the header,
    /// every row and every detail block read ONE bool latched at the top of
    /// <see cref="DrawWindow"/> (<see cref="drawTuning"/>), so a Layout pass and its
    /// Repaint pass always draw the same control set. Basic shows the Every column
    /// read-only and hides Runs, the steppers, Link, Recent runs and Flights used.</para>
    /// </summary>
    internal class LogisticsWindowUI
    {
        private readonly ParsekUI parentUI;

        private bool showWindow;
        private Rect windowRect;
        /// <summary>
        /// The live window rect, readable and writable from outside the draw pass.
        /// <para>Two consumers: an in-game test that needs the measured rect, and the
        /// automation-only <c>UiAction op=rect</c> seam op, which places and enlarges a
        /// window so one capture shows more rows than the default size fits. Writing it is
        /// safe before the first draw as well as after: <c>DrawIfOpen</c> only seeds its
        /// default when <c>width &lt; 1</c>, so a commanded rect suppresses the seed rather
        /// than being overwritten by it.</para>
        /// </summary>
        // ----- GUI-census seam accessors (UiAction op=expand / op=picker) -----
        //
        // One set backs every disclosure in this window, and these are ALL FOUR key shapes
        // it holds, re-derived from the four sites that write `expandedRows` rather than
        // from this comment's earlier wording:
        //   DrawRouteRow      route.Id ?? "<no-id>"          (:1040)
        //   DrawCandidateRow  "cand:" + (tree.Id ?? "<no-tree>")  (:1546)
        //   the three fixed subsection headers  dormant: / nearmiss: / dismissed: section
        // The seam drives the set by RAW KEY and the three section constants are published
        // here rather than copied into the seam.
        //
        // EnumerateRowKeysForTesting must list every one of those shapes, because it is
        // what `op=expand key=all` iterates AND what `key=row:<k>` is validated against:
        // a shape it omits is silently unexpandable in bulk and REJECTED
        // `expand-key-unknown` when named. The candidate rows were the omission
        // (fixed 2026-09-11); a route with a null id is deliberately still skipped,
        // because "<no-id>" collides across every such route and naming one would toggle
        // an arbitrary other.
        //
        // The link picker's opener is the one genuinely unreachable surface in the census's
        // click-gated list: it is armed from an expanded route row's button and is private.
        // Exposed as a wrapper over the SAME private method, so the picker opens through its
        // own production path (source validation, rect reset, selection cleared) and the
        // seam adds no second opener.

        internal const string DormantSectionKeyForTesting = DormantSectionKey;
        internal const string NearMissSectionKeyForTesting = NearMissSectionKey;
        internal const string DismissedSectionKeyForTesting = DismissedSectionKey;

        internal bool IsRowExpandedForTesting(string key)
            => key != null && expandedRows.Contains(key);

        internal bool SetRowExpandedForTesting(string key, bool expanded)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return expanded ? expandedRows.Add(key) : expandedRows.Remove(key);
        }

        internal int ExpandedRowCountForTesting => expandedRows.Count;

        /// <summary>Every raw <c>expandedRows</c> key the window could draw a disclosure
        /// for: the three fixed sections, every committed route's id, and one
        /// <c>"cand:"</c> key per candidate row.
        ///
        /// <para>The candidate keys come from this window's OWN cached candidate list, not
        /// from a fresh <c>RouteCandidateFinder.DeriveCandidates()</c> call: the cache is
        /// what <c>DrawCandidateRow</c> draws from (rebuilt on the ~1 Hz refresh), so a
        /// live derivation here could answer keys for rows the window is not drawing this
        /// second - and deriving candidates off the throttle is the one thing the cache
        /// exists to prevent. The null form matches the draw site's exactly
        /// (<c>"cand:" + (Tree?.Id ?? "&lt;no-tree&gt;")</c>); a mismatch there would be
        /// worse than the omission, because the key would LOOK addressable and toggle
        /// nothing.</para></summary>
        internal List<string> EnumerateRowKeysForTesting()
        {
            var keys = new List<string>
            {
                DormantSectionKey, NearMissSectionKey, DismissedSectionKey,
            };
            IReadOnlyList<Logistics.Route> routes = Logistics.RouteStore.CommittedRoutes;
            for (int i = 0; i < routes.Count; i++)
            {
                string id = routes[i] != null ? routes[i].Id : null;
                if (!string.IsNullOrEmpty(id)) keys.Add(id);
            }
            List<RouteCandidate> candidates = cachedCandidates;
            for (int i = 0; candidates != null && i < candidates.Count; i++)
                keys.Add(CandidateRowKey(candidates[i]));
            return keys;
        }

        /// <summary>The <c>expandedRows</c> key of one candidate row. ONE site, called by
        /// both <see cref="DrawCandidateRow"/> and
        /// <see cref="EnumerateRowKeysForTesting"/>, so the drawn key and the enumerated
        /// key cannot drift - which is the defect the enumeration's omission was a
        /// symptom of.</summary>
        internal static string CandidateRowKey(RouteCandidate candidate)
            => "cand:" + (candidate?.Tree?.Id ?? "<no-tree>");

        /// <summary>Opens the round-trip link picker on a route through the production
        /// opener. The mouse position only seeds the popup's first rect, so the seam passes
        /// the window's own origin rather than a cursor it does not have.</summary>
        internal bool OpenLinkPickerForTesting(Logistics.Route source)
        {
            if (source == null) return false;
            OpenLinkPicker(source, new Vector2(windowRect.x, windowRect.y));
            return linkPickerOpen;
        }

        /// <summary>Whether the link picker is up, for the seam's post-settle
        /// read-back.</summary>
        internal bool LinkPickerOpenForTesting => linkPickerOpen;

        /// <summary>Whether the link picker's opener (the detail block's Link control) is
        /// drawn in the applied mode. The <c>op=picker window=logistics</c> seam refuses
        /// with <c>picker-hidden-in-basic</c> when it is not, rather than opening a picker
        /// the next Basic pass would close.</summary>
        internal bool LinkPickerAvailableForTesting =>
            LogisticsRoutePresentation.ShowsRouteTuning(ParsekUI.AppliedUiComplexityMode);

        /// <summary>Whether the round-trip link picker popup is open (the Basic close
        /// set's read; see <c>ParsekUI.BuildGatedWindowCloseSet</c>).</summary>
        internal bool IsLinkPickerOpen => linkPickerOpen;

        /// <summary>
        /// Closes the round-trip link picker on a switch to Basic: its opener (the detail
        /// block's Link control) is Advanced-only, so a picker left standing would offer a
        /// tuning action Basic does not show. Owns no input lock; the Logistics window
        /// itself stays open.
        /// </summary>
        internal void CloseLinkPickerForModeChange()
        {
            if (!linkPickerOpen) return;
            linkPickerOpen = false;
            linkPickerSelectedId = null;
            ParsekLog.Verbose("UI", "Logistics: link picker closed for the switch to Basic");
        }

        // The three route confirms the GUI census raises (UiAction op=raise popup=
        // deleteroute | deletedormantroute | createroute). Wrappers over the SAME private
        // spawn methods the row buttons reach through ApplyPendingActions, so each dialog
        // is spawned by its own production path (body text, button labels and callbacks
        // included) and the seam adds no second spawn site. Each returns false when its own
        // guard would have returned silently, so the caller can answer
        // dialog-target-unavailable instead of reporting a modal that is not there.

        internal bool SpawnDeleteRouteConfirmationForTesting(Route route)
        {
            if (route == null) return false;
            SpawnDeleteRouteConfirmation(route);
            return true;
        }

        internal bool SpawnDeleteDormantRouteConfirmationForTesting(Route route)
        {
            if (route == null) return false;
            SpawnDeleteDormantRouteConfirmation(route);
            return true;
        }

        internal bool SpawnCreateRouteConfirmationForTesting(RouteCandidate candidate)
        {
            if (candidate?.Tree == null || candidate.Analysis == null) return false;
            SpawnCreateRouteConfirmation(candidate);
            return true;
        }

        /// <summary>The candidate list the window is CURRENTLY DRAWING.
        ///
        /// <para>The cache rather than a fresh <c>RouteCandidateFinder.DeriveCandidates()</c>
        /// call, for <see cref="EnumerateRowKeysForTesting"/>'s reason: the cache is what
        /// <c>DrawCandidateRow</c> draws from (rebuilt on the ~1 Hz refresh), so a live
        /// derivation here could hand back a candidate the window is not drawing this
        /// second - and deriving candidates off the throttle is the one thing the cache
        /// exists to prevent. A census that raises the Create Route confirm over a
        /// candidate row nobody can see is exactly the dishonest picture the closed dialog
        /// set exists to avoid.</para></summary>
        internal IReadOnlyList<RouteCandidate> CachedCandidatesForTesting => cachedCandidates;

        internal Rect WindowRectForTesting
        {
            get { return windowRect; }
            set { windowRect = value; }
        }

        // Persist the window rect (position + size) across scene changes. ParsekUI
        // and all its sub-windows are re-instantiated per scene (the two ParsekUI
        // constructors each do `new LogisticsWindowUI(this)`), so an instance-only
        // rect would reset to the first-open default on every KSC <-> flight
        // transition. A static survives the re-instantiation for the whole KSP
        // session, so a window the player resized or moved reappears unchanged after
        // a scene change. Stays default-zero until the window is drawn once, which
        // keeps the first-open default-init intact.
        private static Rect sessionWindowRect;
        private bool windowHasInputLock;
        private const string InputLockId = "Parsek_LogisticsWindow";

        private bool isResizing;
        private Vector2 scrollPos;
        // Horizontal scroll when a narrow screen caps the window below MinWindowWidth
        // (WideWindowLayout): the section stack keeps its natural width inside the
        // existing scroll view, whose own horizontal bar then reaches every column.
        private readonly WideWindowScroll wideScroll =
            new WideWindowScroll("Logistics window", MinWindowWidth);
        private Rect lastWindowRect;

        // Expand/collapse state, keyed by route Id (routes) or "cand:"+treeId (candidates).
        private readonly HashSet<string> expandedRows = new HashSet<string>();

        // Throttled candidate cache: deriving candidates walks every committed
        // tree through RouteAnalysisEngine, so recompute at most once per second
        // while the window is open rather than every IMGUI frame.
        private List<RouteCandidate> cachedCandidates = new List<RouteCandidate>();
        private float lastCandidateComputeRealtime = -1f;
        /// <summary>
        /// The key this window's IMGUI window id is hashed from. Named once and used at
        /// BOTH the <c>ClickThruBlocker.GUILayoutWindow</c> call below and
        /// <c>UiWindowHandle.GetWindowId</c> (wired by
        /// <c>ParsekTestCommandAddon.ResolveWindowHandle</c>), which the <c>UiAction op=find</c>
        /// seam uses to scope a captured GUI tree to THIS window's subtree. Two copies of
        /// the literal would let the seam search the wrong window's children and answer a
        /// plausible rect for a control in another window.
        /// </summary>
        internal const string WindowIdKey = "ParsekLogistics";

        private const float CandidateRecomputeIntervalSeconds = 1.0f;

        // Run-cost (Phase 3.4): per-candidate net funds cost, keyed by tree id and
        // recomputed on the SAME ~1 Hz candidate-cache timer as cachedCandidates.
        // The candidate cost is an O(actions) ELS scan plus a snapshot part-cost
        // walk, so it MUST NOT run on the IMGUI draw path; the "Would deliver" cell
        // only READS this cache. Empty until the first candidate refresh; a cache
        // miss draws no suffix (the cell is unchanged).
        private readonly Dictionary<string, RouteRunCostCalculator.RouteRunCost> candidateRunCostCache =
            new Dictionary<string, RouteRunCostCalculator.RouteRunCost>();

        // M3 near-miss cache: the "recently committed trees not yet eligible" list
        // (DeriveNearMisses walks every committed tree through RouteAnalysisEngine,
        // exactly like DeriveCandidates), throttled on the SAME ~1 Hz timer as the
        // candidate cache so the subsection never scans live on the IMGUI draw path.
        // The draw path only READS this cached list.
        private List<RouteNearMiss> cachedNearMisses = new List<RouteNearMiss>();
        private float lastNearMissComputeRealtime = -1f;

        // M6 candidate intent helper: cached display rows for the collapsed
        // "Dismissed (N)" subsection, rebuilt on the SAME ~1 Hz timer as the
        // candidate cache (inside GetCandidates) so tree display names are never
        // resolved live on the IMGUI draw path. Sorted by label (then id) for a
        // stable row order across HashSet iteration.
        private readonly List<(string treeId, string label)> cachedDismissedRows =
            new List<(string treeId, string label)>();

        // Throttled per-route legibility cache (Phase 2 H1/H2/H3). Recomputing the
        // next-dock-crossing countdown (H1, a LoopUnit build) and the realized /
        // cumulative delivery summary (H2/H3, an ELS scan) is NOT free, and many
        // routes can be open at once, so we recompute the WHOLE dictionary on a
        // realtime timer (mirrors the candidate cache) once per ~1s rather than per
        // route per IMGUI frame. The draw path only READS the cache by route.Id.
        // In-memory instance state only: it is rebuilt from RouteStore + the ledger
        // and persists nothing across save/load.
        private readonly Dictionary<string, RouteLegibility> legibilityCache =
            new Dictionary<string, RouteLegibility>();
        private float lastLegibilityComputeRealtime = -1f;

        /// <summary>
        /// Dirties the throttled route-legibility cache, which every production handler that
        /// changes a route's NAME, LINK or CADENCE does as its last act.
        ///
        /// <para>For the automation-only <c>RouteCommand action=link|unlink|set-cadence</c>
        /// actions. Without it the window keeps drawing the pre-action Interval, Next and
        /// Destination cells for up to a refresh period - the sort keys live in that cache -
        /// so a capture taken straight after the action photographs the OLD row under a
        /// label claiming the new one. The seam does what the click does.</para>
        /// </summary>
        internal void DirtyLegibilityCacheForTesting()
        {
            lastLegibilityComputeRealtime = -1f;
        }
        private const float LegibilityRecomputeIntervalSeconds = 1.0f;

        // L2 route-table sort state. Shared by both the Active and the Paused section
        // (the column the player clicks sorts both tables the same way). Default is
        // Name ascending so the table reads alphabetically until the player sorts.
        private LogisticsRouteSortColumn routeSortColumn = LogisticsRouteSortColumn.Name;
        private bool routeSortAscending = true;

        /// <summary>
        /// The route tables' shared sort column (as the
        /// <see cref="LogisticsRouteSortColumn"/> value's own explicit number) and direction,
        /// for the automation-only <c>UiAction op=sort</c> seam op. ONE sort state drives
        /// BOTH the Active and the Paused table, exactly as the header click does.
        ///
        /// <para>The setter clears both section row counts, which is what the header click's
        /// <c>onChanged</c> callback does: the two caches re-sort only when their row count,
        /// the sort tuple or the legibility stamp moves, and clearing the counts guarantees
        /// the miss even when the row count is unchanged.</para>
        /// </summary>
        internal int RouteSortColumnIndexForTesting
        {
            get { return (int)routeSortColumn; }
            set
            {
                if (value < (int)LogisticsRouteSortColumn.Name
                    || value > (int)LogisticsRouteSortColumn.Delivery)
                    return;
                routeSortColumn = (LogisticsRouteSortColumn)value;
                cachedActiveCount = -1;
                cachedPausedCount = -1;
            }
        }

        internal bool RouteSortAscendingForTesting
        {
            get { return routeSortAscending; }
            set
            {
                routeSortAscending = value;
                cachedActiveCount = -1;
                cachedPausedCount = -1;
            }
        }

        // L2 cached-sorted route lists, one independent cache PER section (Active and
        // Paused are two disjoint row sets), mirroring the SpawnControlUI cached-sorted
        // idiom (cachedSortedCandidates + invalidation tuple). A section re-sorts ONLY
        // when its row count changes, the sort key/direction changes, OR the throttled
        // legibility cache was refreshed this tick (so the NextDelivery / Destination
        // sort keys, which live in that cache, do not go stale). NEVER per IMGUI frame.
        // The legibility-refresh token is lastLegibilityComputeRealtime, bumped once per
        // ~1 Hz refresh (and reset to -1 on any dirtying mutation), so comparing it to a
        // cached copy detects exactly the refreshes that could move a sort key.
        private List<Route> cachedSortedActive = new List<Route>();
        private int cachedActiveCount = -1;
        private List<Route> cachedSortedPaused = new List<Route>();
        private int cachedPausedCount = -1;
        private LogisticsRouteSortColumn cachedRouteSortColumn = LogisticsRouteSortColumn.Name;
        private bool cachedRouteSortAscending = true;
        // Per-section legibility stamps: the Active and Paused tables are drawn in the
        // same IMGUI pass (Active first), so a SHARED stamp would be consumed by Active
        // and leave Paused sorting on stale dynamic keys after a ~1 Hz refresh. Keep one
        // stamp per section (mirroring cachedActiveCount / cachedPausedCount).
        private float cachedActiveLegibilityStamp = -2f;
        private float cachedPausedLegibilityStamp = -2f;

        /// <summary>
        /// One route's recomputed-on-timer legibility values: every cell and detail line
        /// whose inputs are not free (a LoopUnit build for the countdown, an ELS scan for
        /// deliveries and run cost, a FlightGlobals resolve for the endpoint names, a date
        /// format). Built once per ~1 Hz cache refresh, read by route.Id while drawing, so
        /// no IMGUI frame recomputes them. In-memory only; nothing persists.
        /// </summary>
        private struct RouteLegibility
        {
            // Countdown: the branch and seconds (the Next sort key), the run-scheduled gate
            // (a Paused route not armed by Send shows "-"), and the rendered cell, hover and
            // detail line (the Missions "T- " form, " (!)" when warned).
            public LogisticsCountdownPresentation.CountdownBranch CountdownBranch;
            public double CountdownSeconds;
            public bool RunScheduled;
            public string NextCellText;
            public string NextTooltip;
            public string NextLine;

            // The route's window basis + the label after the cadence ("(Duna transfer)";
            // null for a flat route). Read by the Advanced stepper and the Every hover.
            public RouteWindowBasis Basis;
            public string BasisLabel;
            // The read-only Every cell ("every 4.0d" / "every 2nd window") and its hover.
            public string EveryText;
            public string EveryTooltip;

            // Realized deliveries from the ledger: whether any, the latest run's line and
            // its shortfall tint, the cumulative total, and the dated "Last delivered" line.
            public bool HasDeliveries;
            public string LastCycleText;     // "delivered 40.0 of 150.0 LiquidFuel (110.0 did not fit)"
            public bool LastCycleShortfall;  // drives the yellow tint
            public string CumulativeText;    // "1240.0 LiquidFuel, 30.0 Oxidizer" or "(none)"
            public string LastDeliveredLine;

            // The Route cell's second line ("KSC -> Depot Station Duna I") and its hover
            // (the full origin and destination incl. coordinates), plus the destination
            // name alone (the Delivers line and the Destination sort key). Resolved here
            // because an unresolved surface endpoint does an O(vessels) FlightGlobals scan.
            public string OriginShort;
            public string DestinationText;
            public string DestinationTooltip;
            public string FromToText;
            public string FromToTooltip;
            public string DeliversCellText;  // per-run manifest for the Delivers column
            public string DeliversLine;      // "Delivers each run: ... to <destination>."

            // DestinationFull free-capacity context line, from a LIVE capacity probe over
            // the resolved destination (DestinationFull only; null otherwise).
            public string CapacityContext;

            // Per-run net funds cost (Career + KSC origin only; Applicable / CostKnown
            // false otherwise and the detail draws nothing). An O(actions) ELS scan.
            public RouteRunCostCalculator.RouteRunCost RunCost;

            // The persisted last hold in player language: HoldShort is the long clause,
            // HoldText the dated detail line ("Last run held on <date>: ..."), HoldCellText
            // the compact "Held: ..." Status-cell form. All null when no hold displays
            // (LogisticsHoldPresentation.ShouldDisplayHold), so a not-yet-refreshed row
            // never flashes a hold line.
            public string HoldText;
            public string HoldShort;
            public string HoldCellText;

            // The dated last-partial-delivery report; null when the latest delivery was
            // full or none happened. Shown on every status: it reports a past loss.
            public string PartialText;

            // The merged Status cell (word, text, colour) and its one-sentence hover. The
            // armed flags are the Send-armed / Pause-armed classification the Interact
            // cell also reads.
            public LogisticsRoutePresentation.StatusCell Status;
            public string StatusTooltip;

            // Advanced "Recent runs": one compact line per recent completed run, newest
            // first, bounded to LogisticsFlowPresentation.MaxCyclesShown. Null when the
            // route has no run-scoped ledger rows yet.
            public List<LogisticsFlowPresentation.CycleFlowLine> FlowLines;
        }

        // Deferred mutations: collected during the draw loop and applied after the
        // scroll view so we never mutate RouteStore.CommittedRoutes mid-iteration.
        private Route pendingPause;
        private Route pendingActivate;
        private Route pendingSendOnce;
        // M6: which routes were armed via Send Once (not Pause). Both arming paths set
        // the same Route.PauseAfterCurrentCycle flag and a Send-Once arm un-pauses
        // Paused -> Active -> InTransit while still armed, so route.Status alone cannot
        // tell a Send-Once-in-transit from a Pause-armed-in-transit. This UI-side set
        // (populated when a Send Once succeeds, pruned on the ~1 Hz refresh) records the
        // provenance so the disabled-button label is correct for the whole cycle; the
        // status heuristic stays the fallback for entries lost across save/reload.
        private readonly HashSet<string> sendOnceArmedRouteIds = new HashSet<string>();
        // Two-step delete: the X button captures the route to confirm here during
        // the draw loop; ApplyPendingActions spawns the confirm dialog (once) and
        // clears the field. The dialog's Delete button calls RouteStore.RemoveRoute
        // directly in its callback, which is safe
        // because the callback fires outside the draw-loop route iteration. Deletion
        // never happens without the player confirming.
        private Route pendingConfirmDeleteRoute;
        // Dormant-section delete: same two-step shape as pendingConfirmDeleteRoute
        // but the dialog's Delete callback routes through
        // RouteStore.RemoveDormantRoute (dormant routes live in the sparse
        // dormant list, never the committed list).
        private Route pendingConfirmDeleteDormantRoute;
        private RouteCandidate pendingCreate;
        // Deferred cadence edit: the stepper records (route, new N) during the draw
        // loop; ApplyPendingActions recomputes DispatchInterval = N x span after the
        // scroll view (same deferred-mutation discipline as the action buttons).
        private Route pendingCadenceRoute;
        private int pendingCadenceMultiplier;
        // Deferred priority edit (M1 dispatch priority): the stepper records
        // (route, new value) during the draw loop; ApplyPendingActions commits via
        // RoutePriority.Apply after the scroll view (same deferred-mutation
        // discipline as the cadence stepper).
        private Route pendingPriorityRoute;
        private int pendingPriorityValue;
        // M6 candidate intent helper: deferred dismiss / restore of a candidate
        // tree. The row buttons record the tree id + display label during the
        // draw loop; ApplyPendingActions commits through
        // RouteStore.DismissCandidateTree / RestoreCandidateTree after the
        // scroll view (same deferred-mutation discipline as the action buttons)
        // and dirties the candidate + near-miss caches so the row disappears /
        // reappears immediately instead of waiting out the ~1s timer.
        private string pendingDismissTreeId;
        private string pendingDismissLabel;
        private string pendingRestoreTreeId;
        private string pendingRestoreLabel;

        // M1 inline interval text field (deferred-commit, SettingsWindowUI idiom).
        // Keyed by route.Id (NOT a row index) because routes are re-sectioned /
        // added / removed between frames. While editing, the typed text is held here
        // and committed on Enter or click-outside through ParseAndSnapInterval ->
        // ApplyMultiplier (run DIRECTLY in the commit, not via a frame-reset pending
        // field, per the QW2 lesson). intervalEditRect is the field's screen rect for
        // the click-outside-to-commit hit test.
        private string intervalEditRouteId;
        private string intervalEditText;
        private bool intervalEditFocused;
        private Rect intervalEditRect;

        // M2 detail-panel route rename (deferred-commit, RecordingsTableUI idiom).
        // Keyed by route.Id for the same re-sectioning reason as the interval edit.
        // The rename commit writes Route.Name directly (already persisted via the
        // existing codec, so no schema work); empty / whitespace / unchanged input is
        // rejected by the pure LogisticsRenamePresentation.ComputeRouteRename helper.
        private string renamingRouteId;
        private string renamingRouteText;
        private bool renamingRouteFocused;
        private Rect renamingRouteRect;

        // M4c round-trip link picker (synchronous IMGUI popup, GroupPickerUI idiom).
        // Persistent state (deliberately NOT in the frame-top pending reset): armed by
        // the detail-panel "Link round-trip..." button, drawn as its OWN window from
        // DrawIfOpen after the main window (a nested GUILayoutWindow is illegal), and
        // committed inline on the Link button (RouteStore.LinkRoutes) so the QW2
        // deferred-field trap does not apply (the commit runs inside the draw pass).
        private bool linkPickerOpen;
        private string linkPickerSourceRouteId;
        private string linkPickerSourceName;
        private string linkPickerSelectedId;
        private Vector2 linkPickerPosition;
        private Rect linkPickerRect;
        private bool linkPickerResizing;
        private Vector2 linkPickerScroll;
        private const float LinkPickerMinW = 240f;
        private const float LinkPickerMinH = 180f;

        // Status text styles (lazy; mirrors RecordingsTableUI.EnsureStatusStyles). The
        // five colours come from the shared ParsekUI palette; Scheduled is the label's
        // default white (statusStyleWhite).
        private GUIStyle statusStyleGreen;   // Delivering
        private GUIStyle statusStyleYellow;  // Held
        private GUIStyle statusStyleRed;     // Broken
        private GUIStyle statusStyleGrey;    // Paused, and the "-" of an unscheduled Next
        private GUIStyle statusStyleCyan;    // New / Sending one run / Pausing after this run
        private GUIStyle statusStyleWhite;   // Scheduled
        private GUIStyle detailStyle;
        // The two-line Route cell: line 1 the caret + name, line 2 the grey from/to at the
        // same font size. Both clip rather than wrap, so every row is exactly two lines
        // tall and the grid of single-line cells beside it never shifts. The muted colour
        // is set ON THE STYLE (MissionsWindowUI's MissionSummaryTextColor), never through
        // GUI.color.
        private GUIStyle routeNameStyle;
        private GUIStyle routeFromToStyle;
        // Wrapping cell for Delivers and Status (a long manifest or hold reason takes a
        // second line inside its own column).
        private GUIStyle wrapCellStyle;
        private GUIStyle wrapStatusGreen, wrapStatusYellow, wrapStatusRed, wrapStatusGrey,
            wrapStatusCyan, wrapStatusWhite;
        // The Next countdown in the Missions amber (MissionsWindowUI.LoopPeriodClampColor).
        private GUIStyle nextAmberStyle;
        // The Interact column: zero horizontal margin so a single and a pair line up on
        // every row (MissionsWindowUI's interactButtonStyle / interactPairButtonStyle), and
        // the centred header cell over it (the Missions colHdrCellContainerStyle shape).
        private GUIStyle interactButtonStyle;
        private GUIStyle interactPairButtonStyle;
        private GUIStyle interactHeaderContainerStyle;
        private GUIStyle interactHeaderLabelStyle;

        // The route tuning gate (UiSurface.LogisticsRouteTuning), latched ONCE at the top
        // of DrawWindow from the frame-latched ParsekUI.AppliedUiComplexityMode. The header,
        // every row and every detail block read this field, never the mode, so the Layout
        // and Repaint passes of one frame always add and drop the same columns.
        private bool drawTuning = true;

        // Column widths. Header and rows use the same constants and live in the
        // same per-section box, so columns line up like the Recordings window.
        private const float ColW_Num = 30f;        // "#" row-index column (per section)
        // Route tables (Model 1): # | Route (expands; name + grey from/to line) | Delivers |
        // Every | Runs (Advanced) | Next | Status | Interact. Origin and Destination fold
        // into the Route cell's second line.
        private const float ColW_Delivers = 200f;
        // The Every column holds the Advanced inline "[-] field [+] Nx" stepper or Basic's
        // read-only "every 4.0d"; one width so a mode switch never moves a column.
        private const float ColW_Interval = 150f;
        private const float ColW_Runs = 80f;       // Advanced only: "3" / "3, 1 held"
        private const float ColW_NextDelivery = 135f; // the amber "T- 1y 291d" countdown (+ " (!)")
        private const float ColW_Status = 260f;    // merged Status: one word + a short reason, wraps
        // Uniform width for the detail block's button row (Link round-trip... / Unlink,
        // Rename, Delete) so they read as one group; sized for the widest label.
        private const float RouteDetailButtonWidth = 104f;
        // The candidates table keeps its own columns (L3): # / Name / Origin / Destination /
        // Would deliver / Transit / Actions. Its Actions cell is its OWN constant so the
        // route tables' Interact column can follow the Missions width without squeezing
        // Create Route + Dismiss.
        private const float ColW_Origin = 95f;      // candidates: "KSC (funds)" / "depot pid=N"
        private const float ColW_Destination = 180f; // candidates: "Kerbin (surface)"
        private const float ColW_CandidateActions = 190f;
        // L3: the Candidates section has its own purpose-built header. The Would-deliver
        // cell holds the per-run delivery manifest text, which can be long, so it gets a
        // wide cell. The candidates bubble is a separate box and does not have to match
        // the route bubble width, so this column does not push MinWindowWidth.
        private const float ColW_WouldDeliver = 260f;
        // L3: the Candidates Transit cell shows the candidate's natural run duration.
        private const float ColW_CandidateTransit = 80f;

        // Bottom "hovered control help text" strip. See TooltipEchoBox for why it is a
        // permanently visible box of constant height. Single-line: the widest window in
        // the mod (1556px) fits one line per help text after the copy trims that came
        // with this change (pinned by TooltipEchoBudgetTests); overflow marquee-scrolls.
        private readonly TooltipEchoBox tooltipEcho =
            new TooltipEchoBox(SpacingSmall, TooltipEchoBox.SingleLine);

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 8f;
        // The route tables' fixed columns total 891 px in Basic (Num 30 + Delivers 200 +
        // Every 150 + Next 135 + Status 260 + Interact 116) and 971 px in Advanced (+ Runs
        // 80), so at this floor the expanding Route column keeps over 400 px in either
        // mode. One floor for both modes: a mode switch never resizes the window. The
        // candidates table (805 px fixed) fits too.
        internal const float MinWindowWidth = 1410f;

        /// <summary>
        /// First-open window width (px), which also sizes the single-line hover strip's
        /// text budget (<see cref="TooltipEchoBox.BudgetChars"/>).
        /// </summary>
        internal const float DefaultWindowWidth = 1556f;

        internal const float MinWindowHeight = 500f;

        public bool IsOpen
        {
            get { return showWindow; }
            // M4c: closing the window must also dismiss the round-trip link picker
            // (it draws as its own window from DrawIfOpen, which is skipped while the
            // host is closed). Without this, re-opening the window the same scene
            // re-pops a picker armed with stale source state — the leak the mirrored
            // GroupPickerUI idiom avoids via its host's Close() calls.
            set { showWindow = value; if (!value) linkPickerOpen = false; }
        }

        internal LogisticsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
            // Restore the rect saved before the last scene change. If the window has
            // never been drawn this session, sessionWindowRect is default-zero
            // (width < 1) so the first-open default-init in DrawIfOpen still runs.
            windowRect = sessionWindowRect;
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (windowRect.width < 1f)
            {
                // First-open default. MUST be >= MinWindowWidth/Height: HandleResizeDrag only
                // enforces the minimum during a resize drag, so a smaller default would open
                // the window below its own minimum and snap on first touch (the old 1360
                // default predated the 1410 minimum bump and did exactly that). 1556 is the
                // playtest-preferred width (2026-06-10 log: last resize ended w=1556 h=500),
                // which also fits the widened Next column.
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                windowRect = new Rect(x, mainWindowRect.y, DefaultWindowWidth, 500);
                ParsekLog.Verbose("UI",
                    $"Logistics window initial position: x={windowRect.x.ToString("F0", CultureInfo.InvariantCulture)} y={windowRect.y.ToString("F0", CultureInfo.InvariantCulture)}");
            }

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizing,
                MinWindowWidth, MinWindowHeight, "Logistics window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    WindowIdKey.GetHashCode(),
                    windowRect,
                    DrawWindow,
                    "Parsek - Logistics",
                    opaqueWindowStyle,
                    GUILayout.Width(windowRect.width),
                    GUILayout.Height(windowRect.height));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition("Logistics", ref lastWindowRect, windowRect);
            // Capture the (possibly moved or resized) rect so it survives the next
            // scene change and is restored by the constructor of the next instance.
            sessionWindowRect = windowRect;

            // M4c: the round-trip link picker draws as its own window on top of the
            // logistics window (the GroupPickerUI idiom — drawn after the host window).
            // Armed from the detail panel; commits synchronously on Link.
            DrawLinkPicker();

            if (windowRect.Contains(Event.current.mousePosition))
            {
                if (!windowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, InputLockId);
                    windowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        public void ReleaseInputLock()
        {
            if (!windowHasInputLock) return;
            InputLockManager.RemoveControlLock(InputLockId);
            windowHasInputLock = false;
        }

        private void DrawWindow(int windowID)
        {
            EnsureStyles();
            GUILayout.Space(5);
            wideScroll.Latch(windowRect.width);

            // ONE read of the tuning gate per pass: the header, every row and every
            // detail block below draw from this latched bool (Layout and Repaint agree).
            drawTuning = LogisticsRoutePresentation.ShowsRouteTuning(ParsekUI.AppliedUiComplexityMode);
            if (!drawTuning)
                DropTuningStateForBasic();

            double currentUT = TryGetCurrentUT();
            IReadOnlyList<Route> routes = RouteStore.CommittedRoutes;
            List<RouteCandidate> candidates = GetCandidates();
            // M3: committed-but-not-yet-eligible trees, throttled on the same ~1 Hz
            // timer as the candidates above (never derived on the draw path).
            List<RouteNearMiss> nearMisses = GetNearMisses();

            // Click outside an active inline edit field (M1 interval / M2 rename) ->
            // commit it. Runs BEFORE the rows draw (matching the RecordingsTableUI
            // defocus ordering) so the commit lands this frame; the rect was captured
            // on the previous frame's draw. Both commits run their action directly
            // (no frame-reset pending field), so an async click cannot be clobbered.
            HandleLogisticsDefocus(routes);

            // Throttled per-route legibility recompute (H1/H2/H3). Runs at most once
            // per ~1s over the committed routes, NOT per row per IMGUI frame; the
            // row/detail draw paths only read the cache afterward.
            RefreshLegibilityCacheIfDue(routes, currentUT);

            // Split stored routes by enablement. The Paused table holds every Paused
            // route plus a route armed by Send making its one run (it is a paused route
            // doing a single run, so it never jumps sections); Active holds everything
            // else (running, held, pause-armed and hard-broken).
            var activeRoutes = new List<Route>();
            var pausedRoutes = new List<Route>();
            int routeCount = routes?.Count ?? 0;
            for (int i = 0; i < routeCount; i++)
            {
                Route r = routes[i];
                if (r == null) continue;
                if (LogisticsRoutePresentation.BelongsInPausedTable(r.Status, IsSendingOnce(r)))
                    pausedRoutes.Add(r);
                else
                    activeRoutes.Add(r);
            }

            // Reset deferred actions for this frame.
            pendingPause = null;
            pendingActivate = null;
            pendingSendOnce = null;
            pendingConfirmDeleteRoute = null;
            pendingConfirmDeleteDormantRoute = null;
            pendingCreate = null;
            pendingCadenceRoute = null;
            pendingCadenceMultiplier = 0;
            pendingPriorityRoute = null;
            pendingPriorityValue = 0;
            pendingDismissTreeId = null;
            pendingDismissLabel = null;
            pendingRestoreTreeId = null;
            pendingRestoreLabel = null;

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
            wideScroll.BeginContentFloor(ContentFloorReservedWidth());

            // Each section is its own gray bubble with its own header row, so the
            // header and data columns share the box and line up exactly. Titles are the
            // plain section name (no count), centered (see DrawSectionHeader).
            DrawRouteSectionBubble("Active Routes", "No active routes.", activeRoutes, RouteSection.Active, currentUT);
            DrawRouteSectionBubble("Paused Routes", "No paused routes.", pausedRoutes, RouteSection.Paused, currentUT);
            // Rewind-visibility follow-up: the collapsed "Dormant Routes (N)"
            // disclosure. Renders nothing when no route is dormant (the common
            // no-rewind save shows no extra chrome).
            DrawDormantSectionBubble();
            DrawCandidateSectionBubble("Candidates", candidates, nearMisses);

            wideScroll.EndContentFloor();
            GUILayout.EndScrollView();

            // Tooltip echo strip (shared house helper). Fixed single-line height, always
            // present, and drawn AFTER every cell / button so the live GUI.tooltip read
            // inside Draw() sees the hovered control's text - but directly ABOVE the
            // Close button, which is the house ordering: Close is always last.
            tooltipEcho.Draw();

            // Full-width Close button at the bottom (matches Kerbals / Settings windows).
            GUILayout.Space(SpacingSmall);
            if (GUILayout.Button("Close"))
            {
                showWindow = false;
                linkPickerOpen = false; // M4c: dismiss the link picker with the window.
                ParsekLog.Verbose("UI", "Logistics window closed");
            }

            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Logistics window");
            GUI.DragWindow();

            // Apply deferred mutations now that the draw loop is done.
            ApplyPendingActions(currentUT);
        }

        /// <summary>
        /// What the window spends outside its section stack at its natural width: the
        /// window's own horizontal padding, the scroll view's, and the vertical scrollbar.
        /// The rest of <see cref="MinWindowWidth"/> is the content floor a capped window
        /// keeps, so its columns scroll instead of squeezing the Name column.
        /// </summary>
        private float ContentFloorReservedWidth()
        {
            GUIStyle windowStyle = parentUI.GetOpaqueWindowStyle();
            float reserved = ParsekUI.VerticalScrollbarFootprintWidth();
            if (windowStyle != null) reserved += windowStyle.padding.horizontal;
            if (GUI.skin != null && GUI.skin.scrollView != null)
                reserved += GUI.skin.scrollView.padding.horizontal;
            return reserved;
        }

        /// <summary>
        /// Commits an active inline edit (M1 interval field / M2 rename field) when
        /// the player clicks OUTSIDE its field rect, mirroring
        /// <c>RecordingsTableUI.HandleRecordingsDefocus</c> and the SettingsWindowUI
        /// click-outside-commit. Only acts on a MouseDown whose position is outside
        /// the captured field rect (a non-zero-width rect from the previous frame's
        /// draw). The route is resolved by the stored edit id from
        /// <paramref name="routes"/>; both commit helpers run their action directly,
        /// so a resolved-null id just clears the edit state.
        /// </summary>
        private void HandleLogisticsDefocus(IReadOnlyList<Route> routes)
        {
            if (Event.current.type != EventType.MouseDown)
                return;

            Vector2 mouse = Event.current.mousePosition;

            if (renamingRouteId != null && renamingRouteRect.width > 0
                && !renamingRouteRect.Contains(mouse))
            {
                CommitRouteRename(FindRouteById(routes, renamingRouteId));
            }
            else if (intervalEditRouteId != null && intervalEditRect.width > 0
                && !intervalEditRect.Contains(mouse))
            {
                Route route = FindRouteById(routes, intervalEditRouteId);
                if (route != null)
                    CommitIntervalEdit(route);
                else
                    ClearIntervalEdit();
            }
        }

        private static Route FindRouteById(IReadOnlyList<Route> routes, string id)
        {
            if (routes == null || string.IsNullOrEmpty(id))
                return null;
            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r != null && string.Equals(r.Id, id, System.StringComparison.Ordinal))
                    return r;
            }
            return null;
        }

        /// <summary>
        /// Drops the state of a tuning control that is not drawn in Basic, on the first
        /// Basic pass after a switch: an in-progress Every edit (its field is gone, so
        /// its click-outside commit would fire against a control that is no longer
        /// drawn) and the round-trip link picker (its opener is Advanced-only; the Basic
        /// close set closes it too, this is the belt-and-braces for a picker opened by
        /// the automation seam). Nothing is committed: a half-typed interval is
        /// discarded, exactly like Escape.
        /// </summary>
        private void DropTuningStateForBasic()
        {
            if (intervalEditRouteId != null)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics: interval edit dropped for Basic route={ShortId(intervalEditRouteId)}");
                ClearIntervalEdit();
            }
            CloseLinkPickerForModeChange();
        }

        /// <summary>
        /// True when a route is armed by Send to make one run (and has not landed back in
        /// Paused yet): the Paused-table membership, the "Sending one run" Status word and
        /// the greyed "Sending..." button all read this one predicate. The provenance is
        /// the persisted <see cref="Route.SendOnceArmed"/> flag, plus this session's set
        /// for the frame between the click and the next refresh.
        /// </summary>
        private bool IsSendingOnce(Route route)
        {
            if (route == null || !ShouldShowSendingButton(route))
                return false;
            bool sendOnceArmed = !string.IsNullOrEmpty(route.Id)
                && (sendOnceArmedRouteIds.Contains(route.Id) || route.SendOnceArmed);
            return ResolveArmedKind(sendOnceArmed, route.Status) == ArmedSendKind.SendOnce;
        }

        /// <summary>True when a route is armed by Pause while a run is in flight.</summary>
        private bool IsPausingAfterRun(Route route)
        {
            return route != null && ShouldShowSendingButton(route) && !IsSendingOnce(route);
        }

        private void DrawRouteSectionBubble(string title, string emptyText, List<Route> rows,
            RouteSection section, double currentUT)
        {
            DrawSectionHeader(title);
            GUILayout.BeginVertical(GUI.skin.box);
            // L2: clickable sort headers (cached re-sort). The Active and Paused tables
            // share the sort column / direction; clicking a header re-sorts both.
            DrawRouteSortableHeader();
            if (rows.Count == 0)
                GUILayout.Label("  " + emptyText, statusStyleGrey);
            else
            {
                // Draw the cached-sorted rows (re-sorted only on count / sort-state /
                // legibility-stamp change, never per frame). Row index is the display
                // position in the sorted list.
                List<Route> sorted = GetSortedRoutesForSection(rows, section);
                for (int i = 0; i < sorted.Count; i++)
                    DrawRouteRow(sorted[i], section, i + 1, currentUT);
            }
            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        // Dormant-routes section key (collapsible via the shared expandedRows set,
        // mirroring NearMissSectionKey / DismissedSectionKey). Absent from the set
        // by default, so the disclosure starts COLLAPSED.
        private const string DormantSectionKey = "dormant:section";

        // Cached disclosure-header content, rebuilt only when the dormant count
        // or expanded state changes (the header's only inputs), so the
        // per-IMGUI-frame draw does no string interpolation while the section
        // shows.
        private GUIContent cachedDormantHeader;
        private int cachedDormantHeaderCount = -1;
        private bool cachedDormantHeaderExpanded;
        private const string DormantSectionTooltip =
            "Routes created after the rewind point you rewound past. Each is dormant (not running, not visible elsewhere) and reappears Paused when the game reaches its creation date again. Delete removes one for good.";

        /// <summary>
        /// Draws the collapsed-by-default "Dormant Routes (N)" disclosure bubble
        /// (rewind-visibility follow-up). Shown ONLY when at least one route is
        /// dormant (<see cref="LogisticsDormantPresentation.ShouldShowDormantSection"/>);
        /// per route: display name, "appears at &lt;date&gt;" (the route's
        /// <see cref="Route.CreatedUT"/> through the house
        /// <c>KSPUtil.PrintDateCompact</c> formatter, guarded off-Unity), and a
        /// Delete button deferred through
        /// <see cref="pendingConfirmDeleteDormantRoute"/> into the same
        /// confirm-dialog flow as committed-route deletion. Read-only otherwise:
        /// a dormant route cannot be activated, edited, or expanded - it does
        /// not exist on the timeline yet. Reads <see cref="RouteStore.DormantRoutes"/>
        /// directly (a plain list read, no derivation, so it is draw-path safe).
        /// </summary>
        private void DrawDormantSectionBubble()
        {
            IReadOnlyList<Route> dormant = RouteStore.DormantRoutes;
            if (!LogisticsDormantPresentation.ShouldShowDormantSection(dormant?.Count ?? 0))
                return;

            GUILayout.BeginVertical(GUI.skin.box);
            bool expanded = expandedRows.Contains(DormantSectionKey);
            if (cachedDormantHeaderCount != dormant.Count
                || cachedDormantHeaderExpanded != expanded
                || cachedDormantHeader == null)
            {
                string arrow = expanded ? "▼" : "▶";
                cachedDormantHeader = new GUIContent(
                    $"{arrow} {LogisticsDormantPresentation.DormantSectionTitle(dormant.Count)}",
                    DormantSectionTooltip);
                cachedDormantHeaderCount = dormant.Count;
                cachedDormantHeaderExpanded = expanded;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button(cachedDormantHeader, GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(DormantSectionKey, "dormant section");
            GUILayout.EndHorizontal();

            if (expanded)
            {
                for (int i = 0; i < dormant.Count; i++)
                {
                    Route route = dormant[i];
                    if (route == null) continue;
                    string display = LogisticsDormantPresentation.DormantRouteDisplayName(route.Name, route.Id);
                    string appears = LogisticsDormantPresentation.DormantAppearsLabel(
                        route.CreatedUT, SafePrintDateCompact(route.CreatedUT));
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(24f);
                    GUILayout.Label($"{display} - {appears}", detailStyle, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button(new GUIContent("Delete",
                            "Delete this dormant route. It will never re-materialize."),
                            GUILayout.Width(60)))
                        pendingConfirmDeleteDormantRoute = route;
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        /// <summary>
        /// House date formatting (<c>KSPUtil.PrintDateCompact</c>, the same
        /// formatter the Recordings / Missions / Timeline windows use) guarded
        /// for off-Unity contexts: returns null when the formatter is
        /// unavailable so <see cref="LogisticsDormantPresentation.DormantAppearsLabel"/>
        /// renders its honest "&lt;unknown&gt;" fallback.
        /// </summary>
        private static string SafePrintDateCompact(double ut)
        {
            if (ut < 0.0) return null;
            try { return KSPUtil.PrintDateCompact(ut, true); }
            catch { return null; }
        }

        private void DrawCandidateSectionBubble(string title, List<RouteCandidate> rows, List<RouteNearMiss> nearMisses)
        {
            DrawSectionHeader(title);
            GUILayout.BeginVertical(GUI.skin.box);
            DrawCandidateColumnHeader();
            if (rows.Count == 0)
                GUILayout.Label("  No supply runs to offer yet. Fly a cargo run that docks, transfers cargo and undocks, then finish the mission.", statusStyleGrey);
            for (int i = 0; i < rows.Count; i++)
                DrawCandidateRow(rows[i], i + 1);

            // M3: a collapsible subsection that surfaces WHY a recently committed tree
            // is NOT a candidate (not fully sealed, or sealed-but-ineligible), so the
            // player can tell "I have not committed/sealed it yet" apart from "the run
            // does not match the dock-deliver-undock proof". Reads only the cached list.
            DrawNearMissSubsection(nearMisses);

            // M6 candidate intent helper: the collapsed "Dismissed (N)" subsection
            // listing player-dismissed trees, each with a Restore control. Reads
            // only the cached rows (rebuilt on the ~1 Hz candidate refresh).
            DrawDismissedSubsection();

            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        // M3 near-miss subsection key (collapsible via the shared expandedRows set).
        private const string NearMissSectionKey = "nearmiss:section";

        /// <summary>
        /// Draws the M3 "Recently committed trees not yet eligible" collapsible
        /// subsection at the bottom of the Candidates bubble. A caret header toggled
        /// through the shared <see cref="expandedRows"/> / <see cref="ToggleExpanded"/>
        /// idiom (fixed key <see cref="NearMissSectionKey"/>); when expanded, one line
        /// per <see cref="RouteNearMiss"/> showing the tree display name and its
        /// blocking reason from the pure
        /// <see cref="LogisticsRejectPresentation.DescribeNearMiss"/>. Nothing renders
        /// when the cached near-miss list is empty. All reads are against the cached
        /// list (built on the ~1 Hz timer in <see cref="GetNearMisses"/>), never live
        /// on the IMGUI draw path.
        /// </summary>
        private void DrawNearMissSubsection(List<RouteNearMiss> nearMisses)
        {
            if (nearMisses == null || nearMisses.Count == 0)
                return;

            bool expanded = expandedRows.Contains(NearMissSectionKey);
            string arrow = expanded ? "\u25bc" : "\u25b6";
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button(
                    new GUIContent(
                        $"{arrow} Missions that cannot become routes yet ({nearMisses.Count.ToString(CultureInfo.InvariantCulture)})",
                        "Finished missions that are not supply runs yet, with the reason: still open to re-flying, or the flight does not dock, transfer cargo and undock."),
                    GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(NearMissSectionKey, "near-miss subsection");
            GUILayout.EndHorizontal();

            if (!expanded)
                return;

            for (int i = 0; i < nearMisses.Count; i++)
            {
                RouteNearMiss nm = nearMisses[i];
                if (nm == null) continue;
                string treeName = NearMissTreeLabel(nm.Tree);
                string reason = LogisticsRejectPresentation.DescribeNearMiss(
                    nm.Status, nm.NotSealed, nm.ReflyableCount, nm.RejectDetail);
                // M6 candidate intent helper: near-miss rows get the same
                // Dismiss control as candidate rows (a dismissed tree leaves
                // the WHOLE Candidates section). Row layout mirrors DetailLine
                // (24px indent + detailStyle label) with the button appended;
                // the button only draws for a resolvable tree id, a per-row
                // condition that is stable within a frame (cached list), so the
                // IMGUI control count stays consistent across Layout/Repaint.
                GUILayout.BeginHorizontal();
                GUILayout.Space(24f);
                GUILayout.Label($"{treeName} - {reason}", detailStyle, GUILayout.ExpandWidth(true));
                string nmTreeId = nm.Tree?.Id;
                if (!string.IsNullOrEmpty(nmTreeId)
                    && GUILayout.Button(new GUIContent("Dismiss",
                        "Hide this mission from the Candidates section (it is not meant to become a route). Restore it any time from the Hidden missions list below."),
                        GUILayout.Width(70)))
                {
                    pendingDismissTreeId = nmTreeId;
                    pendingDismissLabel = treeName;
                }
                GUILayout.EndHorizontal();
            }
        }

        // M6 dismissed-candidates subsection key (collapsible via the shared
        // expandedRows set, mirroring NearMissSectionKey).
        private const string DismissedSectionKey = "dismissed:section";

        /// <summary>
        /// Draws the M6 "Dismissed (N)" collapsible subsection at the bottom of
        /// the Candidates bubble: the trees the player dismissed as route
        /// candidates, each with a Restore control. Mirrors the near-miss
        /// subsection idiom exactly (caret header via the shared
        /// <see cref="expandedRows"/> / <see cref="ToggleExpanded"/>, fixed key
        /// <see cref="DismissedSectionKey"/>); nothing renders when nothing is
        /// dismissed. Reads only <see cref="cachedDismissedRows"/> (labels
        /// resolved on the ~1 Hz candidate-cache refresh, never live on the
        /// IMGUI draw path); Restore defers through
        /// <see cref="pendingRestoreTreeId"/> to <see cref="ApplyPendingActions"/>.
        /// </summary>
        private void DrawDismissedSubsection()
        {
            if (cachedDismissedRows.Count == 0)
                return;

            bool expanded = expandedRows.Contains(DismissedSectionKey);
            string arrow = expanded ? "▼" : "▶";
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button(
                    new GUIContent(
                        $"{arrow} Hidden missions ({cachedDismissedRows.Count.ToString(CultureInfo.InvariantCulture)})",
                        "Missions you hid from this list. Restore shows one again."),
                    GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(DismissedSectionKey, "dismissed subsection");
            GUILayout.EndHorizontal();

            if (!expanded)
                return;

            for (int i = 0; i < cachedDismissedRows.Count; i++)
            {
                (string treeId, string label) = cachedDismissedRows[i];
                GUILayout.BeginHorizontal();
                GUILayout.Space(24f);
                GUILayout.Label(label, detailStyle, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent("Restore",
                        "Offer this mission as a supply run candidate again."),
                        GUILayout.Width(70)))
                {
                    pendingRestoreTreeId = treeId;
                    pendingRestoreLabel = label;
                }
                GUILayout.EndHorizontal();
            }
        }

        // Display label for a near-miss tree: the player-visible TreeName, falling
        // back to the short tree id and then "<unnamed>" so a row is never blank.
        private static string NearMissTreeLabel(RecordingTree tree)
        {
            if (tree == null)
                return "<unnamed>";
            if (!string.IsNullOrEmpty(tree.TreeName))
                return tree.TreeName;
            if (!string.IsNullOrEmpty(tree.Id))
                return ShortId(tree.Id);
            return "<unnamed>";
        }

        // L3: purpose-built static header for the Candidates section. A candidate is a
        // sealed-but-not-yet-promoted Supply Run, so the route-only columns (Interval /
        // Cycle / Next / Status / Delivery) do not apply and used to render literal "-" /
        // "eligible" placeholders. This header carries only the columns that mean
        // something for a candidate: # / Name / Origin / Destination / Would deliver /
        // Transit / Actions. The "eligible" / sealed explanation that used to live in
        // the dropped Status cell is relocated to the section-header "Would deliver"
        // tooltip so the copy is not lost. The Candidates section is now INDEPENDENT of
        // the route DrawRouteSortableHeader / DrawRouteRow pair; DrawCandidateRow must add /
        // drop the SAME cells in the SAME order as this header to stay column-aligned.
        private void DrawCandidateColumnHeader()
        {
            GUIStyle h = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            GUILayout.Label("#", h, GUILayout.Width(ColW_Num));
            GUILayout.Label("Name", h, GUILayout.ExpandWidth(true));
            GUILayout.Label("Origin", h, GUILayout.Width(ColW_Origin));
            GUILayout.Label("Destination", h, GUILayout.Width(ColW_Destination));
            GUILayout.Label(
                new GUIContent("Would deliver",
                    "Each candidate is a finished supply run: the resources and stored parts it would deliver each run. Create Route makes it a Paused route you can Send or Activate."),
                h, GUILayout.Width(ColW_WouldDeliver));
            GUILayout.Label("Transit", h, GUILayout.Width(ColW_CandidateTransit));
            GUILayout.Label("Actions", h, GUILayout.Width(ColW_CandidateActions));
            GUILayout.EndHorizontal();
        }

        // L2: the clickable sort header for the Active / Paused route tables. Each
        // sortable column routes through parentUI.DrawSortableHeaderCore (the shared
        // generic header used by SpawnControlUI / RecordingsTableUI / Missions), toggling
        // the shared routeSortColumn / routeSortAscending and invalidating the cached-sorted
        // lists on change. The "#" index, Delivers and Interact are not sortable. Cell
        // order and widths match DrawRouteRow exactly, and both read the same latched
        // drawTuning, so the Advanced-only Runs column is added or dropped in both.
        private void DrawRouteSortableHeader()
        {
            GUIStyle h = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            GUILayout.Label("#", h, GUILayout.Width(ColW_Num));
            DrawRouteSortColumn("Route", LogisticsRouteSortColumn.Name, 0f, true,
                LogisticsRoutePresentation.RouteHeaderTooltip);
            GUILayout.Label(new GUIContent("Delivers",
                    "What each run delivers. The detail below the row names the destination."),
                h, GUILayout.Width(ColW_Delivers));
            DrawRouteSortColumn("Every", LogisticsRouteSortColumn.Interval, ColW_Interval, false,
                LogisticsRoutePresentation.EveryHeaderTooltip);
            if (drawTuning)
                DrawRouteSortColumn("Runs", LogisticsRouteSortColumn.Cycles, ColW_Runs, false,
                    LogisticsRoutePresentation.RunsTooltip);
            DrawRouteSortColumn("Next", LogisticsRouteSortColumn.NextDelivery, ColW_NextDelivery, false,
                LogisticsRoutePresentation.NextHeaderTooltip);
            DrawRouteSortColumn("Status", LogisticsRouteSortColumn.Status, ColW_Status, false,
                LogisticsRoutePresentation.StatusHeaderTooltip);
            // Interact: a centred label in a zero-margin dark box exactly ColW_Interact wide,
            // like each row's zero-margin Interact cell below it (the Missions header shape).
            GUILayout.BeginHorizontal(interactHeaderContainerStyle,
                GUILayout.Width(MissionsWindowUI.ColW_Interact));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Interact", interactHeaderLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndHorizontal();
        }

        // Thin per-window wrapper around the shared sortable-header helper (mirrors
        // SpawnControlUI.DrawSpawnSortableHeader). The arrow follows the EFFECTIVE sort
        // column (a Runs sort reads as a Route sort in Basic, where Runs has no header), and
        // a click writes the new column back into the stored routeSortColumn. The onChanged
        // callback logs the decision (once, on the click) and dirties the cached sort tuple
        // so both sections re-sort on the next draw.
        private void DrawRouteSortColumn(string label, LogisticsRouteSortColumn col, float width, bool expand,
            string tooltip)
        {
            LogisticsRouteSortColumn shown =
                LogisticsRoutePresentation.EffectiveSortColumn(routeSortColumn, drawTuning);
            parentUI.DrawSortableHeaderCore(
                label, col, ref shown, ref routeSortAscending, width, expand,
                () =>
                {
                    routeSortColumn = shown;
                    // Force a re-sort next draw: clearing the cached counts guarantees
                    // both section caches miss even if the row count is unchanged.
                    cachedActiveCount = -1;
                    cachedPausedCount = -1;
                    ParsekLog.Verbose("UI",
                        $"Logistics route sort changed column={routeSortColumn} ascending={routeSortAscending}");
                },
                tooltip: tooltip);
        }

        private enum RouteSection { Active, Paused }

        private void DrawRouteRow(Route route, RouteSection section, int rowNum, double currentUT)
        {
            if (route == null) return;
            string rowKey = route.Id ?? "<no-id>";
            bool expanded = expandedRows.Contains(rowKey);

            // Read the throttled per-route legibility values once for this row; the draw
            // path never recomputes them.
            RouteLegibility leg = GetLegibility(route);
            bool sendingOnce = IsSendingOnce(route);
            bool pausingAfterRun = IsPausingAfterRun(route);

            GUILayout.BeginHorizontal();

            GUILayout.Label(rowNum.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Num));

            // Route: line 1 the caret + name (click to expand), line 2 the grey
            // "KSC -> Depot Station Duna I" at the same font size; the hover carries the
            // full origin and destination including coordinates.
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            string arrow = expanded ? "▼" : "▶";
            if (GUILayout.Button($"{arrow} {route.Name ?? "<unnamed>"}", routeNameStyle, GUILayout.ExpandWidth(true)))
                ToggleExpanded(rowKey, route.Name);
            GUILayout.Label(new GUIContent("   " + (leg.FromToText ?? "-"), leg.FromToTooltip ?? string.Empty),
                routeFromToStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndVertical();

            GUILayout.Label(
                new GUIContent(leg.DeliversCellText ?? "-", leg.DeliversLine ?? string.Empty),
                wrapCellStyle, GUILayout.Width(ColW_Delivers));

            // Every: the Advanced inline stepper, or Basic's read-only "every 4.0d". A route
            // armed by Send shows the read-only form in both modes: its one run ignores the
            // schedule, and an edit in progress on it is dropped (its field is gone).
            if (drawTuning && !sendingOnce)
                DrawIntervalCell(route, leg);
            else
            {
                if (sendingOnce && string.Equals(intervalEditRouteId, route.Id, System.StringComparison.Ordinal))
                    ClearIntervalEdit();
                GUILayout.Label(new GUIContent(leg.EveryText ?? "-", leg.EveryTooltip ?? string.Empty),
                    GUILayout.Width(ColW_Interval));
            }

            if (drawTuning)
                GUILayout.Label(
                    new GUIContent(LogisticsRoutePresentation.FormatRunsCell(route.CompletedCycles, route.SkippedCycles),
                        LogisticsRoutePresentation.RunsTooltip),
                    GUILayout.Width(ColW_Runs));

            // Next: the Missions countdown exactly (amber "T- " + two units, " (!)" when
            // warned); "-" in grey when no run is scheduled. The exact date is the hover.
            bool hasCountdown = leg.NextCellText != null && leg.NextCellText != "-";
            GUILayout.Label(new GUIContent(leg.NextCellText ?? "-", leg.NextTooltip ?? string.Empty),
                hasCountdown ? nextAmberStyle : statusStyleGrey, GUILayout.Width(ColW_NextDelivery));

            // Status: ONE colour-coded word + a short reason (merges the old Status and
            // Delivery columns); the hover is the full dated sentence.
            GUILayout.Label(new GUIContent(leg.Status.Text ?? "-", leg.StatusTooltip ?? string.Empty),
                WrapStatusStyleFor(leg.Status.Color), GUILayout.Width(ColW_Status));

            DrawRouteInteractCell(route, section, sendingOnce, pausingAfterRun);

            GUILayout.EndHorizontal();

            if (expanded)
                DrawRouteDetail(route, currentUT);
        }

        /// <summary>
        /// The Interact column (Missions Model 1 shape: a 100 px single on line 1, two 48 px
        /// halves of one single on line 2, 8 px inset). Line 1: Activate (Paused table) /
        /// Pause (Active table), or the greyed Sending... / Pausing... of an armed route.
        /// Line 2: Send (one run, then stay Paused; live only on an unarmed Paused row) and
        /// Log (the Mission Log of the mission the route was built from). Every greyed button
        /// carries its reason to the hover strip (DisabledHoverEcho). Deliberate difference
        /// from Missions: Log is a pair half here, because Logistics has no name-cell grid.
        /// </summary>
        private void DrawRouteInteractCell(Route route, RouteSection section, bool sendingOnce, bool pausingAfterRun)
        {
            bool armed = sendingOnce || pausingAfterRun;
            GUILayout.BeginVertical(GUILayout.Width(MissionsWindowUI.ColW_Interact));

            GUILayout.BeginHorizontal();
            GUILayout.Space(MissionsWindowUI.InteractCellInset);
            if (armed)
            {
                ArmedSendKind kind = sendingOnce ? ArmedSendKind.SendOnce : ArmedSendKind.PauseAfterCycle;
                string reason = TooltipForArmedState(kind);
                bool prevEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Button(new GUIContent(LabelForArmedState(kind), reason),
                    interactButtonStyle, GUILayout.Width(MissionsWindowUI.InteractButtonWidth));
                DisabledHoverEcho.CarryLastControl(false, reason);
                GUI.enabled = prevEnabled;
            }
            else if (section == RouteSection.Active)
            {
                if (GUILayout.Button(new GUIContent("Pause",
                        "Stop running this route on its schedule. A run already in flight finishes first."),
                        interactButtonStyle, GUILayout.Width(MissionsWindowUI.InteractButtonWidth)))
                    pendingPause = route;
            }
            else
            {
                if (GUILayout.Button(new GUIContent("Activate",
                        "Run this route on its schedule."),
                        interactButtonStyle, GUILayout.Width(MissionsWindowUI.InteractButtonWidth)))
                    pendingActivate = route;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Space(MissionsWindowUI.InteractCellInset);
            string sendReason = LogisticsRoutePresentation.SendDisabledReason(
                section == RouteSection.Active, armed);
            bool sendEnabled = string.IsNullOrEmpty(sendReason);
            bool prev = GUI.enabled;
            GUI.enabled = sendEnabled;
            bool sendClicked = GUILayout.Button(new GUIContent("Send",
                    sendEnabled
                        ? "Make one run at the next moment conditions allow (funds, cargo, destination, launch window), then stay Paused."
                        : sendReason),
                interactPairButtonStyle, GUILayout.Width(MissionsWindowUI.InteractPairButtonWidth));
            DisabledHoverEcho.CarryLastControl(sendEnabled, sendReason);
            GUI.enabled = prev;
            if (sendClicked && sendEnabled)
                pendingSendOnce = route;
            GUILayout.Space(MissionsWindowUI.InteractButtonGap);
            DrawRouteLogButton(route);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// The route's Log: the Log of the mission the route was built from (the same window
        /// the Missions-tab Log opens). Greyed with its reason for a route not built from a
        /// recorded mission.
        /// </summary>
        private void DrawRouteLogButton(Route route)
        {
            string sourceTreeId = ResolveRouteSourceTreeId(route);
            bool hasSourceMission = !string.IsNullOrEmpty(sourceTreeId);
            bool prevEnabled = GUI.enabled;
            GUI.enabled = hasSourceMission;
            bool missionLogClicked = GUILayout.Button(new GUIContent("Log",
                    "Step-by-step log of the mission this route was built from"),
                interactPairButtonStyle, GUILayout.Width(MissionsWindowUI.InteractPairButtonWidth));
            DisabledHoverEcho.CarryLastControl(
                hasSourceMission, MissionLogButtonDisabledReason(sourceTreeId));
            GUI.enabled = prevEnabled;
            if (missionLogClicked && hasSourceMission)
            {
                // The source tree's ORIGINAL mission: clones share the tree, and the
                // route was built from the flight the original stands for.
                Mission sourceMission = MissionStore.FindOriginalMission(sourceTreeId);
                ParsekLog.Info("UI",
                    $"Route Log button: route={(string.IsNullOrEmpty(route.Id) ? "<null>" : route.Id)} tree={sourceTreeId ?? "<null>"} " +
                    $"mission={sourceMission?.Id ?? "<none>"}");
                parentUI.OpenStructureWindowForMission(sourceTreeId, sourceMission?.Id,
                    !string.IsNullOrEmpty(sourceMission?.Name)
                        ? sourceMission.Name
                        : ResolveTreeDisplayName(sourceTreeId));
            }
        }

        /// <summary>
        /// Draws the M1 inline cadence control inside the Interval cell of a route
        /// row: a compact "[-] field [+]" stepper plus a small "Nx" multiplier label.
        /// The "-" / "+" buttons step the multiplier through the existing
        /// deferred-mutation fields (committed synchronously the same frame in
        /// <see cref="ApplyPendingActions"/>), so the +/- path is NOT the QW2
        /// frame-reset trap. The editable TextField holds a typed target interval in
        /// seconds using the SettingsWindowUI deferred-commit idiom
        /// (<see cref="GUI.SetNextControlName"/> keyed per route, edit-start on focus,
        /// commit on Enter / click-outside); its commit runs
        /// <see cref="RouteCadence.ParseAndSnapInterval"/> -> the snapped N ->
        /// <see cref="RouteCadence.ApplyMultiplier"/> DIRECTLY in
        /// <see cref="CommitIntervalEdit"/> (never via a frame-reset pending field).
        /// </summary>
        private void DrawIntervalCell(Route route, RouteLegibility leg)
        {
            string controlName = "LogiInterval_" + (route.Id ?? "<no-id>");
            bool editingThis = renamingRouteId == null
                && string.Equals(intervalEditRouteId, route.Id, System.StringComparison.Ordinal);
            int n = Route.ClampCadenceMultiplier(route.CadenceMultiplier);

            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Interval));

            // "-" decrements the multiplier (no-op + greyed at the 1x floor). Routes
            // through the synchronous deferred fields like the detail-panel stepper.
            bool atFloor = n <= 1;
            const string cadenceAtFloorReason =
                "Already at the minimum (1x = the fastest the run allows)";
            GUI.enabled = !atFloor;
            bool cadenceDownClicked = GUILayout.Button(new GUIContent("-",
                    atFloor ? cadenceAtFloorReason : "Run more often"),
                    GUILayout.Width(20f));
            DisabledHoverEcho.CarryLastControl(!atFloor, cadenceAtFloorReason);
            if (cadenceDownClicked)
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, -1);
            }
            GUI.enabled = true;

            // Editable target-interval field (seconds). Deferred commit: while not
            // editing this route we show the live interval value and arm editing when
            // the field gains focus; while editing we hold the typed text and commit
            // on Enter (focus-loss / click-outside is handled in HandleLogisticsDefocus).
            if (!editingThis)
            {
                string display = FormatIntervalFieldValue(route.DispatchInterval);
                GUI.SetNextControlName(controlName);
                string newText = GUILayout.TextField(display, GUILayout.Width(64f));
                if (GUI.GetNameOfFocusedControl() == controlName)
                {
                    intervalEditRouteId = route.Id;
                    intervalEditText = newText;
                    intervalEditFocused = true;
                    intervalEditRect = GUILayoutUtility.GetLastRect();
                    ParsekLog.Verbose("UI",
                        $"Logistics: interval edit started route={ShortId(route.Id)} value='{newText}'");
                }
            }
            else
            {
                bool submit = Event.current.type == EventType.KeyDown
                    && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                bool cancel = Event.current.type == EventType.KeyDown
                    && Event.current.keyCode == KeyCode.Escape;

                GUI.SetNextControlName(controlName);
                string newText = GUILayout.TextField(intervalEditText, GUILayout.Width(64f));
                intervalEditRect = GUILayoutUtility.GetLastRect();
                if (newText != intervalEditText)
                    intervalEditText = newText;

                if (!intervalEditFocused)
                {
                    GUI.FocusControl(controlName);
                    intervalEditFocused = true;
                }

                if (submit)
                {
                    CommitIntervalEdit(route);
                    Event.current.Use();
                }
                else if (cancel)
                {
                    ParsekLog.Verbose("UI",
                        $"Logistics: interval edit cancelled route={ShortId(route.Id)}");
                    ClearIntervalEdit();
                    Event.current.Use();
                }
            }

            if (GUILayout.Button(new GUIContent("+", "Run less often"), GUILayout.Width(20f)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, +1);
            }

            // Compact "Nx" multiplier readout (the human duration moves to the tooltip
            // so the cell stays narrow); hovering shows the cadence + the transit time
            // (which no longer has its own column after the L2 narrowing). M5 (D8):
            // for a WINDOWED basis the tooltip leads with the windowed wording
            // ("2x (every 2nd window)") - interval arithmetic is actively
            // misleading on synodic spacing - and a small basis label follows the
            // Nx readout. Flat rows draw the pre-M5 content byte-identically.
            bool windowed = RouteWindowBasisPresentation.IsWindowedBasis(leg.Basis);
            // Pure builder in LogisticsIntervalPresentation: the flat variant is the
            // window's longest runtime-composed tooltip, so its length is pinned by
            // TooltipEchoBudgetTests against the single-line strip budget.
            // Transit is formatted only for the flat branch - the windowed wording
            // never reads it, so a windowed row should not pay the string.Format.
            string nxTooltip = LogisticsIntervalPresentation.BuildNxCellTooltip(
                windowed, n, leg.BasisLabel ?? string.Empty,
                windowed ? null : FormatDuration(route.TransitDuration));
            GUILayout.Label(
                new GUIContent(
                    string.Format(CultureInfo.InvariantCulture, "{0}x", n),
                    nxTooltip),
                GUILayout.Width(28f));
            if (windowed && !string.IsNullOrEmpty(leg.BasisLabel))
                GUILayout.Label(leg.BasisLabel, GUILayout.ExpandWidth(false));

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Commits the M1 inline interval edit for <paramref name="route"/>: parses
        /// the typed target seconds and snaps it to a cadence multiplier via
        /// <see cref="RouteCadence.ParseAndSnapInterval"/>, then applies it through
        /// <see cref="RouteCadence.ApplyMultiplier"/> DIRECTLY here (not via a
        /// frame-reset pending field). A rejected parse (empty / garbage /
        /// non-positive) leaves the route unchanged and Warn-logs (mirrors
        /// <c>CommitAutoLoopEdit</c>'s reject branch). On a real change the legibility
        /// cache is dirtied (<c>lastLegibilityComputeRealtime = -1f</c>) so the
        /// route's cells refresh immediately rather than waiting out the ~1s timer.
        /// Always clears the edit state.
        /// </summary>
        private void CommitIntervalEdit(Route route)
        {
            string typed = intervalEditText;
            double span = route != null ? route.TransitDuration : 0.0;
            if (RouteCadence.ParseAndSnapInterval(typed, span, out int multiplier))
            {
                bool changed = RouteCadence.ApplyMultiplier(route, multiplier);
                ParsekLog.Info("UI",
                    $"Logistics: interval edit committed route={ShortId(route?.Id)} typed='{typed}' " +
                    $"N={multiplier.ToString(CultureInfo.InvariantCulture)} result={(changed ? "applied" : "unchanged")}");
                if (changed)
                    lastLegibilityComputeRealtime = -1f;
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Logistics: interval edit rejected route={ShortId(route?.Id)} typed='{typed}' " +
                    $"span={span.ToString("R", CultureInfo.InvariantCulture)} (route unchanged)");
            }
            ClearIntervalEdit();
        }

        private void ClearIntervalEdit()
        {
            intervalEditRouteId = null;
            intervalEditText = null;
            intervalEditFocused = false;
            intervalEditRect = default;
            GUIUtility.keyboardControl = 0;
        }

        /// <summary>
        /// Formats the editable interval field's display value: a friendly duration
        /// WITH a unit (e.g. "14.0m", "1.6d") via <see cref="FormatDuration"/>, so the
        /// player reads the cadence at a glance. It round-trips through the unit-aware
        /// <see cref="RouteCadence.ParseAndSnapInterval"/> (which accepts that same
        /// "Nm"/"Nh"/"Nd"/"Ns"/plain-number form). A non-positive / non-finite interval
        /// shows "0" so the field is always editable. Pure for unit testing.
        /// </summary>
        internal static string FormatIntervalFieldValue(double seconds)
        {
            if (seconds <= 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return "0";
            return FormatDuration(seconds);
        }

        /// <summary>
        /// True when a route's action cell should show the disabled "Sending..."
        /// affordance instead of a live action button. A route carrying
        /// <see cref="Route.PauseAfterCurrentCycle"/> has a one-shot / in-flight
        /// cycle committed (armed by Send Once, which un-pauses the route to
        /// Active, or by Pause-while-InTransit): it will dispatch one cycle at the
        /// next dispatch window, then return to Paused. While it is armed and has
        /// not yet landed back in Paused (and is not in a hard-broken
        /// endpoint/source state that cannot send), the player should see that the
        /// click registered and the route is waiting for its dispatch window
        /// rather than idle. Pure for unit testing.
        /// </summary>
        internal static bool ShouldShowSendingButton(Route route)
        {
            if (route == null || !route.PauseAfterCurrentCycle)
                return false;
            switch (route.Status)
            {
                case RouteStatus.Active:
                case RouteStatus.InTransit:
                case RouteStatus.WaitingForResources:
                case RouteStatus.WaitingForFunds:
                case RouteStatus.DestinationFull:
                    return true;
                default:
                    // Paused (the cycle landed / the route is idle) or a
                    // hard-broken endpoint/source state: not actively sending,
                    // so show the normal action buttons.
                    return false;
            }
        }

        /// <summary>
        /// Why the round-trip "Link" button is greyed out. The picker commits a PAIR, so
        /// it stays inert until a partner row is actually selected. Pure for unit testing.
        /// </summary>
        internal static string LinkButtonDisabledReason(string selectedPartnerRouteId)
        {
            return string.IsNullOrEmpty(selectedPartnerRouteId)
                ? "Pick a route above to pair this one with"
                : string.Empty;
        }

        /// <summary>
        /// Why the route's "Log" is greyed out. The button opens the Log of the mission a
        /// route was built from, so a route created by hand rather than from a flown mission
        /// has nothing to open. Pure for unit testing.
        /// </summary>
        internal static string MissionLogButtonDisabledReason(string sourceTreeId)
        {
            return string.IsNullOrEmpty(sourceTreeId)
                ? "This route was not built from a recorded mission"
                : string.Empty;
        }

        /// <summary>
        /// Which action armed a route's <see cref="Route.PauseAfterCurrentCycle"/>
        /// flag (M6). There is no separately tracked armedBy field; both arming
        /// paths set the same bool, so the armer is inferred from the route's status
        /// at the moment the disabled affordance is shown.
        /// </summary>
        internal enum ArmedSendKind
        {
            /// <summary>Armed by Send Once (Active / blocked-active dispatchable wait).</summary>
            SendOnce = 0,

            /// <summary>Armed by Pause requested while the cycle was InTransit.</summary>
            PauseAfterCycle = 1,
        }

        /// <summary>
        /// HEURISTIC fallback for which action armed
        /// <see cref="Route.PauseAfterCurrentCycle"/> when the send-once provenance is
        /// unknown (e.g. lost across save/reload). The two orchestrator paths leave
        /// disjoint statuses AT ARM TIME: <c>TrySendOneCycleNow</c> leaves the route
        /// Active (un-pausing from Paused) or a blocked-active wait; <c>TryPause</c>
        /// sets the flag only on the InTransit branch. But a Send-Once arm then
        /// dispatches Active -> InTransit while still armed, so once the cycle is in
        /// flight status alone reads InTransit for BOTH paths. That is why the live
        /// label uses <see cref="ResolveArmedKind"/> with the
        /// <c>sendOnceArmedRouteIds</c> provenance set as the authoritative source and
        /// this status heuristic only as the post-reload fallback.
        /// </summary>
        internal static ArmedSendKind ClassifyArmedSend(RouteStatus priorStatus)
        {
            return priorStatus == RouteStatus.InTransit
                ? ArmedSendKind.PauseAfterCycle
                : ArmedSendKind.SendOnce;
        }

        /// <summary>
        /// Resolves the armed-state kind for the disabled-button label (M6). When the
        /// route is known to have been armed by Send Once (<paramref name="sendOnceArmed"/>,
        /// from the UI provenance set) the answer is unambiguously
        /// <see cref="ArmedSendKind.SendOnce"/> regardless of the route's current status,
        /// which fixes the mislabel where a Send-Once cycle that has dispatched to
        /// InTransit would otherwise read "Pausing after this cycle...". Otherwise it
        /// falls back to the <see cref="ClassifyArmedSend"/> status heuristic. Pure.
        /// </summary>
        internal static ArmedSendKind ResolveArmedKind(bool sendOnceArmed, RouteStatus priorStatus)
        {
            return sendOnceArmed ? ArmedSendKind.SendOnce : ClassifyArmedSend(priorStatus);
        }

        /// <summary>
        /// The greyed Interact line-1 label for an armed state (M6). A Pause-armed route
        /// shows "Pausing..."; a Send-armed route shows "Sending...". Both fit the 100 px
        /// single; the Status cell carries the longer words. Plain ASCII (literal three
        /// dots), no glyphs.
        /// </summary>
        internal static string LabelForArmedState(ArmedSendKind kind)
        {
            switch (kind)
            {
                case ArmedSendKind.PauseAfterCycle:
                    return LogisticsRoutePresentation.PausingButtonLabel;
                case ArmedSendKind.SendOnce:
                default:
                    return LogisticsRoutePresentation.SendingButtonLabel;
            }
        }

        /// <summary>
        /// The hover tooltip for an armed state (M6). The Send-Once branch reuses the
        /// existing Send-Once "Sending..." tooltip text verbatim so the prior
        /// behavior is preserved; the Pause branch explains the finish-current-cycle
        /// -then-stop semantics. Plain ASCII.
        /// </summary>
        internal static string TooltipForArmedState(ArmedSendKind kind)
        {
            switch (kind)
            {
                case ArmedSendKind.PauseAfterCycle:
                    return "Pause requested: this route finishes the run in flight, "
                        + "then stops running on its schedule.";
                case ArmedSendKind.SendOnce:
                default:
                    return "Sending one run at the next window (funds, cargo, destination "
                        + "and launch window permitting), then pausing again.";
            }
        }

        private void DrawCandidateRow(RouteCandidate candidate, int rowNum)
        {
            if (candidate?.Analysis == null) return;
            string treeId = candidate.Tree?.Id ?? "<no-tree>";
            string rowKey = CandidateRowKey(candidate);
            bool expanded = expandedRows.Contains(rowKey);

            string name = RouteCreationFormatters.GenerateDefaultRouteName(candidate.Analysis, candidate.Tree);

            GUILayout.BeginHorizontal();

            GUILayout.Label(rowNum.ToString(CultureInfo.InvariantCulture), GUILayout.Width(ColW_Num));

            string arrow = expanded ? "\u25bc" : "\u25b6";
            if (GUILayout.Button($"{arrow} {name}", GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(rowKey, name);

            GUILayout.Label(FormatCandidateOrigin(candidate.Analysis, candidate.Tree), GUILayout.Width(ColW_Origin));
            RouteEndpoint? candidateEndpoint = candidate.Analysis.ConnectionWindow?.EndpointAtDock;
            GUILayout.Label(
                new GUIContent(FormatEndpointPlace(candidateEndpoint),
                    candidateEndpoint.HasValue ? FormatEndpointShort(candidateEndpoint.Value) : string.Empty),
                GUILayout.Width(ColW_Destination));
            // L3: Would-deliver cell. The per-cycle manifest text comes from the shared
            // pure LogisticsDeliveryPresentation.FormatWouldDeliver, the same formatter
            // the candidate detail line uses, so the cell and the detail never diverge.
            // The "eligible" / sealed copy that used to ride the dropped Status cell now
            // lives in this cell's tooltip plus the section-header tooltip.
            //
            // Run-cost (Phase 3.4): a compact net-cost suffix + tooltip detail is added
            // ONLY for Career + KSC origin with a known launch cost. The cost is read
            // from candidateRunCostCache (computed on the ~1 Hz candidate refresh,
            // never here on the draw path); a cache miss or a not-applicable / unknown
            // cost leaves the cell exactly as before (no suffix, base tooltip).
            string wouldDeliverText = LogisticsDeliveryPresentation.FormatWouldDeliver(
                candidate.Analysis.ResourceDeliveryManifest,
                candidate.Analysis.InventoryDeliveryManifest);
            string wouldDeliverTip =
                "A finished supply run: what it would deliver each run. Create Route makes it a Paused route you can Send or Activate.";
            if (candidateRunCostCache.TryGetValue(treeId, out RouteRunCostCalculator.RouteRunCost candCost)
                && candCost.Applicable && candCost.CostKnown)
            {
                wouldDeliverText += LogisticsCostPresentation.FormatCandidateSuffix(candCost);
                // Single-line strip: the tooltip carries only the fixed-length
                // explanation, which fits the one-line budget with the numbers no
                // longer embedded. The exact figures stay visible on the cell itself
                // (the net-cost candidate suffix) and in the expanded candidate
                // detail's "Cost/run:" line (DrawCandidateDetail), which carries the
                // launch/recovered breakdown a net figure alone cannot convey.
                wouldDeliverTip = LogisticsCostPresentation.FormatDetailTooltip(candCost);
            }
            GUILayout.Label(
                new GUIContent(wouldDeliverText, wouldDeliverTip),
                GUILayout.Width(ColW_WouldDeliver));
            // L3: Transit cell (the candidate's natural run duration) now has its own
            // column in the candidate header, so it draws a real value instead of riding
            // a placeholder tooltip.
            GUILayout.Label(FormatDuration(CandidateTransit(candidate)), GUILayout.Width(ColW_CandidateTransit));

            GUILayout.BeginHorizontal(GUILayout.Width(ColW_CandidateActions));
            if (GUILayout.Button(new GUIContent("Create Route",
                    "Make this supply run a stored route (created Paused; use Send to test it, then Activate)."),
                    GUILayout.Width(100)))
                pendingCreate = candidate;
            // M6 candidate intent helper: hide a tree the player never intends
            // as a route. Deferred through pendingDismissTreeId (applied in
            // ApplyPendingActions); always reversible from the Dismissed
            // subsection at the bottom of this section.
            if (GUILayout.Button(new GUIContent("Dismiss",
                    "Hide this mission from the Candidates section (it is not meant to become a route). Restore it any time from the Hidden missions list below."),
                    GUILayout.Width(70)))
            {
                pendingDismissTreeId = candidate.Tree?.Id;
                pendingDismissLabel = name;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndHorizontal();

            if (expanded)
                DrawCandidateDetail(candidate);
        }

        /// <summary>
        /// The expanded detail block: only what the row cannot say, every line participle-
        /// or noun-first with exact dates. Basic: what each run delivers and where, the next
        /// run, the dated hold / partial lines, the capacity context and Re-scan of a broken
        /// route, the last delivery from the ledger, the run cost, the mission the route was
        /// built from, the round-trip partner, and Rename / Delete. Advanced adds the Every
        /// and Priority steppers, Recent runs, Flights used and Link round-trip. Every
        /// conditional line is keyed on a CACHED legibility field (never on live route state
        /// combined with it) or on the latched drawTuning, so the IMGUI control count stays
        /// stable across Layout/Repaint while the ~1 Hz cache and the live route drift apart.
        /// </summary>
        private void DrawRouteDetail(Route route, double currentUT)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            RouteLegibility leg = GetLegibility(route);

            DetailLine(leg.DeliversLine ?? LogisticsRoutePresentation.FormatDeliversEachRun(null, null));
            if (leg.NextLine != null)
                DetailLine(leg.NextLine);

            // The last recorded hold, dated (Route.LastHoldUT is the last check, so the
            // line names the last held run, never when the hold began).
            if (leg.HoldText != null)
                DetailLine(leg.HoldText, statusStyleYellow);

            // Last-partial-delivery report: what a mid-transit capacity shrink actually
            // cost (the undelivered remainder is lost), dated.
            if (leg.PartialText != null)
                DetailLine(leg.PartialText, statusStyleYellow);

            // DestinationFull: the live free-capacity context ("Munar Station tanks full:
            // 0.0 of 150.0 LiquidFuel free"), so full tanks read apart from a misroute.
            if (route.Status == RouteStatus.DestinationFull && !string.IsNullOrEmpty(leg.CapacityContext))
                DetailLine(leg.CapacityContext, statusStyleYellow);

            // A recoverable surface EndpointLost route: "Re-scan for endpoint"; otherwise
            // the disabled-with-explanation note.
            if (route.Status == RouteStatus.EndpointLost)
                DrawEndpointRescan(route);

            // The last delivery, from the route's RouteCargoDelivered ledger rows (the
            // route stores no delivered UT), plus the running total.
            if (leg.LastDeliveredLine != null)
                DetailLine(leg.LastDeliveredLine, leg.LastCycleShortfall ? statusStyleYellow : detailStyle);

            // Run cost (Career + KSC origin, known cost only; nothing otherwise).
            if (leg.RunCost.Applicable && leg.RunCost.CostKnown)
            {
                DetailLine(new GUIContent(
                    LogisticsCostPresentation.FormatDetailLine(leg.RunCost),
                    LogisticsCostPresentation.FormatDetailTooltip(leg.RunCost)));
            }

            DrawRouteBuiltFromNote(route);

            // Round-trip pairing note, only when linked (a fact in both modes; linking and
            // unlinking are Advanced).
            DrawRouteLinkNote(route);

            if (drawTuning)
            {
                DrawCadenceStepper(route, leg);
                DrawPriorityStepper(route);

                // Recent runs: what each recent completed run paid, picked up and
                // delivered, newest first. Shortfall runs tint yellow.
                if (leg.FlowLines != null && leg.FlowLines.Count > 0)
                {
                    DetailLine(LogisticsFlowPresentation.RecentCyclesHeader);
                    for (int i = 0; i < leg.FlowLines.Count; i++)
                    {
                        LogisticsFlowPresentation.CycleFlowLine flowLine = leg.FlowLines[i];
                        DetailLine("  " + flowLine.Text,
                            flowLine.Shortfall ? statusStyleYellow : detailStyle);
                    }
                }

                DrawFlightsUsedLine(route);
            }

            DrawRouteDetailButtonRow(route);
            GUILayout.EndVertical();
        }

        /// <summary>
        /// "Built from mission 'X'." for the route's source mission (its ORIGINAL mission's
        /// name, else the mission's tree name, else the short id), plus the manual-looping
        /// clause in Advanced. Skipped on a degenerate route with no source. Drawn only on
        /// expand, so the by-id lookups stay off the per-frame path of collapsed rows.
        /// </summary>
        private void DrawRouteBuiltFromNote(Route route)
        {
            string treeId = ResolveRouteSourceTreeId(route);
            if (string.IsNullOrEmpty(treeId))
                return;
            DetailLine(LogisticsRoutePresentation.FormatBuiltFromMission(
                ResolveMissionDisplayName(treeId), drawTuning));
        }

        /// <summary>
        /// A source tree's player-facing mission name: the ORIGINAL mission's name (clones
        /// share the tree), else the tree's display name.
        /// </summary>
        private static string ResolveMissionDisplayName(string treeId)
        {
            Mission mission = string.IsNullOrEmpty(treeId) ? null : MissionStore.FindOriginalMission(treeId);
            return !string.IsNullOrEmpty(mission?.Name) ? mission.Name : ResolveTreeDisplayName(treeId);
        }

        /// <summary>
        /// Draws the M4c round-trip pairing note when <paramref name="route"/> is
        /// linked to a partner. Resolves the partner's display name via
        /// <see cref="RouteStore.TryGetRoute"/> (falling back to the short id when the
        /// partner cannot resolve, e.g. it was just deleted and the dangling link not
        /// yet swept), then renders the pure
        /// <see cref="LogisticsLinkPresentation.FormatLinkedNote"/> line. Drawn only on
        /// expand, so the by-id store lookup is off the per-frame hot path.
        /// </summary>
        private void DrawRouteLinkNote(Route route)
        {
            if (route == null || string.IsNullOrEmpty(route.LinkedRouteId))
                return;
            string partnerName =
                RouteStore.TryGetRoute(route.LinkedRouteId, out Route partner)
                    && partner != null && !string.IsNullOrEmpty(partner.Name)
                ? partner.Name
                : ShortId(route.LinkedRouteId);
            DetailLine(LogisticsLinkPresentation.FormatLinkedNote(partnerName));
        }

        /// <summary>
        /// Arms the M4c round-trip link picker for <paramref name="source"/> (the
        /// route whose "Link round-trip..." button was clicked). Sets the persistent
        /// popup state read by <see cref="DrawLinkPicker"/>; the popup is drawn from
        /// <see cref="DrawIfOpen"/> after the main window. Does NOT itself mutate the
        /// store — the partner is chosen + committed in the popup.
        /// </summary>
        private void OpenLinkPicker(Route source, Vector2 mousePos)
        {
            if (source == null || string.IsNullOrEmpty(source.Id))
                return;
            linkPickerOpen = true;
            linkPickerSourceRouteId = source.Id;
            linkPickerSourceName = source.Name ?? "<unnamed>";
            linkPickerSelectedId = null;
            linkPickerPosition = mousePos;
            linkPickerRect = new Rect(0, 0, 0, 0);
            linkPickerResizing = false;
            linkPickerScroll = Vector2.zero;
            ParsekLog.Verbose("UI", $"Logistics: link picker opened source={ShortId(source.Id)}");
        }

        /// <summary>
        /// Draws the M4c round-trip link picker as its own window on top of the
        /// logistics window when armed (mirrors <c>GroupPickerUI.Draw</c>: a separate
        /// <see cref="ClickThruBlocker.GUILayoutWindow"/> rather than a nested one).
        /// Synchronous — the Link button commits through <see cref="RouteStore.LinkRoutes"/>
        /// inside the draw pass, so the deferred-field trap does not apply.
        /// </summary>
        private void DrawLinkPicker()
        {
            if (!linkPickerOpen) return;

            ParsekUI.HandleResizeDrag(ref linkPickerRect, ref linkPickerResizing,
                LinkPickerMinW, LinkPickerMinH, null);

            if (linkPickerRect.width < 1f)
            {
                linkPickerRect = new Rect(
                    Mathf.Clamp(linkPickerPosition.x, 0, Screen.width - 340f),
                    Mathf.Clamp(linkPickerPosition.y, 0, Screen.height - 380f),
                    340f, 380f);
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                linkPickerRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekLogiLinkPicker".GetHashCode(),
                    linkPickerRect,
                    DrawLinkPickerContents,
                    "Link round-trip partner",
                    opaqueWindowStyle,
                    GUILayout.Width(linkPickerRect.width),
                    GUILayout.Height(linkPickerRect.height));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
        }

        /// <summary>
        /// Window-body callback for the link picker: a single-select list of eligible
        /// partner routes (<see cref="LogisticsLinkPresentation.BuildLinkCandidates"/>:
        /// other routes that are not already linked) plus Link / Cancel. Link commits
        /// <see cref="RouteStore.LinkRoutes"/> directly and dirties the legibility
        /// cache so both routes' rows refresh; Cancel just closes. Both close the popup.
        /// </summary>
        private void DrawLinkPickerContents(int windowID)
        {
            EnsureStyles();
            GUILayout.Label($"Link '{linkPickerSourceName}' with:", detailStyle);
            GUILayout.Space(3);

            List<LogisticsLinkPresentation.LinkCandidate> candidates =
                LogisticsLinkPresentation.BuildLinkCandidates(RouteStore.CommittedRoutes, linkPickerSourceRouteId);

            linkPickerScroll = GUILayout.BeginScrollView(linkPickerScroll, GUILayout.ExpandHeight(true));
            if (candidates.Count == 0)
            {
                GUILayout.Label(
                    "No eligible routes. A partner must be another route that is not already linked.",
                    detailStyle);
            }
            else
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    LogisticsLinkPresentation.LinkCandidate c = candidates[i];
                    bool selected = string.Equals(linkPickerSelectedId, c.Id, System.StringComparison.Ordinal);
                    bool now = GUILayout.Toggle(selected, "  " + c.Name);
                    if (now && !selected)
                        linkPickerSelectedId = c.Id;
                    else if (!now && selected)
                        linkPickerSelectedId = null;
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(3);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            bool prevEnabled = GUI.enabled;
            bool linkEnabled = !string.IsNullOrEmpty(linkPickerSelectedId);
            GUI.enabled = linkEnabled;
            bool linkClicked = GUILayout.Button(new GUIContent("Link",
                    "Pair these two routes as a round-trip: they alternate, each running only after its partner completes a run."),
                    GUILayout.Width(70));
            DisabledHoverEcho.CarryLastControl(linkEnabled, LinkButtonDisabledReason(linkPickerSelectedId));
            if (linkClicked)
            {
                string src = linkPickerSourceRouteId;
                string sel = linkPickerSelectedId;
                if (RouteStore.LinkRoutes(src, sel))
                {
                    ParsekLog.Info("UI",
                        $"Logistics: round-trip linked source={ShortId(src)} partner={ShortId(sel)} (via picker)");
                    lastLegibilityComputeRealtime = -1f;
                }
                linkPickerOpen = false;
            }
            GUI.enabled = prevEnabled;

            if (GUILayout.Button("Cancel", GUILayout.Width(70)))
            {
                ParsekLog.Verbose("UI", $"Logistics: link picker cancelled source={ShortId(linkPickerSourceRouteId)}");
                linkPickerOpen = false;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(linkPickerRect, ref linkPickerResizing, null);
            GUI.DragWindow();
        }

        /// <summary>
        /// Draws the M4 EndpointLost re-scan affordance. For a recoverable surface
        /// endpoint (<see cref="LogisticsDeliveryPresentation.ShouldOfferEndpointRescan"/>)
        /// it renders a "Re-scan for endpoint" button; otherwise a disabled-with-note
        /// label explaining why re-scan cannot help (orbital endpoints can only be
        /// matched by the baked PID). The button does its work DIRECTLY in the click
        /// branch (this is the synchronous draw path, not an async dialog callback, so
        /// the QW2 frame-reset-field trap does not apply and an inline action is
        /// correct): it calls <see cref="RouteEndpointResolver.TryResolveEndpoint"/>,
        /// logs the outcome, and on success clears
        /// <see cref="Route.NextEligibilityCheckUT"/> so the orchestrator re-attempts
        /// the cycle on the next tick (instead of waiting out the 30s retry interval),
        /// then dirties the legibility cache so the destination cell + capacity context
        /// refresh next frame. It does NOT flip the route status: the orchestrator's
        /// dispatch evaluator re-resolves the endpoint every retry tick and recovers
        /// the status itself. A re-scan miss just re-logs and leaves the route
        /// EndpointLost (the orchestrator keeps retrying on its own clock).
        /// A multi-stop route (GUI-P20) keeps the ONE button: it is offered when any
        /// stop is a recoverable surface endpoint, the click re-scans EVERY stop (the
        /// dispatch gate needs all of them to resolve), logs each stop's outcome on one
        /// line, and clears the retry gate only when every stop resolved.
        /// </summary>
        private void DrawEndpointRescan(Route route)
        {
            if (route?.Stops != null && route.Stops.Count > 1)
            {
                DrawMultiStopEndpointRescan(route);
                return;
            }
            RouteStop stop = route?.Stops != null && route.Stops.Count > 0 ? route.Stops[0] : null;
            if (stop == null)
                return;
            RouteEndpoint endpoint = stop.Endpoint;

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);

            if (LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(route.Status, endpoint))
            {
                if (GUILayout.Button(new GUIContent("Re-scan for endpoint",
                        "Search the destination body for a surface vessel near the recorded endpoint, then retry delivery immediately if found."),
                        GUILayout.Width(180f)))
                {
                    bool resolved = RouteEndpointResolver.TryResolveEndpoint(
                        endpoint, out Vessel v, out string reason);
                    if (resolved && v != null)
                    {
                        // Clear the retry rate-limit so the orchestrator re-resolves and
                        // recovers the status on the very next tick (it owns the status
                        // flip; we never set Active here).
                        route.NextEligibilityCheckUT = null;
                        ParsekLog.Info("UI",
                            $"Logistics: endpoint re-scan route={ShortId(route.Id)} resolved=true name='{v.vesselName}' (cleared retry gate)");
                    }
                    else
                    {
                        ParsekLog.Info("UI",
                            $"Logistics: endpoint re-scan route={ShortId(route.Id)} resolved=false reason='{reason}' (still EndpointLost)");
                    }
                    // Refresh the destination cell + capacity context next frame.
                    lastLegibilityComputeRealtime = -1f;
                }
            }
            else
            {
                bool prevEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Button(new GUIContent("Re-scan for endpoint",
                    LogisticsDeliveryPresentation.RescanIneligibleReason(endpoint)),
                    GUILayout.Width(180f));
                DisabledHoverEcho.CarryLastControl(
                    false, LogisticsDeliveryPresentation.RescanIneligibleReason(endpoint));
                GUI.enabled = prevEnabled;
                GUILayout.Label(LogisticsDeliveryPresentation.RescanIneligibleReason(endpoint),
                    detailStyle, GUILayout.ExpandWidth(true));
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// The multi-stop form of <see cref="DrawEndpointRescan"/>: the same single
        /// button and the same disabled-with-note fallback, decided over every stop
        /// (<see cref="LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan"/>).
        /// The click re-scans every non-null stop, logs one summary line naming each
        /// stop's outcome (<see cref="LogisticsDeliveryPresentation.FormatRescanOutcome"/>),
        /// and clears the retry gate only when every stop resolved, because the
        /// dispatch gate refuses the cycle while any stop is lost.
        /// </summary>
        private void DrawMultiStopEndpointRescan(Route route)
        {
            if (LogisticsDeliveryPresentation.CountStops(route.Stops) == 0)
                return;

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);

            if (LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan(route.Status, route.Stops))
            {
                if (GUILayout.Button(new GUIContent("Re-scan for endpoint",
                        "Search each stop's body for a surface vessel near its recorded endpoint, then retry delivery immediately if every stop is found."),
                        GUILayout.Width(180f)))
                {
                    var results = new List<LogisticsDeliveryPresentation.RescanStopResult>(route.Stops.Count);
                    for (int i = 0; i < route.Stops.Count; i++)
                    {
                        RouteStop s = route.Stops[i];
                        if (s == null) continue;
                        bool ok = RouteEndpointResolver.TryResolveEndpoint(s.Endpoint, out Vessel v, out string reason);
                        results.Add(new LogisticsDeliveryPresentation.RescanStopResult(
                            i, ok && v != null, ok && v != null ? v.vesselName : null, reason));
                    }
                    bool allResolved = LogisticsDeliveryPresentation.AllStopsResolved(results);
                    if (allResolved)
                        route.NextEligibilityCheckUT = null;
                    ParsekLog.Info("UI",
                        $"Logistics: endpoint re-scan route={ShortId(route.Id)} " +
                        LogisticsDeliveryPresentation.FormatRescanOutcome(results) +
                        (allResolved ? " (cleared retry gate)" : " (still EndpointLost)"));
                    lastLegibilityComputeRealtime = -1f;
                }
            }
            else
            {
                string reason = LogisticsDeliveryPresentation.RouteRescanIneligibleReason(route.Stops);
                bool prevEnabled = GUI.enabled;
                GUI.enabled = false;
                GUILayout.Button(new GUIContent("Re-scan for endpoint", reason), GUILayout.Width(180f));
                DisabledHoverEcho.CarryLastControl(false, reason);
                GUI.enabled = prevEnabled;
                GUILayout.Label(reason, detailStyle, GUILayout.ExpandWidth(true));
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Resolves the route's source tree id for the M5 ownership note: the first
        /// non-empty <c>SourceRefs[].TreeId</c>, falling back to
        /// <see cref="Route.BackingMissionTreeId"/>. Returns null when neither is set.
        /// </summary>
        internal static string ResolveRouteSourceTreeId(Route route)
        {
            if (route == null)
                return null;
            if (route.SourceRefs != null)
            {
                for (int s = 0; s < route.SourceRefs.Count; s++)
                {
                    RouteSourceRef sref = route.SourceRefs[s];
                    if (sref != null && !string.IsNullOrEmpty(sref.TreeId))
                        return sref.TreeId;
                }
            }
            return string.IsNullOrEmpty(route.BackingMissionTreeId) ? null : route.BackingMissionTreeId;
        }

        /// <summary>
        /// Resolves a tree's player-visible display name from
        /// <see cref="RecordingStore.CommittedTrees"/> by id (M5). Falls back to the
        /// short tree id, then "<unknown tree>", so the ownership note / toast never
        /// shows a null name. Reads CommittedTrees (NOT the grep-gated
        /// CommittedRecordings list), so it stays off the ERS/ELS gate.
        /// </summary>
        private static string ResolveTreeDisplayName(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return "<unknown mission>";
            var trees = RecordingStore.CommittedTrees;
            if (trees != null)
            {
                for (int i = 0; i < trees.Count; i++)
                {
                    RecordingTree t = trees[i];
                    if (t != null && string.Equals(t.Id, treeId, System.StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(t.TreeName))
                            return t.TreeName;
                        break;
                    }
                }
            }
            return ShortId(treeId);
        }

        /// <summary>
        /// The detail block's closing button row, right-aligned: [Link round-trip...] or
        /// [Unlink] (Advanced only), [Rename], [Delete]. Delete lives here, out of the scan
        /// line, and still goes through the unchanged confirm dialog
        /// (<see cref="SpawnDeleteRouteConfirmation"/> via <see cref="pendingConfirmDeleteRoute"/>).
        /// While this route is being renamed the row is the deferred-commit name field
        /// instead (the RecordingsTableUI idiom): Enter commits here, a click outside
        /// commits in <see cref="HandleLogisticsDefocus"/>, Escape cancels. Editing is keyed
        /// by <see cref="Route.Id"/> (NOT a row index) because routes are re-sectioned /
        /// added / removed between frames; the committed name shows in the row at once
        /// because <see cref="DrawRouteRow"/> reads <see cref="Route.Name"/> live.
        /// </summary>
        private void DrawRouteDetailButtonRow(Route route)
        {
            const string controlName = "LogiRouteRename";
            bool editingThis = string.Equals(renamingRouteId, route.Id, System.StringComparison.Ordinal);

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);

            if (!editingThis)
            {
                GUILayout.FlexibleSpace();

                // Round-trip link control (Advanced). Unlinked: "Link round-trip..." arms
                // the partner picker. Linked: "Unlink" breaks the pair inline, DIRECTLY in
                // the click branch (synchronous draw path, so the async-callback trap does
                // not apply). Either way the legibility cache is dirtied so the row and
                // detail refresh next frame.
                if (drawTuning)
                {
                    if (string.IsNullOrEmpty(route.LinkedRouteId))
                    {
                        if (GUILayout.Button(new GUIContent("Link round-trip...",
                                "Pair this route with another so they alternate: each runs only after its partner completes a run (a single reused transport flying out and back)."),
                                GUILayout.Width(RouteDetailButtonWidth)))
                        {
                            OpenLinkPicker(route, Event.current.mousePosition);
                        }
                    }
                    else
                    {
                        if (GUILayout.Button(new GUIContent("Unlink",
                                "Break this route's round-trip pairing; both routes return to running on their own schedule."),
                                GUILayout.Width(RouteDetailButtonWidth)))
                        {
                            ParsekLog.Info("UI",
                                $"Logistics: unlink button route={ShortId(route.Id)} partner={ShortId(route.LinkedRouteId)}");
                            RouteStore.UnlinkRoute(route.Id);
                            lastLegibilityComputeRealtime = -1f;
                        }
                    }
                }

                if (GUILayout.Button(new GUIContent("Rename", "Edit this route's name"), GUILayout.Width(RouteDetailButtonWidth)))
                {
                    // Editing the interval and the name at once would cross-wire the two
                    // deferred commits; the interval edit-start already suppresses itself
                    // while a rename is active, so clear any pending interval edit first.
                    ClearIntervalEdit();
                    renamingRouteId = route.Id;
                    renamingRouteText = route.Name ?? string.Empty;
                    renamingRouteFocused = false;
                    renamingRouteRect = default;
                    ParsekLog.Verbose("UI",
                        $"Logistics: rename started route={ShortId(route.Id)} current='{route.Name}'");
                }

                if (GUILayout.Button(new GUIContent("Delete", "Delete this route (asks first)."),
                        GUILayout.Width(RouteDetailButtonWidth)))
                    pendingConfirmDeleteRoute = route;
            }
            else
            {
                bool submit = Event.current.type == EventType.KeyDown
                    && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                bool cancel = Event.current.type == EventType.KeyDown
                    && Event.current.keyCode == KeyCode.Escape;

                GUILayout.Label("Name:", detailStyle, GUILayout.Width(46f));
                GUI.SetNextControlName(controlName);
                renamingRouteText = GUILayout.TextField(renamingRouteText ?? string.Empty, GUILayout.ExpandWidth(true));
                renamingRouteRect = GUILayoutUtility.GetLastRect();

                if (!renamingRouteFocused)
                {
                    GUI.FocusControl(controlName);
                    renamingRouteFocused = true;
                }

                if (submit)
                {
                    CommitRouteRename(route);
                    Event.current.Use();
                }
                else if (cancel)
                {
                    ParsekLog.Verbose("UI",
                        $"Logistics: rename cancelled route={ShortId(route.Id)}");
                    ClearRouteRename();
                    Event.current.Use();
                }
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Commits the M2 rename for <paramref name="route"/>: routes the typed text
        /// through the pure <see cref="LogisticsRenamePresentation.ComputeRouteRename"/>
        /// (trim + empty-guard + unchanged-guard), and on a real change writes
        /// <see cref="Route.Name"/> directly and Info-logs the from/to. Empty /
        /// whitespace / unchanged input leaves the name untouched. Always clears the
        /// rename edit state. Tolerates a null route (the field id resolved to nothing)
        /// by just clearing.
        /// </summary>
        private void CommitRouteRename(Route route)
        {
            if (route == null)
            {
                ClearRouteRename();
                return;
            }

            if (LogisticsRenamePresentation.ComputeRouteRename(route.Name, renamingRouteText, out string committed))
            {
                ParsekLog.Info("UI",
                    $"Logistics: route renamed from '{route.Name}' to '{committed}' (route={ShortId(route.Id)})");
                route.Name = committed;
                // Dirty the legibility cache so a sort-by-Name re-sorts immediately on
                // rename rather than lagging until the next ~1 Hz refresh (mirrors
                // CommitIntervalEdit's invalidation for a sort-by-Interval change).
                lastLegibilityComputeRealtime = -1f;
            }
            else
            {
                ParsekLog.Verbose("UI",
                    $"Logistics: rename no-op route={ShortId(route.Id)} typed='{renamingRouteText}' (empty/whitespace/unchanged)");
            }
            ClearRouteRename();
        }

        private void ClearRouteRename()
        {
            renamingRouteId = null;
            renamingRouteText = null;
            renamingRouteFocused = false;
            renamingRouteRect = default;
            GUIUtility.keyboardControl = 0;
        }

        /// <summary>
        /// The Advanced "Flights used:" line: the flights the route copies, by name, a
        /// repeated name numbered "Name [1]", "Name [2]" (the Mission Log convention)
        /// instead of the old "rec N of tree 'X'" clauses. Each id resolves through the
        /// literal-free <see cref="RecordingStore.TryResolveRecordingDisplayInfo"/> accessor
        /// (the raw committed-list read stays in that already-allowlisted file, so the
        /// ERS/ELS grep gate stays green); an unresolved id falls back to its short id. The
        /// short ids ride the hover for debugging. Drawn only on expand in Advanced.
        /// </summary>
        private void DrawFlightsUsedLine(Route route)
        {
            (string text, string tooltip) = BuildFlightsUsedContent(route);
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(new GUIContent(text, tooltip), detailStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Builds the "Flights used:" text and its short-id hover. Logs one Verbose batch
        /// summary (resolved-vs-total) for the route, not per id.
        /// </summary>
        private static (string text, string tooltip) BuildFlightsUsedContent(Route route)
        {
            if (route?.RecordingIds == null || route.RecordingIds.Count == 0)
                return (LogisticsRoutePresentation.FormatFlightsUsed(null, null), string.Empty);

            var names = new List<string>(route.RecordingIds.Count);
            var shortIds = new List<string>(route.RecordingIds.Count);
            int resolved = 0;
            for (int i = 0; i < route.RecordingIds.Count; i++)
            {
                string id = route.RecordingIds[i];
                bool ok = RecordingStore.TryResolveRecordingDisplayInfo(
                    id, out string recName, out _, out _);
                if (ok) resolved++;
                names.Add(ok ? recName : null);
                shortIds.Add(ShortId(id));
            }

            ParsekLog.Verbose("UI",
                $"Logistics: flights used line route={ShortId(route.Id)} " +
                $"resolved={resolved.ToString(CultureInfo.InvariantCulture)}/{route.RecordingIds.Count.ToString(CultureInfo.InvariantCulture)}");
            return (LogisticsRoutePresentation.FormatFlightsUsed(names, shortIds), string.Join(", ", shortIds));
        }

        // Cadence stepper (Phase 6): "- N x (~human) +" where N is the dispatch
        // cadence multiplier (>= 1) and the human duration is N x the run span.
        // 1x is the floor (the route cannot dispatch faster). Records the edit into
        // the deferred-mutation fields; ApplyPendingActions recomputes the interval.
        private void DrawCadenceStepper(Route route, RouteLegibility leg)
        {
            int n = Route.ClampCadenceMultiplier(route.CadenceMultiplier);
            // M5 (D8/OQ2): a windowed basis swaps the readout to per-window
            // wording ("2x (every 2nd window)") - the interval arithmetic is
            // actively misleading on synodic spacing. Flat routes unchanged.
            bool windowed = RouteWindowBasisPresentation.IsWindowedBasis(leg.Basis);

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(
                new GUIContent("Every:",
                    "How often the route runs, as a multiple of the run duration. 1x is the floor (the fastest the run allows); raise it to run less often."),
                detailStyle, GUILayout.Width(70f));

            // "-" decrements (no-op + greyed at the 1x floor).
            bool atFloor = n <= 1;
            const string cadenceAtFloorReason =
                "Already at the minimum (1x = the fastest the run allows)";
            GUI.enabled = !atFloor;
            bool cadenceDownClicked = GUILayout.Button(new GUIContent("-",
                    atFloor ? cadenceAtFloorReason : "Run more often"),
                    GUILayout.Width(24f));
            DisabledHoverEcho.CarryLastControl(!atFloor, cadenceAtFloorReason);
            if (cadenceDownClicked)
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, -1);
            }
            GUI.enabled = true;

            // Current N x + the resulting human cadence (windowed wording for a
            // windowed basis; wider cell to fit "2x (every 2nd window)").
            GUILayout.Label(
                windowed ? RouteWindowBasisPresentation.FormatWindowedCadence(n) : FormatCadence(route),
                detailStyle, GUILayout.Width(windowed ? 170f : 110f));

            if (GUILayout.Button(new GUIContent("+", "Run less often"), GUILayout.Width(24f)))
            {
                pendingCadenceRoute = route;
                pendingCadenceMultiplier = RouteCadence.StepMultiplier(n, +1);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // Priority stepper (M1 dispatch priority): "- N +" where N is the route's
        // dispatch priority (>= 0). Lower values dispatch first when several routes
        // contend in the same orchestrator tick; 0 is the floor (and the default).
        // Records the edit into the deferred-mutation fields; ApplyPendingActions
        // commits it via RoutePriority.Apply.
        private void DrawPriorityStepper(Route route)
        {
            int p = Route.ClampPriority(route.DispatchPriority);

            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(
                new GUIContent("Priority:",
                    "When several routes are due at once, the lower number runs first. 0 is the highest priority (and the default)."),
                detailStyle, GUILayout.Width(70f));

            // "-" decrements (no-op + greyed at the 0 floor).
            bool atFloor = p <= 0;
            const string priorityAtFloorReason = "Already at the highest priority (0)";
            GUI.enabled = !atFloor;
            bool priorityDownClicked = GUILayout.Button(new GUIContent("-",
                    atFloor ? priorityAtFloorReason : "Run earlier when routes are due at once"),
                    GUILayout.Width(24f));
            DisabledHoverEcho.CarryLastControl(!atFloor, priorityAtFloorReason);
            if (priorityDownClicked)
            {
                pendingPriorityRoute = route;
                pendingPriorityValue = RoutePriority.Step(p, -1);
            }
            GUI.enabled = true;

            GUILayout.Label(p.ToString(CultureInfo.InvariantCulture), detailStyle, GUILayout.Width(110f));

            if (GUILayout.Button(new GUIContent("+", "Run later when routes are due at once"), GUILayout.Width(24f)))
            {
                pendingPriorityRoute = route;
                pendingPriorityValue = RoutePriority.Step(p, +1);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// The expanded candidate: only what the row cannot say. The run-cost breakdown
        /// (the row cell shows only the NET suffix; this is the one pre-creation surface
        /// carrying "launch X - recovered Y", the split a player needs to judge a
        /// cheap-looking net) and the mission the candidate comes from. Same gate and
        /// cache as the row (never computed on the draw path).
        /// </summary>
        private void DrawCandidateDetail(RouteCandidate candidate)
        {
            GUILayout.BeginVertical(GUI.skin.box);

            if (candidateRunCostCache.TryGetValue(
                    candidate.Tree?.Id ?? "<no-tree>",
                    out RouteRunCostCalculator.RouteRunCost candCost)
                && candCost.Applicable && candCost.CostKnown)
            {
                DetailLine(new GUIContent(
                    LogisticsCostPresentation.FormatDetailLine(candCost),
                    LogisticsCostPresentation.FormatDetailTooltip(candCost)));
            }

            string treeId = candidate.Tree?.Id;
            string missionName = !string.IsNullOrEmpty(treeId)
                ? ResolveMissionDisplayName(treeId)
                : candidate.Tree?.TreeName;
            DetailLine(LogisticsRoutePresentation.FormatBuiltFromMission(missionName, false));
            GUILayout.EndVertical();
        }

        private void DetailLine(string text)
        {
            DetailLine(text, detailStyle);
        }

        // Detail line with an explicit style (e.g. statusStyleYellow for the H2
        // shortfall line). Same indented full-width layout as the default overload.
        private void DetailLine(string text, GUIStyle style)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(text, style ?? detailStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        // Detail line carrying a hover tooltip (GUIContent). Same indented full-width
        // layout as the string overloads; used by the run-cost line so the net =
        // launch - recovered explanation + D1 caveat surface on hover.
        private void DetailLine(GUIContent content)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            GUILayout.Label(content, detailStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawSectionHeader(string text)
        {
            // Use the shared house section-header bar (bold label in a box, full-width)
            // so Logistics headers match Settings / Recordings / Timeline / Missions,
            // but CENTER the text via a local clone so the shared (left-aligned) style
            // those other windows use is not changed. Built once and reused.
            if (sectionHeaderCenteredStyle == null)
            {
                sectionHeaderCenteredStyle = new GUIStyle(parentUI.GetSectionHeaderStyle())
                {
                    alignment = TextAnchor.MiddleCenter
                };
            }
            GUILayout.Space(SpacingSmall);
            // Nest the header label inside a skin box so the section-subtitle cell carries
            // the SAME dark "box-on-box" background the column-header row has. The column
            // header draws its box-styled cells INSIDE a BeginVertical(GUI.skin.box)
            // container (two box layers, reading as a solid dark bar); a bare full-width
            // box-label sits on only the window background (one layer) and looks lighter.
            // Wrapping the label in a box horizontal adds the second box layer so the
            // subtitle matches the table-header shade.
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label(text, sectionHeaderCenteredStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }
        private GUIStyle sectionHeaderCenteredStyle;

        private void ToggleExpanded(string key, string nameForLog)
        {
            if (expandedRows.Contains(key))
            {
                expandedRows.Remove(key);
                ParsekLog.Verbose("UI", $"Logistics row collapsed: '{nameForLog}'");
            }
            else
            {
                expandedRows.Add(key);
                ParsekLog.Verbose("UI", $"Logistics row expanded: '{nameForLog}'");
            }
        }

        private void ApplyPendingActions(double currentUT)
        {
            // These four mutate route state HERE (status / cadence), which changes the
            // legibility cache inputs (the countdown and the merged Status cell). Force a recompute next
            // frame so a player action refreshes the cells immediately instead of
            // waiting out the ~1s timer. Create and Delete are deliberately NOT listed:
            // in this method they only SPAWN their confirm dialogs, and the actual
            // mutation (plus its cache invalidation) happens later in the dialog
            // callbacks (Create dirties both caches on a successful build; a Delete just
            // removes the route, whose stale cache entry is never drawn again). Listing
            // them here would force a wasted full recompute on every dialog open / Cancel.
            bool routeStateMutated =
                pendingPause != null || pendingActivate != null || pendingSendOnce != null
                || pendingCadenceRoute != null;

            if (pendingPause != null)
            {
                bool ok = RouteOrchestrator.TryPause(pendingPause);
                // An explicit Pause overrides any prior Send-Once provenance (both leave
                // PauseAfterCurrentCycle set): drop the id so a Send-Once-then-Pause
                // armed cycle reads "Pausing after this cycle..." not "Sending one cycle...".
                if (ok && !string.IsNullOrEmpty(pendingPause.Id))
                    sendOnceArmedRouteIds.Remove(pendingPause.Id);
                ParsekLog.Info("UI", $"Logistics: Pause route={ShortId(pendingPause.Id)} result={(ok ? "paused" : "rejected")}");
            }
            if (pendingActivate != null)
            {
                bool ok = RouteOrchestrator.TryActivate(pendingActivate, currentUT);
                ParsekLog.Info("UI", $"Logistics: Activate route={ShortId(pendingActivate.Id)} result={(ok ? "active" : "rejected")}");
            }
            if (pendingSendOnce != null)
            {
                bool ok = RouteOrchestrator.TrySendOneCycleNow(pendingSendOnce, currentUT);
                // Record the Send-Once provenance (M6) so the armed-button label stays
                // "Sending one cycle..." even after the cycle dispatches to InTransit.
                if (ok && !string.IsNullOrEmpty(pendingSendOnce.Id))
                    sendOnceArmedRouteIds.Add(pendingSendOnce.Id);
                ParsekLog.Info("UI", $"Logistics: Send Once route={ShortId(pendingSendOnce.Id)} result={(ok ? "armed" : "rejected")}");
            }
            if (pendingConfirmDeleteRoute != null)
            {
                // Spawns the modal confirm once on the click frame; the actual
                // RouteStore.RemoveRoute runs in the dialog's Delete callback (see
                // SpawnDeleteRouteConfirmation), never here, so deletion is gated on
                // an explicit confirm.
                SpawnDeleteRouteConfirmation(pendingConfirmDeleteRoute);
            }
            if (pendingConfirmDeleteDormantRoute != null)
            {
                // Same one-shot confirm shape as the committed-route delete;
                // the actual RouteStore.RemoveDormantRoute runs in the dialog's
                // Delete callback, never here. No cache dirtying needed: dormant
                // routes feed no legibility / candidate cache.
                SpawnDeleteDormantRouteConfirmation(pendingConfirmDeleteDormantRoute);
            }
            if (pendingCreate != null)
            {
                // Spawns the informed confirm once on the click frame; the actual
                // RouteBuilder.BuildRoute (+ TryActivate on "Create and Activate")
                // runs in the dialog button callbacks (see
                // SpawnCreateRouteConfirmation), never here, so a route is created
                // only on an explicit confirm and the candidate / legibility caches
                // are dirtied in-callback once the route actually exists.
                SpawnCreateRouteConfirmation(pendingCreate);
            }
            if (pendingCadenceRoute != null)
            {
                bool changed = RouteCadence.ApplyMultiplier(pendingCadenceRoute, pendingCadenceMultiplier);
                ParsekLog.Info("UI",
                    $"Logistics: Cadence route={ShortId(pendingCadenceRoute.Id)} N={pendingCadenceMultiplier} " +
                    $"result={(changed ? "applied" : "unchanged")}");
            }
            if (pendingPriorityRoute != null)
            {
                // Deliberately NOT in routeStateMutated: priority feeds only the
                // orchestrator's per-tick processing order, never a legibility-cache
                // input (no countdown / status / cost change), so forcing a recompute
                // here would be wasted work.
                bool changed = RoutePriority.Apply(pendingPriorityRoute, pendingPriorityValue);
                ParsekLog.Info("UI",
                    $"Logistics: Priority route={ShortId(pendingPriorityRoute.Id)} value={pendingPriorityValue} " +
                    $"result={(changed ? "applied" : "unchanged")}");
            }
            if (pendingDismissTreeId != null)
            {
                // M6 candidate intent helper: pure UI-intent mutation (no route /
                // ledger effect, always reversible). RouteStore logs the
                // authoritative Info line with tree id + display name. On success
                // both derivation caches are dirtied so the tree vanishes from the
                // candidates AND near-miss lists this second, not after ~1s.
                bool ok = RouteStore.DismissCandidateTree(pendingDismissTreeId, pendingDismissLabel);
                ParsekLog.Info("UI",
                    $"Logistics: Dismiss candidate tree={ShortId(pendingDismissTreeId)} " +
                    $"name='{pendingDismissLabel}' result={(ok ? "dismissed" : "no-op")}");
                if (ok)
                {
                    lastCandidateComputeRealtime = -1f;
                    lastNearMissComputeRealtime = -1f;
                }
            }
            if (pendingRestoreTreeId != null)
            {
                bool ok = RouteStore.RestoreCandidateTree(pendingRestoreTreeId, pendingRestoreLabel);
                ParsekLog.Info("UI",
                    $"Logistics: Restore candidate tree={ShortId(pendingRestoreTreeId)} " +
                    $"name='{pendingRestoreLabel}' result={(ok ? "restored" : "no-op")}");
                if (ok)
                {
                    lastCandidateComputeRealtime = -1f;
                    lastNearMissComputeRealtime = -1f;
                }
            }

            if (routeStateMutated)
                lastLegibilityComputeRealtime = -1f;

            pendingPause = null;
            pendingActivate = null;
            pendingSendOnce = null;
            pendingConfirmDeleteRoute = null;
            pendingConfirmDeleteDormantRoute = null;
            pendingCreate = null;
            pendingCadenceRoute = null;
            pendingCadenceMultiplier = 0;
            pendingPriorityRoute = null;
            pendingPriorityValue = 0;
            pendingDismissTreeId = null;
            pendingDismissLabel = null;
            pendingRestoreTreeId = null;
            pendingRestoreLabel = null;
        }

        /// <summary>
        /// Spawns the modal "Delete route '...'? This cannot be undone." confirm.
        /// The Delete button calls <see cref="RouteStore.RemoveRoute"/> directly in
        /// its callback, performing the destructive action in-callback; this is
        /// safe because the callback fires outside the window's route iteration, and
        /// it avoids the frame-top deferred-field reset that would otherwise clobber
        /// an asynchronously set delete request. Cancel only logs. Deletion never
        /// happens without a confirm.
        /// </summary>
        private void SpawnDeleteRouteConfirmation(Route route)
        {
            if (route == null)
                return;

            // Capture the id locally so the Delete closure does not depend on a
            // mutable instance field that ApplyPendingActions nulls this frame.
            string routeId = route.Id;
            string body = BuildDeleteConfirmBody(route);

            ParsekLog.Info("UI",
                $"Logistics: Delete route={ShortId(routeId)} confirm dialog spawned");

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekLogisticsDeleteRouteConfirm",
                    body,
                    "Confirm: Delete Route",
                    HighLogic.UISkin,
                    new DialogGUIButton("Delete", () =>
                    {
                        bool ok = RouteStore.RemoveRoute(routeId);
                        ParsekLog.Info("UI",
                            $"Logistics: Delete route={ShortId(routeId)} confirmed result={(ok ? "removed" : "not-found")}");
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI",
                            $"Logistics: Delete route={ShortId(routeId)} cancelled");
                    })),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// Spawns the modal delete confirm for a DORMANT route (mirrors
        /// <see cref="SpawnDeleteRouteConfirmation"/>: id captured into a local so
        /// the closure survives the frame-top field reset; the destructive
        /// <see cref="RouteStore.RemoveDormantRoute"/> runs in the Delete callback
        /// only, so deletion never happens without an explicit confirm).
        /// </summary>
        private void SpawnDeleteDormantRouteConfirmation(Route route)
        {
            if (route == null)
                return;

            string routeId = route.Id;
            string display = LogisticsDormantPresentation.DormantRouteDisplayName(route.Name, route.Id);
            string body = LogisticsDormantPresentation.BuildDeleteDormantConfirmBody(display);

            ParsekLog.Info("UI",
                $"Logistics: Delete dormant route={ShortId(routeId)} confirm dialog spawned");

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekLogisticsDeleteDormantRouteConfirm",
                    body,
                    "Confirm: Delete Dormant Route",
                    HighLogic.UISkin,
                    new DialogGUIButton("Delete", () =>
                    {
                        bool ok = RouteStore.RemoveDormantRoute(routeId);
                        ParsekLog.Info("UI",
                            $"Logistics: Delete dormant route={ShortId(routeId)} confirmed result={(ok ? "removed" : "not-found")}");
                        // Delete-after-materialize race: the route can leave the
                        // dormant list between dialog spawn and Confirm (the
                        // orchestrator tick reached its CreatedUT). Tell the
                        // player where it went instead of silently doing nothing;
                        // the normal section's own Delete handles it from here.
                        if (!ok && RouteStore.TryGetRoute(routeId, out _))
                        {
                            ParsekLog.Info("UI",
                                $"Logistics: dormant route={ShortId(routeId)} re-materialized before " +
                                "delete confirm; directing player to the routes section");
                            try
                            {
                                ScreenMessages.PostScreenMessage(
                                    $"[Parsek] Supply route '{display}' re-materialized while the " +
                                    "confirmation was open. Delete it from its routes section instead.", 6f);
                            }
                            catch (System.Exception ex)
                            {
                                ParsekLog.Verbose("UI",
                                    $"Logistics: screen message unavailable ({ex.GetType().Name})");
                            }
                        }
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI",
                            $"Logistics: Delete dormant route={ShortId(routeId)} cancelled");
                    })),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// Builds the body text for the route-delete confirm dialog. Uses the
        /// route's display name when present, falling back to its short id so the
        /// player always sees an identifier (never the literal "null"). Pure and
        /// Unity-free for unit testing.
        /// </summary>
        internal static string BuildDeleteConfirmBody(Route route)
        {
            string name = route != null && !string.IsNullOrEmpty(route.Name)
                ? route.Name
                : ShortId(route?.Id);
            return $"Delete route '{name}'?\n\nThis cannot be undone.";
        }

        /// <summary>
        /// Spawns the informed "Create Supply Route?" confirm for a candidate: a
        /// window-owned <see cref="MultiOptionDialog"/> rendering
        /// <see cref="RouteCreationFormatters.BuildSummaryBlock"/> (the SAME summary
        /// the post-commit auto-dialog shows) with three buttons: "Create Paused"
        /// (the existing window-create behavior), "Create and Activate" (build then
        /// <see cref="RouteOrchestrator.TryActivate"/>), and "Cancel". Mirrors
        /// <see cref="SpawnDeleteRouteConfirmation"/>: the candidate is captured into
        /// a LOCAL so the closures do not depend on the <c>pendingCreate</c> instance
        /// field that <see cref="ApplyPendingActions"/> nulls this frame, and each
        /// create callback runs the build DIRECTLY in the closure (the
        /// dialog-callback fires asynchronously, outside the draw pass, so setting a
        /// frame-reset deferred field would be silently clobbered before
        /// ApplyPendingActions reads it). After a build the callback dirties both
        /// the candidate cache (the promoted run leaves the Candidates list) and the
        /// legibility cache (the new route's cells appear immediately). This is now the
        /// ONLY route-creation confirm in the mod: the post-commit auto-dialog that used to
        /// share the geometry was deleted 2026-09-11 as unreachable (GUI census D4), leaving
        /// <see cref="RouteCreationDialog"/> as a pure span helper.
        /// </summary>
        private void SpawnCreateRouteConfirmation(RouteCandidate candidate)
        {
            if (candidate?.Analysis == null || candidate.Tree == null)
            {
                ParsekLog.Warn("UI", "Logistics: Create Route confirm - null candidate/analysis/tree, ignored");
                return;
            }

            // Capture into locals so the (asynchronous) button closures do not depend
            // on a mutable instance field that ApplyPendingActions nulls this frame.
            RouteCandidate cand = candidate;
            Game.Modes mode = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Mode
                : Game.Modes.SANDBOX;
            // Same summary call the post-commit auto-dialog makes (analysis + mode +
            // tree); passing cand.Tree makes the Transit line resolve the real
            // [root..dock] span so the dialog matches the route that gets built.
            // The run-cost block (Career + KSC origin) is computed here from the
            // candidate's source recording + tree (no Route exists yet) and passed in.
            RouteRunCostCalculator.RouteRunCost runCost =
                ComputeCandidateRunCost(cand.Analysis, cand.Tree);
            string body = RouteCreationFormatters.BuildSummaryBlock(cand.Analysis, mode, cand.Tree, runCost);

            ParsekLog.Info("UI",
                $"Logistics: Create Route confirm dialog spawned tree={ShortId(cand.Tree.Id)} mode={mode}");

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekLogisticsCreateRouteConfirm",
                    body,
                    "Create Supply Route?",
                    HighLogic.UISkin,
                    new DialogGUIButton("Create Paused", () =>
                        HandleCreateRouteChoice(cand, LogisticsCreatePresentation.CreateRouteChoice.CreatePaused)),
                    new DialogGUIButton("Create and Activate", () =>
                        HandleCreateRouteChoice(cand, LogisticsCreatePresentation.CreateRouteChoice.CreateAndActivate)),
                    new DialogGUIButton("Cancel", () =>
                        HandleCreateRouteChoice(cand, LogisticsCreatePresentation.CreateRouteChoice.Cancel))),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// Runs the in-callback work for a Create Route confirm button. The pure
        /// <see cref="LogisticsCreatePresentation"/> decision drives the branch:
        /// <see cref="LogisticsCreatePresentation.ShouldBuild"/> gates the build and
        /// <see cref="LogisticsCreatePresentation.ShouldActivate"/> gates the
        /// post-build <see cref="RouteOrchestrator.TryActivate"/>, so the button ->
        /// effect mapping is unit-tested off the IMGUI path. The build runs DIRECTLY
        /// here (this is a dialog-button callback, fired asynchronously outside the
        /// draw pass; the SpawnDeleteRouteConfirmation precedent) so a frame-reset
        /// deferred field would be clobbered at frame top before ApplyPendingActions
        /// reads it. After a build both caches are dirtied so the promoted run leaves
        /// the Candidates list and the new route's cells appear immediately.
        /// </summary>
        private void HandleCreateRouteChoice(RouteCandidate cand, LogisticsCreatePresentation.CreateRouteChoice choice)
        {
            if (!LogisticsCreatePresentation.ShouldBuild(choice))
            {
                ParsekLog.Verbose("UI",
                    $"Logistics: Create Route cancelled tree={ShortId(cand?.Tree?.Id)}");
                return;
            }

            Route r = CreateRouteFromCandidate(cand);
            bool activated = false;
            if (r != null && LogisticsCreatePresentation.ShouldActivate(choice))
                activated = RouteOrchestrator.TryActivate(r, TryGetCurrentUT());

            // Only dirty the caches when a route was actually created: the promoted run
            // then leaves the Candidates list and the new route's H1/H2/H3/H4 cells
            // appear immediately. On a builder reject (r == null) nothing changed, so a
            // forced recompute would be wasted work.
            if (r != null)
            {
                lastCandidateComputeRealtime = -1f;
                lastLegibilityComputeRealtime = -1f;
                // M6 Record-Supply-Run helper: the candidate was promoted to a
                // route, so a still-pending main-window banner for this tree is
                // stale - drop it.
                RouteRunPrompt.ClearPendingPromptIfTree(cand?.Tree?.Id, "route-created");
            }

            ParsekLog.Info("UI",
                $"Logistics: Create Route choice={choice} tree={ShortId(cand?.Tree?.Id)} result={(r != null ? "created" : "rejected")} activated={activated}");
        }

        /// <summary>
        /// The default dispatch interval a window-created route gets, resolved
        /// through the SAME span helper the Create Route dialog feeds into its
        /// <see cref="RouteBuilder.RouteCreationInputs.DispatchIntervalSeconds"/>
        /// (<see cref="RouteCreationDialog.ComputeRootToUndockSpan"/>): the rendered
        /// [root..dock] span (== the route's TransitDuration, the N=1 cadence floor),
        /// floored at 1.0. Routing both the window and the dialog through one helper
        /// guarantees a window create and a dialog create produce the identical
        /// interval for the same candidate analysis. Pure for unit testing; returns
        /// the helper's floor (1.0) when the candidate / analysis / tree is null.
        /// </summary>
        internal static double ResolveWindowCreateInterval(RouteCandidate candidate)
        {
            double interval = RouteCreationDialog.ComputeRootToUndockSpan(
                candidate?.Analysis, candidate?.Tree);
            ParsekLog.Verbose("UI",
                $"Logistics: window create interval resolved tree={ShortId(candidate?.Tree?.Id)} " +
                $"interval={interval.ToString("R", CultureInfo.InvariantCulture)}s");
            return interval;
        }

        /// <summary>
        /// Builds (and stores) a Paused route from a candidate via the single
        /// <see cref="RouteCreationService.CreatePausedFromCandidate"/> funnel and
        /// returns the built <see cref="Route"/> (or null on a null candidate or a
        /// builder reject). The H6 confirm callbacks call this to build, then the
        /// "Create and Activate" branch additionally calls
        /// <see cref="RouteOrchestrator.TryActivate"/> on the returned non-null
        /// route; returning the route keeps a single AddRoute +
        /// manual-loop-clear funnel and guarantees the window-created route's
        /// interval / geometry stays identical to today.
        ///
        /// <para>The build + store + manual-loop-clear sequence itself lives in
        /// <see cref="RouteCreationService"/> so the <c>RouteCommand action=create</c>
        /// seam verb drives the SAME production path a player click does rather than
        /// re-deriving it. What stays here is presentation: the interval resolve, the
        /// one-shot "manual loop turned off" toast, and this window's own grep-stable
        /// Info lines.</para>
        /// </summary>
        private Route CreateRouteFromCandidate(RouteCandidate candidate)
        {
            if (candidate?.Analysis == null || candidate.Tree == null)
            {
                ParsekLog.Warn("UI", "Logistics: Create Route - null candidate/analysis/tree, ignored");
                return null;
            }

            Game.Modes mode = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Mode
                : Game.Modes.SANDBOX;

            // Window-created routes use the SAME default interval the Create Route
            // dialog computes: the rendered [root..dock] span (== TransitDuration,
            // N=1 cadence floor). Both paths funnel through
            // RouteCreationDialog.ComputeRootToUndockSpan so a route created from
            // the window is identical (in interval) to one created from the dialog
            // for the same candidate analysis. Created Paused so the player verifies
            // via Send Once before turning on periodic dispatch.
            double interval = ResolveWindowCreateInterval(candidate);

            RouteCreationService.RouteCreateOutcome outcome =
                RouteCreationService.CreatePausedFromCandidate(
                    candidate,
                    name: null, // RouteBuilder generates a default name
                    intervalSeconds: interval,
                    mode: mode,
                    currentUT: TryGetCurrentUT());

            if (outcome.Route != null)
            {
                // M5: a manual loop was actually turned off by this create, so tell the
                // player with a one-shot toast (and the always-visible detail note). The
                // toast fires ONLY when something was cleared, never on every create.
                // Done DIRECTLY here in the synchronous create path (no PopupDialog
                // callback / frame-reset field), so the QW2 trap does not apply.
                if (LogisticsCreatePresentation.ShouldToastManualLoopCleared(outcome.ManualLoopsCleared))
                {
                    string treeName = ResolveMissionDisplayName(ResolveRouteSourceTreeId(outcome.Route));
                    string toast = LogisticsCreatePresentation.FormatManualLoopTurnedOffToast(treeName);
                    ParsekLog.ScreenMessage(toast, 5f);
                    ParsekLog.Info("UI",
                        $"Logistics: manual loop turned off by create route={ShortId(outcome.Route.Id)} tree='{treeName}' cleared={outcome.ManualLoopsCleared.ToString(CultureInfo.InvariantCulture)} (toast posted)");
                }
                ParsekLog.Info("UI",
                    $"Logistics: Create Route from candidate tree={ShortId(candidate.Tree.Id)} -> route={ShortId(outcome.Route.Id)} name='{outcome.Route.Name}' (Paused, interval={interval.ToString("R", CultureInfo.InvariantCulture)}s)");
                return outcome.Route;
            }

            ParsekLog.Info("UI",
                $"Logistics: Create Route rejected tree={ShortId(candidate.Tree.Id)} reason={outcome.RejectReason ?? "<none>"}");
            return null;
        }

        // ------------------------------------------------------------------
        // Candidate cache (throttled)
        // ------------------------------------------------------------------

        private List<RouteCandidate> GetCandidates()
        {
            float now = Time.realtimeSinceStartup;
            if (lastCandidateComputeRealtime < 0f
                || now - lastCandidateComputeRealtime >= CandidateRecomputeIntervalSeconds)
            {
                cachedCandidates = RouteCandidateFinder.DeriveCandidates();
                lastCandidateComputeRealtime = now;

                // Run-cost (Phase 3.4): recompute each candidate's net cost on this
                // ~1 Hz refresh and stash it by tree id so DrawCandidateRow reads it
                // off the draw path. Rebuilt wholesale each refresh (the candidate
                // set is small and can change), so stale tree ids never linger.
                candidateRunCostCache.Clear();
                int costed = 0;
                for (int i = 0; i < cachedCandidates.Count; i++)
                {
                    RouteCandidate cand = cachedCandidates[i];
                    string treeId = cand?.Tree?.Id;
                    if (cand?.Analysis == null || string.IsNullOrEmpty(treeId))
                        continue;
                    candidateRunCostCache[treeId] = ComputeCandidateRunCost(cand.Analysis, cand.Tree);
                    costed++;
                }
                ParsekLog.Verbose("UI",
                    $"Logistics candidate run-cost cache refreshed candidates={cachedCandidates.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"costed={costed.ToString(CultureInfo.InvariantCulture)}");

                // M6: rebuild the Dismissed-subsection display rows on the same
                // refresh (tree names resolved here, never on the draw path).
                // Sorted by label then id so the row order is stable across
                // HashSet iteration order changes.
                cachedDismissedRows.Clear();
                foreach (string dismissedId in RouteStore.DismissedCandidateTreeIds)
                    cachedDismissedRows.Add((dismissedId, ResolveTreeDisplayName(dismissedId)));
                cachedDismissedRows.Sort((a, b) =>
                {
                    int byLabel = string.CompareOrdinal(a.label, b.label);
                    return byLabel != 0 ? byLabel : string.CompareOrdinal(a.treeId, b.treeId);
                });
            }
            return cachedCandidates ?? new List<RouteCandidate>();
        }

        // M3: the throttled "recently committed trees not yet eligible" near-miss
        // list. Same ~1 Hz timer as GetCandidates (both derive from the same
        // committed trees through RouteAnalysisEngine), so the subsection never
        // scans live on the IMGUI draw path. Gate-safe: DeriveNearMisses reads only
        // RecordingTree.Recordings[].MergeState + RouteAnalysisEngine, no raw
        // committed-recording / ledger read.
        private List<RouteNearMiss> GetNearMisses()
        {
            float now = Time.realtimeSinceStartup;
            if (lastNearMissComputeRealtime < 0f
                || now - lastNearMissComputeRealtime >= CandidateRecomputeIntervalSeconds)
            {
                cachedNearMisses = RouteCandidateFinder.DeriveNearMisses();
                lastNearMissComputeRealtime = now;
            }
            return cachedNearMisses ?? new List<RouteNearMiss>();
        }

        // ------------------------------------------------------------------
        // Per-route legibility cache (throttled): H1 next-delivery countdown,
        // H2 realized / cumulative delivery, the merged Status cell.
        // ------------------------------------------------------------------

        /// <summary>
        /// Recomputes the whole per-route legibility cache on the realtime timer
        /// (mirrors <see cref="GetCandidates"/>). For each committed route it builds:
        /// the H1 next-dock-crossing countdown (throttled <see cref="RouteOrchestrator"/>
        /// accessor + the wait-state retry fallback branch), and the H2/H3 realized /
        /// cumulative delivery summary from a SINGLE ELS scan of the route's
        /// <c>RouteCargoDelivered</c> rows (H3 reuses H2's scan). Emits one batch-summary
        /// Verbose line after the pass (route count + how many had deliveries), never
        /// per route. Called once per frame at the top of <see cref="DrawWindow"/>; the
        /// timer / dirty-flag gate keeps the LoopUnit build + ledger scan to ~1 Hz.
        /// </summary>
        private void RefreshLegibilityCacheIfDue(IReadOnlyList<Route> routes, double currentUT)
        {
            float now = Time.realtimeSinceStartup;
            if (lastLegibilityComputeRealtime >= 0f
                && now - lastLegibilityComputeRealtime < LegibilityRecomputeIntervalSeconds)
                return;
            lastLegibilityComputeRealtime = now;

            legibilityCache.Clear();
            // M6: prune the Send-Once provenance set to routes that still exist and are
            // still armed, so a disarmed/deleted route never leaks a stale entry. Only
            // allocate when the set is non-empty (the common case is empty).
            bool pruneArmed = sendOnceArmedRouteIds.Count > 0;
            HashSet<string> stillArmed = pruneArmed ? new HashSet<string>() : null;
            int routeCount = routes?.Count ?? 0;
            int withCountdown = 0;
            int withDeliveries = 0;
            int withHold = 0;
            int withFlow = 0;
            for (int i = 0; i < routeCount; i++)
            {
                Route route = routes[i];
                if (route == null || string.IsNullOrEmpty(route.Id)) continue;
                if (pruneArmed && route.PauseAfterCurrentCycle)
                    stillArmed.Add(route.Id);

                RouteLegibility leg = ComputeRouteLegibility(route, currentUT);
                legibilityCache[route.Id] = leg;
                if (leg.CountdownBranch != LogisticsCountdownPresentation.CountdownBranch.None)
                    withCountdown++;
                if (leg.HasDeliveries)
                    withDeliveries++;
                // M6 hold reasons: batch counter, one summary line below.
                if (leg.HoldText != null)
                    withHold++;
                // M6 per-cycle flow: batch counter, same summary line.
                if (leg.FlowLines != null)
                    withFlow++;
            }
            if (pruneArmed)
                sendOnceArmedRouteIds.RemoveWhere(id => !stillArmed.Contains(id));

            ParsekLog.Verbose("UI",
                $"Logistics legibility cache refreshed routes={routeCount.ToString(CultureInfo.InvariantCulture)} " +
                $"withCountdown={withCountdown.ToString(CultureInfo.InvariantCulture)} " +
                $"withDeliveries={withDeliveries.ToString(CultureInfo.InvariantCulture)} " +
                $"withHold={withHold.ToString(CultureInfo.InvariantCulture)} " +
                $"withFlow={withFlow.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Builds one route's legibility values: the countdown (next dock crossing, next
        /// launch window, or the held route's recheck) and its Next cell / hover / detail
        /// line, the read-only Every cell, one ledger scan for realized deliveries (the
        /// dated Last delivered line and the Advanced Recent runs), the destination and
        /// from/to names, the run cost, the dated hold and partial-delivery lines, and the
        /// merged Status cell with its sentence. Every date goes through
        /// <see cref="ReservationExplanation.DefaultDateFormatter"/>.
        /// </summary>
        private RouteLegibility ComputeRouteLegibility(Route route, double currentUT)
        {
            var leg = new RouteLegibility();
            System.Func<double, string> formatDate = ReservationExplanation.DefaultDateFormatter;
            bool sendingOnce = IsSendingOnce(route);
            bool pausingAfterRun = IsPausingAfterRun(route);
            bool inPausedTable = LogisticsRoutePresentation.BelongsInPausedTable(route.Status, sendingOnce);

            // Countdown: next dock crossing (read-only; the LoopUnit build stays behind the
            // allowlisted RouteOrchestrator accessor), or the basis-aware next launch window
            // for the windowed bases the flat helper refuses. Both accessors share the
            // signature-cached LoopUnit resolve, so this stays a ~1 Hz cost.
            bool hasCrossing = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, currentUT, out double secondsToCrossing);
            bool hasWindow = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                route, currentUT, out double secondsToWindow,
                out RouteWindowBasis basis, out string targetBody);
            leg.Basis = basis;
            leg.BasisLabel = RouteWindowBasisPresentation.BasisLabel(basis, targetBody);
            LogisticsCountdownPresentation.CountdownDecision countdown =
                LogisticsCountdownPresentation.ResolveDetailCountdown(
                    route.Status, route.NextEligibilityCheckUT,
                    hasCrossing, secondsToCrossing,
                    hasWindow, secondsToWindow, currentUT);
            leg.CountdownBranch = countdown.Branch;
            leg.CountdownSeconds = countdown.Seconds;
            // A run is scheduled unless the route sits in the Paused table without a Send
            // arm: a Send-armed route keeps its countdown to the run it is about to make.
            leg.RunScheduled = !(inPausedTable && !sendingOnce);
            double nextUT = countdown.Branch != LogisticsCountdownPresentation.CountdownBranch.None
                ? currentUT + countdown.Seconds
                : double.NaN;

            // The read-only Every cell (Basic, and a Send-armed row in either mode).
            bool windowed = RouteWindowBasisPresentation.IsWindowedBasis(basis);
            leg.EveryText = LogisticsRoutePresentation.FormatEveryReadOnly(
                windowed, route.CadenceMultiplier, FormatDuration(route.DispatchInterval));
            leg.EveryTooltip = LogisticsRoutePresentation.EveryTooltip(windowed, leg.BasisLabel);

            // Realized deliveries: one ELS scan -> the latest run, its shortfall flag and
            // the cumulative total. The SAME walk collects the debit / pickup / delivery
            // rows the Advanced Recent runs lines bucket below (no second ledger walk).
            var flowRows = new List<LogisticsFlowPresentation.FlowRow>();
            LogisticsDeliveryPresentation.RouteDeliverySummary summary =
                CollectRouteDeliverySummary(route.Id, flowRows);
            leg.HasDeliveries = summary.HasAny;
            leg.LastCycleText = summary.HasAny
                ? LogisticsDeliveryPresentation.FormatRealizedDelivery(summary.LastRequested, summary.LastActual)
                : null;
            leg.LastCycleShortfall = summary.HasAny
                && LogisticsDeliveryPresentation.HasShortfall(summary.LastRequested, summary.LastActual);
            leg.CumulativeText = LogisticsDeliveryPresentation.FormatCumulativeTotal(summary.CumulativeTotal);
            leg.LastDeliveredLine = LogisticsRoutePresentation.FormatLastDeliveredLine(
                summary.HasAny, summary.LastUt, leg.LastCycleText, leg.CumulativeText,
                route.CompletedCycles, formatDate);

            // Recent runs (Advanced): bucket the collected rows by run and render the
            // bounded newest-first lines HERE (endpoint name resolution touches
            // FlightGlobals, never the IMGUI draw path). Null when there are none.
            leg.FlowLines = BuildPerCycleFlowLines(route, flowRows, currentUT);

            // Destination name (the Delivers line, the from/to line and the sort key). It
            // touches FlightGlobals and, on an unresolved surface endpoint, scans all
            // vessels; the resolved Vessel feeds the capacity probe below so there is no
            // second O(vessels) scan.
            ResolveDestinationCell(route, out string destText, out string destTooltip, out Vessel destVessel,
                out Vessel[] stopVessels, out string[] stopTexts);
            leg.DestinationText = destText;
            leg.DestinationTooltip = destTooltip;
            // The row and the detail name places, never raw coordinates: an unresolved
            // single-stop destination or depot origin reads "Kerbin (surface)", and the
            // coordinates ride the from/to hover.
            string destShort = destVessel == null && route.Stops != null && route.Stops.Count == 1
                && route.Stops[0] != null
                ? FormatEndpointPlace(route.Stops[0].Endpoint)
                : destText;
            leg.OriginShort = LogisticsRoutePresentation.FormatOriginShort(
                route.IsKscOrigin, route.IsHarvestOrigin,
                route.IsKscOrigin || route.IsHarvestOrigin
                    ? null
                    : (TryResolveLiveVesselName(route.Origin.VesselPersistentId) ?? FormatEndpointPlace(route.Origin)));
            leg.FromToText = LogisticsRoutePresentation.FormatFromTo(leg.OriginShort, destShort);
            leg.FromToTooltip = "From " + FormatOrigin(route) + " to " + destText
                + (string.IsNullOrEmpty(destTooltip) ? string.Empty : " (" + destTooltip + ")") + ".";
            leg.DeliversCellText = FormatRouteDelivery(route);
            leg.DeliversLine = LogisticsRoutePresentation.FormatDeliversEachRun(leg.DeliversCellText, destShort);

            if (route.Status == RouteStatus.DestinationFull)
                leg.CapacityContext = ResolveCapacityContext(route, destVessel, destText, stopVessels, stopTexts);

            // Run cost (Career + KSC origin): SumRecoveredCredits is an O(actions) ELS scan
            // (ComputeELS is memoized, so no second ledger walk).
            leg.RunCost = ComputeRouteRunCost(route);

            // Last-partial-delivery report, dated. Not gated by ShouldDisplayHold: it
            // reports a past physical loss, which no status makes misleading; the
            // orchestrator clears it on the next full delivery.
            leg.PartialText = LogisticsHoldPresentation.FormatPartialDeliveryLine(
                route.LastPartialDeliverySummary, route.LastPartialDeliveryUT, formatDate);

            // The persisted last hold in player language. ShouldDisplayHold gates DISPLAY
            // only (persistence is unconditional): MissingSourceRecording / SourceChanged
            // rows suppress an older hold that would mislead. The detail line and the
            // Status hover carry the dated long clause; the cell carries the compact one.
            if (LogisticsHoldPresentation.ShouldDisplayHold(route.Status, route.LastHoldKind))
            {
                leg.HoldShort = LogisticsHoldPresentation.DescribeHold(
                    route.LastHoldKind, route.LastHoldDetail, route.LastHoldShortfall);
                leg.HoldText = LogisticsHoldPresentation.FormatHoldDetailLine(
                    leg.HoldShort, route.LastHoldUT, formatDate);
                leg.HoldCellText = LogisticsHoldPresentation.StatusCellText(
                    route.LastHoldKind, route.LastHoldDetail, route.LastHoldShortfall);
            }

            // The merged Status cell + its sentence, and the Next cell (warned when the
            // last run was held: the countdown may well be held again).
            bool originLost = route.Status == RouteStatus.EndpointLost
                && route.LastHoldDetail != null
                && route.LastHoldDetail.StartsWith("origin-", System.StringComparison.Ordinal);
            leg.Status = LogisticsRoutePresentation.ClassifyStatus(
                route.Status, sendingOnce, pausingAfterRun, leg.HoldCellText,
                route.CompletedCycles, originLost);
            leg.StatusTooltip = LogisticsRoutePresentation.StatusTooltip(
                leg.Status, route.Status, leg.HoldText, route.CompletedCycles,
                route.CreatedUT, nextUT, originLost, formatDate);
            bool warned = leg.Status.Word == LogisticsRoutePresentation.StatusWord.Held;
            leg.NextCellText = LogisticsRoutePresentation.FormatNextCell(
                countdown.Branch, countdown.Seconds, leg.RunScheduled, warned);
            leg.NextTooltip = LogisticsRoutePresentation.FormatNextTooltip(
                countdown.Branch, nextUT, leg.RunScheduled, warned, formatDate);
            leg.NextLine = LogisticsRoutePresentation.FormatNextLine(
                countdown.Branch, nextUT, leg.RunScheduled,
                route.TransitDuration > 0.0 ? ParsekTimeFormat.FormatDuration(route.TransitDuration) : null,
                formatDate);

            // The classification is logged once per refresh per route, and at once when
            // its word changes (the word is in the rate key).
            ParsekLog.VerboseRateLimited("UI", "route-status-" + route.Id + "-" + leg.Status.Word,
                $"Logistics route status route={ShortId(route.Id)} word={leg.Status.Word} " +
                $"table={(inPausedTable ? "paused" : "active")} sendingOnce={sendingOnce} " +
                $"pausing={pausingAfterRun} scheduled={leg.RunScheduled}",
                30.0);

            return leg;
        }

        /// <summary>
        /// Computes one route's per-run net funds cost
        /// (<see cref="RouteRunCostCalculator.RouteRunCost"/>) for the legibility
        /// cache. This is the one non-pure piece (it needs the live
        /// <see cref="EffectiveState.ComputeELS"/> and the live Career probe), so it
        /// stays in the window file and feeds the pure calculator + presentation
        /// helpers; called only on the ~1 Hz refresh, never per IMGUI frame.
        ///
        /// <para>Career is probed with the same defensively-wrapped shape as
        /// <see cref="LiveRouteRuntimeEnvironment.IsCareer"/>
        /// (<c>HighLogic.CurrentGame.Mode == Game.Modes.CAREER</c>). ComputeELS is
        /// wrapped in a try/catch (it can throw during early load before the scenario
        /// module is published) and treated as no recoveries on failure, mirroring
        /// <see cref="CollectRouteDeliverySummary"/>. The tree-member id set is
        /// resolved once per route per refresh via
        /// <see cref="RouteRunCostCalculator.ResolveTreeRecordingIds(Route)"/>.</para>
        /// </summary>
        private static RouteRunCostCalculator.RouteRunCost ComputeRouteRunCost(Route route)
        {
            bool isCareer;
            try
            {
                isCareer = HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics run-cost: IsCareer probe threw {ex.GetType().Name}: {ex.Message}; defaulting false");
                isCareer = false;
            }

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics run-cost: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as no recoveries");
                els = null;
            }

            HashSet<string> treeRecordingIds = RouteRunCostCalculator.ResolveTreeRecordingIds(route);
            return RouteRunCostCalculator.Compute(route, isCareer, els, treeRecordingIds);
        }

        /// <summary>
        /// Computes a CANDIDATE's per-run net funds cost (Phase 3.2 / 3.4): the
        /// route-creation summary and the candidate row run before any
        /// <see cref="Route"/> exists, so this derives the inputs from the
        /// candidate's source recording + owning tree instead of a Route. KSC origin
        /// is decided exactly as <c>RouteBuilder</c> decides the built
        /// <see cref="Route.IsKscOrigin"/>: from the tree ROOT recording
        /// (<c>LaunchSiteName</c> set AND <c>StartBodyName == "Kerbin"</c>), NOT from
        /// the dock-child <c>analysis.SourceRecording</c> (which carries a null
        /// launch site), via <see cref="RouteRunCostCalculator.IsCandidateKscOrigin"/>.
        /// Career and ELS are probed with the same defensive wrap + memoized
        /// <see cref="EffectiveState.ComputeELS"/> as the route path. Returns a
        /// not-applicable / unknown cost (which the UI then suppresses) when the
        /// analysis or source recording is null. Called only off the IMGUI draw path
        /// (dialog spawn + the ~1 Hz candidate cache), never per frame.
        /// </summary>
        internal static RouteRunCostCalculator.RouteRunCost ComputeCandidateRunCost(
            RouteAnalysisResult analysis, RecordingTree tree)
        {
            Recording source = analysis?.SourceRecording;

            bool isCareer;
            try
            {
                isCareer = HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics candidate run-cost: IsCareer probe threw {ex.GetType().Name}: {ex.Message}; defaulting false");
                isCareer = false;
            }

            // KSC origin: derived from the tree ROOT recording, exactly as
            // RouteBuilder derives the built Route.IsKscOrigin. The launch-site /
            // start-body info lives on the first recording of the flight (the
            // tree root), NOT on analysis.SourceRecording: that child started
            // mid-flight at the dock and carries LaunchSiteName == null, so a
            // source-only check would suppress the cost block on the common
            // docking case (G5 / D2). The helper falls back to the source only
            // when the tree has no resolvable root (legacy single-recording).
            bool isKscOrigin = RouteRunCostCalculator.IsCandidateKscOrigin(source, tree);

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics candidate run-cost: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as no recoveries");
                els = null;
            }

            return RouteRunCostCalculator.ComputeForCandidate(
                source, tree, isCareer, isKscOrigin, els,
                LiveRouteRuntimeEnvironment.LookupPartCost,
                LiveRouteRuntimeEnvironment.LookupResourceUnitCost);
        }

        /// <summary>
        /// Builds the M4 DestinationFull free-capacity context line for the legibility
        /// cache. Reads the route's first stop's <see cref="RouteStop.DeliveryManifest"/>
        /// (the requested amounts) and, for each resource, the LIVE free capacity on the
        /// resolved destination <paramref name="destVessel"/> via a
        /// <see cref="LiveDeliveryCapacityProbe"/> constructed with the same
        /// <see cref="RouteOrchestrator.EndpointStoreIsLiveParts(Vessel)"/> gate the orchestrator uses (so the reported
        /// number matches what a real delivery would fill). When the vessel could not be
        /// resolved or the manifest is empty, the entry list is empty and the pure
        /// <see cref="LogisticsDeliveryPresentation.FormatCapacityContext"/> renders a
        /// "(capacity unknown)" line. This is the one non-pure piece of M4 (it touches a
        /// live Vessel), so it stays in the window file and runs only on the ~1 Hz
        /// refresh; the draw path reads the cached string. Logs the probe decision
        /// (rate-limited per route).
        /// </summary>
        private string ResolveCapacityContext(Route route, Vessel destVessel, string destName,
            Vessel[] stopVessels, string[] stopTexts)
        {
            var entries = new List<LogisticsDeliveryPresentation.CapacityEntry>();
            Dictionary<string, double> manifest;
            int capacityStop = 0;
            int fullStop = -1;
            if (route?.Stops != null && route.Stops.Count > 1 && stopVessels != null && stopTexts != null)
            {
                // GUI-P20: name and probe the stop the capacity gate refuses (its
                // requested amounts include earlier stops delivering to the same
                // vessel, as the gate counts them); when every stop fits now, fall
                // back to the stop the Destination cell names.
                fullStop = FindFullStopIndex(route, stopVessels);
                capacityStop = fullStop >= 0
                    ? fullStop
                    : LogisticsDeliveryPresentation.ResolveDestinationStopIndex(route.Stops);
                if (capacityStop >= 0)
                {
                    destVessel = stopVessels[capacityStop];
                    destName = stopTexts[capacityStop];
                    Vessel target = destVessel;
                    manifest = LogisticsDeliveryPresentation.CombineRequestedForFullStop(
                        route.Stops, capacityStop,
                        i => target != null && stopVessels[i] != null
                            && stopVessels[i].persistentId == target.persistentId);
                }
                else
                {
                    destVessel = null;
                    manifest = null;
                }
            }
            else
            {
                manifest = route?.Stops != null && route.Stops.Count > 0 && route.Stops[0] != null
                    ? route.Stops[0].DeliveryManifest
                    : null;
            }

            if (destVessel != null && manifest != null && manifest.Count > 0)
            {
                bool destinationIsLoaded = RouteOrchestrator.EndpointStoreIsLiveParts(destVessel);
                var probe = new LiveDeliveryCapacityProbe(destVessel, destinationIsLoaded);
                foreach (KeyValuePair<string, double> kv in manifest)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    double free = probe.ProbeResourceFreeCapacity(kv.Key);
                    entries.Add(new LogisticsDeliveryPresentation.CapacityEntry(kv.Key, kv.Value, free));
                }
            }

            ParsekLog.VerboseRateLimited("UI", "dest-capacity-" + route.Id,
                $"Logistics: capacity context route={ShortId(route.Id)} dest='{destName}' " +
                $"resolved={(destVessel != null ? "true" : "false")} resources={entries.Count.ToString(CultureInfo.InvariantCulture)}" +
                (route.Stops != null && route.Stops.Count > 1
                    ? $" stop={(capacityStop + 1).ToString(CultureInfo.InvariantCulture)}/{route.Stops.Count.ToString(CultureInfo.InvariantCulture)} gateFull={(fullStop >= 0 ? "true" : "false")}"
                    : string.Empty),
                5.0);

            return LogisticsDeliveryPresentation.FormatCapacityContext(destName, entries);
        }

        /// <summary>
        /// Scans the effective ledger state (<see cref="EffectiveState.ComputeELS"/>,
        /// gate-safe and already tombstone-filtered) for this route's
        /// <see cref="GameActionType.RouteCargoDelivered"/> rows and reduces them to a
        /// <see cref="LogisticsDeliveryPresentation.RouteDeliverySummary"/> (latest
        /// cycle + cumulative total). Mirrors
        /// <c>RouteOrchestrator.IsDeliveryAlreadyInLedger</c>'s scan idiom and
        /// catch-as-empty convention. Matches rows by RouteId only (all cycles), NOT by
        /// cycle id. This is the one non-pure piece of H2/H3 (it needs a live Scenario
        /// for ComputeELS), so it stays in the window file and feeds the pure
        /// summary/format helpers; called only on the ~1 Hz cache refresh.
        /// M6 per-cycle flow: the SAME single walk also fills
        /// <paramref name="flowRows"/> (debit / pickup / delivery rows bucketed
        /// later by cycle) via <see cref="LogisticsFlowPresentation.CollectRows"/>,
        /// so the flow display adds NO second ledger walk; pass null to skip.
        /// </summary>
        private static LogisticsDeliveryPresentation.RouteDeliverySummary CollectRouteDeliverySummary(
            string routeId, List<LogisticsFlowPresentation.FlowRow> flowRows)
        {
            var rows = new List<LogisticsDeliveryPresentation.DeliveryRow>();
            if (string.IsNullOrEmpty(routeId))
                return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics delivery scan: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as no deliveries");
                return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);
            }

            LogisticsFlowPresentation.CollectRows(els, routeId, rows, flowRows);
            return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);
        }

        /// <summary>
        /// Builds the M6 per-cycle flow lines for the legibility cache from the
        /// rows collected in the shared ELS walk. This is the non-pure half (it
        /// resolves live vessel names), so it stays in the window file and runs
        /// only on the ~1 Hz refresh: endpoint pids on debit / pickup rows
        /// resolve through the O(1) <c>FlightGlobals.FindVessel</c> (the
        /// RouteEndpointResolver.ResolveByPid idiom - a vanished vessel misses
        /// the map and the pure formatter renders its "vessel pid=N" fallback,
        /// never blank), and per-stop delivery destinations resolve to the live
        /// vessel name by the stop endpoint's baked pid with the recorded
        /// coords as the fallback (the H4 name-else-coords contract, without
        /// the O(vessels) surface re-scan). The pure
        /// <see cref="LogisticsFlowPresentation.FormatPerCycleFlow"/> does the
        /// bucketing / bounding / wording. Returns null when there is nothing
        /// to show so the detail panel draws no header.
        /// </summary>
        private static List<LogisticsFlowPresentation.CycleFlowLine> BuildPerCycleFlowLines(
            Route route, List<LogisticsFlowPresentation.FlowRow> flowRows, double currentUT)
        {
            if (flowRows == null || flowRows.Count == 0)
                return null;

            // Live names for the endpoint pids that appear on the rows.
            var endpointNames = new Dictionary<uint, string>();
            for (int i = 0; i < flowRows.Count; i++)
            {
                uint pid = flowRows[i].EndpointPid;
                if (pid == 0u || endpointNames.ContainsKey(pid)) continue;
                string name = TryResolveLiveVesselName(pid);
                if (!string.IsNullOrEmpty(name))
                    endpointNames[pid] = name;
            }

            // Per-stop delivery destination names, indexed by stop index.
            List<string> stopDestinations = null;
            if (route?.Stops != null && route.Stops.Count > 0)
            {
                stopDestinations = new List<string>(route.Stops.Count);
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    RouteStop stop = route.Stops[i];
                    if (stop == null)
                    {
                        stopDestinations.Add(null);
                        continue;
                    }
                    string name = TryResolveLiveVesselName(stop.Endpoint.VesselPersistentId);
                    string destination = !string.IsNullOrEmpty(name)
                        ? name
                        : LogisticsDeliveryPresentation.FormatEndpointCoords(stop.Endpoint);
                    destination += RouteCreationFormatters.ConnectionKindSuffix(stop.ConnectionKind);
                    stopDestinations.Add(destination);
                }
            }

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    flowRows, endpointNames, FormatOrigin(route), stopDestinations,
                    currentUT, LogisticsFlowPresentation.MaxCyclesShown);
            return lines.Count > 0 ? lines : null;
        }

        /// <summary>
        /// Live vessel name by persistent id, or null when the pid is 0 / the
        /// vessel is gone / FlightGlobals is unavailable. O(1) per pid
        /// (FlightGlobals.PersistentVesselIds lookup); defensively wrapped like
        /// <c>RouteEndpointResolver.ResolveByPid</c> so a stock-side teardown
        /// null-deref degrades to the pid fallback instead of a crash.
        /// </summary>
        private static string TryResolveLiveVesselName(uint pid)
        {
            if (pid == 0u) return null;
            try
            {
                if (FlightGlobals.fetch != null
                    && FlightGlobals.FindVessel(pid, out Vessel found)
                    && found != null)
                    return found.vesselName;
            }
            catch
            {
                // Best-effort name only; the formatter's pid fallback covers a miss.
            }
            return null;
        }

        /// <summary>
        /// Reads a route's cached legibility values by id. Returns a default
        /// (no-countdown / no-deliveries / grey "-" Status)
        /// struct when the route is absent from the cache (e.g. the very first frame
        /// before the timer has fired). The draw path never recomputes; it only reads
        /// here.
        /// </summary>
        private RouteLegibility GetLegibility(Route route)
        {
            if (route != null && !string.IsNullOrEmpty(route.Id)
                && legibilityCache.TryGetValue(route.Id, out RouteLegibility leg))
                return leg;
            // Cache miss (route not yet refreshed this cycle, or a null/empty id the
            // refresh loop skipped): an explicit "unknown" struct. The Status cell reads a
            // grey "-", never a green Delivering or a cyan New it has not earned, and Next
            // reads "-", until the cache fills.
            return new RouteLegibility
            {
                DestinationText = "-",
                FromToText = "-",
                NextCellText = "-",
                Status = new LogisticsRoutePresentation.StatusCell
                {
                    Word = LogisticsRoutePresentation.StatusWord.Paused,
                    Text = "-",
                    Color = ParsekUI.StatusColorKind.Grey
                },
            };
        }

        // ------------------------------------------------------------------
        // L2: cached-sorted route tables. Re-sort a section only when its row
        // count changes, the sort key/direction changes, or the throttled
        // legibility cache was refreshed (the NextDelivery / Destination sort
        // keys live in that cache). Never per IMGUI frame.
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the cached, sorted list for one route section, re-sorting only when
        /// the section's row count, the shared sort column/direction, or the legibility
        /// stamp changed since the last sort (the SpawnControlUI cached-sorted idiom,
        /// one cache per section). The sort keys for Origin / Destination / NextDelivery
        /// / Status / Delivery are projected from the throttled legibility cache through
        /// <see cref="BuildRouteSortKeys"/>, so the pure comparer never recomputes them.
        /// Logs the decision (the column / direction) once per actual re-sort, NOT per
        /// frame, via a Verbose line.
        /// </summary>
        private List<Route> GetSortedRoutesForSection(List<Route> rows, RouteSection section)
        {
            bool active = section == RouteSection.Active;
            int rowCount = rows?.Count ?? 0;
            int cachedCount = active ? cachedActiveCount : cachedPausedCount;
            float cachedStamp = active ? cachedActiveLegibilityStamp : cachedPausedLegibilityStamp;
            // The column the tables sort by this pass: a sort on a column with no header in
            // the current mode (Runs in Basic) reads as a Route-name sort; the stored
            // routeSortColumn is untouched, so Advanced gets it back.
            LogisticsRouteSortColumn effectiveColumn =
                LogisticsRoutePresentation.EffectiveSortColumn(routeSortColumn, drawTuning);

            bool sortStateChanged = effectiveColumn != cachedRouteSortColumn
                || routeSortAscending != cachedRouteSortAscending
                || cachedStamp != lastLegibilityComputeRealtime;

            if (rowCount != cachedCount || sortStateChanged)
            {
                Dictionary<string, RouteSortKeys> keys = BuildRouteSortKeys(rows);
                List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                    rows, effectiveColumn, routeSortAscending, keys);

                if (active)
                {
                    cachedSortedActive = sorted;
                    cachedActiveCount = rowCount;
                    cachedActiveLegibilityStamp = lastLegibilityComputeRealtime;
                }
                else
                {
                    cachedSortedPaused = sorted;
                    cachedPausedCount = rowCount;
                    cachedPausedLegibilityStamp = lastLegibilityComputeRealtime;
                }
                // The sort column/direction is genuinely shared (both sections sort the
                // same way); a header click resets BOTH counts so both re-sort. The
                // legibility freshness is per-section (stamped above) so a ~1 Hz refresh
                // re-sorts each section independently.
                cachedRouteSortColumn = effectiveColumn;
                cachedRouteSortAscending = routeSortAscending;

                ParsekLog.Verbose("UI",
                    $"Logistics route sort applied section={section} " +
                    $"column={effectiveColumn} stored={routeSortColumn} ascending={routeSortAscending} " +
                    $"rows={rowCount.ToString(CultureInfo.InvariantCulture)}");
            }

            return active ? cachedSortedActive : cachedSortedPaused;
        }

        /// <summary>
        /// Projects the per-route sort keys (Origin / Destination / NextDelivery /
        /// Status / Delivery display values) from the throttled legibility cache into a
        /// plain dictionary the pure <see cref="LogisticsSortPresentation.SortRoutes"/>
        /// comparer reads. Built only when a section actually re-sorts (throttled), not
        /// per frame. Origin and Status come from the same pure formatters the cells
        /// render, so a column sorts by exactly what the player sees.
        /// </summary>
        private Dictionary<string, RouteSortKeys> BuildRouteSortKeys(List<Route> rows)
        {
            var keys = new Dictionary<string, RouteSortKeys>();
            if (rows == null) return keys;
            for (int i = 0; i < rows.Count; i++)
            {
                Route route = rows[i];
                if (route == null || string.IsNullOrEmpty(route.Id)) continue;

                RouteLegibility leg = GetLegibility(route);
                // Next sorts by the countdown only where the cell shows one (a Paused
                // route that is not Send-armed reads "-" and sorts with the dashes).
                bool hasNext = leg.RunScheduled && leg.CountdownBranch
                    != LogisticsCountdownPresentation.CountdownBranch.None;
                // Status sorts by exactly what the merged cell shows; the retired
                // Delivery column's sort member (kept so the seam's sort vocabulary still
                // parses) aliases it.
                string statusText = LogisticsRoutePresentation.StatusSortKey(leg.Status);

                keys[route.Id] = new RouteSortKeys
                {
                    OriginText = leg.OriginShort ?? FormatOrigin(route),
                    DestinationText = leg.DestinationText ?? string.Empty,
                    NextDeliverySeconds = leg.CountdownSeconds,
                    HasNextDelivery = hasNext,
                    StatusText = statusText,
                    DeliveryText = statusText
                };
            }
            return keys;
        }

        /// <summary>
        /// Resolves the H4 Destination cell strings for the legibility cache: the
        /// resolved destination vessel name as the text (coords in the hover tooltip),
        /// or the coords string as the text with no extra tooltip when the vessel
        /// cannot be resolved (e.g. it is unloaded / out of range). The live
        /// <see cref="RouteEndpointResolver.TryResolveEndpoint"/> call (which touches
        /// FlightGlobals and, on an unresolved surface endpoint, scans every vessel) is
        /// why this runs on the ~1 Hz cache refresh and NOT per IMGUI frame; the pure
        /// <see cref="LogisticsDeliveryPresentation.FormatDestinationDisplay"/> picks
        /// name-vs-coords. Logs the resolve decision (rate-limited per route).
        /// </summary>
        private void ResolveDestinationCell(Route route, out string text, out string tooltip,
            out Vessel resolvedVessel, out Vessel[] stopVessels, out string[] stopTexts)
        {
            resolvedVessel = null;
            stopVessels = null;
            stopTexts = null;
            if (route?.Stops != null && route.Stops.Count > 1)
            {
                ResolveMultiStopDestinationCell(route, out text, out tooltip,
                    out resolvedVessel, out stopVessels, out stopTexts);
                return;
            }
            if (route?.Stops == null || route.Stops.Count == 0 || route.Stops[0] == null)
            {
                text = "-";
                tooltip = string.Empty;
                return;
            }

            RouteEndpoint endpoint = route.Stops[0].Endpoint;
            string coords = LogisticsDeliveryPresentation.FormatEndpointCoords(endpoint);

            string resolvedName = null;
            bool resolved = RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel v, out string reason);
            if (resolved && v != null)
            {
                resolvedName = v.vesselName;
                // Surface the resolved Vessel so the M4 capacity probe reuses this one
                // resolve pass instead of a second O(vessels) surface scan.
                resolvedVessel = v;
            }

            text = LogisticsDeliveryPresentation.FormatDestinationDisplay(resolvedName, endpoint);
            // When the name resolved, the coords go to the tooltip; on the coords
            // fallback the cell already shows the coords, so no extra tooltip.
            tooltip = resolved ? coords : string.Empty;

            ParsekLog.VerboseRateLimited("UI", "dest-resolve-" + route.Id,
                resolved
                    ? $"Logistics: destination route={ShortId(route.Id)} resolved=true name='{resolvedName}'"
                    : $"Logistics: destination route={ShortId(route.Id)} resolved=false reason='{reason}' (showing coords)",
                5.0);
        }

        /// <summary>
        /// GUI-P20 multi-stop Destination cell: resolves EVERY stop's endpoint (the
        /// dispatch gate resolves every stop each tick too, so this is the same set of
        /// lookups at ~1 Hz), names the stop
        /// <see cref="LogisticsDeliveryPresentation.ResolveDestinationStopIndex"/> picks
        /// plus "(+N stops)", and puts the whole visit-ordered stop list with each
        /// stop's cargo direction in the cell's hover tooltip. The per-stop vessels and
        /// display texts are surfaced so the DestinationFull capacity line can name and
        /// probe the stop that is actually full without a second resolve pass.
        /// </summary>
        private void ResolveMultiStopDestinationCell(Route route, out string text, out string tooltip,
            out Vessel resolvedVessel, out Vessel[] stopVessels, out string[] stopTexts)
        {
            int count = route.Stops.Count;
            stopVessels = new Vessel[count];
            stopTexts = new string[count];
            var roles = new string[count];
            int resolvedCount = 0;
            for (int i = 0; i < count; i++)
            {
                RouteStop stop = route.Stops[i];
                if (stop == null) continue;
                string name = null;
                if (RouteEndpointResolver.TryResolveEndpoint(stop.Endpoint, out Vessel v, out _) && v != null)
                {
                    name = v.vesselName;
                    stopVessels[i] = v;
                    resolvedCount++;
                }
                stopTexts[i] = LogisticsDeliveryPresentation.FormatDestinationDisplay(name, stop.Endpoint);
                roles[i] = LogisticsDeliveryPresentation.StopRoleLabel(stop);
            }

            int shown = LogisticsDeliveryPresentation.ResolveDestinationStopIndex(route.Stops);
            resolvedVessel = shown >= 0 ? stopVessels[shown] : null;
            text = shown >= 0
                ? LogisticsDeliveryPresentation.FormatMultiStopDestination(
                    stopTexts[shown], LogisticsDeliveryPresentation.CountStops(route.Stops))
                : "-";
            tooltip = LogisticsDeliveryPresentation.FormatStopListTooltip(stopTexts, roles,
                TooltipEchoBox.BudgetChars(DefaultWindowWidth, TooltipEchoBox.SingleLine));

            ParsekLog.VerboseRateLimited("UI", "dest-resolve-" + route.Id,
                $"Logistics: destination route={ShortId(route.Id)} stops={count.ToString(CultureInfo.InvariantCulture)} " +
                $"resolved={resolvedCount.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} " +
                $"shownStop={(shown + 1).ToString(CultureInfo.InvariantCulture)} text='{text}'",
                5.0);
        }

        /// <summary>
        /// Replays the destination capacity gate over the already-resolved stop vessels
        /// to find which stop it refuses, so the DestinationFull capacity line names the
        /// stop that is actually full. Mirrors
        /// <c>LiveRouteRuntimeEnvironment.DestinationHasCapacity</c>: one
        /// <see cref="LiveDeliveryCapacityProbe"/> per resolved vessel pid, shared by every
        /// stop delivering there, and an unresolved stop fails open (null probe). Returns
        /// -1 when every stop fits now (capacity freed since the hold) or none resolved.
        /// </summary>
        private static int FindFullStopIndex(Route route, Vessel[] stopVessels)
        {
            if (route?.Stops == null || stopVessels == null)
                return -1;
            var probeByPid = new Dictionary<uint, IDeliveryCapacityProbe>();
            bool fits = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route,
                stopIndex =>
                {
                    Vessel v = stopIndex < stopVessels.Length ? stopVessels[stopIndex] : null;
                    if (v == null) return null;
                    if (probeByPid.TryGetValue(v.persistentId, out IDeliveryCapacityProbe cached))
                        return cached;
                    var probe = new LiveDeliveryCapacityProbe(v, RouteOrchestrator.EndpointStoreIsLiveParts(v));
                    probeByPid[v.persistentId] = probe;
                    return probe;
                },
                out _,
                out int fullStopIndex);
            return fits ? -1 : fullStopIndex;
        }

        // ------------------------------------------------------------------
        // Formatting helpers
        // ------------------------------------------------------------------

        private static double TryGetCurrentUT()
        {
            try
            {
                return Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private static double CandidateTransit(RouteCandidate candidate)
        {
            // CRE-2: the player-shown transit must match the route's ACTUAL span,
            // which the builder computes as [root.StartUT .. undockUT] (the full
            // rendered path), NOT the leaf dock-child span (src.EndUT - src.StartUT).
            // Reuse the single span helper so display and creation never diverge.
            if (candidate?.Analysis == null) return 0.0;
            return RouteCreationDialog.ComputeRootToUndockSpan(candidate.Analysis, candidate.Tree);
        }

        private static string FormatOrigin(Route route)
        {
            if (route.IsKscOrigin) return "KSC (funds)";
            // M2 harvest origin (plan D7): no origin vessel, nothing debited.
            if (route.IsHarvestOrigin) return "harvested en route";
            if (route.Origin.VesselPersistentId != 0u || !string.IsNullOrEmpty(route.Origin.BodyName))
                return FormatEndpointShort(route.Origin);
            return "-";
        }

        internal static string FormatCandidateOrigin(RouteAnalysisResult analysis, RecordingTree tree)
        {
            // Resolve origin off the tree ROOT (the launch) via the shared helper,
            // not the dock-child source: the source started mid-flight at the dock
            // and has no LaunchSiteName, so reading it directly mis-reports a KSC
            // route as "-". Keeps this cell in step with the dialog summary, the
            // default name, and the built Route.IsKscOrigin.
            RouteCreationFormatters.RouteOriginIdentity id =
                RouteCreationFormatters.ResolveOriginIdentity(analysis, tree);
            switch (id.Kind)
            {
                case RouteCreationFormatters.RouteOriginKind.Ksc:
                    return "KSC (funds)";
                case RouteCreationFormatters.RouteOriginKind.Depot:
                    return "depot " + RouteCreationFormatters.FormatDepotIdentity(id);
                case RouteCreationFormatters.RouteOriginKind.Harvest:
                    return "harvested en route";
                default:
                    return "-";
            }
        }

        private static string FormatEndpointShort(RouteEndpoint? ep)
        {
            if (!ep.HasValue) return "-";
            return FormatEndpointShort(ep.Value);
        }

        /// <summary>
        /// A place name without coordinates for the candidate Destination cell:
        /// "Kerbin (surface)" / "Mun (orbit)"; the coordinates ride the hover
        /// (<see cref="FormatEndpointShort(RouteEndpoint)"/>). "-" when unknown.
        /// </summary>
        internal static string FormatEndpointPlace(RouteEndpoint? ep)
        {
            if (!ep.HasValue || string.IsNullOrEmpty(ep.Value.BodyName)) return "-";
            return ep.Value.BodyName + (ep.Value.IsSurface ? " (surface)" : " (orbit)");
        }

        private static string FormatEndpointShort(RouteEndpoint ep)
        {
            if (string.IsNullOrEmpty(ep.BodyName)) return "-";
            string sit = ep.IsSurface ? "surface" : "orbit";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} ({1}) {2:F2},{3:F2}",
                ep.BodyName, sit, ep.Latitude, ep.Longitude);
        }

        // "Nx (~human)" — the cadence multiplier plus the resulting dispatch
        // interval as a human duration (Phase 6). When the span is unknown the
        // duration falls back to a dash from FormatDuration.
        internal static string FormatCadence(Route route)
        {
            int n = Route.ClampCadenceMultiplier(route.CadenceMultiplier);
            string human = FormatDuration(route.DispatchInterval);
            return string.Format(CultureInfo.InvariantCulture, "{0}x (~{1})", n, human);
        }

        internal static string FormatDuration(double seconds)
        {
            if (seconds <= 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return "-";
            if (seconds < 60.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F0}s", seconds);
            if (seconds < 3600.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}m", seconds / 60.0);
            // The day length follows the player's calendar setting (Kerbin 6 h or Earth 24 h),
            // and RouteCadence.ParseAndSnapInterval reads a typed "d" with the same length, so
            // the displayed interval round-trips through the edit field on either calendar.
            double secsPerDay = ParsekTimeFormat.SecsPerDay;
            if (seconds < secsPerDay)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}h", seconds / 3600.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}d", seconds / secsPerDay);
        }

        // "Delivers each run": every stop's delivery lands each run, so a
        // multi-stop route shows the summed manifest (GUI-P20); a single-stop route
        // renders exactly that stop's manifest, as before.
        private static string FormatRouteDelivery(Route route)
        {
            return LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(route?.Stops);
        }

        // L3: the manifest formatting moved to the pure
        // LogisticsDeliveryPresentation.FormatWouldDeliver so the Candidates section
        // "Would deliver" cell and the route / candidate detail lines share one
        // unit-tested formatter. This thin window-side forwarder keeps the existing
        // callers (route delivery, candidate detail) on the same text.
        private static string FormatManifest(
            Dictionary<string, double> resources,
            List<InventoryPayloadItem> inventory)
        {
            return LogisticsDeliveryPresentation.FormatWouldDeliver(resources, inventory);
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<none>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // The merged Status cell's wrapping style for a palette colour (null = the
        // Scheduled word's default white).
        private GUIStyle WrapStatusStyleFor(ParsekUI.StatusColorKind? color)
        {
            if (!color.HasValue) return wrapStatusWhite;
            switch (color.Value)
            {
                case ParsekUI.StatusColorKind.Green: return wrapStatusGreen;
                case ParsekUI.StatusColorKind.Yellow: return wrapStatusYellow;
                case ParsekUI.StatusColorKind.Red: return wrapStatusRed;
                case ParsekUI.StatusColorKind.Cyan: return wrapStatusCyan;
                case ParsekUI.StatusColorKind.Grey:
                default:
                    return wrapStatusGrey;
            }
        }

        private void EnsureStyles()
        {
            if (statusStyleGreen != null) return;

            // L4: the five status-text colors come from the one shared ParsekUI
            // palette (the house source) so the literals live in a single place; this
            // window keeps its own GUIStyle objects (no padding here, unlike the
            // Recordings table) and only the text color is centralized. detailStyle's
            // (0.8, 0.8, 0.8) is Logistics-only and stays a local literal.
            statusStyleGreen = new GUIStyle(GUI.skin.label);
            statusStyleGreen.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Green);
            statusStyleYellow = new GUIStyle(GUI.skin.label);
            statusStyleYellow.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Yellow);
            statusStyleRed = new GUIStyle(GUI.skin.label);
            statusStyleRed.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Red);
            statusStyleGrey = new GUIStyle(GUI.skin.label);
            statusStyleGrey.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Grey);
            statusStyleCyan = new GUIStyle(GUI.skin.label);
            statusStyleCyan.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Cyan);

            statusStyleWhite = new GUIStyle(GUI.skin.label);

            detailStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            detailStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

            routeNameStyle = new GUIStyle(GUI.skin.label) { wordWrap = false, clipping = TextClipping.Clip };
            routeFromToStyle = new GUIStyle(routeNameStyle);
            routeFromToStyle.normal.textColor = MissionsWindowUI.MissionSummaryTextColor;

            wrapCellStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            wrapStatusGreen = WrapOf(statusStyleGreen);
            wrapStatusYellow = WrapOf(statusStyleYellow);
            wrapStatusRed = WrapOf(statusStyleRed);
            wrapStatusGrey = WrapOf(statusStyleGrey);
            wrapStatusCyan = WrapOf(statusStyleCyan);
            wrapStatusWhite = WrapOf(statusStyleWhite);

            nextAmberStyle = new GUIStyle(GUI.skin.label);
            nextAmberStyle.normal.textColor = MissionsWindowUI.LoopPeriodClampColor;

            // Interact buttons: zero horizontal margin so a single starts exactly at the
            // column inset and a pair spans exactly one single (the Missions styles); the
            // pair halves take the compact 2 px text padding.
            interactButtonStyle = new GUIStyle(GUI.skin.button)
            {
                margin = new RectOffset(
                    0, 0, GUI.skin.button.margin.top, GUI.skin.button.margin.bottom)
            };
            interactPairButtonStyle = new GUIStyle(interactButtonStyle)
            {
                padding = new RectOffset(
                    2, 2, GUI.skin.button.padding.top, GUI.skin.button.padding.bottom)
            };
            GUIStyle columnHeader = parentUI.GetColumnHeaderStyle();
            interactHeaderContainerStyle = new GUIStyle(columnHeader)
            {
                margin = new RectOffset(0, 0, columnHeader.margin.top, columnHeader.margin.bottom)
            };
            interactHeaderLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal = { textColor = columnHeader.normal.textColor }
            };

            ParsekLog.Verbose("UI", "Logistics status styles built from shared ParsekUI palette");
        }

        private static GUIStyle WrapOf(GUIStyle style)
        {
            return new GUIStyle(style) { wordWrap = true };
        }
    }
}
