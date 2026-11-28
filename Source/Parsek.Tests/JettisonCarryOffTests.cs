using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The carried-off rule of <see cref="PartStatePoller.PollJettisons"/>: stock re-parents an
    /// isFairing + decoupleEnabled shroud onto the part on its bottom node, so the shroud leaves
    /// with that part on a decouple while <c>isJettisoned</c> stays false (the 1.25 m heat
    /// shield's fairing). Only a shroud no longer drawn on the vessel counts as jettisoned.
    /// </summary>
    public class JettisonCarryOffTests
    {
        [Theory]
        // The enum is internal and an xUnit theory method is public, so the rows pass names.
        [InlineData("NoReference", false)]
        [InlineData("OwnPart", false)]
        [InlineData("OtherPartSameVessel", false)]
        [InlineData("OtherVessel", true)]
        [InlineData("NoCarrier", true)]
        [InlineData("Destroyed", true)]
        public void IsJettisonCarriedOff_OnlyWhenTheShroudLeftTheVessel(string state, bool expected)
        {
            var parsed = (PartStatePoller.JettisonCarrierState)System.Enum.Parse(
                typeof(PartStatePoller.JettisonCarrierState), state);
            Assert.Equal(expected, PartStatePoller.IsJettisonCarriedOff(parsed));
        }

        // The poll and the start-of-recording seed share this rule, so a recording that starts
        // after the shroud left seeds it at its start instead of emitting it on its first poll.
        [InlineData(true, "NoReference", true)]
        [InlineData(true, "OwnPart", true)]
        [InlineData(false, "NoReference", false)]
        [InlineData(false, "OwnPart", false)]
        [InlineData(false, "OtherPartSameVessel", false)]
        [InlineData(false, "OtherVessel", true)]
        [InlineData(false, "NoCarrier", true)]
        [InlineData(false, "Destroyed", true)]
        [Theory]
        public void IsShroudGone_StockFlagOrCarriedOff(bool isJettisoned, string state, bool expected)
        {
            var parsed = (PartStatePoller.JettisonCarrierState)System.Enum.Parse(
                typeof(PartStatePoller.JettisonCarrierState), state);
            Assert.Equal(expected, PartStatePoller.IsShroudGone(isJettisoned, parsed));
        }

        [Fact]
        public void IsJettisonCarriedOff_EveryStateHasARow()
        {
            Assert.Equal(6, System.Enum.GetValues(typeof(PartStatePoller.JettisonCarrierState)).Length);
        }

        [Fact]
        public void FormatJettisonCarriedOff_NamesPartCarrierAndState_Invariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = PartStatePoller.FormatJettisonCarriedOff(
                    "HeatShield1", 4077831288u, PartStatePoller.JettisonCarrierState.OtherVessel,
                    "Decoupler.1");
                Assert.Equal(
                    "Jettison shroud of 'HeatShield1' pid=4077831288 carried off: carrier=OtherVessel " +
                    "carrierPart='Decoupler.1' (stock re-parented it onto the bottom-node part; " +
                    "isJettisoned stays false)", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void FormatJettisonCarriedOff_NullNames_ReadAsPlaceholders()
        {
            string line = PartStatePoller.FormatJettisonCarriedOff(
                null, 1u, PartStatePoller.JettisonCarrierState.Destroyed, null);
            Assert.Contains("'unknown' pid=1", line);
            Assert.Contains("carrier=Destroyed carrierPart='(none)'", line);
        }
    }
}
