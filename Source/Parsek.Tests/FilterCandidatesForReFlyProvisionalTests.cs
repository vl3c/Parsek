using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers the pure overload of
    /// <see cref="ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(ReFlySessionMarker,string,System.Collections.Generic.ICollection{string},System.Collections.Generic.IReadOnlyList{RecordingAnchorCandidate})"/>.
    /// The narrowed-gate filter is the re-fly provisional anchor-selection
    /// default: while a re-fly session is active and the active recording is
    /// the live provisional, every candidate whose recording id is a member of
    /// the same <see cref="RecordingTree"/> as the provisional drops out of the
    /// nearest-search input. Out-of-tree candidates (real persistent vessels
    /// from other lineages, stations, bases) pass through.
    ///
    /// <para>Also covers
    /// <see cref="ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(ReFlySessionMarker, string)"/>
    /// (both overloads), the predicate the filter depends on.</para>
    ///
    /// Pinned to <c>[Collection("Sequential")]</c> because the rate-limited
    /// drop log uses <see cref="ParsekLog.TestSinkForTesting"/> shared static
    /// state.
    /// </summary>
    [Collection("Sequential")]
    public class FilterCandidatesForReFlyProvisionalTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FilterCandidatesForReFlyProvisionalTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingAnchorCandidate Cand(
            string recordingId,
            AnchorCandidateSource source = AnchorCandidateSource.Live,
            uint pid = 0u)
        {
            return new RecordingAnchorCandidate(
                recordingId,
                Vector3d.zero,
                Quaternion.identity,
                source,
                diagnosticPid: pid);
        }

        private static ReFlySessionMarker MarkerForProvisional(
            string provisionalId,
            string supersedeTargetId = "supersede-target")
        {
            return new ReFlySessionMarker
            {
                ActiveReFlyRecordingId = provisionalId,
                SupersedeTargetId = supersedeTargetId
            };
        }

        [Fact]
        public void NullCandidates_ReturnsNull()
        {
            var marker = MarkerForProvisional("rec_prov");
            var sameTree = new HashSet<string>(StringComparer.Ordinal) { "rec_prov" };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov", sameTree, candidates: null);

            Assert.Null(result);
        }

        [Fact]
        public void EmptyCandidates_PassesThroughUnchanged()
        {
            var marker = MarkerForProvisional("rec_prov");
            var sameTree = new HashSet<string>(StringComparer.Ordinal) { "rec_prov" };
            var empty = new List<RecordingAnchorCandidate>();

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov", sameTree, empty);

            Assert.Same(empty, result);
        }

        [Fact]
        public void NullMarker_PassesThroughUnchanged()
        {
            // No re-fly session active -> filter inactive, the caller's
            // existing nearest-search behavior runs against the unfiltered
            // candidate set.
            var sameTree = new HashSet<string>(StringComparer.Ordinal) { "rec_in_tree" };
            var candidates = new List<RecordingAnchorCandidate> { Cand("rec_in_tree") };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker: null,
                activeRecordingId: "rec_other",
                sameTreeRecordingIds: sameTree,
                candidates: candidates);

            Assert.Same(candidates, result);
        }

        [Fact]
        public void ActiveRecordingNotProvisional_PassesThroughUnchanged()
        {
            // Marker present but the active recording is NOT the marked
            // provisional (e.g. a docked sibling recorder ticking under the
            // same scenario). Filter inactive.
            var marker = MarkerForProvisional("rec_prov_a");
            var sameTree = new HashSet<string>(StringComparer.Ordinal) { "rec_in_tree" };
            var candidates = new List<RecordingAnchorCandidate> { Cand("rec_in_tree") };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker,
                activeRecordingId: "rec_prov_b",
                sameTreeRecordingIds: sameTree,
                candidates: candidates);

            Assert.Same(candidates, result);
        }

        [Fact]
        public void NullSameTreeIds_PassesThroughUnchanged()
        {
            // Defensive: if the tree-id set is unavailable (null), the
            // filter cannot do its job and must not silently drop every
            // candidate. Pass through.
            var marker = MarkerForProvisional("rec_prov");
            var candidates = new List<RecordingAnchorCandidate> { Cand("rec_anywhere") };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker,
                activeRecordingId: "rec_prov",
                sameTreeRecordingIds: null,
                candidates: candidates);

            Assert.Same(candidates, result);
        }

        [Fact]
        public void EmptySameTreeIds_PassesThroughUnchanged()
        {
            // Edge case: the active tree carries no recordings (shouldn't
            // happen during a real re-fly, but the filter must not iterate
            // an empty set redundantly). Same shape as null: pass through.
            var marker = MarkerForProvisional("rec_prov");
            var sameTree = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<RecordingAnchorCandidate> { Cand("rec_anywhere") };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker,
                activeRecordingId: "rec_prov",
                sameTreeRecordingIds: sameTree,
                candidates: candidates);

            Assert.Same(candidates, result);
        }

        [Fact]
        public void OnlyOutOfTreeCandidates_PassesThroughUnchanged()
        {
            // No candidate is in the same tree as the provisional, so the
            // filter has nothing to drop. The Same.Object identity matters
            // for the allocation-free fast path: if the filter rebuilt the
            // list on every call it would churn the GC.
            var marker = MarkerForProvisional("rec_prov");
            var sameTree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec_prov",
                "rec_supersede_target",
                "rec_origin_launch_lower_stage"
            };
            var candidates = new List<RecordingAnchorCandidate>
            {
                Cand("rec_station_alpha"),
                Cand("rec_base_kerbin"),
                Cand("rec_live_relay_satellite"),
            };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov", sameTree, candidates);

            Assert.Same(candidates, result);
        }

        [Fact]
        public void OnlyInTreeCandidates_FiltersToEmpty()
        {
            // Pure re-fly scenario: every nearby candidate is in the tree
            // (the provisional itself, the supersede target, the original
            // launch's lower stage). All filtered out -> nearest-search
            // gets an empty list -> no Relative anchor found -> Absolute.
            var marker = MarkerForProvisional("rec_prov");
            var sameTree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec_prov",
                "rec_supersede_target",
                "rec_origin_launch_lower_stage"
            };
            var candidates = new List<RecordingAnchorCandidate>
            {
                Cand("rec_supersede_target", AnchorCandidateSource.Ghost),
                Cand("rec_origin_launch_lower_stage", AnchorCandidateSource.Ghost),
            };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov", sameTree, candidates);

            Assert.NotNull(result);
            Assert.NotSame(candidates, result);
            Assert.Empty(result);
        }

        [Fact]
        public void MixedCandidates_KeepsOutOfTreeDropsInTree()
        {
            // Realistic mid-docking-approach re-fly: the supersede target
            // and the lower-stage ghost are in-tree (drop), the station
            // ghost is out-of-tree (keep). Closes regression #1.
            var marker = MarkerForProvisional("rec_prov");
            var sameTree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec_prov",
                "rec_supersede_target",
                "rec_lower_stage_ghost"
            };
            var candidates = new List<RecordingAnchorCandidate>
            {
                Cand("rec_supersede_target", AnchorCandidateSource.Ghost),
                Cand("rec_station_alpha", AnchorCandidateSource.Live, pid: 12345u),
                Cand("rec_lower_stage_ghost", AnchorCandidateSource.Ghost),
            };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov", sameTree, candidates);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal("rec_station_alpha", result[0].RecordingId);
        }

        [Fact]
        public void SupersedeTargetSpecifically_FilteredOut()
        {
            // The supersede target is the recording the old bypass pinned
            // to. Pin that specific drop explicitly so a future refactor
            // that, say, makes the supersede target a special always-kept
            // candidate cannot regress the experiment's validated outcome.
            var marker = MarkerForProvisional("rec_prov", supersedeTargetId: "rec_st");
            var sameTree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec_prov", "rec_st"
            };
            var candidates = new List<RecordingAnchorCandidate>
            {
                Cand("rec_st", AnchorCandidateSource.Ghost),
                Cand("rec_other_station_lineage", AnchorCandidateSource.Live),
            };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov", sameTree, candidates);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal("rec_other_station_lineage", result[0].RecordingId);
            Assert.DoesNotContain(result, c => c.RecordingId == "rec_st");
        }

        [Fact]
        public void CandidateWithEmptyRecordingId_NeverFiltered()
        {
            // The same-tree set is keyed by recording id; a candidate with a
            // null / empty id can never match. The filter must not match
            // empty-vs-empty by accident (some HashSet<string> impls allow
            // empty-string keys). Pass through unchanged.
            var marker = MarkerForProvisional("rec_prov");
            var sameTree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec_prov",
                ""  // pathological, but defensible to test
            };
            var candidates = new List<RecordingAnchorCandidate>
            {
                Cand(string.Empty, AnchorCandidateSource.Live),
                Cand(null, AnchorCandidateSource.Live),
            };

            var result = ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov", sameTree, candidates);

            Assert.NotNull(result);
            // Both candidates pass through (the filter only drops on a
            // non-empty id match).
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void DropsAreLogged_WithCountAndProvisionalId()
        {
            // Observability: when the filter drops any candidate, a
            // rate-limited Anchor log line records the count + kept count +
            // provisional id. Lets a playtest reader see whether the
            // narrowed gate is actually firing.
            var marker = MarkerForProvisional("rec_prov_to_observe");
            var sameTree = new HashSet<string>(StringComparer.Ordinal)
            {
                "rec_prov_to_observe", "rec_st"
            };
            var candidates = new List<RecordingAnchorCandidate>
            {
                Cand("rec_st", AnchorCandidateSource.Ghost),
                Cand("rec_keeper", AnchorCandidateSource.Live),
            };

            ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov_to_observe", sameTree, candidates);

            Assert.Contains(logLines, l => l.Contains("[Anchor]")
                && l.Contains("FilterCandidatesForReFlyProvisional")
                && l.Contains("dropped=1")
                && l.Contains("kept=1")
                && l.Contains("rec_prov_to_observe"));
        }

        [Fact]
        public void NoDropsAreNotLogged()
        {
            // Symmetric pin: when no candidate is dropped, the rate-limited
            // log line must not fire (otherwise every recorder tick would
            // emit a noisy "dropped=0" line).
            var marker = MarkerForProvisional("rec_prov");
            var sameTree = new HashSet<string>(StringComparer.Ordinal) { "rec_prov" };
            var candidates = new List<RecordingAnchorCandidate>
            {
                Cand("rec_far_station_a", AnchorCandidateSource.Live),
                Cand("rec_far_station_b", AnchorCandidateSource.Live),
            };

            ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                marker, "rec_prov", sameTree, candidates);

            Assert.DoesNotContain(logLines, l => l.Contains("[Anchor]")
                && l.Contains("FilterCandidatesForReFlyProvisional"));
        }

        #region IsActiveRecordingReFlyProvisional predicate

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

        // -----------------------------------------------------------------
        // Production-wrapper coverage: IsActiveRecordingReFlyProvisional(RecordingTree)
        // reads ParsekScenario.Instance + activeTree.ActiveRecordingId.
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
        /// A re-fly provisional whose origin was a controlled-decoupled child
        /// (DebrisParentRecordingId non-null) fires the predicate the same as
        /// a top-level re-fly: the predicate does not consult
        /// DebrisParentRecordingId.
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
                // DebrisParentRecordingId on the recording is not consulted by
                // the predicate. The recording entry itself doesn't even need
                // to exist in tree.Recordings.
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

        #endregion
    }
}
