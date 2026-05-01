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
