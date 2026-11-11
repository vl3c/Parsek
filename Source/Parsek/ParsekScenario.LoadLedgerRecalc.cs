using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The ledger walk a load runs: the load clock and cutoff, the in-session recalculation, the
    // deferred seed, the budget deduction and the rewind resource adjustment.
    public partial class ParsekScenario
    {
        /// <summary>
        /// The ledger's clock: the Planetarium reading (<c>double.MaxValue</c> without one),
        /// raised to the ledger held-through UT while it is behind it (a Rewind-to-Launch lead
        /// window, <see cref="LedgerHeldWindow"/>).
        /// </summary>
        internal static double GetCurrentTimelineUTForLedgerRecalc()
        {
            if (CurrentTimelineUTProviderForTesting != null)
                return LedgerHeldWindow.ResolveLedgerNowUT(CurrentTimelineUTProviderForTesting());

            return Planetarium.fetch != null
                ? LedgerHeldWindow.ResolveLedgerNowUT(Planetarium.GetUniversalTime())
                : double.MaxValue;
        }

        /// <summary>
        /// Pure: does the in-session load's ledger walk take the current-UT cutoff? A stock
        /// revert takes it like any other in-session load (todo
        /// REVERT-AFTER-REWIND-WALKS-PAST-THE-CLOCK): the revert prune has already removed the
        /// reverted flight's own untagged rows, so any row still ahead of the loaded clock is
        /// committed future, and a walk without the cutoff would patch its gains into the pools
        /// before the clock reaches it.
        /// </summary>
        internal static bool ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
            bool loadedSceneSupportsCurrentUtCutoff,
            bool planetariumReady,
            bool hasPendingTree,
            ActiveTreeRestoreMode restoreMode,
            bool hasLiveRecorder,
            bool hasActiveUncommittedTree,
            bool hasFutureLedgerActions)
        {
            return loadedSceneSupportsCurrentUtCutoff
                && planetariumReady
                && !hasPendingTree
                && restoreMode == ActiveTreeRestoreMode.None
                && !hasLiveRecorder
                && !hasActiveUncommittedTree
                && hasFutureLedgerActions;
        }

        /// <summary>
        /// Pure: the clock the in-session load's ledger walk cuts off at. The loaded save's
        /// own <c>flightState.universalTime</c> when usable (finite, positive): inside OnLoad
        /// Planetarium can still read the clock from BEFORE the load (the same rule
        /// <see cref="KerbalsModule.ResolveWalkClockUT(double?,bool,double,bool,double,double)"/>
        /// applies to reservations). Else the Planetarium value when it is ready, else NaN.
        /// </summary>
        internal static double ResolveInSessionLoadLedgerClockUT(
            double loadedSaveUT, bool planetariumReady, double planetariumUT)
        {
            if (!double.IsNaN(loadedSaveUT) && !double.IsInfinity(loadedSaveUT) && loadedSaveUT > 0.0)
                return loadedSaveUT;
            if (planetariumReady && !double.IsNaN(planetariumUT) && !double.IsInfinity(planetariumUT))
                return planetariumUT;
            return double.NaN;
        }

        /// <summary>
        /// Pure: the clock the cold branch reconciles and walks at (design-load-restores-the-save.md
        /// 4.1, "The clock"). Inside a Space Center or Tracking Station OnLoad the Planetarium
        /// still reads the clock from before the load: stock <c>Game.Load</c> runs the scenario
        /// modules' OnLoad before <c>FlightState.Load</c> sets the universal time (decompiled KSP
        /// 1.12.5). So a ready Planetarium reading (positive) is replaced by the loaded save's own
        /// <c>flightState.universalTime</c> whenever that is finite and not negative; a reading that
        /// is not ready (0 or NaN, the boot load) is returned as it is, so the existing not-ready
        /// rules (keep every row, full replay, the deferred seed walks once the clock is set) hold.
        /// </summary>
        internal static double ResolveColdLoadClockUT(double loadedSaveUT, double planetariumUT)
        {
            if (double.IsNaN(planetariumUT) || double.IsInfinity(planetariumUT) || planetariumUT <= 0.0)
                return planetariumUT;
            if (!double.IsNaN(loadedSaveUT) && !double.IsInfinity(loadedSaveUT) && loadedSaveUT >= 0.0)
                return loadedSaveUT;
            return planetariumUT;
        }

        /// <summary>The loaded game's <c>flightState.universalTime</c> as written, or NaN.</summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static double ReadLoadedFlightStateUTRaw()
        {
            try
            {
                var flightState = HighLogic.CurrentGame?.flightState;
                return flightState != null ? flightState.universalTime : double.NaN;
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        /// <summary>
        /// The in-session OnLoad's ledger recalculation: reads the live load state, decides
        /// between the current-UT cutoff and the plain walk
        /// (<see cref="ShouldUseCurrentUtCutoffForPostRewindFlightLoad"/>), logs the decision
        /// and runs it. Returns true when the cutoff walk ran.
        /// </summary>
        /// <param name="planetariumUT">The Planetarium reading, logged beside the clock the
        /// walk used so a stale reading stays visible; NaN when not read.</param>
        /// <param name="reFlyDiscard">True for the walk after the load-time sweep removed a
        /// zombie Re-Fly attempt's ledger rows: the patch takes the pools back to the ledger
        /// as an authoritative reduction (rewind design section 2.12 Rule 3).</param>
        internal static bool RecalculateLedgerForInSessionLoad(
            bool isRevert, GameScenes loadedScene, bool planetariumReady, double loadedUT,
            double planetariumUT = double.NaN, bool reFlyDiscard = false)
        {
            // The load replaced the live pools with the save's: rows that were ahead of the
            // clock at an earlier patch say nothing about what this save charged.
            LedgerOrchestrator.InvalidateCommittedFutureSnapshot("in-session-load");
            bool loadedSceneSupportsCurrentUtCutoff =
                IsCurrentUtCutoffSupportedScene(loadedScene);
            bool hasPendingTree = PendingTreeSlot.HasPendingTree;
            ActiveTreeRestoreMode restoreMode = ScheduleActiveTreeRestoreOnFlightReady;
            bool hasLiveRecorder = GameStateRecorder.HasLiveRecorder();
            bool hasActiveUncommittedTree = GameStateRecorder.HasActiveUncommittedTree();
            bool hasFutureLedgerActions = LedgerOrchestrator.HasActionsAfterUT(loadedUT);
            bool useCurrentUtCutoffForPostRewindFlightLoad =
                ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
                    loadedSceneSupportsCurrentUtCutoff,
                    planetariumReady,
                    hasPendingTree,
                    restoreMode,
                    hasLiveRecorder,
                    hasActiveUncommittedTree,
                    hasFutureLedgerActions);
            ParsekLog.Info("Scenario",
                $"OnLoad: post-rewind current-UT cutoff decision useCurrentUtCutoff={useCurrentUtCutoffForPostRewindFlightLoad} " +
                $"loadedUT={loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"planetariumUT={planetariumUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"isRevert={isRevert} loadedScene={loadedScene} " +
                $"loadedSceneSupportsCurrentUtCutoff={loadedSceneSupportsCurrentUtCutoff} " +
                $"planetariumReady={planetariumReady} hasPendingTree={hasPendingTree} " +
                $"restoreMode={restoreMode} hasLiveRecorder={hasLiveRecorder} " +
                $"hasActiveUncommittedTree={hasActiveUncommittedTree} " +
                $"hasFutureLedgerActions={hasFutureLedgerActions}");
            if (useCurrentUtCutoffForPostRewindFlightLoad)
            {
                ParsekLog.Info("Scenario",
                    $"OnLoad: post-rewind scene-load recalc using current-UT cutoff {loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    "to keep future funds/contracts filtered until replay catches up");
                if (reFlyDiscard)
                    LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(
                        loadedUT, "post-rewind-load", reFlyDiscard: true);
                else
                    RecalculateAndPatchForPostRewindFlightLoad(loadedUT);
            }
            else
            {
                LedgerOrchestrator.RecalculateAndPatch(reFlyDiscard: reFlyDiscard);
            }
            return useCurrentUtCutoffForPostRewindFlightLoad;
        }

        /// <summary>
        /// Scenes whose loads keep committed future actions out of the patched career until
        /// the clock reaches them (current-UT cutoff), and whose cold load preserves those
        /// rows instead of pruning them. The Tracking Station is aligned with FLIGHT and the
        /// Space Center (owner ruling 2026-10-06); like the Space Center it applies a crossed
        /// action as its clock moves (<see cref="CareerLedgerClockCursor"/>). The editor
        /// takes the cutoff too: a plain walk there writes committed future earnings onto the
        /// live funds, and the VAB / SPH would let the player build with them. Its clock does
        /// not move, so it needs no cursor.
        /// </summary>
        internal static bool IsCurrentUtCutoffSupportedScene(GameScenes scene)
        {
            return scene == GameScenes.FLIGHT
                || scene == GameScenes.SPACECENTER
                || scene == GameScenes.TRACKSTATION
                || scene == GameScenes.EDITOR;
        }

        /// <summary>
        /// Resolves the launch-boundary cutoff for the revert orphan-ledger prune. Pure so the
        /// cutoff/strictness contract is unit-testable outside the OnLoad lifecycle.
        ///
        /// <para>Revert-to-Launch rewinds the game clock to the launch instant, so
        /// <paramref name="loadedUT"/> is the exact launch UT; the at-launch rollout is kept
        /// (the vessel stays on the pad), hence <paramref name="inclusive"/>=false. Revert to
        /// the editor (VAB/SPH, <see cref="RevertKind.Prelaunch"/>) does NOT rewind the clock, so
        /// <paramref name="loadedUT"/> is the revert-moment UT (after the in-flight actions) and
        /// is useless as a launch boundary. <paramref name="editorBoundaryUT"/> (the captured
        /// fresh-launch UT, else the VesselRollout-spend UT, via
        /// <see cref="ResolveEditorRevertBoundaryUT"/>) is the real launch UT, and the rollout is
        /// dropped too (KSP refunds it), hence <paramref name="inclusive"/>=true. When neither is
        /// available (NaN, e.g. a quickload-resumed free / science-mode vessel) the editor case
        /// falls back to <paramref name="loadedUT"/>, which prunes nothing harmful rather than
        /// risking a wrong cutoff.</para>
        /// </summary>
        internal static double ResolveRevertPruneCutoff(
            RevertKind revertKind, double loadedUT, double editorBoundaryUT, out bool inclusive)
        {
            if (revertKind == RevertKind.Prelaunch)
            {
                inclusive = true;
                return double.IsNaN(editorBoundaryUT) ? loadedUT : editorBoundaryUT;
            }

            inclusive = false;
            return loadedUT;
        }

        /// <summary>
        /// Scene-load follow-up recalculation for post-rewind current-timeline
        /// case where the loaded UT is still behind future committed actions.
        /// Filters the walk to the already-captured loaded UT while preserving the
        /// normal patch-deferral and same-branch repeatable-record behavior.
        /// </summary>
        internal static void RecalculateAndPatchForPostRewindFlightLoad(double loadedUT)
        {
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(loadedUT, "post-rewind-load");
        }

        /// <summary>
        /// Captures an initial game state baseline if none exist yet and this is the initial load.
        /// </summary>
        private static void CaptureInitialBaseline()
        {
            if (!initialLoadDone && GameStateStore.BaselineCount == 0)
            {
                try
                {
                    GameStateStore.CaptureBaselineIfNeeded();
                }
                catch (System.Exception ex)
                {
                    ScenarioLog($"[Parsek Scenario] Failed to capture initial baseline: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Waits for resource singletons to be ready, then triggers RecalculateAndPatch
        /// so the ledger's FundsInitial/ScienceInitial/ReputationInitial actions capture
        /// correct values. Called on initial save load because OnLoad runs before KSP's
        /// Funding/R&amp;D/Reputation scenarios have loaded their data from the save file.
        /// </summary>
        private IEnumerator DeferredSeedAndRecalculate()
        {
            // Phase 0 (CAREER-SCIENCE-SEED-LOST-ON-FLIGHT-ROUTE): yield at least one frame
            // before probing anything.
            //
            // StartCoroutine runs a coroutine body SYNCHRONOUSLY up to its first yield, and
            // this one is started from OnLoad - which KSP itself calls from the middle of
            // ScenarioRunner.LoadModules, while it is still walking the save's SCENARIO nodes
            // and constructing the remaining ScenarioModules. Every readiness probe below can
            // be satisfied by whichever currency singleton happens to have been loaded BEFORE
            // Parsek's own module, so without this yield the "deferred" seed was not deferred
            // at all: on the FLIGHT route it ran inside OnLoad with Funding already present
            // and ResearchAndDevelopment / Reputation still null, seeded funds only, and left
            // science and reputation permanently unseeded (the later retry correctly refuses
            // to treat a mid-career pool as initial). One frame is enough - LoadModules is
            // synchronous, so by the next frame every scenario module for this scene exists.
            yield return null;

            // Phase 1: wait for the currency singletons to exist (non-null).
            //
            // Waits for ALL of the ones this game mode creates, not just any one: seeding
            // funds from a frame where R&D has not appeared yet is what dropped the science
            // seed. Career carries all three, Science only R&D, Sandbox none
            // (CurrencyScenarioReadiness.ExpectedFor), so Science and Sandbox no longer pay
            // the full 120-frame timeout for singletons stock never builds. Still bounded:
            // an unknown mode keeps the legacy all-three wait and falls through on timeout.
            var expectedSingletons = CurrencyScenarioReadiness.ExpectedForCurrentGame();
            int maxWait = CurrencySingletonWaitMaxFrames;
            while (maxWait-- > 0 && !CurrencyScenarioReadiness.AllExpectedPresent())
                yield return null;

            int singletonFramesWaited = (CurrencySingletonWaitMaxFrames - 1) - maxWait;
            bool allSingletonsPresent = CurrencyScenarioReadiness.AllExpectedPresent();

            if (Funding.Instance == null && ResearchAndDevelopment.Instance == null
                && Reputation.Instance == null)
            {
                ParsekLog.Verbose("Scenario",
                    "DeferredSeed: no resource singletons available (sandbox mode) — skipping");
                yield break;
            }

            // Phase 2: wait for every present singleton to have LOADED its save value.
            //
            // The readiness signal is positive, not a non-zero value: a singleton is loaded
            // once the game's ProtoScenarioModule for it holds it as moduleRef, which stock
            // assigns only after the module's OnLoad returned
            // (CurrencyScenarioReadiness.IsScenarioModuleLoaded). The old gate spun until
            // SOME pool read non-zero, so a StartingFunds = 0 career or a Science game at 0
            // science paid the full 600 frames on every load (KSP-SETTINGS-AUDIT S4). A
            // career with non-zero pools exits on the first check either way.
            int maxValueWait = 600; // ~10 seconds at 60fps
            while (maxValueWait-- > 0 && !CurrencyScenarioReadiness.AllPresentLoaded())
                yield return null;

            int framesWaited = 599 - maxValueWait; // post-decrement: 600->599 on first check

            // Phase 3 (BUG-F): wait for the universe clock to be initialized before deciding
            // whether to apply a current-UT ledger cutoff. On a cold load
            // Planetarium.GetUniversalTime() returns 0 until the scene's clock is set up;
            // cutting the committed ledger off at UT=0 filters out an entire established career
            // (funds collapse to the starting seed, science to 0). The value-wait above can exit
            // immediately (singletons already carry their save values) while the clock is still
            // 0, so an explicit clock-readiness wait is needed. Spin until the clock reports a
            // real positive UT, or bail on timeout — the recalc below then falls back to the
            // safe full replay (cutoffUT=null), which never wipes an established career.
            // TryReadReadyUniverseTime wraps Planetarium.GetUniversalTime() in a try/catch
            // (it can throw during very early load / scene teardown — see Update()), so the
            // per-frame poll cannot kill this coroutine. A throw / null fetch is treated as
            // not-ready (keep waiting) and yields currentUT=0.0, which routes to full replay.
            int maxUtWait = 600; // ~10 seconds at 60fps
            double currentUT = 0.0;
            bool clockReady = false;
            while (maxUtWait-- > 0)
            {
                clockReady = TryReadReadyUniverseTime(out currentUT);
                if (clockReady)
                    break;
                yield return null;
            }
            int utFramesWaited = 599 - maxUtWait;

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Scenario",
                $"DeferredSeed: expected singletons={CurrencyScenarioReadiness.Format(expectedSingletons)} " +
                $"present={allSingletonsPresent} after " +
                $"{singletonFramesWaited} frames, loaded after {framesWaited} frames, " +
                $"clock ready={clockReady} after {utFramesWaited} frames (currentUT={currentUT.ToString("R", ic)}) — " +
                $"Funding={(Funding.Instance != null ? Funding.Instance.Funds.ToString("F0", ic) : "null")}, " +
                $"Science={(ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science.ToString("F0", ic) : "null")}, " +
                $"Rep={(Reputation.Instance != null ? Reputation.Instance.reputation.ToString("F1", ic) : "null")}");

            // Route through the guarded helper: it applies the current-UT cutoff only when the
            // clock is ready (currentUT > 0) AND committed actions still lie ahead of it
            // (post-rewind / future-timeline case), otherwise it replays the full committed
            // ledger. A not-ready clock therefore restores an established career instead of
            // wiping it.
            // A cold load's sweep runs after OnKspLoad, so this is the first walk after it:
            // when it discarded a zombie Re-Fly attempt, the pools the save carries still hold
            // what that attempt earned, and only the Re-Fly discard walk takes them back to the
            // ledger (rewind design section 2.12 Rule 3; the drawdown guard keeps them otherwise).
            bool reFlyDiscard = LoadTimeSweep.SweepDiscardedAnAttempt(
                LoadTimeSweep.LedgerRowsRemovedByLastRun, LoadTimeSweep.ZombiesDiscardedByLastRun);
            if (reFlyDiscard)
                ParsekLog.Info("Scenario",
                    $"DeferredSeed: the load-time sweep discarded {LoadTimeSweep.ZombiesDiscardedByLastRun} " +
                    $"zombie attempt recording(s) ({LoadTimeSweep.LedgerRowsRemovedByLastRun} ledger row(s)); " +
                    "walking as a Re-Fly discard");
            if (IsCurrentUtCutoffSupportedScene(HighLogic.LoadedScene))
            {
                LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                    currentUT,
                    "deferred-seed",
                    reFlyDiscard);
            }
            else
            {
                LedgerOrchestrator.RecalculateAndPatch(reFlyDiscard: reFlyDiscard);
            }
        }

        /// <summary>
        /// Pure: true when the in-session load's ledger patch ran before every currency
        /// module this game creates existed and had loaded its save value, so a pool the
        /// patch skipped (or wrote before the module's own OnLoad overwrote it) needs a
        /// second patch.
        /// </summary>
        internal static bool ShouldRepatchAfterInSessionLoad(
            CurrencySingletons expected, CurrencySingletons present, CurrencySingletons loaded)
        {
            return !CurrencyScenarioReadiness.Covers(expected, present)
                || !CurrencyScenarioReadiness.Covers(present, loaded);
        }

        /// <summary>
        /// Repeats the in-session load's ledger patch once every currency module has loaded,
        /// with the same walk (the loaded save's cutoff, or the plain walk) the load chose.
        /// Without it a pool whose module loads after Parsek's keeps the save's value: after
        /// a quicksave load the science spent by a later committed action stays available.
        /// The coroutine dies with this module, so a later load never sees a stale one.
        /// </summary>
        private IEnumerator RepatchAfterInSessionLoadWhenCurrenciesLoaded(
            bool useCutoff, double cutoffUT, bool reFlyDiscard)
        {
            // OnLoad runs inside ScenarioRunner.LoadModules; the remaining modules are
            // constructed and loaded before the next frame.
            yield return null;
            Game game = HighLogic.CurrentGame;
            int maxWait = CurrencySingletonWaitMaxFrames;
            while (maxWait-- > 0 && !CurrencyScenarioReadiness.AllExpectedPresent())
                yield return null;
            int maxLoadWait = 600;
            while (maxLoadWait-- > 0 && !CurrencyScenarioReadiness.AllPresentLoaded())
                yield return null;

            if (!ReferenceEquals(game, HighLogic.CurrentGame))
            {
                ParsekLog.Info("Scenario",
                    "OnLoad: in-session ledger re-patch skipped: the game changed while waiting");
                yield break;
            }

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("Scenario",
                $"OnLoad: in-session ledger re-patch after the currency modules loaded " +
                $"(present={CurrencyScenarioReadiness.Format(CurrencyScenarioReadiness.PresentNow())} " +
                $"loaded={CurrencyScenarioReadiness.Format(CurrencyScenarioReadiness.LoadedNow())} " +
                $"useCutoff={useCutoff} cutoffUT={cutoffUT.ToString("R", ic)} reFlyDiscard={reFlyDiscard})");
            // A pool the first patch skipped gets its first patch here, so a Discard Re-fly load
            // walks it as the same time-travel patch.
            if (useCutoff && reFlyDiscard)
                LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(
                    cutoffUT, "post-rewind-load", reFlyDiscard: true);
            else if (useCutoff)
                RecalculateAndPatchForPostRewindFlightLoad(cutoffUT);
            else
                LedgerOrchestrator.RecalculateAndPatch(reFlyDiscard: reFlyDiscard);
        }

        /// <summary>
        /// Tops up any resource baseline <see cref="GameStateRecorder.Subscribe"/> could not
        /// seed because its currency singleton had not been constructed yet.
        ///
        /// <para>
        /// Zero cost in the common case: when Subscribe seeded all three the coroutine exits
        /// before its first yield. Otherwise it polls until every baseline is filled or the
        /// bounded budget runs out (sandbox, where the singletons never appear).
        /// </para>
        ///
        /// <para>
        /// Bails the moment <paramref name="recorder"/> stops being the live recorder: OnLoad
        /// replaces the recorder on every scene load, and a coroutine left over from a previous
        /// load must not write baselines into an instance nothing reads.
        /// </para>
        /// </summary>
        private IEnumerator SeedRecorderResourceBaselinesWhenReady(GameStateRecorder recorder)
        {
            if (recorder == null || !recorder.HasUnseededResourceBaselines)
                yield break;

            int maxWait = CurrencySingletonWaitMaxFrames;
            while (maxWait-- > 0)
            {
                yield return null;

                if (recorder != stateRecorder)
                {
                    ParsekLog.Verbose("Scenario",
                        "SeedRecorderResourceBaselines: recorder replaced by a newer scene load - abandoning top-up");
                    yield break;
                }

                if (recorder.SeedResourceState())
                {
                    ParsekLog.Verbose("Scenario",
                        "SeedRecorderResourceBaselines: all resource baselines seeded after " +
                        ((CurrencySingletonWaitMaxFrames - 1) - maxWait).ToString(CultureInfo.InvariantCulture) +
                        " frame(s) - first currency change of this scene will be captured");
                    yield break;
                }
            }

            ParsekLog.Verbose("Scenario",
                "SeedRecorderResourceBaselines: gave up after " +
                CurrencySingletonWaitMaxFrames.ToString(CultureInfo.InvariantCulture) +
                " frames with baselines still unseeded (expected in sandbox, where the " +
                "currency singletons never load)");
        }

        /// <summary>
        /// Reads the universe clock for the deferred-seed readiness wait, returning true only
        /// when the clock is initialized to a real positive UT. Wrapped in try/catch because
        /// <see cref="Planetarium.GetUniversalTime"/> can throw during very early load / scene
        /// teardown (same defensive pattern as <see cref="Update"/>); a throw or null fetch is
        /// treated as not-ready so the per-frame poll cannot kill the coroutine, and
        /// <paramref name="ut"/> is set to 0.0 (which the recalc routes to a safe full replay).
        /// </summary>
        private static bool TryReadReadyUniverseTime(out double ut)
        {
            ut = 0.0;
            if (Planetarium.fetch == null)
                return false;

            try
            {
                ut = Planetarium.GetUniversalTime();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Scenario",
                    $"DeferredSeed: Planetarium.GetUniversalTime threw {ex.GetType().Name}: " +
                    $"{ex.Message}; treating clock as not ready");
                ut = 0.0;
                return false;
            }

            return LedgerOrchestrator.IsCurrentUtReadyForCutoff(ut);
        }

        /// <summary>
        /// Waits for resource singletons to be available, then deducts committed
        /// budget from the game state. This ensures the KSP top bar and all
        /// purchase checks reflect available (non-committed) resources.
        /// </summary>
        private IEnumerator ApplyBudgetDeductionWhenReady()
        {
            // Wait until every resource singleton THIS GAME MODE creates is available (may
            // take a few frames after scene load). Science and Sandbox never build Funding /
            // Reputation, so keying on all three made them pay the full 120 frames.
            var expectedSingletons = CurrencyScenarioReadiness.ExpectedForCurrentGame();
            int maxWait = CurrencySingletonWaitMaxFrames; // ~2 seconds at 60fps
            while (maxWait-- > 0 && !CurrencyScenarioReadiness.AllExpectedPresent())
                yield return null;

            ParsekLog.Verbose("Scenario",
                $"ApplyBudgetDeduction: singletons ready after {CurrencySingletonWaitMaxFrames - maxWait} frames " +
                $"(expected={CurrencyScenarioReadiness.Format(expectedSingletons)}). " +
                $"Funding={Funding.Instance != null}, R&D={ResearchAndDevelopment.Instance != null}, Rep={Reputation.Instance != null}");

            if (budgetDeductionApplied)
            {
                ParsekLog.Verbose("Scenario",
                    "Budget deduction already applied for this revert load");
    
                yield break;
            }
            budgetDeductionApplied = true;

            // Audited for #527: this coroutine only runs on true revert follow-up
            // budget restoration. The rewind path marks this guard itself and
            // uses ApplyRewindResourceAdjustment(adjustedUT) instead.
            // This can run synchronously inside OnLoad (the singletons are often already
            // there), where Planetarium may still read the pre-load clock, so the loaded save's
            // own clock wins, as in the in-session load's walk.
            double planetariumUT;
            bool planetariumReady = TryReadReadyUniverseTime(out planetariumUT);
            double revertUT = ResolveInSessionLoadLedgerClockUT(
                KerbalsModule.ReadLoadedSaveUT(), planetariumReady, planetariumUT);
            RecalculateAndPatchAfterRevert(HighLogic.LoadedScene, !double.IsNaN(revertUT), revertUT);
        }

        /// <summary>
        /// The revert follow-up walk. A revert inside a rewound timeline still has committed
        /// future rows ahead of the reverted clock, so in a cutoff scene it walks at that clock
        /// (todo REVERT-AFTER-REWIND-WALKS-PAST-THE-CLOCK); with nothing ahead, or with no ready
        /// clock, it is the plain full replay it always was.
        /// </summary>
        internal static void RecalculateAndPatchAfterRevert(
            GameScenes loadedScene, bool clockReady, double revertUT)
        {
            bool cutoffScene = IsCurrentUtCutoffSupportedScene(loadedScene);
            ParsekLog.Info("Scenario",
                $"Revert budget walk: scene={loadedScene} cutoffScene={cutoffScene} clockReady={clockReady} " +
                $"revertUT={revertUT.ToString("R", CultureInfo.InvariantCulture)}");
            if (cutoffScene && clockReady)
            {
                LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                    revertUT, "revert-budget");
                return;
            }
            LedgerOrchestrator.RecalculateAndPatch();
        }

        /// <summary>
        /// Applies the differential resource adjustment after a rewind.
        /// Deferred via coroutine because Funding/R&amp;D/Reputation singletons
        /// are not available during OnLoad.
        /// </summary>
        private IEnumerator ApplyRewindResourceAdjustment()
        {
            // Capture rewind state before yielding — flags are cleared synchronously
            // in OnLoad after StartCoroutine returns.
            var saved = RewindContext.RewindReserved;
            double rewindUT = RewindContext.RewindUT;
            double adjustedUT = RewindContext.RewindAdjustedUT;
            double baselineFunds = RewindContext.RewindBaselineFunds;
            double baselineScience = RewindContext.RewindBaselineScience;
            float baselineRep = RewindContext.RewindBaselineRep;
            bool rewindGameWrittenToPersistent = RewindContext.RewindGameWrittenToPersistent;

            // CRITICAL: yield at least one frame before touching any singleton.
            // During OnLoad, singletons from the OLD scene may still be alive.
            // Without this yield, SetUniversalTime modifies the OLD Planetarium
            // which is then destroyed when the new scene finishes loading.
            yield return null;

            var ic = CultureInfo.InvariantCulture;

            // Apply adjusted UT unconditionally — Planetarium is always available
            // after the first yield. This must NOT be gated on resource singletons
            // (sandbox/science mode has no Funding/R&D/Reputation, but still needs UT).
            // UT=0 is valid (recording near game start with lead time clamped to 0).
            {
                double prePlanetariumUT = Planetarium.GetUniversalTime();
                Planetarium.SetUniversalTime(adjustedUT);
                RecordingStore.RewindUTAdjustmentPending = false;
                ParsekLog.Info("Rewind",
                    $"UT adjustment: {prePlanetariumUT.ToString("F1", ic)} → {adjustedUT.ToString("F1", ic)} " +
                    $"(post-set check: {Planetarium.GetUniversalTime().ToString("F1", ic)})");
            }

            // Wait for the resource singletons this game mode creates (all three in career,
            // R&D only in Science, none in Sandbox - the rest are permanently null there).
            int maxWait = CurrencySingletonWaitMaxFrames; // ~2 seconds at 60fps
            while (maxWait-- > 0 && !CurrencyScenarioReadiness.AllExpectedPresent())
                yield return null;

            // Pass the adjusted UT captured BEFORE `yield return null` above.
            // RewindContext.EndRewind() has already cleared the global by the time
            // this coroutine resumes, so we cannot read from RewindContext here.
            // The cutoff applies to career resources; crew reservations are rebuilt
            // from the full committed timeline inside LedgerOrchestrator.
            //
            // Drawdown-guard signal 5 (Blocker 1): this is the AUTHORITATIVE deferred
            // plain-rewind drawdown. Clear the in-progress flag in a finally so a clean OR
            // thrown patch both release it; the flag must stay true THROUGH this patch so
            // the guard authorizes any legitimate rewind reduction (all four other signals
            // are false here). NOT cleared by EndRewind (which ran before this resumed).
            try
            {
                // The same career cutoff as HandleRewindOnLoad's walk (the wound-back clock
                // raised to the ledger held-through UT, 0 included); the Planetarium stays at
                // adjustedUT.
                LedgerOrchestrator.RecalculateAndPatch(
                    LedgerHeldWindow.ResolveRewindCutoffUT(adjustedUT),
                    suppressSuspiciousDrawdownWarnings: true);
            }
            finally
            {
                RewindContext.EndRewindResourceAdjustment();
            }

            // Belt-and-suspenders guard: if some future refactor accidentally schedules
            // the normal revert budget-deduction coroutine during this rewind load, it
            // should no-op instead of patching the same balances again.
            budgetDeductionApplied = true;

            // The rewind wrote its own game to persistent.sfs before this scene loaded (with the
            // last live Parsek node); put the stripped world, the holds and the patched pools on
            // disk together so a crash from here on cold-loads a consistent file.
            if (rewindGameWrittenToPersistent)
                RewindLauncher.WritePersistentAfterRewindAdjustment();
        }
    }
}
