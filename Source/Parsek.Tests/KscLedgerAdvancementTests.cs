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

        [Fact]
        public void ShouldSeedCareerLedgerForKscUT_FirstValidFrame_ReturnsTrue()
        {
            Assert.True(ParsekKSC.ShouldSeedCareerLedgerForKscUT(
                currentUT: 19.0,
                lastAppliedUT: double.NaN));
        }

        [Fact]
        public void ShouldSeedCareerLedgerForKscUT_InvalidCurrentUt_ReturnsFalse()
        {
            Assert.False(ParsekKSC.ShouldSeedCareerLedgerForKscUT(
                currentUT: double.NaN,
                lastAppliedUT: double.NaN));
            Assert.False(ParsekKSC.ShouldSeedCareerLedgerForKscUT(
                currentUT: double.PositiveInfinity,
                lastAppliedUT: double.NaN));
        }

        [Fact]
        public void ShouldSeedCareerLedgerForKscUT_AlreadySeeded_ReturnsFalse()
        {
            Assert.False(ParsekKSC.ShouldSeedCareerLedgerForKscUT(
                currentUT: 20.0,
                lastAppliedUT: 19.0));
        }

        [Fact]
        public void GetCareerLedgerAdvanceReasonForKscUT_BackwardClockMove_ReturnsBackwardReason()
        {
            Assert.Equal(
                "ksc-clock-backward",
                ParsekKSC.GetCareerLedgerAdvanceReasonForKscUT(
                    currentUT: 19.0,
                    lastAppliedUT: 129.0,
                    epsilon: 0.05));
        }

        [Fact]
        public void GetCareerLedgerAdvanceReasonForKscUT_ForwardClockMove_ReturnsForwardReason()
        {
            Assert.Equal(
                "ksc-clock",
                ParsekKSC.GetCareerLedgerAdvanceReasonForKscUT(
                    currentUT: 129.0,
                    lastAppliedUT: 19.0,
                    epsilon: 0.05));
        }
    }
}
