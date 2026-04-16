using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for CareerStateWindowUI.Build() — design doc §8.1 / §8.3 / §8.5.
    ///
    /// Build() is a pure walk over a GameAction list plus four module instances
    /// (read only for slot-helper access, so the modules themselves just need to
    /// be non-null). Fixtures are hand-crafted minimal GameAction instances.
    /// </summary>
    [Collection("Sequential")]
    public class CareerStateWindowUITests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CareerStateWindowUITests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            CareerStateWindowUI.ContractTitleLookupForTesting = null;
            CareerStateWindowUI.StrategyTitleLookupForTesting = null;
        }

        public void Dispose()
        {
            CareerStateWindowUI.ContractTitleLookupForTesting = null;
            CareerStateWindowUI.StrategyTitleLookupForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        private static (ContractsModule, StrategiesModule, FacilitiesModule, MilestonesModule) Modules()
        {
            return (new ContractsModule(), new StrategiesModule(),
                    new FacilitiesModule(), new MilestonesModule());
        }

        private static GameAction Accept(string contractId, double ut, string title = null, bool effective = true)
        {
            return new GameAction
            {
                Type = GameActionType.ContractAccept,
                UT = ut,
                ContractId = contractId,
                ContractTitle = title,
                DeadlineUT = float.NaN,
                Effective = effective
            };
        }

        private static GameAction Complete(string contractId, double ut, bool effective = true)
        {
            return new GameAction
            {
                Type = GameActionType.ContractComplete,
                UT = ut,
                ContractId = contractId,
                Effective = effective
            };
        }

        private static GameAction Activate(string strategyId, double ut, float commitment = 0.1f,
            StrategyResource src = StrategyResource.Funds,
            StrategyResource tgt = StrategyResource.Science)
        {
            return new GameAction
            {
                Type = GameActionType.StrategyActivate,
                UT = ut,
                StrategyId = strategyId,
                SourceResource = src,
                TargetResource = tgt,
                Commitment = commitment,
                Effective = true
            };
        }

        private static GameAction Deactivate(string strategyId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.StrategyDeactivate,
                UT = ut,
                StrategyId = strategyId,
                Effective = true
            };
        }

        private static GameAction Upgrade(string facilityId, int toLevel, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.FacilityUpgrade,
                UT = ut,
                FacilityId = facilityId,
                ToLevel = toLevel,
                Effective = true
            };
        }

        private static GameAction Destroy(string facilityId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.FacilityDestruction,
                UT = ut,
                FacilityId = facilityId,
                Effective = true
            };
        }

        private static GameAction Repair(string facilityId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.FacilityRepair,
                UT = ut,
                FacilityId = facilityId,
                Effective = true
            };
        }

        private static GameAction Milestone(string id, double ut, bool effective = true,
            float funds = 0f, float rep = 0f, float sci = 0f)
        {
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                MilestoneId = id,
                Effective = effective,
                MilestoneFundsAwarded = funds,
                MilestoneRepAwarded = rep,
                MilestoneScienceAwarded = sci
            };
        }

        // ──────────────────────────────────────────────────────────────────
        // §8.1 Build() coverage
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_NoActions_EmptyVM()
        {
            // Regression: this fails if the walk throws on empty input or populates
            // per-tab collections from thin air.
            var (c, s, f, m) = Modules();

            var vm = CareerStateWindowUI.Build(new List<GameAction>(), 0.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.ProjectedRows);
            Assert.Empty(vm.Strategies.CurrentRows);
            Assert.Empty(vm.Strategies.ProjectedRows);
            Assert.Empty(vm.Milestones.Rows);
            // Facilities tab still emits the 9-row default inventory.
            Assert.Equal(9, vm.Facilities.Rows.Count);
            Assert.False(vm.HasDivergence);
        }

        [Fact]
        public void Build_Contracts_CurrentEqualsProjected_NoDivergence()
        {
            // Regression: this fails if the walk double-counts terminal state or
            // incorrectly flags divergence when current and projected match.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-1", ut: 100.0, title: "Explore Mun")
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Contracts.CurrentRows);
            Assert.Single(vm.Contracts.ProjectedRows);
            Assert.False(vm.Contracts.CurrentRows[0].IsPendingAccept);
            Assert.False(vm.Contracts.ProjectedRows[0].IsPendingAccept);
            Assert.False(vm.HasDivergence);
        }

        [Fact]
        public void Build_Contracts_PendingAccept_AppearsInProjectedOnly()
        {
            // Regression: this fails if the UT filter is off-by-one or if
            // IsPendingAccept is mis-set.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-future", ut: 300.0, title: "Rescue Kerbal")
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Single(vm.Contracts.ProjectedRows);
            Assert.True(vm.Contracts.ProjectedRows[0].IsPendingAccept);
            Assert.Equal("Rescue Kerbal", vm.Contracts.ProjectedRows[0].DisplayTitle);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_Contracts_CompletedAfterLiveUT_StaysActiveInCurrent()
        {
            // Regression: this fails if the walk drops pending-complete contracts
            // from the current snapshot — accept should count now, completion only later.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-1", ut: 100.0, title: "Explore Mun"),
                Complete("ctr-1", ut: 300.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.ProjectedRows);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_Contracts_IneffectiveAcceptSkipped()
        {
            // Regression: this fails if Effective=false contract actions are still
            // written into the walk's active-set (mirrors ContractsModule.ProcessAction).
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-bad", ut: 100.0, title: "Ghost Contract", effective: false)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.ProjectedRows);
        }

        [Fact]
        public void Build_Strategies_ActivateDeactivateRoundTrip()
        {
            // Regression: this fails if the activate/deactivate pairing logic is
            // inverted or if the current snapshot doesn't observe the activation.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Activate("Subsidy", ut: 100.0, commitment: 0.1f),
                Deactivate("Subsidy", ut: 200.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Strategies.CurrentRows);
            Assert.Empty(vm.Strategies.ProjectedRows);
            Assert.Equal(0.1f, vm.Strategies.CurrentRows[0].Commitment);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_Facilities_UpgradeSequence()
        {
            // Regression: this fails if last-write-wins semantics break (e.g., the walk
            // keeps an earlier upgrade level once a later one is observed).
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Upgrade("LaunchPad", 2, ut: 100.0),
                Upgrade("LaunchPad", 3, ut: 200.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s, f, m);

            var pad = vm.Facilities.Rows.First(r => r.FacilityId == "LaunchPad");
            Assert.Equal(2, pad.CurrentLevel);
            Assert.Equal(3, pad.ProjectedLevel);
            Assert.True(pad.HasUpcomingChange);
        }

        [Fact]
        public void Build_Facilities_DestructionThenRepair()
        {
            // Regression: this fails if repair doesn't clear Destroyed — the projected
            // state should be not-destroyed even though the current is still destroyed.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Destroy("Runway", ut: 100.0),
                Repair("Runway", ut: 200.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s, f, m);

            var runway = vm.Facilities.Rows.First(r => r.FacilityId == "Runway");
            Assert.True(runway.CurrentDestroyed);
            Assert.False(runway.ProjectedDestroyed);
            Assert.True(runway.HasUpcomingChange);
        }

        [Fact]
        public void Build_Facilities_UnseenFacilityDefaults()
        {
            // Regression: this fails if the walk drops facilities that never appear
            // in the action stream — the KSC inventory must stay stable at 9 rows.
            var (c, s, f, m) = Modules();

            var vm = CareerStateWindowUI.Build(new List<GameAction>(), 0.0,
                Game.Modes.CAREER, c, s, f, m);

            var astro = vm.Facilities.Rows.First(r => r.FacilityId == "AstronautComplex");
            Assert.Equal(1, astro.CurrentLevel);
            Assert.False(astro.CurrentDestroyed);
            Assert.Equal(1, astro.ProjectedLevel);
            Assert.False(astro.ProjectedDestroyed);
        }

        [Fact]
        public void Build_Milestones_PendingCreditShowsInProjectedOnly()
        {
            // Regression: this fails if a future-UT milestone is credited into the
            // "current" count or if IsPendingCredit isn't flipped on the row.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Milestone("FirstOrbit", ut: 300.0, funds: 15000f)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(0, vm.Milestones.CurrentCreditedCount);
            Assert.Equal(1, vm.Milestones.ProjectedCreditedCount);
            Assert.Single(vm.Milestones.Rows);
            Assert.True(vm.Milestones.Rows[0].IsPendingCredit);
            Assert.Equal(15000f, vm.Milestones.Rows[0].FundsAwarded);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_Milestones_IneffectiveDuplicateSkipped()
        {
            // Regression: this fails if Effective=false duplicates still emit rows —
            // design doc §4.3 requires only Effective actions mutate state.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Milestone("FirstLaunch", ut: 100.0, effective: true, funds: 10000f),
                Milestone("FirstLaunch", ut: 200.0, effective: false)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 500.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Milestones.Rows);
            Assert.Equal(100.0, vm.Milestones.Rows[0].CreditedUT);
        }

        [Fact]
        public void Build_FacilityLevelsEchoed_ContractsUseMissionControl_StrategiesUseAdministration()
        {
            // Regression: guards against the two tabs reading the wrong facility for slot math.
            // Per LedgerOrchestrator.UpdateSlotLimitsFromFacilities (LedgerOrchestrator.cs:1440-1446)
            // contracts slots derive from MissionControl level; strategies slots derive from
            // Administration level. Upgrading Administration alone must NOT bump contract slots
            // and vice versa — an earlier Phase-1 revision had this crossed.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Upgrade("MissionControl", 2, ut: 50.0),
                Upgrade("Administration", 2, ut: 100.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(2, vm.Contracts.MissionControlLevel);
            Assert.Equal(2, vm.Contracts.ProjectedMissionControlLevel);
            // GetContractSlots(2) == 7 per LedgerOrchestrator.cs:1462.
            Assert.Equal(7, vm.Contracts.CurrentMaxSlots);
            Assert.Equal(7, vm.Contracts.ProjectedMaxSlots);

            Assert.Equal(2, vm.Strategies.AdminLevel);
            Assert.Equal(2, vm.Strategies.ProjectedAdminLevel);
            // GetStrategySlots(2) == 3 per LedgerOrchestrator.cs:1476.
            Assert.Equal(3, vm.Strategies.CurrentMaxSlots);
            Assert.Equal(3, vm.Strategies.ProjectedMaxSlots);
        }

        [Fact]
        public void Build_LiveUTEqualsActionUT_CountsAsApplied()
        {
            // Regression: design doc §4.3 calls for <=, not <. An action at the exact
            // live UT must be treated as already-applied. Fails if the comparison
            // mistakenly uses strict inequality.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-at-boundary", ut: 200.0, title: "Boundary Contract")
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Contracts.CurrentRows);
            Assert.False(vm.Contracts.CurrentRows[0].IsPendingAccept);
        }

        // ──────────────────────────────────────────────────────────────────
        // §8.3 Log-assertion tests
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_Logs_VMRebuildOncePerCall()
        {
            // Regression: this fails if the walk accidentally rebuilds twice (e.g.
            // logs once for each of current/projected snapshots) — the "rebuilt VM"
            // marker must appear exactly once per Build() call.
            var (c, s, f, m) = Modules();

            CareerStateWindowUI.Build(new List<GameAction>(), 0.0,
                Game.Modes.CAREER, c, s, f, m);

            int matches = logLines.Count(l =>
                l.Contains("[UI]") && l.Contains("CareerStateWindow: rebuilt VM"));
            Assert.Equal(1, matches);
        }

        [Fact]
        public void Build_Logs_DivergenceFlaggedWhenPendingExists()
        {
            // Regression: guards against a silent-computed-never-logged bug — if the
            // divergence flag is set in the VM but never logged, diagnostics lose the
            // "pending actions present" signal.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-pending", ut: 300.0, title: "Future")
            };

            CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Contains(logLines, l =>
                l.Contains("CareerStateWindow: rebuilt VM")
                && l.Contains("divergence=True"));
        }

        [Fact]
        public void Build_Logs_ContractTitleFallback()
        {
            // Regression: synthetic contract id with no action.ContractTitle and a
            // null-returning lookup — the fallback branch must emit a Verbose line
            // with the id so debugging is traceable.
            CareerStateWindowUI.ContractTitleLookupForTesting = _ => null;
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-orphan", ut: 100.0, title: null)
            };

            CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("contract title fallback")
                && l.Contains("id=ctr-orphan"));
        }

        // ──────────────────────────────────────────────────────────────────
        // §8.5 Edge-case tests
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_Mode_Sandbox_AllTabsEmptyWithBanner()
        {
            // Regression: E1 — Sandbox mode must show empty tabs regardless of whether
            // the ledger has career actions. Fails if the walk still populates rows.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-1", ut: 100.0, title: "Career Contract"),
                Activate("Strat", ut: 100.0),
                Upgrade("LaunchPad", 2, ut: 100.0),
                Milestone("FirstLaunch", ut: 100.0, funds: 10000f)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 500.0,
                Game.Modes.SANDBOX, c, s, f, m);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.ProjectedRows);
            Assert.Empty(vm.Strategies.CurrentRows);
            Assert.Empty(vm.Strategies.ProjectedRows);
            Assert.Empty(vm.Facilities.Rows);
            Assert.Empty(vm.Milestones.Rows);
            Assert.Equal(Game.Modes.SANDBOX, vm.Mode);
        }

        [Fact]
        public void Build_Mode_Science_HidesContractsAndStrategies()
        {
            // Regression: E2 — Science mode must hide Contracts and Strategies but keep
            // Facilities and Milestones populated. Fails if science-mode gating is missed.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-1", ut: 100.0, title: "Should Be Hidden"),
                Activate("Strat", ut: 100.0),
                Upgrade("LaunchPad", 2, ut: 100.0),
                Milestone("FirstLaunch", ut: 100.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 500.0,
                Game.Modes.SCIENCE_SANDBOX, c, s, f, m);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Strategies.CurrentRows);
            // Facilities and Milestones still render.
            Assert.Equal(9, vm.Facilities.Rows.Count);
            Assert.Single(vm.Milestones.Rows);
        }

        [Fact]
        public void Build_Facilities_EmptyLedger_AllNineAtLevel1()
        {
            // Regression: E3 strengthened — an empty ledger must produce the nine
            // stock facilities in FACILITY_DISPLAY_ORDER at L1 not-destroyed so the
            // fresh-career view isn't blank.
            var (c, s, f, m) = Modules();

            var vm = CareerStateWindowUI.Build(new List<GameAction>(), 0.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(9, vm.Facilities.Rows.Count);
            Assert.Equal(new[]
            {
                "VehicleAssemblyBuilding", "SpaceplaneHangar", "LaunchPad", "Runway",
                "Administration", "MissionControl", "TrackingStation",
                "ResearchAndDevelopment", "AstronautComplex"
            }, vm.Facilities.Rows.Select(r => r.FacilityId).ToArray());
            Assert.All(vm.Facilities.Rows, r =>
            {
                Assert.Equal(1, r.CurrentLevel);
                Assert.False(r.CurrentDestroyed);
            });
        }

        [Fact]
        public void Build_MissionControlLevel_MultipleFutureUpgrades_CurrentEchoesLiveUTLevel()
        {
            // Regression: E6 — two FacilityUpgrade actions for MissionControl at UT 100
            // (→L2) and UT 200 (→L3), liveUT=150. Current MissionControlLevel=2,
            // CurrentMaxSlots=GetContractSlots(2)=7; projected level=3, ProjectedMaxSlots=999.
            // Fails if projections leak into current.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Upgrade("MissionControl", 2, ut: 100.0),
                Upgrade("MissionControl", 3, ut: 200.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(2, vm.Contracts.MissionControlLevel);
            Assert.Equal(3, vm.Contracts.ProjectedMissionControlLevel);
            Assert.Equal(7, vm.Contracts.CurrentMaxSlots);
            Assert.Equal(999, vm.Contracts.ProjectedMaxSlots);
        }

        [Fact]
        public void Build_Facilities_DestroyAndRepairBothInFuture()
        {
            // Regression: E7 — destroy at UT 100, repair at UT 200, liveUT=50. Current
            // not-destroyed, projected not-destroyed, HasUpcomingChange=false. Fails if
            // the walk processes future actions into the current snapshot.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Destroy("LaunchPad", ut: 100.0),
                Repair("LaunchPad", ut: 200.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 50.0,
                Game.Modes.CAREER, c, s, f, m);

            var pad = vm.Facilities.Rows.First(r => r.FacilityId == "LaunchPad");
            Assert.False(pad.CurrentDestroyed);
            Assert.False(pad.ProjectedDestroyed);
            Assert.False(pad.HasUpcomingChange);
        }

        [Fact]
        public void Build_NullModules_ReturnsEmptyVMWithWarn()
        {
            // Regression: E12 — cold-start race where LedgerOrchestrator modules aren't
            // initialized. Build() must return an empty VM and emit a Warn, not NRE.
            var vm = CareerStateWindowUI.Build(
                new List<GameAction>(),
                0.0,
                Game.Modes.CAREER,
                contracts: null,
                strategies: null,
                facilities: null,
                milestones: null);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Facilities.Rows);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[UI]")
                && l.Contains("null module or actions"));
        }

        [Fact]
        public void Build_ContractTitleLookup_Throws_FallsBackToId()
        {
            // Regression: E13 — contract title lookup throws (KSP bug or mod
            // interference). The walk must catch and fall back to raw id with a
            // Verbose log. Fails if the lookup path isn't wrapped in try/catch.
            CareerStateWindowUI.ContractTitleLookupForTesting =
                _ => throw new InvalidOperationException("boom");
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-crash", ut: 100.0, title: null)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Contracts.CurrentRows);
            Assert.Equal("ctr-crash", vm.Contracts.CurrentRows[0].DisplayTitle);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("contract title")
                && l.Contains("id=ctr-crash"));
        }

        // ──────────────────────────────────────────────────────────────────
        // Humanization helper — SpaceBeforeCapitals (design doc §4.4, §8.2)
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void SpaceBeforeCapitals_ExpandsPascalCase()
        {
            // Regression: fails if the helper loses words or inserts extra spaces
            // between consecutive words in a PascalCase identifier.
            Assert.Equal("First Mun Flyby",
                CareerStateWindowUI.SpaceBeforeCapitals("FirstMunFlyby"));
            Assert.Equal("First Launch",
                CareerStateWindowUI.SpaceBeforeCapitals("FirstLaunch"));
        }

        [Fact]
        public void SpaceBeforeCapitals_PreservesAcronymsAndNullInput()
        {
            // Regression: fails if runs of consecutive capitals collapse (e.g. VAB
            // becomes "V A B") or if null/empty input throws. The rule "space only
            // when prev is not uppercase" protects acronyms.
            Assert.Equal("VAB", CareerStateWindowUI.SpaceBeforeCapitals("VAB"));
            Assert.Equal("SPH", CareerStateWindowUI.SpaceBeforeCapitals("SPH"));
            Assert.Null(CareerStateWindowUI.SpaceBeforeCapitals(null));
            Assert.Equal("", CareerStateWindowUI.SpaceBeforeCapitals(""));
        }

        // ──────────────────────────────────────────────────────────────────
        // §8.2 Formatting tests (Phase 2)
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void FormatContractRow_WithDeadline_ShowsDeadline()
        {
            // Regression: fails if the deadline is dropped from the formatted row
            // or if F0 formatting is replaced with a locale-dependent call.
            var row = new CareerStateWindowUI.ContractRow
            {
                ContractId = "ctr-1",
                DisplayTitle = "Explore Mun",
                AcceptUT = 104230.0,
                DeadlineUT = 240000.0,
                IsPendingAccept = false
            };

            string s = CareerStateWindowUI.FormatContractRow(row);

            Assert.Contains("Explore Mun", s);
            Assert.Contains("accepted UT 104230", s);
            Assert.Contains("deadline UT 240000", s);
            Assert.DoesNotContain("(pending)", s);
            Assert.DoesNotContain("--", s);
        }

        [Fact]
        public void FormatContractRow_NaNDeadline_ShowsDoubleDash()
        {
            // Regression: fails if NaN silently renders as "NaN" or is dropped
            // rather than replaced with "(deadline --)".
            var row = new CareerStateWindowUI.ContractRow
            {
                ContractId = "ctr-1",
                DisplayTitle = "Rescue Kerbal",
                AcceptUT = 118900.0,
                DeadlineUT = double.NaN,
                IsPendingAccept = false
            };

            string s = CareerStateWindowUI.FormatContractRow(row);

            Assert.Contains("Rescue Kerbal", s);
            Assert.Contains("(deadline --)", s);
            Assert.DoesNotContain("NaN", s);
        }

        [Fact]
        public void FormatContractRow_PendingAccept_IncludesPendingTag()
        {
            // Regression: fails if IsPendingAccept=true is silently elided from
            // the output — pending entries must be visually distinguishable.
            var row = new CareerStateWindowUI.ContractRow
            {
                ContractId = "ctr-future",
                DisplayTitle = "Future Contract",
                AcceptUT = 500.0,
                DeadlineUT = double.NaN,
                IsPendingAccept = true
            };

            string s = CareerStateWindowUI.FormatContractRow(row);

            Assert.Contains("(pending)", s);
        }

        [Fact]
        public void FormatStrategyRow_ShowsSourceTargetAndCommitment()
        {
            // Regression: fails if the resource direction swaps (Source↔Target)
            // or if the commitment percentage loses InvariantCulture (e.g. on a
            // comma-locale system "12.5%" becomes "12,5%").
            var row = new CareerStateWindowUI.StrategyRow
            {
                StrategyId = "Subsidy",
                DisplayTitle = "Subsidy",
                ActivateUT = 150.0,
                SourceResource = StrategyResource.Funds,
                TargetResource = StrategyResource.Science,
                Commitment = 0.125f,
                IsPendingActivate = false
            };

            string s = CareerStateWindowUI.FormatStrategyRow(row);

            Assert.Contains("Subsidy", s);
            Assert.Contains("activated UT 150", s);
            Assert.Contains("Funds -> Science", s);
            Assert.Contains("12.5%", s);
            Assert.DoesNotContain(",5%", s);
            Assert.DoesNotContain("(pending)", s);
        }

        [Fact]
        public void FormatFacilityRow_UpcomingChange_ShowsArrow()
        {
            // Regression: fails if an upcoming level change collapses to a
            // single "L2" label instead of showing "L2 -> L3 (upcoming)".
            var row = new CareerStateWindowUI.FacilityRow
            {
                FacilityId = "LaunchPad",
                DisplayTitle = "Launch Pad",
                CurrentLevel = 2,
                ProjectedLevel = 3,
                CurrentDestroyed = false,
                ProjectedDestroyed = false,
                HasUpcomingChange = true
            };

            string s = CareerStateWindowUI.FormatFacilityRow(row);

            Assert.Contains("Launch Pad", s);
            Assert.Contains("L2 -> L3", s);
            Assert.Contains("(upcoming)", s);
        }

        [Fact]
        public void FormatFacilityRow_DestroyedWithRepairPending_ShowsBothTags()
        {
            // Regression: fails if the combined "destroyed + repair pending"
            // state collapses to just "(destroyed)" — the projected-clears-destroyed
            // case must show the repair signal.
            var row = new CareerStateWindowUI.FacilityRow
            {
                FacilityId = "Runway",
                DisplayTitle = "Runway",
                CurrentLevel = 1,
                ProjectedLevel = 1,
                CurrentDestroyed = true,
                ProjectedDestroyed = false,
                HasUpcomingChange = true
            };

            string s = CareerStateWindowUI.FormatFacilityRow(row);

            Assert.Contains("Runway", s);
            Assert.Contains("destroyed", s);
            Assert.Contains("repair pending", s);
        }

        [Fact]
        public void FormatMilestoneRow_ZeroRewards_ElidesSuffix()
        {
            // Regression: fails if zero rewards still render ("+ 0 sci" clutter)
            // instead of being elided per design doc E8.
            var row = new CareerStateWindowUI.MilestoneRow
            {
                MilestoneId = "FirstLaunch",
                DisplayTitle = "First Launch",
                CreditedUT = 8230.0,
                FundsAwarded = 10000f,
                RepAwarded = 5f,
                ScienceAwarded = 0f,
                IsPendingCredit = false
            };

            string s = CareerStateWindowUI.FormatMilestoneRow(row);

            Assert.Contains("UT 8230", s);
            Assert.Contains("First Launch", s);
            Assert.Contains("+ 10000 funds", s);
            Assert.Contains("+ 5 rep", s);
            Assert.DoesNotContain("sci", s);
            Assert.DoesNotContain("(pending)", s);
        }

        [Fact]
        public void FormatMilestoneRow_PendingCredit_ShowsPendingTag()
        {
            // Regression: fails if IsPendingCredit=true doesn't surface the
            // "(pending)" suffix — players must see which milestones are
            // projected-not-yet-credited.
            var row = new CareerStateWindowUI.MilestoneRow
            {
                MilestoneId = "FirstOrbit",
                DisplayTitle = "First Orbit",
                CreditedUT = 118900.0,
                FundsAwarded = 0f,
                RepAwarded = 0f,
                ScienceAwarded = 0f,
                IsPendingCredit = true
            };

            string s = CareerStateWindowUI.FormatMilestoneRow(row);

            Assert.Contains("First Orbit", s);
            Assert.Contains("(pending)", s);
        }

        // ──────────────────────────────────────────────────────────────────
        // §8.4 Cache test (Phase 2)
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void InvalidateCache_NullsCachedVM()
        {
            // Regression: fails if InvalidateCache no longer nulls the cached VM —
            // the next DrawIfOpen would reuse stale data. Mirrors
            // KerbalsWindowUITests.InvalidateCache_DoesNotClearFoldedKerbals.
            // ParsekUI ctor takes no args; we only need the field-level access.
            var ui = new CareerStateWindowUI(parentUI: null);
            ui.CachedVMForTesting = new CareerStateWindowUI.CareerStateViewModel
            {
                Mode = Game.Modes.CAREER,
                LiveUT = 123.0,
                TerminalUT = 123.0,
                HasDivergence = false
            };

            Assert.NotNull(ui.CachedVMForTesting);

            ui.InvalidateCache();

            Assert.Null(ui.CachedVMForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("CareerStateWindow: cache invalidated"));
        }

        // ──────────────────────────────────────────────────────────────────
        // Tab switching & mode-render log-assertion tests (Phase 2)
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void TabSwitch_Logs_OldAndNew()
        {
            // Regression: fails if the SwitchTab helper stops logging the old and
            // new indices — diagnostics lose the tab-change trail.
            CareerStateWindowUI.SwitchTab(oldTab: 1, newTab: 3);

            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("CareerStateWindow: tab switched")
                && l.Contains("1->3"));
        }

        [Fact]
        public void ModeRender_SandboxLogsOncePerChange()
        {
            // Regression: fails if the rate-limit sentinel breaks and Sandbox
            // mode starts spamming its log every frame (or never logs at all).
            Game.Modes last = Game.Modes.CAREER;
            CareerStateWindowUI.LogModeRender(Game.Modes.SANDBOX, ref last);
            CareerStateWindowUI.LogModeRender(Game.Modes.SANDBOX, ref last);
            CareerStateWindowUI.LogModeRender(Game.Modes.SANDBOX, ref last);

            int matches = logLines.Count(l =>
                l.Contains("[UI]") && l.Contains("rendered sandbox-empty state"));
            Assert.Equal(1, matches);
            Assert.Equal(Game.Modes.SANDBOX, last);
        }

        [Fact]
        public void ModeRender_ScienceLogsOncePerChange()
        {
            // Regression: fails if the Science branch log stops firing or if the
            // rate-limit sentinel swallows mode flips (CAREER -> SCIENCE -> CAREER
            // -> SCIENCE must log twice).
            Game.Modes last = Game.Modes.CAREER;
            CareerStateWindowUI.LogModeRender(Game.Modes.SCIENCE_SANDBOX, ref last);
            CareerStateWindowUI.LogModeRender(Game.Modes.SCIENCE_SANDBOX, ref last);
            CareerStateWindowUI.LogModeRender(Game.Modes.CAREER, ref last);
            CareerStateWindowUI.LogModeRender(Game.Modes.SCIENCE_SANDBOX, ref last);

            int matches = logLines.Count(l =>
                l.Contains("[UI]")
                && l.Contains("rendered science-mode")
                && l.Contains("contracts/strategies hidden"));
            Assert.Equal(2, matches);
        }
    }
}
