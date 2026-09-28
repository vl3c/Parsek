using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Career-mode slot counts the Timeline's Contracts and Strategies view buttons
    /// carry in their hover (<see cref="CareerSlotSummary"/>): the walk over the effective
    /// ledger, the shared Mission Control forecast it reads for contracts, the peak walk it
    /// keeps for strategies, the one hover sentence, and the Timeline's pure composition
    /// and refresh helpers.
    /// </summary>
    [Collection("Sequential")]
    public class CareerSlotSummaryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CareerSlotSummaryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------- fixtures

        private static GameAction Accept(string contractId, double ut, bool effective = true)
        {
            return new GameAction
            {
                Type = GameActionType.ContractAccept,
                UT = ut,
                ContractId = contractId,
                DeadlineUT = double.NaN,
                Effective = effective
            };
        }

        private static GameAction AcceptWithDeadline(string contractId, double ut, double deadlineUT)
        {
            GameAction a = Accept(contractId, ut);
            a.DeadlineUT = deadlineUT;
            return a;
        }

        private static GameAction Complete(string contractId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.ContractComplete, UT = ut, ContractId = contractId, Effective = true
            };
        }

        private static GameAction Activate(string strategyId, double ut, bool effective = true)
        {
            return new GameAction
            {
                Type = GameActionType.StrategyActivate,
                UT = ut,
                StrategyId = strategyId,
                SourceResource = StrategyResource.Funds,
                TargetResource = StrategyResource.Science,
                Commitment = 0.1f,
                Effective = effective
            };
        }

        private static GameAction Deactivate(string strategyId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.StrategyDeactivate, UT = ut, StrategyId = strategyId, Effective = true
            };
        }

        private static GameAction Upgrade(string facilityId, int toLevel, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.FacilityUpgrade, UT = ut, FacilityId = facilityId, ToLevel = toLevel,
                Effective = true
            };
        }

        private static CareerSlotSummary.SlotUsage Usage(int active, int limit,
            params CareerSlotSummary.SlotChange[] changes)
            => CareerSlotSummary.ComputeSlotUsage(active, limit, changes);

        private static string ContractSentence(CareerSlotSummary.Snapshot s)
            => CareerSlotSummary.FormatSlotSentence(CareerSlotSummary.SlotKind.Contracts, s.Contracts);

        private static string StrategySentence(CareerSlotSummary.Snapshot s)
            => CareerSlotSummary.FormatSlotSentence(CareerSlotSummary.SlotKind.Strategies, s.Strategies);

        /// <summary>
        /// The shared forecast (<see cref="ContractSlotReservation"/>) over the same rows,
        /// computed independently of the summary: the index over every row, the contracts
        /// active at <paramref name="liveUT"/>, and Mission Control L1's two slots.
        /// </summary>
        private static ContractSlotForecast SharedForecast(List<GameAction> actions, double liveUT,
            ContractSlotHolder[] activeNow, int limitNow = 2, Func<string, bool> isAutoAccept = null)
        {
            var index = CommittedFutureIndex.Build(actions, id => true, null, null, isAutoAccept);
            var holders = activeNow.Where(h => isAutoAccept == null || !isAutoAccept(h.Key));
            return ContractSlotReservation.Forecast(index, holders, limitNow, liveUT,
                LedgerOrchestrator.GetContractSlots);
        }

        // ---------------------------------------------------------------- the sentence

        [Fact]
        public void Sentence_FreeFirstThenActiveThenReserved()
        {
            // The owner's order: free, then active, then reserved.
            Assert.Equal("Contract slots: 4 of 7 free now (2 active, 1 reserved for later).",
                CareerSlotSummary.FormatSlotSentence(CareerSlotSummary.SlotKind.Contracts,
                    Usage(2, 7, CareerSlotSummary.SlotChange.Occupy(300.0))));
            Assert.Equal("Strategy slots: 1 of 3 free now (1 active, 1 reserved for later).",
                CareerSlotSummary.FormatSlotSentence(CareerSlotSummary.SlotKind.Strategies,
                    Usage(1, 3, CareerSlotSummary.SlotChange.Occupy(300.0))));
        }

        [Fact]
        public void Sentence_ZeroReservedDropsTheReservedClause()
        {
            Assert.Equal("Contract slots: 5 of 7 free now (2 active).",
                CareerSlotSummary.FormatSlotSentence(CareerSlotSummary.SlotKind.Contracts, Usage(2, 7)));
            // Over-subscribed (a downgrade, or a rewound career): nothing free, no negatives.
            Assert.Equal("Contract slots: 0 of 2 free now (3 active).",
                CareerSlotSummary.FormatSlotSentence(CareerSlotSummary.SlotKind.Contracts, Usage(3, 2)));
        }

        [Fact]
        public void Sentence_UnlimitedReadsNoSlotLimit()
        {
            // At or above the threshold stock has no limit (Mission Control L3 = 999), and
            // a reservation against no limit is meaningless, so none is printed.
            Assert.Equal("Contract slots: no slot limit (2 active).",
                CareerSlotSummary.FormatSlotSentence(CareerSlotSummary.SlotKind.Contracts,
                    Usage(2, CareerSlotSummary.UnlimitedSlotThreshold,
                        CareerSlotSummary.SlotChange.Occupy(300.0))));
        }

        [Fact]
        public void Sentence_IsInvariantUnderAForeignCulture()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal("Contract slots: 1234 of 2000 free now (5 active, 761 reserved for later).",
                    CareerSlotSummary.FormatSlotSentence(CareerSlotSummary.SlotKind.Contracts,
                        new CareerSlotSummary.SlotUsage { Active = 5, Limit = 2000, Free = 1234, Reserved = 761 }));
                Assert.StartsWith("liveUT=1234568 ",
                    CareerSlotSummary.FormatSnapshotForLog(new CareerSlotSummary.Snapshot(), 1234567.6));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---------------------------------------------------------------- the Timeline hover

        [Fact]
        public void CategoryTooltip_AppendsTheSentenceOnlyWhenThereIsOne()
        {
            Assert.Equal("Only contract rows, past and future.",
                TimelineWindowUI.BuildCategoryTooltip(TimelineWindowUI.ContractsToggleTooltip, null));
            Assert.Equal("Only contract rows, past and future.",
                TimelineWindowUI.BuildCategoryTooltip(TimelineWindowUI.ContractsToggleTooltip, ""));
            Assert.Equal("Only contract rows, past and future. Contract slots: 4 of 7 free now (2 active, 1 reserved for later).",
                TimelineWindowUI.BuildCategoryTooltip(TimelineWindowUI.ContractsToggleTooltip,
                    "Contract slots: 4 of 7 free now (2 active, 1 reserved for later)."));
        }

        [Fact]
        public void ComposedTooltips_CareerGetsTheCounts_OtherModesAndNoSnapshotDoNot()
        {
            var snapshot = new CareerSlotSummary.Snapshot
            {
                Contracts = Usage(2, 7, CareerSlotSummary.SlotChange.Occupy(300.0)),
                Strategies = Usage(0, 1)
            };

            TimelineWindowUI.ComposeCareerSlotTooltips(Game.Modes.CAREER, snapshot,
                out string contracts, out string strategies);
            Assert.Equal("Only contract rows, past and future. "
                + "Contract slots: 4 of 7 free now (2 active, 1 reserved for later).", contracts);
            Assert.Equal("Only strategy rows, past and future. "
                + "Strategy slots: 1 of 1 free now (0 active).", strategies);

            // Science and Sandbox have no contracts or strategies: the sentence is skipped
            // (the buttons do not draw there at all, but the text never claims counts).
            foreach (Game.Modes mode in new[] { Game.Modes.SCIENCE_SANDBOX, Game.Modes.SANDBOX })
            {
                TimelineWindowUI.ComposeCareerSlotTooltips(mode, snapshot, out contracts, out strategies);
                Assert.Equal(TimelineWindowUI.ContractsToggleTooltip, contracts);
                Assert.Equal(TimelineWindowUI.StrategiesToggleTooltip, strategies);
            }
            TimelineWindowUI.ComposeCareerSlotTooltips(null, snapshot, out contracts, out strategies);
            Assert.Equal(TimelineWindowUI.ContractsToggleTooltip, contracts);
            TimelineWindowUI.ComposeCareerSlotTooltips(Game.Modes.CAREER, null, out contracts, out strategies);
            Assert.Equal(TimelineWindowUI.StrategiesToggleTooltip, strategies);
        }

        [Fact]
        public void RefreshRule_RebuildsOnDirtyMissingRewindOrANewMinuteOnly()
        {
            Assert.True(TimelineWindowUI.ShouldRebuildCareerSlots(true, false, 100.0, 100.0));
            Assert.True(TimelineWindowUI.ShouldRebuildCareerSlots(false, true, 100.0, 100.0));
            // Same game minute: the counts cannot have moved on their own.
            Assert.False(TimelineWindowUI.ShouldRebuildCareerSlots(false, false, 61.0, 119.0));
            // A new game minute begins.
            Assert.True(TimelineWindowUI.ShouldRebuildCareerSlots(false, false, 119.0, 120.0));
            // The clock went backwards (a rewind) inside the same minute.
            Assert.True(TimelineWindowUI.ShouldRebuildCareerSlots(false, false, 119.0, 118.0));
        }

        // ---------------------------------------------------------------- the peak walk

        [Fact]
        public void ComputeSlotUsage_ACompletionBeforeALaterAcceptSharesOneSlot()
        {
            // catches: counting every committed future accept as holding a slot now.
            var u = Usage(2, 2,
                CareerSlotSummary.SlotChange.Release(50.0),
                CareerSlotSummary.SlotChange.Occupy(60.0));
            Assert.Equal(2, u.PeakNeed);
            Assert.Equal(0, u.Reserved);
            Assert.Equal(0, u.Free);
        }

        [Fact]
        public void ComputeSlotUsage_OverlappingAcceptsAddUpAtThePeak()
        {
            var u = Usage(1, 7,
                CareerSlotSummary.SlotChange.Occupy(60.0),
                CareerSlotSummary.SlotChange.Occupy(70.0),
                CareerSlotSummary.SlotChange.Release(80.0),
                CareerSlotSummary.SlotChange.Release(90.0),
                CareerSlotSummary.SlotChange.Occupy(100.0));
            Assert.Equal(3, u.PeakNeed);
            Assert.Equal(2, u.Reserved);
            Assert.Equal(4, u.Free);
            Assert.Equal(7, u.Free + u.Active + u.Reserved);
        }

        [Fact]
        public void ComputeSlotUsage_AReleaseAndAnOccupyOnOneTickShareTheSlot()
        {
            // Input order puts the occupy first; the walk still frees before it occupies.
            var u = Usage(2, 2,
                CareerSlotSummary.SlotChange.Occupy(50.0),
                CareerSlotSummary.SlotChange.Release(50.0));
            Assert.Equal(2, u.PeakNeed);
            Assert.Equal(0, u.Reserved);
        }

        [Fact]
        public void ComputeSlotUsage_ALaterUpgradeCountsAgainstTodaysLimit()
        {
            var u = Usage(1, 2,
                CareerSlotSummary.SlotChange.Limit(40.0, 7),
                CareerSlotSummary.SlotChange.Occupy(50.0),
                CareerSlotSummary.SlotChange.Occupy(60.0),
                CareerSlotSummary.SlotChange.Occupy(70.0));
            Assert.Equal(1, u.PeakNeed);
            Assert.Equal(0, u.Reserved);
            Assert.Equal(1, u.Free);

            // An occupy BEFORE the upgrade still needs today's slot.
            u = Usage(1, 2,
                CareerSlotSummary.SlotChange.Occupy(30.0),
                CareerSlotSummary.SlotChange.Limit(40.0, 7));
            Assert.Equal(2, u.PeakNeed);
            Assert.Equal(1, u.Reserved);
            Assert.Equal(0, u.Free);

            // No limit from day 40: nothing held after it crowds out today.
            u = Usage(1, 2,
                CareerSlotSummary.SlotChange.Limit(40.0, 999),
                CareerSlotSummary.SlotChange.Occupy(50.0),
                CareerSlotSummary.SlotChange.Occupy(60.0));
            Assert.Equal(1, u.PeakNeed);
            Assert.Equal(1, u.Free);
        }

        [Fact]
        public void SlotUsageFromForecast_ANegativeFreeReadsZeroAndNullIsEmpty()
        {
            var usage = CareerSlotSummary.SlotUsageFromForecast(new ContractSlotForecast(2, 2, 4, -2, null));
            Assert.Equal(0, usage.Free);
            Assert.Equal(4, usage.PeakNeed);
            Assert.Equal(2, usage.Reserved);
            Assert.Equal(new CareerSlotSummary.SlotUsage(), CareerSlotSummary.SlotUsageFromForecast(null));
        }

        // ---------------------------------------------------------------- the ledger walk

        private static void AssertContractsMatchForecast(CareerSlotSummary.Snapshot s, ContractSlotForecast f)
        {
            Assert.Equal(f.ActiveNow, s.Contracts.Active);
            Assert.Equal(f.LimitNow, s.Contracts.Limit);
            Assert.Equal(Math.Max(0, f.FreeSlotsForNewAcceptNow), s.Contracts.Free);
            Assert.Equal(Math.Max(f.ActiveNow, f.LimitNow - f.FreeSlotsForNewAcceptNow), s.Contracts.PeakNeed);
        }

        [Fact]
        public void Build_Contracts_ACompletionFreesTheSlotALaterAcceptTakes()
        {
            // Two active at L1 (two slots); a completes, then a flight accepts c: c takes
            // the slot a frees, so nothing is reserved - the shared forecast's numbers.
            var actions = new List<GameAction>
            {
                Accept("a", 100.0), Accept("b", 110.0), Complete("a", 500.0), Accept("c", 600.0),
            };
            var s = CareerSlotSummary.Build(actions, 200.0);
            AssertContractsMatchForecast(s, SharedForecast(actions, 200.0, new[]
            {
                new ContractSlotHolder("a", 100.0, double.NaN),
                new ContractSlotHolder("b", 110.0, double.NaN),
            }));
            Assert.Equal("ledger", s.ContractSource);
            Assert.Equal("Contract slots: 0 of 2 free now (2 active).", ContractSentence(s));

            // Overlapping instead: the later accept comes before the completion.
            actions = new List<GameAction> { Accept("a", 100.0), Accept("c", 400.0), Complete("a", 500.0) };
            Assert.Equal("Contract slots: 0 of 2 free now (1 active, 1 reserved for later).",
                ContractSentence(CareerSlotSummary.Build(actions, 200.0)));
        }

        [Fact]
        public void Build_Contracts_ADeadlineReleasesTheSlotALaterAcceptTakes()
        {
            var actions = new List<GameAction>
            {
                AcceptWithDeadline("a", 100.0, 450.0), Accept("c", 500.0),
            };
            var s = CareerSlotSummary.Build(actions, 200.0);
            AssertContractsMatchForecast(s, SharedForecast(actions, 200.0, new[]
            {
                new ContractSlotHolder("a", 100.0, 450.0),
            }));
            Assert.Equal("Contract slots: 1 of 2 free now (1 active).", ContractSentence(s));

            // The same contract with no deadline holds its slot through c's accept.
            actions = new List<GameAction> { Accept("a", 100.0), Accept("c", 500.0) };
            Assert.Equal("Contract slots: 0 of 2 free now (1 active, 1 reserved for later).",
                ContractSentence(CareerSlotSummary.Build(actions, 200.0)));
        }

        [Fact]
        public void Build_Contracts_AContractWhoseDeadlinePassedBeforeNowHoldsNoSlot()
        {
            // The deadline ran out between two past actions: the contract closed at it.
            var actions = new List<GameAction>
            {
                AcceptWithDeadline("a", 100.0, 150.0), Accept("b", 160.0),
            };
            var s = CareerSlotSummary.Build(actions, 200.0);
            Assert.Equal(1, s.Contracts.Active);
        }

        [Fact]
        public void Build_Contracts_SkipsAutoAcceptContractsLikeStock()
        {
            var actions = new List<GameAction>
            {
                Accept("a", 100.0), Accept("auto-now", 110.0), Accept("auto-later", 300.0),
            };
            Func<string, bool> isAuto = id => id.StartsWith("auto", StringComparison.Ordinal);
            var s = CareerSlotSummary.Build(actions, 200.0, isAuto);
            AssertContractsMatchForecast(s, SharedForecast(actions, 200.0, new[]
            {
                new ContractSlotHolder("a", 100.0, double.NaN),
                new ContractSlotHolder("auto-now", 110.0, double.NaN),
            }, isAutoAccept: isAuto));
            Assert.Equal("Contract slots: 1 of 2 free now (1 active).", ContractSentence(s));

            // Without the auto-accept marks every row holds a slot.
            Assert.Equal("Contract slots: 0 of 2 free now (2 active, 1 reserved for later).",
                ContractSentence(CareerSlotSummary.Build(actions, 200.0)));
        }

        [Fact]
        public void Build_Contracts_AnUpgradeAheadAddsTheSlotsLaterAcceptsNeed()
        {
            var actions = new List<GameAction>
            {
                Accept("a", 100.0),
                Upgrade("SpaceCenter/MissionControl", 2, 300.0),
                Accept("c1", 400.0), Accept("c2", 410.0), Accept("c3", 420.0),
                Accept("c4", 430.0), Accept("c5", 440.0),
            };
            var s = CareerSlotSummary.Build(actions, 200.0);
            AssertContractsMatchForecast(s, SharedForecast(actions, 200.0, new[]
            {
                new ContractSlotHolder("a", 100.0, double.NaN),
            }));
            Assert.Equal(0, s.Contracts.Reserved);
            Assert.Equal("Contract slots: 1 of 2 free now (1 active).", ContractSentence(s));

            // A sixth later accept overflows the upgraded limit by one.
            actions.Add(Accept("c6", 450.0));
            Assert.Equal("Contract slots: 0 of 2 free now (1 active, 1 reserved for later).",
                ContractSentence(CareerSlotSummary.Build(actions, 200.0)));
        }

        [Fact]
        public void Build_Contracts_MissionControlL3ReadsNoSlotLimit()
        {
            var actions = new List<GameAction>
            {
                Upgrade("SpaceCenter/MissionControl", 3, 50.0),
                Accept("a", 100.0), Accept("b", 110.0), Accept("later", 300.0),
            };
            var s = CareerSlotSummary.Build(actions, 200.0);
            Assert.Equal(3, s.MissionControlLevel);
            Assert.Equal("Contract slots: no slot limit (2 active).", ContractSentence(s));
        }

        [Fact]
        public void Build_Contracts_ALiveForecastWinsOverTheLedgerOne()
        {
            // The Timeline passes ContractSlotReservation.ForecastNow(); when it is there the
            // counts are its numbers (stock reports three active of seven, two free).
            var actions = new List<GameAction> { Accept("a", 100.0) };
            var live = new ContractSlotForecast(7, 3, 5, 2, null);
            var s = CareerSlotSummary.Build(actions, 200.0, null, live);
            AssertContractsMatchForecast(s, live);
            Assert.Equal("live", s.ContractSource);
            Assert.Equal("Contract slots: 2 of 7 free now (3 active, 2 reserved for later).",
                ContractSentence(s));
        }

        [Fact]
        public void Build_Strategies_CountActivationsDeactivationsAndTheAdministrationLevel()
        {
            // Administration L2 (three slots): A active now; B activates later while A still
            // runs (a reservation), A deactivates, C then takes A's slot.
            var actions = new List<GameAction>
            {
                Upgrade("SpaceCenter/Administration", 2, 50.0),
                Activate("A", 100.0),
                Activate("B", 300.0),
                Deactivate("A", 400.0),
                Activate("C", 500.0),
            };
            var s = CareerSlotSummary.Build(actions, 200.0);
            Assert.Equal(2, s.AdministrationLevel);
            Assert.Equal("Strategy slots: 1 of 3 free now (1 active, 1 reserved for later).",
                StrategySentence(s));
        }

        [Fact]
        public void Build_IgnoresIneffectiveRowsAndAnEmptyLedger()
        {
            var actions = new List<GameAction>
            {
                Accept("a", 100.0, effective: false),
                Activate("A", 100.0, effective: false),
                Accept("later", 300.0, effective: false),
            };
            var s = CareerSlotSummary.Build(actions, 200.0);
            Assert.Equal("Contract slots: 2 of 2 free now (0 active).", ContractSentence(s));
            Assert.Equal("Strategy slots: 1 of 1 free now (0 active).", StrategySentence(s));

            s = CareerSlotSummary.Build(null, 200.0);
            Assert.Equal("Contract slots: 2 of 2 free now (0 active).", ContractSentence(s));
            Assert.Equal(1, s.MissionControlLevel);
        }

        [Fact]
        public void FormatSnapshotForLog_NamesBothPoolsTheSourceAndTheLevels()
        {
            var actions = new List<GameAction> { Accept("a", 100.0), Accept("later", 300.0) };
            string line = CareerSlotSummary.FormatSnapshotForLog(
                CareerSlotSummary.Build(actions, 200.0), 200.0);
            Assert.Equal("liveUT=200 "
                + "contractSlots=active=1/limit=2/peak=2/reserved=1/free=0/source=ledger "
                + "strategySlots=active=0/limit=1/peak=0/reserved=0/free=1 "
                + "missionControl=L1 administration=L1", line);
        }
    }
}
