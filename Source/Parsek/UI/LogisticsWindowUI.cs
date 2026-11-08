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
    internal partial class LogisticsWindowUI
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
            List<NearMissGroup> groups = cachedNearMissGroups;
            for (int i = 0; groups != null && i < groups.Count; i++)
                keys.Add(NearMissGroupRowKey(groups[i]));
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
        /// opener. The click point only places the popup's first rect, and the seam has no
        /// cursor, so it passes none and the picker centres over this window.</summary>
        internal bool OpenLinkPickerForTesting(Logistics.Route source)
        {
            if (source == null) return false;
            OpenLinkPicker(source, PickerWindowLayout.NoClickPoint);
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

        // M3 near-miss cache: the "recently committed trees not yet eligible" list
        // (DeriveNearMisses walks every committed tree through RouteAnalysisEngine,
        // exactly like DeriveCandidates), throttled on the SAME ~1 Hz timer as the
        // candidate cache so the subsection never scans live on the IMGUI draw path.
        // The draw path only READS this cached list.
        private List<RouteNearMiss> cachedNearMisses = new List<RouteNearMiss>();
        private float lastNearMissComputeRealtime = -1f;
        // The near-miss list folded into one group per reason, rebuilt with the list above
        // (names numbered, header previews and hovers composed there, never on the draw path).
        private List<NearMissGroup> cachedNearMissGroups = new List<NearMissGroup>();

        // Per-candidate display text for the Route cell and the Would deliver cell,
        // keyed by tree id and rebuilt on the candidate refresh: the from/to line resolves
        // the destination vessel live, which must not run on the draw path.
        private sealed class CandidateDisplay
        {
            // The default name Create would give right now (the source mission's name,
            // numbered against the stored routes), resolved here because it reads the
            // mission and route stores.
            public string Name;
            public string FromTo;
            public string FromToTooltip;
            public string Cargo;
            // The destination's live vessel name, null when none answers; the create
            // dialog names the destination by it.
            public string DestinationName;
        }
        private readonly Dictionary<string, CandidateDisplay> candidateDisplayCache =
            new Dictionary<string, CandidateDisplay>();

        // M6 candidate intent helper: cached display rows for the collapsed
        // "Dismissed (N)" subsection, rebuilt on the SAME ~1 Hz timer as the
        // candidate cache (inside GetCandidates) so tree display names are never
        // resolved live on the IMGUI draw path. Sorted by label (then id) for a
        // stable row order across HashSet iteration.
        private readonly List<(string treeId, string label)> cachedDismissedRows =
            new List<(string treeId, string label)>();

        // Throttled per-route legibility cache (Phase 2 H1/H2/H3). Recomputing the
        // next-dock-crossing countdown (H1, a LoopUnit build) and the realized /
        // cumulative delivery summary (H2/H3, a route-event-log scan) is NOT free, and many
        // routes can be open at once, so we recompute the WHOLE dictionary on a
        // realtime timer (mirrors the candidate cache) once per ~1s rather than per
        // route per IMGUI frame. The draw path only READS the cache by route.Id.
        // In-memory instance state only: it is rebuilt from RouteStore + the route event log
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
        /// whose inputs are not free (a LoopUnit build for the countdown, an event-log scan for
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

            // Realized deliveries from the route event log: whether any, the latest run's line and
            // its shortfall tint, the cumulative total, and the dated "Last delivered" line.
            public bool HasDeliveries;
            public string LastCycleText;     // "delivered 40.0 of 150.0 LiquidFuel (110.0 did not fit)"
            public bool LastCycleShortfall;  // drives the yellow tint
            public string CumulativeText;    // "1240.0 LiquidFuel, 30.0 Oxidizer" or ""
            public string LastDeliveredLine;

            // The Route cell's second line ("KSC [U+2192] Depot Station Duna I") and its hover
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
            // The greyed Delivering... hover of a Send-armed run in flight (launch and
            // arrival dates); null otherwise.
            public string DeliveringTooltip;

            // The detail block's Update parts hover, dated with the route's last endpoint
            // part adoption when it has one.
            public string UpdatePartsTooltip;

            // Go to: a recording of the route's source mission still in the effective set
            // (what the Missions cross-link resolves from), or null when the mission is
            // gone and the button greys.
            public string GoToRecordingId;
        }

        // Deferred mutations: collected during the draw loop and applied after the
        // scroll view so we never mutate RouteStore.CommittedRoutes mid-iteration.
        private Route pendingPause;
        private Route pendingCancelSend;
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
        internal const float LinkPickerMinW = 260f;
        internal const float LinkPickerMinH = 200f;
        internal const float LinkPickerDefaultW = 360f;
        internal const float LinkPickerDefaultH = 320f;

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
        // The Every / Priority steppers' shared value-cell width (measured with detailStyle).
        private float stepperValueWidth = LogisticsRoutePresentation.StepperValueMinWidth;
        // The Every / Priority steppers' shared row height (a slot-button line's height).
        private float stepperRowHeight;
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
        // The Next countdown in the house countdown amber (ParsekUI.CountdownTextColor).
        private GUIStyle nextAmberStyle;
        // The Interact column: zero horizontal margin so a single and a pair line up on
        // every row (MissionsWindowUI's interactButtonStyle / interactPairButtonStyle), and
        // the centred header cell over it (the Missions colHdrCellContainerStyle shape).
        private GUIStyle interactButtonStyle;
        private GUIStyle interactPairButtonStyle;
        private GUIStyle interactHeaderContainerStyle;
        // The Interact grid's measured widths (EnsureStyles, once per style build): one
        // pair cell fits the widest grid label in the skin's pair-button style; the column
        // is two cells, the gap and the two insets; a detail-block single spans both cells
        // and the gap. Fields, not constants, because the label widths come from the skin;
        // the header, every row and every detail block read the same values.
        private float interactPairWidth = MissionsWindowUI.InteractPairButtonWidth;
        private float interactColumnWidth = MissionsWindowUI.ColW_Interact;
        private float InteractSingleWidth => 2f * interactPairWidth + MissionsWindowUI.InteractButtonGap;
        // The detail block's Interact singles (Rename / Delete / Update parts / Link
        // round-trip...): the zero-margin single, with the compact 2 px text padding so the
        // longest label fits the 100 px Missions single.
        private GUIStyle detailSlotButtonStyle;
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
        // The candidates table follows the route tables' grid: # | Route (expands; the
        // candidate name over a grey "Origin -> Destination" line) | Would deliver |
        // Transit | Interact. Transit takes the Every column's width and the Interact
        // column is the route tables' measured one, so a candidate's buttons sit under
        // the routes' Activate / Pause. Would deliver keeps its own wider cell (it carries
        // the net-cost suffix) and wraps.
        private const float ColW_WouldDeliver = 260f;

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
        // candidates table (556 px fixed) fits too.
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
                windowRect = ParsekUiScale.Window(
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
            // route; Active holds everything else (running, held, Send-armed while it
            // sends, pause-armed and hard-broken). A Send-armed route returns to Paused
            // when its run completes (the backend's PauseAfterCurrentCycle).
            var activeRoutes = new List<Route>();
            var pausedRoutes = new List<Route>();
            int routeCount = routes?.Count ?? 0;
            for (int i = 0; i < routeCount; i++)
            {
                Route r = routes[i];
                if (r == null) continue;
                if (LogisticsRoutePresentation.BelongsInPausedTable(r.Status))
                    pausedRoutes.Add(r);
                else
                    activeRoutes.Add(r);
            }

            // Reset deferred actions for this frame.
            pendingPause = null;
            pendingCancelSend = null;
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
            pendingSectionToggle = null;

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
            wideScroll.BeginContentFloor(ContentFloorReservedWidth());

            // Each section is its own gray bubble with its own header row, so the
            // header and data columns share the box and line up exactly. Titles are the
            // plain section name (no count), centered (see DrawSectionHeader).
            DrawRouteSectionBubble(LogisticsRoutePresentation.ActiveSectionName, "No active routes.",
                activeRoutes, RouteSection.Active, currentUT);
            DrawSectionSeparator();
            DrawRouteSectionBubble(LogisticsRoutePresentation.PausedSectionName, "No paused routes.",
                pausedRoutes, RouteSection.Paused, currentUT);
            // Rewind-visibility follow-up: the collapsed "Dormant Routes (N)"
            // disclosure. Renders nothing when no route is dormant (the common
            // no-rewind save shows no extra chrome).
            DrawDormantSectionBubble();
            DrawSectionSeparator();
            DrawCandidateSectionBubble(LogisticsRoutePresentation.CandidatesSectionName, candidates, nearMisses);

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
        /// Paused yet): the "Sending one run" Status word, its live Cancel / greyed
        /// Delivering... and its greyed Send all read this one predicate. The provenance is
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

        // M3 near-miss subsection key (collapsible via the shared expandedRows set).
        private const string NearMissSectionKey = "nearmiss:section";

        internal const string NearMissGroupKeyPrefix = "nearmiss:group:";

        // M6 dismissed-candidates subsection key (collapsible via the shared
        // expandedRows set, mirroring NearMissSectionKey).
        private const string DismissedSectionKey = "dismissed:section";

        internal const string WindowedIntervalFieldDisabledReason =
            "This route delivers on launch windows, so it takes no typed interval; use -/+ to deliver every Nth window";

        // ----- The detail block's Interact column -----
        //
        // Rename, Delete, Update parts and (Advanced) Link round-trip... / Unlink sit at the
        // right of the detail block's first lines, one 100 px single per line, under the row's
        // Activate / Pause: there is no button row of their own. Every route-detail line
        // ends in one slot cell of the same width (a button, or a same-width space), so
        // the label beside it wraps at the same width on every line, and the slot a line
        // gets depends only on its position in the block - which is decided by cached
        // fields and the latched drawTuning, exactly like the lines themselves, so a
        // frame's Layout and Repaint passes draw the same controls. detailSlotRoute is
        // null outside DrawRouteDetail (the candidate detail shares DetailLine and draws
        // no slot).
        private Route detailSlotRoute;
        private int detailSlotNext;

        internal const int RenameSlot = 0;
        internal const int DeleteSlot = 1;
        internal const int UpdatePartsSlot = 2;
        internal const int LinkSlot = 3;

        // The Missions tab's vessel names for the Flights used line, cached ACROSS frames
        // (MissionVesselNaming.Cache, keyed on StateVersion, DisplayNames.Version and the
        // missions' names), so an expanded route reads a dictionary per tree per frame.
        private readonly MissionVesselNaming.Cache flightsUsedNamingCache = new MissionVesselNaming.Cache();
        private int flightsUsedSignatureFrame = -1;
        private int flightsUsedSignature;
        private static readonly System.Func<RecordingTree, MissionStructure> FlightsUsedStructure =
            t => MissionStructureBuilder.Build(t);
        private static readonly System.Func<string, string> FlightsUsedMissionName =
            treeId => MissionStore.FindOriginalMission(treeId)?.Name;

        // ----- Section headers (Active / Paused / Candidates) -----
        //
        // Each title bar is ONE button over the whole width: the caret, the centred title
        // with its count ("Active Routes (2)") in a font 2 pt over the shared section header,
        // and a thin accent bar in the section's status colour along the bottom of the
        // header box (green Active, grey Paused, cyan Candidates) - a bar rather than a
        // coloured title, because it marks where a section starts even when its title has
        // scrolled out of view. A click queues the toggle (pendingSectionToggle) and
        // ApplyPendingActions flips it after the draw, so a frame's Layout and Repaint draw
        // the same controls. Collapsed sections draw only the bar. Session state only.
        private readonly HashSet<string> collapsedSections = new HashSet<string>();
        private string pendingSectionToggle;
        private GUIStyle sectionTitleButtonStyle;
        private readonly Dictionary<ParsekUI.StatusColorKind, GUIStyle> sectionAccentStyles =
            new Dictionary<ParsekUI.StatusColorKind, GUIStyle>();
        private GUIStyle sectionRuleStyle;

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

        internal static string ShortId(string id)
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
            detailStyle.normal.textColor = ParsekUI.MutedTextColor;
            // The Every / Priority steppers' shared value cell, measured once with the
            // style that draws it.
            stepperValueWidth = LogisticsRoutePresentation.StepperValueCellWidth(
                text => detailStyle.CalcSize(new GUIContent(text)).x);

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
            nextAmberStyle.normal.textColor = ParsekUI.CountdownTextColor;

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
            float widestLabel = 0f;
            for (int i = 0; i < LogisticsRoutePresentation.InteractGridLabels.Length; i++)
                widestLabel = Mathf.Max(widestLabel,
                    interactPairButtonStyle.CalcSize(new GUIContent(LogisticsRoutePresentation.InteractGridLabels[i])).x);
            interactPairWidth = LogisticsRoutePresentation.InteractPairWidth(widestLabel);
            interactColumnWidth = LogisticsRoutePresentation.InteractColumnWidth(interactPairWidth);
            ParsekLog.Verbose("UI",
                $"Logistics Interact grid measured: widestLabel={widestLabel.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"pair={interactPairWidth.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"column={interactColumnWidth.ToString("F0", CultureInfo.InvariantCulture)}");
            detailSlotButtonStyle = new GUIStyle(interactButtonStyle)
            {
                padding = new RectOffset(
                    2, 2, GUI.skin.button.padding.top, GUI.skin.button.padding.bottom)
            };
            stepperRowHeight = LogisticsRoutePresentation.StepperRowHeight(
                detailSlotButtonStyle.CalcHeight(new GUIContent("Link round-trip..."), InteractSingleWidth)
                    + detailSlotButtonStyle.margin.vertical,
                detailStyle.CalcHeight(new GUIContent("Every:"), LogisticsRoutePresentation.StepperLabelWidth)
                    + detailStyle.margin.vertical);
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
