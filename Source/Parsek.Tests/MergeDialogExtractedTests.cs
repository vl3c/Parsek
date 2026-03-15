using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for methods extracted from MergeDialog.ShowTreeDialog:
    /// ComputeTreeDurationRange, CountDestroyedLeaves, BuildTreeDialogMessage.
    /// </summary>
    [Collection("Sequential")]
    public class MergeDialogExtractedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MergeDialogExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        #region ComputeTreeDurationRange

        [Fact]
        public void ComputeTreeDurationRange_NullTree_ReturnsZero()
        {
            double result = MergeDialog.ComputeTreeDurationRange(null);
            Assert.Equal(0, result);
        }

        [Fact]
        public void ComputeTreeDurationRange_EmptyRecordings_ReturnsZero()
        {
            var tree = new RecordingTree();
            double result = MergeDialog.ComputeTreeDurationRange(tree);
            Assert.Equal(0, result);
        }

        [Fact]
        public void ComputeTreeDurationRange_SingleRecording_ReturnsDuration()
        {
            var tree = new RecordingTree();
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                ExplicitStartUT =100,
                ExplicitEndUT =250
            };
            double result = MergeDialog.ComputeTreeDurationRange(tree);
            Assert.Equal(150, result);
        }

        [Fact]
        public void ComputeTreeDurationRange_MultipleRecordings_ReturnsFullSpan()
        {
            var tree = new RecordingTree();
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                ExplicitStartUT =100,
                ExplicitEndUT =200
            };
            tree.Recordings["r2"] = new RecordingStore.Recording
            {
                ExplicitStartUT =150,
                ExplicitEndUT =400
            };
            // min start = 100, max end = 400, span = 300
            double result = MergeDialog.ComputeTreeDurationRange(tree);
            Assert.Equal(300, result);
        }

        [Fact]
        public void ComputeTreeDurationRange_OverlappingRecordings_CorrectSpan()
        {
            var tree = new RecordingTree();
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                ExplicitStartUT =50,
                ExplicitEndUT =200
            };
            tree.Recordings["r2"] = new RecordingStore.Recording
            {
                ExplicitStartUT =100,
                ExplicitEndUT =150
            };
            // min start = 50, max end = 200, span = 150
            double result = MergeDialog.ComputeTreeDurationRange(tree);
            Assert.Equal(150, result);
        }

        #endregion

        #region CountDestroyedLeaves

        [Fact]
        public void CountDestroyedLeaves_NullList_ReturnsZero()
        {
            int result = MergeDialog.CountDestroyedLeaves(null);
            Assert.Equal(0, result);
        }

        [Fact]
        public void CountDestroyedLeaves_EmptyList_ReturnsZero()
        {
            int result = MergeDialog.CountDestroyedLeaves(new List<RecordingStore.Recording>());
            Assert.Equal(0, result);
        }

        [Fact]
        public void CountDestroyedLeaves_NoDestroyed_ReturnsZero()
        {
            var leaves = new List<RecordingStore.Recording>
            {
                new RecordingStore.Recording { TerminalStateValue = TerminalState.Landed },
                new RecordingStore.Recording { TerminalStateValue = TerminalState.Orbiting },
                new RecordingStore.Recording { TerminalStateValue = null }
            };
            int result = MergeDialog.CountDestroyedLeaves(leaves);
            Assert.Equal(0, result);
        }

        [Fact]
        public void CountDestroyedLeaves_SomeDestroyed_ReturnsCount()
        {
            var leaves = new List<RecordingStore.Recording>
            {
                new RecordingStore.Recording { TerminalStateValue = TerminalState.Destroyed },
                new RecordingStore.Recording { TerminalStateValue = TerminalState.Landed },
                new RecordingStore.Recording { TerminalStateValue = TerminalState.Destroyed },
                new RecordingStore.Recording { TerminalStateValue = TerminalState.Orbiting }
            };
            int result = MergeDialog.CountDestroyedLeaves(leaves);
            Assert.Equal(2, result);
        }

        [Fact]
        public void CountDestroyedLeaves_AllDestroyed_ReturnsTotal()
        {
            var leaves = new List<RecordingStore.Recording>
            {
                new RecordingStore.Recording { TerminalStateValue = TerminalState.Destroyed },
                new RecordingStore.Recording { TerminalStateValue = TerminalState.Destroyed }
            };
            int result = MergeDialog.CountDestroyedLeaves(leaves);
            Assert.Equal(2, result);
        }

        [Fact]
        public void CountDestroyedLeaves_NullTerminalState_NotCounted()
        {
            var leaves = new List<RecordingStore.Recording>
            {
                new RecordingStore.Recording { TerminalStateValue = null },
                new RecordingStore.Recording { TerminalStateValue = null }
            };
            int result = MergeDialog.CountDestroyedLeaves(leaves);
            Assert.Equal(0, result);
        }

        #endregion

        #region BuildTreeDialogMessage

        [Fact]
        public void BuildTreeDialogMessage_BasicTree_ContainsTreeName()
        {
            var tree = new RecordingTree { TreeName = "MyRocket" };
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                RecordingId = "r1",
                VesselName = "MyRocket",
                ExplicitStartUT =100,
                ExplicitEndUT =200,
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition { body = "Kerbin" }
            };

            var allLeaves = new List<RecordingStore.Recording> { tree.Recordings["r1"] };
            var spawnableLeaves = new List<RecordingStore.Recording> { tree.Recordings["r1"] };

            int surviving, destroyed;
            string message = MergeDialog.BuildTreeDialogMessage(
                tree, allLeaves, spawnableLeaves,
                out surviving, out destroyed);

            Assert.Contains("MyRocket", message);
            Assert.Equal(1, surviving);
            Assert.Equal(0, destroyed);
        }

        [Fact]
        public void BuildTreeDialogMessage_AllDestroyed_ShowsLostFooter()
        {
            var tree = new RecordingTree { TreeName = "Doomed" };
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                RecordingId = "r1",
                VesselName = "Doomed",
                ExplicitStartUT =100,
                ExplicitEndUT =200,
                TerminalStateValue = TerminalState.Destroyed
            };

            var allLeaves = new List<RecordingStore.Recording> { tree.Recordings["r1"] };
            var spawnableLeaves = new List<RecordingStore.Recording>();

            int surviving, destroyed;
            string message = MergeDialog.BuildTreeDialogMessage(
                tree, allLeaves, spawnableLeaves,
                out surviving, out destroyed);

            Assert.Equal(0, surviving);
            Assert.Equal(1, destroyed);
            Assert.Contains("All vessels were lost", message);
        }

        [Fact]
        public void BuildTreeDialogMessage_SurvivingVessels_ShowsAppearFooter()
        {
            var tree = new RecordingTree { TreeName = "Explorer" };
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                RecordingId = "r1",
                VesselName = "Explorer",
                ExplicitStartUT =100,
                ExplicitEndUT =300,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin"
            };

            var allLeaves = new List<RecordingStore.Recording> { tree.Recordings["r1"] };
            var spawnableLeaves = new List<RecordingStore.Recording> { tree.Recordings["r1"] };

            int surviving, destroyed;
            string message = MergeDialog.BuildTreeDialogMessage(
                tree, allLeaves, spawnableLeaves,
                out surviving, out destroyed);

            Assert.Equal(1, surviving);
            Assert.Contains("will appear after ghost playback", message);
        }

        [Fact]
        public void BuildTreeDialogMessage_ActiveRecording_MarkedInOutput()
        {
            var tree = new RecordingTree
            {
                TreeName = "MultiVessel",
                ActiveRecordingId = "r2"
            };
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                RecordingId = "r1",
                VesselName = "Booster",
                ExplicitStartUT =100,
                ExplicitEndUT =150,
                TerminalStateValue = TerminalState.Destroyed
            };
            tree.Recordings["r2"] = new RecordingStore.Recording
            {
                RecordingId = "r2",
                VesselName = "Capsule",
                ExplicitStartUT =100,
                ExplicitEndUT =300,
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition { body = "Kerbin" }
            };

            var allLeaves = new List<RecordingStore.Recording>
            {
                tree.Recordings["r1"],
                tree.Recordings["r2"]
            };
            var spawnableLeaves = new List<RecordingStore.Recording>
            {
                tree.Recordings["r2"]
            };

            int surviving, destroyed;
            string message = MergeDialog.BuildTreeDialogMessage(
                tree, allLeaves, spawnableLeaves,
                out surviving, out destroyed);

            Assert.Equal(1, surviving);
            Assert.Equal(1, destroyed);
            Assert.Contains("<-- you are here", message);
            Assert.Contains("Capsule", message);
            Assert.Contains("Booster", message);
            Assert.Contains("1 vessel (1 destroyed)", message);
        }

        [Fact]
        public void BuildTreeDialogMessage_MultipleVessels_PluralText()
        {
            var tree = new RecordingTree { TreeName = "Fleet" };
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                RecordingId = "r1",
                VesselName = "Ship1",
                ExplicitStartUT =100,
                ExplicitEndUT =200,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin"
            };
            tree.Recordings["r2"] = new RecordingStore.Recording
            {
                RecordingId = "r2",
                VesselName = "Ship2",
                ExplicitStartUT =100,
                ExplicitEndUT =200,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin"
            };

            var allLeaves = new List<RecordingStore.Recording>
            {
                tree.Recordings["r1"],
                tree.Recordings["r2"]
            };
            var spawnableLeaves = new List<RecordingStore.Recording>
            {
                tree.Recordings["r1"],
                tree.Recordings["r2"]
            };

            int surviving, destroyed;
            string message = MergeDialog.BuildTreeDialogMessage(
                tree, allLeaves, spawnableLeaves,
                out surviving, out destroyed);

            Assert.Equal(2, surviving);
            Assert.Contains("2 vessels", message);
        }

        [Fact]
        public void BuildTreeDialogMessage_NullSpawnable_ZeroSurviving()
        {
            var tree = new RecordingTree { TreeName = "Test" };
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                RecordingId = "r1",
                VesselName = "Test",
                ExplicitStartUT =100,
                ExplicitEndUT =200,
                TerminalStateValue = TerminalState.Destroyed
            };

            var allLeaves = new List<RecordingStore.Recording> { tree.Recordings["r1"] };

            int surviving, destroyed;
            string message = MergeDialog.BuildTreeDialogMessage(
                tree, allLeaves, null,
                out surviving, out destroyed);

            Assert.Equal(0, surviving);
        }

        [Fact]
        public void BuildTreeDialogMessage_DurationFormatted()
        {
            var tree = new RecordingTree { TreeName = "LongFlight" };
            tree.Recordings["r1"] = new RecordingStore.Recording
            {
                RecordingId = "r1",
                VesselName = "LongFlight",
                ExplicitStartUT =0,
                ExplicitEndUT =3661,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin"
            };

            var allLeaves = new List<RecordingStore.Recording> { tree.Recordings["r1"] };
            var spawnableLeaves = new List<RecordingStore.Recording> { tree.Recordings["r1"] };

            int surviving, destroyed;
            string message = MergeDialog.BuildTreeDialogMessage(
                tree, allLeaves, spawnableLeaves,
                out surviving, out destroyed);

            // 3661s = 1h 1m
            Assert.Contains("1h 1m", message);
        }

        #endregion
    }
}
