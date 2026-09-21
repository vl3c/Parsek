using System;
using System.Collections.Generic;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// Catalogue states for the Career State window (all four tabs plus the mode banner).
    ///
    /// <para><b>Every state is a synthetic LEDGER, not a synthetic view model.</b> Each
    /// builder assembles a <c>GameAction</c> list and a live UT and hands them to the real
    /// <see cref="CareerStateWindowUI.Build"/>, which is the same pure walk the window
    /// runs over <c>EffectiveState.ComputeELS()</c>. So the current-vs-projected split,
    /// the <c>(pending)</c> / <c>(closing)</c> tags, the divergence flag, the slot counts
    /// and the facility levels are all decided by production code - the catalogue only
    /// decides what happened in the career.</para>
    ///
    /// <para>That is also what makes the completeness guard meaningful on this window: the
    /// ten <c>GameActionType</c> branches <c>Build</c> switches on are the row variants,
    /// and a state claims the branch it drives.</para>
    ///
    /// <para><b>One live cell this window keeps under a mock</b>, recorded rather than
    /// hidden: <c>DrawCareerStateWindow</c> reads <c>HighLogic.CurrentGame</c> and
    /// <c>Planetarium.GetUniversalTime()</c> ABOVE the VM read, and the banner then prints
    /// the VM's own <c>LiveUT</c> - which is the mocked one. So the banner text is
    /// mocked; only the null-game fallback branch is not reachable under a mock, and no
    /// catalogue state claims it.</para>
    /// </summary>
    internal static class GuiMockCareerStates
    {
        private const string ContractsTab = "contracts";
        private const string StrategiesTab = "strategies";
        private const string FacilitiesTab = "facilities";
        private const string MilestonesTab = "milestones";

        // The Milestones tab's four fixed columns total 680px, so anything under ~700
        // clips (IMGUI does not reflow). 980x560 shows every catalogue state whole.
        private const int RectW = 980;
        private const int RectH = 560;

        /// <summary>The live UT every state is built against. A round number so the
        /// banner's "UT 500000" reads cleanly in a capture.</summary>
        private const double LiveUT = 500_000.0;

        internal static void Append(List<GuiMockState> into)
        {
            // ---------- Contracts ----------

            into.Add(New("career.contracts.active-rows", ContractsTab,
                "The populated happy path of tab 1: every census capture reads Active (0), "
                + "even on the dense operator career.",
                new[] { "GameActionType.ContractAccept" },
                () => Build(ContractsActive())));

            into.Add(New("career.contracts.pending-split", ContractsTab,
                "The divergence layout: 'Active now (N)' over 'Pending in timeline (K)'.",
                new[] { "GameActionType.ContractAccept" },
                () => Build(ContractsPendingSplit())));

            into.Add(New("career.contracts.closing", ContractsTab,
                "A contract active now that the recorded timeline completes, fails or "
                + "cancels before its end - the (closing) tag.",
                new[] { "GameActionType.ContractComplete", "GameActionType.ContractFail",
                        "GameActionType.ContractCancel" },
                () => Build(ContractsClosing())));

            into.Add(New("career.contracts.deadline-none", ContractsTab,
                "The '--' deadline cell, which only a contract accepted with no deadline "
                + "produces.",
                new string[0],
                () => Build(ContractsNoDeadline())));

            into.Add(New("career.banner.divergent", ContractsTab,
                "The whole point of the window: 'Career mode - UT N  (timeline ends at "
                + "UT M)'. No capture has it.",
                new[] { "GameActionType.ContractAccept" },
                () => Build(ContractsPendingSplit())));

            into.Add(New("career.contracts.slots-full", ContractsTab,
                "Active == max slots at a Mission Control above level 1, so the header "
                + "shows the raised slot count rather than the day-one one.",
                new[] { "GameActionType.FacilityUpgrade" },
                () => Build(ContractsSlotsFull())));

            // ---------- Strategies ----------

            into.Add(New("career.strategies.active-rows", StrategiesTab,
                "The Flow column ('Source -> Target @ pct%') has no picture at all in the "
                + "census.",
                new[] { "GameActionType.StrategyActivate" },
                () => Build(StrategiesActive())));

            into.Add(New("career.strategies.pending-split", StrategiesTab,
                "The strategies mirror of the contracts pending split.",
                new[] { "GameActionType.StrategyActivate" },
                () => Build(StrategiesPendingSplit())));

            into.Add(New("career.strategies.closing", StrategiesTab,
                "A strategy active now that the recorded timeline deactivates.",
                new[] { "GameActionType.StrategyDeactivate" },
                () => Build(StrategiesClosing())));

            into.Add(New("career.strategies.admin-above-one", StrategiesTab,
                "An Administration building above level 1, which is what raises the "
                + "strategy slot count.",
                new[] { "GameActionType.FacilityUpgrade" },
                () => Build(StrategiesAdminUpgraded())));

            // ---------- Facilities ----------

            into.Add(New("career.facilities.level-above-one", FacilitiesTab,
                "Every capture across three fixtures reads nine uniform L1 rows.",
                new[] { "GameActionType.FacilityUpgrade" },
                () => Build(FacilitiesUpgraded())));

            into.Add(New("career.facilities.upcoming-upgrade", FacilitiesTab,
                "'L2 -> L3 (upcoming)' - the projected column's only visual form.",
                new[] { "GameActionType.FacilityUpgrade" },
                () => Build(FacilitiesUpcoming())));

            into.Add(New("career.facilities.destroyed", FacilitiesTab,
                "A destroyed facility that the recorded timeline never repairs.",
                new[] { "GameActionType.FacilityDestruction" },
                () => Build(FacilitiesDestroyed(repairInTimeline: false))));

            into.Add(New("career.facilities.destroyed-repair-pending", FacilitiesTab,
                "Destroyed now, repaired later in the recorded timeline.",
                new[] { "GameActionType.FacilityRepair" },
                () => Build(FacilitiesDestroyed(repairInTimeline: true))));

            // ---------- Milestones ----------

            into.Add(New("career.milestones.pending", MilestonesTab,
                "A milestone the recorded timeline credits after live UT: the (pending) "
                + "row status.",
                new[] { "GameActionType.MilestoneAchievement" },
                () => Build(MilestonesPending())));

            into.Add(New("career.milestones.zero-reward", MilestonesTab,
                "A milestone that paid nothing, which is what the Rewards column looks "
                + "like with every award at zero.",
                new[] { "GameActionType.MilestoneAchievement" },
                () => Build(MilestonesZeroReward())));

            into.Add(New("career.milestones.divergent-count", MilestonesTab,
                "Credited now versus credited by the timeline's end, side by side.",
                new[] { "GameActionType.MilestoneAchievement" },
                () => Build(MilestonesDivergent())));
        }

        // ----- state factory -----

        private static GuiMockState New(
            string id, string tab, string note, string[] covers,
            Func<CareerStateWindowUI.CareerStateViewModel> build)
        {
            return new GuiMockState
            {
                Id = id,
                Window = GuiMockSession.CareerWindow,
                Tab = tab,
                RectW = RectW,
                RectH = RectH,
                Covers = covers,
                Note = note,
                Build = () => new GuiMockPayload
                {
                    Window = GuiMockSession.CareerWindow,
                    Career = build(),
                },
            };
        }

        /// <summary>
        /// Runs the REAL VM walk over a synthetic ledger. The four modules are read only
        /// for their slot helpers, so fresh instances are what the window would hand it.
        ///
        /// <para><b>The list is UT-SORTED first, and that is load-bearing rather than
        /// tidy.</b> <c>Build</c> walks the actions in LIST order and takes its
        /// current-state snapshot lazily at the first action past live UT, so an
        /// out-of-order list drops every later past-UT action out of the "active now"
        /// half - which reads as a builder bug in the catalogue and is really a statement
        /// about the input. The effective ledger the window walks is UT-ordered, so this
        /// makes the synthetic one the same shape.</para>
        /// </summary>
        private static CareerStateWindowUI.CareerStateViewModel Build(List<GameAction> actions)
        {
            actions.Sort((a, b) => a.UT.CompareTo(b.UT));
            return CareerStateWindowUI.Build(
                actions, LiveUT, Game.Modes.CAREER,
                new ContractsModule(), new StrategiesModule(),
                new FacilitiesModule(), new MilestonesModule());
        }

        // ----- synthetic ledgers -----

        private static GameAction Accept(string id, double ut, string title, double deadline)
            => new GameAction
            {
                Type = GameActionType.ContractAccept,
                UT = ut,
                ContractId = id,
                ContractTitle = title,
                DeadlineUT = deadline,
                Effective = true,
            };

        private static GameAction Contract(GameActionType type, string id, double ut)
            => new GameAction { Type = type, UT = ut, ContractId = id, Effective = true };

        private static GameAction Activate(string id, double ut, float commitment,
                                           StrategyResource src, StrategyResource tgt)
            => new GameAction
            {
                Type = GameActionType.StrategyActivate,
                UT = ut,
                StrategyId = id,
                SourceResource = src,
                TargetResource = tgt,
                Commitment = commitment,
                Effective = true,
            };

        private static GameAction Deactivate(string id, double ut)
            => new GameAction
            {
                Type = GameActionType.StrategyDeactivate, UT = ut, StrategyId = id,
                Effective = true,
            };

        private static GameAction Upgrade(string facilityId, int toLevel, double ut)
            => new GameAction
            {
                Type = GameActionType.FacilityUpgrade, UT = ut,
                FacilityId = facilityId, ToLevel = toLevel, Effective = true,
            };

        private static GameAction Destroy(string facilityId, double ut)
            => new GameAction
            {
                Type = GameActionType.FacilityDestruction, UT = ut,
                FacilityId = facilityId, Effective = true,
            };

        private static GameAction Repair(string facilityId, double ut)
            => new GameAction
            {
                Type = GameActionType.FacilityRepair, UT = ut,
                FacilityId = facilityId, Effective = true,
            };

        private static GameAction Milestone(string id, double ut,
                                            float funds, float rep, float science)
            => new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                MilestoneId = id,
                MilestoneFundsAwarded = funds,
                MilestoneRepAwarded = rep,
                MilestoneScienceAwarded = science,
                Effective = true,
            };

        private static List<GameAction> ContractsActive()
            => new List<GameAction>
            {
                Accept("ctr-mun-flyby", 120_000.0, "Perform a flyby of the Mun", 900_000.0),
                Accept("ctr-minmus-sci", 240_000.0, "Transmit science from Minmus", 1_200_000.0),
                Accept("ctr-rescue-1", 300_000.0, "Rescue Dilbert Kerman from orbit of Kerbin",
                       double.NaN),
            };

        private static List<GameAction> ContractsPendingSplit()
        {
            var actions = ContractsActive();
            actions.Add(Accept("ctr-duna-survey", 1_400_000.0,
                               "Survey the surface of Duna", 4_000_000.0));
            actions.Add(Accept("ctr-eve-probe", 1_800_000.0,
                               "Put a probe into orbit of Eve", 5_000_000.0));
            return actions;
        }

        private static List<GameAction> ContractsClosing()
            => new List<GameAction>
            {
                Accept("ctr-mun-flyby", 120_000.0, "Perform a flyby of the Mun", 900_000.0),
                Contract(GameActionType.ContractComplete, "ctr-mun-flyby", 800_000.0),
                Accept("ctr-minmus-sci", 240_000.0, "Transmit science from Minmus", 1_200_000.0),
                Contract(GameActionType.ContractFail, "ctr-minmus-sci", 1_250_000.0),
                Accept("ctr-tourist-2", 300_000.0, "Take Dilbert Kerman on a suborbital hop",
                       1_000_000.0),
                Contract(GameActionType.ContractCancel, "ctr-tourist-2", 700_000.0),
            };

        private static List<GameAction> ContractsNoDeadline()
            => new List<GameAction>
            {
                Accept("ctr-explore-kerbin", 60_000.0, "Explore Kerbin", double.NaN),
                Accept("ctr-explore-mun", 90_000.0, "Explore the Mun", double.NaN),
            };

        private static List<GameAction> ContractsSlotsFull()
        {
            // The slot count comes from the Mission Control LEVEL, which the ledger walk
            // reads off FacilityUpgrade actions - so the header's "N / M" is production
            // arithmetic over this list rather than a number typed here.
            var actions = new List<GameAction> { Upgrade("MissionControl", 2, 80_000.0) };
            actions.AddRange(ContractsActive());
            actions.Add(Accept("ctr-survey-kerbin", 320_000.0,
                               "Survey the eastern coastline of Kerbin", 1_100_000.0));
            actions.Add(Accept("ctr-part-test", 340_000.0,
                               "Test the TD-12 decoupler in flight over Kerbin", 1_150_000.0));
            actions.Add(Accept("ctr-tourist-3", 360_000.0,
                               "Take three tourists to orbit of Kerbin", 1_200_000.0));
            actions.Add(Accept("ctr-satellite-1", 380_000.0,
                               "Position a satellite in a keosynchronous orbit", 1_300_000.0));
            return actions;
        }

        private static List<GameAction> StrategiesActive()
            => new List<GameAction>
            {
                Activate("AggressiveNegotiations", 150_000.0, 0.35f,
                         StrategyResource.Reputation, StrategyResource.Funds),
                Activate("FundraisingCampaign", 260_000.0, 0.10f,
                         StrategyResource.Science, StrategyResource.Funds),
            };

        private static List<GameAction> StrategiesPendingSplit()
        {
            var actions = StrategiesActive();
            actions.Add(Activate("PatentsLicensing", 1_600_000.0, 0.55f,
                                 StrategyResource.Science, StrategyResource.Funds));
            return actions;
        }

        private static List<GameAction> StrategiesClosing()
        {
            var actions = StrategiesActive();
            actions.Add(Deactivate("FundraisingCampaign", 1_100_000.0));
            return actions;
        }

        private static List<GameAction> StrategiesAdminUpgraded()
        {
            var actions = new List<GameAction> { Upgrade("Administration", 2, 90_000.0) };
            actions.AddRange(StrategiesActive());
            actions.Add(Activate("OutsourcedRnD", 300_000.0, 0.25f,
                                 StrategyResource.Funds, StrategyResource.Science));
            return actions;
        }

        private static List<GameAction> FacilitiesUpgraded()
            => new List<GameAction>
            {
                Upgrade("VehicleAssemblyBuilding", 3, 100_000.0),
                Upgrade("LaunchPad", 2, 120_000.0),
                Upgrade("TrackingStation", 2, 200_000.0),
                Upgrade("ResearchAndDevelopment", 3, 260_000.0),
                Upgrade("AstronautComplex", 2, 300_000.0),
            };

        private static List<GameAction> FacilitiesUpcoming()
        {
            var actions = FacilitiesUpgraded();
            // After live UT, so the row reads "L2 -> L3 (upcoming)" in amber.
            actions.Add(Upgrade("LaunchPad", 3, 1_500_000.0));
            actions.Add(Upgrade("Runway", 2, 1_700_000.0));
            return actions;
        }

        private static List<GameAction> FacilitiesDestroyed(bool repairInTimeline)
        {
            var actions = FacilitiesUpgraded();
            actions.Add(Destroy("LaunchPad", 400_000.0));
            if (repairInTimeline)
                actions.Add(Repair("LaunchPad", 1_600_000.0));
            return actions;
        }

        private static List<GameAction> MilestonesCredited()
            => new List<GameAction>
            {
                Milestone("FirstLaunch", 40_000.0, 0f, 2f, 0f),
                Milestone("FirstOrbitKerbin", 90_000.0, 8_000f, 6f, 12f),
                Milestone("FirstFlybyMun", 150_000.0, 14_000f, 9f, 24f),
            };

        private static List<GameAction> MilestonesPending()
        {
            var actions = MilestonesCredited();
            actions.Add(Milestone("FirstLandingMun", 1_400_000.0, 22_000f, 18f, 45f));
            actions.Add(Milestone("FirstOrbitMinmus", 1_700_000.0, 16_000f, 11f, 30f));
            return actions;
        }

        private static List<GameAction> MilestonesZeroReward()
            => new List<GameAction>
            {
                Milestone("FirstLaunch", 40_000.0, 0f, 0f, 0f),
                Milestone("FirstSuborbitalKerbin", 60_000.0, 0f, 0f, 0f),
            };

        private static List<GameAction> MilestonesDivergent()
        {
            var actions = MilestonesCredited();
            actions.Add(Milestone("FirstLandingMun", 1_400_000.0, 22_000f, 18f, 45f));
            return actions;
        }
    }
}
