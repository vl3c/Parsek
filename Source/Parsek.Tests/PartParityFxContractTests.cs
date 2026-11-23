using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the P8 sweep contract (design-part-ghost-parity.md Appendix E.5, piece
    /// F1): the <c>fx=</c> arg, its refusals and the interim <c>fx-runs-not-built</c> gate;
    /// <c>IsFxRun</c>, <c>settings.fx</c>, <c>ControlSetFor</c> and <c>PlanCases</c> for an FX
    /// run; the two no-change cells of E.1 (a <c>states=all</c> plan holds no FX key, and with
    /// <c>fx=none</c> every PP-1 to PP-6 plan, control set and setting is today's); the FX key
    /// tables and the FX atlas rows.
    /// </summary>
    public class PartParityFxContractTests
    {
        private static Dictionary<string, string> Args(params string[] kv)
        {
            var d = new Dictionary<string, string> { { "label", "pp7" } };
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

        // ----- fx= -----

        [Fact]
        public void Fx_ParsesNoneAllOrACanonicalCsv()
        {
            var a = Grammar();
            Assert.Null(a.Fx);
            Assert.False(TestCommandPartParitySweep.IsFxRun(a));
            Assert.Equal("none", TestCommandPartParitySweep.FormatFx(a));
            a = Grammar("fx", "none");
            Assert.False(TestCommandPartParitySweep.IsFxRun(a));
            a = Grammar("fx", "all");
            Assert.True(a.FxAll);
            Assert.Equal(PartParityFxRules.ArgWords, a.Fx);
            Assert.Equal("all", TestCommandPartParitySweep.FormatFx(a));
            a = Grammar("fx", "plate,plume,plume");
            Assert.Equal(new[] { "plume", "plate" }, a.Fx);
            Assert.Equal("plume,plate", TestCommandPartParitySweep.FormatFx(a));
            Assert.True(TestCommandPartParitySweep.IsFxRun(a));
            Assert.False(TestCommandPartParitySweep.IsStateRun(a));
            a = Grammar("fx", "plume", "contexts", "air");
            Assert.True(TestCommandPartParitySweep.IsAirRun(a));
            // fx= with states=none / contexts=none is still an FX run.
            Assert.True(TestCommandPartParitySweep.IsFxRun(Grammar("fx", "rcs", "states", "none", "contexts", "none")));
        }

        [Theory]
        [InlineData("fx", "")]
        [InlineData("fx", "Plume")]
        [InlineData("fx", "plume,")]
        [InlineData("fx", "light")]
        [InlineData("fx", "smoke")]
        public void BadFx_AreRefused(string key, string value)
            => Assert.Equal("fx-arg-invalid", GrammarReject(key, value));

        [Theory]
        [InlineData("states", "all")]
        [InlineData("states", "lit")]
        [InlineData("contexts", "alone")]
        [InlineData("contexts", "stacked")]
        [InlineData("contexts", "fgv")]
        public void Fx_NamesNoStateWordAndNoOrbitContext(string key, string value)
        {
            Assert.Equal("fx-arg-invalid", GrammarReject("fx", "plume", key, value));
            // Without fx= the same arg is a P6 run, unchanged.
            Assert.True(TestCommandPartParitySweep.IsStateRun(Grammar(key, value)));
            // fx=none beside it is no FX run, so nothing is refused.
            Assert.False(TestCommandPartParitySweep.IsFxRun(Grammar("fx", "none", key, value)));
        }

        [Fact]
        public void AnOrbitFxRun_Parses_AndAnAirOneIsRefusedUntilItsSpawnIsBuilt()
        {
            Assert.True(TestCommandPartParitySweep.FxRunsBuilt);
            Assert.False(TestCommandPartParitySweep.FxAirSpawnBuilt);
            Assert.True(TestCommandPartParitySweep.TryParseArgs(Args("fx", "plume,rcs"), out var parsed,
                out string reason, out string detail));
            Assert.True(TestCommandPartParitySweep.IsFxRun(parsed));
            Assert.Null(reason);
            Assert.False(TestCommandPartParitySweep.TryParseArgs(Args("fx", "all", "contexts", "air"), out parsed, out reason, out detail));
            Assert.Null(parsed);
            Assert.Equal("fx-runs-not-built", reason);
            Assert.Equal("fx=all", detail);
            // A bad value keeps its own reason; fx=none parses as before.
            Assert.False(TestCommandPartParitySweep.TryParseArgs(Args("fx", "bogus"), out _, out reason, out _));
            Assert.Equal("fx-arg-invalid", reason);
            Assert.True(TestCommandPartParitySweep.TryParseArgs(Args("fx", "none", "states", "all"), out parsed, out reason, out _));
            Assert.Null(reason);
            Assert.True(TestCommandPartParitySweep.IsStateRun(parsed));
            // The refusals sit in the parse order: fx-arg-invalid after the contexts token,
            // fx-runs-not-built after the last grammar token, before the admission.
            var reasons = TestCommandPartParitySweep.Reasons.ToList();
            Assert.Equal(reasons.IndexOf("contexts-arg-invalid") + 1, reasons.IndexOf("fx-arg-invalid"));
            Assert.Equal(reasons.IndexOf("maxseconds-arg-invalid") + 1, reasons.IndexOf("fx-runs-not-built"));
            Assert.Equal(reasons.IndexOf("fx-runs-not-built") + 1, reasons.IndexOf("no-active-vessel"));
            Assert.Contains("fx", TestCommandPartParitySweep.ArgKeys);
        }

        // ----- settings and control sets -----

        [Fact]
        public void AnFxRun_RecordsFx_AndItsControlSet()
        {
            var a = Grammar("fx", "all");
            var settings = TestCommandPartParitySweep.StateSettings(a);
            Assert.Equal(new[] { "states", "contexts", "controlSet", "fx" }, settings.Select(p => p.Key));
            Assert.Equal("all", settings[3].Value);
            Assert.Equal(string.Join(",", TestCommandPartParitySweep.FxOrbitControlIds), settings[2].Value);
            Assert.Equal(new[]
            {
                "ctl-empty", "ctl-ghost-absent", "ctl-fx-real-vs-real", "ctl-fx-plume-off", "ctl-fx-plume-short",
                "ctl-fx-plume-hue", "ctl-fx-plume-shift", "ctl-fx-smoke-dropped", "ctl-heat-events-dropped",
                "ctl-plate-light-off", "ctl-plate-light-off-point",
            }, TestCommandPartParitySweep.ControlSetFor(a));
            Assert.Equal(new[] { "ctl-empty", "ctl-ghost-absent", "ctl-fx-real-vs-real-air", "ctl-fx-plume-off-air" },
                TestCommandPartParitySweep.ControlSetFor(Grammar("fx", "plume", "contexts", "air")));
            Assert.Equal(11, TestCommandPartParitySweep.FxOrbitControlIds.Length);
            Assert.Equal(TestCommandPartParitySweep.AllControlIds.Length, TestCommandPartParitySweep.AllControlIds.Distinct().Count());
            foreach (string id in TestCommandPartParitySweep.FxOrbitControlIds.Concat(TestCommandPartParitySweep.FxAirControlIds))
            {
                Assert.Contains(id, TestCommandPartParitySweep.AllControlIds);
                Assert.NotNull(PartParitySweepPlan.ControlSubject(id, false, true));
                Assert.NotNull(PartParitySweepPlan.ControlRequired(id));
            }
            // The two coverage controls of an FX run burn; of a stowed run they photograph the tank.
            Assert.Contains("burning", PartParitySweepPlan.ControlSubject("ctl-empty", false, true));
            Assert.Contains("fuelTankSmallFlat", PartParitySweepPlan.ControlSubject("ctl-empty"));
            Assert.Contains("solidBooster.sm.v2", PartParitySweepPlan.ControlSubject("ctl-ghost-absent", true, true));
        }

        [Fact]
        public void TheOptionalPointLightControl_CountsAsMetWhenNotApplicable()
        {
            string[] set = TestCommandPartParitySweep.FxOrbitControlIds;
            var outcomes = Enumerable.Repeat("ok", set.Length - 1).Concat(new[] { "n/a" }).ToList();
            Assert.Equal("ok", TestCommandPartParitySweep.ControlsState(true, outcomes, set));
            outcomes[0] = "bad";
            Assert.Equal("bad", TestCommandPartParitySweep.ControlsState(true, outcomes, set));
            Assert.Equal(new[] { "ctl-plate-light-off-point" }, TestCommandPartParitySweep.OptionalControlIds);
        }

        [Fact]
        public void TheNewSetupStatuses_AreFaults()
        {
            foreach (string s in new[] { "plate-not-lit", "rcs-input-mismatch" })
            {
                Assert.Contains(s, TestCommandPartParitySweep.SetupStatuses);
                Assert.True(TestCommandPartParitySweep.IsSetupFault(s));
            }
            // The press gate's statuses stay P6's.
            Assert.Equal(new[] { "action-failed", "state-timeout" }, PartParityStateRules.PressGateStatuses);
        }

        // ----- PlanCases -----

        private static PartParityCaseKey Stowed(string part) => new PartParityCaseKey { Part = part, Source = "Squad" };

        private static List<PartParityCaseKey> FxKeys()
        {
            var keys = new List<PartParityCaseKey>();
            void Add(PartParityStatePart p) => keys.AddRange(PartParityFxRules.Enumerate(p, Stowed(p.Name)).Cases);
            var eng = new PartParityStatePart { Name = "liquidEngine2.v2" };
            eng.Modules.Add(new PartParityStateModule { Index = 0, Name = "ModuleEngines" });
            Add(eng);
            var jet = new PartParityStatePart { Name = "JetEngine" };
            var jm = new PartParityStateModule { Index = 0, Name = "ModuleEnginesFX", Config = new ConfigNode("MODULE") };
            jm.TypeChain.AddRange(new[] { "ModuleEnginesFX", "ModuleEngines", "PartModule" });
            jm.Config.AddNode("PROPELLANT").AddValue("name", "IntakeAir");
            jet.Modules.Add(jm);
            Add(jet);
            var lamp = new PartParityStatePart { Name = "spotLight1" };
            lamp.Modules.Add(new PartParityStateModule { Index = 0, Name = "ModuleLight" });
            lamp.Modules.Add(new PartParityStateModule { Index = 1, Name = "ModuleAnimateHeat" });
            Add(lamp);
            return keys;
        }

        private static List<PartParityCaseKey> StateKeys()
        {
            var lamp = new PartParityStatePart { Name = "spotLight1" };
            lamp.Modules.Add(new PartParityStateModule { Index = 0, Name = "ModuleLight" });
            return PartParityStateRules.Enumerate(lamp, Stowed("spotLight1")).Cases;
        }

        private static string[] Ids(List<PartParityPlannedCase> plan) => plan.Select(c => c.CaseId).ToArray();

        [Fact]
        public void AnFxRun_PlansOnlyItsFamiliesInItsContext()
        {
            var stowed = new List<PartParityCaseKey> { Stowed("liquidEngine2.v2"), Stowed("spotLight1") };
            var fx = FxKeys();
            var plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("fx", "all"), fx);
            Assert.Equal(new[] { "burn-full/alone", "burn-low/alone", "heat-hot/alone", "heat-medium/alone", "lit/plate" },
                plan.Select(c => c.Key.State + "/" + c.Key.Context).OrderBy(s => s, System.StringComparer.Ordinal));
            Assert.All(plan, c => Assert.NotNull(c.Key.FxOption));
            plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("fx", "plume", "contexts", "air"), fx);
            Assert.Equal(new[] { "JetEngine burn-full/air", "JetEngine burn-low/air" },
                plan.Select(c => c.Key.Part + " " + c.Key.State + "/" + c.Key.Context));
            plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("fx", "plate"), fx);
            Assert.Equal("lit/plate", plan.Single().Key.State + "/" + plan.Single().Key.Context);
            // filter / shard / limit apply as to every run.
            plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("fx", "all", "filter", "spot*", "limit", "1"), fx);
            Assert.Equal("spotLight1", plan.Single().Key.Part);
            // No FX keys handed over: an FX run plans nothing, never the stowed list.
            Assert.Empty(TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("fx", "all")));
        }

        [Fact]
        public void AStatesAllPlan_HoldsNoFxKey()
        {
            var stowed = new List<PartParityCaseKey> { Stowed("liquidEngine2.v2"), Stowed("spotLight1") };
            var fx = FxKeys();
            foreach (string[] kv in new[] { new[] { "states", "all" }, new[] { "states", "all", "contexts", "air" },
                new[] { "states", "all", "fx", "none" }, new string[0] })
            {
                var plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar(kv), fx);
                Assert.All(plan, c => Assert.Null(c.Key.FxOption));
                // Handing the FX keys over changes nothing for a run without fx=.
                Assert.Equal(Ids(TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar(kv))), Ids(plan));
            }
        }

        /// <summary>The run step args of PP-1 to PP-6 (harness/scenarios/PP-*.toml), and what
        /// each recorded before F1: its control set and its manifest settings.</summary>
        public static IEnumerable<object[]> TodaysLanes()
        {
            string stowed = string.Join(",", TestCommandPartParitySweep.ControlIds);
            string orbit = stowed + ",ctl-state-events-dropped";
            const string air = "ctl-empty,ctl-ghost-absent,ctl-real-vs-real,ctl-state-events-dropped-air";
            foreach (string label in new[] { "pp1", "pp2" })
                yield return new object[] { new[] { "label", label, "op", "run", "skins", "all", "controls", "true", "maxSeconds", "480" },
                    "none", "none", stowed };
            foreach (string label in new[] { "pp3", "pp4" })
                yield return new object[] { new[] { "label", label, "op", "run", "states", "all", "controls", "true", "maxSeconds", "480" },
                    "all", "none", orbit };
            foreach (string label in new[] { "pp5", "pp6" })
                yield return new object[] { new[] { "label", label, "op", "run", "contexts", "air", "states", "all", "controls", "true", "maxSeconds", "480" },
                    "all", "air", air };
        }

        [Theory]
        [MemberData(nameof(TodaysLanes))]
        public void WithFxNone_EveryLaneOfP6PlansAndRecordsWhatItDidBefore(string[] kv, string states, string contexts, string controlSet)
        {
            var raw = new Dictionary<string, string>();
            for (int i = 0; i < kv.Length; i += 2) raw[kv[i]] = kv[i + 1];
            foreach (bool withFxNone in new[] { false, true })
            {
                if (withFxNone) raw["fx"] = "none";
                Assert.True(TestCommandPartParitySweep.TryParseArgs(raw, out var a, out string reason, out _), reason);
                Assert.False(TestCommandPartParitySweep.IsFxRun(a));
                Assert.Equal(controlSet, string.Join(",", TestCommandPartParitySweep.ControlSetFor(a)));
                var settings = TestCommandPartParitySweep.StateSettings(a);
                Assert.Equal(new[] { "states=" + states, "contexts=" + contexts, "controlSet=" + controlSet },
                    settings.Select(p => p.Key + "=" + p.Value));
                var stowed = new List<PartParityCaseKey> { Stowed("liquidEngine2.v2"), Stowed("spotLight1") };
                Assert.Equal(Ids(TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), a)),
                    Ids(TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), a, FxKeys())));
            }
        }

        // ----- the FX key tables and rows -----

        [Fact]
        public void TheFxAtlasRows_AreThePureWritersAndCopies()
        {
            Assert.Equal(new[] { "real-flame", "ghost-flame", "ghost-start-flame", "real-smoke", "ghost-smoke", "ghost-start-smoke" },
                PartParityJson.FxAtlasRowsFor(false));
            string[] rcs = PartParityJson.FxAtlasRowsFor(true);
            Assert.Equal(8, rcs.Length);
            Assert.Equal(new[] { "ghost-all-flame", "ghost-all-smoke" }, rcs.Skip(6));
            rcs[0] = "mutated";
            Assert.Equal("real-flame", PartParityJson.RcsFxAtlasRows[0]);
            Assert.Equal(".fx.png", TestCommandPartParitySweep.FxAtlasSuffix);
            Assert.Equal("abc.strip.png", TestCommandPartParitySweep.StripFileName("abc"));
        }

        [Fact]
        public void TheCaseSidecar_WritesTheFxBlocks_NullForANonFxCase()
        {
            string doc = PartParityJson.WriteCase(new PartParityCaseSidecar { CaseId = "x" });
            Assert.Contains("\"fx\": null", doc);
            Assert.Contains("\"fxAtlas\": null", doc);
            Assert.Contains("\"strip\": null", doc);
            Assert.Equal(new[] { "fx", "fxAtlas", "strip" }, PartParityJson.CaseKeys.Skip(PartParityJson.CaseKeys.Length - 3));
        }

        [Fact]
        public void KeyedBlock_WritesEveryKeyInOrder()
        {
            var frag = new Dictionary<string, string> { ["frames"] = "12", ["file"] = PartParityJson.Str("a.strip.png"), ["extra"] = "1" };
            Assert.Equal("{\"file\":\"a.strip.png\",\"frames\":12}", PartParityJson.KeyedBlock(PartParityJson.FxStripKeys, frag));
            Assert.Equal(new[] { "extra" }, PartParityJson.UnknownKeys(PartParityJson.FxStripKeys, frag));
            Assert.Equal("null", PartParityJson.KeyedBlock(PartParityJson.FxKeys, null));
            string fx = PartParityJson.KeyedBlock(PartParityJson.FxKeys, new Dictionary<string, string> { ["family"] = "\"plume\"" });
            foreach (string k in PartParityJson.FxKeys) Assert.Contains("\"" + k + "\":", fx);
            Assert.Contains("\"reference\":null", fx);
        }

        [Fact]
        public void TheFxKeyTables_HoldNoDuplicate()
        {
            foreach (string[] t in new[]
            {
                PartParityJson.FxKeys, PartParityJson.FxDriveKeys, PartParityJson.FxSideKeys, PartParityJson.FxPlateauKeys,
                PartParityJson.FxHoldKeys, PartParityJson.FxRcsKeys, PartParityJson.FxFramingKeys, PartParityJson.FxLayerKeys,
                PartParityJson.FxViewKeys, PartParityJson.FxAxisKeys, PartParityJson.FxSideMeasureKeys, PartParityJson.FxCompareKeys,
                PartParityJson.FxSplitHalfKeys, PartParityJson.FxHalfKeys, PartParityJson.FxParticleKeys,
                PartParityJson.FxStructureKeys, PartParityJson.FxPlateKeys, PartParityJson.FxAtlasKeys, PartParityJson.FxStripKeys,
            })
                Assert.Equal(t.Length, t.Distinct().Count());
            // The side keys are the rules' sides, the layer keys the rules' renderer layers.
            Assert.Equal(PartParityFxRules.Sides, PartParityJson.FxSideKeys);
            Assert.Equal(PartParityFxRules.RendererLayers, PartParityJson.FxLayerKeys);
        }
    }
}
