using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The 1.0 save-format rule "never destroy data this build does not understand" for the
    /// game-state surfaces: an unknown game-state event type is kept as its raw node out of every
    /// consumer's sight and re-emitted, a milestones node of a newer version is held verbatim with
    /// its MILESTONE_STATE nodes, and a mission node's newer selection generation and unknown keys
    /// round-trip.
    /// </summary>
    [Collection("Sequential")]
    public class SaveFormatHoldTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public SaveFormatHoldTests()
        {
            SaveFormat.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            MilestoneStore.ResetForTesting();
            MissionStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-saveformat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            SaveFormat.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            MilestoneStore.ResetForTesting();
            MissionStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            try { Directory.Delete(tempDir, true); } catch { }
        }

        // ---------------------------------------------------------------- game-state events

        private static ConfigNode KnownEvent(double ut, GameStateEventType type, string key)
        {
            var node = new ConfigNode("GAME_STATE_EVENT");
            new GameStateEvent { ut = ut, eventType = type, key = key, valueBefore = 1, valueAfter = 2 }.SerializeInto(node);
            return node;
        }

        private static ConfigNode UnknownEvent(string type)
        {
            var node = new ConfigNode("GAME_STATE_EVENT");
            node.AddValue("ut", "150");
            node.AddValue("type", type);
            node.AddValue("key", "future-key");
            node.AddValue("futureField", "x = y");
            node.AddNode("FUTURE_PAYLOAD").AddValue("a", "1");
            return node;
        }

        [Theory]
        [InlineData("9999")]
        [InlineData("notANumber")]
        public void GameStateEvent_UnreadableType_IsDetected(string type)
        {
            Assert.True(GameStateEvent.IsUnreadableTypeNode(UnknownEvent(type)));
            Assert.False(GameStateEvent.IsUnreadableTypeNode(KnownEvent(1, GameStateEventType.TechResearched, "t")));
            var noType = new ConfigNode("GAME_STATE_EVENT");
            noType.AddValue("ut", "1");
            Assert.True(GameStateEvent.IsUnreadableTypeNode(noType));
        }

        [Fact]
        public void UnknownEventType_IsSkippedByConsumers_AndRoundTripsVerbatim()
        {
            var root = new ConfigNode(GameStateStore.EventsNodeName);
            root.AddValue("version", "1");
            root.AddNode(KnownEvent(100, GameStateEventType.TechResearched, "basicRocketry"));
            ConfigNode unknown = UnknownEvent("9999");
            root.AddNode(unknown);
            root.AddNode(KnownEvent(200, GameStateEventType.FundsChanged, "VesselRecovery"));

            Assert.True(GameStateStore.LoadEventsFromSaveNode(root));

            // Consumers read GameStateStore.Events: the unknown row is not there, and nothing
            // reads as the enum's zero value (ContractOffered) in its place.
            Assert.Equal(2, GameStateStore.EventCount);
            Assert.DoesNotContain(GameStateStore.Events, e => e.eventType == GameStateEventType.ContractOffered);
            Assert.Equal(1, GameStateStore.UnreadableEventCount);
            Assert.Contains(logLines, l => l.Contains("[GameStateStore]") && l.Contains("unreadableTypesKeptVerbatim=1"));
            Assert.DoesNotContain(logLines, l => l.Contains("Unknown event type id"));

            ConfigNode saved = GameStateStore.BuildEventsNode(null).Root;
            ConfigNode[] events = saved.GetNodes("GAME_STATE_EVENT");
            Assert.Equal(3, events.Length);
            Assert.Equal(unknown.ToString(), events[2].ToString());

            // A reload of the save keeps it again (a fixed point).
            Assert.True(GameStateStore.LoadEventsFromSaveNode(saved));
            Assert.Equal(1, GameStateStore.UnreadableEventCount);
            Assert.Equal(unknown.ToString(),
                GameStateStore.BuildEventsNode(null).Root.GetNodes("GAME_STATE_EVENT")[2].ToString());
        }

        [Fact]
        public void UnknownAttemptEvent_IsNotRestoredToMemory()
        {
            var attempt = new ConfigNode(GameStateStore.AttemptEventsNodeName);
            attempt.AddNode(KnownEvent(100, GameStateEventType.TechResearched, "basicRocketry"));
            attempt.AddNode(UnknownEvent("9999"));

            int restored = GameStateStore.RestoreAttemptEventsToMemory(attempt, null, out int dropped);

            Assert.Equal(1, restored);
            Assert.Equal(1, dropped);
            Assert.Equal(1, GameStateStore.EventCount);
            Assert.Contains(logLines, l => l.Contains("[GameStateStore]") && l.Contains("skipped 1 attempt event(s)"));
        }

        [Fact]
        public void Milestone_UnknownEventType_IsOutOfEvents_AndWrittenBack()
        {
            var node = new ConfigNode("MILESTONE");
            node.AddValue("id", "m1");
            node.AddValue("lastReplayedIdx", "0");
            node.AddNode(KnownEvent(100, GameStateEventType.TechResearched, "basicRocketry"));
            ConfigNode unknown = UnknownEvent("31");
            node.AddNode(unknown);

            Milestone m = Milestone.DeserializeFrom(node);

            Assert.Single(m.Events);
            Assert.Equal(GameStateEventType.TechResearched, m.Events[0].eventType);
            Assert.Single(m.UnreadableEventNodes);
            var saved = new ConfigNode("MILESTONE");
            m.SerializeInto(saved);
            ConfigNode[] events = saved.GetNodes("GAME_STATE_EVENT");
            Assert.Equal(2, events.Length);
            Assert.Equal(unknown.ToString(), events[1].ToString());
        }

        // ---------------------------------------------------------------- milestones version

        private static ConfigNode MilestonesNode(string version)
        {
            var root = new ConfigNode(MilestoneStore.MilestonesNodeName);
            root.AddValue("version", version);
            ConfigNode m = root.AddNode("MILESTONE");
            m.AddValue("id", "ms-1");
            m.AddValue("startUT", "0");
            m.AddValue("endUT", "10");
            m.AddNode(KnownEvent(5, GameStateEventType.TechResearched, "basicRocketry"));
            return root;
        }

        [Fact]
        public void Milestones_NewerVersion_AreHeld_AndReEmittedWithTheirState()
        {
            ConfigNode original = MilestonesNode("2");
            original.AddValue("futureMilestonesKey", "kept");
            var scenario = new ConfigNode("SCENARIO");
            ConfigNode state = scenario.AddNode("MILESTONE_STATE");
            state.AddValue("id", "ms-1");
            state.AddValue("lastReplayedIdx", "0");
            state.AddValue("futureStateKey", "kept");

            Assert.True(MilestoneStore.LoadMilestonesFromSaveNode(original));
            MilestoneStore.RestoreMutableState(scenario);

            Assert.Equal(0, MilestoneStore.MilestoneCount);
            Assert.True(MilestoneStore.IsHeldFromNewerVersion);
            Assert.Single(logLines, l => l.Contains("[WARN][MilestoneStore]")
                && l.Contains("version=2 newer than this build's 1") && l.Contains("held verbatim"));
            Assert.Equal(original.ToString(), MilestoneStore.BuildMilestonesNode().ToString());

            var savedScenario = new ConfigNode("SCENARIO");
            MilestoneStore.SaveMutableState(savedScenario);
            ConfigNode[] states = savedScenario.GetNodes("MILESTONE_STATE");
            Assert.Single(states);
            Assert.Equal(state.ToString(), states[0].ToString());
        }

        [Fact]
        public void Milestones_CurrentVersion_LoadAsBefore()
        {
            Assert.True(MilestoneStore.LoadMilestonesFromSaveNode(MilestonesNode("1")));

            Assert.Equal(1, MilestoneStore.MilestoneCount);
            Assert.False(MilestoneStore.IsHeldFromNewerVersion);
            Assert.Contains(logLines, l => l.Contains("[MilestoneStore]") && l.Contains("Loaded 1 milestones")
                && l.Contains("unreadableEventTypesKeptVerbatim=0"));
        }

        // ---------------------------------------------------------------- missions

        [Fact]
        public void Mission_NewerSelectionGeneration_WarnsOnce_AndUnknownKeysRoundTrip()
        {
            MissionStore.SuppressLogging = false;
            var scenario = new ConfigNode("SCENARIO");
            for (int i = 0; i < 2; i++)
            {
                ConfigNode m = scenario.AddNode("MISSION");
                m.AddValue("id", "mission-" + i);
                m.AddValue("treeId", "tree-" + i);
                m.AddValue("name", "Mission " + i);
                m.AddValue("selectionSchemaGeneration", "2");
                m.AddValue("futureMissionKey", "value " + i);
                m.AddNode("FUTURE_SELECTION").AddValue("k", "v");
            }

            MissionStore.Load(scenario);

            Assert.Equal(2, MissionStore.Missions.Count);
            Assert.Single(logLines, l => l.Contains("[WARN][Mission]")
                && l.Contains("2 mission(s) carry a newer selectionSchemaGeneration"));
            Assert.Contains(logLines, l => l.Contains("[INFO][Mission]") && l.Contains("unknownKeysKeptVerbatim=4"));

            var saved = new ConfigNode("SCENARIO");
            MissionStore.Save(saved);
            ConfigNode first = saved.GetNodes("MISSION")[0];
            Assert.Equal("value 0", first.GetValue("futureMissionKey"));
            Assert.Equal("v", first.GetNode("FUTURE_SELECTION").GetValue("k"));
            Assert.Equal("2", first.GetValue("selectionSchemaGeneration"));
            Assert.Single(first.GetValues("selectionSchemaGeneration"));
        }
    }
}
