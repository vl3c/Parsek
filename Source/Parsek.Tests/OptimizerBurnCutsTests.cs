using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static Parsek.Tests.OptimizerSplitShapes;

namespace Parsek.Tests
{
    /// <summary>
    /// The version-3 split rules (RecordingOptimizer.BurnCuts.cs): a space row is cut where a
    /// burn changes the orbit kind (falling / in orbit / leaving), one cell per case of
    /// docs/handoff/recording-split-review/atmosphere-pass-analysis.md section 7, the deorbit
    /// row on every body (owner 2026-11-20), the staging gap, the Mun transfer ellipse, the
    /// mid-course correction, the powered flyby, recordings that start / end mid-burn, the
    /// short-row and interplanetary guards, EVA / debris, the version-1 / version-2 pins and
    /// the merge refusal. Every shape runs the real optimization pass twice; the second pass
    /// must change nothing.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerBurnCutsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OptimizerBurnCutsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ------------------------------------------------------------ shapes

        private struct Sec
        {
            internal SegmentEnvironment Env;
            internal double Duration;
            internal string Body;
            internal bool Rails;
            internal double Sma;
            internal double Ecc;
            internal double MeanAnomaly;
        }

        private static Sec S(SegmentEnvironment env, double d, string body)
            => new Sec { Env = env, Duration = d, Body = body };

        private static Sec PadS(double d, string body = "Kerbin") => S(SegmentEnvironment.SurfaceStationary, d, body);
        private static Sec GroundS(double d, string body = "Kerbin") => S(SegmentEnvironment.SurfaceMobile, d, body);
        private static Sec AtmoS(double d, string body = "Kerbin") => S(SegmentEnvironment.Atmospheric, d, body);
        private static Sec ApproachS(double d, string body = "Mun") => S(SegmentEnvironment.Approach, d, body);
        private static Sec CoastS(double d, string body = "Kerbin") => S(SegmentEnvironment.ExoBallistic, d, body);
        private static Sec BurnS(double d, string body = "Kerbin") => S(SegmentEnvironment.ExoPropulsive, d, body);

        /// <summary>An on-rails coast with exact elements (a time-warped stretch).</summary>
        private static Sec Rails(double d, string body, double sma, double ecc, double meanAnomaly = 1.0)
            => new Sec
            {
                Env = SegmentEnvironment.ExoBallistic, Duration = d, Body = body, Rails = true,
                Sma = sma, Ecc = ecc, MeanAnomaly = meanAnomaly,
            };

        // Kerbin: floor 670 km radius, SOI 84,159 km. Mun: floor 200 km, SOI 2,430 km.
        private static Sec KerbinSuborbital(double d) => Rails(d, "Kerbin", 440890, 0.54, 2.8);
        private static Sec Lko(double d) => Rails(d, "Kerbin", 680000, 0.006);
        private static Sec HighKerbinOrbit(double d) => Rails(d, "Kerbin", 900000, 0.02);
        private static Sec MunTransferEllipse(double d, double sma = 6378363) => Rails(d, "Kerbin", sma, 0.894, 0.01);
        private static Sec KerbinEscape(double d) => Rails(d, "Kerbin", -5000000, 1.2, 0.5);
        private static Sec MunArrival(double d) => Rails(d, "Mun", -509906, 1.666, -3.6);
        private static Sec MunDeparture(double d) => Rails(d, "Mun", -509906, 1.666, 0.5);
        private static Sec MunOrbit(double d) => Rails(d, "Mun", 230000, 0.01);
        private static Sec MunFalling(double d) => Rails(d, "Mun", 190000, 0.1, 4.0);

