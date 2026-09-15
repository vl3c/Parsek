using System;
using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Kerbals window - two read-only COLUMN TABLES:
    ///   * "Roster" (seam tab token <c>roster</c>): one row per kerbal the player can
    ///     see, with what he is doing right now, when that started and how his last
    ///     flight ended. Rows Parsek has something to say about are listed first; plain
    ///     available kerbals with no recorded flight collapse under one fold row.
    ///   * "Flights" (seam tab token <c>outcomes</c>): per-kerbal flight history, one
    ///     row per MISSION (one recording tree) with its calendar start date, mission
    ///     name, final outcome word and the stand-in who flew it. The segments a mission
    ///     collapsed are counted and listed in the outcome cell's hover text.
    ///
    /// <para>Both tabs draw their column-header row and their body rows with the shared
    /// inset containers (<c>ParsekUI.GetTableRowStyle</c> /
    /// <c>GetTableBodyBoxStyle</c>), which is what keeps every cell under its own header
    /// - see <c>ParsekUI.TableRowHorizontalInsetPx</c> and
    /// <c>TableRowInsetAlignmentTests</c>. The header rows sit INSIDE the same scroll
    /// view as the body, so neither reserves a scrollbar gutter.</para>
    ///
    /// <para>Every row text, status word, date cell and both sort orders are derived in
    /// the pure <see cref="KerbalsPresentation"/>; this file gathers the live inputs and
    /// draws. Full contract: <c>docs/dev/design-gui-kerbals-window.md</c>.</para>
    /// </summary>
    internal class KerbalsWindowUI
    {
        private readonly ParsekUI parentUI;

        private bool showKerbalsWindow;
        private Rect kerbalsWindowRect;
        /// <summary>
        /// The live window rect, readable and writable from outside the draw pass.
        /// <para>Two consumers: an in-game test that needs the measured rect, and the
        /// automation-only <c>UiAction op=rect</c> seam op, which places and enlarges a
        /// window so one capture shows more rows than the default size fits. Writing it is
        /// safe before the first draw as well as after: <c>DrawIfOpen</c> only seeds its
        /// default when <c>width &lt; 1</c>, so a commanded rect suppresses the seed rather
        /// than being overwritten by it.</para>
        /// </summary>
        internal Rect WindowRectForTesting
        {
            get { return kerbalsWindowRect; }
            set { kerbalsWindowRect = value; }
        }

        private bool kerbalsWindowHasInputLock;
        private bool isResizingKerbalsWindow;
        private Vector2 kerbalsScrollPos;
        // Internal: see CareerStateWindowUI.CareerStateInputLockId (design 7.2 close set).
        internal const string KerbalsInputLockId = "Parsek_KerbalsWindow";

        // ---- column widths, per tab ----
        // Roster: the Name cell holds "Valentina Kerman [Scientist]" (28 chars) and the
        // Status cell "Reserved for Valentina Kerman until Y1, D23" (43); Since and Date
        // hold one compact date, which KSPUtil.PrintDateCompact renders down to the
        // minute ("Y1, D01, 02:29", 14 chars = 98 px at the skin's ~7 px advance) - the
        // first flight measured that clipped at 80. Last flight expands.
        private const float ColW_RosterName = 190f;
        /// <summary>Internal because <see cref="KerbalsPresentation"/> budgets the
        /// "Status now" text against it: the stand-in form only carries its vessel inline
        /// when the composed string fits this column, otherwise the vessel moves into the
        /// cell's hover text.</summary>
        internal const float ColW_RosterStatus = 220f;
        private const float ColW_RosterSince = 130f;
        // Flights: Date holds the same compact date, Mission a mission name, Outcome the
        // longest outcome word ("Outcome unknown", 15 chars), Crew note expands.
        private const float ColW_FlightDate = 130f;
        private const float ColW_FlightMission = 210f;
        private const float ColW_FlightOutcome = 110f;

        /// <summary>
        /// Minimum width, derived from the Roster tab (the wider of the two tables) rather
        /// than guessed. Every term is measured off the census dumps, whose header cells
        /// sit at x=284 / 478 / 702 / 836 inside a window placed at x=270:
        ///
        /// <para>540 px of fixed columns (190 + 220 + 130) + 12 px of inter-column cell
        /// margin (the 4 px the skin adds between neighbours, three times: 478-284=194,
        /// 702-478=224, 836-702=134) + 4 px of margin before the expanding column + 100 px
        /// of readable sliver for it + 28 px of window chrome (14 px per side: 284-270) +
        /// 16 px of scrollbar gutter (<c>ParsekUI.DefaultVerticalScrollbarFootprintWidth</c>)
        /// = <b>700</b>.</para>
        ///
        /// <para>The previous 570 was 540 + 30 and left out the chrome, the margins and the
        /// gutter, so the smallest size the player could drag to clipped the fixed columns -
        /// IMGUI does not reflow a pinned width. Recorded in
        /// <c>docs/dev/design-gui-kerbals-window.md</c> section 5.</para>
        /// </summary>
        internal const float MinWindowWidth = 700f;
        internal const float MinWindowHeight = 150f;

        /// <summary>
        /// The key this window's IMGUI window id is hashed from. Named once and used at
        /// BOTH the <c>ClickThruBlocker.GUILayoutWindow</c> call below and
        /// <c>UiWindowHandle.GetWindowId</c> (wired by
        /// <c>ParsekTestCommandAddon.ResolveWindowHandle</c>), which the <c>UiAction op=find</c>
        /// seam uses to scope a captured GUI tree to THIS window's subtree. Two copies of
        /// the literal would let the seam search the wrong window's children and answer a
        /// plausible rect for a control in another window.
        /// </summary>
        internal const string WindowIdKey = "ParsekKerbals";

        /// <summary>
        /// First-open width: the Roster tab's 540 px of fixed columns plus 200 px for the
        /// expanding "Last flight" column plus the window chrome, rounded to 760. The old
        /// 410 was half of Career's 820 so the two could sit side by side; two column
        /// tables do not fit in 410, and 760 still leaves Career's own 820 room on a
        /// 1920-wide screen.
        /// </summary>
        internal const float DefaultWindowWidth = 760f;
        private const float DefaultWindowHeight = 400f;
        private Rect lastKerbalsWindowRect;

        // Bottom "hovered control help text" strip. See TooltipEchoBox for why it is a
        // permanently visible box of constant height. Default spacing (3f) matches every
        // other window's SpacingSmall.
        private readonly TooltipEchoBox tooltipEcho = new TooltipEchoBox();

        private KerbalsViewModel? cachedVM;

        /// <summary>
        /// The built view model, writable from outside the draw pass. The expand seam's
        /// key enumerations read it (a key the window does not draw must never be
        /// offered), and in production it is seeded by the first drawn frame - which is
        /// why <c>op=expand</c> is two-phase. Exposed so the seam's key / count contracts
        /// are unit-testable headlessly, where no frame ever draws.
        /// </summary>
        internal KerbalsViewModel? CachedViewModelForTesting
        {
            get { return cachedVM; }
            set { cachedVM = value; }
        }

        // Fold-toggle arrow glyphs; match the chain-block pattern in RecordingsTableUI.
        private const string FoldedArrow = "\u25b6";
        private const string UnfoldedArrow = "\u25bc";

        /// <summary>The seam key for the Roster tab's one fold row (the plain-kerbal
        /// bucket). Parenthesised so it cannot collide with a kerbal name, which is what
        /// the other keys in that set are.</summary>
        internal const string PlainBucketKey = "(available)";

        // Transient fold state for Flights groups. Default-unfolded means we only store
        // names that are currently folded, so HashSet fits the access pattern.
        // InvalidateCache does NOT clear this - fold is UI preference, not data.
        internal readonly HashSet<string> foldedKerbals = new HashSet<string>(StringComparer.Ordinal);

        // Transient expand state for a Roster row's replacement-chain view.
        // Default-collapsed - the set only contains kerbal names currently expanded, so
        // the initial view is one line per kerbal. Orthogonal to data, so not cleared by
        // InvalidateCache.
        private readonly HashSet<string> expandedSlots = new HashSet<string>(StringComparer.Ordinal);

        // The Roster tab's plain-kerbal bucket. Closed by default (the "minimal,
        // need-to-know" ruling); transient like the two sets above.
        private bool plainBucketExpanded;

        private GUIStyle grayStyle;
        private GUIStyle columnHeaderStyle;
        private GUIStyle groupHeaderStyle;
        private GUIStyle deadStyle;
        private GUIStyle recoveredStyle;
        private GUIStyle aboardStyle;
        private GUIStyle activeChainStyle;
        private GUIStyle displacedStyle;
        // Toggle button style for tab bar - mirrors CareerStateWindowUI / TimelineWindowUI:
        // the "on" background is copied from GUI.skin.button.active so the selected tab
        // looks visibly pushed in.
        private GUIStyle toggleButtonStyle;

        // Transient tab selection for the Kerbals window. Matches the Career State pattern.
        private int selectedTab;

        /// <summary>
        /// The transient tab selection, exposed the way <c>RecordingsTableUI</c> and
        /// <c>CareerStateWindowUI</c> expose theirs: so a test can pin the order, and so the
        /// automation-only <c>UiAction op=tab</c> seam op can select a tab for a screenshot
        /// without a synthetic click. Transient either way - nothing persists it.
        /// </summary>
        internal int SelectedTabForTesting
        {
            get { return selectedTab; }
            set { selectedTab = value; }
        }

        /// <summary>How many tabs the toolbar draws. Read by the seam's tab-vocabulary
        /// coverage cell so the wire token list cannot drift from the real toolbar.</summary>
        internal static int TabCountForTesting { get { return TabLabels.Length; } }

        // GUIContent (not bare strings) so each tab explains itself in the bottom help
        // strip on hover - the tab names are the two least obvious words in the window.
        private static readonly GUIContent[] TabLabels = new[]
        {
            new GUIContent("Roster",
                "What each kerbal is doing now: available, aboard, reserved, standing in, retired or lost."),
            new GUIContent("Flights",
                "Every mission a kerbal flew, and how each one ended for him.")
        };

        internal struct KerbalsViewModel
        {
            public List<CrewEndStateEntry> EndStates;
            public List<KerbalsPresentation.FlightGroup> Flights;
            public KerbalsPresentation.RosterRowSet Roster;
        }

        internal enum ChainMemberStatus
        {
            Active,
            Retired,
            Displaced,
            Unknown
        }

        internal struct ChainMember
        {
            public string Name;
            public int ChainIndex;
            public ChainMemberStatus Status;
        }

        /// <summary>One kerbal's end state on ONE recorded SEGMENT. A mission is a whole
        /// tree of these; <see cref="KerbalsPresentation.BuildFlightRows"/> collapses the
        /// segments of one tree into one Flights row.</summary>
        internal struct CrewEndStateEntry
        {
            public string KerbalName;
            public string RecordingName;
            public string RecordingId;
            /// <summary>The tree this segment belongs to, which is the MISSION key the
            /// Flights tab groups by. Null on a pre-tree / standalone recording, and the
            /// builder then keys that segment by its own recording id, so such a recording
            /// stays a row of its own.</summary>
            public string TreeId;
            /// <summary>The segment's start UT. The Flights row's Date cell is the
            /// EARLIEST of these across the kerbal's segments of one tree.</summary>
            public double StartUT;
            public double EndUT;
            public KerbalEndState EndState;
        }

        internal delegate int ActiveChainIndexFunc(KerbalsModule.KerbalSlot slot);

        public bool IsOpen
        {
            get { return showKerbalsWindow; }
            set { showKerbalsWindow = value; }
        }

        internal KerbalsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void InvalidateCache()
        {
            cachedVM = null;
            ParsekLog.Verbose("UI", "KerbalsWindow: cache invalidated");
        }

        // ------------------------- the live-crew refresh -------------------------

        /// <summary>
        /// The stock GameEvents that change what the two tabs read off LIVE state, as
        /// opposed to off the ledger. <c>LedgerOrchestrator.OnTimelineDataChanged</c>
        /// covers every ledger-side change, but the view model is ALSO built from the stock
        /// roster walk (<c>CrewRoster.Crew</c> / <c>.Applicants</c> / <c>.Tourist</c>) and
        /// from the live crew-to-vessel map (<c>GatherAssignedVessels</c>), and neither
        /// moves the ledger: a transfer, an EVA, a board, a hire or a dismissal would leave
        /// a stale `Assigned (&lt;vessel&gt;)` cell - or a missing row - until some
        /// unrelated ledger write happened to drop the cache.
        ///
        /// <para>Every one funnels into <see cref="OnLiveCrewStateChanged"/>, which drops
        /// the cache and logs ONE line naming the event, so the log says which of the eight
        /// refreshed the tab. Subscribed and unsubscribed exactly where the timeline hook
        /// is (<c>ParsekUI</c>'s two constructors and <c>ParsekUI.Cleanup</c>), so the
        /// window's subscriptions live and die with its owning scene's ParsekUI.</para>
        ///
        /// <para>Which events, and why each one: the table is in
        /// <c>docs/dev/design-gui-kerbals-window.md</c> section 7.</para>
        /// </summary>
        internal void SubscribeLiveCrewEvents()
        {
            GameEvents.onVesselCrewWasModified.Add(OnCrewVesselEvent);
            GameEvents.onVesselChange.Add(OnCrewVesselChangeEvent);
            GameEvents.onCrewTransferred.Add(OnCrewTransferredEvent);
            GameEvents.onCrewOnEva.Add(OnCrewEvaEvent);
            GameEvents.onCrewBoardVessel.Add(OnCrewBoardEvent);
            GameEvents.onKerbalAdded.Add(OnKerbalRosterEvent);
            GameEvents.onKerbalRemoved.Add(OnKerbalRosterEvent);
            GameEvents.onKerbalStatusChange.Add(OnKerbalStatusEvent);
            ParsekLog.Verbose("UI",
                "KerbalsWindow: subscribed to 8 live-crew GameEvents");
        }

        internal void UnsubscribeLiveCrewEvents()
        {
            GameEvents.onVesselCrewWasModified.Remove(OnCrewVesselEvent);
            GameEvents.onVesselChange.Remove(OnCrewVesselChangeEvent);
            GameEvents.onCrewTransferred.Remove(OnCrewTransferredEvent);
            GameEvents.onCrewOnEva.Remove(OnCrewEvaEvent);
            GameEvents.onCrewBoardVessel.Remove(OnCrewBoardEvent);
            GameEvents.onKerbalAdded.Remove(OnKerbalRosterEvent);
            GameEvents.onKerbalRemoved.Remove(OnKerbalRosterEvent);
            GameEvents.onKerbalStatusChange.Remove(OnKerbalStatusEvent);
            ParsekLog.Verbose("UI",
                "KerbalsWindow: unsubscribed from 8 live-crew GameEvents");
        }

        // One typed shim per GameEvents delegate shape, each funnelling into the single
        // handler below. The shims exist only because the eight events carry five
        // different payload types; nothing reads the payload.
        private void OnCrewVesselEvent(Vessel v)
        {
            OnLiveCrewStateChanged("onVesselCrewWasModified");
        }

        private void OnCrewVesselChangeEvent(Vessel v)
        {
            OnLiveCrewStateChanged("onVesselChange");
        }

        private void OnCrewTransferredEvent(
            GameEvents.HostedFromToAction<ProtoCrewMember, Part> data)
        {
            OnLiveCrewStateChanged("onCrewTransferred");
        }

        private void OnCrewEvaEvent(GameEvents.FromToAction<Part, Part> data)
        {
            OnLiveCrewStateChanged("onCrewOnEva");
        }

        private void OnCrewBoardEvent(GameEvents.FromToAction<Part, Part> data)
        {
            OnLiveCrewStateChanged("onCrewBoardVessel");
        }

        private void OnKerbalRosterEvent(ProtoCrewMember crew)
        {
            OnLiveCrewStateChanged("onKerbalAdded/Removed");
        }

        private void OnKerbalStatusEvent(
            ProtoCrewMember crew,
            ProtoCrewMember.RosterStatus oldStatus,
            ProtoCrewMember.RosterStatus newStatus)
        {
            OnLiveCrewStateChanged("onKerbalStatusChange");
        }

        /// <summary>
        /// The one handler behind all eight shims: drops the cached view model and logs
        /// which event did it. Pure apart from the field write, so the log contract is
        /// unit-testable - see <see cref="DescribeLiveCrewRefresh"/>.
        /// </summary>
        private void OnLiveCrewStateChanged(string eventName)
        {
            bool hadCache = cachedVM != null;
            cachedVM = null;
            ParsekLog.Verbose("UI", DescribeLiveCrewRefresh(eventName, hadCache));
        }

        /// <summary>The single Verbose line the live-crew refresh writes. Pure, so the
        /// wording is pinned by a unit cell rather than by a flight.</summary>
        internal static string DescribeLiveCrewRefresh(string eventName, bool hadCache)
        {
            return "KerbalsWindow: live crew state changed (" + (eventName ?? "?")
                   + ") - cache " + (hadCache ? "invalidated" : "already empty");
        }

        /// <summary>The events <see cref="SubscribeLiveCrewEvents"/> wires, named once so a
        /// unit cell can assert the set rather than re-listing it. Documentation and test
        /// surface only - the subscribe call above is the production truth, and
        /// <c>KerbalsLiveCrewRefreshTests</c> reads THIS file to keep the two in step.</summary>
        internal static readonly string[] LiveCrewRefreshEvents =
        {
            "onVesselCrewWasModified",
            "onVesselChange",
            "onCrewTransferred",
            "onCrewOnEva",
            "onCrewBoardVessel",
            "onKerbalAdded",
            "onKerbalRemoved",
            "onKerbalStatusChange"
        };

        // ------------------------- the op=expand seam -------------------------

        /// <summary>Every Roster row key the expand seam can drive: each kerbal whose row
        /// carries a replacement chain, plus the plain-kerbal fold row. Enumerated off the
        /// built view model so a key the window does not draw is never offered.</summary>
        internal List<string> EnumerateRosterExpandKeysForTesting()
        {
            var keys = new List<string>();
            if (cachedVM == null) return keys;
            KerbalsPresentation.RosterRowSet set = cachedVM.Value.Roster;
            AppendChainKeys(set.Involved, keys);
            AppendChainKeys(set.Plain, keys);
            keys.Add(PlainBucketKey);
            return keys;
        }

        private static void AppendChainKeys(
            List<KerbalsPresentation.RosterRow> rows, List<string> keys)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Chain != null && rows[i].Chain.Count > 0)
                    keys.Add(rows[i].FoldKey);
            }
        }

        /// <summary>Writes one Roster expand key. Returns whether the state changed.</summary>
        internal bool SetRosterExpandedForTesting(string key, bool expanded)
        {
            if (string.Equals(key, PlainBucketKey, StringComparison.Ordinal))
            {
                if (plainBucketExpanded == expanded) return false;
                plainBucketExpanded = expanded;
                return true;
            }
            return expanded ? expandedSlots.Add(key) : expandedSlots.Remove(key);
        }

        /// <summary>How many Roster keys are expanded right now (the seam's
        /// <c>expanded=</c> term).</summary>
        internal int ExpandedRosterCountForTesting
        {
            get { return expandedSlots.Count + (plainBucketExpanded ? 1 : 0); }
        }

        /// <summary>Every Flights group key the expand seam can drive: one per kerbal with
        /// a recorded flight.</summary>
        internal List<string> EnumerateFlightExpandKeysForTesting()
        {
            var keys = new List<string>();
            if (cachedVM == null) return keys;
            List<KerbalsPresentation.FlightGroup> groups = cachedVM.Value.Flights;
            if (groups == null) return keys;
            for (int i = 0; i < groups.Count; i++) keys.Add(groups[i].FoldKey);
            return keys;
        }

        /// <summary>Writes one Flights group key. INVERTED on the production side
        /// (<c>foldedKerbals</c> holds what is FOLDED) - the wire always speaks
        /// "expanded".</summary>
        internal bool SetFlightExpandedForTesting(string key, bool expanded)
        {
            return expanded ? foldedKerbals.Remove(key) : foldedKerbals.Add(key);
        }

        /// <summary>How many Flights groups are expanded: every group minus the folded
        /// ones. Clamped at zero - <c>foldedKerbals</c> can hold a name the current view
        /// model no longer carries, and a negative <c>expanded=</c> would read as a seam
        /// defect.</summary>
        internal int ExpandedFlightCountForTesting
        {
            get
            {
                List<string> keys = EnumerateFlightExpandKeysForTesting();
                int folded = 0;
                for (int i = 0; i < keys.Count; i++)
                    if (foldedKerbals.Contains(keys[i])) folded++;
                return Math.Max(0, keys.Count - folded);
            }
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showKerbalsWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (kerbalsWindowRect.width < 1f)
            {
                kerbalsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    DefaultWindowWidth,
                    DefaultWindowHeight);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Kerbals window initial position: x={kerbalsWindowRect.x.ToString("F0", ic)} y={kerbalsWindowRect.y.ToString("F0", ic)}");
            }

            ParsekUI.HandleResizeDrag(ref kerbalsWindowRect, ref isResizingKerbalsWindow,
                MinWindowWidth, MinWindowHeight, "Kerbals window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                kerbalsWindowRect = ClickThruBlocker.GUILayoutWindow(
                    WindowIdKey.GetHashCode(),
                    kerbalsWindowRect,
                    DrawKerbalsWindow,
                    "Parsek - Kerbals",
                    opaqueWindowStyle,
                    GUILayout.Width(kerbalsWindowRect.width),
                    GUILayout.Height(kerbalsWindowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition("Kerbals", ref lastKerbalsWindowRect, kerbalsWindowRect);

            if (kerbalsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!kerbalsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, KerbalsInputLockId);
                    kerbalsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        /// <summary>
        /// Whether this window currently holds its KSP input lock (diagnostic read for the
        /// design-7.2 close handler; see CareerStateWindowUI.HasInputLock).
        /// </summary>
        internal bool HasInputLock => kerbalsWindowHasInputLock;

        internal void ReleaseInputLock()
        {
            if (!kerbalsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(KerbalsInputLockId);
            kerbalsWindowHasInputLock = false;
        }

        private void EnsureStyles()
        {
            // Column-header style is shared across the mod via ParsekUI; reassign every
            // draw so any ParsekUI-level updates flow through.
            columnHeaderStyle = parentUI.GetColumnHeaderStyle();
            if (grayStyle != null) return;
            grayStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) }
            };
            groupHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
            deadStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.95f, 0.45f, 0.45f) }
            };
            recoveredStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.55f, 0.85f, 0.55f) }
            };
            aboardStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.6f, 0.8f, 0.95f) }
            };
            activeChainStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.6f, 0.8f, 0.95f) }
            };
            displacedStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
            };
            // Tab bar button: selected tab looks pressed via onNormal.background copied
            // from GUI.skin.button.active.background (matches CareerStateWindowUI and
            // TimelineWindowUI toggle idiom).
            toggleButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            toggleButtonStyle.onNormal.background = GUI.skin.button.active.background;
            toggleButtonStyle.onHover.background = GUI.skin.button.active.background;
            toggleButtonStyle.onNormal.textColor = Color.white;
            toggleButtonStyle.onHover.textColor = Color.white;
        }

        private void DrawKerbalsWindow(int windowID)
        {
            EnsureStyles();
            // Breathing room below the title bar - matches Timeline's visual spacing.
            GUILayout.Space(5);

            if (cachedVM == null) cachedVM = GatherViewModel();
            var vm = cachedVM.Value;

            // Tab bar - same idiom as CareerStateWindowUI.
            int newTab = GUILayout.Toolbar(selectedTab, TabLabels, toggleButtonStyle);
            if (newTab != selectedTab)
            {
                SwitchTab(selectedTab, newTab);
                selectedTab = newTab;
                kerbalsScrollPos.y = 0f;
            }

            kerbalsScrollPos = GUILayout.BeginScrollView(kerbalsScrollPos, GUILayout.ExpandHeight(true));

            switch (selectedTab)
            {
                case 0:
                    DrawRosterTab(vm.Roster);
                    break;

                case 1:
                    if (vm.Flights == null || vm.Flights.Count == 0)
                    {
                        GUILayout.Label("No recorded flights with crew yet.", grayStyle);
                    }
                    else
                    {
                        DrawFlightsColumnHeader();
                        GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
                        for (int i = 0; i < vm.Flights.Count; i++)
                            DrawFlightGroup(vm.Flights[i]);
                        GUILayout.EndVertical();
                    }
                    break;
            }

            GUILayout.EndScrollView();

            // Bottom "hovered control help text" strip (shared house helper), drawn after
            // the tables (so the live GUI.tooltip read sees a hovered row) and directly
            // above the Close button - the house ordering every Parsek window uses. Fixed
            // two-line height, always present.
            tooltipEcho.Draw();

            if (GUILayout.Button("Close"))
            {
                showKerbalsWindow = false;
                ParsekLog.Verbose("UI", "Kerbals window closed via button");
            }

            ParsekUI.DrawResizeHandle(kerbalsWindowRect, ref isResizingKerbalsWindow,
                "Kerbals window");

            GUI.DragWindow();
        }

        // ------------------------- the Roster tab -------------------------

        private void DrawRosterTab(KerbalsPresentation.RosterRowSet set)
        {
            int total = (set.Involved != null ? set.Involved.Count : 0)
                        + (set.Plain != null ? set.Plain.Count : 0);
            if (total == 0)
            {
                // Only reachable on a save whose stock roster is empty (no career can be
                // in that state; a hand-built sandbox or science save can).
                GUILayout.Label("No kerbals in the roster.", grayStyle);
                return;
            }

            DrawRosterColumnHeader();
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());

            if (set.Involved != null)
            {
                for (int i = 0; i < set.Involved.Count; i++)
                    DrawRosterRow(set.Involved[i], dimmed: false);
            }

            if (set.Plain != null && set.Plain.Count > 0)
            {
                string arrow = plainBucketExpanded ? UnfoldedArrow : FoldedArrow;
                if (GUILayout.Button(
                        new GUIContent(
                            arrow + " " + KerbalsPresentation.FormatPlainFoldHeader(set.Plain.Count),
                            "Kerbals with no reservation, stand-in or recorded flight; click to list them."),
                        groupHeaderStyle, GUILayout.ExpandWidth(true)))
                {
                    plainBucketExpanded = !plainBucketExpanded;
                    ParsekLog.Verbose("UI",
                        $"Kerbals plain bucket {(plainBucketExpanded ? "expanded" : "collapsed")} ({set.Plain.Count} kerbals)");
                }
                if (plainBucketExpanded)
                {
                    for (int i = 0; i < set.Plain.Count; i++)
                        DrawRosterRow(set.Plain[i], dimmed: true);
                }
            }

            GUILayout.EndVertical();
        }

        // Header and body rows both open with parentUI.GetTableRowStyle() and declare the
        // same three fixed widths plus one expanding column, which is what keeps each cell
        // under its own header (ParsekUI.TableRowHorizontalInsetPx). The header is INSIDE
        // the body's scroll view, so it must NOT reserve a scrollbar gutter.
        private void DrawRosterColumnHeader()
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label("Kerbal", columnHeaderStyle, GUILayout.Width(ColW_RosterName));
            GUILayout.Label("Status now", columnHeaderStyle, GUILayout.Width(ColW_RosterStatus));
            GUILayout.Label(
                new GUIContent("Since",
                    "When the status started, for the two the mod dates: a loss and a reservation."),
                columnHeaderStyle, GUILayout.Width(ColW_RosterSince));
            GUILayout.Label(
                new GUIContent("Last flight",
                    "The kerbal's most recent recorded flight, and how it ended."),
                columnHeaderStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawRosterRow(KerbalsPresentation.RosterRow row, bool dimmed)
        {
            bool expandable = row.Chain != null && row.Chain.Count > 0;
            bool expanded = expandedSlots.Contains(row.FoldKey);
            GUIStyle cellStyle = dimmed
                ? grayStyle
                : StyleForRosterStatus(row.Status);

            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            string nameCell = FormatRosterNameCell(row, expandable, expanded);
            if (expandable)
            {
                if (GUILayout.Button(
                        new GUIContent(nameCell,
                            "Shows the stand-ins who have covered this kerbal's slot."),
                        cellStyle, GUILayout.Width(ColW_RosterName)))
                {
                    if (expanded) expandedSlots.Remove(row.FoldKey);
                    else expandedSlots.Add(row.FoldKey);
                    ParsekLog.Verbose("UI",
                        $"Kerbal slot '{row.FoldKey}' {(expanded ? "collapsed" : "expanded")} ({row.Chain.Count} chain members)");
                }
            }
            else
            {
                GUILayout.Label(nameCell, cellStyle, GUILayout.Width(ColW_RosterName));
            }
            // The status cell carries hover text for exactly one status: an active stand-in
            // who is also aboard a craft whose name does not fit the column inline.
            if (string.IsNullOrEmpty(row.StatusTooltipText))
            {
                GUILayout.Label(row.StatusText, cellStyle, GUILayout.Width(ColW_RosterStatus));
            }
            else
            {
                GUILayout.Label(new GUIContent(row.StatusText, row.StatusTooltipText),
                    cellStyle, GUILayout.Width(ColW_RosterStatus));
            }
            GUILayout.Label(row.SinceText, cellStyle, GUILayout.Width(ColW_RosterSince));
            GUILayout.Label(row.LastFlightText, cellStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            if (expandable && expanded)
            {
                int lastIdx = row.Chain.Count - 1;
                for (int c = 0; c < row.Chain.Count; c++)
                {
                    ChainMember member = row.Chain[c];
                    GUILayout.Label(
                        FormatRosterChainMemberText(member, isLast: (c == lastIdx)),
                        StyleForChainMember(member.Status));
                }
            }
        }

        /// <summary>The Name cell: the fold arrow when the row has a chain, then
        /// <c>"Name [Trait]"</c> (the bracket dropped when the trait is unknown). The two
        /// leading spaces on a leaf row line its name up with an arrow-prefixed one.</summary>
        internal static string FormatRosterNameCell(
            KerbalsPresentation.RosterRow row, bool expandable, bool expanded)
        {
            string who = string.IsNullOrEmpty(row.Trait)
                ? row.Name
                : row.Name + " [" + row.Trait + "]";
            if (!expandable) return "  " + who;
            return (expanded ? UnfoldedArrow : FoldedArrow) + " " + who;
        }

        private GUIStyle StyleForRosterStatus(KerbalsPresentation.RosterStatus s)
        {
            switch (s)
            {
                case KerbalsPresentation.RosterStatus.Lost: return deadStyle;
                case KerbalsPresentation.RosterStatus.Retired: return grayStyle;
                case KerbalsPresentation.RosterStatus.Assigned: return aboardStyle;
                case KerbalsPresentation.RosterStatus.StandIn: return activeChainStyle;
                default: return GUI.skin.label;
            }
        }

        private GUIStyle StyleForChainMember(ChainMemberStatus s)
        {
            switch (s)
            {
                case ChainMemberStatus.Active: return activeChainStyle;
                case ChainMemberStatus.Retired: return grayStyle;
                case ChainMemberStatus.Displaced: return displacedStyle;
                default: return grayStyle;
            }
        }

        /// <summary>
        /// Leading indent used by every subitem row under a fold/expand parent in this
        /// window. Four spaces puts the first subitem character roughly under the
        /// parent kerbal-name's first character (after the fold arrow).
        /// </summary>
        internal const string SubitemIndent = "    ";

        /// <summary>
        /// Renders a Roster chain-member subitem row as a single pre-indented string with
        /// a tree-branch glyph. <paramref name="isLast"/> picks between the "mid" and
        /// "last" tree characters.
        /// </summary>
        internal static string FormatRosterChainMemberText(ChainMember m, bool isLast)
        {
            string branch = isLast ? "\u2514\u2500 " : "\u251c\u2500 ";
            return SubitemIndent + branch + FormatChainMember(m);
        }

        internal static string FormatChainMember(ChainMember m)
        {
            string tag;
            switch (m.Status)
            {
                case ChainMemberStatus.Active: tag = "active"; break;
                case ChainMemberStatus.Retired: tag = "retired"; break;
                case ChainMemberStatus.Displaced: tag = "displaced"; break;
                default: tag = "?"; break;
            }
            return $"{m.Name} ({tag})";
        }

        // ------------------------- the Flights tab -------------------------

        private void DrawFlightsColumnHeader()
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label("Date", columnHeaderStyle, GUILayout.Width(ColW_FlightDate));
            GUILayout.Label("Mission", columnHeaderStyle, GUILayout.Width(ColW_FlightMission));
            GUILayout.Label("Outcome", columnHeaderStyle, GUILayout.Width(ColW_FlightOutcome));
            GUILayout.Label(
                new GUIContent("Crew",
                    "Who actually flew it, when a stand-in covered this kerbal's seat."),
                columnHeaderStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawFlightGroup(KerbalsPresentation.FlightGroup group)
        {
            bool folded = foldedKerbals.Contains(group.FoldKey);
            string arrow = folded ? FoldedArrow : UnfoldedArrow;

            if (GUILayout.Button(
                    new GUIContent(arrow + " " + group.HeaderText,
                        "Folds or unfolds this kerbal's mission history."),
                    groupHeaderStyle, GUILayout.ExpandWidth(true)))
            {
                ToggleFold(foldedKerbals, group.FoldKey,
                    group.Rows != null ? group.Rows.Count : 0);
            }

            if (folded || group.Rows == null) return;
            for (int i = 0; i < group.Rows.Count; i++)
                DrawFlightRow(group.Rows[i]);
        }

        private void DrawFlightRow(KerbalsPresentation.FlightRow row)
        {
            GUIStyle cellStyle = StyleForEndState(row.EndState);
            // The whole row is the Timeline cross-link, so every cell is a label-styled
            // button carrying the same help text: a click anywhere on the row scrolls the
            // Timeline to the flight it came from.
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            bool clicked = GUILayout.Button(
                new GUIContent(row.DateText,
                    "Scrolls the Timeline window to this mission's last recorded flight."),
                cellStyle, GUILayout.Width(ColW_FlightDate));
            clicked |= GUILayout.Button(
                new GUIContent(row.MissionText, DescribeFlightRow(row)),
                cellStyle, GUILayout.Width(ColW_FlightMission));
            // The outcome is the mission's FINAL one, so its hover carries the per-segment
            // list the row collapsed - which is where the detail the segment rows used to
            // show went.
            clicked |= GUILayout.Button(
                new GUIContent(row.OutcomeText, DescribeFlightRowOutcome(row)),
                cellStyle, GUILayout.Width(ColW_FlightOutcome));
            clicked |= GUILayout.Button(
                new GUIContent(row.CrewNoteText,
                    "Who actually flew it, when a stand-in covered this kerbal's seat."),
                cellStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            if (clicked)
            {
                // Mirrors the Timeline row cross-link pattern (TimelineWindowUI.DrawEntryRow).
                // GetTimelineUI() can return null during cold-start scene transitions - the
                // helper tolerates a null callback and still emits the diagnostic log (E14).
                var timelineUI = parentUI != null ? parentUI.GetTimelineUI() : null;
                Action<string> scrollCallback = timelineUI != null
                    ? timelineUI.ScrollToRecording
                    : (Action<string>)null;
                OnFatesRowClicked(scrollCallback, row.RecordingId);
            }
        }

        /// <summary>The row-level hover text: the raw recording behind a mission name, so
        /// the id and the recorded craft name stay readable without a column of their own.
        /// The recording named is the mission's LAST segment - the one a click jumps
        /// to.</summary>
        internal static string DescribeFlightRow(KerbalsPresentation.FlightRow row)
        {
            string rec = string.IsNullOrEmpty(row.RecordingName) ? "(unnamed)" : row.RecordingName;
            return "Recorded flight '" + rec + "' (id " + row.RecordingId + ").";
        }

        /// <summary>
        /// The outcome cell's hover text: what the final outcome word means, plus the
        /// mission's segment count and each segment's own outcome when the mission is more
        /// than one segment. A single-segment mission adds nothing, so it keeps the plain
        /// sentence.
        /// </summary>
        internal static string DescribeFlightRowOutcome(KerbalsPresentation.FlightRow row)
        {
            string sentence = TooltipForOutcome(row.EndState);
            if (row.SegmentCount <= 1 || string.IsNullOrEmpty(row.SegmentSummaryText))
                return sentence;
            return sentence + " " + row.SegmentSummaryText + ".";
        }

        internal static string TooltipForOutcome(KerbalEndState state)
        {
            switch (state)
            {
                case KerbalEndState.Recovered:
                    return "The flight ended with this kerbal recovered.";
                case KerbalEndState.Dead:
                    return "This kerbal did not come back from the flight.";
                case KerbalEndState.Aboard:
                    return "The flight ended with this kerbal still aboard the craft.";
                default:
                    return "The flight has no recorded ending.";
            }
        }

        private GUIStyle StyleForEndState(KerbalEndState s)
        {
            switch (s)
            {
                case KerbalEndState.Dead: return deadStyle;
                case KerbalEndState.Recovered: return recoveredStyle;
                case KerbalEndState.Aboard: return aboardStyle;
                default: return grayStyle;
            }
        }

        // ------------------------- shared helpers -------------------------

        // Logs the tab-switch. Extracted as a pure helper so the log contract stays
        // testable outside IMGUI (mirrors CareerStateWindowUI.SwitchTab).
        internal static void SwitchTab(int oldTab, int newTab)
        {
            ParsekLog.Verbose("UI",
                $"KerbalsWindow: tab switched {oldTab}->{newTab}");
        }

        // Pure mutation + log helper so the toggle contract is unit-testable outside IMGUI.
        // Returns the new folded state (true = now folded, false = now unfolded).
        internal static bool ToggleFold(
            HashSet<string> foldedKerbals, string kerbalName, int missionCount)
        {
            bool wasFolded = foldedKerbals.Contains(kerbalName);
            if (wasFolded) foldedKerbals.Remove(kerbalName);
            else foldedKerbals.Add(kerbalName);
            ParsekLog.Verbose("UI",
                $"Kerbals fold toggled: '{kerbalName}' -> {(wasFolded ? "unfolded" : "folded")} ({missionCount} missions)");
            return !wasFolded;
        }

        // Pure helper for the Flights -> Timeline cross-link. Production passes
        // `parentUI.GetTimelineUI().ScrollToRecording` as the callback; tests pass a
        // lambda spy. Tolerates a null callback (E14 - GetTimelineUI() can be null
        // during cold-start scene transitions) so the click never NREs; the log
        // still fires so stale-id clicks leave a diagnostic trail.
        internal static void OnFatesRowClicked(
            Action<string> scrollCallback, string recordingId)
        {
            ParsekLog.Verbose("UI",
                $"Kerbals Fates \u2192 Timeline scroll: recordingId={recordingId}");
            if (scrollCallback != null) scrollCallback(recordingId);
        }

        /// <summary>
        /// The calendar date a row cell shows: the Timeline's own compact-date form with
        /// its raw-UT fallback (<c>TimelineWindowUI.FormatTimelineEntryTimeLabel</c>), so
        /// the two windows date the same flight the same way. A KSP call, which is why
        /// <see cref="KerbalsPresentation"/> takes it as a delegate.
        /// </summary>
        internal static string FormatRowDate(double ut)
        {
            try { return KSPUtil.PrintDateCompact(ut, true); }
            catch
            {
                return ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // ------------------------- the view model -------------------------

        /// <summary>
        /// Gathers the live inputs and hands them to the pure builders. Everything
        /// KSP-shaped happens here: the stock roster walk, the live crew-to-vessel map,
        /// the ledger's slots / reservations / retired set, and the mission names.
        /// </summary>
        private KerbalsViewModel GatherViewModel()
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            // [Phase 3] ERS-routed: the kerbals window reads visible recordings only;
            // NotCommitted / superseded / session-suppressed entries are excluded from
            // roster context.
            IReadOnlyList<Recording> recordings = EffectiveState.ComputeERS();

            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots =
                kerbals != null ? kerbals.Slots : null;
            IReadOnlyDictionary<string, KerbalsModule.KerbalReservation> reservations =
                kerbals != null ? kerbals.Reservations : null;
            IReadOnlyList<string> retired = kerbals != null ? kerbals.GetRetiredKerbals() : null;
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> rawCrew =
                kerbals != null ? kerbals.RawRecordingCrewByRecordingId : null;
            ActiveChainIndexFunc activeChainIndexOf = kerbals != null
                ? (ActiveChainIndexFunc)(slot => kerbals.GetActiveChainIndex(slot))
                : null;

            return BuildViewModel(
                GatherRoster(),
                slots,
                reservations,
                retired,
                recordings,
                GatherMissionNames(recordings),
                rawCrew,
                CrewReservationManager.CrewReplacements,
                activeChainIndexOf,
                FormatRowDate);
        }

        /// <summary>
        /// The stock roster as rows: crew always, applicants and tourists only when
        /// <c>KerbalsModule.IsManaged</c> says so - a live reservation, membership of the
        /// retired set, or membership of some slot's replacement chain. A hiring pool of
        /// forty applicants is not what this window is for.
        ///
        /// <para>Those three are the WHOLE predicate: <c>IsManaged</c> checks no
        /// "ledger-created name" set and does not check slot OWNERSHIP either. Neither
        /// omission loses a row. An owner is added by the pure builder straight from
        /// <c>Slots</c>, and a Parsek-created stand-in sitting in the applicant pool is a
        /// chain member by construction, so the third clause already carries it.</para>
        /// </summary>
        private static List<KerbalsPresentation.RosterKerbal> GatherRoster()
        {
            var rows = new List<KerbalsPresentation.RosterKerbal>();
            int crew = 0, extra = 0, skipped = 0;
            try
            {
                if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.CrewRoster == null)
                {
                    ParsekLog.Verbose("UI", "KerbalsWindow: no crew roster to gather");
                    return rows;
                }
                Dictionary<string, string> vesselOf = GatherAssignedVessels();
                KerbalsModule kerbals = LedgerOrchestrator.Kerbals;

                foreach (ProtoCrewMember pcm in HighLogic.CurrentGame.CrewRoster.Crew)
                {
                    if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;
                    rows.Add(BuildRosterKerbal(pcm, vesselOf));
                    crew++;
                }
                foreach (ProtoCrewMember pcm in HighLogic.CurrentGame.CrewRoster.Applicants)
                {
                    if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;
                    if (kerbals == null || !kerbals.IsManaged(pcm.name)) { skipped++; continue; }
                    rows.Add(BuildRosterKerbal(pcm, vesselOf));
                    extra++;
                }
                foreach (ProtoCrewMember pcm in HighLogic.CurrentGame.CrewRoster.Tourist)
                {
                    if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;
                    if (kerbals == null || !kerbals.IsManaged(pcm.name)) { skipped++; continue; }
                    rows.Add(BuildRosterKerbal(pcm, vesselOf));
                    extra++;
                }
            }
            catch (Exception ex)
            {
                // The headless xUnit host has no HighLogic at all; a transient KSP-side
                // failure lands here too. The roster half of the tab then reads from the
                // ledger's own names only, which is what the pure builder adds anyway.
                ParsekLog.Verbose("UI",
                    $"KerbalsWindow: roster gather failed ({ex.GetType().Name}) - ledger names only");
            }
            ParsekLog.Verbose("UI",
                $"KerbalsWindow: roster gathered - crew={crew} managedNonCrew={extra} skipped={skipped}");
            return rows;
        }

        private static KerbalsPresentation.RosterKerbal BuildRosterKerbal(
            ProtoCrewMember pcm, Dictionary<string, string> vesselOf)
        {
            string vessel = null;
            if (vesselOf != null) vesselOf.TryGetValue(pcm.name, out vessel);
            return new KerbalsPresentation.RosterKerbal
            {
                Name = pcm.name,
                Trait = pcm.trait ?? "",
                AssignedVesselName = vessel
            };
        }

        /// <summary>
        /// Crew name -> the vessel it is aboard right now. Ghost-map ProtoVessels are
        /// skipped first (<c>GhostMapPresence.IsGhostMapVessel</c>), so a ghost's recorded
        /// crew never reads as a live assignment.
        /// </summary>
        private static Dictionary<string, string> GatherAssignedVessels()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            int vessels = 0, ghosts = 0, seated = 0;
            try
            {
                var all = FlightGlobals.Vessels;
                if (all == null) return map;
                for (int v = 0; v < all.Count; v++)
                {
                    Vessel vessel = all[v];
                    if (vessel == null) continue;
                    if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) { ghosts++; continue; }
                    vessels++;
                    var vesselCrew = vessel.GetVesselCrew();
                    if (vesselCrew == null) continue;
                    for (int c = 0; c < vesselCrew.Count; c++)
                    {
                        ProtoCrewMember pcm = vesselCrew[c];
                        if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;
                        map[pcm.name] = vessel.vesselName ?? "";
                        seated++;
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"KerbalsWindow: live crew map failed ({ex.GetType().Name}) - no Assigned rows");
                return map;
            }
            ParsekLog.Verbose("UI",
                $"KerbalsWindow: live crew map - vessels={vessels} ghostsSkipped={ghosts} seated={seated}");
            return map;
        }

        /// <summary>Recording id -> the mission name that recording belongs to, through
        /// its tree's original mission. A recording with no tree or no mission is absent
        /// from the map, and the row then falls back to the recorded vessel name.</summary>
        private static Dictionary<string, string> GatherMissionNames(
            IReadOnlyList<Recording> recordings)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (recordings == null) return map;
            int named = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                if (string.IsNullOrEmpty(rec.TreeId)) continue;
                Mission mission = MissionStore.FindOriginalMission(rec.TreeId);
                if (mission == null || string.IsNullOrEmpty(mission.Name)) continue;
                map[rec.RecordingId] = mission.Name;
                named++;
            }
            ParsekLog.Verbose("UI",
                $"KerbalsWindow: mission names resolved for {named} of {recordings.Count} recordings");
            return map;
        }

        /// <summary>
        /// Composes the whole view model out of pure inputs. The seam between the gather
        /// above and the builders in <see cref="KerbalsPresentation"/>, and the entry point
        /// the unit tests drive.
        /// </summary>
        internal static KerbalsViewModel BuildViewModel(
            IReadOnlyList<KerbalsPresentation.RosterKerbal> roster,
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots,
            IReadOnlyDictionary<string, KerbalsModule.KerbalReservation> reservations,
            IReadOnlyList<string> retired,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyDictionary<string, string> missionNameByRecordingId,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> rawCrewByRecordingId,
            IReadOnlyDictionary<string, string> replacements,
            ActiveChainIndexFunc activeChainIndexOf,
            Func<double, string> formatDate)
        {
            List<CrewEndStateEntry> endStates = BuildEndStates(committedRecordings);
            List<KerbalsPresentation.FlightGroup> flights = KerbalsPresentation.BuildFlightRows(
                endStates,
                missionNameByRecordingId,
                rawCrewByRecordingId,
                replacements,
                slots,
                BuildTraitMap(roster, slots),
                formatDate);
            KerbalsPresentation.RosterRowSet rosterRows = KerbalsPresentation.BuildRosterRows(
                roster, slots, reservations, retired, flights, activeChainIndexOf, formatDate);

            ParsekLog.Verbose("UI",
                $"KerbalsWindow: built VM - roster={rosterRows.Involved.Count}+{rosterRows.Plain.Count} "
                + $"flightGroups={flights.Count} endStates={endStates.Count}");

            return new KerbalsViewModel
            {
                EndStates = endStates,
                Flights = flights,
                Roster = rosterRows
            };
        }

        private static Dictionary<string, string> BuildTraitMap(
            IReadOnlyList<KerbalsPresentation.RosterKerbal> roster,
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (slots != null)
            {
                foreach (var pair in slots)
                {
                    if (pair.Value == null || string.IsNullOrEmpty(pair.Key)) continue;
                    map[pair.Key] = pair.Value.OwnerTrait ?? "";
                }
            }
            // The live roster wins over the slot's stored trait: the slot copy is a
            // snapshot taken when the walk created it.
            if (roster != null)
            {
                for (int i = 0; i < roster.Count; i++)
                {
                    if (string.IsNullOrEmpty(roster[i].Name)) continue;
                    map[roster[i].Name] = roster[i].Trait ?? "";
                }
            }
            return map;
        }

        /// <summary>
        /// Per-recording crew end-states, flattened and sorted (kerbal name ordinal, then
        /// EndUT ascending). Skips recordings where <c>CrewEndStatesResolved == false</c>
        /// (end-states still pending) or where the dictionary is null (nothing committed
        /// yet).
        /// </summary>
        internal static List<CrewEndStateEntry> BuildEndStates(
            IReadOnlyList<Recording> committedRecordings)
        {
            var endStates = new List<CrewEndStateEntry>();
            if (committedRecordings == null) return endStates;

            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec == null) continue;
                if (!rec.CrewEndStatesResolved) continue;
                if (rec.CrewEndStates == null) continue;
                foreach (var kvp in rec.CrewEndStates)
                {
                    if (string.IsNullOrEmpty(kvp.Key)) continue;
                    endStates.Add(new CrewEndStateEntry
                    {
                        KerbalName = kvp.Key,
                        RecordingName = rec.VesselName ?? "",
                        RecordingId = rec.RecordingId ?? "",
                        TreeId = rec.TreeId,
                        StartUT = rec.StartUT,
                        EndUT = rec.EndUT,
                        EndState = kvp.Value
                    });
                }
            }

            endStates.Sort((a, b) =>
            {
                int n = StringComparer.Ordinal.Compare(a.KerbalName, b.KerbalName);
                if (n != 0) return n;
                return a.EndUT.CompareTo(b.EndUT);
            });
            return endStates;
        }
    }
}
