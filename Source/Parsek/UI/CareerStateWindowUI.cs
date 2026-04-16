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
        private GUIStyle pendingStyle;
        private GUIStyle grayStyle;
        private GUIStyle bannerStyle;

        private const float DefaultWindowWidth = 420f;
        private const float DefaultWindowHeight = 400f;
        private const float MinWindowWidth = 320f;
        private const float MinWindowHeight = 200f;
        private const string CareerStateInputLockId = "Parsek_CareerStateWindow";

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
            public bool HasDivergence;
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
            public bool Touched; // true once any action has been observed for this facility
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
            var ic = CultureInfo.InvariantCulture;

            if (contracts == null || strategies == null || facilities == null
                || milestones == null || actions == null)
            {
                ParsekLog.Warn("UI",
                    "CareerStateWindow: Build called with null module or actions; returning empty VM");
                return EmptyVM(liveUT, mode);
            }

            // Per-walk skip-log dedup set (design doc §7 walk-skip logging).
            var loggedSkipKeys = new HashSet<string>(StringComparer.Ordinal);

            // Terminal-state accumulators, walked forward through the action list.
            var activeContractsTerm = new Dictionary<string, ContractAcc>(StringComparer.Ordinal);
            var activeStrategiesTerm = new Dictionary<string, StrategyAcc>(StringComparer.Ordinal);
            var facilityStateTerm = new Dictionary<string, FacilityAcc>(StringComparer.Ordinal);
            var creditedMilestonesTerm = new HashSet<string>(StringComparer.Ordinal);
            var allMilestoneRows = new List<MilestoneRow>();

            // "Current" snapshots — populated when we cross the liveUT boundary.
            Dictionary<string, ContractAcc> activeContractsCurSnap = null;
            Dictionary<string, StrategyAcc> activeStrategiesCurSnap = null;
            Dictionary<string, FacilityAcc> facilityStateCurSnap = null;
            HashSet<string> creditedMilestonesCurSnap = null;

            double terminalUT = liveUT;

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

                switch (a.Type)
                {
                    case GameActionType.ContractAccept:
                        if (!a.Effective)
                        {
                            LogSkip(loggedSkipKeys, "ContractAccept", "Ineffective", a);
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
                            LogSkip(loggedSkipKeys, a.Type.ToString(), "Ineffective", a);
                            break;
                        }
                        activeContractsTerm.Remove(a.ContractId ?? "");
                        break;

                    case GameActionType.StrategyActivate:
                        if (!a.Effective)
                        {
                            LogSkip(loggedSkipKeys, "StrategyActivate", "Ineffective", a);
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
                            LogSkip(loggedSkipKeys, "StrategyDeactivate", "Ineffective", a);
                            break;
                        }
                        activeStrategiesTerm.Remove(a.StrategyId ?? "");
                        break;

                    case GameActionType.FacilityUpgrade:
                        {
                            if (!a.Effective)
                            {
                                LogSkip(loggedSkipKeys, "FacilityUpgrade", "Ineffective", a);
                                break;
                            }
                            string fid = a.FacilityId ?? "";
                            FacilityAcc f;
                            if (!facilityStateTerm.TryGetValue(fid, out f))
                                f = new FacilityAcc { Level = 1, Destroyed = false };
                            f.Level = a.ToLevel;
                            f.Touched = true;
                            facilityStateTerm[fid] = f;
                        }
                        break;

                    case GameActionType.FacilityDestruction:
                        {
                            if (!a.Effective)
                            {
                                LogSkip(loggedSkipKeys, "FacilityDestruction", "Ineffective", a);
                                break;
                            }
                            string fid = a.FacilityId ?? "";
                            FacilityAcc f;
                            if (!facilityStateTerm.TryGetValue(fid, out f))
                                f = new FacilityAcc { Level = 1, Destroyed = false };
                            f.Destroyed = true;
                            f.Touched = true;
                            facilityStateTerm[fid] = f;
                        }
                        break;

                    case GameActionType.FacilityRepair:
                        {
                            if (!a.Effective)
                            {
                                LogSkip(loggedSkipKeys, "FacilityRepair", "Ineffective", a);
                                break;
                            }
                            string fid = a.FacilityId ?? "";
                            FacilityAcc f;
                            if (!facilityStateTerm.TryGetValue(fid, out f))
                                f = new FacilityAcc { Level = 1, Destroyed = false };
                            f.Destroyed = false;
                            f.Touched = true;
                            facilityStateTerm[fid] = f;
                        }
                        break;

                    case GameActionType.MilestoneAchievement:
                        {
                            string mid = a.MilestoneId ?? "";
                            if (!a.Effective)
                            {
                                // Mirrors MilestonesModule: ineffective duplicates are skipped.
                                LogSkip(loggedSkipKeys, "MilestoneAchievement", "Ineffective", a);
                                break;
                            }
                            if (creditedMilestonesTerm.Contains(mid))
                            {
                                // Defensive: Effective=true but already credited (should not happen
                                // given MilestonesModule.ProcessAction semantics, but we don't want
                                // to duplicate rows if an upstream bug slips through).
                                LogSkip(loggedSkipKeys, "MilestoneAchievement", "AlreadyCredited", a);
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

            // --- Mode gating (design doc E1/E2) ---
            bool contractsVisible = mode == Game.Modes.CAREER;
            bool strategiesVisible = mode == Game.Modes.CAREER;
            bool facilitiesVisible = mode == Game.Modes.CAREER || mode == Game.Modes.SCIENCE_SANDBOX;
            bool milestonesVisible = mode == Game.Modes.CAREER || mode == Game.Modes.SCIENCE_SANDBOX;

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

            // --- Contracts tab ---
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
                    contractsVM.CurrentRows.Add(new ContractRow
                    {
                        ContractId = c.ContractId,
                        DisplayTitle = c.Title,
                        AcceptUT = c.AcceptUT,
                        DeadlineUT = c.DeadlineUT,
                        IsPendingAccept = false
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

            // --- Strategies tab ---
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
                    strategiesVM.CurrentRows.Add(new StrategyRow
                    {
                        StrategyId = s.StrategyId,
                        DisplayTitle = s.Title,
                        ActivateUT = s.ActivateUT,
                        SourceResource = s.SourceResource,
                        TargetResource = s.TargetResource,
                        Commitment = s.Commitment,
                        IsPendingActivate = false
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

            // --- Facilities tab ---
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

            // --- Milestones tab ---
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
                HasDivergence = divergence
            };

            ParsekLog.Verbose("UI",
                "CareerStateWindow: rebuilt VM "
                + $"liveUT={liveUT.ToString("F0", ic)} "
                + $"terminalUT={terminalUT.ToString("F0", ic)} "
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

        private static CareerStateViewModel EmptyVM(double liveUT, Game.Modes mode)
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
                HasDivergence = false
            };
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
                    Destroyed = kvp.Value.Destroyed,
                    Touched = kvp.Value.Touched
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

        private static void LogSkip(
            HashSet<string> loggedSkipKeys, string actionType, string reason, GameAction a)
        {
            string key = "CareerStateWindow.skip." + actionType + "." + reason;
            if (loggedSkipKeys.Contains(key)) return;
            loggedSkipKeys.Add(key);
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("UI",
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
                ParsekLog.Verbose("UI",
                    $"CareerStateWindow: contract title lookup threw id={contractId} ex={ex.GetType().Name}");
                // Fall through to raw-id fallback.
            }

            // Preference 3: raw id fallback.
            ParsekLog.Verbose("UI",
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
                var lookup = StrategyTitleLookupForTesting;
                if (lookup != null)
                {
                    string viaStub = lookup(strategyId);
                    if (!string.IsNullOrEmpty(viaStub)) return viaStub;
                }
                // Production path would query Strategies.StrategySystem.Instance.Strategies here.
                // Not wired in Phase 1 — Phase 2 adds the live lookup.
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"CareerStateWindow: strategy title lookup threw id={strategyId} ex={ex.GetType().Name}");
            }

            ParsekLog.Verbose("UI",
                $"CareerStateWindow: strategy title fallback id={strategyId}");
            return strategyId;
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
            careerStateWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekCareerState".GetHashCode(),
                careerStateWindowRect,
                DrawCareerStateWindow,
                "Parsek \u2014 Career State",
                opaqueWindowStyle,
                GUILayout.Width(careerStateWindowRect.width),
                GUILayout.Height(careerStateWindowRect.height)
            );
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
            if (sectionHeaderStyle != null) return;
            sectionHeaderStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                stretchWidth = true
            };
            groupHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
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

            // Rebuild the cached VM on demand. Defensive against a null CurrentGame
            // (e.g. transient scene transitions or a mod that hot-swaps HighLogic).
            var currentGame = HighLogic.CurrentGame;
            if (currentGame == null)
            {
                ParsekLog.Warn("UI",
                    "CareerStateWindow: HighLogic.CurrentGame is null; rendering fallback");
                GUILayout.Label("Career state unavailable \u2014 game not loaded", bannerStyle);
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

            // Rebuild if no cache or if mode has changed while the window was open.
            if (cachedVM == null || cachedVM.Value.Mode != currentMode)
            {
                cachedVM = Build(
                    Ledger.Actions,
                    Planetarium.GetUniversalTime(),
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
            int newTab = GUILayout.Toolbar(selectedTab, TabLabels);
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
            var ic = CultureInfo.InvariantCulture;
            string line;
            if (vm.Mode == Game.Modes.CAREER)
            {
                line = $"Career mode \u2014 UT {vm.LiveUT.ToString("F0", ic)}";
                if (vm.HasDivergence)
                {
                    double ahead = vm.TerminalUT - vm.LiveUT;
                    if (ahead < 0) ahead = 0;
                    line += $"  (projection ahead: {ahead.ToString("F0", ic)})";
                }
            }
            else if (vm.Mode == Game.Modes.SCIENCE_SANDBOX)
            {
                line = "Science mode \u2014 contracts and strategies unavailable";
            }
            else
            {
                // SANDBOX, MISSION_BUILDER, MISSION: all treated as sandbox-equivalent.
                line = "Sandbox mode \u2014 career state is not tracked";
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
                $"Mission Control L{tab.MissionControlLevel.ToString(ic)} \u2014 slots {tab.CurrentActive.ToString(ic)}/{tab.CurrentMaxSlots.ToString(ic)} now, {tab.ProjectedActive.ToString(ic)}/{tab.ProjectedMaxSlots.ToString(ic)} projected",
                sectionHeaderStyle);

            bool sameAsProjected = RowsEqual(tab.CurrentRows, tab.ProjectedRows);
            if (sameAsProjected)
            {
                GUILayout.Label($"Active ({tab.CurrentRows.Count.ToString(ic)})", groupHeaderStyle);
                GUILayout.BeginVertical(GUI.skin.box);
                if (tab.CurrentRows.Count == 0)
                    GUILayout.Label("  (no active contracts)", grayStyle);
                for (int i = 0; i < tab.CurrentRows.Count; i++)
                    GUILayout.Label("  " + FormatContractRow(tab.CurrentRows[i]), GUI.skin.label);
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.Label($"Active now ({tab.CurrentRows.Count.ToString(ic)})", groupHeaderStyle);
                GUILayout.BeginVertical(GUI.skin.box);
                if (tab.CurrentRows.Count == 0)
                    GUILayout.Label("  (no active contracts)", grayStyle);
                for (int i = 0; i < tab.CurrentRows.Count; i++)
                    GUILayout.Label("  " + FormatContractRow(tab.CurrentRows[i]), GUI.skin.label);
                GUILayout.EndVertical();

                // "Pending in timeline" is the projected rows minus the current rows.
                GUILayout.Space(3);
                int pendingCount = 0;
                for (int i = 0; i < tab.ProjectedRows.Count; i++)
                    if (tab.ProjectedRows[i].IsPendingAccept) pendingCount++;
                GUILayout.Label($"Pending in timeline ({pendingCount.ToString(ic)})", groupHeaderStyle);
                GUILayout.BeginVertical(GUI.skin.box);
                if (pendingCount == 0)
                    GUILayout.Label("  (none)", grayStyle);
                for (int i = 0; i < tab.ProjectedRows.Count; i++)
                {
                    var r = tab.ProjectedRows[i];
                    if (!r.IsPendingAccept) continue;
                    GUILayout.Label("  " + FormatContractRow(r), pendingStyle);
                }
                GUILayout.EndVertical();
            }
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
                $"Administration L{tab.AdminLevel.ToString(ic)} \u2014 slots {tab.CurrentActive.ToString(ic)}/{tab.CurrentMaxSlots.ToString(ic)} now, {tab.ProjectedActive.ToString(ic)}/{tab.ProjectedMaxSlots.ToString(ic)} projected",
                sectionHeaderStyle);

            bool sameAsProjected = StrategyRowsEqual(tab.CurrentRows, tab.ProjectedRows);
            if (sameAsProjected)
            {
                GUILayout.Label($"Active ({tab.CurrentRows.Count.ToString(ic)})", groupHeaderStyle);
                GUILayout.BeginVertical(GUI.skin.box);
                if (tab.CurrentRows.Count == 0)
                    GUILayout.Label("  (no active strategies)", grayStyle);
                for (int i = 0; i < tab.CurrentRows.Count; i++)
                    GUILayout.Label("  " + FormatStrategyRow(tab.CurrentRows[i]), GUI.skin.label);
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.Label($"Active now ({tab.CurrentRows.Count.ToString(ic)})", groupHeaderStyle);
                GUILayout.BeginVertical(GUI.skin.box);
                if (tab.CurrentRows.Count == 0)
                    GUILayout.Label("  (no active strategies)", grayStyle);
                for (int i = 0; i < tab.CurrentRows.Count; i++)
                    GUILayout.Label("  " + FormatStrategyRow(tab.CurrentRows[i]), GUI.skin.label);
                GUILayout.EndVertical();

                GUILayout.Space(3);
                int pendingCount = 0;
                for (int i = 0; i < tab.ProjectedRows.Count; i++)
                    if (tab.ProjectedRows[i].IsPendingActivate) pendingCount++;
                GUILayout.Label($"Pending in timeline ({pendingCount.ToString(ic)})", groupHeaderStyle);
                GUILayout.BeginVertical(GUI.skin.box);
                if (pendingCount == 0)
                    GUILayout.Label("  (none)", grayStyle);
                for (int i = 0; i < tab.ProjectedRows.Count; i++)
                {
                    var r = tab.ProjectedRows[i];
                    if (!r.IsPendingActivate) continue;
                    GUILayout.Label("  " + FormatStrategyRow(r), pendingStyle);
                }
                GUILayout.EndVertical();
            }
        }

        private void DrawFacilitiesTab(FacilitiesTabVM tab, Game.Modes mode)
        {
            if (mode == Game.Modes.SANDBOX || mode == Game.Modes.MISSION_BUILDER || mode == Game.Modes.MISSION)
            {
                GUILayout.Label("Career state is not tracked in Sandbox mode.", grayStyle);
                return;
            }

            GUILayout.Label("Facilities", sectionHeaderStyle);
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
                    GUILayout.Label("  " + FormatFacilityRow(row), rowStyle);
                }
            }
            GUILayout.EndVertical();
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
                $"Milestones ({tab.CurrentCreditedCount.ToString(ic)} credited / {tab.ProjectedCreditedCount.ToString(ic)} projected)",
                sectionHeaderStyle);
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
                    GUILayout.Label("  " + FormatMilestoneRow(row), rowStyle);
                }
            }
            GUILayout.EndVertical();
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

        /// <summary>
        /// Formats a contract row for display. NaN deadline renders as
        /// "(deadline --)"; pending rows append " (pending)".
        /// </summary>
        internal static string FormatContractRow(ContractRow r)
        {
            var ic = CultureInfo.InvariantCulture;
            string title = string.IsNullOrEmpty(r.DisplayTitle) ? (r.ContractId ?? "(unknown)") : r.DisplayTitle;
            string deadline = double.IsNaN(r.DeadlineUT)
                ? "(deadline --)"
                : $"deadline UT {r.DeadlineUT.ToString("F0", ic)}";
            string baseLine = $"{title}  accepted UT {r.AcceptUT.ToString("F0", ic)}  {deadline}";
            if (r.IsPendingAccept) baseLine += " (pending)";
            return baseLine;
        }

        /// <summary>
        /// Formats a strategy row for display. Includes Source → Target and the
        /// commitment percentage (InvariantCulture F1).
        /// </summary>
        internal static string FormatStrategyRow(StrategyRow r)
        {
            var ic = CultureInfo.InvariantCulture;
            string title = string.IsNullOrEmpty(r.DisplayTitle) ? (r.StrategyId ?? "(unknown)") : r.DisplayTitle;
            string pct = (r.Commitment * 100f).ToString("F1", ic) + "%";
            string baseLine =
                $"{title}  activated UT {r.ActivateUT.ToString("F0", ic)}  {r.SourceResource} -> {r.TargetResource} @ {pct}";
            if (r.IsPendingActivate) baseLine += " (pending)";
            return baseLine;
        }

        /// <summary>
        /// Formats a facility row for display. Upcoming level change renders as
        /// "Title  L{cur} -> L{proj} (upcoming)"; destroyed rows append
        /// "(destroyed)" or "(destroyed, repair pending)" if projected not-destroyed.
        /// </summary>
        internal static string FormatFacilityRow(FacilityRow r)
        {
            string title = string.IsNullOrEmpty(r.DisplayTitle) ? (r.FacilityId ?? "(unknown)") : r.DisplayTitle;
            string levelPart;
            if (r.HasUpcomingChange && r.CurrentLevel != r.ProjectedLevel)
                levelPart = $"L{r.CurrentLevel} -> L{r.ProjectedLevel} (upcoming)";
            else
                levelPart = $"L{r.CurrentLevel}";

            string baseLine = $"{title}  {levelPart}";

            if (r.CurrentDestroyed)
            {
                if (!r.ProjectedDestroyed)
                    baseLine += "  (destroyed, repair pending)";
                else
                    baseLine += "  (destroyed)";
            }
            return baseLine;
        }

        /// <summary>
        /// Formats a milestone row for display. Zero-reward suffix entries are
        /// elided (e.g. "+ 0 sci" drops). Pending rows append " (pending)".
        /// </summary>
        internal static string FormatMilestoneRow(MilestoneRow r)
        {
            var ic = CultureInfo.InvariantCulture;
            string title = string.IsNullOrEmpty(r.DisplayTitle) ? (r.MilestoneId ?? "(unknown)") : r.DisplayTitle;
            var sb = new StringBuilder();
            sb.Append("UT ").Append(r.CreditedUT.ToString("F0", ic));
            sb.Append("   ").Append(title);
            if (r.FundsAwarded != 0f)
                sb.Append("  + ").Append(r.FundsAwarded.ToString("F0", ic)).Append(" funds");
            if (r.RepAwarded != 0f)
                sb.Append("  + ").Append(r.RepAwarded.ToString("F0", ic)).Append(" rep");
            if (r.ScienceAwarded != 0f)
                sb.Append("  + ").Append(r.ScienceAwarded.ToString("F1", ic)).Append(" sci");
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
