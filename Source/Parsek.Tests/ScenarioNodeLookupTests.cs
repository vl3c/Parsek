using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <see cref="ScenarioNodeLookup"/>: the one "find a scenario node" reader. Pins the
    /// helper's own contract (null, absent, first of two, a node with no name) and, per caller
    /// that used to carry its own loop, the match order and not-found result that caller had,
    /// so a "last match" or "any match" drift reds here naming the caller.
    /// </summary>
    [Collection("Sequential")]
    public class ScenarioNodeLookupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> tempFiles = new List<string>();

        public ScenarioNodeLookupTests()
        {
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            PlaybackScopeTracker.ResetForTesting();
            ParsekScenario.PersistentSavePathOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            foreach (string f in tempFiles)
            {
                try { File.Delete(f); } catch { }
            }
        }

        private static ConfigNode Scenario(string name, string mark = null)
        {
            var node = new ConfigNode("SCENARIO");
            if (name != null)
                node.AddValue("name", name);
            node.AddValue("scene", "5, 7, 6, 8");
            if (mark != null)
                node.AddValue("mark", mark);
            return node;
        }

        private static ProtoScenarioModule Proto(ConfigNode node) => new ProtoScenarioModule(node);

        private static ConfigNode Game(params ConfigNode[] scenarios)
        {
            var game = new ConfigNode("GAME");
            game.AddValue("version", "1.12.5");
            game.AddNode("PARAMETERS");
            foreach (ConfigNode s in scenarios)
                game.AddNode(s);
            return game;
        }

        // ------------------------------------------------------------------ the helper itself

        [Fact]
        public void ParsekScenarioName_IsTheScenarioClassName()
        {
            Assert.Equal(nameof(ParsekScenario), ScenarioNodeLookup.ParsekScenarioName);
            Assert.Equal(ScenarioNodeLookup.ParsekScenarioName, SaveReferenceScanner.ParsekScenarioName);
        }

        [Fact]
        public void FindInProtoList_NullAbsentFoundFirstOfTwo()
        {
            string name = ScenarioNodeLookup.ParsekScenarioName;
            Assert.Null(ScenarioNodeLookup.FindInProtoList(null, name));
            Assert.Null(ScenarioNodeLookup.FindInProtoList(new List<ProtoScenarioModule>(), name));
            Assert.Null(ScenarioNodeLookup.FindInProtoList(
                new List<ProtoScenarioModule> { Proto(Scenario("ResearchAndDevelopment", "rnd")) }, name));

            var protos = new List<ProtoScenarioModule>
            {
                null,
                Proto(Scenario(null, "nameless")),
                Proto(Scenario("parsekscenario", "wrong-case")),
                Proto(Scenario("ResearchAndDevelopment", "rnd")),
                Proto(Scenario(name, "first")),
                Proto(Scenario(name, "second")),
            };
            ConfigNode found = ScenarioNodeLookup.FindInProtoList(protos, name);
            Assert.NotNull(found);
            Assert.Equal("first", found.GetValue("mark"));
            Assert.Same(protos[4].GetData(), found);
            Assert.Equal("rnd", ScenarioNodeLookup.FindInProtoList(protos, "ResearchAndDevelopment").GetValue("mark"));
        }

        [Fact]
        public void FindInGameNode_NullAbsentFoundFirstOfTwo()
        {
            string name = ScenarioNodeLookup.ParsekScenarioName;
            Assert.Null(ScenarioNodeLookup.FindInGameNode(null, name));
            Assert.Null(ScenarioNodeLookup.FindInGameNode(Game(), name));
            Assert.Null(ScenarioNodeLookup.FindInGameNode(Game(Scenario("ResearchAndDevelopment")), name));

            ConfigNode game = Game(
                Scenario(null, "nameless"),
                Scenario("parsekscenario", "wrong-case"),
                Scenario(name, "first"),
                Scenario(name, "second"));
            // A non-SCENARIO child carrying the name is not a scenario node.
            ConfigNode decoy = game.AddNode("NOT_A_SCENARIO");
            decoy.AddValue("name", name);
            ConfigNode found = ScenarioNodeLookup.FindInGameNode(game, name);
            Assert.NotNull(found);
            Assert.Equal("first", found.GetValue("mark"));
            Assert.Same(game.GetNodes("SCENARIO")[2], found);
        }

        [Fact]
        public void EnumerateScenarioNodes_FileOrderOnlyScenarioChildren_EmptyForNull()
        {
            Assert.Empty(ScenarioNodeLookup.EnumerateScenarioNodes(null));
            Assert.Empty(ScenarioNodeLookup.EnumerateScenarioNodes(Game()));

            ConfigNode game = Game(Scenario("A"), Scenario(null), Scenario("B"));
            ConfigNode[] all = ScenarioNodeLookup.EnumerateScenarioNodes(game);
            Assert.Equal(3, all.Length);
            Assert.Equal("A", all[0].GetValue("name"));
            Assert.Null(all[1].GetValue("name"));
            Assert.Equal("B", all[2].GetValue("name"));
        }

        // ------------------------------------------------------------------ per caller: first match

        private static ConfigNode ParsekWithScope(string mark, string inScopeId)
        {
            ConfigNode node = Scenario(ScenarioNodeLookup.ParsekScenarioName, mark);
            node.AddNode(PlaybackScopeTracker.NodeName).AddValue(PlaybackScopeTracker.InScopeKey, inScopeId);
            return node;
        }

        [Fact]
        public void PlaybackScopeTracker_UnionFromScenarios_TakesTheFirstParsekProto_LogsAbsence()
        {
            var protos = new List<ProtoScenarioModule>
            {
                null,
                Proto(ParsekWithScope("first", "rec-first")),
                Proto(ParsekWithScope("second", "rec-second")),
            };
            Assert.Equal(1, PlaybackScopeTracker.UnionFromScenarios(protos, "pin"));
            Assert.True(PlaybackScopeTracker.IsInReplayScope("rec-first"));
            Assert.False(PlaybackScopeTracker.IsInReplayScope("rec-second"));

            logLines.Clear();
            Assert.Equal(0, PlaybackScopeTracker.UnionFromScenarios(null, "pin"));
            Assert.Equal(0, PlaybackScopeTracker.UnionFromScenarios(
                new List<ProtoScenarioModule> { Proto(Scenario("ResearchAndDevelopment")) }, null));
            Assert.Contains(logLines, l => l.Contains("[PlaybackScope]")
                && l.Contains("Replay scope merged from the save (pin): no Parsek scenario in the parsed save, nothing to merge"));
            Assert.Contains(logLines, l => l.Contains("[PlaybackScope]")
                && l.Contains("Replay scope merged from the save (unspecified): no Parsek scenario in the parsed save, nothing to merge"));
        }

        [Fact]
        public void PlaybackScopeTracker_UnionFromGameNode_TakesTheFirstParsekNode_LogsAbsence()
        {
            ConfigNode game = Game(ParsekWithScope("first", "rec-first"), ParsekWithScope("second", "rec-second"));
            Assert.Equal(1, PlaybackScopeTracker.UnionFromGameNode(game, "pin"));
            Assert.True(PlaybackScopeTracker.IsInReplayScope("rec-first"));
            Assert.False(PlaybackScopeTracker.IsInReplayScope("rec-second"));

            logLines.Clear();
            Assert.Equal(0, PlaybackScopeTracker.UnionFromGameNode(null, "pin"));
            Assert.Contains(logLines, l => l.Contains("[PlaybackScope]")
                && l.Contains("Replay scope merged from the save (pin): no Parsek scenario in the save, nothing to merge"));
        }

        [Fact]
        public void RouteLoadReconcile_ReadSavedEventsFromScenarios_TakesTheFirstParsekProto()
        {
            ConfigNode withoutLog = Scenario(ScenarioNodeLookup.ParsekScenarioName, "first");
            ConfigNode withLog = Scenario(ScenarioNodeLookup.ParsekScenarioName, "second");
            new RouteEvent { Type = RouteEventType.RouteDispatched, UT = 3000.0, RouteId = "r" }
                .SerializeInto(withLog.AddNode(RouteEventLog.NodeName));

            List<RouteEvent> read = RouteLoadReconcile.ReadSavedEventsFromScenarios(
                new List<ProtoScenarioModule> { Proto(withoutLog), Proto(withLog) });
            Assert.NotNull(read);
            Assert.Empty(read);
            Assert.Single(RouteLoadReconcile.ReadSavedEventsFromScenarios(
                new List<ProtoScenarioModule> { null, Proto(withLog), Proto(withoutLog) }));

            Assert.Null(RouteLoadReconcile.ReadSavedEventsFromScenarios(null));
            Assert.Null(RouteLoadReconcile.ReadSavedEventsFromScenarios(
                new List<ProtoScenarioModule> { Proto(Scenario("ResearchAndDevelopment")) }));
        }

        [Fact]
        public void RouteStore_ReadSavedRouteStateFloorUTFromScenarios_TakesTheFirstParsekProto()
        {
            ConfigNode first = Scenario(ScenarioNodeLookup.ParsekScenarioName, "first");
            first.AddValue("routeStateFloorUT", "100");
            ConfigNode second = Scenario(ScenarioNodeLookup.ParsekScenarioName, "second");
            second.AddValue("routeStateFloorUT", "200");

            Assert.Equal(100.0, RouteStore.ReadSavedRouteStateFloorUTFromScenarios(
                new List<ProtoScenarioModule> { null, Proto(first), Proto(second) }));
            Assert.True(double.IsNaN(RouteStore.ReadSavedRouteStateFloorUTFromScenarios(null)));
            Assert.True(double.IsNaN(RouteStore.ReadSavedRouteStateFloorUTFromScenarios(
                new List<ProtoScenarioModule> { Proto(Scenario("ResearchAndDevelopment")) })));
        }

        [Fact]
        public void RouteStore_ReadSavedCommittedRoutesFromScenarios_TakesTheFirstParsekProto()
        {
            ConfigNode noRoutes = Scenario(ScenarioNodeLookup.ParsekScenarioName, "first");
            ConfigNode withRoutes = Scenario(ScenarioNodeLookup.ParsekScenarioName, "second");
            var route = new Route
            {
                Id = "route-lookup",
                Name = "route-lookup",
                Status = RouteStatus.Active,
                CreatedUT = 900.0,
                IsKscOrigin = true,
                DispatchInterval = 300.0,
                TransitDuration = 300.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop { Endpoint = new RouteEndpoint { VesselPersistentId = 42u } },
                },
            };
            route.SerializeInto(withRoutes.AddNode("ROUTES").AddNode("ROUTE"));

            List<Route> read = RouteStore.ReadSavedCommittedRoutesFromScenarios(
                new List<ProtoScenarioModule> { Proto(noRoutes), Proto(withRoutes) });
            Assert.NotNull(read);
            Assert.Empty(read);
            Route other = Assert.Single(RouteStore.ReadSavedCommittedRoutesFromScenarios(
                new List<ProtoScenarioModule> { null, Proto(withRoutes), Proto(noRoutes) }));
            Assert.Equal("route-lookup", other.Id);
            Assert.Null(RouteStore.ReadSavedCommittedRoutesFromScenarios(null));
            Assert.Null(RouteStore.ReadSavedCommittedRoutesFromScenarios(
                new List<ProtoScenarioModule> { Proto(Scenario("ResearchAndDevelopment")) }));
        }

        [Fact]
        public void LedgerHeldWindow_ReadSavedFromScenarios_TakesTheFirstParsekProto()
        {
            ConfigNode first = Scenario(ScenarioNodeLookup.ParsekScenarioName, "first");
            first.AddValue(LedgerHeldWindow.ValueName, "100");
            ConfigNode second = Scenario(ScenarioNodeLookup.ParsekScenarioName, "second");
            second.AddValue(LedgerHeldWindow.ValueName, "200");

            Assert.Equal(100.0, LedgerHeldWindow.ReadSavedFromScenarios(
                new List<ProtoScenarioModule> { null, Proto(first), Proto(second) }));
            Assert.True(double.IsNaN(LedgerHeldWindow.ReadSavedFromScenarios(null)));
            Assert.True(double.IsNaN(LedgerHeldWindow.ReadSavedFromScenarios(
                new List<ProtoScenarioModule> { Proto(Scenario("ResearchAndDevelopment")) })));
        }

        [Fact]
        public void PreParsekBackup_Footprint_ReadsAnyParsekNode_NotOnlyTheFirst()
        {
            // The footprint rule is "any populated Parsek node": an empty first node must not
            // hide a populated second one.
            ConfigNode empty = new ConfigNode("SCENARIO");
            empty.AddValue("name", ScenarioNodeLookup.ParsekScenarioName);
            empty.AddValue("scene", "5, 7, 6, 8");
            ConfigNode populated = Scenario(ScenarioNodeLookup.ParsekScenarioName, "populated");

            var root = new ConfigNode();
            root.AddNode(Game(empty, populated));
            Assert.True(PreParsekBackup.HasParsekGameplayFootprint(root, parsekSubdirExists: false));

            var unwrapped = Game(empty.CreateCopy());
            Assert.False(PreParsekBackup.HasParsekGameplayFootprint(unwrapped, parsekSubdirExists: false));
            unwrapped.AddNode(populated.CreateCopy());
            Assert.True(PreParsekBackup.HasParsekGameplayFootprint(unwrapped, parsekSubdirExists: false));
        }

        private string WriteSfs(ConfigNode root)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "parsek-scenario-lookup-" + Guid.NewGuid().ToString("N") + ".sfs");
            root.Save(path);
            tempFiles.Add(path);
            return path;
        }

        [Fact]
        public void ParsekScenario_CarryForwardSource_TakesTheFirstParsekNode_WrappedOrNot()
        {
            string name = ScenarioNodeLookup.ParsekScenarioName;
            var wrapped = new ConfigNode();
            wrapped.AddNode(Game(Scenario("ResearchAndDevelopment"), Scenario(name, "first"), Scenario(name, "second")));
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteSfs(wrapped);
            ConfigNode found = ParsekScenario.TryLoadCarryForwardSourceParsekNode(out _);
            Assert.NotNull(found);
            Assert.Equal("first", found.GetValue("mark"));

            // A root with no GAME wrapper is read as the GAME node itself.
            ConfigNode bare = Game(Scenario(name, "first"), Scenario(name, "second"));
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteSfs(bare);
            Assert.Equal("first", ParsekScenario.TryLoadCarryForwardSourceParsekNode(out _).GetValue("mark"));

            ParsekScenario.PersistentSavePathOverrideForTesting = WriteSfs(Game(Scenario("ResearchAndDevelopment")));
            Assert.Null(ParsekScenario.TryLoadCarryForwardSourceParsekNode(out _));
        }
    }
}
