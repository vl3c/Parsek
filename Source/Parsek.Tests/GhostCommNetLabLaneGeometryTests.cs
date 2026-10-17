using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The derivation behind LAB-11's preset (SyntheticRecordingTests.GhostCommNetLabPreset):
    /// a live science lab in low Kerbin orbit whose ONLY path home, at the window press, is one
    /// ghost relay, and which has no path at all at the negative-control press. Pinned against
    /// the committed lab-lko-relay bytes, so a wrong constant reds here instead of in a flight:
    /// <list type="bullet">
    ///   <item><description>the host: the lab's ORBIT node, its saved latitude / altitude /
    ///       longitude (the Kerbin rotation calibration CN-1 pins on other fixtures must map the
    ///       lab back onto its own saved longitude), the save clock, and the CommNet parameters
    ///       (ground stations off, so the KSC is the only home; occluder = Kerbin's full
    ///       radius);</description></item>
    ///   <item><description>the geometry, with stock's horizon-culling occluder test
    ///       (decompiled <c>CommNet.Occluders.OccluderHorizonCulling.Raycast</c>) replicated:
    ///       around the control press neither the KSC nor the relay sees the lab; around the
    ///       window press the KSC does not see the lab, the relay sees both, and every link is
    ///       well inside range; each with at least two minutes of margin on both
    ///       sides;</description></item>
    ///   <item><description>the preset's production shape (one RA-100 relay, its window open at
    ///       both presses, eligible <c>in-window</c>) and the spec's WarpToUT literals.</description></item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class GhostCommNetLabLaneGeometryTests : IDisposable
    {
        private const string Fixture = "lab-lko-relay";
        private const string SpecFile = "LAB-11-lab-transmit-ghost-relay.toml";
        /// <summary>SCIENCE_SANDBOX facilities read as maxed: GameVariables.GetDSNRange(1) = 2.5e11.</summary>
        private const double KscDsnPower = 2.5e11;
        private const double RaHundredPower = 1e11;
        /// <summary>The lab's Communotron 16-S (DIRECT 5e5), its strongest antenna.</summary>
        private const double SixteenSPower = 5e5;
        private const double RangeModifier = 0.65;
        /// <summary>occlusionMultiplierAtm = 1 in the fixture: the occluder is Kerbin's full radius.</summary>
        private const double KerbinOccluderRadius = StockEphemeris.KerbinRadius;
        /// <summary>The KSC home sits at the pad's surface position (the CN-1 calibration's KSC meridian).</summary>
        private const double KscLatDeg = -0.0972;
        private const double KscAltitude = 70.0;
        /// <summary>Settle at 1x before each press (spec literal: press - 5).</summary>
        private const double SettleSeconds = 5.0;
        /// <summary>Margin required between a checked interval and the edge of its window.</summary>
        private const double RequiredMarginSeconds = 120.0;
        /// <summary>Time after the post-press wait the lab is still read (the inspect step).</summary>
        private const double ReadAfterWaitSeconds = 30.0;

        private readonly List<string> logLines = new List<string>();

        public GhostCommNetLabLaneGeometryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------ geometry model

        private static double Rad(double deg) => deg * Math.PI / 180.0;

        private static FixtureVec3 BodyFixed(double latDeg, double lonDeg, double radius)
        {
            double la = Rad(latDeg), lo = Rad(lonDeg);
            return new FixtureVec3(radius * Math.Cos(la) * Math.Cos(lo), radius * Math.Cos(la) * Math.Sin(lo),
                radius * Math.Sin(la));
        }

        /// <summary>A Kerbin element-frame position at a UT, rotated into the body-fixed frame.</summary>
        private static FixtureVec3 ToBodyFixed(FixtureVec3 elementFrame, double ut)
        {
            double lon = SyntheticRecordingTests.GhostCommNetSurfaceLonOfElementAngle(
                StockEphemeris.ElementAngleDeg(elementFrame), ut);
            return BodyFixed(StockEphemeris.LatitudeDeg(elementFrame), lon, elementFrame.Magnitude);
        }

        /// <summary>
        /// Stock's horizon-culling occluder test for a sphere centred at the origin, transcribed
        /// from the decompiled <c>OccluderHorizonCulling.Raycast</c> (KSP 1.12.5): the nearer
        /// endpoint is the observer; the far one is hidden when it lies past the observer's
        /// horizon plane and inside the cone the sphere subtends.
        /// </summary>
        internal static bool Occluded(FixtureVec3 a, FixtureVec3 b, double radius)
        {
            FixtureVec3 source = a * (1.0 / radius), dest = b * (1.0 / radius);
            if (source.Dot(source) > dest.Dot(dest))
            {
                FixtureVec3 t = source;
                source = dest;
                dest = t;
            }
            double num = source.Dot(source) - 1.0;
            FixtureVec3 lhs = dest - source;
            double num2 = -lhs.Dot(source);
            if (num < 0.0)
                return num2 > 0.0;
            return num2 > num && num2 * num2 / lhs.Dot(lhs) > num;
        }

        private struct Links
        {
            public bool LabKsc, LabRelay, RelayKsc;
            public double LabRelayDistance, RelayKscDistance;
        }

        private static readonly FixtureVec3 Ksc = BodyFixed(KscLatDeg, SyntheticRecordingTests.GhostCommNetKscLon,
            StockEphemeris.KerbinRadius + KscAltitude);

        private static FixtureOrbitElements RelayElements(double save)
        {
            OrbitSegment seg = MaterializeRelay(save).OrbitSegments[0];
            return new FixtureOrbitElements
            {
                Sma = seg.semiMajorAxis, Ecc = seg.eccentricity, IncDeg = seg.inclination,
                LanDeg = seg.longitudeOfAscendingNode, ArgPeDeg = seg.argumentOfPeriapsis,
                MnaRad = seg.meanAnomalyAtEpoch, Epoch = seg.epoch,
            };
        }

        private static Recording MaterializeRelay(double save)
            => SyntheticRecordingTests.MaterializeSpawnSafetyRecording(
                SyntheticRecordingTests.GhostCommNetLabPreset(save)[0],
                SyntheticRecordingTests.GhostCommNetLabRelayRecordingId);

        private static Links LinksAt(double ut, FixtureOrbitElements relay)
        {
            FixtureVec3 lab = ToBodyFixed(StockEphemeris.Position(SyntheticRecordingTests.LabLkoRelayOrbit,
                StockEphemeris.KerbinGravParameter, ut), ut);
            FixtureVec3 rel = ToBodyFixed(StockEphemeris.Position(relay, StockEphemeris.KerbinGravParameter, ut), ut);
            return new Links
            {
                LabKsc = !Occluded(lab, Ksc, KerbinOccluderRadius),
                LabRelay = !Occluded(lab, rel, KerbinOccluderRadius),
                RelayKsc = !Occluded(rel, Ksc, KerbinOccluderRadius),
                LabRelayDistance = (lab - rel).Magnitude,
                RelayKscDistance = (rel - Ksc).Magnitude,
            };
        }

        /// <summary>
        /// The run of whole seconds around <paramref name="ut"/> for which
        /// <paramref name="holds"/> stays true, as [first, last]; scans up to 3,000 s each way.
        /// </summary>
        private static (double first, double last) WindowAround(double ut, Func<double, bool> holds)
        {
            Assert.True(holds(ut), "the predicate does not hold at UT " + ut.ToString("R", CultureInfo.InvariantCulture));
            double first = ut, last = ut;
            while (first - 1.0 > ut - 3000.0 && holds(first - 1.0)) first -= 1.0;
            while (last + 1.0 < ut + 3000.0 && holds(last + 1.0)) last += 1.0;
            return (first, last);
        }

        // ------------------------------------------------------------------ host pins

        private static string ReadSave()
        {
            string path = Path.Combine(SyntheticRecordingTests.ProjectRoot, "harness", "fixtures", "saves",
                Fixture, "persistent.sfs");
            Assert.True(File.Exists(path), "fixture save missing: " + path);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        [Fact]
        public void Host_TheConstantsAreTheFixturesOwnBytes()
        {
            string save = ReadSave();
            string block = SyntheticRecordingTests.ReadFixtureVesselBlock(Fixture,
                SyntheticRecordingTests.LabLkoRelayVesselName);
            FixtureOrbitElements o = SyntheticRecordingTests.LabLkoRelayOrbit;
            Assert.Equal(o.Sma, SyntheticRecordingTests.ReadOrbitValue(block, "SMA"));
            Assert.Equal(o.Ecc, SyntheticRecordingTests.ReadOrbitValue(block, "ECC"));
            Assert.Equal(o.IncDeg, SyntheticRecordingTests.ReadOrbitValue(block, "INC"));
            Assert.Equal(o.LanDeg, SyntheticRecordingTests.ReadOrbitValue(block, "LAN"));
            Assert.Equal(o.ArgPeDeg, SyntheticRecordingTests.ReadOrbitValue(block, "LPE"));
            Assert.Equal(o.MnaRad, SyntheticRecordingTests.ReadOrbitValue(block, "MNA"));
            Assert.Equal(o.Epoch, SyntheticRecordingTests.ReadOrbitValue(block, "EPH"));
            Assert.Equal(1.0, SyntheticRecordingTests.ReadOrbitValue(block, "REF"));
            Assert.Equal(SyntheticRecordingTests.LabLkoRelaySavedLon, SyntheticRecordingTests.ReadVesselValue(block, "lon"));
            Assert.Contains("\n\t\t\tsit = ORBITING\n", "\n" + block);
            // The lab carries no relay antenna of its own: it is the endpoint, never a hop.
            Assert.DoesNotContain("name = RelayAntenna", block);
            Assert.DoesNotContain("name = HighGainAntenna", block);
            Assert.Contains("\t\t\t\tname = SurfAntenna\n", block);

            // The save's clock, its single (active) vessel, its mode.
            Match ut = Regex.Match(save, @"FLIGHTSTATE\n\t\{\n\t\tversion = [^\n]+\n\t\tUT = ([^\n]+)\n\t\tactiveVessel = (\d+)");
            Assert.True(ut.Success);
            Assert.Equal(SyntheticRecordingTests.LabLkoRelaySaveUT, double.Parse(ut.Groups[1].Value, CultureInfo.InvariantCulture));
            Assert.Equal("0", ut.Groups[2].Value);
            Assert.Single(Regex.Matches(save, @"\n\t\tVESSEL\n").Cast<Match>());
            Assert.Contains("\n\tMode = SCIENCE_SANDBOX\n", save);

            // CommNet: on, ground stations OFF (the KSC is the only home, AllowOtherLaunchSites
            // being False), the occluder Kerbin's full radius, the base's range modifier.
            Assert.Contains("\t\t\tEnableCommNet = True\n", save);
            Assert.Contains("\t\t\tenableGroundStations = False\n", save);
            Assert.DoesNotContain("enableGroundStations = True", save);
            Assert.Contains("\t\t\tAllowOtherLaunchSites = False\n", save);
            Assert.Contains("\t\t\tocclusionMultiplierAtm = 1\n", save);
            Assert.Contains("\t\t\trangeModifier = 0.65\n", save);
            Assert.Contains("\t\t\tDSNModifier = 1\n", save);

            // The lab holds the seeded 12.5 science with research off.
            Assert.Contains("\t\t\t\t\tstoredScience = 12.5\n", block);
        }

        [Fact]
        public void Host_TheRotationCalibrationMapsTheLabOntoItsOwnSavedPosition()
        {
            // CN-1 pins the calibration on three other fixtures; here it must also reproduce THIS
            // lab's saved longitude from its ORBIT node, plus the latitude and altitude, which
            // do not depend on the rotation and so check the element convention on their own.
            string block = SyntheticRecordingTests.ReadFixtureVesselBlock(Fixture,
                SyntheticRecordingTests.LabLkoRelayVesselName);
            double save = SyntheticRecordingTests.LabLkoRelaySaveUT;
            FixtureVec3 p = StockEphemeris.Position(SyntheticRecordingTests.LabLkoRelayOrbit,
                StockEphemeris.KerbinGravParameter, save);
            double lon = SyntheticRecordingTests.GhostCommNetSurfaceLonOfElementAngle(StockEphemeris.ElementAngleDeg(p), save);
            double lonError = Math.Abs(StockEphemeris.Wrap180(lon - SyntheticRecordingTests.LabLkoRelaySavedLon));
            Assert.True(lonError < 0.002, "lab longitude off by " + lonError.ToString("R", CultureInfo.InvariantCulture) + " deg");
            Assert.True(Math.Abs(StockEphemeris.LatitudeDeg(p) - SyntheticRecordingTests.ReadVesselValue(block, "lat")) < 0.002);
            Assert.True(Math.Abs(p.Magnitude - StockEphemeris.KerbinRadius - SyntheticRecordingTests.ReadVesselValue(block, "alt")) < 5.0);
        }

        // ------------------------------------------------------------------ the occluder replica

        [Fact]
        public void Occluder_TheReplicaIsTheTangentGeometryForASphere()
        {
            // From 700 km radius the horizon of a 600 km sphere is arccos(6/7) = 31.0 deg away:
            // a surface point 30 deg off is seen, 32 deg off is not.
            double r = 600000.0;
            FixtureVec3 observer = BodyFixed(0, 0, 700000.0);
            Assert.False(Occluded(observer, BodyFixed(0, 30.0, r + 1.0), r));
            Assert.True(Occluded(observer, BodyFixed(0, 32.0, r + 1.0), r));
            // Two 700 km-radius points 120 deg apart clear the sphere (midpoint 350 km < 600 km:
            // no), 60 deg apart (midpoint 606 km) do.
            Assert.True(Occluded(observer, BodyFixed(0, 120.0, 700000.0), r));
            Assert.False(Occluded(observer, BodyFixed(0, 60.0, 700000.0), r));
            // Symmetric in its endpoints.
            Assert.Equal(Occluded(observer, BodyFixed(0, 61.0, 3463000.0), r),
                Occluded(BodyFixed(0, 61.0, 3463000.0), observer, r));
        }

        // ------------------------------------------------------------------ LAB-11 geometry

        [Fact]
        public void Geometry_TheControlPressHasNoPathHomeAtAll()
        {
            double save = SyntheticRecordingTests.LabLkoRelaySaveUT;
            FixtureOrbitElements relay = RelayElements(save);
            double press = SyntheticRecordingTests.GhostCommNetLabControlPressUT;
            var w = WindowAround(press, t => { Links l = LinksAt(t, relay); return !l.LabKsc && !l.LabRelay; });
            double from = press - SettleSeconds;
            double to = press + SyntheticRecordingTests.GhostCommNetLabPressWaitSeconds + ReadAfterWaitSeconds;
            Assert.True(from - w.first >= RequiredMarginSeconds,
                "no-path window opens at " + w.first.ToString("F0", CultureInfo.InvariantCulture) + ", too close to " + from.ToString("F0", CultureInfo.InvariantCulture));
            Assert.True(w.last - to >= RequiredMarginSeconds,
                "no-path window closes at " + w.last.ToString("F0", CultureInfo.InvariantCulture) + ", too close to " + to.ToString("F0", CultureInfo.InvariantCulture));
            // The relay itself still sees the KSC there: only the lab's leg is missing.
            Assert.True(LinksAt(press, relay).RelayKsc);
        }

        [Fact]
        public void Geometry_TheWindowPressReachesHomeOnlyThroughTheRelay()
        {
            double save = SyntheticRecordingTests.LabLkoRelaySaveUT;
            FixtureOrbitElements relay = RelayElements(save);
            double press = SyntheticRecordingTests.GhostCommNetLabWindowPressUT;
            var w = WindowAround(press, t =>
            {
                Links l = LinksAt(t, relay);
                return !l.LabKsc && l.LabRelay && l.RelayKsc;
            });
            double from = press - SettleSeconds;
            double to = press + SyntheticRecordingTests.GhostCommNetLabPressWaitSeconds + ReadAfterWaitSeconds;
            Assert.True(from - w.first >= RequiredMarginSeconds,
                "relay-only window opens at " + w.first.ToString("F0", CultureInfo.InvariantCulture) + ", too close to " + from.ToString("F0", CultureInfo.InvariantCulture));
            Assert.True(w.last - to >= RequiredMarginSeconds,
                "relay-only window closes at " + w.last.ToString("F0", CultureInfo.InvariantCulture) + ", too close to " + to.ToString("F0", CultureInfo.InvariantCulture));

            // Every link far inside range across the checked interval (stock: range =
            // sqrt(powerA * powerB), each power scaled by the range modifier, the DSN by
            // DSNModifier = 1): the lab - relay leg at about 3 % of its range, the relay - KSC
            // leg at under 0.01 %.
            double labRelayRange = Math.Sqrt(SixteenSPower * RangeModifier * RaHundredPower * RangeModifier);
            double relayKscRange = Math.Sqrt(RaHundredPower * RangeModifier * KscDsnPower);
            for (double t = from; t <= to; t += 1.0)
            {
                Links l = LinksAt(t, relay);
                Assert.True(l.LabRelayDistance < 0.1 * labRelayRange);
                Assert.True(l.RelayKscDistance < 0.01 * relayKscRange);
            }
        }

        [Fact]
        public void Geometry_TheLabsDirectLinkIsTheOnlyOtherWayHomeAndItIsOccludedAtBothPresses()
        {
            // With ground stations off and no other vessel in the save, the lab has exactly two
            // candidate paths home: direct to the KSC, or through the relay ghost. The direct leg
            // must be occluded at both presses with margin, so a credit at the window press can
            // only have travelled through the ghost.
            double save = SyntheticRecordingTests.LabLkoRelaySaveUT;
            FixtureOrbitElements relay = RelayElements(save);
            foreach (double press in new[] { SyntheticRecordingTests.GhostCommNetLabControlPressUT,
                SyntheticRecordingTests.GhostCommNetLabWindowPressUT })
            {
                var w = WindowAround(press, t => !LinksAt(t, relay).LabKsc);
                Assert.True(press - SettleSeconds - w.first >= RequiredMarginSeconds);
                Assert.True(w.last - (press + SyntheticRecordingTests.GhostCommNetLabPressWaitSeconds + ReadAfterWaitSeconds)
                    >= RequiredMarginSeconds);
            }
        }

        // ------------------------------------------------------------------ the preset

        [Fact]
        public void Preset_OneRelayOnItsSynchronousStationOpenAtBothPresses()
        {
            double save = SyntheticRecordingTests.LabLkoRelaySaveUT;
            RecordingBuilder[] preset = SyntheticRecordingTests.GhostCommNetLabPreset(save);
            Assert.Single(preset);
            Recording r = MaterializeRelay(save);
            Assert.Single(r.Points);
            Assert.Single(r.OrbitSegments);
            Assert.Equal(SyntheticRecordingTests.GhostCommNetLabRelayRecordingId, r.RecordingId);
            Assert.Equal(SyntheticRecordingTests.GhostCommNetLabRelayName, r.VesselName);
            Assert.Equal(TerminalState.Orbiting, r.TerminalStateValue);
            Assert.NotNull(r.VesselSnapshot);

            var parts = new List<string>();
            var crew = new List<string>();
            foreach (ConfigNode part in preset[0].GetVesselSnapshot().GetNodes("PART"))
            {
                parts.Add(part.GetValue("name"));
                crew.AddRange(part.GetValues("crew"));
            }
            Assert.Equal(new[] { "probeStackLarge", "RelayAntenna100" }, parts);
            Assert.Empty(crew);

            // The start point sits on the relay's station, 50 deg west of the KSC, and the orbit
            // keeps it there through the window press.
            double start = save + SyntheticRecordingTests.GhostCommNetLabStartOffsetSeconds;
            Assert.Equal(start, r.Points[0].ut, 6);
            double station = StockEphemeris.Wrap180(SyntheticRecordingTests.GhostCommNetKscLon
                + SyntheticRecordingTests.GhostCommNetLabRelayLonOffsetDeg);
            Assert.True(Math.Abs(StockEphemeris.Wrap180(r.Points[0].longitude - station)) < 1e-9);
            FixtureOrbitElements el = RelayElements(save);
            foreach (double t in new[] { start, SyntheticRecordingTests.GhostCommNetLabWindowPressUT + 100.0 })
            {
                FixtureVec3 p = StockEphemeris.Position(el, StockEphemeris.KerbinGravParameter, t);
                double lon = SyntheticRecordingTests.GhostCommNetSurfaceLonOfElementAngle(StockEphemeris.ElementAngleDeg(p), t);
                Assert.True(Math.Abs(StockEphemeris.Wrap180(lon - station)) < 0.01,
                    "relay drifted to lon " + lon.ToString("R", CultureInfo.InvariantCulture));
            }

            // The window: opens 1 s after the save, runs far past the lane, eligible at both presses.
            double activation = GhostPlaybackEngine.ResolveGhostActivationStartUT(r);
            Assert.Equal(start, activation, 6);
            Assert.True(r.EndUT > SyntheticRecordingTests.GhostCommNetLabWindowPressUT + 50000.0);
            try
            {
                PlaybackScopeTracker.NotePlayhead(r.RecordingId, save, activation);
                foreach (double press in new[] { SyntheticRecordingTests.GhostCommNetLabControlPressUT,
                    SyntheticRecordingTests.GhostCommNetLabWindowPressUT })
                {
                    var input = new GhostCommNetEligibilityInput
                    {
                        HasRecordingId = true,
                        HasRenderableData = GhostPlaybackEngine.HasRenderableGhostData(r),
                        HistoricalNeverReplayed = GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                            r.LoopPlayback, false,
                            PlaybackScopeTracker.IsHistoricalNeverReplayed(r.RecordingId, press, activation),
                            forSpawn: true),
                        ActivationStartUT = activation,
                        EndUT = r.EndUT,
                    };
                    Assert.Equal("in-window", GhostCommNetMath.EvaluateEligibility(input, press).Reason);
                }
                Assert.False(RecordingOptimizer.TrimBoringTail(r, new List<Recording> { r }));
            }
            finally
            {
                PlaybackScopeTracker.ResetForTesting();
            }
        }

        // ------------------------------------------------------------------ spec literals

        [Fact]
        public void Spec_WarpTargetsAreThePinnedDerivations()
        {
            string path = Path.Combine(SyntheticRecordingTests.ProjectRoot, "harness", "scenarios", SpecFile);
            Assert.True(File.Exists(path), "spec missing: " + path);
            string spec = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Contains("injectedRecordings = \"ghost-commnet-lab\"", spec);
            Assert.Contains("saveTemplate = \"fixtures/saves/" + Fixture + "\"", spec);
            var targets = new List<double>();
            foreach (Match m in Regex.Matches(spec, @"\n\s*\{ cmd = ""WarpToUT"",\s*args = \{ ut = ""([0-9.]+)"""))
                targets.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            double c = SyntheticRecordingTests.GhostCommNetLabControlPressUT;
            double w = SyntheticRecordingTests.GhostCommNetLabWindowPressUT;
            double wait = SyntheticRecordingTests.GhostCommNetLabPressWaitSeconds;
            Assert.Contains(c - SettleSeconds, targets);
            Assert.Contains(c, targets);
            Assert.Contains(c + wait, targets);
            Assert.Contains(w - SettleSeconds, targets);
            Assert.Contains(w, targets);
            Assert.Contains(w + wait, targets);
            // Nothing in between the control read and the window approach lingers near an edge:
            // every target is either before the control settle or one of the six above.
            foreach (double t in targets)
                Assert.True(t < c - SettleSeconds || new[] { c - SettleSeconds, c, c + wait, w - SettleSeconds, w, w + wait }.Contains(t),
                    "unexpected WarpToUT target " + t.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
