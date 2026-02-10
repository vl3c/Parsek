using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Parsek.Tests.Generators
{
    public class ScenarioWriter
    {
        private readonly List<ConfigNode> recordings = new List<ConfigNode>();
        private readonly List<(string original, string replacement)> crewReplacements
            = new List<(string, string)>();

        public ScenarioWriter AddRecording(ConfigNode recNode)
        {
            recordings.Add(recNode);
            return this;
        }

        public ScenarioWriter AddRecording(RecordingBuilder builder)
        {
            recordings.Add(builder.Build());
            return this;
        }

        public ScenarioWriter AddCrewReplacement(string original, string replacement)
        {
            crewReplacements.Add((original, replacement));
            return this;
        }

        public ConfigNode BuildScenarioNode()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", "ParsekScenario");
            node.AddValue("scene", "5, 6, 7, 8");

            foreach (var rec in recordings)
                node.AddNode(rec);

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
