using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Signature-cached HOST for the pure <see cref="DockEventGraph"/> (design
    /// design-dock-event-graph.md 6.3 "Host cache"). This is the ONLY file in the pair that
    /// touches live state: it supplies the committed tree list and derives the visibility
    /// predicate, so the derivation core stays parameter-injected and headlessly testable.
    ///
    /// <para><b>Rebuild policy (design 2.6).</b> Build at load / refresh, NEVER per frame. Every
    /// call recomputes a cheap topology signature - <see cref="RecordingStore.StateVersion"/> +
    /// <see cref="ParsekScenario.SupersedeStateVersion"/> + per-tree (branch-point count,
    /// recording count) - and returns the cached instance unless it moved. UI draw paths should
    /// additionally memo the returned reference per frame (a signature string is cheap but not
    /// free at hundreds of rows per frame).</para>
    ///
    /// <para><b>ERS routing (design 6.3, and the reason no allowlist entry is needed).</b> The
    /// visibility predicate is derived from <see cref="EffectiveState.ComputeERS"/> - the
    /// sanctioned routed surface - and the tree list comes from
    /// <c>RecordingStore.CommittedTrees</c>, a tree-level surface with no ERS filter defined
    /// over it. NEITHER this file NOR <c>DockEventGraph.cs</c> may ever read the flat
    /// <c>CommittedRecordings</c> list: <c>scripts/grep-audit-ers-els.ps1</c> fails CI on that
    /// token and this design deliberately adds no <c>ers-els-audit-allowlist.txt</c> entry. A
    /// source-text gate test (<c>DockEventGraphTests</c>) pins that. (The token is written
    /// without its leading dot here on purpose - the gate is a literal scan and would flag this
    /// very comment.)</para>
    /// </summary>
    internal static class DockEventGraphCache
    {
        private static readonly object syncRoot = new object();
        private static DockEventGraph cached;

        /// <summary>
        /// Returns the cached graph, rebuilding only when the committed topology signature
        /// moved. Never null.
        /// </summary>
        internal static DockEventGraph GetOrBuild()
        {
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            string signature = ComputeSignature(trees);

            lock (syncRoot)
            {
                if (cached != null
                    && string.Equals(cached.BuildSignature, signature, StringComparison.Ordinal))
                    return cached;
            }

            // Visibility: the ERS id set. A re-fly-superseded merged child leaves its branch
            // point in the tree (topology is tree-level), so the node is BUILT and FLAGGED and
            // consumers skip it, rather than surfacing a phantom story row (design 6.3 step 3).
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            var visibleIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ers.Count; i++)
                if (ers[i] != null && !string.IsNullOrEmpty(ers[i].RecordingId))
                    visibleIds.Add(ers[i].RecordingId);

            DockEventGraph built = DockEventGraph.Build(
                trees, id => visibleIds.Contains(id), signature);

            lock (syncRoot)
            {
                cached = built;
                return cached;
            }
        }

        /// <summary>Drops the cached graph so the next <see cref="GetOrBuild"/> rebuilds.</summary>
        internal static void InvalidateForTesting()
        {
            lock (syncRoot)
                cached = null;
        }

        /// <summary>Full reset hook, mirroring the other static stores' test seams.</summary>
        internal static void ResetForTesting()
        {
            InvalidateForTesting();
            DockEventGraph.SuppressLogging = false;
        }

        // Topology signature: any change that could move a node, a resolution or a visibility
        // flag must move this string. Recording/branch counts catch adds, removes and re-parents
        // within a tree; StateVersion catches commits / deletes / renames; SupersedeStateVersion
        // catches a re-fly supersede that changes only the ERS (the visibility predicate).
        private static string ComputeSignature(List<RecordingTree> trees)
        {
            var ic = CultureInfo.InvariantCulture;
            var scenario = ParsekScenario.Instance;
            // Unity fake-null convention (mirrors EffectiveState.ComputeERS).
            int supersedeVersion = !object.ReferenceEquals(null, scenario)
                ? scenario.SupersedeStateVersion
                : 0;

            var sb = new StringBuilder(64);
            sb.Append(RecordingStore.StateVersion.ToString(ic)).Append('|');
            sb.Append(supersedeVersion.ToString(ic)).Append('|');
            int count = trees?.Count ?? 0;
            sb.Append(count.ToString(ic)).Append(':');
            for (int i = 0; i < count; i++)
            {
                RecordingTree tree = trees[i];
                if (tree == null)
                {
                    sb.Append("~,");
                    continue;
                }
                sb.Append(tree.Id ?? "").Append('/');
                sb.Append((tree.BranchPoints?.Count ?? 0).ToString(ic)).Append('/');
                sb.Append((tree.Recordings?.Count ?? 0).ToString(ic)).Append(',');
            }
            return sb.ToString();
        }
    }
}
