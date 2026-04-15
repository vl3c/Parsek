using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #382: tests for <see cref="GhostPlaybackLogic.AdvanceGroupWatchCursor"/>.
    /// The helper is a pure-static rotation advancer, so every test exercises it
    /// directly without any Unity / ParsekUI scaffolding.
    /// </summary>
    [Collection("Sequential")]
    public class GroupWatchCursorTests : IDisposable
    {
        public GroupWatchCursorTests()
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
        public void NullDescendants_ReturnsEmpty()
        {
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                null, new List<Recording>(), AllEligible, null, null);
            Assert.Null(result.NextRecordingId);
            Assert.Equal(0, result.TotalEligible);
            Assert.False(result.IsToggleOff);
        }

        [Fact]
        public void EmptyDescendants_ReturnsEmpty()
        {
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                new HashSet<int>(), new List<Recording>(), AllEligible, null, null);
            Assert.Null(result.NextRecordingId);
            Assert.Equal(0, result.TotalEligible);
        }

        [Fact]
        public void AllIneligible_ReturnsEmpty()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, _ => false, null, null);
            Assert.Null(result.NextRecordingId);
            Assert.Equal(0, result.TotalEligible);
        }

        [Fact]
        public void NullRecordingId_Skipped()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, null),         // filtered: null RecordingId
                MakeRec(200, "b"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, null, null);
            Assert.Equal("b", result.NextRecordingId);
            Assert.Equal(1, result.TotalEligible);
        }

        // ── Single-entry rotations ──

        [Fact]
        public void SingleEligibleNotWatched_ReturnsIt()
        {
            var committed = new List<Recording> { MakeRec(100, "only") };
            var descendants = new HashSet<int> { 0 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, null, null);
            Assert.Equal("only", result.NextRecordingId);
            Assert.Equal(1, result.Position);
            Assert.Equal(1, result.TotalEligible);
            Assert.False(result.IsToggleOff);
            Assert.False(result.IsWrap);
        }

        [Fact]
        public void SingleEligibleIsWatched_ReturnsToggleOff()
        {
            var committed = new List<Recording> { MakeRec(100, "only") };
            var descendants = new HashSet<int> { 0 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "only", currentlyWatchedRecId: "only");
            Assert.True(result.IsToggleOff);
            Assert.Equal("only", result.NextRecordingId);
            Assert.Equal(1, result.TotalEligible);
        }

        // ── Two-entry rotations ──

        [Fact]
        public void TwoEligibleCursorNull_ReturnsFirstByStartUT()
        {
            var committed = new List<Recording>
            {
                MakeRec(200, "late"),
                MakeRec(100, "early"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, null, null);
            Assert.Equal("early", result.NextRecordingId);
            Assert.Equal(1, result.Position);
            Assert.Equal(2, result.TotalEligible);
            Assert.False(result.IsWrap);
        }

        [Fact]
        public void CursorOnFirst_ReturnsSecond()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "early"),
                MakeRec(200, "late"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "early", currentlyWatchedRecId: "early");
            Assert.Equal("late", result.NextRecordingId);
            Assert.Equal(2, result.Position);
            Assert.False(result.IsWrap);
            Assert.False(result.IsToggleOff);
        }

        [Fact]
        public void CursorOnSecond_WrapsToFirst()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "early"),
                MakeRec(200, "late"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "late", currentlyWatchedRecId: "late");
            Assert.Equal("early", result.NextRecordingId);
            Assert.Equal(1, result.Position);
            Assert.True(result.IsWrap);
        }

        [Fact]
        public void StaleCursor_ReturnsFirstEligible()
        {
            // Cursor id not in eligible list → behaves like cursor=null.
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "ghost", currentlyWatchedRecId: null);
            Assert.Equal("a", result.NextRecordingId);
            Assert.Equal(1, result.Position);
            Assert.False(result.IsWrap);
        }

        [Fact]
        public void CursorMatchesWatched_AdvancePastWatched()
        {
            // Cursor on "a", watched is "b": the rotation should skip "b" and
            // wrap back to "a" (which is not watched). Also covers the
            // companion case cursor=a, watched=a → advance to b.
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var descendants = new HashSet<int> { 0, 1 };

            // cursor=a, watched=b → next non-watched is "a" (wrap).
            var r1 = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "a", currentlyWatchedRecId: "b");
            Assert.Equal("a", r1.NextRecordingId);

            // cursor=a, watched=a → next non-watched is "b".
            var r2 = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "a", currentlyWatchedRecId: "a");
            Assert.Equal("b", r2.NextRecordingId);
            Assert.False(r2.IsToggleOff);
        }

        [Fact]
        public void StartUtTieBrokenByRecordingIdOrdinal()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "zulu"),
                MakeRec(100, "alpha"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, null, null);
            // Ordinal ascending: "alpha" < "zulu".
            Assert.Equal("alpha", result.NextRecordingId);
        }

        [Fact]
        public void OutOfRangeDescendantSkipped()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var descendants = new HashSet<int> { 0, 1, 99 };  // 99 is out of range
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, null, null);
            Assert.Equal("a", result.NextRecordingId);
            Assert.Equal(2, result.TotalEligible);
        }

        [Fact]
        public void StaleDescendant_NoCrash()
        {
            var committed = new List<Recording>
            {
                null,                  // stale: committed[0] is null
                MakeRec(200, "b"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, null, null);
            Assert.Equal("b", result.NextRecordingId);
            Assert.Equal(1, result.TotalEligible);
        }

        [Fact]
        public void ThreeEntryFullCycle()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "A"),
                MakeRec(200, "B"),
                MakeRec(300, "C"),
            };
            var descendants = new HashSet<int> { 0, 1, 2 };

            // cursor null → first by StartUT = A.
            var r1 = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, null, null);
            Assert.Equal("A", r1.NextRecordingId);
            Assert.False(r1.IsWrap);

            // cursor A → B (advance, no wrap).
            var r2 = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "A", currentlyWatchedRecId: "A");
            Assert.Equal("B", r2.NextRecordingId);
            Assert.False(r2.IsWrap);

            // cursor B → C (advance, no wrap).
            var r3 = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "B", currentlyWatchedRecId: "B");
            Assert.Equal("C", r3.NextRecordingId);
            Assert.False(r3.IsWrap);

            // cursor C → A (wrap).
            var r4 = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: "C", currentlyWatchedRecId: "C");
            Assert.Equal("A", r4.NextRecordingId);
            Assert.True(r4.IsWrap);
        }

        [Fact]
        public void NullCurrentlyWatched_FirstPress_ReturnsFirst()
        {
            var committed = new List<Recording>
            {
                MakeRec(100, "a"),
                MakeRec(200, "b"),
            };
            var descendants = new HashSet<int> { 0, 1 };
            var result = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, AllEligible, cursorRecordingId: null, currentlyWatchedRecId: null);
            Assert.Equal("a", result.NextRecordingId);
            Assert.False(result.IsToggleOff);
            Assert.Equal(1, result.Position);
        }
    }
}
