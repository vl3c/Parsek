using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Parsek.Tests.SaveRoundTrip;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The state the frozen format golden <c>Fixtures/FormatGolden/v1/</c> was written from, and
    /// the one-shot writer that produced it. The writer runs the production save writers in
    /// <c>ParsekScenario.OnSave</c>'s phase order over generator-built state (the real OnSave
    /// needs a live game, so each phase is called directly) and writes a KSP-shaped save folder.
    /// It is SKIPPED unless <c>PARSEK_WRITE_FORMAT_GOLDEN=v1</c>, so a normal run never rewrites
    /// the committed files; the golden is frozen once written (see FormatGolden/README.md).
    /// </summary>
    [Collection("Sequential")]
    public class FormatGoldenV1GeneratorTests : IDisposable
    {
        // ---------------------------------------------------------------- identities

        internal const double SaveUT = 1200.0;

        internal const string LanderTreeId = "a10e0000000000000000000000000001";
        internal const string LanderTreeName = "Golden Lander";
        internal const string RootId = "a10e0000000000000000000000000011";
        internal const string UpperId = "a10e0000000000000000000000000012";
        internal const string DebrisId = "a10e0000000000000000000000000013";
        internal const string ReflyId = "a10e0000000000000000000000000014";
        internal const string SeparationBpId = "a10e00000000000000000000000000b1";

        internal const string RoverTreeId = "b20f0000000000000000000000000002";
        internal const string RoverTreeName = "Golden Rover";
        internal const string RoverId = "b20f0000000000000000000000000021";

        internal const string RewindPointId = "rp_c30a0000000000000000000000000031";
        internal const string SupersedeId = "rsr_c30a0000000000000000000000000032";
        internal const string TombstoneId = "tomb_c30a0000000000000000000000000033";
        internal const string TombstonedActionId = "act_c30a0000000000000000000000000034";
        internal const string ContractGuid = "d40b0000-0000-0000-0000-000000000041";

        internal const string LanderMissionName = "Golden Mun Mission";
        internal const string RoverDecisionName = "Golden Rover Mk2";
        internal const string RouteName0 = "Golden Fuel Run";
        internal const string RouteName1 = "Golden Ore Run";
        internal const string DormantRouteName = "Golden Dormant Run";

        internal const string Pilot = "Jebediah Kerman";
        internal const string StandIn = "Jedlong Kerman";
        internal const string Engineer = "Bill Kerman";

        internal const uint LanderPid = 3100000u;
        internal const uint RoverPid = 3200000u;

        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes originalScene;
        private readonly string staging;

        public FormatGoldenV1GeneratorTests()
        {
            originalScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            ResetAllStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            staging = Path.Combine(Path.GetTempPath(), "parsek-format-golden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            ResetAllStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            HighLogic.LoadedScene = originalScene;
            try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        internal static void ResetAllStores()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            MissionStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            RemovedReplayFlags.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            RewindVesselHold.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            CareerSettingsLock.ResetForTesting();
            StockPoolReference.ResetForTesting();
            RouteStore.ResetForTesting();
            RouteEventLog.ResetForTesting();
            RetiredRouteCargoStore.ResetForTesting();
            RecalculationEngine.ClearModules();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            SaveFormat.ResetForTesting();
        }

        /// <summary>Set PARSEK_WRITE_FORMAT_GOLDEN=v1 to write the v1 golden (once; it is frozen).</summary>
        internal sealed class WriteFormatGoldenV1FactAttribute : FactAttribute
        {
            public WriteFormatGoldenV1FactAttribute()
            {
                if (Environment.GetEnvironmentVariable(FormatGolden.WriteEnvVar) != "v1")
                    Skip = "set " + FormatGolden.WriteEnvVar + "=v1 to write Fixtures/FormatGolden/v1 "
                           + "(frozen once written; a format bump adds a new vN folder instead)";
            }
        }

        // ---------------------------------------------------------------- the writer

        [Trait("Category", "Manual")]
        [WriteFormatGoldenV1Fact]
        public void WriteV1()
        {
            string repo = SyntheticRecordingTests.ResolveProjectRoot();
            RecordingPaths.SaveRootOverrideForTesting = staging;

            ParsekScenario scenario = BuildState();
            ConfigNode parsek = RunSaveWriters(scenario);

            // The KSP save around the PARSEK node: a stock career save (no Parsek footprint).
            string basePath = Path.Combine(repo, "harness", "fixtures", "saves", "fresh-career", "persistent.sfs");
            ConfigNode baseRoot = ConfigNode.Load(basePath);
            Assert.NotNull(baseRoot);
            ConfigNode game = baseRoot.GetNode("GAME");
            Assert.NotNull(game);
            Assert.Null(game.GetNodes("SCENARIO").FirstOrDefault(s => s.GetValue("name") == "ParsekScenario"));
            game.AddNode(parsek);
            string sfs = Path.Combine(staging, "persistent.sfs");
            baseRoot.Save(sfs);

            // The rewind point's quicksave: a KSP save of the same world, as RewindPointAuthor
            // copies it under Parsek/RewindPoints/.
            string rpDir = Path.Combine(staging, "Parsek", "RewindPoints");
            Directory.CreateDirectory(rpDir);
            File.Copy(sfs, Path.Combine(rpDir, RewindPointId + ".sfs"));

            File.WriteAllText(Path.Combine(staging, FormatGolden.ManifestFileName),
                FormatGolden.BuildManifestText(SaveFormat.All));

            // Move into the committed tree.
            string target = FormatGolden.VersionDir(1);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            FormatGolden.CopyTree(staging, target);
            string careerSource = Path.Combine(repo, "Source", "Parsek.Tests", "Fixtures", "C2CareerPostFix");
            FormatGolden.CopyTree(careerSource, Path.Combine(target, FormatGolden.CareerFolderName));

            // Checksums: this folder's lines are rewritten, every other folder's kept verbatim.
            string root = FormatGolden.Root();
            string checksumsPath = Path.Combine(root, FormatGolden.ChecksumsFileName);
            var map = File.Exists(checksumsPath)
                ? FormatGolden.ReadChecksums(checksumsPath)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in map.Keys.Where(k => k.StartsWith("v1/", StringComparison.Ordinal)).ToList())
                map.Remove(key);
            foreach (string path in FormatGolden.FrozenFiles(target))
                map[FormatGolden.RelativeKey(root, path)] = FormatGolden.Sha256(path);
            File.WriteAllText(checksumsPath, FormatGolden.BuildChecksumsText(map));
        }

        // ---------------------------------------------------------------- the save writers

        /// <summary>
        /// The PARSEK scenario node, written by the production writers in OnSave's phase order
        /// (recordings, missions, vessel names, game state, Re-Fly state, routes, stock pool
        /// reference, held window, rewind vessel holds, replay scope, save stamp, save format).
        /// Sidecars and the game-state files land under <see cref="RecordingPaths.SaveRootOverrideForTesting"/>.
        /// </summary>
        internal static ConfigNode RunSaveWriters(ParsekScenario scenario)
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", "ParsekScenario");
            node.AddValue("scene", "7, 5, 8, 6");

            ParsekScenario.SaveTreeRecordings(node);
            MissionStore.Save(node);
            VesselNameStore.Save(node);
            PersistGameStateAndMilestones(node);
            InvokeInstance(scenario, "SaveRewindStagingState", node);
            RouteStore.SaveRoutesTo(node);
            StockPoolReference.SaveTo(node);
            InvokeStatic("SaveLedgerHeldWindow", node);
            RewindVesselHold.SaveInto(node);
            PlaybackScopeTracker.SaveInto(node);
            RemovedReplayFlags.SaveInto(node);
            ParsekScenario.WriteSaveStamp(node);
            SaveFormat.StampScenarioNode(node);
            return node;
        }

        /// <summary>
        /// <c>ParsekScenario.PersistGameStateAndMilestones</c> call for call, minus its milestone
        /// flush (which reads <c>Planetarium.GetUniversalTime()</c>, unavailable headless; the
        /// state builder flushed at <see cref="SaveUT"/> instead).
        /// </summary>
        private static void PersistGameStateAndMilestones(ConfigNode node)
        {
            LedgerOrchestrator.Kerbals?.SaveSlots(node);
            CrewReservationManager.SaveCrewReplacements(node);
            GroupHierarchyStore.SaveInto(node);

            var eventsBuild = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);
            GameStateStore.WriteEventsNode(eventsBuild);
            node.AddValue("gameStateEventCount", GameStateStore.EventCount);

            var baselinesNode = new ConfigNode(ParsekScenario.BaselinesNodeName);
            foreach (var baseline in GameStateStore.Baselines)
            {
                ConfigNode baselineNode = GameStateStore.BuildBaselineNode(baseline);
                if (baselineNode == null)
                    continue;
                GameStateStore.WriteBaselineNode(baseline, baselineNode);
                baselinesNode.AddNode(baselineNode);
            }

            ConfigNode milestonesNode = MilestoneStore.BuildMilestonesNode();
            MilestoneStore.WriteMilestonesNode(milestonesNode);
            MilestoneStore.SaveMutableState(node);

            ConfigNode ledgerNode = Ledger.BuildSaveNode();
            LedgerOrchestrator.OnSave(ledgerNode);

            node.RemoveNodes(ParsekScenario.GameStateNodeName);
            node.AddNode(ParsekScenario.BuildGameStateNode(
                ledgerNode, eventsBuild.Root, milestonesNode, baselinesNode,
                GameStateRecorder.BuildPendingScienceNode(GameStateRecorder.PendingScienceSubjects),
                eventsBuild.AttemptEvents));
        }

        private static void InvokeStatic(string method, ConfigNode node)
        {
            MethodInfo mi = typeof(ParsekScenario).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            mi.Invoke(null, new object[] { node });
        }

        internal static void InvokeInstance(ParsekScenario scenario, string method, ConfigNode node)
        {
            MethodInfo mi = typeof(ParsekScenario).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            mi.Invoke(scenario, new object[] { node });
        }

        private static void SetPrivate(object target, string field, object value)
        {
            FieldInfo fi = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(fi);
            fi.SetValue(target, value);
        }

        // ---------------------------------------------------------------- the state

        /// <summary>
        /// Populates every store the scenario save writes: two committed trees (a lander with a
        /// separation, parent-anchored debris and a Re-Fly fork; a looping rover), missions,
        /// a vessel name decision with a link and a dock-absorb row, groups, kerbal slots, crew
        /// replacements, game-state events / contract snapshot / baseline / milestone, the
        /// ledger, the Re-Fly bookkeeping, routes with every route event type, and the small
        /// stores (filled field by field). Returns the scenario instance holding the Re-Fly state.
        /// </summary>
        internal static ParsekScenario BuildState()
        {
            LedgerOrchestrator.Initialize();

            RecordingTree lander = BuildLanderTree();
            RecordingTree rover = BuildRoverTree();
            TreeCommitService.CommitTree(lander);
            TreeCommitService.CommitTree(rover);

            // Missions: the default per tree, the lander's renamed, plus a clone with a selection.
            MissionStore.EnsureDefaultsForTrees(new[] { lander, rover });
            Mission landerMission = MissionStore.Missions.First(m => m.TreeId == LanderTreeId);
            MissionStore.RenameMission(landerMission, LanderMissionName);
            Mission clone = MissionStore.Clone(landerMission);
            clone.Name = LanderMissionName + " (no debris)";
            clone.ExcludedIntervalKeys.Add(DebrisId + "@160");
            Mission roverMission = MissionStore.Missions.First(m => m.TreeId == RoverTreeId);
            roverMission.LoopPlayback = true;
            roverMission.LoopIntervalSeconds = 600.0;
            roverMission.LoopAnchorUT = 500.0;

            // Vessel names: a player rename of the rover, a spawn link, a dock-absorb row.
            Assert.True(VesselNameStore.RecordParsekRename(
                rover.Recordings[RoverId], RoverDecisionName, new[] { lander, rover }, out string refusal),
                "rover rename refused: " + refusal);
            Assert.True(VesselNameStore.AddLink(
                "e60c0000000000000000000000000061", ScenarioWriter.DeriveVesselLaunchGuid(RoverId),
                VesselIdentityLinkKind.Spawn, "format golden"));
            Assert.True(VesselNameStore.RecordDockAbsorb(
                7100u, ScenarioWriter.DeriveVesselLaunchGuid(UpperId), "format golden"));

            // Groups: the rover's group nested under an ops folder, the debris group hidden.
            GroupHierarchyStore.SetGroupParent(RoverTreeName, LanderTreeName);
            GroupHierarchyStore.AddHiddenGroup(LanderTreeName + " / Debris");

            // Kerbal slots and crew replacements (loaded through the production readers).
            var crewSeed = new ConfigNode("SEED");
            ConfigNode slots = crewSeed.AddNode("KERBAL_SLOTS");
            ConfigNode slot = slots.AddNode("SLOT");
            slot.AddValue("owner", Pilot);
            slot.AddValue("trait", "Pilot");
            slot.AddNode("CHAIN_ENTRY").AddValue("name", StandIn);
            ConfigNode slot2 = slots.AddNode("SLOT");
            slot2.AddValue("owner", Engineer);
            slot2.AddValue("trait", "Engineer");
            slot2.AddValue("permanentlyGone", "True");
            ConfigNode repl = crewSeed.AddNode("CREW_REPLACEMENTS").AddNode("ENTRY");
            repl.AddValue("original", Pilot);
            repl.AddValue("replacement", StandIn);
            ConfigNode rescued = crewSeed.AddNode(CrewReservationManager.RescuePlacedNodeName).AddNode("ENTRY");
            rescued.AddValue("name", "Bob Kerman");
            rescued.AddValue("vesselPid", "3300000");
            LedgerOrchestrator.Kerbals.LoadSlots(crewSeed);
            CrewReservationManager.LoadCrewReplacements(crewSeed);

            BuildGameState();
            BuildLedger();
            BuildRoutes();
            FillSmallStores();

            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { BuildRewindPoint() },
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = SupersedeId,
                        OldRecordingId = UpperId,
                        NewRecordingId = ReflyId,
                        UT = 160.0,
                        CreatedRealTime = "2026-11-04T10:00:00.0000000Z",
                    },
                },
                // No 1.0 writer produces a rewind retirement (rows predate 2026-10-25).
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>
                {
                    new LedgerTombstone
                    {
                        TombstoneId = TombstoneId,
                        ActionId = TombstonedActionId,
                        RetiringRecordingId = ReflyId,
                        UT = 330.0,
                        CreatedRealTime = "2026-11-04T10:00:01.0000000Z",
                    },
                },
            };
            BuildSessionMarkers(scenario);
            return scenario;
        }

        private static TrajectoryPoint Pt(double ut, double lat, double lon, double alt, string body = "Kerbin",
            float velY = 0f, float rotZ = 0f, float rotW = 1f, double funds = 0, float science = 0f, float rep = 0f)
        {
            return new TrajectoryPoint
            {
                ut = ut, latitude = lat, longitude = lon, altitude = alt, bodyName = body,
                rotation = new Quaternion(0f, 0f, rotZ, rotW), velocity = new Vector3(0f, velY, 0f),
                funds = funds, science = science, reputation = rep,
            };
        }

        private static OrbitSegment Orbit(double startUT, double endUT, double sma, double ecc, double inc,
            string body = "Kerbin")
        {
            return new OrbitSegment
            {
                startUT = startUT, endUT = endUT, semiMajorAxis = sma, eccentricity = ecc, inclination = inc,
                longitudeOfAscendingNode = 92.0, argumentOfPeriapsis = 47.0, meanAnomalyAtEpoch = 0.18,
                epoch = startUT, bodyName = body,
            };
        }

        private static RecordingTree BuildLanderTree()
        {
            string landerGuid = ScenarioWriter.DeriveVesselLaunchGuid(RootId);
            TrajectoryPoint p0 = Pt(100, -0.0972, -74.5575, 77, velY: 6f, funds: 25000);
            TrajectoryPoint p1 = Pt(120, -0.0963, -74.5550, 3200, velY: 240f, rotZ: 0.10f, rotW: 0.99f, funds: 25000);
            TrajectoryPoint p2 = Pt(140, -0.0905, -74.5450, 22000, velY: 610f, rotZ: 0.31f, rotW: 0.95f, funds: 25000);
            TrajectoryPoint p3 = Pt(160, -0.0860, -74.5380, 71000, velY: 1900f, rotZ: 0.38f, rotW: 0.92f,
                funds: 25800, science: 2.5f);
            var root = new RecordingBuilder(LanderTreeName)
                .WithRecordingId(RootId)
                .WithRecordedVesselGuid(landerGuid)
                .WithVesselPersistentId(LanderPid)
                .WithLaunchIdentity("LaunchPad", "Prelaunch")
                .AddPoint(p0.ut, p0.latitude, p0.longitude, p0.altitude, funds: p0.funds)
                .AddPoint(p1.ut, p1.latitude, p1.longitude, p1.altitude, rotZ: 0.10f, rotW: 0.99f, funds: p1.funds)
                .AddPoint(p2.ut, p2.latitude, p2.longitude, p2.altitude, rotZ: 0.31f, rotW: 0.95f, funds: p2.funds)
                .AddPoint(p3.ut, p3.latitude, p3.longitude, p3.altitude, rotZ: 0.38f, rotW: 0.92f,
                    funds: p3.funds, science: 2.5f)
                .AddPartEvent(101, LanderPid + 2, (int)PartEventType.EngineIgnited, "liquidEngine", value: 1f)
                .AddPartEvent(130, LanderPid + 2, (int)PartEventType.EngineThrottle, "liquidEngine", value: 0.6f)
                .AddPartEvent(160, LanderPid + 1, (int)PartEventType.Decoupled, "Decoupler.1")
                .AddFlagEvent(100, "Golden Pad Flag", Pilot, "format golden", "Squad/Flags/default",
                    -0.0972, -74.5575, 77.0, body: "Kerbin")
                .AddSegmentEvent(SegmentEventType.ControllerChange, 120, "gravity turn")
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric, referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active, startUT = 100, endUT = 140, sampleRateHz = 10f,
                    minAltitude = 77f, maxAltitude = 22000f,
                    frames = new List<TrajectoryPoint> { p0, p1, p2 }, checkpoints = new List<OrbitSegment>(),
                })
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.ExoPropulsive, referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active, startUT = 140, endUT = 160, sampleRateHz = 10f,
                    boundaryDiscontinuityMeters = 0.25f, minAltitude = 22000f, maxAltitude = 71000f,
                    frames = new List<TrajectoryPoint> { p2, p3 }, checkpoints = new List<OrbitSegment>(),
                })
                .WithVesselSnapshot(VesselSnapshotBuilder.CrewedShip(LanderTreeName, Pilot, LanderPid)
                    .WithLaunchGuid(landerGuid).AsFlyingAtSurface(-0.0860, -74.5380, 71000))
                .WithGhostVisualSnapshot(VesselSnapshotBuilder.FleaRocket(LanderTreeName, Pilot, LanderPid)
                    .AsLanded(-0.0972, -74.5575, 77.0))
                .WithRecordingGroup(LanderTreeName);

            OrbitSegment parking = Orbit(170, 400, 705000, 0.003, 28.5);
            TrajectoryPoint u0 = Pt(160, -0.0860, -74.5380, 71000, velY: 1900f, funds: 25800);
            TrajectoryPoint u1 = Pt(170, -0.0700, -74.0000, 76000, velY: 2250f, funds: 25800);
            var upper = new RecordingBuilder(LanderTreeName)
                .WithRecordingId(UpperId)
                .WithRecordedVesselGuid(landerGuid)
                .WithVesselPersistentId(LanderPid)
                .WithParentRecordingId(RootId)
                .AddPoint(u0.ut, u0.latitude, u0.longitude, u0.altitude, funds: u0.funds)
                .AddPoint(u1.ut, u1.latitude, u1.longitude, u1.altitude, funds: u1.funds)
                .AddOrbitSegment(170, 400, inc: 28.5, ecc: 0.003, sma: 705000, lan: 92, argPe: 47, mna: 0.18,
                    epoch: 170, ofrX: 0.1f, ofrY: 0.2f, ofrZ: 0.3f, ofrW: 0.927f)
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic, referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active, startUT = 160, endUT = 170, sampleRateHz = 10f,
                    minAltitude = 71000f, maxAltitude = 76000f,
                    frames = new List<TrajectoryPoint> { u0, u1 }, checkpoints = new List<OrbitSegment>(),
                })
                .AddOrbitalCheckpointSection(170, 400, parking)
                .AddPartEvent(165, LanderPid + 3, (int)PartEventType.DeployableExtended, "solarPanels4")
                .WithTerminalState((int)TerminalState.Orbiting)
                .WithTerminalOrbit("Kerbin", 705000, 0.003, 28.5, 92, 47, 0.18, 170)
                .WithVesselSnapshot(VesselSnapshotBuilder.CrewedShip(LanderTreeName, Pilot, LanderPid)
                    .WithLaunchGuid(landerGuid).AsOrbiting(705000, 0.003, 28.5, 92, 47, 0.18, 170))
                .WithRecordingGroup(LanderTreeName);

            // Parent-anchored debris: a Relative section with both authored surfaces.
            var d0 = Pt(160, -0.0860, -74.5380, 71000, velY: 1890f);
            var d1 = Pt(170, -0.0850, -74.5200, 72500, velY: 1700f);
            var debris = new RecordingBuilder(LanderTreeName + " Debris")
                .WithRecordingId(DebrisId)
                .WithRecordedVesselGuid(ScenarioWriter.DeriveVesselLaunchGuid(DebrisId))
                .WithParentRecordingId(RootId)
                .AsDebris()
                .WithParentAnchorRecordingId(RootId)
                .AddPoint(d0.ut, d0.latitude, d0.longitude, d0.altitude)
                .AddPoint(d1.ut, d1.latitude, d1.longitude, d1.altitude)
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic, referenceFrame = ReferenceFrame.Relative,
                    source = TrackSectionSource.Background, startUT = 160, endUT = 170, sampleRateHz = 5f,
                    anchorVesselId = LanderPid, anchorRecordingId = RootId,
                    minAltitude = 71000f, maxAltitude = 72500f,
                    frames = new List<TrajectoryPoint>
                    {
                        Pt(160, 0.0, -1.5, 0.2), Pt(170, 2.0, -40.0, 3.0),
                    },
                    bodyFixedFrames = new List<TrajectoryPoint> { d0, d1 },
                    checkpoints = new List<OrbitSegment>(),
                })
                .WithTerminalState((int)TerminalState.Destroyed)
                .WithRecordingGroup(LanderTreeName + " / Debris");

            // The Re-Fly fork of the upper stage (superseding it), committed provisional.
            var r0 = Pt(160, -0.0860, -74.5380, 71000, velY: 1900f, funds: 25800);
            var r1 = Pt(175, -0.0600, -73.9000, 78000, velY: 2260f, funds: 25800);
            var refly = new RecordingBuilder(LanderTreeName)
                .WithRecordingId(ReflyId)
                .WithRecordedVesselGuid(landerGuid)
                .WithVesselPersistentId(LanderPid)
                .WithParentRecordingId(RootId)
                .AddPoint(r0.ut, r0.latitude, r0.longitude, r0.altitude, funds: r0.funds)
                .AddPoint(r1.ut, r1.latitude, r1.longitude, r1.altitude, funds: r1.funds)
                .AddOrbitSegment(175, 420, inc: 28.6, ecc: 0.002, sma: 710000, lan: 92, argPe: 47, mna: 0.18,
                    epoch: 175)
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.ExoPropulsive, referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active, startUT = 160, endUT = 175, sampleRateHz = 10f,
                    minAltitude = 71000f, maxAltitude = 78000f,
                    frames = new List<TrajectoryPoint> { r0, r1 }, checkpoints = new List<OrbitSegment>(),
                })
                .AddOrbitalCheckpointSection(175, 420, Orbit(175, 420, 710000, 0.002, 28.6))
                .WithTerminalState((int)TerminalState.Orbiting)
                .WithTerminalOrbit("Kerbin", 710000, 0.002, 28.6, 92, 47, 0.18, 175)
                .WithMergeState(MergeState.CommittedProvisional)
                .WithVesselSnapshot(VesselSnapshotBuilder.CrewedShip(LanderTreeName, Pilot, LanderPid)
                    .WithLaunchGuid(landerGuid).AsOrbiting(710000, 0.002, 28.6, 92, 47, 0.18, 175))
                .WithRecordingGroup(LanderTreeName);

            var builders = new[] { root, upper, debris, refly };
            BranchPoint sep = ScenarioWriter.SeparationBranch(
                SeparationBpId, RootId, new[] { UpperId, DebrisId, ReflyId }, 160.0);
            sep.DecouplerPartId = LanderPid + 1;
            sep.RewindPointId = RewindPointId;
            RecordingTree tree = ScenarioWriter.MaterializeTree(builders, null, new[] { sep });
            tree.Id = LanderTreeId;
            tree.TreeName = LanderTreeName;
            FinishTree(tree, builders);

            Recording rootRec = tree.Recordings[RootId];
            rootRec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            tree.Recordings[UpperId].CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                [Pilot] = KerbalEndState.Aboard,
            };
            return tree;
        }

        private static RecordingTree BuildRoverTree()
        {
            string guid = ScenarioWriter.DeriveVesselLaunchGuid(RoverId);
            var s0 = Pt(500, -0.0972, -74.5575, 70);
            var s1 = Pt(530, -0.0965, -74.5560, 71);
            var s2 = Pt(560, -0.0958, -74.5545, 72);
            var rover = new RecordingBuilder(RoverTreeName)
                .WithRecordingId(RoverId)
                .WithRecordedVesselGuid(guid)
                .WithVesselPersistentId(RoverPid)
                .WithLaunchIdentity("LaunchPad", "Prelaunch")
                .AddPoint(s0.ut, s0.latitude, s0.longitude, s0.altitude)
                .AddPoint(s1.ut, s1.latitude, s1.longitude, s1.altitude)
                .AddPoint(s2.ut, s2.latitude, s2.longitude, s2.altitude)
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.SurfaceMobile, referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active, startUT = 500, endUT = 560, sampleRateHz = 2f,
                    minAltitude = 70f, maxAltitude = 72f,
                    frames = new List<TrajectoryPoint> { s0, s1, s2 }, checkpoints = new List<OrbitSegment>(),
                })
                .AddPartEvent(505, RoverPid + 1, (int)PartEventType.DeployableExtended, "longAntenna")
                .WithLoopPlayback(true, 600.0)
                .WithTerminalState((int)TerminalState.Landed)
                .WithTerrainHeightAtEnd(68.5)
                .WithVesselSnapshot(VesselSnapshotBuilder.ProbeShip(RoverTreeName, RoverPid)
                    .WithLaunchGuid(guid).AsLanded(-0.0958, -74.5545, 72))
                .WithRecordingGroup(RoverTreeName);
            var builders = new[] { rover };
            RecordingTree tree = ScenarioWriter.MaterializeTree(builders);
            tree.Id = RoverTreeId;
            tree.TreeName = RoverTreeName;
            FinishTree(tree, builders);
            return tree;
        }

        /// <summary>Hydrates each recording's trajectory from its builder and re-stamps the tree id.</summary>
        private static void FinishTree(RecordingTree tree, RecordingBuilder[] builders)
        {
            foreach (RecordingBuilder b in builders)
            {
                Recording rec = tree.Recordings[b.GetRecordingId()];
                rec.TreeId = tree.Id;
                TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(b.BuildTrajectoryNode(), rec);
                rec.MarkFilesDirty();
            }
        }

        private static RewindPoint BuildRewindPoint()
        {
            return new RewindPoint
            {
                RewindPointId = RewindPointId,
                BranchPointId = SeparationBpId,
                UT = 160.0,
                QuicksaveFilename = RewindPointId + ".sfs",
                SessionProvisional = false,
                Corrupted = false,
                CreatedRealTime = "2026-11-04T09:59:00.0000000Z",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = UpperId, Controllable = true },
                    new ChildSlot
                    {
                        SlotIndex = 1, OriginChildRecordingId = DebrisId, Controllable = false,
                        Disabled = true, DisabledReason = "uncontrollable debris",
                    },
                },
                PidSlotMap = new Dictionary<uint, int> { { LanderPid, 0 }, { LanderPid + 50, 1 } },
                RootPartPidMap = new Dictionary<uint, int> { { LanderPid + 10, 0 }, { LanderPid + 60, 1 } },
            };
        }

        /// <summary>The live-session singletons, filled field by field and pointed at the golden's ids.</summary>
        private static void BuildSessionMarkers(ParsekScenario scenario)
        {
            var rt = new FieldRoundTrip(ScenarioRecordSaveRoundTripTests.Specs(), seed: 61000);
            ReFlySessionMarker marker = rt.Build<ReFlySessionMarker>();
            marker.SessionId = "sess_e70d0000000000000000000000000071";
            marker.TreeId = LanderTreeId;
            marker.ActiveReFlyRecordingId = ReflyId;
            marker.OriginChildRecordingId = UpperId;
            marker.SupersedeTargetId = UpperId;
            marker.RewindPointId = RewindPointId;
            marker.InvokedUT = 160.0;
            scenario.ActiveReFlySessionMarker = marker;

            MergeJournal journal = rt.Build<MergeJournal>();
            journal.JournalId = "mj_e70d0000000000000000000000000072";
            journal.SessionId = marker.SessionId;
            journal.TreeId = LanderTreeId;
            scenario.ActiveMergeJournal = journal;

            SetPrivate(scenario, "activeStockActionIntent", rt.Build<StockActionIntentMarker>());
            SetPrivate(scenario, "activeSwitchSegmentSession", rt.Build<SwitchSegmentSession>());
        }

        private static void BuildGameState()
        {
            var events = new[]
            {
                new GameStateEvent
                {
                    ut = 50.0, eventType = GameStateEventType.ContractAccepted, key = ContractGuid,
                    detail = "title=Golden Orbit;deadline=99999", recordingId = "",
                },
                new GameStateEvent
                {
                    ut = 60.0, eventType = GameStateEventType.TechResearched, key = "basicRocketry",
                    detail = "cost=5", valueBefore = 5, valueAfter = 0, recordingId = "",
                },
                new GameStateEvent
                {
                    ut = 70.0, eventType = GameStateEventType.FacilityUpgraded,
                    key = "SpaceCenter/LaunchPad", detail = "level=1", valueBefore = 0, valueAfter = 0.5,
                    recordingId = "",
                },
                new GameStateEvent
                {
                    ut = 80.0, eventType = GameStateEventType.CrewHired, key = Engineer,
                    detail = "trait=Engineer", recordingId = "",
                },
                new GameStateEvent
                {
                    ut = 165.0, eventType = GameStateEventType.MilestoneAchieved, key = "FirstLaunch",
                    detail = "funds=800;rep=1;sci=0", recordingId = RootId,
                },
                new GameStateEvent
                {
                    ut = 400.0, eventType = GameStateEventType.ContractCompleted, key = ContractGuid,
                    detail = "funds=6000;rep=5;sci=2", recordingId = UpperId,
                },
            };
            for (int i = 0; i < events.Length; i++)
            {
                GameStateEvent e = events[i];
                GameStateStore.AddEvent(ref e);
            }

            var contract = new ConfigNode("CONTRACT");
            contract.AddValue("guid", ContractGuid);
            contract.AddValue("type", "AchieveOrbit");
            contract.AddValue("state", "Active");
            contract.AddValue("funds_reward", "6000");
            GameStateStore.AddContractSnapshot(ContractGuid, contract, 50.0);

            var rt = new FieldRoundTrip(ScenarioRecordSaveRoundTripTests.Specs(), seed: 62000);
            GameStateBaseline baseline = rt.Build<GameStateBaseline>();
            baseline.ut = 9.0;
            baseline.funds = 25000.0;
            baseline.science = 5.0;
            baseline.reputation = 0f;
            baseline.scienceKnown = true;
            baseline.reputationKnown = true;
            baseline.researchedTechIds = new List<string> { "start" };
            baseline.facilityLevels = new Dictionary<string, float>
            {
                ["SpaceCenter/LaunchPad"] = 0f, ["SpaceCenter/VehicleAssemblyBuilding"] = 0f,
            };
            baseline.buildingIntact = new Dictionary<string, bool> { ["SpaceCenter/LaunchPad/Facility"] = true };
            baseline.crewEntries = new List<GameStateBaseline.CrewEntry>
            {
                new GameStateBaseline.CrewEntry { name = Pilot, status = "Available", trait = "Pilot" },
                new GameStateBaseline.CrewEntry { name = Engineer, status = "Available", trait = "Engineer" },
            };
            var active = new ConfigNode("CONTRACT");
            active.AddValue("guid", ContractGuid);
            active.AddValue("state", "Active");
            baseline.activeContracts = new List<ConfigNode> { active };
            GameStateStore.AddBaseline(baseline);

            GameStateStore.RecordOriginalScience("crewReport@KerbinSrfLandedLaunchPad", 5f);
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "mysteryGoo@KerbinFlyingLow", science = 3f, subjectMaxValue = 13f,
                captureUT = 130.0, reasonKey = "ScienceTransmission", recordingId = RootId,
                scienceGainMultiplier = 1f,
            });

            // The milestone OnSave's flush would cut at the save clock (headless the clock reads 0).
            MilestoneStore.FlushPendingEvents(SaveUT);
        }

        private static GameAction Row(double ut, GameActionType type, string recordingId = null)
        {
            return new GameAction
            {
                ActionId = "act_f80e" + ((int)(ut * 10)).ToString("D8", CultureInfo.InvariantCulture)
                    + ((int)type).ToString("D4", CultureInfo.InvariantCulture) + "0000000000000000",
                UT = ut, Type = type, RecordingId = recordingId,
            };
        }

        private static void BuildLedger()
        {
            GameAction a;
            a = Row(0, GameActionType.FundsInitial); a.InitialFunds = 25000f; Ledger.AddAction(a);
            a = Row(0, GameActionType.ScienceInitial); a.InitialScience = 5f; Ledger.AddAction(a);
            a = Row(0, GameActionType.ReputationInitial); a.InitialReputation = 0f; Ledger.AddAction(a);
            a = Row(50, GameActionType.ContractAccept); a.ContractId = ContractGuid; a.ContractTitle = "Golden Orbit";
            a.ContractType = "AchieveOrbit"; a.AdvanceFunds = 1000f; a.FundsReward = 6000f; a.RepReward = 5f;
            a.ScienceReward = 2f; a.DeadlineUT = 99999f; Ledger.AddAction(a);
            a = Row(60, GameActionType.ScienceSpending); a.NodeId = "basicRocketry"; a.Cost = 5f; Ledger.AddAction(a);
            a = Row(70, GameActionType.FacilityUpgrade); a.FacilityId = "SpaceCenter/LaunchPad"; a.ToLevel = 2;
            a.FacilityCost = 2000f; Ledger.AddAction(a);
            a = Row(80, GameActionType.KerbalHire); a.KerbalName = Engineer; a.HireCost = 1500f; Ledger.AddAction(a);
            a = Row(99, GameActionType.FundsSpending, RootId); a.FundsSpent = 8000f;
            a.FundsSpendingSource = FundsSpendingSource.VesselBuild; a.DedupKey = "VesselBuild:" + RootId;
            Ledger.AddAction(a);
            a = Row(100, GameActionType.KerbalAssignment, RootId); a.KerbalName = Pilot; a.KerbalRole = "Pilot";
            a.StartUT = 100f; a.EndUT = 400f; a.KerbalEndStateField = KerbalEndState.Aboard; Ledger.AddAction(a);
            a = Row(130, GameActionType.ScienceEarning, RootId); a.SubjectId = "mysteryGoo@KerbinFlyingLow";
            a.ExperimentId = "mysteryGoo"; a.Body = "Kerbin"; a.Situation = "FlyingLow"; a.ScienceAwarded = 3f;
            a.Method = ScienceMethod.Transmitted; a.SubjectMaxValue = 13f; a.TransmitScalar = 1f; Ledger.AddAction(a);
            a = Row(165, GameActionType.MilestoneAchievement, RootId); a.MilestoneId = "FirstLaunch";
            a.MilestoneFundsAwarded = 800f; a.MilestoneRepAwarded = 1f; Ledger.AddAction(a);
            a = Row(330, GameActionType.ReputationPenalty, UpperId); a.ActionId = TombstonedActionId;
            a.NominalPenalty = 2f; a.RepPenaltySource = ReputationPenaltySource.KerbalDeath; Ledger.AddAction(a);
            a = Row(400, GameActionType.ContractComplete, UpperId); a.ContractId = ContractGuid;
            a.FundsReward = 6000f; a.RepReward = 5f; a.ScienceReward = 2f; Ledger.AddAction(a);
            a = Row(560, GameActionType.FundsEarning, RoverId); a.FundsAwarded = 3000f;
            a.FundsSource = FundsEarningSource.Recovery; a.DedupKey = "VesselRecovery:" + RoverId;
            Ledger.AddAction(a);
            a = Row(600, GameActionType.ReputationEarning, RoverId); a.NominalRep = 3f;
            a.RepSource = ReputationSource.Other; Ledger.AddAction(a);
            Assert.True(Ledger.TrySetProgressSeed(PreLedgerProgressSeed.CreateCaptured(
                new[] { "Kerbin/Landing" }, new[] { new KeyValuePair<string, int>("RecordsAltitude", 2) })));
        }

        private static void BuildRoutes()
        {
            var rt = new FieldRoundTrip(RouteSaveRoundTripTests.Specs(), seed: 63000);
            var committed = StaticFieldValue<List<Route>>(typeof(RouteStore), "committedRoutes");
            var dormant = StaticFieldValue<List<Route>>(typeof(RouteStore), "dormantRoutes");
            Route r0 = rt.Build<Route>();
            r0.Name = RouteName0;
            Route r1 = rt.Build<Route>();
            r1.Name = RouteName1;
            Route d = rt.Build<Route>();
            d.Name = DormantRouteName;
            committed.Add(r0);
            committed.Add(r1);
            dormant.Add(d);
            StaticFieldValue<HashSet<string>>(typeof(RouteStore), "dismissedCandidateTreeIds").Add(LanderTreeId);
            StaticFieldValue<HashSet<string>>(typeof(RouteStore), "promptedCandidateTreeIds").Add(RoverTreeId);
            RouteStore.StateFloorUT = 450.25;

            foreach (RouteEventType type in Enum.GetValues(typeof(RouteEventType)))
            {
                var e = rt.Build<RouteEvent>();
                e.Type = type;
                RouteEventLog.Add(e);
            }
            var cargoRt = new FieldRoundTrip(RetiredRouteCargoSaveRoundTripTests.Specs(), seed: 64000);
            RetiredRouteCargoSaveRoundTripTests.Populate(cargoRt, RouteEventType.RouteCargoDelivered);
        }

        private static T StaticFieldValue<T>(Type type, string name)
        {
            FieldInfo f = StaticFieldRule.StateFields(type).Single(x => FieldRoundTrip.LogicalName(x) == name);
            return (T)f.GetValue(null);
        }

        /// <summary>
        /// The small stores (rewind vessel holds, held window, replay scope, stock-shadow
        /// take-back, career settings lock, stock pool reference), every field filled with a
        /// distinct value by the save round-trip harness, under that suite's field contracts.
        /// </summary>
        private static void FillSmallStores()
        {
            var wanted = new[]
            {
                "RewindVesselHold", "LedgerHeldWindow", "PlaybackScopeTracker",
                "StockShadowTakeBack", "CareerSettingsLock", "StockPoolReference",
                "RemovedReplayFlags",
            };
            int seed = 65000;
            foreach (string name in wanted)
            {
                StoreSaveRoundTripTests.StoreCase c = StoreSaveRoundTripTests.Stores.Single(s => s.Name == name);
                FieldSpec spec = c.Spec();
                var rt = new FieldRoundTrip(new[] { spec }.Concat(c.NestedSpecs()), seed);
                StaticRoundTrip.Fill(c.Store, rt, spec);
                seed += 1000;
            }
        }
    }
}
