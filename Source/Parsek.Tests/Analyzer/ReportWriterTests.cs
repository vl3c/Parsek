using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Parsek;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Guards the frozen report-format contract (design doc "Report format stability
    // tests"). The harness parses .analysis.json and greps .analysis.txt, so any
    // silent drift here breaks a downstream consumer.
    public class ReportWriterTests : IDisposable
    {
        private readonly string tempDir;

        public ReportWriterTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-report-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static AnalysisReport SampleReport(List<Finding> findings)
        {
            return new AnalysisReport
            {
                SaveName = "testsave",
                SubjectSchemaGeneration = 4,
                Findings = findings,
                Counts = Counts.From(findings),
            };
        }

        private static List<Finding> TwoFindings()
        {
            return new List<Finding>
            {
                new Finding("INV1-UT-MONOTONIC", VerdictLevel.Fail, "rec-b", 2,
                    "back-step at ut", "TrajectoryPoint.ut"),
                new Finding("INV2-NO-DOUBLE-COVER", VerdictLevel.Warn, "rec-a", -1,
                    "uncovered span", "TrackSection"),
            };
        }

        // Guards: writing the same model twice produces byte-identical .analysis.json
        // (no timestamps, no absolute paths, no dictionary order leak). If a wall
        // clock or a HashSet iteration leaked in, CI report diffing would be useless.
        [Fact]
        public void Write_SameModelTwice_ProducesByteIdenticalJson()
        {
            string dir1 = Path.Combine(tempDir, "run1");
            string dir2 = Path.Combine(tempDir, "run2");

            ReportWriter.Write(SampleReport(TwoFindings()), dir1);
            ReportWriter.Write(SampleReport(TwoFindings()), dir2);

            byte[] a = File.ReadAllBytes(Path.Combine(dir1, "testsave.analysis.json"));
            byte[] b = File.ReadAllBytes(Path.Combine(dir2, "testsave.analysis.json"));
            Assert.Equal(a, b);
        }

        // Guards: the JSON is order-independent of the input finding list, because
        // the writer sorts. Two identical finding SETS in different order must
        // serialize identically.
        [Fact]
        public void BuildJson_IsInputOrderIndependent()
        {
            var forward = TwoFindings();
            var reversed = new List<Finding>(forward);
            reversed.Reverse();

            Assert.Equal(
                ReportWriter.BuildJson(SampleReport(forward)),
                ReportWriter.BuildJson(SampleReport(reversed)));
        }

        // Guards: the deterministic total order is (level desc, ruleId, target,
        // sectionIndex). A sort regression would churn every downstream diff.
        [Fact]
        public void SortFindings_OrdersByLevelDescThenRuleIdThenTargetThenSection()
        {
            var findings = new List<Finding>
            {
                new Finding("INV2", VerdictLevel.Info, "z", 0, "m", "c"),
                new Finding("INV2", VerdictLevel.Fail, "b", 1, "m", "c"),
                new Finding("INV2", VerdictLevel.Fail, "b", 0, "m", "c"),
                new Finding("INV1", VerdictLevel.Fail, "b", 5, "m", "c"),
                new Finding("INV9", VerdictLevel.StaleFixture, "a", 0, "m", "c"),
                new Finding("INV2", VerdictLevel.Fail, "a", 9, "m", "c"),
            };

            var sorted = ReportWriter.SortFindings(findings);

            // StaleFixture first (level desc).
            Assert.Equal("INV9", sorted[0].RuleId);
            Assert.Equal(VerdictLevel.StaleFixture, sorted[0].Level);
            // Then Fail block, ordered by ruleId: INV1 before INV2.
            Assert.Equal("INV1", sorted[1].RuleId);
            // Within INV2 Fails: target asc (a before b), then sectionIndex asc.
            Assert.Equal("INV2", sorted[2].RuleId);
            Assert.Equal("a", sorted[2].Target);
            Assert.Equal("INV2", sorted[3].RuleId);
            Assert.Equal("b", sorted[3].Target);
            Assert.Equal(0, sorted[3].SectionIndex);
            Assert.Equal("b", sorted[4].Target);
            Assert.Equal(1, sorted[4].SectionIndex);
            // Info last.
            Assert.Equal(VerdictLevel.Info, sorted[5].Level);
        }

        // Guards: the exact .analysis.json schema. Changing a field name, order, or
        // the AnalyzerVersion without a deliberate bump fails this golden.
        [Fact]
        public void BuildJson_MatchesGoldenSchema()
        {
            string golden = string.Join("\n", new[]
            {
                "{",
                "  \"analyzerVersion\": \"3\",",
                "  \"saveName\": \"testsave\",",
                "  \"subjectSchemaGeneration\": 4,",
                "  \"counts\": {",
                "    \"fail\": 1,",
                "    \"warn\": 1,",
                "    \"info\": 0,",
                "    \"staleFixture\": 0,",
                "    \"baselined\": 0,",
                "    \"failNonBaselined\": 1,",
                "    \"staleNonBaselined\": 0",
                "  },",
                "  \"findings\": [",
                "    {",
                "      \"ruleId\": \"INV1-UT-MONOTONIC\",",
                "      \"level\": \"FAIL\",",
                "      \"target\": \"rec-b\",",
                "      \"sectionIndex\": 2,",
                "      \"message\": \"back-step at ut\",",
                "      \"citedContract\": \"TrajectoryPoint.ut\",",
                "      \"baselined\": false",
                "    },",
                "    {",
                "      \"ruleId\": \"INV2-NO-DOUBLE-COVER\",",
                "      \"level\": \"WARN\",",
                "      \"target\": \"rec-a\",",
                "      \"sectionIndex\": -1,",
                "      \"message\": \"uncovered span\",",
                "      \"citedContract\": \"TrackSection\",",
                "      \"baselined\": false",
                "    }",
                "  ],",
                // Additive careerSave block (module M-B2). SampleReport carries no
                // CareerSave, so the block is the always-emitted parsed:false shape.
                "  \"careerSave\": {",
                "    \"parsed\": false",
                "  }",
                "}",
                "",
            });

            Assert.Equal(golden, ReportWriter.BuildJson(SampleReport(TwoFindings())));
        }

        // Guards: empty findings still emits a valid, stable JSON with an empty array.
        [Fact]
        public void BuildJson_NoFindings_EmitsEmptyArray()
        {
            string json = ReportWriter.BuildJson(SampleReport(new List<Finding>()));
            Assert.Contains("\"findings\": []", json);
        }

        // Guards: the human header line + per-finding line format that grep-based
        // triage scripts key on.
        [Fact]
        public void BuildHumanSummary_HeaderAndFindingLines_MatchContract()
        {
            string summary = ReportWriter.BuildHumanSummary(SampleReport(TwoFindings()));
            string[] lines = summary.Split('\n');

            Assert.Equal(
                "[Analyzer] save=testsave generation=4 FAIL=1 WARN=1 INFO=0 STALE=0 BASELINED=0 RED=1",
                lines[0]);
            // Section-scoped finding carries #section.
            Assert.Equal(
                "FAIL INV1-UT-MONOTONIC target=rec-b#2 back-step at ut",
                lines[1]);
            // Non-section finding omits the #section suffix.
            Assert.Equal(
                "WARN INV2-NO-DOUBLE-COVER target=rec-a uncovered span",
                lines[2]);
        }

        // Guards: string escaping so a message with a quote/backslash cannot break
        // the JSON the harness parses.
        [Fact]
        public void BuildJson_EscapesSpecialCharactersInStrings()
        {
            var findings = new List<Finding>
            {
                new Finding("INV1", VerdictLevel.Fail, "rec\"x", -1,
                    "line\\path\tand\"quote", "C"),
            };
            string json = ReportWriter.BuildJson(SampleReport(findings));

            Assert.Contains("\"target\": \"rec\\\"x\"", json);
            Assert.Contains("\"message\": \"line\\\\path\\tand\\\"quote\"", json);
        }

        private static Finding Baselined(Finding f)
        {
            f.Baselined = true;
            return f;
        }

        private static List<Finding> FiveBaselinedFails()
        {
            var list = new List<Finding>();
            for (int i = 0; i < 5; i++)
            {
                list.Add(Baselined(new Finding(
                    "INV2-NO-DOUBLE-COVER", VerdictLevel.Fail, "rec" + i, i,
                    "INV2 overlap recording=rec" + i + " a=[100,200] b=[150,250]",
                    "RecordingOptimizer.IsSplittableEnvOrBodyBoundary")));
            }
            return list;
        }

        // Guards (design "Baselined flag + counts"): a report with 5 baselined FAILs
        // shows Fail==5, Baselined==5, FailNonBaselined==0, each finding Baselined==true,
        // and the JSON carries the flag + baselined=5 + failNonBaselined=0. Fails if a
        // baselined finding is dropped from the report (silent suppression, the
        // forbidden behavior) or its flag is lost.
        [Fact]
        public void Baselined_FiveFails_CountsSplitAndJsonCarryFlag()
        {
            AnalysisReport report = SampleReport(FiveBaselinedFails());

            Assert.Equal(5, report.Counts.Fail);
            Assert.Equal(5, report.Counts.Baselined);
            Assert.Equal(0, report.Counts.FailNonBaselined);
            Assert.False(report.IsRed);
            Assert.All(report.Findings, f => Assert.True(f.Baselined));

            string json = ReportWriter.BuildJson(report);
            Assert.Contains("\"baselined\": 5", json);
            Assert.Contains("\"failNonBaselined\": 0", json);
            Assert.Contains("\"baselined\": true", json);
            // Every finding line still present: nothing silently suppressed.
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(json, "\"ruleId\"").Count);
        }

        // Guards (design "Human summary contract"): the header carries the terminal
        // RED token, a five-baselined-FAIL report is RED=0, and every baselined line
        // carries the [baselined] suffix. Fails if a grep-based triage script breaks
        // on format drift or RED disagrees with the non-baselined splits.
        [Fact]
        public void HumanSummary_AllBaselined_IsRed0_WithBaselinedSuffix()
        {
            string summary = ReportWriter.BuildHumanSummary(SampleReport(FiveBaselinedFails()));
            string[] lines = summary.Split('\n');

            Assert.EndsWith(" RED=0", lines[0]);
            Assert.Contains("BASELINED=5", lines[0]);
            // Every finding line is a baselined FAIL and carries the suffix.
            for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
                Assert.EndsWith(" [baselined]", lines[i]);
        }

        // Guards (design "Human summary contract"): one extra NON-baselined FAIL on
        // top of the five baselined FAILs flips the terminal token to RED=1. Fails if
        // a baselined finding wrongly suppresses the gate for an unrelated red.
        [Fact]
        public void HumanSummary_OneExtraNonBaselinedFail_IsRed1()
        {
            var findings = FiveBaselinedFails();
            findings.Add(new Finding("INV1-UT-MONOTONIC", VerdictLevel.Fail, "recNew", 0,
                "back-step at ut", "TrajectoryPoint.ut"));

            AnalysisReport report = SampleReport(findings);
            Assert.True(report.IsRed);

            string header = ReportWriter.BuildHumanSummary(report).Split('\n')[0];
            Assert.EndsWith(" RED=1", header);
            Assert.Contains("FAIL=6", header);
            Assert.Contains("BASELINED=5", header);
        }

        // Guards: IsRed policy - any NON-baselined FAIL or STALE is red; WARN/INFO
        // alone is green.
        [Fact]
        public void IsRed_TrueOnFailOrStale_FalseOnWarnInfoOnly()
        {
            Assert.True(SampleReport(new List<Finding>
            {
                new Finding("INV1", VerdictLevel.Fail, "t", -1, "m", "c"),
            }).IsRed);

            Assert.True(SampleReport(new List<Finding>
            {
                new Finding("FIX", VerdictLevel.StaleFixture, "t", -1, "m", "c"),
            }).IsRed);

            Assert.False(SampleReport(new List<Finding>
            {
                new Finding("INV2", VerdictLevel.Warn, "t", -1, "m", "c"),
                new Finding("INV6", VerdictLevel.Info, "t", -1, "m", "c"),
            }).IsRed);
        }

        // ---- careerSave export block (module M-B2, the ledger-oracle produced-save
        // leg). The harness's Python ledger-oracle verifier parses this block, so the
        // field names / nesting / absent-vs-flag semantics are a frozen contract. ----

        private static AnalysisReport ReportWith(CareerSaveSnapshot career, List<Finding> findings)
        {
            return new AnalysisReport
            {
                SaveName = "testsave",
                SubjectSchemaGeneration = 4,
                Findings = findings ?? new List<Finding>(),
                Counts = Counts.From(findings ?? new List<Finding>()),
                CareerSave = career,
            };
        }

        // A fully populated career snapshot with collections deliberately inserted in
        // NON-sorted order, so the export's deterministic key-sort is exercised.
        private static CareerSaveSnapshot PopulatedCareer()
        {
            var cs = new CareerSaveSnapshot
            {
                Parsed = true,
                HasFunds = true,
                Funds = 25000.0,
                HasScience = true,
                SciencePool = 0.0,
                HasRep = true,
                Reputation = 0.0,
            };
            // Insertion order is reversed from sorted order on purpose.
            cs.SubjectScience["z@X"] = 2.5;
            cs.SubjectScience["a@Y"] = 1.0;
            cs.FacilityLevelFrac["SpaceCenter/LaunchPad"] = 0.0;
            cs.ActiveContractGuids.Add("guid-b");
            cs.ActiveContractGuids.Add("guid-a");
            cs.CompletedMilestoneIds.Add("FirstLaunch");
            cs.Vessels.Add(new SaveVessel
            {
                Pid = "v-guid",
                PersistentId = 100000,
                Name = "X",
                Type = "Ship",
                ResourceTotals = new Dictionary<string, double>
                {
                    ["Oxidizer"] = 110.0,
                    ["LiquidFuel"] = 90.0,
                },
            });
            return cs;
        }

        // Guards (design "careerSave export"): the exact populated-block schema -
        // field names, nesting, placement AFTER the findings array, key-sorted maps /
        // arrays, InvariantCulture "R" numbers. A drift here silently breaks the
        // Python ledger-oracle parser. Fails if a facet is dropped, renamed, or the
        // block moves.
        [Fact]
        public void BuildJson_PopulatedCareerSave_MatchesGoldenSchema()
        {
            string golden = string.Join("\n", new[]
            {
                "{",
                "  \"analyzerVersion\": \"3\",",
                "  \"saveName\": \"testsave\",",
                "  \"subjectSchemaGeneration\": 4,",
                "  \"counts\": {",
                "    \"fail\": 0,",
                "    \"warn\": 0,",
                "    \"info\": 0,",
                "    \"staleFixture\": 0,",
                "    \"baselined\": 0,",
                "    \"failNonBaselined\": 0,",
                "    \"staleNonBaselined\": 0",
                "  },",
                "  \"findings\": [],",
                "  \"careerSave\": {",
                "    \"parsed\": true,",
                "    \"hasFunds\": true,",
                "    \"funds\": 25000,",
                "    \"hasScience\": true,",
                "    \"sciencePool\": 0,",
                "    \"hasRep\": true,",
                "    \"reputation\": 0,",
                "    \"subjectScience\": {",
                "      \"a@Y\": 1,",
                "      \"z@X\": 2.5",
                "    },",
                "    \"facilityLevelFrac\": {",
                "      \"SpaceCenter/LaunchPad\": 0",
                "    },",
                "    \"activeContractGuids\": [",
                "      \"guid-a\",",
                "      \"guid-b\"",
                "    ],",
                "    \"completedMilestoneIds\": [",
                "      \"FirstLaunch\"",
                "    ],",
                "    \"vessels\": [",
                "      {",
                "        \"pid\": \"v-guid\",",
                "        \"persistentId\": 100000,",
                "        \"name\": \"X\",",
                "        \"type\": \"Ship\",",
                "        \"resourceTotals\": {",
                "          \"LiquidFuel\": 90,",
                "          \"Oxidizer\": 110",
                "        }",
                "      }",
                "    ]",
                "  }",
                "}",
                "",
            });

            Assert.Equal(golden, ReportWriter.BuildJson(ReportWith(PopulatedCareer(), null)));
        }

        // Guards (design "Export round-trip + determinism"): two fresh, identically
        // built populated snapshots serialize byte-identically (no dictionary /
        // HashSet iteration order leaks into the output). Fails if the export churns
        // and breaks CI report diffing / the Python parser.
        [Fact]
        public void BuildJson_PopulatedCareerSave_IsByteDeterministic()
        {
            Assert.Equal(
                ReportWriter.BuildJson(ReportWith(PopulatedCareer(), null)),
                ReportWriter.BuildJson(ReportWith(PopulatedCareer(), null)));
        }

        // Guards (design "Always-emitted block + facet flags"): a Science / Sandbox
        // model (Parsed == true, HasFunds == false) emits parsed:true with the correct
        // hasX flags (the loader no longer nulls it on the funds facet), so the Python
        // verifier reads facet-absence from the flags. Fails if a Sandbox save's block
        // is omitted (which would alias facet-absence with tooling-absence) or the flags
        // are wrong.
        [Fact]
        public void BuildJson_NonFundsCareerSave_EmitsBlockWithFacetFlags()
        {
            var cs = new CareerSaveSnapshot
            {
                Parsed = true,
                HasFunds = false,
                HasScience = true,
                SciencePool = 12.0,
                HasRep = false,
            };
            string json = ReportWriter.BuildJson(ReportWith(cs, null));

            Assert.Contains("\"careerSave\": {", json);
            Assert.Contains("\"parsed\": true", json);
            Assert.Contains("\"hasFunds\": false", json);
            Assert.Contains("\"hasScience\": true", json);
            Assert.Contains("\"sciencePool\": 12", json);
            Assert.Contains("\"hasRep\": false", json);
        }

        // Guards (design "Always-emitted block + facet flags"): a null CareerSave
        // (truly non-career / unparsable) emits an ALWAYS-present block collapsed to
        // {parsed:false}, never an omitted block. The Python verifier treats
        // parsed:false as facet-absent and only a MISSING block as tooling-missing.
        // Fails if the writer omits the block on a null snapshot.
        [Fact]
        public void BuildJson_NullCareerSave_EmitsParsedFalseBlock()
        {
            string json = ReportWriter.BuildJson(ReportWith(null, null));

            Assert.Contains("\"careerSave\": {", json);
            Assert.Contains("\"parsed\": false", json);
            Assert.DoesNotContain("\"hasFunds\"", json);
        }

        // Guards: the export tolerates a parsed snapshot whose optional collections are
        // empty - every map / array degenerates to the compact {} / [] form and the
        // block stays valid, key-sorted JSON. Fails if an empty facet emits a dangling
        // comma or a malformed container.
        [Fact]
        public void BuildJson_ParsedCareerSave_EmptyCollections_EmitCompactContainers()
        {
            var cs = new CareerSaveSnapshot { Parsed = true, HasFunds = true, Funds = 1000.0 };
            string json = ReportWriter.BuildJson(ReportWith(cs, null));

            Assert.Contains("\"subjectScience\": {}", json);
            Assert.Contains("\"facilityLevelFrac\": {}", json);
            Assert.Contains("\"activeContractGuids\": []", json);
            Assert.Contains("\"completedMilestoneIds\": []", json);
            Assert.Contains("\"vessels\": []", json);
        }
    }
}
