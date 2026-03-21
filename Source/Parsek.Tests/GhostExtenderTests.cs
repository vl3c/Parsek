using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostExtenderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        // Kerbin-like body constants
        private const double KerbinRadius = 600000.0;
        private const double KerbinGM = 3.5316e12;

        // 100km circular orbit at Kerbin
        private const double CircularSMA = 700000.0;
        private const double CircularEcc = 0.0;

        // Period of 100km circular orbit: 2*pi*sqrt(a^3/GM)
        private static readonly double CircularPeriod =
            2.0 * Math.PI * Math.Sqrt(CircularSMA * CircularSMA * CircularSMA / KerbinGM);

        public GhostExtenderTests()
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

        #region ChooseStrategy

        /// <summary>
        /// Recording with TerminalOrbitBody="Kerbin" and SMA > 0 returns Orbital.
        /// Guards: orbital terminal state correctly detected.
        /// </summary>
        [Fact]
        public void ChooseStrategy_OrbitalTerminal_ReturnsOrbital()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = CircularSMA,
                TerminalOrbitEccentricity = 0.0
            };

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.Orbital, strategy);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("Orbital") &&
                l.Contains("Kerbin"));
        }

        /// <summary>
        /// Recording with TerminalPosition set returns Surface.
        /// Guards: surface terminal state correctly detected.
        /// </summary>
        [Fact]
        public void ChooseStrategy_SurfaceTerminal_ReturnsSurface()
        {
            var rec = new Recording
            {
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0972,
                    longitude = -74.5575,
                    altitude = 67.0
                }
            };

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.Surface, strategy);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("Surface"));
        }

        /// <summary>
        /// No terminal orbit or position, but has Points returns LastRecordedPosition.
        /// Guards: fallback to trajectory points.
        /// </summary>
        [Fact]
        public void ChooseStrategy_NoTerminalData_WithPoints_ReturnsLastRecorded()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 1000, latitude = 10.0, longitude = 20.0, altitude = 5000.0
                    }
                }
            };

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.LastRecordedPosition, strategy);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("LastRecordedPosition"));
        }

        /// <summary>
        /// Empty recording, no terminal data, no points returns None.
        /// Guards: empty recording handled gracefully.
        /// </summary>
        [Fact]
        public void ChooseStrategy_NoData_ReturnsNone()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>()
            };

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.None, strategy);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("None"));
        }

        /// <summary>
        /// Null recording returns None without throwing.
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void ChooseStrategy_NullRecording_ReturnsNone()
        {
            var strategy = GhostExtender.ChooseStrategy(null);

            Assert.Equal(GhostExtensionStrategy.None, strategy);
        }

        /// <summary>
        /// Orbital takes priority over surface when both are present.
        /// Guards: priority ordering.
        /// </summary>
        [Fact]
        public void ChooseStrategy_OrbitalTakesPriorityOverSurface()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = CircularSMA,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 100.0
                }
            };

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.Orbital, strategy);
        }

        /// <summary>
        /// TerminalOrbitBody set but SMA=0 falls through to Surface.
        /// Guards: incomplete orbital data does not select Orbital.
        /// </summary>
        [Fact]
        public void ChooseStrategy_OrbitalBodyButZeroSMA_ReturnsSurface()
        {
            var rec = new Recording
            {
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 0,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 5.0,
                    longitude = 10.0,
                    altitude = 200.0
                }
            };

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.Surface, strategy);
        }

        #endregion

        #region PropagateOrbital

        /// <summary>
        /// Circular orbit (ecc=0), SMA=700000, Kerbin-like body.
        /// Propagate to T+0.25*period produces different lat/lon than T+0.
        /// Guards: frozen position bug.
        /// </summary>
        [Fact]
        public void PropagateOrbital_CircularOrbit_PositionChangesWithUT()
        {
            double epoch = 1000.0;
            double inc = 0.0;
            double lan = 0.0;
            double argPe = 0.0;
            double mAe = 0.0;

            var pos0 = GhostExtender.PropagateOrbital(
                inc, CircularEcc, CircularSMA, lan, argPe, mAe, epoch,
                KerbinRadius, KerbinGM, epoch);

            var posQuarter = GhostExtender.PropagateOrbital(
                inc, CircularEcc, CircularSMA, lan, argPe, mAe, epoch,
                KerbinRadius, KerbinGM, epoch + CircularPeriod * 0.25);

            // Position should have changed
            bool latChanged = Math.Abs(posQuarter.lat - pos0.lat) > 0.01;
            bool lonChanged = Math.Abs(posQuarter.lon - pos0.lon) > 0.01;
            Assert.True(latChanged || lonChanged,
                $"Position should change after quarter period. " +
                $"pos0=({pos0.lat:F4},{pos0.lon:F4}) posQ=({posQuarter.lat:F4},{posQuarter.lon:F4})");
        }

        /// <summary>
        /// Circular orbit at 100km altitude produces altitude always near 100000m.
        /// Guards: altitude varies wildly (math bug).
        /// </summary>
        [Fact]
        public void PropagateOrbital_CircularOrbit_AltitudeConsistent()
        {
            double epoch = 1000.0;
            double inc = 0.5; // ~28.6 deg
            double lan = 1.0;
            double argPe = 0.0;
            double mAe = 0.0;
            double expectedAlt = CircularSMA - KerbinRadius; // 100000

            // Sample at 10 equally spaced points around the orbit
            for (int i = 0; i < 10; i++)
            {
                double ut = epoch + CircularPeriod * i / 10.0;
                var pos = GhostExtender.PropagateOrbital(
                    inc, CircularEcc, CircularSMA, lan, argPe, mAe, epoch,
                    KerbinRadius, KerbinGM, ut);

                // For circular orbit, altitude should be constant within floating point tolerance
                Assert.InRange(pos.alt, expectedAlt - 1.0, expectedAlt + 1.0);
            }
        }

        /// <summary>
        /// Elliptical orbit, ecc=0.3, SMA=800000.
        /// Altitude should be between periapsis and apoapsis altitudes.
        /// Guards: negative altitude or NaN.
        /// </summary>
        [Fact]
        public void PropagateOrbital_EllipticalOrbit_AltitudeInRange()
        {
            double sma = 800000.0;
            double ecc = 0.3;
            double inc = 0.5;
            double lan = 0.5;
            double argPe = 1.0;
            double mAe = 0.0;
            double epoch = 1000.0;

            double periAlt = sma * (1.0 - ecc) - KerbinRadius; // 560000 - 600000 = -40000 (suborbital at pe)
            double apoAlt = sma * (1.0 + ecc) - KerbinRadius;  // 1040000 - 600000 = 440000

            // Use wider bounds for numerical safety
            double minAlt = periAlt - 100.0;
            double maxAlt = apoAlt + 100.0;

            double period = 2.0 * Math.PI * Math.Sqrt(sma * sma * sma / KerbinGM);

            for (int i = 0; i < 20; i++)
            {
                double ut = epoch + period * i / 20.0;
                var pos = GhostExtender.PropagateOrbital(
                    inc, ecc, sma, lan, argPe, mAe, epoch,
                    KerbinRadius, KerbinGM, ut);

                Assert.False(double.IsNaN(pos.alt),
                    $"Altitude is NaN at sample {i}");
                Assert.False(double.IsInfinity(pos.alt),
                    $"Altitude is Infinity at sample {i}");
                Assert.InRange(pos.alt, minAlt, maxAlt);
            }
        }

        /// <summary>
        /// Propagate exactly one full period returns to same (lat,lon,alt) as epoch.
        /// Guards: drift accumulation.
        /// </summary>
        [Fact]
        public void PropagateOrbital_FullPeriod_ReturnsToStart()
        {
            double epoch = 1000.0;
            double inc = 0.8;
            double lan = 1.2;
            double argPe = 0.5;
            double mAe = 0.3;

            var pos0 = GhostExtender.PropagateOrbital(
                inc, CircularEcc, CircularSMA, lan, argPe, mAe, epoch,
                KerbinRadius, KerbinGM, epoch);

            var pos1 = GhostExtender.PropagateOrbital(
                inc, CircularEcc, CircularSMA, lan, argPe, mAe, epoch,
                KerbinRadius, KerbinGM, epoch + CircularPeriod);

            // After exactly one period, should return to same position
            // Tolerance: 0.01 degrees (~11m at equator) and 1m altitude
            Assert.InRange(pos1.lat, pos0.lat - 0.01, pos0.lat + 0.01);
            Assert.InRange(pos1.alt, pos0.alt - 1.0, pos0.alt + 1.0);

            // Longitude comparison needs wrapping awareness
            double lonDiff = Math.Abs(pos1.lon - pos0.lon);
            if (lonDiff > 180.0) lonDiff = 360.0 - lonDiff;
            Assert.True(lonDiff < 0.01,
                $"Longitude drift after full period: {lonDiff:F6} degrees " +
                $"(pos0.lon={pos0.lon:F6}, pos1.lon={pos1.lon:F6})");
        }

        /// <summary>
        /// Elliptical orbit also returns to start after one period.
        /// Guards: eccentricity-dependent drift.
        /// </summary>
        [Fact]
        public void PropagateOrbital_EllipticalFullPeriod_ReturnsToStart()
        {
            double sma = 800000.0;
            double ecc = 0.3;
            double inc = 0.5;
            double lan = 0.5;
            double argPe = 1.0;
            double mAe = 0.5;
            double epoch = 1000.0;

            double period = 2.0 * Math.PI * Math.Sqrt(sma * sma * sma / KerbinGM);

            var pos0 = GhostExtender.PropagateOrbital(
                inc, ecc, sma, lan, argPe, mAe, epoch,
                KerbinRadius, KerbinGM, epoch);

            var pos1 = GhostExtender.PropagateOrbital(
                inc, ecc, sma, lan, argPe, mAe, epoch,
                KerbinRadius, KerbinGM, epoch + period);

            Assert.InRange(pos1.lat, pos0.lat - 0.01, pos0.lat + 0.01);
            Assert.InRange(pos1.alt, pos0.alt - 1.0, pos0.alt + 1.0);

            double lonDiff = Math.Abs(pos1.lon - pos0.lon);
            if (lonDiff > 180.0) lonDiff = 360.0 - lonDiff;
            Assert.True(lonDiff < 0.01,
                $"Longitude drift after full period: {lonDiff:F6} degrees");
        }

        /// <summary>
        /// Equatorial circular orbit: latitude stays near zero at all times.
        /// Guards: inclination handling for equatorial orbits.
        /// </summary>
        [Fact]
        public void PropagateOrbital_EquatorialOrbit_LatitudeNearZero()
        {
            double epoch = 1000.0;
            double inc = 0.0; // equatorial
            double lan = 0.0;
            double argPe = 0.0;
            double mAe = 0.0;

            for (int i = 0; i < 10; i++)
            {
                double ut = epoch + CircularPeriod * i / 10.0;
                var pos = GhostExtender.PropagateOrbital(
                    inc, CircularEcc, CircularSMA, lan, argPe, mAe, epoch,
                    KerbinRadius, KerbinGM, ut);

                Assert.InRange(pos.lat, -0.01, 0.01);
            }
        }

        /// <summary>
        /// Polar orbit: latitude reaches near +/-90 degrees.
        /// Guards: polar orbit produces valid lat range.
        /// </summary>
        [Fact]
        public void PropagateOrbital_PolarOrbit_LatitudeReachesHighValues()
        {
            double epoch = 1000.0;
            double inc = Math.PI / 2.0; // 90 deg polar
            double lan = 0.0;
            double argPe = 0.0;
            double mAe = 0.0;

            double maxLat = 0.0;

            for (int i = 0; i < 100; i++)
            {
                double ut = epoch + CircularPeriod * i / 100.0;
                var pos = GhostExtender.PropagateOrbital(
                    inc, CircularEcc, CircularSMA, lan, argPe, mAe, epoch,
                    KerbinRadius, KerbinGM, ut);

                if (Math.Abs(pos.lat) > maxLat)
                    maxLat = Math.Abs(pos.lat);
            }

            // Polar orbit should reach at least 85 degrees latitude
            Assert.True(maxLat > 85.0,
                $"Polar orbit max latitude was only {maxLat:F2} degrees");
        }

        /// <summary>
        /// Hyperbolic orbit (ecc=1.5, sma=800000) triggers the guard at the top
        /// of PropagateOrbital and returns the fallback position with valid (non-NaN) values.
        /// Guards: hyperbolic orbit guard added in 6c review fix.
        /// </summary>
        [Fact]
        public void PropagateOrbital_HyperbolicOrbit_ReturnsFallback()
        {
            double ecc = 1.5;
            double sma = 800000.0;
            double inc = 0.5;
            double lan = 0.5;
            double argPe = 1.0;
            double mAe = 0.0;
            double epoch = 1000.0;

            var pos = GhostExtender.PropagateOrbital(
                inc, ecc, sma, lan, argPe, mAe, epoch,
                KerbinRadius, KerbinGM, epoch + 500.0);

            // Should return fallback values, not NaN
            Assert.False(double.IsNaN(pos.lat), "Latitude is NaN for hyperbolic orbit");
            Assert.False(double.IsNaN(pos.lon), "Longitude is NaN for hyperbolic orbit");
            Assert.False(double.IsNaN(pos.alt), "Altitude is NaN for hyperbolic orbit");

            // Fallback: lat=0, lon=0, alt=sma-bodyRadius
            Assert.Equal(0.0, pos.lat);
            Assert.Equal(0.0, pos.lon);
            Assert.Equal(sma - KerbinRadius, pos.alt);

            // Verify the guard logged
            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("unsupported orbit"));
        }

        /// <summary>
        /// Backward propagation (currentUT before epoch) also works.
        /// Guards: negative dt does not produce NaN or crash.
        /// </summary>
        [Fact]
        public void PropagateOrbital_BackwardPropagation_NoNaN()
        {
            double epoch = 10000.0;
            double inc = 0.5;
            double lan = 0.5;
            double argPe = 0.5;
            double mAe = 0.5;

            var pos = GhostExtender.PropagateOrbital(
                inc, CircularEcc, CircularSMA, lan, argPe, mAe, epoch,
                KerbinRadius, KerbinGM, epoch - 500.0);

            Assert.False(double.IsNaN(pos.lat), "Latitude is NaN");
            Assert.False(double.IsNaN(pos.lon), "Longitude is NaN");
            Assert.False(double.IsNaN(pos.alt), "Altitude is NaN");
        }

        #endregion

        #region PropagateSurface

        /// <summary>
        /// Recording with TerminalPosition returns those exact values.
        /// Guards: surface position passthrough.
        /// </summary>
        [Fact]
        public void PropagateSurface_ReturnsTerminalPosition()
        {
            var rec = new Recording
            {
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0972,
                    longitude = -74.5575,
                    altitude = 67.0
                }
            };

            var (lat, lon, alt) = GhostExtender.PropagateSurface(rec);

            Assert.Equal(-0.0972, lat, 6);
            Assert.Equal(-74.5575, lon, 6);
            Assert.Equal(67.0, alt, 6);
        }

        /// <summary>
        /// No TerminalPosition but has Points falls back to last point's position.
        /// Guards: fallback path.
        /// </summary>
        [Fact]
        public void PropagateSurface_NoTerminalPosition_FallsBack()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 1000, latitude = 5.0, longitude = 15.0, altitude = 200.0
                    },
                    new TrajectoryPoint
                    {
                        ut = 1010, latitude = 6.0, longitude = 16.0, altitude = 250.0
                    }
                }
            };

            var (lat, lon, alt) = GhostExtender.PropagateSurface(rec);

            // Should return last point's values
            Assert.Equal(6.0, lat, 6);
            Assert.Equal(16.0, lon, 6);
            Assert.Equal(250.0, alt, 6);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("falling back"));
        }

        /// <summary>
        /// Null recording returns (0,0,0) without throwing.
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void PropagateSurface_NullRecording_ReturnsZero()
        {
            var (lat, lon, alt) = GhostExtender.PropagateSurface(null);

            Assert.Equal(0, lat);
            Assert.Equal(0, lon);
            Assert.Equal(0, alt);
        }

        #endregion

        #region LastRecordedPosition

        /// <summary>
        /// Recording with Points returns last point's lat/lon/alt.
        /// Guards: correct point selection.
        /// </summary>
        [Fact]
        public void LastRecordedPosition_ReturnsLastPoint()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 1000, latitude = 1.0, longitude = 2.0, altitude = 100.0
                    },
                    new TrajectoryPoint
                    {
                        ut = 1010, latitude = 3.0, longitude = 4.0, altitude = 200.0
                    },
                    new TrajectoryPoint
                    {
                        ut = 1020, latitude = 5.0, longitude = 6.0, altitude = 300.0
                    }
                }
            };

            var (lat, lon, alt) = GhostExtender.LastRecordedPosition(rec);

            Assert.Equal(5.0, lat, 6);
            Assert.Equal(6.0, lon, 6);
            Assert.Equal(300.0, alt, 6);
        }

        /// <summary>
        /// Empty points returns (0,0,0) without throwing.
        /// Guards: empty points safety.
        /// </summary>
        [Fact]
        public void LastRecordedPosition_EmptyPoints_ReturnsZero()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>()
            };

            var (lat, lon, alt) = GhostExtender.LastRecordedPosition(rec);

            Assert.Equal(0, lat);
            Assert.Equal(0, lon);
            Assert.Equal(0, alt);
        }

        /// <summary>
        /// Null recording returns (0,0,0) without throwing.
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void LastRecordedPosition_NullRecording_ReturnsZero()
        {
            var (lat, lon, alt) = GhostExtender.LastRecordedPosition(null);

            Assert.Equal(0, lat);
            Assert.Equal(0, lon);
            Assert.Equal(0, alt);
        }

        #endregion

        #region Log assertion tests

        /// <summary>
        /// ChooseStrategy logs the chosen strategy.
        /// Guards: decision logging.
        /// </summary>
        [Fact]
        public void ChooseStrategy_LogsDecision()
        {
            logLines.Clear();

            var rec = new Recording
            {
                TerminalOrbitBody = "Mun",
                TerminalOrbitSemiMajorAxis = 300000.0
            };

            GhostExtender.ChooseStrategy(rec);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("Orbital") &&
                l.Contains("Mun"));
        }

        /// <summary>
        /// LastRecordedPosition logs the returned position.
        /// Guards: fallback logging.
        /// </summary>
        [Fact]
        public void LastRecordedPosition_LogsPosition()
        {
            logLines.Clear();

            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 2000, latitude = 12.5, longitude = -45.3, altitude = 1500.0
                    }
                }
            };

            GhostExtender.LastRecordedPosition(rec);

            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("LastRecordedPosition") &&
                l.Contains("UT=2000"));
        }

        #endregion
    }
}
