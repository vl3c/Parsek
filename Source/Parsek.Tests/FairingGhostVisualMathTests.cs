using System.Collections.Generic;
using Parsek.InGameTests;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless guards for the pure helpers behind the FGV-1 fairing snapshot lane
    /// (<see cref="FairingGhostVisualMath"/>). Each cell names the image property it protects,
    /// so a drift reds here instead of costing a KSP boot and a set of useless pictures.
    /// </summary>
    public class FairingGhostVisualMathTests
    {
        [Fact]
        public void ColourVariant_PrefersTheListedOrder_AndNeverTheBase()
        {
            var offered = new List<string> { "White", "Orange", "BlackAndWhite" };
            Assert.Equal("BlackAndWhite", FairingGhostVisualMath.ChooseColourVariant(offered, "White"));
        }

        [Fact]
        public void ColourVariant_FallsBackToAnyNonBaseName()
        {
            var offered = new List<string> { "White", "Teal" };
            Assert.Equal("Teal", FairingGhostVisualMath.ChooseColourVariant(offered, "white"));
        }

        [Fact]
        public void ColourVariant_NoneWhenOnlyTheBaseIsOffered()
        {
            Assert.Null(FairingGhostVisualMath.ChooseColourVariant(new List<string> { "White" }, "White"));
            Assert.Null(FairingGhostVisualMath.ChooseColourVariant(null, "White"));
        }

        [Fact]
        public void FramingDistance_FitsTheSphereInTheNarrowerHalfAngle()
        {
            // 60 deg vertical at 16:9: the vertical half-angle (30 deg) is the narrower one,
            // so a 3 m sphere needs 3 / sin(30 deg) = 6 m before the margin.
            double d = FairingGhostVisualMath.ComputeFramingDistance(3.0, 60.0, 16.0 / 9.0, 1.0, 1.0);
            Assert.Equal(6.0, d, 6);
            // A portrait aspect makes the horizontal half-angle the binding one: farther.
            double portrait = FairingGhostVisualMath.ComputeFramingDistance(3.0, 60.0, 0.5, 1.0, 1.0);
            Assert.True(portrait > d);
        }

        [Fact]
        public void FramingDistance_NeverInsideTheMinimum()
        {
            Assert.Equal(4.0, FairingGhostVisualMath.ComputeFramingDistance(0.1, 60.0, 1.7, 1.0, 4.0), 6);
            Assert.Equal(4.0, FairingGhostVisualMath.ComputeFramingDistance(double.NaN, 60.0, 1.7, 1.0, 4.0), 6);
        }

        [Fact]
        public void Shadow_OnlyBehindTheBodyAndInsideItsRadius()
        {
            // Point 700 km from a 600 km body; the sun is far along +X.
            // Sun side of the body (body behind the point, away from the sun): lit.
            Assert.False(FairingGhostVisualMath.IsInBodyShadow(-700e3, 0, 0, 1e10, 0, 0, 600e3));
            // Night side, straight behind: shadow.
            Assert.True(FairingGhostVisualMath.IsInBodyShadow(700e3, 0, 0, 1e10, 0, 0, 600e3));
            // Night side but off the shadow cylinder (perpendicular distance 650 km): lit.
            Assert.False(FairingGhostVisualMath.IsInBodyShadow(250e3, 650e3, 0, 1e10, 0, 0, 600e3));
            // Over the terminator (body square to the sun line): lit.
            Assert.False(FairingGhostVisualMath.IsInBodyShadow(0, 700e3, 0, 1e10, 0, 0, 600e3));
        }

        [Fact]
        public void UpwardNode_FirstUpPointingCandidate_ElseFallback()
        {
            var nodes = new List<KeyValuePair<string, double>>
            {
                new KeyValuePair<string, double>("interstage02a", -1.0),
                new KeyValuePair<string, double>("interstage02b", 1.0),
            };
            Assert.Equal("interstage02b", FairingGhostVisualMath.PickUpwardNode(nodes, "x"));
            var down = new List<KeyValuePair<string, double>>
            {
                new KeyValuePair<string, double>("interstage02a", -1.0),
            };
            Assert.Equal("interstage02b", FairingGhostVisualMath.PickUpwardNode(down, "interstage02b"));
            Assert.Equal("fb", FairingGhostVisualMath.PickUpwardNode(null, "fb"));
        }

        [Fact]
        public void Labels_AreTheSpecPinnedNames_AndValidScreenshotLabels()
        {
            // harness/scenarios/FGV-1-fairing-ghost-visual.toml pins these exact names.
            string[] expected =
            {
                "fgv-1-intact", "fgv-1-jettisoned", "fgv-2-intact", "fgv-2-jettisoned",
                "fgv-2-separated", "fgv-2-separated-branch", "fgv-3-intact", "fgv-3-jettisoned",
                "fgv-4-intact", "fgv-4-jettisoned",
            };
            string[] built =
            {
                FairingGhostVisualMath.LabelFor(1, "intact"), FairingGhostVisualMath.LabelFor(1, "jettisoned"),
                FairingGhostVisualMath.LabelFor(2, "intact"), FairingGhostVisualMath.LabelFor(2, "jettisoned"),
                FairingGhostVisualMath.LabelFor(2, "separated"), FairingGhostVisualMath.LabelFor(2, "separated-branch"),
                FairingGhostVisualMath.LabelFor(3, "intact"), FairingGhostVisualMath.LabelFor(3, "jettisoned"),
                FairingGhostVisualMath.LabelFor(4, "intact"), FairingGhostVisualMath.LabelFor(4, "jettisoned"),
            };
            Assert.Equal(expected, built);
            foreach (string label in built)
                Assert.True(TestCommandCaptureScreenshot.IsValidLabel(label), label);
        }

        [Fact]
        public void CapRadius_DecidesTheClosedNose()
        {
            Assert.True(FairingGhostVisualMath.EndsAtCapRadius(new List<double> { 0.625, 0.78, 0.2 }, 0.2));
            Assert.False(FairingGhostVisualMath.EndsAtCapRadius(new List<double> { 1.25, 1.31, 1.31 }, 0.375));
            Assert.False(FairingGhostVisualMath.EndsAtCapRadius(new List<double>(), 0.2));
        }

        [Fact]
        public void GhostOffset_ClearsBothSilhouettes()
        {
            Assert.Equal(2.0 * 1.3125 + 1.2, FairingGhostVisualMath.GhostSideOffset(1.3125, 1.2), 9);
            Assert.Equal(0.0, FairingGhostVisualMath.GhostSideOffset(-1.0, -5.0), 9);
        }
    }
}
