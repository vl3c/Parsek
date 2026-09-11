using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    // The Basic-mode gate over the Missions tab's manual-loop authoring controls (design
    // docs/dev/design-ui-basic-advanced.md section 4.5): the per-mission "Loop" toggle, the
    // loop-period cell beside it, and the include checkboxes that pick which intervals /
    // partner journeys the loop replays.
    //
    // The draw sites themselves are IMGUI callbacks with no headless seam, so what is
    // testable is the decision they all read: MissionsWindowUI.ShowsLoopAuthoringControls.
    // Each test names the regression it catches.
    public class MissionsWindowLoopGateTests
    {
        // The feature itself. Basic drops the three controls; Advanced is unchanged
        // (philosophy 6 - Advanced stays behaviorally identical to today).
        [Fact]
        public void BasicHidesTheLoopAuthoringControlsAndAdvancedKeepsThem()
        {
            Assert.False(
                MissionsWindowUI.ShowsLoopAuthoringControls(UiComplexityMode.Basic),
                "Basic must not draw the Missions tab's manual-loop authoring controls");
            Assert.True(
                MissionsWindowUI.ShowsLoopAuthoringControls(UiComplexityMode.Advanced),
                "Advanced must keep every loop control it draws today");
        }

        // The gate is the shared decision point, not a private second opinion (philosophy 4).
        // Fails if someone re-implements the rule inline instead of re-keying this helper.
        [Fact]
        public void TheGateIsDerivedFromTheSharedSurfaceDecision()
        {
            foreach (UiComplexityMode mode in new[] { UiComplexityMode.Basic, UiComplexityMode.Advanced })
            {
                Assert.Equal(
                    UiSurfaceVisibility.IsVisible(UiSurface.MissionsLoopControls, mode),
                    MissionsWindowUI.ShowsLoopAuthoringControls(mode));
            }
        }

        // Scope guard. The loop controls live INSIDE the Missions tab, so a mis-keyed gate
        // (pointing at the tab instead of the control group) would take the whole tab dark in
        // Basic - the surface design section 4 pins as a core-loop keeper.
        [Fact]
        public void HidingTheLoopControlsDoesNotHideTheMissionsTab()
        {
            Assert.True(
                UiSurfaceVisibility.IsVisible(UiSurface.TabMissions, UiComplexityMode.Basic),
                "the Missions tab must stay visible in Basic; only its loop controls are gated");
            Assert.True(
                UiSurfaceVisibility.IsVisible(UiSurface.MainButtonRecordings, UiComplexityMode.Basic),
                "the Missions launcher must stay visible in Basic");
        }

        // catches: an include checkbox drawn OUTSIDE the gate, which is finding P21 exactly -
        // the chapter header row's tri-state Toggle wrote Mission.ExcludedIntervalKeys with no
        // ShowsLoopAuthoringControls check, so in Basic a player still had a click that
        // authored the loop set while every checkbox around it was hidden. Source inspection
        // because the draw sites are IMGUI callbacks with no headless seam; the scan is over
        // the four GUIContent fields the include family uses, so a NEW include checkbox is
        // covered the moment it takes one of them (and a fifth field reds the count below).
        [Fact]
        public void EveryIncludeCheckboxIsDrawnInsideTheLoopAuthoringGate()
        {
            string src = ReadMissionsWindowSource();
            string[] includeContents =
            {
                "VesselIncludeCheckboxContent",
                "IntervalIncludeCheckboxContent",
                "ChapterIncludeCheckboxContent",
                "PartnerJourneyCheckboxContent",
            };

            foreach (string field in includeContents)
            {
                int useIndex = IndexOfToggleUsing(src, field);
                Assert.True(useIndex > 0,
                    field + " should be passed to a GUILayout.Toggle in MissionsWindowUI.cs");

                int methodStart = LastIndexOfMethodStart(src, useIndex);
                Assert.True(methodStart >= 0,
                    "could not locate the enclosing method of the " + field + " draw site");

                string enclosing = src.Substring(methodStart, useIndex - methodStart);
                Assert.Contains("ShowsLoopAuthoringControls(", enclosing);
            }
        }

        // catches: a fifth include-family GUIContent field appearing without a row in the scan
        // above, which would leave it ungated and unnoticed.
        [Fact]
        public void TheIncludeCheckboxContentFamilyIsStillFourFields()
        {
            string src = ReadMissionsWindowSource();
            var found = new List<string>();
            const string Needle = "CheckboxContent =";
            int i = 0;
            while (true)
            {
                int at = src.IndexOf(Needle, i, StringComparison.Ordinal);
                if (at < 0) break;
                int lineStart = src.LastIndexOf('\n', at) + 1;
                found.Add(src.Substring(lineStart, at - lineStart).Trim());
                i = at + Needle.Length;
            }

            Assert.Equal(4, found.Count);
        }

        private static string ReadMissionsWindowSource()
        {
            string srcRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Parsek"));
            return File.ReadAllText(Path.Combine(srcRoot, "UI", "MissionsWindowUI.cs"));
        }

        private static int IndexOfToggleUsing(string src, string contentField)
        {
            const string Needle = "GUILayout.Toggle(";
            int i = 0;
            while (true)
            {
                int at = src.IndexOf(Needle, i, StringComparison.Ordinal);
                if (at < 0) return -1;
                int close = src.IndexOf(')', at);
                int scanEnd = close < 0 ? Math.Min(src.Length, at + 200) : close;
                if (src.IndexOf(contentField, at, scanEnd - at, StringComparison.Ordinal) >= 0)
                    return at;
                i = at + Needle.Length;
            }
        }

        // Methods in this file are declared at 8-space indentation, so the last such
        // declaration before an index is the enclosing method's opening line.
        private static int LastIndexOfMethodStart(string src, int before)
        {
            int privateAt = src.LastIndexOf("\n        private ", before, StringComparison.Ordinal);
            int internalAt = src.LastIndexOf("\n        internal ", before, StringComparison.Ordinal);
            return Math.Max(privateAt, internalAt);
        }
    }
}
