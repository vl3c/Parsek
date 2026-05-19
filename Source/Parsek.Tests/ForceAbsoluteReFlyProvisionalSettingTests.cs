using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers the pure overload of
    /// <see cref="ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(ReFlySessionMarker, string)"/>
    /// (the predicate gating the experimental
    /// <c>forceAbsoluteForReFlyProvisional</c> setting), plus the setting's
    /// default value, persistence round-trip, and notify-helper behavior.
    /// The recorder gates themselves are validated by the in-game test.
    ///
    /// <para>Pinned to <c>[Collection("Sequential")]</c> because the tests
    /// touch <see cref="ParsekLog.TestSinkForTesting"/> shared static
    /// state plus the persistence store.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ForceAbsoluteReFlyProvisionalSettingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ForceAbsoluteReFlyProvisionalSettingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekSettingsPersistence.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_NullMarker_ReturnsFalse()
        {
            bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker: null,
                activeRecordingId: "rec_prov");

            Assert.False(result);
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_MismatchActiveId_ReturnsFalse()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_other",
                SupersedeTargetId = "rec_target"
            };

            bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker,
                activeRecordingId: "rec_prov");

            Assert.False(result);
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_NullActiveId_ReturnsFalse()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov"
            };

            bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker,
                activeRecordingId: null);

            Assert.False(result);
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_EmptyActiveId_ReturnsFalse()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov"
            };

            bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker,
                activeRecordingId: string.Empty);

            Assert.False(result);
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_MatchingMarker_ReturnsTrue()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = "rec_target"
            };

            bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker,
                activeRecordingId: "rec_prov");

            Assert.True(result);
        }

        [Fact]
        public void ForceAbsoluteSetting_Default_IsFalse()
        {
            // Off-by-default so the setting only affects re-fly authoring
            // when the user opts in for A/B testing. Fresh installs and
            // existing recordings produce the same on-disk shape as before.
            var settings = new ParsekSettings();
            Assert.False(settings.forceAbsoluteForReFlyProvisional);
        }

        [Fact]
        public void ForceAbsoluteSetting_PersistenceRoundTrip()
        {
            // What makes it fail: missing the field from
            // ParsekSettingsPersistence.Save would lose the user's choice
            // on every reload.
            ParsekSettingsPersistence.SetStoredForceAbsoluteForReFlyProvisionalForTesting(true);
            Assert.True(ParsekSettingsPersistence.GetStoredForceAbsoluteForReFlyProvisional().Value);

            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredForceAbsoluteForReFlyProvisional());

            ParsekSettingsPersistence.SetStoredForceAbsoluteForReFlyProvisionalForTesting(false);
            Assert.False(ParsekSettingsPersistence.GetStoredForceAbsoluteForReFlyProvisional().Value);
        }

        [Fact]
        public void ForceAbsoluteSetting_FlipLogsInfo()
        {
            // An Anchor Info line on flip so the experiment-gate toggle
            // moment is visible in KSP.log next to recorder Anchor logs.
            ParsekSettings.NotifyForceAbsoluteForReFlyProvisionalChanged(false, true);
            Assert.Contains(logLines, l => l.Contains("[Anchor]")
                && l.IndexOf("false->true", StringComparison.OrdinalIgnoreCase) >= 0
                && l.Contains("forceAbsoluteForReFlyProvisional"));
        }

        [Fact]
        public void ForceAbsoluteSetting_NoLogWhenUnchanged()
        {
            ParsekSettings.NotifyForceAbsoluteForReFlyProvisionalChanged(true, true);
            Assert.DoesNotContain(logLines,
                l => l.Contains("[Anchor]") && l.Contains("forceAbsoluteForReFlyProvisional:"));
        }

        /// <summary>
        /// During the per-load cycle (IsReconciled==false), KSP's
        /// GameParameters.OnLoad calls the setter to restore the field from
        /// the .sfs node. That is a state restore, not a user-driven flip,
        /// so the Anchor "X->Y" Info line must NOT fire. The 2026-05-19
        /// PR 901 validation playtest exposed 14 spurious "False->True"
        /// lines from this exact path before the gate was added.
        /// </summary>
        [Fact]
        public void ForceAbsoluteSetting_SetterDuringUnreconciledLoad_DoesNotLog()
        {
            // Make sure we start unreconciled (ResetForTesting did this
            // already, but be explicit so the assertion below is meaningful).
            ParsekSettingsPersistence.InvalidateReconciliation();
            Assert.False(ParsekSettingsPersistence.IsReconciledForTesting);

            var settings = new ParsekSettings();
            settings.forceAbsoluteForReFlyProvisional = true;

            Assert.True(settings.forceAbsoluteForReFlyProvisional);
            Assert.DoesNotContain(logLines,
                l => l.Contains("[Anchor]") && l.Contains("forceAbsoluteForReFlyProvisional:"));
        }

        /// <summary>
        /// After ApplyTo flips IsReconciled true (the steady-state window
        /// where the UI toggle runs), a real user-driven flip must log
        /// normally. Pairs with
        /// <see cref="ForceAbsoluteSetting_SetterDuringUnreconciledLoad_DoesNotLog"/>
        /// to show the gate is precisely about the load-cycle window, not a
        /// blanket Notify suppression.
        /// </summary>
        [Fact]
        public void ForceAbsoluteSetting_SetterAfterReconciliation_LogsFlip()
        {
            ParsekSettingsPersistence.MarkReconciledForTesting();
            Assert.True(ParsekSettingsPersistence.IsReconciledForTesting);

            var settings = new ParsekSettings();
            settings.forceAbsoluteForReFlyProvisional = true;

            Assert.True(settings.forceAbsoluteForReFlyProvisional);
            Assert.Contains(logLines, l => l.Contains("[Anchor]")
                && l.IndexOf("false->true", StringComparison.OrdinalIgnoreCase) >= 0
                && l.Contains("forceAbsoluteForReFlyProvisional"));
        }

        // -----------------------------------------------------------------
        // Production-wrapper coverage: IsActiveRecordingReFlyProvisional(RecordingTree)
        // reads ParsekScenario.Instance + activeTree.ActiveRecordingId.
        // No recording-table lookup after the carve-out removal.
        // -----------------------------------------------------------------

        [Fact]
        public void IsActiveRecordingReFlyProvisional_Wrapper_NullScenario_ReturnsFalse()
        {
            ParsekScenario.ResetInstanceForTesting();
            try
            {
                var tree = new RecordingTree { ActiveRecordingId = "rec_prov" };
                bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(tree);
                Assert.False(result);
            }
            finally
            {
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_Wrapper_NullActiveTree_ReturnsFalse()
        {
            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    ActiveReFlyRecordingId = "rec_prov"
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(activeTree: null);
                Assert.False(result);
            }
            finally
            {
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_Wrapper_MarkerMatchesActiveId_ReturnsTrue()
        {
            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    ActiveReFlyRecordingId = "rec_prov"
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                var tree = new RecordingTree { ActiveRecordingId = "rec_prov" };
                bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(tree);
                Assert.True(result);
            }
            finally
            {
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        /// <summary>
        /// Pin the post-carve-out behavior: a re-fly provisional whose
        /// origin was a controlled-decoupled child (DebrisParentRecordingId
        /// non-null) now fires the predicate the same as a top-level
        /// re-fly. Runtime analysis (2026-05-19 Kerbal X Probe re-fly)
        /// showed that the recorder's TryResolveReFlyProvisionalAnchor
        /// bypass pins to the supersede target for parent-anchored
        /// provisionals identically to top-level ones, so the gate
        /// applies to both populations.
        /// </summary>
        [Fact]
        public void IsActiveRecordingReFlyProvisional_Wrapper_MarkerMatchesParentAnchored_ReturnsTrue()
        {
            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    ActiveReFlyRecordingId = "rec_prov_child"
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            try
            {
                var tree = new RecordingTree { ActiveRecordingId = "rec_prov_child" };
                // DebrisParentRecordingId on the recording is no longer
                // consulted by the predicate. The recording entry itself
                // doesn't even need to exist in tree.Recordings.
                tree.Recordings["rec_prov_child"] = new Recording
                {
                    RecordingId = "rec_prov_child",
                    DebrisParentRecordingId = "rec_parent"
                };

                bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(tree);
                Assert.True(result);
            }
            finally
            {
                ParsekScenario.ResetInstanceForTesting();
            }
        }
    }
}
