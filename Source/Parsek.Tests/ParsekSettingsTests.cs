using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
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

        [Fact]
        public void AutoRecordOnSwitchSettings_UseCustomParameterUiAttribute()
        {
            // Fails if: the first-modification toggle is renamed / removed / loses
            // its [GameParameters.CustomParameterUI] annotation and stops showing
            // up in the KSP difficulty options panel.
            FieldInfo field = typeof(ParsekSettings).GetField(
                nameof(ParsekSettings.autoRecordOnFirstModificationAfterSwitch));
            Assert.NotNull(field);
            Assert.NotNull(field.GetCustomAttribute<GameParameters.CustomParameterUI>());
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
