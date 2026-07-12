using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Log-assertion tests for the baseline filter's decision lines, per the
    // canonical RewindLoggingTests pattern (sink in the constructor,
    // ResetTestOverrides in Dispose, [Collection("Sequential")]). These prove the
    // log-first debugging contract: a decision the report reflects must also be
    // visible in the KSP.log.
    [Collection("Sequential")]
    public class BaselineFilterLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public BaselineFilterLoggingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Finding F(string ruleId, VerdictLevel level, string target, int section, string message)
        {
            return new Finding(ruleId, level, target, section, message, "cited");
        }

        private static BaselineEntry EntryFor(Finding f, VerdictLevel captured)
        {
            BaselineKey k = BaselineFilter.KeyOf(f);
            return new BaselineEntry(k.RuleId, k.Target, k.SectionIndex, k.MessageDigest, captured, "known");
        }

        private static AnalysisReport Report(params Finding[] findings)
        {
            var report = new AnalysisReport { SaveName = "s", Findings = new List<Finding>(findings) };
            report.Counts = Counts.From(report.Findings);
            return report;
        }

        private static AnalysisBaseline Baseline(params BaselineEntry[] e)
        {
            return new AnalysisBaseline { Entries = new List<BaselineEntry>(e) };
        }

        // Guards: a masked-then-promoted FAIL is visible in the log, not only by
        // reading the report. An entry captured at WARN against a now-FAIL finding
        // emits the escalated Warn line.
        [Fact]
        public void EscalatedNotBaselined_LogsWarnLine()
        {
            var f = F("INV3-ABSOLUTE-RANGE", VerdictLevel.Fail, "rec", 0, "INV3 lat=95");
            BaselineFilter.Apply(Report(f), Baseline(EntryFor(f, VerdictLevel.Warn)),
                BaselineMode.Apply, true, null);

            Assert.Contains(logLines, l => l.Contains("[Analyzer]")
                && l.Contains("baseline escalated")
                && l.Contains("captured=WARN")
                && l.Contains("now=FAIL")
                && l.Contains("notBaselined"));
        }

        // Guards: the fresh-save structural refusal is observable. Forbid over a save
        // carrying a baseline emits the FORBIDDEN Warn line.
        [Fact]
        public void ForbidTrip_LogsForbiddenLine()
        {
            var f = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec", 1, "INV2 overlap a=[1,2] b=[1,2]");
            BaselineFilter.Apply(Report(f), Baseline(EntryFor(f, VerdictLevel.Fail)),
                BaselineMode.Forbid, true, null);

            Assert.Contains(logLines, l => l.Contains("[Analyzer]")
                && l.Contains("baseline FORBIDDEN")
                && l.Contains("save='s'"));
        }

        // Guards: exactly ONE match-sweep summary line per Apply run (batch-counting
        // convention). Fails if the summary is missing (no per-run accounting) or
        // emitted per-entry (log spam).
        [Fact]
        public void MatchSweep_SummaryLogsExactlyOnce()
        {
            var findings = new List<Finding>();
            var entries = new List<BaselineEntry>();
            for (int i = 0; i < 3; i++)
            {
                var f = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec" + i, i,
                    "INV2 overlap recording=rec" + i + " a=[1,2] b=[1,2]");
                findings.Add(f);
                entries.Add(EntryFor(f, VerdictLevel.Fail));
            }
            BaselineFilter.Apply(Report(findings.ToArray()), Baseline(entries.ToArray()),
                BaselineMode.Apply, true, null);

            int summaries = logLines.Count(l => l.Contains("[Analyzer]") && l.Contains("baseline applied matched="));
            Assert.Equal(1, summaries);
        }
    }
}
