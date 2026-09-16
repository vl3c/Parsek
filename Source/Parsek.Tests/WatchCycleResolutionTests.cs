using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="WatchModeController.ResolveCycleTarget"/>, the pure
    /// resolver that backs the W-key watch-cycle keypress. Exercises descendants
    /// construction, watched-id resolution, and the HasTarget gate that the
    /// keypress handler reads to decide whether to call EnterWatchMode.
    /// </summary>
    [Collection("Sequential")]
    public class WatchCycleResolutionTests : IDisposable
    {
        public WatchCycleResolutionTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private static Recording MakeRec(double startUT, string recordingId, string vesselName = "test", bool debris = false)
        {
            var r = new Recording { VesselName = vesselName, RecordingId = recordingId };
            r.Points.Add(new TrajectoryPoint { ut = startUT });
            r.IsDebris = debris;
            return r;
        }

        private static Func<int, bool> AllEligible => _ => true;

        // ── Empty / guard cases ──

        [Fact]
        public void NullCommitted_ReturnsEmpty()
        {
            var result = WatchModeController.ResolveCycleTarget(
                null, AllEligible, currentWatchedIndex: 0, cursorRecordingId: null);
            Assert.False(result.HasTarget);
            Assert.Equal(-1, result.NextIndex);
            Assert.Null(result.NextRecordingId);
            Assert.Equal(0, result.TotalEligible);
        }

        [Fact]
        public void EmptyCommitted_ReturnsEmpty()
        {
            var result = WatchModeController.ResolveCycleTarget(
                new List<Recording>(), AllEligible, currentWatchedIndex: -1, cursorRecordingId: null);
            Assert.False(result.HasTarget);
            Assert.Equal(0, result.TotalEligible);
        }

        [Fact]
        public void NullPredicate_ReturnsEmpty()
        {
            var committed = new List<Recording> { MakeRec(100, "a") };
            var result = WatchModeController.ResolveCycleTarget(
                committed, null, currentWatchedIndex: 0, cursorRecordingId: null);
            Assert.False(result.HasTarget);
        }

        [Fact]
        public void AllIneligible_ReturnsEmpty()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var result = WatchModeController.ResolveCycleTarget(
                committed, _ => false, currentWatchedIndex: 0, cursorRecordingId: null);
            Assert.False(result.HasTarget);
            Assert.Equal(0, result.TotalEligible);
        }

        // ── Toggle-off (single eligible == watched) ──

        [Fact]
        public void OnlyWatchedIsEligible_ReturnsToggleOffWithoutTarget()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "watched"),
                MakeRec(200, "other"),
            };
            // Only index 0 ("watched") passes the predicate.
            Func<int, bool> isEligible = idx => idx == 0;

            var result = WatchModeController.ResolveCycleTarget(
                committed, isEligible, currentWatchedIndex: 0, cursorRecordingId: null);

            Assert.True(result.IsToggleOff);
            Assert.False(result.HasTarget);
            Assert.Equal("watched", result.NextRecordingId);
            Assert.Equal(1, result.TotalEligible);
        }

        // ── Normal cycle paths ──

        [Fact]
        public void TwoEligibleCursorNull_AdvancesPastWatched()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),    // watched
                MakeRec(200, "b"),
            };
            var result = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: 0, cursorRecordingId: null);

            Assert.True(result.HasTarget);
            Assert.Equal(1, result.NextIndex);
            Assert.Equal("b", result.NextRecordingId);
            Assert.Equal(2, result.TotalEligible);
            Assert.False(result.IsToggleOff);
        }

        [Fact]
        public void CursorOnWatched_WrapsToNonWatched()
        {
            // Cycle progression: watching "b", cursor was last set to "b" by a
            // prior advance. Next press should wrap forward to "a".
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var result = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: 1, cursorRecordingId: "b");

            Assert.True(result.HasTarget);
            Assert.Equal(0, result.NextIndex);
            Assert.Equal("a", result.NextRecordingId);
            Assert.True(result.IsWrap);
        }

        [Fact]
        public void ThreeEligibleAdvancesInStartUtOrder()
        {
            var committed = new List<Recording>
            {
                MakeRec(300, "c"),
                MakeRec(100, "a"),    // watched
                MakeRec(200, "b"),
            };
            // Watching "a" at index 1; cursor null → next is "b" by StartUT order.
            var r1 = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: 1, cursorRecordingId: null);
            Assert.True(r1.HasTarget);
            Assert.Equal("b", r1.NextRecordingId);
            Assert.Equal(2, r1.NextIndex);

            // After we enter watch on "b" (index 2), cursor was set to "b".
            // Next W press advances to "c".
            var r2 = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: 2, cursorRecordingId: "b");
            Assert.True(r2.HasTarget);
            Assert.Equal("c", r2.NextRecordingId);
            Assert.Equal(0, r2.NextIndex);
        }

        // ── Predicate plumbing ──

        [Fact]
        public void DebrisFilteredOutByPredicate()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "junk", debris: true),
                MakeRec(300, "b"),
            };
            // Caller's predicate excludes debris (mirrors the CycleToNextWatchable
            // composition).
            Func<int, bool> isEligible = idx => committed[idx] != null && !committed[idx].IsDebris;

            var result = WatchModeController.ResolveCycleTarget(
                committed, isEligible, currentWatchedIndex: 0, cursorRecordingId: null);

            Assert.True(result.HasTarget);
            Assert.Equal("b", result.NextRecordingId);
            Assert.Equal(2, result.TotalEligible);
        }

        [Fact]
        public void CoverageRetiredChild_SkippedByPredicate()
        {
            // #895 regression, resolver contract. CycleToNextWatchable's live
            // predicate excludes a coverage-retired controlled-decoupled child via
            // IsGhostCoverageRetired (covered by the in-game test, since it reads
            // engine state). This test pins the resolver half of that composition:
            // given a predicate that rejects the child index, ResolveCycleTarget
            // must advance past it to the next watchable ghost rather than steering
            // the camera onto a target that would fail watch entry.
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),               // watched
                MakeRec(200, "retired-child"),   // non-debris, but coverage-retired
                MakeRec(300, "b"),
            };
            // Mirror the live composition: not debris AND not coverage-retired.
            // Index 1 is the retired child -> excluded despite being non-debris.
            Func<int, bool> isEligible = idx =>
                committed[idx] != null && !committed[idx].IsDebris && idx != 1;

            var result = WatchModeController.ResolveCycleTarget(
                committed, isEligible, currentWatchedIndex: 0, cursorRecordingId: null);

            Assert.True(result.HasTarget);
            Assert.Equal("b", result.NextRecordingId);
            Assert.Equal(2, result.NextIndex);
            Assert.Equal(2, result.TotalEligible);
        }

        [Fact]
        public void CoverageRetiredChild_AsOnlyOtherCandidate_ReportsNoAdvance()
        {
            // #895 regression, freeze guard (resolver half). When the retired child
            // is the ONLY non-watched candidate, the resolver must report toggle-off
            // / no-target so the keypress handler reads HasTarget==false, never calls
            // EnterWatchMode, and leaves the camera on the current ghost instead of
            // tearing it down for a target that cannot be entered. The live
            // IsGhostCoverageRetired exclusion is covered by the in-game test.
            var committed = new List<Recording>
            {
                MakeRec(100, "watched"),
                MakeRec(200, "retired-child"),   // non-debris, coverage-retired
            };
            Func<int, bool> isEligible = idx =>
                committed[idx] != null && !committed[idx].IsDebris && idx != 1;

            var result = WatchModeController.ResolveCycleTarget(
                committed, isEligible, currentWatchedIndex: 0, cursorRecordingId: null);

            Assert.False(result.HasTarget);
            Assert.True(result.IsToggleOff);
            Assert.Equal(1, result.TotalEligible);
        }

        [Fact]
        public void WatchedIndexOutOfRange_TreatedAsNoWatchedId()
        {
            // currentWatchedIndex out of bounds means watchedId resolves to null;
            // AdvanceGroupWatchCursor then returns the first eligible entry.
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var result = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: 99, cursorRecordingId: null);

            Assert.True(result.HasTarget);
            Assert.Equal("a", result.NextRecordingId);
            Assert.Equal(2, result.TotalEligible);
        }

        [Fact]
        public void WatchedIndexNegative_TreatedAsNoWatchedId()
        {
            // Defensive: -1 (not currently watching) maps to null watchedId.
            // CycleToNextWatchable already guards on IsWatchingGhost before
            // calling the resolver, but the resolver itself must stay coherent.
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var result = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: -1, cursorRecordingId: null);

            Assert.True(result.HasTarget);
            Assert.Equal("a", result.NextRecordingId);
            Assert.False(result.IsToggleOff);
        }

        [Fact]
        public void NullRecordingIdInCommitted_FilteredOutOfRotation()
        {
            // AdvanceGroupWatchCursor rejects rows with null/empty RecordingId.
            // Pin the behavior at this composition surface so a regression in
            // the lower helper (or an accidental allow-empty change here) is
            // caught locally instead of as a spooky cycle-skip in playtests.
            var committed = new List<Recording>
            {
                MakeRec(100, null),         // filtered: null RecordingId
                MakeRec(200, "watched"),
                MakeRec(300, "other"),
            };
            var result = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: 1, cursorRecordingId: null);

            Assert.True(result.HasTarget);
            Assert.Equal("other", result.NextRecordingId);
            Assert.Equal(2, result.TotalEligible);
        }

        [Fact]
        public void GlobalScope_IncludesEveryCommittedIndex()
        {
            // The W keypress cycle is global, not group-scoped: a recording
            // anywhere in CommittedRecordings is eligible if the predicate
            // accepts it. Pin that the resolver does not silently restrict to
            // a subrange or to the watched recording's neighbourhood.
            var committed = new List<Recording>
            {
                MakeRec(100, "first",  vesselName: "tree-A-root"),    // watched
                MakeRec(200, "middle", vesselName: "tree-B-root"),    // different tree
                MakeRec(300, "last",   vesselName: "tree-C-root"),    // different tree
            };
            var result = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: 0, cursorRecordingId: null);
            Assert.True(result.HasTarget);
            Assert.Equal(3, result.TotalEligible);
            Assert.Equal("middle", result.NextRecordingId);

            // After advancing to "middle" the next press reaches the third tree.
            var r2 = WatchModeController.ResolveCycleTarget(
                committed, AllEligible, currentWatchedIndex: 1, cursorRecordingId: "middle");
            Assert.True(r2.HasTarget);
            Assert.Equal("last", r2.NextRecordingId);
        }

        // ── Overlap-loop watch cadence (#443) ──

        [Fact]
        public void ResolveWatchPlaybackUT_CadenceAdjustedLoop_UsesEffectiveCadence()
        {
            // A 2000 s recording looped every 40 s exceeds the 20-cycle cap, so the
            // engine launches cycles every 2000/20 = 100 s and numbers loopCycleIndex
            // with THAT cadence. Watch mode must rebuild the cycle start the same way:
            // cycle 3 starts at 1000 + 3*100 = 1300, so at UT 1400 the watched ghost is
            // 100 s into its trajectory and the camera belongs at 1100. Rebuilding from
            // the stored 40 s period puts cycle 3 at 1120 and the camera 180 s further
            // along, at 1280 - the wrong-loop-phase defect.
            double ut = WatchModeController.ComputeWatchOverlapPlaybackUT(
                currentUT: 1400.0,
                scheduleStartUT: 1000.0,
                playbackStartUT: 1000.0,
                duration: 2000.0,
                userPeriodSeconds: 40.0,
                loopCycleIndex: 3,
                maxOverlapGhosts: 20);

            Assert.Equal(1100.0, ut, 6);
        }

        [Fact]
        public void ResolveWatchPlaybackUT_PeriodAboveTheCapFloor_UsesTheStoredPeriod()
        {
            // Mirror: a 250 s period over the same 2000 s recording already fits the cap
            // (2000/250 = 8 live cycles), so nothing is raised and the stored period is
            // the cadence. Cycle 3 starts at 1000 + 3*250 = 1750 and UT 1900 is 150 s in.
            double ut = WatchModeController.ComputeWatchOverlapPlaybackUT(
                currentUT: 1900.0,
                scheduleStartUT: 1000.0,
                playbackStartUT: 1000.0,
                duration: 2000.0,
                userPeriodSeconds: 250.0,
                loopCycleIndex: 3,
                maxOverlapGhosts: 20);

            Assert.Equal(1150.0, ut, 6);
        }

        // ── Lock mask sanity ──

        [Fact]
        public void WatchModeLockMaskIncludesPitch()
        {
            // W (and S) are KSP's stock PITCH-axis keys; they must be locked out
            // of the active vessel while the player watches a ghost so the W
            // keypress can be repurposed for cycling without bleeding pitch
            // input through to the unattended vessel.
            Assert.True((WatchModeController.WatchModeLockMask & ControlTypes.PITCH) == ControlTypes.PITCH);
        }
    }
}
