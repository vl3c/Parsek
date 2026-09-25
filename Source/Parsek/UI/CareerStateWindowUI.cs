using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Contracts;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Career State window - the STATE view of the two slot-limited career modules,
    /// Contracts and Strategies. Each tab reads "what holds a slot now" and "what the
    /// recorded timeline still does": a Pending-in-timeline fold for rows the future adds,
    /// and a Timeline-end column for what it does to the rows that exist now. Dated career
    /// history (every contract, strategy, facility, milestone and tech event) belongs to
    /// the Timeline's Career view, and a row's name cell links there
    /// (<see cref="TimelineWindowUI.ScrollToCareerSubject(TimelineCareerCategory, string)"/>).
    /// Career mode only (<see cref="ModeOffersLauncher"/>).
    ///
    /// Design doc: docs/dev/done/plans/career-state-window.md (#416); the current shape is
    /// recorded in docs/dev/design-gui-inventory.md (Career State).
    /// </summary>
    internal class CareerStateWindowUI
    {
        private readonly ParsekUI parentUI;

        // --- Phase 2 window-chrome state ---
        private bool showCareerStateWindow;
        private Rect careerStateWindowRect;
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
            get { return careerStateWindowRect; }
            set { careerStateWindowRect = value; }
        }

        private Rect lastCareerStateWindowRect;
        private bool careerStateWindowHasInputLock;
        private bool isResizingCareerStateWindow;
        private Vector2 careerStateScrollPos;
        private int selectedTab;
        private CareerStateViewModel? cachedVM;

        // Rate-limit mode-render logs: only log when the mode changes from last render.
        // Starts at CAREER so the first SANDBOX/SCIENCE render always logs.
        private Game.Modes lastRenderedMode = Game.Modes.CAREER;

        // Style cache (initialized lazily on first draw, mirrors KerbalsWindowUI.EnsureStyles).
        private GUIStyle groupHeaderStyle;
        private GUIStyle columnHeaderStyle;
        // Toggle-button style for the tab bar and the pending fold - reuses GUI.skin.button's
        // own active-state texture as the "on" background so a selected tab or an expanded
        // fold looks pressed (mirrors TimelineWindowUI.toggleButtonStyle).
        private GUIStyle toggleButtonStyle;
        private GUIStyle alertStyle;
        private GUIStyle nameCellStyle;
        private GUIStyle grayStyle;
        private GUIStyle bannerStyle;

        // Bottom "hovered control help text" strip. See TooltipEchoBox for why it is a
        // permanently visible box of constant height. Single-line: at this window's
        // 820px first-open width every help text fits one wrapped line (pinned by
        // TooltipEchoBudgetTests), and anything rarer scrolls via the strip's marquee.
        private readonly TooltipEchoBox tooltipEcho =
            new TooltipEchoBox(TooltipEchoBox.DefaultSpacing, TooltipEchoBox.SingleLine);

        internal const float DefaultWindowWidth = 820f;
        /// <summary>
        /// The key this window's IMGUI window id is hashed from. Named once and used at
        /// BOTH the <c>ClickThruBlocker.GUILayoutWindow</c> call below and
        /// <c>UiWindowHandle.GetWindowId</c> (wired by
        /// <c>ParsekTestCommandAddon.ResolveWindowHandle</c>), which the <c>UiAction op=find</c>
        /// seam uses to scope a captured GUI tree to THIS window's subtree. Two copies of
        /// the literal would let the seam search the wrong window's children and answer a
        /// plausible rect for a control in another window.
        /// </summary>
        internal const string WindowIdKey = "ParsekCareerState";

        private const float DefaultWindowHeight = 400f;
        internal const float MinWindowWidth = 520f;
        // Tall enough that the chrome (banner, tabs, the one heading line, the column
        // header, help strip, Close) still leaves four or five table rows visible; at the
        // old 200 px the scroll body was about 20 px tall and showed no row at all.
        internal const float MinWindowHeight = 320f;
        // Internal so the design-7.2 mode-change close set (`ParsekUI.BuildGatedWindowCloseSet`)
        // can carry the id, which lets the in-game lock-leak test assert directly against
        // InputLockManager instead of hardcoding a duplicate string.
        internal const string CareerStateInputLockId = "Parsek_CareerStateWindow";

        // Column widths - shared between header and body. The NAME column of both tables
        // is not in this list: it takes GUILayout.ExpandWidth(true) in the header and in
        // every row (the Kerbals window's pattern), so a long contract title stops wrapping
        // and the header strip spans the whole table. The fixed columns are sized for a
        // compact KSP date ("Y12, D426, 05:17", 16 characters) at the 7 px/char bound
        // TooltipEchoBudgetTests uses, plus cell padding.
        internal const float ColW_Date = 145f;
        // A date plus its relative tail: "Y12, D426, 05:17 (overdue 99d)", 30 characters.
        internal const float ColW_Deadline = 220f;
        // The longest outcome: "deactivates Y12, D426, 05:17" (28 characters).
        internal const float ColW_TimelineEnd = 240f;
        // Strategies: "Reputation -> Science @ 100.0%" is 30 characters.
        internal const float ColW_Flow = 230f;
        // The smallest width the expanding name column is budgeted, for the fit test.
        internal const float MinNameColumnWidth = 160f;
        // The floor the expanding name cell may shrink to, in header and rows alike. Name
        // cells do not wrap (nameCellStyle clips instead), so a narrow window keeps one
        // row per line - a wrapped title made one row four lines tall at the 520 px
        // minimum and left no room for a second row.
        internal const float NameCellMinWidth = 80f;

        // Transient fold state for the Contracts / Strategies "Accepted / Activated later" folds.
        // Default-unfolded means we only store names that are currently folded. Tab switches
        // do NOT clear this set - folds persist across the window's lifetime.
        internal readonly HashSet<string> foldedGroups = new HashSet<string>(StringComparer.Ordinal);

        // Group-name constants for foldedGroups keys. Kept as constants so tests and
        // production share the exact strings.
        internal const string GroupKey_ContractsPending = "Contracts.Pending";
        internal const string GroupKey_StrategiesPending = "Strategies.Pending";

        // GUIContent (not bare strings) so each tab explains itself in the bottom help
        // strip on hover.
        private static readonly GUIContent[] TabLabels = new[]
        {
            new GUIContent("Contracts",
                "Contracts you hold now, and the ones your recorded flights accept later."),
            new GUIContent("Strategies",
                "Strategies running now, and the ones the recorded timeline activates later."),
        };

        /// <summary>Hover text of a contract row's name cell (the Timeline cross-link).</summary>
        internal const string ContractLinkTooltip =
            "Click to open the Timeline on Contracts, scrolled to this contract.";

        /// <summary>Hover text of a strategy row's name cell (the Timeline cross-link).</summary>
        internal const string StrategyLinkTooltip =
            "Click to open the Timeline on Strategies, scrolled to this strategy.";

        /// <summary>The empty-state line of each tab: the whole tab body when the tab has
        /// no row now and none pending.</summary>
        internal const string NoActiveContractsText = "No active contracts.";
        internal const string NoActiveStrategiesText = "No active strategies.";

        /// <summary>
        /// Test seam for contract title lookup. When non-null, Build() calls this
        /// instead of the live ContractSystem.Instance path. Tests inject a throwing
        /// or stubbed delegate to exercise the fallback branch (design doc E13).
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Design", "CA2211", Justification = "Test seam; not user-mutable at runtime.")]
        internal static Func<string, string> ContractTitleLookupForTesting;

        /// <summary>
        /// Test seam for strategy title lookup, forwarded to
        /// <see cref="StrategyDisplayNames.TitleLookupForTesting"/> so the Career window and
        /// the Timeline resolve a strategy's name through one resolver.
        /// </summary>
        internal static Func<string, string> StrategyTitleLookupForTesting
        {
            get => StrategyDisplayNames.TitleLookupForTesting;
            set => StrategyDisplayNames.TitleLookupForTesting = value;
        }

        public bool IsOpen
        {
            get { return showCareerStateWindow; }
            set
            {
                if (showCareerStateWindow == value) return;
                showCareerStateWindow = value;
                ParsekLog.Verbose("UI",
                    value
                        ? "Career State window toggled: open"
                        : "Career State window toggled: closed");
            }
        }

        internal CareerStateWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        // Test seam: exposes the cached VM so InvalidateCache_NullsCachedVM can assert
        // null state without reflection. Not a production path.
        internal CareerStateViewModel? CachedVMForTesting
        {
            get { return cachedVM; }
            set { cachedVM = value; }
        }

        public void InvalidateCache()
        {
            // GUI state gallery: this is the OTHER writer of cachedVM, reached from
            // LedgerOrchestrator.OnTimelineDataChanged through
            // ParsekUI.OnTimelineDataChanged - so suppressing only the rebuild PREDICATE
            // left a hole: any ledger write nulled the mocked VM, the predicate then
            // answered "do not rebuild", and the draw dereferenced a null Nullable every
            // frame. Suppressed here, and the predicate additionally always rebuilds a
            // NULL cache (see ShouldRebuildCachedVM) so the hole cannot reopen even if a
            // third writer appears.
            if (Parsek.UI.Gallery.GuiMockSession.Suppressed(
                    Parsek.UI.Gallery.GuiMockSession.CareerInvalidate))
                return;
            cachedVM = null;
            ParsekLog.Verbose("UI", "CareerStateWindow: cache invalidated");
        }

        // ================================================================
        // View-model types
        // ================================================================

        internal struct CareerStateViewModel
        {
            public ContractsTabVM Contracts;
            public StrategiesTabVM Strategies;
            public Game.Modes Mode;
            public double LiveUT;
            public double TerminalUT;
            public double NextRelevantActionUT;
            public bool HasDivergence;
            public bool IsTransientFallback;
            // Display text, formatted ONCE per rebuild (FillDisplayText) so OnGUI's
            // several events per frame never re-run the KSP date formatter.
            public string BannerText;
            // How often (game seconds) the rebuild predicate re-formats: 60 while every
            // drawn date and relative tail has minute resolution, 1 while a deadline sits
            // within two minutes of live UT and its tail can read in seconds.
            public double RefreshSeconds;
        }

        /// <summary>
        /// What the recorded timeline does to a contract or strategy row before it ends.
        /// Carried on the row so the "Timeline end" cell can say it instead of a bare
        /// "(closing)": a future FAILURE costs funds and reputation and must not read
        /// like a completion.
        /// </summary>
        internal enum TimelineEndKind
        {
            None = 0,
            Completed,
            Failed,
            Cancelled,
            Deactivated,
            // The contract's deadline passes before the recorded timeline completes it:
            // stock's Contract.State.DeadlineExpired (it fires the same onFailed event as
            // a failure, so the ledger tells the two apart by the accepted deadline -
            // ContractsModule.IsDeadlineExpiryFail). Appended last so no member renumbers.
            Expired,
        }

        /// <summary>
        /// One change the recorded timeline makes to a tab's slot usage after live UT:
        /// a contract accepted (+1) or closed (-1, at its deadline for an expiry), a
        /// strategy activated or deactivated, or the building behind the limit upgraded
        /// (<see cref="NewLimit"/>; -1 when the limit does not change).
        /// </summary>
        internal struct SlotChange
        {
            public double UT;
            public int Delta;
            public int NewLimit;

            internal static SlotChange Occupy(double ut) => new SlotChange { UT = ut, Delta = 1, NewLimit = -1 };
            internal static SlotChange Release(double ut) => new SlotChange { UT = ut, Delta = -1, NewLimit = -1 };
            internal static SlotChange Limit(double ut, int newLimit) => new SlotChange { UT = ut, Delta = 0, NewLimit = newLimit };
        }

        /// <summary>
        /// A tab's slots NOW, read against the recorded future (<see cref="ComputeSlotUsage"/>):
        /// <c>Free + Active + Reserved == Limit</c> whenever the limit is not over-subscribed.
        /// </summary>
        internal struct SlotUsage
        {
            public int Active;       // active at live UT
            public int Limit;        // the slot limit at live UT
            public bool Unlimited;   // Limit >= UnlimitedSlotThreshold
            public int PeakNeed;     // most slots the recorded future holds at once, against today's limit
            public int Reserved;     // PeakNeed - Active, never negative
            public int Free;         // Limit - PeakNeed, never negative
        }

        internal struct ContractsTabVM
        {
            public int CurrentActive;
            public int ProjectedActive;
            public int CurrentMaxSlots;
            public int ProjectedMaxSlots;
            public int MissionControlLevel;          // MissionControl level at liveUT (drives slot count per LedgerOrchestrator.UpdateSlotLimitsFromFacilities)
            public int ProjectedMissionControlLevel; // MissionControl level at terminal UT
            public List<ContractRow> CurrentRows;
            // Active at the timeline's terminal UT (drives the "at timeline end" slot count).
            public List<ContractRow> ProjectedRows;
            // Every contract the recorded timeline ACCEPTS after live UT, including the ones
            // it also completes / fails / cancels before its end - the "Accepted later"
            // fold. A superset of ProjectedRows' pending entries: a contract accepted and
            // closed in the future is in neither CurrentRows nor ProjectedRows.
            public List<ContractRow> PendingRows;
            // Slots now against the recorded future (the heading's numbers).
            public SlotUsage Slots;
            // "4 of 7 slots free (2 active, 1 reserved for later)", its hover text, and
            // the fold row "Accepted later by your recorded flights (1)" with its hover
            // (FillDisplayText).
            public string GroupHeadingText;
            public string GroupHeadingTooltip;
            public string PendingFoldText;
            public string PendingFoldTooltip;
        }

        internal struct ContractRow
        {
            public string ContractId;
            public string DisplayTitle;
            public double AcceptUT;
            public double DeadlineUT;   // NaN if none
            public bool IsPendingAccept;
            // Completes / fails / cancels before the timeline's terminal UT. Mirrors
            // EndKind != None; kept as a flag because the gallery's coverage keys name it.
            public bool IsClosingByTimelineEnd;
            public TimelineEndKind EndKind;
            public double EndUT;        // meaningful only when EndKind != None
            public string AcceptText;
            public string DeadlineText;
            public bool DeadlineOverdue;
            public string TimelineEndText;
        }

        internal struct StrategiesTabVM
        {
            public int CurrentActive;
            public int ProjectedActive;
            public int CurrentMaxSlots;
            public int ProjectedMaxSlots;
            public int AdminLevel;
            public int ProjectedAdminLevel;
            public List<StrategyRow> CurrentRows;
            public List<StrategyRow> ProjectedRows;
            // Every strategy the recorded timeline activates after live UT (see
            // ContractsTabVM.PendingRows).
            public List<StrategyRow> PendingRows;
            public SlotUsage Slots;
            public string GroupHeadingText;
            public string GroupHeadingTooltip;
            public string PendingFoldText;
            public string PendingFoldTooltip;
        }

        internal struct StrategyRow
        {
            public string StrategyId;
            public string DisplayTitle;
            public double ActivateUT;
            public StrategyResource SourceResource;
            public StrategyResource TargetResource;
            public float Commitment;
            public bool IsPendingActivate;
            // Deactivates before the timeline's terminal UT (EndKind == Deactivated).
            public bool IsClosingByTimelineEnd;
            public TimelineEndKind EndKind;
            public double EndUT;
            public string ActivateText;
            public string FlowText;
            public string TimelineEndText;
        }

        // Internal per-walk accumulator for contracts. Keyed by ContractId; stores the
        // accept action so we can project AcceptUT/DeadlineUT/Title at snapshot time.
        private sealed class ContractAcc
        {
            public string ContractId;
            public string Title;          // resolved title (action.ContractTitle preferred)
            public double AcceptUT;
            public double DeadlineUT;     // NaN if none
        }

        private sealed class StrategyAcc
        {
            public string StrategyId;
            public string Title;
            public double ActivateUT;
            public StrategyResource SourceResource;
            public StrategyResource TargetResource;
            public float Commitment;
        }

        private sealed class EndAcc
        {
            public TimelineEndKind Kind;
            public double UT;
        }

        // The two buildings whose level sets a slot limit (matches
        // LedgerOrchestrator.UpdateSlotLimitsFromFacilities).
        private const string MissionControlFacilityId = "MissionControl";
        private const string AdministrationFacilityId = "Administration";

        // ================================================================
        // Build()
        // ================================================================

        /// <summary>
        /// Walks <paramref name="actions"/> in order once, projecting both tabs as it goes.
        /// Snapshots "current" state at the boundary action.UT &lt;= liveUT; emits
        /// "projected" as the terminal state at the end of the walk.
        ///
        /// Only <see cref="GameAction.Effective"/> actions mutate contract / strategy state
        /// (mirrors ContractsModule / StrategiesModule.ProcessAction). Facility upgrades are
        /// read only for the Mission Control and Administration levels behind the two
        /// slot limits.
        ///
        /// <para><paramref name="formatDate"/> formats every date cell once here
        /// (<see cref="FillDisplayText"/>); null falls back to raw UT.</para>
        /// </summary>
        internal static CareerStateViewModel Build(
            IReadOnlyList<GameAction> actions,
            double liveUT,
            Game.Modes mode,
            ContractsModule contracts,
            StrategiesModule strategies,
            Func<double, string> formatDate = null)
        {
            if (contracts == null || strategies == null || actions == null)
            {
                ParsekLog.Warn("UI",
                    "CareerStateWindow: Build called with null module or actions; returning empty VM");
                var empty = EmptyVM(liveUT, mode, isTransientFallback: true);
                FillDisplayText(ref empty, formatDate);
                return empty;
            }

            // Terminal-state accumulators, walked forward through the action list.
            var activeContractsTerm = new Dictionary<string, ContractAcc>(StringComparer.Ordinal);
            var activeStrategiesTerm = new Dictionary<string, StrategyAcc>(StringComparer.Ordinal);
            var facilityLevelsTerm = new Dictionary<string, int>(StringComparer.Ordinal);

            // Future-only accumulators: what the recorded timeline accepts / activates after
            // live UT, and how each contract / strategy it touches ends. An ending is only
            // ever read for a row that is NOT active at the terminal UT, so the last removal
            // recorded for an id is the one that matters.
            var pendingContracts = new Dictionary<string, ContractAcc>(StringComparer.Ordinal);
            var pendingStrategies = new Dictionary<string, StrategyAcc>(StringComparer.Ordinal);
            var contractEnds = new Dictionary<string, EndAcc>(StringComparer.Ordinal);
            var strategyEnds = new Dictionary<string, EndAcc>(StringComparer.Ordinal);
            // How the instance active NOW ends: the first removal after live UT of an
            // entry accepted / activated at or before it. Kept apart from the per-id
            // endings above because a later re-accept / re-activation clears those, and
            // the row that is true now still ends at that first removal.
            var currentContractEnds = new Dictionary<string, EndAcc>(StringComparer.Ordinal);
            var currentStrategyEnds = new Dictionary<string, EndAcc>(StringComparer.Ordinal);
            // Every slot change after the current snapshot, for the heading's peak count.
            var contractSlotChanges = new List<SlotChange>();
            var strategySlotChanges = new List<SlotChange>();
            var expiredScratch = new List<string>();
            int expiredCount = 0;

            // Mode gating (design doc E1/E2): contracts and strategies exist in Career only.
            bool careerVisible = ModeShowsCareerState(mode);

            // "Current" snapshots - populated when we cross the liveUT boundary.
            Dictionary<string, ContractAcc> activeContractsCurSnap = null;
            Dictionary<string, StrategyAcc> activeStrategiesCurSnap = null;
            Dictionary<string, int> facilityLevelsCurSnap = null;

            double terminalUT = liveUT;
            double nextRelevantActionUT = double.PositiveInfinity;

            // Walk actions, snapshotting current-state the moment we pass liveUT.
            // An action with UT <= liveUT counts as already-applied, so the snapshot is
            // taken lazily - right before processing the first action with UT > liveUT.
            bool snapshotTaken = false;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;

                if (!snapshotTaken && a.UT > liveUT)
                {
                    activeContractsCurSnap = CopyMap(activeContractsTerm);
                    activeStrategiesCurSnap = CopyMap(activeStrategiesTerm);
                    facilityLevelsCurSnap = CopyMap(facilityLevelsTerm);
                    snapshotTaken = true;
                }

                if (a.UT > terminalUT) terminalUT = a.UT;

                // Deadlines first, as ContractsModule.ProcessAction runs CheckDeadlines
                // before it dispatches: a contract whose deadline has passed by this
                // action's UT expired AT its deadline. Stock's own DeadlineExpired fail
                // row (recorded at or after the deadline) then finds the contract already
                // closed as Expired and leaves it so.
                expiredCount += ExpireContractDeadlines(a.UT, liveUT, snapshotTaken,
                    activeContractsTerm, contractEnds, currentContractEnds,
                    contractSlotChanges, expiredScratch);

                // Snapshotting follows the full future action stream because the
                // current-vs-projected split is defined by exact `<= liveUT`
                // classification across all actions. The cache-expiry boundary is
                // narrower: hidden action categories cannot change the rendered
                // window until a mode switch, and mode switches already rebuild.
                CaptureNextRelevantActionUT(
                    ref nextRelevantActionUT,
                    a,
                    liveUT,
                    mode);

                switch (a.Type)
                {
                    case GameActionType.ContractAccept:
                        if (!a.Effective)
                        {
                            LogSkip("ContractAccept", "Ineffective", a);
                            break;
                        }
                        string cid = a.ContractId ?? "";
                        var acc = new ContractAcc
                        {
                            ContractId = cid,
                            Title = ResolveContractTitle(a, cid),
                            AcceptUT = a.UT,
                            // Already an absolute UT and already a double
                            // (CONTRACT-DEADLINE-CAPTURED-AS-DURATION) - no narrowing
                            // round-trip left to undo.
                            DeadlineUT = a.DeadlineUT
                        };
                        if (snapshotTaken && !activeContractsTerm.ContainsKey(cid))
                            contractSlotChanges.Add(SlotChange.Occupy(a.UT));
                        activeContractsTerm[cid] = acc;
                        if (a.UT > liveUT)
                            pendingContracts[cid] = acc;
                        // A re-accept supersedes an earlier ending of the same id.
                        contractEnds.Remove(cid);
                        break;

                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        if (!a.Effective)
                        {
                            LogSkip(a.Type.ToString(), "Ineffective", a);
                            break;
                        }
                        {
                            string rid = a.ContractId ?? "";
                            ContractAcc removed;
                            bool wasActive = activeContractsTerm.TryGetValue(rid, out removed);
                            EndAcc priorEnd;
                            if (!wasActive
                                && contractEnds.TryGetValue(rid, out priorEnd)
                                && priorEnd.Kind == TimelineEndKind.Expired)
                            {
                                // The deadline already closed this contract; a later
                                // fail / cancel row for it is stock reporting that expiry.
                                break;
                            }
                            if (snapshotTaken && wasActive)
                                contractSlotChanges.Add(SlotChange.Release(a.UT));
                            if (a.UT > liveUT
                                && wasActive
                                && removed.AcceptUT <= liveUT
                                && !currentContractEnds.ContainsKey(rid))
                            {
                                currentContractEnds[rid] = new EndAcc
                                {
                                    Kind = ContractEndKindFor(a.Type),
                                    UT = a.UT
                                };
                            }
                            activeContractsTerm.Remove(rid);
                            contractEnds[rid] = new EndAcc
                            {
                                Kind = ContractEndKindFor(a.Type),
                                UT = a.UT
                            };
                        }
                        break;

                    case GameActionType.StrategyActivate:
                        if (!a.Effective)
                        {
                            LogSkip("StrategyActivate", "Ineffective", a);
                            break;
                        }
                        string sid = a.StrategyId ?? "";
                        var sacc = new StrategyAcc
                        {
                            StrategyId = sid,
                            Title = ResolveStrategyTitle(sid),
                            ActivateUT = a.UT,
                            SourceResource = a.SourceResource,
                            TargetResource = a.TargetResource,
                            Commitment = a.Commitment
                        };
                        if (snapshotTaken && !activeStrategiesTerm.ContainsKey(sid))
                            strategySlotChanges.Add(SlotChange.Occupy(a.UT));
                        activeStrategiesTerm[sid] = sacc;
                        if (a.UT > liveUT)
                            pendingStrategies[sid] = sacc;
                        strategyEnds.Remove(sid);
                        break;

                    case GameActionType.StrategyDeactivate:
                        if (!a.Effective)
                        {
                            LogSkip("StrategyDeactivate", "Ineffective", a);
                            break;
                        }
                        {
                            string rsid = a.StrategyId ?? "";
                            StrategyAcc removedStrategy;
                            bool strategyWasActive =
                                activeStrategiesTerm.TryGetValue(rsid, out removedStrategy);
                            if (snapshotTaken && strategyWasActive)
                                strategySlotChanges.Add(SlotChange.Release(a.UT));
                            if (a.UT > liveUT
                                && strategyWasActive
                                && removedStrategy.ActivateUT <= liveUT
                                && !currentStrategyEnds.ContainsKey(rsid))
                            {
                                currentStrategyEnds[rsid] = new EndAcc
                                {
                                    Kind = TimelineEndKind.Deactivated,
                                    UT = a.UT
                                };
                            }
                            activeStrategiesTerm.Remove(rsid);
                            strategyEnds[rsid] = new EndAcc
                            {
                                Kind = TimelineEndKind.Deactivated,
                                UT = a.UT
                            };
                        }
                        break;

                    case GameActionType.FacilityUpgrade:
                        // Read only for the two slot-limit levels; the upgrade itself is a
                        // dated row in the Timeline's Facilities view.
                        if (!a.Effective)
                        {
                            LogSkip("FacilityUpgrade", "Ineffective", a);
                            break;
                        }
                        {
                            string upgradedId = FacilityDisplayNames.FacilityIdForBuilding(a.FacilityId);
                            facilityLevelsTerm[upgradedId] = a.ToLevel;
                            if (snapshotTaken && upgradedId == MissionControlFacilityId)
                                contractSlotChanges.Add(SlotChange.Limit(
                                    a.UT, LedgerOrchestrator.GetContractSlots(a.ToLevel)));
                            else if (snapshotTaken && upgradedId == AdministrationFacilityId)
                                strategySlotChanges.Add(SlotChange.Limit(
                                    a.UT, LedgerOrchestrator.GetStrategySlots(a.ToLevel)));
                        }
                        break;

                    default:
                        // Action types this window does not project (science / funds /
                        // rep / kerbal / milestone / building damage). Silent.
                        break;
                }
            }

            // If no action ever exceeded liveUT, everything is "current" - snapshot now.
            if (!snapshotTaken)
            {
                activeContractsCurSnap = CopyMap(activeContractsTerm);
                activeStrategiesCurSnap = CopyMap(activeStrategiesTerm);
                facilityLevelsCurSnap = CopyMap(facilityLevelsTerm);
            }

            // --- Facility levels for slot math (matches LedgerOrchestrator.UpdateSlotLimitsFromFacilities) ---
            // Contracts draw slots from MissionControl level; Strategies draw from Administration level.
            int missionControlLevelCur = LevelOf(facilityLevelsCurSnap, MissionControlFacilityId);
            int missionControlLevelTerm = LevelOf(facilityLevelsTerm, MissionControlFacilityId);
            int adminLevelCur = LevelOf(facilityLevelsCurSnap, AdministrationFacilityId);
            int adminLevelTerm = LevelOf(facilityLevelsTerm, AdministrationFacilityId);

            var contractsVM = CreateContractsTabVM(
                activeContractsCurSnap,
                activeContractsTerm,
                pendingContracts,
                contractEnds,
                currentContractEnds,
                missionControlLevelCur,
                missionControlLevelTerm,
                liveUT,
                careerVisible);
            var strategiesVM = CreateStrategiesTabVM(
                activeStrategiesCurSnap,
                activeStrategiesTerm,
                pendingStrategies,
                strategyEnds,
                currentStrategyEnds,
                adminLevelCur,
                adminLevelTerm,
                liveUT,
                careerVisible);

            // Slots now against the recorded future. A hidden tab carries no rows, so it
            // reads its bare limit rather than a future its rows do not show.
            // When the stock-UI overlay work's shared "free slots for a new accept now"
            // query (over CommittedFutureIndex) lands, the heading should read that query.
            contractsVM.Slots = ComputeSlotUsage(contractsVM.CurrentActive,
                contractsVM.CurrentMaxSlots, careerVisible ? contractSlotChanges : null);
            strategiesVM.Slots = ComputeSlotUsage(strategiesVM.CurrentActive,
                strategiesVM.CurrentMaxSlots, careerVisible ? strategySlotChanges : null);

            // --- Divergence (either tab where current != projected). ---
            bool divergence =
                contractsVM.CurrentActive != contractsVM.ProjectedActive
                || contractsVM.PendingRows.Count > 0
                || AnyRowEnds(contractsVM.CurrentRows)
                || strategiesVM.CurrentActive != strategiesVM.ProjectedActive
                || strategiesVM.PendingRows.Count > 0
                || AnyRowEnds(strategiesVM.CurrentRows)
                || (careerVisible && missionControlLevelCur != missionControlLevelTerm)
                || (careerVisible && adminLevelCur != adminLevelTerm);

            var vm = new CareerStateViewModel
            {
                Contracts = contractsVM,
                Strategies = strategiesVM,
                Mode = mode,
                LiveUT = liveUT,
                TerminalUT = terminalUT,
                NextRelevantActionUT = nextRelevantActionUT,
                HasDivergence = divergence,
                IsTransientFallback = false
            };
            FillDisplayText(ref vm, formatDate);

            ParsekLog.Verbose("UI",
                "CareerStateWindow: rebuilt VM "
                + $"liveUT={GetDisplayedUtText(liveUT)} "
                + $"terminalUT={GetDisplayedUtText(terminalUT)} "
                + $"divergence={divergence} "
                + $"mode={mode} "
                + $"contracts={contractsVM.CurrentActive}/{contractsVM.ProjectedActive} "
                + $"contractsPending={contractsVM.PendingRows.Count} "
                + $"strategies={strategiesVM.CurrentActive}/{strategiesVM.ProjectedActive} "
                + $"strategiesPending={strategiesVM.PendingRows.Count} "
                + $"contractsExpired={expiredCount} "
                + "contractSlots=" + FormatSlotUsageForLog(contractsVM.Slots) + " "
                + "strategySlots=" + FormatSlotUsageForLog(strategiesVM.Slots) + " "
                + $"missionControl=L{missionControlLevelCur}/L{missionControlLevelTerm} "
                + $"administration=L{adminLevelCur}/L{adminLevelTerm} "
                + "refresh=" + vm.RefreshSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s");

            return vm;
        }

        // ================================================================
        // Helpers
        // ================================================================

        internal static TimelineEndKind ContractEndKindFor(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ContractComplete: return TimelineEndKind.Completed;
                case GameActionType.ContractFail: return TimelineEndKind.Failed;
                case GameActionType.ContractCancel: return TimelineEndKind.Cancelled;
                default: return TimelineEndKind.None;
            }
        }

        /// <summary>
        /// Closes every active contract whose deadline has passed by <paramref name="ut"/>
        /// (<see cref="ContractsModule.HasContractDeadlineElapsed"/>, the ledger's own
        /// test) as <see cref="TimelineEndKind.Expired"/> AT its deadline. After the
        /// current snapshot the expiry is a future slot release, and for a contract active
        /// now it is also that row's ending. Returns how many expired.
        /// </summary>
        private static int ExpireContractDeadlines(
            double ut, double liveUT, bool snapshotTaken,
            Dictionary<string, ContractAcc> activeContracts,
            Dictionary<string, EndAcc> contractEnds,
            Dictionary<string, EndAcc> currentContractEnds,
            List<SlotChange> slotChanges,
            List<string> scratch)
        {
            if (activeContracts.Count == 0) return 0;
            scratch.Clear();
            foreach (var kvp in activeContracts)
            {
                if (ContractsModule.HasContractDeadlineElapsed(
                        ut, kvp.Value.DeadlineUT, kvp.Value.AcceptUT))
                    scratch.Add(kvp.Key);
            }
            for (int i = 0; i < scratch.Count; i++)
            {
                string id = scratch[i];
                ContractAcc acc = activeContracts[id];
                activeContracts.Remove(id);
                var end = new EndAcc { Kind = TimelineEndKind.Expired, UT = acc.DeadlineUT };
                contractEnds[id] = end;
                if (!snapshotTaken) continue;
                slotChanges.Add(SlotChange.Release(acc.DeadlineUT));
                if (acc.AcceptUT <= liveUT && !currentContractEnds.ContainsKey(id))
                    currentContractEnds[id] = end;
            }
            return scratch.Count;
        }

        /// <summary>
        /// A tab's slots now, read against the recorded future. Walks the future
        /// <paramref name="changes"/> in UT order from <paramref name="activeNow"/>
        /// (a release before an occupy at the same UT: a slot a completion frees is free
        /// for an accept on the same tick) and keeps the PEAK number held at once. A
        /// contract that ends on day 50 and one a flight accepts on day 60 share one slot;
        /// two accepts that overlap need two. A later upgrade of the building counts
        /// against today's limit: the need at a moment is what is held then minus how many
        /// slots the upgrade has added by then.
        /// </summary>
        internal static SlotUsage ComputeSlotUsage(int activeNow, int limitNow,
                                                  IList<SlotChange> changes)
        {
            var usage = new SlotUsage
            {
                Active = activeNow,
                Limit = limitNow,
                Unlimited = limitNow >= UnlimitedSlotThreshold,
                PeakNeed = activeNow
            };
            if (changes != null && changes.Count > 0)
            {
                var sorted = new List<KeyValuePair<int, SlotChange>>(changes.Count);
                for (int i = 0; i < changes.Count; i++)
                    sorted.Add(new KeyValuePair<int, SlotChange>(i, changes[i]));
                sorted.Sort(CompareSlotChanges);
                int held = activeNow;
                int limit = limitNow;
                for (int i = 0; i < sorted.Count; i++)
                {
                    SlotChange c = sorted[i].Value;
                    held += c.Delta;
                    if (c.NewLimit >= 0) limit = c.NewLimit;
                    // No limit at that moment: nothing held then can crowd out today.
                    if (limit >= UnlimitedSlotThreshold) continue;
                    int need = held - (limit - limitNow);
                    if (need > usage.PeakNeed) usage.PeakNeed = need;
                }
            }
            usage.Reserved = Math.Max(0, usage.PeakNeed - activeNow);
            usage.Free = Math.Max(0, limitNow - usage.PeakNeed);
            return usage;
        }

        // UT order; at one UT releases, then limit changes, then occupies; then input
        // order, so the sort is stable.
        private static int CompareSlotChanges(KeyValuePair<int, SlotChange> x,
                                              KeyValuePair<int, SlotChange> y)
        {
            int byUt = x.Value.UT.CompareTo(y.Value.UT);
            if (byUt != 0) return byUt;
            int byRank = SlotChangeRank(x.Value).CompareTo(SlotChangeRank(y.Value));
            if (byRank != 0) return byRank;
            return x.Key.CompareTo(y.Key);
        }

        private static int SlotChangeRank(SlotChange c)
        {
            if (c.Delta < 0) return 0;
            if (c.Delta == 0) return 1;
            return 2;
        }

        internal static string FormatSlotUsageForLog(SlotUsage u)
        {
            var ic = CultureInfo.InvariantCulture;
            return "active=" + u.Active.ToString(ic)
                + "/limit=" + (u.Unlimited ? "none" : u.Limit.ToString(ic))
                + "/peak=" + u.PeakNeed.ToString(ic)
                + "/reserved=" + u.Reserved.ToString(ic)
                + "/free=" + u.Free.ToString(ic);
        }

        // A facility with no upgrade in the ledger is at level 1.
        private static int LevelOf(Dictionary<string, int> levels, string facilityId)
        {
            int level;
            return levels != null && levels.TryGetValue(facilityId, out level) ? level : 1;
        }

        private static ContractsTabVM CreateContractsTabVM(
            Dictionary<string, ContractAcc> activeContractsCurSnap,
            Dictionary<string, ContractAcc> activeContractsTerm,
            Dictionary<string, ContractAcc> pendingContracts,
            Dictionary<string, EndAcc> contractEnds,
            Dictionary<string, EndAcc> currentContractEnds,
            int missionControlLevelCur,
            int missionControlLevelTerm,
            double liveUT,
            bool contractsVisible)
        {
            var contractsVM = new ContractsTabVM
            {
                CurrentRows = new List<ContractRow>(),
                ProjectedRows = new List<ContractRow>(),
                PendingRows = new List<ContractRow>(),
                MissionControlLevel = missionControlLevelCur,
                ProjectedMissionControlLevel = missionControlLevelTerm,
                CurrentMaxSlots = LedgerOrchestrator.GetContractSlots(missionControlLevelCur),
                ProjectedMaxSlots = LedgerOrchestrator.GetContractSlots(missionControlLevelTerm)
            };
            if (contractsVisible)
            {
                foreach (var kvp in activeContractsCurSnap)
                    contractsVM.CurrentRows.Add(
                        CurrentContractRowFor(kvp.Value, currentContractEnds));
                foreach (var kvp in activeContractsTerm)
                {
                    var c = kvp.Value;
                    contractsVM.ProjectedRows.Add(
                        ContractRowFor(c, c.AcceptUT > liveUT, activeContractsTerm, contractEnds));
                }
                foreach (var kvp in pendingContracts)
                    contractsVM.PendingRows.Add(
                        ContractRowFor(kvp.Value, true, activeContractsTerm, contractEnds));
                contractsVM.CurrentRows.Sort(CompareContractRowByAcceptUT);
                contractsVM.ProjectedRows.Sort(CompareContractRowByAcceptUT);
                contractsVM.PendingRows.Sort(CompareContractRowByAcceptUT);
            }
            contractsVM.CurrentActive = contractsVM.CurrentRows.Count;
            contractsVM.ProjectedActive = contractsVM.ProjectedRows.Count;
            return contractsVM;
        }

        // A row active now: it ends at the first removal after live UT, even when the
        // recorded future accepts the same id again later (that instance is a pending row).
        private static ContractRow CurrentContractRowFor(
            ContractAcc c, Dictionary<string, EndAcc> currentContractEnds)
        {
            EndAcc end;
            bool ends = currentContractEnds.TryGetValue(c.ContractId, out end);
            return new ContractRow
            {
                ContractId = c.ContractId,
                DisplayTitle = c.Title,
                AcceptUT = c.AcceptUT,
                DeadlineUT = c.DeadlineUT,
                IsPendingAccept = false,
                IsClosingByTimelineEnd = ends,
                EndKind = ends ? end.Kind : TimelineEndKind.None,
                EndUT = ends ? end.UT : double.NaN
            };
        }

        private static ContractRow ContractRowFor(
            ContractAcc c, bool pending,
            Dictionary<string, ContractAcc> activeContractsTerm,
            Dictionary<string, EndAcc> contractEnds)
        {
            // Not active at the terminal UT -> the recorded timeline completes, fails or
            // cancels it; the ending recorded for the id says which, and when.
            TimelineEndKind kind = TimelineEndKind.None;
            double endUT = double.NaN;
            EndAcc end;
            if (!activeContractsTerm.ContainsKey(c.ContractId)
                && contractEnds.TryGetValue(c.ContractId, out end))
            {
                kind = end.Kind;
                endUT = end.UT;
            }
            return new ContractRow
            {
                ContractId = c.ContractId,
                DisplayTitle = c.Title,
                AcceptUT = c.AcceptUT,
                DeadlineUT = c.DeadlineUT,
                IsPendingAccept = pending,
                IsClosingByTimelineEnd = kind != TimelineEndKind.None,
                EndKind = kind,
                EndUT = endUT
            };
        }

        private static StrategiesTabVM CreateStrategiesTabVM(
            Dictionary<string, StrategyAcc> activeStrategiesCurSnap,
            Dictionary<string, StrategyAcc> activeStrategiesTerm,
            Dictionary<string, StrategyAcc> pendingStrategies,
            Dictionary<string, EndAcc> strategyEnds,
            Dictionary<string, EndAcc> currentStrategyEnds,
            int adminLevelCur,
            int adminLevelTerm,
            double liveUT,
            bool strategiesVisible)
        {
            var strategiesVM = new StrategiesTabVM
            {
                CurrentRows = new List<StrategyRow>(),
                ProjectedRows = new List<StrategyRow>(),
                PendingRows = new List<StrategyRow>(),
                AdminLevel = adminLevelCur,
                ProjectedAdminLevel = adminLevelTerm,
                CurrentMaxSlots = LedgerOrchestrator.GetStrategySlots(adminLevelCur),
                ProjectedMaxSlots = LedgerOrchestrator.GetStrategySlots(adminLevelTerm)
            };
            if (strategiesVisible)
            {
                foreach (var kvp in activeStrategiesCurSnap)
                    strategiesVM.CurrentRows.Add(
                        CurrentStrategyRowFor(kvp.Value, currentStrategyEnds));
                foreach (var kvp in activeStrategiesTerm)
                {
                    var s = kvp.Value;
                    strategiesVM.ProjectedRows.Add(
                        StrategyRowFor(s, s.ActivateUT > liveUT, activeStrategiesTerm, strategyEnds));
                }
                foreach (var kvp in pendingStrategies)
                    strategiesVM.PendingRows.Add(
                        StrategyRowFor(kvp.Value, true, activeStrategiesTerm, strategyEnds));
                strategiesVM.CurrentRows.Sort(CompareStrategyRowByActivateUT);
                strategiesVM.ProjectedRows.Sort(CompareStrategyRowByActivateUT);
                strategiesVM.PendingRows.Sort(CompareStrategyRowByActivateUT);
            }
            strategiesVM.CurrentActive = strategiesVM.CurrentRows.Count;
            strategiesVM.ProjectedActive = strategiesVM.ProjectedRows.Count;
            return strategiesVM;
        }

        // See CurrentContractRowFor: a strategy active now, deactivated later and
        // activated again after that is two rows, and this one says when it deactivates.
        private static StrategyRow CurrentStrategyRowFor(
            StrategyAcc s, Dictionary<string, EndAcc> currentStrategyEnds)
        {
            EndAcc end;
            bool ends = currentStrategyEnds.TryGetValue(s.StrategyId, out end);
            return new StrategyRow
            {
                StrategyId = s.StrategyId,
                DisplayTitle = s.Title,
                ActivateUT = s.ActivateUT,
                SourceResource = s.SourceResource,
                TargetResource = s.TargetResource,
                Commitment = s.Commitment,
                IsPendingActivate = false,
                IsClosingByTimelineEnd = ends,
                EndKind = ends ? end.Kind : TimelineEndKind.None,
                EndUT = ends ? end.UT : double.NaN
            };
        }

        private static StrategyRow StrategyRowFor(
            StrategyAcc s, bool pending,
            Dictionary<string, StrategyAcc> activeStrategiesTerm,
            Dictionary<string, EndAcc> strategyEnds)
        {
            TimelineEndKind kind = TimelineEndKind.None;
            double endUT = double.NaN;
            EndAcc end;
            if (!activeStrategiesTerm.ContainsKey(s.StrategyId)
                && strategyEnds.TryGetValue(s.StrategyId, out end))
            {
                kind = end.Kind;
                endUT = end.UT;
            }
            return new StrategyRow
            {
                StrategyId = s.StrategyId,
                DisplayTitle = s.Title,
                ActivateUT = s.ActivateUT,
                SourceResource = s.SourceResource,
                TargetResource = s.TargetResource,
                Commitment = s.Commitment,
                IsPendingActivate = pending,
                IsClosingByTimelineEnd = kind != TimelineEndKind.None,
                EndKind = kind,
                EndUT = endUT
            };
        }

        private static CareerStateViewModel EmptyVM(
            double liveUT,
            Game.Modes mode,
            bool isTransientFallback = false)
        {
            return new CareerStateViewModel
            {
                Contracts = new ContractsTabVM
                {
                    CurrentRows = new List<ContractRow>(),
                    ProjectedRows = new List<ContractRow>(),
                    PendingRows = new List<ContractRow>(),
                    MissionControlLevel = 1,
                    ProjectedMissionControlLevel = 1,
                    CurrentMaxSlots = LedgerOrchestrator.GetContractSlots(1),
                    ProjectedMaxSlots = LedgerOrchestrator.GetContractSlots(1),
                    Slots = ComputeSlotUsage(0, LedgerOrchestrator.GetContractSlots(1), null)
                },
                Strategies = new StrategiesTabVM
                {
                    CurrentRows = new List<StrategyRow>(),
                    ProjectedRows = new List<StrategyRow>(),
                    PendingRows = new List<StrategyRow>(),
                    AdminLevel = 1,
                    ProjectedAdminLevel = 1,
                    CurrentMaxSlots = LedgerOrchestrator.GetStrategySlots(1),
                    ProjectedMaxSlots = LedgerOrchestrator.GetStrategySlots(1),
                    Slots = ComputeSlotUsage(0, LedgerOrchestrator.GetStrategySlots(1), null)
                },
                Mode = mode,
                LiveUT = liveUT,
                TerminalUT = liveUT,
                NextRelevantActionUT = double.PositiveInfinity,
                HasDivergence = false,
                IsTransientFallback = isTransientFallback
            };
        }

        internal static bool ShouldRebuildCachedVM(
            CareerStateViewModel? cachedVM,
            Game.Modes currentMode,
            double liveUT)
        {
            // GUI state gallery: while an ARMED seam session owns this window, the cached
            // VM is a mocked one. This is the site that MUST be suppressed rather than one
            // that merely benefits - the UT-text compare below rebuilds within one game
            // second, so without it a mocked career would not survive to the frame a
            // capture needs. The predicate is false in every player build (only
            // ParsekTestCommandAddon.UiMock can create a session) and it logs one Verbose
            // line per site per session, never per poll.
            //
            // A NULL CACHE IS ALWAYS REBUILT, session or not, and that ORDER is the fix
            // for a real defect: suppressing ahead of the null check meant a nulled mocked
            // VM was never rebuilt and the draw dereferenced a null Nullable every frame,
            // so a capture photographed a half-drawn window under the mocked state's
            // label. The scope is marked BROKEN so the applier answers
            // `mock-scope-broken` instead of reporting a state it is no longer showing.
            if (cachedVM == null)
            {
                Parsek.UI.Gallery.GuiMockSession.NoteScopeBroken(
                    Parsek.UI.Gallery.GuiMockSession.CareerVmRebuild,
                    "cached-vm-nulled");
                return true;
            }

            if (Parsek.UI.Gallery.GuiMockSession.Suppressed(
                    Parsek.UI.Gallery.GuiMockSession.CareerVmRebuild))
                return false;

            var vm = cachedVM.Value;
            if (vm.Mode != currentMode)
                return true;

            if (vm.IsTransientFallback)
                return true;

            // Only Career draws time-sensitive rows. Science and Sandbox show a one-line
            // banner, so they rebuild only on explicit invalidation, mode changes, or
            // transient fallback.
            bool hasVisibleTimelineState = ModeShowsCareerState(currentMode);

            if (hasVisibleTimelineState && liveUT < vm.LiveUT)
                return true;

            // The banner's date and the deadline tails are minute-resolution text, so a
            // rebuild per game second only re-formatted identical strings. A deadline
            // within a couple of minutes of now drops the cadence to one second
            // (RefreshSeconds), where its tail reads in seconds.
            if (hasVisibleTimelineState
                && RefreshBucket(vm.LiveUT, vm.RefreshSeconds) != RefreshBucket(liveUT, vm.RefreshSeconds))
                return true;

            return hasVisibleTimelineState
                && !double.IsPositiveInfinity(vm.NextRelevantActionUT)
                && liveUT >= vm.NextRelevantActionUT;
        }

        internal static string GetDisplayedUtText(double liveUT)
        {
            return liveUT.ToString("F0", CultureInfo.InvariantCulture);
        }

        private static void CaptureNextRelevantActionUT(
            ref double nextRelevantActionUT,
            GameAction action,
            double liveUT,
            Game.Modes mode)
        {
            if (!double.IsPositiveInfinity(nextRelevantActionUT)
                || action == null
                || !action.Effective
                || action.UT <= liveUT
                || !IsVisibleTimelineAction(action.Type, mode))
                return;

            nextRelevantActionUT = action.UT;
        }

        private static bool IsVisibleTimelineAction(
            GameActionType actionType,
            Game.Modes mode)
        {
            switch (actionType)
            {
                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                case GameActionType.StrategyActivate:
                case GameActionType.StrategyDeactivate:
                // An upgrade moves a slot limit, which both headings print.
                case GameActionType.FacilityUpgrade:
                    return ModeShowsCareerState(mode);

                default:
                    return false;
            }
        }

        /// <summary>Contracts and strategies exist in Career mode only.</summary>
        private static bool ModeShowsCareerState(Game.Modes mode)
        {
            return mode == Game.Modes.CAREER;
        }

        private static Dictionary<string, T> CopyMap<T>(Dictionary<string, T> src)
        {
            var dst = new Dictionary<string, T>(src.Count, StringComparer.Ordinal);
            foreach (var kvp in src) dst[kvp.Key] = kvp.Value;
            return dst;
        }

        private static bool AnyRowEnds(List<ContractRow> rows)
        {
            if (rows == null) return false;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].EndKind != TimelineEndKind.None) return true;
            return false;
        }

        private static bool AnyRowEnds(List<StrategyRow> rows)
        {
            if (rows == null) return false;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].EndKind != TimelineEndKind.None) return true;
            return false;
        }

        private static int CompareContractRowByAcceptUT(ContractRow a, ContractRow b)
        {
            int c = a.AcceptUT.CompareTo(b.AcceptUT);
            if (c != 0) return c;
            return StringComparer.Ordinal.Compare(a.ContractId, b.ContractId);
        }

        private static int CompareStrategyRowByActivateUT(StrategyRow a, StrategyRow b)
        {
            int c = a.ActivateUT.CompareTo(b.ActivateUT);
            if (c != 0) return c;
            return StringComparer.Ordinal.Compare(a.StrategyId, b.StrategyId);
        }

        private static void LogSkip(string actionType, string reason, GameAction a)
        {
            // Rate-limited across Build() calls via ParsekLog.VerboseRateLimited
            // (design doc section 7). Keyed on type+reason so each skip category throttles
            // independently; repeated walks of the same ledger do not spam the log.
            var ic = CultureInfo.InvariantCulture;
            string key = "CareerStateWindow.skip." + actionType + "." + reason;
            ParsekLog.VerboseRateLimited("UI", key,
                $"CareerStateWindow: action skipped actionType={actionType} " +
                $"ut={a.UT.ToString("F0", ic)} reason={reason}");
        }

        // ================================================================
        // Title resolution (design doc section 4.4)
        // ================================================================

        private static string ResolveContractTitle(GameAction a, string contractId)
        {
            // Preference 1: action.ContractTitle set at Accept.
            if (!string.IsNullOrEmpty(a.ContractTitle)) return a.ContractTitle;

            // Preference 2: live ContractSystem.Instance lookup (wrapped so tests
            // can inject a throwing delegate to exercise the catch branch - E13).
            try
            {
                string fromLive = LookupContractTitleLive(contractId);
                if (!string.IsNullOrEmpty(fromLive)) return fromLive;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("UI",
                    "CareerStateWindow.contractTitleThrew." + contractId,
                    $"CareerStateWindow: contract title lookup threw id={contractId} ex={ex.GetType().Name}");
                // Fall through to raw-id fallback.
            }

            // Preference 3: raw id fallback - rate-limited per id so mod-generated or
            // cancelled contracts do not re-log on every ledger invalidation.
            ParsekLog.VerboseRateLimited("UI",
                "CareerStateWindow.contractTitleFallback." + contractId,
                $"CareerStateWindow: contract title fallback id={contractId}");
            return contractId;
        }

        private static string LookupContractTitleLive(string contractId)
        {
            var lookup = ContractTitleLookupForTesting;
            if (lookup != null) return lookup(contractId);

            // Production path: read live ContractSystem. Guarded with a null-check
            // because pre-scene-load (or outside KSP) Instance will be null.
            if (ContractSystem.Instance == null) return null;
            var list = ContractSystem.Instance.Contracts;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                if (c.ContractID.ToString() == contractId) return c.Title;
            }
            return null;
        }

        // Stock's localized title, else the humanized config name: the same resolver the
        // Timeline's strategy rows use, so both windows name a strategy identically.
        private static string ResolveStrategyTitle(string strategyId)
            => StrategyDisplayNames.Resolve(strategyId);

        // ================================================================
        // SpaceBeforeCapitals - humanization helper (design doc section 4.4)
        // ================================================================

        /// <summary>
        /// Inserts a space before a capital letter unless the previous character is
        /// also uppercase. Preserves acronyms like "VAB" (stays "VAB") while
        /// expanding camel/pascal-cased names like "FirstMunFlyby" -> "First Mun Flyby".
        /// Returns the input unchanged if null or empty. Shared with the Timeline's
        /// contract and facility display (GameActionDisplay, FacilityDisplayNames).
        /// </summary>
        internal static string SpaceBeforeCapitals(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c))
                {
                    char prev = s[i - 1];
                    // Space only when the previous character is not also uppercase
                    // (keeps runs of caps together - "VAB", "SPH", "RCS"). We also
                    // keep the space when the previous char is a lowercase letter or
                    // digit; the "prev not uppercase" rule captures both.
                    if (!char.IsUpper(prev)) sb.Append(' ');
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ================================================================
        // Window chrome (Phase 2)
        // ================================================================

        /// <summary>
        /// Renders the Career State window if open. Mirrors KerbalsWindowUI.DrawIfOpen:
        /// initializes the window rect on first draw, runs resize-drag, wires
        /// ClickThruBlocker.GUILayoutWindow, and takes/releases the CAMERACONTROLS
        /// input lock based on mouse position.
        /// </summary>
        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showCareerStateWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (careerStateWindowRect.width < 1f)
            {
                careerStateWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    DefaultWindowWidth,
                    DefaultWindowHeight);
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Career State window initial position: x={careerStateWindowRect.x.ToString("F0", ic)} y={careerStateWindowRect.y.ToString("F0", ic)}");
            }

            ParsekUI.HandleResizeDrag(ref careerStateWindowRect, ref isResizingCareerStateWindow,
                MinWindowWidth, MinWindowHeight, "Career State window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                careerStateWindowRect = ClickThruBlocker.GUILayoutWindow(
                    WindowIdKey.GetHashCode(),
                    careerStateWindowRect,
                    DrawCareerStateWindow,
                    "Parsek - Career State",
                    opaqueWindowStyle,
                    GUILayout.Width(careerStateWindowRect.width),
                    GUILayout.Height(careerStateWindowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            parentUI.LogWindowPosition("CareerState", ref lastCareerStateWindowRect, careerStateWindowRect);

            if (careerStateWindowRect.Contains(Event.current.mousePosition))
            {
                if (!careerStateWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, CareerStateInputLockId);
                    careerStateWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        /// <summary>
        /// Whether this window currently holds its KSP input lock. Read by the design-7.2
        /// mode-change close handler purely for its diagnostic line: the release itself is
        /// unconditional, so a wrong read here can never leak a lock.
        /// </summary>
        internal bool HasInputLock => careerStateWindowHasInputLock;

        // Logical tab indices. What the seam's `UiAction op=tab window=career` vocabulary
        // (contracts, strategies - by position) and SelectedTabForTesting speak.
        internal const int TabContracts = 0;
        internal const int TabStrategies = 1;

        private static readonly int[] CareerTabs = { TabContracts, TabStrategies };
        private static readonly int[] NoTabs = new int[0];

        /// <summary>
        /// Test seam for the transient tab selection. Writable so the in-game
        /// `ModeRoundTripPreservesWindowState` check can set a distinguishable bit of state
        /// and assert the design-7.2 auto-close preserved it (edge case 1: nothing is
        /// destroyed, only hidden).
        ///
        /// <para>A write is coerced onto a tab the current mode actually draws (read from
        /// the cached view model). Only Career draws tabs, and it draws both, so the
        /// coercion only matters for an out-of-range index. With no cached view model yet
        /// the value is stored as written and the draw coerces it.</para>
        /// </summary>
        internal int SelectedTabForTesting
        {
            get { return selectedTab; }
            set
            {
                selectedTab = cachedVM.HasValue
                    ? CoerceTab(value, VisibleTabsFor(cachedVM.Value.Mode))
                    : value;
            }
        }

        /// <summary>Number of tabs this window draws (test seam for the round-trip check).</summary>
        internal static int TabCountForTesting => TabLabels.Length;

        /// <summary>
        /// The tabs a game mode draws, in order: Contracts and Strategies in Career, none
        /// anywhere else (the banner says why; the launcher is not offered there at all).
        /// </summary>
        internal static int[] VisibleTabsFor(Game.Modes mode)
        {
            return ModeShowsCareerState(mode) ? CareerTabs : NoTabs;
        }

        /// <summary>
        /// The tab to draw for a stored selection: the selection itself when the mode draws
        /// it (or draws no tabs at all), else the first tab the mode does draw.
        /// </summary>
        internal static int CoerceTab(int tab, int[] visibleTabs)
        {
            if (visibleTabs == null || visibleTabs.Length == 0) return tab;
            if (Array.IndexOf(visibleTabs, tab) >= 0) return tab;
            return visibleTabs[0];
        }

        /// <summary>
        /// Whether the Career launcher on the main window is offered in this game mode:
        /// Career only. Contracts and strategies exist nowhere else, and the dated career
        /// history Science mode does have (milestones, facilities, tech) is the Timeline's
        /// Career view.
        /// </summary>
        internal static bool ModeOffersLauncher(Game.Modes mode)
        {
            return ModeShowsCareerState(mode);
        }

        internal void ReleaseInputLock()
        {
            if (!careerStateWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(CareerStateInputLockId);
            careerStateWindowHasInputLock = false;
        }

        private void EnsureStyles()
        {
            // The column header style is shared across the mod via ParsekUI; reassign every
            // draw so any ParsekUI-level updates flow through.
            columnHeaderStyle = parentUI.GetColumnHeaderStyle();
            if (toggleButtonStyle != null) return;
            groupHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
            // Toggle button "on" state reuses the button's own pressed texture so a
            // selected tab / expanded fold looks pressed. Mirrors TimelineWindowUI
            // (onNormal/onHover copied from button.active).
            toggleButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            toggleButtonStyle.onNormal.background = GUI.skin.button.active.background;
            toggleButtonStyle.onHover.background = GUI.skin.button.active.background;
            toggleButtonStyle.onNormal.textColor = Color.white;
            toggleButtonStyle.onHover.textColor = Color.white;

            // Amber marks a CELL that needs attention (an overdue deadline, a recorded
            // failure). Rows that are merely in the future are not coloured: they already
            // sit under the "Accepted later" fold, and a second marker on every such
            // row would bury the warnings.
            alertStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.95f, 0.78f, 0.45f) }
            };
            grayStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) }
            };
            // The name cell is a label-styled BUTTON (the Timeline cross-link), so it
            // clips instead of wrapping like a label and shows no button chrome.
            nameCellStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            bannerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.82f, 0.82f, 0.82f) }
            };
        }

        private void DrawCareerStateWindow(int windowID)
        {
            EnsureStyles();
            // Breathing room below the title bar - matches Timeline's visual spacing.
            GUILayout.Space(5);

            // Rebuild the cached VM on demand. Defensive against a null CurrentGame
            // (e.g. transient scene transitions or a mod that hot-swaps HighLogic).
            var currentGame = HighLogic.CurrentGame;
            if (currentGame == null)
            {
                ParsekLog.Warn("UI",
                    "CareerStateWindow: HighLogic.CurrentGame is null; rendering fallback");
                GUILayout.Label("Career state unavailable - game not loaded", bannerStyle);
                if (GUILayout.Button("Close"))
                {
                    IsOpen = false;
                }
                ParsekUI.DrawResizeHandle(careerStateWindowRect, ref isResizingCareerStateWindow,
                    "Career State window");
                GUI.DragWindow();
                return;
            }

            var currentMode = currentGame.Mode;
            double liveUT = Planetarium.GetUniversalTime();

            if (ShouldRebuildCachedVM(cachedVM, currentMode, liveUT))
            {
                // [Phase 3] ELS-routed: career state view reads non-tombstoned
                // ledger actions only (design section 3.4 career-state UI).
                cachedVM = Build(
                    EffectiveState.ComputeELS(),
                    liveUT,
                    currentMode,
                    LedgerOrchestrator.Contracts,
                    LedgerOrchestrator.Strategies,
                    FormatDate);
            }

            var vm = cachedVM.Value;

            // Mode banner (design doc section 5.4).
            GUILayout.Label(vm.BannerText ?? FormatModeBanner(vm, FormatDate), bannerStyle);
            LogModeRender(vm.Mode, ref lastRenderedMode);

            // Tab bar. Use the pressed-style button (see toggleButtonStyle in EnsureStyles)
            // so the selected tab is visibly pushed in - matches the Timeline window's
            // filter-toggle idiom.
            int[] visibleTabs = VisibleTabsFor(vm.Mode);
            if (visibleTabs.Length > 0)
            {
                int coerced = CoerceTab(selectedTab, visibleTabs);
                if (coerced != selectedTab)
                {
                    ParsekLog.Verbose("UI",
                        $"CareerStateWindow: tab {selectedTab} not drawn in mode={vm.Mode}; showing tab {coerced}");
                    selectedTab = coerced;
                    careerStateScrollPos.y = 0f;
                }
                int pos = Array.IndexOf(visibleTabs, selectedTab);
                int newPos = GUILayout.Toolbar(pos, TabLabels, toggleButtonStyle);
                if (newPos != pos && newPos >= 0 && newPos < visibleTabs.Length)
                {
                    SwitchTab(selectedTab, visibleTabs[newPos]);
                    selectedTab = visibleTabs[newPos];
                    careerStateScrollPos.y = 0f;
                }
            }

            careerStateScrollPos = GUILayout.BeginScrollView(careerStateScrollPos, GUILayout.ExpandHeight(true));

            if (visibleTabs.Length > 0)
            {
                if (selectedTab == TabStrategies)
                    DrawStrategiesTab(vm.Strategies);
                else
                    DrawContractsTab(vm.Contracts, vm.LiveUT);
            }

            GUILayout.EndScrollView();

            // Bottom "hovered control help text" strip (shared house helper), drawn after
            // the tab body (so the live GUI.tooltip read sees a hovered column header or
            // row) and directly above the Close button - the house ordering every Parsek
            // window uses. Fixed single-line height here, always present.
            tooltipEcho.Draw();

            if (GUILayout.Button("Close"))
            {
                IsOpen = false;
            }

            ParsekUI.DrawResizeHandle(careerStateWindowRect, ref isResizingCareerStateWindow,
                "Career State window");

            GUI.DragWindow();
        }

        /// <summary>Refresh cadence while every drawn date has minute resolution.</summary>
        internal const double MinuteRefreshSeconds = 60.0;

        /// <summary>
        /// Refresh cadence while a deadline is close enough to live UT that its relative
        /// tail reads in seconds (<c>(in 45s)</c> / <c>(overdue 12s)</c>).
        /// </summary>
        internal const double SecondRefreshSeconds = 1.0;

        // A deadline within this many seconds of live UT (either side) switches the
        // window to per-second refresh. Two minutes, not one: the rebuild that first sees
        // it may run up to a minute late.
        private const double SecondResolutionWindowSeconds = 120.0;

        /// <summary>
        /// Formats every string the draw needs, once. OnGUI runs several events per
        /// frame, so formatting inside the draw re-ran the KSP date formatter on every one
        /// of them. Also sets <see cref="CareerStateViewModel.RefreshSeconds"/>.
        /// </summary>
        internal static void FillDisplayText(ref CareerStateViewModel vm,
                                             Func<double, string> formatDate)
        {
            bool secondResolution = false;

            var c = vm.Contracts;
            c.GroupHeadingText = FormatSlotHeading(c.Slots);
            c.GroupHeadingTooltip = FormatSlotHeadingTooltip(SlotTab.Contracts, c.Slots,
                c.MissionControlLevel, c.ProjectedMissionControlLevel);
            c.PendingFoldText = FormatPendingFold(SlotTab.Contracts,
                c.PendingRows != null ? c.PendingRows.Count : 0);
            c.PendingFoldTooltip = FormatPendingFoldTooltip(c.ProjectedActive, c.ProjectedMaxSlots);
            secondResolution |= FillContractText(c.CurrentRows, vm.LiveUT, formatDate);
            secondResolution |= FillContractText(c.ProjectedRows, vm.LiveUT, formatDate);
            secondResolution |= FillContractText(c.PendingRows, vm.LiveUT, formatDate);
            vm.Contracts = c;

            var st = vm.Strategies;
            st.GroupHeadingText = FormatSlotHeading(st.Slots);
            st.GroupHeadingTooltip = FormatSlotHeadingTooltip(SlotTab.Strategies, st.Slots,
                st.AdminLevel, st.ProjectedAdminLevel);
            st.PendingFoldText = FormatPendingFold(SlotTab.Strategies,
                st.PendingRows != null ? st.PendingRows.Count : 0);
            st.PendingFoldTooltip = FormatPendingFoldTooltip(st.ProjectedActive, st.ProjectedMaxSlots);
            FillStrategyText(st.CurrentRows, formatDate);
            FillStrategyText(st.ProjectedRows, formatDate);
            FillStrategyText(st.PendingRows, formatDate);
            vm.Strategies = st;

            vm.BannerText = FormatModeBanner(vm, formatDate);
            vm.RefreshSeconds = secondResolution ? SecondRefreshSeconds : MinuteRefreshSeconds;
        }

        // Returns whether any row's deadline tail is near enough to read in seconds.
        private static bool FillContractText(List<ContractRow> rows, double liveUT,
                                             Func<double, string> formatDate)
        {
            if (rows == null) return false;
            bool near = false;
            for (int i = 0; i < rows.Count; i++)
            {
                ContractRow r = rows[i];
                r.AcceptText = FormatContractRow_Accept(r, formatDate);
                r.DeadlineText = FormatContractRow_Deadline(r, liveUT, formatDate);
                r.DeadlineOverdue = IsDeadlineOverdue(r, liveUT);
                r.TimelineEndText = FormatContractRow_TimelineEnd(r, formatDate);
                rows[i] = r;
                if (!double.IsNaN(r.DeadlineUT)
                    && Math.Abs(r.DeadlineUT - liveUT) < SecondResolutionWindowSeconds)
                    near = true;
            }
            return near;
        }

        private static void FillStrategyText(List<StrategyRow> rows,
                                             Func<double, string> formatDate)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++)
            {
                StrategyRow r = rows[i];
                r.ActivateText = FormatStrategyRow_Activate(r, formatDate);
                r.FlowText = FormatStrategyRow_Flow(r);
                r.TimelineEndText = FormatStrategyRow_TimelineEnd(r, formatDate);
                rows[i] = r;
            }
        }

        /// <summary>
        /// The refresh bucket a live UT falls in: the rebuild predicate re-formats when
        /// it changes. Minute buckets line up with the KSP clock's minute boundaries
        /// (the compact date's finest unit), so the banner turns over on time.
        /// </summary>
        internal static long RefreshBucket(double ut, double refreshSeconds)
        {
            double period = refreshSeconds > 0.0 ? refreshSeconds : MinuteRefreshSeconds;
            return (long)Math.Floor(ut / period);
        }

        /// <summary>
        /// The italic line above the tabs. Career shows the live date and, when the
        /// recorded timeline still changes something, the date it ends on. Any other mode
        /// (reachable only through the automation seam, since the launcher is Career-only)
        /// says what the mode leaves out.
        /// </summary>
        internal static string FormatModeBanner(CareerStateViewModel vm,
                                                Func<double, string> formatDate)
        {
            if (vm.Mode == Game.Modes.CAREER)
            {
                string line = "Career mode - " + FormatDateCell(vm.LiveUT, formatDate);
                if (vm.HasDivergence)
                    line += "  (timeline ends " + FormatDateCell(vm.TerminalUT, formatDate) + ")";
                return line;
            }
            if (vm.Mode == Game.Modes.SCIENCE_SANDBOX)
                return "Science mode - contracts and strategies are not tracked";
            // SANDBOX, MISSION_BUILDER, MISSION: all treated as sandbox-equivalent.
            return "Sandbox mode - career state is not tracked";
        }

        // ---- Heading and fold text (pure, InvariantCulture, testable) ----

        /// <summary>A slot limit at or above this is stock's "no limit" (Mission Control L3 = 999).</summary>
        internal const int UnlimitedSlotThreshold = 999;

        /// <summary>Which tab a slot text is for: the two differ only in their verbs.</summary>
        internal enum SlotTab
        {
            Contracts,
            Strategies,
        }

        /// <summary>
        /// <c>4 of 7 slots free</c>: free first, the noun agreeing with the limit
        /// (<c>1 of 1 slot free</c>).
        /// </summary>
        internal static string FormatFreeSlots(int free, int max)
        {
            var ic = CultureInfo.InvariantCulture;
            return free.ToString(ic) + " of " + max.ToString(ic)
                + (max == 1 ? " slot free" : " slots free");
        }

        /// <summary>
        /// The one heading line of a tab, free first, then active, then reserved:
        /// <c>4 of 7 slots free (2 active, 1 reserved for later)</c>, <c>5 of 7 slots free
        /// (2 active)</c> when the recorded future reserves none, <c>No slot limit (2
        /// active)</c> at an unlimited building.
        /// </summary>
        internal static string FormatSlotHeading(SlotUsage u)
        {
            var ic = CultureInfo.InvariantCulture;
            string active = u.Active.ToString(ic) + " active";
            if (u.Unlimited)
                return "No slot limit (" + active + ")";
            string text = FormatFreeSlots(u.Free, u.Limit) + " (" + active;
            if (u.Reserved > 0)
                text += ", " + u.Reserved.ToString(ic) + " reserved for later";
            return text + ")";
        }

        /// <summary>
        /// The heading's hover text. With a reservation it says why fewer slots are free
        /// than the active count leaves: <c>Contracts your recorded flights accept later need 1 more
        /// slot at peak, so only 4 are free for a new one.</c> It states the ledger's
        /// count and nothing more: stock Mission Control / Administration count only what
        /// is active now and do not refuse an accept or activation over it. Without one it
        /// names the building level the limit comes from (and the level at the timeline
        /// end when the recorded timeline upgrades it).
        /// </summary>
        internal static string FormatSlotHeadingTooltip(SlotTab tab, SlotUsage u,
                                                        int levelNow, int levelAtEnd)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!u.Unlimited && u.Reserved > 0)
            {
                string subject = tab == SlotTab.Strategies
                    ? "Strategies your recorded flights activate later"
                    : "Contracts your recorded flights accept later";
                return subject + " need " + u.Reserved.ToString(ic)
                    + (u.Reserved == 1 ? " more slot" : " more slots")
                    + " at peak, so only " + u.Free.ToString(ic)
                    + (u.Free == 1 ? " is" : " are") + " free for a new one.";
            }
            string building = tab == SlotTab.Strategies ? "Administration" : "Mission Control";
            string text = (u.Unlimited ? "No slot limit at " : "Slot limit from ")
                + building + " L" + levelNow.ToString(ic);
            if (levelAtEnd != levelNow)
                text += " (L" + levelAtEnd.ToString(ic) + " at timeline end)";
            return text + ".";
        }

        /// <summary>
        /// The fold row that opens the group of rows the recorded flights add later:
        /// <c>Accepted later by your recorded flights (1)</c> /
        /// <c>Activated later by your recorded flights (1)</c>.
        /// </summary>
        internal static string FormatPendingFold(SlotTab tab, int pendingCount)
        {
            return (tab == SlotTab.Strategies ? "Activated" : "Accepted")
                + " later by your recorded flights ("
                + pendingCount.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// The fold row's hover text: <c>Not active yet. At the end of the recorded
        /// timeline: 3 of 7 slots free.</c> (<c>... timeline: no slot limit.</c> at an
        /// unlimited building).
        /// </summary>
        internal static string FormatPendingFoldTooltip(int activeAtEnd, int maxSlotsAtEnd)
        {
            const string head = "Not active yet. At the end of the recorded timeline: ";
            if (maxSlotsAtEnd >= UnlimitedSlotThreshold)
                return head + "no slot limit.";
            return head + FormatFreeSlots(Math.Max(0, maxSlotsAtEnd - activeAtEnd), maxSlotsAtEnd) + ".";
        }

        /// <summary>
        /// What a tab body draws: nothing but the grey empty line when it has no row now
        /// and none pending, else the heading, one column header and the rows.
        /// </summary>
        internal static bool IsTabEmpty(int currentRowCount, int pendingRowCount)
        {
            return currentRowCount == 0 && pendingRowCount == 0;
        }

        // ---- Timeline cross-link ----

        /// <summary>
        /// The subject a contract row's name cell scrolls the Timeline to: the ledger
        /// contract id, which is exactly what the Timeline stamps on that contract's rows
        /// (<see cref="TimelineCareerCategories.ResolveSubjectId"/> reads
        /// <c>GameAction.ContractId</c>). Null when the row carries no id, and then the
        /// cell is not a link.
        /// </summary>
        internal static string ContractLinkSubject(ContractRow r)
            => string.IsNullOrEmpty(r.ContractId) ? null : r.ContractId;

        /// <summary>The strategy equivalent of <see cref="ContractLinkSubject"/>
        /// (<c>GameAction.StrategyId</c>).</summary>
        internal static string StrategyLinkSubject(StrategyRow r)
            => string.IsNullOrEmpty(r.StrategyId) ? null : r.StrategyId;

        /// <summary>
        /// Pure helper for the Career -> Timeline cross-link. Production passes
        /// <c>parentUI.GetTimelineUI().ScrollToCareerSubject</c>; tests pass a spy.
        /// Tolerates a null callback (the Timeline window can be null during a cold-start
        /// scene transition) so the click never throws; the log still fires.
        /// </summary>
        internal static void OnRowNameClicked(
            Action<TimelineCareerCategory, string> scrollCallback,
            TimelineCareerCategory category, string subjectId)
        {
            ParsekLog.Verbose("UI",
                $"CareerStateWindow: row -> Timeline scroll category={category} subject={subjectId}"
                + (scrollCallback == null ? " (no Timeline window)" : ""));
            if (scrollCallback != null) scrollCallback(category, subjectId);
        }

        private Action<TimelineCareerCategory, string> TimelineScrollCallback()
        {
            TimelineWindowUI timelineUI = parentUI != null ? parentUI.GetTimelineUI() : null;
            return timelineUI != null
                ? timelineUI.ScrollToCareerSubject
                : (Action<TimelineCareerCategory, string>)null;
        }

        // ================================================================
        // Per-tab renderers
        // ================================================================

        private const string TimelineEndTooltip =
            "What the recorded timeline does to this row before it ends.";

        // Both tabs share one layout: the heading line (slots now), ONE column header, and
        // one body box holding the rows active now and - only when the recorded timeline
        // adds rows - a fold row with the pending rows under it, so both groups sit under
        // the same columns. A tab with nothing now and nothing pending is one grey line.
        private void DrawContractsTab(ContractsTabVM tab, double liveUT)
        {
            if (IsTabEmpty(tab.CurrentRows.Count, tab.PendingRows.Count))
            {
                GUILayout.Label(NoActiveContractsText, grayStyle);
                return;
            }

            bool showEnd = AnyRowEnds(tab.CurrentRows) || AnyRowEnds(tab.PendingRows);
            GUILayout.Label(new GUIContent(tab.GroupHeadingText ?? "", tab.GroupHeadingTooltip),
                groupHeaderStyle);
            DrawContractsColumnHeader(showEnd);
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            if (tab.CurrentRows.Count == 0)
                GUILayout.Label(NoActiveContractsText, grayStyle);
            for (int i = 0; i < tab.CurrentRows.Count; i++)
                DrawContractRow(tab.CurrentRows[i], liveUT, showEnd);
            if (tab.PendingRows.Count > 0
                && DrawPendingFold(GroupKey_ContractsPending, tab.PendingFoldText,
                                   tab.PendingFoldTooltip))
            {
                for (int i = 0; i < tab.PendingRows.Count; i++)
                    DrawContractRow(tab.PendingRows[i], liveUT, showEnd);
            }
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Draws one "Accepted later by your recorded flights (n)" fold row inside a table
        /// body and returns whether its group is expanded. Both tabs share it.
        /// </summary>
        private bool DrawPendingFold(string foldKey, string text, string tooltip)
        {
            bool expanded = !foldedGroups.Contains(foldKey);
            bool newExpanded = GUILayout.Toggle(expanded,
                new GUIContent(text ?? "", tooltip ?? ""),
                toggleButtonStyle,
                GUILayout.ExpandWidth(true));
            if (newExpanded != expanded)
                ToggleSection(foldedGroups, foldKey);
            return newExpanded;
        }

        // Both tables in this window open BOTH their column-header row and every body row
        // with parentUI.GetTableRowStyle(), and wrap the body in
        // parentUI.GetTableBodyBoxStyle(). That is what keeps each cell under its own
        // header: this window's header rows and body rows live in DIFFERENT parents
        // (the header directly in the window scroll view, the rows inside the body
        // box), and with plain BeginHorizontal() the box's own margin put every cell
        // 4px right of its header. The header is INSIDE the same scroll view as the
        // body, so it must NOT reserve a scrollbar gutter - it shrinks with the body
        // already. Contract: ParsekUI.TableRowHorizontalInsetPx.
        //
        // The FIRST (name) column of both tables expands in header and rows alike; the
        // zero horizontal inset of both parents gives it the same width on both sides,
        // so the fixed columns to its right stay under their headers.
        private void DrawContractsColumnHeader(bool showEnd)
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label("Contract", columnHeaderStyle,
                GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth));
            GUILayout.Label(
                new GUIContent("Accepted", "When the contract was taken on."),
                columnHeaderStyle, GUILayout.Width(ColW_Date));
            GUILayout.Label(
                new GUIContent("Deadline",
                    "When the contract expires, and how long that is from now."),
                columnHeaderStyle, GUILayout.Width(ColW_Deadline));
            if (showEnd)
                GUILayout.Label(new GUIContent("Timeline end", TimelineEndTooltip),
                    columnHeaderStyle, GUILayout.Width(ColW_TimelineEnd));
            GUILayout.EndHorizontal();
        }

        private void DrawContractRow(ContractRow r, double liveUT, bool showEnd)
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            // The name cell is the Timeline cross-link: a label-styled button, so the row
            // declares exactly the columns its header does (TableRowInsetAlignmentTests).
            string subject = ContractLinkSubject(r);
            if (GUILayout.Button(
                    new GUIContent(FormatContractRow_Title(r),
                        subject != null ? ContractLinkTooltip : null),
                    nameCellStyle,
                    GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth))
                && subject != null)
            {
                OnRowNameClicked(TimelineScrollCallback(),
                    TimelineCareerCategory.Contracts, subject);
            }
            GUILayout.Label(r.AcceptText ?? "", GUI.skin.label,
                GUILayout.Width(ColW_Date));
            GUILayout.Label(r.DeadlineText ?? "",
                r.DeadlineOverdue ? alertStyle : GUI.skin.label,
                GUILayout.Width(ColW_Deadline));
            if (showEnd)
                GUILayout.Label(r.TimelineEndText ?? "",
                    IsTimelineEndAlert(r.EndKind) ? alertStyle : GUI.skin.label,
                    GUILayout.Width(ColW_TimelineEnd));
            GUILayout.EndHorizontal();
        }

        private void DrawStrategiesTab(StrategiesTabVM tab)
        {
            if (IsTabEmpty(tab.CurrentRows.Count, tab.PendingRows.Count))
            {
                GUILayout.Label(NoActiveStrategiesText, grayStyle);
                return;
            }

            bool showEnd = AnyRowEnds(tab.CurrentRows) || AnyRowEnds(tab.PendingRows);
            GUILayout.Label(new GUIContent(tab.GroupHeadingText ?? "", tab.GroupHeadingTooltip),
                groupHeaderStyle);
            DrawStrategiesColumnHeader(showEnd);
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            if (tab.CurrentRows.Count == 0)
                GUILayout.Label(NoActiveStrategiesText, grayStyle);
            for (int i = 0; i < tab.CurrentRows.Count; i++)
                DrawStrategyRow(tab.CurrentRows[i], showEnd);
            if (tab.PendingRows.Count > 0
                && DrawPendingFold(GroupKey_StrategiesPending, tab.PendingFoldText,
                                   tab.PendingFoldTooltip))
            {
                for (int i = 0; i < tab.PendingRows.Count; i++)
                    DrawStrategyRow(tab.PendingRows[i], showEnd);
            }
            GUILayout.EndVertical();
        }

        private void DrawStrategiesColumnHeader(bool showEnd)
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label("Strategy", columnHeaderStyle,
                GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth));
            GUILayout.Label(
                new GUIContent("Activated", "When the strategy was switched on."),
                columnHeaderStyle, GUILayout.Width(ColW_Date));
            GUILayout.Label(
                new GUIContent("Flow",
                    "What the strategy converts into what, and at what commitment."),
                columnHeaderStyle, GUILayout.Width(ColW_Flow));
            if (showEnd)
                GUILayout.Label(new GUIContent("Timeline end", TimelineEndTooltip),
                    columnHeaderStyle, GUILayout.Width(ColW_TimelineEnd));
            GUILayout.EndHorizontal();
        }

        private void DrawStrategyRow(StrategyRow r, bool showEnd)
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            string subject = StrategyLinkSubject(r);
            if (GUILayout.Button(
                    new GUIContent(FormatStrategyRow_Title(r),
                        subject != null ? StrategyLinkTooltip : null),
                    nameCellStyle,
                    GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth))
                && subject != null)
            {
                OnRowNameClicked(TimelineScrollCallback(),
                    TimelineCareerCategory.Strategies, subject);
            }
            GUILayout.Label(r.ActivateText ?? "", GUI.skin.label,
                GUILayout.Width(ColW_Date));
            GUILayout.Label(r.FlowText ?? "", GUI.skin.label, GUILayout.Width(ColW_Flow));
            if (showEnd)
                GUILayout.Label(r.TimelineEndText ?? "", GUI.skin.label,
                    GUILayout.Width(ColW_TimelineEnd));
            GUILayout.EndHorizontal();
        }

        // ================================================================
        // Formatting helpers (pure, InvariantCulture, testable)
        // ================================================================

        /// <summary>
        /// The window's live date formatter: the Timeline's own compact KSP date
        /// (<c>TimelineWindowUI.FormatTimelineEntryTimeLabel</c>, the same one the
        /// Kerbals window uses), e.g. <c>Y1, D51, 05:17</c>, with its raw-UT fallback
        /// outside KSP. The pure formatters below take it as a delegate.
        /// </summary>
        internal static string FormatDate(double ut)
        {
            return TimelineWindowUI.FormatTimelineEntryTimeLabel(ut, 0.0, false);
        }

        /// <summary>A date cell: <c>--</c> for NaN, else the formatter's text.</summary>
        internal static string FormatDateCell(double ut, Func<double, string> formatDate)
        {
            if (double.IsNaN(ut)) return "--";
            if (formatDate == null) return ut.ToString("F0", CultureInfo.InvariantCulture);
            string text = formatDate(ut);
            return string.IsNullOrEmpty(text) ? "--" : text;
        }

        /// <summary>
        /// The relative tail after a deadline: <c>(in 12d)</c> while it lies ahead of
        /// <paramref name="liveUT"/>, <c>(overdue 3d)</c> once it has passed. Only the
        /// largest unit of the house compact duration, in the player's calendar.
        /// </summary>
        internal static string FormatRelativeTail(double targetUT, double liveUT)
        {
            double delta = targetUT - liveUT;
            string span = LargestUnit(ParsekTimeFormat.FormatDuration(Math.Abs(delta)));
            return delta > 0 ? "(in " + span + ")" : "(overdue " + span + ")";
        }

        private static string LargestUnit(string compactDuration)
        {
            if (string.IsNullOrEmpty(compactDuration)) return compactDuration;
            int space = compactDuration.IndexOf(' ');
            return space < 0 ? compactDuration : compactDuration.Substring(0, space);
        }

        /// <summary>
        /// The "Timeline end" text for a contract or strategy: what the recorded timeline
        /// does to it and when, or empty when it is still running at the end.
        /// A failure is upper-cased: it costs funds and reputation.
        /// </summary>
        internal static string FormatTimelineEnd(TimelineEndKind kind, double endUT,
                                                 Func<double, string> formatDate)
        {
            switch (kind)
            {
                case TimelineEndKind.Completed: return "completes " + FormatDateCell(endUT, formatDate);
                case TimelineEndKind.Failed: return "FAILS " + FormatDateCell(endUT, formatDate);
                case TimelineEndKind.Expired: return "expires " + FormatDateCell(endUT, formatDate);
                case TimelineEndKind.Cancelled: return "cancelled " + FormatDateCell(endUT, formatDate);
                case TimelineEndKind.Deactivated: return "deactivates " + FormatDateCell(endUT, formatDate);
                default: return "";
            }
        }

        /// <summary>Whether a Timeline-end cell is drawn in the alert colour: a failure, and
        /// an expiry, which costs the same failure penalties in stock.</summary>
        internal static bool IsTimelineEndAlert(TimelineEndKind kind)
        {
            return kind == TimelineEndKind.Failed || kind == TimelineEndKind.Expired;
        }

        // ---- Per-column contract helpers ----

        /// <summary>
        /// Returns the contract display title, falling back to the raw id and
        /// then to "(unknown)" when both are null/empty.
        /// </summary>
        internal static string FormatContractRow_Title(ContractRow r)
        {
            return string.IsNullOrEmpty(r.DisplayTitle) ? (r.ContractId ?? "(unknown)") : r.DisplayTitle;
        }

        internal static string FormatContractRow_Accept(ContractRow r, Func<double, string> formatDate)
        {
            return FormatDateCell(r.AcceptUT, formatDate);
        }

        /// <summary>
        /// The deadline date plus its relative tail (<c>Y1, D90 (in 12d)</c>), or the
        /// literal <c>--</c> for a contract with no deadline.
        /// </summary>
        internal static string FormatContractRow_Deadline(ContractRow r, double liveUT,
                                                          Func<double, string> formatDate)
        {
            if (double.IsNaN(r.DeadlineUT)) return "--";
            return FormatDateCell(r.DeadlineUT, formatDate) + " "
                + FormatRelativeTail(r.DeadlineUT, liveUT);
        }

        /// <summary>Whether the deadline has already passed at <paramref name="liveUT"/>.</summary>
        internal static bool IsDeadlineOverdue(ContractRow r, double liveUT)
        {
            return !double.IsNaN(r.DeadlineUT) && r.DeadlineUT <= liveUT;
        }

        internal static string FormatContractRow_TimelineEnd(ContractRow r,
                                                             Func<double, string> formatDate)
        {
            return FormatTimelineEnd(r.EndKind, r.EndUT, formatDate);
        }

        // ---- Per-column strategy helpers ----

        internal static string FormatStrategyRow_Title(StrategyRow r)
        {
            return string.IsNullOrEmpty(r.DisplayTitle) ? (r.StrategyId ?? "(unknown)") : r.DisplayTitle;
        }

        internal static string FormatStrategyRow_Activate(StrategyRow r, Func<double, string> formatDate)
        {
            return FormatDateCell(r.ActivateUT, formatDate);
        }

        /// <summary>
        /// Returns "Source -> Target @ pct%" (e.g. "Funds -> Science @ 10.0%"),
        /// InvariantCulture F1 on the commitment percentage.
        /// </summary>
        internal static string FormatStrategyRow_Flow(StrategyRow r)
        {
            var ic = CultureInfo.InvariantCulture;
            string pct = (r.Commitment * 100f).ToString("F1", ic) + "%";
            return $"{r.SourceResource} -> {r.TargetResource} @ {pct}";
        }

        internal static string FormatStrategyRow_TimelineEnd(StrategyRow r,
                                                             Func<double, string> formatDate)
        {
            return FormatTimelineEnd(r.EndKind, r.EndUT, formatDate);
        }

        // ================================================================
        // Pure helpers for log-assertion tests (extracted for testability)
        // ================================================================

        /// <summary>
        /// Logs a tab-switch Verbose message. Called from DrawCareerStateWindow
        /// when the user clicks a different tab; extracted as a pure helper so
        /// the log contract is unit-testable outside IMGUI.
        /// </summary>
        internal static void SwitchTab(int oldTab, int newTab)
        {
            ParsekLog.Verbose("UI",
                $"CareerStateWindow: tab switched {oldTab}->{newTab}");
        }

        /// <summary>
        /// Toggles the fold state of the named section in the supplied set and
        /// logs the new state. Extracted as a pure helper (mirrors
        /// KerbalsWindowUI.ToggleFold) so the contract is unit-testable outside
        /// IMGUI. Returns true when the section is now folded.
        /// </summary>
        internal static bool ToggleSection(HashSet<string> foldedGroups, string name)
            => SetSectionFolded(foldedGroups, !foldedGroups.Contains(name), name);

        /// <summary>
        /// Folds or unfolds one section, and returns whether it is now folded.
        ///
        /// <para>The absolute form behind <see cref="ToggleSection"/>, which a CLICK wants
        /// (it has no direction of its own) and a commanded write does NOT: the
        /// automation-only <c>UiAction op=expand window=career</c> seam op is told which
        /// state to reach, and a toggle would have flipped an already-correct fold into the
        /// wrong one. One writer for both, so the log line is the same either way.</para>
        /// </summary>
        internal static bool SetSectionFolded(HashSet<string> foldedGroups, bool folded,
                                              string name)
        {
            if (foldedGroups == null) return false;
            bool wasFolded = foldedGroups.Contains(name);
            if (folded) foldedGroups.Add(name);
            else foldedGroups.Remove(name);
            if (wasFolded != folded)
                ParsekLog.Verbose("UI",
                    $"CareerStateWindow: section toggled name={name} folded={folded}");
            return folded;
        }

        /// <summary>
        /// Every fold key this window keeps, in tab order.
        ///
        /// <para>Two, and the window has no others: the only other UI state here is the
        /// tab index. Named as a list so the seam's <c>key=all</c> / <c>key=none</c> form
        /// has something to enumerate instead of a copy of the constants; the list is
        /// BUILT from those constants, so it cannot drift from them.</para>
        /// </summary>
        internal static readonly string[] FoldGroupKeys = new[]
        {
            GroupKey_ContractsPending, GroupKey_StrategiesPending,
        };

        /// <summary>
        /// Emits a one-shot-per-mode-change Verbose log for Sandbox / Science
        /// mode renders. <paramref name="last"/> is updated in place to the
        /// current mode; repeat calls with the same mode produce no log.
        /// Extracted for log-assertion testing.
        /// </summary>
        internal static void LogModeRender(Game.Modes current, ref Game.Modes last)
        {
            if (current == last) return;
            last = current;
            if (current == Game.Modes.SANDBOX
                || current == Game.Modes.MISSION_BUILDER
                || current == Game.Modes.MISSION)
            {
                ParsekLog.Verbose("UI", "CareerStateWindow: rendered sandbox-empty state");
            }
            else if (current == Game.Modes.SCIENCE_SANDBOX)
            {
                ParsekLog.Verbose("UI", "CareerStateWindow: rendered science-mode banner (no tabs)");
            }
            // CAREER: no log (it's the default "normal" render).
        }
    }
}
