using System.Collections.Generic;

namespace Parsek.Analyzer
{
    // Loader/core output for one analysis subject (design doc "Loader output and
    // report"). The report format is a frozen output contract: AnalyzerVersion is
    // bumped only when the .analysis.json schema changes, never when a rule is
    // added (rules are data inside findings).

    /// <summary>Per-level finding tally. Never negative.</summary>
    internal struct Counts
    {
        public int Fail;
        public int Warn;
        public int Info;
        public int StaleFixture;

        /// <summary>
        /// Count of findings with <see cref="Finding.Baselined"/> == true (accepted
        /// by a per-save baseline). A baselined FAIL still increments <see cref="Fail"/>
        /// AND this, so the report shows "5 FAIL, of which 5 baselined" rather than
        /// hiding them.
        /// </summary>
        public int Baselined;

        /// <summary>FAIL findings with <see cref="Finding.Baselined"/> == false. Gate input.</summary>
        public int FailNonBaselined;

        /// <summary>STALE-FIXTURE findings with <see cref="Finding.Baselined"/> == false. Gate input.</summary>
        public int StaleNonBaselined;

        /// <summary>
        /// Aggregates a finding list into per-level counts plus the baselined tally
        /// and the non-baselined FAIL / STALE splits (the SINGLE source of truth for
        /// the gate; see <see cref="AnalysisReport.IsRed"/>).
        /// </summary>
        public static Counts From(IEnumerable<Finding> findings)
        {
            var c = new Counts();
            if (findings == null)
                return c;
            foreach (Finding f in findings)
            {
                switch (f.Level)
                {
                    case VerdictLevel.Fail:
                        c.Fail++;
                        if (!f.Baselined) c.FailNonBaselined++;
                        break;
                    case VerdictLevel.Warn: c.Warn++; break;
                    case VerdictLevel.Info: c.Info++; break;
                    case VerdictLevel.StaleFixture:
                        c.StaleFixture++;
                        if (!f.Baselined) c.StaleNonBaselined++;
                        break;
                }
                if (f.Baselined)
                    c.Baselined++;
            }
            return c;
        }
    }

    internal sealed class AnalysisReport
    {
        /// <summary>
        /// Frozen report-format version. Bumped ONLY on a .analysis.json schema
        /// change; the golden-JSON test fails if the schema drifts without a bump.
        /// Bumped "1" -> "2" for the per-save baseline layer (Finding.baselined +
        /// the Counts baselined / failNonBaselined / staleNonBaselined fields + the
        /// .txt BASELINED= / terminal RED= tokens + the [baselined] line suffix).
        /// Bumped "2" -> "3" for the additive careerSave export block (module M-B2,
        /// the ledger-oracle produced-save leg): the analyzer already parses the save
        /// into <see cref="CareerSave"/> and now serializes that snapshot as a
        /// careerSave block in the .analysis.json. Additive JSON alongside the
        /// existing counts/findings; existing per-save baselines stay applicable
        /// because createdAtAnalyzerVersion is provenance-only and never gates
        /// baseline matching.
        /// </summary>
        public const string CurrentAnalyzerVersion = "3";

        public string SaveName;

        public string AnalyzerVersion = CurrentAnalyzerVersion;

        /// <summary>
        /// The career-save snapshot the loader already parsed (<c>model.CareerSave</c>),
        /// threaded here so <c>ReportWriter</c> can serialize the additive careerSave
        /// export block (module M-B2). Null when the save is non-career / unparsable;
        /// the writer then emits <c>"careerSave": {"parsed": false}</c> (the block is
        /// ALWAYS emitted whenever the analyzer ran, so its ABSENCE from an
        /// .analysis.json means an old/broken analyzer, not facet-absence). Not part
        /// of the verdict / RED gate: purely an export surface for the harness's
        /// independent ledger oracle. Never serialized into the human .txt summary.
        /// </summary>
        public CareerSaveSnapshot CareerSave;

        /// <summary>Discovered from the analyzed data.</summary>
        public int SubjectSchemaGeneration;

        /// <summary>
        /// Runtime-only (never serialized into a report, so it does NOT bump the
        /// report schema): true when the analyzed subject carried a FixtureStamp
        /// (a stamped fixture corpus). <c>BaselineFilter.Apply</c> reads it to refuse
        /// an Apply over a stamped subject wholesale (BASELINE-REFUSED-STAMPED),
        /// keeping the fresh-managed STALE-FIXTURE gate un-softenable by a baseline.
        /// Set by <c>InvariantEvaluator.Evaluate</c> from <c>AnalyzerModel.FixtureStamp</c>.
        /// </summary>
        public bool SubjectIsStampedFixture;

        /// <summary>
        /// Findings in insertion order. <see cref="ReportWriter"/> applies the
        /// deterministic (level desc, ruleId, target, sectionIndex) sort on write.
        /// </summary>
        public List<Finding> Findings = new List<Finding>();

        public Counts Counts;

        /// <summary>
        /// A run is red when it carries a NON-baselined FAIL or a NON-baselined
        /// STALE-FIXTURE. Baselined findings (accepted by a per-save baseline) stay
        /// in the report but never gate. Reads the split counts, NOT the raw
        /// <see cref="Counts.Fail"/> / <see cref="Counts.StaleFixture"/> totals (those
        /// still include baselined findings). WARN/INFO never red. When no baseline
        /// is applied, FailNonBaselined == Fail and StaleNonBaselined == StaleFixture,
        /// so this reduces to the pre-feature gate.
        /// </summary>
        public bool IsRed => Counts.FailNonBaselined + Counts.StaleNonBaselined > 0;
    }
}
