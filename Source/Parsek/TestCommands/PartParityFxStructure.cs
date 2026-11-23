using System;
using System.Collections.Generic;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>One FX asset missing or extra on the ghost (design E.4, structure): its key
    /// (canonical asset name and parent) and whether the side that has it draws it.</summary>
    internal sealed class PartParityFxStructureEntry
    {
        internal string Key;
        internal bool Rendered;
    }

    /// <summary>
    /// The deterministic FX structure diff of one case (design-part-ghost-parity.md E.4): the
    /// <c>GhostFxFingerprint.FormatEntry</c> lines of two sides paired by their canonical name and
    /// parent, multiset-wise, so a part with two copies of one flame pairs two. Pure.
    /// </summary>
    internal static class PartParityFxStructure
    {
        /// <summary>The pairing key of one inventory line: <c>name&lt;parent</c>, what precedes its
        /// placement.</summary>
        internal static string KeyOf(string line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            int pos = line.IndexOf(" pos=", StringComparison.Ordinal);
            return pos >= 0 ? line.Substring(0, pos) : line;
        }

        /// <summary>
        /// Splits the two inventories into missing on the ghost (on the real side only), extra on
        /// the ghost, and <c>real -> ghost</c> lines for pairs whose remainder differs.
        /// </summary>
        internal static void Diff(IList<string> real, IList<bool> realRendered, IList<string> ghost, IList<bool> ghostRendered,
            List<PartParityFxStructureEntry> missing, List<PartParityFxStructureEntry> extra, List<string> differs)
        {
            var available = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < ghost.Count; i++)
            {
                string key = KeyOf(ghost[i]);
                if (!available.TryGetValue(key, out List<int> list)) available[key] = list = new List<int>();
                list.Add(i);
            }
            for (int i = 0; i < real.Count; i++)
            {
                string key = KeyOf(real[i]);
                if (!available.TryGetValue(key, out List<int> list) || list.Count == 0)
                {
                    missing.Add(new PartParityFxStructureEntry { Key = key, Rendered = At(realRendered, i) });
                    continue;
                }
                // The first unpaired ghost line that matches exactly, else the first.
                int pick = list[0];
                foreach (int g in list)
                    if (string.Equals(ghost[g], real[i], StringComparison.Ordinal)) { pick = g; break; }
                list.Remove(pick);
                if (!string.Equals(ghost[pick], real[i], StringComparison.Ordinal))
                    differs.Add(real[i] + " -> " + ghost[pick]);
            }
            foreach (var kv in available)
                foreach (int g in kv.Value)
                    extra.Add(new PartParityFxStructureEntry { Key = kv.Key, Rendered = At(ghostRendered, g) });
        }

        private static bool At(IList<bool> v, int i) => v != null && i < v.Count && v[i];

        internal static string Join(IList<PartParityFxStructureEntry> v)
        {
            var parts = new List<string>();
            foreach (PartParityFxStructureEntry e in v) parts.Add(e.Key + (e.Rendered ? string.Empty : "(hidden)"));
            return string.Join(";", parts.ToArray());
        }

        /// <summary>The entries as a JSON array of {key, rendered} objects.</summary>
        internal static string Json(IList<PartParityFxStructureEntry> v)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < v.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"key\":").Append(PartParityJson.Str(v[i].Key)).Append(",\"rendered\":")
                    .Append(PartParityJson.Bool(v[i].Rendered)).Append('}');
            }
            return sb.Append(']').ToString();
        }
    }
}
