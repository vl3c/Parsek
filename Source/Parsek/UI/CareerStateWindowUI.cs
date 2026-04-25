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
    /// Career State window — surfaces the four career modules (Contracts, Strategies,
    /// Facilities, Milestones) in a tabbed window with current-vs-projected columns.
    ///
    /// Phase 1 (this file in its current form): view-model + Build() walk + humanization
    /// helpers only. No DrawIfOpen yet; Phase 2 adds the window chrome.
    ///
    /// Design doc: docs/dev/plans/career-state-window.md (#416).
    /// </summary>
    internal class CareerStateWindowUI
    {
        private readonly ParsekUI parentUI;

        // --- Phase 2 window-chrome state ---
        private bool showCareerStateWindow;
        private Rect careerStateWindowRect;
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
        private GUIStyle pendingStyle;
        private GUIStyle grayStyle;
        private GUIStyle bannerStyle;

        internal const float DefaultWindowWidth = 820f;
        private const float DefaultWindowHeight = 400f;
        internal const float MinWindowWidth = 520f;
        private const float MinWindowHeight = 200f;
        private const string CareerStateInputLockId = "Parsek_CareerStateWindow";

        // Column widths — shared between header and body for alignment (mirrors RecordingsTableUI:30-42).
        // Contracts tab.
        private const float ColW_ContractTitle = 240f;
        private const float ColW_AcceptUT = 90f;
        private const float ColW_DeadlineUT = 90f;
        private const float ColW_PendingTag = 70f;
        // Strategies tab.
        private const float ColW_StrategyTitle = 220f;
        private const float ColW_ActivateUT = 90f;
        private const float ColW_Flow = 140f;
        // Milestones tab.
        private const float ColW_MilestoneUT = 90f;
        private const float ColW_MilestoneTitle = 200f;
        private const float ColW_Rewards = 180f;
        // Facilities tab.
        private const float ColW_FacilityTitle = 200f;
        private const float ColW_Level = 120f;
        private const float ColW_Status = 180f;

        // Disclosure arrows (mirrors KerbalsWindowUI:34-35).

        // Transient fold state for Contracts/Strategies "Pending in timeline" section headers.
        // Default-unfolded means we only store names that are currently folded. Tab switches
        // do NOT clear this set — folds persist across the window's lifetime.
        internal readonly HashSet<string> foldedGroups = new HashSet<string>(StringComparer.Ordinal);

        // Group-name constants for foldedGroups keys. Kept as constants so tests and
        // production share the exact strings.
        internal const string GroupKey_ContractsPending = "Contracts.Pending";
        internal const string GroupKey_StrategiesPending = "Strategies.Pending";

        private static readonly string[] TabLabels = new[]
        {
            "Contracts", "Strategies", "Facilities", "Milestones"
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
            public List<ContractRow> ProjectedRows;
        }

        internal struct ContractRow
        {
            public string ContractId;
            public string DisplayTitle;
            public double AcceptUT;
            public double DeadlineUT;   // NaN if none
            public bool IsPendingAccept;
            // Active now but will complete / fail / cancel before the timeline's terminal UT
            // (only meaningful on CurrentRows entries). Surfaces as "(closing)" in the Status
            // column so the player sees that A will disappear, not just that pending-B will
            // appear.
            public bool IsClosingByTimelineEnd;
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
            // Active now but will deactivate before the timeline's terminal UT (only
            // meaningful on CurrentRows entries).
            public bool IsClosingByTimelineEnd;
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
        }

        internal struct MilestonesTabVM
        {
            public int CurrentCreditedCount;
            public int ProjectedCreditedCount;
            public List<MilestoneRow> Rows;
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

        private sealed class FacilityAcc
        {
            public int Level;
            public bool Destroyed;
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
        /// </summary>
        internal static CareerStateViewModel Build(
            IReadOnlyList<GameAction> actions,
            double liveUT,
            Game.Modes mode,
            ContractsModule contracts,
            StrategiesModule strategies,
            FacilitiesModule facilities,
            MilestonesModule milestones)
        {
            if (contracts == null || strategies == null || facilities == null
                || milestones == null || actions == null)
            {
                ParsekLog.Warn("UI",
                    "CareerStateWindow: Build called with null module or actions; returning empty VM");
                return EmptyVM(liveUT, mode, isTransientFallback: true);
            }

            // Terminal-state accumulators, walked forward through the action list.
            var activeContractsTerm = new Dictionary<string, ContractAcc>(StringComparer.Ordinal);
            var activeStrategiesTerm = new Dictionary<string, StrategyAcc>(StringComparer.Ordinal);
            var facilityStateTerm = new Dictionary<string, FacilityAcc>(StringComparer.Ordinal);
            var creditedMilestonesTerm = new HashSet<string>(StringComparer.Ordinal);
            var allMilestoneRows = new List<MilestoneRow>();

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
                            DeadlineUT = float.IsNaN(a.DeadlineUT) ? double.NaN : (double)a.DeadlineUT
                        };
                        activeContractsTerm[cid] = acc;
                        break;

                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        if (!a.Effective)
                        {
                            LogSkip(a.Type.ToString(), "Ineffective", a);
                            break;
                        }
                        activeContractsTerm.Remove(a.ContractId ?? "");
                        break;

                    case GameActionType.StrategyActivate:
                        if (!a.Effective)
                        {
                            LogSkip("StrategyActivate", "Ineffective", a);
                            break;
                        }
                        string sid = a.StrategyId ?? "";
                        activeStrategiesTerm[sid] = new StrategyAcc
                        {
                            StrategyId = sid,
                            Title = ResolveStrategyTitle(sid),
                            ActivateUT = a.UT,
                            SourceResource = a.SourceResource,
                            TargetResource = a.TargetResource,
                            Commitment = a.Commitment
                        };
                        break;

                    case GameActionType.StrategyDeactivate:
                        if (!a.Effective)
                        {
                            LogSkip("StrategyDeactivate", "Ineffective", a);
                            break;
                        }
                        activeStrategiesTerm.Remove(a.StrategyId ?? "");
                        break;

                    case GameActionType.FacilityUpgrade:
                        {
                            if (!a.Effective)
                            {
                                LogSkip("FacilityUpgrade", "Ineffective", a);
                                break;
                            }
                            string fid = a.FacilityId ?? "";
                            FacilityAcc f;
                            if (!facilityStateTerm.TryGetValue(fid, out f))
                                f = new FacilityAcc { Level = 1, Destroyed = false };
                            f.Level = a.ToLevel;
                            facilityStateTerm[fid] = f;
                        }
                        break;

                    case GameActionType.FacilityDestruction:
                        {
                            if (!a.Effective)
                            {
                                LogSkip("FacilityDestruction", "Ineffective", a);
                                break;
                            }
                            string fid = a.FacilityId ?? "";
                            FacilityAcc f;
                            if (!facilityStateTerm.TryGetValue(fid, out f))
                                f = new FacilityAcc { Level = 1, Destroyed = false };
                            f.Destroyed = true;
                            facilityStateTerm[fid] = f;
                        }
                        break;

                    case GameActionType.FacilityRepair:
                        {
                            if (!a.Effective)
                            {
                                LogSkip("FacilityRepair", "Ineffective", a);
                                break;
                            }
                            string fid = a.FacilityId ?? "";
                            FacilityAcc f;
                            if (!facilityStateTerm.TryGetValue(fid, out f))
                                f = new FacilityAcc { Level = 1, Destroyed = false };
                            f.Destroyed = false;
                            facilityStateTerm[fid] = f;
                        }
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
                                DisplayTitle = SpaceBeforeCapitals(mid),
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

            // --- Facility levels for slot math (matches LedgerOrchestrator.UpdateSlotLimitsFromFacilities:1440-1446) ---
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
                missionControlLevelCur,
                missionControlLevelTerm,
                liveUT,
                contractsVisible);
            var strategiesVM = CreateStrategiesTabVM(
                activeStrategiesCurSnap,
                activeStrategiesTerm,
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
                || strategiesVM.CurrentActive != strategiesVM.ProjectedActive
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

            ParsekLog.Verbose("UI",
                "CareerStateWindow: rebuilt VM "
                + $"liveUT={GetDisplayedUtText(liveUT)} "
                + $"terminalUT={GetDisplayedUtText(terminalUT)} "
                + $"divergence={divergence} "
                + $"mode={mode} "
                + $"contracts={contractsVM.CurrentActive}/{contractsVM.ProjectedActive} "
                + $"strategies={strategiesVM.CurrentActive}/{strategiesVM.ProjectedActive} "
                + $"facilities={facilitiesVM.Rows.Count} "
                + $"milestones={milestonesVM.CurrentCreditedCount}/{milestonesVM.ProjectedCreditedCount}");

            return vm;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static ContractsTabVM CreateContractsTabVM(
            Dictionary<string, ContractAcc> activeContractsCurSnap,
            Dictionary<string, ContractAcc> activeContractsTerm,
            int missionControlLevelCur,
            int missionControlLevelTerm,
            double liveUT,
            bool contractsVisible)
        {
            var contractsVM = new ContractsTabVM
            {
                CurrentRows = new List<ContractRow>(),
                ProjectedRows = new List<ContractRow>(),
                MissionControlLevel = missionControlLevelCur,
                ProjectedMissionControlLevel = missionControlLevelTerm,
                CurrentMaxSlots = LedgerOrchestrator.GetContractSlots(missionControlLevelCur),
                ProjectedMaxSlots = LedgerOrchestrator.GetContractSlots(missionControlLevelTerm)
            };
            if (contractsVisible)
            {
                foreach (var kvp in activeContractsCurSnap)
                {
                    var c = kvp.Value;
                    // Active now but not in the terminal snapshot -> will complete/fail/cancel
                    // before timeline end. Surfaces as "(closing)" so the UI doesn't silently
                    // hide disappearing contracts (bug: without this, only pending-new rows
                    // show the delta, leaving contracts that wind down invisible).
                    bool closing = !activeContractsTerm.ContainsKey(c.ContractId);
                    contractsVM.CurrentRows.Add(new ContractRow
                    {
                        ContractId = c.ContractId,
                        DisplayTitle = c.Title,
                        AcceptUT = c.AcceptUT,
                        DeadlineUT = c.DeadlineUT,
                        IsPendingAccept = false,
                        IsClosingByTimelineEnd = closing
                    });
                }
                foreach (var kvp in activeContractsTerm)
                {
                    var c = kvp.Value;
                    bool pending = c.AcceptUT > liveUT;
                    contractsVM.ProjectedRows.Add(new ContractRow
                    {
                        ContractId = c.ContractId,
                        DisplayTitle = c.Title,
                        AcceptUT = c.AcceptUT,
                        DeadlineUT = c.DeadlineUT,
                        IsPendingAccept = pending
                    });
                }
                contractsVM.CurrentRows.Sort(CompareContractRowByAcceptUT);
                contractsVM.ProjectedRows.Sort(CompareContractRowByAcceptUT);
            }
            contractsVM.CurrentActive = contractsVM.CurrentRows.Count;
            contractsVM.ProjectedActive = contractsVM.ProjectedRows.Count;
            return contractsVM;
        }

        private static StrategiesTabVM CreateStrategiesTabVM(
            Dictionary<string, StrategyAcc> activeStrategiesCurSnap,
            Dictionary<string, StrategyAcc> activeStrategiesTerm,
            int adminLevelCur,
            int adminLevelTerm,
            double liveUT,
            bool strategiesVisible)
        {
            var strategiesVM = new StrategiesTabVM
            {
                CurrentRows = new List<StrategyRow>(),
                ProjectedRows = new List<StrategyRow>(),
                AdminLevel = adminLevelCur,
                ProjectedAdminLevel = adminLevelTerm,
                CurrentMaxSlots = LedgerOrchestrator.GetStrategySlots(adminLevelCur),
                ProjectedMaxSlots = LedgerOrchestrator.GetStrategySlots(adminLevelTerm)
            };
            if (strategiesVisible)
            {
                foreach (var kvp in activeStrategiesCurSnap)
                {
                    var s = kvp.Value;
                    // Active now but not in the terminal snapshot -> will deactivate by
                    // timeline end. Surfaces as "(closing)" in the Status column.
                    bool closing = !activeStrategiesTerm.ContainsKey(s.StrategyId);
                    strategiesVM.CurrentRows.Add(new StrategyRow
                    {
                        StrategyId = s.StrategyId,
                        DisplayTitle = s.Title,
                        ActivateUT = s.ActivateUT,
                        SourceResource = s.SourceResource,
                        TargetResource = s.TargetResource,
                        Commitment = s.Commitment,
                        IsPendingActivate = false,
                        IsClosingByTimelineEnd = closing
                    });
                }
                foreach (var kvp in activeStrategiesTerm)
                {
                    var s = kvp.Value;
                    bool pending = s.ActivateUT > liveUT;
                    strategiesVM.ProjectedRows.Add(new StrategyRow
                    {
                        StrategyId = s.StrategyId,
                        DisplayTitle = s.Title,
                        ActivateUT = s.ActivateUT,
                        SourceResource = s.SourceResource,
                        TargetResource = s.TargetResource,
                        Commitment = s.Commitment,
                        IsPendingActivate = pending
                    });
                }
                strategiesVM.CurrentRows.Sort(CompareStrategyRowByActivateUT);
                strategiesVM.ProjectedRows.Sort(CompareStrategyRowByActivateUT);
            }
            strategiesVM.CurrentActive = strategiesVM.CurrentRows.Count;
            strategiesVM.ProjectedActive = strategiesVM.ProjectedRows.Count;
            return strategiesVM;
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
                        cur = new FacilityAcc { Level = 1, Destroyed = false };
                    FacilityAcc term;
                    if (!facilityStateTerm.TryGetValue(fid, out term))
                        term = new FacilityAcc { Level = 1, Destroyed = false };

                    bool upcoming = (cur.Level != term.Level) || (cur.Destroyed != term.Destroyed);

                    facilitiesVM.Rows.Add(new FacilityRow
                    {
                        FacilityId = fid,
                        DisplayTitle = SpaceBeforeCapitals(fid),
                        CurrentLevel = cur.Level,
                        CurrentDestroyed = cur.Destroyed,
                        ProjectedLevel = term.Level,
                        ProjectedDestroyed = term.Destroyed,
                        HasUpcomingChange = upcoming
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
                    MissionControlLevel = 1,
                    ProjectedMissionControlLevel = 1,
                    CurrentMaxSlots = LedgerOrchestrator.GetContractSlots(1),
                    ProjectedMaxSlots = LedgerOrchestrator.GetContractSlots(1)
                },
                Strategies = new StrategiesTabVM
                {
                    CurrentRows = new List<StrategyRow>(),
                    ProjectedRows = new List<StrategyRow>(),
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
            if (cachedVM == null)
                return true;

            var vm = cachedVM.Value;
            if (vm.Mode != currentMode)
                return true;

            if (vm.IsTransientFallback)
                return true;

            bool displaysLiveUT = ModeDisplaysLiveUT(currentMode);
            bool hasVisibleTimelineState = ModeHasVisibleTimelineState(currentMode);

            // Science Sandbox still has time-sensitive facility/milestone rows even
            // without the Career-only UT banner. Sandbox shows neither, so it only
            // rebuilds on explicit invalidation, mode changes, or transient fallback.
            if (hasVisibleTimelineState && liveUT < vm.LiveUT)
                return true;

            if (displaysLiveUT && GetDisplayedUtText(vm.LiveUT) != GetDisplayedUtText(liveUT))
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

        private static bool ModeDisplaysLiveUT(Game.Modes mode)
        {
            return mode == Game.Modes.CAREER;
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
                dst[kvp.Key] = new FacilityAcc
                {
                    Level = kvp.Value.Level,
                    Destroyed = kvp.Value.Destroyed
                };
            }
            return dst;
        }

        private static bool AnyFacilityUpcomingChange(List<FacilityRow> rows)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].HasUpcomingChange) return true;
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
                    "ParsekCareerState".GetHashCode(),
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

        internal void ReleaseInputLock()
        {
            if (!careerStateWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(CareerStateInputLockId);
            careerStateWindowHasInputLock = false;
        }

        private void EnsureStyles()
        {
            // Section + column header styles are shared across the mod via ParsekUI;
            // reassign every draw so any ParsekUI-level updates flow through.
            sectionHeaderStyle = parentUI.GetSectionHeaderStyle();
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

            // Pending rows: muted amber to mark "not yet lived" entries.
            pendingStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.95f, 0.78f, 0.45f) }
            };
            grayStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) }
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
                    LedgerOrchestrator.Milestones);
            }

            var vm = cachedVM.Value;

            // Mode banner (design doc §5.4).
            DrawModeBanner(vm);
            LogModeRender(vm.Mode, ref lastRenderedMode);

            // Tab bar.
            // Use the pressed-style button (see toggleButtonStyle in EnsureStyles) so
            // the selected tab is visibly pushed in — matches the Timeline window's
            // filter-toggle idiom. Default Toolbar rendering is too subtle about which
            // tab is currently selected.
            int newTab = GUILayout.Toolbar(selectedTab, TabLabels, toggleButtonStyle);
            if (newTab != selectedTab)
            {
                SwitchTab(selectedTab, newTab);
                selectedTab = newTab;
                careerStateScrollPos.y = 0f;
            }

            careerStateScrollPos = GUILayout.BeginScrollView(careerStateScrollPos, GUILayout.ExpandHeight(true));

            switch (selectedTab)
            {
                case 0: DrawContractsTab(vm.Contracts, vm.Mode); break;
                case 1: DrawStrategiesTab(vm.Strategies, vm.Mode); break;
                case 2: DrawFacilitiesTab(vm.Facilities, vm.Mode); break;
                case 3: DrawMilestonesTab(vm.Milestones, vm.Mode); break;
                default: DrawContractsTab(vm.Contracts, vm.Mode); break;
            }

            GUILayout.EndScrollView();

            if (GUILayout.Button("Close"))
            {
                IsOpen = false;
            }

            ParsekUI.DrawResizeHandle(careerStateWindowRect, ref isResizingCareerStateWindow,
                "Career State window");

            GUI.DragWindow();
        }

        private void DrawModeBanner(CareerStateViewModel vm)
        {
            string line;
            if (vm.Mode == Game.Modes.CAREER)
            {
                line = $"Career mode - UT {GetDisplayedUtText(vm.LiveUT)}";
                if (vm.HasDivergence)
                {
                    line += $"  (timeline ends at UT {GetDisplayedUtText(vm.TerminalUT)})";
                }
            }
            else if (vm.Mode == Game.Modes.SCIENCE_SANDBOX)
            {
                line = "Science mode - contracts and strategies unavailable";
            }
            else
            {
                // SANDBOX, MISSION_BUILDER, MISSION: all treated as sandbox-equivalent.
                line = "Sandbox mode - career state is not tracked";
            }
            GUILayout.Label(line, bannerStyle);
        }

        // ================================================================
        // Per-tab renderers
        // ================================================================

        private void DrawContractsTab(ContractsTabVM tab, Game.Modes mode)
        {
            if (mode == Game.Modes.SANDBOX || mode == Game.Modes.MISSION_BUILDER || mode == Game.Modes.MISSION)
            {
                GUILayout.Label("Career state is not tracked in Sandbox mode.", grayStyle);
                return;
            }
            if (mode == Game.Modes.SCIENCE_SANDBOX)
            {
                GUILayout.Label("Contracts are unavailable in Science mode.", grayStyle);
                return;
            }

            var ic = CultureInfo.InvariantCulture;
            GUILayout.Label(
                $"Mission Control L{tab.MissionControlLevel.ToString(ic)} - slots {tab.CurrentActive.ToString(ic)}/{tab.CurrentMaxSlots.ToString(ic)} now, {tab.ProjectedActive.ToString(ic)}/{tab.ProjectedMaxSlots.ToString(ic)} at timeline end",
                sectionHeaderStyle);

            bool sameAsProjected = RowsEqual(tab.CurrentRows, tab.ProjectedRows);
            if (sameAsProjected)
            {
                GUILayout.Label($"Active ({tab.CurrentRows.Count.ToString(ic)})", groupHeaderStyle);
                DrawContractsColumnHeader();
                GUILayout.BeginVertical(GUI.skin.box);
                if (tab.CurrentRows.Count == 0)
                    GUILayout.Label("  (no active contracts)", grayStyle);
                for (int i = 0; i < tab.CurrentRows.Count; i++)
                    DrawContractRow(tab.CurrentRows[i], GUI.skin.label);
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.Label($"Active now ({tab.CurrentRows.Count.ToString(ic)})", groupHeaderStyle);
                DrawContractsColumnHeader();
                GUILayout.BeginVertical(GUI.skin.box);
                if (tab.CurrentRows.Count == 0)
                    GUILayout.Label("  (no active contracts)", grayStyle);
                for (int i = 0; i < tab.CurrentRows.Count; i++)
                    DrawContractRow(tab.CurrentRows[i], GUI.skin.label);
                GUILayout.EndVertical();

                // "Pending in timeline" is the projected rows minus the current rows.
                GUILayout.Space(3);
                int pendingCount = 0;
                for (int i = 0; i < tab.ProjectedRows.Count; i++)
                    if (tab.ProjectedRows[i].IsPendingAccept) pendingCount++;

                bool expanded = !foldedGroups.Contains(GroupKey_ContractsPending);
                string headerText = $"Pending in timeline ({pendingCount.ToString(ic)})";
                bool newExpanded = GUILayout.Toggle(expanded, headerText, toggleButtonStyle,
                    GUILayout.ExpandWidth(true));
                if (newExpanded != expanded)
                {
                    ToggleSection(foldedGroups, GroupKey_ContractsPending);
                }
                bool folded = !newExpanded;

                if (!folded)
                {
                    DrawContractsColumnHeader();
                    GUILayout.BeginVertical(GUI.skin.box);
                    if (pendingCount == 0)
                        GUILayout.Label("  (none)", grayStyle);
                    for (int i = 0; i < tab.ProjectedRows.Count; i++)
                    {
                        var r = tab.ProjectedRows[i];
                        if (!r.IsPendingAccept) continue;
                        DrawContractRow(r, pendingStyle);
                    }
                    GUILayout.EndVertical();
                }
            }
        }

        private void DrawContractsColumnHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Contract", columnHeaderStyle, GUILayout.Width(ColW_ContractTitle));
            GUILayout.Label("Accepted UT", columnHeaderStyle, GUILayout.Width(ColW_AcceptUT));
            GUILayout.Label("Deadline UT", columnHeaderStyle, GUILayout.Width(ColW_DeadlineUT));
            GUILayout.Label("Status", columnHeaderStyle, GUILayout.Width(ColW_PendingTag));
            GUILayout.EndHorizontal();
        }

        private void DrawContractRow(ContractRow r, GUIStyle rowStyle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(FormatContractRow_Title(r), rowStyle, GUILayout.Width(ColW_ContractTitle));
            GUILayout.Label(FormatContractRow_Accept(r), rowStyle, GUILayout.Width(ColW_AcceptUT));
            GUILayout.Label(FormatContractRow_Deadline(r), rowStyle, GUILayout.Width(ColW_DeadlineUT));
            GUILayout.Label(FormatContractRow_Pending(r), rowStyle, GUILayout.Width(ColW_PendingTag));
            GUILayout.EndHorizontal();
        }

        private void DrawStrategiesTab(StrategiesTabVM tab, Game.Modes mode)
        {
            if (mode == Game.Modes.SANDBOX || mode == Game.Modes.MISSION_BUILDER || mode == Game.Modes.MISSION)
            {
                GUILayout.Label("Career state is not tracked in Sandbox mode.", grayStyle);
                return;
            }
            if (mode == Game.Modes.SCIENCE_SANDBOX)
            {
                GUILayout.Label("Strategies are unavailable in Science mode.", grayStyle);
                return;
            }

            var ic = CultureInfo.InvariantCulture;
            GUILayout.Label(
                $"Administration L{tab.AdminLevel.ToString(ic)} - slots {tab.CurrentActive.ToString(ic)}/{tab.CurrentMaxSlots.ToString(ic)} now, {tab.ProjectedActive.ToString(ic)}/{tab.ProjectedMaxSlots.ToString(ic)} at timeline end",
                sectionHeaderStyle);

            bool sameAsProjected = StrategyRowsEqual(tab.CurrentRows, tab.ProjectedRows);
            if (sameAsProjected)
            {
                GUILayout.Label($"Active ({tab.CurrentRows.Count.ToString(ic)})", groupHeaderStyle);
                DrawStrategiesColumnHeader();
                GUILayout.BeginVertical(GUI.skin.box);
                if (tab.CurrentRows.Count == 0)
                    GUILayout.Label("  (no active strategies)", grayStyle);
                for (int i = 0; i < tab.CurrentRows.Count; i++)
                    DrawStrategyRow(tab.CurrentRows[i], GUI.skin.label);
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.Label($"Active now ({tab.CurrentRows.Count.ToString(ic)})", groupHeaderStyle);
                DrawStrategiesColumnHeader();
                GUILayout.BeginVertical(GUI.skin.box);
                if (tab.CurrentRows.Count == 0)
                    GUILayout.Label("  (no active strategies)", grayStyle);
                for (int i = 0; i < tab.CurrentRows.Count; i++)
                    DrawStrategyRow(tab.CurrentRows[i], GUI.skin.label);
                GUILayout.EndVertical();

                GUILayout.Space(3);
                int pendingCount = 0;
                for (int i = 0; i < tab.ProjectedRows.Count; i++)
                    if (tab.ProjectedRows[i].IsPendingActivate) pendingCount++;

                bool expanded = !foldedGroups.Contains(GroupKey_StrategiesPending);
                string headerText = $"Pending in timeline ({pendingCount.ToString(ic)})";
                bool newExpanded = GUILayout.Toggle(expanded, headerText, toggleButtonStyle,
                    GUILayout.ExpandWidth(true));
                if (newExpanded != expanded)
                {
                    ToggleSection(foldedGroups, GroupKey_StrategiesPending);
                }
                bool folded = !newExpanded;

                if (!folded)
                {
                    DrawStrategiesColumnHeader();
                    GUILayout.BeginVertical(GUI.skin.box);
                    if (pendingCount == 0)
                        GUILayout.Label("  (none)", grayStyle);
                    for (int i = 0; i < tab.ProjectedRows.Count; i++)
                    {
                        var r = tab.ProjectedRows[i];
                        if (!r.IsPendingActivate) continue;
                        DrawStrategyRow(r, pendingStyle);
                    }
                    GUILayout.EndVertical();
                }
            }
        }

        private void DrawStrategiesColumnHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Strategy", columnHeaderStyle, GUILayout.Width(ColW_StrategyTitle));
            GUILayout.Label("Activated UT", columnHeaderStyle, GUILayout.Width(ColW_ActivateUT));
            GUILayout.Label("Flow", columnHeaderStyle, GUILayout.Width(ColW_Flow));
            GUILayout.Label("Status", columnHeaderStyle, GUILayout.Width(ColW_PendingTag));
            GUILayout.EndHorizontal();
        }

        private void DrawStrategyRow(StrategyRow r, GUIStyle rowStyle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(FormatStrategyRow_Title(r), rowStyle, GUILayout.Width(ColW_StrategyTitle));
            GUILayout.Label(FormatStrategyRow_Activate(r), rowStyle, GUILayout.Width(ColW_ActivateUT));
            GUILayout.Label(FormatStrategyRow_Flow(r), rowStyle, GUILayout.Width(ColW_Flow));
            GUILayout.Label(FormatStrategyRow_Pending(r), rowStyle, GUILayout.Width(ColW_PendingTag));
            GUILayout.EndHorizontal();
        }

        private void DrawFacilitiesTab(FacilitiesTabVM tab, Game.Modes mode)
        {
            if (mode == Game.Modes.SANDBOX || mode == Game.Modes.MISSION_BUILDER || mode == Game.Modes.MISSION)
            {
                GUILayout.Label("Career state is not tracked in Sandbox mode.", grayStyle);
                return;
            }

            GUILayout.Label("Facilities", sectionHeaderStyle);
            DrawFacilitiesColumnHeader();
            GUILayout.BeginVertical(GUI.skin.box);
            if (tab.Rows.Count == 0)
            {
                GUILayout.Label("  (no facility data)", grayStyle);
            }
            else
            {
                for (int i = 0; i < tab.Rows.Count; i++)
                {
                    var row = tab.Rows[i];
                    GUIStyle rowStyle = row.HasUpcomingChange ? pendingStyle : GUI.skin.label;
                    DrawFacilityRow(row, rowStyle);
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawFacilitiesColumnHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Facility", columnHeaderStyle, GUILayout.Width(ColW_FacilityTitle));
            GUILayout.Label("Level", columnHeaderStyle, GUILayout.Width(ColW_Level));
            GUILayout.Label("Status", columnHeaderStyle, GUILayout.Width(ColW_Status));
            GUILayout.EndHorizontal();
        }

        private void DrawFacilityRow(FacilityRow r, GUIStyle rowStyle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(FormatFacilityRow_Title(r), rowStyle, GUILayout.Width(ColW_FacilityTitle));
            GUILayout.Label(FormatFacilityRow_Level(r), rowStyle, GUILayout.Width(ColW_Level));
            GUILayout.Label(FormatFacilityRow_Status(r), rowStyle, GUILayout.Width(ColW_Status));
            GUILayout.EndHorizontal();
        }

        private void DrawMilestonesTab(MilestonesTabVM tab, Game.Modes mode)
        {
            if (mode == Game.Modes.SANDBOX || mode == Game.Modes.MISSION_BUILDER || mode == Game.Modes.MISSION)
            {
                GUILayout.Label("Career state is not tracked in Sandbox mode.", grayStyle);
                return;
            }

            var ic = CultureInfo.InvariantCulture;
            GUILayout.Label(
                $"Milestones ({tab.CurrentCreditedCount.ToString(ic)} credited / {tab.ProjectedCreditedCount.ToString(ic)} at timeline end)",
                sectionHeaderStyle);
            DrawMilestonesColumnHeader();
            GUILayout.BeginVertical(GUI.skin.box);
            if (tab.Rows.Count == 0)
            {
                GUILayout.Label("  (no milestones credited)", grayStyle);
            }
            else
            {
                for (int i = 0; i < tab.Rows.Count; i++)
                {
                    var row = tab.Rows[i];
                    GUIStyle rowStyle = row.IsPendingCredit ? pendingStyle : GUI.skin.label;
                    DrawMilestoneRow(row, rowStyle);
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawMilestonesColumnHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Credited UT", columnHeaderStyle, GUILayout.Width(ColW_MilestoneUT));
            GUILayout.Label("Milestone", columnHeaderStyle, GUILayout.Width(ColW_MilestoneTitle));
            GUILayout.Label("Rewards", columnHeaderStyle, GUILayout.Width(ColW_Rewards));
            GUILayout.Label("Status", columnHeaderStyle, GUILayout.Width(ColW_PendingTag));
            GUILayout.EndHorizontal();
        }

        private void DrawMilestoneRow(MilestoneRow r, GUIStyle rowStyle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(FormatMilestoneRow_UT(r), rowStyle, GUILayout.Width(ColW_MilestoneUT));
            GUILayout.Label(FormatMilestoneRow_Title(r), rowStyle, GUILayout.Width(ColW_MilestoneTitle));
            GUILayout.Label(FormatMilestoneRow_Rewards(r), rowStyle, GUILayout.Width(ColW_Rewards));
            GUILayout.Label(FormatMilestoneRow_Pending(r), rowStyle, GUILayout.Width(ColW_PendingTag));
            GUILayout.EndHorizontal();
        }

        // Equality helpers for the "collapse to single Active section" decision.
        // Two rows are equal when their id + AcceptUT/ActivateUT match; we don't
        // compare IsPending* because collapsed view only fires when current ==
        // projected (which implies no pending rows).
        private static bool RowsEqual(List<ContractRow> a, List<ContractRow> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].ContractId, b[i].ContractId, StringComparison.Ordinal)) return false;
                if (a[i].AcceptUT != b[i].AcceptUT) return false;
            }
            return true;
        }

        private static bool StrategyRowsEqual(List<StrategyRow> a, List<StrategyRow> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].StrategyId, b[i].StrategyId, StringComparison.Ordinal)) return false;
                if (a[i].ActivateUT != b[i].ActivateUT) return false;
            }
            return true;
        }

        // ================================================================
        // Formatting helpers (pure, InvariantCulture, testable)
        // ================================================================

        // ---- Per-column contract helpers (Phase 2b column-aligned rows) ----

        /// <summary>
        /// Returns the contract display title, falling back to the raw id and
        /// then to "(unknown)" when both are null/empty.
        /// </summary>
        internal static string FormatContractRow_Title(ContractRow r)
        {
            return string.IsNullOrEmpty(r.DisplayTitle) ? (r.ContractId ?? "(unknown)") : r.DisplayTitle;
        }

        /// <summary>
        /// Returns the accepted UT as an InvariantCulture F0 string (no unit
        /// prefix — the column header carries the "Accepted UT" label).
        /// </summary>
        internal static string FormatContractRow_Accept(ContractRow r)
        {
            return r.AcceptUT.ToString("F0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns the deadline UT as F0, or the literal "--" when the deadline
        /// is NaN.
        /// </summary>
        internal static string FormatContractRow_Deadline(ContractRow r)
        {
            if (double.IsNaN(r.DeadlineUT)) return "--";
            return r.DeadlineUT.ToString("F0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns "(pending)" when the row is flagged pending-accept, else
        /// empty. The status column renders this verbatim.
        /// </summary>
        internal static string FormatContractRow_Pending(ContractRow r)
        {
            if (r.IsPendingAccept) return "(pending)";
            if (r.IsClosingByTimelineEnd) return "(closing)";
            return "";
        }

        /// <summary>
        /// Single-string formatter retained as a thin wrapper for the Phase 2
        /// substring tests. New callers should use the per-column helpers; the
        /// Phase 2b tab renderers do.
        /// </summary>
        internal static string FormatContractRow(ContractRow r)
        {
            string title = FormatContractRow_Title(r);
            string deadline = double.IsNaN(r.DeadlineUT)
                ? "(deadline --)"
                : $"deadline UT {FormatContractRow_Deadline(r)}";
            string baseLine = $"{title}  accepted UT {FormatContractRow_Accept(r)}  {deadline}";
            if (r.IsPendingAccept) baseLine += " (pending)";
            else if (r.IsClosingByTimelineEnd) baseLine += " (closing)";
            return baseLine;
        }

        // ---- Per-column strategy helpers ----

        internal static string FormatStrategyRow_Title(StrategyRow r)
        {
            return string.IsNullOrEmpty(r.DisplayTitle) ? (r.StrategyId ?? "(unknown)") : r.DisplayTitle;
        }

        internal static string FormatStrategyRow_Activate(StrategyRow r)
        {
            return r.ActivateUT.ToString("F0", CultureInfo.InvariantCulture);
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

        internal static string FormatStrategyRow_Pending(StrategyRow r)
        {
            if (r.IsPendingActivate) return "(pending)";
            if (r.IsClosingByTimelineEnd) return "(closing)";
            return "";
        }

        /// <summary>
        /// Single-string formatter retained as a thin wrapper for Phase 2 tests.
        /// </summary>
        internal static string FormatStrategyRow(StrategyRow r)
        {
            string title = FormatStrategyRow_Title(r);
            string baseLine =
                $"{title}  activated UT {FormatStrategyRow_Activate(r)}  {FormatStrategyRow_Flow(r)}";
            if (r.IsPendingActivate) baseLine += " (pending)";
            else if (r.IsClosingByTimelineEnd) baseLine += " (closing)";
            return baseLine;
        }

        // ---- Per-column facility helpers ----

        internal static string FormatFacilityRow_Title(FacilityRow r)
        {
            return string.IsNullOrEmpty(r.DisplayTitle) ? (r.FacilityId ?? "(unknown)") : r.DisplayTitle;
        }

        /// <summary>
        /// Returns "L{cur}" or "L{cur} -> L{proj} (upcoming)" when an upgrade
        /// is pending. Destroyed-state lives in the Status column, not here.
        /// </summary>
        internal static string FormatFacilityRow_Level(FacilityRow r)
        {
            if (r.HasUpcomingChange && r.CurrentLevel != r.ProjectedLevel)
                return $"L{r.CurrentLevel} -> L{r.ProjectedLevel} (upcoming)";
            return $"L{r.CurrentLevel}";
        }

        /// <summary>
        /// Returns "(destroyed)" / "(destroyed, repair pending)" / empty for
        /// the Status column. Non-destroyed facilities return empty so the
        /// column stays visually quiet.
        /// </summary>
        internal static string FormatFacilityRow_Status(FacilityRow r)
        {
            if (!r.CurrentDestroyed) return "";
            return r.ProjectedDestroyed ? "(destroyed)" : "(destroyed, repair pending)";
        }

        /// <summary>
        /// Single-string formatter retained as a thin wrapper for Phase 2 tests.
        /// </summary>
        internal static string FormatFacilityRow(FacilityRow r)
        {
            string baseLine = $"{FormatFacilityRow_Title(r)}  {FormatFacilityRow_Level(r)}";
            string status = FormatFacilityRow_Status(r);
            if (!string.IsNullOrEmpty(status)) baseLine += "  " + status;
            return baseLine;
        }

        // ---- Per-column milestone helpers ----

        internal static string FormatMilestoneRow_UT(MilestoneRow r)
        {
            return r.CreditedUT.ToString("F0", CultureInfo.InvariantCulture);
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

        internal static string FormatMilestoneRow_Pending(MilestoneRow r)
        {
            return r.IsPendingCredit ? "(pending)" : "";
        }

        /// <summary>
        /// Single-string formatter retained as a thin wrapper for Phase 2 tests.
        /// </summary>
        internal static string FormatMilestoneRow(MilestoneRow r)
        {
            var sb = new StringBuilder();
            sb.Append("UT ").Append(FormatMilestoneRow_UT(r));
            sb.Append("   ").Append(FormatMilestoneRow_Title(r));
            string rewards = FormatMilestoneRow_Rewards(r);
            if (!string.IsNullOrEmpty(rewards))
                sb.Append("  ").Append(rewards);
            if (r.IsPendingCredit) sb.Append(" (pending)");
            return sb.ToString();
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
        {
            bool wasFolded = foldedGroups.Contains(name);
            if (wasFolded) foldedGroups.Remove(name);
            else foldedGroups.Add(name);
            bool nowFolded = !wasFolded;
            ParsekLog.Verbose("UI",
                $"CareerStateWindow: section toggled name={name} folded={nowFolded}");
            return nowFolded;
        }

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
