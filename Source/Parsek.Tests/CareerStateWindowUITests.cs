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
        // Save/restore the global SuppressLogging flag so this fixture doesn't leak
        // state into later Sequential suites (TryAppendCapturedToTreeTests etc. do
        // not set SuppressLogging and depend on whatever the prior test left behind).
        private readonly bool savedSuppressLogging;

        public CareerStateWindowUITests()
        {
            savedSuppressLogging = ParsekLog.SuppressLogging;
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
            ParsekLog.SuppressLogging = savedSuppressLogging;
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        // A deterministic stand-in for KSPUtil.PrintDateCompact: "D<ut>".
        private static string FakeDate(double ut)
            => "D" + ut.ToString("F0", CultureInfo.InvariantCulture);

        private static (ContractsModule, StrategiesModule, FacilitiesModule, MilestonesModule) Modules()
        {
            return (new ContractsModule(), new StrategiesModule(),
                    new FacilitiesModule(), new MilestonesModule());
        }

        private static CareerStateWindowUI.CareerStateViewModel CachedVm(
            double liveUT,
            double nextRelevantActionUT = double.PositiveInfinity,
            Game.Modes mode = Game.Modes.CAREER,
            bool isTransientFallback = false)
        {
            return new CareerStateWindowUI.CareerStateViewModel
            {
                Mode = mode,
                LiveUT = liveUT,
                TerminalUT = liveUT,
                NextRelevantActionUT = nextRelevantActionUT,
                HasDivergence = false,
                IsTransientFallback = isTransientFallback
            };
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
            Assert.True(double.IsPositiveInfinity(vm.NextRelevantActionUT));
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
        public void Build_Contracts_ClosingByTimelineEnd_FlaggedInCurrentRow()
        {
            // Regression: without IsClosingByTimelineEnd, a contract active now but
            // completed before terminal UT would only disappear from ProjectedRows,
            // leaving the user with no on-screen indication it will wind down.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-closing", ut: 100.0, title: "Explore Mun"),
                Complete("ctr-closing", ut: 300.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Contracts.CurrentRows);
            Assert.True(vm.Contracts.CurrentRows[0].IsClosingByTimelineEnd);
            Assert.False(vm.Contracts.CurrentRows[0].IsPendingAccept);
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Completed,
                vm.Contracts.CurrentRows[0].EndKind);
            Assert.Equal(300.0, vm.Contracts.CurrentRows[0].EndUT);
            Assert.Equal("completes D300",
                CareerStateWindowUI.FormatContractRow_TimelineEnd(
                    vm.Contracts.CurrentRows[0], FakeDate));
        }

        [Fact]
        public void Build_Strategies_ClosingByTimelineEnd_FlaggedInCurrentRow()
        {
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Activate("strat-closing", ut: 100.0),
                Deactivate("strat-closing", ut: 300.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Strategies.CurrentRows);
            Assert.True(vm.Strategies.CurrentRows[0].IsClosingByTimelineEnd);
            Assert.False(vm.Strategies.CurrentRows[0].IsPendingActivate);
            Assert.Equal("deactivates D300",
                CareerStateWindowUI.FormatStrategyRow_TimelineEnd(
                    vm.Strategies.CurrentRows[0], FakeDate));
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
                // A FRESH id, so the AlreadyCredited arm below the Effective guard cannot
                // catch it: only the Effective guard keeps the second row out.
                Milestone("GhostMilestone", ut: 200.0, effective: false, funds: 500f)
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
            // Per LedgerOrchestrator.UpdateSlotLimitsFromFacilities, production
            // facility-upgrade events use KSP's SpaceCenter/<id> keys.
            // contracts slots derive from MissionControl level; strategies slots derive from
            // Administration level. Upgrading Administration alone must NOT bump contract slots
            // and vice versa — an earlier Phase-1 revision had this crossed.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                // DIFFERENT levels, so a crossed key read changes both the echoed level
                // and the slot count on each tab.
                Upgrade("SpaceCenter/MissionControl", 2, ut: 50.0),
                Upgrade("SpaceCenter/Administration", 3, ut: 100.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(2, vm.Contracts.MissionControlLevel);
            Assert.Equal(2, vm.Contracts.ProjectedMissionControlLevel);
            // GetContractSlots(2) == 7 per LedgerOrchestrator.cs:1462.
            Assert.Equal(7, vm.Contracts.CurrentMaxSlots);
            Assert.Equal(7, vm.Contracts.ProjectedMaxSlots);

            Assert.Equal(3, vm.Strategies.AdminLevel);
            Assert.Equal(3, vm.Strategies.ProjectedAdminLevel);
            // GetStrategySlots(3) == 5.
            Assert.Equal(5, vm.Strategies.CurrentMaxSlots);
            Assert.Equal(5, vm.Strategies.ProjectedMaxSlots);
        }

        [Fact]
        public void Build_FacilityUpgrade_ProductionSpaceCenterIdDisplaysOnStockRow()
        {
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Upgrade("SpaceCenter/LaunchPad", 2, ut: 100.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s, f, m);

            var pad = vm.Facilities.Rows.First(r => r.FacilityId == "LaunchPad");
            Assert.Equal(2, pad.CurrentLevel);
            Assert.Equal(2, pad.ProjectedLevel);
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

        [Fact]
        public void Build_NextRelevantActionUT_TracksFirstFutureProjectedAction()
        {
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-now", ut: 100.0, title: "Explore Mun"),
                Complete("ctr-now", ut: 123.75),
                Accept("ctr-later", ut: 200.0, title: "Rescue Kerbal")
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 123.4,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(123.75, vm.NextRelevantActionUT);
        }

        [Fact]
        public void Build_NextRelevantActionUT_ScienceSandbox_IgnoresHiddenCareerActions()
        {
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-hidden", ut: 123.4, title: "Hidden Contract"),
                Upgrade("MissionControl", toLevel: 2, ut: 150.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 100.0,
                Game.Modes.SCIENCE_SANDBOX, c, s, f, m);

            Assert.Equal(150.0, vm.NextRelevantActionUT);
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
        // Timeline-end outcomes, pending groups, facility ids (round 3)
        // ──────────────────────────────────────────────────────────────────

        private static GameAction ContractEnd(GameActionType type, string contractId, double ut)
        {
            return new GameAction { Type = type, UT = ut, ContractId = contractId, Effective = true };
        }

        [Fact]
        public void Build_Contracts_EachEndingKeepsItsOwnOutcomeAndUT()
        {
            // catches: a future FAILURE rendering like a completion (the old "(closing)").
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("done", ut: 10.0, title: "Done"),
                Accept("fail", ut: 20.0, title: "Fail"),
                Accept("drop", ut: 30.0, title: "Drop"),
                Accept("keep", ut: 40.0, title: "Keep"),
                ContractEnd(GameActionType.ContractComplete, "done", 300.0),
                ContractEnd(GameActionType.ContractFail, "fail", 400.0),
                ContractEnd(GameActionType.ContractCancel, "drop", 500.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            var byId = vm.Contracts.CurrentRows.ToDictionary(r => r.ContractId);
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Completed, byId["done"].EndKind);
            Assert.Equal(300.0, byId["done"].EndUT);
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Failed, byId["fail"].EndKind);
            Assert.Equal(400.0, byId["fail"].EndUT);
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Cancelled, byId["drop"].EndKind);
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.None, byId["keep"].EndKind);
            Assert.False(byId["keep"].IsClosingByTimelineEnd);
            Assert.Equal("FAILS D400",
                CareerStateWindowUI.FormatContractRow_TimelineEnd(byId["fail"], FakeDate));
            Assert.Equal("", CareerStateWindowUI.FormatContractRow_TimelineEnd(byId["keep"], FakeDate));
        }

        [Fact]
        public void Build_Contracts_PendingRows_IncludeContractsAcceptedAndClosedInTheFuture()
        {
            // catches: a contract the recorded future both accepts and completes being in
            // NEITHER the current nor the terminal set, and so on no row at all.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("now", ut: 100.0, title: "Now"),
                Accept("later", ut: 300.0, title: "Later"),
                Accept("brief", ut: 350.0, title: "Brief"),
                ContractEnd(GameActionType.ContractComplete, "brief", 380.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(new[] { "now" }, vm.Contracts.CurrentRows.Select(r => r.ContractId));
            Assert.Equal(new[] { "now", "later" }, vm.Contracts.ProjectedRows.Select(r => r.ContractId));
            Assert.Equal(new[] { "later", "brief" }, vm.Contracts.PendingRows.Select(r => r.ContractId));
            Assert.All(vm.Contracts.PendingRows, r => Assert.True(r.IsPendingAccept));
            var brief = vm.Contracts.PendingRows.Single(r => r.ContractId == "brief");
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Completed, brief.EndKind);
            Assert.Equal(380.0, brief.EndUT);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_Divergence_ClosingPlusPendingWithEqualCounts()
        {
            // catches: divergence keyed only on active COUNTS, which one closing plus one
            // pending contract leave equal (1 now, 1 at the end) - the banner then hid the
            // timeline end although both rows change.
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("old", ut: 100.0),
                ContractEnd(GameActionType.ContractComplete, "old", 300.0),
                Accept("new", ut: 400.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(vm.Contracts.CurrentActive, vm.Contracts.ProjectedActive);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_Contracts_EndingBeforeLiveUT_LeavesNoRow()
        {
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Accept("past", ut: 100.0),
                ContractEnd(GameActionType.ContractFail, "past", 150.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.PendingRows);
            Assert.False(vm.HasDivergence);
        }

        [Fact]
        public void Build_Strategies_PendingRowsAndDeactivation()
        {
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Activate("now", ut: 100.0),
                Activate("later", ut: 300.0),
                Deactivate("later", ut: 350.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Single(vm.Strategies.CurrentRows);
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.None, vm.Strategies.CurrentRows[0].EndKind);
            var later = Assert.Single(vm.Strategies.PendingRows);
            Assert.Equal("later", later.StrategyId);
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Deactivated, later.EndKind);
            Assert.Equal("deactivates D350",
                CareerStateWindowUI.FormatStrategyRow_TimelineEnd(later, FakeDate));
        }

        [Theory]
        [InlineData("SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_launchPad", "LaunchPad")]
        [InlineData("SpaceCenter/VehicleAssemblyBuilding/Facility/mainBuilding", "VehicleAssemblyBuilding")]
        [InlineData("SpaceCenter/Runway", "Runway")]
        [InlineData("Runway", "Runway")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void FacilityIdForBuilding_ReducesABuildingIdToItsFacility(string buildingId, string expected)
        {
            Assert.Equal(expected, CareerStateWindowUI.FacilityIdForBuilding(buildingId));
        }

        [Fact]
        public void Build_Facilities_ProductionDestructibleIdsReachTheFacilityRow()
        {
            // catches: destruction keyed by the raw DestructibleBuilding id
            // ("SpaceCenter/LaunchPad/Facility/..."), which never matched a facility row, so
            // a destroyed building never showed as destroyed.
            var (c, s, f, m) = Modules();
            const string tank = "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/Tank";
            const string tower = "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/Tower";
            var actions = new List<GameAction>
            {
                Destroy(tank, ut: 100.0),
                Destroy(tower, ut: 110.0),
                // Only one of the two buildings is repaired in the recorded future: the
                // facility stays destroyed at the timeline end.
                Repair(tank, ut: 300.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            var pad = vm.Facilities.Rows.Single(r => r.FacilityId == "LaunchPad");
            Assert.True(pad.CurrentDestroyed);
            Assert.True(pad.ProjectedDestroyed);
            Assert.Equal("L1 (destroyed)", CareerStateWindowUI.FormatFacilityRow_Level(pad));
        }

        [Fact]
        public void Build_Facilities_RecordTheUTOfEachFutureChange()
        {
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Upgrade("SpaceCenter/LaunchPad", 2, ut: 100.0),
                Upgrade("SpaceCenter/LaunchPad", 3, ut: 400.0),
                Destroy("SpaceCenter/Runway/Facility/x", ut: 450.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, f, m);

            var pad = vm.Facilities.Rows.Single(r => r.FacilityId == "LaunchPad");
            Assert.Equal("upgrades to L3, D400",
                CareerStateWindowUI.FormatFacilityRow_TimelineEnd(pad, true, FakeDate));
            var runway = vm.Facilities.Rows.Single(r => r.FacilityId == "Runway");
            Assert.Equal("destroyed D450",
                CareerStateWindowUI.FormatFacilityRow_TimelineEnd(runway, true, FakeDate));
        }

        [Fact]
        public void Build_Milestones_UseTheTimelineTitleShape()
        {
            // catches: "Kerbin/ Science" (SpaceBeforeCapitals does not treat the slash as
            // a word break).
            var (c, s, f, m) = Modules();
            var actions = new List<GameAction>
            {
                Milestone("Kerbin/Science", ut: 10.0),
                Milestone("Minmus/ReturnFromFlyBy", ut: 20.0),
                Milestone("FirstLaunch", ut: 30.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 100.0,
                Game.Modes.CAREER, c, s, f, m);

            Assert.Equal(
                new[] { "Kerbin - Science", "Minmus - Return From Fly By", "First Launch" },
                vm.Milestones.Rows.Select(r => r.DisplayTitle));
            Assert.DoesNotContain(vm.Milestones.Rows, r => r.DisplayTitle.Contains("/ "));
        }

        [Fact]
        public void Build_FacilityNames_ComeFromTheStockLookup()
        {
            CareerStateWindowUI.FacilityNameLookupForTesting =
                id => id == "LaunchPad" ? "Launchpad" : null;
            try
            {
                var (c, s, f, m) = Modules();
                var vm = CareerStateWindowUI.Build(new List<GameAction>(), 0.0,
                    Game.Modes.CAREER, c, s, f, m);

                Assert.Equal("Launchpad",
                    vm.Facilities.Rows.Single(r => r.FacilityId == "LaunchPad").DisplayTitle);
                // No stock answer -> the humanized id, with stock's lowercase conjunction.
                Assert.Equal("Research and Development",
                    vm.Facilities.Rows.Single(r => r.FacilityId == "ResearchAndDevelopment").DisplayTitle);
            }
            finally
            {
                CareerStateWindowUI.FacilityNameLookupForTesting = null;
            }
        }

        [Fact]
        public void ResolveFacilityDisplayName_IgnoresAnUnresolvedLocalizationTag()
        {
            CareerStateWindowUI.FacilityNameLookupForTesting = id => "#autoLOC_6001646";
            try
            {
                Assert.Equal("Research and Development",
                    CareerStateWindowUI.ResolveFacilityDisplayName("ResearchAndDevelopment"));
            }
            finally
            {
                CareerStateWindowUI.FacilityNameLookupForTesting = null;
            }
        }

        [Theory]
        [InlineData("ResearchAndDevelopment", "Research and Development")]
        [InlineData("VehicleAssemblyBuilding", "Vehicle Assembly Building")]
        [InlineData("MissionControl", "Mission Control")]
        public void HumanizeFacilityId_SplitsPascalCaseWithALowercaseAnd(string id, string expected)
        {
            Assert.Equal(expected, CareerStateWindowUI.HumanizeFacilityId(id));
        }

        // ──────────────────────────────────────────────────────────────────
        // Pure formatters (round 3: dates, relative tails, outcomes)
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void FormatDateCell_NaNIsDoubleDash_NullFormatterFallsBackToSeconds()
        {
            Assert.Equal("--", CareerStateWindowUI.FormatDateCell(double.NaN, FakeDate));
            Assert.Equal("D42", CareerStateWindowUI.FormatDateCell(42.0, FakeDate));
            Assert.Equal("42", CareerStateWindowUI.FormatDateCell(42.0, null));
            Assert.Equal("--", CareerStateWindowUI.FormatDateCell(42.0, ut => ""));
        }

        [Fact]
        public void FormatRelativeTail_UsesTheLargestUnitOfThePlayersCalendar()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
            try
            {
                const double day = 21600.0;
                Assert.Equal("(in 12d)",
                    CareerStateWindowUI.FormatRelativeTail(1000.0 + 12 * day + 3 * 3600, 1000.0));
                Assert.Equal("(overdue 3d)",
                    CareerStateWindowUI.FormatRelativeTail(1000.0, 1000.0 + 3 * day + 60));
                Assert.Equal("(in 5h)",
                    CareerStateWindowUI.FormatRelativeTail(5 * 3600 + 120, 0.0));
                // Exactly at the deadline counts as passed.
                Assert.Equal("(overdue 0s)", CareerStateWindowUI.FormatRelativeTail(50.0, 50.0));
            }
            finally
            {
                ParsekTimeFormat.KerbinTimeOverrideForTesting = null;
            }
        }

        [Fact]
        public void FormatContractRow_Deadline_ShowsDateAndRelativeTail()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
            try
            {
                var row = new CareerStateWindowUI.ContractRow { DeadlineUT = 21600.0 * 2 };
                Assert.Equal("D43200 (in 2d)",
                    CareerStateWindowUI.FormatContractRow_Deadline(row, 0.0, FakeDate));
                Assert.False(CareerStateWindowUI.IsDeadlineOverdue(row, 0.0));

                Assert.Equal("D43200 (overdue 1d)",
                    CareerStateWindowUI.FormatContractRow_Deadline(row, 21600.0 * 3, FakeDate));
                Assert.True(CareerStateWindowUI.IsDeadlineOverdue(row, 21600.0 * 3));

                var none = new CareerStateWindowUI.ContractRow { DeadlineUT = double.NaN };
                Assert.Equal("--", CareerStateWindowUI.FormatContractRow_Deadline(none, 0.0, FakeDate));
                Assert.False(CareerStateWindowUI.IsDeadlineOverdue(none, 1e9));
            }
            finally
            {
                ParsekTimeFormat.KerbinTimeOverrideForTesting = null;
            }
        }

        [Theory]
        [InlineData((int)CareerStateWindowUI.TimelineEndKind.None, "", false)]
        [InlineData((int)CareerStateWindowUI.TimelineEndKind.Completed, "completes D7", false)]
        [InlineData((int)CareerStateWindowUI.TimelineEndKind.Failed, "FAILS D7", true)]
        [InlineData((int)CareerStateWindowUI.TimelineEndKind.Cancelled, "cancelled D7", false)]
        [InlineData((int)CareerStateWindowUI.TimelineEndKind.Deactivated, "deactivates D7", false)]
        public void FormatTimelineEnd_NamesTheOutcomeAndItsDate(
            int kindValue, string expected, bool alert)
        {
            var kind = (CareerStateWindowUI.TimelineEndKind)kindValue;
            Assert.Equal(expected, CareerStateWindowUI.FormatTimelineEnd(kind, 7.0, FakeDate));
            Assert.Equal(alert, CareerStateWindowUI.IsTimelineEndAlert(kind));
        }

        [Fact]
        public void ContractEndKindFor_MapsTheThreeClosingActions()
        {
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Completed,
                CareerStateWindowUI.ContractEndKindFor(GameActionType.ContractComplete));
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Failed,
                CareerStateWindowUI.ContractEndKindFor(GameActionType.ContractFail));
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Cancelled,
                CareerStateWindowUI.ContractEndKindFor(GameActionType.ContractCancel));
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.None,
                CareerStateWindowUI.ContractEndKindFor(GameActionType.ContractAccept));
        }

        [Fact]
        public void FormatFacilityRow_TimelineEnd_CoversEveryChange()
        {
            var up = new CareerStateWindowUI.FacilityRow
            {
                CurrentLevel = 1, ProjectedLevel = 2, LevelChangeUT = 40.0
            };
            Assert.Equal("upgrades to L2, D40",
                CareerStateWindowUI.FormatFacilityRow_TimelineEnd(up, true, FakeDate));
            // Levels are not shown in Science mode, so a level change says nothing there.
            Assert.Equal("", CareerStateWindowUI.FormatFacilityRow_TimelineEnd(up, false, FakeDate));
            Assert.True(CareerStateWindowUI.FacilityRowChanges(up, true));
            Assert.False(CareerStateWindowUI.FacilityRowChanges(up, false));

            var repaired = new CareerStateWindowUI.FacilityRow
            {
                CurrentLevel = 2, ProjectedLevel = 2,
                CurrentDestroyed = true, ProjectedDestroyed = false, DestroyedChangeUT = 90.0
            };
            Assert.Equal("repaired D90",
                CareerStateWindowUI.FormatFacilityRow_TimelineEnd(repaired, false, FakeDate));

            var both = new CareerStateWindowUI.FacilityRow
            {
                CurrentLevel = 1, ProjectedLevel = 3, LevelChangeUT = 40.0,
                ProjectedDestroyed = true, DestroyedChangeUT = 90.0
            };
            Assert.Equal("upgrades to L3, D40; destroyed D90",
                CareerStateWindowUI.FormatFacilityRow_TimelineEnd(both, true, FakeDate));

            var still = new CareerStateWindowUI.FacilityRow { CurrentLevel = 2, ProjectedLevel = 2 };
            Assert.Equal("", CareerStateWindowUI.FormatFacilityRow_TimelineEnd(still, true, FakeDate));
        }

        [Fact]
        public void FormatFacilityRow_LevelAndState()
        {
            var ok = new CareerStateWindowUI.FacilityRow { CurrentLevel = 2 };
            var down = new CareerStateWindowUI.FacilityRow { CurrentLevel = 3, CurrentDestroyed = true };
            Assert.Equal("L2", CareerStateWindowUI.FormatFacilityRow_Level(ok));
            Assert.Equal("L3 (destroyed)", CareerStateWindowUI.FormatFacilityRow_Level(down));
            Assert.Equal("intact", CareerStateWindowUI.FormatFacilityRow_State(ok));
            Assert.Equal("destroyed", CareerStateWindowUI.FormatFacilityRow_State(down));
        }

        // ──────────────────────────────────────────────────────────────────
        // Mode-appropriate tabs, banner, launcher (round 3)
        // ──────────────────────────────────────────────────────────────────

        private static CareerStateWindowUI.FacilitiesTabVM Facilities(params bool[] destroyed)
        {
            var tab = new CareerStateWindowUI.FacilitiesTabVM
            {
                Rows = new List<CareerStateWindowUI.FacilityRow>()
            };
            foreach (bool d in destroyed)
                tab.Rows.Add(new CareerStateWindowUI.FacilityRow { CurrentLevel = 1, CurrentDestroyed = d });
            return tab;
        }

        [Fact]
        public void VisibleTabsFor_EachMode()
        {
            Assert.Equal(new[] { 0, 1, 2, 3 },
                CareerStateWindowUI.VisibleTabsFor(Game.Modes.CAREER, Facilities(false)));
            // Science: no contracts, no strategies, no building levels. Facilities only
            // while a building is destroyed.
            Assert.Equal(new[] { CareerStateWindowUI.TabMilestones },
                CareerStateWindowUI.VisibleTabsFor(Game.Modes.SCIENCE_SANDBOX, Facilities(false, false)));
            Assert.Equal(new[] { CareerStateWindowUI.TabFacilities, CareerStateWindowUI.TabMilestones },
                CareerStateWindowUI.VisibleTabsFor(Game.Modes.SCIENCE_SANDBOX, Facilities(false, true)));
            Assert.Empty(CareerStateWindowUI.VisibleTabsFor(Game.Modes.SANDBOX, Facilities(true)));
            Assert.Empty(CareerStateWindowUI.VisibleTabsFor(Game.Modes.MISSION, Facilities()));
        }

        [Fact]
        public void VisibleTabsFor_ScienceCountsAPendingDestruction()
        {
            var tab = Facilities(false);
            var row = tab.Rows[0];
            row.ProjectedDestroyed = true;
            tab.Rows[0] = row;
            Assert.Contains(CareerStateWindowUI.TabFacilities,
                CareerStateWindowUI.VisibleTabsFor(Game.Modes.SCIENCE_SANDBOX, tab));
        }

        [Fact]
        public void FacilityRowVisible_ScienceListsOnlyDestroyedBuildings()
        {
            var ok = new CareerStateWindowUI.FacilityRow { CurrentLevel = 1 };
            var down = new CareerStateWindowUI.FacilityRow { CurrentLevel = 1, CurrentDestroyed = true };
            Assert.True(CareerStateWindowUI.FacilityRowVisible(ok, Game.Modes.CAREER));
            Assert.False(CareerStateWindowUI.FacilityRowVisible(ok, Game.Modes.SCIENCE_SANDBOX));
            Assert.True(CareerStateWindowUI.FacilityRowVisible(down, Game.Modes.SCIENCE_SANDBOX));
            Assert.False(CareerStateWindowUI.FacilityRowVisible(down, Game.Modes.SANDBOX));
        }

        [Fact]
        public void CoerceTab_KeepsADrawnTabAndOtherwiseTakesTheFirst()
        {
            int[] science = { CareerStateWindowUI.TabMilestones };
            Assert.Equal(CareerStateWindowUI.TabMilestones,
                CareerStateWindowUI.CoerceTab(CareerStateWindowUI.TabContracts, science));
            Assert.Equal(CareerStateWindowUI.TabMilestones,
                CareerStateWindowUI.CoerceTab(CareerStateWindowUI.TabMilestones, science));
            Assert.Equal(2, CareerStateWindowUI.CoerceTab(2, new[] { 0, 1, 2, 3 }));
            // A mode that draws no tabs keeps the stored selection for later.
            Assert.Equal(1, CareerStateWindowUI.CoerceTab(1, new int[0]));
        }

        [Fact]
        public void SelectedTabForTesting_CoercesAgainstTheCachedMode()
        {
            // catches: the seam writing tab=contracts in Science mode, reading it back as
            // applied, and photographing Milestones under a Contracts label.
            var ui = new CareerStateWindowUI(parentUI: null);
            ui.CachedVMForTesting = new CareerStateWindowUI.CareerStateViewModel
            {
                Mode = Game.Modes.SCIENCE_SANDBOX,
                Facilities = Facilities(false)
            };
            ui.SelectedTabForTesting = CareerStateWindowUI.TabContracts;
            Assert.Equal(CareerStateWindowUI.TabMilestones, ui.SelectedTabForTesting);

            // No cached VM yet: stored as written, the draw coerces it.
            var fresh = new CareerStateWindowUI(parentUI: null);
            fresh.SelectedTabForTesting = CareerStateWindowUI.TabStrategies;
            Assert.Equal(CareerStateWindowUI.TabStrategies, fresh.SelectedTabForTesting);
        }

        [Fact]
        public void ModeOffersLauncher_HidesCareerInSandbox()
        {
            Assert.True(CareerStateWindowUI.ModeOffersLauncher(Game.Modes.CAREER));
            Assert.True(CareerStateWindowUI.ModeOffersLauncher(Game.Modes.SCIENCE_SANDBOX));
            Assert.False(CareerStateWindowUI.ModeOffersLauncher(Game.Modes.SANDBOX));
            Assert.False(CareerStateWindowUI.ModeOffersLauncher(Game.Modes.MISSION));
            Assert.False(CareerStateWindowUI.ModeOffersLauncher(Game.Modes.MISSION_BUILDER));
        }

        [Fact]
        public void FormatModeBanner_DatesNotSeconds()
        {
            var vm = CachedVm(liveUT: 1000.0);
            vm.TerminalUT = 5000.0;
            Assert.Equal("Career mode - D1000", CareerStateWindowUI.FormatModeBanner(vm, FakeDate));

            vm.HasDivergence = true;
            Assert.Equal("Career mode - D1000  (timeline ends D5000)",
                CareerStateWindowUI.FormatModeBanner(vm, FakeDate));

            Assert.Equal("Science mode - no contracts, strategies or building levels",
                CareerStateWindowUI.FormatModeBanner(
                    CachedVm(1000.0, mode: Game.Modes.SCIENCE_SANDBOX), FakeDate));
            Assert.Equal("Sandbox mode - career state is not tracked",
                CareerStateWindowUI.FormatModeBanner(
                    CachedVm(1000.0, mode: Game.Modes.SANDBOX), FakeDate));
        }

        [Fact]
        public void CountPendingMilestones_CountsOnlyFutureCredits()
        {
            var rows = new List<CareerStateWindowUI.MilestoneRow>
            {
                new CareerStateWindowUI.MilestoneRow { IsPendingCredit = false },
                new CareerStateWindowUI.MilestoneRow { IsPendingCredit = true },
                new CareerStateWindowUI.MilestoneRow { IsPendingCredit = true },
            };
            Assert.Equal(2, CareerStateWindowUI.CountPendingMilestones(rows));
            Assert.Equal(0, CareerStateWindowUI.CountPendingMilestones(null));
        }

        [Fact]
        public void TheSeamPendingValuesMapToTheWindowsFoldKeys()
        {
            // The automation seam's `op=expand window=career key=pending:<tab>` names the
            // TAB; every wire value must resolve to one of the window's own fold keys.
            Assert.Equal(new[] { "contracts", "strategies", "milestones" },
                Parsek.TestCommands.TestCommandUiState.CareerPendingFoldValues);
            Assert.Equal(CareerStateWindowUI.GroupKey_MilestonesPending,
                Parsek.TestCommands.TestCommandUiState.CareerFoldKeyFor("milestones"));
            foreach (string v in Parsek.TestCommands.TestCommandUiState.CareerPendingFoldValues)
                Assert.Contains(Parsek.TestCommands.TestCommandUiState.CareerFoldKeyFor(v),
                    CareerStateWindowUI.FoldGroupKeys);
            Assert.Null(Parsek.TestCommands.TestCommandUiState.CareerFoldKeyFor("facilities"));
        }

        [Fact]
        public void FoldGroupKeys_CoverThePendingGroupOfEveryTabThatSplits()
        {
            Assert.Equal(
                new[]
                {
                    CareerStateWindowUI.GroupKey_ContractsPending,
                    CareerStateWindowUI.GroupKey_StrategiesPending,
                    CareerStateWindowUI.GroupKey_MilestonesPending,
                },
                CareerStateWindowUI.FoldGroupKeys);
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

        [Fact]
        public void ShouldRebuildCachedVM_NullCache_ReturnsTrue()
        {
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                cachedVM: null,
                currentMode: Game.Modes.CAREER,
                liveUT: 123.4));
        }

        [Fact]
        public void ShouldRebuildCachedVM_ModeChanged_ReturnsTrue()
        {
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.4, mode: Game.Modes.CAREER),
                currentMode: Game.Modes.SCIENCE_SANDBOX,
                liveUT: 123.5));
        }

        [Theory]
        [InlineData(123.4, "123")]
        [InlineData(123.5, "124")]
        [InlineData(124.5, "125")]
        [InlineData(125.5, "126")]
        [InlineData(-124.5, "-125")]
        public void GetDisplayedUtText_MatchesWindowF0Rounding(double liveUt, string expected)
        {
            Assert.Equal(expected, CareerStateWindowUI.GetDisplayedUtText(liveUt));
        }

        [Fact]
        public void GetDisplayedUtText_NonFiniteInput_DoNotThrow()
        {
            Assert.Equal(
                double.NaN.ToString("F0", CultureInfo.InvariantCulture),
                CareerStateWindowUI.GetDisplayedUtText(double.NaN));

            Assert.Equal(
                double.PositiveInfinity.ToString("F0", CultureInfo.InvariantCulture),
                CareerStateWindowUI.GetDisplayedUtText(double.PositiveInfinity));

            Assert.Equal(
                double.NegativeInfinity.ToString("F0", CultureInfo.InvariantCulture),
                CareerStateWindowUI.GetDisplayedUtText(double.NegativeInfinity));
        }

        [Fact]
        public void ShouldRebuildCachedVM_BeforeNextRelevantActionAndSameDisplayedUt_ReturnsFalse()
        {
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.1, nextRelevantActionUT: 123.45),
                currentMode: Game.Modes.CAREER,
                liveUT: 123.4));
        }

        [Fact]
        public void ShouldRebuildCachedVM_DisplayedUtChangesWithinSameActionGap_ReturnsTrue()
        {
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.4, nextRelevantActionUT: 200.0),
                currentMode: Game.Modes.CAREER,
                liveUT: 123.6));
        }

        [Fact]
        public void ShouldRebuildCachedVM_AtNextRelevantActionWithinSameDisplayedUt_ReturnsTrue()
        {
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.1, nextRelevantActionUT: 123.4),
                currentMode: Game.Modes.CAREER,
                liveUT: 123.4));
        }

        [Fact]
        public void ShouldRebuildCachedVM_TimeGoesBackwardWithinSameDisplayedUt_ReturnsTrue()
        {
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.4, nextRelevantActionUT: 200.0),
                currentMode: Game.Modes.CAREER,
                liveUT: 123.1));
        }

        [Fact]
        public void ShouldRebuildCachedVM_PositiveInfinitySentinelBoundary_ReturnsFalse()
        {
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(double.PositiveInfinity, nextRelevantActionUT: double.PositiveInfinity),
                currentMode: Game.Modes.CAREER,
                liveUT: double.PositiveInfinity));
        }

        [Fact]
        public void ShouldRebuildCachedVM_ScienceSandbox_DisplayedUtChangeWithinSameActionGap_ReturnsFalse()
        {
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.4, nextRelevantActionUT: 200.0, mode: Game.Modes.SCIENCE_SANDBOX),
                currentMode: Game.Modes.SCIENCE_SANDBOX,
                liveUT: 123.6));
        }

        [Fact]
        public void ShouldRebuildCachedVM_ScienceSandbox_VisibleActionBoundary_ReturnsTrue()
        {
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.1, nextRelevantActionUT: 123.4, mode: Game.Modes.SCIENCE_SANDBOX),
                currentMode: Game.Modes.SCIENCE_SANDBOX,
                liveUT: 123.4));
        }

        [Fact]
        public void ShouldRebuildCachedVM_Sandbox_ActionBoundary_ReturnsFalse()
        {
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.1, nextRelevantActionUT: 123.4, mode: Game.Modes.SANDBOX),
                currentMode: Game.Modes.SANDBOX,
                liveUT: 123.4));
        }

        [Fact]
        public void Build_NullModulesFallback_ForcesImmediateRetry()
        {
            var fallbackVm = CareerStateWindowUI.Build(
                actions: Array.Empty<GameAction>(),
                liveUT: 123.4,
                mode: Game.Modes.CAREER,
                contracts: null,
                strategies: null,
                facilities: null,
                milestones: null);

            Assert.True(fallbackVm.IsTransientFallback);
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                fallbackVm,
                currentMode: Game.Modes.CAREER,
                liveUT: 123.4));

            var (contracts, strategies, facilities, milestones) = Modules();
            var recoveredVm = CareerStateWindowUI.Build(
                actions: Array.Empty<GameAction>(),
                liveUT: 123.4,
                mode: Game.Modes.CAREER,
                contracts: contracts,
                strategies: strategies,
                facilities: facilities,
                milestones: milestones);

            Assert.False(recoveredVm.IsTransientFallback);
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
                recoveredVm,
                currentMode: Game.Modes.CAREER,
                liveUT: 123.4));
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

        // ──────────────────────────────────────────────────────────────────
        // Phase 2b per-column formatting helpers
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void FormatContractRow_Title_UsesDisplayTitleOrId()
        {
            // Regression: fails if the per-column title helper stops preferring
            // DisplayTitle over raw id, or if the empty/null fallback to
            // "(unknown)" is lost.
            var rowWithTitle = new CareerStateWindowUI.ContractRow
            {
                ContractId = "ctr-1", DisplayTitle = "Explore Mun"
            };
            var rowIdOnly = new CareerStateWindowUI.ContractRow
            {
                ContractId = "ctr-7", DisplayTitle = null
            };
            var rowBlank = new CareerStateWindowUI.ContractRow
            {
                ContractId = null, DisplayTitle = ""
            };

            Assert.Equal("Explore Mun", CareerStateWindowUI.FormatContractRow_Title(rowWithTitle));
            Assert.Equal("ctr-7", CareerStateWindowUI.FormatContractRow_Title(rowIdOnly));
            Assert.Equal("(unknown)", CareerStateWindowUI.FormatContractRow_Title(rowBlank));
        }

        [Fact]
        public void FormatStrategyRow_Title_UsesDisplayTitleOrId()
        {
            // Regression: mirrors the contract-title helper; fails if the
            // fallback chain (DisplayTitle → id → "(unknown)") is broken.
            var rowWithTitle = new CareerStateWindowUI.StrategyRow
            {
                StrategyId = "strat-1", DisplayTitle = "Subsidy"
            };
            var rowIdOnly = new CareerStateWindowUI.StrategyRow
            {
                StrategyId = "strat-7", DisplayTitle = null
            };
            var rowBlank = new CareerStateWindowUI.StrategyRow
            {
                StrategyId = null, DisplayTitle = ""
            };

            Assert.Equal("Subsidy", CareerStateWindowUI.FormatStrategyRow_Title(rowWithTitle));
            Assert.Equal("strat-7", CareerStateWindowUI.FormatStrategyRow_Title(rowIdOnly));
            Assert.Equal("(unknown)", CareerStateWindowUI.FormatStrategyRow_Title(rowBlank));
        }

        [Fact]
        public void FormatStrategyRow_Activate_ShowsDate()
        {
            // Regression: the cell is the house date for the activation UT.
            var row = new CareerStateWindowUI.StrategyRow { ActivateUT = 150.0 };

            Assert.Equal("D150", CareerStateWindowUI.FormatStrategyRow_Activate(row, FakeDate));
        }

        [Fact]
        public void FormatStrategyRow_Flow_RendersSourceTargetAndPct()
        {
            // Regression: fails if Source↔Target swaps or if the percentage
            // loses InvariantCulture (e.g. "10,0%" on a comma-locale).
            var row = new CareerStateWindowUI.StrategyRow
            {
                SourceResource = StrategyResource.Funds,
                TargetResource = StrategyResource.Science,
                Commitment = 0.1f
            };

            string s = CareerStateWindowUI.FormatStrategyRow_Flow(row);

            Assert.Equal("Funds -> Science @ 10.0%", s);
        }

        [Fact]
        public void FormatFacilityRow_Title_UsesDisplayTitleOrId()
        {
            // Regression: mirrors the contract/strategy title helpers.
            var rowWithTitle = new CareerStateWindowUI.FacilityRow
            {
                FacilityId = "LaunchPad", DisplayTitle = "Launch Pad"
            };
            var rowIdOnly = new CareerStateWindowUI.FacilityRow
            {
                FacilityId = "LaunchPad", DisplayTitle = null
            };
            var rowBlank = new CareerStateWindowUI.FacilityRow
            {
                FacilityId = null, DisplayTitle = ""
            };

            Assert.Equal("Launch Pad", CareerStateWindowUI.FormatFacilityRow_Title(rowWithTitle));
            Assert.Equal("LaunchPad", CareerStateWindowUI.FormatFacilityRow_Title(rowIdOnly));
            Assert.Equal("(unknown)", CareerStateWindowUI.FormatFacilityRow_Title(rowBlank));
        }

        [Fact]
        public void FormatFacilityRow_Level_CurrentOnly()
        {
            // Regression: the Level cell is the level NOW; what the timeline changes
            // lives in the Timeline-end cell, never as an arrow here.
            var row = new CareerStateWindowUI.FacilityRow
            {
                CurrentLevel = 2, ProjectedLevel = 3, HasUpcomingChange = true
            };

            Assert.Equal("L2", CareerStateWindowUI.FormatFacilityRow_Level(row));
        }

        [Fact]
        public void FormatMilestoneRow_UT_ShowsDate()
        {
            var row = new CareerStateWindowUI.MilestoneRow { CreditedUT = 8230.0 };

            Assert.Equal("D8230", CareerStateWindowUI.FormatMilestoneRow_UT(row, FakeDate));
        }

        [Fact]
        public void FormatMilestoneRow_Title_UsesDisplayTitleOrId()
        {
            // Regression: mirrors the other title helpers.
            var rowWithTitle = new CareerStateWindowUI.MilestoneRow
            {
                MilestoneId = "FirstOrbit", DisplayTitle = "First Orbit"
            };
            var rowIdOnly = new CareerStateWindowUI.MilestoneRow
            {
                MilestoneId = "FirstOrbit", DisplayTitle = null
            };
            var rowBlank = new CareerStateWindowUI.MilestoneRow
            {
                MilestoneId = null, DisplayTitle = ""
            };

            Assert.Equal("First Orbit", CareerStateWindowUI.FormatMilestoneRow_Title(rowWithTitle));
            Assert.Equal("FirstOrbit", CareerStateWindowUI.FormatMilestoneRow_Title(rowIdOnly));
            Assert.Equal("(unknown)", CareerStateWindowUI.FormatMilestoneRow_Title(rowBlank));
        }

        [Fact]
        public void FormatMilestoneRow_Rewards_ElidesZeros()
        {
            // Regression: fails if zero-reward entries leak into the column
            // as "+ 0 sci" clutter (design doc E8).
            var row = new CareerStateWindowUI.MilestoneRow
            {
                FundsAwarded = 10000f, RepAwarded = 5f, ScienceAwarded = 0f
            };

            string s = CareerStateWindowUI.FormatMilestoneRow_Rewards(row);

            Assert.Contains("+ 10000 funds", s);
            Assert.Contains("+ 5 rep", s);
            Assert.DoesNotContain("sci", s);
        }

        [Fact]
        public void FormatMilestoneRow_Rewards_EmptyWhenNoRewards()
        {
            // Regression: fails if a rewards-less milestone still emits
            // separators or leaves an empty-space placeholder.
            var row = new CareerStateWindowUI.MilestoneRow
            {
                FundsAwarded = 0f, RepAwarded = 0f, ScienceAwarded = 0f
            };

            Assert.Equal("", CareerStateWindowUI.FormatMilestoneRow_Rewards(row));
        }

        // catches: the Rewards column narrowing back under what a three-part reward needs.
        // The 2026-09-11 GUI census caught this LIVE in a shipped capture
        // (ksc-career-milestones-advanced.gui.json): at the old 180f, two three-part cells
        // rendered 36 px tall inside a 21 px row grid - IMGUI wrapped them, and a wrapped
        // label in a fixed-stride row overlaps its neighbours. The 7 px/char advance and the
        // padding allowance are the same figures TooltipEchoBudgetTests uses for this font.
        //
        // WHERE THE VALUES COME FROM. Parsek captures whatever floats KSP passes to
        // ProgressNode.AwardProgress (Patches/ProgressRewardPatch.cs) and stores them
        // verbatim, so nothing on Parsek's side bounds them. KSP's own milestone path is
        // ProgressNode.AwardProgressStandard -> FinePrint.Utilities.ProgressUtilities
        // .WorldFirstStandardReward (decompiled, 1.12.5):
        //   ContractDefs.Progression.<Currency>.BaseReward   80000 funds / 8 sci / 16 rep
        //                                                    (GameData/Squad/Contracts/Contracts.cfg)
        //   * PassiveBaseRatio (0.2)                         the milestone's share of that
        //   * ScoreProgressType(type, body)                  <= 2.0 (the record tracks)
        //   * OutlierMilestoneMultiplier (1.5) when outlier
        //   * GetContract<Currency>CompletionFactor(prestige) prestige * a GameVariables asset
        //                                                    factor * the career's reward
        //                                                    multiplier (a difficulty slider)
        //   * (funds only) 1 + (destinationWeight - 1) * PassiveBodyRatio (0.3)
        // The last two factors are a PLAYER SETTING times a Unity-asset value, so there is no
        // code-derivable maximum. The bound below is therefore a DOCUMENTED CHOICE, not a
        // derived cap: 7 digits of funds, 4 of reputation, 4 + one decimal of science. At
        // stock Normal the same product is about 48000 funds / 5 sci / 10 rep before the
        // completion factor, so the funds figure still holds a ~200x reward multiplier and
        // the other two hold more.
        [Fact]
        public void MilestoneRewardsColumn_HoldsAThreePartRewardOnOneLine()
        {
            const float AvgCharWidthPx = 7f;
            // Cell padding: GUI.skin.label's own horizontal padding plus the horizontal
            // group's inter-column spacing - the same 30 px allowance TooltipEchoBudgetTests
            // makes for window chrome + box padding. Without it the 7 px/char bound is spent
            // to the last pixel and a one-character growth wraps.
            const float CellPaddingPx = 30f;

            var row = new CareerStateWindowUI.MilestoneRow
            {
                FundsAwarded = 9999999f, RepAwarded = 9999f, ScienceAwarded = 9999.9f
            };
            string text = CareerStateWindowUI.FormatMilestoneRow_Rewards(row);

            Assert.Contains("funds", text);
            Assert.Contains("rep", text);
            Assert.Contains("sci", text);
            Assert.True(
                text.Length * AvgCharWidthPx + CellPaddingPx <= CareerStateWindowUI.ColW_Rewards,
                $"the widest three-part reward is {text.Length} chars = "
                + $"{text.Length * AvgCharWidthPx + CellPaddingPx} px with padding, but the "
                + $"Rewards column is {CareerStateWindowUI.ColW_Rewards} px - IMGUI will wrap "
                + "it into a row grid that has no room for a second line");
        }

        // catches: a table's fixed columns growing until the expanding name column has
        // no room at the window's default width. Each row is one table's fixed columns at
        // their widest (with the Timeline-end column shown).
        [Theory]
        [InlineData("contracts", CareerStateWindowUI.ColW_Date + CareerStateWindowUI.ColW_Deadline + CareerStateWindowUI.ColW_TimelineEnd)]
        [InlineData("strategies", CareerStateWindowUI.ColW_Date + CareerStateWindowUI.ColW_Flow + CareerStateWindowUI.ColW_TimelineEnd)]
        [InlineData("facilities", CareerStateWindowUI.ColW_Level + CareerStateWindowUI.ColW_TimelineEnd)]
        [InlineData("milestones", CareerStateWindowUI.ColW_Date + CareerStateWindowUI.ColW_Rewards)]
        public void EachTableLeavesTheNameColumnRoomAtTheDefaultWidth(string table, float fixedColumns)
        {
            float needed = fixedColumns + CareerStateWindowUI.MinNameColumnWidth;
            Assert.True(needed <= CareerStateWindowUI.DefaultWindowWidth - 40f,
                $"the {table} table needs {needed} px (fixed {fixedColumns} + name "
                + $"{CareerStateWindowUI.MinNameColumnWidth}), which does not leave the window "
                + $"chrome its room inside {CareerStateWindowUI.DefaultWindowWidth} px");
        }

        [Fact]
        public void TheDateColumnsHoldACompactKspDate()
        {
            // "Y12, D426, 05:17" at the 7 px/char bound plus the 30 px cell allowance.
            Assert.True("Y12, D426, 05:17".Length * 7f + 30f <= CareerStateWindowUI.ColW_Date);
            Assert.True("Y12, D426, 05:17 (in 99d)".Length * 7f + 30f
                        <= CareerStateWindowUI.ColW_Deadline);
            Assert.True("upgrades to L3, Y12, D426, 05:17".Length * 7f
                        <= CareerStateWindowUI.ColW_TimelineEnd);
        }

        [Fact]
        public void MinWindowHeightLeavesRowsVisible()
        {
            Assert.True(CareerStateWindowUI.MinWindowHeight >= 320f);
        }

        [Fact]
        public void ToggleSection_LogsFoldState()
        {
            // Regression: fails if the Pending-section disclosure arrow stops
            // logging its new fold state — KerbalsWindowUI has the same log
            // contract and we must match it so diagnostics are uniform.
            var set = new HashSet<string>();

            bool nowFolded = CareerStateWindowUI.ToggleSection(set, "Contracts.Pending");

            Assert.True(nowFolded);
            Assert.Contains("Contracts.Pending", set);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("CareerStateWindow: section toggled")
                && l.Contains("name=Contracts.Pending")
                && l.Contains("folded=True"));

            logLines.Clear();

            bool nowFoldedAgain = CareerStateWindowUI.ToggleSection(set, "Contracts.Pending");

            Assert.False(nowFoldedAgain);
            Assert.DoesNotContain("Contracts.Pending", set);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("CareerStateWindow: section toggled")
                && l.Contains("name=Contracts.Pending")
                && l.Contains("folded=False"));
        }
    }
}
