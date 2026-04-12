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
    }
}
