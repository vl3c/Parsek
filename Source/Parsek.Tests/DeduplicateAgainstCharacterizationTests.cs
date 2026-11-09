using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <see cref="LedgerOrchestrator.DeduplicateAgainst"/>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. Each scenario hands it an existing row list and a candidate
    /// list covering: the empty-ledger early return (by reference), science capture
    /// identities (new, repeated inside the batch, already filed), event capture identities
    /// (same capture, a second event with the same key, a different key, a row without a
    /// capture number), the legacy economic match (inside and outside the 0.1 s window,
    /// another type, a KerbalAssignment at another UT, a science row's single-precision
    /// window), and null ids. It records which candidates came back, in order, and every
    /// log line.
    /// </summary>
    [Collection("Sequential")]
    public class DeduplicateAgainstCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DeduplicateAgainstCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Spend(string id, double ut, string node, long cseq = 0, bool eventId = false)
        {
            return new GameAction
            {
                ActionId = id,
                Type = GameActionType.ScienceSpending,
                UT = ut,
                NodeId = node,
                CaptureSequence = cseq,
                HasEventCaptureIdentity = eventId,
            };
        }

        private static GameAction Science(string id, double ut, string subject, bool captureId,
            float startUT = 0f, float endUT = 0f)
        {
            return new GameAction
            {
                ActionId = id,
                Type = GameActionType.ScienceEarning,
                UT = ut,
                SubjectId = subject,
                StartUT = startUT,
                EndUT = endUT,
                HasScienceCaptureIdentity = captureId,
            };
        }

        private static GameAction Crew(string id, double ut, string rec, string kerbal)
        {
            return new GameAction
            {
                ActionId = id,
                Type = GameActionType.KerbalAssignment,
                UT = ut,
                RecordingId = rec,
                KerbalName = kerbal,
            };
        }

        private void Case(StringBuilder sb, string label, List<GameAction> existing, List<GameAction> candidates)
        {
            logLines.Clear();
            List<GameAction> result = LedgerOrchestrator.DeduplicateAgainst(existing, candidates);
            sb.Append("== ").Append(label).Append('\n');
            sb.Append("same-instance=").Append(ReferenceEquals(result, candidates) ? "yes" : "no").Append('\n');
            sb.Append("kept=");
            for (int i = 0; i < result.Count; i++)
            {
                if (i > 0) sb.Append(',');
                int at = candidates.IndexOf(result[i]);
                sb.Append('#').Append(at.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(result[i].ActionId ?? "<null>");
            }
            sb.Append('\n');
            foreach (string line in logLines)
                sb.Append("log ").Append(line).Append('\n');
        }

        private static void AssertGolden(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                return;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden-actual");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".txt");
            File.WriteAllText(path, actual);
            Assert.True(false, "golden mismatch for " + name + "; actual transcript written to " + path);
        }

        [Fact]
        public void DeduplicateAgainst_Golden()
        {
            var sb = new StringBuilder();
            var none = new List<GameAction>();

            Case(sb, "D01 empty ledger, no science capture",
                none, new List<GameAction> { Spend("c1", 100.0, "n1"), Spend("c2", 100.0, "n1") });

            Case(sb, "D02 empty ledger, science captures with a batch repeat",
                none, new List<GameAction>
                {
                    Science("cap-a", 100.0, "s1", true),
                    Science("cap-b", 100.0, "s1", true),
                    Science("cap-a", 100.0, "s1", true),
                    Science("plain", 100.0, "s1", false),
                });

            Case(sb, "D03 science capture already filed",
                new List<GameAction> { Science("cap-a", 50.0, "s9", true), Spend("x", 100.0, "n1") },
                new List<GameAction>
                {
                    Science("cap-a", 100.0, "s1", true),
                    Science("cap-c", 100.0, "s1", true),
                    Science("", 100.0, "s1", true),
                });

            Case(sb, "D04 event capture: same capture, second event, other key, no capture number",
                new List<GameAction>
                {
                    Spend("e1", 100.0, "n1", cseq: 7, eventId: true),
                    Spend("e2", 100.0, "n2", cseq: 8, eventId: true),
                    Spend("e3", 100.0, "n3"),
                    Spend(null, 100.0, "n4", cseq: 9, eventId: true),
                },
                new List<GameAction>
                {
                    Spend("c-same", 100.0, "n1", cseq: 7, eventId: true),
                    Spend("c-second", 100.0625, "n2", cseq: 11, eventId: true),
                    Spend("c-otherkey", 100.0, "n5", cseq: 12, eventId: true),
                    Spend("c-legacy", 100.0, "n3", cseq: 13, eventId: true),
                    Spend("c-nullrow", 100.0, "n4", cseq: 14, eventId: true),
                    Spend("c-cseq-noflag", 100.0, "n1", cseq: 7, eventId: false),
                    Spend("c-flag-nocseq", 100.0, "n2", cseq: 0, eventId: true),
                });

            Case(sb, "D05 legacy window and type",
                new List<GameAction>
                {
                    Spend("l1", 100.0, "n1"),
                    Science("l2", 100.0, "n1", false),
                    Crew("l3", 10.0, "rec-a", "Jeb"),
                },
                new List<GameAction>
                {
                    Spend("c-in", 100.0625, "n1"),
                    Spend("c-out", 100.125, "n1"),
                    Spend("c-out-low", 99.875, "n1"),
                    Spend("c-otherkey", 100.0, "n2"),
                    Crew("c-crew-late", 500.0, "rec-a", "Jeb"),
                    Crew("c-crew-other", 10.0, "rec-a", "Bill"),
                    Crew("c-crew-otherrec", 10.0, "rec-b", "Jeb"),
                });

            Case(sb, "D06 science single-precision window",
                new List<GameAction>
                {
                    Science("w1", 900.0, "s1", false, startUT: 200.5f, endUT: 210f),
                    Science("w2", 0.0, "s2", false),
                },
                new List<GameAction>
                {
                    Science("c-win", 300.0, "s1", false, startUT: 200.5f, endUT: 220f),
                    Science("c-ut", 200.5, "s1", false),
                    Science("c-zero", 0.0625, "s2", false),
                    Science("c-zero-far", 5.0, "s2", false),
                });

            Case(sb, "D07 nothing removed, nothing kept as second event",
                new List<GameAction> { Spend("x1", 100.0, "n1") },
                new List<GameAction> { Spend("c1", 300.0, "n1"), Spend("c2", 100.0, "n9") });

            Case(sb, "D08 every counter at once",
                new List<GameAction>
                {
                    Science("cap-a", 1.0, "s1", true),
                    Spend("e1", 100.0, "n1", cseq: 7, eventId: true),
                    Spend("e2", 100.0, "n2", cseq: 8, eventId: true),
                    Spend("l1", 100.0, "n3"),
                },
                new List<GameAction>
                {
                    Science("cap-a", 1.0, "s1", true),
                    Science("cap-b", 1.0, "s1", true),
                    Science("cap-b", 1.0, "s1", true),
                    Spend("c-same", 100.0, "n1", cseq: 7, eventId: true),
                    Spend("c-second", 100.0, "n2", cseq: 21, eventId: true),
                    Spend("c-legacy", 100.0, "n3"),
                    Spend("c-new", 100.0, "n4"),
                });

            Case(sb, "D09 event capture with a second event against a row without id",
                new List<GameAction> { Spend(null, 100.0, "n1", cseq: 3, eventId: true) },
                new List<GameAction> { Spend("c1", 100.0, "n1", cseq: 4, eventId: true) });

            Case(sb, "D10 empty candidates against a non-empty ledger",
                new List<GameAction> { Spend("x1", 100.0, "n1") }, new List<GameAction>());

            AssertGolden("DeduplicateAgainst", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== D01 empty ledger, no science capture",
            "same-instance=yes",
            "kept=#0:c1,#1:c2",
            "== D02 empty ledger, science captures with a batch repeat",
            "same-instance=no",
            "kept=#0:cap-a,#1:cap-b,#3:plain",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 1 duplicates from 4 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=1 eventCaptureMatches=0 legacyKeyMatches=0 eventDuplicatesKept=0)",
            "== D03 science capture already filed",
            "same-instance=no",
            "kept=#1:cap-c,#2:",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 1 duplicates from 3 candidates (scienceCaptureIdMatches=1 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=0 eventDuplicatesKept=0)",
            "== D04 event capture: same capture, second event, other key, no capture number",
            "same-instance=no",
            "kept=#1:c-second,#2:c-otherkey,#4:c-nullrow",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: kept ScienceSpending key='n2' ut=100.0625 cseq=11 as a second event: row e2 ut=100 has the same key but capture cseq=8",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: kept ScienceSpending key='n4' ut=100 cseq=14 as a second event: row (none) ut=100 has the same key but capture cseq=9",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 4 duplicates from 7 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=1 legacyKeyMatches=3 eventDuplicatesKept=2)",
            "== D05 legacy window and type",
            "same-instance=no",
            "kept=#1:c-out,#2:c-out-low,#3:c-otherkey,#5:c-crew-other,#6:c-crew-otherrec",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 2 duplicates from 7 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=2 eventDuplicatesKept=0)",
            "== D06 science single-precision window",
            "same-instance=no",
            "kept=#3:c-zero-far",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 3 duplicates from 4 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=3 eventDuplicatesKept=0)",
            "== D07 nothing removed, nothing kept as second event",
            "same-instance=no",
            "kept=#0:c1,#1:c2",
            "== D08 every counter at once",
            "same-instance=no",
            "kept=#1:cap-b,#4:c-second,#6:c-new",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: kept ScienceSpending key='n2' ut=100 cseq=21 as a second event: row e2 ut=100 has the same key but capture cseq=8",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 4 duplicates from 7 candidates (scienceCaptureIdMatches=1 scienceCaptureIdBatchRepeats=1 eventCaptureMatches=1 legacyKeyMatches=1 eventDuplicatesKept=1)",
            "== D09 event capture with a second event against a row without id",
            "same-instance=no",
            "kept=#0:c1",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: kept ScienceSpending key='n1' ut=100 cseq=4 as a second event: row (none) ut=100 has the same key but capture cseq=3",
            "log [Parsek][VERBOSE][LedgerOrchestrator] DeduplicateAgainstLedger: removed 0 duplicates from 1 candidates (scienceCaptureIdMatches=0 scienceCaptureIdBatchRepeats=0 eventCaptureMatches=0 legacyKeyMatches=0 eventDuplicatesKept=1)",
            "== D10 empty candidates against a non-empty ledger",
            "same-instance=no",
            "kept=",
        }) + "\n";
    }
}
