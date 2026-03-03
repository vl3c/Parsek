using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Parsek.Tests.Generators
{
    public class ScenarioWriter
    {
        private readonly List<ConfigNode> recordings = new List<ConfigNode>();
        private readonly List<RecordingBuilder> v3Builders = new List<RecordingBuilder>();
        private readonly List<ConfigNode> trees = new List<ConfigNode>();
        private readonly List<(string original, string replacement)> crewReplacements
            = new List<(string, string)>();
        private readonly List<Milestone> milestones = new List<Milestone>();
        private readonly List<GameStateEvent> gameStateEvents = new List<GameStateEvent>();
        private uint milestoneEpoch;
        private bool useV3Format;

        public ScenarioWriter AddRecording(ConfigNode recNode)
        {
            recordings.Add(recNode);
            return this;
        }

        public ScenarioWriter AddRecording(RecordingBuilder builder)
        {
            if (useV3Format)
            {
                recordings.Add(builder.BuildV3Metadata());
                v3Builders.Add(builder);
            }
            else
            {
                recordings.Add(builder.Build());
            }
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

            foreach (var rec in recordings)
                node.AddNode(rec);

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
        }

        /// <summary>
        /// Writes .prec, _vessel.craft, and _ghost.craft sidecar files
        /// for all v3 recordings to the Parsek/Recordings/ subdirectory
        /// relative to the given save directory.
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
                var trajNode = builder.BuildTrajectoryNode();
                trajNode.Save(Path.Combine(recordingsDir, $"{id}.prec"));

                // Write _vessel.craft
                var vesselSnapshot = builder.GetVesselSnapshot();
                if (vesselSnapshot != null)
                    vesselSnapshot.Save(Path.Combine(recordingsDir, $"{id}_vessel.craft"));

                // Write _ghost.craft
                var ghostSnapshot = builder.GetGhostVisualSnapshot();
                if (ghostSnapshot != null)
                    ghostSnapshot.Save(Path.Combine(recordingsDir, $"{id}_ghost.craft"));
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
    }
}
