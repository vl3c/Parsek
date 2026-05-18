using System;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the three Phase 3 playback-gate flips that admit controlled-decoupled
    /// children into the parent-anchored playback path:
    ///   1. <see cref="DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss"/>
    ///      no longer requires <c>IsDebris == true</c> - non-debris recordings with
    ///      a non-empty <c>DebrisParentRecordingId</c> are now eligible.
    ///   2. The canonical <c>GhostPlaybackEngine.TryPositionRelativeSectionAtPlaybackUT</c>
    ///      gate (verified transitively via <see cref="DebrisRelativePlaybackPolicy"/>).
    ///   3. The intermediate retirement helper
    ///      <see cref="DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage"/>
    ///      and the skip-recorded-relative-resolver predicate
    ///      <see cref="DebrisRelativePlaybackPolicy.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap"/>
    ///      inherit the widened gate.
    ///
    /// Regression fixtures keep genuine debris (<c>IsDebris == true</c>) on the
    /// same code path as before the flip - the change is strictly additive at
    /// the predicate level.
    /// </summary>
    [Collection("Sequential")]
    public class ControlledChildParentAnchoredPlaybackTests : IDisposable
    {
        public ControlledChildParentAnchoredPlaybackTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
        }

        // ===== A. ShouldRetireOnRecordedParentAnchorMiss =====

        [Fact]
        public void ShouldRetireOnRecordedParentAnchorMiss_ControlledChild_WithParentAnchor_True()
        {
            var traj = new MockTrajectory
            {
                IsDebris = false,
                DebrisParentRecordingId = "parent-1"
            };

            Assert.True(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj));
        }

        [Fact]
        public void ShouldRetireOnRecordedParentAnchorMiss_GenuineDebris_Unchanged_True()
        {
            // Regression: genuine debris keeps its existing behavior.
            var traj = new MockTrajectory
            {
                IsDebris = true,
                DebrisParentRecordingId = "parent-2"
            };

            Assert.True(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj));
        }

        [Fact]
        public void ShouldRetireOnRecordedParentAnchorMiss_NonDebrisWithoutParent_False()
        {
            // Ordinary recordings with no parent-anchor surface stay outside the policy.
            var traj = new MockTrajectory
            {
                IsDebris = false,
                DebrisParentRecordingId = null
            };

            Assert.False(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj));
        }

        [Fact]
        public void ShouldRetireOnRecordedParentAnchorMiss_DebrisWithoutParent_False()
        {
            // Edge: debris without parent-anchor (orphan / legacy) still fails the gate.
            var traj = new MockTrajectory
            {
                IsDebris = true,
                DebrisParentRecordingId = null
            };

            Assert.False(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj));
        }

        [Fact]
        public void ShouldRetireOnRecordedParentAnchorMiss_NullTrajectory_False()
        {
            Assert.False(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(null));
        }

        // ===== B. ShouldRetireOutsideAuthoredRelativeCoverage (transitive) =====

        [Fact]
        public void ShouldRetireOutsideAuthoredRelativeCoverage_ControlledChild_NoSections_RetiresWithDiagnostic()
        {
            // Controlled child enters the policy (gate widening) and a no-track-sections
            // diagnostic correctly triggers retirement - same outcome as for a genuine
            // debris recording in the same state.
            var traj = new MockTrajectory
            {
                IsDebris = false,
                DebrisParentRecordingId = "parent",
                TrackSections = null
            };

            bool result = DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(
                traj, 100.0, out var diagnostic);

            Assert.True(result);
            Assert.Equal("no-track-sections", diagnostic.Reason);
        }

        [Fact]
        public void ShouldRetireOutsideAuthoredRelativeCoverage_NonDebrisWithoutParent_NotEligible_False()
        {
            // Regression: an ordinary recording with no parent-anchor surface stays
            // outside the policy entirely (predicate returns false at the gate level
            // before the diagnostic is consulted).
            var traj = new MockTrajectory
            {
                IsDebris = false,
                DebrisParentRecordingId = null,
                TrackSections = null
            };

            bool result = DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(
                traj, 100.0, out _);

            Assert.False(result);
        }

        [Fact]
        public void ShouldRetireOutsideAuthoredRelativeCoverage_GenuineDebris_NoSections_RetiresWithDiagnostic()
        {
            // Regression: genuine debris keeps the same retirement decision as
            // before the gate flip.
            var traj = new MockTrajectory
            {
                IsDebris = true,
                DebrisParentRecordingId = "parent",
                TrackSections = null
            };

            bool result = DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(
                traj, 100.0, out var diagnostic);

            Assert.True(result);
            Assert.Equal("no-track-sections", diagnostic.Reason);
        }

        // ===== C. IsParentAnchoredFocusRecording (RelativeAnchorResolver) =====
        //
        // The resolver's predicate is private; we exercise it indirectly through
        // the in-game test framework. Here we pin the field-stamping contract
        // that the resolver consumes: a controlled-decoupled child Recording
        // surfaces a non-empty DebrisParentRecordingId.

        [Fact]
        public void ControlledChildRecording_AfterApplyDebrisAnchorContract_CarriesParentAnchor()
        {
            var parent = new Recording { RecordingId = "parent" };
            var child = new Recording { RecordingId = "controlled-child", IsDebris = false };

            Recording.ApplyDebrisAnchorContract(child, parent);

            Assert.False(child.IsDebris);
            Assert.Equal("parent", child.DebrisParentRecordingId);
        }

        // ===== D. KEEP-debris-only policy guards =====
        //
        // These tests pin the inline-commented policy decisions: controlled
        // children do NOT inherit the debris-aware sample cap (their post-window
        // Absolute tail is unbounded and would multiply storage) and do NOT
        // inherit the tail-normalize policy (their post-window Absolute tail
        // must not be truncated to the Relative section's authored coverage).

        [Fact]
        public void IsDebrisAwareSampleCapEligible_ControlledChildWithParentAnchor_False()
        {
            var rec = new Recording
            {
                RecordingId = "controlled-child",
                IsDebris = false,
                DebrisParentRecordingId = "parent"
            };

            Assert.False(BackgroundRecorder.IsDebrisAwareSampleCapEligible(rec));
        }

        [Fact]
        public void ShouldNormalizeParentAnchoredDebris_ControlledChildWithParentAnchor_False()
        {
            var rec = new Recording
            {
                RecordingId = "controlled-child",
                IsDebris = false,
                DebrisParentRecordingId = "parent"
            };

            Assert.False(DebrisRelativeRecorderPolicy.ShouldNormalizeParentAnchoredDebris(rec));
        }

        [Fact]
        public void ShouldNormalizeParentAnchoredDebris_GenuineDebrisWithParentAnchor_True()
        {
            // Regression: genuine debris stays in the tail-normalize policy.
            var rec = new Recording
            {
                RecordingId = "debris",
                IsDebris = true,
                DebrisParentRecordingId = "parent",
                LoopAnchorVesselId = 0u
            };

            Assert.True(DebrisRelativeRecorderPolicy.ShouldNormalizeParentAnchoredDebris(rec));
        }
    }
}
