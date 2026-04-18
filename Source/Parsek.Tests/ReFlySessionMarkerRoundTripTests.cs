using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards against ReFlySessionMarker save/load drift (design doc section 5.7).
    /// </summary>
    public class ReFlySessionMarkerRoundTripTests
    {
        [Fact]
        public void ReFlySessionMarker_AllFields_RoundTrips()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "rf_c3d4",
                OriginRpId = "rp_a1b2",
                OriginSlotIndex = 1,
                ProvisionalRecordingId = "rec_prov",
                StartRealTime = "2026-04-17T23:15:00Z",
                StartUT = 1742810.25
            };

            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);
            var node = parent.GetNode("REFLY_SESSION_MARKER");
            Assert.NotNull(node);

            var restored = ReFlySessionMarker.LoadFrom(node);
            Assert.Equal("rf_c3d4", restored.SessionId);
            Assert.Equal("rp_a1b2", restored.OriginRpId);
            Assert.Equal(1, restored.OriginSlotIndex);
            Assert.Equal("rec_prov", restored.ProvisionalRecordingId);
            Assert.Equal("2026-04-17T23:15:00Z", restored.StartRealTime);
            Assert.Equal(1742810.25, restored.StartUT);
        }

        [Fact]
        public void ReFlySessionMarker_DefaultSlotIndexZero_RoundTrips()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                OriginRpId = "rp",
                OriginSlotIndex = 0,
                ProvisionalRecordingId = "rec",
                StartUT = 0.0
            };

            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);
            var restored = ReFlySessionMarker.LoadFrom(parent.GetNode("REFLY_SESSION_MARKER"));

            Assert.Equal(0, restored.OriginSlotIndex);
            Assert.Null(restored.StartRealTime);
        }
    }
}
