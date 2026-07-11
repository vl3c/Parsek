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

        /// <summary>Aggregates a finding list into per-level counts.</summary>
        public static Counts From(IEnumerable<Finding> findings)
        {
            var c = new Counts();
            if (findings == null)
                return c;
            foreach (Finding f in findings)
            {
                switch (f.Level)
                {
                    case VerdictLevel.Fail: c.Fail++; break;
                    case VerdictLevel.Warn: c.Warn++; break;
                    case VerdictLevel.Info: c.Info++; break;
                    case VerdictLevel.StaleFixture: c.StaleFixture++; break;
                }
            }
            return c;
        }
    }

    internal sealed class AnalysisReport
    {
        /// <summary>
        /// Frozen report-format version. Bumped ONLY on a .analysis.json schema
        /// change; the golden-JSON test fails if the schema drifts without a bump.
        /// </summary>
        public const string CurrentAnalyzerVersion = "1";

        public string SaveName;

        public string AnalyzerVersion = CurrentAnalyzerVersion;

        /// <summary>Discovered from the analyzed data.</summary>
        public int SubjectSchemaGeneration;

        /// <summary>
        /// Findings in insertion order. <see cref="ReportWriter"/> applies the
        /// deterministic (level desc, ruleId, target, sectionIndex) sort on write.
        /// </summary>
        public List<Finding> Findings = new List<Finding>();

        public Counts Counts;

        /// <summary>
        /// A run is red when it carries any FAIL or any STALE-FIXTURE (design run
        /// modes: both fail the build; WARN/INFO never do).
        /// </summary>
        public bool IsRed => Counts.Fail > 0 || Counts.StaleFixture > 0;
    }
}
