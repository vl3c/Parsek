using Parsek;
using Parsek.MapRender;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 guard for the typed <see cref="ITrajectorySample"/> view (design §5.3) that splits the
    /// frame-overloaded <c>TrajectoryPoint</c>. The load-bearing property is FRAME DISCRIMINATION: an
    /// <see cref="AbsoluteSample"/> must report degrees-in-Absolute and a <see cref="RelativeSample"/>
    /// must report metres-in-Relative, with distinct <see cref="TrajectorySampleKind"/>s, so no
    /// downstream reader can mis-read a Relative sample's metres as lat/lon (the documented "ghost
    /// inside the planet" trap).
    ///
    /// Each assertion states the bug it catches: a wrong <c>Frame</c>/<c>Kind</c> would route a
    /// metre-scale Relative offset through <c>body.GetWorldSurfacePosition(lat,lon,alt)</c> and place
    /// the ghost deep inside the planet.
    /// </summary>
    public class TrajectorySampleTests
    {
        [Fact]
        public void AbsoluteSample_ReportsDegreesInAbsoluteFrame()
        {
            var s = new AbsoluteSample(
                ut: 123.0, latitude: 10.5, longitude: -45.0, altitude: 1200.0,
                bodyName: "Kerbin", srfRelRotation: Quaternion.identity);
            ITrajectorySample view = s;

            Assert.Equal(TrajectorySampleKind.Absolute, view.Kind);
            Assert.Equal(ReferenceFrame.Absolute, view.Frame);
            Assert.Equal(123.0, view.Ut);
            Assert.Equal(10.5, s.Latitude);
            Assert.Equal(-45.0, s.Longitude);
            Assert.Equal(1200.0, s.Altitude);
            Assert.Equal("Kerbin", s.BodyName);
        }

        [Fact]
        public void RelativeSample_ReportsMetresInRelativeFrame()
        {
            var s = new RelativeSample(
                ut: 200.0, localX: 5000.0, localY: -250.0, localZ: 17.0,
                localRotation: Quaternion.identity);
            ITrajectorySample view = s;

            Assert.Equal(TrajectorySampleKind.Relative, view.Kind);
            Assert.Equal(ReferenceFrame.Relative, view.Frame);
            Assert.Equal(200.0, view.Ut);
            // Metre-scale offsets that would be NONSENSE if read as degrees - the discriminator is the
            // whole point.
            Assert.Equal(5000.0, s.LocalX);
            Assert.Equal(-250.0, s.LocalY);
            Assert.Equal(17.0, s.LocalZ);
        }

        [Fact]
        public void OrbitalState_WrapsOrbitSegment_AtItsEpoch()
        {
            var seg = new OrbitSegment
            {
                startUT = 500.0,
                endUT = 900.0,
                bodyName = "Sun",
                semiMajorAxis = 1.3e10,
                eccentricity = 0.2,
            };
            var s = OrbitalState.FromSegment(seg);
            ITrajectorySample view = s;

            Assert.Equal(TrajectorySampleKind.Orbital, view.Kind);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, view.Frame);
            Assert.Equal(500.0, view.Ut); // FromSegment uses the segment's startUT as the epoch
            Assert.Equal("Sun", s.Segment.bodyName);
            Assert.Equal(1.3e10, s.Segment.semiMajorAxis);
        }

        [Fact]
        public void OrbitalState_ExplicitUt_OverridesEpoch()
        {
            var seg = new OrbitSegment { startUT = 500.0, bodyName = "Sun" };
            var s = new OrbitalState(ut: 777.0, segment: seg);
            Assert.Equal(777.0, s.Ut);
        }

        [Fact]
        public void SampleKinds_AreDistinct()
        {
            // The three views must not collide on Kind/Frame - that distinction is what prevents the
            // mis-read.
            Assert.NotEqual(TrajectorySampleKind.Absolute, TrajectorySampleKind.Relative);
            Assert.NotEqual(TrajectorySampleKind.Relative, TrajectorySampleKind.Orbital);
            Assert.NotEqual(ReferenceFrame.Absolute, ReferenceFrame.Relative);
        }
    }
}
