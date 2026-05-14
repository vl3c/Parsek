using Xunit;

namespace Parsek.Tests
{
    public class KscLedgerAdvancementTests
    {
        [Fact]
        public void ShouldAdvanceCareerLedgerForKscUT_BeforeNextAction_ReturnsFalse()
        {
            Assert.False(ParsekKSC.ShouldAdvanceCareerLedgerForKscUT(
                currentUT: 20.0,
                lastAppliedUT: 19.0,
                nextActionUT: 21.2,
                epsilon: 0.05));
        }

        [Fact]
        public void ShouldAdvanceCareerLedgerForKscUT_WithinEpsilonBeforeNextAction_ReturnsFalse()
        {
            Assert.False(ParsekKSC.ShouldAdvanceCareerLedgerForKscUT(
                currentUT: 21.16,
                lastAppliedUT: 19.0,
                nextActionUT: 21.2,
                epsilon: 0.05));
        }

        [Fact]
        public void ShouldAdvanceCareerLedgerForKscUT_CrossingNextAction_ReturnsTrue()
        {
            Assert.True(ParsekKSC.ShouldAdvanceCareerLedgerForKscUT(
                currentUT: 21.2,
                lastAppliedUT: 19.0,
                nextActionUT: 21.2,
                epsilon: 0.05));
        }

        [Fact]
        public void ShouldAdvanceCareerLedgerForKscUT_WarpSkipPastNextAction_ReturnsTrue()
        {
            Assert.True(ParsekKSC.ShouldAdvanceCareerLedgerForKscUT(
                currentUT: 129.0,
                lastAppliedUT: 19.0,
                nextActionUT: 21.2,
                epsilon: 0.05));
        }

        [Fact]
        public void ShouldAdvanceCareerLedgerForKscUT_BackwardClockMove_ReturnsTrue()
        {
            Assert.True(ParsekKSC.ShouldAdvanceCareerLedgerForKscUT(
                currentUT: 19.0,
                lastAppliedUT: 129.0,
                nextActionUT: 153.0,
                epsilon: 0.05));
        }

        [Fact]
        public void ShouldAdvanceCareerLedgerForKscUT_NoNextAction_ReturnsFalse()
        {
            Assert.False(ParsekKSC.ShouldAdvanceCareerLedgerForKscUT(
                currentUT: 200.0,
                lastAppliedUT: 184.0,
                nextActionUT: double.PositiveInfinity,
                epsilon: 0.05));
        }
    }
}
