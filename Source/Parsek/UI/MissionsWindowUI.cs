using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The Missions tab content, drawn INSIDE the Recordings window (<see cref="RecordingsTableUI"/>
    /// hosts the window chrome and a two-tab bar; this class draws the body of the "Missions" tab
    /// via <see cref="DrawMissionsTabContent"/>). It reuses RecordingsTableUI's visual style and
    /// rendering helpers (column-header style, dark table-body box, tree-branch connector glyphs,
    /// expand/collapse carets, section-header bar) without inheriting it. Where the recordings tab
    /// groups individual recordings (groups -> chains -> recordings), this groups the higher
    /// mission abstraction: it lists saved <see cref="Mission"/>s (from <see cref="MissionStore"/>),
    /// and renders each tree's continuous-vessel composition over time with the offshoots that left
    /// each vessel as children. Per Mission: a name + Loop/period + Watch + Clone/Delete + Archive,
    /// and include checkboxes bound to that Mission's selection (persisted). A Mission loops as a
    /// single unit. See docs/dev/design-mission-abstractions.md.
    /// </summary>
    internal partial class MissionsWindowUI
    {
        private readonly ParsekUI parentUI;

        // Scroll position of the Missions tab's list. The host Recordings window owns the
        // window rect, resize, drag, and input lock; this tab only keeps its own scroll.
        private Vector2 scrollPos;

        // Cross-link target from the Timeline GoTo button (see RevealMissionForRecording).
        // Mirrors the recordings tab's two-frame handshake (pendingScrollToRecordingId ->
        // pendingScrollRowIndex): the row is FOUND during a draw pass and the scroll is
        // APPLIED at the top of a later one, because writing scrollPos after the scroll view
        // has opened has no effect for that frame.
        private string pendingRevealMissionId;

        // Layout y of the FIRST mission header drawn this pass, and the scroll offset derived
        // for the target. The scroll target is the DIFFERENCE between the target header's y and
        // the first header's y, so it is a distance between two rects measured the same way
        // rather than a raw layout coordinate - which keeps it correct regardless of what origin
        // GUILayoutUtility reports inside a scroll view. NaN = nothing pending.
        private float revealFirstHeaderY;
        private bool revealFirstHeaderSeen;
        private float pendingRevealScrollY = float.NaN;

        // Frame the list last completed a LAYOUT pass in. A Repaint event is NOT on its own
        // proof that rects are real: GUILayoutUtility solves them at the end of an
        // EventType.Layout pass and later passes reuse that window's cache, so a window opened
        // mid-frame (exactly what the GoTo click does when the window was closed) can paint a
        // Repaint against a cache that was never solved and hand back zero rects. Measuring then
        // would consume the target, land the list at offset 0, and log it as a success. The
        // capture waits for a pass whose own Layout ran.
        private int missionsLayoutFrame = -1;

        // Frame pendingRevealScrollY was captured in. The apply is the very next drawn pass, so
        // anything older is a target the player has since navigated away from (tab switch,
        // window close) - applying it minutes later would scroll a list they are looking at for
        // an unrelated reason.
        private int pendingRevealScrollFrame = -1;
        private const int RevealScrollMaxAgeFrames = 4;

        // Queued by the cross-link when its target mission is collapsed: the mission to EXPAND,
        // applied by the draw on a LAYOUT pass only (see RevealMissionForRecording for why it
        // cannot be written from the click handler).
        private string pendingExpandMissionId;

        // For tests: the queued expand, whose application is draw-loop-only.
        internal string PendingExpandMissionIdForTesting => pendingExpandMissionId;

        // The scheduled cross-link target, for tests: the draw loop that consumes it is an
        // IMGUI callback with no headless seam, so this is the only observable outcome of
        // RevealMissionForRecording that a unit test can assert on.
        internal string PendingRevealMissionIdForTesting => pendingRevealMissionId;

        // The offset the last CAPTURE measured, latched and never cleared - unlike
        // pendingRevealScrollY, which the apply consumes one pass later. This is what the
        // in-game MissionRevealScrollsToTheTargetMission test reads: the capture is the step
        // that reads real layout rects, and the failure it guards against (measuring an
        // unsolved layout cache) is silent precisely because it produces a plausible-looking
        // zero rather than an exception. NaN = nothing captured yet this session.
        internal float LastRevealScrollOffsetForTesting = float.NaN;

        // So an in-game test can leave the list where it found it: the reveal it drives
        // legitimately scrolls, and an operator running the batch by hand should not come back
        // to a list parked on a probe row that no longer exists.
        internal Vector2 ScrollPosForTesting
        {
            get { return scrollPos; }
            set { scrollPos = value; }
        }

        // ----- GUI-census seam accessors (UiAction op=expand) -----
        //
        // Three INDEPENDENT expansion collections back this tab (vessel rows, interval legs
        // and each mission's collapse caret), and the seam addresses them through one
        // namespaced key grammar (TestCommandUiState). They are exposed
        // rather than driven by a synthesised click for the reason the UiAction applier's
        // header gives: every click handler's whole body IS one of these writes, so the
        // seam does the same write and reads it back.
        //
        // POLARITY: collapsedLegs holds what is COLLAPSED. The two leg accessors below
        // speak "expanded" so the wire never has to carry a flip, and the flip lives here
        // once instead of in every spec that expands a leg.
        //
        // The enumerations exist for `key=all` and for the "what does exist" reject
        // message. They are DERIVED, not cached: a census runs them a handful of times per
        // lane, and a stale list would expand keys the window no longer draws.

        internal bool IsVesselExpandedForTesting(string key)
            => key != null && expandedVessels.Contains(key);

        internal bool SetVesselExpandedForTesting(string key, bool expanded)
        {
            if (key == null) return false;
            return expanded ? expandedVessels.Add(key) : expandedVessels.Remove(key);
        }

        internal bool IsLegExpandedForTesting(string key)
            => key != null && !collapsedLegs.Contains(key);

        internal bool SetLegExpandedForTesting(string key, bool expanded)
        {
            if (key == null) return false;
            return expanded ? collapsedLegs.Remove(key) : collapsedLegs.Add(key);
        }

        internal int ExpandedVesselCountForTesting => expandedVessels.Count;

        /// <summary>Every mission id: the key a mission's collapse caret is driven by.</summary>
        internal List<string> EnumerateMissionExpandKeysForTesting()
        {
            var keys = new List<string>();
            var missions = MissionStore.Missions;
            for (int i = 0; i < missions.Count; i++)
                if (missions[i] != null && !string.IsNullOrEmpty(missions[i].Id))
                    keys.Add(missions[i].Id);
            return keys;
        }

        /// <summary>Writes a mission's collapse flag the way its caret in the # column does,
        /// with the same log line. True when the flag changed.</summary>
        internal bool SetMissionExpandedForTesting(string missionId, bool expanded)
        {
            Mission m = FindMissionById(missionId);
            if (m == null || m.Collapsed == !expanded)
                return false;
            m.Collapsed = !expanded;
            ParsekLog.Info("Mission", $"Mission '{m.Name}' collapsed={m.Collapsed}");
            return true;
        }

        internal int ExpandedMissionCountForTesting
        {
            get
            {
                int n = 0;
                var missions = MissionStore.Missions;
                for (int i = 0; i < missions.Count; i++)
                    if (missions[i] != null && !missions[i].Collapsed) n++;
                return n;
            }
        }

        internal int CollapsedLegCountForTesting => collapsedLegs.Count;

        /// <summary>Every <c>missionId:headId</c> key a per-vessel row could expand under,
        /// walked over the SAME flattened row model the draw uses.</summary>
        internal List<string> EnumerateVesselExpandKeysForTesting()
        {
            var keys = new List<string>();
            var trees = RecordingStore.CommittedTrees;
            var missions = MissionStore.Missions;
            for (int i = 0; i < missions.Count; i++)
            {
                Mission mission = missions[i];
                RecordingTree tree = FindTree(trees, mission.TreeId);
                if (tree == null) continue;
                CollectVesselRowKeys(mission, GetVesselRows(tree), keys);
            }
            return keys;
        }

        private void CollectVesselRowKeys(Mission mission, List<MissionVesselRow> rows,
                                          List<string> keys)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++)
            {
                MissionVesselRow row = rows[i];
                if (row == null) continue;
                if (!string.IsNullOrEmpty(row.OwnerHeadId))
                    keys.Add(CollapseKey(mission, row.OwnerHeadId));
                CollectVesselRowKeys(mission, row.Children, keys);
            }
        }

        /// <summary>
        /// Every <c>missionId:headId</c> key a composition (interval) row could collapse
        /// under, taken from the vessel rows' own interval lists.
        ///
        /// <para>It is a SUPERSET: only a node WITH CHILDREN reads the set, so some keys
        /// here are inert. That is deliberate and cheaper than re-walking the composition
        /// tree for the has-children predicate - an inert key in the set changes no
        /// drawing, while a MISSING key would leave a staircase the census cannot
        /// open.</para>
        /// </summary>
        internal List<string> EnumerateLegExpandKeysForTesting()
        {
            var keys = new List<string>();
            var trees = RecordingStore.CommittedTrees;
            var missions = MissionStore.Missions;
            for (int i = 0; i < missions.Count; i++)
            {
                Mission mission = missions[i];
                RecordingTree tree = FindTree(trees, mission.TreeId);
                if (tree == null) continue;
                CollectLegKeys(mission, GetVesselRows(tree), keys);
            }
            return keys;
        }

        private void CollectLegKeys(Mission mission, List<MissionVesselRow> rows,
                                    List<string> keys)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++)
            {
                MissionVesselRow row = rows[i];
                if (row == null) continue;
                for (int j = 0; j < row.Intervals.Count; j++)
                {
                    string headLegId = row.Intervals[j] != null
                        ? row.Intervals[j].HeadLegId : null;
                    if (!string.IsNullOrEmpty(headLegId))
                        keys.Add(CollapseKey(mission, headLegId));
                }
                CollectLegKeys(mission, row.Children, keys);
            }
        }

        // Collapsed through-line heads, keyed "missionId:headId" so two Missions over
        // the same tree collapse independently. Transient UI state (not persisted). The
        // include selection lives per-Mission in Mission.ExcludedIntervalKeys.
        private readonly HashSet<string> collapsedLegs = new HashSet<string>();

        // Loop-period inline edit state (mirrors RecordingsTableUI's loopPeriodFocusedRi /
        // loopPeriodEditText / loopPeriodEditRect). At most one period field is being edited at a
        // time, so a single set of fields suffices. The value is held in the buffer while the
        // player types and COMMITTED on Enter or click-away (a window-level MouseDown outside the
        // field rect) - NOT on every keystroke, so it does not depend on per-frame keyboard-focus
        // detection staying stable (which the old commit-on-type path relied on and which broke
        // when the header layout changed). loopPeriodFocusedMissionId == null means not editing.
        private string loopPeriodFocusedMissionId;
        private string loopPeriodEditText = "";
        private Rect loopPeriodEditRect;

        // Per-frame cache of the derived mission read model (structure + through-line
        // view) keyed by tree id. OnGUI fires several times per frame (Layout, Repaint,
        // input events) and multiple missions can target the same tree, so without this
        // the two builders ran 6-12 times per frame for unchanged trees. The cache is
        // cleared whenever a new frame starts, so it always reflects the current trees
        // (no invalidation logic, no staleness) and the Layout and Repaint passes of one
        // frame see identical data.
        private readonly Dictionary<string, (MissionStructure structure, MissionThroughLineView view)>
            missionViewCache =
                new Dictionary<string, (MissionStructure structure, MissionThroughLineView view)>();
        private int missionViewCacheFrame = -1;

        // Per-frame cache of the composition-over-time trees (one list of root nodes per tree id),
        // built from the same MissionStructure. Cleared alongside missionViewCache each frame.
        // M-MIS-8: per-frame caches for the cross-tree partner-journey affordance - the derived
        // dock links per mission tree, and each link's journey leg ids (keyed by link id).
        // Cleared together with missionViewCache on the first lookup of a new frame.
        private readonly Dictionary<string, List<ForeignDockLink>> foreignLinksCache =
            new Dictionary<string, List<ForeignDockLink>>();
        private readonly Dictionary<string, List<string>> journeyLegsCache =
            new Dictionary<string, List<string>>();

        private readonly Dictionary<string, List<MissionCompositionNode>> compositionCache =
            new Dictionary<string, List<MissionCompositionNode>>();

        // Dock-event graph (design-dock-event-graph.md 6.5) held per FRAME, so the composition
        // rows can name a dock partner without walking the graph host once per row per OnGUI
        // pass. DockEventGraphCache itself is signature-gated (it rebuilds only when the
        // committed topology moves) but recomputing that signature per row would still allocate;
        // this reference memo makes a naming lookup a pure dictionary hit. The description memo
        // is keyed by (tree id, interval key) - the whole row identity - and clears with the
        // other per-frame caches in GetMissionView.
        private DockEventGraph dockEventGraphCache;
        private int dockEventGraphCacheFrame = -1;
        private readonly Dictionary<string, string> dockPartnerTextCache =
            new Dictionary<string, string>();

        // Mission event digest (design-dock-event-graph.md 7.1). The digest is not drawn as a
        // list; it is the source of the partner naming and the "Go to" target
        // on the Docked partner rows (DrawForeignDockLinkRows). NOT a per-frame cache: the rows
        // are keyed on the GRAPH INSTANCE (which the host cache only replaces when the committed
        // topology signature moves) plus a fingerprint of the mission's included foreign dock
        // links (the one selection input, via the R5 gap rows), so the partner rows cost one
        // dictionary hit + one fingerprint fold per frame instead of a rebuild. Cleared only by
        // those two inputs changing, so it survives across frames like the graph it mirrors.
        private struct DigestCacheEntry
        {
            public DockEventGraph Graph;
            public int LinkFingerprint;
            public List<MissionEventRow> Rows;
        }
        private readonly Dictionary<string, DigestCacheEntry> digestCache =
            new Dictionary<string, DigestCacheEntry>();

        // Chapter grouping (design-dock-event-graph.md 7.2). Same memo shape and the same
        // rationale as digestCache above: chapters and their key expansions derive from the TREE
        // alone (never from the selection - the selection only decides the checkbox state), and
        // any tree change moves the dock-event graph's topology signature, so the graph INSTANCE
        // is a sufficient staleness key. Cached across frames like the graph it mirrors, so an
        // open Missions window costs one dictionary hit per mission per frame instead of a
        // structure + through-line + composition walk per chapter per frame.
        private sealed class MissionChapterEntry
        {
            public ChapterRoot Root;
            public HashSet<string> IntervalKeys;
            /// <summary>The chapter's earliest interval key: the row its header sits above.</summary>
            public string AnchorKey;
        }
        private struct ChapterCacheEntry
        {
            public DockEventGraph Graph;
            public List<MissionChapterEntry> Chapters;
            /// <summary>anchor interval key -> index into <see cref="Chapters"/>, so the row
            /// walker's per-node lookup is one dictionary hit.</summary>
            public Dictionary<string, int> AnchorToIndex;
        }
        private readonly Dictionary<string, ChapterCacheEntry> chapterCache =
            new Dictionary<string, ChapterCacheEntry>();

        // The chapters of the mission whose OWN composition rows are currently being drawn, set
        // for the duration of that mission's compRoots loop and cleared straight after. Scoping
        // it to that loop is deliberate: the foreign partner-journey rows drawn afterwards go
        // through the same row walker, and a chapter is a statement about THIS mission's tree.
        private List<MissionChapterEntry> currentChapters;
        private Dictionary<string, int> currentChapterAnchors;

        // Per-frame cache of the T2.2 flattened per-vessel rows, keyed by TREE id (derived from
        // the composition roots, so every Mission over one tree shares them). Cleared alongside
        // missionViewCache on the first lookup of a new frame.
        // Cross-frame: keyed on RecordingStore.StateVersion + the missions' names, so a
        // steady frame reads it and computes nothing (MissionVesselNaming.Cache).
        private readonly MissionVesselNaming.Cache vesselNamingCache = new MissionVesselNaming.Cache();
        private int vesselNamingRebuildsLogged;
        // Held in fields so a cache hit allocates no closure; the mission signature is read
        // once per frame, not per tree.
        private System.Func<RecordingTree, MissionStructure> vesselNamingStructure;
        private static readonly System.Func<string, string> VesselNamingMissionName =
            otherTreeId => ResolvePartnerMissionName(otherTreeId, null);
        private int vesselNamingSignature;
        private int vesselNamingSignatureFrame = -1;
        private readonly Dictionary<string, List<MissionVesselRow>> vesselRowsCache =
            new Dictionary<string, List<MissionVesselRow>>();

        // Vessel rows whose interval detail is EXPANDED (default collapsed - the flattened rows
        // are the reading surface, the intervals are the opt-in authoring detail). Keyed like
        // collapsedLegs (mission id + head id), session-transient.
        private readonly HashSet<string> expandedVessels = new HashSet<string>();

        // Actual rendered width of the expanding name cell, per row depth, captured on Repaint.
        // IMGUI measures a word-wrapped label's height BEFORE the horizontal group resolves its
        // width, so an ExpandWidth name cell that wraps gets a rect roughly a line short - and
        // MiddleLeft centering then crops the text at BOTH ends (first line clipped at the top,
        // last line at the bottom). Feeding the previous Repaint's width back through CalcHeight
        // gives the layout the true height; same depth = same fixed columns + indent = same
        // width, and the cache re-converges one repaint after any window resize.
        private readonly Dictionary<int, float> wideCellWidthCache = new Dictionary<int, float>();

        // The same measured-width feedback for the wrapping summary cell on each mission bar's
        // second line, keyed by Mission.Id: its width depends on which buttons that mission's
        // line draws (the period cell is content-sized), so one shared value would not converge.
        private readonly Dictionary<string, float> missionSummaryCellWidth =
            new Dictionary<string, float>();

        // The last logged warned state of each mission's summary countdown, keyed by Mission.Id,
        // so the per-frame summary draw logs the "(!)" marker only when it appears or clears.
        private readonly Dictionary<string, bool> lastCountdownWarnedByMission =
            new Dictionary<string, bool>();

        // Draws the expanding name cell (label, or caret button when asButton) at the explicit
        // wrapped height described above. Returns true when the button was clicked.
        private bool DrawWideRowCell(GUIContent content, int depth, bool asButton)
        {
            bool clicked;
            if (wideCellWidthCache.TryGetValue(depth, out float w) && w > 1f)
            {
                float h = Mathf.Max(CompositionRowMinHeight,
                    compositionCellLabel.CalcHeight(content, w));
                clicked = asButton
                    ? GUILayout.Button(content, compositionCellLabel,
                        GUILayout.ExpandWidth(true), GUILayout.Height(h))
                    : Label(content, GUILayout.ExpandWidth(true), GUILayout.Height(h));
            }
            else
            {
                clicked = asButton
                    ? GUILayout.Button(content, compositionCellLabel, GUILayout.ExpandWidth(true))
                    : Label(content, GUILayout.ExpandWidth(true));
            }
            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                if (r.width > 1f)
                    wideCellWidthCache[depth] = r.width;
            }
            return clicked;

            bool Label(GUIContent c, params GUILayoutOption[] opts)
            {
                GUILayout.Label(c, compositionCellLabel, opts);
                return false;
            }
        }

        // Constant-payload checkbox contents, allocated once instead of per row per pass.
        private static readonly GUIContent VesselIncludeCheckboxContent =
            new GUIContent("", MissionPresentation.VesselIncludeCheckboxTooltip);
        private static readonly GUIContent IntervalIncludeCheckboxContent =
            new GUIContent("", MissionPresentation.IncludeCheckboxTooltip);
        private static readonly GUIContent PartnerJourneyCheckboxContent =
            new GUIContent("", MissionPresentation.PartnerJourneyTooltip);
        private static readonly GUIContent ChapterIncludeCheckboxContent =
            new GUIContent("", MissionPresentation.ChapterIncludeCheckboxTooltip);

        // Per-frame cache of the header summary-line facts (T1.1), keyed by TREE id: the facts are
        // derived from the tree's read models only (span, vessel count, crew union, terminal word),
        // never from a Mission's selection, so every Mission over one tree shares them. Cleared
        // alongside missionViewCache on the first lookup of a new frame.
        private readonly Dictionary<string, MissionPresentation.MissionSummaryFacts> summaryFactsCache =
            new Dictionary<string, MissionPresentation.MissionSummaryFacts>();

        // Per-frame cache of the REAL Mission LoopUnitSet (the SAME one the scene drivers build via
        // MissionLoopUnitBuilder.Build with FlightGlobalsBodyInfo.Instance), so the T- countdown
        // points to the engine's ACTUAL next relaunch (PhaseAnchorUT + n*relaunchCadence) instead of
        // the next faithful P-window (which the engine SKIPS when the relaunch cadence is a multiple
        // m*P with m>=2). Built at most once per frame (keyed by Time.frameCount, like the view cache
        // above) and read-only: this is a DISPLAY mirror of the engine's schedule and never feeds the
        // engine/scene drivers, so it cannot affect playback. Lazily computed by GetLoopUnitSet.
        private GhostPlaybackLogic.LoopUnitSet loopUnitSetCache;
        private int loopUnitSetCacheFrame = -1;

        // Fixed-width columns to the right of the expanding "Missions and vessels" column,
        // mirroring the recordings window's fixed-width cells so the window reads as the
        // same table style. Layout: [index/check] Missions and vessels | Start time |
        // Start event | End event | End time | Re-Fly | Archive. The first column doubles as the
        // mission index cell (on the mission bar's first line) and the include checkbox (on
        // vessel rows). Since Missions Model 1 the mission bar's first line is a row of this
        // table too: its values sit under these headings.
        // ColW_Enable mirrors the recordings tab's leading enable-toggle column (which the Missions
        // tab has no equivalent for): a blank cell of this width precedes the index/checkbox in
        // every Missions row, and the header's "#" cell is the same merged [enable+index] width, so
        // the "Missions and vessels" column lines up with the recordings tab's "Name" column.
        private const float ColW_Enable = 20f;
        private const float ColW_Index = 30f;
        private const float ColW_StartTime = 120f;
        // Both event cells show a bare event word ("Docked", "Decoupled"); a fuller phrase (the
        // named dock partner, "Docked with Munport Station", T1.4) is the cell's tooltip, and a
        // word wider than its cell carries itself as the tooltip (DrawEventCell). The Start event
        // column keeps its wider width so the header row does not move.
        private const float ColW_StartEvent = 110f;
        private const float ColW_EndEvent = 85f;
        private const float ColW_EndTime = 120f;
        // Width of the Advanced loop buttons (Clone, Delete, Warp to...): the old Clone width
        // (60) + 10 px, so they read as one group.
        private const float ColW_HeaderButton = 70f;

        // ----- the Advanced loop grid (owner mock-up 2026-09-30, aligned 2026-10-01) -----
        //
        // Clone, Delete, Warp to... and the Loop row sit as a 2x2 grid across the mission bar's
        // two lines, inside the name cell, right-aligned, so the summary keeps the width they
        // used to take in one row after it:
        //   line 1: title ...  [A: Clone ] [B: Warp to...        ] [Log           ]
        //   line 2: summary .. [A: Delete] [B: Loop [x] every    ] [period: [10][sec]]
        // Every grid control has zero horizontal margin (loopGridButtonStyle, interactButtonStyle
        // and the loopCell* styles), so each column is exactly its constant wide on both lines:
        // the line-2 loop row starts at Warp's left edge and ends at Log's right edge, its
        // "Loop [x] every" part fills column B under Warp, and its period part fills the Log slot
        // under Log. Warp is always drawn in Advanced (greyed with its reason when it cannot
        // act) and every width is fixed, so the grid never shifts between states.
        internal const float LoopGridColumnAWidth = ColW_HeaderButton;
        internal const float LoopGridGap = 4f;
        internal const float LoopGridColumnBWidth = 92f;
        // The loop row spans column B plus the Log slot above it.
        internal const float LoopCellWidth = LoopGridColumnBWidth + InteractButtonWidth;
        // Column B on line 2: "Loop", the checkbox's slot, "every". The three sum to column B.
        internal const float LoopRowLabelWidth = 28f;
        internal const float LoopRowToggleSlotWidth = 22f;
        internal const float LoopRowEveryWidth =
            LoopGridColumnBWidth - LoopRowLabelWidth - LoopRowToggleSlotWidth;
        // The Log slot on line 2: the period value field, a gap and the unit button (the
        // recordings window's 40 px unit button), summing to Log's width. A value-only locked
        // period ("~13d-19d") is one label of the whole slot.
        internal const float LoopPeriodUnitButtonWidth = 40f;
        internal const float LoopPeriodGap = 4f;
        internal const float LoopPeriodValueWidth =
            InteractButtonWidth - LoopPeriodUnitButtonWidth - LoopPeriodGap;
        // One line: every control in the loop row is centred on it.
        private const float LoopCellHeight = 22f;
        // ----- the Interact column (Missions Model 1, owner mock-up 2026-09-30) -----
        //
        // ONE right-hand column carries every per-row action: Watch on a mission's first line,
        // Rewind / Forward on its second (a reserved slot of the same size when neither
        // applies), Stash / Fly + Seal on vessel and interval rows, Go to on Docked partner
        // rows. Every button in it is either a SINGLE (full width) or half of a PAIR, and a pair
        // spans exactly one single, so the column's button edges line up on every row. Log (in
        // the name cell of a mission's first line) takes the single width too.
        // MissionsTabColumnSequenceTests pins
        // 2 * InteractPairButtonWidth + InteractButtonGap == InteractButtonWidth.
        //
        // Sizing: the owner asked for the single width at the old Watch width (70) + 10 and for
        // the pairs to span one single with wider buttons than today's cramped Stash. Those two
        // cannot both hold at 80 (a pair of 80 is 38 + 4 + 38, exactly today's Stash), so the
        // single is 100 and each pair half 48 - wide enough for "Forward" and "Stash".
        internal const float InteractButtonWidth = 100f;
        internal const float InteractButtonGap = 4f;
        internal const float InteractPairButtonWidth = (InteractButtonWidth - InteractButtonGap) / 2f;
        // Space inside the column either side of its buttons.
        internal const float InteractCellInset = 8f;
        internal const float ColW_Interact = InteractButtonWidth + 2f * InteractCellInset;
        // Fixed column-header height so every Missions header cell is the same height (matches
        // RecordingsTableUI.ColHeaderHeight); the toggle-bearing Archive cell would otherwise be
        // taller than the plain-label cells.
        private const float ColHeaderHeight = 32f;
        // Minimum height for each composition (vessel) row, matching the recordings table's per-row
        // stride (RecordingsTableUI scrolls at 22 px/row). The composition cells are mostly plain
        // labels, which alone measure shorter than a recordings row; without this floor the rows
        // pack too tightly and leave no room for the per-row Fly / Seal button.
        // The pickers' entry rows reuse it (PickerWindowLayout.RowMinHeight).
        internal const float CompositionRowMinHeight = 22f;

        // How a Mission row list is ordered. Index = the per-tree index number (clones of a
        // tree share it); Name = alphabetic mission name; StartTime = the mission span start.
        internal enum MissionSortColumn { Index, Name, StartTime }
        private MissionSortColumn sortColumn = MissionSortColumn.Index;
        private bool sortAscending = true;

        /// <summary>
        /// The Missions tab's sort column (as an INDEX into
        /// <see cref="MissionSortColumn"/>'s declaration order) and direction, for the
        /// automation-only <c>UiAction op=sort</c> seam op. An int for the reason
        /// <c>RecordingsTableUI.SortColumnIndexForTesting</c> is one, and an out-of-range
        /// write is ignored.
        ///
        /// <para>No cache to invalidate here, unlike the Recordings tab's: this tab sorts its
        /// row list live in the draw pass, and the header click's own callback only
        /// logs.</para>
        /// </summary>
        internal int SortColumnIndexForTesting
        {
            get { return (int)sortColumn; }
            set
            {
                if (value < (int)MissionSortColumn.Index
                    || value > (int)MissionSortColumn.StartTime)
                    return;
                sortColumn = (MissionSortColumn)value;
            }
        }

        internal bool SortAscendingForTesting
        {
            get { return sortAscending; }
            set { sortAscending = value; }
        }

        /// <summary>
        /// The flattened per-vessel rows for a tree, for the automation-only
        /// <c>UiAction op=select</c> seam op: the SAME list the draw pass reads, through the
        /// same per-frame cache, so the rows the op writes inclusion for are the rows the
        /// capture beside it photographs.
        ///
        /// <para>Safe from outside a draw pass: the cache behind it is keyed on
        /// <c>Time.frameCount</c> and the first lookup in a new frame rebuilds, so a caller
        /// in <c>Update</c> gets this frame's rows rather than the previous frame's.</para>
        /// </summary>
        internal List<MissionVesselRow> VesselRowsForTesting(RecordingTree tree)
            => tree == null || string.IsNullOrEmpty(tree.Id)
                ? new List<MissionVesselRow>()
                : GetVesselRows(tree);

        /// <summary>The derived cross-tree partner-journey links for a tree, for the same op.
        /// Routed through the window's own cache, which already wraps the walk in
        /// <c>MissionCrossTreeDock.SuppressLogging</c>.</summary>
        internal List<ForeignDockLink> ForeignDockLinksForTesting(
            RecordingTree tree, List<RecordingTree> trees)
            => tree == null || string.IsNullOrEmpty(tree.Id) || trees == null
                ? new List<ForeignDockLink>()
                : GetForeignDockLinks(tree, trees);

        // ----- the mission-title rename editor, for UiAction op=edit -----
        //
        // Same three-write arming contract as the recordings window's two editors (row
        // key, draft, focus sentinel), and the same reason the commit is a separate call:
        // committing a MAIN mission's title runs MissionGroupLink.RenameMissionGroup, which
        // renames the root group plus its auto subgroups plus Mission.Name atomically and
        // rejects both halves on a collision. The census wants the mid-edit layout.

        /// <summary>Arms the mission-title rename editor on a Mission id with a draft.
        /// False when no mission carries that id.</summary>
        internal bool TryBeginMissionRenameForTesting(string missionId, string draft)
        {
            if (string.IsNullOrEmpty(missionId)) return false;
            if (FindMissionById(missionId) == null) return false;
            renamingMissionId = missionId;
            renamingMissionText = draft ?? string.Empty;
            renamingMissionFocused = false;
            return true;
        }

        internal string RenamingMissionIdForTesting => renamingMissionId;

        /// <summary>The mission-title rename's draw-produced focus sentinel, the signal that
        /// the text field really drew rather than the row key merely surviving. See
        /// <c>TestCommandUiEdit.EditNotDrawnReason</c>.</summary>
        internal bool RenamingMissionFocusedForTesting => renamingMissionFocused;

        internal string RenamingMissionTextForTesting => renamingMissionText;

        /// <summary>Runs the mission rename's own commit body for the armed id, the way the
        /// Enter key and the click-away path do.</summary>
        internal void CommitMissionRenameForTesting()
            => CommitMissionRenameById(renamingMissionId);

        /// <summary>
        /// The body of ONE interval's include checkbox (the expanded vessel detail rows and the
        /// foreign partner-journey subtrees): moves the interval's key into or out of
        /// <see cref="Mission.ExcludedIntervalKeys"/> with NO cascade (unticking the launch
        /// interval start-trims the loop and leaves the post-separation survivor included),
        /// and logs the edit. The checkbox and the
        /// automation-only <c>UiAction op=select key=leg:</c> both call it, so the seam drives
        /// the exact write and the exact log line a click produces.
        ///
        /// <para>Idempotent: a request matching the current state writes and logs
        /// nothing and returns false. Both checkbox callers derive the shown state from exact
        /// set membership, so a click always changes the set and its behaviour is unchanged;
        /// the seam takes an explicit direction and relies on the no-op answer.</para>
        /// </summary>
        /// <returns>True when the excluded set changed.</returns>
        internal static bool ApplyIntervalInclusion(Mission mission, string headLegId, bool include)
        {
            if (mission == null || string.IsNullOrEmpty(headLegId))
                return false;
            bool changed = include
                ? mission.ExcludedIntervalKeys.Remove(headLegId)
                : mission.ExcludedIntervalKeys.Add(headLegId);
            if (!changed)
                return false;
            ParsekLog.Info("Mission",
                $"Mission '{mission.Name}' interval '{headLegId}' included={include}");
            return true;
        }

        // Inline mission-title rename (double-click the name), mirroring the recordings
        // window group rename. renamingMissionId is the Mission.Id currently being edited.
        private string renamingMissionId;
        private string renamingMissionText = "";
        private bool renamingMissionFocused;
        private Rect activeMissionRenameRect;
        private string lastClickedMissionId;
        private float lastMissionClickTime;
        private const float DoubleClickThreshold = 0.3f;

        private static readonly Color DimColor = ParsekUI.DimTextColor;

        // Mission-header summary line (T1.1): a muted grey, quieter than the bold title above
        // it, so the line reads as annotation rather than a second heading. A text colour on
        // the style, never an alpha on GUI.color (see EnsureStyles).
        // Internal: the Logistics window's grey Route second line reuses it (one muted text
        // colour for a two-line title row across windows).
        internal static readonly Color MissionSummaryTextColor = ParsekUI.MutedTextColor;

        // Tint for a loop-period value that the overlap cap raised above what was requested
        // (so the cell shows the real effective cadence in a distinct colour), and the colour
        // of the summary line's countdown (MissionPresentation.SummaryCountdownColorHex, held
        // equal to this by a unit test). Soft amber.
        internal static readonly Color LoopPeriodClampColor = ParsekUI.CountdownTextColor;

        // -- Recreated styles --
        // RecordingsTableUI builds these privately inside EnsurePhaseStyles();
        // we cannot reach those instances from a subclass, so we lazily rebuild
        // the same styles here (verbatim, adapted) to match the look exactly.
        private GUIStyle bodyCellLabel;
        // Composition (vessel) row cell label: same as bodyCellLabel but vertically centered and
        // stretched to the row height, so the name + time text sit centered in the (MinHeight'd)
        // row instead of floating at the top - matching how the recordings rows read (their cells
        // fill the row height via the row's buttons). The include checkbox is centered separately
        // (ExpandHeight on the toggle).
        private GUIStyle compositionCellLabel;
        // Same cell label, but NON-wrapping: used by the mission bar's value cells
        // (DrawMissionValueCell), where a long value must not grow the bar's height; a value that
        // clips carries itself as the cell's tooltip.
        private GUIStyle compositionCellLabelNoWrap;
        // The mission-header summary line (T1.1): a thin, non-bold, non-wrapping second line inside
        // the header bubble carrying span / duration / vessel + crew counts / outcome / next launch.
        private GUIStyle missionSummaryLabel;
        private GUIStyle tableBodyBoxStyle;
        private GUIStyle interactButtonStyle;
        // The mission's collapse caret on line 2 of the # column: a clickable glyph with no
        // button frame (the house expand-caret click style: a Button drawn in a label style).
        private GUIStyle missionCaretStyle;
        private GUIStyle loopGridButtonStyle;
        // The Advanced loop row (line 2 of the grid): the row itself carries the grid buttons'
        // top / bottom margin so its 22 px line sits on Delete's line; every control inside has
        // zero margin so the row's fixed widths are its controls' exact rects.
        private GUIStyle loopCellRowStyle;
        private GUIStyle loopCellLabelStyle;
        private GUIStyle loopCellWordStyle;
        private GUIStyle loopCellToggleStyle;
        private GUIStyle loopCellFieldStyle;
        private GUIStyle loopCellUnitButtonStyle;
        // Boxed header-cell container + bold inner label for a header cell that shares its
        // dark box with a toggle (Archive), mirroring RecordingsTableUI.colHdrCellContainerStyle
        // / boldHeaderInnerLabel. The container's left/right margin is zeroed (so its footprint
        // is exactly Width(X), matching the body cells below it) while the box background spans
        // the whole cell, label + checkbox included.
        private GUIStyle colHdrCellContainerStyle;
        private GUIStyle boldHeaderInnerLabel;
        // The merged [enable + index] header cell and its sortable "#": laid out exactly like a
        // row's two leading cells (see EnsureStyles), so "#" sits over the row numbers, the
        // collapse carets and the include checkboxes.
        private GUIStyle indexHeaderCellStyle;
        private GUIStyle indexHeaderEnableStyle;
        private GUIStyle indexHeaderLabelStyle;
        // Mission-header ROW bubble: the dark section-header box used as the background of the
        // WHOLE mission header row (index, title, Loop, period, Watch, Clone, Delete, Archive),
        // so the bubble spans the full row width with every control sitting on it. Cloned from
        // the shared section-header box with left/right margin + padding zeroed so the bubble
        // reaches the row edges and the contents are not inset (keeps the Archive checkbox under
        // its column header). The index + title use missionHeaderTextStyle (bold transparent
        // text) instead of their own boxes so they don't double-box on top of the bubble.
        private GUIStyle missionHeaderRowStyle;
        private GUIStyle missionHeaderTextStyle;
        // Inline (non-bold) header-bar label, vertically centered + stretched to the row height, so
        // the "Loop" word and the read-only phase-locked period label ("~6.4d (Mun window)") sit on
        // the same baseline as each other and the row's buttons (a plain GUI.skin.label / bodyCellLabel
        // top-aligns and reads a few pixels high against the centered buttons).
        private GUIStyle missionHeaderInlineLabel;

        // Column-header text inset (matches RecordingsTableUI.BodyCellTextIndent so
        // body labels land under their header text).
        private const int BodyCellTextIndent = 5;

        // -- Caret glyphs (built from char codes so the source stays ASCII, the
        // same way RecordingsTableUI builds its TreeConnector glyphs) --
        // U+25BC down caret (expanded), U+25B6 right caret (collapsed).
        private static readonly string CaretDown =
            new string(new[] { (char)0x25BC, ' ' });
        private static readonly string CaretRight =
            new string(new[] { (char)0x25B6, ' ' });

        // The mission's collapse caret contents: the glyph alone (MissionPresentation owns which
        // glyph and hover go with which state), allocated once.
        internal const string MissionHeaderRenameHoverOriginal =
            "Double-click to rename (a name in use, or one with = { }, is refused).";
        internal const string MissionHeaderRenameHoverClone = "Double-click to rename.";

        private static readonly GUIContent MissionCaretExpandedContent = new GUIContent(
            MissionPresentation.MissionCollapseCaretGlyph(false),
            MissionPresentation.MissionCollapseCaretTooltip(false));
        private static readonly GUIContent MissionCaretCollapsedContent = new GUIContent(
            MissionPresentation.MissionCollapseCaretGlyph(true),
            MissionPresentation.MissionCollapseCaretTooltip(true));

        public MissionsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        /// <summary>
        /// The "#" header cell's right padding once its left inset is removed: the old left
        /// inset (max of the box's left padding and the first cell's left margin) moves to the
        /// right, added to the old right inset (max of the box's right padding and the last
        /// cell's right margin), so the cell's width is unchanged and no header to its right
        /// moves. Pure.
        /// </summary>
        internal static int IndexHeaderRightPadding(
            int boxPaddingLeft, int boxPaddingRight, int cellMarginLeft, int cellMarginRight)
        {
            return System.Math.Max(boxPaddingLeft, cellMarginLeft)
                + System.Math.Max(boxPaddingRight, cellMarginRight);
        }

        // Lazily rebuilds the body-cell label and dark table-body box styles the
        // recordings window uses. Same definitions as RecordingsTableUI.EnsurePhaseStyles
        // (cell label padding = left-only indent matching the header text inset;
        // body box = dark background, zero horizontal padding so columns align with
        // the header above the scroll view).
        private void EnsureStyles()
        {
            if (bodyCellLabel != null) return;

            var cellLabelPadding = new RectOffset(BodyCellTextIndent, 0, 0, 0);
            bodyCellLabel = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };

            // Composition-row cell: same padding, but vertically centered + stretched to the row
            // height so the text sits centered in the MinHeight'd row (not floating at the top).
            // The 5 px BOTTOM padding exists for the WRAPPED case: when a long cell (the T2.2
            // vessel event chain, typically) wraps, IMGUI's computed height comes up a few px
            // short and the last line's descenders clip (owner playtest 2026-08-20). Padding
            // grows the computed height only past what the row's MinHeight already grants, so
            // single-line rows render exactly as before (label ~16+5 px < the 22 px row floor).
            //
            // clipping = Overflow: the 5 px bottom padding shrinks a single-line cell's content
            // rect to 17 px in a 22 px row, and the default Clip then cut every descender
            // ("Orbitinq", "Decounled"). The text keeps its position; it is simply not clipped
            // to the padded rect. Horizontal overflow cannot happen here - the style wraps, and
            // the one non-wrapping variant below sets Clip back explicitly.
            compositionCellLabel = new GUIStyle(bodyCellLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                stretchHeight = true,
                padding = new RectOffset(BodyCellTextIndent, 0, 0, 5),
                clipping = TextClipping.Overflow
            };

            // Non-wrapping variant for the mission bar's value cells: a long value clips instead
            // of wrapping the bar taller (the clipped value is the tooltip).
            // Keeps the ORIGINAL padding - it is single-line by construction, and the wrapped-crop
            // bottom padding above would push its centered text off-baseline.
            compositionCellLabelNoWrap = new GUIStyle(compositionCellLabel)
            {
                wordWrap = false,
                clipping = TextClipping.Clip,
                padding = cellLabelPadding
            };

            tableBodyBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 2, 2),
                margin = new RectOffset(0, 0, 0, 0)
            };

            // Interact column buttons: zero horizontal margin, so a button's rect starts exactly
            // at the column's inset on every row (a skin button's 4 px margin would shift a
            // single button against a pair drawn with explicit gaps).
            interactButtonStyle = new GUIStyle(GUI.skin.button)
            {
                margin = new RectOffset(
                    0, 0, GUI.skin.button.margin.top, GUI.skin.button.margin.bottom)
            };
            // The loop grid's buttons (Clone, Delete, Warp to...): the same zero horizontal
            // margin, so the grid's columns are exactly their constants wide on both lines.
            loopGridButtonStyle = new GUIStyle(interactButtonStyle);
            // The loop row: see LoopGridColumnBWidth. The labels are centred on the line
            // (MiddleLeft, zero vertical padding), "Loop" and "Looped by route" start flush at the
            // row's left edge (Warp's left edge), and "every" keeps a small inset after the
            // checkbox. The field, unit button and checkbox drop their vertical margin; each sits
            // in a fixed 22 px slot that centres it (BeginLoopCellSlot).
            loopCellRowStyle = new GUIStyle(GUIStyle.none)
            {
                margin = new RectOffset(
                    0, 0, GUI.skin.button.margin.top, GUI.skin.button.margin.bottom),
                padding = new RectOffset(0, 0, 0, 0)
            };
            loopCellLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0)
            };
            loopCellWordStyle = new GUIStyle(loopCellLabelStyle)
            {
                padding = new RectOffset(3, 0, 0, 0)
            };
            loopCellToggleStyle = new GUIStyle(GUI.skin.toggle)
            {
                margin = new RectOffset(0, 0, 0, 0)
            };
            loopCellFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                margin = new RectOffset(0, 0, 0, 0)
            };
            loopCellUnitButtonStyle = new GUIStyle(GUI.skin.button)
            {
                margin = new RectOffset(0, 0, 0, 0)
            };
            // Boxed container for the Archive header cell: clone the shared column-header
            // box but zero only the LEFT/RIGHT margin so the BeginHorizontal footprint is
            // exactly Width(ColW_Archive) (top/bottom margin kept for the header-to-list gap).
            // This is what lets the dark box wrap the whole cell (label + checkbox) without
            // shifting the column relative to the body rows.
            colHdrCellContainerStyle = new GUIStyle(parentUI.GetColumnHeaderStyle())
            {
                margin = new RectOffset(0, 0, 4, 4)
            };

            // Bold inner label for the Archive header cell: the container box already supplies
            // the dark background, so the label uses a plain bold style (boxing it again would
            // double-box).
            boldHeaderInnerLabel = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            // The "#" header cell. A row's leading cells start at the row's left edge (a row group
            // has no style, so its first cell's margin passes up to the parent), but a boxed group
            // insets its first child by max(padding.left, the child's margin.left), which put the
            // header's index slot 4 px right of the rows' index cell (GUI-25 dump: "#" at x=38,
            // the index numbers at x=34). Zero left padding plus a blank enable slot with zero
            // left margin puts the slot at the rows' x. The inset moves to the right padding, so
            // the cell keeps the footprint it had (its content sets it past the Width option) and
            // no header to its right moves. The label is left-aligned with the label skin's
            // padding, like the index number and the collapse caret below it, so the "#" glyph
            // starts where they start, and a sort arrow trails it without moving it.
            var hdrPad = colHdrCellContainerStyle.padding;
            indexHeaderEnableStyle = new GUIStyle(GUI.skin.label);
            indexHeaderEnableStyle.margin = new RectOffset(
                0, GUI.skin.label.margin.right, GUI.skin.label.margin.top, GUI.skin.label.margin.bottom);
            indexHeaderCellStyle = new GUIStyle(colHdrCellContainerStyle);
            indexHeaderCellStyle.padding = new RectOffset(
                0,
                IndexHeaderRightPadding(hdrPad.left, hdrPad.right,
                    GUI.skin.label.margin.left, GUI.skin.label.margin.right),
                hdrPad.top, hdrPad.bottom);
            indexHeaderLabelStyle = new GUIStyle(boldHeaderInnerLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };

            // Mission-header row bubble: the section-header box stretched across the whole row.
            // L/R margin + padding are zeroed so the dark bar reaches the row edges and its contents
            // are not inset (the Archive checkbox stays under its column header). Top/bottom padding
            // are EQUAL (2 px each) so the gap above the buttons/text matches the gap below - the old
            // 0-top / 4-bottom split left a visible extra band under the buttons inside the bubble.
            var sectionHeader = parentUI.GetSectionHeaderStyle();
            missionHeaderRowStyle = new GUIStyle(sectionHeader)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 2, 2)
            };

            // Bold transparent text for the index + title sitting on the row bubble (no box of
            // their own, so they don't double-box over the bubble background).
            missionHeaderTextStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };

            // Inline header-bar label (non-bold), vertically centered + stretched so "Loop" and the
            // read-only locked period label share a baseline with each other and the centered buttons.
            // Non-wrapping: on a mission's second line these labels ("Loop", "Looped by route",
            // the locked "~13d-19d (Mun window, varies)" period) share the name cell with the
            // wrapping summary, and a wrapping label there was squeezed into two clipped lines
            // (GUI-17, 2026-09-30). Non-wrapping, each takes its one-line width and the summary
            // (MinWidth 0) wraps into what is left.
            missionHeaderInlineLabel = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                stretchHeight = true,
                wordWrap = false
            };

            // The collapse caret: the plain label skin the Recordings group rows and the
            // Logistics section headers click their carets through, left-aligned like the index
            // number above it so the glyph sits under the digit, and stretched to the line's
            // height so the whole # cell of line 2 (ColW_Index wide, at least one row tall) is
            // the hit area. A label skin has no background, so no frame draws.
            missionCaretStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                stretchHeight = true,
                wordWrap = false
            };

            // Mission summary (T1.1): the left cell of the mission bar's second line, at the
            // title's font size. It WRAPS (see DrawMissionSummaryCell): it shares its line with
            // the action buttons, and the amber countdown at its end is what a clip would cut. It
            // stays secondary to the bold title through weight and a muted text colour, set on the
            // STYLE rather than through GUI.color: GUI.color multiplies every colour on the label,
            // which would dim the amber countdown the rich text carries
            // (MissionPresentation.BuildNarrativeSummaryLine).
            missionSummaryLabel = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                richText = true
            };
            missionSummaryLabel.normal.textColor = MissionSummaryTextColor;
        }

        /// <summary>
        /// Cross-link entry point for the Timeline GoTo button: schedules a scroll to the
        /// mission that owns <paramref name="recordingId"/>, expanding that mission first
        /// when it is collapsed. Called through
        /// <see cref="RecordingsTableUI.ShowMissionForRecording"/>, which owns opening the
        /// window and selecting the Missions tab.
        /// <para>Visible in BOTH complexity modes: the Missions tab is the one Basic keeps
        /// (design `docs/dev/design-ui-basic-advanced.md` section 4.1a), which is exactly why
        /// the cross-link targets it instead of the raw Recordings tab.</para>
        /// <para>Resolution is recording -> <see cref="Recording.TreeId"/> -> that tree's
        /// ORIGINAL mission (<see cref="MissionStore.FindOriginalMission"/>). A tree may carry
        /// several missions (clones); the original is the deterministic, name-linked one, the
        /// same pick <see cref="MissionGroupLink"/> makes. Every failure warns and lands the
        /// player on the tab unscrolled rather than dangling a pending id.</para>
        /// </summary>
        internal void RevealMissionForRecording(string recordingId)
        {
            pendingRevealMissionId = null;
            pendingRevealScrollY = float.NaN;

            // ERS-routed, matching the cross-link contract the old Recordings-tab path carried:
            // navigation resolves to the EFFECTIVE set only, so a superseded recording cannot be
            // navigated to. No behavior change for today's only caller (the Timeline builds its
            // rows from ERS too), but it keeps the invariant in code rather than in the caller's
            // habits - the Kerbals window already has a cross-link of the same shape.
            Recording rec = null;
            var effective = EffectiveState.ComputeERS();
            for (int i = 0; i < effective.Count; i++)
            {
                if (effective[i] != null && effective[i].RecordingId == recordingId)
                {
                    rec = effective[i];
                    break;
                }
            }

            if (rec == null)
            {
                ParsekLog.Warn("UI",
                    $"Cross-link: recording {recordingId ?? "<null>"} not found in the effective " +
                    "recording set; opening the Missions tab unscrolled");
                return;
            }

            if (string.IsNullOrEmpty(rec.TreeId))
            {
                ParsekLog.Warn("UI",
                    $"Cross-link: recording \"{rec.VesselName}\" id={recordingId} has no tree, " +
                    "so it belongs to no mission; opening the Missions tab unscrolled");
                return;
            }

            // Seed the default mission first. A tree committed mid-scene (a post-revert merge, a
            // rapid-switch commit) has none until the Missions tab's own draw seeds it, and the
            // freshest flight is exactly the row a player is most likely to click GoTo on - so
            // without this the common case lands on the warn path one frame before the tab would
            // have created the mission anyway. Calling the same idempotent static the draw and
            // ParsekScenario.OnLoad both call is not a second seam.
            MissionStore.EnsureDefaultsForTrees(RecordingStore.CommittedTrees);

            Mission mission = MissionStore.FindOriginalMission(rec.TreeId);
            if (mission == null)
            {
                // Backstop: the recording names a tree that is not in the committed set, so
                // there is nothing to seed a mission from.
                ParsekLog.Warn("UI",
                    $"Cross-link: tree {rec.TreeId} has no mission for recording " +
                    $"\"{rec.VesselName}\" id={recordingId}; opening the Missions tab unscrolled");
                return;
            }

            // A collapsed destination still lists its bar, but the rows the player navigated to
            // see are hidden, so the reveal EXPANDS it (one click on its collapse caret folds it
            // back). DEFERRED, not written here: this runs inside the Timeline's button handler,
            // i.e. mid-frame, and the flag decides how many rows the mission's block draws.
            // Flipping it between a frame's Layout and Repaint passes changes the control count
            // and throws "Getting control N's position in a group with only M controls". The
            // draw consumes the request on its own Layout pass, where both passes agree.
            if (mission.Collapsed)
            {
                pendingExpandMissionId = mission.Id;
                ParsekLog.Verbose("UI",
                    $"Cross-link: queued expanding collapsed mission '{mission.Name}' id={mission.Id}");
            }

            pendingRevealMissionId = mission.Id;
            ParsekLog.Verbose("UI",
                $"Cross-link: reveal requested for mission '{mission.Name}' id={mission.Id} " +
                $"tree={rec.TreeId} from recording \"{rec.VesselName}\" id={recordingId}");
        }

        /// <summary>
        /// Records the target mission header's position during a draw pass. Only the Repaint
        /// pass carries real rects (Layout reports zeros), so the capture is Repaint-only and
        /// the scroll it produces lands on a LATER pass.
        /// </summary>
        private void CaptureRevealAnchor(Mission mission)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            // Only measure against a layout tree this frame actually solved. Without this the
            // most common GoTo path - window closed, click opens it mid-frame - can measure an
            // unsolved cache, get zeros, and silently consume the target at offset 0.
            if (missionsLayoutFrame != Time.frameCount)
                return;

            // The rect of the header bubble's outermost VERTICAL group (title row + summary
            // line, T1.1), which DrawMissionHeader has just closed. Its y is still the block
            // top, so the scroll math is unchanged by the second line.
            Rect headerRect = GUILayoutUtility.GetLastRect();

            // A real header row is never zero-height; an unsolved layout entry always is. Cheap
            // second line of defence on the same failure, since a wrong measurement here is
            // silent by nature - it looks exactly like a successful capture.
            if (headerRect.height <= 0f)
                return;

            if (!revealFirstHeaderSeen)
            {
                revealFirstHeaderY = headerRect.y;
                revealFirstHeaderSeen = true;
            }

            if (pendingRevealMissionId == null || mission.Id != pendingRevealMissionId)
                return;

            pendingRevealScrollY = Mathf.Max(0f, headerRect.y - revealFirstHeaderY);
            pendingRevealScrollFrame = Time.frameCount;
            LastRevealScrollOffsetForTesting = pendingRevealScrollY;
            pendingRevealMissionId = null;
            ParsekLog.Verbose("UI",
                $"Cross-link: mission '{mission.Name}' id={mission.Id} found at list offset " +
                $"{pendingRevealScrollY.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}px");
        }

        /// <summary>
        /// True when the tab draws its manual-loop surfaces: the per-mission "Loop" toggle,
        /// the loop-period cell beside it, the include checkboxes that pick which intervals
        /// / partner journeys the loop replays, Clone, Delete, "Warp to...", the summary
        /// line's "Next launch T-" piece, and the loop-selection styling
        /// (dimmed excluded rows, the "(partial)" suffix, the chapter "[~]" marker and the
        /// partner-journey rows an include pulls in) - design
        /// `docs/dev/design-ui-basic-advanced.md` section 4.5.
        /// <para>Basic returns false. Manually looping a mission is a presentation choice
        /// with no career consequence; everything above only exists because of it (Delete
        /// and Warp to... included: a mission has something to delete only once it was
        /// cloned, and a next launch only once it loops), and a Basic player reaches the
        /// same flights through Watch, the Log, the Timeline and Logistics without it.</para>
        /// <para>Scope, stated positively so a later reader does not widen it by accident:
        /// the "Looped by route" status label, Watch, Rewind / Forward, Log, Archive,
        /// Re-Fly, the chapter headers and the Docked partner rows all stay. A mission
        /// looped in Advanced keeps looping after the switch (philosophy 1: visibility
        /// only, never behavior) and its ghosts keep flying where Watch reaches them.
        /// Nothing here writes <c>Mission.LoopPlayback</c> or the selection sets either:
        /// state authored in Advanced survives the switch untouched and returns intact
        /// (philosophy 2).</para>
        /// <para>Derived from <see cref="UiSurfaceVisibility.IsVisible"/> so this rule has
        /// the same single decision point as every other gated surface, and takes the mode
        /// as a PARAMETER so it is unit-testable; every draw site passes the frame-latched
        /// <see cref="ParsekUI.AppliedUiComplexityMode"/> (design 7.1), never the raw
        /// setting.</para>
        /// </summary>
        internal static bool ShowsLoopAuthoringControls(UiComplexityMode mode)
        {
            return UiSurfaceVisibility.IsVisible(UiSurface.MissionsLoopControls, mode);
        }

        /// <summary>
        /// Draws the Missions tab's body inside the host Recordings window. The host
        /// (<see cref="RecordingsTableUI"/>) supplies the window chrome (title, tab bar,
        /// breathing-room space, bottom Close button, resize handle, drag, and input lock);
        /// this method only draws the column header + the scrollable mission list, mirroring
        /// the recordings tab's own header-plus-scroll structure (no outer vertical, no Close,
        /// no resize/drag of its own).
        /// </summary>
        internal void DrawMissionsTabContent(WideWindowScroll wideScroll)
        {
            EnsureStyles();

            // No per-pass mode latch here: every gate in this tab reads
            // ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode) at its own draw
            // site, and the applied mode only changes from Update(), outside OnGUI, so those
            // reads already agree across one frame's Layout and Repaint passes. The
            // `basicUiMode` field this used to assign was never read anywhere, and its
            // comment claimed Basic renders "#" as a label while DrawColumnHeader makes it a
            // sort button in BOTH modes - a dead field with a false contract on it.

            // Click outside an active rename field -> commit (mirrors the recordings
            // window's defocus handling).
            if (Event.current.type == EventType.MouseDown && renamingMissionId != null
                && activeMissionRenameRect.width > 0
                && !activeMissionRenameRect.Contains(Event.current.mousePosition))
            {
                CommitMissionRenameById(renamingMissionId);
            }

            // Click outside an active loop-period field -> commit (mirrors RecordingsTableUI's
            // window-level loop-period commit). Uses the field rect captured last frame; runs
            // before the cells are drawn so the in-progress buffer is committed on click-away.
            //
            // Basic draws no period field at all, so there is nothing to click away FROM and
            // loopPeriodEditRect is a rect from whichever frame Advanced last drew one: an
            // edit left open across the switch is DROPPED here rather than committed against
            // a stale rect. Dropping an uncommitted buffer is the Escape path, not data loss
            // (mission.LoopIntervalSeconds is untouched), and dropping it HERE is what keeps
            // the mode visibility-only: leave the focus id armed and the click-away branch
            // below would commit that stale buffer to Mission.LoopIntervalSeconds on the next
            // MouseDown anywhere in the window - a loop write performed in Basic.
            //
            // Every other site that ends an edit (the click-away commit below, Enter, the unit
            // button, loop-off, and the auto / phase-locked branches of the cell) needs the
            // mission's own period cell to draw, which is exactly what Basic does not do, so
            // this is the only teardown that can still run. It ends the edit through
            // CommitMissionLoopPeriodEdit(null) - the documented "just ends the edit" path -
            // so the text buffer and the now-undrawn field's keyboard focus are released the
            // same way they are on every other exit, minus the commit. Runs on every event
            // type: it writes private fields only, never a control.
            bool loopAuthoring = ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode);
            if (!loopAuthoring)
            {
                if (loopPeriodFocusedMissionId != null)
                {
                    ParsekLog.Verbose("Mission",
                        $"Loop period edit on mission id={loopPeriodFocusedMissionId} dropped " +
                        "uncommitted: Basic UI mode draws no loop controls");
                    CommitMissionLoopPeriodEdit(null);
                }
            }
            else if (Event.current.type == EventType.MouseDown && loopPeriodFocusedMissionId != null
                && !loopPeriodEditRect.Contains(Event.current.mousePosition))
            {
                CommitMissionLoopPeriodEdit(FindMissionById(loopPeriodFocusedMissionId));
            }

            // Consume a queued cross-link expand. LAYOUT-pass only: this changes how many rows
            // the mission's block draws, so applying it on any other pass would desync that pass
            // from this frame's solved layout.
            if (pendingExpandMissionId != null && Event.current.type == EventType.Layout)
            {
                Mission toExpand = FindMissionById(pendingExpandMissionId);
                pendingExpandMissionId = null;
                if (toExpand != null && toExpand.Collapsed)
                {
                    toExpand.Collapsed = false;
                    ParsekLog.Info("UI",
                        $"Cross-link: expanded collapsed mission '{toExpand.Name}' id={toExpand.Id} " +
                        "so the revealed rows are listed");
                }
            }

            var trees = RecordingStore.CommittedTrees;
            MissionStore.EnsureDefaultsForTrees(trees);
            var missions = MissionStore.Missions;
            if (missions == null || missions.Count == 0)
            {
                // Capture and apply straddle a frame boundary, so a list emptied in between
                // (a load-time sweep, an internal removal) could otherwise leave an armed
                // offset behind that applies, silently, whenever the list refills.
                DropPendingReveal("the list is empty");
                pendingRevealScrollY = float.NaN;
                GUILayout.Label("No missions recorded yet.", parentUI.GetEmptyStateStyle());
                return;
            }

            // Fixed column-header row (outside the scroll view), styled with the
            // recordings window's shared column-header style. On a screen narrower than
            // the window's natural width the header and the body scroll horizontally
            // together inside one wrapper (a no-op on a screen the window fits).
            wideScroll?.BeginPinnedHeaderArea(parentUI.GetOpaqueWindowStyle());
            DrawColumnHeader();

            // Apply a cross-link scroll captured on an earlier pass. It MUST land before the
            // scroll view opens: writing scrollPos afterwards is read back only next frame.
            if (!float.IsNaN(pendingRevealScrollY))
            {
                // The apply is the very next drawn pass. An older offset means the tab stopped
                // drawing in between (the player switched tabs or closed the window), so the
                // scroll no longer corresponds to anything they did - discard rather than yank
                // the list when they come back.
                int age = Time.frameCount - pendingRevealScrollFrame;
                if (age > RevealScrollMaxAgeFrames)
                {
                    ParsekLog.Verbose("UI",
                        $"Cross-link: discarded a stale missions scroll ({age} frames old)");
                }
                else
                {
                    scrollPos.y = pendingRevealScrollY;
                    ParsekLog.Verbose("UI",
                        "Cross-link: applied deferred missions scroll y=" +
                        pendingRevealScrollY.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                }
                pendingRevealScrollY = float.NaN;
            }
            revealFirstHeaderSeen = false;

            // This pass laid the list out, so rects measured during it are real (see
            // missionsLayoutFrame).
            if (Event.current.type == EventType.Layout)
                missionsLayoutFrame = Time.frameCount;

            scrollPos = GUILayout.BeginScrollView(scrollPos, false, true, GUILayout.ExpandHeight(true));

            // Dark list-area background (matches the recordings window) with no
            // horizontal padding so columns align with the header above.
            GUILayout.BeginVertical(tableBodyBoxStyle);

            // Per-tree index numbers (1-based, in committed-tree order). Clones share a
            // tree, so they share its index; renaming a mission never changes it.
            Dictionary<string, int> treeIndex = BuildTreeIndexMap(trees);

            // Snapshot + sort for display. Clone / Delete (which mutate the store) are
            // invoked from within the draw loop; the change takes effect next frame.
            var ordered = BuildSortedMissionRows(missions, trees, treeIndex);
            int missionCount = 0;
            int rowCount = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                Mission mission = ordered[i].mission;
                RecordingTree tree = FindTree(trees, mission.TreeId);
                if (tree == null)
                    continue;
                missionCount++;
                // Collapse: read ONCE, before the bar draws. The bar's own collapse caret
                // writes the flag mid-pass, and the rows below it must follow the value
                // this pass's Layout used, or the control count desyncs; the click shows next
                // frame. A collapsed mission keeps its whole two-line bar.
                bool collapsed = mission.Collapsed;

                var (structure, view) = GetMissionView(tree);

                // Mission periodicity (Phase-1 / Tier-1 solution), computed ONCE per mission here
                // and shared by the header's period cell + the summary's countdown, so we
                // extract/solve only once per frame per mission. Only looping missions need it; a
                // non-looping mission gets the no-solution default (no countdown).
                MissionPeriodicityDisplay periodicity = mission.LoopPlayback
                    ? ComputeMissionPeriodicity(mission, view)
                    : default;

                // The bar's Start event cell is the first vessel row's start event. A collapsed
                // mission draws only its bar and needs no names, so it reads that event off the
                // composition roots instead of building the named rows.
                string startEventText = collapsed
                    ? MissionVesselRowBuilder.FirstRowStartEvent(GetCompositionRoots(tree))
                    : MissionPresentation.MissionStartEventText(GetVesselRows(tree));
                DrawMissionHeader(mission, ordered[i].index, view, periodicity,
                    GetSummaryFacts(tree), startEventText, collapsed);
                CaptureRevealAnchor(mission);
                if (collapsed)
                    continue;
                var vesselRows = GetVesselRows(tree);

                // T2.2: the FLATTENED per-vessel rows - one row per physical vessel / EVA
                // kerbal, depth = separation lineage only, the event chain inline; each
                // vessel's structural intervals are an expandable detail (Advanced) with the
                // same independent include checkboxes bound to Mission.ExcludedIntervalKeys -
                // no cascade (the per-vessel checkbox expands to the vessel's own explicit
                // keys). The row derivations (T1.3 delta labels, T1.4 dock-partner naming)
                // read this tree's own structure / through-line view.
                var rowCtx = new RowDeriveContext
                {
                    Structure = structure, View = view, Mission = mission,
                    VesselNames = GetVesselNames(tree),
                };
                // Chapter grouping (design 7.2): armed only for this mission's own rows, so a
                // header appears above the vessel row that carries a chapter's first interval.
                // Cleared in finally so a mid-draw exception cannot leak one mission's chapters
                // onto the next. Under the T2.2 flattened layout the header anchors to the
                // VESSEL row containing the chapter's anchor interval (a SwitchContinuation
                // chapter's first interval can sit mid-line, folded into the pre-switch
                // through-line, so the row - not an interval sub-row - is the anchor surface).
                ArmChapters(mission, tree);
                try
                {
                    for (int r = 0; r < vesselRows.Count; r++)
                    {
                        bool isLast = r == vesselRows.Count - 1;
                        rowCount += TryDrawChapterHeaderRowForVesselRow(mission, vesselRows[r]);
                        rowCount += DrawVesselRow(vesselRows[r], mission, 1, isLast, rowCtx);
                    }
                }
                finally
                {
                    DisarmChapters();
                }

                // M-MIS-8: cross-tree partner-journey affordance - one row per derived foreign
                // dock link on this tree's vessel(s), with the journey's intervals as child rows
                // when the link is included (explicit player action; default off).
                rowCount += DrawForeignDockLinkRows(mission, tree, trees);
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            wideScroll?.EndPinnedHeaderArea();

            // A target that survived a full Repaint pass is not in the list (its tree is gone,
            // or a filter this method does not clear is dropping it). Drop it rather than let it
            // sit and fire on some unrelated future frame.
            if (Event.current.type == EventType.Repaint)
                DropPendingReveal("it was not drawn in the mission list");

            ParsekLog.VerboseRateLimited("UI", "missions-tab-draw",
                $"Missions tab: missions={missionCount} rows={rowCount}", 5.0);
        }

        // Abandons an unconsumed cross-link target, loudly. Silent expiry would leave the player
        // looking at an unscrolled list with nothing in the log to explain it. Only the id is
        // dropped; a captured offset is separately age-checked at the apply site, and the
        // empty-list branch clears it outright.
        private void DropPendingReveal(string reason)
        {
            if (pendingRevealMissionId == null)
                return;
            ParsekLog.Warn("UI",
                $"Cross-link: dropping reveal of mission {pendingRevealMissionId} - {reason}");
            pendingRevealMissionId = null;
        }

        // The composition layer labels a merge-started interval through MergeEventName ->
        // BranchEventName, so the words are taken FROM that function rather than duplicated here
        // (a relabel there must not silently switch this naming off). Gating on the label keeps
        // the graph lookup off the hot path for the ~99% of rows that start at a launch /
        // decouple / EVA edge. Null cause: a Dock/Board branch point's MergeCause ("DOCK",
        // "BOARD") never matches BranchEventName's cause switch, so the
        // type arm is what both a real row and this probe resolve through.
        private static readonly string DockedEventLabel =
            MissionCompositionBuilder.BranchEventName(BranchPointType.Dock, null);
        private static readonly string BoardedEventLabel =
            MissionCompositionBuilder.BranchEventName(BranchPointType.Board, null);

        // Merge-leg UTs and interval edges are the same doubles; this only absorbs
        // representation noise (mirrors MissionCrossTreeDock.WindowEpsilon).
        private const double DockPartnerUTEpsilon = 1e-3;

        // Memo-key separator: a character no tree id or interval key can contain, written as an
        // escape so it stays visible in a diff (a literal control character reads as a MISSING
        // separator). Interval keys append "/segN" / "@dockM" to a recording id, so a printable
        // separator would not be provably collision-free.
        private const string KeySeparator = "";

        // Sizes the reserved line-2 slot like a one-line Interact button.
        private static readonly GUIContent InteractReservedSlotContent = new GUIContent(" ");

        // Last logged loop-row state per mission id, so the per-frame draw logs only changes.
        private readonly Dictionary<string, string> lastMissionLoopRowState =
            new Dictionary<string, string>(System.StringComparer.Ordinal);

        /// <summary>The route label's Advanced text (the loop row's vocabulary).</summary>
        internal const string RouteBoundLabelAdvanced = "Looped by route";

        /// <summary>The route label's Basic text: Basic reads no loop word.</summary>
        internal const string RouteBoundLabelBasic = "Run by route";

        // Last logged "Warp to..." state per mission id ("enabled" or the greyed reason), so the
        // per-frame enable decision logs only when it changes.
        private readonly Dictionary<string, string> lastMissionWarpState =
            new Dictionary<string, string>(System.StringComparer.Ordinal);

        private void LogSortChanged()
        {
            ParsekLog.Verbose("UI",
                $"Missions sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
        }

        // Finds a mission by id (for the window-level click-away commit). Null if not found.
        /// <summary>This window's name for <see cref="MissionStore.FindById"/>, kept so
        /// the draw code reads the way it always did while the lookup itself has one
        /// owner.</summary>
        private static Mission FindMissionById(string id) => MissionStore.FindById(id);

        private static string CollapseKey(Mission mission, string headId)
        {
            return mission.Id + ":" + headId;
        }

        // Column-header row: a checkbox column, a wide expanding "Vessel" column, then
        // Start time / Start event / End event / End time, all in the recordings
        // window's shared column-header style.
        private void DrawColumnHeader()
        {
            var colHdr = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            // Sortable headers (click to sort, click again to flip direction), mirroring the
            // recordings window. The first (index) column, the expanding name column, and the
            // Start time column sort Missions; the per-leg event columns stay static.
            // Every header cell is forced to ColHeaderHeight so the row reads as one uniform band
            // (the Archive cell carries a toggle and would otherwise be taller), matching the
            // recordings tab. The sortable headers take the height via DrawSortableHeaderCore.
            //
            // First column = a merged [enable+index] cell of the SAME width as the recordings tab's
            // first header (Width(ColW_Enable + ColW_Index + 8)), so the expanding "Missions and
            // vessels" column starts at the same x as the recordings "Name" column. There is no
            // per-row enable in missions, so the enable slot is blank; the sortable "#" lives inline
            // (indexHeaderLabelStyle) in the index slot so the dark container is not double-boxed,
            // and the cell is laid out like a row's leading cells (indexHeaderCellStyle) so the
            // "#" sits over the index numbers and carets.
            GUILayout.BeginHorizontal(indexHeaderCellStyle,
                GUILayout.Width(ColW_Enable + ColW_Index + 8f), GUILayout.Height(ColHeaderHeight));
            GUILayout.Label("", indexHeaderEnableStyle, GUILayout.Width(ColW_Enable));
            // Sortable "#" in BOTH modes: the per-tree index number now shows on mission rows in
            // Basic too (the T1.7 hide read as a rendering gap - owner playtest 2026-08-20), so
            // the header that sorts by it comes back with it.
            {
                string hashArrow = (sortColumn == MissionSortColumn.Index)
                    ? (sortAscending ? " \u25b2" : " \u25bc") : "";
                if (GUILayout.Button("#" + hashArrow, indexHeaderLabelStyle, GUILayout.Width(ColW_Index)))
                {
                    if (sortColumn == MissionSortColumn.Index) sortAscending = !sortAscending;
                    else { sortColumn = MissionSortColumn.Index; sortAscending = true; }
                    LogSortChanged();
                }
            }
            GUILayout.EndHorizontal();
            parentUI.DrawSortableHeaderCore("Missions and vessels", MissionSortColumn.Name,
                ref sortColumn, ref sortAscending, 0f, true, LogSortChanged, ColHeaderHeight);

            parentUI.DrawSortableHeaderCore("Start time", MissionSortColumn.StartTime,
                ref sortColumn, ref sortAscending, ColW_StartTime, false, LogSortChanged, ColHeaderHeight);
            GUILayout.Label("Start event", colHdr, GUILayout.Width(ColW_StartEvent), GUILayout.Height(ColHeaderHeight));
            GUILayout.Label("End event", colHdr, GUILayout.Width(ColW_EndEvent), GUILayout.Height(ColHeaderHeight));
            GUILayout.Label("End time", colHdr, GUILayout.Width(ColW_EndTime), GUILayout.Height(ColHeaderHeight));

            // Interact column header: a plain label over the one right-hand column that carries
            // every per-row action (Watch, Rewind / Forward, Fly / Stash + Seal, Go to). The
            // dark box (colHdrCellContainerStyle, zero horizontal margin) spans exactly
            // ColW_Interact, like each row's margin-0 Interact cell below it, so the data columns
            // left of it line up with their headers.
            GUILayout.BeginHorizontal(colHdrCellContainerStyle, GUILayout.Width(ColW_Interact), GUILayout.Height(ColHeaderHeight));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Interact", boldHeaderInnerLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Reserve the vertical scrollbar's footprint so the fixed header's right edge
            // aligns with the row cells' right edges (the scroll view always shows a
            // vertical scrollbar). A scroll view spends fixedWidth + margin.left on the bar
            // (16 in KSP's skin), not fixedWidth alone: reserving 15 left the header's
            // expanding column 1 px wider than the rows' and every data column 1 px right of
            // its cells. This tab's body box has no right padding and its last cell no right
            // margin, so it owes the bare footprint, not the Recordings tab's gutter
            // (footprint + one cell margin).
            GUILayout.Space(ParsekUI.VerticalScrollbarFootprintWidth());

            GUILayout.EndHorizontal();
        }

    }
}
