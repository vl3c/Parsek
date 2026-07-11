using System.Collections.Generic;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // Fixture-generation stamp check. Pure in-memory model (the stamp side of the
    // loader parse is covered separately in the loader tests). Each test names the
    // regression it guards.
    public class FixtureStampRuleTests
    {
        private static AnalyzerModel ModelWith(FixtureStamp? stamp, params string[] recordingIds)
        {
            return new AnalyzerModel
            {
                SaveName = "fixstamp",
                FixtureStamp = stamp,
                Recordings = recordingIds
                    .Select(id => new Recording { RecordingId = id })
                    .ToList<Recording>(),
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new FixtureStampRule().Evaluate(model).ToList();
        }

        // Guards: a stamp at the current generation -> zero findings. Fails if a
        // current fixture corpus is wrongly reported stale.
        [Fact]
        public void CurrentGenerationStamp_NoFindings()
        {
            var stamp = new FixtureStamp(RecordingStore.CurrentRecordingSchemaGeneration, "synthetic");
            Assert.Empty(Run(ModelWith(stamp, "r0", "r1")));
        }

        // Guards (edge case 22): a stamp at a stale generation -> STALE-FIXTURE for
        // EVERY recording, distinct from FAIL, and the run reads red-but-distinct.
        // Fails if a stale corpus reads as a code bug (FAIL) or silently passes.
        [Fact]
        public void StaleGenerationStamp_StaleFixturePerRecording()
        {
            int staleGen = RecordingStore.CurrentRecordingSchemaGeneration - 1;
            var stamp = new FixtureStamp(staleGen, "synthetic");
            AnalyzerModel model = ModelWith(stamp, "r0", "r1");

            List<Finding> findings = Run(model);

            Assert.Equal(2, findings.Count);
            Assert.All(findings, f =>
            {
                Assert.Equal(VerdictLevel.StaleFixture, f.Level);
                Assert.Contains("stale-fixture", f.Message);
            });

            // Through the full pipeline: red via StaleFixture, not Fail.
            AnalysisReport report = Analyzer.Evaluate(model, new List<IRecordingInvariant> { new FixtureStampRule() });
            Assert.Equal(2, report.Counts.StaleFixture);
            Assert.Equal(0, report.Counts.Fail);
            Assert.True(report.IsRed);
        }

        // Guards: a harvested stamp carries the re-harvest-queue note (harvested
        // fixtures cannot be script-regenerated). Fails if the action note is wrong.
        [Fact]
        public void HarvestedStaleStamp_CarriesReHarvestNote()
        {
            var stamp = new FixtureStamp(RecordingStore.CurrentRecordingSchemaGeneration - 1, "harvested");
            List<Finding> findings = Run(ModelWith(stamp, "r0"));

            Finding f = Assert.Single(findings);
            Assert.Contains("action=re-harvest-queue", f.Message);
        }

        // Guards: a non-fixture subject (no stamp) skips the check entirely. Fails
        // if an unstamped harness / triage save is wrongly flagged stale.
        [Fact]
        public void Unstamped_NoFindings()
        {
            Assert.Empty(Run(ModelWith(null, "r0", "r1")));
        }
    }
}
