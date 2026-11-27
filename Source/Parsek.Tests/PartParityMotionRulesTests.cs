using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <see cref="PartParityMotionRules"/> (design-part-ghost-parity.md
    /// Appendix H, piece M1): the families and exclusions over the committed motion fixtures
    /// (the per-install counts H.1 left to M1), the frame schedule of H.2, pose and picture
    /// progress, the timing measures and flags of H.1 over the committed golden curves (each
    /// synthetic curve moves exactly its own measure, and the C# agrees with
    /// <c>partparity.motion_timing</c> within 1e-9), and the H.3 control judges.
    /// </summary>
    public class PartParityMotionRulesTests
    {
        private static PartParityCaseKey Base(string part)
            => new PartParityCaseKey { Part = part, Source = "Squad", Skin = "default" };

        private static List<PartParityStatePart> Fixture(string profile)
            => PartParityStateRulesTests.LoadPartsFixture("motion-parts-" + profile + ".txt");

        private static PartParityMotionEnumeration EnumerateAll(IEnumerable<PartParityStatePart> parts)
        {
            var all = new PartParityMotionEnumeration();
            foreach (var p in parts) all.AddRange(PartParityMotionRules.Enumerate(p, Base(p.Name)));
            return all;
        }

        private static PartParityMotionEnumeration Of(string profile, string part)
            => PartParityMotionRules.Enumerate(Fixture(profile).Single(p => p.Name == part), Base(part));

        private static Dictionary<string, int> Count<T>(IEnumerable<T> items, Func<T, string> key)
            => items.GroupBy(key).ToDictionary(g => g.Key, g => g.Count());

        // ----- the contract's shape -----

        [Fact]
        public void TheWords_AreTheJudgedFamiliesAndLight()
        {
            Assert.Equal(12, PartParityMotionRules.Families.Length);
            Assert.Equal(PartParityMotionRules.Families.Take(10), PartParityMotionRules.ArgWords);
            Assert.Equal(PartParityMotionRules.ArgWords.Concat(new[] { "light" }), PartParityMotionRules.ArgKnownWords);
            Assert.DoesNotContain("light", PartParityMotionRules.ArgWords);
            Assert.Equal("light", PartParityMotionRules.ArgForFamily("cabin-light"));
            Assert.Equal("robotics", PartParityMotionRules.ArgForFamily("robotics"));
            Assert.All(PartParityMotionRules.P6MotionFamilies, f => Assert.Contains(f, PartParityStateRules.Families));
            Assert.All(PartParityMotionRules.States, s => Assert.True(PartParityMotionRules.IsMotionState(s)));
            Assert.Equal("motion-deployed", PartParityMotionRules.StateFor("deployed"));
            Assert.False(PartParityMotionRules.IsMotionState("deployed"));
            Assert.Equal(PartParityMotionRules.ExclusionReasons.Length, PartParityMotionRules.ExclusionReasons.Distinct().Count());
            // The carried reasons are P6's own words.
            Assert.All(PartParityMotionRules.ExclusionReasons.Skip(6), r => Assert.Contains(r, PartParityStateRules.ExclusionReasons));
            Assert.Equal(new[] { "t02", "t10", "t50", "t90", "t98" }, PartParityMotionRules.CrossingKeys);
            Assert.Equal(PartParityMotionRules.CrossingKeys, PartParityJson.MotionCrossingKeys);
        }

        [Fact]
        public void TheServoModules_AreTheRoboticPollsOwnNames()
        {
            // Called, not read: the recorder's robotic poll and the motion family agree on names.
            foreach (string m in PartParityMotionRules.ServoModules)
                Assert.True(FlightRecorder.IsRoboticModuleName(m), m);
            Assert.True(FlightRecorder.IsRoboticModuleName(PartParityMotionRules.ModuleServoRotor));
            Assert.Equal("ModuleControlSurface", PartParityMotionRules.ModuleControlSurface);
            Assert.Equal("BaseServo", PartParityMotionRules.TypeBaseServo);
        }

        // ----- enumeration over synthetic parts -----

        private static PartParityStateModule Module(int index, string name, params string[] chain)
        {
            var m = new PartParityStateModule { Index = index, Name = name };
            m.TypeChain.AddRange(chain);
            return m;
        }

        [Fact]
        public void AServo_IsOneSweptCase_ARotorAndAModServoAreExcluded()
        {
            var part = new PartParityStatePart { Name = "mix" };
            part.Modules.Add(Module(0, "ModuleRoboticServoHinge", "ModuleRoboticServoHinge", "BaseServo", "PartModule"));
            part.Modules.Add(Module(1, "ModuleRoboticServoPiston", "ModuleRoboticServoPiston", "BaseServo", "PartModule"));
            part.Modules.Add(Module(2, "ModuleRoboticServoRotor", "ModuleRoboticServoRotor", "BaseServo", "PartModule"));
            part.Modules.Add(Module(3, "ModSpinner", "ModSpinner", "BaseServo", "PartModule"));
            var r = PartParityMotionRules.Enumerate(part, Base("mix"));
            Assert.Equal(new[] { "motion-swept-m0/alone", "motion-swept-m1/alone" }, r.Cases.Select(k => k.State + "/" + k.Context));
            Assert.Equal(new[] { "targetAngle", "targetExtension" }, r.Cases.Select(k => k.MotionOption.DriveField));
            Assert.All(r.Cases, k => Assert.Equal("servo", k.MotionOption.Kind));
            Assert.Equal(new[] { "rotor-spin-not-primed", "not-polled" }, r.Exclusions.Select(e => e.Reason));
        }

        [Fact]
        public void AControlSurface_IsARadialDeployCase_AndItsDeflectionIsExcluded()
        {
            var part = new PartParityStatePart { Name = "StandardCtrlSrf" };
            part.Modules.Add(Module(0, "ModuleControlSurface", "ModuleControlSurface", "ModuleLiftingSurface", "PartModule"));
            var r = PartParityMotionRules.Enumerate(part, Base("StandardCtrlSrf"));
            var k = r.Cases.Single();
            Assert.Equal("motion-deployed/radial", k.State + "/" + k.Context);
            Assert.Equal("ActionExtend", k.MotionOption.Action);
            Assert.True(k.MotionOption.ActionIsKspAction);
            Assert.Equal("surface", k.MotionOption.Kind);
            Assert.Equal("synthesized-from-trajectory", r.Exclusions.Single().Reason);
            Assert.StartsWith("standardctrlsrf--default--motion-deployed--radial--", TestCommandPartParitySweep.CaseId(k));
        }

        [Fact]
        public void AGimbalAScannerAndARunningLoop_AreExclusions()
        {
            var part = new PartParityStatePart { Name = "x" };
            part.Modules.Add(Module(0, "ModuleGimbal"));
            part.Modules.Add(Module(1, "ModuleRobotArmScanner"));
            part.Modules.Add(Module(2, "ModuleJettison"));
            part.Modules.Add(Module(3, "ModuleProceduralFairing"));
            var r = PartParityMotionRules.Enumerate(part, Base("x"));
            Assert.Empty(r.Cases);
            Assert.Equal(new[] { "synthesized-from-trajectory", "ground-scan", "motion-physics", "motion-physics" },
                r.Exclusions.Select(e => e.Reason));
            Assert.Equal(new[] { "jettison", "fairing" }, r.Exclusions.Skip(2).Select(e => e.Family));
        }

        // ----- enumeration over the committed fixtures (the H.1 counts) -----

        [Theory]
        [InlineData("stock-minimal")]
        [InlineData("modded-compat")]
        public void TheMotionFixture_IsExactlyTheStatePartsWithAMotion(string profile)
        {
            var state = PartParityStateRulesTests.LoadPartsFixture("state-parts-" + profile + ".txt");
            var motion = Fixture(profile);
            var expected = state.Where(p =>
            {
                var r = PartParityMotionRules.Enumerate(p, Base(p.Name));
                return r.Cases.Count + r.Exclusions.Count > 0;
            }).ToList();
            Assert.Equal(expected.Select(p => p.Name), motion.Select(p => p.Name));
            foreach (var pair in expected.Zip(motion, (a, b) => new { a, b }))
                Assert.Equal(pair.a.Modules.Select(m => m.Index + m.Name), pair.b.Modules.Select(m => m.Index + m.Name));
        }

        [Theory]
        [InlineData("stock-minimal", 19, 8, 2, 4, 8, 12, 1, 10, 13, 24, 15, 26)]
        [InlineData("modded-compat", 22, 9, 2, 4, 9, 17, 1, 10, 13, 24, 18, 37)]
        public void TheFixtureCases_PerFamily(string profile, int deployable, int gear, int ladder, int animationGroup,
            int cargoBay, int animateGeneric, int airbrake, int parachute, int robotics, int controlSurface,
            int light, int cabinLight)
        {
            var r = EnumerateAll(Fixture(profile));
            var byFamily = Count(r.Cases, k => k.MotionOption.Family);
            var expected = new Dictionary<string, int>
            {
                ["deployable"] = deployable, ["gear"] = gear, ["ladder"] = ladder, ["animation-group"] = animationGroup,
                ["cargo-bay"] = cargoBay, ["animate-generic"] = animateGeneric, ["airbrake"] = airbrake,
                ["parachute"] = parachute, ["robotics"] = robotics, ["control-surface"] = controlSurface,
                ["light"] = light, ["cabin-light"] = cabinLight,
            };
            Assert.Equal(expected.OrderBy(p => p.Key), byFamily.OrderBy(p => p.Key));
            // Robotics: 4 rotation servos, 5 hinges, 4 pistons on both installs.
            Assert.Equal(new Dictionary<string, int>
                {
                    ["ModuleRoboticRotationServo"] = 4, ["ModuleRoboticServoHinge"] = 5, ["ModuleRoboticServoPiston"] = 4,
                }.OrderBy(p => p.Key),
                Count(r.Cases.Where(k => k.MotionOption.Family == "robotics"), k => k.MotionOption.Module).OrderBy(p => p.Key));
            // Chutes: one semi and one full case each, all in the air.
            Assert.Equal(parachute / 2, r.Cases.Count(k => k.State.StartsWith("motion-semi", StringComparison.Ordinal)));
            Assert.All(r.Cases.Where(k => k.MotionOption.Family == "parachute"), k => Assert.Equal("air", k.Context));
            // Every control surface sits on the radial context; every light case is reading-only.
            Assert.All(r.Cases.Where(k => k.MotionOption.Family == "control-surface"), k => Assert.Equal("radial", k.Context));
            Assert.All(r.Cases, k => Assert.Equal(k.MotionOption.Family == "light" || k.MotionOption.Family == "cabin-light",
                k.MotionOption.ReadingOnly));
            // Every key is unique: the tiebreak of D.1 holds over (word, context).
            Assert.Equal(r.Cases.Count, r.Cases.Select(TestCommandPartParitySweep.CaseId).Distinct().Count());
        }

        [Theory]
        [InlineData("stock-minimal", 48, 41, 53, 5, 3, 9, 0, 9, 2, 5, 3, 0, 1)]
        [InlineData("modded-compat", 78, 55, 70, 4, 3, 9, 0, 9, 2, 8, 5, 0, 0)]
        public void TheFixtureExclusions_PerReason(string profile, int physics, int lightFade, int synthesized, int loopPhase,
            int groundScan, int rotor, int notPolled, int groundDeploy, int noPose, int fairingBay, int bayNotAnimated,
            int noAnimation, int autoDeploy)
        {
            var r = EnumerateAll(Fixture(profile));
            int[] counts = { physics, lightFade, synthesized, loopPhase, groundScan, rotor, notPolled, groundDeploy, noPose,
                fairingBay, bayNotAnimated, noAnimation, autoDeploy };
            var expected = PartParityMotionRules.ExclusionReasons.Zip(counts, (k, v) => new KeyValuePair<string, int>(k, v))
                .Where(p => p.Value > 0).OrderBy(p => p.Key);
            Assert.Equal(expected, Count(r.Exclusions, e => e.Reason).OrderBy(p => p.Key));
            Assert.All(r.Exclusions, e => Assert.Contains(e.Reason, PartParityMotionRules.ExclusionReasons));
            // A light-fade row stands beside every reading-only light case.
            Assert.Equal(r.Cases.Count(k => k.MotionOption.ReadingOnly), lightFade);
        }

        [Fact]
        public void TheLaneSubjects_PlanWhatTheControlsAndM3Read()
        {
            var antenna = Of("stock-minimal", "longAntenna").Cases.Single();
            Assert.Equal("longantenna--default--motion-deployed--", TestCommandPartParitySweep.CaseId(antenna).Substring(0, 39));
            Assert.Equal("clip", antenna.MotionOption.Kind);
            Assert.NotNull(antenna.MotionOption.StateOption);
            var brake = Of("stock-minimal", "airbrake1").Cases.Single();
            Assert.Equal("surface", brake.MotionOption.Kind);
            Assert.Equal("motion-deployed", brake.State);
            var chute = Of("stock-minimal", "parachuteSingle").Cases;
            Assert.Equal(new[] { "motion-full/air", "motion-semi/air" }, chute.Select(k => k.State + "/" + k.Context).OrderBy(s => s, StringComparer.Ordinal));
            Assert.All(chute, k => Assert.Equal("canopy", k.MotionOption.Kind));
            Assert.Equal("motion-swept", Of("stock-minimal", "hinge.01").Cases.Single().State);
            Assert.Equal("motion-open", Of("stock-minimal", "mk2CargoBayS").Cases.Single().State);
        }

        [Fact]
        public void KeySelected_ReadsTheWordsAndTheAirContext()
        {
            var r = EnumerateAll(Fixture("stock-minimal"));
            var all = PartParityMotionRules.ArgWords;
            Assert.DoesNotContain(r.Cases.Where(k => PartParityMotionRules.KeySelected(k, all, false)), k => k.MotionOption.ReadingOnly);
            Assert.All(r.Cases.Where(k => PartParityMotionRules.KeySelected(k, all, true)), k => Assert.Equal("parachute", k.MotionOption.Family));
            Assert.Equal(10, r.Cases.Count(k => PartParityMotionRules.KeySelected(k, all, true)));
            Assert.Equal(91, r.Cases.Count(k => PartParityMotionRules.KeySelected(k, all, false)));
            Assert.Equal(41, r.Cases.Count(k => PartParityMotionRules.KeySelected(k, new[] { "light" }, false)));
            var fade = r.Exclusions.First(e => e.Reason == "light-fade");
            Assert.True(PartParityMotionRules.ExclusionApplies(fade, all, false));
            Assert.False(PartParityMotionRules.ExclusionApplies(fade, new[] { "light" }, false));
            Assert.False(PartParityMotionRules.ExclusionApplies(fade, all, true));
        }

        [Fact]
        public void FormatCounts_ListsFamiliesAndReasonsInTableOrder()
        {
            var r = PartParityMotionRules.Enumerate(Fixture("stock-minimal").Single(p => p.Name == "StandardCtrlSrf"), Base("StandardCtrlSrf"));
            Assert.Equal("motion cases: motion=all contexts=none install=1 (control-surface=1) planned=1 excluded=1 (synthesized-from-trajectory=1)",
                PartParityMotionRules.FormatCounts("all", null, r.Cases, 1, r.Exclusions));
        }

        // ----- the frame schedule (H.2) -----

        [Theory]
        [InlineData(0.1, 0.25)]
        [InlineData(2.0, 2.0)]
        [InlineData(45.0, 30.0)]
        [InlineData(double.NaN, 30.0)]
        [InlineData(-1.0, 30.0)]
        public void ThePredictedTime_IsClamped(double t, double expected)
            => Assert.Equal(expected, PartParityMotionRules.ClampPredictedSeconds(t));

        [Fact]
        public void TheFrameTargets_AreKTOver8_ThenDoublePastFrame33()
        {
            Assert.Equal(0.0, PartParityMotionRules.FrameTargetSeconds(0, 2.0, 8));
            Assert.Equal(0.25, PartParityMotionRules.FrameTargetSeconds(1, 2.0, 8), 12);
            Assert.Equal(8.0, PartParityMotionRules.FrameTargetSeconds(32, 2.0, 8), 12);
            // Frame 33 is the first at the doubled spacing; the next block doubles again.
            Assert.Equal(8.5, PartParityMotionRules.FrameTargetSeconds(33, 2.0, 8), 12);
            Assert.Equal(24.0, PartParityMotionRules.FrameTargetSeconds(64, 2.0, 8), 12);
            Assert.Equal(25.0, PartParityMotionRules.FrameTargetSeconds(65, 2.0, 8), 12);
            for (int k = 1; k < 120; k++)
                Assert.True(PartParityMotionRules.FrameTargetSeconds(k, 2.0, 8) > PartParityMotionRules.FrameTargetSeconds(k - 1, 2.0, 8));
            // frames= sets the spacing: 4 per motion time.
            Assert.Equal(0.5, PartParityMotionRules.FrameTargetSeconds(1, 2.0, 4), 12);
        }

        [Fact]
        public void TheWindow_EndsAtRealDonePlusTheGhostsRunPlusPad_CappedAt40()
        {
            // A 2 s clip whose ghost runs its 2 s clip: about 2 T + 1 s, 17 frames past frame 0.
            var w = PartParityMotionRules.PlanFrameTargets(2.0, 2.1, 2.0, 0.0, 8);
            Assert.Equal(4.6, w.EndS, 12);
            Assert.Equal(0.25, w.SpacingS, 12);
            Assert.False(w.Capped);
            Assert.Equal(19, w.TargetsS.Count);
            Assert.Equal(4.5, w.TargetsS.Last(), 12);
            // The late control's 1 s shift extends the window by its own change.
            Assert.Equal(23, PartParityMotionRules.PlanFrameTargets(2.0, 2.1, 2.0, 1.0, 8).TargetsS.Count);
            var capped = PartParityMotionRules.PlanFrameTargets(30.0, 30.0, 30.0, 0.0, 8);
            Assert.True(capped.Capped);
            Assert.Equal(40.0, capped.EndS);
            Assert.True(capped.TargetsS.Last() <= 40.0);
        }

        [Fact]
        public void FrameJitter_TagsAFrameLaterThanHalfAnInterval()
        {
            Assert.Null(PartParityMotionRules.FrameJitterTag(1.0, 1.125, 2.0, 8));
            Assert.Equal("frame-jitter:126", PartParityMotionRules.FrameJitterTag(1.0, 1.126, 2.0, 8));
            Assert.Null(PartParityMotionRules.FrameJitterTag(1.0, 0.9, 2.0, 8));
        }

        [Fact]
        public void TheServoTarget_SweepsTowardTheNearerLimit()
        {
            Assert.Equal(45.0, PartParityMotionRules.ServoTarget(false, 0.0, -90.0, 90.0));
            Assert.Equal(90.0, PartParityMotionRules.ServoTarget(false, 60.0, -90.0, 90.0));
            Assert.Equal(45.0, PartParityMotionRules.ServoTarget(false, 90.0, -90.0, 90.0));
            Assert.Equal(1.2, PartParityMotionRules.ServoTarget(true, 0.0, 0.0, 2.4), 12);
            Assert.Equal(2.0, PartParityMotionRules.ServoSeconds(0.0, 45.0, 22.5), 12);
            Assert.True(double.IsNaN(PartParityMotionRules.ServoSeconds(0.0, 45.0, 0.0)));
            Assert.Equal(2.0, PartParityMotionRules.ClipSeconds(1.0, -0.5), 12);
            Assert.True(PartParityMotionRules.ServoDone(false, 44.95, 45.0, 0.1));
            Assert.False(PartParityMotionRules.ServoDone(true, 45.0, 45.0, 0.1));
        }

        [Fact]
        public void TheScout_MergesTheGridWithEveryRealFrame()
        {
            var s = PartParityMotionRules.PlanScoutOffsets(0.2, new[] { 0.0, 0.07, 0.1 });
            Assert.Equal(new[] { 0.0, 0.05, 0.07, 0.1, 0.15, 0.2 }, s.Select(v => Math.Round(v, 9)));
        }

        [Fact]
        public void TheMatchedOffset_InterpolatesAndRefusesAJump()
        {
            var ss = new[] { 0.0, 0.1, 0.2, 0.3 };
            Assert.Equal(0.15, PartParityMotionRules.MatchedOffset(ss, new[] { 0.0, 0.2, 0.4, 0.6 }, 0.3), 12);
            Assert.Equal(0.0, PartParityMotionRules.MatchedOffset(ss, new[] { 0.0, 0.2, 0.4, 0.6 }, 0.0));
            Assert.True(double.IsNaN(PartParityMotionRules.MatchedOffset(ss, new[] { 0.0, 0.2, 0.4, 0.6 }, 0.7)));
            Assert.True(double.IsNaN(PartParityMotionRules.MatchedOffset(ss, new[] { 0.0, 0.0, 1.0, 1.0 }, 0.5)));
            Assert.Equal(0.2, PartParityMotionRules.MatchedOffset(ss, new[] { 0.0, 0.8, 1.0, 1.0 }, 1.0), 12);
            Assert.True(PartParityMotionRules.RenderAgrees(true, 0.5, 0.50005, 0.3));
            Assert.False(PartParityMotionRules.RenderAgrees(true, 0.5, 0.501, 0.3));
            Assert.True(PartParityMotionRules.RenderAgrees(false, 0.5, 0.55, 0.1));
        }

        // ----- progress (H.1) -----

        private static PartParityMotionPose RotZ(double deg)
        {
            double h = deg * Math.PI / 360.0;
            return new PartParityMotionPose { Rotation = new[] { 0.0, 0.0, Math.Sin(h), Math.Cos(h) } };
        }

        [Fact]
        public void PoseProgress_AccumulatesTravel_PastHalfATurn()
        {
            var track = new PartParityMotionTrack { Path = "a" };
            for (int k = 0; k <= 8; k++) track.Poses.Add(RotZ(k * 30.0));
            var t = PartParityMotionRules.TransformProgress(track);
            Assert.Equal("rotation", t.Channel);
            Assert.Equal(240.0, t.TravelRotationDeg, 9);
            Assert.Equal(0.5, t.Fraction[4], 9);
            Assert.Equal(1.0, t.Fraction[8], 9);
        }

        [Fact]
        public void SideProgress_IsTheTravelWeightedMean_AStillTransformLeftOut()
        {
            var fast = new PartParityMotionTrack { Path = "fast" };
            var slow = new PartParityMotionTrack { Path = "slow" };
            var still = new PartParityMotionTrack { Path = "still" };
            for (int k = 0; k <= 2; k++)
            {
                fast.Poses.Add(RotZ(k == 0 ? 0.0 : 90.0));
                slow.Poses.Add(RotZ(k * 15.0));
                still.Poses.Add(RotZ(k * 0.1));
            }
            var side = PartParityMotionRules.SideProgress(new[] { fast, slow, still }, 3);
            Assert.Null(side.Transforms[2].Channel);
            // fast: weight 180 (90 deg / 0.5), fraction 1 at frame 1; slow: weight 60, fraction 0.5.
            Assert.Equal((180.0 * 1.0 + 60.0 * 0.5) / 240.0, side.Progress[1], 9);
            Assert.Equal(1.0, side.Progress[2], 9);
            Assert.Equal(0.0, side.Progress[0], 9);
        }

        [Fact]
        public void PictureProgress_IsAOverAPlusB()
        {
            Assert.Equal(0.25, PartParityMotionRules.PictureProgress(10, 30));
            Assert.Equal(0.0, PartParityMotionRules.PictureProgress(0, 0));
        }

        // ----- timing over the committed golden (H.1) -----

        private static readonly string[] Measures =
            { "lagS", "onsetLagS", "endLagS", "durationRatio", "curveShapeDev", "staggerDev", "travelRatio", "angleMaxDevDeg" };

        private static List<Dictionary<string, object>> GoldenCases()
        {
            string path = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek.Tests",
                "Fixtures", "PartParity", "motion", "timing-golden.json");
            var doc = (Dictionary<string, object>)PartParityJsonReader.Parse(File.ReadAllText(path));
            Assert.Equal("parsek-part-parity-motion-golden/1", doc["schema"]);
            return ((List<object>)doc["cases"]).Cast<Dictionary<string, object>>().ToList();
        }

        private static double D(object o) => o == null ? double.NaN : Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);

        private static double[] Arr(object o) => o == null ? null : ((List<object>)o).Select(D).ToArray();

        private static PartParityMotionSeries Series(Dictionary<string, object> c)
        {
            var s = (Dictionary<string, object>)c["series"];
            var series = new PartParityMotionSeries
            {
                RealS = Arr(s["realS"]), RealProgress = Arr(s["realProgress"]),
                ScoutS = Arr(s["scoutS"]), ScoutProgress = Arr(s["scoutProgress"]),
                GhostSameProgress = Arr(s["ghostSameProgress"]),
                RealClock = Arr(s["realClock"]), GhostClock = Arr(s["ghostClock"]),
            };
            foreach (Dictionary<string, object> t in (List<object>)s["transforms"])
                series.Transforms.Add(new PartParityMotionTransformSeries
                {
                    Path = (string)t["path"], TravelReal = D(t["travelReal"]), TravelGhost = D(t["travelGhost"]),
                    Real = Arr(t["real"]), Ghost = Arr(t["ghost"]),
                });
            return series;
        }

        private static double Measure(PartParityMotionTiming t, string key)
        {
            switch (key)
            {
                case "lagS": return t.LagS;
                case "onsetLagS": return t.OnsetLagS;
                case "endLagS": return t.EndLagS;
                case "durationRatio": return t.DurationRatio;
                case "curveShapeDev": return t.CurveShapeDev;
                case "staggerDev": return t.StaggerDev;
                case "travelRatio": return t.TravelRatio;
                case "angleMaxDevDeg": return t.AngleMaxDevDeg;
                default: throw new ArgumentException(key);
            }
        }

        private static bool Same(double a, double b) => (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) <= 1e-9;

        [Fact]
        public void TheTiming_EqualsThePythonRecomputeOfEveryGoldenCurve()
        {
            var cases = GoldenCases();
            Assert.Equal(10, cases.Count);
            foreach (var c in cases)
            {
                string name = (string)c["name"];
                var t = PartParityMotionRules.Timing(Series(c));
                var expected = (Dictionary<string, object>)c["timing"];
                foreach (string key in Measures)
                    Assert.True(Same(D(expected[key]), Measure(t, key)), name + " " + key + " " + Measure(t, key));
                for (int i = 0; i < 5; i++)
                {
                    string ck = PartParityJson.MotionCrossingKeys[i];
                    Assert.True(Same(D(((Dictionary<string, object>)expected["real"])[ck]), t.RealCrossings[i]), name + " real." + ck);
                    Assert.True(Same(D(((Dictionary<string, object>)expected["ghost"])[ck]), t.GhostCrossings[i]), name + " ghost." + ck);
                }
                Assert.Equal(((List<object>)expected["flags"]).Cast<string>(), t.Flags);
                // The writer's timing block round-trips through the reader to the same numbers.
                var back = (Dictionary<string, object>)PartParityJsonReader.Parse(PartParityJson.MotionTimingBlock(t));
                Assert.Equal(PartParityJson.MotionTimingKeys, back.Keys);
                foreach (string key in Measures) Assert.True(Same(D(back[key]), Measure(t, key)), name + " block " + key);
            }
        }

        [Fact]
        public void EachSyntheticCurve_MovesExactlyItsOwnMeasure()
        {
            var cases = GoldenCases();
            var same = PartParityMotionRules.Timing(Series(cases.Single(c => (string)c["name"] == "same")));
            Assert.Empty(same.Flags);
            Assert.Equal(0.0, same.LagS, 12);
            Assert.Equal(1.0, same.DurationRatio, 12);
            Assert.Equal(0.0, same.CurveShapeDev, 12);
            foreach (var c in cases)
            {
                string name = (string)c["name"];
                var moves = new HashSet<string>(((List<object>)c["moves"]).Cast<string>());
                var t = PartParityMotionRules.Timing(Series(c));
                foreach (string key in Measures)
                    Assert.True(moves.Contains(key) != Same(Measure(same, key), Measure(t, key)), name + " " + key);
                Assert.Equal(moves.Where(m => m.StartsWith("flag:", StringComparison.Ordinal)).Select(m => m.Substring(5)).OrderBy(f => f, StringComparer.Ordinal),
                    t.Flags.OrderBy(f => f, StringComparer.Ordinal));
            }
            var byName = cases.ToDictionary(c => (string)c["name"], c => PartParityMotionRules.Timing(Series(c)));
            Assert.Equal(1.0, byName["late"].LagS, 9);
            Assert.Equal(2.0, byName["slow"].DurationRatio, 9);
            Assert.Equal(0.15, byName["eased"].CurveShapeDev, 9);
            Assert.Equal(0.0, byName["staged"].CurveShapeDev, 9);
            Assert.True(byName["staged"].StaggerDev > 0.3);
            Assert.Equal(9.0, byName["servo-late"].AngleMaxDevDeg, 9);
            Assert.Equal(3.0, byName["travel-differs"].TravelRatio, 9);
            // The fail lines read the measures as H.3 does.
            Assert.True(PartParityMotionRules.LagFails(byName["late"].LagS, byName["late"].RealDurationS));
            Assert.False(PartParityMotionRules.LagFails(byName["same"].LagS, byName["same"].RealDurationS));
            Assert.True(PartParityMotionRules.DurationFails(byName["slow"].DurationRatio));
            Assert.False(PartParityMotionRules.DurationFails(byName["late"].DurationRatio));
        }

        [Fact]
        public void TheFlags_ReadTheirOwnShapes()
        {
            double[] real = { 0.0, 0.0, 0.2, 0.4, 0.6, 0.8, 1.0, 1.0 };
            Assert.True(PartParityMotionRules.Snapped(real, new[] { 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0 }));
            // A real that itself jumps in two frames is not a snap to compare against.
            Assert.False(PartParityMotionRules.Snapped(new[] { 0.0, 0.0, 0.5, 1.0 }, new[] { 0.0, 0.0, 1.0, 1.0 }));
            Assert.True(PartParityMotionRules.Stepped(real, new[] { 0.0, 0.0, 0.3, 0.3, 0.6, 0.6, 1.0, 1.0 }));
            Assert.False(PartParityMotionRules.Stepped(real, new[] { 0.0, 0.0, 0.3, 0.3, 0.6, 0.8, 1.0, 1.0 }));
            // A late start's flat head is not a step: the holds count only inside the band.
            Assert.False(PartParityMotionRules.Stepped(real, new[] { 0.0, 0.0, 0.0, 0.0, 0.2, 0.4, 0.6, 0.8 }));
            Assert.True(PartParityMotionRules.NotModelled(real, new[] { 0.0, 0.05 }));
            Assert.False(PartParityMotionRules.NotModelled(real, new[] { 0.0, 0.1 }));
            Assert.True(PartParityMotionRules.ProgressDisagrees(1.0, 1.6, 0.25));
            Assert.False(PartParityMotionRules.ProgressDisagrees(1.0, 1.4, 0.25));
        }

        // ----- the control judges (H.3) -----

        private static PartParityMotionTiming T(double lag, double ratio, double shape, double onset = 0.0, double t02 = 0.5, double t98 = 3.0)
            => new PartParityMotionTiming
            {
                LagS = lag, DurationRatio = ratio, CurveShapeDev = shape, OnsetLagS = onset,
                RealCrossings = new[] { t02, 1.0, 1.75, 2.5, t98 },
            };

        [Fact]
        public void TheRealVsRealJudge_HoldsTheNoiseFloor()
        {
            Assert.Equal("ok", PartParityMotionRules.JudgeRealVsReal(T(0.02, 1.01, 0.01), 0.25, 2.0, true, "ok", out string d));
            Assert.Null(d);
            Assert.Equal("bad", PartParityMotionRules.JudgeRealVsReal(T(0.3, 1.0, 0.0), 0.25, 2.0, true, "ok", out d));
            Assert.Contains("lagS", d);
            Assert.Equal("bad", PartParityMotionRules.JudgeRealVsReal(T(0.0, 1.06, 0.0), 0.25, 2.0, true, "ok", out d));
            Assert.Equal("bad", PartParityMotionRules.JudgeRealVsReal(T(0.0, 1.0, 0.06), 0.25, 2.0, true, "ok", out d));
            Assert.Equal("bad", PartParityMotionRules.JudgeRealVsReal(T(0.0, 1.0, 0.0), 0.25, 2.0, false, "ok", out d));
            Assert.Equal("bad", PartParityMotionRules.JudgeRealVsReal(T(0.0, 1.0, 0.0), 0.25, 2.0, true, "action-failed", out d));
            Assert.Equal("setup action-failed", d);
        }

        [Fact]
        public void TheSlowJudge_WantsTheRateDoubled_TheOnsetKept_AndTheDurationLineCrossed()
        {
            var normal = T(0.0, 1.0, 0.0);
            Assert.Equal("ok", PartParityMotionRules.JudgeGhostSlow(normal, T(1.25, 2.0, 0.2, 0.05), 0.25, true, "ok", out _));
            Assert.Equal("bad", PartParityMotionRules.JudgeGhostSlow(normal, T(1.25, 1.5, 0.2, 0.05), 0.25, true, "ok", out string d));
            Assert.Contains("ratio", d);
            Assert.Equal("bad", PartParityMotionRules.JudgeGhostSlow(normal, T(1.25, 2.0, 0.2, 0.5), 0.25, true, "ok", out d));
            Assert.Contains("onset", d);
            Assert.Equal("bad", PartParityMotionRules.JudgeGhostSlow(normal, T(1.25, 2.0, 0.2, 0.05), 0.25, false, "ok", out d));
            // A normal route already off on rate moves the slow route's ratio with it: judged relative.
            Assert.Equal("ok", PartParityMotionRules.JudgeGhostSlow(T(0.0, 0.9, 0.0), T(1.0, 1.8, 0.2), 0.25, true, "ok", out _));
        }

        [Fact]
        public void TheLateJudge_WantsTheShiftExactly_AndTheLagLineCrossedOnlyWhenTheShiftIsPastIt()
        {
            var normal = T(0.1, 1.0, 0.0);
            Assert.Equal("ok", PartParityMotionRules.JudgeGhostLate(normal, T(1.1, 1.0, 0.0), "ok", out _));
            Assert.Equal("bad", PartParityMotionRules.JudgeGhostLate(normal, T(0.9, 1.0, 0.0), "ok", out string d));
            Assert.Contains("lag shift", d);
            Assert.Equal("bad", PartParityMotionRules.JudgeGhostLate(normal, T(1.1, 1.05, 0.0), "ok", out d));
            Assert.Contains("durationRatio", d);
            // The lag line is max(0.5 s, 0.25 T_real): a 10 s real motion holds a 1 s lag inside
            // it, so the relative requirement is the whole test there.
            Assert.Equal("ok", PartParityMotionRules.JudgeGhostLate(T(0.1, 1.0, 0.0, 0.0, 0.0, 10.0),
                T(1.1, 1.0, 0.0, 0.0, 0.0, 10.0), "ok", out _));
            // A 2 s real motion has a 0.5 s line: the 1 s shift is past it and must fail on lag.
            Assert.Equal("bad", PartParityMotionRules.JudgeGhostLate(T(-0.6, 1.0, 0.0, 0.0, 0.0, 2.0),
                T(0.4, 1.0, 0.0, 0.0, 0.0, 2.0), "ok", out d));
            Assert.Contains("does not fail on lag", d);
        }

        [Theory]
        [InlineData(0.0, true)]
        [InlineData(2.0, true)]
        [InlineData(3.99, true)]
        [InlineData(4.0, false)]
        [InlineData(10.0, false)]
        [InlineData(double.NaN, true)]
        public void TheLateRoute_MustFailOnLag_OnlyBelowFourSecondsOfRealMotion(double realDurationS, bool must)
            => Assert.Equal(must, PartParityMotionRules.LateMustFailOnLag(realDurationS));
    }
}
