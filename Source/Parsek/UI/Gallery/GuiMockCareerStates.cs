using System;
using System.Collections.Generic;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// Catalogue states for the Career State window (both tabs plus the mode banner).
    ///
    /// <para><b>Every state is a synthetic LEDGER, not a synthetic view model.</b> Each
    /// builder assembles a <c>GameAction</c> list and a live UT and hands them to the real
    /// <see cref="CareerStateWindowUI.Build"/>, which is the same pure walk the window
    /// runs over <c>EffectiveState.ComputeELS()</c>. So the now-vs-pending split, the
    /// Timeline-end outcomes, the divergence flag and the slot counts (from the Mission
    /// Control / Administration levels) are all decided by production code - the catalogue only decides what happened in
    /// the career.</para>
    ///
    /// <para>That is also what makes the completeness guard meaningful on this window: the
    /// seven <c>GameActionType</c> branches <c>Build</c> switches on are the row variants,
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
        // The Contracts tab's three fixed columns plus Timeline end total 605px beside the
        // expanding name column (IMGUI does not reflow). 980x560 shows every catalogue
        // state whole.
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

            // There is no separate "pending-split" state: it shared a BUILDER with
            // career.banner.divergent below, so the two were one picture filed twice.
            // The split layout IS the divergent banner's own layout - a career with
            // pending contracts draws both at once - so one state carries both.
            into.Add(New("career.contracts.closing", ContractsTab,
                "Contracts active now that the recorded timeline completes, fails or "
                + "cancels before its end - the Timeline-end column, with the failure in "
                + "the alert colour.",
                new[] { "GameActionType.ContractComplete", "GameActionType.ContractFail",
                        "GameActionType.ContractCancel",
                        "ContractRow.IsClosingByTimelineEnd",
                        "TimelineEndKind.Completed", "TimelineEndKind.Failed",
                        "TimelineEndKind.Cancelled" },
                () => Build(ContractsClosing())));

            into.Add(New("career.contracts.deadline-none", ContractsTab,
                "The '--' deadline cell, which only a contract accepted with no deadline "
                + "produces.",
                new string[0],
                () => Build(ContractsNoDeadline())));

            into.Add(New("career.banner.divergent", ContractsTab,
                "The whole point of the window, and no capture has it: 'Career mode - <date> "
                + " (timeline ends <date>)' over the 'Active now: N of M slots' rows and the "
                + "in-table 'Pending in timeline (K) - ...' fold - one career produces both.",
                new[] { "CareerBanner.Divergent", "GameActionType.ContractAccept",
                        "ContractRow.IsPendingAccept" },
                () => Build(ContractsPendingSplit())));

            into.Add(New("career.contracts.slots-full", ContractsTab,
                "Active == max slots at a Mission Control above level 1, so the heading "
                + "shows the raised slot count rather than the day-one one. LIVE CELL "
                + "NOTE: that count is a NUMBER, so the state's witnesses are its own "
                + "contract titles instead - which is why its ledger shares no contract "
                + "with career.contracts.active-rows.",
                new[] { "GameActionType.ContractAccept", "GameActionType.FacilityUpgrade" },
                () => Build(ContractsSlotsFull())));

            // ---------- Strategies ----------

            into.Add(New("career.strategies.active-rows", StrategiesTab,
                "The Flow column ('Source -> Target @ pct%') has no picture at all in the "
                + "census.",
                new[] { "GameActionType.StrategyActivate" },
                () => Build(StrategiesActive())));

            into.Add(New("career.strategies.pending-split", StrategiesTab,
                "The strategies mirror of the contracts pending split.",
                new[] { "StrategyRow.IsPendingActivate" },
                () => Build(StrategiesPendingSplit())));

            into.Add(New("career.strategies.closing", StrategiesTab,
                "A strategy active now that the recorded timeline deactivates.",
                new[] { "GameActionType.StrategyDeactivate",
                        "StrategyRow.IsClosingByTimelineEnd",
                        "TimelineEndKind.Deactivated" },
                () => Build(StrategiesClosing())));

            into.Add(New("career.strategies.admin-above-one", StrategiesTab,
                "An Administration building above level 1, which is what raises the "
                + "strategy slot count. LIVE CELL NOTE: that count is a NUMBER, so the "
                + "state's witnesses are its own strategy rows instead - which is why its "
                + "ledger shares no strategy with career.strategies.active-rows.",
                new[] { "GameActionType.StrategyActivate", "GameActionType.FacilityUpgrade" },
                () => Build(StrategiesAdminUpgraded())));
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
        /// Runs the REAL VM walk over a synthetic ledger. The two modules are only
        /// null-checked, so fresh instances are what the window would hand it.
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
                CareerStateWindowUI.FormatDate);
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

        private static List<GameAction> ContractsActive()
            => new List<GameAction>
            {
                // Mission Control L2 (7 slots): a real career cannot hold more contracts
                // than its limit, and the heading prints "N of M slots" from the real walk.
                Upgrade("MissionControl", 2, 60_000.0),
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
                // Mission Control L2 (7 slots): a real career cannot hold more contracts
                // than its limit, and the heading prints "N of M slots" from the real walk.
                Upgrade("MissionControl", 2, 60_000.0),
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
            // reads off FacilityUpgrade actions - so the heading's "N of M slots" is production
            // arithmetic over this list rather than a number typed here.
            //
            // It shares NO contract with ContractsActive on purpose: two states whose
            // first row is the same contract derive the same witness set, and then nothing
            // in the read-back can tell their captures apart.
            return new List<GameAction>
            {
                Upgrade("MissionControl", 2, 80_000.0),
                Accept("ctr-survey-kerbin", 320_000.0,
                       "Survey the eastern coastline of Kerbin", 1_100_000.0),
                Accept("ctr-part-test", 340_000.0,
                       "Test the TD-12 decoupler in flight over Kerbin", 1_150_000.0),
                Accept("ctr-tourist-3", 360_000.0,
                       "Take three tourists to orbit of Kerbin", 1_200_000.0),
                Accept("ctr-satellite-1", 380_000.0,
                       "Position a satellite in a keosynchronous orbit", 1_300_000.0),
                Accept("ctr-station-1", 400_000.0,
                       "Build an orbital station around Kerbin", 1_400_000.0),
                Accept("ctr-base-1", 420_000.0,
                       "Build a base on the surface of the Mun", 1_500_000.0),
                // The seventh, so Active == max at Mission Control L2 (7 slots), which is
                // what this state claims to show.
                Accept("ctr-rover-1", 440_000.0,
                       "Drive a rover to the Mun's East Crater", 1_600_000.0),
            };
        }

        // ----- the stock strategy ids, verbatim -----
        //
        // These are the cfg NAMES from GameData/Squad/Strategies/Strategies.cfg, which is
        // what a ledger row carries and what the window's title lookup resolves against
        // (StrategySystem.Instance is consulted by StrategyId, falling back to the raw id).
        // An invented id renders as the id and the mocked row cannot draw the display
        // title a real one would - which the first version of this file did with four
        // made-up names, including "AggressiveNegotiations" for a strategy stock spells
        // "AgressiveNegotiations" (sic) and which is a CurrencyOperation with no
        // Source -> Target flow at all.
        //
        // The SOURCE and TARGET below are each strategy's own declared input / output, so
        // the mocked Flow cell reads what stock would produce rather than a plausible
        // pair. Pinned by GuiMockCareerIdShapeTests against the committed fixture data.
        private const string FundraisingCampaign = "FundraisingCampaignCfg";
        private const string AppreciationCampaign = "AppreciationCampaignCfg";
        private const string PatentsLicensing = "PatentsLicensingCfg";
        private const string OutsourcedResearch = "OutsourcedResearchCfg";
        private const string UnpaidResearchProgram = "UnpaidResearchProgramCfg";
        private const string OpenSourceTechProgram = "OpenSourceTechProgramCfg";

        private static List<GameAction> StrategiesActive()
            => new List<GameAction>
            {
                // Administration L2 (3 slots), for the same reason as the contract states:
                // two strategies do not fit day-one Administration's one slot.
                Upgrade("Administration", 2, 60_000.0),
                // Reputation -> Funds, the cfg's own input / output.
                Activate(FundraisingCampaign, 150_000.0, 0.35f,
                         StrategyResource.Reputation, StrategyResource.Funds),
                // Science -> Funds.
                Activate(PatentsLicensing, 260_000.0, 0.10f,
                         StrategyResource.Science, StrategyResource.Funds),
            };

        private static List<GameAction> StrategiesPendingSplit()
        {
            var actions = StrategiesActive();
            // Funds -> Reputation, the cfg's own input / output.
            actions.Add(Activate(AppreciationCampaign, 1_600_000.0, 0.55f,
                                 StrategyResource.Funds, StrategyResource.Reputation));
            return actions;
        }

        private static List<GameAction> StrategiesClosing()
        {
            var actions = StrategiesActive();
            actions.Add(Deactivate(PatentsLicensing, 1_100_000.0));
            return actions;
        }

        private static List<GameAction> StrategiesAdminUpgraded()
        {
            // It shares NO strategy with StrategiesActive on purpose: two states whose
            // first row is the same strategy derive the same witness set, and then nothing
            // in the read-back can tell their captures apart. The raised slot count IS
            // this state's point, and it is a number rather than a cell - so the picture
            // is the header and the witnesses are these rows.
            return new List<GameAction>
            {
                // L3 (5 slots), above the L2 the other strategy states already carry.
                Upgrade("Administration", 3, 90_000.0),
                // Funds -> Science.
                Activate(OutsourcedResearch, 150_000.0, 0.25f,
                         StrategyResource.Funds, StrategyResource.Science),
                // Reputation -> Science.
                Activate(UnpaidResearchProgram, 260_000.0, 0.45f,
                         StrategyResource.Reputation, StrategyResource.Science),
                // Science -> Reputation.
                Activate(OpenSourceTechProgram, 300_000.0, 0.60f,
                         StrategyResource.Science, StrategyResource.Reputation),
            };
        }

    }
}
