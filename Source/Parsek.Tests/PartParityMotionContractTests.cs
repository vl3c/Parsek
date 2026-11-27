using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the P7 sweep contract (design-part-ghost-parity.md Appendix H, piece
    /// M1): the <c>motion=</c> and <c>frames=</c> args and their refusals; <c>IsMotionRun</c>,
    /// the motion views and tile, <c>settings.motion</c> / <c>settings.frames</c>,
    /// <c>ControlSetFor</c> and <c>PlanCases</c> for a motion run; the no-change cell (with
    /// <c>motion=none</c> every PP-1 to PP-10 plan, control set and setting is today's); the
    /// motion key tables and the motion atlas rows.
    /// </summary>
    public class PartParityMotionContractTests
    {
        private static Dictionary<string, string> Args(params string[] kv)
        {
            var d = new Dictionary<string, string> { { "label", "pp14" } };
            for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        private static PartParitySweepArgs Grammar(params string[] kv)
        {
            Assert.True(TestCommandPartParitySweep.TryParseGrammar(Args(kv), out var a, out string reason, out string detail),
                "rejected " + reason + " " + detail);
            return a;
        }

        private static string Reject(params string[] kv)
        {
            Assert.False(TestCommandPartParitySweep.TryParseGrammar(Args(kv), out _, out string reason, out _));
            return reason;
        }

        // ----- motion= and frames= -----

        [Fact]
        public void Motion_ParsesNoneAllAndACsvInCanonicalOrder()
        {
            Assert.Null(Grammar().Motion);
            Assert.Null(Grammar("motion", "none").Motion);
            var all = Grammar("motion", "all");
            Assert.True(all.MotionAll);
            Assert.Equal(PartParityMotionRules.ArgWords, all.Motion);
            Assert.DoesNotContain("light", all.Motion);
            var csv = Grammar("motion", "robotics,gear,robotics");
            Assert.False(csv.MotionAll);
            Assert.Equal(new[] { "gear", "robotics" }, csv.Motion);
            Assert.Equal(new[] { "light" }, Grammar("motion", "light").Motion);
            Assert.Equal("gear,robotics", TestCommandPartParitySweep.FormatMotion(csv));
            Assert.Equal("all", TestCommandPartParitySweep.FormatMotion(all));
            Assert.Equal("none", TestCommandPartParitySweep.FormatMotion(Grammar()));
        }

        [Theory]
        [InlineData("motion", "")]
        [InlineData("motion", "jettison")]
        [InlineData("motion", "gear,")]
        [InlineData("motion", "All")]
        [InlineData("motion", "all", "states", "all")]
        [InlineData("motion", "all", "fx", "all")]
        [InlineData("motion", "all", "contexts", "stacked")]
        [InlineData("motion", "all", "contexts", "alone")]
        public void Motion_RefusesAnUnknownWordOrAMixedRun(params string[] kv)
            => Assert.Equal("motion-arg-invalid", Reject(kv));

        [Fact]
        public void Motion_TakesTheAirContext_ForTheChutes()
        {
            var a = Grammar("motion", "all", "contexts", "air");
            Assert.True(TestCommandPartParitySweep.IsMotionRun(a));
            Assert.True(TestCommandPartParitySweep.IsAirRun(a));
            // fx=none and states=none are no selection: still a motion run.
            Assert.True(TestCommandPartParitySweep.IsMotionRun(Grammar("motion", "all", "fx", "none", "states", "none")));
        }

        [Theory]
        [InlineData("4", 4)]
        [InlineData("8", 8)]
        [InlineData("16", 16)]
        public void Frames_ParsesInRange_OnAMotionRun(string raw, int expected)
            => Assert.Equal(expected, Grammar("motion", "all", "frames", raw).Frames);

        [Theory]
        [InlineData("motion", "all", "frames", "3")]
        [InlineData("motion", "all", "frames", "17")]
        [InlineData("motion", "all", "frames", "+8")]
        [InlineData("motion", "all", "frames", "8.0")]
        [InlineData("frames", "8")]
        [InlineData("motion", "none", "frames", "8")]
        [InlineData("states", "all", "frames", "8")]
        public void Frames_RefusesOutOfRangeOrWithoutAMotionRun(params string[] kv)
            => Assert.Equal("frames-arg-invalid", Reject(kv));

        [Fact]
        public void AMotionRun_DefaultsToTwoSideViewsAt256_AndKeepsWhatTheStepNames()
        {
            var a = Grammar("motion", "all");
            Assert.Equal(new[] { "side-a", "side-b" }, a.Views);
            Assert.Equal(256, a.Tile);
            Assert.Equal(8, a.Frames);
            var named = Grammar("motion", "all", "views", "required", "tile", "512");
            Assert.Equal(TestCommandPartParitySweep.RequiredViews, named.Views);
            Assert.Equal(512, named.Tile);
            // A run without motion= keeps today's defaults.
            Assert.Equal(TestCommandPartParitySweep.RequiredViews, Grammar().Views);
            Assert.Equal(TestCommandPartParitySweep.DefaultTile, Grammar().Tile);
        }

        [Fact]
        public void TheRefusals_AreInTheVocabularyAndTheArgKeysInParseOrder()
        {
            int fx = System.Array.IndexOf(TestCommandPartParitySweep.Reasons, "fx-arg-invalid");
            Assert.Equal(new[] { "fx-arg-invalid", "motion-arg-invalid", "frames-arg-invalid", "views-arg-invalid" },
                TestCommandPartParitySweep.Reasons.Skip(fx).Take(4));
            int fxKey = System.Array.IndexOf(TestCommandPartParitySweep.ArgKeys, "fx");
            Assert.Equal(new[] { "fx", "motion", "frames", "views" }, TestCommandPartParitySweep.ArgKeys.Skip(fxKey).Take(4));
        }

        // ----- settings and control sets (H.2, H.3) -----

        [Fact]
        public void AMotionRun_RecordsMotionAndFrames_AfterTheStateSettings()
        {
            var settings = TestCommandPartParitySweep.StateSettings(Grammar("motion", "robotics,gear", "frames", "12"));
            Assert.Equal(new[]
                {
                    "states=none", "contexts=none",
                    "controlSet=ctl-empty,ctl-ghost-absent,ctl-real-vs-real,ctl-motion-real-vs-real,ctl-motion-ghost-slow,ctl-motion-ghost-late",
                    "motion=gear,robotics", "frames=12",
                },
                settings.Select(p => p.Key + "=" + p.Value));
            settings = TestCommandPartParitySweep.StateSettings(Grammar("motion", "all", "contexts", "air"));
            Assert.Equal(new[]
                {
                    "states=none", "contexts=air", "controlSet=ctl-empty,ctl-ghost-absent,ctl-motion-real-vs-real-air",
                    "motion=all", "frames=8",
                },
                settings.Select(p => p.Key + "=" + p.Value));
        }

        [Fact]
        public void TheMotionControls_AreKnownWithSubjectsAndRequirements()
        {
            foreach (string id in TestCommandPartParitySweep.MotionOrbitControlIds.Concat(TestCommandPartParitySweep.MotionAirControlIds))
            {
                Assert.Contains(id, TestCommandPartParitySweep.AllControlIds);
                Assert.NotNull(PartParitySweepPlan.ControlSubject(id));
                Assert.NotNull(PartParitySweepPlan.ControlRequired(id));
            }
            Assert.Equal(TestCommandPartParitySweep.AllControlIds.Length, TestCommandPartParitySweep.AllControlIds.Distinct().Count());
            Assert.Contains("longAntenna", PartParitySweepPlan.ControlSubject("ctl-motion-ghost-late"));
            Assert.Contains("airbrake1", PartParitySweepPlan.ControlSubject("ctl-motion-ghost-slow"));
            Assert.Contains("parachuteSingle", PartParitySweepPlan.ControlSubject("ctl-motion-real-vs-real-air"));
            Assert.True(PartParitySweepPlan.IsMotionOnlyControl("ctl-motion-ghost-slow"));
            Assert.False(PartParitySweepPlan.IsMotionOnlyControl("ctl-real-vs-real"));
            Assert.False(PartParitySweepPlan.IsMotionOnlyControl("ctl-empty"));
            Assert.False(PartParitySweepPlan.IsFxOnlyControl("ctl-motion-ghost-slow"));
            // The capture primitive's real-vs-real keeps its stowed subject on a motion run.
            Assert.Equal(PartParitySweepPlan.ControlSubject("ctl-real-vs-real"),
                PartParitySweepPlan.ControlSubject("ctl-real-vs-real", false, false));
        }

        // ----- PlanCases -----

        private static PartParityCaseKey Stowed(string part) => new PartParityCaseKey { Part = part, Source = "Squad" };

        private static List<PartParityCaseKey> MotionKeys()
        {
            var keys = new List<PartParityCaseKey>();
            foreach (var p in PartParityStateRulesTests.LoadPartsFixture("motion-parts-stock-minimal.txt")
                .Where(p => p.Name == "longAntenna" || p.Name == "parachuteSingle" || p.Name == "hinge.01"
                            || p.Name == "StandardCtrlSrf" || p.Name == "spotLight1"))
                keys.AddRange(PartParityMotionRules.Enumerate(p, Stowed(p.Name)).Cases);
            return keys;
        }

        private static List<PartParityCaseKey> StateKeys()
        {
            var keys = new List<PartParityCaseKey>();
            foreach (var p in PartParityStateRulesTests.LoadPartsFixture("state-parts-stock-minimal.txt")
                .Where(p => p.Name == "longAntenna" || p.Name == "parachuteSingle"))
                keys.AddRange(PartParityStateRules.Enumerate(p, Stowed(p.Name)).Cases);
            return keys;
        }

        private static string[] Ids(List<PartParityPlannedCase> plan) => plan.Select(c => c.CaseId).ToArray();

        [Fact]
        public void AMotionRun_PlansOnlyItsFamiliesInItsContext()
        {
            var stowed = new List<PartParityCaseKey> { Stowed("longAntenna"), Stowed("hinge.01") };
            var motion = MotionKeys();
            var plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("motion", "all"), null, motion);
            Assert.Equal(new[] { "StandardCtrlSrf motion-deployed/radial", "hinge.01 motion-swept/alone", "longAntenna motion-deployed/alone" },
                plan.Select(c => c.Key.Part + " " + c.Key.State + "/" + c.Key.Context));
            Assert.All(plan, c => Assert.NotNull(c.Key.MotionOption));
            plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("motion", "all", "contexts", "air"), null, motion);
            Assert.Equal(new[] { "motion-full/air", "motion-semi/air" }, plan.Select(c => c.Key.State + "/" + c.Key.Context));
            plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("motion", "robotics"), null, motion);
            Assert.Equal("hinge.01", plan.Single().Key.Part);
            // light by name plans the reading cases; all never does.
            plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("motion", "light"), null, motion);
            Assert.Equal(new[] { "spotLight1 motion-lit" }, plan.Select(c => c.Key.Part + " " + c.Key.State));
            // filter / shard / limit apply as to every run.
            plan = TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("motion", "all", "filter", "long*"), null, motion);
            Assert.Equal("longAntenna", plan.Single().Key.Part);
            // No motion keys handed over (the runner before M3): a motion run plans nothing.
            Assert.Empty(TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), Grammar("motion", "all")));
            Assert.True(TestCommandPartParitySweep.IsMotionCase(plan.Single().Key));
            Assert.False(TestCommandPartParitySweep.IsMotionCase(StateKeys()[0]));
        }

        [Fact]
        public void AMotionCaseId_NeverMovesAP6Id()
        {
            var motion = MotionKeys().Select(TestCommandPartParitySweep.CaseId);
            var state = StateKeys().Select(TestCommandPartParitySweep.CaseId);
            Assert.Empty(motion.Intersect(state));
            Assert.Contains(motion, id => id.StartsWith("parachutesingle--default--motion-semi--air--", System.StringComparison.Ordinal));
        }

        /// <summary>Every distinct step of PP-1 to PP-10 (harness/scenarios/PP-*.toml), with
        /// what it recorded before M1: its control set and its manifest settings.</summary>
        public static IEnumerable<object[]> TodaysLanes()
        {
            string stowed = string.Join(",", TestCommandPartParitySweep.ControlIds);
            string orbit = stowed + ",ctl-state-events-dropped";
            const string air = "ctl-empty,ctl-ghost-absent,ctl-real-vs-real,ctl-state-events-dropped-air";
            string fxOrbit = string.Join(",", TestCommandPartParitySweep.FxOrbitControlIds);
            const string fxAir = "ctl-empty,ctl-ghost-absent,ctl-fx-real-vs-real-air,ctl-fx-plume-off-air";
            var run = new[] { "op", "run", "controls", "true", "maxSeconds", "480" };
            foreach (string label in new[] { "pp1", "pp2" })
            {
                yield return new object[] { new[] { "label", label, "op", "reset" }, "none", "none", stowed, null };
                yield return new object[] { new[] { "label", label, "skins", "all" }.Concat(run).ToArray(), "none", "none", stowed, null };
            }
            foreach (string label in new[] { "pp3", "pp4" })
            {
                yield return new object[] { new[] { "label", label, "op", "reset" }, "none", "none", stowed, null };
                yield return new object[] { new[] { "label", label, "states", "all" }.Concat(run).ToArray(), "all", "none", orbit, null };
            }
            foreach (string label in new[] { "pp5", "pp6" })
            {
                yield return new object[] { new[] { "label", label, "op", "reset", "contexts", "air" }, "none", "air", air, null };
                yield return new object[] { new[] { "label", label, "contexts", "air", "states", "all" }.Concat(run).ToArray(), "all", "air", air, null };
            }
            foreach (string label in new[] { "pp7", "pp8", "pp9" })
            {
                yield return new object[] { new[] { "label", label, "op", "reset" }, "none", "none", stowed, null };
                yield return new object[] { new[] { "label", label, "fx", "all" }.Concat(run).ToArray(), "none", "none", fxOrbit, "all" };
            }
            foreach (string label in new[] { "pp10", "pp5fx", "pp6fx" })
            {
                yield return new object[] { new[] { "label", label, "op", "reset", "contexts", "air", "fx", "all" }, "none", "air", fxAir, "all" };
                yield return new object[] { new[] { "label", label, "contexts", "air", "fx", "all" }.Concat(run).ToArray(), "none", "air", fxAir, "all" };
            }
        }

        [Theory]
        [MemberData(nameof(TodaysLanes))]
        public void WithMotionNone_EveryLaneOfP6AndP8PlansAndRecordsWhatItDidBefore(string[] kv, string states,
            string contexts, string controlSet, string fx)
        {
            var raw = new Dictionary<string, string>();
            for (int i = 0; i < kv.Length; i += 2) raw[kv[i]] = kv[i + 1];
            var expected = new List<string> { "states=" + states, "contexts=" + contexts, "controlSet=" + controlSet };
            if (fx != null) expected.Add("fx=" + fx);
            foreach (bool withMotionNone in new[] { false, true })
            {
                if (withMotionNone) raw["motion"] = "none";
                Assert.True(TestCommandPartParitySweep.TryParseArgs(raw, out var a, out string reason, out _), reason);
                Assert.False(TestCommandPartParitySweep.IsMotionRun(a));
                Assert.Equal(controlSet, string.Join(",", TestCommandPartParitySweep.ControlSetFor(a)));
                Assert.Equal(expected, TestCommandPartParitySweep.StateSettings(a).Select(p => p.Key + "=" + p.Value));
                Assert.Equal(TestCommandPartParitySweep.RequiredViews, a.Views);
                Assert.Equal(TestCommandPartParitySweep.DefaultTile, a.Tile);
                var stowed = new List<PartParityCaseKey> { Stowed("longAntenna"), Stowed("parachuteSingle") };
                Assert.Equal(Ids(TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), a)),
                    Ids(TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), a, null, MotionKeys())));
                Assert.All(TestCommandPartParitySweep.PlanCases(stowed, StateKeys(), a, null, MotionKeys()),
                    c => Assert.Null(c.Key.MotionOption));
            }
        }

        // ----- the motion key tables and rows -----

        [Fact]
        public void TheMotionAtlasRows_ArePerViewRealGhostMatched()
        {
            Assert.Equal(new[] { "real:side-a", "ghost:side-a", "ghost-matched:side-a", "real:side-b", "ghost:side-b", "ghost-matched:side-b" },
                PartParityJson.MotionAtlasRows(PartParityMotionRules.DefaultViews, true));
            Assert.Equal(new[] { "real:side-a", "ghost:side-a" }, PartParityJson.MotionAtlasRows(new[] { "side-a" }, false));
            Assert.Empty(PartParityJson.MotionAtlasRows(null, true));
            Assert.Equal("abc.motion.png", TestCommandPartParitySweep.MotionAtlasFileName("abc"));
        }

        [Fact]
        public void TheCaseSidecar_WritesTheMotionBlocksLast_NullForANonMotionCase()
        {
            string doc = PartParityJson.WriteCase(new PartParityCaseSidecar { CaseId = "x" });
            Assert.Contains("\"motion\": null", doc);
            Assert.Contains("\"motionAtlas\": null", doc);
            Assert.Equal(new[] { "motion", "motionAtlas" }, PartParityJson.CaseKeys.Skip(PartParityJson.CaseKeys.Length - 2));
            string motion = PartParityJson.KeyedBlock(PartParityJson.MotionKeys, new Dictionary<string, string> { ["family"] = "\"gear\"" });
            foreach (string k in PartParityJson.MotionKeys) Assert.Contains("\"" + k + "\":", motion);
            Assert.Contains("\"timing\":null", motion);
        }

        [Fact]
        public void TheWindowBlock_WritesItsKeys()
        {
            var w = PartParityMotionRules.PlanFrameTargets(2.0, 2.1, 2.0, 0.0, 8);
            Assert.Equal("{\"endS\":4.6,\"spacingS\":0.25,\"capped\":false}", PartParityJson.MotionWindowBlock(w));
            Assert.Equal("null", PartParityJson.MotionTimingBlock(null));
        }

        [Fact]
        public void TheMotionKeyTables_HoldNoDuplicate()
        {
            foreach (string[] t in new[]
            {
                PartParityJson.MotionKeys, PartParityJson.MotionWindowKeys, PartParityJson.MotionScoutKeys,
                PartParityJson.MotionFrameKeys, PartParityJson.MotionFrameViewKeys, PartParityJson.MotionTransformKeys,
                PartParityJson.MotionTimingKeys, PartParityJson.MotionCrossingKeys, PartParityJson.MotionAtlasKeys,
            })
                Assert.Equal(t.Length, t.Distinct().Count());
        }
    }
}
