using System;
using System.Collections.Generic;
using Parsek.Analyzer;
using UnityEngine;

namespace Parsek.InGameTests
{
    // [ERS-exempt] Hook H5 (module M-A3, design docs/dev/design-autotest-autorun-hooks.md
    // "H5 - RecordingInvariants in-game category" + "ERS/ELS grep-gate decision") walks
    // RecordingStore.CommittedRecordings / CommittedTrees and Ledger.Actions RAW (no
    // EffectiveState.ComputeERS / ComputeELS routing) BY DESIGN. The invariant core
    // validates the STRUCTURAL integrity of the persisted data, including NotCommitted
    // provisionals, superseded entries, and tombstone / supersede link targets. ERS is the
    // visibility-filtered effective set (it drops superseded + NotCommitted rows), so
    // feeding H5 the ERS view would HIDE exactly the rows whose links INV7 exists to check
    // -- a superseded recording ERS drops is still a row whose SupersedeTargetId must
    // resolve. The offline analyzer walks the whole save directory including those rows; to
    // produce the SAME verdict from the SAME core the in-game walker must feed the SAME
    // complete set. This is the identical rationale the allowlist already records for
    // RecordingStoreTestSnapshot ("supersede-aware filtering would corrupt the round-trip").
    // No new allowlist entry is needed: the grep gate allowlists Source/Parsek/InGameTests/
    // (this file lives there), and the invariant core (Parsek.Analyzer) reads no store
    // symbols at all -- it takes materialized lists as arguments, so it is pure by
    // construction and needs no exemption. GrepAuditTests is the regression.
    //
    // ================= PENDING-OPERATOR RUNBOOK =================
    // The H5 category and the H1/H2 hooks need an operator to launch KSP; an agent cannot
    // pilot it. Four live-verification items (see the design doc's "PENDING-OPERATOR
    // Runbook" section for the full step lists):
    //   1. Killed-batch reconcile cycle: kill KSP mid-batch, relaunch with the autorun env,
    //      confirm H1 holds fire until the crash-reconcile deferred reload completes
    //      ("crash-reconcile in progress" -> "complete; cleared"), then fires once.
    //   2. H1 live fire/settle/re-arm: boot with PARSEK_AUTORUN_TESTS set, confirm the
    //      settle-waiting log names the stuck condition, the FIRING line fires exactly once
    //      per process, and a FLIGHT->FLIGHT isolation reload re-arms but does not re-fire.
    //   3. Real H2 process exit: with PARSEK_AUTORUN_EXIT=1 confirm KSP quits cleanly after
    //      the BATCH_COMPLETE line (clean batch) AND after an NRE-storm-abort batch (H2
    //      supersedes the Space Center bounce, disk already reverted).
    //   4. H5 vs a real populated store + a deliberately malformed injection: a clean store
    //      passes with zero Fail findings; a UT-non-monotonic / dangling-supersede injection
    //      produces a Fail carrying RuleId + Target + Message.
    // ===========================================================

    /// <summary>
    /// Hook H5: the in-game <c>RecordingInvariants</c> test category. Builds an
    /// <see cref="AnalyzerModel"/> from the LIVE store and runs module M-A1's pure invariant
    /// core over it, mapping Fail findings to in-game test failures (P6.2). This partial
    /// file (P6.1) owns the live-store model builder; the FLIGHT-scoped test methods and the
    /// verdict mapping.
    /// </summary>
    public class RecordingInvariantsInGameTests
    {
        private const string Tag = "RecordingInvariants";

        /// <summary>
        /// Store size above which the synchronous walk logs a one-frame-hitch warning
        /// (design "Execution model"). Coroutine slicing for very large stores is deferred.
        /// </summary>
        internal const int AutorunInvariantWalkSizeWarnThreshold = 500;

