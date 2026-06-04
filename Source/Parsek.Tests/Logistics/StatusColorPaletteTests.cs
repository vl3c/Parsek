using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// L4 lock-in: pins the centralized house status-text palette
    /// (<see cref="ParsekUI.StatusColor"/>) to its canonical RGBA values. Both the
    /// Logistics window (all five kinds) and the Recordings table (cyan only) resolve
    /// their status-text colors through this one source, so freezing the literals here
    /// proves the centralization did not drift any rendered color. The values are taken
    /// verbatim from the prior Logistics palette (the house source); the Recordings
    /// window's other four colors are a separate semantic set and are NOT centralized,
    /// so they are intentionally not asserted here.
    /// </summary>
    public class StatusColorPaletteTests
    {
        [Fact]
        public void StatusColor_Green_IsCanonical()
        {
            Assert.Equal(new Color(0.55f, 1f, 0.55f), ParsekUI.StatusColor(ParsekUI.StatusColorKind.Green));
        }

        [Fact]
        public void StatusColor_Yellow_IsCanonical()
        {
            Assert.Equal(new Color(1f, 1f, 0.4f), ParsekUI.StatusColor(ParsekUI.StatusColorKind.Yellow));
        }

        [Fact]
        public void StatusColor_Red_IsCanonical()
        {
            // The deployed Logistics red is (1, 0.4, 0.4); the plan part-3 text lists a
            // softer (0.95, 0.45, 0.45) but the CODE is the source of truth for "no
            // rendered change", so the canonical Red stays (1, 0.4, 0.4).
            Assert.Equal(new Color(1f, 0.4f, 0.4f), ParsekUI.StatusColor(ParsekUI.StatusColorKind.Red));
        }

        [Fact]
        public void StatusColor_Grey_IsCanonical()
        {
            // Canonical grey is 0.7 (Logistics' value), not 0.6.
            Assert.Equal(new Color(0.7f, 0.7f, 0.7f), ParsekUI.StatusColor(ParsekUI.StatusColorKind.Grey));
        }

        [Fact]
        public void StatusColor_Cyan_IsCanonical()
        {
            // The one color shared with the Recordings table (its Stationary tail).
            Assert.Equal(new Color(0.65f, 0.85f, 1f), ParsekUI.StatusColor(ParsekUI.StatusColorKind.Cyan));
        }
    }
}
