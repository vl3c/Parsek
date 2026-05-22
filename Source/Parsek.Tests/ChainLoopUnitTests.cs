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
    }
}