        /// <summary>
        /// Builds an <see cref="AnalyzerModel"/> from live in-game state (design "H5 - What
        /// it does", the model-builder table). Walks the store RAW ([ERS-exempt], see the
        /// file header): CommittedRecordings / CommittedTrees, the live ParsekScenario's
        /// tombstones + supersede rows, and RAW Ledger.Actions. CareerSave / FixtureStamp /
        /// SaveDirectory are null and LoadFaults empty (nothing is parsed from disk here);
        /// the body resolver is a FlightGlobals.Bodies lookup. Emits the walk-count line up
        /// front on EVERY run (recordings=0 distinguishes an empty store from a not-run one,
        /// edge 15) plus a size warning above the threshold.
        /// </summary>
        internal static AnalyzerModel BuildLiveStoreModel()
        {
            IReadOnlyList<Recording> recordings = RecordingStore.CommittedRecordings;
            IReadOnlyList<RecordingTree> trees = RecordingStore.CommittedTrees;
            ParsekScenario scenario = ParsekScenario.Instance;
            IReadOnlyList<LedgerTombstone> tombstones =
                scenario != null ? scenario.LedgerTombstones : new List<LedgerTombstone>();
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                scenario != null ? scenario.RecordingSupersedes : new List<RecordingSupersedeRelation>();
            IReadOnlyList<GameAction> ledger = Ledger.Actions;

            int recCount = recordings != null ? recordings.Count : 0;
            int treeCount = trees != null ? trees.Count : 0;

            // Walk-count line up front on every run (design "Diagnostic Logging" H5).
            ParsekLog.Info(Tag, $"RecordingInvariants walk: recordings={recCount} trees={treeCount}");
            if (recCount > AutorunInvariantWalkSizeWarnThreshold)
                ParsekLog.Warn(Tag,
                    $"RecordingInvariants walk over {recCount} recordings may hitch a frame; "
                    + "coroutine slicing deferred");

            return new AnalyzerModel
            {
                SaveName = HighLogic.SaveFolder,
                Recordings = recordings ?? new List<Recording>(),
                Trees = trees ?? new List<RecordingTree>(),
                Tombstones = tombstones ?? new List<LedgerTombstone>(),
                SupersedeRelations = supersedes ?? new List<RecordingSupersedeRelation>(),
                Ledger = ledger ?? new List<GameAction>(),
                CareerSave = null,
                LoadFaults = new List<LoadFault>(),
                FixtureStamp = null,
                SaveDirectory = null,
                BodyResolver = ResolveBody,
            };
        }

