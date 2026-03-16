using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class Tier3CExtractedTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Tier3CExtractedTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        #region AccumulateReplayResult

        [Fact]
        public void AccumulateReplayResult_Success_IncrementsSuccessCount()
        {
            int success = 0, skip = 0, fail = 0;
            ActionReplay.AccumulateReplayResult(true, false, ref success, ref skip, ref fail);
            Assert.Equal(1, success);
            Assert.Equal(0, skip);
            Assert.Equal(0, fail);
        }

        [Fact]
        public void AccumulateReplayResult_Skipped_IncrementsSkipCount()
        {
            int success = 0, skip = 0, fail = 0;
            ActionReplay.AccumulateReplayResult(true, true, ref success, ref skip, ref fail);
            Assert.Equal(0, success);
            Assert.Equal(1, skip);
            Assert.Equal(0, fail);
        }

        [Fact]
        public void AccumulateReplayResult_Failed_IncrementsFailCount()
        {
            int success = 0, skip = 0, fail = 0;
            ActionReplay.AccumulateReplayResult(false, false, ref success, ref skip, ref fail);
            Assert.Equal(0, success);
            Assert.Equal(0, skip);
            Assert.Equal(1, fail);
        }

        [Fact]
        public void AccumulateReplayResult_FailedButSkipped_IncrementsSkipNotFail()
        {
            // When wasSkipped=true, the skip path takes priority regardless of success
            int success = 0, skip = 0, fail = 0;
            ActionReplay.AccumulateReplayResult(false, true, ref success, ref skip, ref fail);
            Assert.Equal(0, success);
            Assert.Equal(1, skip);
            Assert.Equal(0, fail);
        }

        [Fact]
        public void AccumulateReplayResult_MultipleCallsAccumulate()
        {
            int success = 0, skip = 0, fail = 0;
            ActionReplay.AccumulateReplayResult(true, false, ref success, ref skip, ref fail);
            ActionReplay.AccumulateReplayResult(true, false, ref success, ref skip, ref fail);
            ActionReplay.AccumulateReplayResult(false, false, ref success, ref skip, ref fail);
            ActionReplay.AccumulateReplayResult(true, true, ref success, ref skip, ref fail);
            Assert.Equal(2, success);
            Assert.Equal(1, skip);
            Assert.Equal(1, fail);
        }

        #endregion

        #region BuildStateMap

        [Fact]
        public void BuildStateMap_NullNodes_ReturnsEmptyMap()
        {
            try
            {
                var result = MilestoneStore.BuildStateMap(null);
                Assert.NotNull(result);
                Assert.Empty(result);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_EmptyArray_ReturnsEmptyMap()
        {
            try
            {
                var result = MilestoneStore.BuildStateMap(new ConfigNode[0]);
                Assert.NotNull(result);
                Assert.Empty(result);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_SingleValidNode_ParsesCorrectly()
        {
            try
            {
                var node = new ConfigNode("MILESTONE_STATE");
                node.AddValue("id", "abc123");
                node.AddValue("lastReplayedIdx", "5");

                var result = MilestoneStore.BuildStateMap(new[] { node });
                Assert.Single(result);
                Assert.Equal(5, result["abc123"]);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_MultipleNodes_ParsesAll()
        {
            try
            {
                var node1 = new ConfigNode("MILESTONE_STATE");
                node1.AddValue("id", "ms1");
                node1.AddValue("lastReplayedIdx", "3");

                var node2 = new ConfigNode("MILESTONE_STATE");
                node2.AddValue("id", "ms2");
                node2.AddValue("lastReplayedIdx", "7");

                var result = MilestoneStore.BuildStateMap(new[] { node1, node2 });
                Assert.Equal(2, result.Count);
                Assert.Equal(3, result["ms1"]);
                Assert.Equal(7, result["ms2"]);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_MissingId_SkipsEntry()
        {
            try
            {
                var node = new ConfigNode("MILESTONE_STATE");
                // No "id" value
                node.AddValue("lastReplayedIdx", "5");

                var result = MilestoneStore.BuildStateMap(new[] { node });
                Assert.Empty(result);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_MissingIdx_SkipsEntry()
        {
            try
            {
                var node = new ConfigNode("MILESTONE_STATE");
                node.AddValue("id", "abc123");
                // No "lastReplayedIdx" value

                var result = MilestoneStore.BuildStateMap(new[] { node });
                Assert.Empty(result);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_InvalidIdxFormat_SkipsEntry()
        {
            try
            {
                var node = new ConfigNode("MILESTONE_STATE");
                node.AddValue("id", "abc123");
                node.AddValue("lastReplayedIdx", "not_a_number");

                var result = MilestoneStore.BuildStateMap(new[] { node });
                Assert.Empty(result);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_NegativeIdx_ParsesCorrectly()
        {
            try
            {
                var node = new ConfigNode("MILESTONE_STATE");
                node.AddValue("id", "abc123");
                node.AddValue("lastReplayedIdx", "-1");

                var result = MilestoneStore.BuildStateMap(new[] { node });
                Assert.Single(result);
                Assert.Equal(-1, result["abc123"]);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_LogsEntryCount()
        {
            try
            {
                var node1 = new ConfigNode("MILESTONE_STATE");
                node1.AddValue("id", "ms1");
                node1.AddValue("lastReplayedIdx", "2");

                var node2 = new ConfigNode("MILESTONE_STATE");
                node2.AddValue("id", "ms2");
                node2.AddValue("lastReplayedIdx", "4");

                MilestoneStore.BuildStateMap(new[] { node1, node2 });

                Assert.Contains(logLines,
                    l => l.Contains("[MilestoneStore]") && l.Contains("parsed 2 entries") && l.Contains("2 MILESTONE_STATE"));
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        [Fact]
        public void BuildStateMap_MixedValidAndInvalid_ParsesOnlyValid()
        {
            try
            {
                var valid = new ConfigNode("MILESTONE_STATE");
                valid.AddValue("id", "good");
                valid.AddValue("lastReplayedIdx", "10");

                var badIdx = new ConfigNode("MILESTONE_STATE");
                badIdx.AddValue("id", "bad1");
                badIdx.AddValue("lastReplayedIdx", "xyz");

                var missingId = new ConfigNode("MILESTONE_STATE");
                missingId.AddValue("lastReplayedIdx", "5");

                var result = MilestoneStore.BuildStateMap(new[] { valid, badIdx, missingId });
                Assert.Single(result);
                Assert.Equal(10, result["good"]);
            }
            finally
            {
                // cleanup handled by IDisposable.Dispose
            }
        }

        #endregion
    }
}
