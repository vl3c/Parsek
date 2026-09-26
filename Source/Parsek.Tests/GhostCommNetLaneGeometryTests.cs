using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek.InGameTests;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The derivations behind the two ghost CommNet lanes' presets (SyntheticRecordingTests
    /// GhostCommNetLivePreset / GhostCommNetTimelinePreset), pinned against committed fixture
    /// bytes so a wrong constant reds here instead of costing a flight:
    /// <list type="bullet">
    ///   <item><description>the stock ephemeris (<see cref="StockEphemeris"/>) against three
    ///       SOI crossings the fixtures recorded, where one vessel's parent-frame and child-frame
    ///       positions must agree;</description></item>
    ///   <item><description>CN-2's host: duna-park-probe's active DD1 probe, its ORBIT node, its
    ///       saved latitude / altitude (which checks the 3D element convention) and the save's
    ///       CommNet parameters;</description></item>
    ///   <item><description>CN-2's geometry at its warp target: the probe and the shadow relay
    ///       behind Duna, the limb relay seeing both the probe and Kerbin, every link in range,
    ///       Ike out of the way;</description></item>
    ///   <item><description>CN-3's timeline ordering and each recording's production shape
    ///       (deploy gate, destroyed end, spawnable held end);</description></item>
    ///   <item><description>both specs' WarpToUT literals and CN-3's held-radius token.</description></item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class GhostCommNetLaneGeometryTests : IDisposable
    {
        private const double KerbinSoiRadius = 84159286.0;
        private const double DunaSoiRadius = 47921949.0;
        private const double IkeSoiRadius = 1049598.9;
        /// <summary>Sandbox facilities are maxed, so the KSC DSN is GameVariables.GetDSNRange(1) = 2.5e11.</summary>
        private const double KscDsnPower = 2.5e11;
        private const double RaTwoPower = 2e9;
        private const double RaHundredPower = 1e11;
        private const double InternalAntennaPower = 5000.0;
        /// <summary>Stock CommNet occluder radii: body radius x occlusionMultiplierAtm / Vac (0.75 / 0.9).</summary>
        private const double DunaOccluderRadius = StockEphemeris.DunaRadius * 0.75;
        private const double IkeOccluderRadius = StockEphemeris.IkeRadius * 0.9;
        /// <summary>How long after the warp lands the batch may still be reading the geometry.</summary>
        private const double BatchAfterWarpSeconds = 200.0;

        private readonly List<string> logLines = new List<string>();

        public GhostCommNetLaneGeometryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------ ephemeris pins

        private struct RecordedSegment
        {
            public string Body;
            public double StartUT;
            public FixtureOrbitElements Elements;
        }

        private static List<RecordedSegment> ReadRecordedSegments(string fixture, string recordingId)
        {
            string path = Path.Combine(SyntheticRecordingTests.ProjectRoot, "harness", "fixtures", "saves",
                fixture, "Parsek", "Recordings", recordingId + ".prec.txt");
            Assert.True(File.Exists(path), "recording text sidecar missing: " + path);
            string text = File.ReadAllText(path);
            var segments = new List<RecordedSegment>();
            foreach (Match m in Regex.Matches(text, @"ORBIT_SEGMENT\s*\{(?<b>[^}]*)\}"))
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (Match kv in Regex.Matches(m.Groups["b"].Value, @"(\w+) = (\S+)"))
                    values[kv.Groups[1].Value] = kv.Groups[2].Value;
                Func<string, double> d = k => double.Parse(values[k], NumberStyles.Float, CultureInfo.InvariantCulture);
                segments.Add(new RecordedSegment
                {
                    Body = values["body"],
                    StartUT = d("startUT"),
                    Elements = new FixtureOrbitElements
                    {
                        Sma = d("sma"), Ecc = d("ecc"), IncDeg = d("inc"), LanDeg = d("lan"),
                        ArgPeDeg = d("argPe"), MnaRad = d("mna"), Epoch = d("epoch"),
                    },
                });
            }
            Assert.NotEmpty(segments);
            return segments;
        }

        /// <summary>
        /// At the first sample in the new frame of a recorded SOI crossing, the vessel's
        /// parent-frame position must equal the child body's position plus its child-frame
        /// position. Returns the residual in metres.
        /// </summary>
        private static double SoiCrossingResidual(
            string fixture, string recordingId, string parentBody, double parentMu,
            string childBody, double childMu, double soiRadius, Func<double, FixtureVec3> childBodyInParent)
        {
            List<RecordedSegment> segs = ReadRecordedSegments(fixture, recordingId);
            for (int i = 0; i + 1 < segs.Count; i++)
            {
                bool exit = segs[i].Body == childBody && segs[i + 1].Body == parentBody;
                bool entry = segs[i].Body == parentBody && segs[i + 1].Body == childBody;
                if (!exit && !entry) continue;
                RecordedSegment parent = exit ? segs[i + 1] : segs[i];
                RecordedSegment child = exit ? segs[i] : segs[i + 1];
                double ut = segs[i + 1].StartUT;
                FixtureVec3 inChild = StockEphemeris.Position(child.Elements, childMu, ut);
                FixtureVec3 inParent = StockEphemeris.Position(parent.Elements, parentMu, ut);
                // The crossing is at the SOI boundary, so this really is the frame handoff.
                Assert.InRange(inChild.Magnitude, soiRadius * 0.995, soiRadius * 1.005);
                return (inParent - (childBodyInParent(ut) + inChild)).Magnitude;
            }
            Assert.True(false, "no " + childBody + " / " + parentBody + " SOI crossing in " + fixture + "/" + recordingId);
            return double.NaN;
        }

        [Fact]
        public void Ephemeris_KerbinMatchesTheRecordedKerbinExitInDunaDirect()
        {
            double residual = SoiCrossingResidual("duna-direct-recorded", "311d98e32547491e8dd37aec2526d25d",
                "Sun", StockEphemeris.SunGravParameter, "Kerbin", StockEphemeris.KerbinGravParameter,
                KerbinSoiRadius, StockEphemeris.KerbinHeliocentric);
            Assert.True(residual < 20000.0, "Kerbin ephemeris residual " + residual.ToString("F0", CultureInfo.InvariantCulture) + " m");
        }

        [Fact]
        public void Ephemeris_DunaMatchesTheRecordedDunaEntryInDunaDirect()
        {
            double residual = SoiCrossingResidual("duna-direct-recorded", "311d98e32547491e8dd37aec2526d25d",
                "Sun", StockEphemeris.SunGravParameter, "Duna", StockEphemeris.DunaGravParameter,
                DunaSoiRadius, StockEphemeris.DunaHeliocentric);
            Assert.True(residual < 20000.0, "Duna ephemeris residual " + residual.ToString("F0", CultureInfo.InvariantCulture) + " m");
        }

        [Fact]
        public void Ephemeris_IkeMatchesTheRecordedIkeEntryInIkeOrbit()
        {
            double residual = SoiCrossingResidual("ike-orbit-recorded", "05ceee33806d4079a1d9d125a1359115",
                "Duna", StockEphemeris.DunaGravParameter, "Ike", StockEphemeris.IkeGravParameter,
                IkeSoiRadius, ut => StockEphemeris.Position(StockEphemeris.Ike, StockEphemeris.DunaGravParameter, ut));
            Assert.True(residual < 1000.0, "Ike ephemeris residual " + residual.ToString("F0", CultureInfo.InvariantCulture) + " m");
        }

        // ------------------------------------------------------------------ CN-2 host

        [Fact]
        public void LiveHost_TheProbeIsDunaParkProbesActiveUncrewedVesselWithItsSavedOrbit()
        {
            string path = Path.Combine(SyntheticRecordingTests.ProjectRoot, "harness", "fixtures", "saves",
                "duna-park-probe", "persistent.sfs");
            string save = File.ReadAllText(path).Replace("\r\n", "\n");

            string block = SyntheticRecordingTests.ReadFixtureVesselBlock("duna-park-probe",
                SyntheticRecordingTests.DunaParkProbeVesselName);
            FixtureOrbitElements o = SyntheticRecordingTests.DunaParkProbeOrbit;
            Assert.Equal(o.Sma, SyntheticRecordingTests.ReadOrbitValue(block, "SMA"));
            Assert.Equal(o.Ecc, SyntheticRecordingTests.ReadOrbitValue(block, "ECC"));
            Assert.Equal(o.IncDeg, SyntheticRecordingTests.ReadOrbitValue(block, "INC"));
            Assert.Equal(o.ArgPeDeg, SyntheticRecordingTests.ReadOrbitValue(block, "LPE"));
            Assert.Equal(o.LanDeg, SyntheticRecordingTests.ReadOrbitValue(block, "LAN"));
            Assert.Equal(o.MnaRad, SyntheticRecordingTests.ReadOrbitValue(block, "MNA"));
            Assert.Equal(o.Epoch, SyntheticRecordingTests.ReadOrbitValue(block, "EPH"));
            Assert.Equal((double)SyntheticRecordingTests.DunaFlightGlobalsIndex,
                SyntheticRecordingTests.ReadOrbitValue(block, "REF"));
            Assert.Equal(SyntheticRecordingTests.DunaParkProbeSavedLon,
                SyntheticRecordingTests.ReadVesselValue(block, "lon"));
            Assert.Equal(SyntheticRecordingTests.DunaParkProbeSaveUT, o.Epoch);
            Assert.DoesNotContain("\n\t\t\t\tcrew = ", "\n" + block);
            Assert.Contains("\n\t\t\tsit = ORBITING\n", "\n" + block);

            // The save's own clock, its active vessel (FLIGHTSTATE index) and its mode.
            Match ut = Regex.Match(save, @"FLIGHTSTATE\n\t\{\n\t\tversion = [^\n]+\n\t\tUT = ([^\n]+)\n\t\tactiveVessel = (\d+)");
            Assert.True(ut.Success);
            Assert.Equal(SyntheticRecordingTests.DunaParkProbeSaveUT,
                double.Parse(ut.Groups[1].Value, CultureInfo.InvariantCulture));
            var names = new List<string>();
            foreach (Match v in Regex.Matches(save, @"\n\t\tVESSEL\n\t\t\{\n\t\t\tpid = \w+\n\t\t\tpersistentId = \d+\n\t\t\tname = ([^\n]+)\n"))
                names.Add(v.Groups[1].Value);
            Assert.Equal(SyntheticRecordingTests.DunaParkProbeVesselName,
                names[int.Parse(ut.Groups[2].Value, CultureInfo.InvariantCulture)]);
            Assert.Contains("\n\tMode = SANDBOX\n", save);

            // CommNet: stock parameters, so the DSN, the occluder radii and the range model are stock.
            Assert.Contains("\t\t\tEnableCommNet = True\n", save);
            Assert.Contains("\t\t\trangeModifier = 1\n", save);
            Assert.Contains("\t\t\tDSNModifier = 1\n", save);
            Assert.Contains("\t\t\tocclusionMultiplierVac = 0.9\n", save);
            Assert.Contains("\t\t\tocclusionMultiplierAtm = 0.75\n", save);

            // The control point's pilot: a Pilot, Available (on no vessel), in this roster.
            Match k = Regex.Match(save, @"\n\t\t\tname = " + Regex.Escape(SyntheticRecordingTests.GhostCommNetLivePilot)
                + @"\n\t\t\tgender = \w+\n\t\t\ttype = Crew\n\t\t\ttrait = Pilot\n.*?\n\t\t\tstate = (\w+)\n", RegexOptions.Singleline);
            Assert.True(k.Success, "no Pilot " + SyntheticRecordingTests.GhostCommNetLivePilot + " in the roster");
            Assert.Equal("Available", k.Groups[1].Value);
        }

        [Fact]
        public void LiveHost_TheOrbitNodeReproducesTheProbesSavedLatitudeAndAltitude()
        {
            // Latitude and altitude do not depend on Duna's rotation, so they check the 3D
            // element convention (LAN / inclination / argPe order and signs) independently.
            string block = SyntheticRecordingTests.ReadFixtureVesselBlock("duna-park-probe",
                SyntheticRecordingTests.DunaParkProbeVesselName);
            FixtureVec3 p = StockEphemeris.Position(SyntheticRecordingTests.DunaParkProbeOrbit,
                StockEphemeris.DunaGravParameter, SyntheticRecordingTests.DunaParkProbeSaveUT);
            Assert.True(Math.Abs(StockEphemeris.LatitudeDeg(p) - SyntheticRecordingTests.ReadVesselValue(block, "lat")) < 1e-4);
            Assert.True(Math.Abs(p.Magnitude - StockEphemeris.DunaRadius - SyntheticRecordingTests.ReadVesselValue(block, "alt")) < 5.0);
            // And the rotation calibration maps the probe back onto its own saved longitude.
            Assert.True(Math.Abs(StockEphemeris.Wrap180(SyntheticRecordingTests.DunaSurfaceLonDeg(p,
                SyntheticRecordingTests.DunaParkProbeSaveUT) - SyntheticRecordingTests.DunaParkProbeSavedLon)) < 1e-9);
        }

        // ------------------------------------------------------------------ CN-2 geometry

        private struct LiveGeometry
        {
            public FixtureVec3 Duna, Kerbin, Ike, Probe, Limb, Shadow, Control;
        }

        private static LiveGeometry GeometryAt(double ut)
        {
            FixtureOrbitElements o = SyntheticRecordingTests.DunaParkProbeOrbit;
            double mu = StockEphemeris.DunaGravParameter;
            FixtureVec3 duna = StockEphemeris.DunaHeliocentric(ut);
            Func<double, FixtureVec3> onOrbit = leadDeg =>
                duna + StockEphemeris.Position(o.WithMeanAnomalyOffset(leadDeg * Math.PI / 180.0), mu, ut);
            return new LiveGeometry
            {
                Duna = duna,
                Kerbin = StockEphemeris.KerbinHeliocentric(ut),
                Ike = StockEphemeris.IkeHeliocentric(ut),
                Probe = onOrbit(0.0),
                Limb = onOrbit(SyntheticRecordingTests.GhostCommNetLiveLimbLeadDeg),
                Shadow = onOrbit(SyntheticRecordingTests.GhostCommNetLiveShadowLeadDeg),
                Control = onOrbit(SyntheticRecordingTests.GhostCommNetLiveControlLeadDeg),
            };
        }

        private static bool BehindDuna(LiveGeometry g, FixtureVec3 node)
            => FixtureVec3.SegmentDistanceTo(node, g.Kerbin, g.Duna) < DunaOccluderRadius;

        /// <summary>DD1's combined relay power: three RA-2 plus the octo core's INTERNAL antenna.</summary>
        private static double ProbeRelayPower()
        {
            var inputs = new List<GhostAntennaPowerInput>
            {
                new GhostAntennaPowerInput { Power = RaTwoPower, IsRelay = true, Combinable = true, Exponent = 0.75 },
                new GhostAntennaPowerInput { Power = RaTwoPower, IsRelay = true, Combinable = true, Exponent = 0.75 },
                new GhostAntennaPowerInput { Power = RaTwoPower, IsRelay = true, Combinable = true, Exponent = 0.75 },
                new GhostAntennaPowerInput { Power = InternalAntennaPower, IsRelay = false, Combinable = false, Exponent = 0.75 },
            };
            return GhostCommNetMath.ComputeVesselPowers(inputs, false, 1.0).RelayPower;
        }

        [Fact]
        public void LiveGeometry_TheWarpTargetSitsWhereTheProbeAndTheShadowRelayAreBothBehindDuna()
        {
            double save = SyntheticRecordingTests.DunaParkProbeSaveUT;
            double target = SyntheticRecordingTests.GhostCommNetLiveWarpTargetUT;
            // Scan one probe orbit forward from the save for the joint occlusion window.
            double first = double.NaN, last = double.NaN;
            bool probeBehindAtSave = BehindDuna(GeometryAt(save), GeometryAt(save).Probe);
            for (double ut = save; ut <= save + 12200.0; ut += 1.0)
            {
                LiveGeometry g = GeometryAt(ut);
                bool both = BehindDuna(g, g.Probe) && BehindDuna(g, g.Shadow);
                if (both && double.IsNaN(first)) first = ut;
                if (!both && !double.IsNaN(first) && double.IsNaN(last)) last = ut;
            }
            Assert.False(probeBehindAtSave, "the probe is already behind Duna at the save; the warp premise changed");
            Assert.False(double.IsNaN(first) || double.IsNaN(last), "no joint occlusion window in the first orbit");
            // Derived 2026-09-26: joint window about [save + 1801, save + 2600].
            Assert.InRange(target, first + 60.0, last - BatchAfterWarpSeconds);
            // The spawn-free premise: nothing in the preset ends inside the lane.
            Assert.True(save + SyntheticRecordingTests.GhostCommNetLiveStartOffsetSeconds
                + SyntheticRecordingTests.GhostCommNetLiveWindowSeconds > last + 1000.0);
        }

        [Fact]
        public void LiveGeometry_EveryLinkTheCellsNeedIsInRangeAndEveryOcclusionIsDunas()
        {
            double target = SyntheticRecordingTests.GhostCommNetLiveWarpTargetUT;
            double probeRelay = ProbeRelayPower();
            Assert.True(probeRelay > 4e9 && probeRelay < 5e9, "DD1 combined relay " + probeRelay.ToString("R", CultureInfo.InvariantCulture));
            for (double ut = target; ut <= target + BatchAfterWarpSeconds; ut += 10.0)
            {
                LiveGeometry g = GeometryAt(ut);
                double toKerbin = (g.Probe - g.Kerbin).Magnitude;
                // The probe: home in range (StandardRangeModel: sqrt(a * b)), cut off only by Duna.
                Assert.True(Math.Sqrt(probeRelay * KscDsnPower) > 1.5 * toKerbin);
                Assert.True(FixtureVec3.SegmentDistanceTo(g.Probe, g.Kerbin, g.Duna) < DunaOccluderRadius - 30000.0);
                // The limb relay sees the probe and Kerbin, clear of Duna and Ike, in range of both.
                Assert.True(FixtureVec3.SegmentDistanceTo(g.Limb, g.Kerbin, g.Duna) > DunaOccluderRadius + 200000.0);
                Assert.True(FixtureVec3.SegmentDistanceTo(g.Limb, g.Probe, g.Duna) > DunaOccluderRadius + 200000.0);
                Assert.True(FixtureVec3.SegmentDistanceTo(g.Limb, g.Kerbin, g.Ike) > IkeOccluderRadius + 1000000.0);
                Assert.True(FixtureVec3.SegmentDistanceTo(g.Limb, g.Probe, g.Ike) > IkeOccluderRadius + 1000000.0);
                Assert.True(Math.Sqrt(RaHundredPower * KscDsnPower) > 1.5 * (g.Limb - g.Kerbin).Magnitude);
                Assert.True(Math.Sqrt(RaHundredPower * probeRelay) > 1.5 * (g.Limb - g.Probe).Magnitude);
                // The shadow relay links the probe but is behind Duna from Kerbin (home in its range).
                Assert.True(FixtureVec3.SegmentDistanceTo(g.Shadow, g.Probe, g.Duna) > DunaOccluderRadius + 200000.0);
                Assert.True(FixtureVec3.SegmentDistanceTo(g.Shadow, g.Kerbin, g.Duna) < DunaOccluderRadius - 30000.0);
                Assert.True(Math.Sqrt(RaHundredPower * KscDsnPower) > 1.5 * (g.Shadow - g.Kerbin).Magnitude);
                Assert.True(Math.Sqrt(RaHundredPower * probeRelay) > 1.5 * (g.Shadow - g.Probe).Magnitude);
                // The control point (INTERNAL antennas only) reaches the probe's relay, never home.
                Assert.True(FixtureVec3.SegmentDistanceTo(g.Control, g.Probe, g.Duna) > DunaOccluderRadius + 200000.0);
                Assert.True(Math.Sqrt(InternalAntennaPower * probeRelay) > 10.0 * (g.Control - g.Probe).Magnitude);
                Assert.True(Math.Sqrt(InternalAntennaPower * KscDsnPower) < 0.1 * (g.Control - g.Kerbin).Magnitude);
            }
        }

        [Fact]
        public void LivePreset_ThreeRecordingsRideTheProbesOrbitAndLatchAtLoad()
        {
            double save = SyntheticRecordingTests.DunaParkProbeSaveUT;
            RecordingBuilder[] preset = SyntheticRecordingTests.GhostCommNetLivePreset(save);
            string[] ids =
            {
                GhostCommNetLiveInGameTests.LimbRelayRecordingId,
                GhostCommNetLiveInGameTests.ShadowRelayRecordingId,
                GhostCommNetLiveInGameTests.ControlPointRecordingId,
            };
            double[] leads =
            {
                SyntheticRecordingTests.GhostCommNetLiveLimbLeadDeg,
                SyntheticRecordingTests.GhostCommNetLiveShadowLeadDeg,
                SyntheticRecordingTests.GhostCommNetLiveControlLeadDeg,
            };
            Assert.Equal(3, preset.Length);
            PlaybackScopeTracker.ResetForTesting();
            try
            {
                for (int i = 0; i < preset.Length; i++)
                {
                    Recording r = SyntheticRecordingTests.MaterializeSpawnSafetyRecording(preset[i], ids[i]);
                    Assert.Single(r.Points);
                    Assert.Single(r.OrbitSegments);
                    OrbitSegment seg = r.OrbitSegments[0];
                    FixtureOrbitElements o = SyntheticRecordingTests.DunaParkProbeOrbit;
                    Assert.Equal("Duna", seg.bodyName);
                    Assert.Equal("Duna", r.Points[0].bodyName);
                    Assert.Equal(o.Sma, seg.semiMajorAxis);
                    Assert.Equal(o.Ecc, seg.eccentricity);
                    Assert.Equal(o.IncDeg, seg.inclination);
                    Assert.Equal(o.LanDeg, seg.longitudeOfAscendingNode);
                    Assert.Equal(o.ArgPeDeg, seg.argumentOfPeriapsis);
                    Assert.Equal(o.Epoch, seg.epoch);
                    double lead = StockEphemeris.Wrap180((seg.meanAnomalyAtEpoch - o.MnaRad) * 180.0 / Math.PI);
                    Assert.True(Math.Abs(lead - leads[i]) < 1e-9, ids[i] + " lead " + lead.ToString("R", CultureInfo.InvariantCulture));
                    Assert.Equal(TerminalState.Orbiting, r.TerminalStateValue);
                    Assert.Equal("Duna", r.TerminalOrbitBody);
                    Assert.NotNull(r.VesselSnapshot);

                    // The start point is where the orbit segment puts the vessel at the start.
                    double start = save + SyntheticRecordingTests.GhostCommNetLiveStartOffsetSeconds;
                    Assert.Equal(start, r.Points[0].ut, 6);
                    FixtureVec3 p = StockEphemeris.Position(new FixtureOrbitElements
                    {
                        Sma = seg.semiMajorAxis, Ecc = seg.eccentricity, IncDeg = seg.inclination,
                        LanDeg = seg.longitudeOfAscendingNode, ArgPeDeg = seg.argumentOfPeriapsis,
                        MnaRad = seg.meanAnomalyAtEpoch, Epoch = seg.epoch,
                    }, StockEphemeris.DunaGravParameter, start);
                    Assert.True(Math.Abs(StockEphemeris.LatitudeDeg(p) - r.Points[0].latitude) < 1e-9);
                    Assert.True(Math.Abs(StockEphemeris.Wrap180(SyntheticRecordingTests.DunaSurfaceLonDeg(p, start)
                        - r.Points[0].longitude)) < 1e-9);
                    Assert.True(Math.Abs(p.Magnitude - StockEphemeris.DunaRadius - r.Points[0].altitude) < 1e-3);

                    // The window: opens 1 s after the save and is latched there, runs far past the lane.
                    double activation = GhostPlaybackEngine.ResolveGhostActivationStartUT(r);
                    Assert.Equal(start, activation, 6);
                    Assert.True(r.EndUT > SyntheticRecordingTests.GhostCommNetLiveWarpTargetUT + 50000.0);
                    PlaybackScopeTracker.NotePlayhead(ids[i], save, activation);
                    var input = new GhostCommNetEligibilityInput
                    {
                        HasRecordingId = true,
                        HasRenderableData = GhostPlaybackEngine.HasRenderableGhostData(r),
                        HistoricalNeverReplayed = GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                            r.LoopPlayback, false,
                            PlaybackScopeTracker.IsHistoricalNeverReplayed(ids[i],
                                SyntheticRecordingTests.GhostCommNetLiveWarpTargetUT, activation),
                            forSpawn: true),
                        ActivationStartUT = activation,
                        EndUT = r.EndUT,
                    };
                    Assert.Equal("in-window", GhostCommNetMath.EvaluateEligibility(
                        input, SyntheticRecordingTests.GhostCommNetLiveWarpTargetUT).Reason);
                    Assert.False(RecordingOptimizer.TrimBoringTail(r, new List<Recording> { r }));
                }

                // Parts: RA-100 on the two relays; the control point carries a pilot and no relay part.
                Assert.Equal(new[] { "probeStackLarge", "RelayAntenna100" }, PartNames(preset[0]));
                Assert.Equal(new[] { "probeStackLarge", "RelayAntenna100" }, PartNames(preset[1]));
                Assert.Equal(new[] { "probeStackLarge", "mk1pod.v2" }, PartNames(preset[2]));
                Assert.Empty(Crew(preset[0]));
                Assert.Empty(Crew(preset[1]));
                Assert.Equal(new[] { SyntheticRecordingTests.GhostCommNetLivePilot }, Crew(preset[2]));
            }
            finally
            {
                PlaybackScopeTracker.ResetForTesting();
            }
        }

        private static string[] PartNames(RecordingBuilder b)
        {
            var names = new List<string>();
            foreach (ConfigNode part in b.GetVesselSnapshot().GetNodes("PART"))
                names.Add(part.GetValue("name"));
            return names.ToArray();
        }

        private static string[] Crew(RecordingBuilder b)
        {
            var crew = new List<string>();
            foreach (ConfigNode part in b.GetVesselSnapshot().GetNodes("PART"))
                crew.AddRange(part.GetValues("crew"));
            return crew.ToArray();
        }

        // ------------------------------------------------------------------ CN-3

        [Fact]
        public void TimelinePreset_TransitionsAreOrderedInsideTheWarpAndEachRecordingHasItsShape()
        {
            double save = SyntheticRecordingTests.GhostCommNetTimelineSaveUT;
            double warp = SyntheticRecordingTests.GhostCommNetTimelineWarpTargetUT;
            // gloops-airshow's own clock.
            Assert.Equal(SyntheticRecordingTests.GhostCommNetKscLanRefUT, save);
            RecordingBuilder[] preset = SyntheticRecordingTests.GhostCommNetTimelinePreset(save);
            Recording deploy = SyntheticRecordingTests.MaterializeSpawnSafetyRecording(preset[0],
                GhostCommNetTimelineInGameTests.DeployRelayRecordingId);
            Recording doomed = SyntheticRecordingTests.MaterializeSpawnSafetyRecording(preset[1],
                GhostCommNetTimelineInGameTests.DoomedRelayRecordingId);
            Recording held = SyntheticRecordingTests.MaterializeSpawnSafetyRecording(preset[2],
                GhostCommNetTimelineInGameTests.HeldRelayRecordingId);
            double start = save + SyntheticRecordingTests.GhostCommNetTimelineStartOffsetSeconds;
            double deployUT = save + SyntheticRecordingTests.GhostCommNetTimelineDeployOffsetSeconds;
            foreach (Recording r in new[] { deploy, doomed, held })
            {
                Assert.Equal(start, GhostPlaybackEngine.ResolveGhostActivationStartUT(r), 6);
                Assert.True(GhostPlaybackEngine.HasRenderableGhostData(r));
                Assert.False(RecordingOptimizer.TrimBoringTail(r, new List<Recording> { r }));
            }

            // Ordering: every transition falls inside the warp, with room for the settle after.
            Assert.True(start + 60.0 < deployUT);
            Assert.True(deployUT < doomed.EndUT && doomed.EndUT < held.EndUT);
            Assert.True(held.EndUT < warp - 150.0);
            Assert.True(deploy.EndUT > warp + 50000.0);

            // Deploy relay: one DeployableExtended on the HG-5 (part index 1), which gates it.
            Assert.Single(deploy.PartEvents);
            PartEvent e = deploy.PartEvents[0];
            Assert.Equal(PartEventType.DeployableExtended, e.eventType);
            Assert.Equal(SyntheticRecordingTests.GhostCommNetTimelineDeployPartPid, e.partPersistentId);
            Assert.Equal(deployUT, e.ut, 6);
            ConfigNode[] parts = deploy.VesselSnapshot.GetNodes("PART");
            Assert.Equal(SyntheticRecordingTests.GhostCommNetTimelineDeployPartName, parts[1].GetValue("name"));
            Assert.Equal(SyntheticRecordingTests.GhostCommNetTimelineDeployPartPid.ToString(CultureInfo.InvariantCulture),
                parts[1].GetValue("persistentId"));
            var events = new List<PartEvent> { e };
            Assert.False(GhostCommNetMath.DeployGatedCanCommAt(e.partPersistentId, events, deployUT - 0.01, true));
            Assert.True(GhostCommNetMath.DeployGatedCanCommAt(e.partPersistentId, events, deployUT, false));

            // Doomed relay: ends Destroyed with no end snapshot (the ghost-visual snapshot is the spec source).
            Assert.Equal(TerminalState.Destroyed, doomed.TerminalStateValue);
            Assert.Null(doomed.VesselSnapshot);
            Assert.NotNull(doomed.GhostVisualSnapshot);
            Assert.Equal(save + SyntheticRecordingTests.GhostCommNetTimelineDoomedEndOffsetSeconds, doomed.EndUT, 6);
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(doomed, false, false).needsSpawn);

            // Held relay: a spawnable Orbiting end with a terminal orbit and an end snapshot.
            Assert.Equal(TerminalState.Orbiting, held.TerminalStateValue);
            Assert.Equal("Kerbin", held.TerminalOrbitBody);
            Assert.NotNull(held.VesselSnapshot);
            Assert.Equal(save + SyntheticRecordingTests.GhostCommNetTimelineHeldEndOffsetSeconds, held.EndUT, 6);
            var spawn = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(held, false, false);
            Assert.True(spawn.needsSpawn, "the held relay would not spawn: " + spawn.reason);
        }

        [Fact]
        public void TimelinePreset_EligibilityWalksInWindowThenHeldOrEndedAcrossTheWarp()
        {
            double save = SyntheticRecordingTests.GhostCommNetTimelineSaveUT;
            double warp = SyntheticRecordingTests.GhostCommNetTimelineWarpTargetUT;
            double start = save + SyntheticRecordingTests.GhostCommNetTimelineStartOffsetSeconds;
            double doomedEnd = save + SyntheticRecordingTests.GhostCommNetTimelineDoomedEndOffsetSeconds;
            double heldEnd = save + SyntheticRecordingTests.GhostCommNetTimelineHeldEndOffsetSeconds;

            var doomed = new GhostCommNetEligibilityInput
            {
                HasRecordingId = true, HasRenderableData = true, ActivationStartUT = start, EndUT = doomedEnd,
                NeedsSpawn = false,
            };
            GhostCommNetEligibility inWindow = GhostCommNetMath.EvaluateEligibility(doomed, doomedEnd - 1.0);
            Assert.Equal("in-window", inWindow.Reason);
            Assert.False(inWindow.ExpectHoldPastEnd);
            Assert.Equal(GhostCommNetMath.ReasonWindowEnded, GhostCommNetMath.EvaluateEligibility(doomed, doomedEnd + 0.1).Reason);
            Assert.False(GhostCommNetMath.ResolveHookSample(doomedEnd + 0.1, start, doomedEnd, false, false).Lit);

            var held = new GhostCommNetEligibilityInput
            {
                HasRecordingId = true, HasRenderableData = true, ActivationStartUT = start, EndUT = heldEnd,
                NeedsSpawn = true,
            };
            GhostCommNetEligibility before = GhostCommNetMath.EvaluateEligibility(held, heldEnd - 1.0);
            Assert.Equal("in-window", before.Reason);
            Assert.True(before.ExpectHoldPastEnd);
            // Past its end during the warp (spawn deferred): held on the terminal orbit.
            GhostCommNetEligibility during = GhostCommNetMath.EvaluateEligibility(held, (heldEnd + warp) / 2.0);
            Assert.Equal("held-for-spawn", during.Reason);
            Assert.True(during.HoldAtEnd);
            Assert.Equal(GhostCommNetHeldPositionSource.TerminalOrbit,
                GhostCommNetMath.ChooseHeldPositionSource(false, false, true, false));
            // Spawned after the warp: the node goes, the real vessel's own node takes over.
            held.VesselSpawned = true;
            Assert.Equal("vessel-spawned", GhostCommNetMath.EvaluateEligibility(held, warp + 1.0).Reason);
        }

        [Fact]
        public void LiveCells_SegmentOccluderTestUsesTheClosestPointOnTheSegment()
        {
            // The in-game cells name the body between a node and home with this test.
            var center = new Vector3d(0, 0, 0);
            Assert.True(GhostCommNetLiveInGameTests.SegmentPassesWithin(
                new Vector3d(-10, 5, 0), new Vector3d(10, 5, 0), center, 6.0));
            Assert.False(GhostCommNetLiveInGameTests.SegmentPassesWithin(
                new Vector3d(-10, 5, 0), new Vector3d(10, 5, 0), center, 4.0));
            // The perpendicular foot lies past the segment's end: the end point is the closest.
            Assert.False(GhostCommNetLiveInGameTests.SegmentPassesWithin(
                new Vector3d(-10, 5, 0), new Vector3d(-8, 5, 0), center, 9.0));
            Assert.True(GhostCommNetLiveInGameTests.SegmentPassesWithin(
                new Vector3d(-10, 5, 0), new Vector3d(-8, 5, 0), center, 9.5));
            // A degenerate segment is its point.
            Assert.True(GhostCommNetLiveInGameTests.SegmentPassesWithin(
                new Vector3d(1, 1, 1), new Vector3d(1, 1, 1), center, 2.0));
        }

        // ------------------------------------------------------------------ spec literals

        private static string ReadSpec(string fileName)
        {
            string path = Path.Combine(SyntheticRecordingTests.ProjectRoot, "harness", "scenarios", fileName);
            Assert.True(File.Exists(path), "spec missing: " + path);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        private static double SpecWarpTarget(string spec)
        {
            MatchCollection m = Regex.Matches(spec, @"\n\s*\{ cmd = ""WarpToUT"",\s*args = \{ ut = ""([0-9.]+)""");
            Match only = Assert.Single(m.Cast<Match>());
            return double.Parse(only.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        [Fact]
        public void Specs_WarpTargetsAreThePinnedDerivations()
        {
            string live = ReadSpec("CN-2-ghost-commnet-live-probe.toml");
            Assert.Equal(SyntheticRecordingTests.GhostCommNetLiveWarpTargetUT, SpecWarpTarget(live));
            Assert.Contains("injectedRecordings = \"ghost-commnet-live\"", live);
            Assert.Contains("saveTemplate = \"fixtures/saves/duna-park-probe\"", live);
            string timeline = ReadSpec("CN-3-ghost-commnet-timeline-warp.toml");
            Assert.Equal(SyntheticRecordingTests.GhostCommNetTimelineWarpTargetUT, SpecWarpTarget(timeline));
            Assert.Contains("injectedRecordings = \"ghost-commnet-timeline\"", timeline);
            Assert.Contains("saveTemplate = \"fixtures/saves/gloops-airshow\"", timeline);
        }

        [Fact]
        public void Specs_TheHeldRadiusTokenMatchesTheSynchronousOrbit()
        {
            // CN-3 pins the held node's radius from the centre of Kerbin to the metre band the
            // synchronous orbit's sma formats into (FormatHeldPosition prints radius F0).
            string timeline = ReadSpec("CN-3-ghost-commnet-timeline-warp.toml");
            Match m = Regex.Match(timeline, @"radius=(\d+)\[0-9\]");
            Assert.True(m.Success, "CN-3 carries no radius= token");
            string radius = SyntheticRecordingTests.GhostCommNetSynchronousSma().ToString("F0", CultureInfo.InvariantCulture);
            Assert.Equal(m.Groups[1].Value, radius.Substring(0, radius.Length - 1));
        }
    }
}
