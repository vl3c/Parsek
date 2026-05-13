using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pinned format contract for the engine-iteration trace line emitted by
    /// <see cref="GhostPlaybackEngine.UpdatePlayback"/> when
    /// ghostRenderTracing is on. The line bypasses
    /// <c>GhostRenderTrace.ShouldEmitPhase</c> / IsDetailedWindowOpen so a
    /// future ghost-vanish repro can answer: did the recording reach the
    /// per-trajectory loop, what skipReason did it carry, did its trajectory
    /// have renderable data, and was <c>ghostStates[i]</c> still populated.
    ///
    /// The format helpers are pure static so the contract can be pinned
    /// without any KSP-runtime dependency.
    /// </summary>
    public class EngineIterTraceTests
    {
        [Fact]
        public void FormatRecordingIdShort_Truncates_To_First_Eight_Characters()
        {
            string longId = "rec_152453a952804ee7b54f129bdfe2fdc1";

            string shortId = GhostPlaybackEngine.FormatRecordingIdShort(longId);

            Assert.Equal("rec_1524", shortId);
        }

        [Fact]
        public void FormatRecordingIdShort_Returns_Full_Value_When_Eight_Or_Fewer_Characters()
        {
            Assert.Equal("rec_bc0c", GhostPlaybackEngine.FormatRecordingIdShort("rec_bc0c"));
            Assert.Equal("abc", GhostPlaybackEngine.FormatRecordingIdShort("abc"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FormatRecordingIdShort_Returns_None_Placeholder_For_Null_Or_Empty(string value)
        {
            Assert.Equal("<none>", GhostPlaybackEngine.FormatRecordingIdShort(value));
        }

        [Fact]
        public void FormatEngineIterEntry_Active_Trajectory_Reports_None_Skip()
        {
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 9,
                recordingId: "rec_152453a952804ee7b54f129bdfe2fdc1",
                skipReason: GhostPlaybackSkipReason.None,
                hasRenderableData: true,
                inGhostStates: true,
                endUT: 1740.436);

            // Compact format the spec calls out: [i=N rec=ID skip=R hd=T/F hs=T/F endUT=X]
            Assert.Equal(
                "[i=9 rec=rec_1524 skip=None hd=T hs=T endUT=1740.4]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_Skipped_Trajectory_Reports_Producer_Reason()
        {
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 0,
                recordingId: "691dd66b032b4919b752597f48692fd0",
                skipReason: GhostPlaybackSkipReason.SessionSuppressed,
                hasRenderableData: true,
                inGhostStates: false,
                endUT: 131.55);

            Assert.Equal(
                "[i=0 rec=691dd66b skip=session-suppressed hd=T hs=F endUT=131.6]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_NoRenderableData_Continuation_Tail_Marker()
        {
            // Re-Fly continuation rec_bc0c... has hasRenderableData=False and
            // is suppressed via the engine's NoRenderableData fast-skip. The
            // engine-iter line must show hd=F so a log reader can tell at a
            // glance that the slot is the post-supersede continuation marker.
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 10,
                recordingId: "rec_bc0cd07fde9840e4956ce30a524ec670",
                skipReason: GhostPlaybackSkipReason.NoRenderableData,
                hasRenderableData: false,
                inGhostStates: false,
                endUT: 128.27);

            Assert.Equal(
                "[i=10 rec=rec_bc0c skip=no-renderable-data hd=F hs=F endUT=128.3]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_Handles_Null_Recording_Id()
        {
            string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                index: 3,
                recordingId: null,
                skipReason: GhostPlaybackSkipReason.None,
                hasRenderableData: false,
                inGhostStates: false,
                endUT: 0.0);

            Assert.Equal(
                "[i=3 rec=<none> skip=None hd=F hs=F endUT=0.0]",
                entry);
        }

        [Fact]
        public void FormatEngineIterEntry_Uses_InvariantCulture_For_EndUT()
        {
            // Comma-locale systems would otherwise emit "1740,4" which breaks
            // every downstream log parser (Python collect-logs.py, grep, the
            // KSP log validator). Pin the invariant: a comma-locale render
            // must still produce a period decimal separator.
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                string entry = GhostPlaybackEngine.FormatEngineIterEntry(
                    index: 9,
                    recordingId: "rec_1524",
                    skipReason: GhostPlaybackSkipReason.None,
                    hasRenderableData: true,
                    inGhostStates: true,
                    endUT: 1740.4);
                Assert.Contains("endUT=1740.4", entry);
                Assert.DoesNotContain("1740,4", entry);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
        }
    }
}
