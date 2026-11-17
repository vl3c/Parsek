using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// A cheat, debug or test tool whose use is kept on a recording (design 7.12, addition A3
    /// of 15.11). The numbers are written to the save: never renumber a member, and a new
    /// member takes the next free number (additive, no format bump).
    /// </summary>
    public enum ToolKind
    {
        GravityHack = 1,
        CheatTeleport = 2,
        CheatCurrency = 3,
        CheatOption = 4,
        DebugAction = 5,
        TestTooling = 6,
    }

    /// <summary>
    /// One tool used while a recording was flown, with the game date it was first used.
    /// <see cref="Kind"/> may hold a number this build does not define (written by a newer
    /// build): it is kept and written back unchanged, and a reader that needs a known kind
    /// checks <c>Enum.IsDefined</c>.
    /// </summary>
    public struct ToolUse
    {
        public ToolKind Kind;
        public string Detail;   // null when the tool has no detail
        public double FirstUT;
    }

    /// <summary>
    /// The tool-use list's persistence and its carry through merge and split (the clone is a
    /// list copy in <see cref="Recording.ApplyPersistenceArtifactsFrom"/>, kept there so
    /// <see cref="Recording"/> does not depend on this class). A
    /// recording with no uses keeps <see cref="Recording.ToolUses"/> null and writes no
    /// <c>TOOL_USE</c> child, so its node is byte-identical to a build without this fact.
    /// </summary>
    internal static class ToolUseFacts
    {
        internal const string NodeName = "TOOL_USE";

        /// <summary>Writes one <c>TOOL_USE { kind detail ut }</c> child per use; nothing when none.</summary>
        internal static void SaveInto(ConfigNode recNode, Recording rec)
        {
            if (recNode == null || rec == null || rec.ToolUses == null || rec.ToolUses.Count == 0)
                return;
            var ic = CultureInfo.InvariantCulture;
            int unknownKinds = 0;
            for (int i = 0; i < rec.ToolUses.Count; i++)
            {
                ToolUse use = rec.ToolUses[i];
                ConfigNode useNode = recNode.AddNode(NodeName);
                useNode.AddValue("kind", ((int)use.Kind).ToString(ic));
                if (!string.IsNullOrEmpty(use.Detail))
                    useNode.AddValue("detail", use.Detail);
                useNode.AddValue("ut", use.FirstUT.ToString("R", ic));
                if (!Enum.IsDefined(typeof(ToolKind), use.Kind))
                    unknownKinds++;
            }
            ParsekLog.Verbose("RecordingTree",
                "ToolUse saved: rec=" + (rec.RecordingId ?? "<no-id>")
                + " uses=" + rec.ToolUses.Count.ToString(ic)
                + " unknownKindsKept=" + unknownKinds.ToString(ic));
        }

        /// <summary>
        /// Reads the <c>TOOL_USE</c> children into <see cref="Recording.ToolUses"/> (null when
        /// there are none). A positive kind number this build does not define came from a newer
        /// build: it is kept so the next save writes it back (save-format policy section 4). A
        /// child with no positive integer kind or no finite ut is malformed and skipped.
        /// </summary>
        internal static void LoadFrom(ConfigNode recNode, Recording rec)
        {
            if (recNode == null || rec == null)
                return;
            ConfigNode[] useNodes = recNode.GetNodes(NodeName);
            if (useNodes == null || useNodes.Length == 0)
            {
                rec.ToolUses = null;
                return;
            }

            var ic = CultureInfo.InvariantCulture;
            var uses = new List<ToolUse>(useNodes.Length);
            int unknownKinds = 0;
            int malformed = 0;
            for (int i = 0; i < useNodes.Length; i++)
            {
                ConfigNode useNode = useNodes[i];
                if (!int.TryParse(useNode.GetValue("kind"), NumberStyles.Integer, ic, out int kind)
                    || kind <= 0
                    || !double.TryParse(useNode.GetValue("ut"), NumberStyles.Float, ic, out double ut)
                    || double.IsNaN(ut) || double.IsInfinity(ut))
                {
                    malformed++;
                    continue;
                }
                if (!Enum.IsDefined(typeof(ToolKind), kind))
                    unknownKinds++;
                string detail = useNode.GetValue("detail");
                uses.Add(new ToolUse
                {
                    Kind = (ToolKind)kind,
                    Detail = string.IsNullOrEmpty(detail) ? null : detail,
                    FirstUT = ut,
                });
            }
            rec.ToolUses = uses.Count > 0 ? uses : null;
            ParsekLog.Verbose("RecordingTree",
                "ToolUse loaded: rec=" + (rec.RecordingId ?? "<no-id>")
                + " uses=" + uses.Count.ToString(ic)
                + " unknownKindsKept=" + unknownKinds.ToString(ic)
                + " malformedSkipped=" + malformed.ToString(ic));
        }

        /// <summary>
        /// The union of two lists with one entry per (kind, detail), keeping the least first-use
        /// UT, ordered by UT, then kind, then detail. Null when both are empty. The inputs are
        /// not changed.
        /// </summary>
        internal static List<ToolUse> Union(List<ToolUse> first, List<ToolUse> second)
        {
            int firstCount = first != null ? first.Count : 0;
            int secondCount = second != null ? second.Count : 0;
            if (firstCount + secondCount == 0)
                return null;

            var byKey = new Dictionary<string, ToolUse>(StringComparer.Ordinal);
            var order = new List<string>();
            for (int pass = 0; pass < 2; pass++)
            {
                List<ToolUse> list = pass == 0 ? first : second;
                if (list == null)
                    continue;
                for (int i = 0; i < list.Count; i++)
                {
                    ToolUse use = list[i];
                    string key = ((int)use.Kind).ToString(CultureInfo.InvariantCulture)
                        + "|" + (use.Detail ?? "");
                    if (byKey.TryGetValue(key, out ToolUse existing))
                    {
                        if (use.FirstUT < existing.FirstUT)
                            byKey[key] = use;
                        continue;
                    }
                    byKey[key] = use;
                    order.Add(key);
                }
            }

            var result = new List<ToolUse>(order.Count);
            for (int i = 0; i < order.Count; i++)
                result.Add(byKey[order[i]]);
            result.Sort((a, b) =>
            {
                int c = a.FirstUT.CompareTo(b.FirstUT);
                if (c != 0) return c;
                c = ((int)a.Kind).CompareTo((int)b.Kind);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Detail ?? "", b.Detail ?? "");
            });
            return result;
        }

        /// <summary>
        /// Splits <paramref name="first"/>'s uses at <paramref name="splitUT"/>: a use first made
        /// before it stays on <paramref name="first"/> (so the first half also keeps uses from
        /// before its start), every other use moves to <paramref name="second"/> (so the second
        /// half also keeps uses after its end). An empty half holds null.
        /// </summary>
        internal static void PartitionAtUT(Recording first, Recording second, double splitUT)
        {
            if (first == null || second == null)
                return;
            List<ToolUse> uses = first.ToolUses;
            if (uses == null || uses.Count == 0)
            {
                first.ToolUses = null;
                second.ToolUses = null;
                return;
            }

            var head = new List<ToolUse>();
            var tail = new List<ToolUse>();
            for (int i = 0; i < uses.Count; i++)
            {
                if (uses[i].FirstUT < splitUT)
                    head.Add(uses[i]);
                else
                    tail.Add(uses[i]);
            }
            first.ToolUses = head.Count > 0 ? head : null;
            second.ToolUses = tail.Count > 0 ? tail : null;

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Optimizer",
                "ToolUse split: rec=" + (first.RecordingId ?? "<no-id>")
                + " splitUT=" + splitUT.ToString("R", ic)
                + " head=" + head.Count.ToString(ic)
                + " tail=" + tail.Count.ToString(ic));
        }
    }
}
