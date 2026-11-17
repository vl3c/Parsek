using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6.
    //
    // A minimal reader of the cfg text KSP writes (ConfigNode.Save), so the save watch can
    // find the race mark in every written .sfs without KSP's own ConfigNode (pure, so the
    // xUnit suite can pin it). spikes/r0/S6/s6cfg.py is the Python twin the checker uses;
    // both normalise the same way.

    /// <summary>One node of a parsed cfg text: its name, its values in order, its children.</summary>
    internal sealed class SpikeS6CfgNode
    {
        internal readonly string Name;
        internal readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
        internal readonly List<SpikeS6CfgNode> Nodes = new List<SpikeS6CfgNode>();

        internal SpikeS6CfgNode(string name) { Name = name ?? ""; }

        internal string GetValue(string key)
        {
            foreach (var kv in Values)
                if (string.Equals(kv.Key, key, StringComparison.Ordinal)) return kv.Value;
            return null;
        }
    }

    internal static class SpikeS6Cfg
    {
        /// <summary>
        /// Parses cfg text into a synthetic root whose children are the top-level nodes.
        /// Rules (KSP's writer): "key = value" splits at the first '='; a bare word names the
        /// next node and "{" opens it ("NAME {" on one line too); "}" closes; "//" starts a
        /// comment. An unbalanced "}" is ignored; nodes left open at the end are closed.
        /// </summary>
        internal static SpikeS6CfgNode Parse(string text)
        {
            var root = new SpikeS6CfgNode("<root>");
            var stack = new Stack<SpikeS6CfgNode>();
            stack.Push(root);
            string pendingName = null;
            if (text == null) return root;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;

                // A line may carry "NAME {", "{", "}", or several braces; walk it.
                int i = 0;
                while (i < line.Length)
                {
                    int brace = line.IndexOfAny(new[] { '{', '}' }, i);
                    string segment = (brace < 0 ? line.Substring(i) : line.Substring(i, brace - i)).Trim();
                    if (segment.Length > 0)
                    {
                        int eq = segment.IndexOf('=');
                        if (eq >= 0)
                        {
                            if (pendingName != null)
                            {
                                // A bare word followed by a value line: the word was a value-less key.
                                stack.Peek().Values.Add(new KeyValuePair<string, string>(pendingName, ""));
                                pendingName = null;
                            }
                            string key = segment.Substring(0, eq).Trim();
                            string value = segment.Substring(eq + 1).Trim();
                            stack.Peek().Values.Add(new KeyValuePair<string, string>(key, value));
                        }
                        else
                        {
                            pendingName = segment;
                        }
                    }
                    if (brace < 0) break;
                    if (line[brace] == '{')
                    {
                        var node = new SpikeS6CfgNode(pendingName ?? "");
                        pendingName = null;
                        stack.Peek().Nodes.Add(node);
                        stack.Push(node);
                    }
                    else if (stack.Count > 1)
                    {
                        stack.Pop();
                    }
                    i = brace + 1;
                }
            }
            return root;
        }

        private static string StripComment(string line)
        {
            if (line == null) return "";
            int c = line.IndexOf("//", StringComparison.Ordinal);
            return c < 0 ? line : line.Substring(0, c);
        }

        /// <summary>
        /// Every SCENARIO node named <paramref name="scenarioName"/> under the GAME node(s) of a
        /// parsed save (a .sfs has one GAME root). Also accepts a root that IS the GAME's
        /// contents (a node saved without its wrapper).
        /// </summary>
        internal static List<SpikeS6CfgNode> FindScenarios(SpikeS6CfgNode root, string scenarioName)
        {
            var found = new List<SpikeS6CfgNode>();
            if (root == null) return found;
            var games = new List<SpikeS6CfgNode>();
            foreach (var n in root.Nodes)
                if (n.Name == "GAME") games.Add(n);
            if (games.Count == 0) games.Add(root);
            foreach (var g in games)
                foreach (var n in g.Nodes)
                    if (n.Name == "SCENARIO" && n.GetValue("name") == scenarioName)
                        found.Add(n);
            return found;
        }

        /// <summary>Names of every SCENARIO node of the save, in file order.</summary>
        internal static List<string> ScenarioNames(SpikeS6CfgNode root)
        {
            var names = new List<string>();
            if (root == null) return names;
            foreach (var n in root.Nodes)
            {
                if (n.Name != "GAME") continue;
                foreach (var s in n.Nodes)
                    if (s.Name == "SCENARIO") names.Add(s.GetValue("name") ?? "");
            }
            return names;
        }

        /// <summary>
        /// Whitespace-normalised value: trimmed, inner runs of spaces and tabs collapsed to
        /// one space, no space next to a comma ("5, 7, 8" and "5,7,8" compare equal).
        /// </summary>
        internal static string NormalizeValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            bool space = false;
            foreach (char c in value.Trim())
            {
                if (c == ' ' || c == '\t')
                {
                    space = true;
                    continue;
                }
                if (space && c != ',' && sb.Length > 0 && sb[sb.Length - 1] != ',') sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The canonical text of a node: "NAME {", one "key = value" line per value (normalised),
        /// the children in order, "}", lines joined by '\n', no indentation. Order is kept:
        /// a reordered node is a different node.
        /// </summary>
        internal static string Canonical(SpikeS6CfgNode node)
        {
            var sb = new StringBuilder();
            AppendCanonical(node, sb);
            return sb.ToString().TrimEnd('\n');
        }

        private static void AppendCanonical(SpikeS6CfgNode node, StringBuilder sb)
        {
            sb.Append(node.Name).Append(" {\n");
            foreach (var kv in node.Values)
                sb.Append(kv.Key.Trim()).Append(" = ").Append(NormalizeValue(kv.Value)).Append('\n');
            foreach (var child in node.Nodes)
                AppendCanonical(child, sb);
            sb.Append("}\n");
        }

        /// <summary>First 16 hex characters of the SHA-256 of the UTF-8 text.</summary>
        internal static string ShortHash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// The facts the save watch logs for one written file: how many marks it holds, the
        /// canonical hash of the first, and whether the probe scenario is present.
        /// </summary>
        internal static MarkFacts ReadMarkFacts(string sfsText, string markName, string probeName)
        {
            var root = Parse(sfsText);
            var marks = FindScenarios(root, markName);
            var probes = string.IsNullOrEmpty(probeName) ? new List<SpikeS6CfgNode>() : FindScenarios(root, probeName);
            string canonical = marks.Count > 0 ? Canonical(marks[0]) : null;
            bool isGame = false;
            foreach (var n in root.Nodes) if (n.Name == "GAME") { isGame = true; break; }
            return new MarkFacts(isGame, marks.Count, canonical, canonical == null ? "-" : ShortHash(canonical),
                probes.Count, ScenarioNames(root).Count);
        }
    }

    internal struct MarkFacts
    {
        internal readonly bool IsGameSave;
        internal readonly int MarkCount;
        internal readonly string Canonical;
        internal readonly string Hash;
        internal readonly int ProbeCount;
        internal readonly int ScenarioCount;

        internal MarkFacts(bool isGameSave, int markCount, string canonical, string hash, int probeCount, int scenarioCount)
        {
            IsGameSave = isGameSave;
            MarkCount = markCount;
            Canonical = canonical;
            Hash = hash;
            ProbeCount = probeCount;
            ScenarioCount = scenarioCount;
        }
    }
}
