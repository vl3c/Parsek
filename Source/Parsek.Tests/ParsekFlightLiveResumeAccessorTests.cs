using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BUG #2: coverage for the pure id-selection guard
    /// (<c>ParsekFlight.ResolveLiveResumedRecordingId</c>) the pre-switch
    /// Merge/Discard dialog reads, plus the off-Unity behavior of the static
    /// accessors (<c>GetLiveResumedRecordingIdForDialog</c> /
    /// <c>GetLiveResumeSessionStartUT</c>) when there is no
    /// <c>ParsekFlight.Instance</c> (the headless test case).
    /// </summary>
    [Collection("Sequential")]
    public class ParsekFlightLiveResumeAccessorTests
    {
        // Fails if: the guard returns an id while the recorder is not recording
        // (a stale leaf id would then drive the dialog's live-segment branch).
        [Fact]
        public void ResolveLiveResumedRecordingId_NotRecording_ReturnsNull()
        {
            Assert.Null(ParsekFlight.ResolveLiveResumedRecordingId(
                recorderIsRecording: false, activeRecordingId: "rec_live"));
        }

        // Fails if: the guard returns an id when there is no active recording id.
        [Fact]
        public void ResolveLiveResumedRecordingId_NoActiveId_ReturnsNull()
        {
            Assert.Null(ParsekFlight.ResolveLiveResumedRecordingId(
                recorderIsRecording: true, activeRecordingId: null));
            Assert.Null(ParsekFlight.ResolveLiveResumedRecordingId(
                recorderIsRecording: true, activeRecordingId: ""));
        }

        // Fails if: the guard drops the id when recording IS live and an active
        // id exists (the success case the dialog ties its duration to).
        [Fact]
        public void ResolveLiveResumedRecordingId_RecordingWithActiveId_ReturnsId()
        {
            Assert.Equal("rec_live", ParsekFlight.ResolveLiveResumedRecordingId(
                recorderIsRecording: true, activeRecordingId: "rec_live"));
        }

        // Fails if: the accessors throw or return a non-inert value when there
        // is no Unity ParsekFlight.Instance (the headless / unit-test case the
        // dialog seam relies on being inert).
        [Fact]
        public void StaticAccessors_NoInstance_ReturnNullAndNaN()
        {
            Assert.Null(ParsekFlight.GetLiveResumedRecordingIdForDialog());
            Assert.True(double.IsNaN(ParsekFlight.GetLiveResumeSessionStartUT()));
        }
    }
}
