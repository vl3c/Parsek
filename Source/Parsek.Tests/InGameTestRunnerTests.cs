using System.Linq;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    public class InGameTestRunnerTests
    {
        [Fact]
        public void OrderForBatchExecution_PlacesRunLastTestsAfterNormalTests()
        {
            var ordered = InGameTestRunner.OrderForBatchExecution(new[]
            {
                new InGameTestInfo { Category = "B", Name = "NormalB" },
                new InGameTestInfo { Category = "A", Name = "RunLastA", RunLast = true },
                new InGameTestInfo { Category = "A", Name = "NormalA" },
                new InGameTestInfo { Category = "A", Name = "RunLastB", RunLast = true },
            });

            Assert.Equal(
                new[] { "NormalA", "NormalB", "RunLastA", "RunLastB" },
                ordered.Select(t => t.Name).ToArray());
        }

        [Fact]
        public void OrderForBatchExecution_KeepsAlphabeticalOrderWithinEachPriorityBucket()
        {
            var ordered = InGameTestRunner.OrderForBatchExecution(new[]
            {
                new InGameTestInfo { Category = "B", Name = "Beta" },
                new InGameTestInfo { Category = "A", Name = "Alpha" },
                new InGameTestInfo { Category = "B", Name = "Gamma", RunLast = true },
                new InGameTestInfo { Category = "A", Name = "Delta", RunLast = true },
            });

            Assert.Equal(
                new[] { "Alpha", "Beta", "Delta", "Gamma" },
                ordered.Select(t => t.Name).ToArray());
        }

        [Fact]
        public void PrepareBatchExecution_SkipsSingleRunOnlyTestsWithExplicitReason()
        {
            var batchSafe = new InGameTestInfo { Category = "A", Name = "BatchSafe" };
            var singleOnly = new InGameTestInfo
            {
                Category = "A",
                Name = "SceneTransition",
                AllowBatchExecution = false,
                BatchSkipReason = "single-run only"
            };

            var batch = InGameTestRunner.PrepareBatchExecution(new[] { singleOnly, batchSafe });

            Assert.Equal(new[] { "BatchSafe" }, batch.Select(t => t.Name).ToArray());
            Assert.Equal(TestStatus.Skipped, singleOnly.Status);
            Assert.Equal("single-run only", singleOnly.ErrorMessage);
            Assert.Equal(0f, singleOnly.DurationMs);
        }

        [Fact]
        public void PrepareBatchExecution_UsesDefaultReasonWhenSingleRunOnlyTestHasNoCustomReason()
        {
            var singleOnly = new InGameTestInfo
            {
                Category = "A",
                Name = "SceneTransition",
                AllowBatchExecution = false
            };

            var batch = InGameTestRunner.PrepareBatchExecution(new[] { singleOnly });

            Assert.Empty(batch);
            Assert.Equal(TestStatus.Skipped, singleOnly.Status);
            Assert.Equal(InGameTestRunner.DefaultBatchSkipReason, singleOnly.ErrorMessage);
        }
    }
}
