using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the Phase 2 <c>useAnchorCorrection</c> rollout flag (design
    /// doc §18 Phase 2, §19.2 Stage 3 row "Settings flag flip"). Default,
    /// persistence round-trip, and Pipeline-Anchor log line on flip. Mirrors
    /// the Phase 1 pattern in <see cref="UseSmoothingSplinesSettingTests"/>.
    /// Touches static state (<see cref="ParsekLog.TestSinkForTesting"/> /
    /// <see cref="ParsekSettingsPersistence"/>) so runs in the
    /// <c>Sequential</c> collection.
    /// </summary>
    [Collection("Sequential")]
    public class UseAnchorCorrectionSettingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UseAnchorCorrectionSettingTests()
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
        public void UseAnchorCorrection_DefaultsTrue()
        {
            // What makes it fail: a default of false would leave Phase 2's
            // anchor pipeline gated off for fresh installs, masking
            // regressions and making the rollout invisible — every re-fly
            // would silently fall back to the pre-Phase-2 ~1-10 m offset
            // (design doc §3.2).
            var settings = new ParsekSettings();
            Assert.True(settings.useAnchorCorrection);
        }

        [Fact]
        public void UseAnchorCorrection_PersistsAcrossLoad()
        {
            // What makes it fail: if the flag is not in
            // ParsekSettingsPersistence, KSP's GameParameters reset on every
            // save load would silently revert the user's preference —
            // identical to the Phase 1 risk for useSmoothingSplines.
            ParsekSettingsPersistence.SetStoredUseAnchorCorrectionForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseAnchorCorrection().Value);

            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUseAnchorCorrection());

            // Set again, verify the round trip survives a fresh setter.
            ParsekSettingsPersistence.SetStoredUseAnchorCorrectionForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseAnchorCorrection().Value);
        }

        [Fact]
        public void UseAnchorCorrection_FlipLogsInfo()
        {
            // What makes it fail: the rollout-gate flip must produce exactly
            // one Pipeline-Anchor Info line per design doc §19.2 Stage 3
            // row. A silent flip leaves the log unable to attribute a
            // visual artifact to the moment the gate changed.
            ParsekSettings.NotifyUseAnchorCorrectionChanged(false, true);

            // bool.ToString() yields "False" / "True" — match case-insensitively
            // so the log format can use either casing without breaking the test.
            Assert.Contains(logLines, l => l.Contains("[Pipeline-Anchor]")
                && l.IndexOf("false->true", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void UseAnchorCorrection_NoLogWhenUnchanged()
        {
            // What makes it fail: emitting the line on a no-op flip would
            // spam the log on every settings-window save (the UI typically
            // calls Notify on assign regardless of whether the value
            // changed).
            ParsekSettings.NotifyUseAnchorCorrectionChanged(true, true);

            Assert.DoesNotContain(logLines,
                l => l.Contains("[Pipeline-Anchor]") && l.Contains("useAnchorCorrection:"));
        }

        [Fact]
        public void UseAnchorCorrection_DirectAssignThroughProperty_LogsInfo()
        {
            // What makes it fail: when KSP's settings UI flips the flag, the
            // public property is the only entry point — production code never
            // calls NotifyUseAnchorCorrectionChanged directly. The property
            // setter must emit the Info line on a real change so the
            // Pipeline-Anchor flip Info lands in KSP.log.
            var settings = new ParsekSettings();
            settings.useAnchorCorrection = false;

            Assert.Contains(logLines, l => l.Contains("[Pipeline-Anchor]")
                && l.IndexOf("true->false", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void UseAnchorCorrection_DirectAssignSameValue_NoLog()
        {
            // What makes it fail: emitting on every property assignment would
            // spam the log on every settings-window save. Same-value assigns
            // must be silent.
            var settings = new ParsekSettings();
            settings.useAnchorCorrection = true; // already true by default

            Assert.DoesNotContain(logLines,
                l => l.Contains("[Pipeline-Anchor]") && l.Contains("useAnchorCorrection:"));
        }
    }
}
