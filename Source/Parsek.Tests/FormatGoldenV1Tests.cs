using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Parsek.Logistics;
using Xunit;
using G = Parsek.Tests.FormatGoldenV1GeneratorTests;

namespace Parsek.Tests
{
    /// <summary>
    /// Loads the frozen v1 format goldens (<c>Fixtures/FormatGolden/v1/</c>: the generated save
    /// and the harvested <c>career/</c>) through the production load helpers in the cold
    /// <c>ParsekScenario.OnLoad</c> order and asserts concrete content. Every build from 1.0 on
    /// must keep these green: a red cell here means a save written by 1.0 no longer loads.
    /// The golden files are never edited; a fix belongs in the reader or in an upgrade step.
    /// </summary>
    [Collection("Sequential")]
    public class FormatGoldenV1Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> tempDirs = new List<string>();
        private readonly GameScenes originalScene;

        public FormatGoldenV1Tests()
        {
            originalScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            G.ResetAllStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            G.ResetAllStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            HighLogic.LoadedScene = originalScene;
            foreach (string dir in tempDirs)
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ---------------------------------------------------------------- staging and loading

        private string NewTempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "parsek-golden-" + tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            tempDirs.Add(dir);
            return dir;
        }

        /// <summary>A copy of a golden save folder, so nothing a load does can touch the committed files.</summary>
        private string Stage(string relative)
        {
            string source = Path.Combine(FormatGolden.VersionDir(1), relative);
            Assert.True(File.Exists(Path.Combine(source, "persistent.sfs")), "golden save missing at " + source);
            string dest = NewTempDir("stage");
            foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = path.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar);
                if (relative.Length == 0 && rel.StartsWith(FormatGolden.CareerFolderName + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                    continue;
                string to = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(path, to);
            }
            return dest;
        }

        private static ConfigNode ScenarioOf(string saveDir)
        {
            ConfigNode scenario = FormatGolden.LoadParsekScenario(Path.Combine(saveDir, "persistent.sfs"));
            Assert.NotNull(scenario);
            return scenario;
        }

