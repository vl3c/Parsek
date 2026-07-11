using System.Collections.Generic;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV8 part (a): ELS internal consistency over the RAW model. Pure in-memory
    // fixtures (ScenarioWriter cannot emit tombstone rows, correction C4). Each
    // test names the regression it guards.
    public class Inv8LedgerElsTests
    {
        private static AnalyzerModel ModelWith(
            IEnumerable<GameAction> ledger,
            IEnumerable<LedgerTombstone> tombstones,
            bool career = false)
        {
            return new AnalyzerModel
            {
                SaveName = "inv8-els",
                Ledger = (ledger ?? Enumerable.Empty<GameAction>()).ToList(),
                Tombstones = (tombstones ?? Enumerable.Empty<LedgerTombstone>()).ToList(),
                CareerSave = career ? new CareerSaveSnapshot { Parsed = true, HasFunds = true } : null,
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv8Ledger().Evaluate(model).ToList();
        }

        private static GameAction Act(string id) => new GameAction { ActionId = id };

        private static LedgerTombstone Tomb(string tombId, string actionId) =>
            new LedgerTombstone { TombstoneId = tombId, ActionId = actionId, RetiringRecordingId = "r" };

        // Guards: a tombstone whose target ActionId IS present in the raw ledger ->
        // zero INV8 findings, proving the check runs against the RAW (unfiltered)
        // action list so a resolving tombstone is accepted. Fails if the model were
        // fed a pre-filtered ELS list (the tombstoned action would be gone and the
        // check would false-alarm or go vacuous).
        [Fact]
        public void ResolvingTombstone_OverRawLedger_NoFindings()
        {
            var model = ModelWith(
                new[] { Act("act_1"), Act("act_2") },
                new[] { Tomb("t1", "act_1") });

            Assert.Empty(Run(model));
        }

        // Guards (edge case 17b): a tombstone whose target ActionId is absent from
        // the raw ledger -> FAIL. Fails if a dangling tombstone passes (the
        // regression a pre-filtered ELS list would hide by construction).
        [Fact]
        public void DanglingTombstone_Fails()
        {
            var model = ModelWith(
                new[] { Act("act_1") },
                new[] { Tomb("t-bad", "act_missing") });

            List<Finding> findings = Run(model);

            Finding fail = Assert.Single(findings);
            Assert.Equal(Inv8Ledger.RuleIdConst, fail.RuleId);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Equal("t-bad", fail.Target);
            Assert.Contains("actionId=act_missing", fail.Message);
        }

        // Guards (edge case 18): part (a) runs for a non-career save too - a
        // dangling tombstone on a Sandbox model still FAILs. Fails if the ledger
        // rule short-circuits on a null CareerSaveSnapshot.
        [Fact]
        public void NonCareer_ElsConsistency_StillRuns()
        {
            var model = ModelWith(
                new[] { Act("act_1") },
                new[] { Tomb("t-bad", "act_missing") },
                career: false);

            Assert.Contains(Run(model), f =>
                f.Level == VerdictLevel.Fail && f.Message.Contains("els-inconsistency"));
        }

        // Guards (single-report policy): when the ledger file failed to load
        // (LoadFault{FileKind="ledger"}), the RAW action list is incomplete, so
        // every tombstone would look dangling. INV8(a) must skip the per-tombstone
        // dangling check and defer to the LOADER-FAULT finding, mirroring INV5's
        // tested faulted-trajectory skip. Fails if INV8 double-reports a corrupt
        // ledger as an ELS inconsistency.
        [Fact]
        public void LedgerLoadFault_SuppressesDanglingCheck()
        {
            var model = ModelWith(
                Enumerable.Empty<GameAction>(),          // raw actions lost to the fault
                new[] { Tomb("t-bad", "act_missing") }); // would dangle absent the guard
            model.LoadFaults = new List<LoadFault>
            {
                new LoadFault("/save/Parsek/GameState/ledger.pgld", "ledger", "configNode-load-returned-null", null),
            };

            Assert.DoesNotContain(Run(model), f =>
                f.RuleId == Inv8Ledger.RuleIdConst && f.Message.Contains("dangling-tombstone"));
        }

        // Guards: no tombstones -> zero findings, and the rule does not throw on an
        // empty / null ledger. Fails if the ELS reconstruction NREs on empty input.
        [Fact]
        public void EmptyLedgerAndTombstones_NoFindings_NoThrow()
        {
            Assert.Empty(Run(ModelWith(null, null)));
        }
    }
}
