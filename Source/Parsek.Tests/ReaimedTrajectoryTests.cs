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
            // A recorded engine event so PartEvents delegation is observable.
            rec.PartEvents.Add(new PartEvent { ut = 10, partPersistentId = 42, moduleIndex = 0 });
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
        public void Adapter_EmptiesPositionDependentSurfaces()
        {
            var rec = WrappedRecording();
            var assembled = new List<OrbitSegment> { Seg("Sun", 100, 200) };
            IPlaybackTrajectory adapter = new ReaimedTrajectory(rec, assembled);

            // Points/TrackSections trace the PRE-re-aim heliocentric path -> presented empty (the
            // synthesized OrbitSegment replaces that phase). FlagEvents stay empty (surface flags are
            // not part of the transfer replay).
            Assert.Empty(adapter.Points);
            Assert.Empty(adapter.TrackSections);
            Assert.Empty(adapter.FlagEvents);
        }

        [Fact]
        public void Adapter_DelegatesPartEventsToRecording()
        {
            // PartEvents ARE delegated: the assembled segments sit on the recorded-span clock, so a
            // recorded-UT engine/staging event resolves coherently, and the ghost MUST see the real engine
            // events or the orphan-engine audio auto-start (zero events => assume debris booster) misfires
            // on the main ship and loops every engine through the coast.
            var rec = WrappedRecording();
            var assembled = new List<OrbitSegment> { Seg("Sun", 100, 200) };
            IPlaybackTrajectory adapter = new ReaimedTrajectory(rec, assembled);

            Assert.Same(rec.PartEvents, adapter.PartEvents);
            Assert.Single(adapter.PartEvents);
            Assert.Equal(42u, adapter.PartEvents[0].partPersistentId);
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
            // The named behaviour is the SPAN fallback, so the span is what has to be read: with no
            // assembled segments the adapter must present the wrapped recording's own span. The
            // recording is given a non-zero, non-degenerate span so a stubbed 0.0 / 0.0 fallback cannot
            // pass. Both halves of the `assembledSegments ?? new List<>()` coalesce are covered: the
            // null list and the empty list.
            var rec = new Recording { RecordingId = "rec-reaim", VesselName = "Duna Lander" };
            rec.OrbitSegments.Add(Seg("Kerbin", 20, 80));
            rec.Points.Add(new TrajectoryPoint { ut = 20, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 80, bodyName = "Kerbin" });
            Assert.True(rec.StartUT > 0.0);
            Assert.True(rec.EndUT > rec.StartUT);

            IPlaybackTrajectory nullAssembled = new ReaimedTrajectory(rec, null);
            Assert.False(nullAssembled.HasOrbitSegments);
            Assert.Empty(nullAssembled.OrbitSegments);
            Assert.Equal(rec.StartUT, nullAssembled.StartUT, 3);
            Assert.Equal(rec.EndUT, nullAssembled.EndUT, 3);

            IPlaybackTrajectory emptyAssembled = new ReaimedTrajectory(rec, new List<OrbitSegment>());
            Assert.False(emptyAssembled.HasOrbitSegments);
            Assert.Empty(emptyAssembled.OrbitSegments);
            Assert.Equal(rec.StartUT, emptyAssembled.StartUT, 3);
            Assert.Equal(rec.EndUT, emptyAssembled.EndUT, 3);
        }
    }
}
