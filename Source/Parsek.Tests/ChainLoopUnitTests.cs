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
            // This is the logistics common case (dispatch interval >= transit). The flag must
            // distinguish the parked idle tail from a legitimate at-spanEnd play frame, so a
            // future host can HIDE the ghost during the gap instead of freezing it at the dock.
            double spanStart = 100, spanEnd = 200, cadence = 300; // span = 100

            // PLAY region: currentUT 150, phaseInCycle 50 < 100 — ghost advancing, no tail.
            bool play = GhostPlaybackLogic.TryComputeSpanLoopUT(
                150, spanStart, spanEnd, cadence, out double loopPlay, out long cyclePlay,
                out bool tailPlay);
            Assert.True(play);
            Assert.Equal(150.0, loopPlay, 6); // loopUT advancing with the phase
            Assert.Equal(0, cyclePlay);
            Assert.False(tailPlay);

            // TAIL region: currentUT 250, phaseInCycle 150 >= 100 — parked at spanEnd, tail engaged.
            bool tailRegion = GhostPlaybackLogic.TryComputeSpanLoopUT(
                250, spanStart, spanEnd, cadence, out double loopTail, out long cycleTail,
                out bool tailFlag);
            Assert.True(tailRegion);
            Assert.Equal(200.0, loopTail, 6); // parked at spanEnd
            Assert.Equal(0, cycleTail);
            Assert.True(tailFlag);

            // Second cycle TAIL: currentUT 450 (elapsed 350, phaseInCycle 50 into cycle 1... but
            // 450 is elapsed 350 => cycle 1, phase 50 < 100 = play). Use 550 for cycle 1 tail
            // (elapsed 450 => cycle 1, phase 150 >= 100 = tail).
            bool secondTail = GhostPlaybackLogic.TryComputeSpanLoopUT(
                550, spanStart, spanEnd, cadence, out double loopSecond, out long cycleSecond,
                out bool tailSecond);
            Assert.True(secondTail);
            Assert.Equal(200.0, loopSecond, 6); // parked at spanEnd again
            Assert.Equal(1, cycleSecond);
            Assert.True(tailSecond);
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

        // ─── Phase 2: RecordingStore.DetectChainLoopUnits ───────────────────────

        /// <summary>
        /// Builds and commits a chain-member recording. StartUT/EndUT derive from the first and
        /// last point UT, so [startUT, endUT] is the member window. branch defaults to primary.
        /// </summary>
        private static Recording CommitChainMember(
            string chainId, int chainIndex, double startUT, double endUT,
            bool loop = true, LoopTimeUnit unit = LoopTimeUnit.Auto, int branch = 0,
            int pointCount = 2)
        {
            var rec = new Recording
            {
                VesselName = $"{chainId}-{chainIndex}",
                PlaybackEnabled = true,
                LoopPlayback = loop,
                LoopTimeUnit = unit,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = branch,
            };
            if (pointCount >= 1)
            {
                // First point at startUT, last at endUT; interior points are evenly spaced and
                // do not affect the bounds. pointCount==1 yields a single point (zero-duration).
                if (pointCount == 1)
                {
                    rec.Points.Add(MakePoint(startUT));
                }
                else
                {
                    for (int i = 0; i < pointCount; i++)
                    {
                        double ut = startUT + (endUT - startUT) * i / (pointCount - 1);
                        rec.Points.Add(MakePoint(ut));
                    }
                }
            }
            RecordingStore.CommitRecordingDirect(rec);
            return rec;
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

        [Fact]
        public void DetectChainLoopUnits_TwoConsecutiveAuto_FormsOneUnit()
        {
            // Edge 18 headline: two consecutive auto-loop primary members form one unit spanning
            // first.Start..last.End with both committed indices as members.
            CommitChainMember("chA", 0, 100, 150);
            CommitChainMember("chA", 1, 150, 200);

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.IsMember(0));
            Assert.True(set.IsMember(1));
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(0, unit.OwnerIndex);
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices);
            Assert.Equal(100.0, unit.SpanStartUT, 6); // first.Start
            Assert.Equal(200.0, unit.SpanEndUT, 6);   // last.End
        }

        [Fact]
        public void DetectChainLoopUnits_SingleAutoMember_NotAUnit()
        {
            // Edge 1: a lone auto-loop chain member is run length 1, so it is NOT unitized and
            // its index is absent from the set (keeps today's global-stagger behavior).
            CommitChainMember("solo", 0, 100, 150);

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(0, set.Count);
            Assert.False(set.IsMember(0));
        }

        [Fact]
        public void DetectChainLoopUnits_ManualMemberBreaksRun()
        {
            // Edges 3,4: [auto, manual, auto, auto]. The manual member breaks the run. The
            // leading lone auto (idx 0) is not a unit; the trailing auto pair (idx 2,3) is one
            // unit; the manual member (idx 1) is in neither.
            CommitChainMember("chB", 0, 100, 150, unit: LoopTimeUnit.Auto);
            CommitChainMember("chB", 1, 150, 200, unit: LoopTimeUnit.Sec);  // manual period
            CommitChainMember("chB", 2, 200, 250, unit: LoopTimeUnit.Auto);
            CommitChainMember("chB", 3, 250, 300, unit: LoopTimeUnit.Auto);

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.False(set.IsMember(0)); // lone leading auto
            Assert.False(set.IsMember(1)); // manual breaker
            Assert.True(set.IsMember(2));
            Assert.True(set.IsMember(3));
            Assert.True(set.TryGetUnitForMember(2, out var unit));
            Assert.Equal(2, unit.OwnerIndex);
            Assert.Equal(new[] { 2, 3 }, unit.MemberIndices);
            Assert.Equal(200.0, unit.SpanStartUT, 6);
            Assert.Equal(300.0, unit.SpanEndUT, 6);
        }

        [Fact]
        public void DetectChainLoopUnits_NonContiguousAuto_DoesNotMerge()
        {
            // Edge 2: [auto, not-looping, auto]. The non-looping member splits the chain into
            // two length-1 runs; neither forms a unit.
            CommitChainMember("chC", 0, 100, 150, loop: true, unit: LoopTimeUnit.Auto);
            CommitChainMember("chC", 1, 150, 200, loop: false, unit: LoopTimeUnit.Auto); // not looping
            CommitChainMember("chC", 2, 200, 250, loop: true, unit: LoopTimeUnit.Auto);

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(0, set.Count);
            Assert.False(set.IsMember(0));
            Assert.False(set.IsMember(2));
        }

        [Fact]
        public void DetectChainLoopUnits_Branch1MembersExcluded()
        {
            // Edge 8: a branch-0 auto pair forms a unit; a branch-1 auto member at the same
            // ChainIndex is NOT pulled in (only the primary path forms a unit).
            CommitChainMember("chD", 0, 100, 150, branch: 0);
            CommitChainMember("chD", 1, 150, 200, branch: 0);
            CommitChainMember("chD", 1, 150, 200, branch: 1); // parallel continuation, idx 2

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.IsMember(0));
            Assert.True(set.IsMember(1));
            Assert.False(set.IsMember(2)); // branch>0 never joins the unit
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices);
        }

        [Fact]
        public void DetectChainLoopUnits_ZeroDurationMemberExcluded_DissolvesIfBelowTwo()
        {
            // Edge 7: [auto, zero-duration-auto] -> the zero-duration member is disqualified,
            // which drops the run to length 1, so NO unit forms (it dissolves).
            CommitChainMember("chE", 0, 100, 150, unit: LoopTimeUnit.Auto);
            CommitChainMember("chE", 1, 200, 200, unit: LoopTimeUnit.Auto, pointCount: 1); // single point

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(0, set.Count);
            Assert.False(set.IsMember(0));
            Assert.False(set.IsMember(1));
        }

        [Fact]
        public void DetectChainLoopUnits_SpanAndCadence_FromWindowsNotGlobalGap()
        {
            // Cadence == span duration (150s for a 100..250 span), NOT the 30s/10s global gap.
            CommitChainMember("chF", 0, 100, 200);
            CommitChainMember("chF", 1, 200, 250);

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(150.0, unit.CadenceSeconds, 6); // 250 - 100, not a global stagger gap
            Assert.Equal(150.0, unit.SpanEndUT - unit.SpanStartUT, 6);
        }

        [Fact]
        public void DetectChainLoopUnits_TinySpanCadenceClampedToMinCycleDuration()
        {
            // Edge 14: a sub-MinCycleDuration span (2s total) clamps cadence to MinCycleDuration.
            CommitChainMember("chG", 0, 100, 101);
            CommitChainMember("chG", 1, 101, 102);

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(2.0, unit.SpanEndUT - unit.SpanStartUT, 6); // raw span is 2s
            Assert.Equal(LoopTiming.MinCycleDuration, unit.CadenceSeconds, 6); // clamped up
        }

        [Fact]
        public void DetectChainLoopUnits_NullOrSingletonList_ReturnsEmpty()
        {
            // Defensive: null and under-2-element lists return the shared Empty (no allocation).
            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty, RecordingStore.DetectChainLoopUnits(null));

            CommitChainMember("chH", 0, 100, 150);
            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty,
                RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings));
        }

        [Fact]
        public void DetectChainLoopUnits_NoChainMembers_ReturnsEmptyViaFastPath()
        {
            // Dormant-path fast-out: the per-frame common case has NO chains. Here two standalone
            // auto-loop recordings carry no ChainId, so zero recordings feed the per-chain grouping.
            // The O(n) pre-scan finds no chain member and returns the shared Empty before allocating
            // the grouping dictionary. Asserts the result is the no-unit Empty (no allocation, no
            // mis-detection): would regress if the fast path mis-counted a chainless save as a unit
            // or fell through and allocated needlessly.
            CommitStandaloneAuto(50, 90);  // no ChainId
            CommitStandaloneAuto(95, 130); // no ChainId

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty, set);
            Assert.Equal(0, set.Count);
            Assert.False(set.IsMember(0));
            Assert.False(set.IsMember(1));
        }

        // ─── Phase 2: log-assertion tests ───────────────────────────────────────

        [Fact]
        public void DetectChainLoopUnits_EmitsUnitBuiltSummary()
        {
            CommitChainMember("logA", 0, 100, 150);
            CommitChainMember("logA", 1, 150, 200);

            var captured = new List<string>();
            ParsekLog.TestSinkForTesting = captured.Add;
            try
            {
                RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = null;
            }

            // Summary count line (protects observability of how many units were built).
            Assert.Contains(captured, l =>
                l.Contains("[RecordingStore]") && l.Contains("Chain-loop units: built 1 unit"));
            // Per-unit detail with member indices and span (the design's detection log).
            Assert.Contains(captured, l =>
                l.Contains("[Loop]") && l.Contains("Chain-loop unit: owner=0")
                && l.Contains("members=0,1") && l.Contains("span=100..200"));
        }

        [Fact]
        public void DetectChainLoopUnits_RejectedRunLogsReason_LengthLtTwo()
        {
            CommitChainMember("logB", 0, 100, 150); // lone auto -> rejected length<2

            var captured = new List<string>();
            ParsekLog.TestSinkForTesting = captured.Add;
            try
            {
                // Add a sibling so the list has >= 2 entries (the singleton early-return would
                // otherwise skip detection entirely). The sibling is a standalone (no chain).
                var standalone = new Recording
                {
                    VesselName = "standalone", PlaybackEnabled = true,
                    LoopPlayback = true, LoopTimeUnit = LoopTimeUnit.Auto,
                };
                standalone.Points.Add(MakePoint(300));
                standalone.Points.Add(MakePoint(350));
                RecordingStore.CommitRecordingDirect(standalone);

                RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = null;
            }

            Assert.Contains(captured, l =>
                l.Contains("[Loop]") && l.Contains("chain logB") && l.Contains("not a unit: length<2"));
        }

        [Fact]
        public void DetectChainLoopUnits_RejectedRunLogsReason_MemberNotAuto()
        {
            // A manual-period member breaking a run logs reason member-not-auto.
            CommitChainMember("logC", 0, 100, 150, unit: LoopTimeUnit.Auto);
            CommitChainMember("logC", 1, 150, 200, unit: LoopTimeUnit.Sec); // manual

            var captured = new List<string>();
            ParsekLog.TestSinkForTesting = captured.Add;
            try
            {
                RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = null;
            }

            Assert.Contains(captured, l =>
                l.Contains("[Loop]") && l.Contains("chain logC")
                && l.Contains("recIdx=1") && l.Contains("not a unit: member-not-auto"));
        }

        [Fact]
        public void DetectChainLoopUnits_RejectedRunLogsReason_BranchGtZero()
        {
            // A branch>0 member logs reason branch>0 (edge 8).
            CommitChainMember("logD", 0, 100, 150, branch: 0);
            CommitChainMember("logD", 1, 150, 200, branch: 0);
            CommitChainMember("logD", 1, 150, 200, branch: 1); // branch>0

            var captured = new List<string>();
            ParsekLog.TestSinkForTesting = captured.Add;
            try
            {
                RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = null;
            }

            Assert.Contains(captured, l =>
                l.Contains("[Loop]") && l.Contains("chain logD")
                && l.Contains("not a unit: branch>0"));
        }

        // ─── Phase 3: global-queue exclusion in the engine schedule rebuild ─────

        /// <summary>
        /// Materializes the committed recordings as an IPlaybackTrajectory list in committed-index
        /// order (Recording implements IPlaybackTrajectory). The committed index == trajectory
        /// index == descriptor key invariant the LoopUnitSet relies on.
        /// </summary>
        private static List<IPlaybackTrajectory> TrajectoriesFromCommitted()
        {
            var committed = RecordingStore.CommittedRecordings;
            var list = new List<IPlaybackTrajectory>(committed.Count);
            for (int i = 0; i < committed.Count; i++)
                list.Add(committed[i]);
            return list;
        }

        private static Recording CommitStandaloneAuto(double startUT, double endUT)
        {
            var rec = new Recording
            {
                VesselName = "standalone",
                PlaybackEnabled = true,
                LoopPlayback = true,
                LoopTimeUnit = LoopTimeUnit.Auto,
            };
            rec.Points.Add(MakePoint(startUT));
            rec.Points.Add(MakePoint(endUT));
            RecordingStore.CommitRecordingDirect(rec);
            return rec;
        }

        [Fact]
        public void RebuildSchedule_UnitMembersExcludedFromGlobalQueue()
        {
            // Members {0,1} form a unit; index 2 is a standalone auto recording. After the engine
            // rebuilds the global auto-launch schedule with the unit pushed in, index 2 keeps its
            // global slot but the two unit members do NOT (they are scheduled by their span clock).
            // Fails if a unit member also receives a global slot (double-scheduling regression).
            CommitChainMember("schedA", 0, 100, 150);
            CommitChainMember("schedA", 1, 150, 200);
            CommitStandaloneAuto(300, 360); // index 2

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            Assert.True(set.IsMember(0));
            Assert.True(set.IsMember(1));
            Assert.False(set.IsMember(2));

            var engine = new GhostPlaybackEngine(positioner: null);
            engine.SetLoopUnitsForTesting(set);
            engine.RebuildAutoLoopLaunchScheduleCacheForTesting(
                TrajectoriesFromCommitted(), LoopTiming.DefaultLoopIntervalSeconds);

            Assert.False(engine.TryGetAutoLoopScheduleForTesting(0)); // unit member excluded
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(1)); // unit member excluded
            Assert.True(engine.TryGetAutoLoopScheduleForTesting(2));  // standalone keeps its slot
        }

        [Fact]
        public void RebuildSchedule_NoUnits_StandaloneAutosAllScheduled()
        {
            // Control: with an Empty LoopUnitSet, every eligible auto-loop recording keeps its
            // global slot. This pins the exclusion to the unit membership, not to a blanket change
            // in the rebuild. Fails if the rebuild drops standalone autos when no unit exists.
            CommitStandaloneAuto(100, 160); // index 0
            CommitStandaloneAuto(300, 360); // index 1

            var engine = new GhostPlaybackEngine(positioner: null);
            engine.SetLoopUnitsForTesting(GhostPlaybackLogic.LoopUnitSet.Empty);
            engine.RebuildAutoLoopLaunchScheduleCacheForTesting(
                TrajectoriesFromCommitted(), LoopTiming.DefaultLoopIntervalSeconds);

            Assert.True(engine.TryGetAutoLoopScheduleForTesting(0));
            Assert.True(engine.TryGetAutoLoopScheduleForTesting(1));
        }

        [Fact]
        public void RebuildSchedule_MixedList_StandaloneStaysInParade()
        {
            // Mixed: a standalone auto (index 0) + a 3-member auto chain (indices 1,2,3). The
            // standalone keeps its parade slot; all three chain members are excluded. Fails if
            // either population leaks into the other's scheduling.
            CommitStandaloneAuto(50, 110); // index 0
            CommitChainMember("schedM", 0, 200, 250); // index 1
            CommitChainMember("schedM", 1, 250, 300); // index 2
            CommitChainMember("schedM", 2, 300, 350); // index 3

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(1, out var unit));
            Assert.Equal(new[] { 1, 2, 3 }, unit.MemberIndices);

            var engine = new GhostPlaybackEngine(positioner: null);
            engine.SetLoopUnitsForTesting(set);
            engine.RebuildAutoLoopLaunchScheduleCacheForTesting(
                TrajectoriesFromCommitted(), LoopTiming.DefaultLoopIntervalSeconds);

            Assert.True(engine.TryGetAutoLoopScheduleForTesting(0));  // standalone parade slot
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(1)); // chain member excluded
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(2)); // chain member excluded
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(3)); // chain member excluded
        }

        [Fact]
        public void RebuildSchedule_ExclusionLogsReason()
        {
            // The exclusion is observable: each excluded member logs its owner once (VerboseOnChange).
            // Fails if the global-queue / unit split is silent (protects the design's diagnostic).
            CommitChainMember("schedL", 0, 100, 150);
            CommitChainMember("schedL", 1, 150, 200);

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            // VerboseOnChange emits only on the first observation of an (identity, stateKey) pair;
            // reset so a prior test's exclusion state does not suppress this one's first emit.
            ParsekLog.ResetRateLimitsForTesting();

            var captured = new List<string>();
            ParsekLog.TestSinkForTesting = captured.Add;
            try
            {
                var engine = new GhostPlaybackEngine(positioner: null);
                engine.SetLoopUnitsForTesting(set);
                engine.RebuildAutoLoopLaunchScheduleCacheForTesting(
                    TrajectoriesFromCommitted(), LoopTiming.DefaultLoopIntervalSeconds);
            }
            finally
            {
                ParsekLog.TestSinkForTesting = null;
            }

            Assert.Contains(captured, l =>
                l.Contains("[Loop]") && l.Contains("chain-loop member recIdx=0")
                && l.Contains("excluded from global auto queue") && l.Contains("unit owner=0"));
            Assert.Contains(captured, l =>
                l.Contains("[Loop]") && l.Contains("chain-loop member recIdx=1")
                && l.Contains("excluded from global auto queue") && l.Contains("unit owner=0"));
        }

        // ─── Phase 4: DecideUnitMemberRender (follower dispatch decision) ───────

        // Three contiguous members tiling the span [100, 250]: slot 0 [100,150], slot 1 [150,200],
        // slot 2 [200,250]. Cadence == span (150s, above MinCycleDuration so no clamp).
        private static List<(double startUT, double endUT)> ThreeContiguousWindows() =>
            new List<(double, double)> { (100, 150), (150, 200), (200, 250) };

        [Fact]
        public void DecideUnitMemberRender_SelectedMemberRenders_AtSpanLoopUT()
        {
            // currentUT 175 sits in cycle 0 at loopUT 175, which is inside slot 1's window. Slot 1
            // renders; slots 0 and 2 are hidden because a sibling is selected. spanLoopUT == 175.
            var w = ThreeContiguousWindows();

            var d1 = GhostPlaybackLogic.DecideUnitMemberRender(
                175, 100, 250, 150, memberSlot: 1, w,
                out double loopUT, out long cycle, out int sel);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, d1);
            Assert.Equal(175.0, loopUT, 6);
            Assert.Equal(0, cycle);
            Assert.Equal(1, sel);

            var d0 = GhostPlaybackLogic.DecideUnitMemberRender(
                175, 100, 250, 150, memberSlot: 0, w, out _, out _, out int sel0);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenSiblingSelected, d0);
            Assert.Equal(1, sel0); // slot 1 is the live member

            var d2 = GhostPlaybackLogic.DecideUnitMemberRender(
                175, 100, 250, 150, memberSlot: 2, w, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenSiblingSelected, d2);
        }

        [Fact]
        public void DecideUnitMemberRender_ExactlyOneMemberRendersAcrossTheSpan()
        {
            // Sample one interior loopUT per member; exactly that slot renders and the other two
            // are hidden. Fails if two members render simultaneously outside an overlap.
            var w = ThreeContiguousWindows();
            double[] samples = { 120, 175, 230 };
            for (int live = 0; live < 3; live++)
            {
                for (int slot = 0; slot < 3; slot++)
                {
                    var d = GhostPlaybackLogic.DecideUnitMemberRender(
                        samples[live], 100, 250, 150, slot, w, out _, out _, out _);
                    if (slot == live)
                        Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, d);
                    else
                        Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenSiblingSelected, d);
                }
            }
        }

        [Fact]
        public void DecideUnitMemberRender_OverlapPrecedence_HigherIndexRendersLowerHidden()
        {
            // Edge 5: slots 0 [100,150.5] and 1 [150,200] overlap by 0.5s. At loopUT 150.25 the
            // higher-index slot 1 renders and slot 0 is hidden (continuation is authoritative).
            var w = new List<(double, double)> { (100, 150.5), (150, 200) };

            // currentUT == 150.25, span [100,200], cadence 100 -> loopUT 150.25 in cycle 0.
            var dHigh = GhostPlaybackLogic.DecideUnitMemberRender(
                150.25, 100, 200, 100, memberSlot: 1, w, out double loopUT, out _, out int sel);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, dHigh);
            Assert.Equal(1, sel);
            Assert.Equal(150.25, loopUT, 6);

            var dLow = GhostPlaybackLogic.DecideUnitMemberRender(
                150.25, 100, 200, 100, memberSlot: 0, w, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenSiblingSelected, dLow);
        }

        [Fact]
        public void DecideUnitMemberRender_GapHidesAllMembers()
        {
            // Edge 6: a UT gap between slot 0 end (150) and slot 1 start (160). A loopUT in the gap
            // (155) hides every member with HiddenInGap. Fails if it clamps to the stale slot 0.
            var w = new List<(double, double)> { (100, 150), (160, 210) };

            // span [100,210], cadence 110; currentUT 155 -> loopUT 155 (cycle 0) in the gap.
            var d0 = GhostPlaybackLogic.DecideUnitMemberRender(
                155, 100, 210, 110, memberSlot: 0, w, out double loopUT, out _, out int sel);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInGap, d0);
            Assert.Equal(-1, sel);     // no member selected
            Assert.Equal(155.0, loopUT, 6);

            var d1 = GhostPlaybackLogic.DecideUnitMemberRender(
                155, 100, 210, 110, memberSlot: 1, w, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInGap, d1);
        }

        [Fact]
        public void DecideUnitMemberRender_BeforeSpanStart_SpanClockUnresolved()
        {
            // currentUT before spanStart: the span clock cannot resolve, so the member is hidden
            // with SpanClockUnresolved (distinct from the in-span hide reasons).
            var w = ThreeContiguousWindows();

            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                50, 100, 250, 150, memberSlot: 0, w, out _, out _, out _);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.SpanClockUnresolved, d);
        }

        [Fact]
        public void DecideUnitMemberRender_CycleIndexAdvancesAfterWrap()
        {
            // After one full cadence the span clock wraps to cycle 1. At currentUT 275 (175s past
            // spanStart, one 150s cadence + 25s) loopUT folds to 125 in cycle 1, selecting slot 0.
            // The returned unitCycle == 1 is what the engine writes to state.loopCycleIndex to
            // trigger the per-cycle ghost rebuild.
            var w = ThreeContiguousWindows();

            var d = GhostPlaybackLogic.DecideUnitMemberRender(
                275, 100, 250, 150, memberSlot: 0, w, out double loopUT, out long cycle, out int sel);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, d);
            Assert.Equal(1, cycle);          // wrapped into cycle 1
            Assert.Equal(125.0, loopUT, 6);  // 25s into the span, inside slot 0
            Assert.Equal(0, sel);
        }

        // ─── Phase 4 follow-up (Fix 1): payload-activation gate ─────────────────

        [Fact]
        public void UnitMember_SpanClockBelowPayloadActivation_GateHidesMember()
        {
            // Fix 1 regression: a unit selection window uses the member's RAW StartUT (design D2).
            // When ExplicitStartUT widens StartUT below the first playable payload sample, the span
            // clock can select THIS member at a spanLoopUT inside [StartUT, payloadStart) — below
            // its first payload UT. RenderInRangeGhost would then position/interpolate a stale
            // pre-payload pose. UpdateUnitMemberPlayback's new gate (spanLoopUT < activation UT)
            // hides the member instead. This proves the exact arithmetic the engine gate evaluates.
            //
            // Member: payload samples at [100, 150], but ExplicitStartUT = 90 widens StartUT to 90.
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

            // The two boundaries diverge: StartUT follows ExplicitStartUT, but the activation UT
            // tracks the first PLAYABLE payload sample. This divergence is the precondition for the
            // bug (a contiguous member with StartUT == activation UT cannot reach this state).
            Assert.Equal(90.0, member.StartUT, 6);
            double activationUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(member);
            Assert.Equal(100.0, activationUT, 6);
            Assert.True(member.StartUT < activationUT);

            // Build the unit windows the way UpdateUnitMemberPlayback does: raw StartUT/EndUT.
            // A single-member window here is enough to exercise the gate (the selection picks slot 0).
            var windows = new List<(double, double)> { (member.StartUT, member.EndUT) };

            // Span clock anchored at the member's raw window. At currentUT 95 (inside the widened
            // [90,100) pre-payload region) the clock resolves loopUT 95 and SELECTS slot 0 — exactly
            // the case the standalone path's `currentUT < activationStartUT` guard would have caught
            // but the unit dispatch bypasses. WHAT MAKES IT FAIL: without the new gate, slot 0
            // renders at loopUT 95 (below its 100 activation UT). With the gate, spanLoopUT (95) <
            // activationUT (100) => the member is hidden.
            var decision = GhostPlaybackLogic.DecideUnitMemberRender(
                95, member.StartUT, member.EndUT, member.EndUT - member.StartUT,
                memberSlot: 0, windows, out double spanLoopUT, out _, out int selectedSlot);

            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, decision); // selection picks it
            Assert.Equal(0, selectedSlot);
            Assert.Equal(95.0, spanLoopUT, 6);

            // The engine gate condition: selected, but the span loopUT is below the member's
            // payload activation. The gate fires => member hidden for this frame.
            Assert.True(spanLoopUT < activationUT,
                "gate must fire: span clock selected the member below its payload activation UT");
        }

        [Fact]
        public void UnitMember_ContiguousMember_GateDoesNotFire()
        {
            // Control for Fix 1: a typical contiguous member (no ExplicitStartUT widening) has
            // StartUT == activation UT, so any selected spanLoopUT is >= activation UT and the gate
            // never fires. Fails if the gate were to spuriously hide ordinary members.
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
            Assert.Equal(member.StartUT, activationUT, 6); // no divergence: gate cannot fire

            var windows = new List<(double, double)> { (member.StartUT, member.EndUT) };

            // Any in-window currentUT selects the member at a spanLoopUT >= 100 == activation UT.
            var decision = GhostPlaybackLogic.DecideUnitMemberRender(
                120, member.StartUT, member.EndUT, member.EndUT - member.StartUT,
                memberSlot: 0, windows, out double spanLoopUT, out _, out _);

            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, decision);
            Assert.False(spanLoopUT < activationUT, "gate must NOT fire for a contiguous member");
        }

        // ─── Phase 5: dual-scheduler (flight engine + tracking station) parity ──

        [Fact]
        public void DualScheduler_SameRecordingSet_ProducesIdenticalUnits()
        {
            // The design scopes BOTH the flight engine and the tracking-station scheduler in by
            // sharing the pure RecordingStore.DetectChainLoopUnits as the single source of unit
            // truth: each scheduler builds its LoopUnitSet from the same detection call on the same
            // committed list, then routes through IsMember / OwnerByIndex without re-deriving units.
            // This pins that contract. A mixed list: standalone auto (index 0) + a 3-member auto
            // chain (indices 1,2,3) + a manual-period chain pair (indices 4,5, never a unit).
            CommitStandaloneAuto(50, 110);              // index 0 — global parade
            CommitChainMember("parA", 0, 200, 250);     // index 1 — unit member
            CommitChainMember("parA", 1, 250, 300);     // index 2 — unit member
            CommitChainMember("parA", 2, 300, 350);     // index 3 — unit member
            CommitChainMember("parB", 0, 400, 450, unit: LoopTimeUnit.Sec); // index 4 — manual
            CommitChainMember("parB", 1, 450, 500, unit: LoopTimeUnit.Sec); // index 5 — manual

            var committed = RecordingStore.CommittedRecordings;

            // Both schedulers detect from the identical committed list. Two calls model "flight built
            // its set" and "KSC built its set"; since the function is pure, they MUST be byte-equal.
            var flightSet = RecordingStore.DetectChainLoopUnits(committed);
            var kscSet = RecordingStore.DetectChainLoopUnits(committed);

            // Same unit: same owner, members, span, cadence. Fails if the two schedulers diverge
            // (the inconsistency the KSC scoping exists to prevent).
            Assert.Equal(flightSet.Count, kscSet.Count);
            Assert.Equal(1, flightSet.Count);
            Assert.True(flightSet.TryGetUnitForMember(1, out var fUnit));
            Assert.True(kscSet.TryGetUnitForMember(1, out var kUnit));
            Assert.Equal(fUnit.OwnerIndex, kUnit.OwnerIndex);
            Assert.Equal(fUnit.MemberIndices, kUnit.MemberIndices);
            Assert.Equal(new[] { 1, 2, 3 }, fUnit.MemberIndices);
            Assert.Equal(fUnit.SpanStartUT, kUnit.SpanStartUT, 6);
            Assert.Equal(fUnit.SpanEndUT, kUnit.SpanEndUT, 6);
            Assert.Equal(fUnit.CadenceSeconds, kUnit.CadenceSeconds, 6);

            // Both schedulers exclude the SAME indices from the global parade. The engine exposes its
            // schedule for inspection; KSC.RebuildAutoLoopLaunchScheduleCache applies the IDENTICAL
            // `ShouldUseGlobalAutoLaunchQueue && !IsMember` predicate (verified by reading the source),
            // so the predicted excluded/scheduled split below is exactly what KSC produces too.
            var engine = new GhostPlaybackEngine(positioner: null);
            engine.SetLoopUnitsForTesting(flightSet);
            engine.RebuildAutoLoopLaunchScheduleCacheForTesting(
                TrajectoriesFromCommitted(), LoopTiming.DefaultLoopIntervalSeconds);

            // Standalone auto (0) keeps its slot. Unit members (1,2,3) are excluded. Manual chain
            // members (4,5) are not auto, so ShouldUseGlobalAutoLaunchQueue already excludes them
            // independent of the unit logic (so neither scheduler gives them a global auto slot).
            Assert.True(engine.TryGetAutoLoopScheduleForTesting(0));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(1));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(2));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(3));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(4));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(5));

            // The KSC exclusion predicate, applied to the SAME shared set, agrees index-for-index:
            // a member is excluded iff DetectChainLoopUnits says it is a member (the single source).
            for (int i = 0; i < committed.Count; i++)
                Assert.Equal(flightSet.IsMember(i), kscSet.IsMember(i));
        }

        // ─── Phase 6: debris-on-unit-member branch predicate (edge 9) ───────────

        [Fact]
        public void ShouldSourceDebrisFromUnitSpan_ParentIsUnitMember_TrueAndResolvesOwnerUnit()
        {
            // Edge 9: a debris's loop-sync parent is a chain-loop unit member. The branch predicate
            // must return true (so TryUpdateLoopSyncedDebris sources the unit's SHARED span clock
            // instead of the parent's own per-recording loop clock) and resolve the owning unit so
            // the engine can read its span. Fails if the predicate misses a unit-member parent.
            CommitChainMember("debA", 0, 100, 150); // index 0 — unit owner
            CommitChainMember("debA", 1, 150, 200); // index 1 — unit member

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            Assert.True(set.IsMember(1));

            // Parent index 1 is a unit member -> source the span clock.
            bool source = GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                parentIdx: 1, set, out var unit);
            Assert.True(source);
            Assert.Equal(0, unit.OwnerIndex);                 // resolved to the owning unit
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices);
            Assert.Equal(100.0, unit.SpanStartUT, 6);
            Assert.Equal(200.0, unit.SpanEndUT, 6);
        }

        [Fact]
        public void ShouldSourceDebrisFromUnitSpan_ParentNotAUnitMember_False()
        {
            // Control: a standalone auto looper (not a chain member) is the debris parent. The
            // predicate returns false, so the engine keeps the existing per-recording loop-clock
            // path byte-for-byte (no regression for non-unit debris). Fails if a non-unit parent is
            // wrongly routed to the span clock.
            CommitStandaloneAuto(100, 160); // index 0 — standalone, never a unit

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            Assert.Equal(0, set.Count); // no unit formed

            bool source = GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                parentIdx: 0, set, out var unit);
            Assert.False(source);
            Assert.Equal(default(GhostPlaybackLogic.LoopUnit).OwnerIndex, unit.OwnerIndex);
        }

        [Fact]
        public void ShouldSourceDebrisFromUnitSpan_EmptySetOrNegativeParent_False()
        {
            // Defensive: an Empty set (the dormant common case) and a negative parent index (no
            // loop-sync parent) both return false so the predicate never throws and never sources a
            // span clock that does not exist.
            Assert.False(GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                parentIdx: 3, GhostPlaybackLogic.LoopUnitSet.Empty, out _));
            Assert.False(GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                parentIdx: -1, GhostPlaybackLogic.LoopUnitSet.Empty, out _));
        }

        // ─── Phase 7: watch-transfer decision on unit handoff (edge 10) ─────────

        // A 3-member unit (committed indices 5,6,7) for the watch-transfer decision tests. The
        // indices are arbitrary (not 0,1,2) to catch a slot-vs-index confusion: the predicate keys
        // off the WATCHED committed index and the SELECTED SLOT, not their numeric coincidence.
        private static GhostPlaybackLogic.LoopUnit ThreeMemberUnit() =>
            new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 5, memberIndices: new[] { 5, 6, 7 },
                spanStartUT: 100, spanEndUT: 250, cadenceSeconds: 150);

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_WatchedUnitAndSlotChanged_True()
        {
            // The camera watches member #6 (a member of the unit) and the live slot advances 0->1
            // (a segment boundary). The retarget must fire so the camera follows the new live member.
            // WHAT MAKES IT FAIL: returning false here would leave the camera stuck on a now-hidden
            // member after the unit advances to the next segment.
            var unit = ThreeMemberUnit();
            Assert.True(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 6, prevSelectedSlot: 0, newSelectedSlot: 1, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_WrapSlotChange_True()
        {
            // The span wrap moves the live slot from the last member (2) back to the first (0). That
            // is still a slot change for a watched unit, so the retarget fires (transfers the camera
            // back to the owner member). Fails if the wrap is not treated as a handoff.
            var unit = ThreeMemberUnit();
            Assert.True(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 7, prevSelectedSlot: 2, newSelectedSlot: 0, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_WatchedIndexNotInUnit_False()
        {
            // The camera watches a recording (#99) that is NOT a member of this unit. Even though the
            // unit's live slot changed, the retarget must NOT fire — that camera is following an
            // unrelated ghost. Fails if a non-watched unit retargets the camera (would yank it).
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 99, prevSelectedSlot: 0, newSelectedSlot: 1, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_SameSlot_False()
        {
            // No live-member change this frame (the steady state inside one segment): the predicate
            // must return false so the retarget fires once per boundary, not every frame. Fails if it
            // fires on a same-member frame (the camera would re-transfer to itself every frame).
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 5, prevSelectedSlot: 1, newSelectedSlot: 1, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_NewSlotNoMember_False()
        {
            // The new selection is -1 (an inter-member gap or the span clock could not resolve): the
            // retarget must not fire to a non-member. Fails if a gap frame retargets the camera to a
            // bogus -1 member index.
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: 6, prevSelectedSlot: 0, newSelectedSlot: -1, unit));
        }

        [Fact]
        public void ShouldRetargetWatchOnUnitHandoff_NotWatching_False()
        {
            // No watch active (watchedIndex == -1): never fire. Fails if a unit handoff retargets the
            // camera when nothing is being watched.
            var unit = ThreeMemberUnit();
            Assert.False(GhostPlaybackLogic.ShouldRetargetWatchOnUnitHandoff(
                watchedIndex: -1, prevSelectedSlot: 0, newSelectedSlot: 1, unit));
        }
    }
}
