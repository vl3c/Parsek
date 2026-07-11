using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV7 tree topology (design doc "The invariant rules", edge cases 10-14).
    //
    // Runs over the RAW model (never the ERS/ELS visibility-filtered set - a
    // superseded recording that ERS drops is still a row whose links this rule
    // must resolve). Three families:
    //
    //   1. Dangling links (FAIL). Every non-null ParentRecordingId,
    //      TrackSection.anchorRecordingId, ParentAnchorRecordingId,
    //      Recording.SupersedeTargetId, and tombstone RetiringRecordingId must
    //      resolve to a recording present in the model.
    //   2. Cycles (FAIL). The parent-edge graph and the supersede (Old->New)
    //      graph must be acyclic; the walk terminates via a per-walk visited set
    //      so a corrupt A.parent=B / B.parent=A pair cannot loop forever.
    //   3. Chain contiguity (FAIL / INFO). ChainIndex runs are contiguous PER
    //      (ChainId, ChainBranch). A missing index is FAIL unless the chain
    //      carries a supersede boundary (a HEAD/TIP re-fly split), in which case
    //      it is an expected INFO. The exemption reuses the EXACT predicate the
    //      optimizer's re-merge guard uses (EffectiveState.IsSupersededByRelation,
    //      the load-bearing defense in RecordingOptimizer.CanAutoMerge), so INV7
    //      and the optimizer agree on what a supersede boundary is.
    internal sealed class Inv7TreeTopology : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV7-TREE-TOPOLOGY";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "Recording.ChainId/ChainIndex/ChainBranch / RecordingOptimizer.CanAutoMerge / EffectiveState.IsSupersededByRelation";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            var idSet = new HashSet<string>(System.StringComparer.Ordinal);
            var byId = new Dictionary<string, Recording>(System.StringComparer.Ordinal);
            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                idSet.Add(rec.RecordingId);
                byId[rec.RecordingId] = rec;
            }

            CheckDanglingLinks(model, idSet, findings);
            CheckParentCycles(model, idSet, byId, findings);
            CheckSupersedeCycles(model, findings);
            CheckChainContiguity(model, findings);

            return findings;
        }

        // --- Family 1: dangling links ---

        private static void CheckDanglingLinks(
            AnalyzerModel model, HashSet<string> idSet, List<Finding> findings)
        {
            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;

                MaybeDangling(rec.RecordingId, -1, "ParentRecordingId", rec.ParentRecordingId, idSet, findings);
                MaybeDangling(rec.RecordingId, -1, "ParentAnchorRecordingId", rec.ParentAnchorRecordingId, idSet, findings);
                MaybeDangling(rec.RecordingId, -1, "SupersedeTargetId", rec.SupersedeTargetId, idSet, findings);

                if (rec.TrackSections != null)
                {
                    for (int i = 0; i < rec.TrackSections.Count; i++)
                    {
                        MaybeDangling(rec.RecordingId, i, "anchorRecordingId",
                            rec.TrackSections[i].anchorRecordingId, idSet, findings);
                    }
                }
            }

            if (model.Tombstones != null)
            {
                foreach (LedgerTombstone t in model.Tombstones)
                {
                    if (t == null || string.IsNullOrEmpty(t.RetiringRecordingId))
                        continue;
                    if (!idSet.Contains(t.RetiringRecordingId))
                    {
                        string target = t.TombstoneId ?? "<tombstone>";
                        findings.Add(new Finding(
                            RuleIdConst,
                            VerdictLevel.Fail,
                            target,
                            -1,
                            Inv("INV7 badlink recording={0} field=RetiringRecordingId target={1} kind=dangling",
                                target, t.RetiringRecordingId),
                            "EffectiveState.IsSupersededByRelation"));
                    }
                }
            }
        }

        private static void MaybeDangling(
            string recId, int sectionIndex, string field, string value,
            HashSet<string> idSet, List<Finding> findings)
        {
            if (string.IsNullOrEmpty(value))
                return;
            if (idSet.Contains(value))
                return;
            findings.Add(new Finding(
                RuleIdConst,
                VerdictLevel.Fail,
                recId,
                sectionIndex,
                Inv("INV7 badlink recording={0} field={1} target={2} kind=dangling", recId, field, value),
                "Recording.ChainId/ChainIndex/ChainBranch"));
        }

        // --- Family 2a: parent-graph cycles ---

        private static void CheckParentCycles(
            AnalyzerModel model, HashSet<string> idSet,
            Dictionary<string, Recording> byId, List<Finding> findings)
        {
            var reported = new HashSet<string>(System.StringComparer.Ordinal);

            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (reported.Contains(rec.RecordingId))
                    continue;

                var path = new HashSet<string>(System.StringComparer.Ordinal);
                string cur = rec.RecordingId;
                while (cur != null && idSet.Contains(cur))
                {
                    if (path.Contains(cur))
                    {
                        // cur closes a loop -> cycle. Report once and mark the
                        // whole visited path so other start nodes skip it.
                        findings.Add(new Finding(
                            RuleIdConst,
                            VerdictLevel.Fail,
                            cur,
                            -1,
                            Inv("INV7 badlink recording={0} field=ParentRecordingId target={0} kind=cycle", cur),
                            "Recording.ChainId/ChainIndex/ChainBranch"));
                        foreach (string p in path)
                            reported.Add(p);
                        reported.Add(cur);
                        break;
                    }
                    path.Add(cur);
                    byId.TryGetValue(cur, out Recording node);
                    cur = node?.ParentRecordingId;
                }
            }
        }

        // --- Family 2b: supersede-graph cycles (Old -> New) ---

        private static void CheckSupersedeCycles(AnalyzerModel model, List<Finding> findings)
        {
            if (model.SupersedeRelations == null || model.SupersedeRelations.Count == 0)
                return;

            var next = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (RecordingSupersedeRelation rel in model.SupersedeRelations)
            {
                if (rel == null || string.IsNullOrEmpty(rel.OldRecordingId) || string.IsNullOrEmpty(rel.NewRecordingId))
                    continue;
                if (!next.ContainsKey(rel.OldRecordingId))
                    next[rel.OldRecordingId] = rel.NewRecordingId;
            }

            var reported = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string start in next.Keys)
            {
                if (reported.Contains(start))
                    continue;
                var path = new HashSet<string>(System.StringComparer.Ordinal);
                string cur = start;
                while (cur != null && next.ContainsKey(cur))
                {
                    if (path.Contains(cur))
                    {
                        findings.Add(new Finding(
                            RuleIdConst,
                            VerdictLevel.Fail,
                            cur,
                            -1,
                            Inv("INV7 badlink recording={0} field=SupersedeRelation target={0} kind=cycle", cur),
                            "EffectiveState.IsSupersededByRelation"));
                        foreach (string p in path)
                            reported.Add(p);
                        reported.Add(cur);
                        break;
                    }
                    path.Add(cur);
                    cur = next[cur];
                }
            }
        }

        // --- Family 3: chain contiguity per (ChainId, ChainBranch) ---

        private static void CheckChainContiguity(AnalyzerModel model, List<Finding> findings)
        {
            // Group chained recordings by (ChainId, ChainBranch).
            var groups = new Dictionary<(string, int), List<int>>();
            // Track, per ChainId, whether the chain carries a supersede boundary:
            // reuse the optimizer's own re-merge guard predicate so INV7 and
            // RecordingOptimizer.CanAutoMerge agree on what a boundary is.
            var chainHasSupersede = new Dictionary<string, bool>(System.StringComparer.Ordinal);

            foreach (Recording rec in model.Recordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.ChainId))
                    continue;

                if (!chainHasSupersede.TryGetValue(rec.ChainId, out bool already) || !already)
                {
                    bool superseded = EffectiveState.IsSupersededByRelation(rec, model.SupersedeRelations);
                    chainHasSupersede[rec.ChainId] = already || superseded;
                }

                if (rec.ChainIndex < 0)
                    continue;

                var key = (rec.ChainId, rec.ChainBranch);
                if (!groups.TryGetValue(key, out List<int> indices))
                {
                    indices = new List<int>();
                    groups[key] = indices;
                }
                indices.Add(rec.ChainIndex);
            }

            foreach (KeyValuePair<(string ChainId, int Branch), List<int>> g in groups)
            {
                List<int> indices = g.Value;
                indices.Sort();

                int min = indices[0];
                int max = indices[indices.Count - 1];
                var present = new HashSet<int>(indices);

                bool exempt = chainHasSupersede.TryGetValue(g.Key.ChainId, out bool v) && v;

                for (int n = min + 1; n < max; n++)
                {
                    if (present.Contains(n))
                        continue;

                    findings.Add(new Finding(
                        RuleIdConst,
                        exempt ? VerdictLevel.Info : VerdictLevel.Fail,
                        g.Key.ChainId,
                        -1,
                        Inv("INV7 chaingap chainId={0} branch={1} missingIndex={2} supersedeExempt={3}",
                            g.Key.ChainId, g.Key.Branch, n, exempt ? "True" : "False"),
                        "Recording.ChainId/ChainIndex/ChainBranch"));
                }
            }
        }

        private static string Inv(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }
}
