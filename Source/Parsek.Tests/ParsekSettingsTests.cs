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

        /// <summary>
        /// Pins the operator ruling of 2026-09-14: the stock Settings &gt; Difficulty
        /// Options screen draws NO Parsek section. The mechanism is the node's
        /// <c>GameMode</c>: decompiled KSP 1.12.5 <c>DifficultyOptionsMenu</c> skips a
        /// custom parameter node - and so never reaches the
        /// <c>listDictionary.Add(node.Section, ...)</c> that builds the section and its
        /// tab - whenever <c>(node.GameMode &amp; currentGameModeFilter) == 0</c>, tested
        /// before it reads any member or attribute. <c>GameMode.NONE</c> is 0, so the
        /// filter misses for every mode the screen can be opened in.
        ///
        /// This cell reproduces that filter per mode rather than only comparing the
        /// property, so re-annotating or adding a drawable member cannot resurface the
        /// section; and it pins the storage half too, because <c>autoPersistance</c> (not
        /// the GameMode) is what <c>GameParameters.ParameterNode.Save</c> consults when it
        /// writes every public field into the save's <c>ParsekSettings</c> node.
        ///
        /// Fails if: someone restores a non-NONE GameMode (two settings screens for the
        /// same fields, the stock one silently losing the edit - todo GUI-P7), or annotates
        /// a member <c>autoPersistance = false</c> (that value would stop persisting).
        /// </summary>
        [Fact]
        public void StockDifficultyScreen_DrawsNoParsekSection()
        {
            var settings = new ParsekSettings();

            Assert.Equal(GameParameters.GameMode.NONE, settings.GameMode);

            // The four single-mode filters DifficultyOptionsMenu can build, plus ANY.
            foreach (GameParameters.GameMode filter in new[]
            {
                GameParameters.GameMode.SANDBOX,
                GameParameters.GameMode.SCIENCE,
                GameParameters.GameMode.CAREER,
                GameParameters.GameMode.MISSION,
                GameParameters.GameMode.ANY,
            })
            {
                // The stock skip predicate, verbatim: a zero intersection means the node is
                // passed over before any section, tab or control is built for it.
                Assert.Equal(0, (int)(settings.GameMode & filter));
            }

            // Every drawable-annotated member still persists (the attributes are inert for
            // drawing now, but autoPersistance is what keeps the value in the save).
            MemberInfo[] members = typeof(ParsekSettings).GetMembers(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            int annotated = 0;
            foreach (MemberInfo member in members)
            {
                var ui = member.GetCustomAttribute<GameParameters.CustomParameterUI>();
                if (ui == null) continue;
                annotated++;
                Assert.True(ui.autoPersistance,
                    $"{member.Name} would stop persisting into the save");
            }

            // Sanity: the annotated set is non-empty, so the loop above proved something.
            Assert.True(annotated > 0);
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_UsesStoredSamplingDensityWhenPresent()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("samplingDensity", "2");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.High, level);
            Assert.Null(invalidSamplingDensityValue);
        }

        // The three pre-preset-migration cells that used to live here were deleted with the
        // migration itself (GUI census D17): it only fired for a config carrying the four
        // minSampleInterval / maxSampleInterval / velocityDirThreshold / speedChangeThreshold
        // keys, which nothing has written since the preset landed. What remains is the
        // behaviour that replaced it - such a config now reads Medium, the shipping default -
        // plus the invalid-stored-value surface, which is still live.
        [Fact]
        public void ResolveSamplingDensityFromConfig_PrePresetThresholdKeysAreIgnored()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("minSampleInterval", "0.35");
            node.AddValue("maxSampleInterval", "6.5");
            node.AddValue("velocityDirThreshold", "4.5");
            node.AddValue("speedChangeThreshold", "10");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.Medium, level);
            // The key was absent, not invalid, so there is nothing for OnLoad to Warn about.
            Assert.Null(invalidSamplingDensityValue);
        }

        [Fact]
        public void ResolveSamplingDensityFromConfig_InvalidStoredValueFallsBackToMedium()
        {
            var node = new ConfigNode("ParsekSettings");
            node.AddValue("samplingDensity", "99");

            SamplingDensity level = ParsekSettings.ResolveSamplingDensityFromConfig(
                node, out string invalidSamplingDensityValue);

            Assert.Equal(SamplingDensity.Medium, level);
            Assert.Equal("99", invalidSamplingDensityValue);
        }

    }
}