        private static Recording Build(string id, double startUT, params Sec[] sections)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Burn " + id,
                MergeState = MergeState.Immutable,
                VesselPersistentId = 4343,
                SplitRuleVersion = RecordingOptimizer.SplitRuleVersionBurnCuts,
            };
            double cur = startUT;
            foreach (var s in sections)
            {
                double start = cur;
                double end = cur + s.Duration;
                var section = new TrackSection
                {
                    environment = s.Env,
                    referenceFrame = s.Rails ? ReferenceFrame.OrbitalCheckpoint : ReferenceFrame.Absolute,
                    source = s.Rails ? TrackSectionSource.Checkpoint : TrackSectionSource.Active,
                    startUT = start,
                    endUT = end,
                    sampleRateHz = 10f,
                    frames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>(),
                };
                if (s.Rails)
                {
                    var seg = new OrbitSegment
                    {
                        startUT = start, endUT = end, bodyName = s.Body,
                        semiMajorAxis = s.Sma, eccentricity = s.Ecc, inclination = 0,
                        meanAnomalyAtEpoch = s.MeanAnomaly, epoch = start,
                    };
                    section.checkpoints.Add(seg);
                    rec.OrbitSegments.Add(seg);
                }
                else
                {
                    double alt = s.Env == SegmentEnvironment.Atmospheric ? 40000
                        : (s.Env == SegmentEnvironment.SurfaceMobile || s.Env == SegmentEnvironment.SurfaceStationary)
                            ? 70 : 90000;
                    var a = new TrajectoryPoint { ut = start, altitude = alt, bodyName = s.Body };
                    var b = new TrajectoryPoint { ut = end, altitude = alt, bodyName = s.Body };
                    section.frames.Add(a);
                    section.frames.Add(b);
                    rec.Points.Add(a);
                    rec.Points.Add(b);
                }
                rec.TrackSections.Add(section);
                cur = end;
            }
            return rec;
        }

        private static Recording Launch(Recording rec)
        {
            rec.StartBodyName = "Kerbin";
            rec.LaunchSiteName = "Launch Pad";
            return rec;
        }

        private static string Snapshot(string treeId)
        {
            return string.Join(" | ", ChainOf(treeId).Select(r =>
                $"{r.RecordingId}:{r.StartUT:R}-{r.EndUT:R}:{r.SegmentPhase}:{r.SegmentRole}:{r.ChainIndex}"));
        }

        private List<(double start, string role)> Optimize(Recording rec)
        {
            CommitAlone(rec);
            CommittedListOptimizer.RunOptimizationPass();
            string first = Snapshot(rec.TreeId);
            CommittedListOptimizer.RunOptimizationPass();
            Assert.Equal(first, Snapshot(rec.TreeId));
            return ChainOf(rec.TreeId).Select(r => (r.StartUT, r.SegmentRole)).ToList();
        }

        private static double[] Starts(List<(double start, string role)> rows) => rows.Select(r => r.start).ToArray();
        private static string[] Roles(List<(double start, string role)> rows) => rows.Select(r => r.role).ToArray();

        private bool Logged(string a, string b = null)
            => logLines.Any(l => l.Contains("[Optimizer]") && l.Contains(a) && (b == null || l.Contains(b)));

        // ------------------------------------------------------------ case 1, 2: insertion

        [Fact]
        public void Case1_InsertionAfterAnAtmosphereAscent_CircularizationThenOrbit()
        {
            // pad 30, climb 200 (exit at 230), coast to apoapsis on rails, burn 30, orbit.
            var rows = Optimize(Launch(Build("ins", 0,
                PadS(30), AtmoS(200), CoastS(20), KerbinSuborbital(300), BurnS(30), Lko(3000))));
            Assert.Equal(new[] { 0.0, 230.0, 580.0 }, Starts(rows));
            Assert.Equal(new[] { "ascent", "circularization", "orbit" }, Roles(rows));
            Assert.True(Logged("Split candidate (OrbitInsertionBurnEnd)", "splitUT=580.00"));
            Assert.True(Logged("Burn kind change:", "Falling -> InOrbit (Insertion) cut"));
        }

        [Fact]
        public void Case1_ContinuousBurnFromTheAtmosphereIntoOrbit_ReadsTheExitFromTheAscentRow()
        {
            // The burn starts at the atmosphere exit: the ascent row is cut there first, and the
            // space row knows it starts at the floor from the chain piece before it.
            var rows = Optimize(Launch(Build("ins-cont", 0,
                PadS(30), AtmoS(200), BurnS(80), Lko(3000))));
            Assert.Equal(new[] { 0.0, 230.0, 310.0 }, Starts(rows));
            Assert.Equal(new[] { "ascent", "circularization", "orbit" }, Roles(rows));
        }

        [Fact]
        public void Case2_InsertionAfterAnAirlessAscent_ApproachClimbThenCircularization()
        {
            var rows = Optimize(Build("mun-asc", 0,
                GroundS(300, "Mun"), ApproachS(120), BurnS(90, "Mun"), MunOrbit(3000)));
            Assert.Equal(new[] { 0.0, 300.0, 420.0, 510.0 }, Starts(rows));
            Assert.Equal(new[] { "surface", "ascent", "circularization", "orbit" }, Roles(rows));
        }

        // ------------------------------------------------------------ case 3, 4: leaving

        [Fact]
        public void Case3_Departure_OrbitThenTransferFromTheBurnStart()
        {
            var rows = Optimize(Build("dep", 0,
                Lko(3000), BurnS(120), MunTransferEllipse(20000), MunArrival(3000)));
            Assert.Equal(new[] { 0.0, 3000.0 }, Starts(rows));
            Assert.Equal(new[] { "orbit", "transfer" }, Roles(rows));
            Assert.True(Logged("Split candidate (DepartureBurnStart)"));
        }

        [Fact]
        public void MunTransferEllipse_ClosedInsideKerbinsSoi_IsLeavingByTheRecordingsOwnSoiChange()
        {
            // Without the later Mun SOI change the same ellipse is an orbit and nothing is cut.
            var noArrival = Optimize(Build("ellipse-only", 0,
                Lko(3000), BurnS(120), MunTransferEllipse(20000)));
            Assert.Single(noArrival);
            Assert.False(Logged("DepartureBurnStart"));
            Assert.Contains(logLines, l => l.Contains("Split summary") && l.Contains("burnKindKept=1"));
        }

        [Fact]
        public void Case4_DirectAscentToATransfer_AscentInjectionTransfer()
        {
            var rows = Optimize(Launch(Build("inj", 0,
                PadS(30), AtmoS(200), CoastS(20), KerbinSuborbital(300), BurnS(200),
                MunTransferEllipse(20000), MunArrival(3000))));
            Assert.Equal(new[] { 0.0, 230.0, 750.0 }, Starts(rows));
            Assert.Equal(new[] { "ascent", "injection", "transfer" }, Roles(rows));
            Assert.True(Logged("Split candidate (InjectionBurnEnd)"));
        }

        // ------------------------------------------------------------ case 5-8: arriving

        [Fact]
        public void Case5_CaptureByBurn_TransferHoldsTheCaptureBurnThenTargetOrbit()
        {
            var rows = Optimize(Build("cap", 0,
                MunTransferEllipse(20000), MunArrival(3000), BurnS(90, "Mun"), MunOrbit(4000)));
            Assert.Equal(new[] { 0.0, 23090.0 }, Starts(rows));
            Assert.Equal(new[] { "transfer", "orbit" }, Roles(rows));
            Assert.True(Logged("Split candidate (CaptureBurnEnd)"));
        }

        [Fact]
        public void Case6And8_CaptureIntoAnAerobrakingOrbitThenRaise_TransferAerobrakingOrbit()
        {
            // Laythe: floor 550 km radius. Capture into a 522 km periapsis, two passes, raise.
            var falling = Rails(4000, "Laythe", 900000, 0.42, 4.0);
            var rows = Optimize(Build("aerobrake", 0,
                Rails(5000, "Jool", 3.0e7, 0.5), Rails(3000, "Laythe", -400000, 2.5, -2.0),
                BurnS(60, "Laythe"), falling, AtmoS(150, "Laythe"),
                Rails(4000, "Laythe", 880000, 0.40, 1.0), AtmoS(150, "Laythe"),
                Rails(4000, "Laythe", 860000, 0.38, 1.0), BurnS(30, "Laythe"),
                Rails(5000, "Laythe", 900000, 0.30)));
            Assert.Equal(new[] { "transfer", "aerobraking", "orbit" }, Roles(rows));
            Assert.Equal(new[] { 0.0, 8060.0, 20390.0 }, Starts(rows));
            Assert.True(Logged("Split candidate (CaptureToAtmosphereBurnEnd)"));
            Assert.True(Logged("Split candidate (OrbitInsertionBurnEnd)"));
        }

        [Fact]
        public void Case7_Aerocapture_PerRulingB1_Unchanged()
        {
            Func<string, int, Recording> shape = (id, v) =>
            {
                var r = Build(id, 0, Rails(3000, "Laythe", -400000, 2.5, -2.0), AtmoS(200, "Laythe"),
                    Rails(5000, "Laythe", 900000, 0.42, 1.0));
                r.SplitRuleVersion = v;
                return r;
            };
            var v3 = Optimize(shape("aerocap-v3", 3));
            RecordingStore.ResetForTesting();
            var v2 = Optimize(shape("aerocap-v2", 2));
            Assert.Equal(Starts(v2), Starts(v3));
            Assert.Equal(Roles(v2), Roles(v3));
            Assert.Equal(new[] { "space", "aerobraking" }, Roles(v3));
        }

        // ------------------------------------------------------------ case 9-12: descent

        [Fact]
        public void Case9_DeorbitOnAnAirlessBody_DescentFromTheBurnStart()
        {
            var rows = Optimize(Build("mun-deorbit", 0,
                MunOrbit(3000), BurnS(30, "Mun"), MunFalling(600), ApproachS(200), GroundS(100, "Mun")));
            Assert.Equal(new[] { 0.0, 3000.0, 3630.0, 3830.0 }, Starts(rows));
            Assert.Equal(new[] { "orbit", "descent", "approach", "surface" }, Roles(rows));
            Assert.True(Logged("Split candidate (DeorbitBurnStart)"));
        }

        [Fact]
        public void Case9_DeorbitThenAPhysicsCoastToTheGround_TheLandingProvesTheFall()
        {
            var rows = Optimize(Build("mun-deorbit-phys", 0,
                MunOrbit(3000), BurnS(30, "Mun"), CoastS(600, "Mun"), ApproachS(200), GroundS(100, "Mun")));
            Assert.Equal(new[] { "orbit", "descent", "approach", "surface" }, Roles(rows));
        }

        [Fact]
        public void LowPassThroughTheApproachBand_WithoutALanding_ProvesNothing()
        {
            var rows = Optimize(Build("mun-lowpass", 0,
                MunOrbit(3000), BurnS(30, "Mun"), CoastS(600, "Mun"), ApproachS(200), CoastS(600, "Mun")));
            Assert.DoesNotContain("descent", Roles(rows));
        }

        [Fact]
        public void Case10_DeorbitOnAnAtmosphereBody_OrbitDeorbitReentryLanding()
        {
            var rows = Optimize(Build("kerbin-deorbit", 0,
                Lko(3000), BurnS(30), CoastS(600), AtmoS(300), GroundS(100)));
            Assert.Equal(new[] { 0.0, 3000.0, 3630.0, 3930.0 }, Starts(rows));
            Assert.Equal(new[] { "orbit", "deorbit", "reentry", "surface" }, Roles(rows));
        }

        [Fact]
        public void Case11_DirectDescentFromATransfer_Airless()
        {
            var rows = Optimize(Build("direct-descent", 0,
                MunTransferEllipse(20000), MunArrival(3000), BurnS(60, "Mun"), CoastS(300, "Mun"),
                ApproachS(200), GroundS(100, "Mun")));
            Assert.Equal(new[] { 0.0, 23000.0, 23360.0, 23560.0 }, Starts(rows));
            Assert.Equal(new[] { "transfer", "descent", "approach", "surface" }, Roles(rows));
            Assert.True(Logged("Split candidate (DirectDescentBurnStart)"));
        }

        [Fact]
        public void Case12_DirectEntryFromATransfer_NoBurn_Unchanged()
        {
            var rows = Optimize(Build("mun-return", 0,
                MunDeparture(3000), Rails(20000, "Kerbin", 6000000, 0.89, 3.5), AtmoS(300), GroundS(100)));
            Assert.Equal(new[] { 0.0, 23000.0, 23300.0 }, Starts(rows));
            Assert.Equal(new[] { "space", "reentry", "surface" }, Roles(rows));
        }

        // ------------------------------------------------------------ case 13-15, extras

        [Fact]
        public void Case13_BurnsThatKeepTheKind_StayInTheRow()
        {
            var rows = Optimize(Build("keep", 0,
                Lko(3000), BurnS(60), HighKerbinOrbit(3000), BurnS(20), Rails(3000, "Kerbin", 900000, 0.02)));
            Assert.Single(rows);
            Assert.Contains(logLines, l => l.Contains("Split summary") && l.Contains("burnKindKept=2"));
        }

        [Fact]
        public void Case14_SuborbitalHop_NoKindCut()
        {
            var rows = Optimize(Launch(Build("hop", 0,
                PadS(30), AtmoS(200), KerbinSuborbital(300), BurnS(20), KerbinSuborbital(300), AtmoS(200), GroundS(60))));
            Assert.DoesNotContain("circularization", Roles(rows));
            Assert.DoesNotContain("orbit", Roles(rows));
        }

        [Fact]
        public void Case15_StagingGap_OneBurn_ACutAtItsEnd()
        {
            var rows = Optimize(Launch(Build("staging", 0,
                PadS(30), AtmoS(200), KerbinSuborbital(300), BurnS(20), CoastS(5), BurnS(20), Lko(3000))));
            Assert.Equal(new[] { 0.0, 230.0, 575.0 }, Starts(rows));
            Assert.Equal(new[] { "ascent", "circularization", "orbit" }, Roles(rows));
        }

        [Fact]
        public void Case15_ALongGapOrRailsBetweenBurns_TwoBurnsAndNoEvidenceBetween_NoCut()
        {
            var longGap = Optimize(Launch(Build("gap-long", 0,
                PadS(30), AtmoS(200), KerbinSuborbital(300), BurnS(20), CoastS(30), BurnS(20), Lko(3000))));
            Assert.Equal(new[] { "ascent", "space" }, Roles(longGap));
            Assert.Contains(logLines, l => l.Contains("Split summary") && l.Contains("burnsUnjudged=2"));
        }

        [Fact]
        public void MidCourseCorrection_StaysInsideTheTransfer()
        {
            // The correction moves the semi-major axis 5 %: the cut stays at the departure.
            var rows = Optimize(Build("mcc", 0,
                Lko(3000), BurnS(120), MunTransferEllipse(4000), BurnS(20),
                MunTransferEllipse(16000, 6054182), MunArrival(3000)));
            Assert.Equal(new[] { 0.0, 3000.0 }, Starts(rows));
            Assert.Equal(new[] { "orbit", "transfer" }, Roles(rows));
        }

        [Fact]
        public void PoweredFlyby_LeavingToLeaving_NoCut()
        {
            var rows = Optimize(Build("flyby", 0,
                MunTransferEllipse(20000), MunArrival(2000), BurnS(60, "Mun"), MunDeparture(2000),
                Rails(20000, "Kerbin", 7000000, 0.9, 1.0)));
            Assert.Single(rows);
        }

        [Fact]
        public void RecordingStartingMidBurn_NoKindBefore_NoCut()
        {
            var rows = Optimize(Build("start-mid-burn", 0, BurnS(90), Lko(3000)));
            Assert.Single(rows);
        }

        [Fact]
        public void RecordingEndingMidBurn_TheTerminalOrbitIsTheOrbitReached()
        {
            var noTerminal = Optimize(Build("end-mid-burn", 0, Lko(3000), BurnS(120)));
            Assert.Single(noTerminal);

            RecordingStore.ResetForTesting();
            var rec = Build("end-mid-burn-orbit", 0, Lko(3000), BurnS(120));
            rec.TerminalStateValue = TerminalState.SubOrbital;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitSemiMajorAxis = -5000000;
            rec.TerminalOrbitEccentricity = 1.2;
            rec.TerminalOrbitMeanAnomalyAtEpoch = 0.5;
            rec.TerminalOrbitEpoch = 3120;
            var rows = Optimize(rec);
            Assert.Equal(new[] { "orbit", "transfer" }, Roles(rows));
        }

        [Fact]
        public void TerminalOrbitAfterTheLastBurn_JudgesThePhysicsCoastToTheEnd()
        {
            var rec = Build("capture-terminal", 0,
                MunTransferEllipse(20000), MunArrival(3000), BurnS(30, "Mun"), CoastS(200, "Mun"));
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Mun";
            rec.TerminalOrbitSemiMajorAxis = 230000;
            rec.TerminalOrbitEccentricity = 0.01;
            rec.TerminalOrbitEpoch = 23030;
            var rows = Optimize(rec);
            Assert.Equal(new[] { 0.0, 23030.0 }, Starts(rows));
            Assert.Equal(new[] { "transfer", "orbit" }, Roles(rows));
        }

        // ------------------------------------------------------------ guards

        [Fact]
        public void ShortRow_AKindCutNextToAnotherCut_IsDropped()
        {
            // The burn reaches orbit 30 s after the atmosphere exit: no 30 s circularization row.
            var rows = Optimize(Launch(Build("short", 0, PadS(30), AtmoS(200), BurnS(30), Lko(3000))));
            Assert.Equal(new[] { "ascent", "space" }, Roles(rows));
            Assert.True(Logged("Burn kind change:", "SuppressedBurnShortRow"));
            Assert.Contains(logLines, l => l.Contains("Split summary") && l.Contains("burnShortRowKept=1")
                && l.Contains("rules=v3"));
        }

        [Fact]
        public void InterplanetaryTransfer_KeptWhole_NoDepartureOrCaptureCut()
        {
            var rows = Optimize(Build("duna", 0,
                Lko(3000), BurnS(300), KerbinEscape(20000), Rails(9000000, "Sun", 1.7e10, 0.2),
                Rails(20000, "Duna", -800000, 1.5, -2.0), BurnS(200, "Duna"),
                Rails(20000, "Duna", 700000, 0.1)));
            Assert.Single(rows);
            Assert.True(Logged("Burn kind change:", "SuppressedInterplanetaryTransfer"));
            Assert.Contains(logLines, l => l.Contains("Split summary") && l.Contains("transferWholeKept=2"));
        }

        [Fact]
        public void InterplanetaryTransfer_TheInsertionRowBeforeItIsATransferRow()
        {
            var rows = Optimize(Launch(Build("duna-launch", 0,
                PadS(30), AtmoS(200), KerbinSuborbital(300), BurnS(30), Lko(3000), BurnS(300),
                KerbinEscape(20000), Rails(9000000, "Sun", 1.7e10, 0.2),
                Rails(20000, "Duna", -800000, 1.5, -2.0))));
            Assert.Equal(new[] { "ascent", "circularization", "transfer" }, Roles(rows));
        }

        [Fact]
        public void BurnAroundTheParentBetweenTwoMoons_KeptWhole()
        {
            var rows = Optimize(Build("vall-tylo", 0,
                Rails(5000, "Vall", 400000, 0.01), BurnS(200, "Vall"), Rails(5000, "Vall", -900000, 1.4, 0.5),
                Rails(50000, "Jool", 6.0e7, 0.4), BurnS(60, "Jool"), Rails(50000, "Jool", 6.1e7, 0.4),
                Rails(5000, "Tylo", -1500000, 1.8, -2.0)));
            Assert.Single(rows);
        }

        [Fact]
        public void EvaKerbalAndDebris_NoKindCuts()
        {
            var eva = Build("eva", 0, Lko(3000), BurnS(120), MunTransferEllipse(20000), MunArrival(3000));
            eva.EvaCrewName = "Valentina Kerman";
            Assert.Single(Optimize(eva));

            RecordingStore.ResetForTesting();
            var debris = Build("debris", 0, Lko(3000), BurnS(120), MunTransferEllipse(20000), MunArrival(3000));
            debris.IsDebris = true;
            Assert.Single(Optimize(debris));
        }

        // ------------------------------------------------------------ version pins

        [Fact]
        public void VersionsOneAndTwo_MakeNoKindCut_TheirPredicateNeverAnswersAVersionThreeReason()
        {
            Func<int, Recording> shape = v =>
            {
                var r = Launch(Build("pin-v" + v, 0, PadS(30), AtmoS(200), CoastS(20), KerbinSuborbital(300),
                    BurnS(30), Lko(3000), BurnS(120), MunTransferEllipse(20000), MunArrival(3000),
                    BurnS(90, "Mun"), MunOrbit(4000), BurnS(30, "Mun"), MunFalling(600), ApproachS(200),
                    GroundS(100, "Mun")));
                r.SplitRuleVersion = v;
                return r;
            };
            foreach (int v in new[] { 1, 2 })
            {
                var rec = shape(v);
                for (int s = 1; s < rec.TrackSections.Count; s++)
                {
                    RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, s, out var reason);
                    Assert.True(reason < RecordingOptimizer.SplitBoundaryReason.OrbitInsertionBurnEnd,
                        $"v{v} boundary {s} answered {reason}");
                }
            }
            var v2 = Optimize(shape(2));
            Assert.DoesNotContain("orbit", Roles(v2));
            RecordingStore.ResetForTesting();
            var v3 = Optimize(shape(3));
            Assert.Equal(new[] { "ascent", "circularization", "orbit", "transfer", "orbit", "descent", "approach", "surface" },
                Roles(v3));
        }

        [Fact]
        public void CanAutoMerge_RefusesTheRowsOfAKindCut_AndAJoinTheVersionThreeRulesWouldCutAgain()
        {
            var a = Build("km-a", 0, Lko(3000));
            var b = Build("km-b", 3000, BurnS(120), MunTransferEllipse(20000), MunArrival(3000));
            a.ChainId = b.ChainId = "chain-km";
            a.ChainIndex = 0;
            b.ChainIndex = 1;
            a.SegmentPhase = b.SegmentPhase = "exo";
            a.SegmentBodyName = b.SegmentBodyName = "Kerbin";
            a.SegmentRole = "orbit";
            b.SegmentRole = "transfer";
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));

            b.SegmentRole = "orbit";
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
            Assert.True(Logged("CanAutoMerge: rejecting merge of 'km-a' + 'km-b'", "would cut the join again"));

            // Control: the same pair under version 2 has no kind cut, so it merges.
            a.SplitRuleVersion = b.SplitRuleVersion = RecordingOptimizer.SplitRuleVersionPassRules;
            Assert.True(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void CanAutoMerge_AJoinThatStartsInSpace_IsAlsoJudgedAsStartingAtTheAtmosphereExit()
        {
            // Two pieces of one launch's space row split by the recorder at the insertion burn's
            // end: only the floor exit before them says the burn was an insertion.
            var a = Build("ctx-a", 230, BurnS(80));
            var b = Build("ctx-b", 310, Lko(3000));
            a.ChainId = b.ChainId = "chain-ctx";
            a.ChainIndex = 0;
            b.ChainIndex = 1;
            a.SegmentPhase = b.SegmentPhase = "exo";
            a.SegmentBodyName = b.SegmentBodyName = "Kerbin";
            a.SegmentRole = b.SegmentRole = "space";
            Assert.False(RecordingOptimizer.CanAutoMerge(a, b));
        }

        [Fact]
        public void ClassifyOrbitKind_TheThreeKinds()
        {
            var kerbin = RecordingOptimizer.StockBodyFacts("Kerbin");
            Func<double, double, double, RecordingOptimizer.OrbitKind> k = (a, e, m) =>
                RecordingOptimizer.ClassifyOrbitKind(new OrbitSegment
                {
                    semiMajorAxis = a, eccentricity = e, meanAnomalyAtEpoch = m, epoch = 0,
                }, 0, kerbin);
            Assert.Equal(RecordingOptimizer.OrbitKind.InOrbit, k(680000, 0.006, 0));
            Assert.Equal(RecordingOptimizer.OrbitKind.Falling, k(440890, 0.54, 2.8));
            Assert.Equal(RecordingOptimizer.OrbitKind.Leaving, k(-5000000, 1.2, 0.5));
            // An escape path whose low point is under the floor: falling on the way in, leaving
            // on the way out (a direct ascent to escape).
            Assert.Equal(RecordingOptimizer.OrbitKind.Falling, k(-600000, 1.9, -0.5));
            Assert.Equal(RecordingOptimizer.OrbitKind.Leaving, k(-600000, 1.9, 0.5));
            // Apoapsis beyond the SOI is leaving.
            Assert.Equal(RecordingOptimizer.OrbitKind.Leaving, k(6.0e7, 0.5, 1.0));
            Assert.Equal(RecordingOptimizer.OrbitKind.Unknown,
                RecordingOptimizer.ClassifyOrbitKind(new OrbitSegment { semiMajorAxis = 680000 }, 0,
                    RecordingOptimizer.StockBodyFacts("NotABody")));
        }

        [Fact]
        public void BodyFacts_Headless_ReadTheStockSystem()
        {
            var mun = RecordingOptimizer.ResolveBodyFacts("Mun");
            Assert.True(mun.Known);
            Assert.Equal("Kerbin", mun.ParentName);
            Assert.Equal(200000, mun.Radius);
            Assert.Null(RecordingOptimizer.ResolveBodyFacts("Sun").ParentName);
            Assert.False(RecordingOptimizer.ResolveBodyFacts("Earth").Known);
        }
    }
}
