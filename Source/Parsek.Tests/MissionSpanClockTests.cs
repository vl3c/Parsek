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

            // phaseAnchorUT == spanStart reproduces the old absolute-phase behavior.
            bool atEnd = GhostPlaybackLogic.TryComputeSpanLoopUT(
                200, spanStart, spanStart, spanEnd, cadence, out double loopAtEnd, out long cycleAtEnd,
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
                200.0001, spanStart, spanStart, spanEnd, cadence, out double loopAfter, out long cycleAfter,
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
                103, spanStart, spanStart, spanEnd, cadence, out double loopUT, out long cycleIndex, out _);
            Assert.True(ok);
            Assert.Equal(0, cycleIndex);          // clamped to 5s cadence: still cycle 0 at +3s
            Assert.Equal(102.0, loopUT, 6);       // phase 3 clamped to span 2 => parked at spanEnd

            // Just past +5s (one clamped 5s cadence) we wrap to cycle 1 at spanStart. Querying a
            // sliver past the boundary avoids the epsilon-tolerant boundary rollback (which keeps
            // the exact boundary UT showing the prior cycle's final frame).
            bool wrapped = GhostPlaybackLogic.TryComputeSpanLoopUT(
                105.0001, spanStart, spanStart, spanEnd, cadence, out double loopWrap, out long cycleWrap, out _);
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
                50, 100, 100, 200, 100, out double loopUT, out long cycleIndex, out bool tail);
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
                150, 100, 100, 100, 100, out double loopUT, out long cycleIndex, out bool tail);
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
                150, spanStart, spanStart, spanEnd, cadence, out double loopPlay, out long cyclePlay,
                out bool tailPlay);
            Assert.True(play);
            Assert.Equal(150.0, loopPlay, 6); // loopUT advancing with the phase
            Assert.Equal(0, cyclePlay);
            Assert.False(tailPlay);

            // TAIL region: currentUT 250, phaseInCycle 150 >= 100 - parked at spanEnd, tail engaged.
            bool tailRegion = GhostPlaybackLogic.TryComputeSpanLoopUT(
                250, spanStart, spanStart, spanEnd, cadence, out double loopTail, out long cycleTail,
                out bool tailFlag);
            Assert.True(tailRegion);
            Assert.Equal(200.0, loopTail, 6); // parked at spanEnd
            Assert.Equal(0, cycleTail);
            Assert.True(tailFlag);

            // Second cycle TAIL: currentUT 550 (elapsed 450 => cycle 1, phase 150 >= 100 = tail).
            bool secondTail = GhostPlaybackLogic.TryComputeSpanLoopUT(
                550, spanStart, spanStart, spanEnd, cadence, out double loopSecond, out long cycleSecond,
                out bool tailSecond);
            Assert.True(secondTail);
            Assert.Equal(200.0, loopSecond, 6); // parked at spanEnd again
            Assert.Equal(1, cycleSecond);
            Assert.True(tailSecond);
        }

        // ─── Loiter compression (re-aim): cut remap in the uniform path ──────────

        [Fact]
        public void TryComputeSpanLoopUT_EmptyOrNullCuts_BitIdenticalToNoCompression()
        {
            // Guard 1 (review): an empty / null cut list must yield byte-identical
            // (loopUT, cycleIndex, tail) to the pre-compression clock across a UT sweep, so every
            // non-re-aim caller (faithful / overlap / TS / KSC) is provably unaffected.
            double spanStart = 100, spanEnd = 1100, cadence = 5000; // cadence > span => a tail exists
            var empty = new List<GhostPlaybackLogic.LoopCut>();
            for (double t = 100; t <= 11000; t += 37.0)
            {
                bool baseOk = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, spanStart, spanStart, spanEnd, cadence,
                    out double lBase, out long cBase, out bool tailBase); // default null cut arg
                bool nullOk = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, spanStart, spanStart, spanEnd, cadence,
                    out double lNull, out long cNull, out bool tailNull, null, null);
                bool emptyOk = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, spanStart, spanStart, spanEnd, cadence,
                    out double lEmpty, out long cEmpty, out bool tailEmpty, null, empty);
                Assert.Equal(baseOk, nullOk);
                Assert.Equal(baseOk, emptyOk);
                Assert.Equal(lBase, lNull, 9);
                Assert.Equal(lBase, lEmpty, 9);
                Assert.Equal(cBase, cNull);
                Assert.Equal(cBase, cEmpty);
                Assert.Equal(tailBase, tailNull);
                Assert.Equal(tailBase, tailEmpty);
            }
        }

        [Fact]
        public void TryComputeSpanLoopUT_WithCut_WrapsOverCompressedSpan_SkipsCut_BoundaryMatches()
        {
            // Guard 2 (review): with a cut [300,700] (400s) in span [100,1100] (1000s), the active
            // duration is the COMPRESSED span (600s). The phase wraps over that; the clamped compressed
            // phase is remapped to a recorded loopUT that SKIPS [300,700]; the boundary-rollback (spanEnd)
            // is on the SAME recorded-UT scale as the play branch at the compressed-span end.
            double spanStart = 100, spanEnd = 1100, cadence = 5000;
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 300.0, LengthSeconds = 400.0 }, // recorded [300,700]
            };

            // compressed phase 100 -> recorded 200 (before the cut).
            GhostPlaybackLogic.TryComputeSpanLoopUT(spanStart + 100, spanStart, spanStart, spanEnd, cadence,
                out double l1, out _, out bool tail1, null, cuts);
            Assert.Equal(200.0, l1, 6);
            Assert.False(tail1);

            // compressed phase 250 -> compressedLoopUT 350 -> past the cut -> recorded 750 (cut skipped).
            GhostPlaybackLogic.TryComputeSpanLoopUT(spanStart + 250, spanStart, spanStart, spanEnd, cadence,
                out double l2, out _, out bool tail2, null, cuts);
            Assert.Equal(750.0, l2, 6);
            Assert.False(tail2);

            // No remapped loopUT ever lands strictly inside the excised (300,700) across a fine sweep.
            for (double ph = 0; ph <= 600; ph += 0.5)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(spanStart + ph, spanStart, spanStart, spanEnd, cadence,
                    out double l, out _, out _, null, cuts);
                Assert.False(l > 300.0 + 1e-6 && l < 700.0 - 1e-6);
            }

            // At the compressed-span end (phase 600) the clock parks at spanEnd (1100) and the tail engages.
            GhostPlaybackLogic.TryComputeSpanLoopUT(spanStart + 600, spanStart, spanStart, spanEnd, cadence,
                out double lEnd, out _, out bool tailEnd, null, cuts);
            Assert.Equal(1100.0, lEnd, 6);
            Assert.True(tailEnd);

            // Boundary-rollback frame (currentUT == phaseAnchor + cadence) emits recorded spanEnd, on the
            // same scale as the compressed-span-end play frame above (both 1100) - the UT-scale agreement
            // the reviewer flagged to verify rather than assume.
            GhostPlaybackLogic.TryComputeSpanLoopUT(spanStart + cadence, spanStart, spanStart, spanEnd, cadence,
                out double lBoundary, out long cBoundary, out bool tailBoundary, null, cuts);
            Assert.Equal(1100.0, lBoundary, 6);
            Assert.Equal(0, cBoundary);     // rolled back to the prior cycle
            Assert.False(tailBoundary);
        }

        [Fact]
        public void DecideUnitMemberRender_MemberWindowStraddlingCut_RendersNonCutPortions_NeverInsideCut()
        {
            // Guard 3 (review): a member window [200,900] STRADDLING a cut [300,700]. The member renders
            // its non-cut portions ([200,300] and [700,900] in recorded UT) and NEVER samples a loopUT
            // inside the excised (300,700); DecideUnitMemberRender returns Render throughout the in-window
            // recorded UTs (the straddle is intended behavior, not a HiddenOutsideWindow gap).
            double spanStart = 100, spanEnd = 1100, cadence = 5000;
            double memberStart = 200, memberEnd = 900;
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 300.0, LengthSeconds = 400.0 }, // recorded [300,700]
            };
            bool renderedPreCut = false, renderedPostCut = false;
            for (double ph = 0; ph <= 600; ph += 0.5)
            {
                var decision = GhostPlaybackLogic.DecideUnitMemberRender(
                    spanStart + ph, spanStart, spanStart, spanEnd, cadence, memberStart, memberEnd,
                    out double loopUT, out _, out _, null, cuts);
                Assert.False(loopUT > 300.0 + 1e-6 && loopUT < 700.0 - 1e-6); // never inside the cut
                if (decision == GhostPlaybackLogic.UnitMemberRenderDecision.Render)
                {
                    Assert.True(loopUT >= memberStart - 1.0 && loopUT <= memberEnd + 1.0);
                    if (loopUT <= 300.0) renderedPreCut = true;
                    if (loopUT >= 700.0) renderedPostCut = true;
                }
            }
            Assert.True(renderedPreCut);  // the [200,300] pre-cut portion rendered
            Assert.True(renderedPostCut); // the [700,900] post-cut portion rendered
        }

        // ─── Phase anchor (re-enable restarts from the recording start) ─────────

        [Fact]
        public void TryComputeSpanLoopUT_AnchorEqualsCurrentUT_StartsAtSpanStart()
        {
            // Span [100, 200], cadence 100. The loop was enabled at currentUT 5000 (deep into the
            // game, long past the recorded span's absolute UT). With phaseAnchorUT == currentUT the
            // elapsed phase is 0, so loopUT lands exactly on spanStart - the recording's start. This
            // is the headline behavior: every enable starts the looped mission from the beginning.
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                5000, 5000, 100, 200, 100, out double loopUT, out long cycle, out bool tail);
            Assert.True(ok);
            Assert.Equal(100.0, loopUT, 6); // phase 0 -> spanStart
            Assert.Equal(0, cycle);
            Assert.False(tail);
        }

        [Fact]
        public void TryComputeSpanLoopUT_AnchorBeforeCurrentByK_StartsAtSpanStartPlusK()
        {
            // Same span [100, 200], cadence 100. The loop was enabled k=30s before the current UT
            // (k < cadence so we are still in cycle 0). loopUT == spanStart + k == 130. This proves
            // the phase is measured from the anchor, not the absolute span start.
            const double k = 30.0;
            double currentUT = 5000, anchorUT = currentUT - k;
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, anchorUT, 100, 200, 100, out double loopUT, out long cycle, out bool tail);
            Assert.True(ok);
            Assert.Equal(130.0, loopUT, 6); // spanStart + k
            Assert.Equal(0, cycle);
            Assert.False(tail);
        }

        [Fact]
        public void TryComputeSpanLoopUT_BeforeAnchor_ReturnsFalse()
        {
            // currentUT before the anchor (loop not yet enabled at this UT): false, parked at
            // spanStart, no negative phase. The early-return guard keys on the anchor now.
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                4990, 5000, 100, 200, 100, out double loopUT, out long cycle, out bool tail);
            Assert.False(ok);
            Assert.Equal(100.0, loopUT, 6);
            Assert.Equal(0, cycle);
            Assert.False(tail);
        }

        [Fact]
        public void DecideUnitMemberRender_AnchorEqualsCurrentUT_RendersAtSpanStart()
        {
            // Member [100,150] (the owner leg). Span [100,250], cadence 150. The loop was enabled at
            // currentUT 9000. With phaseAnchorUT == currentUT the shared clock loopUT == spanStart
            // (100), which is inside the owner member's window -> Render at 100. The looped mission
            // begins from its first leg, not wherever the absolute UT phase happened to land.
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                9000, 9000, 100, 250, 150, memberStartUT: 100, memberEndUT: 150,
                out double loopUT, out long cycle, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, d);
            Assert.Equal(100.0, loopUT, 6);
            Assert.Equal(0, cycle);
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
            // phaseAnchorUT == spanStartUT (100) reproduces the old absolute-phase behavior.
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                175, 100, 100, 250, 150, memberStartUT: 150, memberEndUT: 200,
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
                120, 100, 100, 250, 150, memberStartUT: 150, memberEndUT: 200,
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
                155, 100, 100, 200, 100, memberStartUT: 100, memberEndUT: 160, out _, out _, out _);
            var dB = GhostPlaybackLogic.DecideUnitMemberRender(
                155, 100, 100, 200, 100, memberStartUT: 150, memberEndUT: 200, out _, out _, out _);
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
                250, 100, 100, 200, 300, memberStartUT: 100, memberEndUT: 150,
                out double loopUT, out _, out bool tail);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail, dMid);
            Assert.True(tail);
            Assert.Equal(200.0, loopUT, 6); // parked at spanEnd

            // Even the member whose window includes spanEnd (150..200) is hidden in the tail.
            var dEnd = GhostPlaybackLogic.DecideUnitMemberRender(
                250, 100, 100, 200, 300, memberStartUT: 150, memberEndUT: 200, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail, dEnd);
        }

        [Fact]
        public void DecideUnitMemberRender_BeforeSpanStart_SpanClockUnresolved()
        {
            // currentUT before spanStart: the shared clock cannot resolve -> SpanClockUnresolved.
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                50, 100, 100, 250, 150, memberStartUT: 100, memberEndUT: 150, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.SpanClockUnresolved, d);
        }

        [Fact]
        public void DecideUnitMemberRender_CycleIndexAdvancesAfterWrap()
        {
            // After one full cadence the shared clock wraps to cycle 1. At currentUT 275 (175s past
            // spanStart, one 150s cadence + 25s) loopUT folds to 125 in cycle 1. A member [100,150]
            // renders (125 in its window); unitCycle == 1 is written to state.loopCycleIndex.
            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                275, 100, 100, 250, 150, memberStartUT: 100, memberEndUT: 150,
                out double loopUT, out long cycle, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, d);
            Assert.Equal(1, cycle);          // wrapped into cycle 1
            Assert.Equal(125.0, loopUT, 6);  // 25s into the span
        }

        // ─── Mission self-overlap routing (UnitMemberOverlaps / member schedule) ──

        [Fact]
        public void UnitMemberOverlaps_CadenceShorterThanSpan_True()
        {
            // span [100,200] = 100s, overlapCadence 10s: a launch every 10s -> several staggered
            // instances overlap, so flight AND the Space Center route through the overlap path.
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, 100, 200, cadenceSeconds: 100, phaseAnchorUT: 100, overlapCadenceSeconds: 10);
            Assert.True(GhostPlaybackLogic.UnitMemberOverlaps(unit));
        }

        [Fact]
        public void UnitMemberOverlaps_CadenceAtOrAboveSpan_False()
        {
            // Cadence == span (no overlap) and cadence > span (one replay at a time) both => single
            // span-clock instance.
            var atSpan = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, 100, 200, cadenceSeconds: 100, phaseAnchorUT: 100, overlapCadenceSeconds: 100);
            var aboveSpan = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, 100, 200, cadenceSeconds: 150, phaseAnchorUT: 100, overlapCadenceSeconds: 150);
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(atSpan));
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(aboveSpan));
        }

        [Fact]
        public void UnitMemberOverlaps_DegenerateSpan_False()
        {
            // A zero-length span (and the default unit) can never overlap.
            var zeroSpan = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, 100, 100, cadenceSeconds: 0, phaseAnchorUT: 100, overlapCadenceSeconds: 10);
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(zeroSpan));
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(default));
        }

        [Fact]
        public void TryComputeMissionInstanceSpanLoopUT_LiveInstance_ReturnsSpanProgress()
        {
            // anchor 100, span [0,50], cadence 10. Instance (cycle) 0 launches at 100; at UT 120 it
            // is 20s into its span, so the watch camera follows it at loopUT 20.
            bool ok = GhostPlaybackLogic.TryComputeMissionInstanceSpanLoopUT(
                100, 0, 50, 10, currentUT: 120, cycle: 0, out double loopUT);
            Assert.True(ok);
            Assert.Equal(20.0, loopUT, 6);

            // Instance 2 launches at 100 + 2*10 = 120; at UT 130 it is 10s in.
            ok = GhostPlaybackLogic.TryComputeMissionInstanceSpanLoopUT(
                100, 0, 50, 10, currentUT: 130, cycle: 2, out loopUT);
            Assert.True(ok);
            Assert.Equal(10.0, loopUT, 6);
        }

        [Fact]
        public void TryComputeMissionInstanceSpanLoopUT_EndedOrNotLaunched_ReturnsFalse()
        {
            // Instance 0 launched at 100; at UT 160 it is 60s in, past the 50s span -> ended (the
            // caller then snaps the camera to the newest in-flight instance).
            Assert.False(GhostPlaybackLogic.TryComputeMissionInstanceSpanLoopUT(
                100, 0, 50, 10, currentUT: 160, cycle: 0, out _));
            // Instance 2 launches at 120; at UT 110 it has not launched yet.
            Assert.False(GhostPlaybackLogic.TryComputeMissionInstanceSpanLoopUT(
                100, 0, 50, 10, currentUT: 110, cycle: 2, out _));
            // Degenerate span / negative cycle.
            Assert.False(GhostPlaybackLogic.TryComputeMissionInstanceSpanLoopUT(
                100, 0, 0, 10, currentUT: 120, cycle: 0, out _));
            Assert.False(GhostPlaybackLogic.TryComputeMissionInstanceSpanLoopUT(
                100, 0, 50, 10, currentUT: 120, cycle: -1, out _));
        }

        [Fact]
        public void ComputeMemberOverlapScheduleStartUT_StaggersByMemberOffsetFromAnchor()
        {
            // The owner member (memberStart == spanStart) launches at the phase anchor; a later
            // member is staggered by its offset within the span, so the whole mission relaunches
            // as one unit each overlap cadence.
            Assert.Equal(500.0,
                GhostPlaybackLogic.ComputeMemberOverlapScheduleStartUT(500, 100, 100), 6);
            Assert.Equal(530.0,
                GhostPlaybackLogic.ComputeMemberOverlapScheduleStartUT(500, 100, 130), 6);
        }

        // ─── ResolveTrackingStationSampleUT (TS span-clock parity, Phase F) ──────

        // Builds a single-unit LoopUnitSet covering committed indices {ownerIndex..} so the
        // TS effective-UT helper has a real member to resolve. Members all share the unit's
        // span clock; the per-member window is supplied separately to the helper.
        private static GhostPlaybackLogic.LoopUnitSet MakeSingleUnitSet(
            int ownerIndex, int[] memberIndices,
            double spanStartUT, double spanEndUT, double cadenceSeconds,
            double phaseAnchorUT = double.NaN)
        {
            // Default anchor == spanStartUT reproduces the old absolute-phase behavior.
            if (double.IsNaN(phaseAnchorUT))
                phaseAnchorUT = spanStartUT;
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex, memberIndices, spanStartUT, spanEndUT, cadenceSeconds, phaseAnchorUT);
            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit>
            {
                { ownerIndex, unit }
            };
            var ownerByIndex = new Dictionary<int, int>();
            foreach (int m in memberIndices)
                ownerByIndex[m] = ownerIndex;
            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_NonMember_ReturnsLiveUT_NotHidden()
        {
            // Index 9 is NOT in the unit (members 5,6,7) -> live UT passes through unchanged,
            // renderHidden=false. This is the common case for every non-looped recording.
            var units = MakeSingleUnitSet(5, new[] { 5, 6, 7 }, 100, 250, 150);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 9, memberStartUT: 100, memberEndUT: 150, liveUT: 12345.0,
                units, out bool hidden);
            Assert.Equal(12345.0, eff, 6);
            Assert.False(hidden);
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_NullSet_ReturnsLiveUT_NotHidden()
        {
            // Null set (dormant feature) -> always live UT, never hidden.
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 100, memberEndUT: 150, liveUT: 777.0,
                units: null, out bool hidden);
            Assert.Equal(777.0, eff, 6);
            Assert.False(hidden);
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_EmptySet_ReturnsLiveUT_NotHidden()
        {
            // Empty set is the inertness contract: every index returns live UT, never hidden,
            // so TS behavior is byte-identical to before when no Mission loops.
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 100, memberEndUT: 150, liveUT: 42.0,
                GhostPlaybackLogic.LoopUnitSet.Empty, out bool hidden);
            Assert.Equal(42.0, eff, 6);
            Assert.False(hidden);
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_MemberInWindow_ReturnsLoopUT_NotHidden()
        {
            // Member 5 window [150,200]. Span [100,250], cadence 150. liveUT 175 -> the shared
            // clock loopUT 175 is inside [150,200] -> Render -> return loopUT 175, not hidden.
            var units = MakeSingleUnitSet(5, new[] { 5 }, 100, 250, 150);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 150, memberEndUT: 200, liveUT: 175.0,
                units, out bool hidden);
            Assert.Equal(175.0, eff, 6);
            Assert.False(hidden);
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_MemberInWindow_WrappedCycle_ReturnsFoldedLoopUT()
        {
            // Member 5 window [100,150]. Span [100,250], cadence 150. liveUT 275 (one cadence past
            // start + 25s) folds to loopUT 125 in cycle 1, which is inside [100,150] -> Render at
            // the folded loopUT 125 (not the live 275). This is the headline span-clock substitution.
            var units = MakeSingleUnitSet(5, new[] { 5 }, 100, 250, 150);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 100, memberEndUT: 150, liveUT: 275.0,
                units, out bool hidden);
            Assert.Equal(125.0, eff, 6);
            Assert.False(hidden);
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_MemberOutsideWindow_ReportsHidden()
        {
            // Member 5 window [150,200]. liveUT 120 -> loopUT 120 is outside the member window ->
            // HiddenOutsideWindow -> renderHidden=true, returned UT is liveUT (unused by caller).
            var units = MakeSingleUnitSet(5, new[] { 5 }, 100, 250, 150);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 150, memberEndUT: 200, liveUT: 120.0,
                units, out bool hidden);
            Assert.True(hidden);
            Assert.Equal(120.0, eff, 6);
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_BeforeSpanStart_ReportsHidden()
        {
            // liveUT 50 is before the span start (100): the shared clock cannot resolve ->
            // SpanClockUnresolved -> renderHidden=true (do not render this frame).
            var units = MakeSingleUnitSet(5, new[] { 5 }, 100, 250, 150);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 100, memberEndUT: 150, liveUT: 50.0,
                units, out bool hidden);
            Assert.True(hidden);
            Assert.Equal(50.0, eff, 6);
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_AnchoredUnit_RemapsLiveUTThroughAnchor()
        {
            // Member 5 window [100,150]. Span [100,250], cadence 150. The unit was anchored at
            // liveUT 8000 (loop enabled then). At liveUT 8030 the phase is 30 (8030 - 8000), so the
            // span clock loopUT == spanStart + 30 == 130, inside [100,150] -> Render at 130, not the
            // live 8030. Without the anchor the absolute-phase clock would have folded 8030 into a
            // mid-cycle phase, landing the ghost somewhere arbitrary in the span.
            var units = MakeSingleUnitSet(5, new[] { 5 }, 100, 250, 150, phaseAnchorUT: 8000);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 100, memberEndUT: 150, liveUT: 8030.0,
                units, out bool hidden);
            Assert.False(hidden);
            Assert.Equal(130.0, eff, 6); // spanStart + (liveUT - anchor)
        }

        [Fact]
        public void ResolveTrackingStationSampleUT_AnchoredUnit_AtAnchor_RendersAtSpanStart()
        {
            // Anchored at liveUT 8000; querying exactly at the anchor gives phase 0 -> loopUT ==
            // spanStart (100), which is in member 5's window [100,150] -> Render at 100.
            var units = MakeSingleUnitSet(5, new[] { 5 }, 100, 250, 150, phaseAnchorUT: 8000);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 100, memberEndUT: 150, liveUT: 8000.0,
                units, out bool hidden);
            Assert.False(hidden);
            Assert.Equal(100.0, eff, 6);
        }

        // ─── Map-presence orbit epoch shift (loopEpochShiftSeconds = liveUT - effUT) ──
        // These guard the arithmetic the map drivers feed into the shared orbit-seed path
        // (GhostMapPresence.UpdateGhostOrbitFromStateVectors / ApplyOrbitToVessel): the seeded
        // orbit epoch + stored arc bounds are pushed forward by (liveUT - effUT) so the icon,
        // drawn at the live clock, lands on the world position recorded at effUT instead of being
        // propagated a fraction of an orbit ahead. The Unity orbit math itself is verified in-game.

        [Fact]
        public void MapPresenceEpochShift_NonMember_IsZero()
        {
            // Off the loop path effUT == liveUT, so the shift is exactly 0 and the seed path is
            // byte-identical to before (epoch == liveUT, bounds unshifted).
            var units = MakeSingleUnitSet(5, new[] { 5, 6, 7 }, 100, 250, 150);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 9, memberStartUT: 100, memberEndUT: 150, liveUT: 12345.0,
                units, out bool hidden);
            double shift = 12345.0 - eff;
            Assert.False(hidden);
            Assert.Equal(0.0, shift, 6);
        }

        [Fact]
        public void MapPresenceEpochShift_WrappedCycle_EqualsWholeCadenceOffset()
        {
            // liveUT 275 folds to effUT 125 in cycle 1 (cadence 150), so the epoch is pushed forward
            // by exactly one cadence (150). The shift is constant within a cycle, so natural orbit
            // propagation between the rate-limited reseeds matches the replay.
            var units = MakeSingleUnitSet(5, new[] { 5 }, 100, 250, 150);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 100, memberEndUT: 150, liveUT: 275.0,
                units, out bool hidden);
            double shift = 275.0 - eff;
            Assert.False(hidden);
            Assert.Equal(150.0, shift, 6);
        }

        [Fact]
        public void MapPresenceEpochShift_AnchoredUnit_EqualsAnchorMinusSpanStart()
        {
            // Anchored at liveUT 8000, span start 100. At liveUT 8030 the phase is 30 -> effUT 130,
            // so the icon epoch is pushed forward by 7900 (= liveUT - effUT) to sit at the replayed
            // pose now rather than ~7900 s ahead along the orbit.
            var units = MakeSingleUnitSet(5, new[] { 5 }, 100, 250, 150, phaseAnchorUT: 8000);
            double eff = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 5, memberStartUT: 100, memberEndUT: 150, liveUT: 8030.0,
                units, out bool hidden);
            double shift = 8030.0 - eff;
            Assert.False(hidden);
            Assert.Equal(7900.0, shift, 6);
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
                95, member.StartUT, member.StartUT, member.EndUT, member.EndUT - member.StartUT,
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
                120, member.StartUT, member.StartUT, member.EndUT, member.EndUT - member.StartUT,
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
                spanStartUT: 100, spanEndUT: 250, cadenceSeconds: 150, phaseAnchorUT: 100);

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

        // === Unit camera-live member selection (two-tier: same vessel name beats highest StartUT) ===
        // Keeps the camera on the through-line the player is watching across a structural fork where a
        // piece peels off at the same UT the parent continues (a crew EVA: continuing pod and EVA
        // kerbal share StartUT, so a plain highest-StartUT pick would tie and could grab the kerbal).

        [Fact]
        public void IsBetterUnitCameraLiveMember_FirstInWindowMemberWins()
        {
            // Caller seeds the scan with currentMatchesWatched=false and
            // currentStartUT=NegativeInfinity, so the first in-window member always replaces the seed
            // (whether or not it matches). WHAT MAKES IT FAIL: returning false would leave liveMemberIdx
            // at -1 with members in window.
            Assert.True(GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                candidateMatchesWatched: false, candidateStartUT: 0.0,
                currentMatchesWatched: false, currentStartUT: double.NegativeInfinity));
            Assert.True(GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                candidateMatchesWatched: true, candidateStartUT: 0.0,
                currentMatchesWatched: false, currentStartUT: double.NegativeInfinity));
        }

        [Fact]
        public void IsBetterUnitCameraLiveMember_SameVesselNameBeatsHigherStartNonMatch()
        {
            // The continuing pod (matches the watched vessel name, StartUT 38.88) must beat the EVA
            // kerbal that peeled off at the SAME or even a LATER StartUT but is a DIFFERENT vessel. The
            // current best here is the non-matching kerbal at the same StartUT; the matching pod must
            // still win. WHAT MAKES IT FAIL: a pure highest-StartUT pick would NOT replace an equal- or
            // higher-StartUT non-match, stranding the camera on the kerbal (the reported bug).
            Assert.True(GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                candidateMatchesWatched: true, candidateStartUT: 38.88,
                currentMatchesWatched: false, currentStartUT: 38.88));
            Assert.True(GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                candidateMatchesWatched: true, candidateStartUT: 38.88,
                currentMatchesWatched: false, currentStartUT: 100.0));
        }

        [Fact]
        public void IsBetterUnitCameraLiveMember_NonMatchNeverBeatsMatch()
        {
            // Once a same-vessel-name member is the current best, no non-matching member can replace it
            // (even with a much higher StartUT). Keeps the camera on the through-line.
            Assert.False(GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                candidateMatchesWatched: false, candidateStartUT: 100.0,
                currentMatchesWatched: true, currentStartUT: 38.88));
        }

        [Fact]
        public void IsBetterUnitCameraLiveMember_WithinTierHighestStartWins()
        {
            // Within the same tier (both match, or both non-match) the newest segment (highest StartUT)
            // wins. Unwatched playback passes matchesWatched=false for every member, so this is the
            // pure highest-StartUT pick.
            Assert.True(GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                candidateMatchesWatched: false, candidateStartUT: 50.0,
                currentMatchesWatched: false, currentStartUT: 38.88));
            Assert.False(GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                candidateMatchesWatched: false, candidateStartUT: 20.0,
                currentMatchesWatched: false, currentStartUT: 38.88));
        }

        // === Unit-handoff retarget gating (follow only a same-vessel continuation) ===

        [Fact]
        public void ResolveUnitHandoffRetargetMember_Continuation_ReturnsLiveMember()
        {
            // The chosen live member matches the watched vessel name (launch stage -> upper stage of
            // the same craft): hand the camera off to it.
            Assert.Equal(10, GhostPlaybackLogic.ResolveUnitHandoffRetargetMember(
                liveMemberIdx: 10, liveMatchesWatched: true));
        }

        [Fact]
        public void ResolveUnitHandoffRetargetMember_NonContinuation_SuppressesRetarget()
        {
            // The only in-window member is a DIFFERENT vessel (the watched craft ended; a kerbal who
            // went EVA, or a separated booster, is all that is left). Return -1 so the handoff is
            // suppressed and the watched member's own terminal end (explosion hold -> return to anchor)
            // takes over instead of the camera jumping onto the sibling at the moment of impact.
            // WHAT MAKES IT FAIL: returning the sibling index would move the watch off the ending
            // member, and HandleOverlapCameraAction would silently drop its ExplosionHoldStart.
            Assert.Equal(-1, GhostPlaybackLogic.ResolveUnitHandoffRetargetMember(
                liveMemberIdx: 11, liveMatchesWatched: false));
        }

        // === Self-healing unit-handoff retarget (deferred-transfer retry) ===
        // The host transfer can defer when the target member's ghost is still being built
        // (time-sliced respawns). ResolveUnitHandoffStoredRenderingEdge decides whether to preserve
        // the rendering edge (true => re-fire next frame) or store the real value (re-firing stops).

        [Fact]
        public void ResolveUnitHandoffStoredRenderingEdge_RetargetPending_PreservesEdge()
        {
            // The retarget fired this frame but the watch camera is still on the OLD member (#6)
            // because the target (#7) ghost has not spawned yet. The edge must stay true so the gate
            // re-fires next frame. WHAT MAKES IT FAIL: storing the real watchedIsRendering (false)
            // would let the steady-state early-return suppress the re-fire and strand the camera.
            Assert.True(GhostPlaybackLogic.ResolveUnitHandoffStoredRenderingEdge(
                retargetFired: true, watchedIndex: 6, newLiveMemberIndex: 7,
                watchedIsRendering: false));
        }

        [Fact]
        public void ResolveUnitHandoffStoredRenderingEdge_TransferLanded_StoresRealValue()
        {
            // The watch camera has landed on the live member (watchedIndex == newLiveMemberIndex):
            // the transfer succeeded, so store the real rendering value (true here) and stop
            // re-firing. WHAT MAKES IT FAIL: preserving true unconditionally would re-fire forever.
            Assert.True(GhostPlaybackLogic.ResolveUnitHandoffStoredRenderingEdge(
                retargetFired: true, watchedIndex: 7, newLiveMemberIndex: 7,
                watchedIsRendering: true));
        }

        [Fact]
        public void ResolveUnitHandoffStoredRenderingEdge_NoRetarget_StoresRealValue()
        {
            // No retarget fired this frame (steady state): store the real watchedIsRendering value
            // (true => still rendering). The pending-edge override must NOT engage.
            Assert.True(GhostPlaybackLogic.ResolveUnitHandoffStoredRenderingEdge(
                retargetFired: false, watchedIndex: 6, newLiveMemberIndex: 6,
                watchedIsRendering: true));
        }

        [Fact]
        public void ResolveUnitHandoffStoredRenderingEdge_NoRetargetNotRendering_StoresFalse()
        {
            // No retarget and the watched member is not rendering (e.g. inter-cycle tail / nothing
            // live): store false so the next rendering->hidden transition is detected cleanly.
            Assert.False(GhostPlaybackLogic.ResolveUnitHandoffStoredRenderingEdge(
                retargetFired: false, watchedIndex: 6, newLiveMemberIndex: -1,
                watchedIsRendering: false));
        }

        // --- ComputeNewestMissionInstanceSpanLoopUT (self-overlap watch handoff) ---

        [Fact]
        public void ComputeNewestMissionInstanceSpanLoopUT_TracksNewestInstancePhase()
        {
            // Span 300 [1000,1300], overlap cadence 60, anchor 1000. At UT 1250: elapsed 250,
            // missionCycle = floor(250/60) = 4; phase = 250 - 240 = 10; loopUT = 1000 + 10 = 1010.
            GhostPlaybackLogic.ComputeNewestMissionInstanceSpanLoopUT(
                phaseAnchorUT: 1000, spanStartUT: 1000, span: 300,
                overlapCadenceSeconds: 60, currentUT: 1250,
                out double loopUT, out long cycle);

            Assert.Equal(4L, cycle);
            Assert.Equal(1010.0, loopUT, 6);
            // The newest-instance loopUT lands in the FIRST member's window [1000,1100], not a later
            // member, so the camera follows the newest instance's first leg here.
            Assert.True(GhostPlaybackLogic.IsLoopUTInMemberWindow(loopUT, 1000, 1100));
            Assert.False(GhostPlaybackLogic.IsLoopUTInMemberWindow(loopUT, 1100, 1300));
        }

        [Fact]
        public void ComputeNewestMissionInstanceSpanLoopUT_PhaseClampedToSpan()
        {
            // Cadence 200 < span 300: an instance is still mid-span when the next launches, but a
            // single instance's phase past spanEnd is clamped. At UT 1990, anchor 1000: elapsed 990,
            // cycle = floor(990/200) = 4; phase = 990 - 800 = 190 (< span 300) -> loopUT 1190.
            GhostPlaybackLogic.ComputeNewestMissionInstanceSpanLoopUT(
                phaseAnchorUT: 1000, spanStartUT: 1000, span: 300,
                overlapCadenceSeconds: 200, currentUT: 1990,
                out double loopUT, out long cycle);

            Assert.Equal(4L, cycle);
            Assert.Equal(1190.0, loopUT, 6);
        }

        [Fact]
        public void ComputeNewestMissionInstanceSpanLoopUT_BeforeAnchor_ReturnsSpanStart()
        {
            GhostPlaybackLogic.ComputeNewestMissionInstanceSpanLoopUT(
                phaseAnchorUT: 1000, spanStartUT: 1000, span: 300,
                overlapCadenceSeconds: 60, currentUT: 500,
                out double loopUT, out long cycle);

            Assert.Equal(0L, cycle);
            Assert.Equal(1000.0, loopUT, 6);
        }
    }
}
