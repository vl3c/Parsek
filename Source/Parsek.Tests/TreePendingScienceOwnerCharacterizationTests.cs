using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.ResolveTreePendingScienceOwnerRecordingId</c>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. The method and its window struct are private, so the test reaches
    /// both by reflection and reads the original and the split method alike. Scenarios:
    /// untagged subjects (window pick, the active-recording tie-break, a NaN capture), a tag
    /// naming no window, a capture inside the tagged window and on its edges, implausible
    /// captures (NaN, infinities, zero, negative), a capture outside the tagged window with no
    /// covering window, with a chain-sibling covering window, with a covering window of another
    /// chain and with both chain ids empty, and a tag naming two windows. It records the
    /// returned owner and every log line.
    /// </summary>
    [Collection("Sequential")]
    public class TreePendingScienceOwnerCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TreePendingScienceOwnerCharacterizationTests()
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

        private static readonly Type WindowType = typeof(LedgerOrchestrator).GetNestedType(
            "RecordingScienceWindow", BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly MethodInfo Resolve = typeof(LedgerOrchestrator).GetMethod(
            "ResolveTreePendingScienceOwnerRecordingId", BindingFlags.NonPublic | BindingFlags.Static);

        private struct W
        {
            public string Id;
            public string Chain;
            public double Start;
            public double End;
            public W(string id, string chain, double start, double end)
            {
                Id = id; Chain = chain; Start = start; End = end;
            }
        }

        private static object Windows(params W[] ws)
        {
            Array arr = Array.CreateInstance(WindowType, ws.Length);
            for (int i = 0; i < ws.Length; i++)
            {
                object box = Activator.CreateInstance(WindowType);
                WindowType.GetField("RecordingId").SetValue(box, ws[i].Id);
                WindowType.GetField("ChainId").SetValue(box, ws[i].Chain);
                WindowType.GetField("StartUT").SetValue(box, ws[i].Start);
                WindowType.GetField("EndUT").SetValue(box, ws[i].End);
                arr.SetValue(box, i);
            }
            return arr;
        }

        private static PendingScienceSubject Subject(string tag, double captureUT) =>
            new PendingScienceSubject
            {
                subjectId = "crewReport@KerbinSrfLanded",
                recordingId = tag,
                captureUT = captureUT,
                science = 1.5f,
            };

        private void Case(StringBuilder sb, string label, PendingScienceSubject subject, object windows,
            string active = "rec-active")
        {
            logLines.Clear();
            object owner = Resolve.Invoke(null, new object[] { subject, windows, active });
            sb.Append("== ").Append(label).Append('\n');
            sb.Append("owner=").Append((string)owner ?? "<null>").Append('\n');
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
        public void ResolveTreePendingScienceOwnerRecordingId_Golden()
        {
            Assert.NotNull(WindowType);
            Assert.NotNull(Resolve);
            var sb = new StringBuilder();

            object three = Windows(
                new W("rec-a", "chain-1", 100.0, 200.0),
                new W("rec-b", "chain-1", 200.0, 300.0),
                new W("rec-c", "chain-2", 250.0, 400.0));

            Case(sb, "S01 untagged, picked by window", Subject(null, 150.0), three);
            Case(sb, "S02 untagged empty tag, latest start wins", Subject("", 260.0), three);
            Case(sb, "S03 untagged, NaN capture", Subject(null, double.NaN), three);
            Case(sb, "S04 untagged, no covering window", Subject(null, 999.0), three);
            Case(sb, "S05 untagged, tie broken by the active recording",
                Subject(null, 150.0),
                Windows(new W("rec-x", null, 100.0, 200.0), new W("rec-active", null, 100.0, 200.0)));

            Case(sb, "S06 tag names no window", Subject("rec-zzz", 150.0), three);
            Case(sb, "S07 tagged, inside", Subject("rec-a", 150.0), three);
            Case(sb, "S08 tagged, on the start edge", Subject("rec-c", 250.0), three);
            Case(sb, "S09 tagged, on the end edge", Subject("rec-c", 400.0), three);
            Case(sb, "S10 tagged, NaN capture", Subject("rec-a", double.NaN), three);
            Case(sb, "S11 tagged, +Inf capture", Subject("rec-a", double.PositiveInfinity), three);
            Case(sb, "S12 tagged, -Inf capture", Subject("rec-a", double.NegativeInfinity), three);
            Case(sb, "S13 tagged, zero capture", Subject("rec-a", 0.0), three);
            Case(sb, "S14 tagged, negative capture", Subject("rec-a", -5.0), three);

            Case(sb, "S15 tagged, outside, nothing covers", Subject("rec-a", 999.5), three);
            Case(sb, "S16 tagged, outside, chain sibling covers", Subject("rec-a", 220.25), three);
            Case(sb, "S17 tagged, outside, another chain covers", Subject("rec-a", 350.0), three);
            Case(sb, "S18 tagged, outside, both chain ids empty",
                Subject("rec-p", 250.0),
                Windows(new W("rec-p", null, 100.0, 200.0), new W("rec-q", null, 200.0, 300.0)));
            Case(sb, "S19 tagged with empty chain, covering window has a chain",
                Subject("rec-p", 250.0),
                Windows(new W("rec-p", "", 100.0, 200.0), new W("rec-q", "chain-9", 200.0, 300.0)));
            Case(sb, "S20 tag names two windows, first decides",
                Subject("rec-d", 150.0),
                Windows(new W("rec-d", null, 300.0, 400.0), new W("rec-d", null, 100.0, 200.0),
                    new W("rec-e", "chain-3", 120.0, 180.0)));
            Case(sb, "S21 covering window is the tagged id itself",
                Subject("rec-d", 350.0),
                Windows(new W("rec-d", "chain-4", 100.0, 200.0), new W("rec-d", "chain-5", 300.0, 400.0)));

            AssertGolden("ResolveTreePendingScienceOwnerRecordingId", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== S01 untagged, picked by window",
            "owner=rec-a",
            "== S02 untagged empty tag, latest start wins",
            "owner=rec-c",
            "== S03 untagged, NaN capture",
            "owner=<null>",
            "== S04 untagged, no covering window",
            "owner=<null>",
            "== S05 untagged, tie broken by the active recording",
            "owner=rec-active",
            "== S06 tag names no window",
            "owner=<null>",
            "== S07 tagged, inside",
            "owner=rec-a",
            "== S08 tagged, on the start edge",
            "owner=rec-c",
            "== S09 tagged, on the end edge",
            "owner=rec-c",
            "== S10 tagged, NaN capture",
            "owner=rec-a",
            "== S11 tagged, +Inf capture",
            "owner=rec-a",
            "== S12 tagged, -Inf capture",
            "owner=rec-a",
            "== S13 tagged, zero capture",
            "owner=rec-a",
            "== S14 tagged, negative capture",
            "owner=rec-a",
            "== S15 tagged, outside, nothing covers",
            "owner=rec-a",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Tree science routing: subject 'crewReport@KerbinSrfLanded' tagged 'rec-a' has captureUT=999.5 outside the tagged window [100, 200] and no other recording window covers it \u2014 keeping tag attribution (post-window on-rails captures are legitimate; the per-subject window checks in ConvertScienceSubjects decide)",
            "== S16 tagged, outside, chain sibling covers",
            "owner=rec-a",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Tree science routing: subject 'crewReport@KerbinSrfLanded' tagged 'rec-a' has captureUT=220.25 outside the tagged window [100, 200], but the covering window 'rec-b' is a chain sibling (chain=chain-1) \u2014 keeping tag attribution (chain-gap-adjusted coverage; the tag is authoritative within one chained flight)",
            "== S17 tagged, outside, another chain covers",
            "owner=rec-c",
            "log [Parsek][WARN][LedgerOrchestrator] Tree science routing: subject 'crewReport@KerbinSrfLanded' tagged 'rec-a' has captureUT=350 outside the tagged window [100, 200] \u2014 re-routing by capture UT to 'rec-c'",
            "== S18 tagged, outside, both chain ids empty",
            "owner=rec-q",
            "log [Parsek][WARN][LedgerOrchestrator] Tree science routing: subject 'crewReport@KerbinSrfLanded' tagged 'rec-p' has captureUT=250 outside the tagged window [100, 200] \u2014 re-routing by capture UT to 'rec-q'",
            "== S19 tagged with empty chain, covering window has a chain",
            "owner=rec-q",
            "log [Parsek][WARN][LedgerOrchestrator] Tree science routing: subject 'crewReport@KerbinSrfLanded' tagged 'rec-p' has captureUT=250 outside the tagged window [100, 200] \u2014 re-routing by capture UT to 'rec-q'",
            "== S20 tag names two windows, first decides",
            "owner=rec-e",
            "log [Parsek][WARN][LedgerOrchestrator] Tree science routing: subject 'crewReport@KerbinSrfLanded' tagged 'rec-d' has captureUT=150 outside the tagged window [300, 400] \u2014 re-routing by capture UT to 'rec-e'",
            "== S21 covering window is the tagged id itself",
            "owner=rec-d",
            "log [Parsek][VERBOSE][LedgerOrchestrator] Tree science routing: subject 'crewReport@KerbinSrfLanded' tagged 'rec-d' has captureUT=350 outside the tagged window [100, 200], but the covering window 'rec-d' is a chain sibling (chain=chain-4) \u2014 keeping tag attribution (chain-gap-adjusted coverage; the tag is authoritative within one chained flight)",
        }) + "\n";
    }
}
