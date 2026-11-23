using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <see cref="PartParityFxRules"/> (design-part-ghost-parity.md Appendix
    /// E, piece F1): the four families and their predicates, the state words, the tiebreak and
    /// the ids, the exclusions, the committed fixture counts of E.1, the RCS input rule, the
    /// renderer layer rule, the 8-bit tile rule, the plateau, the timeouts and the wave bound,
    /// the control subjects and the FX control judges.
    /// </summary>
    public class PartParityFxRulesTests
    {
        // ----- builders -----

        private static PartParityStateModule Mod(int index, string name, string chain = null, params string[] kv)
        {
            var m = new PartParityStateModule { Index = index, Name = name };
            if (chain != null) m.TypeChain.AddRange(chain.Split(','));
            if (kv.Length > 0)
            {
                m.Config = new ConfigNode("MODULE");
                foreach (string pair in kv) AddKey(m.Config, pair);
            }
            return m;
        }

        private static void AddKey(ConfigNode node, string pair)
        {
            int eq = pair.IndexOf('=');
            string key = pair.Substring(0, eq), value = pair.Substring(eq + 1);
            if (key == "PROPELLANT.name") node.AddNode("PROPELLANT").AddValue("name", value);
            else node.AddValue(key, value);
        }

        private static PartParityStatePart Part(string name, params PartParityStateModule[] mods)
        {
            var p = new PartParityStatePart { Name = name };
            p.Modules.AddRange(mods);
            return p;
        }

        private static PartParityFxEnumeration Enumerate(PartParityStatePart p)
            => PartParityFxRules.Enumerate(p, new PartParityCaseKey { Part = p.Name, Source = "Squad" });

        private static string[] Words(PartParityFxEnumeration r)
            => r.Cases.Select(k => k.State + "/" + k.Context).ToArray();

        // ----- families and predicates -----

        [Fact]
        public void TheTables_AreTheDesignsAndDistinctFromP6()
        {
            Assert.Equal(new[] { "plume", "rcs", "heat", "light" }, PartParityFxRules.Families);
            Assert.Equal(new[] { "plume", "rcs", "heat", "plate" }, PartParityFxRules.ArgWords);
            Assert.Equal(new[] { "burn-full", "burn-low", "rcs-fire", "heat-medium", "heat-hot", "lit" }, PartParityFxRules.States);
            Assert.Equal(new[] { "fx-reentry-camera", "fx-blackbody", "fx-ablator-char", "fx-needs-air", "fx-throttle-locked" },
                PartParityFxRules.ExclusionReasons);
            Assert.Equal(new[] { "fx-side-a", "fx-side-b", "fx-bottom", "fx-far" }, PartParityFxRules.Views);
            Assert.Equal("light", PartParityFxRules.FamilyForArg("plate"));
            Assert.Null(PartParityFxRules.FamilyForArg("light"));
            // The plate context and the FX words never enter P6's tables.
            Assert.DoesNotContain("plate", PartParityStateRules.KnownContexts);
            Assert.DoesNotContain("plate", PartParityStateRules.OrbitContexts);
            foreach (string s in PartParityFxRules.States.Where(s => s != "lit"))
                Assert.DoesNotContain(s, PartParityStateRules.KnownStates);
            foreach (string f in new[] { "plume", "rcs", "heat" })
                Assert.DoesNotContain(f, PartParityStateRules.Families);
            Assert.All(PartParityFxRules.ExclusionReasons, r => Assert.DoesNotContain(r, PartParityStateRules.ExclusionReasons));
        }

        [Fact]
        public void AnEngine_PlansBothBurns_AndASolidBoosterOnlyFull()
        {
            var r = Enumerate(Part("liquidEngine2.v2", Mod(0, "ModulePartVariants"),
                Mod(2, "ModuleEngines", null, "PROPELLANT.name=LiquidFuel", "PROPELLANT.name=Oxidizer")));
            Assert.Equal(new[] { "burn-full/alone", "burn-low/alone" }, Words(r));
            Assert.Equal(1.0, r.Cases[0].FxOption.Throttle);
            Assert.Equal(0.3, r.Cases[1].FxOption.Throttle);
            Assert.Equal(100, PartParityFxRules.IndependentThrottlePercentageFor(r.Cases[0].FxOption));
            Assert.Equal(30, PartParityFxRules.IndependentThrottlePercentageFor(r.Cases[1].FxOption));
            Assert.Empty(r.Exclusions);
            Assert.Null(r.Cases[0].StateOption);

            r = Enumerate(Part("solidBooster.sm.v2", Mod(0, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule",
                "throttleLocked=True", "useEngineResponseTime=True", "PROPELLANT.name=SolidFuel")));
            Assert.Equal(new[] { "burn-full/alone" }, Words(r));
            Assert.True(r.Cases[0].FxOption.ThrottleLocked);
            Assert.True(r.Cases[0].FxOption.UseEngineResponseTime);
            Assert.Equal("fx-throttle-locked", Assert.Single(r.Exclusions).Reason);
        }

        [Fact]
        public void AJet_PlansInTheAir_TwoModulesTakeTheTiebreak_AndNeedsAirForOrbit()
        {
            var r = Enumerate(Part("turboJet", Mod(0, "MultiModeEngine"),
                Mod(1, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule", "engineID=Dry",
                    "useEngineResponseTime=True", "PROPELLANT.name=IntakeAir", "PROPELLANT.name=LiquidFuel"),
                Mod(2, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule", "engineID=Wet",
                    "useEngineResponseTime=True", "PROPELLANT.name=IntakeAir", "PROPELLANT.name=LiquidFuel")));
            Assert.Equal(new[] { "burn-full-m1/air", "burn-low-m1/air", "burn-full-m2/air", "burn-low-m2/air" }, Words(r));
            Assert.All(r.Cases, k => Assert.True(k.FxOption.AirBreathing && k.FxOption.MultiMode));
            Assert.Equal("Wet", r.Cases[2].FxOption.EngineId);
            // The recorder keys engines by their position among the part's engine modules
            // (FlightRecorder's engine cache, EncodeEngineKey's midx), not the prefab index the
            // case id's tiebreak reads.
            Assert.Equal(new[] { 0, 0, 1, 1 }, r.Cases.Select(k => k.FxOption.RecorderKeyIndex));
            Assert.Equal(new[] { 1, 1, 2, 2 }, r.Cases.Select(k => k.FxOption.ModuleIndex));
            Assert.Equal(new[] { "fx-needs-air", "fx-needs-air" }, r.Exclusions.Select(e => e.Reason));
            // fx-needs-air belongs to an orbit run's list only.
            Assert.All(r.Exclusions, e => Assert.True(PartParityFxRules.ExclusionApplies(e, false)));
            Assert.All(r.Exclusions, e => Assert.False(PartParityFxRules.ExclusionApplies(e, true)));
            string id = TestCommandPartParitySweep.CaseId(r.Cases[3]);
            Assert.StartsWith("turbojet--default--burn-low-m2--air--", id);
        }

        [Fact]
        public void TheRapier_SplitsItsModesBetweenTheContexts_WithNoTiebreak()
        {
            var r = Enumerate(Part("RAPIER", Mod(0, "MultiModeEngine"),
                Mod(1, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule", "engineID=AirBreathing",
                    "PROPELLANT.name=IntakeAir", "PROPELLANT.name=LiquidFuel"),
                Mod(2, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule", "engineID=ClosedCycle",
                    "PROPELLANT.name=LiquidFuel", "PROPELLANT.name=Oxidizer")));
            Assert.Equal(new[] { "burn-full/air", "burn-low/air", "burn-full/alone", "burn-low/alone" }, Words(r));
            Assert.Equal("ClosedCycle", r.Cases[2].FxOption.EngineId);
        }

        [Fact]
        public void Rcs_Heat_Lamp_AndAblator()
        {
            var r = Enumerate(Part("mk1pod.v2", Mod(0, "ModuleRCSFX", "ModuleRCSFX,ModuleRCS,PartModule"),
                Mod(1, "ModuleAnimateHeat", "ModuleAnimateHeat,ModuleAnimationSetter,PartModule"),
                Mod(2, "ModuleAblator"), Mod(3, "ModuleLight"), Mod(4, "ModuleLight")));
            Assert.Equal(new[] { "rcs-fire/alone", "heat-medium/alone", "heat-hot/alone", "lit/plate" }, Words(r));
            Assert.Equal(0.5, r.Cases[1].FxOption.HeatScalar);
            Assert.Equal(0.999, r.Cases[2].FxOption.HeatScalar);
            // RCS keys count RCS modules only; heat and the light check key on the prefab index.
            Assert.Equal(0, r.Cases[0].FxOption.RecorderKeyIndex);
            Assert.Equal(1, r.Cases[1].FxOption.RecorderKeyIndex);
            Assert.Equal(3, r.Cases[3].FxOption.RecorderKeyIndex);
            var block = Enumerate(Part("RCSBlock.v2", Mod(0, "ModulePartVariants"),
                Mod(1, "ModuleRCSFX", "ModuleRCSFX,ModuleRCS,PartModule")));
            Assert.Equal(1, block.Cases.Single().FxOption.ModuleIndex);
            Assert.Equal(0, block.Cases.Single().FxOption.RecorderKeyIndex);
            // The light check is the first ModuleLight only.
            Assert.Equal(3, r.Cases[3].FxOption.ModuleIndex);
            Assert.Equal("fx-ablator-char", Assert.Single(r.Exclusions).Reason);
            Assert.StartsWith("mk1pod.v2--default--lit--plate--", TestCommandPartParitySweep.CaseId(r.Cases[3]));
            // A heat clip on a subclass is not ModuleAnimateHeat by name: PollAnimateHeat compares the name.
            r = Enumerate(Part("x", Mod(0, "FancyHeat", "FancyHeat,ModuleAnimateHeat,PartModule")));
            Assert.Empty(r.Cases);
        }

        [Fact]
        public void WaterfallParts_AreReferenceForPlumeAndRcsOnly()
        {
            var r = Enumerate(Part("liquidEngine", Mod(0, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule",
                "PROPELLANT.name=LiquidFuel"), Mod(1, "ModuleRCS"), Mod(2, "ModuleAnimateHeat"), Mod(3, "ModuleLight"),
                Mod(4, "ModuleWaterfallFX")));
            foreach (var k in r.Cases)
            {
                bool stochastic = k.FxOption.Family == "plume" || k.FxOption.Family == "rcs";
                Assert.Equal(stochastic ? "waterfall" : null, k.FxOption.Reference);
            }
        }

        [Fact]
        public void TheFxIds_NeverCollideWithTheP6Ids()
        {
            var part = Part("spotLight1", Mod(0, "ModuleLight"));
            var fx = Enumerate(part);
            var state = PartParityStateRules.Enumerate(part, new PartParityCaseKey { Part = "spotLight1" });
            string fxId = TestCommandPartParitySweep.CaseId(fx.Cases.Single());
            string stateId = TestCommandPartParitySweep.CaseId(state.Cases.Single());
            Assert.NotEqual(stateId, fxId);
            Assert.StartsWith("spotlight1--default--lit--", stateId);
            Assert.DoesNotContain("--plate--", stateId);
        }

        // ----- the committed fixtures (the E.1 counts) -----

        private static List<PartParityStatePart> LoadFixture(string profile)
        {
            string path = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek.Tests",
                "Fixtures", "PartParity", "fx-parts-" + profile + ".txt");
            Assert.True(File.Exists(path), "fixture missing: " + path);
            var parts = new List<PartParityStatePart>();
            PartParityStatePart cur = null;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                if (f[0] == "P")
                {
                    cur = new PartParityStatePart { Name = f[1] };
                    if (f.Length > 2 && f[2].Length > 0) cur.StackNodes.AddRange(f[2].Split(','));
                    parts.Add(cur);
                    continue;
                }
                var m = new PartParityStateModule { Index = int.Parse(f[1]), Name = f[2] };
                if (f[3] != "-") m.TypeChain.AddRange(f[3].Split(','));
                if (f[4] != "-")
                {
                    m.Config = new ConfigNode("MODULE");
                    foreach (string kv in f[4].Split('|')) AddKey(m.Config, kv);
                }
                cur.Modules.Add(m);
            }
            return parts;
        }

        private static PartParityFxEnumeration EnumerateFixture(string profile)
        {
            var all = new PartParityFxEnumeration();
            foreach (var p in LoadFixture(profile))
                all.AddRange(PartParityFxRules.Enumerate(p, new PartParityCaseKey { Part = p.Name }));
            return all;
        }

        private static int Count(PartParityFxEnumeration r, string family, string context = null, string state = null)
            => r.Cases.Count(k => k.FxOption.Family == family && (context == null || k.Context == context)
                                  && (state == null || k.FxOption.State == state));

        private static int Reasons(PartParityFxEnumeration r, string reason) => r.Exclusions.Count(e => e.Reason == reason);

        [Theory]
        // profile, orbit burn-full, orbit burn-low, air plume, rcs, heat, plate, throttle-locked, needs-air, ablator, reference
        [InlineData("stock-minimal", 43, 31, 14, 7, 32, 15, 12, 7, 8, 0)]
        [InlineData("modded-compat", 60, 42, 14, 19, 26, 18, 18, 7, 12, 109)]
        public void TheFixtures_EnumerateTheE1Counts(string profile, int full, int low, int air, int rcs, int heat,
            int plate, int locked, int needsAir, int ablator, int reference)
        {
            var r = EnumerateFixture(profile);
            Assert.Equal(full, Count(r, "plume", "alone", "burn-full"));
            Assert.Equal(low, Count(r, "plume", "alone", "burn-low"));
            Assert.Equal(air, Count(r, "plume", "air"));
            Assert.Equal(rcs, Count(r, "rcs"));
            Assert.Equal(heat, Count(r, "heat"));
            Assert.Equal(plate, Count(r, "light", "plate"));
            Assert.Equal(locked, Reasons(r, "fx-throttle-locked"));
            Assert.Equal(needsAir, Reasons(r, "fx-needs-air"));
            Assert.Equal(ablator, Reasons(r, "fx-ablator-char"));
            Assert.Equal(0, Reasons(r, "fx-reentry-camera") + Reasons(r, "fx-blackbody"));
            Assert.Equal(reference, r.Cases.Count(k => k.FxOption.Reference != null));
            Assert.Equal(r.Cases.Count, r.Cases.Select(TestCommandPartParitySweep.CaseId).Distinct().Count());
            Assert.All(r.Cases, k => Assert.True(TestCommandCaptureScreenshot.IsValidLabel(TestCommandPartParitySweep.CaseId(k))));
            string line = PartParityFxRules.FormatCounts("all", "none", r.Cases, 3, r.Exclusions);
            Assert.StartsWith("fx cases: fx=all contexts=none install=" + r.Cases.Count + " (plume=" + (full + low + air)
                + " rcs=" + rcs + " heat=" + heat + " light=" + plate + ") planned=3", line);
        }

        // ----- drive -----

        [Fact]
        public void TheTimeoutsAndBuckets()
        {
            var burn = new PartParityFxOption { Family = "plume" };
            var jet = new PartParityFxOption { Family = "plume", UseEngineResponseTime = true };
            Assert.Equal(PartParityFxBucket.Burn, PartParityFxRules.BucketFor(burn));
            Assert.Equal(PartParityFxBucket.Spool, PartParityFxRules.BucketFor(jet));
            Assert.Equal(10.0, PartParityFxRules.PressTimeoutSeconds(burn));
            Assert.Equal(45.0, PartParityFxRules.PressTimeoutSeconds(jet));
            Assert.Equal(PartParityFxBucket.Rcs, PartParityFxRules.BucketFor(new PartParityFxOption { Family = "rcs" }));
            Assert.Equal(PartParityFxBucket.Heat, PartParityFxRules.BucketFor(new PartParityFxOption { Family = "heat" }));
            Assert.Equal(PartParityFxBucket.Plate, PartParityFxRules.BucketFor(new PartParityFxOption { Family = "light" }));
            Assert.Equal(5.0, PartParityFxRules.PressTimeoutSeconds(new PartParityFxOption { Family = "light" }));
            // A wave of four jets: 20 spawn + 45 spool + 8 plateau + 4 x 3 capture + 5 teardown.
            var wave = Enumerable.Repeat(jet, 3).Concat(new[] { burn }).ToList();
            Assert.Equal(20 + 45 + 8 + 12 + 5, PartParityFxRules.WaveWorstCaseSeconds(wave), 9);
            var plates = Enumerable.Repeat(new PartParityFxOption { Family = "light" }, 2).ToList();
            Assert.Equal(20 + 5 + 5 + 1.0 + 5, PartParityFxRules.WaveWorstCaseSeconds(plates), 9);
            var heats = Enumerable.Repeat(new PartParityFxOption { Family = "heat" }, 4).ToList();
            Assert.Equal(20 + 5 + 0.5 + 1.2 + 5, PartParityFxRules.WaveWorstCaseSeconds(heats), 9);
        }

        [Fact]
        public void ASlowSpool_GetsTheTimeStocksOwnSpoolNeeds()
        {
            // Never under 45 s: the RAPIER (0.2 /s) and the Wheesley (0.12 /s, 38 s to 0.99) fit.
            Assert.Equal(45.0, PartParityFxRules.SpoolPressTimeoutSeconds(1.0, 0.2));
            Assert.Equal(45.0, PartParityFxRules.SpoolPressTimeoutSeconds(1.0, 0.12));
            // The Goliath (0.06 /s): ln(100) / 0.06 = 76.8 s full, ln(30) / 0.06 = 56.7 s low, and 10 %.
            Assert.Equal(1.1 * Math.Log(100.0) / 0.06, PartParityFxRules.SpoolPressTimeoutSeconds(1.0, 0.06), 9);
            Assert.Equal(1.1 * Math.Log(30.0) / 0.06, PartParityFxRules.SpoolPressTimeoutSeconds(0.3, 0.06), 9);
            // Stock's law really lands inside the tolerance by then, 0.02 s a step.
            double throttle = 0.0, t = 0.0;
            for (; Math.Abs(throttle - 1.0) > 0.01; t += 0.02) throttle += (1.0 - throttle) * 0.06 * 0.02;
            Assert.InRange(t, 45.0, PartParityFxRules.SpoolPressTimeoutSeconds(1.0, 0.06));
            // Capped, and an engine that names no speed (or a senseless one) keeps the floor.
            Assert.Equal(120.0, PartParityFxRules.SpoolPressTimeoutSeconds(1.0, 0.001));
            foreach (double bad in new[] { double.NaN, 0.0, -1.0 })
                Assert.Equal(45.0, PartParityFxRules.SpoolPressTimeoutSeconds(1.0, bad));
            Assert.Equal(45.0, PartParityFxRules.SpoolPressTimeoutSeconds(double.NaN, 0.06));
            Assert.Equal(45.0, PartParityFxRules.SpoolPressTimeoutSeconds(0.01, 0.06));
            // The option reads it off the module, and the wave bound follows.
            var r = Enumerate(Part("turboFanSize2", Mod(0, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule",
                "useEngineResponseTime=True", "engineAccelerationSpeed=0.06", "PROPELLANT.name=IntakeAir")));
            Assert.Equal(0.06, r.Cases[0].FxOption.EngineAccelerationSpeed);
            Assert.Equal(1.1 * Math.Log(100.0) / 0.06, PartParityFxRules.PressTimeoutSeconds(r.Cases[0].FxOption), 9);
            Assert.Equal(20 + 1.1 * Math.Log(100.0) / 0.06 + 8 + 6 + 5,
                PartParityFxRules.WaveWorstCaseSeconds(r.Cases.Select(k => k.FxOption).ToList()), 9);
        }

        [Fact]
        public void TheDoneSignals()
        {
            Assert.True(PartParityFxRules.PlumeReached(true, 0.995, 1.0, true));
            Assert.False(PartParityFxRules.PlumeReached(true, 0.98, 1.0, true));
            Assert.False(PartParityFxRules.PlumeReached(false, 1.0, 1.0, true));
            Assert.False(PartParityFxRules.PlumeReached(true, 1.0, 1.0, false));
            Assert.False(PartParityFxRules.PlumeReached(true, double.NaN, 1.0, true));
            Assert.True(PartParityFxRules.HeatReached(0.505, 0.5, true));
            Assert.False(PartParityFxRules.HeatReached(0.52, 0.5, true));
            Assert.False(PartParityFxRules.HeatReached(0.5, 0.5, false));
            Assert.True(PartParityFxRules.HoldOk(0.0));
            Assert.False(PartParityFxRules.HoldOk(0.001));
            Assert.False(PartParityFxRules.HoldOk(double.NaN));
        }

        [Fact]
        public void TheHeatTemperature_InvertsStocksScalar()
        {
            // Stock defaults: lerpOffset = -draperPoint, lerpDivRecip = 1 / (maxTemp - draperPoint).
            double draper = 500, max = 2000;
            double recip = 1.0 / (max - draper);
            double t = PartParityFxRules.HeatTemperatureFor(0.5, -draper, recip);
            Assert.Equal(1250.0, t, 9);
            Assert.Equal(0.5, (t + -draper) * recip, 12);
            Assert.True(PartParityFxRules.HeatTemperatureFor(0.999, -draper, recip) < max);
            Assert.True(double.IsNaN(PartParityFxRules.HeatTemperatureFor(0.5, -draper, 0.0)));
            Assert.True(double.IsNaN(PartParityFxRules.HeatTemperatureFor(0.5, -draper, double.PositiveInfinity)));
        }

        // ----- RCS -----

        private static PartParityRcsThruster T(double x, double y, double z, bool active = true, bool atOrigin = false)
            => new PartParityRcsThruster { Axis = new[] { x, y, z }, Active = active, AtOrigin = atOrigin };

        [Fact]
        public void TheCandidateDirections_AreTheCubesTwentySix()
        {
            var dirs = PartParityFxRules.RcsCandidateDirections();
            Assert.Equal(26, dirs.Count);
            Assert.All(dirs, d => Assert.Equal(1.0, Math.Sqrt(d.Sum(c => c * c)), 12));
            Assert.Equal(26, dirs.Select(d => string.Join(",", d.Select(c => Math.Round(c, 6)))).Distinct().Count());
            Assert.Equal(new[] { 1.0, 0, 0 }, dirs[0]);
            Assert.Equal(new[] { -1.0, 0, 0 }, dirs[1]);
        }

        [Fact]
        public void TheRcsInput_FiresTheMostNozzlesEvenly()
        {
            // A four-way block: nozzles along +x, -x, +z, -z. A face fires one, an edge fires two at 0.71.
            var block = new List<PartParityRcsThruster> { T(1, 0, 0), T(-1, 0, 0), T(0, 0, 1), T(0, 0, -1) };
            PartParityRcsChoice c = PartParityFxRules.ChooseRcsInput(block);
            Assert.Equal(2, c.Counted);
            Assert.Equal(new[] { 0, 2 }, c.Fired);
            Assert.Equal(1.0 / Math.Sqrt(2), c.Forces[0], 9);
            Assert.Equal(0.0, c.Spread, 12);
            Assert.Equal(new[] { 0.707107, 0, 0.707107 }, c.Direction.Select(v => Math.Round(v, 6)));
            // The press feeds ctrlState X = d.x, Y = d.z, Z = d.y.
            Assert.Equal(new[] { c.Direction[0], c.Direction[2], c.Direction[1] }, PartParityFxRules.RcsCtrlStateFor(c.Direction));
        }

        [Fact]
        public void TheRcsInput_SkipsInactiveAndOriginThrusters_AndDisabledAxes()
        {
            // Stock skips inactive or world-origin thruster transforms (E0 U2: 5 of 9 on RCSBlock.v2).
            var block = new List<PartParityRcsThruster>
                { T(1, 0, 0), T(0, 0, 1, active: false), T(0, 1, 0, atOrigin: true), T(-1, 0, 0) };
            PartParityRcsChoice c = PartParityFxRules.ChooseRcsInput(block);
            Assert.Equal(1, c.Counted);
            Assert.Equal(new[] { 0 }, c.Fired);
            Assert.Equal(0.0, c.Forces[1]);
            Assert.Equal(0.0, c.Forces[2]);
            Assert.Equal(new[] { 1.0, 0, 0 }, c.Direction);
            // enableX false removes every direction with a reference-x component.
            c = PartParityFxRules.ChooseRcsInput(new List<PartParityRcsThruster> { T(1, 0, 0), T(0, 1, 0) }, enableX: false);
            Assert.Equal(0.0, c.Direction[0]);
            Assert.Equal(new[] { 1 }, c.Fired);
            Assert.Null(PartParityFxRules.ChooseRcsInput(new List<PartParityRcsThruster> { T(1, 0, 0, active: false) }));
            Assert.Null(PartParityFxRules.ChooseRcsInput(null));
        }

        [Fact]
        public void TheRcsInput_BreaksTiesBySpreadThenMeanThenOrder()
        {
            // Two nozzles 90 deg apart and one opposite the first: the edge between the two fires
            // both at 0.71 (spread 0) and beats the face that fires one at 1.0.
            var c = PartParityFxRules.ChooseRcsInput(new List<PartParityRcsThruster> { T(1, 0, 0), T(0, 1, 0), T(-1, 0, 0) });
            Assert.Equal(new[] { 0, 1 }, c.Fired);
            // A lone nozzle: six faces tie on count; the earliest firing one (+y) wins.
            c = PartParityFxRules.ChooseRcsInput(new List<PartParityRcsThruster> { T(0, 1, 0) });
            Assert.Equal(new[] { 0.0, 1.0, 0.0 }, c.Direction);
        }

        [Fact]
        public void TheSpreadAndMean_ReadEveryFiredNozzle()
        {
            // On the (+x,+y) edge a weak third nozzle fires at 0.28 under the even line (0.35):
            // it does not count, but it widens the spread and lowers the mean (E.2: "the
            // smallest spread between the fired nozzles' forces").
            var thrusters = new List<PartParityRcsThruster> { T(1, 0, 0), T(0, 1, 0), T(0.2, 0.2, -0.959) };
            double e = 1.0 / Math.Sqrt(2);
            PartParityRcsChoice c = PartParityFxRules.ScoreRcsDirection(thrusters, new[] { e, e, 0.0 });
            Assert.Equal(2, c.Counted);
            Assert.Equal(new[] { 0, 1, 2 }, c.Fired);
            Assert.Equal(e - c.Forces[2], c.Spread, 9);
            Assert.Equal((2 * e + c.Forces[2]) / 3, c.Mean, 9);
            Assert.Null(PartParityFxRules.ScoreRcsDirection(thrusters, new[] { -e, -e, 0.0 }));
        }

        [Fact]
        public void TheRcsPressCheck_AndThePowerRatio()
        {
            Assert.True(PartParityFxRules.RcsFiredMatches(new[] { 2, 0 }, new[] { 0.7, 0.0, 0.7, 0.005 }, out string d));
            Assert.Null(d);
            Assert.False(PartParityFxRules.RcsFiredMatches(new[] { 0, 1, 2 }, new[] { 0.0, 0.0, 0.0 }, out d));
            Assert.Equal("predicted=[0,1,2] fired=[]", d);
            Assert.Equal(2.0, PartParityFxRules.RcsPowerRatio(1.0, new[] { 0.5, 0.5 }), 12);
            Assert.True(double.IsNaN(PartParityFxRules.RcsPowerRatio(1.0, new double[0])));
            Assert.Equal("rcs-known-difference:nozzles=2/4,power=0.500",
                PartParityFxRules.FormatRcsKnownDifference(2, 4, 0.5));
            Assert.Equal("rcs-known-difference:nozzles=0/4,power=null",
                PartParityFxRules.FormatRcsKnownDifference(0, 4, double.NaN));
        }

        // ----- plateau and layers -----

        [Fact]
        public void ThePlateau()
        {
            Assert.Equal("particles", PartParityFxRules.PlateauSignalFor(true, false));
            Assert.Equal("coverage", PartParityFxRules.PlateauSignalFor(true, true));
            Assert.Equal("coverage", PartParityFxRules.PlateauSignalFor(false, false));
            Assert.True(PartParityFxRules.PlateauSettled(7.5, 7.0, 95, 100));
            Assert.False(PartParityFxRules.PlateauSettled(6.0, 7.0, 95, 100));  // under the longest lifetime
            Assert.False(PartParityFxRules.PlateauSettled(7.5, 7.0, 89, 100)); // moved 11 %
            Assert.True(PartParityFxRules.PlateauSettled(1.0, 0.5, 0, 0));    // dark throughout: judged, not waited on
            Assert.False(PartParityFxRules.PlateauSettled(double.NaN, 0.5, 0, 0));
            Assert.Equal("fx-not-plateaued:ghostStart", PartParityFxRules.NotPlateauedTag("ghostStart"));
        }

        [Theory]
        [InlineData("MeshRenderer", "KSP/Bumped Specular", "body")]
        [InlineData("SkinnedMeshRenderer", "KSP/Diffuse", "body")]
        [InlineData("MeshRenderer", "Waterfall/Additive (Dynamic)", "flame")]
        [InlineData("MeshRenderer", "Waterfall/Additive Directional", "flame")]
        [InlineData("MeshRenderer", "Waterfall/Distortion (Dynamic)", null)]
        [InlineData("ParticleSystemRenderer", "Legacy Shaders/Particles/Additive", "flame")]
        [InlineData("ParticleSystemRenderer", "KSP/Particles/Additive", "flame")]
        [InlineData("ParticleSystemRenderer", "Legacy Shaders/Particles/Additive (Soft)", "flame")]
        [InlineData("ParticleSystemRenderer", "KSP/Particles/Alpha Blended", "smoke")]
        [InlineData("LineRenderer", "Legacy Shaders/Particles/Additive", null)]
        [InlineData("MeshRenderer", null, "body")]
        public void TheRendererLayerRule(string type, string shader, string layer)
            => Assert.Equal(layer, PartParityFxRules.ClassifyFxRenderer(type, shader));

        // ----- the 8-bit tile rule -----

        [Fact]
        public void TheTileAlpha_RoundTripsEveryCount()
        {
            foreach (int n in new[] { 1, 2, 3, 7, 16, 100, 254, 255 })
                for (int k = 0; k <= n; k++)
                {
                    int a = PartParityFxRules.TileAlpha(k, n);
                    Assert.Equal((int)Math.Floor(255.0 * k / n + 0.5), a);
                    Assert.Equal(k, PartParityFxRules.CoveredSamples(a, n));
                }
            Assert.Equal(0, PartParityFxRules.TileAlpha(3, 0));
            // floor(sum / k + 0.5): 3.5 -> 4, 2.5 -> 3, 2.25 -> 2.
            Assert.Equal(4, PartParityFxRules.TileChannel(7, 2));
            Assert.Equal(3, PartParityFxRules.TileChannel(5, 2));
            Assert.Equal(2, PartParityFxRules.TileChannel(9, 4));
            Assert.Equal(0, PartParityFxRules.TileChannel(9, 0));
        }

        [Fact]
        public void TheCoreEnvelopeAndCoverageRules()
        {
            Assert.True(PartParityFxRules.IsCore(8, 16));
            Assert.False(PartParityFxRules.IsCore(7, 16));
            Assert.True(PartParityFxRules.IsCore(2, 3));
            Assert.False(PartParityFxRules.IsCore(1, 3));
            Assert.True(PartParityFxRules.IsEnvelope(1));
            Assert.False(PartParityFxRules.IsEnvelope(0));
            Assert.True(PartParityFxRules.SampleCovered("flame", 0, 16, 0, 0));
            Assert.False(PartParityFxRules.SampleCovered("flame", 15, 15, 15, 255));
            Assert.True(PartParityFxRules.SampleCovered("smoke", 0, 0, 0, 128));
            Assert.False(PartParityFxRules.SampleCovered("smoke", 255, 255, 255, 127));
            Assert.True(PartParityFxRules.IsSaturated(1, 255, 3));
            Assert.False(PartParityFxRules.IsSaturated(254, 254, 254));
            Assert.Equal(255.0, PartParityFxRules.Luminance(255, 255, 255), 9);
            byte r = 10, g = 20, b = 30;
            PartParityFxRules.RotateHueControl(ref r, ref g, ref b);
            Assert.Equal(new byte[] { 30, 10, 20 }, new[] { r, g, b });
        }

        [Fact]
        public void ThePlate()
        {
            Assert.True(PartParityFxRules.PlateLit(0.02));
            Assert.False(PartParityFxRules.PlateLit(0.019));
            Assert.Equal(3.0, PartParityFxRules.PlateDistanceM("Spot"));
            Assert.Equal(1.0, PartParityFxRules.PlateDistanceM("Point"));
            Assert.Equal("event:fx-far:smoke:smoke-missing", PartParityFxRules.FormatReason("event", "fx-far", "smoke", "smoke-missing"));
        }

        // ----- controls -----

        private static Func<string, PartParityFxSubjectFacts> Install(Dictionary<string, PartParityFxSubjectFacts> d)
            => p => d.TryGetValue(p, out var f) ? f : null;

        private static PartParityFxSubjectFacts F(bool waterfall = false, params string[] lights)
            => new PartParityFxSubjectFacts { Present = true, HasWaterfall = waterfall, LightTypes = lights.ToList() };

        [Fact]
        public void TheControlSubject_SkipsWaterfallParts()
        {
            var stock = Install(new Dictionary<string, PartParityFxSubjectFacts>
                { ["liquidEngine2.v2"] = F(), ["liquidEngine"] = F(), ["solidBooster.sm.v2"] = F() });
            var modded = Install(new Dictionary<string, PartParityFxSubjectFacts>
                { ["liquidEngine2.v2"] = F(true), ["liquidEngine"] = F(true), ["sepMotor1"] = F(), ["solidBooster.sm.v2"] = F() });
            Assert.Equal("liquidEngine2.v2", PartParityFxRules.ChooseControlSubject("ctl-fx-real-vs-real", false, null, stock).Subject);
            var c = PartParityFxRules.ChooseControlSubject("ctl-fx-plume-off", false, null, modded);
            Assert.Equal("sepMotor1", c.Subject);
            Assert.Null(c.Outcome);
            Assert.Equal("subject=sepMotor1 skipped=liquidEngine2.v2:waterfall,liquidEngine:waterfall", c.Detail);
            // The list in order, every unmeetable subject skipped: the Flea is last.
            var noSep = Install(new Dictionary<string, PartParityFxSubjectFacts>()
                { ["liquidEngine2.v2"] = F(true), ["liquidEngine"] = F(true), ["sepMotor1"] = F(true), ["solidBooster.sm.v2"] = F() });
            Assert.Equal("solidBooster.sm.v2", PartParityFxRules.ChooseControlSubject("ctl-fx-real-vs-real", false, null, noSep).Subject);
            // The air controls run on the solid booster held in the air.
            Assert.Equal("solidBooster.sm.v2", PartParityFxRules.ChooseControlSubject("ctl-fx-real-vs-real-air", true, null, stock).Subject);
            // No eligible subject: a required control is bad.
            c = PartParityFxRules.ChooseControlSubject("ctl-fx-smoke-dropped", false, null, Install(new Dictionary<string, PartParityFxSubjectFacts>()));
            Assert.Null(c.Subject);
            Assert.Equal("bad", c.Outcome);
        }

        [Fact]
        public void APlumeControlsKey_IsItsRunsBurnFull_AndABoosterGoesIntoTheAir()
        {
            var rapier = Enumerate(Part("RAPIER", Mod(0, "MultiModeEngine"),
                Mod(1, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule", "engineID=AirBreathing", "PROPELLANT.name=IntakeAir"),
                Mod(2, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule", "engineID=ClosedCycle", "PROPELLANT.name=LiquidFuel"))).Cases;
            Assert.Same(rapier[0], PartParityFxRules.ControlPlumeKey(rapier, true));
            Assert.Same(rapier[2], PartParityFxRules.ControlPlumeKey(rapier, false));
            // The air controls' Flea breathes no air: its one plume is an orbit case, held in the
            // air. Its own key and option stay orbit ones.
            var flea = Enumerate(Part("solidBooster.sm.v2", Mod(0, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule",
                "throttleLocked=True", "useEngineResponseTime=True", "PROPELLANT.name=SolidFuel"))).Cases;
            Assert.Same(flea[0], PartParityFxRules.ControlPlumeKey(flea, false));
            PartParityCaseKey air = PartParityFxRules.ControlPlumeKey(flea, true);
            Assert.NotSame(flea[0], air);
            Assert.Equal("air", air.Context);
            Assert.Equal("air", air.FxOption.Context);
            Assert.Equal("alone", flea[0].Context);
            Assert.Equal("alone", flea[0].FxOption.Context);
            Assert.Equal(new[] { flea[0].Part, flea[0].Source, flea[0].Skin, flea[0].State },
                new[] { air.Part, air.Source, air.Skin, air.State });
            Assert.Equal(flea[0].FxOption.RecorderKeyIndex, air.FxOption.RecorderKeyIndex);
            Assert.True(air.FxOption.ThrottleLocked && air.FxOption.UseEngineResponseTime);
            // A jet held in an orbit run has no key, and no plume is no key.
            var jet = Enumerate(Part("JetEngine", Mod(0, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule", "PROPELLANT.name=IntakeAir"))).Cases;
            Assert.Null(PartParityFxRules.ControlPlumeKey(jet, false));
            Assert.Null(PartParityFxRules.ControlPlumeKey(new List<PartParityCaseKey>(), true));
            Assert.Null(PartParityFxRules.ControlPlumeKey(null, true));
        }

        [Fact]
        public void ThePlateControls_ReadTheLightType_AndThePointOneIsOptional()
        {
            var facts = new Dictionary<string, PartParityFxSubjectFacts>
            {
                ["spotLight1"] = F(true, "Spot"), ["spotLight2"] = F(false, "Spot"), ["navLight1"] = F(false, "Spot"),
            };
            var c = PartParityFxRules.ChooseControlSubject("ctl-plate-light-off", false, facts.Keys, Install(facts));
            Assert.Equal("spotLight2", c.Subject);
            c = PartParityFxRules.ChooseControlSubject("ctl-plate-light-off-point", false, facts.Keys, Install(facts));
            Assert.Null(c.Subject);
            Assert.Equal("n/a", c.Outcome);
            facts["aaPointLamp"] = F(false, "Point");
            facts["zzPointLamp"] = F(false, "Point");
            c = PartParityFxRules.ChooseControlSubject("ctl-plate-light-off-point", false, facts.Keys, Install(facts));
            Assert.Equal("aaPointLamp", c.Subject);
            // A spot lamp with no Light of the type is refused by name.
            facts["spotLight2"] = F(false, "Point");
            facts["navLight1"] = F(false);
            c = PartParityFxRules.ChooseControlSubject("ctl-plate-light-off", false, facts.Keys, Install(facts));
            Assert.Contains("spotLight2:no-spot-light", c.Detail);
        }

        [Fact]
        public void TheHeatControl_SkipsAPartWhoseModelHasNoHeatClip()
        {
            // Stock's two preferred nose cones name a clip their models do not carry.
            var facts = new Dictionary<string, PartParityFxSubjectFacts>
            {
                ["standardNoseCone"] = F(), ["pointyNoseConeA"] = F(),
                ["noseConeAdapter"] = new PartParityFxSubjectFacts { Present = true, HasHeatClip = true },
            };
            var c = PartParityFxRules.ChooseControlSubject("ctl-heat-events-dropped", false, null, Install(facts));
            Assert.Equal("noseConeAdapter", c.Subject);
            Assert.Equal("subject=noseConeAdapter skipped=standardNoseCone:no-heat-clip,pointyNoseConeA:no-heat-clip", c.Detail);
            // The other controls do not ask for the clip.
            Assert.Equal("solidBooster.sm.v2", PartParityFxRules.ChooseControlSubject("ctl-fx-smoke-dropped", false, null,
                Install(new Dictionary<string, PartParityFxSubjectFacts> { ["solidBooster.sm.v2"] = F() })).Subject);
        }

        private static PartParityFxControlEvidence E(params string[] failing)
        {
            var e = new PartParityFxControlEvidence { RealCorePresent = true, EventsRecorded = 1 };
            foreach (string m in failing) e.FailingMeasures.Add(m);
            return e;
        }

        [Theory]
        [InlineData("ctl-fx-real-vs-real", "", "ok")]
        [InlineData("ctl-fx-real-vs-real", "core-iou", "bad")]
        [InlineData("ctl-fx-real-vs-real-air", "", "ok")]
        [InlineData("ctl-fx-plume-off", "presence", "ok")]
        [InlineData("ctl-fx-plume-off", "", "bad")]
        [InlineData("ctl-fx-plume-off-air", "presence", "ok")]
        [InlineData("ctl-fx-plume-short", "length", "ok")]
        [InlineData("ctl-fx-plume-short", "length,colour", "bad")]
        [InlineData("ctl-fx-plume-short", "length,presence", "bad")]
        [InlineData("ctl-fx-plume-hue", "colour", "ok")]
        [InlineData("ctl-fx-plume-hue", "colour,length", "bad")]
        [InlineData("ctl-fx-plume-shift", "origin,lateral", "ok")]
        [InlineData("ctl-fx-plume-shift", "origin", "bad")]
        [InlineData("ctl-fx-plume-shift", "origin,lateral,colour", "bad")]
        [InlineData("ctl-fx-smoke-dropped", "smoke", "ok")]
        [InlineData("ctl-fx-smoke-dropped", "smoke,presence", "bad")]
        public void TheFxControlJudges_ReadTheFailingMeasures(string id, string failing, string outcome)
        {
            var e = E(failing.Length == 0 ? new string[0] : failing.Split(','));
            Assert.Equal(outcome, PartParityFxRules.JudgeFxControl(id, e, out string detail));
            Assert.Equal(outcome == "ok", detail == null);
        }

        [Fact]
        public void TheFxControlJudges_ReadTheirFacts()
        {
            var e = E("presence");
            e.RealCorePresent = false;
            Assert.Equal("bad", PartParityFxRules.JudgeFxControl("ctl-fx-plume-off", e, out string d));
            Assert.Equal("no real core", d);
            e = E("presence");
            e.EventsRecorded = 0;
            Assert.Equal("bad", PartParityFxRules.JudgeFxControl("ctl-fx-plume-off", e, out d));
            Assert.Equal("ok", PartParityFxRules.JudgeFxControl("ctl-empty", new PartParityFxControlEvidence { AllCoverageZero = true }, out d));
            Assert.Equal("bad", PartParityFxRules.JudgeFxControl("ctl-empty", new PartParityFxControlEvidence(), out d));
            Assert.Equal("ok", PartParityFxRules.JudgeFxControl("ctl-ghost-absent",
                new PartParityFxControlEvidence { GhostCoverageZero = true, RealCoverageNonZero = true }, out d));
            Assert.Equal("bad", PartParityFxRules.JudgeFxControl("ctl-ghost-absent",
                new PartParityFxControlEvidence { GhostCoverageZero = true }, out d));
            Assert.Equal("the real side is not covered", d);
            Assert.Equal("ok", PartParityFxRules.JudgeFxControl("ctl-plate-light-off",
                new PartParityFxControlEvidence { RealPlateLit = true, PlateViewsFail = true }, out d));
            Assert.Equal("bad", PartParityFxRules.JudgeFxControl("ctl-plate-light-off-point",
                new PartParityFxControlEvidence { RealPlateLit = false, PlateViewsFail = true }, out d));
            Assert.Equal("bad", PartParityFxRules.JudgeFxControl("ctl-fx-real-vs-real",
                new PartParityFxControlEvidence { SetupStatus = "plate-not-lit" }, out d));
            Assert.Equal("setup plate-not-lit", d);
            Assert.Equal("bad", PartParityFxRules.JudgeFxControl("ctl-ghost-rot90", E(), out d));
        }

        [Fact]
        public void TheHeatControl_FailsOnColour()
        {
            var colour = new PartParityViewRecord { View = "side-a", Measures = new PartParityViewMeasures { ColorBlobPx = 300 } };
            var shape = new PartParityViewRecord { View = "side-a", Measures = new PartParityViewMeasures { RealOnlyBlobPx = 500 } };
            Assert.Equal("ok", PartParityFxRules.JudgeHeatEventsDroppedControl(true, 1, new[] { colour }, "ok", out string d));
            Assert.Equal("bad", PartParityFxRules.JudgeHeatEventsDroppedControl(true, 1, new[] { shape }, "ok", out d));
            Assert.Equal("the event-route views pass on colour", d);
            Assert.Equal("bad", PartParityFxRules.JudgeHeatEventsDroppedControl(false, 1, new[] { colour }, "ok", out d));
            Assert.Equal("bad", PartParityFxRules.JudgeHeatEventsDroppedControl(true, 0, new[] { colour }, "ok", out d));
            Assert.Equal("bad", PartParityFxRules.JudgeHeatEventsDroppedControl(true, 1, new[] { colour }, "state-timeout", out d));
        }
    }
}
