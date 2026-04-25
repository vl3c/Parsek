using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for "post-RP tech-unlock stickiness" (todo-and-known-bugs.md item 12,
    /// 2026-04-25 review follow-ups).
    ///
    /// Background. <see cref="RewindInvoker.RunStripActivateMarker"/> calls
    /// <c>LedgerOrchestrator.RecalculateAndPatch(double.MaxValue)</c> after Strip specifically
    /// because passing a non-null cutoff flips <c>bypassPatchDeferral = true</c> inside
    /// <see cref="LedgerOrchestrator"/> and routes the walk through the rewind-only tech-tree
    /// patch path. Without that bypass the post-RP tech unlocks would silently disappear after
    /// a rewind: KSP's R&amp;D state was just overwritten by the old quicksave, the live ledger
    /// still carries the post-RP <c>RnDTechResearch</c> action, but the orchestrator's "skip
    /// tech patch when no cutoff is supplied" guard would prevent that action from being
    /// re-applied. §2.3 of the rewind-staging design ("career state sticks across rewinds")
    /// requires the unlock to survive.
    ///
    /// These tests pin both halves of that contract:
    /// - With a non-null cutoff (here <c>double.MaxValue</c>), the rewind tech-tree patch
    ///   path runs and <see cref="KspStatePatcher.BuildTargetTechIdsForPatch"/> includes
    ///   BOTH the pre-RP baseline tech and the post-RP <c>ScienceSpending</c> action.
    /// - With a null cutoff, the orchestrator logs "skipping tech-tree patch to preserve
    ///   live unlocks" and never builds a target set — the discriminator that proves the
    ///   first assertion is actually exercising the rewind branch.
    /// </summary>
    [Collection("Sequential")]
    public class RewindTechStickinessTests : IDisposable
    {
        private const string OrchestratorTag = "[LedgerOrchestrator]";
        private const string PatcherTag = "[KspStatePatcher]";

        private const string PreRpTechId = "basicScience";
        private const string PostRpTechId = "engineering101";

        private const double PreRpUt = 100.0;
        private const double RpUt = 150.0;
        private const double PostRpUt = 200.0;

        private readonly List<string> logLines = new List<string>();

        public RewindTechStickinessTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Fixture helpers
        // ================================================================

        /// <summary>
        /// Mirrors the on-disk RP quicksave: a <see cref="GameStateBaseline"/> at <see cref="RpUt"/>
        /// whose <c>researchedTechIds</c> contains ONLY the pre-RP unlock. Whatever the player
        /// unlocked after the RP (here <see cref="PostRpTechId"/>) must come back via the post-RP
        /// ledger action that survives the rewind, NOT via the baseline.
        /// </summary>
        private static GameStateBaseline MakeRpBaseline()
        {
            var baseline = new GameStateBaseline { ut = RpUt };
            baseline.researchedTechIds.Add(PreRpTechId);
            return baseline;
        }

        /// <summary>
        /// Builds the same pre-RP and post-RP <c>RnDTechResearch -&gt; ScienceSpending</c>
        /// actions used end-to-end: pre-RP at <see cref="PreRpUt"/> matches the baseline,
        /// post-RP at <see cref="PostRpUt"/> is the one whose stickiness we are pinning.
        /// </summary>
        private static List<GameAction> MakeTechActions()
        {
            return new List<GameAction>
            {
                new GameAction
                {
                    UT = PreRpUt,
                    Type = GameActionType.ScienceSpending,
                    NodeId = PreRpTechId,
                    Cost = 5f,
                    Affordable = true,
                    RecordingId = "rec-pre-rp"
                },
                new GameAction
                {
                    UT = PostRpUt,
                    Type = GameActionType.ScienceSpending,
                    NodeId = PostRpTechId,
                    Cost = 45f,
                    Affordable = true,
                    RecordingId = "rec-post-rp"
                }
            };
        }

        private static void SeedLedgerForRewindWalk()
        {
            // ScienceInitial seed lets the science module process the spendings without
            // tripping the "no seed yet" early-return in PatchScience. Amount is enough
            // to keep both spendings affordable when the engine walks them.
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = 1000f
            });
            foreach (var action in MakeTechActions())
                Ledger.AddAction(action);
        }

        // ================================================================
        // BuildTargetTechIdsForPatch — the building block invoked from
        // RecalculateAndPatchCore when utCutoff.HasValue (rewind path).
        // ================================================================

        [Fact]
        public void BuildTargetTechIdsForPatch_WithMaxValueCutoff_IncludesBothPreAndPostRpUnlocks()
        {
            // Recreates the exact shape RewindInvoker.RunStripActivateMarker hands to the
            // patcher: the RP quicksave's baseline (pre-RP unlock only) PLUS the live
            // ledger's pre-RP and post-RP ScienceSpending actions.
            var baselines = new List<GameStateBaseline> { MakeRpBaseline() };
            var actions = MakeTechActions();

            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                baselines,
                actions,
                utCutoff: double.MaxValue);

            // §2.3 stickiness: BOTH unlocks must be in the target set so PatchTechTree
            // would re-apply them on top of the just-loaded RP quicksave's R&D state.
            Assert.NotNull(target);
            Assert.Equal(2, target.Count);
            Assert.Contains(PreRpTechId, target);
            Assert.Contains(PostRpTechId, target);
        }

        [Fact]
        public void BuildTargetTechIdsForPatch_DiscriminatorBaselineSnapshotAtRpUt_OmitsPostRpUnlockOnReplayCutoff()
        {
            // Discriminator: prove the previous test is exercising the rewind path and not
            // trivially passing because the baseline already had everything. Using a finite
            // cutoff at the RP UT (the quicksave snapshot moment), the post-RP action is
            // filtered out — only the pre-RP unlock survives. This is the shape a non-rewind
            // walk would take, and it is exactly what the §2.3 fix avoids by passing
            // double.MaxValue (so post-RP ledger actions ARE re-applied).
            var baselines = new List<GameStateBaseline> { MakeRpBaseline() };
            var actions = MakeTechActions();

            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                baselines,
                actions,
                utCutoff: RpUt);

            Assert.NotNull(target);
            Assert.Single(target);
            Assert.Contains(PreRpTechId, target);
            Assert.DoesNotContain(PostRpTechId, target);
        }

        // ================================================================
        // RecalculateAndPatch end-to-end — the cutoff being non-null is what
        // flips bypassPatchDeferral=true and routes the walk through the
        // rewind-only tech-tree patch branch.
        // ================================================================

        [Fact]
        public void RecalculateAndPatch_WithMaxValueCutoff_EnablesRewindTechTreePatchPath()
        {
            // Seed the live ledger to mirror "RP at UT=150, post-RP unlock at UT=200, now
            // post-rewind invoke calls RecalculateAndPatch(double.MaxValue) per
            // RewindInvoker.RunStripActivateMarker."
            SeedLedgerForRewindWalk();
            GameStateStore.AddBaseline(MakeRpBaseline());

            LedgerOrchestrator.RecalculateAndPatch(double.MaxValue);

            // The rewind-only branch ran (utCutoff.HasValue, no patch deferral, target
            // tech set built including both the baseline tech and the post-RP unlock).
            Assert.Contains(logLines, l =>
                l.Contains(OrchestratorTag)
                && l.Contains("rewind-path tech-tree patch enabled")
                && l.Contains("targetCount=2"));

            // The non-rewind skip log MUST NOT have fired — otherwise the post-RP unlock
            // would have been silently dropped (the regression we are pinning).
            Assert.DoesNotContain(logLines, l =>
                l.Contains(OrchestratorTag)
                && l.Contains("skipping tech-tree patch to preserve live unlocks"));

            // PatchAll completed (deferral bypass worked). PatchTechTree itself short-
            // circuits because ResearchAndDevelopment.Instance is null in xUnit — that
            // skip log is the witness that the call site was reached at all.
            Assert.Contains(logLines, l =>
                l.Contains(PatcherTag) && l.Contains("PatchAll complete"));
            Assert.Contains(logLines, l =>
                l.Contains(PatcherTag)
                && l.Contains("PatchTechTree: ResearchAndDevelopment.Instance is null"));
        }

        [Fact]
        public void RecalculateAndPatch_WithNullCutoff_SkipsTechTreePatchAndWouldDropPostRpUnlock()
        {
            // Discriminator for the end-to-end test above: with a null cutoff (the normal
            // commit / warp-exit / KSP-load path), the orchestrator does NOT build a
            // target tech set. If RewindInvoker forgot to pass double.MaxValue, this is
            // the path it would take — and the post-RP unlock would silently disappear
            // because the rewind quicksave just overwrote KSP's R&D state with the older
            // baseline. The "skipping tech-tree patch" verbose log is the canary.
            SeedLedgerForRewindWalk();
            GameStateStore.AddBaseline(MakeRpBaseline());

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l =>
                l.Contains(OrchestratorTag)
                && l.Contains("no cutoff supplied")
                && l.Contains("skipping tech-tree patch to preserve live unlocks"));

            Assert.DoesNotContain(logLines, l =>
                l.Contains(OrchestratorTag)
                && l.Contains("rewind-path tech-tree patch enabled"));
        }
    }
}
