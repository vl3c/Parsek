using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the Phase 6 <c>useAnchorTaxonomy</c> rollout flag (design
    /// doc §18 Phase 6, §19.2 Stage 3 / Stage 3b row "Settings flag flip").
    /// Default, persistence round-trip, and Pipeline-Anchor log line on
    /// flip. Mirrors the Phase 2 pattern in
    /// <see cref="UseAnchorCorrectionSettingTests"/> line-for-line so the
    /// rollout-gate contract is consistent across phases.
    /// </summary>
    [Collection("Sequential")]
    public class UseAnchorTaxonomySettingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UseAnchorTaxonomySettingTests()
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
        public void UseAnchorTaxonomy_DefaultsTrue()
        {
            // What makes it fail: a default of false would leave Phase 6's
            // anchor taxonomy off for fresh installs, masking regressions
            // and making the rollout invisible.
            var settings = new ParsekSettings();
            Assert.True(settings.useAnchorTaxonomy);
        }

        [Fact]
        public void UseAnchorTaxonomy_PersistsAcrossLoad()
        {
            // What makes it fail: if the flag is not in
            // ParsekSettingsPersistence, KSP's GameParameters reset would
            // silently revert the user's preference on every save load.
            ParsekSettingsPersistence.SetStoredUseAnchorTaxonomyForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseAnchorTaxonomy().Value);

            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUseAnchorTaxonomy());

            ParsekSettingsPersistence.SetStoredUseAnchorTaxonomyForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseAnchorTaxonomy().Value);
        }

        [Fact]
        public void UseAnchorTaxonomy_FlipLogsInfo()
        {
            // What makes it fail: the rollout-gate flip must produce
            // exactly one Pipeline-Anchor Info line per design doc §19.2
            // so a developer can attribute a visual artifact to the moment
            // the gate changed.
            ParsekSettings.NotifyUseAnchorTaxonomyChanged(false, true);

            Assert.Contains(logLines, l => l.Contains("[Pipeline-Anchor]")
                && l.IndexOf("false->true", StringComparison.OrdinalIgnoreCase) >= 0
                && l.Contains("useAnchorTaxonomy"));
        }

        [Fact]
        public void UseAnchorTaxonomy_NoLogWhenUnchanged()
        {
            // What makes it fail: a no-op flip emitting the line would
            // spam the log on every settings-window save.
            ParsekSettings.NotifyUseAnchorTaxonomyChanged(true, true);

            Assert.DoesNotContain(logLines,
                l => l.Contains("[Pipeline-Anchor]") && l.Contains("useAnchorTaxonomy:"));
        }

        [Fact]
        public void UseAnchorTaxonomy_DirectAssignThroughProperty_LogsInfo()
        {
            // What makes it fail: the public property is the only entry
            // point KSP's settings UI hits — production code never calls
            // NotifyUseAnchorTaxonomyChanged directly. The setter must
            // emit the Info line on a real change.
            var settings = new ParsekSettings();
            settings.useAnchorTaxonomy = false;

            Assert.Contains(logLines, l => l.Contains("[Pipeline-Anchor]")
                && l.IndexOf("true->false", StringComparison.OrdinalIgnoreCase) >= 0
                && l.Contains("useAnchorTaxonomy"));
        }

        [Fact]
        public void UseAnchorTaxonomy_DirectAssignSameValue_NoLog()
        {
            var settings = new ParsekSettings();
            settings.useAnchorTaxonomy = true; // already true by default

            Assert.DoesNotContain(logLines,
                l => l.Contains("[Pipeline-Anchor]") && l.Contains("useAnchorTaxonomy:"));
        }

        [Fact]
        public void UseAnchorTaxonomy_DirectAssign_PostReconciliation_PersistsToStore()
        {
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUseAnchorTaxonomy());
            ParsekSettingsPersistence.MarkReconciledForTesting();
            Assert.True(ParsekSettingsPersistence.IsReconciled);

            var settings = new ParsekSettings();
            settings.useAnchorTaxonomy = false;

            bool? stored = ParsekSettingsPersistence.GetStoredUseAnchorTaxonomy();
            Assert.True(stored.HasValue);
            Assert.False(stored.Value);
        }

        [Fact]
        public void UseAnchorTaxonomy_DirectAssign_PreReconciliation_DoesNotClobberStore()
        {
            // Mirrors the PR #328 P2-A regression check. KSP restores the
            // stale .sfs value into the property before
            // ParsekScenario.OnLoad calls ApplyTo. The reconciliation
            // gate must keep the setter from clobbering persisted user
            // intent during that window.
            ParsekSettingsPersistence.ResetForTesting();
            ParsekSettingsPersistence.SetStoredUseAnchorTaxonomyForTesting(false);
            Assert.False(ParsekSettingsPersistence.IsReconciled);

            var settings = new ParsekSettings();
            settings.useAnchorTaxonomy = false;
            bool? stored = ParsekSettingsPersistence.GetStoredUseAnchorTaxonomy();
            Assert.True(stored.HasValue);
            Assert.False(stored.Value);
        }
    }
}
