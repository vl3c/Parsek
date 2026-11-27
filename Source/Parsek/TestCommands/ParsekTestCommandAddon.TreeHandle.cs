using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Watch-lane partial: resolves a <c>tree=</c> handle (<c>first</c> / <c>latest</c> /
    /// <c>name:&lt;mission&gt;</c>, <see cref="TestCommandTreeHandle"/>) to a committed
    /// tree id before MissionConfig, StartLoopPlayback or EnterWatchMode reads it. A
    /// plain tree id is not a handle and passes through untouched.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        /// <summary>
        /// True with <paramref name="resolved"/> set when the verb may proceed; false
        /// when the handle did not resolve, in which case the exec result is already
        /// REJECTED with the resolver's reason.
        /// </summary>
        private bool TryResolveTreeHandleArg(string verbTag, string treeArg, out string resolved)
        {
            resolved = treeArg;
            if (!TestCommandTreeHandle.IsHandle(treeArg))
                return true;

            IReadOnlyList<RecordingTree> trees = RecordingStore.CommittedTrees;
            // The display name reads the tree's original mission, which the default
            // seeding creates lazily; seed first so a fresh load resolves by name.
            MissionStore.EnsureDefaultsForTrees(trees);
            var candidates = new List<TreeHandleCandidate>(trees != null ? trees.Count : 0);
            if (trees != null)
                for (int i = 0; i < trees.Count; i++)
                    if (trees[i] != null)
                        candidates.Add(new TreeHandleCandidate(
                            trees[i].Id, MissionStore.DisplayNameForTree(trees[i])));

            string id = TestCommandTreeHandle.Resolve(treeArg, candidates, out string reason);
            if (id == null)
            {
                ParsekLog.Warn(Tag,
                    $"{verbTag} rejected reason={reason} tree={treeArg} committedTrees={candidates.Count}");
                SetExecResult("REJECTED", null, reason);
                return false;
            }
            ParsekLog.Info(Tag,
                $"tree handle resolved: verb={verbTag} handle={treeArg} tree={id} committedTrees={candidates.Count}");
            resolved = id;
            return true;
        }
    }
}
