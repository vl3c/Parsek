using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for loop units using the MAIN-LINK-RUN model.
    ///
    /// A "mission" is a recording tree with a MAIN through-line (the primary vessel's launch -> ...
    /// -> descent recordings) plus SECONDARY recordings (debris / probes that play in parallel). A
    /// unit = a maximal run of >= 2 CONSECUTIVE main links that are ALL loop+auto+renderable, PLUS
    /// every ride-along SECONDARY (loops+auto, overlaps the run span). There is NO UT-contiguity
    /// requirement between main links - gaps are fine; a main link missing loop+auto BREAKS the run;
    /// secondaries never break the run. The whole unit plays on ONE shared mission clock, with each
    /// member rendering concurrently when the clock is in its own window.
    ///
    /// Detection: RecordingStore.DetectChainLoopUnits. Playback decision: the pure span-clock
    /// helpers (TryComputeSpanLoopUT) + per-member render decision (DecideUnitMemberRender) in
    /// GhostPlaybackLogic. See docs/dev/design-chain-sequential-auto-loop.md.
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

        // ─── DetectChainLoopUnits ───────────────────────────────────────────────

        /// <summary>
        /// Builds and commits a tree-member recording. StartUT/EndUT derive from the first and
        /// last point UT, so [startUT, endUT] is the member window. <paramref name="treeId"/>
        /// defaults to <paramref name="chainId"/> so a single id keeps same-tree members together.
        /// A MAIN link has isDebris=false, parentAnchorRecordingId=null, branch=0; pass any of those
        /// to make a SECONDARY (debris / probe / orphan / parallel-continuation).
        /// </summary>
        private static Recording CommitChainMember(
            string chainId, int chainIndex, double startUT, double endUT,
            bool loop = true, LoopTimeUnit unit = LoopTimeUnit.Auto, int branch = 0,
            int pointCount = 2, string treeId = null, string parentAnchorRecordingId = null,
            bool isDebris = false)
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
                TreeId = treeId ?? chainId,
                ParentAnchorRecordingId = parentAnchorRecordingId,
                IsDebris = isDebris,
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
        public void DetectChainLoopUnits_ThreeMainLinks_WithUTGaps_FormOneUnit()
        {
            // HEADLINE / the v1 bug fix: three main links A,B,C all loop+auto, with UT GAPS between
            // them (A [100,150], B [200,250], C [400,450]), still form ONE unit. The old code
            // required windows to touch; the new model groups CONSECUTIVE main links regardless of
            // UT gaps. REGRESSION GUARD: fails if a UT gap between main links still breaks the run.
            CommitChainMember("A", 0, 100, 150, treeId: "mission");
            CommitChainMember("B", 0, 200, 250, treeId: "mission"); // 50s gap before
            CommitChainMember("C", 0, 400, 450, treeId: "mission"); // 150s gap before

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.IsMember(0));
            Assert.True(set.IsMember(1));
            Assert.True(set.IsMember(2));
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(0, unit.OwnerIndex);                    // earliest main link
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices); // sorted by StartUT
            Assert.Equal(100.0, unit.SpanStartUT, 6);            // min StartUT
            Assert.Equal(450.0, unit.SpanEndUT, 6);              // max EndUT (over gaps)
        }

        [Fact]
        public void DetectChainLoopUnits_MiddleMainLinkLacksLoopAuto_BreaksRun_NoUnit()
        {
            // A-B-C all main links, but B lacks loop+auto (it is manual-period). B BREAKS the run.
            // A and C are then NOT consecutive main links (B sits between them in StartUT order), so
            // neither side has >= 2 consecutive eligible main links -> NO unit. REGRESSION GUARD:
            // fails if a run-ineligible main link is silently skipped (bridging A and C).
            CommitChainMember("A", 0, 100, 150, treeId: "m");
            CommitChainMember("B", 0, 150, 200, unit: LoopTimeUnit.Sec, treeId: "m"); // breaks the run
            CommitChainMember("C", 0, 200, 250, treeId: "m");

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(0, set.Count);
            Assert.False(set.IsMember(0));
            Assert.False(set.IsMember(1));
            Assert.False(set.IsMember(2));
        }

        [Fact]
        public void DetectChainLoopUnits_AB_LoopAuto_C_Not_FormsUnitOfAB()
        {
            // A-B have loop+auto, C (a main link) does not. The run is A-B (the trailing C breaks it
            // after the run already has 2 members), so the unit is {A,B}; C is excluded.
            CommitChainMember("A", 0, 100, 150, treeId: "m");
            CommitChainMember("B", 0, 150, 200, treeId: "m");
            CommitChainMember("C", 0, 200, 250, loop: false, treeId: "m"); // not looping

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices);
            Assert.False(set.IsMember(2)); // C not loop+auto, excluded
        }

        [Fact]
        public void DetectChainLoopUnits_SingleMainLink_NotAUnit()
        {
            // A single main link with loop+auto is NOT a unit (it loops standalone as today). Add a
            // run-eligible main link in ANOTHER tree so the global fast-out (>= 2 eligible main
            // links) does not short-circuit before per-tree detection runs on the lone one.
            CommitChainMember("solo", 0, 100, 150, treeId: "soloTree");
            CommitChainMember("other", 0, 500, 550, treeId: "otherTree"); // lone main link in its tree
            // 'other' tree also has only 1 main link, so neither tree forms a unit, but both are
            // run-eligible main links so the fast-out passes and per-tree detection runs.

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(0, set.Count);
            Assert.False(set.IsMember(0));
            Assert.False(set.IsMember(1));
        }

        [Fact]
        public void DetectChainLoopUnits_DebrisWithLoopAuto_OverlapsRun_RidesAlong()
        {
            // A-B main links form a unit; a SECONDARY debris (loop+auto) whose window overlaps the
            // run rides along as a member. The shared clock plays it concurrently with its parent.
            // REGRESSION GUARD: fails if ride-along debris is omitted, OR if it is mistaken for a
            // main link (it must never seed/break a run).
            CommitChainMember("A", 0, 100, 150, treeId: "m");                       // main, idx 0
            CommitChainMember("B", 0, 150, 250, treeId: "m");                       // main, idx 1
            CommitChainMember("deb", 0, 160, 220, treeId: "m",
                parentAnchorRecordingId: "parent", isDebris: true);                 // debris, idx 2

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices); // debris rides along
            Assert.Equal(0, unit.OwnerIndex);                    // owner is still the earliest main link
            Assert.Equal(100.0, unit.SpanStartUT, 6);
            Assert.Equal(250.0, unit.SpanEndUT, 6);
        }

        [Fact]
        public void DetectChainLoopUnits_DebrisWithoutLoopAuto_OmittedDoesNotBreakRun()
        {
            // A-B main links form a unit. A debris WITHOUT loop+auto overlapping the run is simply
            // omitted (not rendered) and does NOT affect the chain. REGRESSION GUARD: fails if a
            // non-loop secondary is pulled in as a member, or breaks/blocks the unit.
            CommitChainMember("A", 0, 100, 150, treeId: "m");                       // main, idx 0
            CommitChainMember("B", 0, 150, 250, treeId: "m");                       // main, idx 1
            CommitChainMember("deb", 0, 160, 220, loop: false, treeId: "m",
                parentAnchorRecordingId: "parent", isDebris: true);                 // debris no-loop, idx 2

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices); // debris omitted, A-B still a unit
            Assert.False(set.IsMember(2));
        }

        [Fact]
        public void DetectChainLoopUnits_RideAlongDebris_OutsideSpan_NotIncluded()
        {
            // A loop+auto debris whose window does NOT overlap the run span is not a ride-along
            // member (nothing to play alongside). A-B span [100,250]; the debris [400,450] is
            // entirely after, so it is not included even though it loops+auto.
            CommitChainMember("A", 0, 100, 150, treeId: "m");                       // main, idx 0
            CommitChainMember("B", 0, 150, 250, treeId: "m");                       // main, idx 1
            CommitChainMember("deb", 0, 400, 450, treeId: "m",
                parentAnchorRecordingId: "parent", isDebris: true);                 // debris, idx 2

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices);
            Assert.False(set.IsMember(2)); // outside the run span
        }

        [Fact]
        public void DetectChainLoopUnits_ParentAnchoredProbe_TreatedAsSecondary()
        {
            // A parent-anchored controlled child (IsDebris=false but ParentAnchorRecordingId set, a
            // probe / lander) is a SECONDARY, never a main link. With loop+auto and an overlapping
            // window it rides along; it never seeds or breaks the chain. REGRESSION GUARD: fails if
            // a parent-anchored probe is treated as a main link (it would break the A-B run, since
            // it sits between them in StartUT order).
            CommitChainMember("A", 0, 100, 200, treeId: "m");                       // main, idx 0
            CommitChainMember("probe", 0, 150, 180, treeId: "m",
                parentAnchorRecordingId: "A-rec", isDebris: false);                 // controlled child, idx 1
            CommitChainMember("B", 0, 200, 250, treeId: "m");                       // main, idx 2

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            // A (0) and B (2) are the consecutive main links; the probe (1) rides along (overlaps).
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices);
            Assert.Equal(0, unit.OwnerIndex); // earliest MAIN link (not the probe)
        }

        [Fact]
        public void DetectChainLoopUnits_Branch1MainExcluded_IsSecondary()
        {
            // ChainBranch > 0 (parallel ghost-only continuation) is NOT a main link (it is
            // SECONDARY). A branch-0 main pair forms the unit; the branch-1 member rides along only
            // if it loops+auto and overlaps (it does here). It never seeds/breaks the run.
            CommitChainMember("A", 0, 100, 150, branch: 0, treeId: "m"); // main, idx 0
            CommitChainMember("B", 0, 150, 200, branch: 0, treeId: "m"); // main, idx 1
            CommitChainMember("B", 1, 150, 200, branch: 1, treeId: "m"); // parallel continuation, idx 2

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(0, unit.OwnerIndex);
            // Branch-1 (idx 2) is a secondary; it loops+auto and overlaps the span, so it rides along.
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices);
        }

        [Fact]
        public void DetectChainLoopUnits_DifferentTreeId_NotMerged()
        {
            // Main links in DIFFERENT trees never merge: detection is scoped per TreeId. Each tree
            // has only 1 main link, so neither forms a unit. (Both are run-eligible so the global
            // fast-out passes.)
            CommitChainMember("a", 0, 100, 150, treeId: "treeA");
            CommitChainMember("b", 0, 150, 200, treeId: "treeB");

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(0, set.Count);
            Assert.False(set.IsMember(0));
            Assert.False(set.IsMember(1));
        }

        [Fact]
        public void DetectChainLoopUnits_ZeroDurationMainLink_BreaksRun()
        {
            // A single-point (< 2 points / zero-duration) main link is NOT renderable, so it is
            // run-INELIGIBLE and BREAKS the run. [auto-main, zero-duration-auto-main] leaves only one
            // eligible main link before the break -> no unit. Add a 2nd eligible tree so the fast-out
            // passes. REGRESSION GUARD: fails if a degenerate main link is treated as run-eligible.
            CommitChainMember("A", 0, 100, 150, unit: LoopTimeUnit.Auto, treeId: "m");
            CommitChainMember("B", 0, 200, 200, unit: LoopTimeUnit.Auto, pointCount: 1, treeId: "m"); // single point
            CommitChainMember("pair", 0, 500, 550, treeId: "pair"); // eligible pair in another tree
            CommitChainMember("pair", 1, 550, 600, treeId: "pair");

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            // 'm' tree dissolves (only A is eligible before B breaks the run); 'pair' tree is a unit.
            Assert.Equal(1, set.Count);
            Assert.False(set.IsMember(0));
            Assert.False(set.IsMember(1));
            Assert.True(set.IsMember(2));
            Assert.True(set.IsMember(3));
        }

        [Fact]
        public void DetectChainLoopUnits_NullTreeId_NotEligible()
        {
            // A null/empty TreeId cannot belong to a mission (shouldn't happen in always-tree mode):
            // two loop+auto recordings with NO TreeId never form a unit.
            CommitStandaloneAuto(100, 150); // no TreeId
            CommitStandaloneAuto(150, 200); // no TreeId

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Equal(0, set.Count);
            Assert.False(set.IsMember(0));
            Assert.False(set.IsMember(1));
        }

        [Fact]
        public void DetectChainLoopUnits_Cadence_MaxOfAutoIntervalAndSpan()
        {
            // cadence = max(autoInterval n, span, MinCycleDuration). Span here is 150s (100..250).
            // With n = 30 (< span) cadence == span (150). With n = 300 (> span) cadence == n (300).
            CommitChainMember("A", 0, 100, 200, treeId: "m");
            CommitChainMember("B", 0, 200, 250, treeId: "m");

            // n < span: cadence == span (back-to-back, no wait).
            var setSmallN = RecordingStore.DetectChainLoopUnits(
                RecordingStore.CommittedRecordings, globalAutoIntervalSeconds: 30);
            Assert.True(setSmallN.TryGetUnitForMember(0, out var unitSmall));
            Assert.Equal(150.0, unitSmall.CadenceSeconds, 6); // span wins
            Assert.Equal(150.0, unitSmall.SpanEndUT - unitSmall.SpanStartUT, 6);

            // n > span: cadence == n (there is a wait, isInInterCycleTail reachable).
            var setBigN = RecordingStore.DetectChainLoopUnits(
                RecordingStore.CommittedRecordings, globalAutoIntervalSeconds: 300);
            Assert.True(setBigN.TryGetUnitForMember(0, out var unitBig));
            Assert.Equal(300.0, unitBig.CadenceSeconds, 6); // autoInterval wins
            Assert.True(unitBig.CadenceSeconds > (unitBig.SpanEndUT - unitBig.SpanStartUT));
        }

        [Fact]
        public void DetectChainLoopUnits_TinySpanAndTinyInterval_CadenceClampedToMinCycleDuration()
        {
            // A sub-MinCycleDuration span (2s) AND a sub-MinCycleDuration autoInterval (2s) clamp
            // cadence to MinCycleDuration (the third term in the max).
            CommitChainMember("A", 0, 100, 101, treeId: "m");
            CommitChainMember("B", 0, 101, 102, treeId: "m");

            var set = RecordingStore.DetectChainLoopUnits(
                RecordingStore.CommittedRecordings, globalAutoIntervalSeconds: 2);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(2.0, unit.SpanEndUT - unit.SpanStartUT, 6); // raw span is 2s
            Assert.Equal(LoopTiming.MinCycleDuration, unit.CadenceSeconds, 6); // clamped up
        }

        [Fact]
        public void DetectChainLoopUnits_SpanIncludesRideAlongDebrisTail()
        {
            // Span = min StartUT..max EndUT over ALL members, including ride-along debris. The
            // debris tail [200,300] extends the span past the main links' [100,250]. cadence then
            // tracks that wider span (200s) when it exceeds the autoInterval.
            CommitChainMember("A", 0, 100, 150, treeId: "m");                       // main, idx 0
            CommitChainMember("B", 0, 150, 250, treeId: "m");                       // main, idx 1
            CommitChainMember("deb", 0, 200, 300, treeId: "m",
                parentAnchorRecordingId: "parent", isDebris: true);                 // debris tail, idx 2

            var set = RecordingStore.DetectChainLoopUnits(
                RecordingStore.CommittedRecordings, globalAutoIntervalSeconds: 30);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices);
            Assert.Equal(100.0, unit.SpanStartUT, 6);
            Assert.Equal(300.0, unit.SpanEndUT, 6);            // debris tail extends span
            Assert.Equal(200.0, unit.CadenceSeconds, 6);       // span (200) > autoInterval (30)
        }

        [Fact]
        public void DetectChainLoopUnits_NullOrSingletonList_ReturnsEmpty()
        {
            // Defensive: null and under-2-element lists return the shared Empty (no allocation).
            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty, RecordingStore.DetectChainLoopUnits(null));

            CommitChainMember("A", 0, 100, 150, treeId: "m");
            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty,
                RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings));
        }

        [Fact]
        public void DetectChainLoopUnits_FewerThanTwoMainLinks_ReturnsEmptyViaFastPath()
        {
            // Dormant-path fast-out: the per-frame common case has < 2 run-eligible MAIN links.
            // Here two standalone auto-loop recordings carry no TreeId (so they are not main links of
            // any mission), so the pre-scan counts 0 run-eligible main links and returns the shared
            // Empty before allocating. REGRESSION GUARD: fails if the fast path mis-counted a
            // non-main-link save as eligible.
            CommitStandaloneAuto(50, 90);  // no TreeId -> not a main link
            CommitStandaloneAuto(95, 130); // no TreeId -> not a main link

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty, set);
            Assert.Equal(0, set.Count);
        }

        // ─── DetectChainLoopUnits log-assertion tests ───────────────────────────

        [Fact]
        public void DetectChainLoopUnits_EmitsUnitBuiltSummary()
        {
            CommitChainMember("A", 0, 100, 150, treeId: "logA");
            CommitChainMember("B", 0, 150, 200, treeId: "logA");

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
            // Per-unit detail with member indices and span.
            Assert.Contains(captured, l =>
                l.Contains("[Loop]") && l.Contains("Chain-loop unit: owner=0")
                && l.Contains("members=0,1") && l.Contains("span=100..200"));
        }

        [Fact]
        public void DetectChainLoopUnits_LoneMainLinkLogsLengthLtTwo()
        {
            // A lone run-eligible main link in its own tree logs the length<2 rejection. A SECOND
            // tree carries a contiguous eligible pair so the global eligible count is >= 2 and the
            // dormant fast-out does not short-circuit.
            CommitChainMember("logB", 0, 100, 150, treeId: "logB"); // lone main link in its tree
            CommitChainMember("pair", 0, 500, 550, treeId: "pair"); // eligible pair in another tree
            CommitChainMember("pair", 1, 550, 600, treeId: "pair");

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
                l.Contains("[Loop]") && l.Contains("tree logB") && l.Contains("not a unit: length<2"));
        }

        [Fact]
        public void DetectChainLoopUnits_RunBreakLogsReason()
        {
            // A main link that lacks loop+auto BREAKS the run and logs WHY (member-not-auto). This
            // makes "why didn't these loop as one" answerable from the log alone.
            CommitChainMember("A", 0, 100, 150, treeId: "logC");
            CommitChainMember("B", 0, 150, 200, unit: LoopTimeUnit.Sec, treeId: "logC"); // manual, breaks
            CommitChainMember("C", 0, 200, 250, treeId: "logC");

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
                l.Contains("[Loop]") && l.Contains("tree logC")
                && l.Contains("recIdx=1") && l.Contains("breaks run: member-not-auto"));
        }

        // ─── IsMainLink / IsRunEligibleMainLink / IsRideAlongSecondary ──────────

        [Fact]
        public void IsMainLink_StructuralSplit()
        {
            // Main link = not debris AND no parent anchor AND branch 0.
            var main = CommitChainMember("m", 0, 100, 150, treeId: "t");
            Assert.True(RecordingStore.IsMainLink(main));

            var debris = CommitChainMember("d", 0, 100, 150, treeId: "t", isDebris: true);
            Assert.False(RecordingStore.IsMainLink(debris));

            var anchored = CommitChainMember("p", 0, 100, 150, treeId: "t",
                parentAnchorRecordingId: "x");
            Assert.False(RecordingStore.IsMainLink(anchored));

            var branch = CommitChainMember("b", 0, 100, 150, treeId: "t", branch: 1);
            Assert.False(RecordingStore.IsMainLink(branch));

            Assert.False(RecordingStore.IsMainLink(null));
        }

        [Fact]
        public void IsRunEligibleMainLink_RequiresMainAndLoopAutoRenderable()
        {
            var ok = CommitChainMember("m", 0, 100, 150, treeId: "t");
            Assert.True(RecordingStore.IsRunEligibleMainLink(ok, out _));

            var notMain = CommitChainMember("d", 0, 100, 150, treeId: "t", isDebris: true);
            Assert.False(RecordingStore.IsRunEligibleMainLink(notMain, out string r1));
            Assert.Equal("not-main-link", r1);

            var notLoop = CommitChainMember("m2", 0, 100, 150, loop: false, treeId: "t");
            Assert.False(RecordingStore.IsRunEligibleMainLink(notLoop, out string r2));
            Assert.Equal("member-not-looping", r2);

            var notAuto = CommitChainMember("m3", 0, 100, 150, unit: LoopTimeUnit.Sec, treeId: "t");
            Assert.False(RecordingStore.IsRunEligibleMainLink(notAuto, out string r3));
            Assert.Equal("member-not-auto", r3);

            var zeroDur = CommitChainMember("m4", 0, 100, 100, pointCount: 1, treeId: "t");
            Assert.False(RecordingStore.IsRunEligibleMainLink(zeroDur, out string r4));
            Assert.Equal("member-zero-duration", r4);
        }

        [Fact]
        public void IsRideAlongSecondary_RequiresSecondaryAndLoopAutoRenderable()
        {
            var debrisOk = CommitChainMember("d", 0, 100, 150, treeId: "t",
                parentAnchorRecordingId: "x", isDebris: true);
            Assert.True(RecordingStore.IsRideAlongSecondary(debrisOk));

            // A main link is NOT a ride-along secondary even if loop+auto.
            var main = CommitChainMember("m", 0, 100, 150, treeId: "t");
            Assert.False(RecordingStore.IsRideAlongSecondary(main));

            // A secondary without loop+auto is omitted.
            var debrisNoLoop = CommitChainMember("d2", 0, 100, 150, loop: false, treeId: "t",
                parentAnchorRecordingId: "x", isDebris: true);
            Assert.False(RecordingStore.IsRideAlongSecondary(debrisNoLoop));
        }

        // ─── Global-queue exclusion in the engine schedule rebuild ──────────────

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
            // global slot but the two unit members do NOT (they are scheduled by the shared clock).
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
            // Control: with an Empty LoopUnitSet, every eligible auto-loop recording keeps its slot.
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
            // standalone keeps its parade slot; all three chain members are excluded.
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
            CommitChainMember("schedL", 0, 100, 150);
            CommitChainMember("schedL", 1, 150, 200);

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);

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

        // ─── Dual-scheduler (flight engine + tracking station) parity ───────────

        [Fact]
        public void DualScheduler_SameRecordingSet_ProducesIdenticalUnits()
        {
            // Both the flight engine and the tracking-station scheduler share the pure
            // RecordingStore.DetectChainLoopUnits as the single source of unit truth, so a looped
            // chain replays identically in both scenes. Mixed list: standalone auto (index 0) + a
            // 3-member auto chain (indices 1,2,3) + a manual-period chain pair (indices 4,5).
            CommitStandaloneAuto(50, 110);              // index 0 - global parade
            CommitChainMember("parA", 0, 200, 250);     // index 1 - unit member
            CommitChainMember("parA", 1, 250, 300);     // index 2 - unit member
            CommitChainMember("parA", 2, 300, 350);     // index 3 - unit member
            CommitChainMember("parB", 0, 400, 450, unit: LoopTimeUnit.Sec); // index 4 - manual
            CommitChainMember("parB", 1, 450, 500, unit: LoopTimeUnit.Sec); // index 5 - manual

            var committed = RecordingStore.CommittedRecordings;

            var flightSet = RecordingStore.DetectChainLoopUnits(committed);
            var kscSet = RecordingStore.DetectChainLoopUnits(committed);

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

            var engine = new GhostPlaybackEngine(positioner: null);
            engine.SetLoopUnitsForTesting(flightSet);
            engine.RebuildAutoLoopLaunchScheduleCacheForTesting(
                TrajectoriesFromCommitted(), LoopTiming.DefaultLoopIntervalSeconds);

            Assert.True(engine.TryGetAutoLoopScheduleForTesting(0));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(1));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(2));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(3));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(4));
            Assert.False(engine.TryGetAutoLoopScheduleForTesting(5));

            for (int i = 0; i < committed.Count; i++)
                Assert.Equal(flightSet.IsMember(i), kscSet.IsMember(i));
        }

        // ─── Debris-on-unit-member branch predicate (edge 9) ────────────────────

        [Fact]
        public void ShouldSourceDebrisFromUnitSpan_ParentIsUnitMember_TrueAndResolvesOwnerUnit()
        {
            // A debris's loop-sync parent is a loop-unit member -> the debris must source the unit's
            // SHARED mission clock (TryUpdateLoopSyncedDebris) instead of the parent's own clock.
            CommitChainMember("debA", 0, 100, 150); // index 0 - unit owner
            CommitChainMember("debA", 1, 150, 200); // index 1 - unit member

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            Assert.True(set.IsMember(1));

            bool source = GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                parentIdx: 1, set, out var unit);
            Assert.True(source);
            Assert.Equal(0, unit.OwnerIndex);
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices);
            Assert.Equal(100.0, unit.SpanStartUT, 6);
            Assert.Equal(200.0, unit.SpanEndUT, 6);
        }

        [Fact]
        public void ShouldSourceDebrisFromUnitSpan_ParentNotAUnitMember_False()
        {
            // Control: a standalone auto looper (not a unit member) is the debris parent -> false,
            // so the engine keeps the existing per-recording loop-clock path.
            CommitStandaloneAuto(100, 160); // index 0 - standalone, never a unit

            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            Assert.Equal(0, set.Count);

            bool source = GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                parentIdx: 0, set, out var unit);
            Assert.False(source);
            Assert.Equal(default(GhostPlaybackLogic.LoopUnit).OwnerIndex, unit.OwnerIndex);
        }

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
