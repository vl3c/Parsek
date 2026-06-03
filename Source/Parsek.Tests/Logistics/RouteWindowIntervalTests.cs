using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins QW1: a route created from the Logistics WINDOW (the Candidates
    /// "Create Route" button) gets the SAME default dispatch interval the Create
    /// Route DIALOG computes. Both paths funnel through
    /// <see cref="RouteCreationDialog.ComputeRootToUndockSpan"/>, so the
    /// directly-testable contract is that
    /// <see cref="LogisticsWindowUI.ResolveWindowCreateInterval"/> returns exactly
    /// that span (the rendered [root..dock] span, == the route's TransitDuration)
    /// and never the old hardcoded 30s placeholder. Pure / Unity-free.
    /// </summary>
    public class RouteWindowIntervalTests
    {
        private const string TreeId = "win-interval-tree";
        private const string RootId = "root-rec";

        // Build a minimal eligible analysis + tree whose [root..dock] span is a
        // real multi-second value (dockUT - rootStartUT) that is NOT 30s, so a
        // lingering hardcoded-30 path would fail the assertion.
        private static RouteCandidate BuildCandidate(double rootStartUT, double dockUT, out double expectedSpan)
        {
            expectedSpan = dockUT - rootStartUT;

            Recording source = new Recording
            {
                RecordingId = RootId,
                TreeId = TreeId,
                ExplicitStartUT = rootStartUT,
                ExplicitEndUT = dockUT
            };

            RouteConnectionWindow window = new RouteConnectionWindow
            {
                WindowId = "w",
                DockUT = dockUT,
                UndockUT = dockUT + 50.0
            };

            RouteAnalysisResult analysis = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window
            };

            RecordingTree tree = new RecordingTree
            {
                Id = TreeId,
                RootRecordingId = RootId
            };
            tree.Recordings[RootId] = source;

            return new RouteCandidate
            {
                Tree = tree,
                Analysis = analysis
            };
        }

        [Fact]
        public void WindowInterval_EqualsDialogSpan_ForSameCandidate()
        {
            // The dialog computes its default interval as ComputeRootToUndockSpan
            // over the same analysis + tree (RouteCreationDialog.cs OnConfirm path);
            // the window must resolve to that identical value.
            RouteCandidate candidate = BuildCandidate(rootStartUT: 1000.0, dockUT: 1600.0, out double expectedSpan);

            double dialogInterval = RouteCreationDialog.ComputeRootToUndockSpan(
                candidate.Analysis, candidate.Tree);
            double windowInterval = LogisticsWindowUI.ResolveWindowCreateInterval(candidate);

            Assert.Equal(expectedSpan, dialogInterval);
            Assert.Equal(dialogInterval, windowInterval);
        }

        [Fact]
        public void WindowInterval_IsNotThirtySecondPlaceholder()
        {
            // Guards the QW1 regression: the window path used to hardcode 30s. With a
            // 600s span the resolved interval must be the real span, not 30.
            RouteCandidate candidate = BuildCandidate(rootStartUT: 0.0, dockUT: 600.0, out double expectedSpan);

            double windowInterval = LogisticsWindowUI.ResolveWindowCreateInterval(candidate);

            Assert.Equal(expectedSpan, windowInterval);
            Assert.NotEqual(30.0, windowInterval);
            Assert.True(windowInterval > 0.0);
        }

        [Fact]
        public void WindowInterval_NullCandidate_ReturnsHelperFloor()
        {
            // Null guard: ComputeRootToUndockSpan floors at 1.0 for null inputs, so
            // the window helper must never throw and never return 30.
            double interval = LogisticsWindowUI.ResolveWindowCreateInterval(null);

            Assert.Equal(1.0, interval);
            Assert.NotEqual(30.0, interval);
        }
    }
}
