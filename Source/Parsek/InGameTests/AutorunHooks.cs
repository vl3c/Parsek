using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsek.InGameTests
{
    // Pure decision core for the autorun hooks (module M-A3, design
    // docs/dev/design-autotest-autorun-hooks.md). Every method here is a pure,
    // Unity-free static function so the whole external contract (env parsing,
    // scene-settle gate, single-fire gate, exit decision) is xUnit-testable without
    // a live KSP. The live wiring in TestRunnerShortcut / InGameTestRunner /
    // ParsekScenario (Phases 3-6) reads process env once, feeds these functions live
    // state, and acts on the returned decisions - it holds no policy of its own.

    /// <summary>
    /// The parsed autorun env contract (design "Env var surface"). Read once at
    /// addon startup so a mid-process env mutation cannot change behavior (env is a
    /// launch-time contract, edge case 14).
    /// </summary>
    internal struct AutorunConfig
    {
        /// <summary>H1 will auto-run a batch. False = fully inert.</summary>
        public bool Enabled;

        /// <summary>Selector was the literal "all" -> runner.RunAll().</summary>
        public bool IsAll;

        /// <summary>
        /// Parsed, trimmed, non-empty category tokens (design multi-category). Empty
        /// for the "all" selector and for an inert config.
        /// </summary>
        public IReadOnlyList<string> Categories;

        /// <summary>H2 armed: quit after teardown+export (PARSEK_AUTORUN_EXIT=="1").</summary>
        public bool ExitArmed;

        /// <summary>The raw PARSEK_AUTORUN_TESTS value, kept for the startup log line.</summary>
        public string RawSelector;

        /// <summary>
        /// WARN lines the startup log should emit for a misconfiguration (edge cases
        /// 2, 9). Never null; empty on a clean config.
        /// </summary>
        public IReadOnlyList<string> Warnings;
    }

    internal static class AutorunHooks
    {
        private static readonly IReadOnlyList<string> NoCategories = new string[0];

        internal const string WarnZeroCategories =
            "autorun selector parsed to zero categories; H1 inert";
        internal const string WarnExitWithoutTests =
            "PARSEK_AUTORUN_EXIT set but PARSEK_AUTORUN_TESTS unset; nothing will auto-run or auto-quit";

        /// <summary>
        /// Parses the two autorun env vars into an <see cref="AutorunConfig"/>
        /// (design "Env var surface", edge cases 1, 2, 3, 9).
        ///
        /// - null / empty tests var  -> inert (edge 1), no selector warning.
        /// - "all"                   -> Enabled, IsAll (runner.RunAll()).
        /// - "A,B,C" / " A , B " / "A,,B" / ",A," -> trim tokens + drop empties (edge 2).
        /// - non-empty but parses to zero tokens (whitespace / commas only) -> inert
        ///   + WarnZeroCategories (edge 2).
        /// - a single unknown category (e.g. "Nope") is kept verbatim; the
        ///   "matched 0 discovered tests" signal is a runtime concern, not a parse
        ///   error (edge 3).
        /// - exit var "1" -> ExitArmed; combined with an unset tests var it adds
        ///   WarnExitWithoutTests (edge 9). Category match is Ordinal (case-sensitive)
        ///   to mirror the runner's category comparison.
        /// </summary>
        internal static AutorunConfig Parse(string testsVar, string exitVar)
        {
            bool exitArmed = string.Equals(exitVar, "1", StringComparison.Ordinal);

            // Truly unset/empty tests var: fully inert (edge 1). Only the exit-without-
            // tests misconfiguration warns here (edge 9).
            if (string.IsNullOrEmpty(testsVar))
            {
                var warnings0 = new List<string>();
                if (exitArmed)
                    warnings0.Add(WarnExitWithoutTests);
                return new AutorunConfig
                {
                    Enabled = false,
                    IsAll = false,
                    Categories = NoCategories,
                    ExitArmed = exitArmed,
                    RawSelector = testsVar,
                    Warnings = warnings0,
                };
            }

            if (string.Equals(testsVar.Trim(), "all", StringComparison.Ordinal))
            {
                return new AutorunConfig
                {
                    Enabled = true,
                    IsAll = true,
                    Categories = NoCategories,
                    ExitArmed = exitArmed,
                    RawSelector = testsVar,
                    Warnings = new List<string>(),
                };
            }

            var categories = testsVar
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (categories.Count == 0)
            {
                // Non-empty but nothing survived the trim/drop (whitespace / commas
                // only): inert + WARN (edge 2). This is distinct from truly unset, so
                // it does NOT also emit the exit-without-tests warning.
                var warnings1 = new List<string> { WarnZeroCategories };
                return new AutorunConfig
                {
                    Enabled = false,
                    IsAll = false,
                    Categories = NoCategories,
                    ExitArmed = exitArmed,
                    RawSelector = testsVar,
                    Warnings = warnings1,
                };
            }

            return new AutorunConfig
            {
                Enabled = true,
                IsAll = false,
                Categories = categories,
                ExitArmed = exitArmed,
                RawSelector = testsVar,
                Warnings = new List<string>(),
            };
        }

        /// <summary>
        /// True for a game scene the runner can execute a batch in (mirrors
        /// HighLogic.LoadedSceneIsGame). LOADING / MAINMENU / PSYSTEM / CREDITS /
        /// SETTINGS are not game scenes, so H1 never fires there (edge case 7).
        /// </summary>
        internal static bool IsGameScene(GameScenes scene)
        {
            return scene == GameScenes.FLIGHT
                || scene == GameScenes.SPACECENTER
                || scene == GameScenes.TRACKSTATION
                || scene == GameScenes.EDITOR;
        }

        /// <summary>
        /// The concrete scene-settle gate (design "H1 - Scene-settle definition").
        /// Returns true only when every condition holds on the same frame, so the
        /// autorun batch never captures its baseline against a half-initialized scene
        /// or a half-reverted crash-reconcile save. Pure: the caller supplies live
        /// state each frame and advances/resets settleFrames itself.
        ///
        /// Conditions:
        /// 1. <paramref name="scene"/> is a game scene (not LOADING/MainMenu) - edge 7.
        /// 2. <paramref name="gameNonNull"/> (CurrentGame != null) AND
        ///    <paramref name="saveLoaded"/> (non-empty SaveFolder) - a save is loaded, edge 7.
        /// 3. FLIGHT only: <paramref name="flightReady"/> AND
        ///    <paramref name="vesselNonNull"/> AND NOT <paramref name="vesselPacked"/>
        ///    (physics-live vessel), so tests do not race the recorder / PartLoader.
        ///    Non-FLIGHT game scenes have no vessel gate.
        /// 4. <paramref name="settleFrames"/> has reached
        ///    <paramref name="settleTarget"/> consecutive qualifying frames.
        /// 5. NOT <paramref name="reconcilePending"/>: a prior killed batch's crash
        ///    reconcile has fully completed (edge 6).
        /// </summary>
        internal static bool SceneSettleDecision(
            GameScenes scene,
            bool gameNonNull,
            bool saveLoaded,
            bool flightReady,
            bool vesselNonNull,
            bool vesselPacked,
            int settleFrames,
            int settleTarget,
            bool reconcilePending)
        {
            if (!IsGameScene(scene))
                return false;
            if (!gameNonNull || !saveLoaded)
                return false;
            if (reconcilePending)
                return false;
            if (scene == GameScenes.FLIGHT)
            {
                if (!flightReady || !vesselNonNull || vesselPacked)
                    return false;
            }
            if (settleFrames < settleTarget)
                return false;
            return true;
        }

        /// <summary>
        /// The single-fire gate (design "H1 - Single-fire per scene-entry", edge
        /// cases 5, 8; correction G1). Returns true only when H1 may start a batch:
        /// no runner batch is already in flight, no M-A2 command-seam batch is running
        /// (G1: without this a settle-fire and a seam RunTests could launch two
        /// concurrent batches and corrupt the campaign), the process latch has not
        /// already consumed this selector, and H1 has not already fired for this
        /// scene entry.
        ///
        /// The two "runner busy" inputs cover edge 5 (a human clicked Run All);
        /// <paramref name="consumedForProcess"/> + <paramref name="firedThisScene"/>
        /// together give edge 8 (the runner's FLIGHT->FLIGHT isolation reload
        /// re-arms per scene but never double-fires).
        /// </summary>
        internal static bool AutorunFireGate(
            bool isRunning,
            bool commandRunnerRunning,
            bool consumedForProcess,
            bool firedThisScene)
        {
            return !isRunning
                && !commandRunnerRunning
                && !consumedForProcess
                && !firedThisScene;
        }

        /// <summary>
        /// The H2 exit decision (design "H2 - Exit after tests", edge cases 11, 13).
        /// </summary>
        internal struct H2Decision
        {
            /// <summary>Quit KSP as the last batch-end step.</summary>
            public bool ShouldQuit;

            /// <summary>Skip the post-corruption Space Center bounce recovery.</summary>
            public bool SkipBounce;
        }

        /// <summary>
        /// Decides whether the batch-end region quits KSP and whether it skips the
        /// Space Center bounce. Quit only when PARSEK_AUTORUN_EXIT is armed AND this
        /// was an autorun batch: a human clicking Run All in a process that happens to
        /// have the env var set latches wasAutorunBatch=false and never quits under
        /// them (edge 13). When quitting, the bounce is skipped - autorun has no
        /// operator to leave in a usable scene and the process is about to die, so H2
        /// takes precedence over the bounce (edge 11). The disk save is already
        /// reverted by teardown regardless, so skipping the bounce never risks the
        /// campaign. bounceArmed is passed for completeness/logging; skipBounce is
        /// driven purely by shouldQuit (skipping an unarmed bounce is a harmless no-op).
        /// </summary>
        internal static H2Decision H2ExitDecision(
            bool exitArmed, bool wasAutorunBatch, bool bounceArmed)
        {
            bool shouldQuit = exitArmed && wasAutorunBatch;
            return new H2Decision
            {
                ShouldQuit = shouldQuit,
                SkipBounce = shouldQuit,
            };
        }

        /// <summary>
        /// The crash-reconcile clear gate for H1's settle (design "H1 - Interaction
        /// with crash-reconcile", correction G3). The settle counter may advance only
        /// when the gate is CLEAR: <see cref="ParsekScenario.CrashReconcileInProgress"/>
        /// is false AND the live TestBatchMarker would NOT trigger a recovery reconcile
        /// (TestBatchMarker.ShouldReconcileOnLoad returns a non-recovery reason). Until
        /// both hold, H1 stays armed and waits, so the autorun baseline is captured
        /// against the reverted save, not a half-reverted one. Returns true when the
        /// gate is clear (reconcile NOT pending); the H1 caller passes the negation as
        /// SceneSettleDecision's reconcilePending input.
        /// </summary>
        internal static bool ReconcileGateClear(
            bool crashReconcileInProgress, bool markerWouldReconcile)
        {
            return !crashReconcileInProgress && !markerWouldReconcile;
        }

        /// <summary>
        /// Running aggregate for the multi-category driver (design "H1 - Multi-category
        /// selector"). Each token runs its OWN batch (its own campaign-isolation baseline)
        /// and emits its OWN per-token BATCH_COMPLETE line; the driver resets the runner
        /// before each token so that per-token line is scoped to that category alone, then
        /// folds the category's final counts into this tally for the single aggregate
        /// summary line. Kept pure so the union math is xUnit-testable without a live runner.
        /// </summary>
        internal struct MultiCategoryBatchTally
        {
            public int Total;
            public int Passed;
            public int Failed;
            public int Skipped;
            public int Batches;
        }

        /// <summary>
        /// Folds one completed category batch's final counts into the running aggregate
        /// (design "H1 - Multi-category selector"). The caller reads each category's counts
        /// off its own tests IMMEDIATELY after that token's batch settles and BEFORE the
        /// next token's runner reset, so the union sum is exact even though the per-token
        /// reset wipes the prior category's live statuses. Pure: no runner / Unity state.
        /// </summary>
        internal static MultiCategoryBatchTally AccumulateCategoryBatch(
            MultiCategoryBatchTally running,
            int categoryConsidered,
            int categoryPassed,
            int categoryFailed,
            int categorySkipped)
        {
            return new MultiCategoryBatchTally
            {
                Total = running.Total + categoryConsidered,
                Passed = running.Passed + categoryPassed,
                Failed = running.Failed + categoryFailed,
                Skipped = running.Skipped + categorySkipped,
                Batches = running.Batches + 1,
            };
        }
    }
}