        /// <summary>FlightGlobals.Bodies name -&gt; CelestialBody lookup for the model's
        /// BodyResolver. Null-safe; returns null when no body matches.</summary>
        private static CelestialBody ResolveBody(string name)
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] != null && bodies[i].bodyName == name)
                    return bodies[i];
            return null;
        }

        /// <summary>Findings partitioned by the in-game verdict policy (design "H5 -
        /// Verdict mapping"): Fail findings fail the test, Warn findings only log.</summary>
        internal struct InvariantVerdictOutcome
        {
            public List<Finding> Fails;
            public List<Finding> Warns;
        }

        /// <summary>
        /// Maps M-A1 <see cref="VerdictLevel"/>s to in-game outcomes (design "H5 - Verdict
        /// mapping"): <see cref="VerdictLevel.Fail"/> -&gt; a test failure (collected in
        /// Fails, carrying RuleId + Target + Message), <see cref="VerdictLevel.Warn"/> -&gt;
        /// a log-only finding (Warns), and Info / StaleFixture ignored (StaleFixture is
        /// unreachable in-game since the live model carries a null FixtureStamp). Pure so
        /// the mapping is xUnit-testable over a synthetic report.
        /// </summary>
        internal static InvariantVerdictOutcome ClassifyFindings(AnalysisReport report)
        {
            var outcome = new InvariantVerdictOutcome
            {
                Fails = new List<Finding>(),
                Warns = new List<Finding>(),
            };
            if (report != null && report.Findings != null)
            {
                foreach (Finding f in report.Findings)
                {
                    if (f.Level == VerdictLevel.Fail) outcome.Fails.Add(f);
                    else if (f.Level == VerdictLevel.Warn) outcome.Warns.Add(f);
                    // Info / StaleFixture: ignored (not a test signal).
                }
            }
            return outcome;
        }

        // ----- [M-A3 hook H5] The RecordingInvariants FLIGHT category -----

        /// <summary>
        /// Walks the LIVE store through the M-A1 pure-core invariant subset (INV1-INV8) and
        /// fails on any Fail finding (design "H5 - What it does each run"). FLIGHT-scoped:
        /// a loaded career with a populated RecordingStore reliably exists there after the
        /// autorun save loads. Synchronous (the walk is read-only); every Warn finding logs
        /// but does not fail, and a summary line closes the run.
        /// </summary>
        [InGameTest(Category = "RecordingInvariants", Scene = GameScenes.FLIGHT,
            Description = "Walk the live RecordingStore through the M-A1 invariant core; Fail findings fail the test")]
        public void RecordingInvariantsHoldOverLiveStore()
        {
            AnalyzerModel model = BuildLiveStoreModel(); // logs the walk-count line
            AnalysisReport report = InvariantEvaluator.Evaluate(
                model, InvariantRegistry.InGamePureCoreRules);
            InvariantVerdictOutcome outcome = ClassifyFindings(report);

            foreach (Finding warn in outcome.Warns)
                ParsekLog.Warn(Tag,
                    $"RecordingInvariants WARN {warn.RuleId} target={warn.Target}: {warn.Message}");
            foreach (Finding fail in outcome.Fails)
                ParsekLog.Warn(Tag,
                    $"RecordingInvariants FAIL {fail.RuleId} target={fail.Target}: {fail.Message}");

            int recCount = model.Recordings != null ? model.Recordings.Count : 0;
            ParsekLog.Info(Tag,
                $"RecordingInvariants summary: fails={outcome.Fails.Count} warns={outcome.Warns.Count} "
                + $"over recordings={recCount}");

            if (outcome.Fails.Count > 0)
            {
                Finding first = outcome.Fails[0];
                InGameAssert.Fail(
                    $"RecordingInvariants: {outcome.Fails.Count} Fail finding(s); first: "
                    + $"{first.RuleId} target={first.Target}: {first.Message}");
            }
        }

        /// <summary>
        /// An EMPTY store is a PASS (no recordings = no findings), not a Skip (edge 15).
        /// Evaluates a hand-built empty model through the same subset so the "empty store
        /// trivially holds" contract is proven deterministically regardless of what the
        /// live store happens to hold, and logs the walk=0 line so an empty run is
        /// distinguishable from a not-run one.
        /// </summary>
        [InGameTest(Category = "RecordingInvariants", Scene = GameScenes.FLIGHT,
            Description = "An empty store passes (edge 15): zero recordings yield zero Fail findings")]
        public void RecordingInvariantsEmptyStorePasses()
        {
            var empty = new AnalyzerModel
            {
                SaveName = HighLogic.SaveFolder,
                Recordings = new List<Recording>(),
                Trees = new List<RecordingTree>(),
                Tombstones = new List<LedgerTombstone>(),
                SupersedeRelations = new List<RecordingSupersedeRelation>(),
                Ledger = new List<GameAction>(),
                CareerSave = null,
                LoadFaults = new List<LoadFault>(),
                FixtureStamp = null,
                SaveDirectory = null,
                BodyResolver = ResolveBody,
            };
            ParsekLog.Info(Tag, "RecordingInvariants walk: recordings=0 trees=0 (empty-store pass check)");
            AnalysisReport report = InvariantEvaluator.Evaluate(
                empty, InvariantRegistry.InGamePureCoreRules);
            InvariantVerdictOutcome outcome = ClassifyFindings(report);

            InGameAssert.AreEqual(0, outcome.Fails.Count,
                "Empty store must yield zero Fail findings (edge 15)");
        }
    }
}
