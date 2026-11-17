using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6 step (f).
    //
    // The canonical tech tree line both reads produce (the game database's TechTree at the main
    // menu, AssetBase.RnDTechTree.GetTreeNodes() in the R&D building) so the two can be
    // compared: "id|cost|anyToUnlock|parent,parent" with parents sorted and the cost as an
    // invariant number. spikes/r0/S6/s6cfg.py builds the same line from a dump.

    internal static class SpikeS6Content
    {
        internal static string TechLine(string id, string cost, bool anyToUnlock, IEnumerable<string> parents)
        {
            var list = new List<string>();
            if (parents != null)
                foreach (string p in parents)
                    if (!string.IsNullOrEmpty(p)) list.Add(p.Trim());
            list.Sort(StringComparer.Ordinal);
            return (id ?? "").Trim() + "|" + NormalizeNumber(cost) + "|" + (anyToUnlock ? "True" : "False")
                + "|" + string.Join(",", list.ToArray());
        }

        /// <summary>"45", "45.0" and " 45 " all read "45"; non-numbers come back trimmed.</summary>
        internal static string NormalizeNumber(string value)
        {
            if (value == null) return "";
            string t = value.Trim();
            double d;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d.ToString("R", CultureInfo.InvariantCulture);
            return t;
        }

        /// <summary>
        /// Tech lines of a parsed TechTree cfg: every RDNode under any TechTree node (the root
        /// itself may be the TechTree's contents), parents from its Parent { parentID } children.
        /// </summary>
        internal static List<string> TechLinesFromCfg(SpikeS6CfgNode root)
        {
            var lines = new List<string>();
            if (root == null) return lines;
            var trees = new List<SpikeS6CfgNode>();
            foreach (var n in root.Nodes) if (n.Name == "TechTree") trees.Add(n);
            if (trees.Count == 0) trees.Add(root);
            foreach (var tree in trees)
                foreach (var rd in tree.Nodes)
                {
                    if (rd.Name != "RDNode") continue;
                    var parents = new List<string>();
                    foreach (var p in rd.Nodes)
                        if (p.Name == "Parent") parents.Add(p.GetValue("parentID"));
                    bool any = string.Equals(rd.GetValue("anyToUnlock"), "True", StringComparison.OrdinalIgnoreCase);
                    lines.Add(TechLine(rd.GetValue("id"), rd.GetValue("cost"), any, parents));
                }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        /// <summary>
        /// Compares two sets of tech lines by node id: ids only on one side and ids whose line
        /// differs. Empty means the two reads are the same tree.
        /// </summary>
        internal static List<string> CompareTech(IEnumerable<string> left, IEnumerable<string> right,
            string leftName, string rightName)
        {
            var l = ById(left);
            var r = ById(right);
            var problems = new List<string>();
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in l.Keys) ids.Add(k);
            foreach (var k in r.Keys) ids.Add(k);
            foreach (string id in ids)
            {
                string lv, rv;
                bool inL = l.TryGetValue(id, out lv);
                bool inR = r.TryGetValue(id, out rv);
                if (!inR) problems.Add("only-" + leftName + " " + lv);
                else if (!inL) problems.Add("only-" + rightName + " " + rv);
                else if (!string.Equals(lv, rv, StringComparison.Ordinal))
                    problems.Add("differs " + leftName + "=" + lv + " " + rightName + "=" + rv);
            }
            return problems;
        }

        private static Dictionary<string, string> ById(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (lines == null) return map;
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                int bar = line.IndexOf('|');
                string id = bar < 0 ? line : line.Substring(0, bar);
                map[id] = line;
            }
            return map;
        }
    }
}
