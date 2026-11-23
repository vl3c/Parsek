using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The air context of the part parity sweep (design-part-ghost-parity.md Appendix D.4,
    /// owner ruling R1): the pure rules of <see cref="PartParityAirPlan"/> (the showcase-band
    /// slots, the pad distance, the canopy window, the full raise, the floor), the canopy tag
    /// the state flags carry, the proof's canopy keys, and source gates on the live wiring
    /// (both admissions read the args-aware rule, every air wave dispatches to the air
    /// context, the full raise sits in the state wait).
    /// </summary>
    public class PartParityAirContextTests
    {
        private const double KerbinRadius = 600000.0;

        private static string Source(string file)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", "TestCommands", file);
            Assert.True(File.Exists(path), path);
            return File.ReadAllText(path);
        }

        // ----- slots on the showcase band -----

        [Fact]
        public void SlotNorthOffset_IsCentredFortyMetresApart()
        {
            double[] five = Enumerable.Range(0, 5).Select(i => PartParityAirPlan.SlotNorthOffsetM(i, 5)).ToArray();
            Assert.Equal(new[] { -80.0, -40.0, 0.0, 40.0, 80.0 }, five);
            Assert.Equal(0.0, PartParityAirPlan.SlotNorthOffsetM(0, 1));
            Assert.Equal(new[] { -20.0, 20.0 }, new[] { PartParityAirPlan.SlotNorthOffsetM(0, 2), PartParityAirPlan.SlotNorthOffsetM(1, 2) });
            // A degenerate count reads as one slot.
            Assert.Equal(0.0, PartParityAirPlan.SlotNorthOffsetM(0, 0));
        }

        [Fact]
        public void SlotLatLon_SitsOnTheBand200MetresEastOfThePad()
        {
            PartParityAirPlan.SlotLatLon(2, 5, KerbinRadius, out double lat, out double lon);
            Assert.Equal(PartParityAirPlan.PadLatitudeDeg, lat, 9);
            double east = PartParityAirPlan.SurfaceDistanceM(PartParityAirPlan.PadLatitudeDeg, PartParityAirPlan.PadLongitudeDeg,
                PartParityAirPlan.PadLatitudeDeg, lon, KerbinRadius);
            Assert.InRange(east, 199.9, 200.1);
            PartParityAirPlan.SlotLatLon(4, 5, KerbinRadius, out double lat4, out double lon4);
            Assert.Equal(lon, lon4, 12);
            double north = PartParityAirPlan.SurfaceDistanceM(lat, lon, lat4, lon4, KerbinRadius);
            Assert.InRange(north, 79.9, 80.1);
        }

        [Fact]
        public void TheSpawnStaysInsideStockFlyingUnpackRange()
        {
            // Stock flying ranges: unpack 2000 m (decompiled VesselRanges). The outermost slot
            // of a five-chute wave at the spawn altitude must sit inside it from the pad.
            PartParityAirPlan.SlotLatLon(0, PartParitySweepPlan.MaxAirWaveCases, KerbinRadius, out double lat, out double lon);
            double ground = PartParityAirPlan.SurfaceDistanceM(lat, lon, PartParityAirPlan.PadLatitudeDeg,
                PartParityAirPlan.PadLongitudeDeg, KerbinRadius);
            double slant = Math.Sqrt(ground * ground + PartParityStateRules.AirSpawnAltitudeM * PartParityStateRules.AirSpawnAltitudeM);
            Assert.True(slant < 2000.0, "slant=" + slant);
        }

        // ----- the pad distance (admission) -----

        [Fact]
        public void DistanceToPad_IsTheSurfaceDistanceOnTheHomeBodyOnly()
        {
            Assert.Equal(0.0, PartParityAirPlan.DistanceToPadM(true, PartParityAirPlan.PadLatitudeDeg,
                PartParityAirPlan.PadLongitudeDeg, KerbinRadius), 6);
            double metersPerDegree = 2.0 * Math.PI * KerbinRadius / 360.0;
            double d = PartParityAirPlan.DistanceToPadM(true, PartParityAirPlan.PadLatitudeDeg + 300.0 / metersPerDegree,
                PartParityAirPlan.PadLongitudeDeg, KerbinRadius);
            Assert.InRange(d, 299.9, 300.1);
            Assert.True(double.IsPositiveInfinity(PartParityAirPlan.DistanceToPadM(false, 0, 0, KerbinRadius)));
            Assert.True(double.IsNaN(PartParityAirPlan.SurfaceDistanceM(double.NaN, 0, 0, 0, KerbinRadius)));
            Assert.True(double.IsNaN(PartParityAirPlan.SurfaceDistanceM(0, 0, 0, 0, 0)));
        }

        [Fact]
        public void TheAdmissionDetail_NamesSituationAndDistance()
        {
            Assert.Equal("situation=PRELAUNCH distanceToPad=12m", PartParityAirPlan.AdmissionDetail("PRELAUNCH", 12.4));
            Assert.Equal("situation=ORBITING distanceToPad=inf", PartParityAirPlan.AdmissionDetail("ORBITING", double.PositiveInfinity));
            Assert.Equal("situation=(none) distanceToPad=inf", PartParityAirPlan.AdmissionDetail(null, double.NaN));
        }

        // ----- angles -----

        [Fact]
        public void AngleDeg_IsTheVectorAngle()
        {
            Assert.Equal(0.0, PartParityAirPlan.AngleDeg(0, 1, 0, 0, 2, 0), 9);
            Assert.Equal(90.0, PartParityAirPlan.AngleDeg(0, 1, 0, 1, 0, 0), 9);
            Assert.Equal(180.0, PartParityAirPlan.AngleDeg(0, 1, 0, 0, -3, 0), 9);
            Assert.Equal(45.0, PartParityAirPlan.AngleDeg(0, 1, 0, 1, 1, 0), 9);
            Assert.True(double.IsNaN(PartParityAirPlan.AngleDeg(0, 0, 0, 0, 1, 0)));
            Assert.True(double.IsNaN(PartParityAirPlan.AngleDeg(double.NaN, 1, 0, 0, 1, 0)));
        }

        // ----- the canopy window -----

        [Fact]
        public void DecideCanopy_CapturesInTheWindow_ElseAtTheCap()
        {
            Assert.Equal(PartParityCanopyStep.Capture, PartParityAirPlan.DecideCanopy(1.0, 1.5, 0.0));
            Assert.Equal(PartParityCanopyStep.Wait, PartParityAirPlan.DecideCanopy(1.0, 2.0, 0.5));
            Assert.Equal(PartParityCanopyStep.Wait, PartParityAirPlan.DecideCanopy(double.NaN, 0.0, 3.99));
            Assert.Equal(PartParityCanopyStep.CaptureTimedOut, PartParityAirPlan.DecideCanopy(2.0, 2.0, 4.0));
            Assert.Equal(PartParityCanopyStep.CaptureTimedOut, PartParityAirPlan.DecideCanopy(double.NaN, double.NaN, 4.5));
            // A capture in the window wins over the cap.
            Assert.Equal(PartParityCanopyStep.Capture, PartParityAirPlan.DecideCanopy(0.5, 0.5, 10.0));
        }

        [Fact]
        public void CanopyWobbleTag_IsTheSumWithOneDecimal()
        {
            Assert.Equal("canopy-wobble:4.3", PartParityAirPlan.CanopyWobbleTag(1.25, 3.05));
            Assert.Equal("canopy-wobble:5.0", PartParityAirPlan.CanopyWobbleTag(-2.0, 3.0));
            Assert.Equal("canopy-wobble:nan", PartParityAirPlan.CanopyWobbleTag(double.NaN, 1.0));
            Assert.StartsWith(PartParityStateRules.TagCanopyWobblePrefix, PartParityAirPlan.CanopyWobbleTag(0, 0));
        }

        [Fact]
        public void TheCanopyTag_IsAStateFlag()
        {
            List<string> f = PartParityStateWave.StateFlags(new PartParityStateWave.FlagInputs
            {
                Pressed = true, Reached = true, EventsRecorded = 1, GhostEvent = "applied", GhostStart = "applied",
                CanopyWobbleTag = "canopy-wobble:4.2",
            });
            Assert.Equal(new[] { "canopy-wobble:4.2" }, f.ToArray());
            // Anything but the tag's own shape writes nothing.
            Assert.Empty(PartParityStateWave.StateFlags(new PartParityStateWave.FlagInputs
            {
                Pressed = true, Reached = true, EventsRecorded = 1, GhostEvent = "applied", GhostStart = "applied",
                CanopyWobbleTag = "wobble",
            }));
        }

        // ----- the chute sequence -----

        [Fact]
        public void ShouldRaiseForFull_OnlyAFullCaseAtAStillSemi_Once()
        {
            Assert.True(PartParityAirPlan.ShouldRaiseForFull("full", "SEMIDEPLOYED", false, false));
            Assert.False(PartParityAirPlan.ShouldRaiseForFull("full", "SEMIDEPLOYED", true, false));
            Assert.False(PartParityAirPlan.ShouldRaiseForFull("full", "SEMIDEPLOYED", false, true));
            Assert.False(PartParityAirPlan.ShouldRaiseForFull("full", "ACTIVE", false, false));
            Assert.False(PartParityAirPlan.ShouldRaiseForFull("semi", "SEMIDEPLOYED", false, false));
            Assert.False(PartParityAirPlan.ShouldRaiseForFull("full", null, false, false));
        }

        [Fact]
        public void TheTimeouts_SemiFirstThenTheFullWaitFromTheSemiDone()
        {
            Assert.Equal(PartParityStateRules.ChuteSemiTimeoutSeconds, PartParityAirPlan.InitialTimeoutSeconds());
            Assert.Equal(3.5 + PartParityStateRules.ChuteFullTimeoutSeconds, PartParityAirPlan.FullTimeoutAfterRaise(3.5));
            Assert.Equal(PartParityStateRules.ChuteFullTimeoutSeconds, PartParityAirPlan.FullTimeoutAfterRaise(-1.0));
        }

        [Fact]
        public void TheSemiDeployAltitude_LeavesMarginUnderTheSpawn()
        {
            // Stock opens fully once a raycast of deployAltitude metres down meets the terrain:
            // the semi value must sit well under the spawn height above the KSC terrain.
            Assert.True(PartParityStateRules.ChuteDeployAltitudeSemi + 500.0 < PartParityStateRules.AirSpawnAltitudeM - 100.0);
            Assert.True(PartParityStateRules.ChuteDeployAltitudeSemi > PartParityAirPlan.AirFloorAglM);
            Assert.True(PartParityStateRules.ChuteDeployAltitudeFull > PartParityStateRules.AirSpawnAltitudeM);
        }

        [Fact]
        public void BelowFloor_ReadsAFiniteHeightOnly()
        {
            Assert.True(PartParityAirPlan.BelowFloor(299.0));
            Assert.False(PartParityAirPlan.BelowFloor(300.0));
            Assert.False(PartParityAirPlan.BelowFloor(double.NaN));
            Assert.False(PartParityAirPlan.BelowFloor(double.NegativeInfinity));
            Assert.Equal("fell under the air floor (250 m above the terrain, floor 300 m) before its capture",
                PartParityAirPlan.FloorDetail(250.2));
        }

        // ----- the proof key S4's judge reads -----

        [Fact]
        public void TheCanopyBlock_CarriesTheCanopyTransform()
        {
            Assert.Equal(new[] { "tiltDeg", "wobbleDeg", "transform" }, PartParityJson.CanopyKeys);
        }

        // ----- live wiring (source gates) -----

        [Fact]
        public void BothAdmissions_ReadTheArgsAwareRule()
        {
            string runner = Source("PartParitySweepRunner.cs");
            string applier = Source("ParsekTestCommandAddon.PartParitySweep.cs");
            string air = Source("PartParityAirContext.cs");
            Assert.Contains("PartParityAirContext.DecideAdmission(ctx.Args, active, out string detail)", runner);
            Assert.Contains("PartParityAirContext.DecideAdmission(args, active, out string admissionDetail)", applier);
            Assert.DoesNotContain("TestCommandPartParitySweep.DecideHostAdmission(active != null, orbiting)", runner);
            Assert.DoesNotContain("TestCommandPartParitySweep.DecideHostAdmission(active != null, orbiting)", applier);
            Assert.Contains("TestCommandPartParitySweep.DecideHostAdmission(args, has, orbiting, landed, distance)", air);
            // The baseline records the run's mode (host.txt mode=air for an air run).
            Assert.Contains("CaptureHostBaseline(ctx.Host, TestCommandPartParitySweep.IsAirRun(a))", runner);
            Assert.Contains("Air = air,", runner);
        }

        [Fact]
        public void EveryAirWave_DispatchesToTheAirContext()
        {
            string runner = Source("PartParitySweepRunner.cs");
            Assert.Contains("IEnumerator w = air ? PartParityAirContext.RunWave(ctx, wave) : RunWave(ctx, wave);", runner);
            // An FX wave (design E.2) has its own flow, the air one included (its admission is
            // refused until piece F7).
            Assert.Contains("if (PartParityStateWave.IsFxKind(kind)) w = PartParityFxDriver.RunWave(ctx, wave);", runner);
            Assert.Contains("!FxAirSpawnBuilt", Source("TestCommandPartParitySweep.cs"));
            Assert.Contains("air ? PartParitySweepPlan.MaxAirWaveCases : PartParitySweepPlan.MaxStateWaveCases", runner);
        }

        [Fact]
        public void TheAirWave_ChecksTheHost_WritesThenChecks_AndTearsDownAfterAStop()
        {
            string air = Source("PartParityAirContext.cs");
            int run = air.IndexOf("internal static IEnumerator RunWave(", StringComparison.Ordinal);
            int end = air.IndexOf("private static bool Healthy(", run, StringComparison.Ordinal);
            string wave = air.Substring(run, end - run);
            Assert.Contains("PartParitySweepRunner.CheckHost(ctx, \"air wave\")", wave);
            Assert.Contains("PartParitySweepRunner.WaitReady(ctx, wave)", wave);
            Assert.Contains("PartParitySweepRunner.WaitSettled(ctx, wave)", wave);
            int stop = wave.IndexOf("if (ctx.HostDamage != null)\n", StringComparison.Ordinal);
            if (stop < 0) stop = wave.IndexOf("if (ctx.HostDamage != null)\r\n", StringComparison.Ordinal);
            int delete = wave.IndexOf("PartParitySweepRunner.DeleteInflight(ctx);", StringComparison.Ordinal);
            int down = wave.IndexOf("PartParitySweepRunner.TearDownWave(ctx, wave)", StringComparison.Ordinal);
            Assert.True(stop >= 0 && delete > stop && down > delete, $"stop={stop} delete={delete} down={down}");
            int fin = air.IndexOf("private static void Finish(", StringComparison.Ordinal);
            string finish = air.Substring(fin, air.IndexOf("private static void WriteRemainingInflight(", fin, StringComparison.Ordinal) - fin);
            int write = finish.IndexOf("PartParitySweepRunner.FinishCase(ctx, wc)", StringComparison.Ordinal);
            int destroy = finish.IndexOf("PartParitySweepRunner.DestroyCase(wc);", StringComparison.Ordinal);
            int check = finish.IndexOf("PartParitySweepRunner.CheckHost(ctx, \"after case \"", StringComparison.Ordinal);
            Assert.True(write >= 0 && destroy > write && check > destroy, $"write={write} destroy={destroy} check={check}");
        }

        [Fact]
        public void TheFullRaise_SitsInTheStateWait_AndTheSpawnGoesThroughSpawnAtPosition()
        {
            string driver = Source("PartParityStateDriver.cs");
            string air = Source("PartParityAirContext.cs");
            int step = driver.IndexOf("internal static void Step(WaveCase wc)", StringComparison.Ordinal);
            int hook = driver.IndexOf("if (run.Air && run.Pressed) PartParityAirContext.StepFull(wc, run, part, now, animating);", StringComparison.Ordinal);
            int reached = driver.IndexOf("if (!run.Reached)", step, StringComparison.Ordinal);
            Assert.True(step >= 0 && hook > step && reached > hook, $"step={step} hook={hook} reached={reached}");
            Assert.Contains("GhostCoOrbitalRig.SpawnInAir(node, body, lat, lon, alt, PartParityStateRules.AirSpawnDescentMps,", air);
            Assert.DoesNotContain("SpawnCoOrbital", air);
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string rig = File.ReadAllText(Path.Combine(root, "Source", "Parsek", "InGameTests", "GhostCoOrbitalRig.cs"));
            int spawn = rig.IndexOf("internal static uint SpawnInAir(", StringComparison.Ordinal);
            Assert.True(spawn >= 0);
            string body = rig.Substring(spawn, rig.IndexOf("return pid;", spawn, StringComparison.Ordinal) - spawn);
            // An inertial velocity (surface + rotating frame) and an upright surface-relative rotation.
            Assert.Contains("Vector3d orbitalVel = surfaceVel + body.getRFrmVel(worldPos);", body);
            Assert.Contains("VesselSpawner.SpawnAtPosition(vesselNode, body, lat, lon, altitudeAsl, orbitalVel, ut,", body);
            Assert.Contains("surfaceRelativeRotation: srfRel", body);
            // The capture of an air case runs its prepare and every view in one frame.
            int prepare = air.IndexOf("PartParityCaseCapture.Prepare(ctx, wc)", StringComparison.Ordinal);
            int view = air.IndexOf("PartParityCaseCapture.CaptureView(ctx, wc, v)", StringComparison.Ordinal);
            Assert.True(prepare > 0 && view > prepare);
            string finish = air.Substring(prepare, view - prepare);
            Assert.DoesNotContain("yield", finish);
        }
    }
}
