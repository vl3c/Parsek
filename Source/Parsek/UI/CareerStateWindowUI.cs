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
    /// Career State window - surfaces the four career modules (Contracts, Strategies,
    /// Facilities, Milestones) in a tabbed window. Every tab reads "what is true now" and
    /// "what the recorded timeline still does": a Pending-in-timeline group for rows the
    /// future adds, and a Timeline-end column for what it does to the rows that exist now.
    /// Which tabs draw depends on the game mode (<see cref="VisibleTabsFor"/>).
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
        private GUIStyle sectionHeaderStyle;
        private GUIStyle groupHeaderStyle;
        private GUIStyle columnHeaderStyle;
        // Toggle-button style for disclosure sections — reuses GUI.skin.button's own
        // active-state texture as the "on" background so expanded sections look pressed
        // (mirrors TimelineWindowUI.toggleButtonStyle).
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
        // Tall enough that the chrome (banner, tabs, section bar, group label, column
        // header, help strip, Close) still leaves four or five table rows visible; at the
        // old 200 px the scroll body was about 20 px tall and showed no row at all.
        internal const float MinWindowHeight = 320f;
        // Internal so the design-7.2 mode-change close set (`ParsekUI.BuildGatedWindowCloseSet`)
        // can carry the id, which lets the in-game lock-leak test assert directly against
        // InputLockManager instead of hardcoding a duplicate string.
        internal const string CareerStateInputLockId = "Parsek_CareerStateWindow";

        // Column widths - shared between header and body. The NAME column of every table
        // is not in this list: it takes GUILayout.ExpandWidth(true) in the header and in
        // every row (the Kerbals window's pattern), so a long contract title stops wrapping
        // and the header strip spans the whole table. The fixed columns are sized for a
        // compact KSP date ("Y12, D426, 05:17", 16 characters) at the 7 px/char bound
        // TooltipEchoBudgetTests uses, plus cell padding.
        internal const float ColW_Date = 145f;
        // A date plus its relative tail: "Y12, D426, 05:17 (overdue 99d)", 30 characters.
        internal const float ColW_Deadline = 220f;
        // The longest outcome: "upgrades to L3, Y12, D426, 05:17" (32 characters); two
        // facility changes at once share the cell and may wrap, which is rare enough.
        internal const float ColW_TimelineEnd = 240f;
        // Strategies: "Reputation -> Science @ 100.0%" is 30 characters.
        internal const float ColW_Flow = 230f;
        // Facilities: "L3 (destroyed)" / "destroyed".
        internal const float ColW_Level = 120f;
        // Milestones tab. Sized to hold a THREE-PART reward on ONE line: a wrapped label
        // in a fixed-stride row overlaps its neighbours (the 2026-09-11 census showed two
        // such cells 36 px tall at the old 180f).
        //
        // The reward values are unbounded in Parsek code - the patch stores whatever KSP
        // passes to ProgressNode.AwardProgress - so the width is sized against a DOCUMENTED
        // bound rather than a derived cap: 7 digits of funds, 4 of reputation, 4 + one decimal
        // of science ("+ 9999999 funds  + 9999 rep  + 9999.9 sci", 41 characters), which is
        // about 200x the stock-Normal milestone payout. 41 chars at the 7 px/char pessimistic
        // advance is 287 px, plus a 30 px cell padding allowance = 317, so 320 clears it. The
        // derivation is on CareerStateWindowUITests.MilestoneRewardsColumn_HoldsAThreePartRewardOnOneLine,
        // which asserts the fit rather than trusting this comment.
        internal const float ColW_Rewards = 320f;
        // The smallest width the expanding name column is budgeted, for the fit test.
        internal const float MinNameColumnWidth = 160f;
        // The floor the expanding name cell may shrink to, in header and rows alike. Name
        // cells do not wrap (nameCellStyle clips instead), so a narrow window keeps one
        // row per line - a wrapped title made one row four lines tall at the 520 px
        // minimum and left no room for a second row.
        internal const float NameCellMinWidth = 80f;

        // Disclosure arrows (mirrors KerbalsWindowUI:34-35).

        // Transient fold state for the Contracts / Strategies / Milestones "Pending in
        // timeline" section headers.
        // Default-unfolded means we only store names that are currently folded. Tab switches
        // do NOT clear this set — folds persist across the window's lifetime.
        internal readonly HashSet<string> foldedGroups = new HashSet<string>(StringComparer.Ordinal);

        // Group-name constants for foldedGroups keys. Kept as constants so tests and
        // production share the exact strings.
        internal const string GroupKey_ContractsPending = "Contracts.Pending";
        internal const string GroupKey_StrategiesPending = "Strategies.Pending";
        internal const string GroupKey_MilestonesPending = "Milestones.Pending";

        // GUIContent (not bare strings) so each tab explains itself in the bottom help
        // strip on hover. Every tab shows the same two-part shape - what is true now, and
        // what the recorded timeline still has to deliver - and nothing in a one-word tab
        // label says so.
        private static readonly GUIContent[] TabLabels = new[]
        {
            new GUIContent("Contracts",
                "Contracts you hold now, and the ones your recorded flights accept later."),
            new GUIContent("Strategies",
                "Strategies running now, and the ones the recorded timeline activates later."),
            new GUIContent("Facilities",
                "KSC buildings now, and what the recorded timeline upgrades, wrecks or repairs."),
            new GUIContent("Milestones",
                "First-time achievements your recorded flights claim, and what each one paid.")
        };

        /// <summary>
        /// Stock KSP upgradeable facilities, in display order. Facilities with no
        /// actions in the ledger walk are merged in with default (L1, not destroyed)
        /// so the player sees a complete KSC inventory even on day 1.
        /// </summary>
        internal static readonly IReadOnlyList<string> FACILITY_DISPLAY_ORDER = new List<string>
        {
            "VehicleAssemblyBuilding",
            "SpaceplaneHangar",
            "LaunchPad",
            "Runway",
            "Administration",
            "MissionControl",
            "TrackingStation",
            "ResearchAndDevelopment",
            "AstronautComplex"
        };

        /// <summary>
        /// Test seam for contract title lookup. When non-null, Build() calls this
        /// instead of the live ContractSystem.Instance path. Tests inject a throwing
        /// or stubbed delegate to exercise the fallback branch (design doc E13).
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Design", "CA2211", Justification = "Test seam; not user-mutable at runtime.")]
        internal static Func<string, string> ContractTitleLookupForTesting;

        /// <summary>
        /// Test seam for strategy title lookup. See <see cref="ContractTitleLookupForTesting"/>.
        /// </summary>
        internal static Func<string, string> StrategyTitleLookupForTesting;

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
            public FacilitiesTabVM Facilities;
            public MilestonesTabVM Milestones;
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
            // it also completes / fails / cancels before its end - the "Pending in timeline"
            // group. A superset of ProjectedRows' pending entries: a contract accepted and
            // closed in the future is in neither CurrentRows nor ProjectedRows.
            public List<ContractRow> PendingRows;
            public string HeaderText;
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
            public string HeaderText;
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

        internal struct FacilitiesTabVM
        {
            public List<FacilityRow> Rows;
        }

        internal struct FacilityRow
        {
            public string FacilityId;
            public string DisplayTitle;
            public int CurrentLevel;
            public bool CurrentDestroyed;
            public int ProjectedLevel;
            public bool ProjectedDestroyed;
            public bool HasUpcomingChange;
            // UT of the last future action that set the terminal level, and of the last
            // intact <-> destroyed TRANSITION of the facility as a whole (a facility is
            // several buildings; one more building falling in an already-destroyed
            // facility is not a transition). Meaningful only when that half differs
            // between now and the timeline end.
            public double LevelChangeUT;
            public double DestroyedChangeUT;
            // Career: "L2" / "L2 (destroyed)"; Science: "destroyed" / "intact".
            public string LevelText;
            public string TimelineEndText;
        }

        internal struct MilestonesTabVM
        {
            public int CurrentCreditedCount;
            public int ProjectedCreditedCount;
            public List<MilestoneRow> Rows;
            public string HeaderText;
        }

        internal struct MilestoneRow
        {
            public string MilestoneId;
            public string DisplayTitle;
            public double CreditedUT;
            public float FundsAwarded;
            public float RepAwarded;
            public float ScienceAwarded;
            public bool IsPendingCredit;
            public string CreditedText;
            public string RewardsText;
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

        // A KSC facility is several DestructibleBuildings (the ledger keys a destruction
        // by the building's own id, "SpaceCenter/LaunchPad/Facility/<part>"), so a
        // facility is destroyed while ANY of its buildings is.
        private sealed class FacilityAcc
        {
            public int Level = 1;
            public readonly HashSet<string> DestroyedBuildings =
                new HashSet<string>(StringComparer.Ordinal);
            public double LevelUT = double.NaN;
            // UT of the facility's last intact <-> destroyed transition (see
            // ApplyBuildingDestroyedState), not of the last building action.
            public double DestroyedUT = double.NaN;
            public bool Destroyed => DestroyedBuildings.Count > 0;
        }

        private sealed class EndAcc
        {
            public TimelineEndKind Kind;
            public double UT;
        }

        // ================================================================
        // Build()
        // ================================================================

        /// <summary>
        /// Walks <paramref name="actions"/> in order once, projecting per-tab state
        /// as it goes. Snapshots "current" state at the boundary action.UT &lt;= liveUT;
        /// emits "projected" as the terminal state at the end of the walk.
        ///
        /// Only <see cref="GameAction.Effective"/> actions mutate contract/milestone
        /// state (mirrors ContractsModule/MilestonesModule.ProcessAction).
        ///
        /// <para>A building's destroyed state NOW comes from
        /// <paramref name="liveDestroyedBuildingIds"/> (stock's ScenarioDestructibles,
        /// read by the caller), never from the ledger: stock's state is what the player
        /// sees and it travels with every save, rewind and revert, while a ledger row can
        /// be missing for a destruction whose recording was discarded after a revert. The
        /// ledger supplies what the recorded future does after <paramref name="liveUT"/>:
        /// a destruction in a committed flight, and a repair made at the KSC after the
        /// rewind point.
        /// Null means no live data: nothing is destroyed now.</para>
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
            FacilitiesModule facilities,
            MilestonesModule milestones,
            Func<double, string> formatDate = null,
            ICollection<string> liveDestroyedBuildingIds = null)
        {
            if (contracts == null || strategies == null || facilities == null
                || milestones == null || actions == null)
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
            var facilityStateTerm = new Dictionary<string, FacilityAcc>(StringComparer.Ordinal);
            var creditedMilestonesTerm = new HashSet<string>(StringComparer.Ordinal);
            var allMilestoneRows = new List<MilestoneRow>();

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
            // Destructions / repairs after live UT, in walk order; applied on top of the
            // live destroyed set after the walk. Ones at or before live UT are ignored.
            var futureBuildingChanges = new List<GameAction>();
            int pastBuildingActionsIgnored = 0;

            // Mode gating (design doc E1/E2). These flags drive both row visibility
            // and which future actions can visibly change the cached window.
            bool contractsVisible = ModeShowsContracts(mode);
            bool strategiesVisible = ModeShowsStrategies(mode);
            bool facilitiesVisible = ModeShowsFacilities(mode);
            bool milestonesVisible = ModeShowsMilestones(mode);

            // "Current" snapshots — populated when we cross the liveUT boundary.
            Dictionary<string, ContractAcc> activeContractsCurSnap = null;
            Dictionary<string, StrategyAcc> activeStrategiesCurSnap = null;
            Dictionary<string, FacilityAcc> facilityStateCurSnap = null;
            HashSet<string> creditedMilestonesCurSnap = null;

            double terminalUT = liveUT;
            double nextRelevantActionUT = double.PositiveInfinity;

            // Walk actions, snapshotting current-state the moment we pass liveUT.
            // An action with UT <= liveUT counts as already-applied, so the snapshot is
            // taken lazily — right before processing the first action with UT > liveUT.
            bool snapshotTaken = false;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;

                if (!snapshotTaken && a.UT > liveUT)
                {
                    activeContractsCurSnap = CopyContracts(activeContractsTerm);
                    activeStrategiesCurSnap = CopyStrategies(activeStrategiesTerm);
                    facilityStateCurSnap = CopyFacilities(facilityStateTerm);
                    creditedMilestonesCurSnap = new HashSet<string>(creditedMilestonesTerm, StringComparer.Ordinal);
                    snapshotTaken = true;
                }

                if (a.UT > terminalUT) terminalUT = a.UT;

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
                            // (CONTRACT-DEADLINE-CAPTURED-AS-DURATION) — no narrowing
                            // round-trip left to undo.
                            DeadlineUT = a.DeadlineUT
                        };
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
                            if (a.UT > liveUT
                                && activeContractsTerm.TryGetValue(rid, out removed)
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
                            if (a.UT > liveUT
                                && activeStrategiesTerm.TryGetValue(rsid, out removedStrategy)
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
                        {
                            if (!a.Effective)
                            {
                                LogSkip("FacilityUpgrade", "Ineffective", a);
                                break;
                            }
                            FacilityAcc f = GetOrAddFacility(
                                facilityStateTerm, FacilityDisplayNames.FacilityIdForBuilding(a.FacilityId));
                            f.Level = a.ToLevel;
                            f.LevelUT = a.UT;
                        }
                        break;

                    case GameActionType.FacilityDestruction:
                    case GameActionType.FacilityRepair:
                        if (!a.Effective)
                        {
                            LogSkip(a.Type.ToString(), "Ineffective", a);
                            break;
                        }
                        // The destroyed state NOW is stock's (liveDestroyedBuildingIds), so a
                        // past destruction or repair here is not re-applied; a future one is
                        // projected on top of it.
                        if (a.UT <= liveUT)
                            pastBuildingActionsIgnored++;
                        else
                            futureBuildingChanges.Add(a);
                        break;

                    case GameActionType.MilestoneAchievement:
                        {
                            string mid = a.MilestoneId ?? "";
                            if (!a.Effective)
                            {
                                // Mirrors MilestonesModule: ineffective duplicates are skipped.
                                LogSkip("MilestoneAchievement", "Ineffective", a);
                                break;
                            }
                            if (creditedMilestonesTerm.Contains(mid))
                            {
                                // Defensive: Effective=true but already credited (should not happen
                                // given MilestonesModule.ProcessAction semantics, but we don't want
                                // to duplicate rows if an upstream bug slips through).
                                LogSkip("MilestoneAchievement", "AlreadyCredited", a);
                                break;
                            }
                            creditedMilestonesTerm.Add(mid);
                            allMilestoneRows.Add(new MilestoneRow
                            {
                                MilestoneId = mid,
                                DisplayTitle = HumanizeMilestoneTitle(mid),
                                CreditedUT = a.UT,
                                FundsAwarded = a.MilestoneFundsAwarded,
                                RepAwarded = a.MilestoneRepAwarded,
                                ScienceAwarded = a.MilestoneScienceAwarded,
                                IsPendingCredit = false // patched below if UT > liveUT
                            });
                        }
                        break;

                    default:
                        // Action types we don't project (science/funds/rep/kerbal). Silent.
                        break;
                }
            }

            // If no action ever exceeded liveUT, everything is "current" — snapshot now.
            if (!snapshotTaken)
            {
                activeContractsCurSnap = CopyContracts(activeContractsTerm);
                activeStrategiesCurSnap = CopyStrategies(activeStrategiesTerm);
                facilityStateCurSnap = CopyFacilities(facilityStateTerm);
                creditedMilestonesCurSnap = new HashSet<string>(creditedMilestonesTerm, StringComparer.Ordinal);
            }

            ApplyBuildingDestroyedState(
                facilityStateCurSnap, facilityStateTerm,
                liveDestroyedBuildingIds, futureBuildingChanges);

            // --- Facility levels for slot math (matches LedgerOrchestrator.UpdateSlotLimitsFromFacilities) ---
            // Contracts draw slots from MissionControl level; Strategies draw from Administration level.
            int missionControlLevelCur = 1;
            int missionControlLevelTerm = 1;
            FacilityAcc mcCur;
            if (facilityStateCurSnap.TryGetValue("MissionControl", out mcCur))
                missionControlLevelCur = mcCur.Level;
            FacilityAcc mcTerm;
            if (facilityStateTerm.TryGetValue("MissionControl", out mcTerm))
                missionControlLevelTerm = mcTerm.Level;

            int adminLevelCur = 1;
            int adminLevelTerm = 1;
            FacilityAcc adminCur;
            if (facilityStateCurSnap.TryGetValue("Administration", out adminCur))
                adminLevelCur = adminCur.Level;
            FacilityAcc adminTerm;
            if (facilityStateTerm.TryGetValue("Administration", out adminTerm))
                adminLevelTerm = adminTerm.Level;

            var contractsVM = CreateContractsTabVM(
                activeContractsCurSnap,
                activeContractsTerm,
                pendingContracts,
                contractEnds,
                currentContractEnds,
                missionControlLevelCur,
                missionControlLevelTerm,
                liveUT,
                contractsVisible);
            var strategiesVM = CreateStrategiesTabVM(
                activeStrategiesCurSnap,
                activeStrategiesTerm,
                pendingStrategies,
                strategyEnds,
                currentStrategyEnds,
                adminLevelCur,
                adminLevelTerm,
                liveUT,
                strategiesVisible);
            var facilitiesVM = CreateFacilitiesTabVM(
                facilityStateCurSnap,
                facilityStateTerm,
                facilitiesVisible);
            var milestonesVM = CreateMilestonesTabVM(
                allMilestoneRows,
                creditedMilestonesCurSnap,
                creditedMilestonesTerm,
                liveUT,
                milestonesVisible);

            // --- Divergence (any tab where current != projected). ---
            bool divergence =
                contractsVM.CurrentActive != contractsVM.ProjectedActive
                || contractsVM.PendingRows.Count > 0
                || AnyRowEnds(contractsVM.CurrentRows)
                || strategiesVM.CurrentActive != strategiesVM.ProjectedActive
                || strategiesVM.PendingRows.Count > 0
                || AnyRowEnds(strategiesVM.CurrentRows)
                || milestonesVM.CurrentCreditedCount != milestonesVM.ProjectedCreditedCount
                || AnyFacilityUpcomingChange(facilitiesVM.Rows)
                || adminLevelCur != adminLevelTerm;

            var vm = new CareerStateViewModel
            {
                Contracts = contractsVM,
                Strategies = strategiesVM,
                Facilities = facilitiesVM,
                Milestones = milestonesVM,
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
                + $"facilities={facilitiesVM.Rows.Count} "
                + "liveDestroyedBuildings=" + (liveDestroyedBuildingIds == null
                    ? "unknown"
                    : liveDestroyedBuildingIds.Count.ToString(CultureInfo.InvariantCulture)) + " "
                + $"futureBuildingChanges={futureBuildingChanges.Count} "
                + $"pastBuildingActionsIgnored={pastBuildingActionsIgnored} "
                + "refresh=" + vm.RefreshSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s "
                + $"milestones={milestonesVM.CurrentCreditedCount}/{milestonesVM.ProjectedCreditedCount}");

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

        private static FacilityAcc GetOrAddFacility(
            Dictionary<string, FacilityAcc> map, string facilityId)
        {
            FacilityAcc f;
            if (!map.TryGetValue(facilityId, out f))
            {
                f = new FacilityAcc();
                map[facilityId] = f;
            }
            return f;
        }

        /// <summary>
        /// Fills the destroyed half of both facility snapshots: NOW is the live set,
        /// the timeline end is the live set with every future destruction / repair
        /// applied in order. A facility's change time is the moment it as a whole goes
        /// from intact to destroyed (or back), so a second building falling in an
        /// already-destroyed facility does not move it.
        /// </summary>
        private static void ApplyBuildingDestroyedState(
            Dictionary<string, FacilityAcc> current,
            Dictionary<string, FacilityAcc> terminal,
            ICollection<string> liveDestroyedBuildingIds,
            List<GameAction> futureBuildingChanges)
        {
            if (liveDestroyedBuildingIds != null)
            {
                foreach (string building in liveDestroyedBuildingIds)
                {
                    if (string.IsNullOrEmpty(building)) continue;
                    string fid = FacilityDisplayNames.FacilityIdForBuilding(building);
                    GetOrAddFacility(current, fid).DestroyedBuildings.Add(building);
                    GetOrAddFacility(terminal, fid).DestroyedBuildings.Add(building);
                }
            }
            if (futureBuildingChanges == null) return;
            for (int i = 0; i < futureBuildingChanges.Count; i++)
            {
                GameAction a = futureBuildingChanges[i];
                string building = a.FacilityId ?? "";
                FacilityAcc f = GetOrAddFacility(terminal, FacilityDisplayNames.FacilityIdForBuilding(building));
                bool wasDestroyed = f.Destroyed;
                if (a.Type == GameActionType.FacilityDestruction)
                    f.DestroyedBuildings.Add(building);
                else
                    f.DestroyedBuildings.Remove(building);
                if (f.Destroyed != wasDestroyed)
                    f.DestroyedUT = a.UT;
            }
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

        private static FacilitiesTabVM CreateFacilitiesTabVM(
            Dictionary<string, FacilityAcc> facilityStateCurSnap,
            Dictionary<string, FacilityAcc> facilityStateTerm,
            bool facilitiesVisible)
        {
            var facilitiesVM = new FacilitiesTabVM { Rows = new List<FacilityRow>() };
            if (facilitiesVisible)
            {
                for (int i = 0; i < FACILITY_DISPLAY_ORDER.Count; i++)
                {
                    string fid = FACILITY_DISPLAY_ORDER[i];
                    FacilityAcc cur;
                    if (!facilityStateCurSnap.TryGetValue(fid, out cur))
                        cur = new FacilityAcc();
                    FacilityAcc term;
                    if (!facilityStateTerm.TryGetValue(fid, out term))
                        term = new FacilityAcc();

                    bool upcoming = (cur.Level != term.Level) || (cur.Destroyed != term.Destroyed);

                    facilitiesVM.Rows.Add(new FacilityRow
                    {
                        FacilityId = fid,
                        DisplayTitle = FacilityDisplayNames.ResolveFacilityDisplayName(fid),
                        CurrentLevel = cur.Level,
                        CurrentDestroyed = cur.Destroyed,
                        ProjectedLevel = term.Level,
                        ProjectedDestroyed = term.Destroyed,
                        HasUpcomingChange = upcoming,
                        LevelChangeUT = term.LevelUT,
                        DestroyedChangeUT = term.DestroyedUT
                    });
                }
            }
            return facilitiesVM;
        }

        private static MilestonesTabVM CreateMilestonesTabVM(
            List<MilestoneRow> allMilestoneRows,
            HashSet<string> creditedMilestonesCurSnap,
            HashSet<string> creditedMilestonesTerm,
            double liveUT,
            bool milestonesVisible)
        {
            var milestonesVM = new MilestonesTabVM
            {
                Rows = new List<MilestoneRow>()
            };
            if (milestonesVisible)
            {
                for (int i = 0; i < allMilestoneRows.Count; i++)
                {
                    var row = allMilestoneRows[i];
                    row.IsPendingCredit = row.CreditedUT > liveUT;
                    milestonesVM.Rows.Add(row);
                }
                milestonesVM.Rows.Sort(CompareMilestoneRowByUT);
                milestonesVM.CurrentCreditedCount = creditedMilestonesCurSnap.Count;
                milestonesVM.ProjectedCreditedCount = creditedMilestonesTerm.Count;
            }
            else
            {
                milestonesVM.CurrentCreditedCount = 0;
                milestonesVM.ProjectedCreditedCount = 0;
            }
            return milestonesVM;
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
                    ProjectedMaxSlots = LedgerOrchestrator.GetContractSlots(1)
                },
                Strategies = new StrategiesTabVM
                {
                    CurrentRows = new List<StrategyRow>(),
                    ProjectedRows = new List<StrategyRow>(),
                    PendingRows = new List<StrategyRow>(),
                    AdminLevel = 1,
                    ProjectedAdminLevel = 1,
                    CurrentMaxSlots = LedgerOrchestrator.GetStrategySlots(1),
                    ProjectedMaxSlots = LedgerOrchestrator.GetStrategySlots(1)
                },
                Facilities = new FacilitiesTabVM { Rows = new List<FacilityRow>() },
                Milestones = new MilestonesTabVM { Rows = new List<MilestoneRow>() },
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

            bool hasVisibleTimelineState = ModeHasVisibleTimelineState(currentMode);

            // Science Sandbox still has time-sensitive facility/milestone rows even
            // without the Career-only UT banner. Sandbox shows neither, so it only
            // rebuilds on explicit invalidation, mode changes, or transient fallback.
            if (hasVisibleTimelineState && liveUT < vm.LiveUT)
                return true;

            // The banner's date and the deadline tails are minute-resolution text, so a
            // rebuild per game second only re-formatted identical strings. A deadline
            // within a couple of minutes of now drops the cadence to one second
            // (RefreshSeconds), where its tail reads in seconds. Science mode has no live
            // date to show but still re-reads stock's destroyed buildings on the same
            // minute cadence, so a building destroyed while the window is open appears.
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
                    return ModeShowsContracts(mode);

                case GameActionType.StrategyActivate:
                case GameActionType.StrategyDeactivate:
                    return ModeShowsStrategies(mode);

                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityDestruction:
                case GameActionType.FacilityRepair:
                    return ModeShowsFacilities(mode);

                case GameActionType.MilestoneAchievement:
                    return ModeShowsMilestones(mode);

                default:
                    return false;
            }
        }

        private static bool ModeShowsContracts(Game.Modes mode)
        {
            return mode == Game.Modes.CAREER;
        }

        private static bool ModeShowsStrategies(Game.Modes mode)
        {
            return mode == Game.Modes.CAREER;
        }

        private static bool ModeShowsFacilities(Game.Modes mode)
        {
            return ModeHasVisibleTimelineState(mode);
        }

        private static bool ModeShowsMilestones(Game.Modes mode)
        {
            return ModeHasVisibleTimelineState(mode);
        }

        private static bool ModeHasVisibleTimelineState(Game.Modes mode)
        {
            return mode == Game.Modes.CAREER || mode == Game.Modes.SCIENCE_SANDBOX;
        }

        private static Dictionary<string, ContractAcc> CopyContracts(
            Dictionary<string, ContractAcc> src)
        {
            var dst = new Dictionary<string, ContractAcc>(src.Count, StringComparer.Ordinal);
            foreach (var kvp in src) dst[kvp.Key] = kvp.Value;
            return dst;
        }

        private static Dictionary<string, StrategyAcc> CopyStrategies(
            Dictionary<string, StrategyAcc> src)
        {
            var dst = new Dictionary<string, StrategyAcc>(src.Count, StringComparer.Ordinal);
            foreach (var kvp in src) dst[kvp.Key] = kvp.Value;
            return dst;
        }

        private static Dictionary<string, FacilityAcc> CopyFacilities(
            Dictionary<string, FacilityAcc> src)
        {
            var dst = new Dictionary<string, FacilityAcc>(src.Count, StringComparer.Ordinal);
            foreach (var kvp in src)
            {
                var copy = new FacilityAcc
                {
                    Level = kvp.Value.Level,
                    LevelUT = kvp.Value.LevelUT,
                    DestroyedUT = kvp.Value.DestroyedUT
                };
                copy.DestroyedBuildings.UnionWith(kvp.Value.DestroyedBuildings);
                dst[kvp.Key] = copy;
            }
            return dst;
        }

        private static bool AnyFacilityUpcomingChange(List<FacilityRow> rows)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].HasUpcomingChange) return true;
            return false;
        }

        private static bool AnyRowEnds(List<ContractRow> rows)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].EndKind != TimelineEndKind.None) return true;
            return false;
        }

        private static bool AnyRowEnds(List<StrategyRow> rows)
        {
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

        private static int CompareMilestoneRowByUT(MilestoneRow a, MilestoneRow b)
        {
            int c = a.CreditedUT.CompareTo(b.CreditedUT);
            if (c != 0) return c;
            return StringComparer.Ordinal.Compare(a.MilestoneId, b.MilestoneId);
        }

        private static void LogSkip(string actionType, string reason, GameAction a)
        {
            // Rate-limited across Build() calls via ParsekLog.VerboseRateLimited
            // (design doc §7). Keyed on type+reason so each skip category throttles
            // independently; repeated walks of the same ledger do not spam the log.
            var ic = CultureInfo.InvariantCulture;
            string key = "CareerStateWindow.skip." + actionType + "." + reason;
            ParsekLog.VerboseRateLimited("UI", key,
                $"CareerStateWindow: action skipped actionType={actionType} " +
                $"ut={a.UT.ToString("F0", ic)} reason={reason}");
        }

        // ================================================================
        // Title resolution (design doc §4.4)
        // ================================================================

        private static string ResolveContractTitle(GameAction a, string contractId)
        {
            // Preference 1: action.ContractTitle set at Accept.
            if (!string.IsNullOrEmpty(a.ContractTitle)) return a.ContractTitle;

            // Preference 2: live ContractSystem.Instance lookup (wrapped so tests
            // can inject a throwing delegate to exercise the catch branch — E13).
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

            // Preference 3: raw id fallback — rate-limited per id so mod-generated or
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

        private static string ResolveStrategyTitle(string strategyId)
        {
            try
            {
                string fromLive = LookupStrategyTitleLive(strategyId);
                if (!string.IsNullOrEmpty(fromLive)) return fromLive;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("UI",
                    "CareerStateWindow.strategyTitleThrew." + strategyId,
                    $"CareerStateWindow: strategy title lookup threw id={strategyId} ex={ex.GetType().Name}");
            }

            // Rate-limited per id so a career with a mod-generated or retired strategy
            // does not re-log the fallback on every ledger invalidation.
            ParsekLog.VerboseRateLimited("UI",
                "CareerStateWindow.strategyTitleFallback." + strategyId,
                $"CareerStateWindow: strategy title fallback id={strategyId}");
            return strategyId;
        }

        private static string LookupStrategyTitleLive(string strategyId)
        {
            var lookup = StrategyTitleLookupForTesting;
            if (lookup != null) return lookup(strategyId);

            // Production path: read live StrategySystem. Guarded with null-checks
            // because pre-scene-load (or Sandbox) Instance will be null.
            var system = Strategies.StrategySystem.Instance;
            if (system == null) return null;
            var list = system.Strategies;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null || s.Config == null) continue;
                if (s.Config.Name == strategyId) return s.Config.Title;
            }
            return null;
        }

        // ================================================================
        // SpaceBeforeCapitals — humanization helper (design doc §4.4)
        // ================================================================

        /// <summary>
        /// Inserts a space before a capital letter unless the previous character is
        /// also uppercase. Preserves acronyms like "VAB" (stays "VAB") while
        /// expanding camel/pascal-cased names like "FirstMunFlyby" → "First Mun Flyby".
        /// Returns the input unchanged if null or empty.
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
                    // (keeps runs of caps together — "VAB", "SPH", "RCS"). We also
                    // keep the space when the previous char is a lowercase letter or
                    // digit; the "prev not uppercase" rule captures both.
                    if (!char.IsUpper(prev)) sb.Append(' ');
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// A milestone id as the player reads it. Delegates to the Timeline's own
        /// humanizer so the two windows name the same achievement the same way:
        /// <c>Kerbin/Science</c> -> <c>Kerbin - Science</c>, <c>FirstLaunch</c> ->
        /// <c>First Launch</c>. (SpaceBeforeCapitals alone produced <c>Kerbin/ Science</c>,
        /// because the slash is not uppercase.)
        /// </summary>
        internal static string HumanizeMilestoneTitle(string milestoneId)
        {
            if (string.IsNullOrEmpty(milestoneId)) return milestoneId;
            return TimelineEntryDisplay.HumanizeMilestoneId(milestoneId);
        }

        /// <summary>
        /// Test seam for the live destroyed-building read. When non-null,
        /// <see cref="ReadLiveDestroyedBuildingIds"/> returns its answer instead of stock's.
        /// </summary>
        internal static Func<ICollection<string>> LiveDestroyedBuildingsForTesting;

        /// <summary>
        /// The KSC buildings destroyed right now, by DestructibleBuilding id
        /// (<c>SpaceCenter/LaunchPad/Facility/...</c>), from stock's own record:
        /// <c>ScenarioDestructibles.protoDestructibles</c>. That table is filled from the
        /// save in every scene the scenario runs in (Space Center, Flight, Editor,
        /// Tracking Station), so it answers even where the buildings are not loaded; an
        /// entry with a live building reads the building, the others read the persisted
        /// <c>intact</c> value. Read once per view-model rebuild, never per frame.
        /// Null when stock cannot answer (headless, or the scenario absent).
        /// </summary>
        internal static ICollection<string> ReadLiveDestroyedBuildingIds()
        {
            var seam = LiveDestroyedBuildingsForTesting;
            if (seam != null) return seam();
            try
            {
                return ReadLiveDestroyedBuildingIdsCore();
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("UI",
                    "CareerStateWindow.liveDestroyedThrew",
                    $"CareerStateWindow: live destroyed-building read threw ex={ex.GetType().Name}; treating every building as intact");
                return null;
            }
        }

        // NoInlining for the same reason as LookupStockFacilityNameCore: mono resolves a
        // KSP type's failing initializer when it JITs the calling method.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ICollection<string> ReadLiveDestroyedBuildingIdsCore()
        {
            var table = ScenarioDestructibles.protoDestructibles;
            if (table == null) return null;
            var destroyed = new List<string>();
            int live = 0, persisted = 0;
            foreach (var kvp in table)
            {
                ScenarioDestructibles.ProtoDestructible proto = kvp.Value;
                if (proto == null) continue;
                bool? intact = null;
                if (proto.dBuildingRefs != null && proto.dBuildingRefs.Count > 0
                    && proto.dBuildingRefs[0] != null)
                {
                    intact = proto.dBuildingRefs[0].IsIntact;
                    live++;
                }
                else if (proto.configNode != null && proto.configNode.HasValue("intact"))
                {
                    bool parsed;
                    if (bool.TryParse(proto.configNode.GetValue("intact"), out parsed))
                        intact = parsed;
                    persisted++;
                }
                if (intact == false)
                    destroyed.Add(kvp.Key);
            }
            ParsekLog.Verbose("UI",
                $"CareerStateWindow: live destroyed-building read buildings={table.Count} live={live} persisted={persisted} destroyed={destroyed.Count}");
            return destroyed;
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

        // Logical tab indices. Stable across game modes: they are what the seam's
        // `UiAction op=tab window=career` vocabulary and SelectedTabForTesting speak, and
        // a mode that hides a tab simply never draws its button (VisibleTabsFor).
        internal const int TabContracts = 0;
        internal const int TabStrategies = 1;
        internal const int TabFacilities = 2;
        internal const int TabMilestones = 3;

        private static readonly int[] CareerTabs =
            { TabContracts, TabStrategies, TabFacilities, TabMilestones };
        private static readonly int[] ScienceTabsWithFacilities = { TabFacilities, TabMilestones };
        private static readonly int[] ScienceTabs = { TabMilestones };
        private static readonly int[] NoTabs = new int[0];

        // Toolbar label subsets, keyed by the (static, shared) visible-tab array itself.
        private static readonly Dictionary<int[], GUIContent[]> tabLabelSubsets =
            new Dictionary<int[], GUIContent[]>();

        /// <summary>
        /// Test seam for the transient tab selection. Writable so the in-game
        /// `ModeRoundTripPreservesWindowState` check can set a distinguishable bit of state
        /// and assert the design-7.2 auto-close preserved it (edge case 1: nothing is
        /// destroyed, only hidden).
        ///
        /// <para>A write is coerced onto a tab the current mode actually draws (read from
        /// the cached view model), so the automation seam's read-back reports
        /// <c>tab-not-applied</c> for a Science-mode `tab=contracts` instead of a capture
        /// labelled Contracts that shows Milestones. With no cached view model yet the value
        /// is stored as written and the draw coerces it.</para>
        /// </summary>
        internal int SelectedTabForTesting
        {
            get { return selectedTab; }
            set
            {
                selectedTab = cachedVM.HasValue
                    ? CoerceTab(value, VisibleTabsFor(cachedVM.Value.Mode, cachedVM.Value.Facilities))
                    : value;
            }
        }

        /// <summary>Number of tabs this window draws (test seam for the round-trip check).</summary>
        internal static int TabCountForTesting => TabLabels.Length;

        /// <summary>
        /// The tabs a game mode draws, in order.
        /// <list type="bullet">
        /// <item>Career: all four.</item>
        /// <item>Science: Milestones, plus Facilities only while a building is destroyed now
        /// or at the timeline end. Stock registers no facility-upgrade scenario in a Science
        /// save and treats every building as fully upgraded, so there are no levels to show;
        /// buildings CAN still be destroyed there (ScenarioDestructibles is registered for
        /// every mode and the default difficulty keeps IndestructibleFacilities off).
        /// Contracts and Strategies do not exist in Science mode.</item>
        /// <item>Sandbox and the mission modes: none - career state is not tracked, and the
        /// banner says so.</item>
        /// </list>
        /// </summary>
        internal static int[] VisibleTabsFor(Game.Modes mode, FacilitiesTabVM facilities)
        {
            if (mode == Game.Modes.CAREER) return CareerTabs;
            if (mode == Game.Modes.SCIENCE_SANDBOX)
                return AnyFacilityRowVisible(facilities.Rows, mode)
                    ? ScienceTabsWithFacilities
                    : ScienceTabs;
            return NoTabs;
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
        /// Whether the Career launcher on the main window is offered in this game mode.
        /// Sandbox and the mission modes track no career state, so every tab would be an
        /// empty sentence; the launcher is hidden there rather than opening a dead window.
        /// </summary>
        internal static bool ModeOffersLauncher(Game.Modes mode)
        {
            return ModeHasVisibleTimelineState(mode);
        }

        private static GUIContent[] TabLabelsFor(int[] visibleTabs)
        {
            GUIContent[] labels;
            if (tabLabelSubsets.TryGetValue(visibleTabs, out labels)) return labels;
            labels = new GUIContent[visibleTabs.Length];
            for (int i = 0; i < visibleTabs.Length; i++)
                labels[i] = TabLabels[visibleTabs[i]];
            tabLabelSubsets[visibleTabs] = labels;
            return labels;
        }

        internal void ReleaseInputLock()
        {
            if (!careerStateWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(CareerStateInputLockId);
            careerStateWindowHasInputLock = false;
        }

        private void EnsureStyles()
        {
            // Section + column header styles are shared across the mod via ParsekUI;
            // reassign every draw so any ParsekUI-level updates flow through. Every
            // section bar in this window labels a table whose column-header row and
            // body box carry the shared zero horizontal inset
            // (ParsekUI.TableRowHorizontalInsetPx), so the bar uses the table variant
            // and spans exactly the table below it.
            sectionHeaderStyle = parentUI.GetTableSectionHeaderStyle();
            columnHeaderStyle = parentUI.GetColumnHeaderStyle();
            if (toggleButtonStyle != null) return;
            groupHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
            // Toggle button "on" state reuses the button's own pressed texture so
            // expanded disclosure sections look pressed. Mirrors TimelineWindowUI
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
            // failure, a destroyed building). Rows that are merely in the future are not
            // coloured: they already sit under a "Pending in timeline" header, and a
            // second marker on every such row would bury the warnings.
            alertStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.95f, 0.78f, 0.45f) }
            };
            grayStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) }
            };
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
            // Breathing room below the title bar — matches Timeline's visual spacing.
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
                // ledger actions only (design §3.4 career-state UI).
                cachedVM = Build(
                    EffectiveState.ComputeELS(),
                    liveUT,
                    currentMode,
                    LedgerOrchestrator.Contracts,
                    LedgerOrchestrator.Strategies,
                    LedgerOrchestrator.Facilities,
                    LedgerOrchestrator.Milestones,
                    FormatDate,
                    ReadLiveDestroyedBuildingIds());
            }

            var vm = cachedVM.Value;

            // Mode banner (design doc §5.4).
            GUILayout.Label(vm.BannerText ?? FormatModeBanner(vm, FormatDate), bannerStyle);
            LogModeRender(vm.Mode, ref lastRenderedMode);

            // Tab bar, over the tabs this mode draws. Use the pressed-style button (see
            // toggleButtonStyle in EnsureStyles) so the selected tab is visibly pushed in -
            // matches the Timeline window's filter-toggle idiom. A mode with a single tab
            // draws no bar (a one-button selector selects nothing).
            int[] visibleTabs = VisibleTabsFor(vm.Mode, vm.Facilities);
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
                if (visibleTabs.Length > 1)
                {
                    int pos = Array.IndexOf(visibleTabs, selectedTab);
                    int newPos = GUILayout.Toolbar(pos, TabLabelsFor(visibleTabs), toggleButtonStyle);
                    if (newPos != pos && newPos >= 0 && newPos < visibleTabs.Length)
                    {
                        SwitchTab(selectedTab, visibleTabs[newPos]);
                        selectedTab = visibleTabs[newPos];
                        careerStateScrollPos.y = 0f;
                    }
                }
            }

            careerStateScrollPos = GUILayout.BeginScrollView(careerStateScrollPos, GUILayout.ExpandHeight(true));

            if (visibleTabs.Length > 0)
            {
                switch (selectedTab)
                {
                    case TabStrategies: DrawStrategiesTab(vm.Strategies, vm.LiveUT); break;
                    case TabFacilities: DrawFacilitiesTab(vm.Facilities, vm.Mode); break;
                    case TabMilestones: DrawMilestonesTab(vm.Milestones); break;
                    default: DrawContractsTab(vm.Contracts, vm.LiveUT); break;
                }
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
        /// frame, so formatting inside the draw re-ran the KSP date formatter and the
        /// reward builders on every one of them. Also sets
        /// <see cref="CareerStateViewModel.RefreshSeconds"/>.
        /// </summary>
        internal static void FillDisplayText(ref CareerStateViewModel vm,
                                             Func<double, string> formatDate)
        {
            var ic = CultureInfo.InvariantCulture;
            bool secondResolution = false;

            var c = vm.Contracts;
            c.HeaderText = "Mission Control L" + c.MissionControlLevel.ToString(ic)
                + " - slots " + c.CurrentActive.ToString(ic) + "/" + c.CurrentMaxSlots.ToString(ic)
                + " now, " + c.ProjectedActive.ToString(ic) + "/" + c.ProjectedMaxSlots.ToString(ic)
                + " at timeline end";
            secondResolution |= FillContractText(c.CurrentRows, vm.LiveUT, formatDate);
            secondResolution |= FillContractText(c.ProjectedRows, vm.LiveUT, formatDate);
            secondResolution |= FillContractText(c.PendingRows, vm.LiveUT, formatDate);
            vm.Contracts = c;

            var st = vm.Strategies;
            st.HeaderText = "Administration L" + st.AdminLevel.ToString(ic)
                + " - slots " + st.CurrentActive.ToString(ic) + "/" + st.CurrentMaxSlots.ToString(ic)
                + " now, " + st.ProjectedActive.ToString(ic) + "/" + st.ProjectedMaxSlots.ToString(ic)
                + " at timeline end";
            FillStrategyText(st.CurrentRows, formatDate);
            FillStrategyText(st.ProjectedRows, formatDate);
            FillStrategyText(st.PendingRows, formatDate);
            vm.Strategies = st;

            bool showLevels = vm.Mode == Game.Modes.CAREER;
            List<FacilityRow> facilityRows = vm.Facilities.Rows;
            if (facilityRows != null)
            {
                for (int i = 0; i < facilityRows.Count; i++)
                {
                    FacilityRow r = facilityRows[i];
                    r.LevelText = showLevels ? FormatFacilityRow_Level(r) : FormatFacilityRow_State(r);
                    r.TimelineEndText = FormatFacilityRow_TimelineEnd(r, showLevels, formatDate);
                    facilityRows[i] = r;
                }
            }

            var m = vm.Milestones;
            m.HeaderText = "Milestones (" + m.CurrentCreditedCount.ToString(ic) + " credited / "
                + m.ProjectedCreditedCount.ToString(ic) + " at timeline end)";
            if (m.Rows != null)
            {
                for (int i = 0; i < m.Rows.Count; i++)
                {
                    MilestoneRow r = m.Rows[i];
                    r.CreditedText = FormatMilestoneRow_UT(r, formatDate);
                    r.RewardsText = FormatMilestoneRow_Rewards(r);
                    m.Rows[i] = r;
                }
            }
            vm.Milestones = m;

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
        /// recorded timeline still changes something, the date it ends on; Science and
        /// Sandbox say what the mode leaves out.
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
                return "Science mode - no contracts, strategies or building levels";
            // SANDBOX, MISSION_BUILDER, MISSION: all treated as sandbox-equivalent.
            return "Sandbox mode - career state is not tracked";
        }

        // ================================================================
        // Per-tab renderers
        // ================================================================

        private const string TimelineEndTooltip =
            "What the recorded timeline does to this row before it ends.";

        private void DrawContractsTab(ContractsTabVM tab, double liveUT)
        {
            var ic = CultureInfo.InvariantCulture;
            GUILayout.Label(tab.HeaderText ?? "", sectionHeaderStyle);

            bool split = tab.PendingRows.Count > 0;
            bool showEnd = AnyRowEnds(tab.CurrentRows) || AnyRowEnds(tab.PendingRows);

            GUILayout.Label(
                (split ? "Active now (" : "Active (") + tab.CurrentRows.Count.ToString(ic) + ")",
                groupHeaderStyle);
            DrawContractsColumnHeader(showEnd);
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            if (tab.CurrentRows.Count == 0)
                GUILayout.Label("  (no active contracts)", grayStyle);
            for (int i = 0; i < tab.CurrentRows.Count; i++)
                DrawContractRow(tab.CurrentRows[i], liveUT, showEnd);
            GUILayout.EndVertical();

            if (!split) return;

            GUILayout.Space(3);
            if (!DrawPendingToggle(GroupKey_ContractsPending, tab.PendingRows.Count))
                return;
            DrawContractsColumnHeader(showEnd);
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            for (int i = 0; i < tab.PendingRows.Count; i++)
                DrawContractRow(tab.PendingRows[i], liveUT, showEnd);
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Draws one "Pending in timeline (n)" fold toggle and returns whether its group is
        /// expanded. The three pending groups (Contracts, Strategies, Milestones) share it.
        /// </summary>
        private bool DrawPendingToggle(string foldKey, int count)
        {
            bool expanded = !foldedGroups.Contains(foldKey);
            string headerText = "Pending in timeline ("
                + count.ToString(CultureInfo.InvariantCulture) + ")";
            bool newExpanded = GUILayout.Toggle(expanded,
                new GUIContent(headerText,
                    "Rows your recorded flights still have to deliver; click to fold."),
                toggleButtonStyle,
                GUILayout.ExpandWidth(true));
            if (newExpanded != expanded)
                ToggleSection(foldedGroups, foldKey);
            return newExpanded;
        }

        // All four tables in this window (Contracts / Strategies / Facilities /
        // Milestones) open BOTH their column-header row and every body row with
        // parentUI.GetTableRowStyle(), and wrap the body in
        // parentUI.GetTableBodyBoxStyle(). That is what keeps each cell under its own
        // header: this window's header rows and body rows live in DIFFERENT parents
        // (the header directly in the window scroll view, the rows inside the body
        // box), and with plain BeginHorizontal() the box's own margin put every cell
        // 4px right of its header. The header is INSIDE the same scroll view as the
        // body, so it must NOT reserve a scrollbar gutter - it shrinks with the body
        // already. Contract: ParsekUI.TableRowHorizontalInsetPx.
        //
        // The FIRST (name) column of every table expands in header and rows alike; the
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
            GUILayout.Label(FormatContractRow_Title(r), nameCellStyle,
                GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth));
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

        private void DrawStrategiesTab(StrategiesTabVM tab, double liveUT)
        {
            var ic = CultureInfo.InvariantCulture;
            GUILayout.Label(tab.HeaderText ?? "", sectionHeaderStyle);

            bool split = tab.PendingRows.Count > 0;
            bool showEnd = AnyRowEnds(tab.CurrentRows) || AnyRowEnds(tab.PendingRows);

            GUILayout.Label(
                (split ? "Active now (" : "Active (") + tab.CurrentRows.Count.ToString(ic) + ")",
                groupHeaderStyle);
            DrawStrategiesColumnHeader(showEnd);
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            if (tab.CurrentRows.Count == 0)
                GUILayout.Label("  (no active strategies)", grayStyle);
            for (int i = 0; i < tab.CurrentRows.Count; i++)
                DrawStrategyRow(tab.CurrentRows[i], showEnd);
            GUILayout.EndVertical();

            if (!split) return;

            GUILayout.Space(3);
            if (!DrawPendingToggle(GroupKey_StrategiesPending, tab.PendingRows.Count))
                return;
            DrawStrategiesColumnHeader(showEnd);
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            for (int i = 0; i < tab.PendingRows.Count; i++)
                DrawStrategyRow(tab.PendingRows[i], showEnd);
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
            GUILayout.Label(FormatStrategyRow_Title(r), nameCellStyle,
                GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth));
            GUILayout.Label(r.ActivateText ?? "", GUI.skin.label,
                GUILayout.Width(ColW_Date));
            GUILayout.Label(r.FlowText ?? "", GUI.skin.label, GUILayout.Width(ColW_Flow));
            if (showEnd)
                GUILayout.Label(r.TimelineEndText ?? "", GUI.skin.label,
                    GUILayout.Width(ColW_TimelineEnd));
            GUILayout.EndHorizontal();
        }

        private void DrawFacilitiesTab(FacilitiesTabVM tab, Game.Modes mode)
        {
            // No section bar: it only repeated the tab name. Career lists all nine
            // buildings with their level; Science lists only the destroyed ones (the tab
            // is not drawn at all while none is), with no level column.
            bool showLevels = mode == Game.Modes.CAREER;
            bool showEnd = false;
            for (int i = 0; i < tab.Rows.Count; i++)
            {
                if (FacilityRowVisible(tab.Rows[i], mode)
                    && FacilityRowChanges(tab.Rows[i], showLevels))
                {
                    showEnd = true;
                    break;
                }
            }

            DrawFacilitiesColumnHeader(showLevels, showEnd);
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            int drawn = 0;
            for (int i = 0; i < tab.Rows.Count; i++)
            {
                if (!FacilityRowVisible(tab.Rows[i], mode)) continue;
                DrawFacilityRow(tab.Rows[i], showLevels, showEnd);
                drawn++;
            }
            if (drawn == 0)
                GUILayout.Label("  (no facility data)", grayStyle);
            GUILayout.EndVertical();
        }

        private void DrawFacilitiesColumnHeader(bool showLevels, bool showEnd)
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label("Facility", columnHeaderStyle,
                GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth));
            GUILayout.Label(
                showLevels
                    ? new GUIContent("Level",
                        "Upgrade level now, and whether the building is destroyed.")
                    : new GUIContent("State", "Whether the building is destroyed now."),
                columnHeaderStyle, GUILayout.Width(ColW_Level));
            if (showEnd)
                GUILayout.Label(new GUIContent("Timeline end", TimelineEndTooltip),
                    columnHeaderStyle, GUILayout.Width(ColW_TimelineEnd));
            GUILayout.EndHorizontal();
        }

        private void DrawFacilityRow(FacilityRow r, bool showLevels, bool showEnd)
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label(FormatFacilityRow_Title(r), nameCellStyle,
                GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth));
            GUILayout.Label(r.LevelText ?? "",
                r.CurrentDestroyed ? alertStyle : GUI.skin.label,
                GUILayout.Width(ColW_Level));
            if (showEnd)
                GUILayout.Label(r.TimelineEndText ?? "",
                    r.ProjectedDestroyed && !r.CurrentDestroyed ? alertStyle : GUI.skin.label,
                    GUILayout.Width(ColW_TimelineEnd));
            GUILayout.EndHorizontal();
        }

        private void DrawMilestonesTab(MilestonesTabVM tab)
        {
            var ic = CultureInfo.InvariantCulture;
            GUILayout.Label(tab.HeaderText ?? "", sectionHeaderStyle);

            int pending = CountPendingMilestones(tab.Rows);
            bool split = pending > 0;
            int credited = tab.Rows.Count - pending;

            GUILayout.Label(
                (split ? "Credited now (" : "Credited (") + credited.ToString(ic) + ")",
                groupHeaderStyle);
            DrawMilestonesColumnHeader();
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            if (credited == 0)
                GUILayout.Label("  (no milestones credited)", grayStyle);
            for (int i = 0; i < tab.Rows.Count; i++)
                if (!tab.Rows[i].IsPendingCredit)
                    DrawMilestoneRow(tab.Rows[i]);
            GUILayout.EndVertical();

            if (!split) return;

            GUILayout.Space(3);
            if (!DrawPendingToggle(GroupKey_MilestonesPending, pending))
                return;
            DrawMilestonesColumnHeader();
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());
            for (int i = 0; i < tab.Rows.Count; i++)
                if (tab.Rows[i].IsPendingCredit)
                    DrawMilestoneRow(tab.Rows[i]);
            GUILayout.EndVertical();
        }

        private void DrawMilestonesColumnHeader()
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label("Milestone", columnHeaderStyle,
                GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth));
            GUILayout.Label(
                new GUIContent("Credited", "When the milestone was reached."),
                columnHeaderStyle, GUILayout.Width(ColW_Date));
            GUILayout.Label(
                new GUIContent("Rewards",
                    "Funds, science and reputation this first-time achievement paid out."),
                columnHeaderStyle, GUILayout.Width(ColW_Rewards));
            GUILayout.EndHorizontal();
        }

        private void DrawMilestoneRow(MilestoneRow r)
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label(FormatMilestoneRow_Title(r), nameCellStyle,
                GUILayout.ExpandWidth(true), GUILayout.MinWidth(NameCellMinWidth));
            GUILayout.Label(r.CreditedText ?? "", GUI.skin.label,
                GUILayout.Width(ColW_Date));
            GUILayout.Label(r.RewardsText ?? "", GUI.skin.label,
                GUILayout.Width(ColW_Rewards));
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
                case TimelineEndKind.Cancelled: return "cancelled " + FormatDateCell(endUT, formatDate);
                case TimelineEndKind.Deactivated: return "deactivates " + FormatDateCell(endUT, formatDate);
                default: return "";
            }
        }

        /// <summary>Whether a Timeline-end cell is drawn in the alert colour.</summary>
        internal static bool IsTimelineEndAlert(TimelineEndKind kind)
        {
            return kind == TimelineEndKind.Failed;
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

        // ---- Per-column facility helpers ----

        internal static string FormatFacilityRow_Title(FacilityRow r)
        {
            return string.IsNullOrEmpty(r.DisplayTitle) ? (r.FacilityId ?? "(unknown)") : r.DisplayTitle;
        }

        /// <summary>
        /// The Career Level cell: the level now, with <c>(destroyed)</c> when the
        /// building is down. What the timeline changes lives in the Timeline-end cell.
        /// </summary>
        internal static string FormatFacilityRow_Level(FacilityRow r)
        {
            string level = "L" + r.CurrentLevel.ToString(CultureInfo.InvariantCulture);
            return r.CurrentDestroyed ? level + " (destroyed)" : level;
        }

        /// <summary>The Science-mode State cell (no levels exist there).</summary>
        internal static string FormatFacilityRow_State(FacilityRow r)
        {
            return r.CurrentDestroyed ? "destroyed" : "intact";
        }

        /// <summary>
        /// Whether the recorded timeline changes this facility before it ends. Level
        /// changes count only where levels are shown (Career).
        /// </summary>
        internal static bool FacilityRowChanges(FacilityRow r, bool showLevels)
        {
            return (showLevels && r.CurrentLevel != r.ProjectedLevel)
                || r.CurrentDestroyed != r.ProjectedDestroyed;
        }

        /// <summary>
        /// The facility Timeline-end text: <c>upgrades to L2, Y1, D40</c>,
        /// <c>destroyed Y1, D40</c>, <c>repaired Y1, D40</c>, joined with "; " when both
        /// halves change, empty when nothing does.
        /// </summary>
        internal static string FormatFacilityRow_TimelineEnd(FacilityRow r, bool showLevels,
                                                             Func<double, string> formatDate)
        {
            string text = "";
            if (showLevels && r.CurrentLevel != r.ProjectedLevel)
            {
                text = (r.ProjectedLevel > r.CurrentLevel ? "upgrades to L" : "downgrades to L")
                    + r.ProjectedLevel.ToString(CultureInfo.InvariantCulture)
                    + ", " + FormatDateCell(r.LevelChangeUT, formatDate);
            }
            if (r.CurrentDestroyed != r.ProjectedDestroyed)
            {
                string destroyed = (r.ProjectedDestroyed ? "destroyed " : "repaired ")
                    + FormatDateCell(r.DestroyedChangeUT, formatDate);
                text = text.Length == 0 ? destroyed : text + "; " + destroyed;
            }
            return text;
        }

        /// <summary>
        /// Whether a facility row is listed in this mode: every building in Career, only a
        /// building destroyed now or at the timeline end in Science, none elsewhere.
        /// </summary>
        internal static bool FacilityRowVisible(FacilityRow r, Game.Modes mode)
        {
            if (mode == Game.Modes.CAREER) return true;
            if (mode == Game.Modes.SCIENCE_SANDBOX) return r.CurrentDestroyed || r.ProjectedDestroyed;
            return false;
        }

        private static bool AnyFacilityRowVisible(List<FacilityRow> rows, Game.Modes mode)
        {
            if (rows == null) return false;
            for (int i = 0; i < rows.Count; i++)
                if (FacilityRowVisible(rows[i], mode)) return true;
            return false;
        }

        // ---- Per-column milestone helpers ----

        internal static string FormatMilestoneRow_UT(MilestoneRow r, Func<double, string> formatDate)
        {
            return FormatDateCell(r.CreditedUT, formatDate);
        }

        internal static string FormatMilestoneRow_Title(MilestoneRow r)
        {
            return string.IsNullOrEmpty(r.DisplayTitle) ? (r.MilestoneId ?? "(unknown)") : r.DisplayTitle;
        }

        /// <summary>
        /// Returns a compact rewards string; zero-reward entries are elided
        /// (design doc E8). Empty string when no rewards are awarded.
        /// </summary>
        internal static string FormatMilestoneRow_Rewards(MilestoneRow r)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            if (r.FundsAwarded != 0f)
                sb.Append("+ ").Append(r.FundsAwarded.ToString("F0", ic)).Append(" funds");
            if (r.RepAwarded != 0f)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append("+ ").Append(r.RepAwarded.ToString("F0", ic)).Append(" rep");
            }
            if (r.ScienceAwarded != 0f)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append("+ ").Append(r.ScienceAwarded.ToString("F1", ic)).Append(" sci");
            }
            return sb.ToString();
        }

        internal static int CountPendingMilestones(List<MilestoneRow> rows)
        {
            if (rows == null) return 0;
            int n = 0;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].IsPendingCredit) n++;
            return n;
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
        /// <para>Three, and the window has no others: the Facilities tab has no fold, and
        /// the only other UI state here is the tab index. Named as a list so
        /// the seam's <c>key=all</c> / <c>key=none</c> form has something to enumerate
        /// instead of a copy of the two constants; the list is BUILT from those constants,
        /// so it cannot drift from them.</para>
        /// </summary>
        internal static readonly string[] FoldGroupKeys = new[]
        {
            GroupKey_ContractsPending, GroupKey_StrategiesPending, GroupKey_MilestonesPending,
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
                ParsekLog.Verbose("UI", "CareerStateWindow: rendered science-mode (contracts/strategies hidden)");
            }
            // CAREER: no log (it's the default "normal" render).
        }
    }
}
