using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    // Sequential: the AutomationEnvPresent test below mutates process-wide env vars
    // and the static automation-env cache.
    [Collection("Sequential")]
    public class ParsekSettingsTests
    {
        [Fact]
        public void SamplingDensityField_UsesCustomIntParameterUi()
        {
            FieldInfo field = typeof(ParsekSettings).GetField(nameof(ParsekSettings.samplingDensity));

            Assert.NotNull(field);
            Assert.NotNull(field.GetCustomAttribute<GameParameters.CustomIntParameterUI>());
        }

        /// <summary>
        /// Pins the default value of the first-modification auto-record toggle.
        /// Defaults ON so the existing post-switch first-modification watcher
        /// stays armed out of the box; flipping the default is a user-visible
        /// behaviour change and must be intentional.
        /// </summary>
        [Fact]
        public void AutoRecordOnSwitchSettings_DefaultOn()
        {
            var settings = new ParsekSettings();

            // Fails if: autoRecordOnFirstModificationAfterSwitch default flipped off.
            Assert.True(settings.autoRecordOnFirstModificationAfterSwitch);
        }

        /// <summary>
        /// The hidden-but-kept settings have no player-facing UI, so a stale value KSP
        /// round-tripped through an old save must be clamped back to the shipping value on
        /// load (ParsekScenario.OnLoad, players only - an armed harness keeps authority).
        /// Pins the pure clamp core: every drifted field is reset, and the changed flag is
        /// true exactly when something actually moved.
        /// </summary>
        [Fact]
        public void ClampHiddenSettingsToShippingValues_ResetsDriftAndReportsChange()
        {
            var drifted = new ParsekSettings
            {
                autoRecordOnLaunch = false,
                autoRecordOnEva = false,
                autoRecordOnFirstModificationAfterSwitch = false,
                autoMerge = false,
                forceFaithfulLoopPlayback = true,
            };

            Assert.True(ParsekSettings.ClampHiddenSettingsToShippingValues(drifted));
            Assert.True(drifted.autoRecordOnLaunch);
            Assert.True(drifted.autoRecordOnEva);
            Assert.True(drifted.autoRecordOnFirstModificationAfterSwitch);
            Assert.True(drifted.autoMerge);
            Assert.False(drifted.forceFaithfulLoopPlayback);

            // Already-shipping values: no-op, and reported as such (the caller only logs
            // when something moved).
            Assert.False(ParsekSettings.ClampHiddenSettingsToShippingValues(drifted));
            Assert.False(ParsekSettings.ClampHiddenSettingsToShippingValues(null));
        }

        /// <summary>
        /// The clamp's automation gate must arm off the hooks' OWN env vars: the command
        /// seam's exact arm value, and any non-empty autorun value. If this gate ever went
        /// false under a harness launch, the clamp would overwrite fixture-pinned /
        /// SetSetting hidden-field values (~40 committed fixtures pin autoMerge=False) at
        /// every scene load. Drives the read-once cache through its test seam; env vars
        /// are process-wide, hence [Collection("Sequential")] on this class.
        /// </summary>
        [Theory]
        [InlineData("1", null, true)]    // command seam armed (the harness's unconditional launch shape)
        [InlineData("0", null, false)]   // seam is exact-match fail-closed
        [InlineData(null, "Missions", true)]  // autorun batch armed
        [InlineData(null, null, false)]  // player session: clamp active
        public void AutomationEnvPresent_ArmsOffTheHookEnvVars(
            string testCommands, string autorunTests, bool expected)
        {
            string priorSeam = System.Environment.GetEnvironmentVariable(
                TestCommands.ParsekTestCommandAddon.EnvVarName);
            string priorAutorun = System.Environment.GetEnvironmentVariable(
                InGameTests.TestRunnerShortcut.EnvTestsVar);
            try
            {
                System.Environment.SetEnvironmentVariable(
                    TestCommands.ParsekTestCommandAddon.EnvVarName, testCommands);
                System.Environment.SetEnvironmentVariable(
                    InGameTests.TestRunnerShortcut.EnvTestsVar, autorunTests);
                ParsekSettings.ResetAutomationEnvCacheForTesting();

                Assert.Equal(expected, ParsekSettings.AutomationEnvPresent);
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(
                    TestCommands.ParsekTestCommandAddon.EnvVarName, priorSeam);
                System.Environment.SetEnvironmentVariable(
                    InGameTests.TestRunnerShortcut.EnvTestsVar, priorAutorun);
                ParsekSettings.ResetAutomationEnvCacheForTesting();
            }
        }

        /// <summary>
        /// Pins the shipping default of the auto-merge toggle. Defaults ON since
        /// 0.10.4: the silent auto-commit path now commits with full spawn-at-end
        /// fidelity (it used to be lossy, which is what kept the default OFF), so a
        /// finished mission goes to the timeline without a per-flight confirmation
        /// dialog. Flipping this back is a user-visible behaviour change and must be
        /// intentional.
        ///
        /// Fails if: the autoMerge field default is flipped off.
        /// </summary>
        [Fact]
        public void AutoMerge_DefaultOn()
        {
            var settings = new ParsekSettings();

            Assert.True(settings.autoMerge);
        }

        [Fact]
        public void HiddenSettings_CarryNoCustomParameterUiAttribute()
        {
            // The 2026-08-27 settings simplification HID the auto-record trio and
            // autoMerge from every UI (Settings window and the KSP difficulty panel);
            // the harness command seam is their only writer. Fails if someone
            // re-annotates one of them, which would resurface a second writer in the
            // stock difficulty screen.
            foreach (string name in new[]
            {
                nameof(ParsekSettings.autoRecordOnLaunch),
                nameof(ParsekSettings.autoRecordOnEva),
                nameof(ParsekSettings.autoRecordOnFirstModificationAfterSwitch),
                nameof(ParsekSettings.autoMerge),
                nameof(ParsekSettings.forceFaithfulLoopPlayback),
            })
            {
                FieldInfo field = typeof(ParsekSettings).GetField(name);
                Assert.NotNull(field);
                Assert.Null(field.GetCustomAttribute<GameParameters.CustomParameterUI>());
            }
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_UsesStoredSamplingDensityWhenPresent()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("samplingDensity", "2");
            node.AddValue("minSampleInterval", "0.5");
            node.AddValue("maxSampleInterval", "8");
            node.AddValue("velocityDirThreshold", "6");
            node.AddValue("speedChangeThreshold", "12");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.High, level);
            Assert.False(migratedFromLegacy);
            Assert.Null(invalidSamplingDensityValue);
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_MigratesLegacyThresholdsToNearestPreset()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("minSampleInterval", "0.35");
            node.AddValue("maxSampleInterval", "6.5");
            node.AddValue("velocityDirThreshold", "4.5");
            node.AddValue("speedChangeThreshold", "10");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.Low, level);
            Assert.True(migratedFromLegacy);
            Assert.Null(invalidSamplingDensityValue);
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_UsesLegacyDefaultsForMissingFields()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("maxSampleInterval", "3");
            node.AddValue("speedChangeThreshold", "5");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.Medium, level);
            Assert.True(migratedFromLegacy);
            Assert.Null(invalidSamplingDensityValue);
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_InvalidStoredValueFallsBackToLegacyThresholds()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("samplingDensity", "99");
            node.AddValue("minSampleInterval", "0.5");
            node.AddValue("maxSampleInterval", "8");
            node.AddValue("velocityDirThreshold", "6");
            node.AddValue("speedChangeThreshold", "12");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.Low, level);
            Assert.True(migratedFromLegacy);
            Assert.Equal("99", invalidSamplingDensityValue);
        }
    }
}
