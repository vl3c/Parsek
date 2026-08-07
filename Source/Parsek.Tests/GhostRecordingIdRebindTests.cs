using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pid -> recordingId reverse-map rebind seam (flight-arrival lane):
    /// the map was write-once-at-create, which froze a ghost's label on the
    /// FIRST recording that bound the pid - the V2 lane measured a looped
    /// ghost's probe lines attributed to a scene-entry stub's recId.
    /// </summary>
    [Collection("Sequential")]
    public class GhostRecordingIdRebindTests : System.IDisposable
    {
        public GhostRecordingIdRebindTests()
        {
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
        }

        [Fact]
        public void Rebind_ReplacesTheStaleCreateTimeId()
        {
            GhostMapPresence.TrackRecordingGhostIdentityForTesting(
                4242u, 0, "stub-rec-id");
            Assert.Equal("stub-rec-id",
                GhostMapPresence.FindRecordingIdByVesselPid(4242u));

            GhostMapPresence.RebindGhostRecordingId(4242u, "driving-rec-id");
            Assert.Equal("driving-rec-id",
                GhostMapPresence.FindRecordingIdByVesselPid(4242u));
        }

        [Fact]
        public void Rebind_NoOpsOnNullEmptyAndZeroPid()
        {
            GhostMapPresence.TrackRecordingGhostIdentityForTesting(
                4242u, 0, "stub-rec-id");

            GhostMapPresence.RebindGhostRecordingId(4242u, null);
            GhostMapPresence.RebindGhostRecordingId(4242u, "");
            Assert.Equal("stub-rec-id",
                GhostMapPresence.FindRecordingIdByVesselPid(4242u));

            GhostMapPresence.RebindGhostRecordingId(0u, "anything");
            Assert.Null(GhostMapPresence.FindRecordingIdByVesselPid(0u));
        }

        [Fact]
        public void Rebind_SameIdIsIdempotent()
        {
            GhostMapPresence.TrackRecordingGhostIdentityForTesting(
                4242u, 0, "rec-a");
            GhostMapPresence.RebindGhostRecordingId(4242u, "rec-a");
            Assert.Equal("rec-a",
                GhostMapPresence.FindRecordingIdByVesselPid(4242u));
        }
    }
}
