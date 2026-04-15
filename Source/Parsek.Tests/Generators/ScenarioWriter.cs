using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Parsek.Tests.Generators
{
    public class ScenarioWriter
    {
        private readonly List<RecordingBuilder> v3Builders = new List<RecordingBuilder>();
        private readonly List<ConfigNode> trees = new List<ConfigNode>();
        private readonly List<(string original, string replacement)> crewReplacements
            = new List<(string, string)>();
        private readonly List<Milestone> milestones = new List<Milestone>();
        private readonly List<GameStateEvent> gameStateEvents = new List<GameStateEvent>();
        private readonly List<ConfigNode> rawMilestoneStates = new List<ConfigNode>();
        private readonly List<(string child, string parent)> groupHierarchyEntries
            = new List<(string, string)>();
        private uint milestoneEpoch;
        private bool useV3Format;

        /// <summary>
        /// Wraps a single RecordingBuilder into a RECORDING_TREE node and adds it
        /// to the scenario. Each recording becomes a single-recording tree.
        /// Sidecar files (.prec, _vessel.craft, _ghost.craft) are also registered
        /// for writing when WithV3Format is active.
        /// </summary>
        public ScenarioWriter AddRecordingAsTree(RecordingBuilder builder)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            return AddRecordingsAsTree(new[] { builder });
        }

        /// <summary>
        /// Wraps multiple related RecordingBuilders into a single RECORDING_TREE node.
        /// This is used for linked chain fixtures so ParentRecordingId references stay
        /// within the same tree when injected into synthetic saves.
        /// </summary>
        public ScenarioWriter AddRecordingsAsTree(IEnumerable<RecordingBuilder> builders)
        {
            if (builders == null)
                throw new ArgumentNullException(nameof(builders));

            var builderList = new List<RecordingBuilder>();
            foreach (var builder in builders)
            {
                if (builder == null)
                    throw new ArgumentException("Recording tree builders cannot contain null entries.", nameof(builders));
                builderList.Add(builder);
            }

            if (builderList.Count == 0)
                throw new ArgumentException("Recording tree requires at least one builder.", nameof(builders));

            var recordings = new List<Recording>(builderList.Count);
            Recording root = null;

            for (int i = 0; i < builderList.Count; i++)
            {
                var rec = BuildRecording(builderList[i]);
                recordings.Add(rec);
                if (root == null && string.IsNullOrEmpty(rec.ParentRecordingId))
                    root = rec;
            }

            if (root == null)
                root = recordings[0];

            var tree = new RecordingTree
            {
                Id = "tree-" + root.RecordingId,
                TreeName = root.VesselName,
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = null
            };

            for (int i = 0; i < recordings.Count; i++)
            {
                recordings[i].TreeId = tree.Id;
                tree.Recordings[recordings[i].RecordingId] = recordings[i];
            }

            AddSerializedTree(tree);
            RegisterV3Builders(builderList);
            return this;
        }

        /// <summary>
        /// Adds a pre-built RECORDING_TREE ConfigNode to be emitted in the scenario.
        /// The node should be built via RecordingTree.Save().
        /// </summary>
        public ScenarioWriter AddTree(ConfigNode treeNode)
        {
            trees.Add(treeNode);
            return this;
        }

        public ScenarioWriter WithV3Format(bool v3 = true)
        {
            useV3Format = v3;
            return this;
        }

        public ScenarioWriter AddCrewReplacement(string original, string replacement)
        {
            crewReplacements.Add((original, replacement));
            return this;
        }

        public ScenarioWriter WithMilestoneEpoch(uint epoch)
        {
            milestoneEpoch = epoch;
            return this;
        }

        public ScenarioWriter AddRawMilestoneState(ConfigNode stateNode)
        {
            rawMilestoneStates.Add(stateNode);
            return this;
        }

        public ScenarioWriter AddGroupHierarchyEntry(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent))
                return this;
            groupHierarchyEntries.Add((child, parent));
            return this;
        }

        internal ScenarioWriter AddMilestone(Milestone milestone)
        {
            milestones.Add(milestone);
            return this;
        }

        internal ScenarioWriter AddGameStateEvent(GameStateEvent e)
        {
            gameStateEvents.Add(e);
            return this;
        }

        public ConfigNode BuildScenarioNode()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", "ParsekScenario");
            node.AddValue("scene", "5, 6, 7, 8");

            foreach (var tree in trees)
                node.AddNode("RECORDING_TREE", tree);

            if (crewReplacements.Count > 0)
            {
                var crNode = node.AddNode("CREW_REPLACEMENTS");
                foreach (var (original, replacement) in crewReplacements)
                {
                    var entry = crNode.AddNode("ENTRY");
                    entry.AddValue("original", original);
                    entry.AddValue("replacement", replacement);
                }
            }

            if (milestones.Count > 0)
            {
                var ic = CultureInfo.InvariantCulture;
                node.AddValue("milestoneEpoch", milestoneEpoch.ToString(ic));

                foreach (var m in milestones)
                {
                    var stateNode = node.AddNode("MILESTONE_STATE");
                    stateNode.AddValue("id", m.MilestoneId ?? "");
                    stateNode.AddValue("lastReplayedIdx",
                        m.LastReplayedEventIndex.ToString(ic));
                }
            }

            foreach (var rawMs in rawMilestoneStates)
                node.AddNode("MILESTONE_STATE", rawMs);

            if (groupHierarchyEntries.Count > 0)
            {
                var hierarchyNode = node.AddNode("GROUP_HIERARCHY");
                foreach (var (child, parent) in groupHierarchyEntries)
                {
                    var entry = hierarchyNode.AddNode("ENTRY");
                    entry.AddValue("child", child);
                    entry.AddValue("parent", parent);
                }
            }

            return node;
        }

        public string SerializeConfigNode(ConfigNode node, string nodeName, int baseIndent = 1)
        {
            var sb = new StringBuilder();
            WriteNode(sb, node, nodeName, baseIndent);
            return sb.ToString();
        }

        private void WriteNode(StringBuilder sb, ConfigNode node, string nodeName, int indent)
        {
            string tabs = new string('\t', indent);
            sb.AppendLine($"{tabs}{nodeName}");
            sb.AppendLine($"{tabs}{{");

            string innerTabs = new string('\t', indent + 1);

            // Values first
            foreach (ConfigNode.Value val in node.values)
                sb.AppendLine($"{innerTabs}{val.name} = {val.value}");

            // Then child nodes
            foreach (ConfigNode child in node.nodes)
                WriteNode(sb, child, child.name, indent + 1);

            sb.AppendLine($"{tabs}}}");
        }

        public string InjectIntoSave(string saveContent)
        {
            var scenarioNode = BuildScenarioNode();
            string serialized = SerializeConfigNode(scenarioNode, "SCENARIO", 1);

            // Remove existing ParsekScenario block if present
            saveContent = RemoveExistingScenario(saveContent);

            // Find FLIGHTSTATE and insert before it
            int flightstateIdx = saveContent.IndexOf("\tFLIGHTSTATE", StringComparison.Ordinal);
            if (flightstateIdx < 0)
                throw new InvalidOperationException("Could not find FLIGHTSTATE in save file");

            return saveContent.Insert(flightstateIdx, serialized);
        }

        public void InjectIntoSaveFile(string inputPath, string outputPath)
        {
            string content = File.ReadAllText(inputPath);
            string modified = InjectIntoSave(content);
            File.WriteAllText(outputPath, modified);

            // If v3 format, write sidecar files alongside the save
            if (useV3Format)
            {
                string saveDir = Path.GetDirectoryName(outputPath);
                WriteSidecarFiles(saveDir);
            }

            // Write milestone and event sidecar files
            if (milestones.Count > 0 || gameStateEvents.Count > 0)
            {
                string saveDir = Path.GetDirectoryName(outputPath);
                WriteGameStateFiles(saveDir);
            }

            // Write rewind save files for v3 recordings that have rewind saves
            if (useV3Format)
            {
                string saveDir = Path.GetDirectoryName(outputPath);
                WriteRewindSaveFiles(saveDir, inputPath);
            }
        }

        /// <summary>
        /// Writes recording sidecar files for all v3 recordings to the
        /// Parsek/Recordings/ subdirectory relative to the given save directory.
        /// Uses RecordingStore's test-facing save path so generated corpora
        /// stay aligned with live sidecar behavior.
        /// </summary>
        public void WriteSidecarFiles(string saveDir)
        {
            string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
            if (!Directory.Exists(recordingsDir))
                Directory.CreateDirectory(recordingsDir);

            foreach (var builder in v3Builders)
            {
                string id = builder.GetRecordingId();

                // Write .prec trajectory file
                var sourceTrajNode = builder.BuildTrajectoryNode();
                var recording = new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = builder.GetFormatVersion(),
                    VesselName = builder.GetVesselName(),
                    VesselSnapshot = builder.GetVesselSnapshot()?.CreateCopy(),
                    GhostVisualSnapshot = builder.GetGhostVisualSnapshot()?.CreateCopy(),
                    FilesDirty = true,
                };
                recording.GhostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(recording);
                RecordingStore.DeserializeTrajectoryFrom(sourceTrajNode, recording);
                string precPath = Path.Combine(recordingsDir, $"{id}.prec");
                string vesselPath = Path.Combine(recordingsDir, $"{id}_vessel.craft");
                string ghostPath = Path.Combine(recordingsDir, $"{id}_ghost.craft");
                if (!RecordingStore.SaveRecordingFilesToPathsForTesting(
                        recording, precPath, vesselPath, ghostPath, incrementEpoch: false))
                    throw new InvalidOperationException($"Failed to write sidecar files for {id}");
            }
        }

        /// <summary>
        /// Writes milestones.pgsm and events.pgse sidecar files to the
        /// Parsek/GameState/ subdirectory relative to the save directory.
        /// </summary>
        public void WriteGameStateFiles(string saveDir)
        {
            string gameStateDir = Path.Combine(saveDir, "Parsek", "GameState");
            if (!Directory.Exists(gameStateDir))
                Directory.CreateDirectory(gameStateDir);

            if (milestones.Count > 0)
            {
                var rootNode = new ConfigNode("PARSEK_MILESTONES");
                rootNode.AddValue("version", "1");
                foreach (var m in milestones)
                {
                    ConfigNode milestoneNode = rootNode.AddNode("MILESTONE");
                    m.SerializeInto(milestoneNode);
                }
                rootNode.Save(Path.Combine(gameStateDir, "milestones.pgsm"));
            }

            if (gameStateEvents.Count > 0)
            {
                var rootNode = new ConfigNode("PARSEK_GAME_STATE");
                rootNode.AddValue("version", "1");
                foreach (var e in gameStateEvents)
                {
                    ConfigNode eventNode = rootNode.AddNode("GAME_STATE_EVENT");
                    e.SerializeInto(eventNode);
                }
                rootNode.Save(Path.Combine(gameStateDir, "events.pgse"));
            }
        }

        /// <summary>
        /// Copies the source save as each v3 recording's rewind quicksave .sfs file
        /// in the Parsek/Saves/ subdirectory.
        /// </summary>
        public void WriteRewindSaveFiles(string saveDir, string sourceSavePath)
        {
            if (!File.Exists(sourceSavePath)) return;

            foreach (var builder in v3Builders)
            {
                string rewindName = builder.GetRewindSaveFileName();
                if (string.IsNullOrEmpty(rewindName)) continue;

                string savesDir = Path.Combine(saveDir, "Parsek", "Saves");
                if (!Directory.Exists(savesDir))
                    Directory.CreateDirectory(savesDir);

                string destPath = Path.Combine(savesDir, rewindName + ".sfs");
                File.Copy(sourceSavePath, destPath, true);
            }
        }

        private static string RemoveExistingScenario(string content)
        {
            // Brace-counting removal: find SCENARIO blocks containing
            // name = ParsekScenario, then track brace depth to find the
            // matching close brace regardless of internal formatting.
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var result = new List<string>(lines.Length);
            int i = 0;

            while (i < lines.Length)
            {
                string trimmed = lines[i].TrimStart('\t', ' ');

                // Look for a SCENARIO opening line at indent level 1
                if (trimmed == "SCENARIO" && i + 2 < lines.Length)
                {
                    string nextTrimmed = lines[i + 1].TrimStart('\t', ' ');
                    // Check if next line is open brace and the line after is name = ParsekScenario
                    if (nextTrimmed == "{")
                    {
                        string valueLine = lines[i + 2].TrimStart('\t', ' ');
                        if (valueLine.StartsWith("name = ParsekScenario", StringComparison.Ordinal))
                        {
                            // Found it — skip lines until matching close brace
                            i++; // skip "SCENARIO"
                            int depth = 0;
                            while (i < lines.Length)
                            {
                                string lt = lines[i].TrimStart('\t', ' ');
                                if (lt.StartsWith("{")) depth++;
                                if (lt.StartsWith("}")) depth--;
                                i++;
                                if (depth == 0) break;
                            }
                            continue;
                        }
                    }
                }

                result.Add(lines[i]);
                i++;
            }

            // Preserve original line ending style
            string sep = content.Contains("\r\n") ? "\r\n" : "\n";
            return string.Join(sep, result);
        }

        /// <summary>
        /// Generates a stable uint from a string via FNV-1a hash.
        /// Used to derive VesselPersistentId from recordingId for synthetic trees.
        /// </summary>
        private static uint StableHashToUint(string input)
        {
            uint hash = 2166136261;
            for (int i = 0; i < input.Length; i++)
            {
                hash ^= input[i];
                hash *= 16777619;
            }
            // Avoid 0 (which means "not set" for VesselPersistentId)
            return hash == 0 ? 1u : hash;
        }

        private void AddSerializedTree(RecordingTree tree)
        {
            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);
            trees.Add(treeNode);
        }

        private void RegisterV3Builders(IEnumerable<RecordingBuilder> builders)
        {
            if (!useV3Format)
                return;

            foreach (var builder in builders)
                v3Builders.Add(builder);
        }

        private static Recording BuildRecording(RecordingBuilder builder)
        {
            string recordingId = builder.GetRecordingId();

            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = builder.GetVesselName(),
                RecordingFormatVersion = builder.GetFormatVersion(),
                VesselPersistentId = StableHashToUint(recordingId),
                ExplicitStartUT = builder.GetStartUT(),
                ExplicitEndUT = builder.GetEndUT(),
                LoopPlayback = builder.GetLoopPlayback(),
                LoopIntervalSeconds = builder.GetLoopIntervalSeconds(),
                PlaybackEnabled = builder.GetPlaybackEnabled(),
                VesselSnapshot = builder.GetVesselSnapshot()?.CreateCopy(),
                GhostVisualSnapshot = builder.GetGhostVisualSnapshot()?.CreateCopy(),
            };
            rec.GhostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(rec);

            int? ts = builder.GetTerminalState();
            if (ts.HasValue)
                rec.TerminalStateValue = (TerminalState)ts.Value;

            double terrainH = builder.GetTerrainHeightAtEnd();
            if (!double.IsNaN(terrainH))
                rec.TerrainHeightAtEnd = terrainH;

            var groups = builder.GetRecordingGroups();
            if (groups != null && groups.Count > 0)
                rec.RecordingGroups = new List<string>(groups);

            if (!string.IsNullOrEmpty(builder.GetSegmentPhase()))
                rec.SegmentPhase = builder.GetSegmentPhase();
            if (!string.IsNullOrEmpty(builder.GetSegmentBodyName()))
                rec.SegmentBodyName = builder.GetSegmentBodyName();

            if (!string.IsNullOrEmpty(builder.GetChainId()))
                rec.ChainId = builder.GetChainId();
            if (builder.GetChainIndex() >= 0)
                rec.ChainIndex = builder.GetChainIndex();
            if (builder.GetChainBranch() > 0)
                rec.ChainBranch = builder.GetChainBranch();

            if (!string.IsNullOrEmpty(builder.GetParentRecordingId()))
                rec.ParentRecordingId = builder.GetParentRecordingId();
            if (!string.IsNullOrEmpty(builder.GetEvaCrewName()))
                rec.EvaCrewName = builder.GetEvaCrewName();

            if (!string.IsNullOrEmpty(builder.GetRewindSaveFileName()))
            {
                rec.RewindSaveFileName = builder.GetRewindSaveFileName();
                rec.RewindReservedFunds = builder.GetRewindReservedFunds();
                rec.RewindReservedScience = builder.GetRewindReservedScience();
                rec.RewindReservedRep = builder.GetRewindReservedRep();
            }

            var controllers = builder.GetControllers();
            if (controllers != null)
                rec.Controllers = new List<ControllerInfo>(controllers);
            rec.IsDebris = builder.GetIsDebris();

            return rec;
        }
    }
}
