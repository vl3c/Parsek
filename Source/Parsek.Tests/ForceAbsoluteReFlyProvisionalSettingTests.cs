using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers the pure overload of
    /// <see cref="ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(ReFlySessionMarker, string, string)"/>
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
                activeRecordingId: "rec_prov",
                debrisParentRecordingId: null);

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
                activeRecordingId: "rec_prov",
                debrisParentRecordingId: null);

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
                activeRecordingId: null,
                debrisParentRecordingId: null);

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
                activeRecordingId: string.Empty,
                debrisParentRecordingId: null);

            Assert.False(result);
        }

        /// <summary>
        /// Parent-anchored carve-out: a re-fly provisional whose original was
        /// a controlled-decoupled child (RewindInvoker.cs:247 propagates the
        /// parent recording id) stays on the parent-anchored contract. The
        /// predicate returns false so the force-Absolute gate never fires.
        /// </summary>
        [Fact]
        public void IsActiveRecordingReFlyProvisional_ParentAnchoredCarveOut_ReturnsFalse()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov_child",
                SupersedeTargetId = "rec_target_child"
            };

            bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker,
                activeRecordingId: "rec_prov_child",
                debrisParentRecordingId: "rec_parent");

            Assert.False(result);
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_MatchingMarkerNonParentAnchored_ReturnsTrue()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov",
                SupersedeTargetId = "rec_target"
            };

            bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker,
                activeRecordingId: "rec_prov",
                debrisParentRecordingId: null);

            Assert.True(result);
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_EmptyDebrisParentId_ReturnsTrue()
        {
            var marker = new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = "rec_prov"
            };

            bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker,
                activeRecordingId: "rec_prov",
                debrisParentRecordingId: string.Empty);

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

        // -----------------------------------------------------------------
        // Production-wrapper coverage: IsActiveRecordingReFlyProvisional(RecordingTree)
        // walks ParsekScenario.Instance + activeTree to derive its inputs.
        // The docstring claims the marker-id-not-in-tree case defaults to
        // "non-parent-anchored" (predicate returns true) as the SAFE
        // failure mode for the experiment. Pin that claim.
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
        public void IsActiveRecordingReFlyProvisional_Wrapper_MarkerMatchesNonParentAnchored_ReturnsTrue()
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
                tree.Recordings["rec_prov"] = new Recording
                {
                    RecordingId = "rec_prov",
                    DebrisParentRecordingId = null
                };

                bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(tree);
                Assert.True(result);
            }
            finally
            {
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        [Fact]
        public void IsActiveRecordingReFlyProvisional_Wrapper_MarkerMatchesParentAnchored_ReturnsFalse()
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
                tree.Recordings["rec_prov_child"] = new Recording
                {
                    RecordingId = "rec_prov_child",
                    DebrisParentRecordingId = "rec_parent"
                };

                bool result = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(tree);
                Assert.False(result);
            }
            finally
            {
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        /// <summary>
        /// Safe-failure-mode pin: when the marker references an active id
        /// that isn't present in activeTree.Recordings, the wrapper falls
        /// through with debrisParentRecordingId=null and the predicate
        /// returns true (the gate would fire). Documented in the wrapper's
        /// XML doc as the intended direction: defaulting to non-parent-
        /// anchored on a transient lookup miss matches the experiment's
        /// goal of forcing Absolute on the re-fly provisional; defaulting
        /// to parent-anchored would silently revert to Relative and
        /// defeat the toggle.
        /// </summary>
        [Fact]
        public void IsActiveRecordingReFlyProvisional_Wrapper_MarkerIdNotInTree_FallsThroughToTrue()
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
                // Deliberately empty Recordings: id matches but lookup fails.
                Assert.False(tree.Recordings.ContainsKey("rec_prov"));

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
