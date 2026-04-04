using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for methods extracted from MergeDialog.ShowTreeDialog:
    /// ComputeTreeDurationRange, FormatDuration.
    /// </summary>
    [Collection("Sequential")]
    public class MergeDialogExtractedTests : IDisposable
    {
        public MergeDialogExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
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
            tree.Recordings["r1"] = new Recording
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
            tree.Recordings["r1"] = new Recording
            {
                ExplicitStartUT =100,
                ExplicitEndUT =200
            };
            tree.Recordings["r2"] = new Recording
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
            tree.Recordings["r1"] = new Recording
            {
                ExplicitStartUT =50,
                ExplicitEndUT =200
            };
            tree.Recordings["r2"] = new Recording
            {
                ExplicitStartUT =100,
                ExplicitEndUT =150
            };
            // min start = 50, max end = 200, span = 150
            double result = MergeDialog.ComputeTreeDurationRange(tree);
            Assert.Equal(150, result);
        }

        #endregion
    }
}