        /// <summary>
        /// The cold load of one PARSEK node, phase for phase in OnLoad's order: the save-wide stamp,
        /// crew and groups, the Re-Fly state, the stock pool reference, the held window, the rewind
        /// vessel holds, the replay scope, the game state, vessel names, the trees (with their
        /// sidecars from <paramref name="saveDir"/>), missions, milestone replay state, routes.
        /// Returns the scenario instance holding the Re-Fly state.
        /// </summary>
        private static ParsekScenario LoadSave(string saveDir, ConfigNode node, double saveUT)
        {
            RecordingPaths.SaveRootOverrideForTesting = saveDir;
            SaveFormat.ClassifyLoadedScenarioNode(node, hasParsekData: true);

            FieldInfo initialLoadDone = typeof(ParsekScenario).GetField(
                "initialLoadDone", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(initialLoadDone);
            object previous = initialLoadDone.GetValue(null);
            try
            {
                initialLoadDone.SetValue(null, false);
                MethodInfo crew = typeof(ParsekScenario).GetMethod(
                    "LoadCrewAndGroupState", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(crew);
                crew.Invoke(null, new object[] { node });
            }
            finally
            {
                initialLoadDone.SetValue(null, previous);
            }

            // The stock-action intent's codec is read here; its load-time freshness check needs
            // Unity's realtime clock, which the headless host lacks, so the staging load gets a
            // copy without it (a cross-process load clears an intent as stale anyway).
            ConfigNode intentNode = node.GetNode(StockActionIntentMarker.NodeName);
            if (intentNode != null)
                Assert.True(StockActionIntentMarker.TryLoadFrom(intentNode, out _), "intent marker did not decode");
            ConfigNode stagingNode = node.CreateCopy();
            stagingNode.RemoveNodes(StockActionIntentMarker.NodeName);
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);
            G.InvokeInstance(scenario, "LoadRewindStagingState", stagingNode);

            StockPoolReference.LoadFrom(node, "Cold");
            LedgerHeldWindow.InstallAfterLoad(saveUT, LedgerHeldWindow.ReadSaved(node), "golden load");
            RewindVesselHold.Replace(RewindVesselHold.LoadFrom(node), "golden load");
            PlaybackScopeTracker.InstallFromSave(node, "golden load");
            RemovedReplayFlags.Replace(RemovedReplayFlags.LoadFrom(node), "golden load");

            ParsekScenario.LoadGameStateFromNodeOrFiles(node);

            VesselNameStore.Load(node);
            ParsekScenario.LoadRecordingTrees(node, RecordingStore.CommittedRecordings);
            MissionStore.Load(node);
            MissionStore.EnsureDefaultsForTrees(RecordingStore.CommittedTrees);
            MilestoneStore.RestoreMutableState(node);
            RouteStore.LoadRoutesFrom(node);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private ParsekScenario LoadGenerated(out string saveDir)
        {
            saveDir = Stage("");
            return LoadSave(saveDir, ScenarioOf(saveDir), G.SaveUT);
        }

        private static Recording Rec(string id)
        {
            Recording rec = RecordingStore.CommittedRecordings.FirstOrDefault(r => r.RecordingId == id);
            Assert.True(rec != null, "recording " + id + " did not load");
            return rec;
        }

        private static double RecalculatedFunds()
        {
            LedgerOrchestrator.Initialize();
            RecalculationEngine.Recalculate(LedgerOrchestrator.Modules, new List<GameAction>(EffectiveState.ComputeELS()));
            return LedgerOrchestrator.Funds.GetRunningBalance();
        }

        // ---------------------------------------------------------------- generated golden: content

        [Fact]
        public void Generated_LoadsBothTreesEveryRecordingAndTheirSidecars()
        {
            LoadGenerated(out _);

            Assert.Equal(2, RecordingStore.CommittedTrees.Count);
            Assert.Equal(0, InertRecordingTrees.Count);
            var ids = RecordingStore.CommittedRecordings.Select(r => r.RecordingId).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(new[] { G.RootId, G.UpperId, G.DebrisId, G.ReflyId, G.RoverId }, ids);

            RecordingTree lander = RecordingStore.CommittedTrees.Single(t => t.Id == G.LanderTreeId);
            Assert.Equal(G.LanderTreeName, lander.TreeName);
            BranchPoint sep = Assert.Single(lander.BranchPoints);
            Assert.Equal(G.SeparationBpId, sep.Id);
            Assert.Equal(G.RewindPointId, sep.RewindPointId);
            Assert.Equal(3, sep.ChildRecordingIds.Count);

            Recording root = Rec(G.RootId);
            Assert.Equal(4, root.Points.Count);
            Assert.Equal(2, root.TrackSections.Count);
            Assert.Equal(3, root.PartEvents.Count);
            Assert.Single(root.FlagEvents);
            Assert.NotNull(root.VesselSnapshot);
            Assert.NotNull(root.GhostVisualSnapshot);
            Assert.Equal(G.LanderTreeName, root.VesselSnapshot.GetValue("name"));
            Assert.Equal("LaunchPad", root.LaunchSiteName);

            Recording upper = Rec(G.UpperId);
            Assert.Equal(2, upper.Points.Count);
            Assert.Single(upper.OrbitSegments);
            Assert.Equal(TerminalState.Orbiting, upper.TerminalStateValue);
            Assert.Equal(705000.0, upper.TerminalOrbitSemiMajorAxis, 3);
            Assert.NotNull(upper.VesselSnapshot);
            Assert.Equal(KerbalEndState.Aboard, upper.CrewEndStates[G.Pilot]);

            Recording debris = Rec(G.DebrisId);
            Assert.True(debris.IsDebris);
            Assert.Equal(G.RootId, debris.ParentAnchorRecordingId);
            TrackSection rel = Assert.Single(debris.TrackSections);
            Assert.Equal(ReferenceFrame.Relative, rel.referenceFrame);
            Assert.Equal(G.RootId, rel.anchorRecordingId);
            Assert.Equal(2, rel.frames.Count);
            Assert.Equal(2, rel.bodyFixedFrames.Count);
            Assert.Null(debris.VesselSnapshot);

            Assert.Equal(MergeState.CommittedProvisional, Rec(G.ReflyId).MergeState);

            Recording rover = Rec(G.RoverId);
            Assert.Equal(3, rover.Points.Count);
            Assert.True(rover.LoopPlayback);
            Assert.Equal(TerminalState.Landed, rover.TerminalStateValue);
            Assert.Equal(G.RoverDecisionName, rover.VesselName);
        }

        [Fact]
        public void Generated_LoadsTheGameStateLedgerAndRecalculatesTheFunds()
        {
            LoadGenerated(out _);

            Assert.Equal(15, Ledger.Actions.Count);
            Assert.Contains(Ledger.Actions, a => a.ActionId == G.TombstonedActionId);
            Assert.True(Ledger.ProgressSeed.Captured);

            Assert.Equal(6, GameStateStore.EventCount);
            Assert.Single(GameStateStore.ContractSnapshots);
            GameStateBaseline baseline = Assert.Single(GameStateStore.Baselines);
            Assert.Equal(9.0, baseline.ut);
            Assert.Equal(25000.0, baseline.funds);
            Milestone milestone = Assert.Single(MilestoneStore.Milestones);
            Assert.Equal(G.SaveUT, milestone.EndUT);
            Assert.Equal(6, milestone.Events.Count);
            Assert.Single(GameStateRecorder.PendingScienceSubjects);

            // 25000 seed + 1000 advance - 2000 pad upgrade - 1500 hire - 8000 build
            // + 800 milestone + 6000 contract reward + 3000 recovery.
            Assert.Equal(24300.0, RecalculatedFunds(), 3);
        }

        [Fact]
        public void Generated_LoadsTheReFlyStateMissionsNamesRoutesAndTheSmallStores()
        {
            ParsekScenario scenario = LoadGenerated(out _);

            RewindPoint rp = Assert.Single(scenario.RewindPoints);
            Assert.Equal(G.RewindPointId, rp.RewindPointId);
            Assert.Equal(G.SeparationBpId, rp.BranchPointId);
            Assert.Equal(2, rp.ChildSlots.Count);
            Assert.Equal(G.UpperId, rp.ChildSlots[0].OriginChildRecordingId);
            Assert.True(rp.ChildSlots[1].Disabled);
            RecordingSupersedeRelation rel = Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal(G.UpperId, rel.OldRecordingId);
            Assert.Equal(G.ReflyId, rel.NewRecordingId);
            Assert.Empty(scenario.RecordingRewindRetirements);
            LedgerTombstone tomb = Assert.Single(scenario.LedgerTombstones);
            Assert.Equal(G.TombstonedActionId, tomb.ActionId);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal(G.ReflyId, scenario.ActiveReFlySessionMarker.ActiveReFlyRecordingId);
            Assert.NotNull(scenario.ActiveMergeJournal);
            Assert.Equal(G.LanderTreeId, scenario.ActiveMergeJournal.TreeId);

            var names = MissionStore.Missions.Select(m => m.Name).ToList();
            Assert.Equal(3, names.Count);
            Assert.Contains(G.LanderMissionName, names);
            Assert.Contains(G.LanderMissionName + " (no debris)", names);
            Assert.Contains(G.RoverTreeName, names);
            Mission roverMission = MissionStore.Missions.Single(m => m.TreeId == G.RoverTreeId);
            Assert.True(roverMission.LoopPlayback);
            Assert.Equal(600.0, roverMission.LoopIntervalSeconds);

            VesselNameDecision decision = Assert.Single(VesselNameStore.Decisions);
            Assert.Equal(G.RoverDecisionName, decision.Name);
            Assert.Contains(G.RoverId, decision.CoveredLegIds);
            Assert.True(VesselNameStore.TryGetLink("e60c0000000000000000000000000061", out _));
            Assert.True(VesselNameStore.TryGetDockAbsorb(7100u, out _));

            Assert.Equal(new[] { G.RouteName0, G.RouteName1 }, RouteStore.CommittedRoutes.Select(r => r.Name));
            Assert.Equal(G.DormantRouteName, Assert.Single(RouteStore.DormantRoutes).Name);
            Assert.Equal(Enum.GetValues(typeof(RouteEventType)).Length, RouteEventLog.Events.Count);
            Assert.Equal(450.25, RouteStore.StateFloorUT);

            Assert.Equal(G.StandIn, LedgerOrchestrator.Kerbals.Slots[G.Pilot].Chain.Single());
            Assert.Equal(G.StandIn, CrewReservationManager.CrewReplacements[G.Pilot]);
            Assert.True(GroupHierarchyStore.TryGetGroupParent(G.RoverTreeName, out string parent));
            Assert.Equal(G.LanderTreeName, parent);
            Assert.True(GroupHierarchyStore.IsGroupHidden(G.LanderTreeName + " / Debris"));

            Assert.NotEmpty(RewindVesselHold.Holds);
            Assert.True(PlaybackScopeTracker.InReplayScopeCount > 0);
            Assert.NotEmpty(RemovedReplayFlags.Tombstones);
            Assert.True(LedgerHeldWindow.IsSet);
        }

        [Fact]
        public void Generated_EveryStoreTheSaveCarries_IsAStoreTheLoadRead()
        {
            // The rewind point's quicksave sits beside the save, as RewindPointAuthor leaves it.
            string saveDir = Stage("");
            Assert.True(File.Exists(Path.Combine(saveDir, "Parsek", "RewindPoints", G.RewindPointId + ".sfs")));
            ConfigNode node = ScenarioOf(saveDir);
            foreach (string child in new[]
            {
                "RECORDING_TREE", "MISSION", "VESSEL_NAMES", "KERBAL_SLOTS", "CREW_REPLACEMENTS",
                CrewReservationManager.RescuePlacedNodeName, "GROUP_HIERARCHY", "HIDDEN_GROUPS",
                "MILESTONE_STATE", ParsekScenario.GameStateNodeName, "REWIND_POINTS", "RECORDING_SUPERSEDES",
                "LEDGER_TOMBSTONES", ReFlySessionMarker.NodeName, MergeJournal.NodeName,
                StockActionIntentMarker.NodeName, SwitchSegmentSession.NodeName, "ROUTES", "DORMANT_ROUTES",
                "ROUTE_EVENT_LOG", "RETIRED_ROUTE_CARGO", "STOCK_POOL_REFERENCE", "REWIND_VESSEL_HOLDS",
                "PLAYBACK_REPLAY_SCOPE",
            })
            {
                Assert.True(node.HasNode(child), "the v1 golden carries no " + child + " node");
            }
        }

        // ---------------------------------------------------------------- stamps

        private sealed class Stamp
        {
            internal SaveFormatInfo Format;
            internal int? Value;
            internal string Where;
        }

        /// <summary>Every format stamp a save folder carries, read from the files themselves.</summary>
        private static List<Stamp> ReadStamps(string saveDir)
        {
            var stamps = new List<Stamp>();
            void Add(SaveFormatInfo f, string raw, string where) =>
                stamps.Add(new Stamp { Format = f, Value = SaveFormat.ParseStored(raw), Where = where });

            ConfigNode node = ScenarioOf(saveDir);
            Add(SaveFormat.Save, node.GetValue(SaveFormat.SaveFormatKey), "PARSEK");
            foreach (ConfigNode tree in node.GetNodes("RECORDING_TREE"))
            {
                string t = "tree " + tree.GetValue("id");
                Add(SaveFormat.TreeFormat, tree.GetValue("treeFormatVersion"), t);
                Add(SaveFormat.RecordingSchemaGeneration, tree.GetValue("recordingSchemaGeneration"), t);
                foreach (ConfigNode rec in tree.GetNodes("RECORDING"))
                {
                    string r = "recording " + rec.GetValue("recordingId");
                    Add(SaveFormat.RecordingFormat, rec.GetValue("recordingFormatVersion"), r);
                    Add(SaveFormat.RecordingSchemaGeneration, rec.GetValue("recordingSchemaGeneration"), r);
                }
            }
            foreach (ConfigNode mission in node.GetNodes("MISSION"))
                Add(SaveFormat.MissionSelection, mission.GetValue("selectionSchemaGeneration"),
                    "mission " + mission.GetValue("id"));
            ConfigNode gameState = node.GetNode(ParsekScenario.GameStateNodeName);
            if (gameState != null)
            {
                Add(SaveFormat.Ledger, gameState.GetNode("LEDGER")?.GetValue("version"), "GAME_STATE/LEDGER");
                Add(SaveFormat.GameStateStoreFormat,
                    gameState.GetNode(GameStateStore.EventsNodeName)?.GetValue("version"), "GAME_STATE/events");
                Add(SaveFormat.Milestones,
                    gameState.GetNode(MilestoneStore.MilestonesNodeName)?.GetValue("version"), "GAME_STATE/milestones");
            }

            string gs = Path.Combine(saveDir, "Parsek", "GameState");
            Add(SaveFormat.Ledger, ConfigNode.Load(Path.Combine(gs, "ledger.pgld")).GetValue("version"), "ledger.pgld");
            Add(SaveFormat.GameStateStoreFormat, ConfigNode.Load(Path.Combine(gs, "events.pgse")).GetValue("version"),
                "events.pgse");
            Add(SaveFormat.Milestones, ConfigNode.Load(Path.Combine(gs, "milestones.pgsm")).GetValue("version"),
                "milestones.pgsm");

            foreach (string prec in Directory.GetFiles(Path.Combine(saveDir, "Parsek", "Recordings"), "*.prec"))
            {
                Assert.True(TrajectorySidecarBinary.TryProbe(prec, out TrajectorySidecarProbe probe), prec);
                Assert.True(probe.Supported, Path.GetFileName(prec) + ": " + probe.FailureReason);
                stamps.Add(new Stamp { Format = SaveFormat.TrajectoryBinary, Value = probe.FormatVersion,
                    Where = Path.GetFileName(prec) });
                stamps.Add(new Stamp { Format = SaveFormat.RecordingSchemaGeneration, Value = probe.SchemaGeneration,
                    Where = Path.GetFileName(prec) });
            }
            foreach (string craft in Directory.GetFiles(Path.Combine(saveDir, "Parsek", "Recordings"), "*.craft"))
            {
                Assert.True(SnapshotSidecarCodec.TryProbe(craft, out SnapshotSidecarProbe probe), craft);
                Assert.True(probe.Supported, Path.GetFileName(craft) + ": " + probe.FailureReason);
                stamps.Add(new Stamp { Format = SaveFormat.Snapshot, Value = probe.FormatVersion,
                    Where = Path.GetFileName(craft) });
            }
            return stamps;
        }

        [Fact]
        public void Generated_EveryStampIsCurrentOrUpgradable_EqualsItsManifest_AndEveryFormatIsExercised()
        {
            string saveDir = Stage("");
            Dictionary<string, int> manifest = FormatGolden.ReadManifest(
                Path.Combine(FormatGolden.VersionDir(1), FormatGolden.ManifestFileName));
            List<Stamp> stamps = ReadStamps(saveDir);

            var problems = new List<string>();
            foreach (Stamp s in stamps)
            {
                SaveFormatClass c = s.Format.Classify(s.Value);
                if (c != SaveFormatClass.Current && c != SaveFormatClass.Upgradable)
                    problems.Add($"{s.Where}: {s.Format.Name}={s.Value?.ToString(CultureInfo.InvariantCulture) ?? "missing"} is {c}");
                else if (manifest[s.Format.Name] != s.Value)
                    problems.Add($"{s.Where}: {s.Format.Name}={s.Value} but formats.txt says {manifest[s.Format.Name]}");
            }
            foreach (SaveFormatInfo f in SaveFormat.All)
            {
                if (!stamps.Any(s => s.Format == f))
                    problems.Add("no stamp of " + f.Name + " in the v1 golden");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        // ---------------------------------------------------------------- generated golden: save + reload

        /// <summary>The loaded content, independent of epochs, stamps and ids a save regenerates.</summary>
        private static string ContentSignature(ParsekScenario scenario)
        {
            var sb = new StringBuilder();
            foreach (Recording r in RecordingStore.CommittedRecordings.OrderBy(x => x.RecordingId, StringComparer.Ordinal))
            {
                sb.Append(r.RecordingId).Append(' ').Append(r.VesselName)
                    .Append(" pts=").Append(r.Points.Count)
                    .Append(" orb=").Append(r.OrbitSegments.Count)
                    .Append(" sec=").Append(r.TrackSections.Count)
                    .Append(" ev=").Append(r.PartEvents.Count)
                    .Append(" flag=").Append(r.FlagEvents.Count)
                    .Append(" term=").Append(r.TerminalStateValue?.ToString() ?? "-")
                    .Append(" merge=").Append(r.MergeState)
                    .Append(" snap=").Append(r.VesselSnapshot != null ? r.VesselSnapshot.ToString().Length : -1)
                    .Append(" ghost=").Append(r.GhostVisualSnapshot != null ? r.GhostVisualSnapshot.ToString().Length : -1)
                    .Append('\n');
                foreach (TrajectoryPoint p in r.Points)
                    sb.Append("  ").Append(p.ut.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                        .Append(p.altitude.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            foreach (RecordingTree t in RecordingStore.CommittedTrees.OrderBy(x => x.Id, StringComparer.Ordinal))
                sb.Append("tree ").Append(t.Id).Append(' ').Append(t.TreeName).Append(" bp=").Append(t.BranchPoints.Count).Append('\n');

            sb.Append(Ledger.BuildSaveNode().ToString());
            sb.Append(GameStateStore.BuildEventsNode(null).Root.ToString());
            foreach (GameStateBaseline b in GameStateStore.Baselines)
                sb.Append(GameStateStore.BuildBaselineNode(b).ToString());
            sb.Append(MilestoneStore.BuildMilestonesNode().ToString());
            sb.Append(VesselNameStore.EncodeNode().ToString());

            var missions = new ConfigNode("M");
            MissionStore.Save(missions);
            sb.Append(missions.ToString());
            var routes = new ConfigNode("R");
            RouteStore.SaveRoutesTo(routes);
            sb.Append(routes.ToString());
            var crew = new ConfigNode("C");
            LedgerOrchestrator.Kerbals.SaveSlots(crew);
            CrewReservationManager.SaveCrewReplacements(crew);
            GroupHierarchyStore.SaveInto(crew);
            sb.Append(crew.ToString());
            var staging = new ConfigNode("S");
            G.InvokeInstance(scenario, "SaveRewindStagingState", staging);
            sb.Append(staging.ToString());
            var small = new ConfigNode("X");
            StockPoolReference.SaveTo(small);
            LedgerHeldWindow.SaveInto(small);
            RewindVesselHold.SaveInto(small);
            PlaybackScopeTracker.SaveInto(small);
            RemovedReplayFlags.SaveInto(small);
            sb.Append(small.ToString());
            return sb.ToString();
        }

        [Fact]
        public void Generated_LoadSaveReload_KeepsTheContent()
        {
            ParsekScenario first = LoadGenerated(out _);
            string before = ContentSignature(first);

            // Save with the production writers into a fresh save folder, then cold-load that.
            string resaved = NewTempDir("resave");
            RecordingPaths.SaveRootOverrideForTesting = resaved;
            ConfigNode node = G.RunSaveWriters(first);
            var root = new ConfigNode();
            root.AddNode("GAME").AddNode(node);
            root.Save(Path.Combine(resaved, "persistent.sfs"));

            G.ResetAllStores();
            ParsekScenario second = LoadSave(resaved, ScenarioOf(resaved), G.SaveUT);
            string after = ContentSignature(second);

            Assert.Equal(before, after);
            Assert.Equal(5, RecordingStore.CommittedRecordings.Count);
        }

        // ---------------------------------------------------------------- the harvested career

        private const string CareerTreeId = "5eb172ab29034ccbbac497ce6406c149";
        private const string CareerFlightId = "1d611e7533a64508ae6f3b305a51615e";
        private const string CareerContinuationId = "5436a7e8840b4c5885afcbaedc9dc037";

        [Fact]
        public void Career_LoadsItsTreeSidecarsLedgerAndPools()
        {
            string saveDir = Stage(FormatGolden.CareerFolderName);
            LoadSave(saveDir, ScenarioOf(saveDir), 408.72);

            RecordingTree tree = Assert.Single(RecordingStore.CommittedTrees);
            Assert.Equal(CareerTreeId, tree.Id);
            Assert.Equal(0, InertRecordingTrees.Count);
            var ids = RecordingStore.CommittedRecordings.Select(r => r.RecordingId).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(new[] { CareerFlightId, CareerContinuationId }, ids);
            Recording flight = Rec(CareerFlightId);
            Assert.True(flight.Points.Count > 100, "flight points=" + flight.Points.Count);
            Assert.NotNull(flight.VesselSnapshot);

            Assert.Equal(15, Ledger.Actions.Count);
            Assert.True(GameStateStore.EventCount > 0);
            Assert.True(MilestoneStore.MilestoneCount > 0);
            Assert.Equal(2, GameStateStore.BaselineCount);
            Assert.Single(MissionStore.Missions);

            // KSP's own funds pool on that save (C2CareerPostFixReplayTests).
            Assert.Equal(536558.0, RecalculatedFunds(), 3);
        }

        [Fact]
        public void Career_EveryStampIsCurrentOrUpgradable_ExceptTheSaveStampItPredates()
        {
            string saveDir = Stage(FormatGolden.CareerFolderName);
            var problems = new List<string>();
            foreach (Stamp s in ReadStamps(saveDir))
            {
                SaveFormatClass c = s.Format.Classify(s.Value);
                // Harvested before parsekSaveFormat existed; a missing stamp loads best effort.
                if (s.Format == SaveFormat.Save && c == SaveFormatClass.Missing)
                    continue;
                if (c != SaveFormatClass.Current && c != SaveFormatClass.Upgradable)
                    problems.Add($"{s.Where}: {s.Format.Name}={s.Value?.ToString(CultureInfo.InvariantCulture) ?? "missing"} is {c}");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
    }
}
