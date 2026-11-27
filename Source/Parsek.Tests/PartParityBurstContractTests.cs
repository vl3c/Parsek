using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the P9 contract (design-part-ghost-parity.md Appendix G, piece X1): the
    /// burst and dust families, words, states, the <c>ground</c> context and its slots, the
    /// enumeration over the committed part lists and its count pins, the <c>fx=</c> grammar
    /// and its refusals, the control sets, subjects and judges, the G.3 lines, the setup
    /// statuses, the sidecar key tables, and the no-change cells: with every fx= value a P8
    /// run could name, each plan, control set and manifest setting is what it was.
    /// </summary>
    public class PartParityBurstContractTests
    {
        // ----- builders -----

        private static Dictionary<string, string> Args(params string[] kv)
        {
            var d = new Dictionary<string, string> { { "label", "pp11" } };
            for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        private static PartParitySweepArgs Grammar(params string[] kv)
        {
            Assert.True(TestCommandPartParitySweep.TryParseGrammar(Args(kv), out var a, out string reason, out string detail),
                "rejected " + reason + " " + detail);
            return a;
        }

        private static string GrammarReject(params string[] kv)
        {
            Assert.False(TestCommandPartParitySweep.TryParseGrammar(Args(kv), out _, out string reason, out _));
            return reason;
        }

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

        private static PartParityBurstPart BurstPart(string name, double potential = 0.5, string[] groups = null,
            params PartParityStateModule[] mods)
        {
            var p = new PartParityStatePart { Name = name };
            p.Modules.AddRange(mods);
            var b = new PartParityBurstPart { Part = p, BaseKey = new PartParityCaseKey { Part = name, Source = "Squad" },
                ExplosionPotential = potential };
            if (groups != null) b.FxGroups.AddRange(groups);
            return b;
        }

        private const string DecoupleChain = "ModuleDecouple,ModuleDecouplerBase,PartModule";

        // ----- the fixture lists (the G.5 counts) -----

        internal static List<PartParityBurstPart> LoadFixture(string profile)
        {
            string path = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek.Tests",
                "Fixtures", "PartParity", "burst-parts-" + profile + ".txt");
            Assert.True(File.Exists(path), "fixture missing: " + path);
            var parts = new List<PartParityBurstPart>();
            PartParityBurstPart cur = null;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                if (f[0] == "P")
                {
                    cur = new PartParityBurstPart
                    {
                        Part = new PartParityStatePart { Name = f[1] },
                        BaseKey = new PartParityCaseKey { Part = f[1] },
                        ExplosionPotential = f[2] == "-" ? PartParityFxRules.DefaultExplosionPotential
                            : double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture),
                    };
                    if (f[3] != "-") cur.FxGroups.AddRange(f[3].Split(','));
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
                cur.Part.Modules.Add(m);
            }
            return parts;
        }

        private static int Count(PartParityFxEnumeration r, string family, string state = null)
            => r.Cases.Count(k => k.FxOption.Family == family && (state == null || k.FxOption.State == state));

        [Theory]
        // profile, parts, potential 0 / 0.1 / 0.5 (default), decouplers with the FX group, without,
        // ModuleSurfaceFX modules, their parts
        [InlineData("stock-minimal", 478, 16, 46, 416, 30, 2, 39, 36)]
        [InlineData("modded-restock", 602, 23, 46, 533, 50, 2, 53, 49)]
        public void TheFixtures_PinTheG5Counts(string profile, int parts, int zero, int tenth, int half,
            int decouplers, int noGroup, int surfaceFx, int surfaceFxParts)
        {
            List<PartParityBurstPart> install = LoadFixture(profile);
            Assert.Equal(parts, install.Count);
            // The power buckets: three on both installs (0, 0.1 and stock's 0.5 default); no part
            // names 1.0 (G.5 assumed a fourth bucket at 1.0).
            Assert.Equal(new[] { 0.0, 0.1, 0.5 }, install.Select(p => p.ExplosionPotential).Distinct().OrderBy(v => v));
            Assert.Equal(zero, install.Count(p => p.ExplosionPotential == 0.0));
            Assert.Equal(tenth, install.Count(p => p.ExplosionPotential == 0.1));
            Assert.Equal(half, install.Count(p => p.ExplosionPotential == 0.5));
            Assert.Equal(surfaceFx, install.Sum(p => p.Part.Modules.Count(m => m.Name == "ModuleSurfaceFX")));
            Assert.Equal(surfaceFxParts, install.Count(p => p.Part.Modules.Any(m => m.Name == "ModuleSurfaceFX")));

            PartParityFxEnumeration r = PartParityFxRules.EnumerateBurstDust(install);
            Assert.Equal(3, Count(r, "explode"));
            Assert.Equal(new[] { 0.0, 0.1, 0.5 }, r.Cases.Where(k => k.FxOption.Family == "explode").Select(k => k.FxOption.ExplosionPotential));
            Assert.Equal(4, Count(r, "vessel-explode"));
            Assert.Equal(5, Count(r, "burst-custom", "custom-l1") + Count(r, "burst-custom", "custom-l5")
                + Count(r, "burst-custom", "custom-l10") + Count(r, "burst-custom", "custom-l15") + Count(r, "burst-custom", "custom-l20"));
            Assert.Equal(3, Count(r, "burst-custom", "puff"));
            Assert.Equal(decouplers, Count(r, "decouple", "decouple"));
            Assert.Equal(2, Count(r, "decouple", "joint-break"));
            Assert.Equal(noGroup, r.Exclusions.Count(e => e.Reason == "decouple-no-fx-group"));
            Assert.Equal(3 * surfaceFx, Count(r, "dust"));
            foreach (string slot in PartParityFxRules.DustSlotStates)
                Assert.Equal(surfaceFx, r.Cases.Count(k => k.FxOption.Family == "dust" && k.FxOption.State == slot));
            Assert.Equal(0, r.Exclusions.Count(e => e.Reason == "dust-no-thrust-provider" || e.Reason == "burst-subject-absent"));
            Assert.All(r.Cases.Where(k => PartParityFxRules.IsBurstFamily(k.FxOption.Family)), k => Assert.Equal("air", k.Context));
            Assert.All(r.Cases.Where(k => k.FxOption.Family == "dust"), k => Assert.Equal("ground", k.Context));
            Assert.Equal(r.Cases.Count, r.Cases.Select(TestCommandPartParitySweep.CaseId).Distinct().Count());
            Assert.All(r.Cases, k => Assert.True(TestCommandCaptureScreenshot.IsValidLabel(TestCommandPartParitySweep.CaseId(k))));
            string line = PartParityFxRules.FormatCounts("explode,vessel-explode,burst-custom,decouple", "air", r.Cases, 0, r.Exclusions);
            Assert.StartsWith("fx cases: fx=explode,vessel-explode,burst-custom,decouple contexts=air install=" + r.Cases.Count
                + " (explode=3 vessel-explode=4 burst-custom=8 decouple=" + (decouplers + 2) + " dust=" + 3 * surfaceFx
                + ") planned=0 excluded=" + noGroup + " (decouple-no-fx-group=" + noGroup + ")", line);
        }

        [Fact]
        public void TheStockFixture_PicksTheseSubjects()
        {
            PartParityFxEnumeration r = PartParityFxRules.EnumerateBurstDust(LoadFixture("stock-minimal"));
            // The ordinal-first part of each potential.
            Assert.Equal(new[] { "MpoProbe", "AdvancedCanard", "CargoStorageUnit" },
                r.Cases.Where(k => k.FxOption.Family == "explode").Select(k => k.Part));
            Assert.Equal(new[] { "MpoProbe", "AdvancedCanard", "CargoStorageUnit" },
                r.Cases.Where(k => k.FxOption.State == "puff").Select(k => k.Part));
            Assert.All(r.Cases.Where(k => k.FxOption.Family == "vessel-explode"), k => Assert.Equal("fuelTank", k.Part));
            Assert.Equal(new[] { 1, 2, 3, 6 }, r.Cases.Where(k => k.FxOption.Family == "vessel-explode").Select(k => k.FxOption.StackSize));
            Assert.Equal(new[] { "fuelTank", "trussPiece1x" }, r.Cases.Where(k => k.FxOption.State == "joint-break").Select(k => k.Part));
            Assert.Equal(new[] { "HeatShield0", "HeatShield1" },
                r.Exclusions.Where(e => e.Reason == "decouple-no-fx-group").Select(e => e.Part).OrderBy(s => s, StringComparer.Ordinal));
            // The LV-T45's dust: ModuleSurfaceFX at 7, its engine at 2 (the part's only engine, key 0).
            var lvt45 = r.Cases.Where(k => k.Part == "liquidEngine2.v2" && k.FxOption.Family == "dust").ToList();
            Assert.Equal(new[] { "dust-low", "dust-high", "dust-water" }, lvt45.Select(k => k.FxOption.State));
            Assert.All(lvt45, k =>
            {
                Assert.Equal(7, k.FxOption.ModuleIndex);
                Assert.Equal(2, k.FxOption.ThrustProviderModuleIndex);
                Assert.Equal(0, k.FxOption.RecorderKeyIndex);
                Assert.Equal(30.0, k.FxOption.SurfaceFxMaxDistanceM);
                Assert.Equal(1.7, k.FxOption.SurfaceFxFalloff);
                Assert.Equal(0.5, k.FxOption.SurfaceFxFxMax);
                Assert.Equal("burn-full", PartParityFxRules.StateBurnFull);
                Assert.Equal(1.0, k.FxOption.Throttle);
            });
            Assert.Equal(new[] { 6.0, 21.0, 12.0 }, lvt45.Select(k => k.FxOption.SlotHeightM).Select(h => Math.Round(h, 9)));
            Assert.Equal(new[] { "terrain", "terrain", "water" }, lvt45.Select(k => k.FxOption.Surface));
            Assert.StartsWith("liquidengine2.v2--default--dust-low--ground--", TestCommandPartParitySweep.CaseId(lvt45[0]));
            // Two ModuleSurfaceFX on one part take the same-word tiebreak by module index.
            Assert.Equal(new[] { "dust-low-m10", "dust-low-m11" },
                r.Cases.Where(k => k.Part == "turboJet" && k.FxOption.State == "dust-low").Select(k => k.State));
            // The turbojet's second module drives its second engine (key 1), an air-breathing spool.
            var jet = r.Cases.Single(k => k.Part == "turboJet" && k.State == "dust-low-m11");
            Assert.Equal(2, jet.FxOption.ThrustProviderModuleIndex);
            Assert.True(jet.FxOption.AirBreathing);
            // The custom stand-in is no part, at Parsek's power for its length.
            var custom = r.Cases.Where(k => k.FxOption.StandIn == "custom").ToList();
            Assert.All(custom, k => Assert.Equal("parsek.customExplosion", k.Part));
            Assert.Equal(new[] { 0.05, 0.25, 0.5, 0.75, 1.0 }, custom.Select(k => k.FxOption.ExplosionPotential));
            Assert.StartsWith("parsek.customexplosion--default--custom-l20--air--", TestCommandPartParitySweep.CaseId(custom[4]));
            // Windows: 2 s for a decouple case, 4 s for every other burst.
            Assert.All(r.Cases.Where(k => k.FxOption.Family == "decouple"), k => Assert.Equal(2.0, k.FxOption.BurstWindowSeconds));
            Assert.All(r.Cases.Where(k => k.FxOption.Family == "explode"), k => Assert.Equal(4.0, k.FxOption.BurstWindowSeconds));
        }

        [Fact]
        public void TheEnumeration_ReadsTheRecordersPredicates()
        {
            var decoupler = BurstPart("Decoupler.1", 0.5, new[] { "decouple" }, Mod(0, "ModuleDecouple", DecoupleChain));
            var renamed = BurstPart("odd", 0.5, new[] { "boom" },
                Mod(0, "ModuleDecouple", DecoupleChain, "fxGroupName=boom"));
            var noGroup = BurstPart("HeatShield0", 0.5, new[] { "running" }, Mod(2, "ModuleDecouple", DecoupleChain));
            var subclass = BurstPart("radial", 0.5, new[] { "decouple" },
                Mod(0, "ModuleAnchoredDecoupler", "ModuleAnchoredDecoupler,ModuleDecouplerBase,PartModule"));
            var noEngine = BurstPart("cosmetic", 0.5, null, Mod(0, "ModuleSurfaceFX", null, "thrustProviderModuleIndex=3"));
            var defaults = BurstPart("plainEngine", 0.5, null, Mod(0, "ModuleEngines"), Mod(1, "ModuleSurfaceFX"));
            var r = PartParityFxRules.EnumerateBurstDust(new[] { decoupler, renamed, noGroup, subclass, noEngine, defaults });
            Assert.Equal(new[] { "Decoupler.1", "odd", "radial" },
                r.Cases.Where(k => k.FxOption.State == "decouple").Select(k => k.Part));
            Assert.Equal("boom", r.Cases.Single(k => k.Part == "odd").FxOption.FxGroup);
            Assert.Contains(r.Exclusions, e => e.Part == "HeatShield0" && e.Reason == "decouple-no-fx-group" && e.ModuleIndex == 2);
            Assert.Contains(r.Exclusions, e => e.Part == "cosmetic" && e.Reason == "dust-no-thrust-provider");
            // A ModuleSurfaceFX naming no provider reads stock's -1: no engine, so it is left out too.
            Assert.Contains(r.Exclusions, e => e.Part == "plainEngine" && e.Reason == "dust-no-thrust-provider");
            // The fixed subjects this install lacks are left out by name.
            Assert.Contains(r.Exclusions, e => e.Part == "fuelTank" && e.Reason == "burst-subject-absent" && e.Family == "vessel-explode");
            Assert.Contains(r.Exclusions, e => e.Part == "trussPiece1x" && e.Reason == "burst-subject-absent");
            // Stock's ModuleSurfaceFX defaults where a config names none.
            var withProvider = BurstPart("e", 0.5, null, Mod(0, "ModuleEngines"), Mod(1, "ModuleSurfaceFX", null, "thrustProviderModuleIndex=0"));
            var d = PartParityFxRules.EnumerateBurstDust(new[] { withProvider }).Cases.Where(k => k.FxOption.Family == "dust").ToList();
            Assert.Equal(3, d.Count);
            Assert.All(d, k =>
            {
                Assert.Equal(50.0, k.FxOption.SurfaceFxMaxDistanceM);
                Assert.Equal(2.0, k.FxOption.SurfaceFxFalloff);
                Assert.Equal(1.0, k.FxOption.SurfaceFxFxMax);
            });
        }

        // ----- the fx= grammar -----

        [Fact]
        public void BurstAndDustWords_ParseWithTheirOwnContext()
        {
            var a = Grammar("fx", "decouple,explode,burst-custom,vessel-explode", "contexts", "air");
            Assert.Equal(new[] { "explode", "vessel-explode", "burst-custom", "decouple" }, a.Fx);
            Assert.True(TestCommandPartParitySweep.IsBurstRun(a));
            Assert.False(TestCommandPartParitySweep.IsDustRun(a));
            Assert.True(TestCommandPartParitySweep.IsAirRun(a) && TestCommandPartParitySweep.IsPadHostRun(a));
            Assert.Equal("explode,vessel-explode,burst-custom,decouple", TestCommandPartParitySweep.FormatFx(a));
            a = Grammar("fx", "dust", "contexts", "ground");
            Assert.True(TestCommandPartParitySweep.IsDustRun(a) && TestCommandPartParitySweep.IsGroundRun(a));
            Assert.False(TestCommandPartParitySweep.IsAirRun(a));
            Assert.True(TestCommandPartParitySweep.IsPadHostRun(a));
            Assert.True(TestCommandPartParitySweep.IsBurstDustRun(a));
            // fx=all stays the four P8 words.
            Assert.Equal(PartParityFxRules.ArgWords, Grammar("fx", "all").Fx);
            Assert.Equal(new[] { "plume", "rcs", "heat", "plate" }, PartParityFxRules.ArgWords);
        }

        [Theory]
        [InlineData("fx", "explode")]                                     // a burst run is an air run
        [InlineData("fx", "explode", "contexts", "alone")]
        [InlineData("fx", "explode", "contexts", "ground")]
        [InlineData("fx", "dust")]                                        // a dust run is a ground run
        [InlineData("fx", "dust", "contexts", "air")]
        [InlineData("fx", "explode,dust", "contexts", "air")]              // one kind per run
        [InlineData("fx", "plume,explode", "contexts", "air")]
        [InlineData("fx", "plume,dust", "contexts", "ground")]
        [InlineData("fx", "plume", "contexts", "ground")]                  // ground is the dust run's
        [InlineData("fx", "all", "contexts", "ground")]
        [InlineData("fx", "explode", "contexts", "air", "states", "lit")]
        [InlineData("fx", "Explode", "contexts", "air")]
        [InlineData("fx", "explosion", "contexts", "air")]
        public void BadCombinations_AreRefusedFxArgInvalid(params string[] kv)
            => Assert.Equal("fx-arg-invalid", GrammarReject(kv));

        [Theory]
        [InlineData("contexts", "ground")]
        [InlineData("contexts", "ground", "fx", "none")]
        [InlineData("contexts", "ground", "states", "all")]
        [InlineData("contexts", "ground,air")]
        [InlineData("contexts", "ground,alone")]
        public void TheGroundContext_StandsAloneAndOnlyBesideFxDust(params string[] kv)
            => Assert.Equal("contexts-arg-invalid", GrammarReject(kv));

        [Fact]
        public void AWellFormedBurstOrDustRun_IsRefusedUntilTheDriverLands()
        {
            Assert.False(TestCommandPartParitySweep.BurstDustRunsBuilt);
            Assert.False(TestCommandPartParitySweep.TryParseArgs(Args("fx", "explode", "contexts", "air"), out var parsed,
                out string reason, out string detail));
            Assert.Null(parsed);
            Assert.Equal("burst-dust-runs-not-built", reason);
            Assert.Equal("fx=explode", detail);
            Assert.False(TestCommandPartParitySweep.TryParseArgs(Args("fx", "dust", "contexts", "ground"), out _, out reason, out detail));
            Assert.Equal("burst-dust-runs-not-built", reason);
            Assert.Equal("fx=dust", detail);
            // A malformed one keeps its own reason; every P8 run parses as before.
            Assert.False(TestCommandPartParitySweep.TryParseArgs(Args("fx", "dust"), out _, out reason, out _));
            Assert.Equal("fx-arg-invalid", reason);
            foreach (var kv in new[] { new[] { "fx", "all" }, new[] { "fx", "plume", "contexts", "air" }, new[] { "fx", "none", "states", "all" } })
            {
                Assert.True(TestCommandPartParitySweep.TryParseArgs(Args(kv), out parsed, out reason, out _), string.Join(" ", kv));
                Assert.Null(reason);
            }
            // The refusal follows the last grammar token, before the admission.
            var reasons = TestCommandPartParitySweep.Reasons.ToList();
            Assert.Equal(reasons.IndexOf("maxseconds-arg-invalid") + 1, reasons.IndexOf("burst-dust-runs-not-built"));
            Assert.Equal(reasons.IndexOf("burst-dust-runs-not-built") + 1, reasons.IndexOf("no-active-vessel"));
        }

        [Fact]
        public void ABurstOrDustRun_IsAdmittedAtThePad()
        {
            foreach (var a in new[] { Grammar("fx", "explode", "contexts", "air"), Grammar("fx", "dust", "contexts", "ground") })
            {
                Assert.Equal("host-not-at-pad", TestCommandPartParitySweep.DecideHostAdmission(a, true, true, false, double.PositiveInfinity));
                Assert.Null(TestCommandPartParitySweep.DecideHostAdmission(a, true, false, true, 10.0));
            }
            // An orbit run still needs an orbiting host.
            Assert.Equal("host-not-orbiting", TestCommandPartParitySweep.DecideHostAdmission(Grammar("fx", "plume"), true, false, true, 10.0));
        }

        // ----- control sets and settings -----

        [Fact]
        public void TheControlSets()
        {
            var burst = Grammar("fx", "explode", "contexts", "air");
            Assert.Equal(new[]
            {
                "ctl-burst-empty", "ctl-burst-real-vs-real", "ctl-burst-off", "ctl-burst-late", "ctl-burst-power",
                "ctl-burst-shift", "ctl-burst-hue",
            }, TestCommandPartParitySweep.ControlSetFor(burst));
            var dust = Grammar("fx", "dust", "contexts", "ground");
            Assert.Equal(new[] { "ctl-dust-real-vs-real", "ctl-dust-off", "ctl-dust-above-reach" },
                TestCommandPartParitySweep.ControlSetFor(dust));
            var settings = TestCommandPartParitySweep.StateSettings(dust);
            Assert.Equal(new[] { "states", "contexts", "controlSet", "fx" }, settings.Select(p => p.Key));
            Assert.Equal(new[] { "none", "ground", "ctl-dust-real-vs-real,ctl-dust-off,ctl-dust-above-reach", "dust" },
                settings.Select(p => p.Value));
            Assert.Equal(TestCommandPartParitySweep.AllControlIds.Length, TestCommandPartParitySweep.AllControlIds.Distinct().Count());
            foreach (string id in TestCommandPartParitySweep.BurstControlIds.Concat(TestCommandPartParitySweep.DustControlIds))
            {
                Assert.Contains(id, TestCommandPartParitySweep.AllControlIds);
                Assert.True(PartParitySweepPlan.IsFxOnlyControl(id));
                Assert.NotNull(PartParitySweepPlan.ControlSubject(id, false, true));
                Assert.NotNull(PartParitySweepPlan.ControlRequired(id));
                Assert.NotNull(PartParityFxRules.BuiltControlPreference(id));
            }
            Assert.Equal(new[] { "ctl-dust-above-reach" }, TestCommandPartParitySweep.KnownDifferenceControlIds);
            Assert.Equal(new[] { "ctl-plate-light-off-point" }, TestCommandPartParitySweep.OptionalControlIds);
        }

        [Fact]
        public void TheControlSubjects()
        {
            var facts = new Dictionary<string, PartParityFxSubjectFacts>
            {
                { "fuelTank", new PartParityFxSubjectFacts { Present = true } },
                { "liquidEngine2.v2", new PartParityFxSubjectFacts { Present = true, HasSurfaceFx = true } },
            };
            Func<string, PartParityFxSubjectFacts> f = p => facts.TryGetValue(p, out var x) ? x : new PartParityFxSubjectFacts();
            Assert.Equal("fuelTank", PartParityFxRules.ChooseControlSubject("ctl-burst-power", true, null, f).Subject);
            Assert.Equal("liquidEngine2.v2", PartParityFxRules.ChooseControlSubject("ctl-dust-off", false, null, f).Subject);
            // A dust subject must carry a ModuleSurfaceFX; a missing one is bad (only the point light is optional).
            facts["liquidEngine2.v2"].HasSurfaceFx = false;
            var c = PartParityFxRules.ChooseControlSubject("ctl-dust-real-vs-real", false, null, f);
            Assert.Null(c.Subject);
            Assert.Equal("bad", c.Outcome);
            Assert.Contains("liquidEngine2.v2:no-surface-fx", c.Detail);
            facts.Remove("fuelTank");
            facts["fuelTankSmallFlat"] = new PartParityFxSubjectFacts { Present = true };
            Assert.Equal("fuelTankSmallFlat", PartParityFxRules.ChooseControlSubject("ctl-burst-empty", true, null, f).Subject);
            // The P8 subjects are unchanged.
            Assert.Equal(PartParityFxRules.SmokeControlParts, PartParityFxRules.ControlPreference("ctl-empty", true));
            Assert.Equal(PartParityFxRules.PlumeControlParts, PartParityFxRules.ControlPreference("ctl-empty", false));
        }

        private static PartParityBurstControlEvidence Burst(params string[] failing)
        {
            var e = new PartParityBurstControlEvidence { RealBlasts = 1, GhostBlasts = 1 };
            foreach (string m in failing) e.FailingMeasures.Add(m);
            return e;
        }

        [Fact]
        public void TheBurstControlJudges()
        {
            string J(string id, PartParityBurstControlEvidence e) => PartParityFxRules.JudgeBurstControl(id, e, out _);
            Assert.Equal("ok", J("ctl-burst-empty", new PartParityBurstControlEvidence { AllCoverageZero = true }));
            Assert.Equal("bad", J("ctl-burst-empty", new PartParityBurstControlEvidence()));
            Assert.Equal("ok", J("ctl-burst-real-vs-real", Burst()));
            Assert.Equal("bad", J("ctl-burst-real-vs-real", Burst("radius")));
            var off = Burst("presence");
            off.GhostBlasts = 0;
            Assert.Equal("ok", J("ctl-burst-off", off));
            Assert.Equal("bad", J("ctl-burst-off", Burst("presence")));
            Assert.Equal("ok", J("ctl-burst-late", Burst("onset", "peak-time")));
            Assert.Equal("bad", J("ctl-burst-late", Burst("onset", "colour")));
            var power = Burst("schedule", "radius");
            power.ScheduleIndexDiffers = true;
            Assert.Equal("ok", J("ctl-burst-power", power));
            Assert.Equal("bad", J("ctl-burst-power", Burst("schedule", "radius")));
            Assert.Equal("ok", J("ctl-burst-shift", Burst("centroid-offset", "schedule")));
            Assert.Equal("bad", J("ctl-burst-shift", Burst("centroid-offset", "peak-area")));
            Assert.Equal("ok", J("ctl-burst-hue", Burst("colour")));
            Assert.Equal("bad", J("ctl-burst-hue", Burst("colour", "brightness")));
            var fault = Burst("colour");
            fault.SetupStatus = "burst-not-triggered";
            Assert.Equal("bad", J("ctl-burst-hue", fault));
            Assert.Equal("bad", J("ctl-fx-plume-hue", Burst("colour")));
        }

        [Fact]
        public void TheDustControlJudges()
        {
            string J(string id, PartParityDustControlEvidence e) => PartParityFxRules.JudgeDustControl(id, e, out _);
            Assert.Equal("ok", J("ctl-dust-real-vs-real", new PartParityDustControlEvidence()));
            var off = new PartParityDustControlEvidence();
            off.FailingDustMeasures.Add("presence");
            Assert.Equal("ok", J("ctl-dust-off", off));
            off.FailingFlameMeasures.Add("length");
            Assert.Equal("bad", J("ctl-dust-off", off));
            // Above the reach: stock's miss is the point; the ghost's dust is n/a while G.8 item 3 stands.
            var above = new PartParityDustControlEvidence { SetupStatus = "dust-no-stock-hit" };
            Assert.Equal("ok", J("ctl-dust-above-reach", above));
            above.GhostDustCovered = true;
            Assert.False(PartParityFxRules.DustAboveReachGhostJudged);
            Assert.Equal("n/a", J("ctl-dust-above-reach", above));
            above.StockRaycastHit = true;
            Assert.Equal("bad", J("ctl-dust-above-reach", above));
            var fault = new PartParityDustControlEvidence { SetupStatus = "dust-no-stock-hit" };
            Assert.Equal("bad", J("ctl-dust-real-vs-real", fault));
        }

        // ----- statuses, windows, bins, helpers -----

        [Fact]
        public void TheSetupStatuses_AreFaults()
        {
            foreach (string s in new[] { "burst-not-triggered", "dust-no-stock-hit" })
            {
                Assert.Contains(s, TestCommandPartParitySweep.SetupStatuses);
                Assert.True(TestCommandPartParitySweep.IsSetupFault(s));
            }
            Assert.Equal("rcs-input-mismatch", TestCommandPartParitySweep.SetupStatuses[10]);
        }

        [Fact]
        public void TheWindowAndBins()
        {
            Assert.Equal(16, PartParityFxRules.BurstBinCount(PartParityFxRules.BurstWindowFor("explode")));
            Assert.Equal(8, PartParityFxRules.BurstBinCount(PartParityFxRules.BurstWindowFor("decouple")));
            Assert.Equal(0, PartParityFxRules.BurstBinIndex(0.0, 4.0));
            Assert.Equal(0, PartParityFxRules.BurstBinIndex(0.2499, 4.0));
            Assert.Equal(1, PartParityFxRules.BurstBinIndex(0.25, 4.0));
            Assert.Equal(15, PartParityFxRules.BurstBinIndex(3.99, 4.0));
            Assert.Equal(-1, PartParityFxRules.BurstBinIndex(4.0, 4.0));
            Assert.Equal(-1, PartParityFxRules.BurstBinIndex(-0.01, 4.0));
            Assert.Equal(-1, PartParityFxRules.BurstBinIndex(double.NaN, 4.0));
            Assert.Equal(7, PartParityFxRules.BurstBinIndex(1.9, 2.0));
            Assert.Equal(new[] { true, false, true, false }, Enumerable.Range(0, 4).Select(PartParityFxRules.IsEvenTrial));
            // Trials x frames per bin must keep a tile's alpha exact (N <= 255): four trials at
            // 60 fps fill a 0.25 s bin with 60 samples.
            Assert.True(PartParityFxRules.BurstTrials * 60 * PartParityFxRules.BurstBinSeconds <= PartParityFxRules.MaxSamples);
        }

        [Fact]
        public void ThePowerAndScaleHelpers()
        {
            Assert.Equal(GhostAudioPresets.VesselLengthPowerDivisor, PartParityFxRules.TerminalExplosionLengthDivisorM, 9);
            Assert.Equal(0.25, PartParityFxRules.TerminalExplosionPower(5.0), 9);
            Assert.Equal(1.0, PartParityFxRules.TerminalExplosionPower(45.0), 9);
            Assert.Equal(0.0, PartParityFxRules.TerminalExplosionPower(-1.0), 9);
            Assert.Equal(0, PartParityFxRules.FxMongerPrefabIndex(0.0, 4));
            Assert.Equal(1, PartParityFxRules.FxMongerPrefabIndex(0.5, 4));
            Assert.Equal(3, PartParityFxRules.FxMongerPrefabIndex(1.0, 4));
            Assert.Equal(3, PartParityFxRules.FxMongerPrefabIndex(1.5, 4));
            Assert.Equal(-1, PartParityFxRules.FxMongerPrefabIndex(0.5, 0));
            // ScaledFX = thrust fraction * fxMax * (1 - d / maxDistance)^falloff (the LV-T45 at its low slot).
            Assert.Equal(0.5 * Math.Pow(0.8, 1.7), PartParityFxRules.StockSurfaceFxScale(1.0, 0.5, 6.0, 30.0, 1.7), 12);
            Assert.Equal(0.0, PartParityFxRules.StockSurfaceFxScale(1.0, 0.5, 30.0, 30.0, 1.7));
            Assert.Equal(0.0, PartParityFxRules.StockSurfaceFxScale(1.0, 0.5, 36.0, 30.0, 1.7));
            Assert.Equal(0.0, PartParityFxRules.StockSurfaceFxScale(double.NaN, 0.5, 6.0, 30.0, 1.7));
            Assert.Equal(36.0, PartParityFxRules.DustSlotHeightM(PartParityFxRules.DustAboveReachFraction, 30.0), 9);
            Assert.Equal("decouple", PartParityFxRules.FxGroupOfValue("0.0, 0.0, 0.0, 0.0, 1.0, 0.0, decouple"));
            Assert.Null(PartParityFxRules.FxGroupOfValue("0.0, "));
            Assert.Equal("dust-extra:launchpad", PartParityFxRules.DustExtraReason("launchpad"));
            Assert.Equal("dust-extra:none", PartParityFxRules.DustExtraReason(null));
            Assert.Equal("vessel-explode-x6", PartParityFxRules.VesselExplodeState(6));
            Assert.Equal("custom-l2.5", PartParityFxRules.CustomExplosionState(2.5));
        }

        [Theory]
        // measure, value, level (the G.3 lines)
        [InlineData("onset", 1.0, "ok")]
        [InlineData("onset", -2.0, "fail")]
        [InlineData("peak-time", 0.0, "ok")]
        [InlineData("peak-time", 1.0, "warn")]
        [InlineData("peak-time", -2.0, "warn")]
        [InlineData("peak-time", 3.0, "fail")]
        [InlineData("duration", 0.2, "ok")]
        [InlineData("duration", -0.3, "warn")]
        [InlineData("duration", 0.41, "fail")]
        [InlineData("peak-area", 1.0, "ok")]
        [InlineData("peak-area", 0.7, "warn")]
        [InlineData("peak-area", 1.3, "warn")]
        [InlineData("peak-area", 0.59, "fail")]
        [InlineData("peak-area", 1.61, "fail")]
        [InlineData("radius", 0.86, "ok")]
        [InlineData("radius", 1.2, "warn")]
        [InlineData("radius", 0.69, "fail")]
        [InlineData("centroid-offset", 0.15, "ok")]
        [InlineData("centroid-offset", 0.2, "warn")]
        [InlineData("centroid-offset", -0.31, "fail")]
        [InlineData("brightness", 2.0, "ok")]
        [InlineData("brightness", 2.1, "warn")]
        public void TheG3Lines(string measure, double value, string level)
            => Assert.Equal(level, PartParityFxRules.BurstMeasureLevel(measure, value));

        [Fact]
        public void TheG3Lines_NullWhereUndefined()
        {
            Assert.Null(PartParityFxRules.BurstMeasureLevel("radius", double.NaN));
            Assert.Null(PartParityFxRules.BurstMeasureLevel("schedule", 1.0));
            Assert.Null(PartParityFxRules.BurstColourLevel(double.NaN, double.NaN));
            Assert.Equal("ok", PartParityFxRules.BurstColourLevel(0.01, double.NaN));
            Assert.Equal("warn", PartParityFxRules.BurstColourLevel(0.02, 0.0));
            Assert.Equal("warn", PartParityFxRules.BurstColourLevel(0.0, 0.2));
            Assert.Equal("fail", PartParityFxRules.BurstColourLevel(0.07, 0.0));
            Assert.Equal("fail", PartParityFxRules.BurstColourLevel(double.NaN, 0.6));
        }

        // ----- the sidecar tables -----

        [Fact]
        public void TheSidecarCarriesTheBurstAndDustBlocks_NullOnEveryOtherCase()
        {
            Assert.Equal(new[] { "fx", "fxAtlas", "strip", "burst", "dust" }, PartParityJson.CaseKeys.Skip(PartParityJson.CaseKeys.Length - 5));
            Assert.Equal(PartParityJson.BurstKeys.Length, PartParityJson.BurstKeys.Distinct().Count());
            Assert.Equal(PartParityJson.DustKeys.Length, PartParityJson.DustKeys.Distinct().Count());
            Assert.Equal(new[] { "real-flame", "ghost-flame", "real-smoke", "ghost-smoke" }, PartParityJson.BurstAtlasRows);
            Assert.Equal(".burst.png", TestCommandPartParitySweep.BurstAtlasSuffix);
            PartParityCaseSidecar sidecar = PartParityJsonTests.SmallSidecar();
            object root = PartParityJsonReader.Parse(PartParityJson.WriteCase(sidecar));
            var obj = (Dictionary<string, object>)root;
            Assert.True(obj.ContainsKey("burst") && obj["burst"] == null);
            Assert.True(obj.ContainsKey("dust") && obj["dust"] == null);
            sidecar.Burst = new Dictionary<string, string> { { "family", "\"explode\"" } };
            obj = (Dictionary<string, object>)PartParityJsonReader.Parse(PartParityJson.WriteCase(sidecar));
            var burst = (Dictionary<string, object>)obj["burst"];
            Assert.Equal(PartParityJson.BurstKeys, burst.Keys);
            Assert.Equal("explode", burst["family"]);
        }

        // ----- the no-change cells -----

        private static List<PartParityCaseKey> P8Keys()
        {
            var keys = new List<PartParityCaseKey>();
            foreach (var p in new[]
            {
                new PartParityStatePart { Name = "liquidEngine2.v2", Modules = { Mod(0, "ModuleEngines", null, "PROPELLANT.name=LiquidFuel") } },
                new PartParityStatePart { Name = "JetEngine", Modules = { Mod(0, "ModuleEnginesFX", "ModuleEnginesFX,ModuleEngines,PartModule", "PROPELLANT.name=IntakeAir") } },
                new PartParityStatePart { Name = "RCSBlock", Modules = { Mod(0, "ModuleRCS") } },
                new PartParityStatePart { Name = "noseCone", Modules = { Mod(0, "ModuleAnimateHeat") } },
                new PartParityStatePart { Name = "spotLight1", Modules = { Mod(0, "ModuleLight") } },
            })
                keys.AddRange(PartParityFxRules.Enumerate(p, new PartParityCaseKey { Part = p.Name }).Cases);
            return keys;
        }

        public static IEnumerable<object[]> EveryP8FxValue()
        {
            foreach (string fx in new[] { "none", "all", "plume", "rcs", "heat", "plate", "plume,rcs", "heat,plate", "plume,rcs,heat,plate" })
                foreach (string contexts in new[] { null, "air" })
                    yield return new object[] { fx, contexts };
        }

        [Theory]
        [MemberData(nameof(EveryP8FxValue))]
        public void WithEveryP8FxValue_ThePlanControlSetAndSettingsAreUnchanged(string fx, string contexts)
        {
            var kv = new List<string> { "fx", fx };
            if (contexts != null) { kv.Add("contexts"); kv.Add(contexts); }
            var a = Grammar(kv.ToArray());
            bool fxRun = fx != "none";
            Assert.False(TestCommandPartParitySweep.IsBurstDustRun(a));
            Assert.False(TestCommandPartParitySweep.IsGroundRun(a));
            // The control set and settings of F1 / F7.
            string[] want = !fxRun ? (contexts == null ? TestCommandPartParitySweep.ControlIds : TestCommandPartParitySweep.AirControlIds)
                : (contexts == null ? TestCommandPartParitySweep.FxOrbitControlIds : TestCommandPartParitySweep.FxAirControlIds);
            Assert.Equal(want, TestCommandPartParitySweep.ControlSetFor(a));
            var settings = TestCommandPartParitySweep.StateSettings(a);
            var keys = new List<string> { "states", "contexts", "controlSet" };
            if (fxRun) keys.Add("fx");
            Assert.Equal(keys, settings.Select(p => p.Key));
            Assert.Equal(contexts ?? "none", settings[1].Value);
            Assert.Equal(string.Join(",", want), settings[2].Value);
            // P9 keys beside the P8 ones never move a P8 plan.
            List<PartParityCaseKey> p8 = P8Keys();
            List<PartParityCaseKey> withBuilt = p8.Concat(PartParityFxRules.EnumerateBurstDust(LoadFixture("stock-minimal")).Cases).ToList();
            var plain = TestCommandPartParitySweep.PlanCases(null, null, a, p8);
            var mixed = TestCommandPartParitySweep.PlanCases(null, null, a, withBuilt);
            Assert.Equal(plain.Select(c => c.CaseId + "@" + c.Index), mixed.Select(c => c.CaseId + "@" + c.Index));
            // The P8 counts line is unchanged by the P9 tables (zero counts are left out).
            Assert.DoesNotContain("explode", PartParityFxRules.FormatCounts(fx, contexts, p8, plain.Count, new List<PartParityFxExclusion>()));
        }

        [Fact]
        public void ABurstOrDustPlan_HoldsOnlyItsOwnKeys()
        {
            List<PartParityCaseKey> keys = P8Keys().Concat(PartParityFxRules.EnumerateBurstDust(LoadFixture("stock-minimal")).Cases).ToList();
            var burst = TestCommandPartParitySweep.PlanCases(null, null, Grammar("fx", "explode,decouple", "contexts", "air"), keys);
            Assert.Equal(3 + 30 + 2, burst.Count);
            Assert.All(burst, c => Assert.Contains(c.Key.FxOption.Family, new[] { "explode", "decouple" }));
            var dust = TestCommandPartParitySweep.PlanCases(null, null, Grammar("fx", "dust", "contexts", "ground", "limit", "5"), keys);
            Assert.Equal(5, dust.Count);
            Assert.All(dust, c => Assert.Equal("dust", c.Key.FxOption.Family));
            // A P8 plan over the same keys holds no P9 key.
            var p8 = TestCommandPartParitySweep.PlanCases(null, null, Grammar("fx", "all"), keys);
            Assert.All(p8, c => Assert.False(PartParityFxRules.IsBuiltFamily(c.Key.FxOption.Family)));
            Assert.NotEmpty(p8);
            // The P9 enumeration is its own call: P8's per-part Enumerate writes no P9 case.
            Assert.All(P8Keys(), k => Assert.False(PartParityFxRules.IsBuiltFamily(k.FxOption.Family)));
        }
    }
}
