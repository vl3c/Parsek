using System.Collections.Generic;
using System.Globalization;
using Parsek;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV8 ledger (design doc "The invariant rules" INV8, edge cases 16-18, 17b).
    //
    // Two parts, distinct severities. Pure over the model; the loader materialized
    // the RAW, unfiltered Ledger.Actions (correction C1) and the tombstone rows so
    // this rule can reconstruct the ELS filter itself.
    //
    // Part (a) -- ELS internal consistency (FAIL, every save). The rule computes
    // the ELS filter internally from model.Ledger (raw actions) minus any action
    // whose ActionId is tombstoned (the ELS definition cited in EffectiveState.cs;
    // it NEVER calls the static EffectiveState.ComputeELS, which reads process
    // statics -- correction C3). Because the input is the RAW list, the check is
    // non-vacuous: every tombstone's target ActionId must resolve against the raw
    // actions, and a tombstone pointing at an id absent from them is a real
    // dangling reference -> FAIL. Runs for career and non-career saves alike.
    //
    // Part (b) -- career-diff reconstruction (WARN, career only). See the 5.2
    // continuation below.
    internal sealed class Inv8Ledger : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV8-LEDGER";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "EffectiveState.ComputeELS (ELS definition) / CareerSaveParser.Parse / LedgerGroundTruthDiff.Compare";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model == null)
                return findings;

            EvaluateEls(model, findings);
            return findings;
        }

        // --- Part (a): ELS internal consistency (RAW model, every save) ---

        private static void EvaluateEls(AnalyzerModel model, List<Finding> findings)
        {
            var rawActionIds = new HashSet<string>(System.StringComparer.Ordinal);
            if (model.Ledger != null)
            {
                foreach (GameAction a in model.Ledger)
                {
                    if (a != null && !string.IsNullOrEmpty(a.ActionId))
                        rawActionIds.Add(a.ActionId);
                }
            }

            // Compute the ELS filter internally (raw actions minus tombstoned ids).
            // Not consumed by the dangling check itself -- it is the ELS definition
            // made explicit so this rule and EffectiveState.ComputeELS agree on the
            // filter without the analyzer touching the process-static computation.
            var tombstonedIds = new HashSet<string>(System.StringComparer.Ordinal);
            if (model.Tombstones != null)
            {
                foreach (LedgerTombstone t in model.Tombstones)
                {
                    if (t != null && !string.IsNullOrEmpty(t.ActionId))
                        tombstonedIds.Add(t.ActionId);
                }
            }
            int elsCount = 0;
            foreach (string id in rawActionIds)
                if (!tombstonedIds.Contains(id))
                    elsCount++;
            ParsekLog.Verbose("Analyzer",
                Inv("INV8 els raw={0} tombstoned={1} els={2}",
                    rawActionIds.Count, tombstonedIds.Count, elsCount));

            if (model.Tombstones == null)
                return;

            foreach (LedgerTombstone t in model.Tombstones)
            {
                if (t == null || string.IsNullOrEmpty(t.ActionId))
                    continue;
                if (rawActionIds.Contains(t.ActionId))
                    continue;
                findings.Add(new Finding(
                    RuleIdConst,
                    VerdictLevel.Fail,
                    t.TombstoneId ?? "<tombstone>",
                    -1,
                    Inv("INV8 dangling-tombstone tombstone={0} actionId={1} kind=els-inconsistency",
                        t.TombstoneId ?? "<none>", t.ActionId),
                    "EffectiveState.ComputeELS"));
            }
        }

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
