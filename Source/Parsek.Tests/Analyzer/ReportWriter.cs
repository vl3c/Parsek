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
                // Trailing comma: the additive careerSave block follows (module M-B2).
                sb.Append("],").Append(Nl);
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
                sb.Append("  ],").Append(Nl);
            }

            // Additive careerSave export block (module M-B2, the ledger-oracle
            // produced-save leg). ALWAYS emitted whenever the analyzer ran, so its
            // ABSENCE from an .analysis.json is unambiguous (an old / broken analyzer,
            // treated as INVALID(tooling) by the verifier, never facet-absence).
            // Facet-absence (Sandbox / Science) is signalled INSIDE the block by the
            // hasX flags. Determinism mirrors the rest of the writer: sorted keys,
            // InvariantCulture "R" floats, "\n" line endings.
            AppendCareerSave(sb, report.CareerSave);

            sb.Append("}").Append(Nl);
            return sb.ToString();
        }

        /// <summary>
        /// Serializes the parsed <see cref="CareerSaveSnapshot"/> as the additive
        /// careerSave block (module M-B2). A null snapshot (non-career / unparsable)
        /// emits <c>{"parsed": false}</c>; a parsed snapshot emits the full facet set
        /// with per-facet hasX flags so the Python verifier reads facet-absence from
        /// the flags, never from a missing block. All collections are key-sorted and
        /// all numbers use InvariantCulture "R" so the output is byte-deterministic.
        /// </summary>
        private static void AppendCareerSave(StringBuilder sb, Parsek.CareerSaveSnapshot cs)
        {
            sb.Append("  \"careerSave\": {").Append(Nl);
            if (cs == null || !cs.Parsed)
            {
                sb.Append("    \"parsed\": false").Append(Nl);
                sb.Append("  }").Append(Nl);
                return;
            }

            sb.Append("    \"parsed\": true,").Append(Nl);
            sb.Append("    \"hasFunds\": ").Append(cs.HasFunds ? "true" : "false").Append(",").Append(Nl);
            sb.Append("    \"funds\": ").Append(JsonDouble(cs.Funds)).Append(",").Append(Nl);
            sb.Append("    \"hasScience\": ").Append(cs.HasScience ? "true" : "false").Append(",").Append(Nl);
            sb.Append("    \"sciencePool\": ").Append(JsonDouble(cs.SciencePool)).Append(",").Append(Nl);
            sb.Append("    \"hasRep\": ").Append(cs.HasRep ? "true" : "false").Append(",").Append(Nl);
            sb.Append("    \"reputation\": ").Append(JsonDouble(cs.Reputation)).Append(",").Append(Nl);

            AppendStringDoubleMap(sb, "subjectScience", cs.SubjectScience);
            sb.Append(",").Append(Nl);
            AppendStringDoubleMap(sb, "facilityLevelFrac", cs.FacilityLevelFrac);
            sb.Append(",").Append(Nl);
            AppendStringArray(sb, "activeContractGuids", cs.ActiveContractGuids);
            sb.Append(",").Append(Nl);
            AppendStringArray(sb, "completedMilestoneIds", cs.CompletedMilestoneIds);
            sb.Append(",").Append(Nl);
            AppendVessels(sb, cs.Vessels);
            sb.Append(Nl);

            sb.Append("  }").Append(Nl);
        }

        /// <summary>
        /// Emits <c>"name": { "k": v, ... }</c> at 4-space indent, keys sorted ordinal,
        /// values as InvariantCulture "R" doubles. No trailing newline (the caller
        /// appends the field separator).
        /// </summary>
        private static void AppendStringDoubleMap(
            StringBuilder sb, string name, IDictionary<string, double> map)
        {
            sb.Append("    ").Append(JsonString(name)).Append(": {");
            if (map == null || map.Count == 0)
            {
                sb.Append("}");
                return;
            }
            var keys = new List<string>(map.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            sb.Append(Nl);
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append("      ").Append(JsonString(keys[i])).Append(": ")
                    .Append(JsonDouble(map[keys[i]]))
                    .Append(i == keys.Count - 1 ? "" : ",").Append(Nl);
            }
            sb.Append("    }");
        }

        /// <summary>
        /// Emits <c>"name": [ "v", ... ]</c> at 4-space indent, values sorted ordinal.
        /// No trailing newline (the caller appends the field separator).
        /// </summary>
        private static void AppendStringArray(
            StringBuilder sb, string name, IEnumerable<string> values)
        {
            var items = values == null ? new List<string>() : new List<string>(values);
            items.Sort(System.StringComparer.Ordinal);
            sb.Append("    ").Append(JsonString(name)).Append(": [");
            if (items.Count == 0)
            {
                sb.Append("]");
                return;
            }
            sb.Append(Nl);
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append("      ").Append(JsonString(items[i]))
                    .Append(i == items.Count - 1 ? "" : ",").Append(Nl);
            }
            sb.Append("    ]");
        }

        /// <summary>
        /// Emits the <c>"vessels": [ ... ]</c> array. Vessels are sorted by
        /// (pid, persistentId, name) ordinal so the output is byte-deterministic
        /// regardless of parse order, and each vessel's resourceTotals map is
        /// key-sorted. No trailing newline (the caller appends the field separator).
        /// </summary>
        private static void AppendVessels(StringBuilder sb, List<Parsek.SaveVessel> vessels)
        {
            var items = vessels == null ? new List<Parsek.SaveVessel>() : new List<Parsek.SaveVessel>(vessels);
            items.Sort((a, b) =>
            {
                int c = System.StringComparer.Ordinal.Compare(a.Pid ?? "", b.Pid ?? "");
                if (c != 0) return c;
                c = a.PersistentId.CompareTo(b.PersistentId);
                if (c != 0) return c;
                return System.StringComparer.Ordinal.Compare(a.Name ?? "", b.Name ?? "");
            });

            sb.Append("    \"vessels\": [");
            if (items.Count == 0)
            {
                sb.Append("]");
                return;
            }
            sb.Append(Nl);
            for (int i = 0; i < items.Count; i++)
            {
                Parsek.SaveVessel v = items[i];
                sb.Append("      {").Append(Nl);
                sb.Append("        \"pid\": ").Append(JsonString(v.Pid)).Append(",").Append(Nl);
                sb.Append("        \"persistentId\": ").Append(v.PersistentId.ToString(IC)).Append(",").Append(Nl);
                sb.Append("        \"name\": ").Append(JsonString(v.Name)).Append(",").Append(Nl);
                sb.Append("        \"type\": ").Append(JsonString(v.Type)).Append(",").Append(Nl);
                sb.Append("        \"resourceTotals\": {");
                if (v.ResourceTotals == null || v.ResourceTotals.Count == 0)
                {
                    sb.Append("}").Append(Nl);
                }
                else
                {
                    var rkeys = new List<string>(v.ResourceTotals.Keys);
                    rkeys.Sort(System.StringComparer.Ordinal);
                    sb.Append(Nl);
                    for (int r = 0; r < rkeys.Count; r++)
                    {
                        sb.Append("          ").Append(JsonString(rkeys[r])).Append(": ")
                            .Append(JsonDouble(v.ResourceTotals[rkeys[r]]))
                            .Append(r == rkeys.Count - 1 ? "" : ",").Append(Nl);
                    }
                    sb.Append("        }").Append(Nl);
                }
                sb.Append("      }").Append(i == items.Count - 1 ? "" : ",").Append(Nl);
            }
            sb.Append("    ]");
        }

        /// <summary>
        /// InvariantCulture round-trip ("R") double, the same format the rest of the
        /// codebase serializes floats with. Emits a valid JSON number for every finite
        /// value; a non-finite value (never expected on a parsed career pool) is
        /// coerced to <c>0</c> so the JSON stays parseable.
        /// </summary>
        private static string JsonDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "0";
            return value.ToString("R", IC);
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
                // careerSave export summary (module M-B2): the block is always emitted;
                // this names its parsed state + per-facet flags so a run log shows what
                // the ledger-oracle verifier will read without opening the json.
                + " careerSave=" + (report.CareerSave != null && report.CareerSave.Parsed ? "parsed" : "absent")
                + " hasFunds=" + (report.CareerSave != null && report.CareerSave.HasFunds ? "1" : "0")
                + " hasScience=" + (report.CareerSave != null && report.CareerSave.HasScience ? "1" : "0")
                + " hasRep=" + (report.CareerSave != null && report.CareerSave.HasRep ? "1" : "0")
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
