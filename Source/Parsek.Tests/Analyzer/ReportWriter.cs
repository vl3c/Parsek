using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Parsek.Tests.Analyzer
{
    // Emits the two frozen report files per subject (design doc "Report format").
    // Determinism is a hard requirement: the same input produces byte-identical
    // output, so there are no timestamps, no absolute paths, and no
    // dictionary-iteration order. All numeric formatting uses InvariantCulture.
    internal static class ReportWriter
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // "\n" everywhere (not Environment.NewLine) so a report generated on
        // Windows and Linux is byte-identical.
        private const string Nl = "\n";

        /// <summary>UPPERCASE token for a level, used in both report files.</summary>
        internal static string LevelToken(VerdictLevel level)
        {
            switch (level)
            {
                case VerdictLevel.Fail: return "FAIL";
                case VerdictLevel.Warn: return "WARN";
                case VerdictLevel.StaleFixture: return "STALE";
                default: return "INFO";
            }
        }

        /// <summary>
        /// Deterministic total order: level DESC (StaleFixture, Fail, Warn, Info),
        /// then ruleId, target, sectionIndex ascending (all ordinal). Returns a new
        /// list; the input is not mutated.
        /// </summary>
        internal static List<Finding> SortFindings(IEnumerable<Finding> findings)
        {
            return findings
                .OrderByDescending(f => (int)f.Level)
                .ThenBy(f => f.RuleId ?? "", System.StringComparer.Ordinal)
                .ThenBy(f => f.Target ?? "", System.StringComparer.Ordinal)
                .ThenBy(f => f.SectionIndex)
                .ToList();
        }

        /// <summary>
        /// Machine-readable, stable, sorted JSON. Field order is fixed:
        /// analyzerVersion, saveName, subjectSchemaGeneration, counts, findings.
        /// </summary>
        internal static string BuildJson(AnalysisReport report)
        {
            var sb = new StringBuilder();
            sb.Append("{").Append(Nl);
            sb.Append("  \"analyzerVersion\": ").Append(JsonString(report.AnalyzerVersion)).Append(",").Append(Nl);
            sb.Append("  \"saveName\": ").Append(JsonString(report.SaveName)).Append(",").Append(Nl);
            sb.Append("  \"subjectSchemaGeneration\": ")
                .Append(report.SubjectSchemaGeneration.ToString(IC)).Append(",").Append(Nl);

            sb.Append("  \"counts\": {").Append(Nl);
            sb.Append("    \"fail\": ").Append(report.Counts.Fail.ToString(IC)).Append(",").Append(Nl);
            sb.Append("    \"warn\": ").Append(report.Counts.Warn.ToString(IC)).Append(",").Append(Nl);
            sb.Append("    \"info\": ").Append(report.Counts.Info.ToString(IC)).Append(",").Append(Nl);
            sb.Append("    \"staleFixture\": ").Append(report.Counts.StaleFixture.ToString(IC)).Append(",").Append(Nl);
            sb.Append("    \"baselined\": ").Append(report.Counts.Baselined.ToString(IC)).Append(",").Append(Nl);
            sb.Append("    \"failNonBaselined\": ").Append(report.Counts.FailNonBaselined.ToString(IC)).Append(",").Append(Nl);
            sb.Append("    \"staleNonBaselined\": ").Append(report.Counts.StaleNonBaselined.ToString(IC)).Append(Nl);
            sb.Append("  },").Append(Nl);

            List<Finding> sorted = SortFindings(report.Findings ?? new List<Finding>());
            sb.Append("  \"findings\": [");
            if (sorted.Count == 0)
            {
                sb.Append("]").Append(Nl);
            }
            else
            {
                sb.Append(Nl);
                for (int i = 0; i < sorted.Count; i++)
                {
                    Finding f = sorted[i];
                    sb.Append("    {").Append(Nl);
                    sb.Append("      \"ruleId\": ").Append(JsonString(f.RuleId)).Append(",").Append(Nl);
                    sb.Append("      \"level\": ").Append(JsonString(LevelToken(f.Level))).Append(",").Append(Nl);
                    sb.Append("      \"target\": ").Append(JsonString(f.Target)).Append(",").Append(Nl);
                    sb.Append("      \"sectionIndex\": ").Append(f.SectionIndex.ToString(IC)).Append(",").Append(Nl);
                    sb.Append("      \"message\": ").Append(JsonString(f.Message)).Append(",").Append(Nl);
                    sb.Append("      \"citedContract\": ").Append(JsonString(f.CitedContract)).Append(",").Append(Nl);
                    sb.Append("      \"baselined\": ").Append(f.Baselined ? "true" : "false").Append(Nl);
                    sb.Append("    }").Append(i == sorted.Count - 1 ? "" : ",").Append(Nl);
                }
                sb.Append("  ]").Append(Nl);
            }

            sb.Append("}").Append(Nl);
            return sb.ToString();
        }

        /// <summary>
        /// Human summary. One header line then one grep-friendly line per finding,
        /// mirroring the validate-ksp-log.ps1 output style.
        /// </summary>
        internal static string BuildHumanSummary(AnalysisReport report)
        {
            // Terminal RED token: the emitter's SINGLE reduction of the non-baselined
            // splits (RED=1 iff failNonBaselined + staleNonBaselined > 0). It is the
            // LAST token on the header line and the ONLY gate source a script reads;
            // the earlier FAIL=/STALE= tokens remain raw totals that still include
            // baselined findings, so a gate must never recompute red from them.
            int red = (report.Counts.FailNonBaselined + report.Counts.StaleNonBaselined) > 0 ? 1 : 0;

            var sb = new StringBuilder();
            sb.Append("[Analyzer] save=").Append(report.SaveName ?? "")
                .Append(" generation=").Append(report.SubjectSchemaGeneration.ToString(IC))
                .Append(" FAIL=").Append(report.Counts.Fail.ToString(IC))
                .Append(" WARN=").Append(report.Counts.Warn.ToString(IC))
                .Append(" INFO=").Append(report.Counts.Info.ToString(IC))
                .Append(" STALE=").Append(report.Counts.StaleFixture.ToString(IC))
                .Append(" BASELINED=").Append(report.Counts.Baselined.ToString(IC))
                .Append(" RED=").Append(red.ToString(IC))
                .Append(Nl);

            foreach (Finding f in SortFindings(report.Findings ?? new List<Finding>()))
            {
                string target = f.SectionIndex >= 0
                    ? (f.Target ?? "") + "#" + f.SectionIndex.ToString(IC)
                    : (f.Target ?? "");
                sb.Append(LevelToken(f.Level)).Append(" ")
                    .Append(f.RuleId ?? "").Append(" target=").Append(target)
                    .Append(" ").Append(f.Message ?? "");
                // A baselined line is suffixed so a human scanning the text sees at a
                // glance which reds are accepted.
                if (f.Baselined)
                    sb.Append(" [baselined]");
                sb.Append(Nl);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Writes <c>&lt;save&gt;.analysis.json</c> and <c>&lt;save&gt;.analysis.txt</c>
        /// into <paramref name="resultsDir"/> (created if absent). UTF-8 without BOM,
        /// so the bytes are deterministic across runs.
        /// </summary>
        internal static void Write(AnalysisReport report, string resultsDir)
        {
            if (!Directory.Exists(resultsDir))
                Directory.CreateDirectory(resultsDir);

            string baseName = string.IsNullOrEmpty(report.SaveName) ? "analysis" : report.SaveName;
            var utf8NoBom = new UTF8Encoding(false);
            string jsonPath = Path.Combine(resultsDir, baseName + ".analysis.json");
            File.WriteAllText(jsonPath, BuildJson(report), utf8NoBom);
            File.WriteAllText(Path.Combine(resultsDir, baseName + ".analysis.txt"), BuildHumanSummary(report), utf8NoBom);

            // Diagnostic logging (design "Diagnostic Logging"): one-shot report-write
            // summary carrying the per-level counts + output path.
            Parsek.ParsekLog.Info("Analyzer",
                "report save='" + (report.SaveName ?? "") + "'"
                + " FAIL=" + report.Counts.Fail.ToString(IC)
                + " WARN=" + report.Counts.Warn.ToString(IC)
                + " INFO=" + report.Counts.Info.ToString(IC)
                + " STALE=" + report.Counts.StaleFixture.ToString(IC)
                + " BASELINED=" + report.Counts.Baselined.ToString(IC)
                + " RED=" + (report.IsRed ? "1" : "0")
                + " json='" + jsonPath + "'");
        }

        private static string JsonString(string value)
        {
            if (value == null)
                return "\"\"";
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                            sb.Append("\\u").Append(((int)ch).ToString("x4", IC));
                        else
                            sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
