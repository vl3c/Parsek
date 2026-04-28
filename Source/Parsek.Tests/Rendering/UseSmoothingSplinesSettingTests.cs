using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the Phase 1 <c>useSmoothingSplines</c> rollout flag (design
    /// doc §18 Phase 1, §19.2 Stage 1 row "Settings flag flip"). Default,
    /// persistence round-trip, and Pipeline-Smoothing log line on flip.
    /// Touches static state (<see cref="ParsekLog.TestSinkForTesting"/> /
    /// <see cref="ParsekSettingsPersistence"/>) so runs in the
    /// <c>Sequential</c> collection.
    /// </summary>
    [Collection("Sequential")]
    public class UseSmoothingSplinesSettingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UseSmoothingSplinesSettingTests()
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
        public void UseSmoothingSplines_DefaultsTrue()
        {
            // What makes it fail: a default of false would leave Phase 1's
            // smoothing pipeline gated off for fresh installs, masking
            // regressions and making the rollout invisible.
            var settings = new ParsekSettings();
            Assert.True(settings.useSmoothingSplines);
        }

        [Fact]
        public void UseSmoothingSplines_PersistsAcrossLoad()
        {
            // What makes it fail: if the flag is not in
            // ParsekSettingsPersistence, KSP's GameParameters reset on every
            // save load would silently revert the user's preference.
            ParsekSettingsPersistence.SetStoredUseSmoothingSplinesForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseSmoothingSplines().Value);

            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUseSmoothingSplines());

            // Set again, verify the round trip survives a fresh setter.
            ParsekSettingsPersistence.SetStoredUseSmoothingSplinesForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseSmoothingSplines().Value);
        }

        [Fact]
        public void UseSmoothingSplines_FlipLogsInfo()
        {
            // What makes it fail: the rollout-gate flip must produce exactly
            // one Pipeline-Smoothing Info line per design doc §19.2 Stage 1
            // row. A silent flip leaves the log unable to attribute a
            // visual artifact to the moment the gate changed.
            ParsekSettings.NotifyUseSmoothingSplinesChanged(false, true);

            // bool.ToString() yields "False" / "True" — match case-insensitively
            // so the log format can use either casing without breaking the test.
            Assert.Contains(logLines, l => l.Contains("[Pipeline-Smoothing]")
                && l.IndexOf("false->true", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void UseSmoothingSplines_NoLogWhenUnchanged()
        {
            // What makes it fail: emitting the line on a no-op flip would
            // spam the log on every settings-window save (the UI typically
            // calls Notify on assign regardless of whether the value
            // changed).
            ParsekSettings.NotifyUseSmoothingSplinesChanged(true, true);

            Assert.DoesNotContain(logLines,
                l => l.Contains("[Pipeline-Smoothing]") && l.Contains("useSmoothingSplines:"));
        }

        [Fact]
        public void UseSmoothingSplines_DirectAssignThroughProperty_LogsInfo()
        {
            // What makes it fail: when KSP's settings UI flips the flag, the
            // public field/property is the only entry point — production code
            // never calls NotifyUseSmoothingSplinesChanged directly. The
            // property setter must emit the Info line on a real change so
            // Pipeline-Smoothing flip Info lands in KSP.log without an
            // explicit Notify caller.
            var settings = new ParsekSettings();
            settings.useSmoothingSplines = false;

            Assert.Contains(logLines, l => l.Contains("[Pipeline-Smoothing]")
                && l.IndexOf("true->false", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void UseSmoothingSplines_DirectAssignSameValue_NoLog()
        {
            // What makes it fail: emitting on every property assignment would
            // spam the log on every settings-window save. Same-value assigns
            // must be silent.
            var settings = new ParsekSettings();
            settings.useSmoothingSplines = true; // already true by default

            Assert.DoesNotContain(logLines,
                l => l.Contains("[Pipeline-Smoothing]") && l.Contains("useSmoothingSplines:"));
        }

        [Fact]
        public void UseSmoothingSplines_DirectAssign_PersistsToStore()
        {
            // What makes it fail: the property setter logs but never calls
            // RecordUseSmoothingSplines, so a user/debug flip via
            // GameParameters or the settings UI would not persist. The
            // value would revert on the next save/rewind load when
            // ApplyTo restores from the store. This test enforces that
            // the property setter reaches the persistence layer.
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUseSmoothingSplines());

            var settings = new ParsekSettings();
            settings.useSmoothingSplines = false;

            bool? stored = ParsekSettingsPersistence.GetStoredUseSmoothingSplines();
            Assert.True(stored.HasValue);
            Assert.False(stored.Value);
        }
    }
}
