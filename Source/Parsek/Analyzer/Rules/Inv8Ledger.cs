using System.Collections.Generic;
using System.Globalization;
using Parsek;

namespace Parsek.Analyzer.Rules
{
    // INV8 ledger (design doc "The invariant rules" INV8, edge cases 16-18, 17b).
    //
    // Two parts, distinct severities. Pure over the model; the builder materialized
    // the RAW, unfiltered ledger actions (correction C1) and the tombstone rows so
    // this rule can reconstruct the ELS filter itself. This rule reads no store
    // symbols -- it operates only on model.Ledger, the materialized list.
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
    // Part (b) -- career-diff reconstruction (WARN, career only). The rule diffs a
    // headless ledger reconstruction against the parsed career totals via
    // LedgerGroundTruthDiff.Compare with a hardcoded stock facility-max-levels map
    // (the live-KSP injection has no offline equivalent). ANY divergence -> WARN,
    // NEVER FAIL offline: the FAIL-severity career diff is the in-game H5 path
    // (module M-A3), where the LedgerGroundTruthHarness reconstructs against a live
    // quicksave. In v1 no headless reconstruction is built (correction C5, the
    // recalc seam is deferred), so a career save with no injected reconstruction
    // reports reconstruction-not-available INFO. A non-career save skips part (b)
    // entirely (silent: the career diff carries no information on a Sandbox save,
    // and staying finding-free preserves the clean-data-is-green convention).
    internal sealed class Inv8Ledger : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV8-LEDGER";
        internal const string CareerDiffRuleId = "INV8-CAREER-DIFF";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "EffectiveState.ComputeELS (ELS definition) / CareerSaveParser.Parse / LedgerGroundTruthDiff.Compare";

        // Hardcoded stock facility-max-levels (0-based max index; stock facilities
        // are 3-tier -> 2), standing in for the live-KSP injection the in-game path
        // uses. Keys match the display facility ids CareerSaveParser records.
        private static readonly IReadOnlyDictionary<string, int> StockFacilityMaxLevels =
            new Dictionary<string, int>(System.StringComparer.Ordinal)
            {
                ["SpaceCenter/LaunchPad"] = 2,
                ["SpaceCenter/Runway"] = 2,
                ["SpaceCenter/VehicleAssemblyBuilding"] = 2,
                ["SpaceCenter/SpaceplaneHangar"] = 2,
                ["SpaceCenter/TrackingStation"] = 2,
                ["SpaceCenter/AstronautComplex"] = 2,
                ["SpaceCenter/MissionControl"] = 2,
                ["SpaceCenter/AdministrationFacility"] = 2,
                ["SpaceCenter/ResearchAndDevelopment"] = 2,
            };

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model == null)
                return findings;

            EvaluateEls(model, findings);
            EvaluateCareerDiff(model, findings);
            return findings;
        }

        // --- Part (b): career-diff reconstruction (WARN, career only) ---

        private static void EvaluateCareerDiff(AnalyzerModel model, List<Finding> findings)
        {
            // Non-funds -> skip (silent). Part (b) has nothing to compare on a
            // Sandbox / Science save. RE-GATE on HasFunds, not on snapshot null-ness
            // (module M-B2): the loader now flows ANY Parsed snapshot to the model
            // (so the careerSave export block is populated on career-but-non-funds
            // Science / Sandbox saves), so a non-null CareerSave no longer implies a
            // funds facet. The funds-only career diff stays unchanged for real career
            // saves; a Science / Sandbox snapshot (HasFunds == false) skips part (b)
            // exactly as a null snapshot used to.
            if (model.CareerSave == null || !model.CareerSave.HasFunds)
                return;

            if (model.LedgerReconstruction == null)
            {
                // No headless reconstruction available offline (correction C5).
                findings.Add(new Finding(
                    CareerDiffRuleId,
                    VerdictLevel.Info,
                    model.SaveName ?? "<save>",
                    -1,
                    "INV8 career-diff reconstruction-not-available (headless recalc deferred)",
                    "LedgerGroundTruthDiff.Compare"));
                return;
            }

            LedgerDivergenceReport report = LedgerGroundTruthDiff.Compare(
                model.CareerSave,
                model.LedgerReconstruction,
                FacetTolerances.Default,
                StockFacilityMaxLevels);

            if (report == null || report.All.Count == 0)
                return; // clean reconstruction -> no finding

            // ANY divergence (hard or report-only) -> WARN offline, never FAIL.
            int hard = report.HardFailures(LedgerGroundTruthDiff.StrictPerIdentityForTesting).Count;
            findings.Add(new Finding(
                CareerDiffRuleId,
                VerdictLevel.Warn,
                model.SaveName ?? "<save>",
                -1,
                Inv("INV8 career-diff divergences={0} hard={1} reportOnly={2}",
                    report.All.Count, hard, report.All.Count - hard),
                "LedgerGroundTruthDiff.Compare"));
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

            // Single-report policy (mirrors INV5's tested faulted-trajectory skip):
            // when the ledger file itself failed to load, the RAW action list is
            // incomplete or empty, so every tombstone would look dangling. That is
            // not an ELS inconsistency -- it is a load failure the LOADER-FAULT rule
            // already reports as the authoritative finding. Skip the per-tombstone
            // dangling check so the corrupt ledger is reported exactly once.
            if (HasLedgerFault(model))
                return;

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

        private static bool HasLedgerFault(AnalyzerModel model)
        {
            if (model.LoadFaults == null)
                return false;
            foreach (LoadFault f in model.LoadFaults)
                if (f.FileKind == "ledger")
                    return true;
            return false;
        }

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
