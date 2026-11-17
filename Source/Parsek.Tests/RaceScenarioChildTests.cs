using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The race scenario node round trip (multiplayer R1-03, design 6.2): the module keeps the
    /// loaded node verbatim, hands a child with a handler to that handler, writes everything else
    /// back byte-equal, and holds the whole node when Parsek is inert or the mark does not read.
    /// The save side is driven the way stock drives it: <c>ScenarioModule.Save</c> writes the
    /// <c>name</c> and <c>scene</c> values, then calls OnSave on the same node.
    /// </summary>
    [Collection("Sequential")]
    public class RaceScenarioChildTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RaceScenarioChildTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekGameModeGate.ResetForTesting();
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.CAREER;
        }

        public void Dispose()
        {
            ParsekGameModeGate.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private sealed class RecordingChild : IRaceScenarioChild
        {
            public readonly List<string> Loaded = new List<string>();
            public int Saves;
            public string ChildNodeName { get; set; }
            public void Load(ConfigNode child) { Loaded.Add(child.GetValue("v")); }
            public void Save(ConfigNode child)
            {
                Saves++;
                child.AddValue("v", "written-by-handler");
            }
        }

        private static ParsekRaceScenario NewScenario()
        {
            var scenario = new ParsekRaceScenario();
            scenario.OnAwake();
            return scenario;
        }

        /// <summary>What stock's ScenarioModule.Save writes before OnSave runs.</summary>
        private static ConfigNode StockSaveNode(ConfigNode loaded)
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", loaded.GetValue("name"));
            node.AddValue("scene", loaded.GetValue("scene"));
            return node;
        }

        private static ConfigNode LinkNodeWithUnknowns()
        {
            var node = RaceLinkCodecTests.BuildLinkNode();
            node.AddValue("futureKey", "kept as is");
            ConfigNode child = node.AddNode("FUTURE_CHILD");
            child.AddValue("a", "1");
            child.AddNode("DEEPER").AddValue("b", "2");
            node.AddValue("lateKey", "after the child");
            return node;
        }

        [Fact]
        public void UnknownKeysAndChildren_RoundTripByteEqual()
        {
            var loaded = LinkNodeWithUnknowns();
            string before = loaded.ToString();
            var scenario = NewScenario();
            scenario.OnLoad(loaded);
            var saved = StockSaveNode(loaded);
            scenario.OnSave(saved);
            // Values are written before children, as ConfigNode always serialises them.
            Assert.Equal(before, saved.ToString());
            Assert.NotNull(scenario.Link);
            Assert.Contains(logLines, l => l.Contains("[Race]") && l.Contains(
                "Mark loaded: raceId=race_0123456789abcdef0123456789abcdef "
                + "playerId=p_fedcba9876543210fedcba9876543210 slot=2 format=1 children=1 handled=0"));
        }

        [Fact]
        public void ChildWithHandler_ReachesLoad_AndIsWrittenByItsSave_InPlace()
        {
            var loaded = RaceLinkCodecTests.BuildLinkNode();
            loaded.AddNode("BEFORE").AddValue("x", "1");
            loaded.AddNode("RACE_TEST_CHILD").AddValue("v", "from-save");
            loaded.AddNode("AFTER").AddValue("y", "2");
            var handler = new RecordingChild { ChildNodeName = "RACE_TEST_CHILD" };
            var scenario = NewScenario();
            scenario.AddChildHandlerForTesting(handler);

            scenario.OnLoad(loaded);
            Assert.Equal(new[] { "from-save" }, handler.Loaded);
            Assert.Contains(logLines, l => l.Contains("Mark loaded:") && l.Contains("children=3 handled=1"));

            var saved = StockSaveNode(loaded);
            scenario.OnSave(saved);
            Assert.Equal(1, handler.Saves);
            Assert.Equal(new[] { "BEFORE", "RACE_TEST_CHILD", "AFTER" },
                Array.ConvertAll(saved.GetNodes(), n => n.name));
            Assert.Equal("written-by-handler", saved.GetNode("RACE_TEST_CHILD").GetValue("v"));
            Assert.Equal("1", saved.GetNode("BEFORE").GetValue("x"));
        }

        [Fact]
        public void HandlerWhoseChildTheSaveLacks_IsNotLoaded_AndAppendsWhatItWrites()
        {
            var handler = new RecordingChild { ChildNodeName = "RACE_TEST_CHILD" };
            var scenario = NewScenario();
            scenario.AddChildHandlerForTesting(handler);
            var loaded = RaceLinkCodecTests.BuildLinkNode();
            scenario.OnLoad(loaded);
            Assert.Empty(handler.Loaded);

            var saved = StockSaveNode(loaded);
            scenario.OnSave(saved);
            Assert.Equal("written-by-handler", saved.GetNode("RACE_TEST_CHILD").GetValue("v"));
        }

        [Fact]
        public void InertGame_HoldsTheNodeVerbatim_AndNoHandlerRuns()
        {
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.MISSION;
            var loaded = LinkNodeWithUnknowns();
            loaded.AddNode("RACE_TEST_CHILD").AddValue("v", "from-save");
            string before = loaded.ToString();
            var handler = new RecordingChild { ChildNodeName = "RACE_TEST_CHILD" };
            var scenario = NewScenario();
            scenario.AddChildHandlerForTesting(handler);

            scenario.OnLoad(loaded);
            var saved = StockSaveNode(loaded);
            scenario.OnSave(saved);

            Assert.Null(scenario.Link);
            Assert.Empty(handler.Loaded);
            Assert.Equal(0, handler.Saves);
            Assert.Equal(before, saved.ToString());
            Assert.Contains(logLines, l => l.Contains("[Race]")
                && l.Contains("Mark held verbatim: reason=inert values=15 nodes=2"));
        }

        [Theory]
        [InlineData("2", "format-newer")]
        [InlineData("0", "format-older")]
        public void UnreadableMark_IsHeldVerbatim_AndNoHandlerRuns(string format, string reason)
        {
            var loaded = LinkNodeWithUnknowns();
            loaded.SetValue(RaceLinkCodec.KeyRaceLinkFormat, format);
            loaded.AddNode("RACE_TEST_CHILD").AddValue("v", "from-save");
            string before = loaded.ToString();
            var handler = new RecordingChild { ChildNodeName = "RACE_TEST_CHILD" };
            var scenario = NewScenario();
            scenario.AddChildHandlerForTesting(handler);

            scenario.OnLoad(loaded);
            var saved = StockSaveNode(loaded);
            scenario.OnSave(saved);

            Assert.Null(scenario.Link);
            Assert.Empty(handler.Loaded);
            Assert.Equal(0, handler.Saves);
            Assert.Equal(before, saved.ToString());
            Assert.Contains(logLines, l => l.Contains("Mark held verbatim: reason=" + reason + " "));
        }

        [Fact]
        public void MissingKey_IsHeldNamingTheKey()
        {
            var loaded = RaceLinkCodecTests.BuildLinkNode();
            loaded.RemoveValue(RaceLinkCodec.KeyPlayerId);
            var scenario = NewScenario();
            scenario.OnLoad(loaded);
            Assert.Null(scenario.Link);
            Assert.Contains(logLines, l => l.Contains("Mark held verbatim: reason=missing:playerId "));
        }

        [Fact]
        public void SaveWithNothingLoaded_WritesOnlyWhatStockWrote()
        {
            var scenario = NewScenario();
            var saved = new ConfigNode("SCENARIO");
            saved.AddValue("name", RaceFormat.ScenarioModuleName);
            scenario.OnSave(saved);
            Assert.Equal(1, saved.values.Count);
            Assert.Empty(saved.GetNodes());
        }

        [Fact]
        public void WriteBack_WithoutHandlers_CopiesEveryChildVerbatim_AndSkipsStockValues()
        {
            var kept = LinkNodeWithUnknowns();
            var target = new ConfigNode("SCENARIO");
            int handlerWritten;
            int written = RaceScenarioChildren.WriteBack(kept, target, null, out handlerWritten);
            Assert.Equal(0, handlerWritten);
            // 11 link values + 2 unknown values + 1 child; name and scene are stock's.
            Assert.Equal(14, written);
            Assert.Null(target.GetValue("name"));
            Assert.Null(target.GetValue("scene"));
        }
    }
}
