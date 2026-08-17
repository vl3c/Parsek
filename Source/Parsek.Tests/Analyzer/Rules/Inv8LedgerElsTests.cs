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

        // --- Part (a2): dangling ledger RecordingId refs (phantom attribution) ---

        private static GameAction Tagged(string actionId, string recordingId) =>
            new GameAction { ActionId = actionId, RecordingId = recordingId };

        private static AnalyzerModel ModelWithRecordings(
            IEnumerable<GameAction> ledger, params string[] recordingIds)
        {
            AnalyzerModel model = ModelWith(ledger, null);
            model.Recordings = recordingIds
                .Select(id => new Recording { RecordingId = id })
                .ToList();
            return model;
        }

        // Guards: an action tagged with a recording id that IS present resolves
        // cleanly -> no finding. Fails if the check flags legitimate attribution.
        [Fact]
        public void ResolvingRecordingRef_NoFindings()
        {
            var model = ModelWithRecordings(
                new[] { Tagged("act_1", "rec_a") }, "rec_a");

            Assert.Empty(Run(model));
        }

        // Guards: an action tagged with an unknown recording id -> WARN, targeting
        // the phantom id. This is the population left behind by the (now closed)
        // PickRecoveryRecordingId provisional-attribution route.
        [Fact]
        public void DanglingRecordingRef_Warns()
        {
            var model = ModelWithRecordings(
                new[] { Tagged("act_1", "rec_gone") }, "rec_a");

            Finding warn = Assert.Single(Run(model));
            Assert.Equal(Inv8Ledger.RuleIdConst, warn.RuleId);
            Assert.Equal(VerdictLevel.Warn, warn.Level);
            Assert.Equal("rec_gone", warn.Target);
            Assert.Contains("dangling-recording-ref", warn.Message);
            Assert.Contains("recordingId=rec_gone", warn.Message);
            Assert.Contains("kind=phantom-attribution", warn.Message);
        }

        // Guards baseline-key stability: the message must carry NO sample actionId.
        // NormalizeMessageDigest masks only numeric RUNS, and a real ActionId is hex,
        // so its letters survive masking - carrying one would change the baseline key
        // whenever the FIRST dangling row for this id changed (a re-order, or an
        // earlier row pruned), silently un-baselining an already-accepted finding.
        // Fails if a sample action id is reintroduced into the message.
        [Fact]
        public void DanglingRecordingRef_MessageCarriesNoActionId_SoTheBaselineKeyIsStable()
        {
            Finding first = Assert.Single(Run(ModelWithRecordings(
                new[] { Tagged("act_aaa", "rec_gone"), Tagged("act_bbb", "rec_gone") },
                "rec_a")));

            // Same phantom id, same row count, DIFFERENT first action id.
            Finding second = Assert.Single(Run(ModelWithRecordings(
                new[] { Tagged("act_bbb", "rec_gone"), Tagged("act_aaa", "rec_gone") },
                "rec_a")));

            Assert.Equal(first.Message, second.Message);
            Assert.DoesNotContain("act_aaa", first.Message);
            Assert.DoesNotContain("act_bbb", first.Message);
        }

        // Guards WARN severity specifically: a pre-existing phantom population must
        // stay gate-neutral. WARN never feeds the .analysis.txt terminal RED token
        // (ReportWriter: RED=1 iff failNonBaselined + staleNonBaselined > 0), so
        // surfacing these in every affected save cannot flip a gated run red. Fails
        // if the severity is ever escalated to Fail / StaleFixture without a
        // deliberate decision about the gate.
        [Fact]
        public void DanglingRecordingRef_IsGateNeutral()
        {
            var model = ModelWithRecordings(
                new[] { Tagged("act_1", "rec_gone") }, "rec_a");

            Counts counts = Counts.From(Run(model));

            Assert.Equal(1, counts.Warn);
            Assert.Equal(0, counts.Fail);
            Assert.Equal(0, counts.FailNonBaselined);
            Assert.Equal(0, counts.StaleNonBaselined);
        }

        // Guards the bounded-output choice: many actions pointing at ONE phantom id
        // collapse to a single finding carrying the count, not N findings. Fails if
        // the rule reverts to per-action emission (a long ledger would flood).
        [Fact]
        public void MultipleActionsOneDanglingId_SingleFindingWithCount()
        {
            var model = ModelWithRecordings(
                new[]
                {
                    Tagged("act_1", "rec_gone"),
                    Tagged("act_2", "rec_gone"),
                    Tagged("act_3", "rec_gone"),
                },
                "rec_a");

            Finding warn = Assert.Single(Run(model));
            Assert.Contains("actions=3", warn.Message);
            Assert.Equal("rec_gone", warn.Target);
        }

        // Guards determinism / one-per-distinct-id: two phantom ids -> two findings
        // in first-appearance order. Order stability matters for baseline matching.
        [Fact]
        public void TwoDanglingIds_TwoFindings_InFirstAppearanceOrder()
        {
            var model = ModelWithRecordings(
                new[]
                {
                    Tagged("act_1", "rec_gone_b"),
                    Tagged("act_2", "rec_gone_a"),
                    Tagged("act_3", "rec_gone_b"),
                },
                "rec_a");

            List<Finding> findings = Run(model)
                .Where(f => f.Message.Contains("dangling-recording-ref")).ToList();

            Assert.Equal(2, findings.Count);
            Assert.Equal("rec_gone_b", findings[0].Target);
            Assert.Equal("rec_gone_a", findings[1].Target);
        }

        // Guards: an untagged action (null / empty RecordingId) is free-standing and
        // legitimate (route rows, KSC-path rows), never a phantom. Fails if the rule
        // treats "no attribution" as "broken attribution".
        [Fact]
        public void UntaggedActions_NoFindings()
        {
            var model = ModelWithRecordings(
                new[] { Act("act_1"), Tagged("act_2", null), Tagged("act_3", "") },
                "rec_a");

            Assert.Empty(Run(model));
        }

        // Guards the single-report policy for this check: an sfs LoadFault means the
        // RECORDING list is incomplete, so every tagged row would look dangling. The
        // LOADER-FAULT rule already owns that failure. Fails if a corrupt save
        // double-reports as a phantom-attribution flood.
        [Fact]
        public void SfsLoadFault_SuppressesDanglingRecordingRefCheck()
        {
            var model = ModelWithRecordings(
                new[] { Tagged("act_1", "rec_gone") });   // no recordings survived the fault
            model.LoadFaults = new List<LoadFault>
            {
                new LoadFault("/save/persistent.sfs", "sfs", "unbalanced-braces", null),
            };

            Assert.DoesNotContain(Run(model), f =>
                f.RuleId == Inv8Ledger.RuleIdConst && f.Message.Contains("dangling-recording-ref"));
        }

        // Guards purity / robustness: a null Recordings list must not NRE, and with
        // no known recordings a tagged row is genuinely dangling.
        [Fact]
        public void NullRecordings_NoThrow_StillReports()
        {
            AnalyzerModel model = ModelWith(new[] { Tagged("act_1", "rec_gone") }, null);
            model.Recordings = null;

            Assert.Contains(Run(model), f => f.Message.Contains("dangling-recording-ref"));
        }
    }
}
