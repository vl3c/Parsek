using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="PlaybackTrace"/> — the targeted post-separation
    /// observability helper. Coverage focus is the gate predicate (which
    /// decides whether a frame trace fires) and the cache lifecycle
    /// (structural-event UT list per recording id, reset on session boundary).
    /// The actual log-line emission is covered by an end-to-end smoke test
    /// using <see cref="ParsekLog.TestSinkForTesting"/>.
    /// </summary>
    [Collection("Sequential")]
    public class PlaybackTraceTests : IDisposable
    {
        public PlaybackTraceTests()
        {
            ParsekLog.SuppressLogging = true;
            PlaybackTrace.Reset();
        }

        public void Dispose()
        {
            PlaybackTrace.Reset();
            ParsekLog.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        // ============ Gate predicate ============

        [Fact]
        public void IsInWindow_NullTrajectory_ReturnsFalse()
        {
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(null, 100.0));
        }

        [Fact]
        public void IsInWindow_NullOrEmptyRecordingId_ReturnsFalse()
        {
            // No recording id → cannot key the cache or emit identifiable logs.
            var traj = MakeTrajWithStructuralEvent(recordingId: null, eventUT: 100.0);
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 100.5));

            traj = MakeTrajWithStructuralEvent(recordingId: "", eventUT: 100.0);
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 100.5));
        }

        [Fact]
        public void IsInWindow_NoStructuralEventFlags_ReturnsFalse()
        {
            // Trajectory with points but none flagged — cache builds an
            // empty list and gate stays closed at every UT.
            var traj = new MockTrajectory { RecordingId = "rec-no-flag" };
            traj.Points.Add(new TrajectoryPoint { ut = 100.0, flags = 0 });
            traj.Points.Add(new TrajectoryPoint { ut = 105.0, flags = 0 });

            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 100.5));
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 200.0));
        }

        [Fact]
        public void IsInWindow_WithinFiveSeconds_ReturnsTrue()
        {
            var traj = MakeTrajWithStructuralEvent(
                recordingId: "rec-event", eventUT: 100.0);

            // Exact match, half-second after, four-second after, five-second after — all in.
            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 100.0));
            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 100.5));
            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 104.0));
            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 105.0));
        }

        [Fact]
        public void IsInWindow_PastFiveSeconds_ReturnsFalse()
        {
            var traj = MakeTrajWithStructuralEvent(
                recordingId: "rec-event", eventUT: 100.0);

            // Just outside the 5-second window.
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 105.001));
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 200.0));
        }

        [Fact]
        public void IsInWindow_BeforeAnyEvent_ReturnsFalse()
        {
            // Pre-event UT must close the gate — there's nothing yet to be
            // "5 seconds after of".
            var traj = MakeTrajWithStructuralEvent(
                recordingId: "rec-event", eventUT: 100.0);
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 50.0));
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 99.999));
        }

        [Fact]
        public void IsInWindow_MultipleEvents_TracksMostRecent()
        {
            // Two structural events — first at 100, second at 200. Gate
            // should follow whichever is most recent at currentUT.
            var traj = new MockTrajectory { RecordingId = "rec-multi" };
            traj.Points.Add(MakeFlaggedPoint(100.0));
            traj.Points.Add(MakeFlaggedPoint(200.0));

            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 100.5));
            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 104.0));
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 110.0));
            // Now a second event fires the gate again.
            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 200.0));
            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 204.5));
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 210.0));
        }

        [Fact]
        public void IsInWindow_UnsortedPoints_StillCorrectViaDefensiveSort()
        {
            // Points appended out of order (e.g. chain merge). The cache
            // builder sorts ascending so the binary search stays correct.
            var traj = new MockTrajectory { RecordingId = "rec-unsorted" };
            traj.Points.Add(MakeFlaggedPoint(200.0));
            traj.Points.Add(MakeFlaggedPoint(100.0));

            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 100.5));
            Assert.True(PlaybackTrace.IsInPostStructuralEventWindow(traj, 200.5));
            Assert.False(PlaybackTrace.IsInPostStructuralEventWindow(traj, 110.0));
        }

        // ============ Cache lifecycle ============

        [Fact]
        public void Cache_LazilyPopulatedOnFirstQuery()
        {
            var traj = MakeTrajWithStructuralEvent("rec-lazy", 50.0);
            Assert.Equal(0, PlaybackTrace.CachedRecordingCountForTesting);

            PlaybackTrace.IsInPostStructuralEventWindow(traj, 50.5);
            Assert.Equal(1, PlaybackTrace.CachedRecordingCountForTesting);

            var cached = PlaybackTrace.GetCachedStructuralEventUTsForTesting("rec-lazy");
            Assert.NotNull(cached);
            Assert.Single(cached);
            Assert.Equal(50.0, cached[0]);
        }

        [Fact]
        public void Cache_NoFlaggedPoints_StoresEmptyList()
        {
            // No structural events → cache an empty list so subsequent
            // queries short-circuit without re-scanning Points.
            var traj = new MockTrajectory { RecordingId = "rec-empty" };
            traj.Points.Add(new TrajectoryPoint { ut = 1.0, flags = 0 });
            traj.Points.Add(new TrajectoryPoint { ut = 2.0, flags = 0 });

            PlaybackTrace.IsInPostStructuralEventWindow(traj, 1.5);
            Assert.Equal(1, PlaybackTrace.CachedRecordingCountForTesting);

            var cached = PlaybackTrace.GetCachedStructuralEventUTsForTesting("rec-empty");
            Assert.NotNull(cached);
            Assert.Empty(cached);
        }

        [Fact]
        public void Reset_ClearsCacheAndTraceState()
        {
            var traj = MakeTrajWithStructuralEvent("rec-reset", 10.0);
            PlaybackTrace.IsInPostStructuralEventWindow(traj, 10.5);
            // Also seed a trace cursor by emitting a frame.
            PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.5,
                renderedPos: new Vector3(1, 2, 3));
            Assert.Equal(1, PlaybackTrace.CachedRecordingCountForTesting);

            PlaybackTrace.Reset();
            Assert.Equal(0, PlaybackTrace.CachedRecordingCountForTesting);
            Assert.Null(PlaybackTrace.GetCachedStructuralEventUTsForTesting("rec-reset"));
        }

        // ============ End-to-end emission ============

        [Fact]
        public void MaybeEmitFrame_OutsideWindow_NoLogEmitted()
        {
            var traj = MakeTrajWithStructuralEvent("rec-out", 10.0);
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // Way past the 5-second window.
                PlaybackTrace.MaybeEmitFrame(
                    traj, ghostIdx: 0, currentUT: 100.0, renderedPos: Vector3.zero);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            Assert.DoesNotContain(logLines, l => l.Contains("[PlaybackTrace]"));
        }

        [Fact]
        public void MaybeEmitFrame_InsideWindow_EmitsLogLine()
        {
            var traj = MakeTrajWithStructuralEvent("rec-in", 10.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 9.5, endUT = 11.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 9.5 },
                    new TrajectoryPoint { ut = 11.0 },
                },
            });

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                PlaybackTrace.MaybeEmitFrame(
                    traj, ghostIdx: 7, currentUT: 10.5,
                    renderedPos: new Vector3(100, 200, 300));
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            Assert.Contains(logLines, l =>
                l.Contains("[PlaybackTrace]")
                && l.Contains("rec=rec-in")
                && l.Contains("#7")
                && l.Contains("ut=10.500")
                && l.Contains("ref=Absolute")
                && l.Contains("worldPos=(100.00,200.00,300.00)")
                && l.Contains("dM=0.00"));   // first frame, no previous to compare
        }

        [Fact]
        public void MaybeEmitFrame_TwoFrames_DeltaReflectsMotion()
        {
            // Two consecutive frames inside the window — the second log
            // line should report the metres travelled since the first.
            var traj = MakeTrajWithStructuralEvent("rec-delta", 10.0);

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.0,
                    renderedPos: new Vector3(0, 0, 0));
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.1,
                    renderedPos: new Vector3(3, 4, 0));   // 5m away → 50 m/s at 0.1s dt
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            // First line: dM=0.00 (no prior). Second: dM=5.00 dSpd=50.0.
            Assert.Equal(2, logLines.FindAll(l => l.Contains("[PlaybackTrace]")).Count);
            Assert.Contains(logLines, l =>
                l.Contains("[PlaybackTrace]") && l.Contains("ut=10.100")
                && l.Contains("dM=5.00")
                && l.Contains("dSpd=50.0"));
        }

        [Fact]
        public void MaybeEmitFrame_CrossesSection_FlagsSectionCrossed()
        {
            // First frame in section 0; next frame past section 0's endUT
            // — sectionCrossed flag must appear on the second line.
            var traj = MakeTrajWithStructuralEvent("rec-cross", 10.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 9.0, endUT = 10.5,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 9.0 },
                    new TrajectoryPoint { ut = 10.5 },
                },
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 10.5, endUT = 12.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 10.5 },
                    new TrajectoryPoint { ut = 12.0 },
                },
            });

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.0,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 11.0,
                    renderedPos: Vector3.zero);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            // First line: in section 0 (Absolute), no sectionCrossed.
            Assert.Contains(logLines, l =>
                l.Contains("[PlaybackTrace]") && l.Contains("ut=10.000")
                && l.Contains("sec=0") && l.Contains("ref=Absolute")
                && !l.Contains("sectionCrossed"));
            // Second line: in section 1 (Relative), sectionCrossed flag set.
            Assert.Contains(logLines, l =>
                l.Contains("[PlaybackTrace]") && l.Contains("ut=11.000")
                && l.Contains("sec=1") && l.Contains("ref=Relative")
                && l.Contains("sectionCrossed"));
        }

        // ============ Loop-wraparound dedup ============

        [Fact]
        public void MaybeEmitFrame_LoopWraparound_SuppressesRepeatEventWindow()
        {
            // First window across event UT 10 emits multiple frames as
            // currentUT advances monotonically inside [10, 15). When the
            // ghost loops and currentUT jumps back to before the event,
            // re-entering the same window must not retrace the event:
            // exactly one trace per unique (recId, ghostIdx, eventUT).
            var traj = MakeTrajWithStructuralEvent("rec-loop", 10.0);

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // First window: three frames inside the 5-second window.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.0,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 11.0,
                    renderedPos: new Vector3(1, 0, 0));
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.9,
                    renderedPos: new Vector3(5, 0, 0));

                int firstWindowLines = logLines.FindAll(
                    l => l.Contains("[PlaybackTrace]")).Count;
                Assert.Equal(3, firstWindowLines);

                // Loop wraparound — currentUT jumps backwards into the
                // same event window. The first re-entry frame (10.0 <
                // the prior pass's last-emitted 14.9) retires event 10
                // into the completed set, so the whole second pass is
                // suppressed — including frames at or above the prior
                // high-water (covered by the dedicated test below).
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.0,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 11.0,
                    renderedPos: new Vector3(1, 0, 0));
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.9,
                    renderedPos: new Vector3(5, 0, 0));
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            // No additional emissions across the second wraparound pass.
            int totalLines = logLines.FindAll(
                l => l.Contains("[PlaybackTrace]")).Count;
            Assert.Equal(3, totalLines);
        }

        [Fact]
        public void MaybeEmitFrame_LoopWraparound_NewEventStillEmits()
        {
            // Two structural events at UT 10 and UT 100. After fully tracing
            // the first event and looping back through it (suppressed by the
            // wraparound guard), the second event must still emit because
            // its event UT differs from the cursor's lastTracedEventUT.
            var traj = new MockTrajectory { RecordingId = "rec-two-events" };
            traj.Points.Add(MakeFlaggedPoint(10.0));
            traj.Points.Add(MakeFlaggedPoint(100.0));

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // Trace the first event window forward up to UT 14.0.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.0,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.0,
                    renderedPos: new Vector3(4, 0, 0));
                Assert.Equal(2, logLines.FindAll(
                    l => l.Contains("[PlaybackTrace]")).Count);

                // Wraparound — currentUT jumps backwards into the first
                // event's window. Same lastTracedEventUT, lower UT → guard
                // suppresses.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.5,
                    renderedPos: Vector3.zero);
                Assert.Equal(2, logLines.FindAll(
                    l => l.Contains("[PlaybackTrace]")).Count);

                // Move forward into the second event's window. Different
                // event UT → guard inert, emit a fresh trace.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 100.5,
                    renderedPos: new Vector3(50, 0, 0));
                Assert.Equal(3, logLines.FindAll(
                    l => l.Contains("[PlaybackTrace]")).Count);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }
        }

        [Fact]
        public void MaybeEmitFrame_LoopWraparound_StateRecordsLastEventUT()
        {
            // The cursor's lastTracedEventUT must equal the most recent
            // structural event UT after the first emission — this is
            // the field the loop-replay guards key on.
            var traj = MakeTrajWithStructuralEvent("rec-cursor", 42.0);

            ParsekLog.SuppressLogging = true; // suppress emission noise
            PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 3, currentUT: 42.5,
                renderedPos: Vector3.zero);

            double traced = PlaybackTrace.GetLastTracedEventUTForTesting(
                "rec-cursor", ghostIdx: 3);
            Assert.Equal(42.0, traced);
        }

        [Fact]
        public void MaybeEmitFrame_GateCloseRetiresEvent()
        {
            // A frame past the 5-second window for a traced event retires
            // that event UT into the completed set — even though the
            // gate-closed frame itself emits nothing. This is what makes a
            // later loop re-entry suppressible regardless of where in the
            // window it lands.
            var traj = MakeTrajWithStructuralEvent("rec-gateclose", 10.0);

            ParsekLog.SuppressLogging = true;
            // Trace one in-window frame, then a gate-closed frame.
            PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.5,
                renderedPos: Vector3.zero);
            Assert.False(PlaybackTrace.IsEventCompletedForTesting(
                "rec-gateclose", ghostIdx: 0, eventUT: 10.0));

            PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 16.0,
                renderedPos: Vector3.zero);
            Assert.True(PlaybackTrace.IsEventCompletedForTesting(
                "rec-gateclose", ghostIdx: 0, eventUT: 10.0));
        }

        [Fact]
        public void MaybeEmitFrame_GateClosedPastSkippedLaterEvent_RetiresEarlierTracedEvent()
        {
            // Two events at UT 10 and UT 100. The ghost traces event A
            // (UT 10), is hidden through event B's window (UT 100–105)
            // entirely, then reappears at UT 120 — a gate-closed frame
            // whose mostRecentEventUT is B, not A. The gate-closed
            // retirement must still retire A: it keys on lastTracedEventUT
            // (whose own window aged out long ago), not on
            // mostRecentEventUT. Without that, A would never be retired
            // and a later loop re-entry of A's window could re-emit.
            var traj = new MockTrajectory { RecordingId = "rec-skip-later" };
            traj.Points.Add(MakeFlaggedPoint(10.0));
            traj.Points.Add(MakeFlaggedPoint(100.0));

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // First pass: trace event A only.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.5,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.9,
                    renderedPos: Vector3.zero);
                Assert.False(PlaybackTrace.IsEventCompletedForTesting(
                    "rec-skip-later", ghostIdx: 0, eventUT: 10.0));

                // Hidden through event B's window; reappears at UT 120 —
                // gate-closed, mostRecentEventUT = B (100) != lastTraced A.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 120.0,
                    renderedPos: Vector3.zero);
                Assert.True(PlaybackTrace.IsEventCompletedForTesting(
                    "rec-skip-later", ghostIdx: 0, eventUT: 10.0));
                // Event B was never traced — it must NOT be retired, so a
                // future loop can still trace it fresh.
                Assert.False(PlaybackTrace.IsEventCompletedForTesting(
                    "rec-skip-later", ghostIdx: 0, eventUT: 100.0));

                // Loop re-entry of A's window at and above the prior
                // high-water — suppressed because A is retired.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.9,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.95,
                    renderedPos: Vector3.zero);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            // Only the two first-pass frames emitted.
            Assert.Equal(2, logLines.FindAll(
                l => l.Contains("[PlaybackTrace]")).Count);
        }

        [Fact]
        public void MaybeEmitFrame_ReEntryAtOrAboveHighWater_Suppressed()
        {
            // Regression for the loop-dedup high-water hole: after the
            // first pass and a gate-closed frame retire the event, a loop
            // re-entry whose first in-window frame lands AT or ABOVE the
            // prior pass's high-water UT must still be suppressed. A guard
            // that only compared currentUT to the high-water would resume
            // logging the tail here; the completed-event set does not.
            var traj = MakeTrajWithStructuralEvent("rec-rewindhw", 10.0);

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // First pass: forward through high-water 14.9.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.0,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.9,
                    renderedPos: new Vector3(5, 0, 0));
                // Gate-closed frame past the window retires the event.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 16.0,
                    renderedPos: new Vector3(6, 0, 0));
                Assert.Equal(2, logLines.FindAll(
                    l => l.Contains("[PlaybackTrace]")).Count);

                // Loop re-entry landing exactly AT and then ABOVE the prior
                // high-water — both must be suppressed.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.9,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.95,
                    renderedPos: Vector3.zero);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            Assert.Equal(2, logLines.FindAll(
                l => l.Contains("[PlaybackTrace]")).Count);
        }

        [Fact]
        public void MaybeEmitFrame_PreEventFrameAfterWrap_RetiresTracedEvent()
        {
            // When a loop replays through the recording's pre-event region
            // (currentUT before every flagged event), that is an
            // unambiguous wrap signal: the previously-traced event is
            // retired so its upcoming re-entry is suppressed regardless of
            // where the loop's first in-window frame lands — including the
            // early-ended-first-pass case a high-water comparison misses.
            var traj = MakeTrajWithStructuralEvent("rec-preevent", 10.0);

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // First pass ends early after a single frame at 10.2.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.2,
                    renderedPos: Vector3.zero);
                Assert.Single(logLines.FindAll(
                    l => l.Contains("[PlaybackTrace]")));
                Assert.False(PlaybackTrace.IsEventCompletedForTesting(
                    "rec-preevent", ghostIdx: 0, eventUT: 10.0));

                // Loop wrap replays a pre-event frame (currentUT 2.0 < the
                // event at 10.0) — retires event 10.0.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 2.0,
                    renderedPos: Vector3.zero);
                Assert.True(PlaybackTrace.IsEventCompletedForTesting(
                    "rec-preevent", ghostIdx: 0, eventUT: 10.0));

                // The loop's first in-window frame lands ABOVE the prior
                // high-water of 10.2 — still suppressed.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.5,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 12.0,
                    renderedPos: Vector3.zero);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            Assert.Single(logLines.FindAll(
                l => l.Contains("[PlaybackTrace]")));
        }

        [Fact]
        public void MaybeEmitFrame_FirstPassEndsEarly_LoopTailNotReEmitted()
        {
            // If the first pass ends after a single frame (ghost
            // hidden/retired/late-spawned), a loop re-entry must not
            // re-emit the whole 5-second tail. The first re-entry frame at
            // the window start (below the early-ended high-water) retires
            // the event, so the rest of that loop pass and every later
            // loop are suppressed.
            var traj = MakeTrajWithStructuralEvent("rec-earlyend", 10.0);

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // First pass: a single frame, then the ghost is gone.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.1,
                    renderedPos: Vector3.zero);
                Assert.Single(logLines.FindAll(
                    l => l.Contains("[PlaybackTrace]")));

                // Loop 2: first in-window frame at the window start retires
                // the event; the rest of the tail is suppressed.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.0,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 11.0,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 14.9,
                    renderedPos: Vector3.zero);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            Assert.Single(logLines.FindAll(
                l => l.Contains("[PlaybackTrace]")));
        }

        [Fact]
        public void MaybeEmitFrame_HiddenThroughPreEventAndIntoWindow_ResidualIsBoundedAndSelfHeals()
        {
            // Documents the one known residual: a ghost that stays hidden
            // (no MaybeEmitFrame calls) through a recording's entire
            // pre-event region AND into the event window on a loop pass.
            // That loop has no wrap signal to observe — no pre-event frame,
            // no below-high-water frame, no gate-closed frame — so it
            // re-emits a partial tail. The point of this test is that the
            // residual is BOUNDED: the next loop, which does replay a
            // pre-event frame, retires the event and suppresses everything.
            var traj = MakeTrajWithStructuralEvent("rec-residual", 10.0);

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // First pass ends early at high-water 10.2.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.2,
                    renderedPos: Vector3.zero);

                // Loop 2: ghost hidden through the whole pre-event region;
                // first observed frame is in-window AND above the prior
                // high-water. No wrap signal — this loop leaks a partial
                // tail.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.5,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 13.0,
                    renderedPos: Vector3.zero);
                int afterLoop2 = logLines.FindAll(
                    l => l.Contains("[PlaybackTrace]")).Count;
                // First pass (1) + the bounded loop-2 leak (2) = 3.
                Assert.Equal(3, afterLoop2);

                // Loop 3: ghost visible normally — a pre-event frame is
                // observed, retiring the event. The leak does not recur.
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 3.0,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 10.5,
                    renderedPos: Vector3.zero);
                PlaybackTrace.MaybeEmitFrame(traj, ghostIdx: 0, currentUT: 13.0,
                    renderedPos: Vector3.zero);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            // Loop 3 added nothing — the residual self-healed.
            Assert.Equal(3, logLines.FindAll(
                l => l.Contains("[PlaybackTrace]")).Count);
        }

        [Fact]
        public void MaybeEmitFrame_NoTrackSections_EmitsWithUnknownSection()
        {
            // Trajectory with no TrackSections (early in build, or
            // orbit-only) still emits within the window — section
            // markers default to sec=-1 / ref=Absolute / [?,?].
            var traj = MakeTrajWithStructuralEvent("rec-nosec", 10.0);
            traj.TrackSections.Clear();

            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                PlaybackTrace.MaybeEmitFrame(
                    traj, ghostIdx: 0, currentUT: 10.5, renderedPos: Vector3.zero);
            }
            finally { ParsekLog.ResetTestOverrides(); ParsekLog.SuppressLogging = true; }

            Assert.Contains(logLines, l =>
                l.Contains("[PlaybackTrace]")
                && l.Contains("sec=-1") && l.Contains("[?,?]"));
        }

        // ============ Helpers ============

        private static MockTrajectory MakeTrajWithStructuralEvent(string recordingId, double eventUT)
        {
            var traj = new MockTrajectory { RecordingId = recordingId };
            traj.Points.Add(MakeFlaggedPoint(eventUT));
            return traj;
        }

        private static TrajectoryPoint MakeFlaggedPoint(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot,
            };
        }
    }
}
