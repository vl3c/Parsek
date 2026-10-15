using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The opening line a loop unit logs on its first evaluation. Since a looped mission's first
    /// run plays as an ordinary recording until the unit's anchor, the first evaluation can land
    /// on a member other than #0 (W1's jump lands on #1), and no hand-off line covers it.
    /// </summary>
    public class LoopUnitFirstEvaluationLogTests
    {
        [Fact]
        public void FirstEvaluation_NamesTheOpeningCameraLiveMember()
        {
            string line = GhostPlaybackEngine.FormatUnitFirstEvaluation(
                "Chain-loop", 0, 1, 434.5899, 0);

            Assert.Equal(
                "Chain-loop unit owner=0 first evaluation: camera-live member #1 at loopUT=434.59 cycle=0",
                line);
        }

        [Fact]
        public void FirstEvaluation_NoMemberInWindow_PrintsMinusOne()
        {
            string line = GhostPlaybackEngine.FormatUnitFirstEvaluation(
                "Mission-loop", 3, -1, 12.0, 2);

            Assert.Equal(
                "Mission-loop unit owner=3 first evaluation: camera-live member #-1 at loopUT=12.00 cycle=2",
                line);
        }

        [Fact]
        public void FirstEvaluation_IsCultureInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                string line = GhostPlaybackEngine.FormatUnitFirstEvaluation(
                    "Chain-loop", 0, 1, 434.5899, 0);
                Assert.Contains("at loopUT=434.59 ", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
