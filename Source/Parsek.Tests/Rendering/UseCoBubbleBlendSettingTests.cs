using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the Phase 5 <c>useCoBubbleBlend</c> rollout flag (design doc
    /// §18 Phase 5, §19.2 Stage 5 row "Settings flag flip"). Mirrors the
    /// Phase 6 pattern in <see cref="UseAnchorTaxonomySettingTests"/>
    /// line-for-line so the rollout-gate contract is consistent across
    /// phases.
    /// </summary>
    [Collection("Sequential")]
    public class UseCoBubbleBlendSettingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UseCoBubbleBlendSettingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekSettingsPersistence.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void UseCoBubbleBlend_DefaultsTrue()
        {
            // What makes it fail: a default of false would leave Phase 5
            // co-bubble blend off for fresh installs, masking regressions.
            var settings = new ParsekSettings();
            Assert.True(settings.useCoBubbleBlend);
        }

        [Fact]
        public void UseCoBubbleBlend_PersistsAcrossLoad()
        {
            // What makes it fail: missing the flag from
            // ParsekSettingsPersistence.Save would lose the user's choice
            // on every reload.
            ParsekSettingsPersistence.SetStoredUseCoBubbleBlendForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseCoBubbleBlend().Value);

            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUseCoBubbleBlend());

            ParsekSettingsPersistence.SetStoredUseCoBubbleBlendForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseCoBubbleBlend().Value);
        }

        [Fact]
        public void UseCoBubbleBlend_FlipLogsInfo()
        {
            // Per §19.2 Stage 5: a Pipeline-CoBubble Info line so the
            // rollout-gate flip is visible in KSP.log.
            ParsekSettings.NotifyUseCoBubbleBlendChanged(false, true);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.IndexOf("false->true", StringComparison.OrdinalIgnoreCase) >= 0
                && l.Contains("useCoBubbleBlend"));
        }

        [Fact]
        public void UseCoBubbleBlend_NoLogWhenUnchanged()
        {
            ParsekSettings.NotifyUseCoBubbleBlendChanged(true, true);
            Assert.DoesNotContain(logLines,
                l => l.Contains("[Pipeline-CoBubble]") && l.Contains("useCoBubbleBlend:"));
        }

        [Fact]
        public void UseCoBubbleBlend_ConfigHashChangesOnFlip()
        {
            // Without byte-52 in the canonical encoding, a flag flip
            // wouldn't invalidate the cached .pann (HR-10 violation).
            byte[] flagOn = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default, useAnchorTaxonomy: true, useCoBubbleBlend: true);
            byte[] flagOff = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default, useAnchorTaxonomy: true, useCoBubbleBlend: false);
            Assert.NotEqual(flagOn, flagOff);
        }

        [Fact]
        public void UseCoBubbleBlend_DirectAssignThroughProperty_LogsInfo()
        {
            var settings = new ParsekSettings();
            settings.useCoBubbleBlend = false;
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.IndexOf("true->false", StringComparison.OrdinalIgnoreCase) >= 0
                && l.Contains("useCoBubbleBlend"));
        }
    }
}
