using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the Settings window's labels and hover texts that the literal-tooltip scan in
    /// <see cref="TooltipEchoBudgetTests"/> cannot see (texts chosen by a method or a
    /// conditional), plus the pure layout and persistence-timing helpers behind the
    /// pressed-toggle option rows and the ghost-audio slider.
    /// </summary>
    public class SettingsWindowTextTests
    {
        private static int SettingsStripBudget =>
            TooltipEchoBudgetTests.BudgetChars(280f, TooltipEchoBox.DoubleLine);

        public static IEnumerable<object[]> HoverTexts()
        {
            yield return new object[] { nameof(SettingsWindowUI.BasicModeTooltip), SettingsWindowUI.BasicModeTooltip };
            yield return new object[] { nameof(SettingsWindowUI.AdvancedModeTooltip), SettingsWindowUI.AdvancedModeTooltip };
            yield return new object[] { nameof(SettingsWindowUI.AutoLaunchTooltip), SettingsWindowUI.AutoLaunchTooltip };
            yield return new object[] { nameof(SettingsWindowUI.VerboseLoggingTooltip), SettingsWindowUI.VerboseLoggingTooltip };
            yield return new object[] { nameof(SettingsWindowUI.ReadableMirrorsTooltip), SettingsWindowUI.ReadableMirrorsTooltip };
            yield return new object[] { nameof(SettingsWindowUI.WipeRecordingsTooltip), SettingsWindowUI.WipeRecordingsTooltip };
            yield return new object[] { nameof(SettingsWindowUI.WipeMilestonesTooltip), SettingsWindowUI.WipeMilestonesTooltip };
            yield return new object[] { nameof(SettingsWindowPresentation.DefaultsButtonTooltip), SettingsWindowPresentation.DefaultsButtonTooltip };
        }

        // catches: a hover text reached through a method or a ternary (invisible to the
        // literal GUIContent scan) growing past the 280 px window's two-line strip.
        [Theory]
        [MemberData(nameof(HoverTexts))]
        public void HoverText_FitsTheSettingsStrip(string name, string text)
        {
            Assert.False(string.IsNullOrEmpty(text), name);
            Assert.DoesNotContain("\n", text);
            Assert.True(text.Length <= SettingsStripBudget,
                $"{name} is {text.Length} chars, over the {SettingsStripBudget}-char Settings strip: \"{text}\"");
        }

        // The name the main window's launcher for each surface carries, or null for a
        // surface that is not a main-window launcher.
        private static string LauncherName(UiSurface surface)
        {
            switch (surface)
            {
                case UiSurface.MainButtonTimeline: return "Timeline";
                case UiSurface.MainButtonRecordings: return "Missions";
                case UiSurface.MainButtonLogistics: return "Logistics";
                case UiSurface.MainButtonKerbals: return "Kerbals";
                case UiSurface.MainButtonSettings: return "Settings";
                case UiSurface.MainButtonCareer: return "Career";
                case UiSurface.MainButtonSpawnControl: return "Spawn";
                case UiSurface.MainButtonGloops: return "Gloops";
                default:
                    Assert.False(surface.ToString().StartsWith("MainButton", StringComparison.Ordinal),
                        $"{surface} is a new main-window launcher: name it here so the Basic hover is checked against it");
                    return null;
            }
        }

        // catches: the Basic hover drifting from the real Basic launcher set again (it left
        // out Kerbals, shown in Basic since the 2026-09-22 re-ruling). Derived from
        // UiSurfaceVisibility, so the next launcher moved in or out of Basic reds here.
        [Fact]
        public void BasicModeTooltip_NamesExactlyTheLaunchersBasicShows()
        {
            string tip = SettingsWindowUI.UiComplexityModeTooltip(UiComplexityMode.Basic);
            Assert.Equal(SettingsWindowUI.BasicModeTooltip, tip);

            int shown = 0;
            foreach (UiSurface surface in Enum.GetValues(typeof(UiSurface)))
            {
                string name = LauncherName(surface);
                if (name == null) continue;
                if (UiSurfaceVisibility.IsVisible(surface, UiComplexityMode.Basic))
                {
                    shown++;
                    Assert.Contains(name, tip);
                }
                else
                {
                    Assert.DoesNotContain(name, tip);
                }
            }
            Assert.Equal(5, shown);
            Assert.Equal(SettingsWindowUI.AdvancedModeTooltip,
                SettingsWindowUI.UiComplexityModeTooltip(UiComplexityMode.Advanced));
        }

        [Fact]
        public void VerboseLogging_LabelAndHover()
        {
            Assert.Equal(" Verbose logging", SettingsWindowUI.VerboseLoggingLabel);
            Assert.DoesNotContain("development", SettingsWindowUI.VerboseLoggingLabel);
            Assert.Equal("Detailed Parsek lines in KSP.log. Keep on if you report bugs.",
                SettingsWindowUI.VerboseLoggingTooltip);
        }

        // catches: the mirrors toggle going back to a "Warning" label now that it is OFF by
        // default, and its hover losing either half (what the files are for, and the cost).
        [Fact]
        public void ReadableMirrors_LabelAndHover()
        {
            Assert.DoesNotContain("Warning", SettingsWindowUI.ReadableMirrorsLabel);
            Assert.Contains(".txt", SettingsWindowUI.ReadableMirrorsLabel);
            Assert.Contains("bug reports", SettingsWindowUI.ReadableMirrorsTooltip);
            Assert.Contains("disk", SettingsWindowUI.ReadableMirrorsTooltip);
        }

        // catches: an enabled wipe button with no hover, or a greyed one publishing its
        // enabled-state text alongside the DisabledHoverEcho reason.
        [Fact]
        public void WipeButtonTooltip_OnlyWhileEnabled()
        {
            Assert.Equal(SettingsWindowUI.WipeRecordingsTooltip,
                SettingsWindowUI.WipeButtonTooltip(true, SettingsWindowUI.WipeRecordingsTooltip));
            Assert.Equal(string.Empty,
                SettingsWindowUI.WipeButtonTooltip(false, SettingsWindowUI.WipeRecordingsTooltip));
            Assert.Equal(SettingsWindowUI.WipeMilestonesTooltip,
                SettingsWindowUI.WipeButtonTooltip(true, SettingsWindowUI.WipeMilestonesTooltip));
            Assert.Equal(string.Empty,
                SettingsWindowUI.WipeButtonTooltip(false, SettingsWindowUI.WipeMilestonesTooltip));
        }

        // catches: the milestone wipe hover promising a ledger wipe. MilestoneStore.ClearAll
        // clears the milestone list only; every GameAction survives (finding P5).
        [Fact]
        public void WipeMilestonesTooltip_SaysCareerActionsStay()
        {
            Assert.Contains("milestone", SettingsWindowUI.WipeMilestonesTooltip);
            Assert.Contains("career actions stay", SettingsWindowUI.WipeMilestonesTooltip);
            Assert.DoesNotContain("game action", SettingsWindowUI.WipeMilestonesTooltip);
            Assert.Contains("Asks first", SettingsWindowUI.WipeRecordingsTooltip);
            Assert.Contains("Asks first", SettingsWindowUI.WipeMilestonesTooltip);
        }

        [Fact]
        public void DefaultsButtonTooltip_ClaimsAdvancedOnlySettingsAndExemptsTheMode()
        {
            Assert.Contains("Advanced-only", SettingsWindowPresentation.DefaultsButtonTooltip);
            Assert.Contains("except the interface mode", SettingsWindowPresentation.DefaultsButtonTooltip);
        }

        // catches: option cells of different widths (the old selected-is-a-box look resized
        // both buttons on every switch) or a row that no longer spans the window.
        [Theory]
        [InlineData(280f, 2)]
        [InlineData(280f, 3)]
        [InlineData(400f, 2)]
        [InlineData(400f, 3)]
        public void OptionCellWidth_EqualCellsSpanTheWindow(float windowWidth, int cells)
        {
            float w = SettingsWindowPresentation.OptionCellWidth(windowWidth, cells);
            float spent = w * cells
                + SettingsWindowPresentation.OptionToggleMarginPx * (cells + 1)
                + 22f;
            Assert.Equal((double)windowWidth, (double)spent, 3);
        }

        [Fact]
        public void OptionCellWidth_FloorsNarrowWindowsAndBadCellCounts()
        {
            Assert.Equal(30.0, (double)SettingsWindowPresentation.OptionCellWidth(50f, 3), 3);
            Assert.Equal(
                (double)SettingsWindowPresentation.OptionCellWidth(280f, 1),
                (double)SettingsWindowPresentation.OptionCellWidth(280f, 0), 3);
        }

        // catches: the ghost-audio slider writing settings.cfg every frame of a drag, or a
        // finished drag never being written.
        [Theory]
        [InlineData(false, 0, false)]
        [InlineData(false, 17, false)]
        [InlineData(true, 17, false)]
        [InlineData(true, 0, true)]
        public void ShouldFlushGhostAudioVolume_OnlyAfterTheDragEnds(bool pending, int hotControl, bool expected)
        {
            Assert.Equal(expected, SettingsWindowPresentation.ShouldFlushGhostAudioVolume(pending, hotControl));
        }
    }
}
