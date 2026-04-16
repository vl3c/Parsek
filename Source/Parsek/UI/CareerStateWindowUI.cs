using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

        // --- Phase 2 placeholders (unused in Phase 1; stubbed so future commits only
        // add DrawIfOpen wiring without re-declaring the chrome fields). ---
#pragma warning disable CS0169, CS0414 // Phase 2 placeholder fields; read/written by DrawIfOpen (Phase 2).
        private bool showCareerStateWindow;
        private Rect careerStateWindowRect;
        private bool careerStateWindowHasInputLock;
        private bool isResizingCareerStateWindow;
        private Vector2 careerStateScrollPos;
        private int selectedTab;
        private CareerStateViewModel? cachedVM;
#pragma warning restore CS0169, CS0414

        private const float DefaultWindowWidth = 420f;
        private const float DefaultWindowHeight = 400f;
        private const float MinWindowWidth = 320f;
        private const float MinWindowHeight = 200f;
        private const string CareerStateInputLockId = "Parsek_CareerStateWindow";

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
            set { showCareerStateWindow = value; }
        }

        internal CareerStateWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
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
    }
}
