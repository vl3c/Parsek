using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// B14: a probe child still near its parent ends inside a RELATIVE section, whose flat
    /// points hold anchor-local metre offsets in the latitude / longitude / altitude fields.
    /// The terminal inference read "altitude &lt; 50" off those metres as Landed and copied the
    /// raw offsets as the landing spot, so the child spawned at a nonsense position. The
    /// readers now take the section's body-fixed shadow, or refuse the point.
    /// </summary>
    [Collection("Sequential")]
    public class RelativeEndpointInferenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RelativeEndpointInferenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static TrajectoryPoint Pt(double ut, double lat, double lon, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin",
            };
        }

        // The last point is a 12 m offset from the parent (the probe sits beside it), recorded
        // in a Relative section; the earlier point is an Absolute high-altitude sample.
        private static Recording ProbeNearParent(bool withShadow, double shadowAltitude)
        {
            TrajectoryPoint absolute = Pt(100.0, -0.1, -74.5, 8000.0);
            TrajectoryPoint offset = Pt(110.0, 3.0, -4.0, 12.0);
            var rec = new Recording { RecordingId = "rec-probe" };
            rec.Points.Add(absolute);
            rec.Points.Add(offset);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.Atmospheric,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint> { absolute },
            });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.Atmospheric,
                startUT = 105.0,
                endUT = 110.0,
                frames = new List<TrajectoryPoint> { offset },
                bodyFixedFrames = withShadow
                    ? new List<TrajectoryPoint> { Pt(110.0, -0.0972, -74.557, shadowAltitude) }
                    : new List<TrajectoryPoint>(),
            });
            return rec;
        }

        // catches: metres read as altitude - a 12 m offset from a parent 6 km up read as Landed.
        [Fact]
        public void Infer_RelativeOffsetUnder50m_WithAHighShadow_IsNotLanded()
        {
            Recording rec = ProbeNearParent(withShadow: true, shadowAltitude: 6000.0);
            Assert.Equal(TerminalState.SubOrbital, ParsekFlight.InferTerminalStateFromTrajectory(rec));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("InferTerminalStateFromTrajectory: last point of 'rec-probe'")
                && l.Contains("reading its body-fixed shadow"));
        }

        [Fact]
        public void Infer_RelativeOffset_NoShadow_IsNotReadAsAnAltitude()
        {
            Recording rec = ProbeNearParent(withShadow: false, shadowAltitude: 0.0);
            Assert.Equal(TerminalState.SubOrbital, ParsekFlight.InferTerminalStateFromTrajectory(rec));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("no body-fixed shadow"));
        }

        [Fact]
        public void Infer_RelativeOffset_LowShadow_IsLanded_AndTheSpotIsTheShadow()
        {
            Recording rec = ProbeNearParent(withShadow: true, shadowAltitude: 20.0);
            TerminalState state = ParsekFlight.InferTerminalStateFromTrajectory(rec);
            Assert.Equal(TerminalState.Landed, state);

            ParsekFlight.PopulateTerminalPositionFromLastPoint(rec, state);

            Assert.True(rec.TerminalPosition.HasValue);
            Assert.Equal(-0.0972, rec.TerminalPosition.Value.latitude, 6);
            Assert.Equal(-74.557, rec.TerminalPosition.Value.longitude, 6);
            Assert.Equal(20.0, rec.TerminalPosition.Value.altitude, 6);
        }

        // catches: the raw offsets copied as the landing spot (lat 3, lon -4, alt 12).
        [Fact]
        public void PopulatePosition_RelativeOffset_NoShadow_WritesNoSpot()
        {
            Recording rec = ProbeNearParent(withShadow: false, shadowAltitude: 0.0);
            ParsekFlight.PopulateTerminalPositionFromLastPoint(rec, TerminalState.Landed);
            Assert.False(rec.TerminalPosition.HasValue);
        }

        [Fact]
        public void AbsoluteLastPoint_KeepsTheOldReading()
        {
            var rec = new Recording { RecordingId = "rec-abs" };
            TrajectoryPoint p = Pt(100.0, -0.1, -74.5, 10.0);
            rec.Points.Add(p);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 90.0,
                endUT = 100.0,
                frames = new List<TrajectoryPoint> { p },
            });
            Assert.Equal(TerminalState.Landed, ParsekFlight.InferTerminalStateFromTrajectory(rec));
            ParsekFlight.PopulateTerminalPositionFromLastPoint(rec, TerminalState.Landed);
            Assert.Equal(-74.5, rec.TerminalPosition.Value.longitude, 6);
        }

        // One Relative point under 50 m is not "real landed evidence" for the single-point
        // fallback guard either.
        [Fact]
        public void SubOrbitalFallbackGuard_SingleRelativeOffset_IsNotLandedEvidence()
        {
            TrajectoryPoint offset = Pt(110.0, 3.0, -4.0, 12.0);
            var rec = new Recording { RecordingId = "rec-one" };
            rec.Points.Add(offset);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.Atmospheric,
                startUT = 105.0,
                endUT = 110.0,
                frames = new List<TrajectoryPoint> { offset },
                bodyFixedFrames = new List<TrajectoryPoint>(),
            });
            Assert.True(ParsekFlight.HasOnlySubOrbitalFallbackEvidence(rec));
        }
    }
}
