using System;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>One committed tree as the tree-handle resolver sees it.</summary>
    internal struct TreeHandleCandidate
    {
        public string TreeId;
        public string DisplayName;

        public TreeHandleCandidate(string treeId, string displayName)
        {
            TreeId = treeId;
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// The <c>tree=</c> handle of the watch-lane verbs (MissionConfig, StartLoopPlayback,
    /// EnterWatchMode): a spec names a mission in ANY save without pinning its id.
    ///
    /// <para>
    /// Vocabulary: <c>first</c> / <c>latest</c> are the first / last committed tree in
    /// <c>RecordingStore.CommittedTrees</c> order (the save's order, which is commit
    /// order); <c>name:&lt;x&gt;</c> matches the tree's display name
    /// (<c>MissionStore.DisplayNameForTree</c>: the original mission's name, else the root
    /// group name, else the launch name), case-insensitively, and must match exactly one
    /// tree. Keywords are case-insensitive. Any other value is not a handle and passes
    /// through unchanged, so every id-pinned spec behaves exactly as before.
    /// </para>
    ///
    /// <para>
    /// The offline twin is <c>harness/lib/watchlane.py</c> (the watch-lane generator
    /// resolves the same handles against a save file);
    /// <c>test_watchlane.TreeHandleSourceSyncTests</c> keeps the keyword and reason
    /// literals byte-equal.
    /// </para>
    /// </summary>
    internal static class TestCommandTreeHandle
    {
        internal const string First = "first";
        internal const string Latest = "latest";
        internal const string NamePrefix = "name:";

        internal const string NoTreesReason = "tree-handle-no-trees";
        internal const string NoMatchReason = "tree-handle-no-match";
        internal const string AmbiguousReason = "tree-handle-ambiguous";

        /// <summary>True when <paramref name="arg"/> is a handle rather than a tree id.</summary>
        internal static bool IsHandle(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return false;
            string v = arg.Trim();
            return string.Equals(v, First, StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, Latest, StringComparison.OrdinalIgnoreCase)
                || v.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The tree id <paramref name="handle"/> names, or null with a refusal reason.
        /// A value that is not a handle is returned unchanged with a null reason (the
        /// verb's own unknown-tree path owns it).
        /// </summary>
        internal static string Resolve(string handle, IList<TreeHandleCandidate> trees,
            out string reason)
        {
            reason = null;
            if (!IsHandle(handle))
                return handle;
            string v = handle.Trim();
            int count = trees != null ? trees.Count : 0;
            if (string.Equals(v, First, StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, Latest, StringComparison.OrdinalIgnoreCase))
            {
                if (count == 0)
                {
                    reason = NoTreesReason;
                    return null;
                }
                bool first = string.Equals(v, First, StringComparison.OrdinalIgnoreCase);
                return trees[first ? 0 : count - 1].TreeId;
            }

            string want = v.Substring(NamePrefix.Length).Trim();
            string match = null;
            int matches = 0;
            for (int i = 0; i < count; i++)
            {
                string name = (trees[i].DisplayName ?? string.Empty).Trim();
                if (!string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
                    continue;
                matches++;
                match = trees[i].TreeId;
            }
            if (matches == 0)
            {
                reason = NoMatchReason;
                return null;
            }
            if (matches > 1)
            {
                reason = AmbiguousReason;
                return null;
            }
            return match;
        }
    }
}
