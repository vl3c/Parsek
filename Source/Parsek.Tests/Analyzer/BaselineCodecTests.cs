using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Codec / fault-robustness + builder tests (design doc "Test Plan":
    // Codec / fault robustness + Script flag round-trip update semantics). Uses a
    // temp dir; no shared statics beyond ParsekLog (suppressed).
    public class BaselineCodecTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;

        public BaselineCodecTests()
        {
            prevSuppress = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-baseline-codec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.SuppressLogging = prevSuppress;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private string WriteRaw(string name, string content)
        {
            string path = Path.Combine(tempDir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static Finding F(string ruleId, VerdictLevel level, string target, int section, string message)
        {
            return new Finding(ruleId, level, target, section, message, "cited");
        }

        private static AnalysisReport Report(params Finding[] findings)
        {
            var report = new AnalysisReport
            {
                SaveName = "s",
                SubjectSchemaGeneration = 4,
                Findings = new List<Finding>(findings),
            };
            report.Counts = Counts.From(report.Findings);
            return report;
        }

        // Guards: a malformed whole file (unbalanced braces) yields a ParseFault and a
        // null baseline, no exception. Fails if a broken baseline crashes triage.
        [Fact]
        public void Load_CorruptFile_ParseFault_NullBaseline()
        {
            string path = WriteRaw("baseline.cfg", "baselineFormatVersion = 1\nENTRY\n{\n  ruleId = X\n"); // no closing brace
            (AnalysisBaseline baseline, List<BaselineLoadFault> faults) = BaselineCodec.Load(path);

            Assert.Null(baseline);
            Assert.Contains(faults, f => f.Kind == BaselineFaultKind.ParseFault);
        }

        // Guards: an ENTRY missing ruleId is dropped with an EntryMalformed fault; the
        // sibling entry still loads. Fails if one bad entry voids the whole baseline.
        [Fact]
        public void Load_MalformedEntry_Dropped_SiblingApplies()
        {
            string path = WriteRaw("baseline.cfg",
                "baselineFormatVersion = 1\n"
                + "createdAtAnalyzerVersion = 2\n"
                + "ENTRY\n{\n  target = recBad\n  sectionIndex = 0\n  messageDigest = d\n  capturedLevel = FAIL\n}\n"
                + "ENTRY\n{\n  ruleId = INV2-NO-DOUBLE-COVER\n  target = recGood\n  sectionIndex = 1\n  messageDigest = d2\n  capturedLevel = FAIL\n}\n");

            (AnalysisBaseline baseline, List<BaselineLoadFault> faults) = BaselineCodec.Load(path);

            Assert.NotNull(baseline);
            Assert.Single(baseline.Entries);
            Assert.Equal("recGood", baseline.Entries[0].Target);
            Assert.Contains(faults, f => f.Kind == BaselineFaultKind.EntryMalformed && f.Target == "recBad");
        }

        // Guards: two ENTRY nodes with an identical BaselineKey collapse to one in the
        // index and emit a DuplicateEntry fault. Fails if a duplicate throws on index
        // build or is double-counted.
        [Fact]
        public void Load_DuplicateEntries_DedupeWithFault()
        {
            string entry = "ENTRY\n{\n  ruleId = INV2\n  target = rec\n  sectionIndex = 3\n  messageDigest = d\n  capturedLevel = FAIL\n}\n";
            string path = WriteRaw("baseline.cfg", "baselineFormatVersion = 1\n" + entry + entry);

            (AnalysisBaseline baseline, List<BaselineLoadFault> faults) = BaselineCodec.Load(path);

            Assert.NotNull(baseline);
            Assert.Single(baseline.Entries);
            Assert.Single(baseline.BuildIndex());
            Assert.Contains(faults, f => f.Kind == BaselineFaultKind.DuplicateEntry);
        }

        // Guards: a future baselineFormatVersion yields a VersionFuture fault + null
        // baseline. Fails if a future baseline is mis-applied.
        [Fact]
        public void Load_FutureFormat_VersionFuture_NullBaseline()
        {
            string path = WriteRaw("baseline.cfg", "baselineFormatVersion = 99\n");
            (AnalysisBaseline baseline, List<BaselineLoadFault> faults) = BaselineCodec.Load(path);

            Assert.Null(baseline);
            Assert.Contains(faults, f => f.Kind == BaselineFaultKind.VersionFuture);
        }

        // Guards: a missing file returns (null, empty) so the caller drives NOT-FOUND
        // (never a fault).
        [Fact]
        public void Load_MissingFile_NullBaseline_NoFaults()
        {
            (AnalysisBaseline baseline, List<BaselineLoadFault> faults) =
                BaselineCodec.Load(Path.Combine(tempDir, "absent.cfg"));
            Assert.Null(baseline);
            Assert.Empty(faults);
        }

        // Guards: a hand-authored file WITH the PARSEK_ANALYSIS_BASELINE wrapper (as
        // the design example shows) still loads (Load unwraps the child).
        [Fact]
        public void Load_WrappedByHand_StillParses()
        {
            string path = WriteRaw("baseline.cfg",
                "PARSEK_ANALYSIS_BASELINE\n{\n"
                + "  baselineFormatVersion = 1\n"
                + "  ENTRY\n  {\n    ruleId = INV2\n    target = rec\n    sectionIndex = 3\n    messageDigest = d\n    capturedLevel = FAIL\n  }\n"
                + "}\n");
            (AnalysisBaseline baseline, List<BaselineLoadFault> faults) = BaselineCodec.Load(path);

            Assert.NotNull(baseline);
            Assert.Single(baseline.Entries);
            Assert.Empty(faults);
        }

        // Guards: FromReport then Write then Load yields entries whose keys match the
        // original findings. Fails if write/read drops or reshapes a key component.
        [Fact]
        public void RoundTrip_BuildWriteLoad_KeysMatchOriginalFindings()
        {
            var fail = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec1", 3,
                "INV2 overlap recording=rec1 a=[100,200] b=[150,250]");
            var warn = F("INV2-UNCOVERED-SPAN", VerdictLevel.Warn, "rec2", 1,
                "INV2 uncovered recording=rec2 span=[47.9,48.0] orbitBridged=False");
            var info = F("INV8-LEDGER", VerdictLevel.Info, "s", -1, "reconstruction-not-available"); // dropped (INFO)
            AnalysisReport report = Report(fail, warn, info);

            string path = Path.Combine(tempDir, "baseline.cfg");
            BaselineBuilder.BuildAndWrite(report, null, keepStale: false, path);

            (AnalysisBaseline baseline, List<BaselineLoadFault> faults) = BaselineCodec.Load(path);
            Assert.Empty(faults);
            Assert.Equal("2", baseline.CreatedAtAnalyzerVersion);
            Assert.Equal(4, baseline.SubjectSchemaGeneration);

            Dictionary<BaselineKey, BaselineEntry> index = baseline.BuildIndex();
            Assert.True(index.ContainsKey(BaselineFilter.KeyOf(fail)));
            Assert.True(index.ContainsKey(BaselineFilter.KeyOf(warn)));
            Assert.False(index.ContainsKey(BaselineFilter.KeyOf(info))); // INFO not captured
            Assert.Equal(2, baseline.Entries.Count);
        }

        // Guards: a placeholder-Target FAIL is skipped by the builder (never written)
        // and surfaced on the skip count.
        [Fact]
        public void Build_PlaceholderTarget_SkippedNotWritten()
        {
            var tomb = F("INV7-DANGLING-TOMBSTONE", VerdictLevel.Fail, "<tombstone>", -1, "INV7 dangling");
            var real = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec", 0, "INV2 overlap a=[1,2] b=[1,2]");

            BaselineBuilder.BaselineBuildResult result =
                BaselineBuilder.FromReport(Report(tomb, real), null, keepStale: false);

            Assert.Equal(1, result.SkippedPlaceholder);
            Assert.Single(result.Baseline.Entries);
            Assert.Equal("rec", result.Baseline.Entries[0].Target);
        }

        // Guards (design "Update preserves reasons, prunes stale, keeps immutable"): a
        // second build after one finding removed + one added retains the survivor's
        // hand reason, prunes the removed one, adds the new one, keeps an immutable
        // still-firing entry. Fails if update loses a human reason or prunes a
        // still-matching entry.
        [Fact]
        public void Update_PreservesReasons_PrunesStale_KeepsStillMatching()
        {
            var immutable = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "recImm", 3,
                "INV2 overlap recording=recImm a=[100,200] b=[150,250]");
            var removed = F("INV9-REWINDPOINT", VerdictLevel.Fail, "recGone", -1, "INV9 missing rewindId=x");

            // First build: both present; hand-edit the reasons afterward.
            BaselineBuilder.BaselineBuildResult first =
                BaselineBuilder.FromReport(Report(immutable, removed), null, keepStale: false);
            var handEdited = new AnalysisBaseline
            {
                Entries = first.Baseline.Entries.Select(e =>
                    new BaselineEntry(e.RuleId, e.Target, e.SectionIndex, e.MessageDigest, e.CapturedLevel,
                        "HUMAN: " + e.Target)).ToList(),
                Reason = "hand reason",
            };

            // Second build: `removed` gone, `added` new, `immutable` still firing.
            var added = F("INV1-UT-MONOTONIC", VerdictLevel.Fail, "recNew", 0, "back-step at ut");
            BaselineBuilder.BaselineBuildResult second =
                BaselineBuilder.FromReport(Report(immutable, added), handEdited, keepStale: false);

            Dictionary<BaselineKey, BaselineEntry> idx = second.Baseline.BuildIndex();

            // Immutable still-firing entry survived and kept its human reason.
            Assert.True(idx.TryGetValue(BaselineFilter.KeyOf(immutable), out BaselineEntry keptImm));
            Assert.Equal("HUMAN: recImm", keptImm.Reason);

            // The removed entry was pruned.
            Assert.False(idx.ContainsKey(BaselineFilter.KeyOf(removed)));
            Assert.Equal(1, second.PrunedCount);

            // The new finding was added.
            Assert.True(idx.ContainsKey(BaselineFilter.KeyOf(added)));
            Assert.Equal(1, second.NewCount);
            Assert.Equal(1, second.PreservedCount);
        }

        // Guards: -KeepStaleBaselineEntries retains a momentarily-unmatched entry.
        [Fact]
        public void Update_KeepStale_RetainsUnmatchedEntry()
        {
            var removed = F("INV9-REWINDPOINT", VerdictLevel.Fail, "recGone", -1, "INV9 missing rewindId=x");
            var existing = BaselineBuilder.FromReport(Report(removed), null, keepStale: false).Baseline;

            var live = F("INV2", VerdictLevel.Warn, "rec", 0, "warn");
            BaselineBuilder.BaselineBuildResult result =
                BaselineBuilder.FromReport(Report(live), existing, keepStale: true);

            Assert.Equal(1, result.KeptStaleCount);
            Assert.True(result.Baseline.BuildIndex().ContainsKey(BaselineFilter.KeyOf(removed)));
        }
    }
}
