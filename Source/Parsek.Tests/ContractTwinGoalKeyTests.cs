using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Contracts;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-10-27 (todo CONTRACT-TWIN-GUID-SEEDED-TYPES): a contract twins a
    /// committed future accept when its stock ContractID matches OR its goal key matches, for
    /// the guid-seeded types whose goal stock offers once per career (exploration milestones,
    /// the Significant plant flag's body, the comet sample's comet). Repeatable types carry no
    /// goal key. Everything else of the step 7b rule is unchanged.
    /// </summary>
    [Collection("Sequential")]
    public class ContractTwinGoalKeyTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string MunExploreHolder = "ExplorationContract:FLYBY@2,ORBIT@2";
        private const string MunFlag = "PlantFlag:body@2";

        public ContractTwinGoalKeyTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static GameAction Accept(string guid, double ut, long stockId, string goal, string title = "Explore the Mun")
            => new GameAction
            {
                UT = ut, Type = GameActionType.ContractAccept, ContractId = guid, ContractTitle = title,
                ContractStockId = stockId, ContractGoalKey = goal,
            };

        private static CommittedFutureIndex Index(params GameAction[] rows)
            => CommittedFutureIndex.Build(rows, id => id == "committed-rec", id => null, null);

        // ------------------------------------------------------------------
        // Goal-key extraction per type (the saved CONTRACT node, as Contract.Save writes it)
        // ------------------------------------------------------------------

        private static ConfigNode ContractNode(string type, int prestige = 1)
        {
            var n = new ConfigNode("CONTRACT");
            n.AddValue("guid", Guid.NewGuid().ToString());
            n.AddValue("type", type);
            n.AddValue("prestige", prestige.ToString());
            n.AddValue("seed", "12345");
            return n;
        }

        private static void AddTracking(ConfigNode n, string milestone, int body)
        {
            ConfigNode p = n.AddNode("PARAM");
            p.AddValue("name", "ProgressTrackingParameter");
            p.AddValue("targetBody", body.ToString());
            p.AddValue("targetType", milestone);
        }

        // catches: an exploration copy keyed on anything but its milestone set (title, seed,
        // parameter order), or another parameter read as a milestone.
        [Fact]
        public void Extract_Exploration_IsItsMilestoneSet_OrderFree()
        {
            ConfigNode a = ContractNode("ExplorationContract", 0);
            AddTracking(a, "ORBIT", 2);
            AddTracking(a, "FLYBY", 2);
            ConfigNode other = a.AddNode("PARAM");
            other.AddValue("name", "VesselSystemsParameter");
            other.AddValue("targetBody", "9");
            other.AddValue("targetType", "LANDING");
            Assert.Equal(MunExploreHolder, ContractGoalKey.FromContractNode(a));

            ConfigNode b = ContractNode("ExplorationContract", 2);
            AddTracking(b, "FLYBY", 2);
            AddTracking(b, "ORBIT", 2);
            AddTracking(b, "FLYBY", 2);
            Assert.Equal(MunExploreHolder, ContractGoalKey.FromContractNode(b));

            Assert.Null(ContractGoalKey.FromContractNode(ContractNode("ExplorationContract")));
            Assert.Equal("ExplorationContract:FIRSTLAUNCH@1",
                ContractGoalKey.ForExploration(new[] { new KeyValuePair<string, int>("FIRSTLAUNCH", 1) }));
        }

        // catches: the Trivial plant flag (offered again on a flagged body once its crew left)
        // getting a key, which would block a legitimate repeat.
        [Fact]
        public void Extract_PlantFlag_OnlyAtSignificantPrestige()
        {
            ConfigNode significant = ContractNode("PlantFlag", 1);
            significant.AddValue("body", "2");
            Assert.Equal(MunFlag, ContractGoalKey.FromContractNode(significant));

            ConfigNode trivial = ContractNode("PlantFlag", 0);
            trivial.AddValue("body", "2");
            Assert.Null(ContractGoalKey.FromContractNode(trivial));

            ConfigNode exceptional = ContractNode("PlantFlag", 2);
            exceptional.AddValue("body", "2");
            Assert.Null(ContractGoalKey.FromContractNode(exceptional));

            Assert.Null(ContractGoalKey.FromContractNode(ContractNode("PlantFlag", 1))); // no body
        }

        [Fact]
        public void Extract_CometSample_IsTheComet()
        {
            ConfigNode n = ContractNode("CometSampleContract");
            n.AddValue("cometName", "C-AAA");
            n.AddValue("cometPersistentId", "3141592653");
            Assert.Equal("CometSampleContract:comet@3141592653", ContractGoalKey.FromContractNode(n));

            ConfigNode none = ContractNode("CometSampleContract");
            none.AddValue("cometPersistentId", "0");
            Assert.Null(ContractGoalKey.FromContractNode(none));
        }

        // catches: a repeatable stock type, a type whose ContractID already names the goal, or a
        // Contract Configurator contract getting a goal key from content that looks like one.
        [Theory]
        [InlineData("GrandTour")]
        [InlineData("TourismContract")]
        [InlineData("RecoverAsset")]
        [InlineData("SatelliteContract")]
        [InlineData("StationContract")]
        [InlineData("BaseContract")]
        [InlineData("ISRUContract")]
        [InlineData("ARMContract")]
        [InlineData("SurveyContract")]
        [InlineData("OrbitalConstructionContract")]
        [InlineData("RoverConstructionContract")]
        [InlineData("VesselRepairContract")]
        [InlineData("PartTest")]
        [InlineData("CollectScience")]
        [InlineData("CollectDeployedScience")]
        [InlineData("CollectROCScienceArm")]
        [InlineData("CollectROCScienceRetrieval")]
        [InlineData("ConfiguredContract")]
        [InlineData("")]
        public void Extract_EveryOtherType_HasNoGoalKey(string type)
        {
            ConfigNode n = ContractNode(type, 1);
            n.AddValue("body", "2");
            n.AddValue("cometPersistentId", "77");
            AddTracking(n, "ORBIT", 2);
            Assert.Null(ContractGoalKey.FromContractNode(n));
            Assert.False(ContractGoalKey.IsGoalType(type));
        }

        [Fact]
        public void Overlaps_SameTypeAndASharedToken_Only()
        {
            Assert.True(ContractGoalKey.Overlaps(MunExploreHolder, "ExplorationContract:LANDING@2,ORBIT@2"));
            Assert.True(ContractGoalKey.Overlaps(MunFlag, MunFlag));
            Assert.False(ContractGoalKey.Overlaps(MunExploreHolder, "ExplorationContract:FLYBY@3,ORBIT@3"));
            Assert.False(ContractGoalKey.Overlaps(MunFlag, "PlantFlag:body@3"));
            Assert.False(ContractGoalKey.Overlaps("PlantFlag:body@2", "CometSampleContract:body@2")); // other type
            Assert.False(ContractGoalKey.Overlaps(null, MunFlag));
            Assert.False(ContractGoalKey.Overlaps(MunFlag, null));
            Assert.False(ContractGoalKey.Overlaps("no-type", "no-type"));
            Assert.False(ContractGoalKey.Overlaps("PlantFlag:", "PlantFlag:"));
        }

        // ------------------------------------------------------------------
        // The pure rule
        // ------------------------------------------------------------------

        // catches: the goal match dropped (a regenerated "Explore the Mun" with a fresh
        // ContractID offered and paid twice), or a goal twin logged as an id twin.
        [Fact]
        public void FindTwinHolder_CatchesAGoalTwinWithAnotherContractId()
        {
            CommittedFutureIndex index = Index(Accept("g-committed", 1000, 111, MunExploreHolder));

            ContractTwinRule.TwinIdentity matched;
            CommittedFutureEntry holder = ContractTwinRule.FindTwinHolder(
                index, 222, "ExplorationContract:LANDING@2,ORBIT@2", "g-copy", 500, out matched);
            Assert.NotNull(holder);
            Assert.Equal("g-committed", holder.Key);
            Assert.Equal(ContractTwinRule.TwinIdentity.Goal, matched);
            Assert.Equal("matched=goal(" + MunExploreHolder + ")", ContractTwinRule.DescribeMatch(matched, holder));

            // Stock id 0 (unread) with a goal key is still judged.
            Assert.NotNull(ContractTwinRule.FindTwinHolder(index, 0, MunExploreHolder, "g-copy", 500, out matched));
            // The id-only reading (today's rule) does not catch it: the goal key is what does.
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 222, "g-copy", 500));
            // Another body's milestones are another goal.
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 222, "ExplorationContract:FLYBY@3", "g-copy", 500, out matched));
            Assert.Equal(ContractTwinRule.TwinIdentity.None, matched);
        }

        [Fact]
        public void FindTwinHolder_TheStockIdStillMatches_AndWinsTheLabel()
        {
            CommittedFutureIndex index = Index(Accept("g-committed", 1000, 4242, MunFlag, "Plant a flag on the Mun"));
            ContractTwinRule.TwinIdentity matched;
            Assert.NotNull(ContractTwinRule.FindTwinHolder(index, 4242, null, "g-copy", 500, out matched));
            Assert.Equal(ContractTwinRule.TwinIdentity.StockId, matched);
            Assert.NotNull(ContractTwinRule.FindTwinHolder(index, 4242, MunFlag, "g-copy", 500, out matched));
            Assert.Equal(ContractTwinRule.TwinIdentity.StockId, matched);
        }

        // catches: a repeatable type caught by goal: neither the committed accept nor the copy
        // of a repeatable type carries a key, so only a matching stock id (never produced for a
        // guid-seeded copy) could hold it; and a Trivial plant flag on a body a Significant
        // committed one names is not a twin.
        [Fact]
        public void FindTwinHolder_ARepeatableTypeIsNeverCaughtByGoal()
        {
            ConfigNode tour = ContractNode("TourismContract", 0);
            tour.AddValue("body", "2");
            string tourKey = ContractGoalKey.FromContractNode(tour);
            Assert.Null(tourKey);
            CommittedFutureIndex index = Index(
                Accept("g-tour", 1000, 31337, tourKey, "Take tourists to the Mun"),
                Accept("g-flag", 1000, 4242, MunFlag, "Plant a flag on the Mun"));

            ContractTwinRule.TwinIdentity matched;
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 99, tourKey, "g-tour-copy", 500, out matched));
            Assert.False(ContractTwinRule.AnyFutureGoalHolderOfType(index, "TourismContract", 500));

            ConfigNode trivialFlag = ContractNode("PlantFlag", 0);
            trivialFlag.AddValue("body", "2");
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 98, ContractGoalKey.FromContractNode(trivialFlag),
                "g-flag-copy", 500, out matched));
        }

        // catches: a career that never rewinds judged at all (no committed accept ahead of the
        // clock: nothing is blocked, and the doors never read a goal key).
        [Fact]
        public void FindTwinHolder_NoFutureAccept_BlocksNothing()
        {
            CommittedFutureIndex empty = Index(
                new GameAction { UT = 1000, Type = GameActionType.ContractComplete, ContractId = "g1" });
            ContractTwinRule.TwinIdentity matched;
            Assert.Null(ContractTwinRule.FindTwinHolder(empty, 0, MunExploreHolder, "g-copy", 500, out matched));
            Assert.False(ContractTwinRule.AnyFutureGoalHolderOfType(empty, "ExplorationContract", 500));
            Assert.Null(ContractTwinRule.FindTwinHolder(null, 1, MunExploreHolder, "g-copy", 500, out matched));
        }

        // catches: an accept already behind the clock (or exactly at it) still blocking its goal;
        // stock may offer it again exactly as it would after the committed contract ends.
        [Fact]
        public void FindTwinHolder_AnAcceptInThePast_BlocksNothing()
        {
            CommittedFutureIndex index = Index(Accept("g-committed", 1000, 111, MunExploreHolder));
            ContractTwinRule.TwinIdentity matched;
            Assert.NotNull(ContractTwinRule.FindTwinHolder(index, 0, MunExploreHolder, "g-copy", 999.9, out matched));
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 0, MunExploreHolder, "g-copy", 1000, out matched));
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 0, MunExploreHolder, "g-copy", 1500, out matched));
            Assert.True(ContractTwinRule.AnyFutureGoalHolderOfType(index, "ExplorationContract", 999));
            Assert.False(ContractTwinRule.AnyFutureGoalHolderOfType(index, "ExplorationContract", 1000));
        }

        [Fact]
        public void FindTwinHolder_TheSameGuidIsNeverItsOwnTwin()
        {
            CommittedFutureIndex index = Index(Accept("g-committed", 1000, 111, MunExploreHolder));
            ContractTwinRule.TwinIdentity matched;
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 111, MunExploreHolder, "g-committed", 500, out matched));
            Assert.Null(ContractTwinRule.FindTwinHolder(index, 0, MunExploreHolder, "G-COMMITTED", 500, out matched));
        }

        [Fact]
        public void SelectOfferedTwins_MatchesOnTheGoalKey()
        {
            CommittedFutureIndex index = Index(Accept("g-committed", 1000, 111, MunFlag, "Plant a flag on the Mun"));
            var list = new List<ContractTwinRule.OfferedContract>
            {
                new ContractTwinRule.OfferedContract { Index = 0, Guid = "a", StockContractId = 5, GoalKey = MunFlag, Offered = false },
                new ContractTwinRule.OfferedContract { Index = 1, Guid = "b", StockContractId = 6, GoalKey = MunFlag, Offered = true },
                new ContractTwinRule.OfferedContract { Index = 2, Guid = "c", StockContractId = 7, GoalKey = "PlantFlag:body@3", Offered = true },
                new ContractTwinRule.OfferedContract { Index = 3, Guid = "d", StockContractId = 8, Offered = true },
            };
            var twins = ContractTwinRule.SelectOfferedTwins(list, index, 10);
            Assert.Equal(new[] { 1 }, twins.Select(t => t.Key.Index).ToArray());
            Assert.Equal(ContractTwinRule.TwinIdentity.Goal, ContractTwinRule.MatchOf(twins[0].Key, twins[0].Value));
        }

        // ------------------------------------------------------------------
        // The live doors
        // ------------------------------------------------------------------

        // catches: the generation door losing its per-holder rate limit for goal twins, or a goal
        // twin's line not naming the identity that matched.
        [Fact]
        public void IsTwinNow_AGoalTwin_LogsTheMatch_OncePerHolderAtGeneration()
        {
            Ledger.AddAction(Accept("g-committed", 1000, 111, MunExploreHolder));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 500;

            for (int i = 0; i < 4; i++)
                Assert.True(ContractTwinRule.IsTwinNow(900 + i, "g-seed" + i, "Explore the Mun", "Generation",
                    rateLimitPerHolder: true, goalKey: "ExplorationContract:ORBIT@2"));
            Assert.Single(logLines, l => l.Contains("[INFO][ContractTwin]") && l.Contains("Generation:")
                && l.Contains("guid=g-committed") && l.Contains("goal=ExplorationContract:ORBIT@2")
                && l.Contains("matched=goal(" + MunExploreHolder + ")") && l.Contains("kept out of the offer pool"));

            Assert.True(ContractTwinRule.IsTwinNow(0, "g-auto", "Explore the Mun", "Auto-accept",
                goalKey: MunExploreHolder));
            Assert.False(ContractTwinRule.IsTwinNow(0, "g-auto", "Explore the Mun", "Auto-accept"));
            Assert.False(ContractTwinRule.IsTwinNow(0, "g-committed", "Explore the Mun", "Auto-accept",
                goalKey: MunExploreHolder));
        }

        // catches: a door paying for a goal-key read in a career with no goal holder of that type.
        [Fact]
        public void ReadIfJudged_ReadsOnlyWhenACommittedFutureAcceptOfTheTypeHasAKey()
        {
            var comet = new CometSampleContract("g-copy", Contract.State.Offered, 3141592653u);
            Assert.Equal("CometSampleContract:comet@3141592653", ContractGoalKeyReader.FromLiveContract(comet));

            CommittedFutureIndex none = Index(Accept("g-flag", 1000, 4242, MunFlag));
            Assert.Null(ContractGoalKeyReader.ReadIfJudged(comet, none, 500, "Generation"));

            CommittedFutureIndex holder = Index(Accept("g-comet", 1000, 4242, "CometSampleContract:comet@3141592653"));
            Assert.Equal("CometSampleContract:comet@3141592653",
                ContractGoalKeyReader.ReadIfJudged(comet, holder, 500, "Generation"));
            Assert.Null(ContractGoalKeyReader.ReadIfJudged(comet, holder, 1000, "Generation"));
            Assert.Null(ContractGoalKeyReader.ReadIfJudged(null, holder, 500, "Generation"));
        }

        // catches: the withdrawal acting without a real clock (it has no undo), and shows the
        // same list IS withdrawn by goal key once the clock is real.
        [Fact]
        public void WithdrawDoor_HonorsTheRealClockGuard_AndMatchesOnTheGoalKey()
        {
            Ledger.AddAction(Accept("g-comet", 1000, 4242, "CometSampleContract:comet@3141592653", "Sample comet C-AAA"));
            var copy = new CometSampleContract("6a7b0c3e-0000-4000-8000-000000000001", Contract.State.Offered, 3141592653u);
            var list = new List<Contract> { copy };

            foreach (double clock in new[] { double.NaN, 0.0, double.PositiveInfinity })
            {
                logLines.Clear();
                Assert.Equal(0, KspStatePatcher.WithdrawOfferedContractTwins(list, clock));
                Assert.Contains(logLines, l => l.Contains("[ContractTwin]") && l.Contains("no real clock"));
                Assert.DoesNotContain(logLines, l => l.Contains("twin offer(s) withdrawn"));
                Assert.Equal(Contract.State.Offered, copy.ContractState);
            }

            logLines.Clear();
            KspStatePatcher.WithdrawOfferedContractTwins(list, 500);
            Assert.Contains(logLines, l => l.Contains("[ContractTwin]") && l.Contains("of 1 twin offer(s) withdrawn"));
            Assert.DoesNotContain(logLines, l => l.Contains("no real clock"));
        }

        // ------------------------------------------------------------------
        // Capture: the goal key rides the accept event, row and index
        // ------------------------------------------------------------------

        [Fact]
        public void GoalKey_DetailSuffix_Converter_RoundTrip_AndIndex()
        {
            Assert.Equal("", GameStateRecorder.FormatContractGoalKeyDetail(null));
            Assert.Equal(";goal=" + MunFlag, GameStateRecorder.FormatContractGoalKeyDetail(MunFlag));

            var evt = new GameStateEvent
            {
                ut = 100, eventType = GameStateEventType.ContractAccepted, key = "g1",
                detail = "title=Plant a flag on the Mun;deadlineAbsUT=NaN;type=PlantFlag;funds=100;failFunds=10;failRep=1"
                    + GameStateRecorder.FormatContractStockIdDetail(55)
                    + GameStateRecorder.FormatContractGoalKeyDetail(MunFlag),
            };
            GameAction row = GameStateEventConverter.ConvertEvent(evt, null);
            Assert.Equal(MunFlag, row.ContractGoalKey);
            Assert.Equal(55L, row.ContractStockId);
            Assert.Contains(logLines, l => l.Contains("ConvertContractAccepted") && l.Contains("goal=" + MunFlag));
            evt.detail = "title=Plant a flag on the Mun;deadlineAbsUT=NaN;type=PlantFlag;funds=100;failFunds=10;failRep=1";
            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null).ContractGoalKey);

            var root = new ConfigNode("ROOT");
            Accept("g1", 100, 0, MunExploreHolder).SerializeInto(root);
            ConfigNode node = root.GetNode("GAME_ACTION");
            Assert.Equal(MunExploreHolder, node.GetValue(GameAction.ContractGoalKeyKey));
            Assert.Equal(MunExploreHolder, GameAction.DeserializeFrom(node).ContractGoalKey);
            var plain = new ConfigNode("ROOT");
            Accept("g2", 100, 0, null).SerializeInto(plain);
            Assert.Null(plain.GetNode("GAME_ACTION").GetValue(GameAction.ContractGoalKeyKey));
            Assert.Null(GameAction.DeserializeFrom(plain.GetNode("GAME_ACTION")).ContractGoalKey);

            CommittedFutureIndex index = Index(
                Accept("g1", 1000, 0, MunFlag),
                new GameAction { UT = 1000, Type = GameActionType.ContractComplete, ContractId = "g1", ContractGoalKey = MunFlag });
            Assert.Equal(MunFlag, index.FirstFuture(CommittedFutureKind.ContractAccept, "g1", 0).GoalKey);
            Assert.Null(index.FirstFuture(CommittedFutureKind.ContractComplete, "g1", 0).GoalKey);
            Assert.Equal(MunFlag, index.FirstFuture(CommittedFutureKind.ContractAccept, "g1", 0)
                .WithAgentTitle("Agent").GoalKey);
        }

        /// <summary>A comet sample contract the tests can build, named like stock's: the live reader keys on the
        /// type name and reads <c>CometPersistentId</c>.</summary>
        private sealed class CometSampleContract : FinePrint.Contracts.CometSampleContract
        {
            internal CometSampleContract(string guid, State state, uint cometPid)
            {
                typeof(Contract).GetField("contractGuid", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(this, new Guid(guid.Length == 36 ? guid : "00000000-0000-4000-8000-000000000002"));
                typeof(Contract).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, state);
                typeof(FinePrint.Contracts.CometSampleContract)
                    .GetField("cometPersistentId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, cometPid);
            }

            protected override string GetTitle() => "Sample a comet";
        }
    }
}
