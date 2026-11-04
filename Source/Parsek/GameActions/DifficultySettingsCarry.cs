using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Parsek's own time travel keeps the player's current difficulty settings (owner ruling
    /// Q1, 2026-11-04, career settings audit section 3.0). Stock's <c>Game</c> loads its
    /// <c>PARAMETERS</c> node on every load, so a Rewind to Launch, Warp to game start, Re-Fly
    /// or Discard Re-fly would otherwise bring back the sliders saved in the rewind save. Each
    /// of those loads captures the live parameters before <c>GamePersistence.LoadGame</c> and
    /// installs them on the loaded game before anything starts it or writes it to
    /// persistent.sfs. A quickload (F9), the Load Game menu and a stock revert restore the
    /// save exactly, as stock does.
    /// </summary>
    internal static class DifficultySettingsCarry
    {
        internal const string Tag = "CareerSettings";

        /// <summary>At most this many changed values are named in the log line.</summary>
        internal const int MaxNamedChanges = 12;

        /// <summary>The live game's parameters as a node, or null when there is no live game.</summary>
        internal static ConfigNode CaptureLive(string label)
        {
            try
            {
                ConfigNode node = CaptureLiveCore();
                if (node == null)
                    ParsekLog.Info(Tag, $"{label}: no live game parameters to keep across the load");
                return node;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"{label}: capturing the live game parameters failed ({ex.GetType().Name}: {ex.Message}); " +
                    "the load keeps the save's own");
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConfigNode CaptureLiveCore()
        {
            var parameters = HighLogic.CurrentGame?.Parameters;
            if (parameters == null)
                return null;
            var node = new ConfigNode("PARAMETERS");
            parameters.Save(node);
            return node;
        }

        /// <summary>
        /// Installs <paramref name="live"/> on <paramref name="game"/> in place of the
        /// parameters the save carried, and logs what that changed. A null node or game is a
        /// logged no-op.
        /// </summary>
        internal static void ApplyTo(Game game, ConfigNode live, string label)
        {
            if (game == null || live == null)
                return;
            try
            {
                ApplyToCore(game, live, label);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"{label}: keeping the live game parameters failed ({ex.GetType().Name}: {ex.Message}); " +
                    "the load keeps the save's own");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyToCore(Game game, ConfigNode live, string label)
        {
            var saved = new ConfigNode("PARAMETERS");
            game.Parameters?.Save(saved);
            List<string> changes = DescribeChanges(saved, live);
            game.Parameters = new GameParameters(live);
            ParsekLog.Info(Tag,
                $"{label}: kept the current difficulty settings across the load " +
                $"(changed={changes.Count}{FormatChanges(changes)})");
        }

        /// <summary>
        /// Pure: every leaf value that differs between the saved and the live parameter node,
        /// as <c>PATH.key: saved -> live</c> (a missing side reads <c>-</c>), in the live node's
        /// order then the saved-only keys. Repeated keys compare by position.
        /// </summary>
        internal static List<string> DescribeChanges(ConfigNode saved, ConfigNode live)
        {
            var changes = new List<string>();
            Walk(saved, live, null, changes);
            return changes;
        }

        private static void Walk(ConfigNode saved, ConfigNode live, string path, List<string> changes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (live != null)
            {
                for (int i = 0; i < live.values.Count; i++)
                {
                    string name = live.values[i].name;
                    if (!seen.Add(name))
                        continue;
                    CompareValues(name, saved?.GetValues(name), live.GetValues(name), path, changes);
                }
            }
            if (saved != null)
            {
                for (int i = 0; i < saved.values.Count; i++)
                {
                    string name = saved.values[i].name;
                    if (!seen.Add(name))
                        continue;
                    CompareValues(name, saved.GetValues(name), null, path, changes);
                }
            }

            var seenNodes = new HashSet<string>(StringComparer.Ordinal);
            if (live != null)
            {
                for (int i = 0; i < live.nodes.Count; i++)
                {
                    string name = live.nodes[i].name;
                    if (seenNodes.Add(name))
                        Walk(saved?.GetNode(name), live.nodes[i], Join(path, name), changes);
                }
            }
            if (saved != null)
            {
                for (int i = 0; i < saved.nodes.Count; i++)
                {
                    string name = saved.nodes[i].name;
                    if (seenNodes.Add(name))
                        Walk(saved.nodes[i], null, Join(path, name), changes);
                }
            }
        }

        private static void CompareValues(
            string name, string[] saved, string[] live, string path, List<string> changes)
        {
            int count = Math.Max(saved?.Length ?? 0, live?.Length ?? 0);
            for (int i = 0; i < count; i++)
            {
                string s = saved != null && i < saved.Length ? saved[i] : null;
                string l = live != null && i < live.Length ? live[i] : null;
                if (string.Equals(s, l, StringComparison.Ordinal))
                    continue;
                changes.Add($"{Join(path, name)}: {s ?? "-"} -> {l ?? "-"}");
            }
        }

        private static string Join(string path, string name)
        {
            return string.IsNullOrEmpty(path) ? name : path + "." + name;
        }

        private static string FormatChanges(List<string> changes)
        {
            if (changes == null || changes.Count == 0)
                return string.Empty;
            var sb = new StringBuilder(": ");
            int shown = Math.Min(changes.Count, MaxNamedChanges);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(changes[i]);
            }
            if (changes.Count > shown)
                sb.Append(", ...");
            return sb.ToString();
        }
    }
}
