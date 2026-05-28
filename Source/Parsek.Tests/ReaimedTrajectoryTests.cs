using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 3b of re-aim: the per-window IPlaybackTrajectory adapter. Guards that it presents the
    // assembled segments + an empty (timeline-coherent) Points/events surface, derives the span from
    // the assembled list, and delegates identity to the wrapped recording.
    public class ReaimedTrajectoryTests
    {
        private static OrbitSegment Seg(string body, double start, double end)
        {
            return new OrbitSegment { bodyName = body, startUT = start, endUT = end, semiMajorAxis = 1e7 };
        }

        private static Recording WrappedRecording()
        {
            var rec = new Recording { RecordingId = "rec-reaim", VesselName = "Duna Lander" };
            // The recorded trajectory differs from the assembled one (to prove the override).
            rec.OrbitSegments.Add(Seg("Kerbin", 0, 100));
            rec.Points.Add(new TrajectoryPoint { ut = 5, bodyName = "Kerbin" });
            return rec;
        }

        [Fact]
        public void Adapter_PresentsAssembledSegments_AndDerivesSpan()
        {
            var rec = WrappedRecording();
            var assembled = new List<OrbitSegment>
            {
                Seg("Kerbin", 1_000_000, 1_000_500),
                Seg("Sun", 1_000_500, 1_500_000),
                Seg("Duna", 1_500_000, 1_503_000),
            };
            IPlaybackTrajectory adapter = new ReaimedTrajectory(rec, assembled);

            Assert.Same(assembled, adapter.OrbitSegments);
            Assert.True(adapter.HasOrbitSegments);
            // Span derived from the assembled list (NOT the recording's 0..100).
            Assert.Equal(1_000_000.0, adapter.StartUT, 3);
            Assert.Equal(1_503_000.0, adapter.EndUT, 3);
        }

        [Fact]
        public void Adapter_EmptiesTimelineIncoherentSurfaces()
        {
            var rec = WrappedRecording();
            var assembled = new List<OrbitSegment> { Seg("Sun", 100, 200) };
            IPlaybackTrajectory adapter = new ReaimedTrajectory(rec, assembled);

            // Points/TrackSections/PartEvents/FlagEvents are at the recorded timeline -> presented empty
            // so nothing binds to a UT outside the absolute-UT assembled segments (review M1).
            Assert.Empty(adapter.Points);
            Assert.Empty(adapter.TrackSections);
            Assert.Empty(adapter.PartEvents);
            Assert.Empty(adapter.FlagEvents);
        }

        [Fact]
        public void Adapter_DelegatesIdentityToRecording()
        {
            var rec = WrappedRecording();
            IPlaybackTrajectory adapter = new ReaimedTrajectory(rec, new List<OrbitSegment> { Seg("Sun", 0, 1) });
            Assert.Equal("rec-reaim", adapter.RecordingId);
            Assert.Equal("Duna Lander", adapter.VesselName);
        }

        [Fact]
        public void Adapter_NullOrEmptyAssembled_FallsBackToInnerSpan()
        {
            var rec = WrappedRecording();
            IPlaybackTrajectory adapter = new ReaimedTrajectory(rec, null);
            Assert.False(adapter.HasOrbitSegments);
            Assert.Empty(adapter.OrbitSegments);
        }
    }
}
