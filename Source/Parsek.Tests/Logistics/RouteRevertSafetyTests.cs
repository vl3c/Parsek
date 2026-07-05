// UNVERIFIED: not compiled/tested in this environment.
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Unit tests for the pure Rec-3 OBSERVABILITY classifier
    /// (<see cref="RouteRevertSafety.PhysicalMutationRevertable"/>). No shared
    /// static state is touched, so this is a plain class (no
    /// <c>[Collection("Sequential")]</c>).
    ///
    /// Plan: <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c> Phase 4
    /// (Rec-3, observability-only scope; the full discard-leak fix is DEFERRED).
    /// Report: <c>docs/dev/research/logistics-time-rewind-compat-report.md</c>
    /// risk #6.
    /// </summary>
    public class RouteRevertSafetyTests
    {
        // ---- The two headline cases from the plan ----

        [Fact]
        public void RewindBackedSegment_IsRevertable()
        {
            // A Rewind-to-Separation / RP-backed session has a full-world quicksave
            // revert reachable, so the physical mutation is revertable.
            Assert.True(RouteRevertSafety.PhysicalMutationRevertable(
                rewindOrRpBackedSessionActive: true,
                inFlightNonRewindSegment: false));
        }

        [Fact]
        public void NonRewindInFlightSegment_IsNotRevertable_TheLeak()
        {
            // The leak: a route physically fired inside a segment the player can
            // discard WITHOUT a quicksave, and there is no rewind backing. The
            // no-reverse-method writers have no rollback path here.
            Assert.False(RouteRevertSafety.PhysicalMutationRevertable(
                rewindOrRpBackedSessionActive: false,
                inFlightNonRewindSegment: true));
        }

        // ---- Edge cases: the rewind/RP backing is the authority ----

        [Fact]
        public void RewindBacked_WinsEvenInsideAnInFlightSegment()
        {
            // Both flags set: a reachable quicksave revert makes the mutation
            // revertable regardless of the segment it fired under (the rewind
            // restore reverts the whole world). Rewind backing dominates.
            Assert.True(RouteRevertSafety.PhysicalMutationRevertable(
                rewindOrRpBackedSessionActive: true,
                inFlightNonRewindSegment: true));
        }

        [Fact]
        public void NoBacking_NoInFlightSegment_IsRevertable_NormalPlay()
        {
            // Neither flag set: there is no pending discardable segment to leak
            // from. Routes ARE meant to mutate during normal play; the mutation is
            // part of the durable, non-discardable timeline, so it is treated as
            // revertable (nothing to discard => no leak).
            Assert.True(RouteRevertSafety.PhysicalMutationRevertable(
                rewindOrRpBackedSessionActive: false,
                inFlightNonRewindSegment: false));
        }

        // ---- Full truth table (belt-and-suspenders) ----

        [Theory]
        // rewindBacked, inFlightNonRewind  => expectedRevertable
        [InlineData(true, true, true)]    // rewind backing wins
        [InlineData(true, false, true)]   // rewind backing, durable timeline
        [InlineData(false, true, false)]  // THE LEAK: discardable, no quicksave
        [InlineData(false, false, true)]  // normal play, nothing to discard
        public void TruthTable(bool rewindBacked, bool inFlightNonRewind, bool expected)
        {
            Assert.Equal(
                expected,
                RouteRevertSafety.PhysicalMutationRevertable(rewindBacked, inFlightNonRewind));
        }
    }
}
