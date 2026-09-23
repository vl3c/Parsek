using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Timeline's two-row filter area and its Career view: the category and subject
    /// every row carries (from the ledger action type, never the display type), which
    /// views and row-2 controls each game mode draws, the Time button's label, the row
    /// filter per view (the time range included on category views) and the career
    /// cross-link's scroll target.
    /// </summary>
    [Collection("Sequential")]
    public class TimelineCareerFiltersTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TimelineCareerFiltersTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            FacilityDisplayNames.FacilityNameLookupForTesting = null;
        }

        public void Dispose()
        {
            FacilityDisplayNames.FacilityNameLookupForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Action(GameActionType type, double ut = 100)
        {
            return new GameAction
            {
                UT = ut, Type = type, Effective = true,
                ActionId = Guid.NewGuid().ToString("N")
            };
        }

        private static TimelineEntry Entry(TimelineCareerCategory category, double ut,
            SignificanceTier tier = SignificanceTier.T2, bool isPlayerAction = true,
            TimelineSource source = TimelineSource.GameAction, string subject = null)
        {
            return new TimelineEntry
            {
                UT = ut, Tier = tier, IsPlayerAction = isPlayerAction, Source = source,
                CareerCategory = category, CareerSubjectId = subject,
                Type = TimelineEntryType.FundsSpending
            };
        }

        // ------------------------------------------------------------------
        // Classification
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(GameActionType.ContractAccept, TimelineCareerCategory.Contracts)]
        [InlineData(GameActionType.ContractComplete, TimelineCareerCategory.Contracts)]
        [InlineData(GameActionType.ContractFail, TimelineCareerCategory.Contracts)]
        [InlineData(GameActionType.ContractCancel, TimelineCareerCategory.Contracts)]
        [InlineData(GameActionType.StrategyActivate, TimelineCareerCategory.Strategies)]
        [InlineData(GameActionType.StrategyDeactivate, TimelineCareerCategory.Strategies)]
        [InlineData(GameActionType.FacilityUpgrade, TimelineCareerCategory.Facilities)]
        [InlineData(GameActionType.FacilityDestruction, TimelineCareerCategory.Facilities)]
        [InlineData(GameActionType.FacilityRepair, TimelineCareerCategory.Facilities)]
        [InlineData(GameActionType.MilestoneAchievement, TimelineCareerCategory.Milestones)]
        [InlineData(GameActionType.FundsSpending, TimelineCareerCategory.None)]
        [InlineData(GameActionType.FundsEarning, TimelineCareerCategory.None)]
        [InlineData(GameActionType.ScienceEarning, TimelineCareerCategory.None)]
        [InlineData(GameActionType.KerbalHire, TimelineCareerCategory.None)]
        [InlineData(GameActionType.FundsInitial, TimelineCareerCategory.None)]
        [InlineData(GameActionType.ReputationEarning, TimelineCareerCategory.None)]
        public void Classify_MapsEachLedgerTypeToItsCategory(GameActionType type,
            TimelineCareerCategory expected)
        {
            Assert.Equal(expected, TimelineCareerCategories.Classify(Action(type)));
        }

        [Fact]
        public void Classify_TechIsAScienceSpendThatNamesANode()
        {
            var unlock = Action(GameActionType.ScienceSpending);
            unlock.NodeId = "basicRocketry";
            Assert.Equal(TimelineCareerCategory.Tech, TimelineCareerCategories.Classify(unlock));

            var nodeless = Action(GameActionType.ScienceSpending);
            Assert.Equal(TimelineCareerCategory.None, TimelineCareerCategories.Classify(nodeless));
        }

        // catches: a Tech filter keyed on the display TimelineEntryType, which maps the
        // strategy currency-exchange science legs onto ScienceSpending / ScienceEarning.
        [Theory]
        [InlineData(GameActionType.StrategyScienceDebit, TimelineEntryType.ScienceSpending)]
        [InlineData(GameActionType.StrategyScienceCredit, TimelineEntryType.ScienceEarning)]
        public void Classify_StrategyScienceLegsAreNeverTech(GameActionType type,
            TimelineEntryType displayType)
        {
            Assert.Equal(displayType, TimelineEntryDisplay.MapGameActionType(type));
            var leg = Action(type);
            leg.StrategyId = "UnpaidInterns";
            Assert.Equal(TimelineCareerCategory.None, TimelineCareerCategories.Classify(leg));
        }

        [Fact]
        public void Classify_NullAction_IsNone()
        {
            Assert.Equal(TimelineCareerCategory.None, TimelineCareerCategories.Classify(null));
        }

        [Fact]
        public void ResolveSubjectId_NamesTheSubjectOfEachCategory()
        {
            var accept = Action(GameActionType.ContractAccept);
            accept.ContractId = "c-guid";
            Assert.Equal("c-guid", TimelineCareerCategories.ResolveSubjectId(
                accept, TimelineCareerCategory.Contracts));

            var strategy = Action(GameActionType.StrategyActivate);
            strategy.StrategyId = "AggressiveNeg";
            Assert.Equal("AggressiveNeg", TimelineCareerCategories.ResolveSubjectId(
                strategy, TimelineCareerCategory.Strategies));

            var repair = Action(GameActionType.FacilityRepair);
            repair.FacilityId = "SpaceCenter/LaunchPad/Facility/LaunchPadMain";
            Assert.Equal("LaunchPad", TimelineCareerCategories.ResolveSubjectId(
                repair, TimelineCareerCategory.Facilities));

            var upgrade = Action(GameActionType.FacilityUpgrade);
            upgrade.FacilityId = "SpaceCenter/VehicleAssemblyBuilding";
            Assert.Equal("VehicleAssemblyBuilding", TimelineCareerCategories.ResolveSubjectId(
                upgrade, TimelineCareerCategory.Facilities));

            var milestone = Action(GameActionType.MilestoneAchievement);
            milestone.MilestoneId = "RecordsSpeed";
            Assert.Equal("RecordsSpeed", TimelineCareerCategories.ResolveSubjectId(
                milestone, TimelineCareerCategory.Milestones));

            var tech = Action(GameActionType.ScienceSpending);
            tech.NodeId = "basicRocketry";
            Assert.Equal("basicRocketry", TimelineCareerCategories.ResolveSubjectId(
                tech, TimelineCareerCategory.Tech));
        }

        [Fact]
        public void ResolveSubjectId_NoneOrMissingKey_IsNull()
        {
            Assert.Null(TimelineCareerCategories.ResolveSubjectId(
                Action(GameActionType.FundsEarning), TimelineCareerCategory.None));
            Assert.Null(TimelineCareerCategories.ResolveSubjectId(
                Action(GameActionType.ContractAccept), TimelineCareerCategory.Contracts));
            Assert.Null(TimelineCareerCategories.ResolveSubjectId(null, TimelineCareerCategory.Tech));
        }

        // ------------------------------------------------------------------
        // Builder stamping
        // ------------------------------------------------------------------

        [Fact]
        public void Build_StampsCategoryAndSubjectOnLedgerRows_AndLogsTheSummary()
        {
            var accept = Action(GameActionType.ContractAccept, 100);
            accept.ContractId = "c1";
            accept.ContractTitle = "Orbit Kerbin";
            var tech = Action(GameActionType.ScienceSpending, 110);
            tech.NodeId = "basicRocketry";
            tech.Cost = 5;
            var debit = Action(GameActionType.StrategyScienceDebit, 120);
            debit.StrategyId = "UnpaidInterns";
            var funds = Action(GameActionType.FundsEarning, 130);
            funds.FundsAwarded = 100;

            List<TimelineEntry> entries = TimelineBuilder.Build(
                new List<Recording>(), new List<GameAction> { accept, tech, debit, funds },
                new List<Milestone>(), _ => true, Game.Modes.CAREER);

            TimelineEntry acceptRow = entries.Single(e => e.Type == TimelineEntryType.ContractAccept);
            Assert.Equal(TimelineCareerCategory.Contracts, acceptRow.CareerCategory);
            Assert.Equal("c1", acceptRow.CareerSubjectId);

            TimelineEntry techRow = entries.Single(e => e.UT == 110);
            Assert.Equal(TimelineCareerCategory.Tech, techRow.CareerCategory);
            Assert.Equal("basicRocketry", techRow.CareerSubjectId);

            // Same display type as the tech row, never in the Tech category.
            TimelineEntry debitRow = entries.Single(e => e.UT == 120);
            Assert.Equal(TimelineEntryType.ScienceSpending, debitRow.Type);
            Assert.Equal(TimelineCareerCategory.None, debitRow.CareerCategory);
            Assert.Null(debitRow.CareerSubjectId);

            Assert.Equal(TimelineCareerCategory.None,
                entries.Single(e => e.UT == 130).CareerCategory);

            Assert.Contains(logLines, l => l.Contains("[Timeline]")
                && l.Contains("Career categories: contracts=1 strategies=0 facilities=0 "
                              + "milestones=0 tech=1 none=2"));
        }

        [Fact]
        public void Build_CompactedFacilityRowsKeepTheFacilitySubject()
        {
            var a = Action(GameActionType.FacilityRepair, 200);
            a.FacilityId = "SpaceCenter/Runway/Facility/Runway1";
            var b = Action(GameActionType.FacilityRepair, 200);
            b.FacilityId = "SpaceCenter/Runway/Facility/Runway2";

            List<TimelineEntry> entries = TimelineBuilder.Build(
                new List<Recording>(), new List<GameAction> { a, b },
                new List<Milestone>(), _ => true, Game.Modes.CAREER);

            TimelineEntry row = Assert.Single(entries);
            Assert.Equal(TimelineCareerCategory.Facilities, row.CareerCategory);
            Assert.Equal("Runway", row.CareerSubjectId);
        }

        // ------------------------------------------------------------------
        // Game-mode gating
        // ------------------------------------------------------------------

        [Fact]
        public void CareerModeShowsAllFive_ScienceShowsThree_SandboxNone()
        {
            Assert.Equal(TimelineCareerCategories.Ordered,
                TimelineCareerCategories.AvailableInMode(Game.Modes.CAREER));
            Assert.Equal(new[]
                {
                    TimelineCareerCategory.Facilities, TimelineCareerCategory.Milestones,
                    TimelineCareerCategory.Tech
                },
                TimelineCareerCategories.AvailableInMode(Game.Modes.SCIENCE_SANDBOX));
            Assert.Empty(TimelineCareerCategories.AvailableInMode(Game.Modes.SANDBOX));
            Assert.Empty(TimelineCareerCategories.AvailableInMode(Game.Modes.MISSION));
            // No game loaded: nothing gated.
            Assert.Equal(TimelineCareerCategories.Ordered,
                TimelineCareerCategories.AvailableInMode(null));
            Assert.False(TimelineCareerCategories.IsAvailableInMode(
                TimelineCareerCategory.None, Game.Modes.CAREER));
        }

        [Fact]
        public void CareerButton_DrawnWhereACategoryExists()
        {
            Assert.True(TimelineWindowUI.ShouldDrawCareerViewButton(Game.Modes.CAREER));
            Assert.True(TimelineWindowUI.ShouldDrawCareerViewButton(Game.Modes.SCIENCE_SANDBOX));
            Assert.False(TimelineWindowUI.ShouldDrawCareerViewButton(Game.Modes.SANDBOX));
        }

        [Fact]
        public void ResolveRemembered_KeepsTheLastCategoryWhenTheModeShowsIt()
        {
            Assert.Equal(TimelineCareerCategory.Tech, TimelineCareerCategories.ResolveRemembered(
                TimelineCareerCategory.Tech, Game.Modes.CAREER));
            Assert.Equal(TimelineCareerCategory.Contracts, TimelineCareerCategories.ResolveRemembered(
                TimelineCareerCategory.None, Game.Modes.CAREER));
            // Science has no contracts: the first category it shows.
            Assert.Equal(TimelineCareerCategory.Facilities, TimelineCareerCategories.ResolveRemembered(
                TimelineCareerCategory.Contracts, Game.Modes.SCIENCE_SANDBOX));
            Assert.Equal(TimelineCareerCategory.None, TimelineCareerCategories.ResolveRemembered(
                TimelineCareerCategory.Tech, Game.Modes.SANDBOX));
        }

        [Fact]
        public void ModeToken_IsLowerCase()
        {
            Assert.Equal("career", TimelineCareerCategories.ModeToken(Game.Modes.CAREER));
            Assert.Equal("science", TimelineCareerCategories.ModeToken(Game.Modes.SCIENCE_SANDBOX));
            Assert.Equal("sandbox", TimelineCareerCategories.ModeToken(Game.Modes.SANDBOX));
            Assert.Equal("unknown", TimelineCareerCategories.ModeToken(null));
        }

        // ------------------------------------------------------------------
        // Views and row 2
        // ------------------------------------------------------------------

        [Fact]
        public void ViewsAndCategoriesRoundTrip_AndTheViewOrderIsTheSeamTabOrder()
        {
            Assert.True(TestCommands.TestCommandUiAction.TryResolveWindow(
                TestCommands.TestCommandUiAction.TimelineWindow,
                out TestCommands.UiWindowSpec spec, out string _));
            string[] expectedTokens =
            {
                "overview", "details", "rewindff", "refly",
                "contracts", "strategies", "facilities", "milestones", "tech"
            };
            var views = (TimelineWindowUI.TimelineTierFilterMode[])
                Enum.GetValues(typeof(TimelineWindowUI.TimelineTierFilterMode));
            Assert.Equal(expectedTokens.Length, views.Length);
            for (int i = 0; i < views.Length; i++)
            {
                Assert.Equal(i, (int)views[i]);
                Assert.Equal(expectedTokens[i], TestCommands.TestCommandUiAction.TabTokenAt(spec, i));
            }

            foreach (TimelineCareerCategory c in TimelineCareerCategories.Ordered)
            {
                TimelineWindowUI.TimelineTierFilterMode view = TimelineWindowUI.ViewOfCategory(c);
                Assert.True(TimelineWindowUI.IsCareerCategoryView(view));
                Assert.Equal(c, TimelineWindowUI.CategoryOfView(view));
                Assert.Equal(c.ToString().ToLowerInvariant(),
                    TestCommands.TestCommandUiAction.TabTokenAt(spec, (int)view));
            }
            Assert.Equal(TimelineWindowUI.TimelineTierFilterMode.Overview,
                TimelineWindowUI.ViewOfCategory(TimelineCareerCategory.None));
            Assert.False(TimelineWindowUI.IsCareerCategoryView(
                TimelineWindowUI.TimelineTierFilterMode.ReFly));
        }

        [Theory]
        [InlineData(0, 0)] // Overview -> SourcesAndArchived
        [InlineData(1, 0)] // Details -> SourcesAndArchived
        [InlineData(2, 1)] // Rewind/FF -> ArchivedOnly
        [InlineData(3, 1)] // Re-Fly -> ArchivedOnly
        [InlineData(4, 2)] // Contracts -> Categories
        [InlineData(8, 2)] // Tech -> Categories
        public void ContextRow_PerView(int mode, int expected)
        {
            Assert.Equal((TimelineWindowUI.TimelineContextRow)expected,
                TimelineWindowUI.ResolveContextRow((TimelineWindowUI.TimelineTierFilterMode)mode));
        }

        [Fact]
        public void ViewIndexAvailability_GatesCategoriesByGameMode()
        {
            int contracts = (int)TimelineWindowUI.TimelineTierFilterMode.Contracts;
            int facilities = (int)TimelineWindowUI.TimelineTierFilterMode.Facilities;
            int overview = (int)TimelineWindowUI.TimelineTierFilterMode.Overview;

            Assert.True(TimelineWindowUI.IsViewIndexAvailableInMode(contracts, Game.Modes.CAREER));
            Assert.False(TimelineWindowUI.IsViewIndexAvailableInMode(contracts, Game.Modes.SCIENCE_SANDBOX));
            Assert.True(TimelineWindowUI.IsViewIndexAvailableInMode(facilities, Game.Modes.SCIENCE_SANDBOX));
            Assert.False(TimelineWindowUI.IsViewIndexAvailableInMode(facilities, Game.Modes.SANDBOX));
            Assert.True(TimelineWindowUI.IsViewIndexAvailableInMode(overview, Game.Modes.SANDBOX));
            Assert.False(TimelineWindowUI.IsViewIndexAvailableInMode(-1, Game.Modes.CAREER));
            Assert.False(TimelineWindowUI.IsViewIndexAvailableInMode(9, Game.Modes.CAREER));
        }

        [Fact]
        public void ResolveViewForMode_FallsBackToAShownCategoryElseOverview()
        {
            var contracts = TimelineWindowUI.TimelineTierFilterMode.Contracts;
            Assert.Equal(contracts, TimelineWindowUI.ResolveViewForMode(
                contracts, Game.Modes.CAREER, TimelineCareerCategory.Contracts));
            Assert.Equal(TimelineWindowUI.TimelineTierFilterMode.Tech,
                TimelineWindowUI.ResolveViewForMode(
                    contracts, Game.Modes.SCIENCE_SANDBOX, TimelineCareerCategory.Tech));
            Assert.Equal(TimelineWindowUI.TimelineTierFilterMode.Facilities,
                TimelineWindowUI.ResolveViewForMode(
                    contracts, Game.Modes.SCIENCE_SANDBOX, TimelineCareerCategory.Contracts));
            Assert.Equal(TimelineWindowUI.TimelineTierFilterMode.Overview,
                TimelineWindowUI.ResolveViewForMode(
                    contracts, Game.Modes.SANDBOX, TimelineCareerCategory.Contracts));
            Assert.Equal(TimelineWindowUI.TimelineTierFilterMode.ReFly,
                TimelineWindowUI.ResolveViewForMode(
                    TimelineWindowUI.TimelineTierFilterMode.ReFly, Game.Modes.SANDBOX,
                    TimelineCareerCategory.Contracts));
        }

        [Fact]
        public void TierIndexSetter_RemembersTheCategory_AndIgnoresOutOfRange()
        {
            var window = new TimelineWindowUI(null);
            window.TierFilterModeIndexForTesting = (int)TimelineWindowUI.TimelineTierFilterMode.Milestones;
            Assert.Equal((int)TimelineWindowUI.TimelineTierFilterMode.Milestones,
                window.TierFilterModeIndexForTesting);
            Assert.Equal(TimelineCareerCategory.Milestones, window.LastCareerCategoryForTesting);

            // A non-career view leaves the remembered category alone.
            window.TierFilterModeIndexForTesting = (int)TimelineWindowUI.TimelineTierFilterMode.Details;
            Assert.Equal(TimelineCareerCategory.Milestones, window.LastCareerCategoryForTesting);

            window.TierFilterModeIndexForTesting = 9;
            Assert.Equal((int)TimelineWindowUI.TimelineTierFilterMode.Details,
                window.TierFilterModeIndexForTesting);
        }

        // ------------------------------------------------------------------
        // The Time button
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(false, null, "Time: All")]
        [InlineData(false, "Last 7d", "Time: All")]
        [InlineData(true, "Last Day", "Time: Last Day")]
        [InlineData(true, "Last 7d", "Time: Last 7d")]
        [InlineData(true, "Last 30d", "Time: Last 30d")]
        [InlineData(true, "This Year", "Time: This Year")]
        [InlineData(true, null, "Time: Custom")]
        public void TimeButtonLabel_NamesTheActiveRange(bool active, string preset, string expected)
        {
            Assert.Equal(expected, TimelineWindowUI.FormatTimeButtonLabel(active, preset));
        }

        [Fact]
        public void TimeButton_LitWhileTheFoldIsOpenOrARangeIsActive()
        {
            Assert.False(TimelineWindowUI.IsTimeButtonLit(false, false));
            Assert.True(TimelineWindowUI.IsTimeButtonLit(true, false));
            Assert.True(TimelineWindowUI.IsTimeButtonLit(false, true));
        }

        [Fact]
        public void CustomRangeStateKey_IsTheTimeFold()
        {
            var window = new TimelineWindowUI(null);
            Assert.False(window.ShowCustomRangeForTesting);
            window.ShowCustomRangeForTesting = true;
            Assert.True(window.ShowCustomRangeForTesting);
        }

        // ------------------------------------------------------------------
        // Row filter
        // ------------------------------------------------------------------

        private static bool Visible(TimelineWindowUI.TimelineTierFilterMode mode, TimelineEntry e,
            bool actionable = true, bool rec = true, bool act = true, bool evt = true,
            double? min = null, double? max = null)
            => TimelineWindowUI.IsEntryVisibleInView(mode, e, actionable, rec, act, evt, min, max);

        [Fact]
        public void CategoryView_ShowsEveryRowOfItsCategory_BothTiersAndBothSources()
        {
            var contracts = TimelineWindowUI.TimelineTierFilterMode.Contracts;
            var acceptT2 = Entry(TimelineCareerCategory.Contracts, 100, SignificanceTier.T2, isPlayerAction: true);
            var completeT1 = Entry(TimelineCareerCategory.Contracts, 200, SignificanceTier.T1, isPlayerAction: false);
            Assert.True(Visible(contracts, acceptT2));
            Assert.True(Visible(contracts, completeT1));
            // The source toggles are ignored in a category view.
            Assert.True(Visible(contracts, acceptT2, rec: false, act: false, evt: false));
            Assert.True(Visible(contracts, completeT1, rec: false, act: false, evt: false));
        }

        [Fact]
        public void CategoryView_HidesOtherCategoriesAndUncategorisedRows()
        {
            var tech = TimelineWindowUI.TimelineTierFilterMode.Tech;
            Assert.False(Visible(tech, Entry(TimelineCareerCategory.Contracts, 100)));
            Assert.False(Visible(tech, Entry(TimelineCareerCategory.None, 100)));
            Assert.False(Visible(tech, Entry(TimelineCareerCategory.None, 100,
                SignificanceTier.T1, source: TimelineSource.Recording)));
            Assert.True(Visible(tech, Entry(TimelineCareerCategory.Tech, 100)));
        }

        // Owner ruling: the time range applies to category views too; a Last N range ends
        // at now and so hides the future (pending) rows.
        [Fact]
        public void CategoryView_HonoursTheTimeRange_LastNHidesFutureRows()
        {
            double now = 50 * 21600.0;
            Assert.True(TimeRangeFilterLogic.TryResolvePresetRange(
                "Last 7d", now, out double min, out double max));
            var milestones = TimelineWindowUI.TimelineTierFilterMode.Milestones;
            var past = Entry(TimelineCareerCategory.Milestones, now - 21600.0, SignificanceTier.T1, false);
            var future = Entry(TimelineCareerCategory.Milestones, now + 21600.0, SignificanceTier.T1, false);
            var old = Entry(TimelineCareerCategory.Milestones, now - 30 * 21600.0, SignificanceTier.T1, false);

            Assert.True(Visible(milestones, past, min: min, max: max));
            Assert.False(Visible(milestones, future, min: min, max: max));
            Assert.False(Visible(milestones, old, min: min, max: max));
            // With no range the future row shows (below the "now" divider).
            Assert.True(Visible(milestones, future));
        }

        [Fact]
        public void OverviewAndDetails_KeepTheirTierAndSourceRules()
        {
            var overview = TimelineWindowUI.TimelineTierFilterMode.Overview;
            var details = TimelineWindowUI.TimelineTierFilterMode.Details;
            var t2Action = Entry(TimelineCareerCategory.Contracts, 100, SignificanceTier.T2, true);
            var t1Event = Entry(TimelineCareerCategory.Milestones, 100, SignificanceTier.T1, false);

            Assert.False(Visible(overview, t2Action));
            Assert.True(Visible(details, t2Action));
            Assert.True(Visible(overview, t1Event));
            Assert.False(Visible(overview, t1Event, evt: false));
            Assert.False(Visible(details, t2Action, act: false));
            Assert.False(Visible(details, t2Action, min: 150, max: 300));
        }

        [Fact]
        public void ActionViews_ShowOnlyActionableRows_AnyTier()
        {
            var rewind = TimelineWindowUI.TimelineTierFilterMode.RewindOrFastForward;
            var refly = TimelineWindowUI.TimelineTierFilterMode.ReFly;
            var launch = Entry(TimelineCareerCategory.None, 100, SignificanceTier.T2,
                source: TimelineSource.Recording);
            Assert.True(Visible(rewind, launch, actionable: true));
            Assert.False(Visible(rewind, launch, actionable: false));
            Assert.True(Visible(refly, launch, actionable: true));
            Assert.False(Visible(refly, launch, actionable: false));
        }

        [Fact]
        public void NullEntry_IsNeverVisible()
        {
            Assert.False(Visible(TimelineWindowUI.TimelineTierFilterMode.Details, null));
        }

        // ------------------------------------------------------------------
        // Scroll target
        // ------------------------------------------------------------------

        [Fact]
        public void FindScrollTargetRow_CountsOnlyVisibleRows_AndMatchesTheSubject()
        {
            var rows = new List<TimelineEntry>
            {
                Entry(TimelineCareerCategory.Tech, 10, subject: "basicRocketry"),
                Entry(TimelineCareerCategory.Contracts, 20, subject: "other"),
                Entry(TimelineCareerCategory.Contracts, 30, subject: "c1"),
                Entry(TimelineCareerCategory.Contracts, 40, subject: "c1"),
            };
            Func<TimelineEntry, bool> contractsVisible = e =>
                Visible(TimelineWindowUI.TimelineTierFilterMode.Contracts, e);

            var target = TimelineWindowUI.TimelineScrollTarget.ForCareerSubject(
                TimelineCareerCategory.Contracts, "c1");
            // The hidden Tech row does not count: "other" is visible row 0, c1's accept row 1.
            Assert.Equal(1, TimelineWindowUI.FindScrollTargetRow(rows, contractsVisible, target));

            // A subject in another category is not a match even with the same id.
            var wrongCategory = TimelineWindowUI.TimelineScrollTarget.ForCareerSubject(
                TimelineCareerCategory.Tech, "c1");
            Assert.Equal(-1, TimelineWindowUI.FindScrollTargetRow(rows, _ => true, wrongCategory));

            // Hidden by the view: not found.
            var techTarget = TimelineWindowUI.TimelineScrollTarget.ForCareerSubject(
                TimelineCareerCategory.Tech, "basicRocketry");
            Assert.Equal(-1, TimelineWindowUI.FindScrollTargetRow(rows, contractsVisible, techTarget));
            Assert.Equal(0, TimelineWindowUI.FindScrollTargetRow(rows, _ => true, techTarget));
        }

        [Fact]
        public void FindScrollTargetRow_RecordingTargetMatchesItsRecordingStartRow()
        {
            var rows = new List<TimelineEntry>
            {
                new TimelineEntry { UT = 1, Type = TimelineEntryType.VesselSpawn, RecordingId = "r1" },
                new TimelineEntry { UT = 2, Type = TimelineEntryType.RecordingStart, RecordingId = "r1" },
            };
            Assert.Equal(1, TimelineWindowUI.FindScrollTargetRow(rows, _ => true,
                TimelineWindowUI.TimelineScrollTarget.ForRecording("r1")));
            Assert.Equal(-1, TimelineWindowUI.FindScrollTargetRow(rows, _ => true,
                TimelineWindowUI.TimelineScrollTarget.ForRecording("r2")));
        }

        [Fact]
        public void EmptyScrollTargets_MatchNothing()
        {
            var rows = new List<TimelineEntry> { Entry(TimelineCareerCategory.Contracts, 1, subject: null) };
            Assert.True(default(TimelineWindowUI.TimelineScrollTarget).IsEmpty);
            Assert.True(TimelineWindowUI.TimelineScrollTarget.ForCareerSubject(
                TimelineCareerCategory.Contracts, null).IsEmpty);
            Assert.Equal(-1, TimelineWindowUI.FindScrollTargetRow(rows, _ => true,
                TimelineWindowUI.TimelineScrollTarget.ForCareerSubject(TimelineCareerCategory.Contracts, "")));
            Assert.Equal(-1, TimelineWindowUI.FindScrollTargetRow(null, _ => true,
                TimelineWindowUI.TimelineScrollTarget.ForRecording("r1")));
        }

        [Fact]
        public void ScrollToCareerSubject_OpensTheWindowOnTheCategoryView()
        {
            var window = new TimelineWindowUI(null);
            Assert.False(window.IsOpen);
            window.ScrollToCareerSubject(TimelineCareerCategory.Strategies, "AggressiveNeg",
                Game.Modes.CAREER);
            Assert.True(window.IsOpen);
            Assert.Equal((int)TimelineWindowUI.TimelineTierFilterMode.Strategies,
                window.TierFilterModeIndexForTesting);
            Assert.Equal(TimelineCareerCategory.Strategies, window.LastCareerCategoryForTesting);
            Assert.Contains(logLines, l => l.Contains("[Timeline]")
                && l.Contains("Cross-link: scroll requested for category=Strategies subject=AggressiveNeg")
                && l.Contains("viewSwitched=True"));
        }

        [Fact]
        public void ScrollToCareerSubject_HiddenCategoryKeepsTheView()
        {
            var window = new TimelineWindowUI(null);
            window.TierFilterModeIndexForTesting = (int)TimelineWindowUI.TimelineTierFilterMode.Details;
            window.ScrollToCareerSubject(TimelineCareerCategory.Contracts, "c1",
                Game.Modes.SCIENCE_SANDBOX);
            Assert.True(window.IsOpen);
            Assert.Equal((int)TimelineWindowUI.TimelineTierFilterMode.Details,
                window.TierFilterModeIndexForTesting);
            Assert.Contains(logLines, l => l.Contains("[Timeline]")
                && l.Contains("viewSwitched=False") && l.Contains("gameMode=science"));
        }

        // ------------------------------------------------------------------
        // Seam refusal
        // ------------------------------------------------------------------

        [Fact]
        public void TabHiddenInGameModeMessage_NamesWindowTabAndMode()
        {
            Assert.Equal("tab-hidden-in-game-mode window=timeline tab=contracts gameMode=science",
                TestCommands.TestCommandUiAction.BuildTabHiddenInGameModeMessage(
                    "timeline", "contracts",
                    TimelineCareerCategories.ModeToken(Game.Modes.SCIENCE_SANDBOX)));
        }
    }
}
