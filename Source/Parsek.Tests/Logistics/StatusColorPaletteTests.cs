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

        [Fact]
        public void StatusColor_Violet_IsSoftVioletB39DDB()
        {
            Color32 c = ParsekUI.StatusColor(ParsekUI.StatusColorKind.Violet);
            Assert.Equal((byte)0xb3, c.r);
            Assert.Equal((byte)0x9d, c.g);
            Assert.Equal((byte)0xdb, c.b);
            Assert.Equal((byte)0xff, c.a);
        }

        // The shared stepper value cell holds the widest Every readout: every windowed
        // form up to two digits, the flat two-digit / four-digit-day form, a Priority;
        // a narrow measure keeps the floor, NaN never wins.
        [Fact]
        public void StepperValueCell_IsTheWidestMeasuredSamplePlusPadding()
        {
            var samples = LogisticsRoutePresentation.StepperValueSamples();
            Assert.Contains("1x (every window)", samples);
            Assert.Contains("23x (every 23rd window)", samples);
            Assert.Contains("99x (~9999.9d)", samples);
            Assert.Contains("999", samples);
            System.Func<string, float> measure = s => s.Length * 7f;
            float longest = 0f;
            foreach (string s in samples) longest = System.Math.Max(longest, s.Length * 7f);
            Assert.Equal((float)System.Math.Ceiling(longest + LogisticsRoutePresentation.StepperValuePadding),
                LogisticsRoutePresentation.StepperValueCellWidth(measure));
            Assert.Equal(LogisticsRoutePresentation.StepperValueMinWidth,
                LogisticsRoutePresentation.StepperValueCellWidth(s => 1f));
            Assert.Equal(LogisticsRoutePresentation.StepperValueMinWidth,
                LogisticsRoutePresentation.StepperValueCellWidth(s => float.NaN));
            Assert.Equal(70f, LogisticsRoutePresentation.StepperLabelWidth);
            Assert.Equal(24f, LogisticsRoutePresentation.StepperButtonWidth);
        }

        // Both stepper rows take the taller of a slot-button line and a label line.
        [Fact]
        public void StepperRowHeight_IsTheTallerLineRoundedUp()
        {
            Assert.Equal(29f, LogisticsRoutePresentation.StepperRowHeight(28.2f, 21f));
            Assert.Equal(22f, LogisticsRoutePresentation.StepperRowHeight(float.NaN, 21.5f));
            Assert.Equal(0f, LogisticsRoutePresentation.StepperRowHeight(-1f, float.PositiveInfinity));
        }

        // The Logistics section accent bars: green Active, soft violet Paused, cyan
        // Candidates, all named palette slots rather than inline literals.
        [Fact]
        public void LogisticsSectionAccents_AreTheNamedPaletteSlots()
        {
            Assert.Equal(ParsekUI.StatusColorKind.Green,
                LogisticsRoutePresentation.SectionAccent(LogisticsRoutePresentation.ActiveSectionName));
            Assert.Equal(ParsekUI.StatusColorKind.Violet,
                LogisticsRoutePresentation.SectionAccent(LogisticsRoutePresentation.PausedSectionName));
            Assert.Equal(ParsekUI.StatusColorKind.Cyan,
                LogisticsRoutePresentation.SectionAccent(LogisticsRoutePresentation.CandidatesSectionName));
        }
    }
}
