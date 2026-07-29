using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 6 source-text gate for the Basic / Advanced UI mode
    /// (`docs/dev/design-ui-basic-advanced.md` sections 6.3, 7.1, 14).
    ///
    /// <para>The gated surfaces themselves are pure and already covered: `IsVisible` for
    /// `SettingsSectionDiagnostics` / `SettingsSectionSampleDensity` is asserted by
    /// `UiComplexityModeTests`. What that CANNOT catch is the wiring: `DrawSettingsWindow`
    /// is an IMGUI callback with no headless seam, and deliberately so - the sections are
    /// plain `DrawXSettings(s)` calls, and extracting a "ShouldDrawSection" indirection
    /// purely to make a constant-true predicate testable would add a layer the code does
    /// not need. So this gate reads the source instead, which is the established pattern
    /// here (`DestinationLoiterTrimWiringTests`, `MissionCrossTreeDockUiWiringTests`).</para>
    ///
    /// <para>Two regressions it catches: a section losing its gate (Basic silently shows
    /// developer instrumentation again), and a section's trailing `GUILayout.Space`
    /// separator escaping its gate (Basic shows a double gap - the same rule phase 4
    /// applied to the main-window button separators).</para>
    ///
    /// <para>xUnit runs from `Source/Parsek.Tests/bin/Debug/net472/`, hence the 5 ".."
    /// segments to the repo root (precedent: `ChainSaveLoadTests`).</para>
    /// </summary>
    public class SettingsSectionGateWiringTests
    {
        private static string ReadSettingsWindowSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "UI", "SettingsWindowUI.cs");
            if (!File.Exists(path))
                path = Path.Combine(projectRoot, "Parsek", "UI", "SettingsWindowUI.cs");
            Assert.True(File.Exists(path), $"SettingsWindowUI.cs not found at {path}");
            return File.ReadAllText(path);
        }

        // The section draw AND its trailing separator must both sit inside the gate.
        private static void AssertSectionGated(string src, string surface, string drawCall)
        {
            var block = new Regex(
                @"if\s*\(\s*UiSurfaceVisibility\.IsVisible\(\s*UiSurface\." + Regex.Escape(surface)
                + @"\s*,\s*complexity\s*\)\s*\)\s*\{(?<body>[^}]*)\}",
                RegexOptions.Singleline);

            Match m = block.Match(src);
            Assert.True(m.Success,
                $"SettingsWindowUI must wrap the {surface} section in "
                + $"IsVisible(UiSurface.{surface}, complexity) reading the frame-latched mode.");

            string body = m.Groups["body"].Value;
            Assert.Contains(drawCall, body);
            Assert.Contains("GUILayout.Space(SpacingSmall);", body);

            // ...and nowhere else, or the section would draw twice / ungated.
            Assert.Equal(1, Regex.Matches(src, Regex.Escape(drawCall)).Count);
        }

        [Fact]
        public void DiagnosticsSectionIsGatedWithItsSeparator()
        {
            AssertSectionGated(
                ReadSettingsWindowSource(),
                "SettingsSectionDiagnostics",
                "DrawDiagnosticsSettings(s);");
        }

        [Fact]
        public void SampleDensitySectionIsGatedWithItsSeparator()
        {
            AssertSectionGated(
                ReadSettingsWindowSource(),
                "SettingsSectionSampleDensity",
                "DrawSamplingSettings(s);");
        }

        // Design 7.1: the gates must read the frame-latched applied mode, never
        // `ParsekSettings.uiComplexityMode`. A raw read changes the IMGUI control count
        // between one frame's Layout and Repaint passes, and the Interface section that
        // hosts the toggle draws BEFORE these two in the same callback.
        [Fact]
        public void GatesReadTheFrameLatchedMode()
        {
            string src = ReadSettingsWindowSource();

            Assert.Contains("UiComplexityMode complexity = ParsekUI.AppliedUiComplexityMode;", src);
            Assert.DoesNotContain("s.uiComplexityMode", src);
            Assert.DoesNotContain("settings.uiComplexityMode", src);
        }

        // Design section 4 / the "Result" paragraph: Basic hides exactly Diagnostics and
        // Sample Density. Everything else in the window is unconditional, including the
        // Interface section that hosts the mode toggle itself and Data Management.
        [Fact]
        public void NoOtherSettingsSectionIsGated()
        {
            string src = ReadSettingsWindowSource();

            foreach (string drawCall in new[]
                     {
                         "DrawInterfaceSettings(s);",
                         "DrawRecordingSettings(s);",
                         "DrawLoopingSettings(s);",
                         "DrawGhostSettings(s);",
                         "DrawStockUiSettings(s);",
                         "DrawDataManagementSettings(s);",
                     })
            {
                Assert.Contains("\n            " + drawCall, src);
            }

            // Exactly two gated sections in this file.
            Assert.Equal(2, Regex.Matches(src, @"UiSurfaceVisibility\.IsVisible\(").Count);
        }
    }
}
