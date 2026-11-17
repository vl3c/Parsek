using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live proof of the race mark (multiplayer R1-03, design 6.2): the scenario node of
    /// <see cref="ParsekRaceScenario"/>, a module stock never adds on its own, survives stock's
    /// own game load, scenario update and save, byte for byte. The xUnit suite proves the
    /// module's verbatim round trip and the codec; only the real game proves what stock does with
    /// the proto. Hosted by RACE-9 (a race save), but the scratch half needs no race and runs in
    /// any save that has a persistent.sfs.
    /// </summary>
    public class RaceModeTests
    {
        private const string Tag = "RaceModeInGame";
        private const string ProbeRaceId = "race_r103probe00000000000000000000000";
        private const string ProbePlayerId = "p_r103probe000000000000000000000000";
        private const string ProbeUnknownKey = "r103FutureKey";
        private const string ProbeUnknownChild = "R103_FUTURE_CHILD";

        [InGameTest(Category = "RaceMode", Scene = GameScenes.SPACECENTER,
            Description = "A ParsekRaceScenario proto added to a scratch copy of the loaded game "
                + "survives stock's Game load, UpdateScenarioModules, Game.Save and a second load "
                + "byte-equal, unknown key and child included; in a race save the live module is "
                + "instantiated and writes its node back verbatim.")]
        public void MarkSurvivesSaveLoad()
        {
            string saveFolder = HighLogic.SaveFolder;
            if (HighLogic.CurrentGame == null || string.IsNullOrEmpty(saveFolder))
            {
                InGameAssert.Skip("requires a loaded game with a save folder");
                return;
            }
            ConfigNode root = GamePersistence.LoadSFSFile("persistent", saveFolder);
            ConfigNode gameNode = root != null ? root.GetNode("GAME") : null;
            if (gameNode == null)
            {
                InGameAssert.Skip("requires the save's persistent.sfs with a GAME node");
                return;
            }

            // The scratch game must not build vessels (protovessels register live pids) and
            // must carry exactly the probe mark.
            gameNode.RemoveNodes("FLIGHTSTATE");
            RemoveRaceScenarioNodes(gameNode);
            ConfigNode probe = BuildProbeNode();
            gameNode.AddNode(probe.CreateCopy());
            string expected = probe.ToString();

            var scratch = new Game(gameNode);
            AssertProtoEquals(scratch, expected, "after new Game(node)");

            GamePersistence.UpdateScenarioModules(scratch);
            AssertProtoEquals(scratch, expected, "after GamePersistence.UpdateScenarioModules");

            var saved = new ConfigNode();
            scratch.Save(saved);
            ConfigNode savedGame = saved.GetNode("GAME");
            InGameAssert.IsNotNull(savedGame, "Game.Save must write a GAME node");
            List<ConfigNode> savedMarks = FindRaceScenarioNodes(savedGame);
            InGameAssert.AreEqual(1, savedMarks.Count, "Game.Save must write exactly one race mark");
            InGameAssert.AreEqual(expected, savedMarks[0].ToString(),
                "the race mark Game.Save wrote must equal the probe byte for byte");

            var reloaded = new Game(savedGame);
            AssertProtoEquals(reloaded, expected, "after a second new Game(saved node)");

            RaceLink parsed;
            string reason;
            InGameAssert.IsTrue(RaceLinkCodec.TryRead(savedMarks[0], out parsed, out reason),
                "the round-tripped mark must read: " + reason);
            InGameAssert.AreEqual(ProbeRaceId, parsed.RaceId, "round-tripped raceId");

            ParsekLog.Info(Tag, "MarkSurvivesSaveLoad: scratch round trip byte-equal ("
                + probe.values.Count + " values, " + probe.nodes.Count + " child)");

            CheckLiveModuleIfRaceSave();
        }

        /// <summary>
        /// In a race save (RACE-9): stock instantiated the module from its proto in this scene,
        /// and the module writes back exactly what it loaded.
        /// </summary>
        private static void CheckLiveModuleIfRaceSave()
        {
            ProtoScenarioModule proto = FindRaceProto(HighLogic.CurrentGame);
            if (proto == null)
            {
                ParsekLog.Info(Tag, "MarkSurvivesSaveLoad: no race mark in the loaded game; live half not run");
                return;
            }
            InGameAssert.IsTrue(proto.targetScenes.Contains(GameScenes.SPACECENTER),
                "the race mark proto must target SPACECENTER");
            var module = proto.moduleRef as ParsekRaceScenario;
            InGameAssert.IsNotNull(module,
                "stock must instantiate ParsekRaceScenario from the loaded race mark proto");
            InGameAssert.IsNotNull(module.Link, "the loaded race mark must read");

            var written = new ConfigNode("SCENARIO");
            module.Save(written);
            InGameAssert.AreEqual(proto.GetData().ToString(), written.ToString(),
                "the live module must write back the node it loaded");
            ParsekLog.Info(Tag, "MarkSurvivesSaveLoad: live module instantiated and verbatim, raceId="
                + module.Link.RaceId);
        }

        private static ConfigNode BuildProbeNode()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", RaceFormat.ScenarioModuleName);
            node.AddValue("scene", "7, 8, 5, 6");
            node.AddValue(RaceLinkCodec.KeyRaceLinkFormat, RaceFormat.LinkCurrent.ToString());
            node.AddValue(RaceLinkCodec.KeyRaceId, ProbeRaceId);
            node.AddValue(RaceLinkCodec.KeyPlayerId, ProbePlayerId);
            node.AddValue(RaceLinkCodec.KeySiteSlot, "2");
            node.AddValue(RaceLinkCodec.KeySiteTableId, RaceFormat.SiteTableKerbinEquator1);
            node.AddValue(RaceLinkCodec.KeyLadderId, RaceFormat.LadderStandard1);
            node.AddValue(RaceLinkCodec.KeyDateUnit, RaceFormat.DateUnitSecond);
            node.AddValue(RaceLinkCodec.KeyCalendar, "KERBIN");
            node.AddValue(RaceLinkCodec.KeyAlternateSites, "False");
            node.AddValue(RaceLinkCodec.KeyGameMode, "CAREER");
            node.AddValue(RaceLinkCodec.KeyDifficultyDigest, "r103-probe-digest");
            node.AddValue(ProbeUnknownKey, "kept");
            ConfigNode child = node.AddNode(ProbeUnknownChild);
            child.AddValue("a", "1");
            return node;
        }

        private static void AssertProtoEquals(Game game, string expected, string stage)
        {
            ProtoScenarioModule proto = FindRaceProto(game);
            InGameAssert.IsNotNull(proto, "the race mark proto must be in game.scenarios " + stage);
            InGameAssert.AreEqual(expected, proto.GetData().ToString(),
                "the race mark proto must equal the probe byte for byte " + stage);
        }

        private static ProtoScenarioModule FindRaceProto(Game game)
        {
            if (game == null || game.scenarios == null)
                return null;
            for (int i = 0; i < game.scenarios.Count; i++)
            {
                ProtoScenarioModule p = game.scenarios[i];
                if (p != null && p.moduleName == RaceFormat.ScenarioModuleName)
                    return p;
            }
            return null;
        }

        private static List<ConfigNode> FindRaceScenarioNodes(ConfigNode gameNode)
        {
            var found = new List<ConfigNode>();
            ConfigNode[] scenarios = gameNode.GetNodes("SCENARIO");
            for (int i = 0; i < scenarios.Length; i++)
                if (scenarios[i].GetValue("name") == RaceFormat.ScenarioModuleName)
                    found.Add(scenarios[i]);
            return found;
        }

        private static void RemoveRaceScenarioNodes(ConfigNode gameNode)
        {
            List<ConfigNode> marks = FindRaceScenarioNodes(gameNode);
            for (int i = 0; i < marks.Count; i++)
                gameNode.RemoveNode(marks[i]);
        }
    }
}
