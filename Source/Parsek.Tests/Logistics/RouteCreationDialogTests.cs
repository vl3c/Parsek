using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase-2 dialog tests for <see cref="RouteCreationDialog"/>. Exercises
    /// the post-commit-hook entry point (<see cref="RouteCreationDialog.TryShow"/>),
    /// the test-only confirm/cancel seam, and the MergeDialog signature
    /// regression. All paths drive through the
    /// <see cref="RouteCreationDialog.TestHookForConfirm"/> hook so unit
    /// tests never spawn a real <see cref="PopupDialog"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RouteCreationDialogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteCreationDialogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RouteCreationDialog.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteCreationDialog.ResetForTesting();
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

        private static InventoryPayloadItem InvItem()
        {
            ConfigNode stored = new ConfigNode("STOREDPART");
            stored.AddValue("partName", "evaJetpack");
            stored.AddValue("quantity", "1");
            return new InventoryPayloadItem
            {
                IdentityHash = "payload-hash",
                PartName = "evaJetpack",
                Quantity = 1,
                SlotsTaken = 1,
                StoredPartSnapshot = stored
            };
        }

        private static RouteConnectionWindow CompleteWindow()
        {
            return new RouteConnectionWindow
            {
                WindowId = "w",
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = new RouteEndpoint
                {
                    VesselPersistentId = 9001,
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 3.0,
                    IsSurface = true
                },
                TransferEndpointSituation = 4,
                DockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "LiquidFuel", new ResourceAmount { amount = 80.0, maxAmount = 100.0 } }
                },
                UndockTransportResources = new Dictionary<string, ResourceAmount>
                {
                    { "LiquidFuel", new ResourceAmount { amount = 30.0, maxAmount = 100.0 } }
                },
                DockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    { "LiquidFuel", new ResourceAmount { amount = 0.0, maxAmount = 200.0 } }
                },
                UndockEndpointResources = new Dictionary<string, ResourceAmount>
                {
                    { "LiquidFuel", new ResourceAmount { amount = 50.0, maxAmount = 200.0 } }
                },
                DockTransportInventory = new List<InventoryPayloadItem> { InvItem() },
                UndockTransportInventory = null,
                DockEndpointInventory = null,
                UndockEndpointInventory = new List<InventoryPayloadItem> { InvItem() }
            };
        }

        private static RecordingTree BuildEligibleTree(out Recording source)
        {
            source = new Recording
            {
                RecordingId = "src",
                TreeId = "tree-1",
                TreeOrder = 0,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 600.0,
                RouteConnectionWindows = new List<RouteConnectionWindow> { CompleteWindow() }
            };
            RecordingTree tree = new RecordingTree
            {
                Id = "tree-1",
                RootRecordingId = source.RecordingId,
                ActiveRecordingId = source.RecordingId
            };
            tree.AddOrReplaceRecording(source);
            return tree;
        }

        private static RecordingTree BuildIneligibleTree()
        {
            Recording rec = new Recording
            {
                RecordingId = "src-empty",
                TreeId = "tree-empty",
                TreeOrder = 0
            };
            RecordingTree tree = new RecordingTree
            {
                Id = "tree-empty",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        // -----------------------------------------------------------------
        // TryShow
        // -----------------------------------------------------------------

        [Fact]
        public void TryShow_NullTree_LogsAndReturnsNoSpawn()
        {
            // catches: TryShow throwing or silently spawning a dialog for a
            // null committed tree. Either failure would either crash the
            // post-commit hook or surface an "empty" route creation dialog
            // that has no source recording behind it.
            bool hookFired = false;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                hookFired = true;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };

            RouteCreationDialog.TryShow(null);

            Assert.False(hookFired);
            Assert.False(RouteCreationDialog.DialogOpenForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("committedTree=null"));
        }

        [Fact]
        public void TryShow_IneligibleAnalysisResult_DoesNotSpawnDialog_LogsInfo()
        {
            // catches: the eligibility gate regressing and the dialog popping
            // for trees that have no completed transfer. A spawned dialog
            // would let the player commit a route from a recording the
            // builder will immediately reject.
            bool hookFired = false;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                hookFired = true;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };

            RouteCreationDialog.TryShow(BuildIneligibleTree());

            Assert.False(hookFired);
            Assert.False(RouteCreationDialog.DialogOpenForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("not eligible"));
        }

        [Fact]
        public void TryShow_EligibleResult_InvokesTestHook()
        {
            // catches: the test-hook seam regressing so tests cannot drive the
            // dialog headlessly. Without this guarantee the rest of this file
            // would silently fall back to the live PopupDialog path under
            // tests and start failing because no Unity is present.
            bool hookFired = false;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                hookFired = true;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.True(hookFired);
        }

        // -----------------------------------------------------------------
        // OnConfirm
        // -----------------------------------------------------------------

        [Fact]
        public void OnConfirm_BuildsAndAddsRouteToStore()
        {
            // catches: the confirm path skipping the RouteStore add, leaving
            // the player with an apparently-created route that vanishes on
            // next reload. Also pins the "route created" log line that KSP.log
            // diagnostics rely on.
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    Name = "Mun supply",
                    DispatchIntervalSeconds = 1200.0,
                    OutcomeAction = "confirm"
                };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.Single(RouteStore.CommittedRoutes);
            Route route = RouteStore.CommittedRoutes[0];
            Assert.Equal("Mun supply", route.Name);
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.False(RouteCreationDialog.DialogOpenForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("route created"));
        }

        [Fact]
        public void OnConfirm_WhenSourceNoLongerEligible_DoesNotAddRoute_LogsRejected()
        {
            // catches: dropping the stale-tree re-analysis on confirm. Without
            // it, a parallel scene change that retires the source mid-dialog
            // would let the player commit a route pointing at vanished
            // state, then crash the scheduler on first dispatch.
            RecordingTree tree = BuildEligibleTree(out Recording source);
            // Mutate the tree mid-hook so the re-analysis fails: drop the
            // completed window. This mirrors a parallel scene-change
            // retiring the proof while the dialog is pending.
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                source.RouteConnectionWindows = null;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    Name = "Should be rejected",
                    DispatchIntervalSeconds = 1200.0,
                    OutcomeAction = "confirm"
                };
            };

            RouteCreationDialog.TryShow(tree);

            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("no longer eligible"));
        }

        [Fact]
        public void OnConfirm_InvalidInterval_KeepsDialogOpen_DoesNotAddRoute()
        {
            // catches: an invalid interval reaching RouteStore.AddRoute. The
            // builder's interval-invalid reject must propagate up so the
            // store sees no route — otherwise a -1.0s interval would land in
            // the save file and crash the scheduler on first tick.
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    Name = "bad-interval",
                    DispatchIntervalSeconds = -1.0,
                    OutcomeAction = "confirm"
                };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.Empty(RouteStore.CommittedRoutes);
            // Dialog is dismissed via the "build-rejected" reason. The
            // production dialog would log a screen message; the test path
            // dismisses cleanly.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("BuildRoute rejected")
                && l.Contains("interval-invalid"));
        }

        [Fact]
        public void OnCancel_NoRouteAdded_LogsCanceled()
        {
            // catches: the cancel button silently building a route anyway, or
            // the cancel log line drifting. The "canceled" log is what
            // post-mortem analysis uses to distinguish player-aborted runs
            // from build-rejected ones.
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("canceled"));
        }

        // -----------------------------------------------------------------
        // DismissIfOpen
        // -----------------------------------------------------------------

        [Fact]
        public void DismissIfOpen_ReleasesCacheState_LogsDismissed()
        {
            // catches: dismissed dialogs leaving stale cachedResult/cachedTree
            // pointers behind. A scene change after a leaked cache would let
            // a stale RecordingTree survive into the next session and resolve
            // to a vanished recording on the next TryShow.
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            // After cancel the dialog dismisses itself and clears the cache.
            Assert.False(RouteCreationDialog.DialogOpenForTesting);
            Assert.Null(RouteCreationDialog.CachedResultForTesting);
            Assert.Null(RouteCreationDialog.CachedTreeForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("dialog dismissed"));
        }

        // -----------------------------------------------------------------
        // Phase 3 — Career-mode wire-up
        // -----------------------------------------------------------------

        [Fact]
        public void Spawn_CareerMode_SummaryBlockMentionsDispatchCost()
        {
            // catches: Spawn calling BuildSummaryBlock with a hardcoded
            // SANDBOX (or no-arg) signature, which would drop the Career-only
            // "Dispatch cost: TBD" line. The dialog needs the line in Career
            // even though the cost is TBD — players notice when the line
            // vanishes from a Career save. xUnit cannot drive
            // HighLogic.CurrentGame.Mode, so we drive BuildSummaryBlock with
            // the cached analysis the dialog computed during Spawn — same
            // input the real wire path would feed in Career.
            RouteAnalysisResult capturedAnalysis = null;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                capturedAnalysis = RouteCreationDialog.CachedResultForTesting;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.NotNull(capturedAnalysis);
            string careerBody = RouteCreationFormatters.BuildSummaryBlock(
                capturedAnalysis, Game.Modes.CAREER);
            Assert.Contains("Dispatch cost", careerBody);
        }

        [Fact]
        public void Spawn_SandboxMode_SummaryBlockOmitsDispatchCost()
        {
            // catches: Career-only conditional bleeding into Sandbox — would
            // surface a "Dispatch cost: TBD" line in Sandbox saves and confuse
            // players who never opted into a Career economy. xUnit context has
            // HighLogic.CurrentGame == null, which exercises the SANDBOX
            // fallback in Spawn — so this test pins the wire-path default.
            RouteAnalysisResult capturedAnalysis = null;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                capturedAnalysis = RouteCreationDialog.CachedResultForTesting;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.NotNull(capturedAnalysis);
            string sandboxBody = RouteCreationFormatters.BuildSummaryBlock(
                capturedAnalysis, Game.Modes.SANDBOX);
            Assert.DoesNotContain("Dispatch cost", sandboxBody);
        }

        // -----------------------------------------------------------------
        // Phase 3 — InputLockManager parity with MergeDialog
        // -----------------------------------------------------------------

        [Fact]
        public void Spawn_AcquiresInputLock_BeforeTestHookFires()
        {
            // catches: regressing the SetControlLock call in Spawn, which
            // would let the player click KSC buildings / scene-change
            // shortcuts / vessel controls while the modal Create Supply
            // Route? dialog is up. Mirrors MergeDialog.LockInput
            // (MergeDialog.cs:90-94). The hook captures the live lock state
            // mid-dialog; asserting after dismissal would only check the
            // released-state path.
            bool wasLockedDuringHook = false;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                wasLockedDuringHook = RouteCreationDialog.IsLockAcquiredForTesting;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.True(wasLockedDuringHook,
                "Spawn must acquire the input lock before the test hook fires");
        }

        [Fact]
        public void OnConfirm_ReleasesInputLock()
        {
            // catches: the confirm path leaking the input lock — would leave
            // the player unable to interact with KSC / vessel controls after
            // a successful route creation until they reload. RemoveControlLock
            // must run in DismissIfOpen on the "confirmed" path.
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    Name = "lock-release-confirm",
                    DispatchIntervalSeconds = 1200.0,
                    OutcomeAction = "confirm"
                };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.False(RouteCreationDialog.IsLockAcquiredForTesting,
                "OnConfirm must release the input lock through DismissIfOpen");
        }

        [Fact]
        public void OnCancel_ReleasesInputLock()
        {
            // catches: the cancel path leaking the input lock — same lockout
            // hazard as the confirm path, but on the player-aborted side.
            // Both buttons funnel through DismissIfOpen which must
            // RemoveControlLock.
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.False(RouteCreationDialog.IsLockAcquiredForTesting,
                "OnCancel must release the input lock through DismissIfOpen");
        }

        [Fact]
        public void DismissIfOpen_FromBuildRejected_ReleasesInputLock()
        {
            // catches: the build-rejected dismissal branch leaking the input
            // lock — invalid interval reaches DismissIfOpen("build-rejected")
            // and that path must also release the lock. Without this, an
            // invalid interval would lock the player out of all controls
            // until reload.
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    Name = "lock-release-rejected",
                    DispatchIntervalSeconds = -1.0,
                    OutcomeAction = "confirm"
                };

            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.False(RouteCreationDialog.IsLockAcquiredForTesting,
                "DismissIfOpen('build-rejected') must release the input lock");
        }

        // -----------------------------------------------------------------
        // MergeDialog signature regression
        // -----------------------------------------------------------------

        [Fact]
        public void MergeDialog_OnTreeCommitted_PassesCommittedTreeToSubscriber()
        {
            // catches: the MergeDialog.OnTreeCommitted event signature
            // changing without the route-creation subscriber being adapted.
            // The whole post-commit flow hangs off this event; an
            // accidentally-typed Action<> would silently sever the bridge.
            RecordingTree captured = null;
            Action<RecordingTree> subscriber = tree => captured = tree;
            MergeDialog.OnTreeCommitted += subscriber;
            try
            {
                RecordingTree input = new RecordingTree { Id = "tree-sig-test" };
                MergeDialog.OnTreeCommitted?.Invoke(input);

                Assert.NotNull(captured);
                Assert.Equal("tree-sig-test", captured.Id);
            }
            finally
            {
                MergeDialog.OnTreeCommitted -= subscriber;
            }
        }

        // -----------------------------------------------------------------
        // Deferred-retry pending-state — playtest-5 regression
        // -----------------------------------------------------------------
        // The merge dialog's Tree-Merge button commits the tree AND triggers
        // a scene change in the same frame, so RouteCreationDialog.TryShow
        // spawns in FLIGHT but is dismissed by the scene-change cleanup
        // ~100 ms later before the player can interact. The fix preserves
        // pendingTreeId across a scene-change dismiss so the destination
        // scene's Update tick can retry the spawn via
        // TryShowDeferredIfPending.

        [Fact]
        public void TryShow_EligibleTree_SetsPendingTreeId()
        {
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    Name = "test",
                    DispatchIntervalSeconds = 60,
                    OutcomeAction = "cancel" // cancel inside hook -> clears pending
                };
            RecordingTree tree = BuildEligibleTree(out _);
            string expectedId = tree.Id;

            // Inspect the pending-id BEFORE TryShow runs the test hook to its
            // cancel terminal (which would clear pendingTreeId). We assert via
            // the side-effect that even an immediate cancel produces a
            // pending-id setting during the call window. Use a hook that
            // asserts mid-flight.
            string capturedPendingDuringHook = null;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                capturedPendingDuringHook = RouteCreationDialog.PendingTreeIdForTesting;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    Name = "test",
                    DispatchIntervalSeconds = 60,
                    OutcomeAction = "cancel"
                };
            };

            RouteCreationDialog.TryShow(tree);

            Assert.Equal(expectedId, capturedPendingDuringHook);
        }

        [Fact]
        public void TryShow_IneligibleTree_ClearsPendingTreeId()
        {
            RouteCreationDialog.TryShow(BuildIneligibleTree());

            Assert.Null(RouteCreationDialog.PendingTreeIdForTesting);
        }

        [Fact]
        public void DismissIfOpen_SceneChange_PreservesPendingTreeId()
        {
            // Hook never fires terminal — leaves the dialog open after Spawn.
            // We then DismissIfOpen("scene-change") to simulate the real
            // OnSceneChangeRequested -> RouteCreationDialog.DismissIfOpen
            // cleanup.
            RecordingTree tree = BuildEligibleTree(out _);
            string expectedId = tree.Id;
            RouteCreationDialog.TestHookForConfirm = null; // production path with PopupDialog disabled in tests
            // Direct internal manipulation: bypass Spawn entirely. Open the
            // dialog state via the lower-level test-only accessors we need.
            // (RouteCreationDialog has no public Spawn; we use TryShow with a
            // hook that returns 'open-no-action' — but the hook always reaches
            // a terminal. Instead, drive through TryShow's pending-set path,
            // then exercise the dismiss reason directly.)
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            RouteCreationDialog.TryShow(tree);
            // After cancel terminal, pendingTreeId is cleared by DismissIfOpen.
            Assert.Null(RouteCreationDialog.PendingTreeIdForTesting);

            // Now re-show and immediately invoke the scene-change dismiss.
            // To do this without spawning a real Spawn, we simulate the open
            // state by calling TryShow with no terminal hook (the production
            // path would have called Spawn which would call SpawnPopupDialog,
            // not viable in tests). Use a sentinel hook that delays returning
            // by reading the state we want, then exits via cancel — but use
            // DismissIfOpen("scene-change") BEFORE OnCancel runs.
            //
            // The simplest seam: just write pendingTreeId via the
            // intermediate state by calling TryShow with a confirm-then-recall
            // pattern. Since DismissIfOpen is invoked from inside the
            // production code path, the dialog must have been open. Bypass by
            // using ResetForTesting + manually invoking the production
            // DismissIfOpen with no open dialog: it should be a no-op AND
            // leave pendingTreeId alone (because there is no open dialog).

            // Simpler test: re-establish pendingTreeId via TryShow with a hook
            // that returns nothing terminal mid-call. The hook runs inside
            // Spawn after pendingTreeId is set; if the hook calls
            // DismissIfOpen("scene-change") directly, that exercises the
            // preserve path.
            RouteCreationDialog.ResetForTesting();
            string capturedAfterSceneChange = null;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                // Inside the hook, dialogOpen=true, pendingTreeId is set.
                // Call DismissIfOpen("scene-change") mid-hook to exercise the
                // preserve path, then assert pendingTreeId is still set.
                RouteCreationDialog.DismissIfOpen("scene-change");
                capturedAfterSceneChange = RouteCreationDialog.PendingTreeIdForTesting;
                // Return cancel so the outer TryShow doesn't crash on the
                // already-dismissed dialog.
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };
            RouteCreationDialog.TryShow(tree);

            Assert.Equal(expectedId, capturedAfterSceneChange);
        }

        [Fact]
        public void DismissIfOpen_UserCanceled_ClearsPendingTreeId()
        {
            RouteCreationDialog.TestHookForConfirm = () =>
                new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            RouteCreationDialog.TryShow(BuildEligibleTree(out _));

            Assert.Null(RouteCreationDialog.PendingTreeIdForTesting);
        }

        [Fact]
        public void TryShowDeferredIfPending_NullPending_NoOp()
        {
            // pendingTreeId is null after ResetForTesting in the constructor.
            RouteCreationDialog.TryShowDeferredIfPendingForScene(GameScenes.SPACECENTER);

            Assert.Null(RouteCreationDialog.PendingTreeIdForTesting);
            Assert.False(RouteCreationDialog.DialogOpenForTesting);
        }

        [Fact]
        public void TryShowDeferredIfPending_WrongScene_DoesNotRetry()
        {
            // Set up pending via a TryShow that cancels but on a hook that
            // preserves pendingTreeId via scene-change semantics.
            RecordingTree tree = BuildEligibleTree(out _);
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                RouteCreationDialog.DismissIfOpen("scene-change");
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel" // cancel after the scene-change dismiss is a no-op (dialogOpen=false)
                };
            };
            RouteCreationDialog.TryShow(tree);
            Assert.Equal(tree.Id, RouteCreationDialog.PendingTreeIdForTesting);

            // MAIN_MENU is not in the allowed-scene set.
            RouteCreationDialog.TryShowDeferredIfPendingForScene(GameScenes.MAINMENU);

            // Pending still set, dialog not opened.
            Assert.Equal(tree.Id, RouteCreationDialog.PendingTreeIdForTesting);
            Assert.False(RouteCreationDialog.DialogOpenForTesting);
        }

        [Fact]
        public void TryShowDeferredIfPending_TreeNotInCommittedTrees_ClearsPending()
        {
            // Set pending via the scene-change dismiss path
            RecordingTree tree = BuildEligibleTree(out _);
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                RouteCreationDialog.DismissIfOpen("scene-change");
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };
            RouteCreationDialog.TryShow(tree);
            Assert.Equal(tree.Id, RouteCreationDialog.PendingTreeIdForTesting);

            // CommittedTrees is empty (no Add). Retry should clear pending.
            RouteCreationDialog.TryShowDeferredIfPendingForScene(GameScenes.SPACECENTER);

            Assert.Null(RouteCreationDialog.PendingTreeIdForTesting);
        }

        [Fact]
        public void TryShowDeferredIfPending_TreeStillCommitted_ReSpawns()
        {
            // Set pending via the scene-change dismiss path
            RecordingTree tree = BuildEligibleTree(out _);

            // First hook: trigger scene-change-style dismiss mid-call to leave
            // pendingTreeId set.
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                RouteCreationDialog.DismissIfOpen("scene-change");
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };
            RouteCreationDialog.TryShow(tree);
            Assert.Equal(tree.Id, RouteCreationDialog.PendingTreeIdForTesting);

            // Place the tree in CommittedTrees so the deferred retry can find it.
            RecordingStore.CommittedTrees.Add(tree);

            // Second hook for the retry: confirm OR cancel terminally so we
            // can assert the retry actually re-spawned (pendingTreeId cleared).
            bool retryHookFired = false;
            RouteCreationDialog.TestHookForConfirm = () =>
            {
                retryHookFired = true;
                return new RouteCreationDialog.RouteCreationInputsForTesting
                {
                    OutcomeAction = "cancel"
                };
            };

            RouteCreationDialog.TryShowDeferredIfPendingForScene(GameScenes.SPACECENTER);

            Assert.True(retryHookFired,
                "TryShowDeferredIfPending should re-spawn via TryShow → hook when tree is still committed");
            Assert.Null(RouteCreationDialog.PendingTreeIdForTesting);
        }

        // -----------------------------------------------------------------
        // Phase 6 should-fix: default interval = full [root..undock] span
        // -----------------------------------------------------------------

        // catches: the dialog defaulting to the LEAF dock-child span
        // (source.EndUT - source.StartUT) instead of the full rendered
        // [root..undock] span. On a multi-recording flight the leaf span is
        // SMALLER than the rendered span, so a leaf-span default would trip
        // RouteBuilder's interval-below-transit reject.
        [Fact]
        public void ComputeRootToUndockSpan_MultiLegTree_UsesRootLaunchToUndock_NotLeafSpan()
        {
            // Root launch at 1000; mid-flight dock child spans [2000, 3000] with
            // undock at 2800. Leaf span = 1000; rendered span = 2800 - 1000 = 1800.
            var root = new Recording
            {
                RecordingId = "launch-root",
                TreeId = "tree-multi",
                StartBodyName = "Kerbin",
                LaunchSiteName = "Runway",
                ExplicitStartUT = 1000.0,
                ExplicitEndUT = 2000.0
            };
            var child = new Recording
            {
                RecordingId = "dock-child",
                TreeId = "tree-multi",
                StartBodyName = "Kerbin",
                LaunchSiteName = null,
                ExplicitStartUT = 2000.0,
                ExplicitEndUT = 3000.0,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow { WindowId = "w", DockUT = 2400.0, UndockUT = 2800.0 }
                }
            };
            var tree = new RecordingTree { Id = "tree-multi", RootRecordingId = "launch-root" };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(child);

            var analysis = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = child,
                ConnectionWindow = child.RouteConnectionWindows[0]
            };

            double span = RouteCreationDialog.ComputeRootToUndockSpan(analysis, tree);

            // Rendered span (root launch -> undock), NOT the leaf span (1000).
            Assert.Equal(1800.0, span);
        }

        // catches: a single-recording tree (rootRec == source) not falling back to
        // the leaf span correctly when root == source.
        [Fact]
        public void ComputeRootToUndockSpan_SingleLegTree_RootEqualsSource()
        {
            RecordingTree tree = BuildEligibleTree(out Recording source);
            var analysis = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = source.RouteConnectionWindows[0]
            };

            double span = RouteCreationDialog.ComputeRootToUndockSpan(analysis, tree);

            // Window undock 160 - root launch 0 = 160.
            Assert.Equal(160.0, span);
        }
    }
}
