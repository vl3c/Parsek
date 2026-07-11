using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    // Hook H5 verdict-mapping tests (module M-A3, design "H5 - Verdict mapping" +
    // "Test Plan" in-game tests). The live-store BUILDER itself is Unity-bound and
    // exercised by the PENDING-OPERATOR runbook, but the pure pieces the in-game test
    // runs -- the INV1-INV8 subset evaluation and the Fail-vs-Warn verdict mapping --
    // are xUnit-provable now that the core lives in Parsek.dll. These build synthetic
    // AnalyzerModels (the exact model shape BuildLiveStoreModel produces) and assert
    // the mapping, so a broken H5 walker (e.g. hiding superseded rows, or a Fail
    // mis-mapped to a pass) is caught headlessly. AnalyzerModel / InvariantEvaluator /
    // InvariantRegistry resolve via the project-wide `global using Parsek.Analyzer`.
    public class RecordingInvariantsVerdictTests
    {
        private static RecordingInvariantsInGameTests.InvariantVerdictOutcome Classify(AnalyzerModel model)
        {
            var report = InvariantEvaluator.Evaluate(model, InvariantRegistry.InGamePureCoreRules);
            return RecordingInvariantsInGameTests.ClassifyFindings(report);
        }

        // Guards: a clean (here, empty) model yields ZERO Fail findings, so the in-game
        // test PASSES an empty/clean store rather than false-failing (edge 15). This is
        // the same model shape BuildLiveStoreModel hands the evaluator.
        [Fact]
        public void CleanModel_NoFailFindings()
        {
            var model = new AnalyzerModel
            {
                SaveName = "h5",
                Recordings = new List<Recording>(),
                Trees = new List<RecordingTree>(),
                Tombstones = new List<LedgerTombstone>(),
                SupersedeRelations = new List<RecordingSupersedeRelation>(),
                Ledger = new List<GameAction>(),
            };

            var outcome = Classify(model);

            Assert.Empty(outcome.Fails);
        }

        // Guards the load-bearing decision behind the raw-CommittedRecordings walk: a
        // dangling supersede link (a Recording.SupersedeTargetId pointing at an id absent
        // from the model) must map to a Fail, so the in-game test FAILS carrying the
        // RuleId + Target + Message. Fails if the walker fed the core an ERS-filtered set
        // (hiding the superseded row) so the broken link went undetected, or if the
        // verdict mapping dropped the Fail.
        [Fact]
        public void DanglingSupersedeModel_MapsToFail()
        {
            var rec = new Recording { RecordingId = "a", SupersedeTargetId = "ghost-missing" };
            var model = new AnalyzerModel
            {
                SaveName = "h5",
                Recordings = new List<Recording> { rec },
            };

            var outcome = Classify(model);

            Assert.NotEmpty(outcome.Fails);
            Assert.Contains(outcome.Fails, f =>
                f.Message.Contains("kind=dangling") && f.Message.Contains("ghost-missing"));
            // The failure carries the identifying triple the in-game InGameAssert surfaces.
            Assert.All(outcome.Fails, f =>
            {
                Assert.False(string.IsNullOrEmpty(f.RuleId));
                Assert.False(string.IsNullOrEmpty(f.Message));
            });
        }

        // Guards the Warn-vs-Fail split (design: Warn does NOT fail the test): a one-sided
        // orphan supersede row (New endpoint resolves, Old endpoint absent) is a Warn, so
        // it lands in Warns and NOT in Fails. Fails if an orphan-supersede WARN is promoted
        // to a test failure and a benign store starts red.
        [Fact]
        public void OrphanSupersede_MapsToWarnNotFail()
        {
            var rec = new Recording { RecordingId = "new" };
            var model = new AnalyzerModel
            {
                SaveName = "h5",
                Recordings = new List<Recording> { rec },
                SupersedeRelations = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_1",
                        OldRecordingId = "missing-old",
                        NewRecordingId = "new",
                    },
                },
            };

            var outcome = Classify(model);

            Assert.Empty(outcome.Fails);
            Assert.Contains(outcome.Warns, f => f.Message.Contains("SupersedeRelation.OldRecordingId"));
        }
    }
}
