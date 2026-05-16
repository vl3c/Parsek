using System;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Post-commit "Create Supply Route?" dialog. Fired by
    /// <see cref="MergeDialog.OnTreeCommitted"/> whenever the merge dialog
    /// commits a tree; checks the tree for route eligibility via
    /// <see cref="RouteAnalysisEngine.AnalyzeTree"/> and, when eligible,
    /// offers the player a chance to commit a <see cref="Route"/> to
    /// <see cref="RouteStore"/>.
    /// </summary>
    /// <remarks>
    /// Tests bypass the live <see cref="PopupDialog"/> via the
    /// <see cref="TestHookForConfirm"/> seam (mirrors
    /// <see cref="CommittedActionDialog.TestHookForTesting"/>): when set,
    /// <see cref="Spawn"/> calls the hook synchronously instead of spawning
    /// the real dialog, then dispatches the returned inputs to
    /// <see cref="OnConfirm"/> or <see cref="OnCancel"/>.
    /// </remarks>
    internal static class RouteCreationDialog
    {
        private const string Tag = "RouteUI";
        private const string DialogName = "ParsekRouteCreation";
        private const string LockId = "ParsekRouteCreationDialog";

        /// <summary>
        /// Test seam. Production callers leave this null; tests assign it to
        /// drive the dialog without spinning up Unity. The hook returns the
        /// inputs that the production dialog WOULD have collected, including
        /// an <see cref="RouteCreationInputsForTesting.OutcomeAction"/> of
        /// <c>"confirm"</c> or <c>"cancel"</c>. The hook may also inspect
        /// the cached state (<see cref="cachedResult"/> / <see cref="cachedTree"/>)
        /// via the test-only accessors to mutate them mid-dialog and
        /// exercise stale-tree paths.
        /// </summary>
        internal static Func<RouteCreationInputsForTesting> TestHookForConfirm;

        /// <summary>
        /// Inputs that the production dialog would collect from the player.
        /// Tests populate this directly via <see cref="TestHookForConfirm"/>.
        /// </summary>
        internal struct RouteCreationInputsForTesting
        {
            public string Name;
            public double DispatchIntervalSeconds;
            /// <summary>"confirm" or "cancel". Anything else is treated as cancel.</summary>
            public string OutcomeAction;
        }

        // Cached state between Spawn / OnConfirm / OnCancel. Only ever set on
        // the unity main thread; static is fine because the dialog is modal
        // and there is exactly one of it at a time.
        private static RouteAnalysisResult cachedResult;
        private static RecordingTree cachedTree;
        private static bool dialogOpen;

        /// <summary>
        /// Entry point from the post-commit hook. Analyses the tree and
        /// spawns the dialog when eligible. No-ops with an Info log on null
        /// inputs or non-eligible analysis.
        /// </summary>
        internal static void TryShow(RecordingTree committedTree)
        {
            if (committedTree == null)
            {
                ParsekLog.Info(Tag, "TryShow skipped: committedTree=null");
                return;
            }

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(committedTree);
            if (result == null)
            {
                ParsekLog.Info(Tag,
                    $"TryShow skipped: analysis=null tree={committedTree.Id ?? "<none>"}");
                return;
            }

            ParsekLog.Info(Tag,
                $"TryShow tree={committedTree.Id ?? "<none>"} status={result.Status} " +
                $"eligible={(result.IsEligible ? "yes" : "no")}");

            if (!result.IsEligible)
            {
                ParsekLog.Info(Tag,
                    $"TryShow: route not eligible tree={committedTree.Id ?? "<none>"} status={result.Status} — not spawning dialog");
                return;
            }

            Spawn(result, committedTree);
        }

        private static void Spawn(RouteAnalysisResult result, RecordingTree tree)
        {
            cachedResult = result;
            cachedTree = tree;
            dialogOpen = true;

            // Acquire input lock BEFORE the test-hook short-circuit so tests
            // can assert lock state during the hook callback. Mirrors
            // MergeDialog.LockInput (MergeDialog.cs:90-94): blocks every other
            // player input — KSC buildings, scene-change shortcuts, vessel
            // controls — while the modal Create Supply Route? dialog is up.
            // Wrapped in try/catch because InputLockManager.SetControlLock
            // fires GameEvents.onInputLocksModified, which can NRE in test
            // contexts that have not initialized the event dispatcher.
            try
            {
                InputLockManager.SetControlLock(ControlTypes.All, LockId);
                ParsekLog.Verbose(Tag, $"Input lock set: {LockId}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Spawn: SetControlLock threw {ex.GetType().Name}: {ex.Message}; continuing");
            }

            Func<RouteCreationInputsForTesting> hook = TestHookForConfirm;
            if (hook != null)
            {
                ParsekLog.Info(Tag,
                    $"dialog spawned (test hook): tree={tree.Id ?? "<none>"} " +
                    $"recording={result.SourceRecording?.RecordingId ?? "<none>"}");
                RouteCreationInputsForTesting response = hook();

                if (string.Equals(response.OutcomeAction, "confirm", StringComparison.Ordinal))
                    OnConfirm(response);
                else
                    OnCancel();
                return;
            }

            ParsekLog.Info(Tag,
                $"dialog spawned: tree={tree.Id ?? "<none>"} " +
                $"recording={result.SourceRecording?.RecordingId ?? "<none>"}");

            // v0 production dialog: summary-only with a Create / Cancel pair.
            // The in-dialog interval/name input wiring is Phase 3 work; for
            // v0 we surface the summary block and use a default interval
            // equal to the recording's transit duration so the route is
            // safe to dispatch immediately. The plan accepts this fallback.
            Game.Modes mode = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Mode
                : Game.Modes.SANDBOX;
            string body = RouteCreationFormatters.BuildSummaryBlock(result, mode);
            double defaultInterval = result.SourceRecording != null
                ? Math.Max(1.0, result.SourceRecording.EndUT - result.SourceRecording.StartUT)
                : 1.0;
            string defaultName = RouteCreationFormatters.GenerateDefaultRouteName(result);

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    DialogName,
                    body,
                    "Create Supply Route?",
                    HighLogic.UISkin,
                    new[]
                    {
                        new DialogGUIButton("Create Route", () =>
                        {
                            OnConfirm(new RouteCreationInputsForTesting
                            {
                                Name = defaultName,
                                DispatchIntervalSeconds = defaultInterval,
                                OutcomeAction = "confirm"
                            });
                        }),
                        new DialogGUIButton("Cancel", OnCancel),
                    }),
                false,
                HighLogic.UISkin);
        }

        /// <summary>Test-only read access to <see cref="cachedResult"/>.</summary>
        internal static RouteAnalysisResult CachedResultForTesting => cachedResult;
        /// <summary>Test-only read access to <see cref="cachedTree"/>.</summary>
        internal static RecordingTree CachedTreeForTesting => cachedTree;
        /// <summary>Test-only flag mirroring <see cref="dialogOpen"/>.</summary>
        internal static bool DialogOpenForTesting => dialogOpen;
        /// <summary>
        /// Test-only accessor that reads the live <see cref="InputLockManager"/>
        /// state for our <see cref="LockId"/>. Returns true when the lock is
        /// in the stack, false when absent or when the underlying check
        /// throws (xUnit contexts without an initialized event dispatcher).
        /// </summary>
        internal static bool IsLockAcquiredForTesting
        {
            get
            {
                try { return InputLockManager.GetControlLock(LockId) != ControlTypes.None; }
                catch { return false; }
            }
        }

        private static void OnConfirm(RouteCreationInputsForTesting inputs)
        {
            try
            {
                // Stale-tree guard: re-analyse just before commit so a tree
                // mutated mid-dialog (e.g. another scene change retired the
                // source recording) is caught here instead of producing a
                // route pointing at vanished state.
                RouteAnalysisResult fresh = cachedTree != null
                    ? RouteAnalysisEngine.AnalyzeTree(cachedTree)
                    : null;
                if (fresh == null || !fresh.IsEligible)
                {
                    string status = fresh != null ? fresh.Status.ToString() : "<null>";
                    ParsekLog.Info(Tag,
                        $"OnConfirm: route no longer eligible status={status} — discarding inputs");
                    DismissIfOpen("source-no-longer-eligible");
                    return;
                }

                Game.Modes mode = HighLogic.CurrentGame != null
                    ? HighLogic.CurrentGame.Mode
                    : Game.Modes.SANDBOX;
                RouteBuilder.RouteCreationInputs builderInputs =
                    new RouteBuilder.RouteCreationInputs
                    {
                        Name = inputs.Name,
                        DispatchIntervalSeconds = inputs.DispatchIntervalSeconds
                    };
                RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                    fresh, cachedTree, builderInputs, mode);
                if (outcome.Route == null)
                {
                    ParsekLog.Info(Tag,
                        $"OnConfirm: BuildRoute rejected reason={outcome.RejectReason ?? "<none>"}");
                    DismissIfOpen("build-rejected");
                    return;
                }

                RouteStore.AddRoute(outcome.Route);
                // Verify it actually landed (covers the duplicate-id Warn
                // path inside AddRoute — Add does NOT throw on dup, it logs
                // a Warn and keeps the original). When the dup case fires
                // our newly-built route is dropped on the floor.
                if (!RouteStore.TryGetRoute(outcome.Route.Id, out _))
                {
                    ParsekLog.Info(Tag,
                        $"OnConfirm: AddRoute did not store new route id={outcome.Route.Id} (likely duplicate)");
                    DismissIfOpen("not-stored");
                    return;
                }

                ParsekLog.Info(Tag,
                    $"OnConfirm: route created id={outcome.Route.Id} name='{outcome.Route.Name ?? "<none>"}' " +
                    $"interval={inputs.DispatchIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)}");
                DismissIfOpen("confirmed");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"OnConfirm: unexpected exception {ex.GetType().Name}: {ex.Message}");
                DismissIfOpen("exception");
            }
        }

        private static void OnCancel()
        {
            ParsekLog.Info(Tag, "OnCancel: route creation canceled");
            DismissIfOpen("user-canceled");
        }

        /// <summary>
        /// Tear down the dialog and clear the cached result/tree. Safe to
        /// call when the dialog is already closed — emits a Verbose log line
        /// and returns. Used by scene-change cleanup and the button
        /// callbacks.
        /// </summary>
        internal static void DismissIfOpen(string reason)
        {
            if (!dialogOpen)
            {
                ParsekLog.Verbose(Tag,
                    $"DismissIfOpen: dialog not open, reason={reason ?? "<none>"}");
                return;
            }

            try
            {
                PopupDialog.DismissPopup(DialogName);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"DismissIfOpen: DismissPopup threw {ex.GetType().Name}: {ex.Message}; continuing cleanup");
            }
            // Release the input lock acquired in Spawn. Safe no-op when the
            // lock id is not in the stack (mirrors MergeDialog.ClearPendingFlag
            // semantics). Wrapped because RemoveControlLock fires
            // onInputLocksModified, which can NRE in test contexts that have
            // not initialized the event dispatcher.
            try
            {
                InputLockManager.RemoveControlLock(LockId);
                ParsekLog.Verbose(Tag, $"Input lock released: {LockId}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"DismissIfOpen: RemoveControlLock threw {ex.GetType().Name}: {ex.Message}; continuing cleanup");
            }

            cachedResult = null;
            cachedTree = null;
            dialogOpen = false;
            ParsekLog.Info(Tag, $"dialog dismissed: reason={reason ?? "<none>"}");
        }

        /// <summary>Test seam: reset all static state (and the test hook).</summary>
        internal static void ResetForTesting()
        {
            cachedResult = null;
            cachedTree = null;
            dialogOpen = false;
            TestHookForConfirm = null;
            // Defensive: release any lingering input lock from a previous test
            // run. Mirrors the production DismissIfOpen cleanup path so test
            // ordering can't leave the lock stack contaminated.
            try { InputLockManager.RemoveControlLock(LockId); }
            catch { /* ignored — xUnit lacks the event dispatcher */ }
        }
    }
}
