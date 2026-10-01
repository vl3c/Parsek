using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Log window's title. The Missions "Log" button and the Logistics "Log (Route)" /
    /// "Log (Mission)" buttons all open <see cref="StructureListWindowUI"/>, so its title
    /// says "Log" and names what it is the log of; the seam token stays <c>structure</c>
    /// (<c>GuiCensusSeamVerbTests</c> pins it).
    /// </summary>
    public class StructureListWindowTitleTests
    {
        [Fact]
        public void TheTitleNamesTheLogAndItsTarget()
        {
            Assert.Equal("Parsek - Log: Kerbal X", StructureListWindowUI.BuildWindowTitle("Kerbal X"));
            Assert.Equal("Parsek - Log: Mun Supply", StructureListWindowUI.BuildWindowTitle("Mun Supply"));
        }

        // catches: an untargeted window (the census opens it bare) reading "Parsek - Log: "
        // with a dangling colon, or falling back to the old "Structure" wording.
        [Fact]
        public void AnUntargetedWindowIsTheBareLog()
        {
            Assert.Equal("Parsek - Log", StructureListWindowUI.BuildWindowTitle(null));
            Assert.Equal("Parsek - Log", StructureListWindowUI.BuildWindowTitle(""));
            Assert.DoesNotContain("Structure", StructureListWindowUI.BuildWindowTitle(null));
        }

        [Fact]
        public void TheSeamTokenIsStillStructure()
        {
            Assert.Equal("structure", TestCommandUiAction.StructureWindow);
        }
    }
}
