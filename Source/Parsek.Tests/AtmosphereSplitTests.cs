using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class AtmosphereSplitTests
    {
        // Kerbin atmosphere depth
        private const double KerbinAtmoDepth = 70000.0;

        [Fact]
        public void NoAtmosphere_ReturnsFalse()
        {
            // Body without atmosphere (atmoDepth = 0)
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: 50000, atmosphereDepth: 0,
                pendingCross: true, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void SameSide_ReturnsFalse()
        {
            // Still inside atmosphere (no boundary crossed)
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: 50000, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: true, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void CrossedButNotFarEnough_ReturnsFalse()
        {
            // Just barely past atmosphere boundary (100m past, need 1000m)
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: KerbinAtmoDepth + 100, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: true, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void CrossedFarButNotLongEnough_ReturnsFalse()
        {
            // Past 1000m but timer not started (pendingCross = false)
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: KerbinAtmoDepth + 2000, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: false, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void CrossedFarAndLongEnough_ExitAtmo_ReturnsTrue()
        {
            // Exiting atmosphere: was in atmo, now above + 2000m, timer sustained 5s
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: KerbinAtmoDepth + 2000, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: true, pendingUT: 100, currentUT: 105);
            Assert.True(result);
        }

        [Fact]
        public void CrossedFarAndLongEnough_EnterAtmo_ReturnsTrue()
        {
            // Entering atmosphere: was exo, now below by 2000m, timer sustained 5s
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: false, altitude: KerbinAtmoDepth - 2000, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: true, pendingUT: 100, currentUT: 105);
            Assert.True(result);
        }

        [Fact]
        public void HysteresisTimeNotMet_ReturnsFalse()
        {
            // Only 2s elapsed, need 3s
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: KerbinAtmoDepth + 2000, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: true, pendingUT: 100, currentUT: 102);
            Assert.False(result);
        }

        [Fact]
        public void ExactlyAtHysteresisThreshold_ReturnsTrue()
        {
            // Exactly 3s elapsed, exactly 1000m past
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: KerbinAtmoDepth + 1000, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: true, pendingUT: 100, currentUT: 103);
            Assert.True(result);
        }

        [Fact]
        public void CustomHysteresis_Respected()
        {
            // Custom: 1s time, 500m distance
            bool result = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: KerbinAtmoDepth + 600, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: true, pendingUT: 100, currentUT: 101.5,
                hysteresisSeconds: 1.0, hysteresisMeters: 500.0);
            Assert.True(result);

            // Same custom, but not enough distance
            bool result2 = FlightRecorder.ShouldSplitAtAtmosphereBoundary(
                wasInAtmo: true, altitude: KerbinAtmoDepth + 400, atmosphereDepth: KerbinAtmoDepth,
                pendingCross: true, pendingUT: 100, currentUT: 101.5,
                hysteresisSeconds: 1.0, hysteresisMeters: 500.0);
            Assert.False(result2);
        }

        [Fact]
        public void GetSegmentPhaseLabel_Formats()
        {
            var rec = new RecordingStore.Recording();
            Assert.Equal("", RecordingStore.GetSegmentPhaseLabel(rec));

            rec.SegmentPhase = "atmo";
            Assert.Equal("atmo", RecordingStore.GetSegmentPhaseLabel(rec));

            rec.SegmentBodyName = "Kerbin";
            Assert.Equal("Kerbin atmo", RecordingStore.GetSegmentPhaseLabel(rec));

            rec.SegmentPhase = "exo";
            Assert.Equal("Kerbin exo", RecordingStore.GetSegmentPhaseLabel(rec));

            rec.SegmentPhase = "space";
            rec.SegmentBodyName = "Mun";
            Assert.Equal("Mun space", RecordingStore.GetSegmentPhaseLabel(rec));
        }

        [Fact]
        public void SoiChangeFlag_DefaultsFalse()
        {
            var recorder = new FlightRecorder();
            Assert.False(recorder.SoiChangePending);
            Assert.Null(recorder.SoiChangeFromBody);
        }
    }
}
