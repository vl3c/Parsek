using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Pure matching + gate-semantics tests for BaselineFilter (design doc
    // docs/dev/design-autotest-findings-baseline.md "Test Plan"). Pure over
    // hand-built Finding lists / AnalysisReports; no file I/O, no statics beyond
    // ParsekLog (suppressed here), so no Sequential collection is needed.
    public class BaselineFilterTests
    {
        private static Finding F(string ruleId, VerdictLevel level, string target, int section, string message)
        {
            return new Finding(ruleId, level, target, section, message, "cited");
        }

        private static BaselineEntry EntryFor(Finding f, VerdictLevel captured, string reason = "known")
        {
            BaselineKey k = BaselineFilter.KeyOf(f);
            return new BaselineEntry(k.RuleId, k.Target, k.SectionIndex, k.MessageDigest, captured, reason);
        }

        private static AnalysisReport Report(params Finding[] findings)
        {
            var report = new AnalysisReport
            {
                SaveName = "s",
                Findings = new List<Finding>(findings),
            };
            report.Counts = Counts.From(report.Findings);
            return report;
        }

        private static AnalysisBaseline Baseline(params BaselineEntry[] entries)
        {
            return new AnalysisBaseline { Entries = new List<BaselineEntry>(entries) };
        }

        // --- Pure matching logic ---

        // Guards: the digest masks numeric literals but keeps the message skeleton.
        // Fails if the mask is too aggressive (collapses distinct findings) or too
        // weak (churns on value drift).
        [Fact]
        public void Digest_MasksNumerics_KeepsSkeleton()
        {
            string a = BaselineFilter.NormalizeMessageDigest(
                "INV2 overlap recording=corpus0 a=[100,200] b=[150,250]");
            string b = BaselineFilter.NormalizeMessageDigest(
                "INV2 overlap recording=corpus0 a=[14031.6,15044.7] b=[14031.6,15044.7]");
            // Same structure, different values -> same digest (stable against drift).
            Assert.Equal(a, b);
            Assert.Equal("INV# overlap recording=corpus# a=[#,#] b=[#,#]", a);

            // Different field name -> different digest (distinguishes structure).
            Assert.NotEqual(
                BaselineFilter.NormalizeMessageDigest("INV7 dangling field=ParentRecordingId"),
                BaselineFilter.NormalizeMessageDigest("INV7 dangling field=SupersedeTargetId"));
        }

        // Guards: a GUID-hex id keeps its letters, so two different ids yield two
        // different digests and are never conflated.
        [Fact]
        public void Digest_GuidHexIds_StayDistinct()
        {
            string a = BaselineFilter.NormalizeMessageDigest("dangling id=60f182f1d20f4ea3abcb906373b9631a");
            string b = BaselineFilter.NormalizeMessageDigest("dangling id=a574fbe8aa5d4756b2ab27d7dc82ebdd");
            Assert.NotEqual(a, b);
        }

        // Guards: key equality is all-four ordinal; a difference in any one component
        // does not match. Fails if the key drops a component and over/under-matches.
        [Fact]
        public void Key_EqualityIsAllFourOrdinal()
        {
            var k = new BaselineKey("INV2", "rec", 3, "d");
            Assert.Equal(k, new BaselineKey("INV2", "rec", 3, "d"));
            Assert.NotEqual(k, new BaselineKey("INV1", "rec", 3, "d"));
            Assert.NotEqual(k, new BaselineKey("INV2", "REC", 3, "d"));
            Assert.NotEqual(k, new BaselineKey("INV2", "rec", 4, "d"));
            Assert.NotEqual(k, new BaselineKey("INV2", "rec", 3, "e"));
        }

        // Guards: the same finding analyzed twice produces identical keys. Fails if a
        // key component picks up run-to-run state (which would un-baseline the known
        // five every run).
        [Fact]
        public void Key_SameFindingTwice_IsStable()
        {
            var f = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec7", 3,
                "INV2 overlap recording=rec7 a=[100,200] b=[150,250]");
            Assert.Equal(BaselineFilter.KeyOf(f), BaselineFilter.KeyOf(f));
        }

        // Guards: a recording-scoped finding (section -1) baselines correctly. Fails
        // if -1 is mishandled as "no section" and mis-keyed.
        [Fact]
        public void Match_SectionMinusOne_Baselines()
        {
            var f = F("INV9-REWINDPOINT", VerdictLevel.Fail, "rec", -1, "INV9 missing rewindId=parsek_rw_3c4e39");
            var report = Report(f);
            BaselineFilter.Apply(report, Baseline(EntryFor(f, VerdictLevel.Fail)),
                BaselineMode.Apply, true, null);
            Assert.True(report.Findings[0].Baselined);
            Assert.False(report.IsRed);
        }

        // Guards: an entry captured at WARN does NOT baseline a now-FAIL finding with
        // the same key; a SEVERITY-ESCALATED WARN is emitted and the FAIL surfaces.
        // Fails if a stale low-severity baseline silently masks a promoted FAIL.
        [Fact]
        public void Match_SeverityEscalation_NotMaskedAndSurfaces()
        {
            var f = F("INV3-ABSOLUTE-RANGE", VerdictLevel.Fail, "rec", 0, "INV3 out of range lat=95");
            var report = Report(f);
            // Captured at WARN, now FAIL.
            BaselineFilter.Apply(report, Baseline(EntryFor(f, VerdictLevel.Warn)),
                BaselineMode.Apply, true, null);

            Assert.False(report.Findings.First(x => x.RuleId == "INV3-ABSOLUTE-RANGE").Baselined);
            Assert.True(report.IsRed); // the FAIL still reds
            Assert.Contains(report.Findings, x => x.RuleId == BaselineFilter.SeverityEscalatedRuleId);
        }

        // --- Gate semantics ---

        // Guards: a baselined FAIL + a non-baselined FAIL -> IsRed true. Fails if a
        // baselined finding wrongly suppresses the gate for unrelated reds.
        [Fact]
        public void Gate_NonBaselinedFail_Reds()
        {
            var known = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec", 1, "INV2 overlap a=[1,2] b=[1,2]");
            var fresh = F("INV1-UT-MONOTONIC", VerdictLevel.Fail, "recNew", 0, "back-step");
            var report = Report(known, fresh);
            BaselineFilter.Apply(report, Baseline(EntryFor(known, VerdictLevel.Fail)),
                BaselineMode.Apply, true, null);

            Assert.Equal(1, report.Counts.Baselined);
            Assert.Equal(1, report.Counts.FailNonBaselined);
            Assert.True(report.IsRed);
        }

        // Guards: all five INV2 FAILs baselined -> IsRed false, findings STILL present
        // (never silently suppressed). Fails if a fully-baselined save reds or its
        // findings vanish.
        [Fact]
        public void Gate_AllBaselined_GoesGreen_FindingsPresent()
        {
            var findings = new List<Finding>();
            var entries = new List<BaselineEntry>();
            for (int i = 0; i < 5; i++)
            {
                var f = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec" + i, i,
                    "INV2 overlap recording=rec" + i + " a=[100,200] b=[150,250]");
                findings.Add(f);
                entries.Add(EntryFor(f, VerdictLevel.Fail));
            }
            var report = Report(findings.ToArray());
            BaselineFilter.Apply(report, Baseline(entries.ToArray()), BaselineMode.Apply, true, null);

            Assert.False(report.IsRed);
            Assert.Equal(5, report.Counts.Baselined);
            Assert.Equal(5, report.Findings.Count(x => x.RuleId == "INV2-NO-DOUBLE-COVER"));
        }

        // Guards: a baselined WARN is green either way and marked baselined. Fails if
        // baselining a WARN is rejected or changes IsRed.
        [Fact]
        public void Gate_BaselinedWarn_Unaffected()
        {
            var f = F("INV9-MISSING-REWIND", VerdictLevel.Warn, "rec", -1, "INV9 benign missing rewind");
            var report = Report(f);
            BaselineFilter.Apply(report, Baseline(EntryFor(f, VerdictLevel.Warn)),
                BaselineMode.Apply, true, null);
            Assert.True(report.Findings[0].Baselined);
            Assert.False(report.IsRed);
        }

        // Guards: STALE stays strict. Apply over a stamped subject is refused wholesale
        // with BASELINE-REFUSED-STAMPED and the STALE stays non-baselined / red. Fails
        // if a STALE is baselinable.
        [Fact]
        public void Gate_StaleFixture_ApplyRefusedOnStampedSubject()
        {
            var stale = F("FIXTURE-STALE", VerdictLevel.StaleFixture, "rec", -1, "FIXTURE stale-fixture");
            var report = Report(stale);
            report.SubjectIsStampedFixture = true;
            BaselineFilter.Apply(report, Baseline(EntryFor(stale, VerdictLevel.StaleFixture)),
                BaselineMode.Apply, true, null);

            Assert.False(report.Findings.First(x => x.RuleId == "FIXTURE-STALE").Baselined);
            Assert.True(report.IsRed);
            Assert.Contains(report.Findings, x => x.RuleId == BaselineFilter.RefusedStampedRuleId);
        }

        // Guards: even on an UNstamped subject, a StaleFixture-level finding is never
        // baselined (the second, independent protection).
        [Fact]
        public void Gate_StaleFixtureLevel_NeverBaselined_EvenUnstamped()
        {
            var stale = F("FIXTURE-STALE", VerdictLevel.StaleFixture, "rec", -1, "FIXTURE stale-fixture");
            var report = Report(stale);
            report.SubjectIsStampedFixture = false;
            BaselineFilter.Apply(report, Baseline(EntryFor(stale, VerdictLevel.StaleFixture)),
                BaselineMode.Apply, true, null);
            Assert.False(report.Findings[0].Baselined);
            Assert.True(report.IsRed);
        }

        // --- Fresh-save refusal (structural guard) ---

        // Guards: Forbid over a save carrying a baseline yields a BASELINE-FORBIDDEN
        // FAIL and applies nothing. Fails if a fresh mission save could be softened.
        [Fact]
        public void Forbid_BaselinePresent_Fails()
        {
            var f = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec", 1, "INV2 overlap a=[1,2] b=[1,2]");
            var report = Report(f);
            BaselineFilter.Apply(report, Baseline(EntryFor(f, VerdictLevel.Fail)),
                BaselineMode.Forbid, true, null);

            Assert.Contains(report.Findings, x => x.RuleId == BaselineFilter.ForbiddenRuleId
                && x.Level == VerdictLevel.Fail);
            Assert.False(report.Findings[0].Baselined); // nothing applied
            Assert.True(report.IsRed);
        }

        // Guards: Forbid over a baseline-free save is a green no-op.
        [Fact]
        public void Forbid_NoBaseline_Clean()
        {
            var report = Report(F("INV2", VerdictLevel.Warn, "rec", 0, "warn"));
            BaselineFilter.Apply(report, null, BaselineMode.Forbid, false, null);
            Assert.False(report.IsRed);
            Assert.DoesNotContain(report.Findings, x => x.RuleId == BaselineFilter.ForbiddenRuleId);
        }

        // Guards: Ignore over a save WITH a baseline leaves every red red and emits
        // BASELINE-PRESENT-NOT-APPLIED. Fails if the default mode ever consults a
        // baseline (WriteBaseline's internal Ignore pass must see the TRUE findings).
        [Fact]
        public void Ignore_BaselinePresent_NeverApplies()
        {
            var f = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec", 1, "INV2 overlap a=[1,2] b=[1,2]");
            var report = Report(f);
            BaselineFilter.Apply(report, Baseline(EntryFor(f, VerdictLevel.Fail)),
                BaselineMode.Ignore, true, null);

            Assert.False(report.Findings[0].Baselined);
            Assert.True(report.IsRed);
            Assert.Contains(report.Findings, x => x.RuleId == BaselineFilter.PresentNotAppliedRuleId);
        }

        // --- Meta-findings ---

        // Guards: one entry matches multiple findings -> the FIRST is baselined, the
        // surplus stays unbaselined and gates, BASELINE-MULTI-MATCH WARN surfaces.
        [Fact]
        public void MultiMatch_FirstBaselined_SurplusGates()
        {
            // Two findings that digest-collapse to the same key (values differ).
            var f1 = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec", 3, "INV2 overlap a=[100,200] b=[150,250]");
            var f2 = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec", 3, "INV2 overlap a=[300,400] b=[350,450]");
            var report = Report(f1, f2);
            BaselineFilter.Apply(report, Baseline(EntryFor(f1, VerdictLevel.Fail)),
                BaselineMode.Apply, true, null);

            int baselined = report.Findings.Count(x => x.RuleId == "INV2-NO-DOUBLE-COVER" && x.Baselined);
            Assert.Equal(1, baselined);
            Assert.True(report.IsRed); // the surplus still gates
            Assert.Contains(report.Findings, x => x.RuleId == BaselineFilter.MultiMatchRuleId);
        }

        // Guards: a placeholder-Target finding is never baselined (always gates), even
        // with a matching entry.
        [Fact]
        public void PlaceholderTarget_NeverBaselined()
        {
            var f = F("INV7-DANGLING-TOMBSTONE", VerdictLevel.Fail, "<tombstone>", -1, "INV7 dangling");
            var report = Report(f);
            BaselineFilter.Apply(report, Baseline(EntryFor(f, VerdictLevel.Fail)),
                BaselineMode.Apply, true, null);
            Assert.False(report.Findings[0].Baselined);
            Assert.True(report.IsRed);
        }

        // Guards: Apply requested but no baseline file present -> BASELINE-NOT-FOUND
        // INFO; every finding surfaces (behaves as Ignore). Not a fault.
        [Fact]
        public void Apply_NoBaselineFile_NotFoundInfo_EverythingSurfaces()
        {
            var f = F("INV2", VerdictLevel.Fail, "rec", 0, "overlap");
            var report = Report(f);
            BaselineFilter.Apply(report, null, BaselineMode.Apply, false, null);
            Assert.True(report.IsRed);
            Assert.Contains(report.Findings, x => x.RuleId == BaselineFilter.NotFoundRuleId
                && x.Level == VerdictLevel.Info);
        }

        // Guards: an entry that matched nothing this run -> BASELINE-STALE-ENTRY INFO;
        // run stays green.
        [Fact]
        public void StaleEntry_MatchesNothing_InfoAndGreen()
        {
            var live = F("INV2", VerdictLevel.Warn, "rec", 0, "warn"); // WARN never reds
            var goneEntry = new BaselineEntry("INV5", "recOld", 2, "gone digest", VerdictLevel.Fail, "was here");
            var report = Report(live);
            BaselineFilter.Apply(report, Baseline(goneEntry), BaselineMode.Apply, true, null);

            Assert.False(report.IsRed);
            Assert.Contains(report.Findings, x => x.RuleId == BaselineFilter.StaleEntryRuleId
                && x.Level == VerdictLevel.Info);
        }

        // Guards: a HARD parse fault reds the run via BASELINE-PARSE-FAULT FAIL and no
        // matching is applied.
        [Fact]
        public void ParseFault_RedsAndAppliesNothing()
        {
            var known = F("INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec", 1, "INV2 overlap a=[1,2] b=[1,2]");
            var report = Report(known);
            var faults = new List<BaselineLoadFault>
            {
                new BaselineLoadFault(BaselineFaultKind.ParseFault, "path", "unbalanced-braces"),
            };
            BaselineFilter.Apply(report, null, BaselineMode.Apply, true, faults);

            Assert.False(report.Findings[0].Baselined);
            Assert.True(report.IsRed);
            Assert.Contains(report.Findings, x => x.RuleId == BaselineFilter.ParseFaultRuleId
                && x.Level == VerdictLevel.Fail);
        }

        // Guards: determinism - applying to two identical reports yields identical
        // sorted finding sets (meta-finding order is deterministic).
        [Fact]
        public void Apply_IsDeterministic()
        {
            List<string> Run()
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
                // Add two stale entries whose sorted emission order must be stable.
                entries.Add(new BaselineEntry("INV5", "zzz", 0, "d1", VerdictLevel.Fail, "r"));
                entries.Add(new BaselineEntry("INV5", "aaa", 0, "d2", VerdictLevel.Fail, "r"));
                var report = Report(findings.ToArray());
                BaselineFilter.Apply(report, Baseline(entries.ToArray()), BaselineMode.Apply, true, null);
                return ReportWriter.SortFindings(report.Findings)
                    .Select(x => x.RuleId + "|" + x.Target + "|" + x.SectionIndex + "|" + x.Baselined).ToList();
            }

            Assert.Equal(Run(), Run());
        }
    }
}
