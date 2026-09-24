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

        private static (ContractsModule, StrategiesModule) Modules()
        {
            return (new ContractsModule(), new StrategiesModule());
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

        private static GameAction AcceptWithDeadline(string contractId, double ut, double deadlineUT)
        {
            GameAction a = Accept(contractId, ut);
            a.DeadlineUT = deadlineUT;
            return a;
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

        // ──────────────────────────────────────────────────────────────────
        // §8.1 Build() coverage
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_NoActions_EmptyVM()
        {
            // Regression: this fails if the walk throws on empty input or populates
            // per-tab collections from thin air.
            var (c, s) = Modules();

            var vm = CareerStateWindowUI.Build(new List<GameAction>(), 0.0,
                Game.Modes.CAREER, c, s);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.ProjectedRows);
            Assert.Empty(vm.Strategies.CurrentRows);
            Assert.Empty(vm.Strategies.ProjectedRows);
            Assert.Empty(vm.Contracts.PendingRows);
            Assert.Empty(vm.Strategies.PendingRows);
            Assert.True(double.IsPositiveInfinity(vm.NextRelevantActionUT));
            Assert.False(vm.HasDivergence);
        }

        [Fact]
        public void Build_Contracts_CurrentEqualsProjected_NoDivergence()
        {
            // Regression: this fails if the walk double-counts terminal state or
            // incorrectly flags divergence when current and projected match.
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-1", ut: 100.0, title: "Explore Mun")
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-future", ut: 300.0, title: "Rescue Kerbal")
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-1", ut: 100.0, title: "Explore Mun"),
                Complete("ctr-1", ut: 300.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-closing", ut: 100.0, title: "Explore Mun"),
                Complete("ctr-closing", ut: 300.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Activate("strat-closing", ut: 100.0),
                Deactivate("strat-closing", ut: 300.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-bad", ut: 100.0, title: "Ghost Contract", effective: false)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.ProjectedRows);
        }

        [Fact]
        public void Build_Strategies_ActivateDeactivateRoundTrip()
        {
            // Regression: this fails if the activate/deactivate pairing logic is
            // inverted or if the current snapshot doesn't observe the activation.
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Activate("Subsidy", ut: 100.0, commitment: 0.1f),
                Deactivate("Subsidy", ut: 200.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s);

            Assert.Single(vm.Strategies.CurrentRows);
            Assert.Empty(vm.Strategies.ProjectedRows);
            Assert.Equal(0.1f, vm.Strategies.CurrentRows[0].Commitment);
            Assert.True(vm.HasDivergence);
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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                // DIFFERENT levels, so a crossed key read changes both the echoed level
                // and the slot count on each tab.
                Upgrade("SpaceCenter/MissionControl", 2, ut: 50.0),
                Upgrade("SpaceCenter/Administration", 3, ut: 100.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s);

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
        public void Build_LiveUTEqualsActionUT_CountsAsApplied()
        {
            // Regression: design doc §4.3 calls for <=, not <. An action at the exact
            // live UT must be treated as already-applied. Fails if the comparison
            // mistakenly uses strict inequality.
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-at-boundary", ut: 200.0, title: "Boundary Contract")
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

            Assert.Single(vm.Contracts.CurrentRows);
            Assert.False(vm.Contracts.CurrentRows[0].IsPendingAccept);
        }

        [Fact]
        public void Build_NextRelevantActionUT_TracksFirstFutureProjectedAction()
        {
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-now", ut: 100.0, title: "Explore Mun"),
                Complete("ctr-now", ut: 123.75),
                Accept("ctr-later", ut: 200.0, title: "Rescue Kerbal")
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 123.4,
                Game.Modes.CAREER, c, s);

            Assert.Equal(123.75, vm.NextRelevantActionUT);
        }

        [Fact]
        public void Build_NextRelevantActionUT_ScienceSandbox_IgnoresHiddenCareerActions()
        {
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-hidden", ut: 123.4, title: "Hidden Contract"),
                Upgrade("MissionControl", toLevel: 2, ut: 150.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 100.0,
                Game.Modes.SCIENCE_SANDBOX, c, s);

            // Science draws no career rows at all, so no future action is relevant.
            Assert.True(double.IsPositiveInfinity(vm.NextRelevantActionUT));
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
            var (c, s) = Modules();

            CareerStateWindowUI.Build(new List<GameAction>(), 0.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-pending", ut: 300.0, title: "Future")
            };

            CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-orphan", ut: 100.0, title: null)
            };

            CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-1", ut: 100.0, title: "Career Contract"),
                Activate("Strat", ut: 100.0),
                Upgrade("LaunchPad", 2, ut: 100.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 500.0,
                Game.Modes.SANDBOX, c, s);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.ProjectedRows);
            Assert.Empty(vm.Strategies.CurrentRows);
            Assert.Empty(vm.Strategies.ProjectedRows);
            Assert.Equal(Game.Modes.SANDBOX, vm.Mode);
        }

        [Fact]
        public void Build_Mode_Science_HidesContractsAndStrategies()
        {
            // Regression: E2 - Science mode has no contracts or strategies, so both tabs
            // stay empty whatever the ledger holds, and nothing diverges.
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-1", ut: 100.0, title: "Should Be Hidden"),
                Activate("Strat", ut: 100.0),
                Upgrade("MissionControl", 2, ut: 600.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 500.0,
                Game.Modes.SCIENCE_SANDBOX, c, s);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Strategies.CurrentRows);
            Assert.False(vm.HasDivergence);
            Assert.Empty(CareerStateWindowUI.VisibleTabsFor(vm.Mode));
        }

        [Fact]
        public void Build_MissionControlLevel_MultipleFutureUpgrades_CurrentEchoesLiveUTLevel()
        {
            // Regression: E6 — two FacilityUpgrade actions for MissionControl at UT 100
            // (→L2) and UT 200 (→L3), liveUT=150. Current MissionControlLevel=2,
            // CurrentMaxSlots=GetContractSlots(2)=7; projected level=3, ProjectedMaxSlots=999.
            // Fails if projections leak into current.
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Upgrade("MissionControl", 2, ut: 100.0),
                Upgrade("MissionControl", 3, ut: 200.0)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 150.0,
                Game.Modes.CAREER, c, s);

            Assert.Equal(2, vm.Contracts.MissionControlLevel);
            Assert.Equal(3, vm.Contracts.ProjectedMissionControlLevel);
            Assert.Equal(7, vm.Contracts.CurrentMaxSlots);
            Assert.Equal(999, vm.Contracts.ProjectedMaxSlots);
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
                strategies: null);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Strategies.CurrentRows);
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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("ctr-crash", ut: 100.0, title: null)
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
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
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("now", ut: 100.0, title: "Now"),
                Accept("later", ut: 300.0, title: "Later"),
                Accept("brief", ut: 350.0, title: "Brief"),
                ContractEnd(GameActionType.ContractComplete, "brief", 380.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("old", ut: 100.0),
                ContractEnd(GameActionType.ContractComplete, "old", 300.0),
                Accept("new", ut: 400.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

            Assert.Equal(vm.Contracts.CurrentActive, vm.Contracts.ProjectedActive);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_Contracts_EndingBeforeLiveUT_LeavesNoRow()
        {
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("past", ut: 100.0),
                ContractEnd(GameActionType.ContractFail, "past", 150.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

            Assert.Empty(vm.Contracts.CurrentRows);
            Assert.Empty(vm.Contracts.PendingRows);
            Assert.False(vm.HasDivergence);
        }

        [Fact]
        public void Build_Strategies_PendingRowsAndDeactivation()
        {
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Activate("now", ut: 100.0),
                Activate("later", ut: 300.0),
                Deactivate("later", ut: 350.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s);

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
            Assert.Equal(expected, FacilityDisplayNames.FacilityIdForBuilding(buildingId));
        }

        [Fact]
        public void FacilityNames_ComeFromTheStockLookup()
        {
            FacilityDisplayNames.FacilityNameLookupForTesting =
                id => id == "LaunchPad" ? "Launchpad" : null;
            try
            {
                Assert.Equal("Launchpad",
                    FacilityDisplayNames.ResolveFacilityDisplayName("LaunchPad"));
                // No stock answer -> the humanized id, with stock's lowercase conjunction.
                Assert.Equal("Research and Development",
                    FacilityDisplayNames.ResolveFacilityDisplayName("ResearchAndDevelopment"));
            }
            finally
            {
                FacilityDisplayNames.FacilityNameLookupForTesting = null;
            }
        }

        [Fact]
        public void ResolveFacilityDisplayName_IgnoresAnUnresolvedLocalizationTag()
        {
            FacilityDisplayNames.FacilityNameLookupForTesting = id => "#autoLOC_6001646";
            try
            {
                Assert.Equal("Research and Development",
                    FacilityDisplayNames.ResolveFacilityDisplayName("ResearchAndDevelopment"));
            }
            finally
            {
                FacilityDisplayNames.FacilityNameLookupForTesting = null;
            }
        }

        [Theory]
        [InlineData("ResearchAndDevelopment", "Research and Development")]
        [InlineData("VehicleAssemblyBuilding", "Vehicle Assembly Building")]
        [InlineData("MissionControl", "Mission Control")]
        public void HumanizeFacilityId_SplitsPascalCaseWithALowercaseAnd(string id, string expected)
        {
            Assert.Equal(expected, FacilityDisplayNames.HumanizeFacilityId(id));
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

        // ──────────────────────────────────────────────────────────────────
        // Mode-appropriate tabs, banner, launcher
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void VisibleTabsFor_EachMode()
        {
            // Two tabs in Career, none anywhere else: contracts and strategies exist only
            // there, and the dated history the other modes have is the Timeline's.
            Assert.Equal(new[] { CareerStateWindowUI.TabContracts, CareerStateWindowUI.TabStrategies },
                CareerStateWindowUI.VisibleTabsFor(Game.Modes.CAREER));
            Assert.Equal(2, CareerStateWindowUI.TabCountForTesting);
            Assert.Empty(CareerStateWindowUI.VisibleTabsFor(Game.Modes.SCIENCE_SANDBOX));
            Assert.Empty(CareerStateWindowUI.VisibleTabsFor(Game.Modes.SANDBOX));
            Assert.Empty(CareerStateWindowUI.VisibleTabsFor(Game.Modes.MISSION));
        }

        [Fact]
        public void CoerceTab_KeepsADrawnTabAndOtherwiseTakesTheFirst()
        {
            int[] career = CareerStateWindowUI.VisibleTabsFor(Game.Modes.CAREER);
            Assert.Equal(CareerStateWindowUI.TabStrategies,
                CareerStateWindowUI.CoerceTab(CareerStateWindowUI.TabStrategies, career));
            // An index past the two tabs (an old Facilities / Milestones selection) falls
            // back to Contracts.
            Assert.Equal(CareerStateWindowUI.TabContracts, CareerStateWindowUI.CoerceTab(3, career));
            // A mode that draws no tabs keeps the stored selection for later.
            Assert.Equal(1, CareerStateWindowUI.CoerceTab(1, new int[0]));
        }

        [Fact]
        public void SelectedTabForTesting_CoercesAgainstTheCachedMode()
        {
            // catches: the seam writing an index the window does not draw and reading it
            // back as applied.
            var ui = new CareerStateWindowUI(parentUI: null);
            ui.CachedVMForTesting = new CareerStateWindowUI.CareerStateViewModel
            {
                Mode = Game.Modes.CAREER
            };
            ui.SelectedTabForTesting = 3;
            Assert.Equal(CareerStateWindowUI.TabContracts, ui.SelectedTabForTesting);
            ui.SelectedTabForTesting = CareerStateWindowUI.TabStrategies;
            Assert.Equal(CareerStateWindowUI.TabStrategies, ui.SelectedTabForTesting);

            // No cached VM yet: stored as written, the draw coerces it.
            var fresh = new CareerStateWindowUI(parentUI: null);
            fresh.SelectedTabForTesting = CareerStateWindowUI.TabStrategies;
            Assert.Equal(CareerStateWindowUI.TabStrategies, fresh.SelectedTabForTesting);
        }

        [Fact]
        public void ModeOffersLauncher_OnlyInCareer()
        {
            Assert.True(CareerStateWindowUI.ModeOffersLauncher(Game.Modes.CAREER));
            // Science mode has no contracts or strategies; its milestones, facilities and
            // tech live in the Timeline's Career view.
            Assert.False(CareerStateWindowUI.ModeOffersLauncher(Game.Modes.SCIENCE_SANDBOX));
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

            Assert.Equal("Science mode - contracts and strategies are not tracked",
                CareerStateWindowUI.FormatModeBanner(
                    CachedVm(1000.0, mode: Game.Modes.SCIENCE_SANDBOX), FakeDate));
            Assert.Equal("Sandbox mode - career state is not tracked",
                CareerStateWindowUI.FormatModeBanner(
                    CachedVm(1000.0, mode: Game.Modes.SANDBOX), FakeDate));
        }

        // ──────────────────────────────────────────────────────────────────
        // Heading line, pending fold, empty state
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void FormatSlotCount_PluralisesOnTheLimit()
        {
            Assert.Equal("2 of 2 slots", CareerStateWindowUI.FormatSlotCount(2, 2));
            Assert.Equal("0 of 3 slots", CareerStateWindowUI.FormatSlotCount(0, 3));
            Assert.Equal("1 of 1 slot", CareerStateWindowUI.FormatSlotCount(1, 1));
            Assert.Equal("0 of 1 slot", CareerStateWindowUI.FormatSlotCount(0, 1));
        }

        [Fact]
        public void FormatActiveHeading_IsOneLineWithTheSlotsNow()
        {
            Assert.Equal("Active now: 2 of 2 slots", CareerStateWindowUI.FormatActiveHeading(2, 2));
            Assert.Equal("Active now: 1 of 1 slot", CareerStateWindowUI.FormatActiveHeading(1, 1));
        }

        [Fact]
        public void FormatSlotLimitTooltip_NamesTheLevelAndAnUpgradeBeforeTheEnd()
        {
            Assert.Equal("Slot limit from Mission Control L1.",
                CareerStateWindowUI.FormatSlotLimitTooltip("Mission Control", 1, 1));
            Assert.Equal("Slot limit from Administration L2 (L3 at timeline end).",
                CareerStateWindowUI.FormatSlotLimitTooltip("Administration", 2, 3));
        }

        [Fact]
        public void FormatPendingFold_CountsAndSlotsAtTheTimelineEnd()
        {
            Assert.Equal("Pending in timeline (1) - 3 of 3 slots at timeline end",
                CareerStateWindowUI.FormatPendingFold(1, 3, 3));
            Assert.Equal("Pending in timeline (2) - 1 of 1 slot at timeline end",
                CareerStateWindowUI.FormatPendingFold(2, 1, 1));
        }

        [Fact]
        public void IsTabEmpty_OnlyWithNothingNowAndNothingPending()
        {
            Assert.True(CareerStateWindowUI.IsTabEmpty(0, 0));
            Assert.False(CareerStateWindowUI.IsTabEmpty(1, 0));
            // Nothing now but something pending still draws the table (the fold lives in it).
            Assert.False(CareerStateWindowUI.IsTabEmpty(0, 2));
            Assert.Equal("No active contracts.", CareerStateWindowUI.NoActiveContractsText);
            Assert.Equal("No active strategies.", CareerStateWindowUI.NoActiveStrategiesText);
        }

        [Fact]
        public void FillDisplayText_WritesTheHeadingTooltipAndFoldFromTheWalk()
        {
            // Mission Control L2 now (7 slots), L3 (999) after a future upgrade; one
            // contract now, one more the recorded timeline accepts.
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Upgrade("SpaceCenter/MissionControl", 2, ut: 50.0),
                Accept("ctr-now", ut: 100.0, title: "Now"),
                Accept("ctr-later", ut: 300.0, title: "Later"),
                Upgrade("SpaceCenter/MissionControl", 3, ut: 400.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, FakeDate);

            Assert.Equal("Active now: 1 of 7 slots", vm.Contracts.GroupHeadingText);
            Assert.Equal("Slot limit from Mission Control L2 (L3 at timeline end).",
                vm.Contracts.GroupHeadingTooltip);
            Assert.Equal("Pending in timeline (1) - 2 of 999 slots at timeline end",
                vm.Contracts.PendingFoldText);
            Assert.Equal("Active now: 0 of 1 slot", vm.Strategies.GroupHeadingText);
            Assert.Equal("Slot limit from Administration L1.", vm.Strategies.GroupHeadingTooltip);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_AFutureSlotUpgradeAloneDiverges()
        {
            // The heading tooltip and the fold both print the limit at the timeline end,
            // so a future Mission Control upgrade with no contract change is still a
            // difference between now and the end.
            var (c, s) = Modules();
            var vm = CareerStateWindowUI.Build(
                new List<GameAction> { Upgrade("SpaceCenter/MissionControl", 2, ut: 300.0) },
                liveUT: 200.0, Game.Modes.CAREER, c, s);
            Assert.True(vm.HasDivergence);
            Assert.Equal(300.0, vm.NextRelevantActionUT);
        }

        // ──────────────────────────────────────────────────────────────────
        // Timeline cross-link
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void LinkSubjects_AreTheIdsTheTimelineStampsOnTheSameActions()
        {
            // catches: the Career row and the Timeline row naming one contract / strategy
            // by different keys, which would make every click log "not found".
            var (c, s) = Modules();
            GameAction accept = Accept("ctr-7", ut: 100.0, title: "Seven");
            GameAction activate = Activate("PatentsLicensingCfg", ut: 100.0);
            var vm = CareerStateWindowUI.Build(new List<GameAction> { accept, activate },
                liveUT: 200.0, Game.Modes.CAREER, c, s);

            Assert.Equal(TimelineCareerCategory.Contracts, TimelineCareerCategories.Classify(accept));
            Assert.Equal(
                TimelineCareerCategories.ResolveSubjectId(accept, TimelineCareerCategory.Contracts),
                CareerStateWindowUI.ContractLinkSubject(vm.Contracts.CurrentRows.Single()));
            Assert.Equal(TimelineCareerCategory.Strategies, TimelineCareerCategories.Classify(activate));
            Assert.Equal(
                TimelineCareerCategories.ResolveSubjectId(activate, TimelineCareerCategory.Strategies),
                CareerStateWindowUI.StrategyLinkSubject(vm.Strategies.CurrentRows.Single()));

            // A row with no id is not a link.
            Assert.Null(CareerStateWindowUI.ContractLinkSubject(new CareerStateWindowUI.ContractRow()));
            Assert.Null(CareerStateWindowUI.StrategyLinkSubject(
                new CareerStateWindowUI.StrategyRow { StrategyId = "" }));
        }

        [Fact]
        public void OnRowNameClicked_ScrollsTheTimelineAndLogs()
        {
            TimelineCareerCategory gotCategory = TimelineCareerCategory.None;
            string gotSubject = null;
            CareerStateWindowUI.OnRowNameClicked(
                (cat, id) => { gotCategory = cat; gotSubject = id; },
                TimelineCareerCategory.Strategies, "PatentsLicensingCfg");

            Assert.Equal(TimelineCareerCategory.Strategies, gotCategory);
            Assert.Equal("PatentsLicensingCfg", gotSubject);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("CareerStateWindow: row -> Timeline scroll")
                && l.Contains("category=Strategies")
                && l.Contains("subject=PatentsLicensingCfg"));

            // A null callback (no Timeline window yet) does not throw and still logs.
            logLines.Clear();
            CareerStateWindowUI.OnRowNameClicked(null, TimelineCareerCategory.Contracts, "ctr-1");
            Assert.Contains(logLines, l => l.Contains("subject=ctr-1")
                                           && l.Contains("(no Timeline window)"));
        }

        [Fact]
        public void TheLinkTooltipsFitTheHelpStrip()
        {
            // The budget scan does not see these (they sit behind a conditional), so the
            // one-line fit at the 820 px first-open width is asserted here with the same
            // 7 px/char bound and 30 px padding TooltipEchoBudgetTests uses.
            int budget = (int)Math.Floor((CareerStateWindowUI.DefaultWindowWidth - 30f) / 7f);
            foreach (string t in new[]
                     { CareerStateWindowUI.ContractLinkTooltip, CareerStateWindowUI.StrategyLinkTooltip })
            {
                Assert.True(t.Length <= budget, t + " is " + t.Length + " chars; budget " + budget);
                Assert.DoesNotContain('\n', t);
            }
        }

        [Fact]
        public void TheLinkTooltipsSayWhatTheClickDoes()
        {
            Assert.Contains("Timeline", CareerStateWindowUI.ContractLinkTooltip);
            Assert.Contains("Contracts", CareerStateWindowUI.ContractLinkTooltip);
            Assert.Contains("Timeline", CareerStateWindowUI.StrategyLinkTooltip);
            Assert.Contains("Strategies", CareerStateWindowUI.StrategyLinkTooltip);
        }

        [Fact]
        public void TheSeamPendingValuesMapToTheWindowsFoldKeys()
        {
            // The automation seam's `op=expand window=career key=pending:<tab>` names the
            // TAB; every wire value must resolve to one of the window's own fold keys.
            Assert.Equal(new[] { "contracts", "strategies" },
                Parsek.TestCommands.TestCommandUiState.CareerPendingFoldValues);
            foreach (string v in Parsek.TestCommands.TestCommandUiState.CareerPendingFoldValues)
                Assert.Contains(Parsek.TestCommands.TestCommandUiState.CareerFoldKeyFor(v),
                    CareerStateWindowUI.FoldGroupKeys);
            Assert.Null(Parsek.TestCommands.TestCommandUiState.CareerFoldKeyFor("milestones"));
            Assert.Null(Parsek.TestCommands.TestCommandUiState.CareerFoldKeyFor("facilities"));
        }

        [Fact]
        public void FoldGroupKeys_CoverThePendingGroupOfEveryTab()
        {
            Assert.Equal(
                new[]
                {
                    CareerStateWindowUI.GroupKey_ContractsPending,
                    CareerStateWindowUI.GroupKey_StrategiesPending,
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
        public void ShouldRebuildCachedVM_SameMinuteWithinSameActionGap_ReturnsFalse()
        {
            // The banner and deadline tails have minute resolution: a new game second
            // re-formatted identical text.
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(123.4, nextRelevantActionUT: 500.0),
                currentMode: Game.Modes.CAREER,
                liveUT: 178.6));
        }

        [Fact]
        public void ShouldRebuildCachedVM_MinuteBoundaryWithinSameActionGap_ReturnsTrue()
        {
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(179.9, nextRelevantActionUT: 500.0),
                currentMode: Game.Modes.CAREER,
                liveUT: 180.1));
        }

        [Fact]
        public void ShouldRebuildCachedVM_SecondCadenceNearADeadline_ReturnsTrueEachSecond()
        {
            var vm = CachedVm(123.4, nextRelevantActionUT: 500.0);
            vm.RefreshSeconds = CareerStateWindowUI.SecondRefreshSeconds;
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                vm, currentMode: Game.Modes.CAREER, liveUT: 124.1));
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
                vm, currentMode: Game.Modes.CAREER, liveUT: 123.9));
        }

        [Fact]
        public void ShouldRebuildCachedVM_ScienceSandbox_MinuteBoundary_ReturnsFalse()
        {
            // Science draws only a banner, so a minute boundary rebuilds nothing.
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
                CachedVm(179.9, nextRelevantActionUT: 500.0, mode: Game.Modes.SCIENCE_SANDBOX),
                currentMode: Game.Modes.SCIENCE_SANDBOX,
                liveUT: 180.1));
        }

        [Fact]
        public void FillDisplayText_FormatsOnceAndPicksTheCadence()
        {
            var (c, s) = Modules();
            var far = new List<GameAction>
            {
                AcceptWithDeadline("far", ut: 100.0, deadlineUT: 10_000.0),
            };
            var vm = CareerStateWindowUI.Build(far, liveUT: 200.0,
                Game.Modes.CAREER, c, s, FakeDate);
            Assert.Equal(CareerStateWindowUI.MinuteRefreshSeconds, vm.RefreshSeconds);
            Assert.Equal("Career mode - D200", vm.BannerText);
            var row = vm.Contracts.CurrentRows.Single();
            Assert.Equal("D100", row.AcceptText);
            Assert.StartsWith("D10000 (in ", row.DeadlineText, StringComparison.Ordinal);
            Assert.False(row.DeadlineOverdue);
            Assert.StartsWith("Active now: 1 of ", vm.Contracts.GroupHeadingText,
                StringComparison.Ordinal);

            var near = new List<GameAction>
            {
                AcceptWithDeadline("near", ut: 100.0, deadlineUT: 230.0),
            };
            vm = CareerStateWindowUI.Build(near, liveUT: 200.0,
                Game.Modes.CAREER, c, s, FakeDate);
            Assert.Equal(CareerStateWindowUI.SecondRefreshSeconds, vm.RefreshSeconds);
        }

        [Fact]
        public void Build_Strategies_ActiveNowDeactivatedThenReactivated_BothRowsSayWhen()
        {
            // catches: the row active now losing its deactivation date because the later
            // re-activation cleared the id's ending; it then read as running to the end.
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Activate("Subsidy", ut: 100.0),
                Deactivate("Subsidy", ut: 300.0),
                Activate("Subsidy", ut: 400.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, FakeDate);

            var now = vm.Strategies.CurrentRows.Single();
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Deactivated, now.EndKind);
            Assert.Equal("deactivates D300", now.TimelineEndText);
            var pending = vm.Strategies.PendingRows.Single();
            Assert.Equal(400.0, pending.ActivateUT);
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.None, pending.EndKind);
            Assert.True(vm.HasDivergence);
        }

        [Fact]
        public void Build_Contracts_ActiveNowCompletedThenReaccepted_CurrentRowSaysWhen()
        {
            var (c, s) = Modules();
            var actions = new List<GameAction>
            {
                Accept("c1", ut: 100.0),
                Complete("c1", ut: 300.0),
                Accept("c1", ut: 400.0),
            };

            var vm = CareerStateWindowUI.Build(actions, liveUT: 200.0,
                Game.Modes.CAREER, c, s, FakeDate);

            var now = vm.Contracts.CurrentRows.Single();
            Assert.Equal(CareerStateWindowUI.TimelineEndKind.Completed, now.EndKind);
            Assert.Equal("completes D300", now.TimelineEndText);
            Assert.Single(vm.Contracts.PendingRows);
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
        public void ShouldRebuildCachedVM_ScienceSandbox_ActionBoundary_ReturnsFalse()
        {
            Assert.False(CareerStateWindowUI.ShouldRebuildCachedVM(
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
                strategies: null);

            Assert.True(fallbackVm.IsTransientFallback);
            Assert.True(CareerStateWindowUI.ShouldRebuildCachedVM(
                fallbackVm,
                currentMode: Game.Modes.CAREER,
                liveUT: 123.4));

            var (contracts, strategies) = Modules();
            var recoveredVm = CareerStateWindowUI.Build(
                actions: Array.Empty<GameAction>(),
                liveUT: 123.4,
                mode: Game.Modes.CAREER,
                contracts: contracts,
                strategies: strategies);

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
                && l.Contains("banner (no tabs)"));
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

        // catches: a table's fixed columns growing until the expanding name column has
        // no room at the window's default width. Each row is one table's fixed columns at
        // their widest (with the Timeline-end column shown).
        [Theory]
        [InlineData("contracts", CareerStateWindowUI.ColW_Date + CareerStateWindowUI.ColW_Deadline + CareerStateWindowUI.ColW_TimelineEnd)]
        [InlineData("strategies", CareerStateWindowUI.ColW_Date + CareerStateWindowUI.ColW_Flow + CareerStateWindowUI.ColW_TimelineEnd)]
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
            Assert.True("deactivates Y12, D426, 05:17".Length * 7f + 30f
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
