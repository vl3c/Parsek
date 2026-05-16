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
            RouteCreationDialog.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteCreationDialog.ResetForTesting();
            RouteStore.ResetForTesting();
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
                && l.Contains("[RouteUI]")
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
                l.Contains("[RouteUI]")
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
                l.Contains("[RouteUI]")
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
                l.Contains("[RouteUI]")
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
                l.Contains("[RouteUI]")
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
                l.Contains("[RouteUI]")
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
                l.Contains("[RouteUI]")
                && l.Contains("dialog dismissed"));
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
    }
}
