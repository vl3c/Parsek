using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the <c>useGhostTrajectoryPolyline</c> rollout flag (design
    /// plan docs/dev/plans/map-trajectory-polyline.md). Default, persistence
    /// round-trip, and GhostMap log line on flip. Touches static state
    /// (<see cref="ParsekLog.TestSinkForTesting"/> /
    /// <see cref="ParsekSettingsPersistence"/>) so runs in the
    /// <c>Sequential</c> collection.
    /// </summary>
    [Collection("Sequential")]
    public class UseGhostTrajectoryPolylineSettingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UseGhostTrajectoryPolylineSettingTests()
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
        public void UseGhostTrajectoryPolyline_DefaultsFalse()
        {
            // The rollout default stays false until the polyline geometry is
            // verified in-game; a fresh ParsekSettings instance must read
            // false so first-install users keep the existing map behavior.
            var settings = new ParsekSettings();
            Assert.False(settings.useGhostTrajectoryPolyline);
        }

        [Fact]
        public void UseGhostTrajectoryPolyline_PersistsAcrossLoad()
        {
            // Without a ParsekSettingsPersistence entry the user's
            // explicit toggle would be lost on every save / load round
            // trip, because KSP's GameParameters resets it.
            ParsekSettingsPersistence.SetStoredUseGhostTrajectoryPolylineForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseGhostTrajectoryPolyline().Value);

            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredUseGhostTrajectoryPolyline());

            ParsekSettingsPersistence.SetStoredUseGhostTrajectoryPolylineForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredUseGhostTrajectoryPolyline().Value);
        }

        [Fact]
        public void UseGhostTrajectoryPolyline_FlipLogsInfo()
        {
            ParsekSettings.NotifyUseGhostTrajectoryPolylineChanged(false, true);

            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.IndexOf("useGhostTrajectoryPolyline:", StringComparison.OrdinalIgnoreCase) >= 0
                && l.IndexOf("false->true", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void UseGhostTrajectoryPolyline_NoLogWhenUnchanged()
        {
            ParsekSettings.NotifyUseGhostTrajectoryPolylineChanged(true, true);

            Assert.DoesNotContain(logLines,
                l => l.Contains("[GhostMap]") && l.Contains("useGhostTrajectoryPolyline:"));
        }

        [Fact]
        public void UseGhostTrajectoryPolyline_RecordThenApplyTo_OverridesLiveSettings()
        {
            // ApplyTo overwrites the live setting with the stored value
            // and flips IsReconciled. Store true so it differs from the
            // false default and we can observe the override.
            ParsekSettingsPersistence.SetStoredUseGhostTrajectoryPolylineForTesting(true);
            var settings = new ParsekSettings();
            Assert.False(settings.useGhostTrajectoryPolyline);

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.True(settings.useGhostTrajectoryPolyline);
            Assert.True(ParsekSettingsPersistence.IsReconciled);
        }
    }
}
