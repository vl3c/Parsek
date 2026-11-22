using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A synthetic vessel line piece for the phase analyzer, built through RecordingBuilder:
    /// sections with two frame points each, checkpoint sections, engine firings, delta-v
    /// snapshots, terminal state and orbit.
    /// </summary>
    internal sealed class ProfilePiece
    {
        private readonly RecordingBuilder b;
        private TerminalState? terminal;
        private string terminalBody;
        private double tSma, tEcc, tEpoch;
        private string launchSite;
        private string startBody = "Kerbin";
        private string startSituation;
        private string evaCrew;
        private string endBiome;
        private uint nextPid = 1;

        internal ProfilePiece(string id, string vessel = "Ship")
        {
            b = new RecordingBuilder(vessel).WithRecordingId(id).WithFormatVersion(RecordingStore.CurrentRecordingFormatVersion);
        }

        internal ProfilePiece Sec(SegmentEnvironment env, double t0, double t1, string body = "Kerbin",
            double alt0 = double.NaN, double alt1 = double.NaN, ReferenceFrame frame = ReferenceFrame.Absolute)
        {
            double a0 = double.IsNaN(alt0) ? DefaultAlt(env) : alt0;
            double a1 = double.IsNaN(alt1) ? a0 : alt1;
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = t0, altitude = a0, bodyName = body, rotation = UnityEngine.Quaternion.identity },
                new TrajectoryPoint { ut = t1, altitude = a1, bodyName = body, rotation = UnityEngine.Quaternion.identity, longitude = 0.01 },
            };
            b.AddTrackSection(new TrackSection
            {
                environment = env, referenceFrame = frame, source = TrackSectionSource.Active,
                startUT = t0, endUT = t1, frames = frames, checkpoints = new List<OrbitSegment>(),
                minAltitude = (float)Math.Min(a0, a1), maxAltitude = (float)Math.Max(a0, a1), sampleRateHz = 1f,
            });
            return this;
        }

        private static double DefaultAlt(SegmentEnvironment env)
        {
            switch (env)
            {
                case SegmentEnvironment.SurfaceMobile:
                case SegmentEnvironment.SurfaceStationary: return 70;
                case SegmentEnvironment.Atmospheric: return 30000;
                case SegmentEnvironment.Approach: return 5000;
                default: return 80000;
            }
        }

        internal ProfilePiece Cp(double t0, double t1, string body, double sma, double ecc, double inc = 0,
            SegmentEnvironment env = SegmentEnvironment.ExoBallistic, bool predicted = false, double mEp = 0)
        {
            b.AddTrackSection(new TrackSection
            {
                environment = env, referenceFrame = ReferenceFrame.OrbitalCheckpoint, source = TrackSectionSource.Checkpoint,
                startUT = t0, endUT = t1, frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = t0, endUT = t1, bodyName = body, semiMajorAxis = sma, eccentricity = ecc,
                        inclination = inc, epoch = t0, meanAnomalyAtEpoch = mEp, isPredicted = predicted,
                    },
                },
                minAltitude = float.NaN, maxAltitude = float.NaN, sampleRateHz = 0.1f,
            });
            return this;
        }

        internal ProfilePiece Engine(double on, double off, string part = "engine")
        {
            uint pid = nextPid++;
            b.AddPartEvent(on, pid, (int)PartEventType.EngineIgnited, part, 1f);
            b.AddPartEvent(off, pid, (int)PartEventType.EngineShutdown, part, 0f);
            return this;
        }

        internal ProfilePiece Chute(double ut)
        {
            b.AddPartEvent(ut, 900, (int)PartEventType.ParachuteDeployed, "parachuteSingle");
            return this;
        }

        internal ProfilePiece Dv(double ut, DeltaVTrigger trigger, double vacuum)
        {
            b.AddDeltaVSnapshot(ut, trigger, vacuum);
            return this;
        }

        internal ProfilePiece Ends(TerminalState t, string body = null, double sma = 0, double ecc = 0, double epoch = 0)
        {
            terminal = t;
            terminalBody = body;
            tSma = sma;
            tEcc = ecc;
            tEpoch = epoch;
            return this;
        }

        internal ProfilePiece Launch(string site = "Launch Pad")
        {
            launchSite = site;
            startSituation = "Prelaunch";
            return this;
        }

        internal ProfilePiece Eva(string kerbal)
        {
            evaCrew = kerbal;
            return this;
        }

        internal ProfilePiece Biome(string biome)
        {
            endBiome = biome;
            return this;
        }

        internal ProfilePiece StartsOn(string body)
        {
            startBody = body;
            return this;
        }

        internal Recording Build()
        {
            Recording rec = RecordingStorageFixtures.MaterializeTrajectory(b);
            rec.TerminalStateValue = terminal;
            rec.TerminalOrbitBody = terminalBody;
            rec.TerminalOrbitSemiMajorAxis = tSma;
            rec.TerminalOrbitEccentricity = tEcc;
            rec.TerminalOrbitEpoch = tEpoch;
            rec.LaunchSiteName = launchSite;
            rec.StartSituation = startSituation;
            rec.StartBodyName = startBody;
            rec.EvaCrewName = evaCrew;
            rec.EndBiome = endBiome;
            List<DeltaVSnapshot> dv = b.GetDeltaVSnapshots();
            rec.DeltaVSnapshots = dv != null ? new List<DeltaVSnapshot>(dv) : null;
            return rec;
        }
    }

    /// <summary>
    /// The phase analyzer's rules one by one (design-mission-profile.md section 11.1): the
    /// atmosphere-pass table, burn names, the evidence tiers, the burn definition, delta-v,
    /// EVA lines, strata and totality.
    /// </summary>
    [Collection("Sequential")]
    public class MissionPhaseAnalyzerTests : IDisposable
    {
        // Stock Kerbin: radius 600 km, atmosphere 70 km.
        private const double LkoSma = 680000, LkoEcc = 0.005;
        private const double FallingSma = 450000, FallingEcc = 0.5;
        private const double MunTransferSma = 6400000, MunTransferEcc = 0.894;

        public MissionPhaseAnalyzerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        internal static PhasePlan Analyze(params Recording[] pieces) => Analyze(null, pieces);

        internal static PhasePlan Analyze(List<ProfileAnchorInput> anchors, params Recording[] pieces)
        {
            var input = new VesselLineInput
            {
                LineId = pieces[0].RecordingId,
                LaunchBodyName = "Kerbin",
                BodyFacts = RecordingOptimizer.StockBodyFacts,
                IsEva = !string.IsNullOrEmpty(pieces[0].EvaCrewName),
            };
            input.Pieces.AddRange(pieces);
            if (anchors != null) input.Anchors.AddRange(anchors);
            PhasePlan plan = MissionPhaseAnalyzer.Analyze(input);
            Assert.True(MissionPhaseAnalyzer.PhasesTile(plan, out string why), why);
            return plan;
        }

        internal static string Trace(PhasePlan plan)
            => string.Join(" | ", plan.Phases.Select(p => p.Kind + "[" + p.StartUT.ToString("F1", CultureInfo.InvariantCulture)
                + "," + p.EndUT.ToString("F1", CultureInfo.InvariantCulture) + "] " + string.Join(";", p.Events.Select(e =>
                e.Kind + (e.Kind == ProfileEventKind.Burn ? ":" + e.BurnKind + "@" + e.UT.ToString("F1", CultureInfo.InvariantCulture) : "")))));

        private static string Kinds(PhasePlan plan)
            => string.Join(",", plan.Phases.Select(p => p.Kind.ToString()));

        private static List<ProfileEvent> Events(PhasePlan plan, ProfileEventKind kind)
            => MissionProfileBuilder.AllEvents(plan.Phases).Where(e => e.Kind == kind).ToList();

        private static List<ProfileEvent> Burns(PhasePlan plan) => Events(plan, ProfileEventKind.Burn);

        // A launch to a low Kerbin orbit: pad, atmosphere, falling coast, circularization.
        private static ProfilePiece Launch(string id = "root")
            => new ProfilePiece(id).Launch()
                .Sec(SegmentEnvironment.SurfaceStationary, 0, 5)
                .Sec(SegmentEnvironment.Atmospheric, 5, 180, alt0: 100, alt1: 70000).Engine(4, 150)
                .Sec(SegmentEnvironment.ExoBallistic, 180, 200, alt0: 70000, alt1: 75000)
                .Cp(200, 260, "Kerbin", FallingSma, FallingEcc)
                .Sec(SegmentEnvironment.ExoBallistic, 260, 270, alt0: 79000, alt1: 79500)
                .Sec(SegmentEnvironment.ExoPropulsive, 270, 290, alt0: 79500, alt1: 80000).Engine(269, 289)
                .Sec(SegmentEnvironment.ExoBallistic, 290, 300, alt0: 80000, alt1: 80000)
                .Cp(300, 2000, "Kerbin", LkoSma, LkoEcc);

        // ------------------------------------------------------------------ launch / ascent

        // catches: a launch to orbit read as anything but Prelaunch -> Ascent -> Circularization
        // -> Orbit, with the circularization burn timed by its engine.
        [Fact]
        public void LaunchToOrbit_ReadsAscentCircularizationOrbit()
        {
            PhasePlan plan = Analyze(Launch().Ends(TerminalState.Orbiting, "Kerbin", LkoSma, LkoEcc, 2000).Build());
            Assert.Equal("Prelaunch,Ascent,Circularization,Orbit", Kinds(plan));
            ProfileEvent circ = Burns(plan).Single();
            Assert.Equal(ProfileBurnKind.Circularization, circ.BurnKind);
            Assert.Equal(269, circ.UT, 3);
            Assert.Equal(289, circ.EndUT, 3);
            Assert.True(circ.EngineTimed);
            Assert.Single(Events(plan, ProfileEventKind.Liftoff));
            Assert.Single(Events(plan, ProfileEventKind.AtmosphereExit));
            Assert.Equal(5, plan.Phases[1].StartUT, 3);
            Assert.Equal(180, plan.Phases[1].EndUT, 3);
        }

        // catches: a takeoff that never reaches space read as a launch (an aircraft flight).
        [Fact]
        public void Takeoff_NeverReachingSpace_IsAFlight()
        {
            Recording r = new ProfilePiece("plane").Launch("Runway")
                .Sec(SegmentEnvironment.SurfaceMobile, 0, 20)
                .Sec(SegmentEnvironment.Atmospheric, 20, 600, alt0: 70, alt1: 3000)
                .Sec(SegmentEnvironment.SurfaceMobile, 600, 660)
                .Ends(TerminalState.Landed).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Prelaunch,Flight,SurfaceStay", Kinds(plan));
            Assert.Single(Events(plan, ProfileEventKind.Touchdown));
        }

        // catches: a hop into space that never reaches an orbit read as an ascent.
        [Fact]
        public void SuborbitalHop_ReadsSuborbitalFlightThenReentry()
        {
            Recording r = new ProfilePiece("hop").Launch()
                .Sec(SegmentEnvironment.SurfaceStationary, 0, 5)
                .Sec(SegmentEnvironment.Atmospheric, 5, 100, alt0: 100, alt1: 70000).Engine(4, 60)
                .Sec(SegmentEnvironment.ExoBallistic, 100, 200, alt0: 70000, alt1: 85000)
                .Sec(SegmentEnvironment.ExoBallistic, 200, 300, alt0: 85000, alt1: 70000)
                .Sec(SegmentEnvironment.Atmospheric, 300, 500, alt0: 70000, alt1: 70)
                .Sec(SegmentEnvironment.SurfaceStationary, 500, 560)
                .Ends(TerminalState.Landed).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Prelaunch,SuborbitalHop,Reentry,SurfaceStay", Kinds(plan));
            Assert.Equal(85000, plan.Phases[1].MaxAltitude, 0);
        }

        // catches: a rocket destroyed in the air during its launch read as an aircraft flight.
        [Fact]
        public void CrashDuringAscent_IsAnAbortedAscent()
        {
            Recording r = new ProfilePiece("crash").Launch()
                .Sec(SegmentEnvironment.SurfaceStationary, 0, 5)
                .Sec(SegmentEnvironment.Atmospheric, 5, 40, alt0: 100, alt1: 4000).Engine(4, 40)
                .Ends(TerminalState.Destroyed).Build();
            PhasePlan plan = Analyze(new List<ProfileAnchorInput>
            {
                new ProfileAnchorInput { Kind = ProfileEventKind.End, UT = 40, Terminal = TerminalState.Destroyed },
            }, r);
            Assert.Equal("Prelaunch,Ascent", Kinds(plan));
            Assert.Equal(MissionPhaseAnalyzer.RuleAbortedAscent, plan.Phases[1].Rule);
            Assert.Contains(plan.Phases[1].Events, e => e.Kind == ProfileEventKind.End);
        }

        // catches: an airless ascent (surface -> low band -> space) not read as an ascent.
        [Fact]
        public void AirlessLaunch_ReadsAscentThroughTheLowBand()
        {
            Recording r = new ProfilePiece("munlaunch").StartsOn("Mun")
                .Sec(SegmentEnvironment.SurfaceStationary, 0, 10, "Mun", 3000, 3000)
                .Sec(SegmentEnvironment.Approach, 10, 60, "Mun", 3000, 25000).Engine(9, 55)
                .Sec(SegmentEnvironment.ExoBallistic, 60, 100, "Mun", 25000, 30000)
                .Cp(100, 400, "Mun", 190000, 0.5)
                .Sec(SegmentEnvironment.ExoPropulsive, 400, 420, "Mun", 30000, 30000).Engine(399, 419)
                .Cp(420, 3000, "Mun", 235000, 0.01)
                .Ends(TerminalState.Orbiting, "Mun", 235000, 0.01, 3000).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("SurfaceStay,Ascent,Circularization,Orbit", Kinds(plan));
            Assert.Equal(ProfileBurnKind.Circularization, Burns(plan).Single().BurnKind);
        }

        // ------------------------------------------------------------------ atmosphere passes

        private static ProfilePiece FromSpace(string id, double sma, double ecc)
            => new ProfilePiece(id).Cp(0, 1000, "Kerbin", sma, ecc);

        // catches: rule direct-entry (one pass, no exit) not reading as Reentry.
        [Fact]
        public void DirectEntry_IsReentryFromTheFirstEntry()
        {
            Recording r = FromSpace("de", 6000000, 0.89)
                .Sec(SegmentEnvironment.Atmospheric, 1000, 1300, alt0: 70000, alt1: 100).Chute(1200)
                .Sec(SegmentEnvironment.SurfaceStationary, 1300, 1400)
                .Ends(TerminalState.Landed).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Orbit,Reentry,SurfaceStay", Kinds(plan));
            Assert.Equal(MissionPhaseAnalyzer.RuleDirectEntry, plan.Phases[1].Rule);
            Assert.Single(Events(plan, ProfileEventKind.AtmosphereEntry));
            Assert.Single(Events(plan, ProfileEventKind.ParachutesDeployed));
        }

        // catches: rule skip-entry: a hop out under 2 minutes is the same pass (a Skip event),
        // never a second pass.
        [Fact]
        public void SkipEntry_HopUnderTwoMinutes_IsOnePassWithASkip()
        {
            Recording r = FromSpace("skip", 6000000, 0.89)
                .Sec(SegmentEnvironment.Atmospheric, 1000, 1100, alt0: 70000, alt1: 55000)
                .Sec(SegmentEnvironment.ExoBallistic, 1100, 1160, alt0: 70000, alt1: 71000)
                .Sec(SegmentEnvironment.Atmospheric, 1160, 1500, alt0: 70000, alt1: 100)
                .Sec(SegmentEnvironment.SurfaceStationary, 1500, 1600)
                .Ends(TerminalState.Landed).Build();
            PhasePlan plan = Analyze(r);
            ProfilePhase reentry = plan.Phases.Single(p => p.Kind == ProfilePhaseKind.Reentry);
            Assert.Equal(MissionPhaseAnalyzer.RuleSkipEntry, reentry.Rule);
            Assert.Equal(1000, reentry.StartUT, 3);
            Assert.Single(Events(plan, ProfileEventKind.Skip));
            Assert.Empty(Events(plan, ProfileEventKind.AtmosphereExit));
        }

        // catches: a hop out over 2 minutes folded into the same pass: two passes, the last
        // landing, read as brake-then-land (Aerocapture, then Reentry from the last entry).
        [Fact]
        public void HopOverTwoMinutes_ThenLanding_IsBrakeThenLand()
        {
            Recording r = FromSpace("btl", -1000000, 1.65)
                .Sec(SegmentEnvironment.Atmospheric, 1000, 1100, alt0: 70000, alt1: 50000)
                .Sec(SegmentEnvironment.ExoBallistic, 1100, 2000, alt0: 70000, alt1: 70000)
                .Sec(SegmentEnvironment.Atmospheric, 2000, 2400, alt0: 70000, alt1: 100)
                .Sec(SegmentEnvironment.SurfaceStationary, 2400, 2500)
                .Ends(TerminalState.Landed).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Transfer,Aerocapture,Reentry,SurfaceStay", Kinds(plan));
            Assert.Equal(MissionPhaseAnalyzer.RuleBrakeThenLand, plan.Phases[2].Rule);
            Assert.Equal(2000, plan.Phases[2].StartUT, 3);
        }

        // catches: rule aerocapture with STORED evidence: escaping before, bound after, a raise
        // burn ends the phase and names itself Periapsis raise.
        [Fact]
        public void Aerocapture_StoredEvidence_EndsAtTheRaiseBurn()
        {
            Recording r = FromSpace("ac", -1000000, 1.65)
                .Sec(SegmentEnvironment.Atmospheric, 1000, 1200, alt0: 70000, alt1: 45000)
                .Cp(1200, 3000, "Kerbin", 900000, 0.27)
                .Sec(SegmentEnvironment.ExoPropulsive, 3000, 3010, alt0: 300000, alt1: 300000).Engine(2999, 3009)
                .Cp(3010, 6000, "Kerbin", 1000000, 0.2)
                .Ends(TerminalState.Orbiting, "Kerbin", 1000000, 0.2, 6000).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Transfer,Aerocapture,Orbit", Kinds(plan));
            Assert.Equal(MissionPhaseAnalyzer.RuleAerocapture, plan.Phases[1].Rule);
            Assert.Equal(ProfileBurnKind.PeriapsisRaise, Burns(plan).Single().BurnKind);
            Assert.Equal(3009, plan.Phases[1].EndUT, 3);
        }

        // catches: rule aerocapture with STRUCTURAL evidence only (no orbit sample after the
        // pass; the next pass recurs with no burn, so the orbit after was bound).
        [Fact]
        public void Aerocapture_StructuralEvidence_TwoPassesThenRaise()
        {
            Recording r = FromSpace("acs", -1000000, 1.65)
                .Sec(SegmentEnvironment.Atmospheric, 1000, 1200, alt0: 70000, alt1: 45000)
                .Sec(SegmentEnvironment.ExoBallistic, 1200, 4000, alt0: 70000, alt1: 70000)
                .Sec(SegmentEnvironment.Atmospheric, 4000, 4200, alt0: 70000, alt1: 50000)
                .Sec(SegmentEnvironment.ExoBallistic, 4200, 5000, alt0: 70000, alt1: 200000)
                .Sec(SegmentEnvironment.ExoPropulsive, 5000, 5010, alt0: 200000, alt1: 200000).Engine(4999, 5009)
                .Cp(5010, 8000, "Kerbin", 1000000, 0.2)
                .Ends(TerminalState.Orbiting, "Kerbin", 1000000, 0.2, 8000).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Transfer,Aerocapture,Orbit", Kinds(plan));
            Assert.Equal(2, Events(plan, ProfileEventKind.Pass).Count);
        }

        // catches: rule aerobraking: a campaign of passes from a bound orbit with Pe in the
        // air, one Pass n event each, ended by the raise burn.
        [Fact]
        public void AerobrakingCampaign_OnePassEventEach_EndsAtTheRaise()
        {
            Recording r = FromSpace("ab", 900000, 0.27)
                .Sec(SegmentEnvironment.Atmospheric, 1000, 1200, alt0: 70000, alt1: 55000)
                .Cp(1200, 3000, "Kerbin", 850000, 0.23)
                .Sec(SegmentEnvironment.Atmospheric, 3000, 3200, alt0: 70000, alt1: 56000)
                .Cp(3200, 5000, "Kerbin", 800000, 0.18)
                .Sec(SegmentEnvironment.Atmospheric, 5000, 5200, alt0: 70000, alt1: 57000)
                .Cp(5200, 7000, "Kerbin", 760000, 0.13)
                .Sec(SegmentEnvironment.ExoPropulsive, 7000, 7010, alt0: 100000, alt1: 100000).Engine(6999, 7009)
                .Cp(7010, 9000, "Kerbin", 780000, 0.05)
                .Ends(TerminalState.Orbiting, "Kerbin", 780000, 0.05, 9000).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Orbit,Aerobraking,Orbit", Kinds(plan));
            Assert.Equal(MissionPhaseAnalyzer.RuleAerobraking, plan.Phases[1].Rule);
            Assert.Equal(new[] { 1, 2, 3 }, Events(plan, ProfileEventKind.Pass).Select(e => e.Count).ToArray());
        }

        // catches: an aerobraking campaign that ends in a landing read as anything but
        // Aerobraking then Reentry from the final pass's entry.
        [Fact]
        public void AerobrakingCampaign_EndingInALanding_EndsWithReentry()
        {
            Recording r = FromSpace("abl", 900000, 0.27)
                .Sec(SegmentEnvironment.Atmospheric, 1000, 1200, alt0: 70000, alt1: 55000)
                .Cp(1200, 3000, "Kerbin", 850000, 0.23)
                .Sec(SegmentEnvironment.Atmospheric, 3000, 3600, alt0: 70000, alt1: 100)
                .Sec(SegmentEnvironment.SurfaceStationary, 3600, 3700)
                .Ends(TerminalState.Landed).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Orbit,Aerobraking,Reentry,SurfaceStay", Kinds(plan));
            Assert.Equal(3000, plan.Phases[2].StartUT, 3);
        }

        // catches: rule braking-flyby: escaping before and after the pass is an atmospheric
        // flyby, a sub-phase of the transfer, never a capture.
        [Fact]
        public void BrakingFlyby_IsAnAtmosphericFlybySubPhase()
        {
            Recording r = new ProfilePiece("bf")
                .Cp(0, 1000, "Kerbin", -1000000, 1.65)
                .Sec(SegmentEnvironment.Atmospheric, 1000, 1100, alt0: 70000, alt1: 60000)
                .Cp(1100, 3000, "Kerbin", -1200000, 1.6)
                .Ends(TerminalState.Orbiting, "Kerbin", -1200000, 1.6, 3000).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Transfer", Kinds(plan));
            ProfilePhase sub = plan.Phases[0].SubPhases.Single();
            Assert.Equal(ProfilePhaseKind.AtmosphericFlyby, sub.Kind);
            Assert.Equal(MissionPhaseAnalyzer.RuleBrakingFlyby, sub.Rule);
        }

        // catches: rule unknown-pass: a pass that exits with nothing later to decide stays a
        // neutral Atmosphere pass.
        [Fact]
        public void UnknownPass_StaysANeutralAtmospherePass()
        {
            Recording r = new ProfilePiece("up")
                .Sec(SegmentEnvironment.ExoBallistic, 0, 500, alt0: 200000, alt1: 70000)
                .Sec(SegmentEnvironment.Atmospheric, 500, 600, alt0: 70000, alt1: 60000)
                .Sec(SegmentEnvironment.ExoBallistic, 600, 1000, alt0: 70000, alt1: 150000)
                .Build();
            PhasePlan plan = Analyze(r);
            Assert.Contains(plan.Phases, p => p.Kind == ProfilePhaseKind.AtmospherePass
                && p.Rule == MissionPhaseAnalyzer.RuleUnknownPass);
        }

        // ------------------------------------------------------------------ the pass table itself

        private static MissionPhaseAnalyzer.PassGroupFacts Pass(int passes, int hops, ProfileOrbitEnergy before,
            bool exits, bool exitsBound = false, bool exitsEscaping = false, bool raise = false, bool endsInSpace = false)
            => new MissionPhaseAnalyzer.PassGroupFacts
            {
                Passes = passes, Hops = hops, EnergyBefore = before, Exits = exits, ExitsBound = exitsBound,
                ExitsEscaping = exitsEscaping, RaiseBurn = raise, LineEndsInSpace = endsInSpace,
            };

        // catches: a row of the atmosphere-pass table matching the wrong facts (one positive and
        // one negative case per rule, through the same matcher the analyzer uses).
        [Theory]
        [InlineData(MissionPhaseAnalyzer.RuleDirectEntry, 1, 0, (int)ProfileOrbitEnergy.Bound, false, false, false, false, false)]
        [InlineData(MissionPhaseAnalyzer.RuleSkipEntry, 1, 2, (int)ProfileOrbitEnergy.Escaping, false, false, false, false, false)]
        [InlineData(MissionPhaseAnalyzer.RuleBrakeThenLand, 3, 0, (int)ProfileOrbitEnergy.Escaping, false, false, false, false, false)]
        [InlineData(MissionPhaseAnalyzer.RuleAerocapture, 1, 0, (int)ProfileOrbitEnergy.Escaping, true, true, false, true, false)]
        [InlineData(MissionPhaseAnalyzer.RuleAerocapture, 1, 0, (int)ProfileOrbitEnergy.Escaping, true, true, false, false, true)]
        [InlineData(MissionPhaseAnalyzer.RuleAerobraking, 2, 0, (int)ProfileOrbitEnergy.Bound, true, false, false, true, false)]
        [InlineData(MissionPhaseAnalyzer.RuleBrakingFlyby, 1, 0, (int)ProfileOrbitEnergy.Escaping, true, false, true, false, true)]
        [InlineData(MissionPhaseAnalyzer.RuleUnknownPass, 1, 0, (int)ProfileOrbitEnergy.Unknown, true, false, false, false, true)]
        [InlineData(MissionPhaseAnalyzer.RuleUnknownPass, 1, 0, (int)ProfileOrbitEnergy.Bound, true, false, false, false, true)]
        public void PassTable_EachRowMatchesItsFacts(string rule, int passes, int hops, int before,
            bool exits, bool exitsBound, bool exitsEscaping, bool raise, bool endsInSpace)
        {
            var facts = Pass(passes, hops, (ProfileOrbitEnergy)before, exits, exitsBound, exitsEscaping, raise, endsInSpace);
            Assert.Equal(rule, MissionPhaseAnalyzer.MatchPassRule(MissionPhaseAnalyzer.PassRules(), facts).Name);
        }

        // catches: a rule table that is not data in priority order, a rule outside its
        // stratum, or a table with no rule that claims every leftover (totality).
        [Fact]
        public void RuleTables_AreOrderedData_InTheirStrata_WithACatchAll()
        {
            MissionPhaseAnalyzer.PassRule[] passes = MissionPhaseAnalyzer.PassRules();
            Assert.All(passes, r => Assert.Equal(MissionPhaseAnalyzer.PassStratum, r.Stratum));
            Assert.Equal(passes.Select(r => r.Priority).OrderBy(p => p), passes.Select(r => r.Priority));
            Assert.Equal(passes.Length, passes.Select(r => r.Priority).Distinct().Count());
            Assert.Equal(passes.Length, passes.Select(r => r.Name).Distinct().Count());
            Assert.True(passes[passes.Length - 1].Match(default(MissionPhaseAnalyzer.PassGroupFacts)));

            MissionPhaseAnalyzer.BurnRule[] burns = MissionPhaseAnalyzer.BurnRules();
            Assert.All(burns, r => Assert.Equal(MissionPhaseAnalyzer.BurnStratum, r.Stratum));
            Assert.Equal(burns.Select(r => r.Priority).OrderBy(p => p), burns.Select(r => r.Priority));
            Assert.Equal(burns.Length, burns.Select(r => r.Priority).Distinct().Count());
            Assert.True(burns[burns.Length - 1].Match(default(MissionPhaseAnalyzer.BurnFacts)));
            // A lower stratum runs before the one that reads it: passes (c) before burns (d).
            Assert.True(MissionPhaseAnalyzer.PassStratum < MissionPhaseAnalyzer.BurnStratum);
        }

        // ------------------------------------------------------------------ burn names (table 4.4)

        private static ProfileOrbit O(ProfileOrbitEnergy e, ProfilePeriapsis pe, string encounter = null,
            ProfileEvidenceTier tier = ProfileEvidenceTier.Stored)
            => new ProfileOrbit { Tier = tier, Energy = e, Periapsis = pe, Encounter = encounter };

        private static readonly ProfileOrbit InOrbit = O(ProfileOrbitEnergy.Bound, ProfilePeriapsis.Clear);
        private static readonly ProfileOrbit FallingO = O(ProfileOrbitEnergy.Bound, ProfilePeriapsis.SubSurface);
        private static readonly ProfileOrbit LeavingO = O(ProfileOrbitEnergy.Bound, ProfilePeriapsis.Clear, "Mun");
        private static readonly ProfileOrbit EscapingO = O(ProfileOrbitEnergy.Escaping, ProfilePeriapsis.Clear);
        private static readonly ProfileOrbit InAir = O(ProfileOrbitEnergy.Bound, ProfilePeriapsis.InAir);

        private static ProfileBurnKind Name(ProfileOrbit before, ProfileOrbit after, bool atmo = true,
            bool inTransfer = false, double toDock = double.NaN, bool plane = false, bool launchTarget = false,
            bool afterPass = false)
        {
            var f = new MissionPhaseAnalyzer.BurnFacts
            {
                BeforeOrbit = before, AfterOrbit = after, Before = before.Kind, After = after.Kind,
                BodyHasAtmosphere = atmo, InTransfer = inTransfer, DockAhead = !double.IsNaN(toDock),
                SecondsToDock = toDock, PlaneChange = plane, TargetIsLaunchBody = launchTarget,
                HasEncounter = !string.IsNullOrEmpty(after.Encounter), AfterPass = afterPass,
            };
            MissionPhaseAnalyzer.BurnRule rule = MissionPhaseAnalyzer.MatchBurnRule(MissionPhaseAnalyzer.BurnRules(), f);
            return rule.Names(f);
        }

        // catches: a row of table 4.4 naming the wrong burn.
        [Fact]
        public void BurnTable_NamesEachRow()
        {
            Assert.Equal(ProfileBurnKind.Circularization, Name(FallingO, InOrbit));
            Assert.Equal(ProfileBurnKind.Injection, Name(FallingO, LeavingO));
            Assert.Equal(ProfileBurnKind.Transfer, Name(InOrbit, LeavingO));
            Assert.Equal(ProfileBurnKind.Return, Name(InOrbit, LeavingO, launchTarget: true));
            Assert.Equal(ProfileBurnKind.Escape, Name(InOrbit, EscapingO));
            Assert.Equal(ProfileBurnKind.Correction, Name(LeavingO, LeavingO));
            Assert.Equal(ProfileBurnKind.Correction, Name(FallingO, FallingO, inTransfer: true));
            Assert.Equal(ProfileBurnKind.Capture, Name(LeavingO, InOrbit));
            Assert.Equal(ProfileBurnKind.CaptureAerobraking, Name(EscapingO, InAir));
            Assert.Equal(ProfileBurnKind.PeriapsisRaise, Name(InAir, InOrbit, afterPass: true));
            Assert.Equal(ProfileBurnKind.Circularization, Name(InAir, InOrbit));
            Assert.Equal(ProfileBurnKind.Deorbit, Name(InOrbit, FallingO));
            Assert.Equal(ProfileBurnKind.Descent, Name(InOrbit, FallingO, atmo: false));
            Assert.Equal(ProfileBurnKind.Phasing, Name(InOrbit, InOrbit, toDock: 1200));
            Assert.Equal(ProfileBurnKind.Approach, Name(InOrbit, InOrbit, toDock: 300));
            Assert.Equal(ProfileBurnKind.PlaneChange, Name(InOrbit, InOrbit, plane: true));
            Assert.Equal(ProfileBurnKind.OrbitChange, Name(InOrbit, InOrbit));
            Assert.Equal(ProfileBurnKind.Unknown, Name(default(ProfileOrbit), default(ProfileOrbit)));
        }

        // catches: hindsight missing: the same in-orbit burn is a phasing burn when a dock
        // follows in that orbit and an orbit change when none does.
        [Fact]
        public void InOrbitBurn_IsPhasingOnlyWhenADockFollows()
        {
            Func<Recording> line = () => new ProfilePiece("ph")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1010).Engine(999, 1009)
                .Cp(1010, 3000, "Kerbin", 700000, 0.02)
                .Ends(TerminalState.Orbiting, "Kerbin", 700000, 0.02, 3000).Build();
            PhasePlan alone = Analyze(line());
            Assert.Equal(ProfileBurnKind.OrbitChange, Burns(alone).Single().BurnKind);
            PhasePlan docked = Analyze(new List<ProfileAnchorInput>
            {
                new ProfileAnchorInput { Kind = ProfileEventKind.Docking, UT = 2500, RefName = "Station", OwnShipAbsorbed = true },
            }, line());
            Assert.Equal(ProfileBurnKind.Phasing, Burns(docked).Single().BurnKind);
            Assert.Equal("Orbit,Rendezvous,Docked", Kinds(docked));
        }

        // catches: a plane change read as an orbit change (inclination moves over 2 deg, Ap and
        // Pe hold within 5 %).
        [Fact]
        public void PlaneChange_InclinationMovesShapeHolds()
        {
            Recording r = new ProfilePiece("pc")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc, inc: 0.1)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1030).Engine(999, 1029)
                .Cp(1030, 3000, "Kerbin", LkoSma, LkoEcc, inc: 8.0)
                .Ends(TerminalState.Orbiting, "Kerbin", LkoSma, LkoEcc, 3000).Build();
            Assert.Equal(ProfileBurnKind.PlaneChange, Burns(Analyze(r)).Single().BurnKind);
        }

        // catches: a Mun transfer ellipse whose Kerbin periapsis is under the surface read as a
        // descent: the encounter (an SOI change before periapsis) beats the periapsis reading.
        [Fact]
        public void Encounter_BeatsASubSurfacePeriapsis()
        {
            Recording r = new ProfilePiece("enc")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1025).Engine(999, 1024)
                .Cp(1025, 5000, "Kerbin", 6050000, 0.934)
                .Cp(5000, 9000, "Mun", -500000, 1.7)
                .Ends(TerminalState.Orbiting, "Mun", -500000, 1.7, 9000).Build();
            PhasePlan plan = Analyze(r);
            ProfileEvent burn = Burns(plan).Single();
            Assert.Equal(ProfileBurnKind.Transfer, burn.BurnKind);
            Assert.Equal(ProfilePeriapsis.SubSurface, burn.After.Periapsis);
            Assert.Equal("Mun", burn.After.Encounter);
            Assert.Equal(ProfileOrbitKind.Leaving, burn.After.Kind);
            Assert.Single(Events(plan, ProfileEventKind.SoiEntry));
        }

        // catches: a flyby read as an arrival (no capture between the SOI entry and exit makes
        // a Flyby sub-phase inside the transfer) and an arrival read as a flyby.
        [Fact]
        public void Flyby_VersusArrival()
        {
            Recording flyby = new ProfilePiece("fb")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1025).Engine(999, 1024)
                .Cp(1025, 5000, "Kerbin", MunTransferSma, MunTransferEcc)
                .Cp(5000, 9000, "Mun", -500000, 1.7)
                .Cp(9000, 20000, "Kerbin", 5000000, 0.85)
                .Ends(TerminalState.Orbiting, "Kerbin", 5000000, 0.85, 20000).Build();
            PhasePlan plan = Analyze(flyby);
            Assert.True(plan.Phases.Count(p => p.Kind == ProfilePhaseKind.Transfer) == 1, Trace(plan));
            ProfilePhase transfer = plan.Phases.Single(p => p.Kind == ProfilePhaseKind.Transfer);
            Assert.Equal(ProfilePhaseKind.Flyby, transfer.SubPhases.Single().Kind);
            Assert.Equal("Mun", transfer.SubPhases.Single().Body);

            Recording arrival = new ProfilePiece("ar")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1025).Engine(999, 1024)
                .Cp(1025, 5000, "Kerbin", MunTransferSma, MunTransferEcc)
                .Cp(5000, 9000, "Mun", -500000, 1.7)
                .Sec(SegmentEnvironment.ExoPropulsive, 9000, 9020, "Mun", 150000, 150000).Engine(8999, 9019)
                .Cp(9020, 20000, "Mun", 350000, 0.01)
                .Ends(TerminalState.Orbiting, "Mun", 350000, 0.01, 20000).Build();
            PhasePlan arrived = Analyze(arrival);
            Assert.Equal("Orbit,Transfer,Orbit", Kinds(arrived));
            Assert.Equal("Mun", arrived.Phases[1].TargetBody);
            Assert.Equal("Mun", arrived.Phases[2].Body);
            Assert.Empty(arrived.Phases[1].SubPhases);
            Assert.Equal(ProfileBurnKind.Capture, Burns(arrived).Last().BurnKind);
        }

        // catches: an SOI exit into a bound clear orbit with nothing later read as an endless
        // transfer.
        [Fact]
        public void SoiExitIntoABoundOrbit_EndsTheTransfer()
        {
            Recording r = new ProfilePiece("sx").StartsOn("Mun")
                .Cp(0, 1000, "Mun", 300000, 0.01)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1010, "Mun", 100000, 100000).Engine(999, 1009)
                .Cp(1010, 5000, "Mun", -900000, 1.3)
                .Cp(5000, 20000, "Kerbin", 9000000, 0.2)
                .Ends(TerminalState.Orbiting, "Kerbin", 9000000, 0.2, 20000).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Orbit,Transfer,Orbit", Kinds(plan));
            Assert.Equal("Kerbin", plan.Phases[2].Body);
            Assert.Equal(ProfileBurnKind.Return, Burns(plan).Single().BurnKind);
        }

        // catches: an airless descent: the burn that drops Pe below the surface starts a
        // Descent, the landing burn in the low band is named, touchdown ends it.
        [Fact]
        public void AirlessDescent_DescentBurnLandingBurnTouchdown()
        {
            Recording r = new ProfilePiece("desc").StartsOn("Mun")
                .Cp(0, 1000, "Mun", 230000, 0.01)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1005, "Mun", 30000, 30000).Engine(999, 1004)
                .Sec(SegmentEnvironment.ExoBallistic, 1005, 1500, "Mun", 30000, 12000)
                .Sec(SegmentEnvironment.Approach, 1500, 1600, "Mun", 12000, 3000).Engine(1540, 1595)
                .Sec(SegmentEnvironment.SurfaceStationary, 1600, 1700, "Mun", 3000, 3000)
                .Ends(TerminalState.Landed).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Orbit,Descent,SurfaceStay", Kinds(plan));
            List<ProfileEvent> burns = Burns(plan);
            Assert.Equal(ProfileBurnKind.Descent, burns[0].BurnKind);
            Assert.Equal(ProfileBurnKind.Landing, burns[1].BurnKind);
            Assert.True(burns[1].FloorBurn);
            ProfileEvent touchdown = Events(plan, ProfileEventKind.Touchdown).Single();
            Assert.Equal(3000, touchdown.SurfaceAltitude, 1);
            Assert.Same(plan.Phases[1], plan.Phases.First(p => p.Events.Contains(touchdown)));
        }

        // ------------------------------------------------------------------ evidence

        // catches: a terminal orbit taken as the orbit after a burn that happened after its
        // epoch (bdock-recorded's root carries the phasing burn's elements while later burns
        // follow).
        [Fact]
        public void TerminalOrbit_IgnoredWhenItsEpochIsBeforeTheLastBurn()
        {
            Recording r = new ProfilePiece("te")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1010).Engine(999, 1009)
                .Sec(SegmentEnvironment.ExoBallistic, 1010, 1050, alt0: 80000, alt1: 95000)
                .Ends(TerminalState.Orbiting, "Kerbin", 700000, 0.02, 500).Build();
            ProfileEvent burn = Burns(Analyze(r)).Single();
            Assert.False(burn.After.IsKnown);

            Recording late = new ProfilePiece("tl")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1010).Engine(999, 1009)
                .Sec(SegmentEnvironment.ExoBallistic, 1010, 1050, alt0: 80000, alt1: 95000)
                .Ends(TerminalState.Orbiting, "Kerbin", 700000, 0.02, 1050).Build();
            ProfileEvent after = Burns(Analyze(late)).Single();
            Assert.Equal(ProfileEvidenceTier.Stored, after.After.Tier);
        }

        // catches: a predicted checkpoint (an extrapolation, never flown) read as evidence.
        [Fact]
        public void PredictedCheckpoint_IsNeverEvidence()
        {
            Recording r = new ProfilePiece("pr")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1010).Engine(999, 1009)
                .Cp(1010, 3000, "Kerbin", 700000, 0.02, predicted: true)
                .Build();
            Assert.False(Burns(Analyze(r)).Single().After.IsKnown);
        }

        // catches: an on-rails section classified by its environment label instead of its
        // conic (a background span keeps one fixed Atmospheric label).
        [Fact]
        public void OnRailsSection_ClassifiedByItsConic_NotItsLabel()
        {
            Recording r = new ProfilePiece("or")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc, env: SegmentEnvironment.Atmospheric)
                .Ends(TerminalState.Orbiting, "Kerbin", LkoSma, LkoEcc, 1000).Build();
            PhasePlan plan = Analyze(r);
            Assert.Equal("Orbit", Kinds(plan));
            Assert.Empty(Events(plan, ProfileEventKind.AtmosphereEntry));
        }

        // catches: an altitude that held after a burn (no orbit sample) not read as an inferred
        // bound, clear orbit, and its tier lost.
        [Fact]
        public void HeldAltitude_InfersABoundClearOrbit()
        {
            Recording r = new ProfilePiece("hold").StartsOn("Mun")
                .Cp(0, 1000, "Mun", -500000, 1.7)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1020, "Mun", 140000, 140000).Engine(999, 1019)
                .Sec(SegmentEnvironment.ExoBallistic, 1020, 1200, "Mun", 140000, 140008)
                .Build();
            PhasePlan plan = Analyze(r);
            ProfileEvent capture = Burns(plan).Single();
            Assert.Equal(ProfileBurnKind.Capture, capture.BurnKind);
            Assert.Equal(ProfileEvidenceTier.Inferred, capture.After.Tier);
            Assert.Equal(140004, capture.After.HeldAltitude, 0);
            Assert.Contains(plan.Gaps, g => g.Reason == MissionPhaseAnalyzer.GapOrbitInferred);
            Assert.True(double.IsNaN(capture.SpentEstimate));
        }

        // ------------------------------------------------------------------ the burn definition

        // catches: the shared burn definition drifting: engine-off gaps up to 10 s merge, a
        // longer gap splits, an on-rails gap ends a burn, an RCS tap is dropped.
        [Fact]
        public void BurnDefinition_GapsRailsAndTaps()
        {
            Recording r = new ProfilePiece("bd")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1010).Engine(999, 1009)
                .Sec(SegmentEnvironment.ExoBallistic, 1010, 1018)
                .Sec(SegmentEnvironment.ExoPropulsive, 1018, 1028).Engine(1017, 1027)
                .Sec(SegmentEnvironment.ExoBallistic, 1028, 1060)
                .Sec(SegmentEnvironment.ExoPropulsive, 1060, 1070).Engine(1059, 1069)
                .Cp(1070, 1080, "Kerbin", 700000, 0.02, env: SegmentEnvironment.ExoPropulsive)
                .Sec(SegmentEnvironment.ExoPropulsive, 1080, 1090).Engine(1079, 1089)
                .Sec(SegmentEnvironment.ExoBallistic, 1090, 1200)
                .Sec(SegmentEnvironment.ExoPropulsive, 1200, 1200.2)
                .Cp(1200.2, 3000, "Kerbin", 710000, 0.02)
                .Build();
            PhasePlan bdPlan = Analyze(r);
            List<ProfileEvent> burns = Burns(bdPlan);
            Assert.True(3 == burns.Count, Trace(bdPlan));
            Assert.Equal(ProfileBurnKind.OrbitChange, burns[2].BurnKind);
            Assert.Equal(999, burns[0].UT, 3);
            Assert.Equal(1027, burns[0].EndUT, 3);
            Assert.Equal(1059, burns[1].UT, 3);
            Assert.Equal(1079, burns[2].UT, 3);
        }

        // catches: a burn carried across the atmosphere exit cut in two.
        [Fact]
        public void BurnAcrossTheAtmosphereExit_StaysOneBurn()
        {
            Recording r = new ProfilePiece("ax").Launch()
                .Sec(SegmentEnvironment.SurfaceStationary, 0, 5)
                .Sec(SegmentEnvironment.Atmospheric, 5, 180, alt0: 100, alt1: 70000).Engine(4, 200)
                .Sec(SegmentEnvironment.ExoPropulsive, 180, 200, alt0: 70000, alt1: 75000)
                .Cp(200, 3000, "Kerbin", LkoSma, LkoEcc)
                .Ends(TerminalState.Orbiting, "Kerbin", LkoSma, LkoEcc, 3000).Build();
            PhasePlan plan = Analyze(r);
            ProfileEvent burn = Burns(plan).Single();
            Assert.Equal(4, burn.UT, 3);
            Assert.Equal(200, burn.EndUT, 3);
        }

        // ------------------------------------------------------------------ delta-v

        private const double KerbinMu = 3.5316e12;

        private static ProfileOrbit Stored(double sma, double ecc, double mEp, double epoch = 0)
            => MissionPhaseAnalyzer.FromSegment(new OrbitSegment
            {
                bodyName = "Kerbin", semiMajorAxis = sma, eccentricity = ecc, meanAnomalyAtEpoch = mEp, epoch = epoch,
            }, RecordingOptimizer.StockBodyFacts("Kerbin"), ProfileEvidenceTier.Stored);

        // catches: the estimate's propagation off against a vis-viva hand check (the burn at
        // apoapsis of the falling orbit, prograde, into the circle of that radius).
        [Fact]
        public void Estimate_Circularization_MatchesVisViva()
        {
            double a1 = 600000 + 40000, e1 = 0.1, rAp = a1 * (1 + e1);
            ProfileOrbit before = Stored(a1, e1, Math.PI);
            ProfileOrbit after = Stored(rAp, 0.0, Math.PI);
            double expected = Math.Sqrt(KerbinMu / rAp) - Math.Sqrt(KerbinMu * (2 / rAp - 1 / a1));
            double est = MissionPhaseAnalyzer.EstimateDeltaV(RecordingOptimizer.StockBodyFacts("Kerbin"), before, after, 0);
            Assert.Equal(expected, est, 3);
        }

        // catches: the estimate off on a Hohmann departure (at periapsis, circle to ellipse).
        [Fact]
        public void Estimate_HohmannLeg_MatchesVisViva()
        {
            double r1 = 680000, r2 = 12000000, a = 0.5 * (r1 + r2), e = (r2 - r1) / (r2 + r1);
            double expected = Math.Sqrt(KerbinMu * (2 / r1 - 1 / a)) - Math.Sqrt(KerbinMu / r1);
            double est = MissionPhaseAnalyzer.EstimateDeltaV(RecordingOptimizer.StockBodyFacts("Kerbin"),
                Stored(r1, 0, 0), Stored(a, e, 0), 0);
            Assert.Equal(expected, est, 3);
        }

        // catches: the estimate off on a capture (hyperbolic periapsis into the circle there).
        [Fact]
        public void Estimate_Capture_MatchesVisViva()
        {
            double rp = 700000, aHyp = -2000000, eHyp = 1 - rp / aHyp;
            double expected = Math.Sqrt(KerbinMu * (2 / rp - 1 / aHyp)) - Math.Sqrt(KerbinMu / rp);
            double est = MissionPhaseAnalyzer.EstimateDeltaV(RecordingOptimizer.StockBodyFacts("Kerbin"),
                Stored(aHyp, eHyp, 0), Stored(rp, 0, 0), 0);
            Assert.Equal(expected, est, 3);
        }

        // catches: a radial component dropped: a burn at the same point that turns a circle
        // into an ellipse with periapsis 90 deg away changes the velocity's direction, not only
        // its size; the expected value is built from flight-path geometry by hand.
        [Fact]
        public void Estimate_RadialCorrection_IncludesTheRadialPart()
        {
            double r = 700000, e = 0.1;
            double a = r / (1 - e * e); // p = r at true anomaly 90 deg
            // True anomaly 90 deg -> eccentric anomaly E = acos(e), mean anomaly E - e sin E.
            double ecc = Math.Acos(e);
            double m = ecc - e * Math.Sin(ecc);
            double vCirc = Math.Sqrt(KerbinMu / r);
            double h = Math.Sqrt(KerbinMu * a * (1 - e * e));
            double vTangential = h / r;
            double vRadial = KerbinMu / h * e; // e sin(nu) with nu = 90 deg
            double expected = Math.Sqrt((vTangential - vCirc) * (vTangential - vCirc) + vRadial * vRadial);
            // The circle's argument of position must match: periapsis of the ellipse sits 90 deg
            // behind the vessel, so the circle's mean anomaly is 90 deg with argPe 0 (same point).
            ProfileOrbit before = Stored(r, 0, Math.PI / 2);
            ProfileOrbit after = Stored(a, e, m);
            double est = MissionPhaseAnalyzer.EstimateDeltaV(RecordingOptimizer.StockBodyFacts("Kerbin"), before, after, 0);
            Assert.Equal(expected, est, 2);
        }

        // catches: an estimate offered on an inferred side, past the 120 s cap, or summed.
        [Fact]
        public void Estimate_RefusedOnInferredSidesAndLongBurns()
        {
            Assert.True(double.IsNaN(MissionPhaseAnalyzer.EstimateDeltaV(RecordingOptimizer.StockBodyFacts("Kerbin"),
                Stored(LkoSma, LkoEcc, 0), new ProfileOrbit { Tier = ProfileEvidenceTier.Inferred }, 0)));
            Recording r = new ProfilePiece("long")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1200).Engine(999, 1199)
                .Cp(1200, 3000, "Kerbin", 900000, 0.2)
                .Build();
            PhasePlan plan = Analyze(r);
            ProfileEvent burn = Burns(plan).Single();
            Assert.True(double.IsNaN(burn.SpentEstimate));
            Assert.Equal(MissionPhaseAnalyzer.GapDeltaVLongBurn, burn.DeltaVGap);
            Assert.True(double.IsNaN(plan.Phases[0].SpentRecorded));
        }

        // catches: Spent / Left not read from the recorded snapshots first, a staging's dropped
        // fuel counted as spent, or the line total missing.
        [Fact]
        public void RecordedDeltaV_SpentLeftAndAStagingDrop()
        {
            Recording r = new ProfilePiece("dv")
                .Cp(0, 1000, "Kerbin", LkoSma, LkoEcc)
                .Dv(0, DeltaVTrigger.Start, 3400)
                .Sec(SegmentEnvironment.ExoPropulsive, 1000, 1030).Engine(999, 1029)
                .Dv(999.5, DeltaVTrigger.BurnStart, 3400)
                .Dv(1010, DeltaVTrigger.Staging, 3000)
                .Dv(1012, DeltaVTrigger.Staging, 2700)
                .Dv(1029.5, DeltaVTrigger.BurnEnd, 2400)
                .Cp(1030, 3000, "Kerbin", 700000, 0.02)
                .Dv(3000, DeltaVTrigger.End, 2400)
                .Build();
            var anchors = new List<ProfileAnchorInput>
            {
                new ProfileAnchorInput { Kind = ProfileEventKind.StageSeparation, UT = 1010, Count = 2 },
            };
            PhasePlan plan = Analyze(anchors, r);
            ProfileEvent burn = Burns(plan).Single();
            Assert.Equal(700, burn.SpentRecorded, 3);
            Assert.Equal(2400, burn.LeftRecorded, 3);
            ProfileEvent stage = Events(plan, ProfileEventKind.StageSeparation).Single();
            Assert.Equal(300, stage.StagingDrop, 3);
            Assert.Equal(2400, plan.LeftAtEnd, 3);
            Assert.Equal(700, plan.SpentRecorded, 3);
        }

        // ------------------------------------------------------------------ EVA lines

        // catches: EVA lines going through the orbit rules instead of their own small set.
        [Fact]
        public void EvaLines_SpaceSurfaceAndJetpack()
        {
            Recording space = new ProfilePiece("eva1", "Jebediah Kerman").Eva("Jebediah Kerman")
                .Sec(SegmentEnvironment.ExoBallistic, 0, 100).Ends(TerminalState.Boarded).Build();
            Assert.Equal("Eva", Kinds(Analyze(space)));

            Recording surface = new ProfilePiece("eva2", "Bill Kerman").Eva("Bill Kerman").StartsOn("Mun")
                .Sec(SegmentEnvironment.SurfaceStationary, 0, 100, "Mun")
                .Sec(SegmentEnvironment.Approach, 100, 130, "Mun", 3000, 3100)
                .Sec(SegmentEnvironment.SurfaceMobile, 130, 200, "Mun")
                .Ends(TerminalState.Landed).Build();
            PhasePlan plan = Analyze(surface);
            Assert.Equal("EvaSurface,JetpackFlight,EvaSurface", Kinds(plan));
            Assert.Empty(Burns(plan));
            Assert.Empty(Events(plan, ProfileEventKind.Liftoff));
        }

        // ------------------------------------------------------------------ include set, totality

        // catches: the include set ignored: an excluded span drops the events inside it and
        // clips the phase it overlaps; a phase wholly inside it is dropped.
        [Fact]
        public void ExcludedSpan_ClipsPhasesAndDropsEvents()
        {
            var input = new VesselLineInput
            {
                LineId = "ex", LaunchBodyName = "Kerbin", BodyFacts = RecordingOptimizer.StockBodyFacts,
            };
            input.Pieces.Add(Launch().Ends(TerminalState.Orbiting, "Kerbin", LkoSma, LkoEcc, 2000).Build());
            input.ExcludedSpans.Add(new ProfileSpan(150, 1000));
            PhasePlan plan = MissionPhaseAnalyzer.Analyze(input);
            Assert.DoesNotContain(plan.Phases, p => p.Kind == ProfilePhaseKind.Circularization);
            Assert.Empty(Burns(plan));
            ProfilePhase ascent = plan.Phases.Single(p => p.Kind == ProfilePhaseKind.Ascent);
            Assert.True(ascent.Clipped);
            Assert.Equal(150, ascent.EndUT, 3);
            Assert.True(plan.Phases.Single(p => p.Kind == ProfilePhaseKind.Orbit).Clipped);
            Assert.True(plan.ClippedByInclude >= 2);
            Assert.Equal(1, plan.DroppedByInclude);
        }

        // catches: an empty line crashing or claiming phases it has no sections for.
        [Fact]
        public void EmptyLine_IsAnEmptyPlanWithASectionGap()
        {
            PhasePlan plan = MissionPhaseAnalyzer.Analyze(new VesselLineInput { LineId = "none" });
            Assert.Empty(plan.Phases);
            Assert.Contains(plan.Gaps, g => g.Reason == MissionPhaseAnalyzer.GapSectionMissing);
        }

        // catches: two pieces sharing an instant (a chain boundary) claiming the same span
        // twice; the later piece wins and the phases still tile.
        [Fact]
        public void PiecesSharingAnInstant_AreDeduplicatedByUT()
        {
            Recording a = new ProfilePiece("p1").Cp(0, 1000, "Kerbin", LkoSma, LkoEcc).Build();
            Recording b = new ProfilePiece("p2").Cp(900, 2000, "Kerbin", LkoSma, LkoEcc)
                .Ends(TerminalState.Orbiting, "Kerbin", LkoSma, LkoEcc, 2000).Build();
            PhasePlan plan = Analyze(a, b);
            Assert.Equal("Orbit", Kinds(plan));
            Assert.Equal(0, plan.Phases[0].StartUT, 3);
            Assert.Equal(2000, plan.Phases[0].EndUT, 3);
        }

        // catches: culture-dependent numbers in the cells (a test host under de-DE).
        [Fact]
        public void Labels_AreInvariantUnderAnotherCulture()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("11,524 km", ProfileLabels.Distance(11524000));
                Assert.Equal("3,854 m", ProfileLabels.Distance(3854));
                Assert.Equal("1,022", ProfileLabels.DeltaV(1022.4));
                Assert.Equal("0.1\u00B0", ProfileLabels.Inclination(0.1));
                Assert.Equal("3m 52s", ProfileLabels.Duration(232.9));
                Assert.Equal("2h 26m", ProfileLabels.Duration(2 * 3600 + 26 * 60 + 59));
                Assert.Equal("4d 2h", ProfileLabels.Duration(4 * 21600 + 2 * 3600 + 5));
                Assert.Equal("Approach burns (3)", ProfileLabels.BurnLabel(ProfileBurnKind.Approach, 3));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
