using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BUG-B: a committed recording the player only ever progressed past in normal
    /// forward time (never rewound to replay it) must be classified historical so the
    /// ghost-playback machinery stays dormant. These pin the discriminant.
    /// </summary>
    [Collection("Sequential")]
    public class PlaybackScopeTrackerTests : IDisposable
    {
        public PlaybackScopeTrackerTests()
        {
            PlaybackScopeTracker.ResetForTesting();
        }

        public void Dispose()
        {
            PlaybackScopeTracker.ResetForTesting();
        }

        [Fact]
        public void ForwardPlayCommit_PlayheadPastStart_IsHistorical()
        {
            // Normal forward play: the recording is committed only after the player
            // has flown past its window, so the playhead is always well past its start.
            PlaybackScopeTracker.NotePlayhead("rec", currentUT: 500.0, activationStartUT: 100.0);
            Assert.False(PlaybackScopeTracker.IsInReplayScope("rec"));
            Assert.True(PlaybackScopeTracker.IsHistoricalNeverReplayed("rec", 500.0, 100.0));
        }

        [Fact]
        public void RewindBeforeStart_ArmsReplayScope_ThenNotHistorical()
        {
            // A rewind drops the playhead before the recording's launch.
            PlaybackScopeTracker.NotePlayhead("rec", currentUT: 50.0, activationStartUT: 100.0);
            Assert.True(PlaybackScopeTracker.IsInReplayScope("rec"));
            // As the playhead advances forward past the recording's end, it stays a
            // legitimate replay — never reclassified as historical.
            Assert.False(PlaybackScopeTracker.IsHistoricalNeverReplayed("rec", 500.0, 100.0));
        }

        [Fact]
        public void RewindLandingWithinActivationTolerance_ArmsScope()
        {
            // Rewind-to-launch lands the playhead on (or a few frames past) the launch,
            // which equals the recording's activation start within timing jitter.
            double justInside = 100.0 + PlaybackScopeTracker.ActivationToleranceSeconds - 0.1;
            PlaybackScopeTracker.NotePlayhead("rec", justInside, 100.0);
            Assert.True(PlaybackScopeTracker.IsInReplayScope("rec"));
            Assert.False(PlaybackScopeTracker.IsHistoricalNeverReplayed("rec", 500.0, 100.0));
        }

        [Fact]
        public void PlayheadJustPastTolerance_DoesNotArm_IsHistorical()
        {
            double justOutside = 100.0 + PlaybackScopeTracker.ActivationToleranceSeconds + 0.1;
            PlaybackScopeTracker.NotePlayhead("rec", justOutside, 100.0);
            Assert.False(PlaybackScopeTracker.IsInReplayScope("rec"));
            Assert.True(PlaybackScopeTracker.IsHistoricalNeverReplayed("rec", justOutside, 100.0));
        }

        [Fact]
        public void OrbitalExtrapolatedTail_StartBehindPlayhead_IsHistorical()
        {
            // R2-B2-S7 in the playtest: the recorded/extrapolated END (210582) ran ahead
            // of the live UT (203585), but the launch START (203566) is behind it. The
            // discriminant keys on the START, so the recording is historical.
            PlaybackScopeTracker.NotePlayhead("rec", currentUT: 203585.0, activationStartUT: 203566.0);
            Assert.True(PlaybackScopeTracker.IsHistoricalNeverReplayed("rec", 203585.0, 203566.0));
        }

        [Fact]
        public void NullOrEmptyId_NeverHistorical()
        {
            Assert.False(PlaybackScopeTracker.IsHistoricalNeverReplayed(null, 500.0, 100.0));
            Assert.False(PlaybackScopeTracker.IsHistoricalNeverReplayed("", 500.0, 100.0));
        }

        [Fact]
        public void Reset_ClearsScope()
        {
            PlaybackScopeTracker.NotePlayhead("rec", 50.0, 100.0);
            Assert.True(PlaybackScopeTracker.IsInReplayScope("rec"));
            PlaybackScopeTracker.Reset();
            Assert.False(PlaybackScopeTracker.IsInReplayScope("rec"));
            Assert.True(PlaybackScopeTracker.IsHistoricalNeverReplayed("rec", 500.0, 100.0));
        }

        [Fact]
        public void RewindToLaterLaunch_EarlierRecordingStaysHistorical()
        {
            // Rewind-to-launch of recording #3 (start 300) puts #3 in scope but leaves
            // earlier recording #1 (start 100, end 200) behind the rewind point — it
            // must stay historical (its content predates the replay origin). This is
            // exactly the case a single global "replay active" flag would get wrong.
            double rewoundUT = 300.0;
            PlaybackScopeTracker.NotePlayhead("rec1", rewoundUT, activationStartUT: 100.0);
            PlaybackScopeTracker.NotePlayhead("rec3", rewoundUT, activationStartUT: 300.0);
            Assert.False(PlaybackScopeTracker.IsInReplayScope("rec1"));
            Assert.True(PlaybackScopeTracker.IsInReplayScope("rec3"));
            Assert.True(PlaybackScopeTracker.IsHistoricalNeverReplayed("rec1", rewoundUT, 100.0));
            Assert.False(PlaybackScopeTracker.IsHistoricalNeverReplayed("rec3", rewoundUT, 300.0));
        }
    }
}
