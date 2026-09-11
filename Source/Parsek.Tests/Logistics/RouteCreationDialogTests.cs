using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Tests for <see cref="RouteCreationDialog.ComputeRootToUndockSpan"/>, the shared
    /// default-interval geometry.
    /// <para>The post-commit modal these tests used to drive was deleted 2026-09-11 (GUI
    /// census D4): `TryShow` / `TryShowDeferredIfPending` / `Spawn` / `OnConfirm` /
    /// `OnCancel` had no production caller, so no player could reach the dialog, and the
    /// ~30 cells that exercised it through the `TestHookForConfirm` seam proved behaviour
    /// the product did not run. They went with it. What remains is the one piece of that
    /// file with live callers.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteCreationDialogTests : IDisposable
    {
        public RouteCreationDialogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

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
                TransferEndpointSituation = 4
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

        // -----------------------------------------------------------------
        // Default interval = full [root..dock] span
        // -----------------------------------------------------------------

        // catches: the default falling back to the LEAF dock-child span
        // (source.EndUT - source.StartUT) instead of the full rendered [root..dock] span.
        // On a multi-recording flight the leaf span is SMALLER than the rendered span, so a
        // leaf-span default would trip RouteBuilder's interval-below-transit reject.
        [Fact]
        public void ComputeRootToUndockSpan_MultiLegTree_UsesRootLaunchToUndock_NotLeafSpan()
        {
            // Root launch at 1000; mid-flight dock child spans [2000, 3000] with
            // dock at 2400, undock at 2800. Leaf span = 1000; rendered [root..DOCK]
            // span = 2400 - 1000 = 1400 (rendering stops at the dock, not the undock).
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

            // Rendered span (root launch -> DOCK), NOT the leaf span (1000).
            Assert.Equal(1400.0, span);
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

            // Window dock 100 - root launch 0 = 100 (segment ends at the dock).
            Assert.Equal(100.0, span);
        }

        // catches: the fallbacks going missing. A null analysis, a window with no usable
        // dock UT, and a tree whose root cannot be resolved must each yield the leaf span
        // (floored at 1.0) rather than a negative or NaN interval reaching RouteBuilder.
        [Fact]
        public void ComputeRootToUndockSpan_UnresolvableInputs_FallBackToTheLeafSpan()
        {
            Assert.Equal(1.0, RouteCreationDialog.ComputeRootToUndockSpan(null, null));

            var lone = new Recording
            {
                RecordingId = "lone",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 700.0
            };
            var noWindow = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = lone,
                ConnectionWindow = null
            };

            // No connection window -> leaf span (700 - 100).
            Assert.Equal(600.0, RouteCreationDialog.ComputeRootToUndockSpan(noWindow, null));

            // Dock BEFORE the root launch would give a non-positive span; the leaf span
            // stands in instead.
            var backwards = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = lone,
                ConnectionWindow = new RouteConnectionWindow { WindowId = "w", DockUT = 50.0 }
            };
            Assert.Equal(600.0, RouteCreationDialog.ComputeRootToUndockSpan(backwards, null));
        }
    }
}
