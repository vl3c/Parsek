using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure span-clock math tests lifted for Mission-level looping.
    ///
    /// Covers the pure playback-decision seam in GhostPlaybackLogic: the span-clock helper
    /// (TryComputeSpanLoopUT), the per-member window check (IsLoopUTInMemberWindow), the
    /// per-member render decision (DecideUnitMemberRender), the payload-activation gate, and
    /// the watch-retarget / debris-source predicates. The chain auto-detection that builds
    /// LoopUnitSet is intentionally NOT exercised here (left behind in this lift); these tests
    /// drive the pure logic with directly constructed inputs.
    /// </summary>
    [Collection("Sequential")]
    public class MissionSpanClockTests : System.IDisposable
    {
        public MissionSpanClockTests()
        {
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            GhostPlaybackLogic.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
        }

        // ─── TryComputeSpanLoopUT (kept) ────────────────────────────────────────

        [Fact]
        public void TryComputeSpanLoopUT_WrapsSeamlessly_AtSpanEnd()
        {
            // Span [100, 200], cadence == span duration (100s, above MinCycleDuration so no
            // clamp interference). At spanEnd the clock parks at spanEnd in cycle 0; one cadence
            // step later it wraps to spanStart in cycle 1. A pause window at the wrap (the
            // standalone global-gap path) would push the wrap UT later than spanStart+cadence -
            // this asserts the seamless back-to-back wrap, so no pause was inserted.
            double spanStart = 100, spanEnd = 200, cadence = 100;

            bool atEnd = GhostPlaybackLogic.TryComputeSpanLoopUT(
                200, spanStart, spanEnd, cadence, out double loopAtEnd, out long cycleAtEnd,
                out bool tailAtEnd);
            Assert.True(atEnd);
            Assert.Equal(200.0, loopAtEnd, 6);
            Assert.Equal(0, cycleAtEnd);
            // At spanEnd with cadence == span this is the legitimate end-of-cycle final frame, NOT
            // the parked idle tail (which only exists when cadence > span).
            Assert.False(tailAtEnd);

            // Just past the cycle-0 boundary (spanStart + cadence + a sliver = 200.0001): wraps to
            // spanStart in cycle 1. A pause window would have delayed the wrap, leaving the clock
            // parked at spanEnd (loopUT == 200) at this UT instead of restarting near spanStart.
            bool afterWrap = GhostPlaybackLogic.TryComputeSpanLoopUT(
                200.0001, spanStart, spanEnd, cadence, out double loopAfter, out long cycleAfter,
                out bool tailAfterWrap);
            Assert.True(afterWrap);
            Assert.Equal(1, cycleAfter);
            Assert.Equal(100.0001, loopAfter, 4); // spanStart + tiny phase, NOT parked at spanEnd
            Assert.False(tailAfterWrap); // back in the play region of cycle 1, not a tail
        }

        [Fact]
        public void TryComputeSpanLoopUT_ClampsTinySpanToMinCycleDuration()
        {
            // Edge 14: a 2s span on a 2s cadence would, without the clamp, complete a full cycle
            // every 2s. MinCycleDuration is 5s, so the clock must still be in cycle 0 at +3s
            // (3 < 5) and the phase must equal the elapsed (3s past start clamped to the 2s span
            // => parked at spanEnd). If the helper divided by the raw 2s span, +3s would already
            // be cycle 1.
            double spanStart = 100, spanEnd = 102, cadence = 2; // raw cadence below MinCycleDuration

            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                103, spanStart, spanEnd, cadence, out double loopUT, out long cycleIndex, out _);
            Assert.True(ok);
            Assert.Equal(0, cycleIndex);          // clamped to 5s cadence: still cycle 0 at +3s
            Assert.Equal(102.0, loopUT, 6);       // phase 3 clamped to span 2 => parked at spanEnd

            // Just past +5s (one clamped 5s cadence) we wrap to cycle 1 at spanStart. Querying a
            // sliver past the boundary avoids the epsilon-tolerant boundary rollback (which keeps
            // the exact boundary UT showing the prior cycle's final frame).
            bool wrapped = GhostPlaybackLogic.TryComputeSpanLoopUT(
                105.0001, spanStart, spanEnd, cadence, out double loopWrap, out long cycleWrap, out _);
            Assert.True(wrapped);
            Assert.Equal(1, cycleWrap);
            Assert.Equal(100.0001, loopWrap, 4); // spanStart + tiny phase, not clamped to spanEnd
        }

        [Fact]
        public void TryComputeSpanLoopUT_BeforeSpanStart_ReturnsFalseOrSpanStart()
        {
            // currentUT before the span start: no negative phase. Returns false and parks
            // loopUT at spanStart (never spanStart - something).
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                50, 100, 200, 100, out double loopUT, out long cycleIndex, out bool tail);
            Assert.False(ok);
            Assert.Equal(100.0, loopUT, 6);
            Assert.Equal(0, cycleIndex);
            Assert.True(loopUT >= 100.0); // never a negative phase
            Assert.False(tail);           // early return path always reports no tail
        }

        [Fact]
        public void TryComputeSpanLoopUT_ZeroDurationSpan_ReturnsFalse()
        {
            // Degenerate span (spanEnd <= spanStart): false, parked at spanStart, no divide.
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                150, 100, 100, 100, out double loopUT, out long cycleIndex, out bool tail);
            Assert.False(ok);
            Assert.Equal(100.0, loopUT, 6);
            Assert.Equal(0, cycleIndex);
            Assert.False(tail);           // span <= 0 early return path always reports no tail
        }

        [Fact]
        public void TryComputeSpanLoopUT_CadenceGreaterThanSpan_FlagsParkedTail()
        {
            // Span [100, 200] (span = 100), cadence 300 (> span): each cycle plays for the first
            // 100s then idles parked at spanEnd for the remaining 200s before the next dispatch.
            // This is the cadence > span case: the "wait" between cycles. The flag must distinguish
            // the parked idle tail (the wait, hide ALL members) from a legitimate at-spanEnd play
            // frame, so the host can HIDE the ghost during the wait instead of freezing it.
            double spanStart = 100, spanEnd = 200, cadence = 300; // span = 100

            // PLAY region: currentUT 150, phaseInCycle 50 < 100 - ghost advancing, no tail.
            bool play = GhostPlaybackLogic.TryComputeSpanLoopUT(
                150, spanStart, spanEnd, cadence, out double loopPlay, out long cyclePlay,
                out bool tailPlay);
            Assert.True(play);
            Assert.Equal(150.0, loopPlay, 6); // loopUT advancing with the phase
            Assert.Equal(0, cyclePlay);
            Assert.False(tailPlay);

            // TAIL region: currentUT 250, phaseInCycle 150 >= 100 - parked at spanEnd, tail engaged.
            bool tailRegion = GhostPlaybackLogic.TryComputeSpanLoopUT(
                250, spanStart, spanEnd, cadence, out double loopTail, out long cycleTail,
                out bool tailFlag);
            Assert.True(tailRegion);
            Assert.Equal(200.0, loopTail, 6); // parked at spanEnd
            Assert.Equal(0, cycleTail);
            Assert.True(tailFlag);

            // Second cycle TAIL: currentUT 550 (elapsed 450 => cycle 1, phase 150 >= 100 = tail).
            bool secondTail = GhostPlaybackLogic.TryComputeSpanLoopUT(
                550, spanStart, spanEnd, cadence, out double loopSecond, out long cycleSecond,
                out bool tailSecond);
            Assert.True(secondTail);
            Assert.Equal(200.0, loopSecond, 6); // parked at spanEnd again
            Assert.Equal(1, cycleSecond);
            Assert.True(tailSecond);
        }

        // ─── IsLoopUTInMemberWindow (per-member window check) ───────────────────

        [Fact]
        public void IsLoopUTInMemberWindow_InsideAndBoundary_True_OutsideFalse()
        {
            // The shared clock drives each member independently: a member renders iff the clock is
            // in its own window (epsilon-tolerant at the boundaries).
            Assert.True(GhostPlaybackLogic.IsLoopUTInMemberWindow(125, 100, 150));
            Assert.True(GhostPlaybackLogic.IsLoopUTInMemberWindow(100, 100, 150)); // start boundary
            Assert.True(GhostPlaybackLogic.IsLoopUTInMemberWindow(150, 100, 150)); // end boundary
            Assert.False(GhostPlaybackLogic.IsLoopUTInMemberWindow(99, 100, 150)); // before
            Assert.False(GhostPlaybackLogic.IsLoopUTInMemberWindow(151, 100, 150)); // after
        }

        [Fact]
        public void IsLoopUTInMemberWindow_OverlappingMembers_BothCoverTheOverlap()
        {
            // CONCURRENT model regression: two members whose windows overlap BOTH cover a loopUT in
            // the overlap (no single-member selection). Members 0 [100,160] and 1 [150,200] overlap
            // [150,160]; at loopUT 155 BOTH return true (both render). Fails if the old
            // higher-index-wins selection logic survived (it would render only one).
            Assert.True(GhostPlaybackLogic.IsLoopUTInMemberWindow(155, 100, 160));
            Assert.True(GhostPlaybackLogic.IsLoopUTInMemberWindow(155, 150, 200));
        }

        private static TrajectoryPoint MakePoint(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0, longitude = 0, altitude = 100,
                rotation = Quaternion.identity, velocity = Vector3.zero,
                bodyName = "Kerbin",
            };
        }

        // ─── DecideUnitMemberRender (per-member, shared-clock concurrent model) ──

        [Fact]
        public void DecideUnitMemberRender_RendersWhenClockInOwnWindow()
        {
            // Member [150,200]. Span [100,250], cadence 150 (== span). At currentUT 175 the shared
            // clock loopUT == 175 is inside [150,200] -> Render at loopUT 175.
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                175, 100, 250, 150, memberStartUT: 150, memberEndUT: 200,
                out double loopUT, out long cycle, out bool tail);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, d);
            Assert.Equal(175.0, loopUT, 6);
            Assert.Equal(0, cycle);
            Assert.False(tail);
        }

        [Fact]
        public void DecideUnitMemberRender_HiddenWhenClockOutsideOwnWindow()
        {
            // Same member [150,200], but currentUT 120 -> loopUT 120 is outside [150,200] -> hidden.
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                120, 100, 250, 150, memberStartUT: 150, memberEndUT: 200,
                out double loopUT, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenOutsideWindow, d);
            Assert.Equal(120.0, loopUT, 6);
        }

        [Fact]
        public void DecideUnitMemberRender_ConcurrentMembers_BothRenderInOverlap()
        {
            // CONCURRENT model: two members whose windows overlap BOTH render when the shared clock
            // is in the overlap. Members A [100,160] and B [150,200] overlap [150,160]; at currentUT
            // 155 (loopUT 155) BOTH decide Render. This is the headline behavior change: debris
            // alongside their parent, like a rewind. Fails if a single-member selection survived.
            var dA = GhostPlaybackLogic.DecideUnitMemberRender(
                155, 100, 200, 100, memberStartUT: 100, memberEndUT: 160, out _, out _, out _);
            var dB = GhostPlaybackLogic.DecideUnitMemberRender(
                155, 100, 200, 100, memberStartUT: 150, memberEndUT: 200, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, dA);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, dB);
        }

        [Fact]
        public void DecideUnitMemberRender_InterCycleTail_HidesEveryMember()
        {
            // cadence > span: the wait between cycles. Span [100,200] (100s), cadence 300. At
            // currentUT 250 the clock is in the parked tail -> HiddenInterCycleTail for EVERY member
            // (render nothing during the wait), regardless of which member's window contains spanEnd.
            var dMid = GhostPlaybackLogic.DecideUnitMemberRender(
                250, 100, 200, 300, memberStartUT: 100, memberEndUT: 150,
                out double loopUT, out _, out bool tail);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail, dMid);
            Assert.True(tail);
            Assert.Equal(200.0, loopUT, 6); // parked at spanEnd

            // Even the member whose window includes spanEnd (150..200) is hidden in the tail.
            var dEnd = GhostPlaybackLogic.DecideUnitMemberRender(
                250, 100, 200, 300, memberStartUT: 150, memberEndUT: 200, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail, dEnd);
        }

        [Fact]
        public void DecideUnitMemberRender_BeforeSpanStart_SpanClockUnresolved()
        {
            // currentUT before spanStart: the shared clock cannot resolve -> SpanClockUnresolved.
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                50, 100, 250, 150, memberStartUT: 100, memberEndUT: 150, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.SpanClockUnresolved, d);
        }

        [Fact]
        public void DecideUnitMemberRender_CycleIndexAdvancesAfterWrap()
        {
            // After one full cadence the shared clock wraps to cycle 1. At currentUT 275 (175s past
            // spanStart, one 150s cadence + 25s) loopUT folds to 125 in cycle 1. A member [100,150]
            // renders (125 in its window); unitCycle == 1 is written to state.loopCycleIndex.
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                275, 100, 250, 150, memberStartUT: 100, memberEndUT: 150,
                out double loopUT, out long cycle, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, d);
            Assert.Equal(1, cycle);          // wrapped into cycle 1
            Assert.Equal(125.0, loopUT, 6);  // 25s into the span
        }

        // ─── Payload-activation gate (Fix 1) ────────────────────────────────────

        [Fact]
        public void UnitMember_SpanClockBelowPayloadActivation_GateHidesMember()
        {
            // A member's window uses raw StartUT (which ExplicitStartUT can widen below the first
            // playable payload sample). When the shared clock is in [StartUT, payloadStart), the
            // member's render must be gated off (the engine's spanLoopUT < activation UT check), not
            // render a stale pre-payload pose. This proves the exact arithmetic the engine gate uses.
            var member = new Recording
            {
                VesselName = "fix1-member",
                PlaybackEnabled = true,
                LoopPlayback = true,
                LoopTimeUnit = LoopTimeUnit.Auto,
                ChainId = "fix1",
                ChainIndex = 0,
                ExplicitStartUT = 90.0, // earlier than the first payload sample (100)
            };
            member.Points.Add(MakePoint(100));
            member.Points.Add(MakePoint(150));

            Assert.Equal(90.0, member.StartUT, 6);
            double activationUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(member);
            Assert.Equal(100.0, activationUT, 6);
            Assert.True(member.StartUT < activationUT);

            // At currentUT 95 (inside the widened [90,100) pre-payload region) the clock resolves
            // loopUT 95 and the member's own window [90,150] CONTAINS it -> decision Render. The
            // engine's gate (spanLoopUT < activationUT) then hides it for the frame.
            var decision = GhostPlaybackLogic.DecideUnitMemberRender(
                95, member.StartUT, member.EndUT, member.EndUT - member.StartUT,
                member.StartUT, member.EndUT, out double spanLoopUT, out _, out _);

            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, decision);
            Assert.Equal(95.0, spanLoopUT, 6);
            Assert.True(spanLoopUT < activationUT,
                "gate must fire: shared clock in the member window below its payload activation UT");
        }

        [Fact]
        public void UnitMember_ContiguousMember_GateDoesNotFire()
        {
            // Control: a typical member (no ExplicitStartUT widening) has StartUT == activation UT,
            // so any in-window spanLoopUT is >= activation UT and the gate never fires.
            var member = new Recording
            {
                VesselName = "fix1-contiguous",
                PlaybackEnabled = true,
                LoopPlayback = true,
                LoopTimeUnit = LoopTimeUnit.Auto,
                ChainId = "fix1c",
                ChainIndex = 0,
            };
            member.Points.Add(MakePoint(100));
            member.Points.Add(MakePoint(150));

            Assert.Equal(100.0, member.StartUT, 6);
            double activationUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(member);
            Assert.Equal(member.StartUT, activationUT, 6);

            var decision = GhostPlaybackLogic.DecideUnitMemberRender(
                120, member.StartUT, member.EndUT, member.EndUT - member.StartUT,
                member.StartUT, member.EndUT, out double spanLoopUT, out _, out _);

            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, decision);
            Assert.False(spanLoopUT < activationUT, "gate must NOT fire for a contiguous member");
        }

        // ─── Debris-on-unit-member branch predicate (edge 9) ────────────────────

        [Fact]
        public void ShouldSourceDebrisFromUnitSpan_EmptySetOrNegativeParent_False()
        {
            Assert.False(GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                parentIdx: 3, GhostPlaybackLogic.LoopUnitSet.Empty, out _));
            Assert.False(GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                parentIdx: -1, GhostPlaybackLogic.LoopUnitSet.Empty, out _));
        }

        // ─── Watch retarget on unit handoff (shared-clock transition) ───────────

        // A 3-member unit (committed indices 5,6,7) for the watch-transfer decision tests. The
        // indices are arbitrary (not 0,1,2) to catch an index confusion.
        private static GhostPlaybackLogic.LoopUnit ThreeMemberUnit() =>
            new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 5, memberIndices: new[] { 5, 6, 7 },
                spanStartUT: 100, spanEndUT: 250, cadenceSeconds: 150);

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_WatchedStopsRendering_NewLiveExists_True()
        {
            // The camera watches member #6 (a member of the unit). Last frame it was rendering; this
            // frame its window ended (no longer rendering) and a different live member (#7) exists ->
            // the camera must move to #7. WHAT MAKES IT FAIL: returning false would strand the camera
            // on the now-hidden member.
            var unit = ThreeMemberUnit();
            Assert.True(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 6, watchedWasRendering: true, watchedIsRendering: false,
                newLiveMemberIndex: 7, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_StillRendering_False()
        {
            // Steady state inside one segment: the watched member is still rendering -> no retarget
            // (fires once per boundary, not every frame).
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 6, watchedWasRendering: true, watchedIsRendering: true,
                newLiveMemberIndex: 6, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_WatchedIndexNotInUnit_False()
        {
            // The camera watches a recording (#99) that is NOT a member of this unit -> never fire.
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 99, watchedWasRendering: true, watchedIsRendering: false,
                newLiveMemberIndex: 7, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_NothingLive_False()
        {
            // The watched member stopped rendering but NO live member exists (inter-cycle wait / a
            // gap) -> hold the current anchor rather than yanking to nothing (-1).
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 6, watchedWasRendering: true, watchedIsRendering: false,
                newLiveMemberIndex: -1, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_WasNotRendering_False()
        {
            // The watched member was not rendering last frame either (no rendering->hidden
            // transition) -> no retarget.
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 6, watchedWasRendering: false, watchedIsRendering: false,
                newLiveMemberIndex: 7, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_NotWatching_False()
        {
            // No watch active (watchedIndex == -1): never fire.
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: -1, watchedWasRendering: true, watchedIsRendering: false,
                newLiveMemberIndex: 7, unit));
        }
    }
}
