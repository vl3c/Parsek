using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the Phase 8 <c>useOutlierRejection</c> rollout flag (design
    /// doc §18 Phase 8, §19.2 Outlier Rejection row "Settings flag flip").
    /// Mirrors <see cref="UseCoBubbleBlendSettingTests"/> line-for-line so
    /// the rollout-gate contract is consistent across phases.
    /// </summary>
    [Collection("Sequential")]
    public class UseOutlierRejectionSettingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UseOutlierRejectionSettingTests()
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
        public void UseOutlierRejection_DefaultsTrue()
        {
            // What makes it fail: a default of false would leave Phase 8
            // outlier rejection off for fresh installs, masking regressions.
            var settings = new ParsekSettings();
            Assert.True(settings.useOutlierRejection);
        }

        [Fact]
        public void UseOutlierRejection_PersistsAcrossLoad()
        {
            ParsekSettingsPersistence.SetStoredUseOutlierRejectionForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseOutlierRejection().Value);

            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUseOutlierRejection());

            ParsekSettingsPersistence.SetStoredUseOutlierRejectionForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseOutlierRejection().Value);
        }

        [Fact]
        public void UseOutlierRejection_FlipLogsInfo()
        {
            // Per §19.2 Outlier Rejection: a Pipeline-Outlier Info line so
            // the rollout-gate flip is visible in KSP.log.
            ParsekSettings.NotifyUseOutlierRejectionChanged(false, true);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-Outlier]")
                && l.IndexOf("false->true", StringComparison.OrdinalIgnoreCase) >= 0
                && l.Contains("useOutlierRejection"));
        }

        [Fact]
        public void UseOutlierRejection_NoLogWhenUnchanged()
        {
            ParsekSettings.NotifyUseOutlierRejectionChanged(true, true);
            Assert.DoesNotContain(logLines,
                l => l.Contains("[Pipeline-Outlier]") && l.Contains("useOutlierRejection:"));
        }

        [Fact]
        public void UseOutlierRejection_ConfigHashChangesOnFlip()
        {
            // Without byte-85 in the canonical encoding, a flag flip would
            // not invalidate the cached .pann (HR-10 violation).
            byte[] flagOn = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default,
                useAnchorTaxonomy: true, useCoBubbleBlend: true, useOutlierRejection: true);
            byte[] flagOff = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default,
                useAnchorTaxonomy: true, useCoBubbleBlend: true, useOutlierRejection: false);
            Assert.NotEqual(flagOn, flagOff);
        }

        [Fact]
        public void UseOutlierRejection_ThresholdFlipChangesHash()
        {
            // Phase 8 also pins HR-10 freshness for individual threshold
            // bytes. Perturbing one threshold should change the hash.
            OutlierThresholds tweaked = OutlierThresholds.Default;
            tweaked.AccelCeilingExoBallistic = 999.0f;
            byte[] defHash = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default, OutlierThresholds.Default,
                useAnchorTaxonomy: true, useCoBubbleBlend: true, useOutlierRejection: true);
            byte[] tweakedHash = PannotationsSidecarBinary.ComputeConfigurationHash(
                SmoothingConfiguration.Default, tweaked,
                useAnchorTaxonomy: true, useCoBubbleBlend: true, useOutlierRejection: true);
            Assert.NotEqual(defHash, tweakedHash);
        }

        [Fact]
        public void UseOutlierRejection_DirectAssignThroughProperty_LogsInfo()
        {
            var settings = new ParsekSettings();
            settings.useOutlierRejection = false;
            Assert.Contains(logLines, l => l.Contains("[Pipeline-Outlier]")
                && l.IndexOf("true->false", StringComparison.OrdinalIgnoreCase) >= 0
                && l.Contains("useOutlierRejection"));
        }
    }
}
