using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// What the Timeline shows for career rows: every contract row names its contract
    /// (from its own title, else the accept's title in the effective ledger, else a
    /// readable id), facility rows name the facility rather than the raw stock id, and
    /// the three source toggles' tooltips describe the rows each toggle governs.
    /// </summary>
    [Collection("Sequential")]
    public class TimelineCareerNamesTests : IDisposable
    {
        private const string ContractGuid = "7a726c83-8544-4b20-8818-73762962a34b";
        private const string ContractTitle = "Test LV-T45 \"Swivel\" Liquid Fuel Engine at the Launch Site.";

        private readonly List<string> logLines = new List<string>();

        public TimelineCareerNamesTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            FacilityDisplayNames.FacilityNameLookupForTesting = null;
        }

        public void Dispose()
        {
            FacilityDisplayNames.FacilityNameLookupForTesting = null;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Accept(string id = ContractGuid, string title = ContractTitle,
            string type = "PartTest", string actionId = null)
        {
            return new GameAction
            {
                UT = 100, Type = GameActionType.ContractAccept, Effective = true,
                ContractId = id, ContractTitle = title, ContractType = type,
                ActionId = actionId ?? Guid.NewGuid().ToString("N")
            };
        }

        private static GameAction Outcome(GameActionType type, string id = ContractGuid,
            float fundsReward = 0f, string title = null, string actionId = null)
        {
            return new GameAction
            {
                UT = 200, Type = type, Effective = true, ContractId = id,
                FundsReward = fundsReward, ContractTitle = title,
                ActionId = actionId ?? Guid.NewGuid().ToString("N")
            };
        }

        private static List<TimelineEntry> BuildFrom(IReadOnlyList<GameAction> actions)
        {
            return TimelineBuilder.Build(
                new List<Recording>(), actions, new List<Milestone>(), _ => true, Game.Modes.CAREER);
        }

        // ------------------------------------------------------------------
        // Contract names
        // ------------------------------------------------------------------

        // catches: outcome rows reading "Complete: unknown" because only the accept
        // carries the title (the census host showed ~200 such rows).
        [Fact]
        public void Build_OutcomeRows_TakeTheirNameFromTheAccept()
        {
            var entries = BuildFrom(new List<GameAction>
            {
                Accept(),
                Outcome(GameActionType.ContractComplete, fundsReward: 4374.9995f),
                Outcome(GameActionType.ContractFail),
                Outcome(GameActionType.ContractCancel),
            });

            var texts = entries.Select(e => e.DisplayText).ToList();
            Assert.Contains("Accept: " + ContractTitle, texts);
            Assert.Contains("Complete: " + ContractTitle + " +4375 funds", texts);
            Assert.Contains("Fail: " + ContractTitle, texts);
            Assert.Contains("Cancel: " + ContractTitle, texts);
            Assert.DoesNotContain(texts, t => t.Contains("unknown"));
            Assert.Contains(logLines, l => l.Contains("[Timeline]")
                && l.Contains("Contract row names: rows=4 fromAccept=3 fromType=0 fromIdFallback=0 acceptIndex=1"));
        }

        // catches: a contract accepted before Parsek (no accept row) reading "unknown".
        [Fact]
        public void Build_OutcomeWithoutAccept_FallsBackToAReadableId()
        {
            var entries = BuildFrom(new List<GameAction>
            {
                Outcome(GameActionType.ContractComplete, fundsReward: 39270f),
            });

            Assert.Equal("Complete: Contract 7a726c83 +39270 funds", Assert.Single(entries).DisplayText);
            Assert.Contains(logLines, l => l.Contains("[Timeline]")
                && l.Contains("rows=1 fromAccept=0 fromType=0 fromIdFallback=1 acceptIndex=0"));
        }

        // catches: a tombstoned accept still naming (or crashing) its outcome rows - the
        // Timeline reads the effective ledger, so the retired accept is gone and the
        // outcome falls back to its id; an outcome carrying its own title keeps it.
        [Fact]
        public void Build_OverTheEffectiveLedger_TombstonedAcceptLeavesTheIdFallback()
        {
            Ledger.AddAction(Accept(actionId: "act_accept"));
            Ledger.AddAction(Outcome(GameActionType.ContractComplete, fundsReward: 100f, actionId: "act_done"));
            Ledger.AddAction(Accept(id: "b1c2d3e4-0000-0000-0000-000000000000", title: "Orbit the Mun.",
                actionId: "act_accept2"));
            Ledger.AddAction(Outcome(GameActionType.ContractFail, id: "b1c2d3e4-0000-0000-0000-000000000000",
                title: "Orbit the Mun.", actionId: "act_fail2"));
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>
                {
                    new LedgerTombstone { TombstoneId = "t1", ActionId = "act_accept" },
                    new LedgerTombstone { TombstoneId = "t2", ActionId = "act_accept2" },
                },
                RewindPoints = new List<RewindPoint>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();

            var texts = BuildFrom(EffectiveState.ComputeELS()).Select(e => e.DisplayText).ToList();

            Assert.Equal(new[] { "Complete: Contract 7a726c83 +100 funds", "Fail: Orbit the Mun." }, texts);
        }

        [Fact]
        public void ResolveContractDisplayName_WalksOwnTitleAcceptTitleTypeThenId()
        {
            var index = GameActionDisplay.BuildContractAcceptIndex(new List<GameAction>
            {
                Accept(),
                Accept(id: "typed-only", title: null, type: "ExploreBody"),
            });
            GameActionDisplay.ContractNameSource source;

            Assert.Equal("Mine",
                GameActionDisplay.ResolveContractDisplayName(
                    Outcome(GameActionType.ContractComplete, title: "Mine"), index, out source));
            Assert.Equal(GameActionDisplay.ContractNameSource.OwnTitle, source);

            Assert.Equal(ContractTitle,
                GameActionDisplay.ResolveContractDisplayName(
                    Outcome(GameActionType.ContractFail), index, out source));
            Assert.Equal(GameActionDisplay.ContractNameSource.AcceptTitle, source);

            Assert.Equal("Explore Body contract",
                GameActionDisplay.ResolveContractDisplayName(
                    Outcome(GameActionType.ContractCancel, id: "typed-only"), index, out source));
            Assert.Equal(GameActionDisplay.ContractNameSource.ContractType, source);

            Assert.Equal("Contract 00ff00aa",
                GameActionDisplay.ResolveContractDisplayName(
                    Outcome(GameActionType.ContractComplete, id: "00ff00aa-1111-2222-3333-444444444444"),
                    index, out source));
            Assert.Equal(GameActionDisplay.ContractNameSource.IdFallback, source);
        }

        [Fact]
        public void BuildContractAcceptIndex_PrefersATitledAcceptAndSkipsIdlessRows()
        {
            var untitled = Accept(title: null);
            var titled = Accept(title: "Second accept");
            var index = GameActionDisplay.BuildContractAcceptIndex(new List<GameAction>
            {
                untitled, titled, Accept(title: "Third accept"), Accept(id: null, title: "No id"),
                Outcome(GameActionType.ContractComplete),
            });

            Assert.Single(index);
            Assert.Same(titled, index[ContractGuid]);
        }

        [Theory]
        [InlineData("7a726c83-8544-4b20-8818-73762962a34b", "Contract 7a726c83")]
        [InlineData("abc", "Contract abc")]
        [InlineData("0123456789abcdef", "Contract 01234567")]
        [InlineData("", "Unnamed contract")]
        [InlineData(null, "Unnamed contract")]
        public void FormatContractIdFallback_IsShortAndReadable(string id, string expected)
        {
            Assert.Equal(expected, GameActionDisplay.FormatContractIdFallback(id));
        }

        // catches: the index-less path (GetDescription) still printing "unknown".
        [Fact]
        public void GetDescription_ContractRowsWithoutAnyName_NeverSayUnknown()
        {
            var accept = new GameAction { Type = GameActionType.ContractAccept, ContractId = ContractGuid };
            var fail = new GameAction { Type = GameActionType.ContractFail, ContractId = null };

            Assert.Equal("Accept: Contract 7a726c83", GameActionDisplay.GetDescription(accept, Game.Modes.CAREER));
            Assert.Equal("Fail: Unnamed contract", GameActionDisplay.GetDescription(fail, Game.Modes.CAREER));
        }

        // ------------------------------------------------------------------
        // Outcome actions carry their own title from now on (additive)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(GameStateEventType.ContractCompleted, "title=Rescue Bill;fundsReward=100;repReward=1;sciReward=0", "Rescue Bill")]
        [InlineData(GameStateEventType.ContractFailed, "title=Rescue Bill;fundsPenalty=10;repPenalty=1", "Rescue Bill")]
        [InlineData(GameStateEventType.ContractCancelled, "title=Rescue Bill;fundsPenalty=10;repPenalty=1", "Rescue Bill")]
        [InlineData(GameStateEventType.ContractCompleted, "fundsReward=100;repReward=1;sciReward=0", null)]
        [InlineData(GameStateEventType.ContractFailed, "title=;fundsPenalty=10;repPenalty=1", null)]
        public void ConvertEvent_ContractOutcome_CarriesTheDetailTitle(
            GameStateEventType eventType, string detail, string expectedTitle)
        {
            var action = GameStateEventConverter.ConvertEvent(new GameStateEvent
            {
                ut = 500, eventType = eventType, key = ContractGuid, detail = detail
            }, "rec1");

            Assert.NotNull(action);
            Assert.Equal(ContractGuid, action.ContractId);
            Assert.Equal(expectedTitle, action.ContractTitle);
        }

        [Theory]
        [InlineData(GameActionType.ContractComplete)]
        [InlineData(GameActionType.ContractFail)]
        [InlineData(GameActionType.ContractCancel)]
        public void OutcomeContractTitle_RoundTripsAndIsOptional(GameActionType type)
        {
            var parent = new ConfigNode("LEDGER");
            Outcome(type, title: "Rescue Bill", actionId: "act_x").SerializeInto(parent);
            var node = parent.GetNode("GAME_ACTION");
            Assert.Equal("Rescue Bill", node.GetValue("contractTitle"));
            Assert.Equal("Rescue Bill", GameAction.DeserializeFrom(node).ContractTitle);

            var bare = new ConfigNode("LEDGER");
            Outcome(type, title: null, actionId: "act_y").SerializeInto(bare);
            var bareNode = bare.GetNode("GAME_ACTION");
            Assert.False(bareNode.HasValue("contractTitle"));
            Assert.Null(GameAction.DeserializeFrom(bareNode).ContractTitle);
        }

        // ------------------------------------------------------------------
        // Facility names
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("SpaceCenter/LaunchPad", "Launchpad")]
        [InlineData("SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_cylTank", "Launchpad")]
        [InlineData("LaunchPad", "Launchpad")]
        [InlineData("SpaceCenter/ResearchAndDevelopment/Facility/CentralBuilding", "Research and Development")]
        [InlineData("SpaceCenter/VehicleAssemblyBuilding", "Vehicle Assembly Building")]
        [InlineData("", FacilityDisplayNames.UnknownFacilityText)]
        [InlineData(null, FacilityDisplayNames.UnknownFacilityText)]
        public void ResolveBuildingDisplayName_MapsEveryIdShapeToTheFacilityName(string id, string expected)
        {
            FacilityDisplayNames.FacilityNameLookupForTesting = f => f == "LaunchPad" ? "Launchpad" : null;
            Assert.Equal(expected, FacilityDisplayNames.ResolveBuildingDisplayName(id));
        }

        // catches: facility rows printing the raw stock key ("SpaceCenter/LaunchPad").
        [Fact]
        public void Build_FacilityRows_NameTheFacility()
        {
            FacilityDisplayNames.FacilityNameLookupForTesting = f => f == "LaunchPad" ? "Launchpad" : null;
            var entries = BuildFrom(new List<GameAction>
            {
                new GameAction { UT = 10, Type = GameActionType.FacilityUpgrade, Effective = true,
                    FacilityId = "SpaceCenter/LaunchPad", ToLevel = 2, FacilityCost = 45000f },
                new GameAction { UT = 20, Type = GameActionType.FacilityDestruction, Effective = true,
                    FacilityId = "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_waterTower" },
                new GameAction { UT = 30, Type = GameActionType.FacilityRepair, Effective = true,
                    FacilityId = "SpaceCenter/Runway/Facility/mainBuilding", FacilityCost = 1200f },
            });

            Assert.Equal(new[]
            {
                "Upgrade Launchpad → Lv.2 -45000",
                "Launchpad destroyed",
                "Repair Runway -1200",
            }, entries.Select(e => e.DisplayText));
            Assert.DoesNotContain(entries, e => e.DisplayText.Contains("SpaceCenter"));
        }

        // ------------------------------------------------------------------
        // Source-toggle tooltips
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(TimelineSource.Recording, false, "Recordings")]
        [InlineData(TimelineSource.Recording, true, "Recordings")]
        [InlineData(TimelineSource.GameAction, true, "Actions")]
        [InlineData(TimelineSource.GameAction, false, "Events")]
        [InlineData(TimelineSource.Legacy, false, "Events")]
        public void ResolveSourceToggle_FollowsSourceAndPlayerAction(
            TimelineSource source, bool isPlayerAction, string expected)
        {
            Assert.Equal(expected, TimelineWindowUI.ResolveSourceToggle(source, isPlayerAction).ToString());
        }

        // catches: a tooltip promising rows another toggle governs (the Events tooltip
        // named crew deaths, a Recordings row; the Actions tooltip named contracts,
        // whose completions and failures are Events). Each phrase must sit in exactly
        // the tooltip of the toggle the code routes that row type to.
        [Theory]
        [InlineData("crew deaths", TimelineSource.Recording, TimelineEntryType.CrewDeath)]
        [InlineData("launches", TimelineSource.Recording, TimelineEntryType.RecordingStart)]
        [InlineData("separations", TimelineSource.Recording, TimelineEntryType.Separation)]
        [InlineData("spawns", TimelineSource.Recording, TimelineEntryType.VesselSpawn)]
        [InlineData("builds", TimelineSource.GameAction, TimelineEntryType.FundsSpending)]
        [InlineData("hires", TimelineSource.GameAction, TimelineEntryType.KerbalHire)]
        [InlineData("tech", TimelineSource.GameAction, TimelineEntryType.ScienceSpending)]
        [InlineData("upgrades", TimelineSource.GameAction, TimelineEntryType.FacilityUpgrade)]
        [InlineData("repairs", TimelineSource.GameAction, TimelineEntryType.FacilityRepair)]
        [InlineData("strategies", TimelineSource.GameAction, TimelineEntryType.StrategyActivate)]
        [InlineData("contract accepts", TimelineSource.GameAction, TimelineEntryType.ContractAccept)]
        [InlineData("cancels", TimelineSource.GameAction, TimelineEntryType.ContractCancel)]
        [InlineData("milestones", TimelineSource.GameAction, TimelineEntryType.MilestoneAchievement)]
        [InlineData("completed or failed contracts", TimelineSource.GameAction, TimelineEntryType.ContractComplete)]
        [InlineData("completed or failed contracts", TimelineSource.GameAction, TimelineEntryType.ContractFail)]
        [InlineData("earnings", TimelineSource.GameAction, TimelineEntryType.FundsEarning)]
        [InlineData("earnings", TimelineSource.GameAction, TimelineEntryType.ScienceEarning)]
        [InlineData("recoveries", TimelineSource.GameAction, TimelineEntryType.FundsEarning)]
        [InlineData("destroyed buildings", TimelineSource.GameAction, TimelineEntryType.FacilityDestruction)]
        public void SourceToggleTooltips_NameOnlyTheRowsTheirToggleGoverns(
            string phrase, TimelineSource source, TimelineEntryType type)
        {
            var owner = TimelineWindowUI.ResolveSourceToggle(
                source, source != TimelineSource.Recording && TimelineEntryDisplay.IsPlayerAction(type));
            var tooltips = new Dictionary<TimelineWindowUI.TimelineSourceToggle, string>
            {
                { TimelineWindowUI.TimelineSourceToggle.Recordings, TimelineWindowUI.RecordingsToggleTooltip },
                { TimelineWindowUI.TimelineSourceToggle.Actions, TimelineWindowUI.ActionsToggleTooltip },
                { TimelineWindowUI.TimelineSourceToggle.Events, TimelineWindowUI.EventsToggleTooltip },
            };

            foreach (var kv in tooltips)
            {
                if (kv.Key == owner)
                    Assert.Contains(phrase, kv.Value);
                else
                    Assert.DoesNotContain(phrase, kv.Value);
            }
        }

        [Fact]
        public void EventsTooltip_NoLongerClaimsCrewDeaths_AndCrewDeathIsARecordingRow()
        {
            Assert.DoesNotContain("crew death", TimelineWindowUI.EventsToggleTooltip);
            Assert.DoesNotContain("contracts,", TimelineWindowUI.ActionsToggleTooltip);

            var rec = new Recording
            {
                RecordingId = "r1", VesselName = "Ship", ExplicitStartUT = 0, ExplicitEndUT = 100,
                TerminalStateValue = TerminalState.Destroyed, PlaybackEnabled = true
            };
            rec.CrewEndStates = new Dictionary<string, KerbalEndState> { { "Jeb", KerbalEndState.Dead } };
            var entries = TimelineBuilder.Build(new List<Recording> { rec }, new List<GameAction>(),
                new List<Milestone>(), _ => true, Game.Modes.CAREER);

            var death = Assert.Single(entries, e => e.Type == TimelineEntryType.CrewDeath);
            Assert.Equal(TimelineSource.Recording, death.Source);
            Assert.Equal(TimelineWindowUI.TimelineSourceToggle.Recordings,
                TimelineWindowUI.ResolveSourceToggle(death.Source, death.IsPlayerAction));
        }
    }
}
