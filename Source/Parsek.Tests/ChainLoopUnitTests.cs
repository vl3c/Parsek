using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for chain-sequential auto looping.
    /// Phase 1: the span-clock helpers (TryComputeSpanLoopUT / TrySelectSpanMember) and the
    /// LoopUnit / LoopUnitSet descriptor types in GhostPlaybackLogic.
    /// Phase 2: the host-side detection helper RecordingStore.DetectChainLoopUnits.
    /// See docs/dev/plan-chain-sequential-auto-loop.md and the design doc.
    /// </summary>
    [Collection("Sequential")]
    public class ChainLoopUnitTests : System.IDisposable
    {
        public ChainLoopUnitTests()
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

        // ─── Phase 1: TryComputeSpanLoopUT ──────────────────────────────────────

        [Fact]
        public void TryComputeSpanLoopUT_WrapsSeamlessly_AtSpanEnd()
        {
            // Span [100, 200], cadence == span duration (100s, above MinCycleDuration so no
            // clamp interference). At spanEnd the clock parks at spanEnd in cycle 0; one cadence
            // step later it wraps to spanStart in cycle 1. A pause window at the wrap (the
            // standalone global-gap path) would push the wrap UT later than spanStart+cadence —
            // this asserts the seamless back-to-back wrap, so no pause was inserted.
            double spanStart = 100, spanEnd = 200, cadence = 100;

            bool atEnd = GhostPlaybackLogic.TryComputeSpanLoopUT(
                200, spanStart, spanEnd, cadence, out double loopAtEnd, out long cycleAtEnd);
            Assert.True(atEnd);
            Assert.Equal(200.0, loopAtEnd, 6);
            Assert.Equal(0, cycleAtEnd);

            // Just past the cycle-0 boundary (spanStart + cadence + a sliver = 200.0001): wraps to
            // spanStart in cycle 1. A pause window would have delayed the wrap, leaving the clock
            // parked at spanEnd (loopUT == 200) at this UT instead of restarting near spanStart.
            bool afterWrap = GhostPlaybackLogic.TryComputeSpanLoopUT(
                200.0001, spanStart, spanEnd, cadence, out double loopAfter, out long cycleAfter);
            Assert.True(afterWrap);
            Assert.Equal(1, cycleAfter);
            Assert.Equal(100.0001, loopAfter, 4); // spanStart + tiny phase, NOT parked at spanEnd
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
                103, spanStart, spanEnd, cadence, out double loopUT, out long cycleIndex);
            Assert.True(ok);
            Assert.Equal(0, cycleIndex);          // clamped to 5s cadence: still cycle 0 at +3s
            Assert.Equal(102.0, loopUT, 6);       // phase 3 clamped to span 2 => parked at spanEnd

            // Just past +5s (one clamped 5s cadence) we wrap to cycle 1 at spanStart. Querying a
            // sliver past the boundary avoids the epsilon-tolerant boundary rollback (which keeps
            // the exact boundary UT showing the prior cycle's final frame).
            bool wrapped = GhostPlaybackLogic.TryComputeSpanLoopUT(
                105.0001, spanStart, spanEnd, cadence, out double loopWrap, out long cycleWrap);
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
                50, 100, 200, 100, out double loopUT, out long cycleIndex);
            Assert.False(ok);
            Assert.Equal(100.0, loopUT, 6);
            Assert.Equal(0, cycleIndex);
            Assert.True(loopUT >= 100.0); // never a negative phase
        }

        [Fact]
        public void TryComputeSpanLoopUT_ZeroDurationSpan_ReturnsFalse()
        {
            // Degenerate span (spanEnd <= spanStart): false, parked at spanStart, no divide.
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                150, 100, 100, 100, out double loopUT, out long cycleIndex);
            Assert.False(ok);
            Assert.Equal(100.0, loopUT, 6);
            Assert.Equal(0, cycleIndex);
        }

        // ─── Phase 1: TrySelectSpanMember ───────────────────────────────────────

        [Fact]
        public void TrySelectSpanMember_TilesSpan_ExactlyOneMember()
        {
            // Three contiguous windows tile [100, 250]. Sample one loopUT inside each member's
            // interior; exactly that member is selected. (Boundary overlap is a separate test.)
            var windows = new List<(double, double)>
            {
                (100, 150),
                (150, 200),
                (200, 250),
            };

            Assert.True(GhostPlaybackLogic.TrySelectSpanMember(120, windows, out int s0, out bool g0));
            Assert.Equal(0, s0);
            Assert.False(g0);

            Assert.True(GhostPlaybackLogic.TrySelectSpanMember(175, windows, out int s1, out bool g1));
            Assert.Equal(1, s1);
            Assert.False(g1);

            Assert.True(GhostPlaybackLogic.TrySelectSpanMember(230, windows, out int s2, out bool g2));
            Assert.Equal(2, s2);
            Assert.False(g2);
        }

        [Fact]
        public void TrySelectSpanMember_OverlapPicksHigherIndex()
        {
            // Edge 5: members 0 and 1 overlap by 0.5s ([100,150.5] and [150,200]). A loopUT in
            // the overlap (150.25) must select member 1 (the higher ChainIndex), not member 0.
            var windows = new List<(double, double)>
            {
                (100, 150.5),
                (150, 200),
            };

            Assert.True(GhostPlaybackLogic.TrySelectSpanMember(150.25, windows, out int slot, out bool gap));
            Assert.Equal(1, slot);   // higher index wins the overlap; fails (==0) if i wins
            Assert.False(gap);
        }

        [Fact]
        public void TrySelectSpanMember_GapReturnsNoMember()
        {
            // Edge 6: a UT gap between member 0's end (150) and member 1's start (160). A loopUT
            // in the gap (155) selects no member and flags inInterMemberGap so the caller hides
            // both instead of clamping to the stale member 0.
            var windows = new List<(double, double)>
            {
                (100, 150),
                (160, 200),
            };

            Assert.False(GhostPlaybackLogic.TrySelectSpanMember(155, windows, out int slot, out bool gap));
            Assert.Equal(-1, slot);  // no stale member
            Assert.True(gap);        // recognized as an inter-member gap, not before/after span
        }

        [Fact]
        public void TrySelectSpanMember_BeforeAndAfterSpan_NoGapFlag()
        {
            // Outside the whole span: no member, but NOT an inter-member gap (so the caller does
            // not log the edge-6 gap line for the wrap tail / pre-start sliver).
            var windows = new List<(double, double)>
            {
                (100, 150),
                (150, 200),
            };

            Assert.False(GhostPlaybackLogic.TrySelectSpanMember(50, windows, out int sBefore, out bool gBefore));
            Assert.Equal(-1, sBefore);
            Assert.False(gBefore);

            Assert.False(GhostPlaybackLogic.TrySelectSpanMember(250, windows, out int sAfter, out bool gAfter));
            Assert.Equal(-1, sAfter);
            Assert.False(gAfter);
        }
    }
}
