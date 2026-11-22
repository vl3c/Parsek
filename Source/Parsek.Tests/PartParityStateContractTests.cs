using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the P6 sweep contract (design-part-ghost-parity.md D.2, D.6): the
    /// <c>states=</c> / <c>contexts=</c> args and refusals, <c>PlanCases</c> (a run without
    /// the new args plans exactly today's list; a state run plans no stowed / alone case),
    /// <c>ControlSetFor</c>, the air host rule and the host guard's air mode, the new sidecar
    /// and manifest keys, the atlas rows, and the committed stowed-id pin.
    /// </summary>
    public class PartParityStateContractTests
    {
        private static Dictionary<string, string> Args(params string[] kv)
        {
            var d = new Dictionary<string, string> { { "label", "pp-3" } };
            for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        private static PartParitySweepArgs Parse(params string[] kv)
        {
            Assert.True(TestCommandPartParitySweep.TryParseArgs(Args(kv), out var a, out string reason, out string detail),
                "rejected " + reason + " " + detail);
            return a;
        }

        private static string Reject(params string[] kv)
        {
            Assert.False(TestCommandPartParitySweep.TryParseArgs(Args(kv), out _, out string reason, out _));
            return reason;
        }

        // ----- args -----

        [Fact]
        public void TheLiveVerb_AcceptsAStateRun_NowTheStateEngineIsBuilt()
        {
            foreach (string[] kv in new[] { new[] { "states", "all" }, new[] { "contexts", "air" },
                new[] { "states", "deployed", "contexts", "stacked" } })
            {
                PartParitySweepArgs a = Parse(kv);
                Assert.True(TestCommandPartParitySweep.IsStateRun(a));
            }
            // A bad value keeps its own reason; `none` is a stowed run.
            Assert.Equal("states-arg-invalid", Reject("states", "bogus"));
            Assert.False(TestCommandPartParitySweep.IsStateRun(Parse("states", "none", "contexts", "none")));
            Assert.DoesNotContain("state-runs-not-built", TestCommandPartParitySweep.Reasons);
        }

        [Fact]
        public void NoStateArgs_IsNotAStateRun()
        {
            var a = Parse();
            Assert.Null(a.States);
            Assert.Null(a.Contexts);
            Assert.False(TestCommandPartParitySweep.IsStateRun(a));
            Assert.Equal("none", TestCommandPartParitySweep.FormatStates(a));
            Assert.Equal("none", TestCommandPartParitySweep.FormatContexts(a));
            a = Parse("states", "none", "contexts", "none");
            Assert.False(TestCommandPartParitySweep.IsStateRun(a));
        }

        [Fact]
        public void States_ParseAllOrACanonicalCsv()
        {
            var a = Parse("states", "all");
            Assert.True(a.StatesAll);
            Assert.Equal(PartParityStateRules.KnownStates, a.States);
            Assert.Equal("all", TestCommandPartParitySweep.FormatStates(a));
            a = Parse("states", "jettisoned,deployed,deployed");
            Assert.Equal(new[] { "deployed", "jettisoned" }, a.States);
            Assert.Equal("deployed,jettisoned", TestCommandPartParitySweep.FormatStates(a));
            Assert.True(TestCommandPartParitySweep.IsStateRun(a));
            Assert.False(TestCommandPartParitySweep.IsAirRun(a));
        }

        [Theory]
        [InlineData("states", "")]
        [InlineData("states", "deployed,")]
        [InlineData("states", "Deployed")]
        [InlineData("states", "intact")]
        [InlineData("states", "deployed-m3")]
        public void BadStates_AreRefused(string key, string value)
            => Assert.Equal("states-arg-invalid", Reject(key, value));

        [Theory]
        [InlineData("")]
        [InlineData("all")]
        [InlineData("orbit")]
        [InlineData("air,alone")]
        [InlineData("stacked,air")]
        public void BadContexts_AreRefused(string value)
            => Assert.Equal("contexts-arg-invalid", Reject("contexts", value));

        [Fact]
        public void Contexts_ParseAndAirStandsAlone()
        {
            var a = Parse("contexts", "stacked,alone");
            Assert.Equal(new[] { "alone", "stacked" }, a.Contexts);
            Assert.False(TestCommandPartParitySweep.IsAirRun(a));
            a = Parse("contexts", "air", "states", "all");
            Assert.True(TestCommandPartParitySweep.IsAirRun(a));
            Assert.Equal("air", TestCommandPartParitySweep.FormatContexts(a));
        }

        // ----- PlanCases -----

        private static List<PartParityCaseKey> StowedKeys()
        {
            var keys = new List<PartParityCaseKey>();
            foreach (string p in new[] { "mk1pod.v2", "longAntenna", "liquidEngine2", "parachuteSingle", "fuelTank" })
            {
                keys.Add(new PartParityCaseKey { Part = p, Source = "Squad", Skin = "default" });
                keys.Add(new PartParityCaseKey { Part = p, Source = "Squad", Skin = "White", SkinOrdinal = 1 });
            }
            return keys;
        }

        private static List<PartParityCaseKey> StateKeys()
        {
            var parts = new[]
            {
                new PartParityStatePart { Name = "longAntenna", Modules =
                {
                    new PartParityStateModule { Index = 0, Name = "ModuleDeployableAntenna", TypeChain = { "ModuleDeployableAntenna", "ModuleDeployablePart", "PartModule" },
                        Config = Node("animationName", "antenna") },
                } },
                new PartParityStatePart { Name = "liquidEngine2", StackNodes = { "top", "bottom" }, Modules =
                {
                    new PartParityStateModule { Index = 0, Name = "ModuleJettison" },
                } },
                new PartParityStatePart { Name = "parachuteSingle", Modules =
                {
                    new PartParityStateModule { Index = 0, Name = "ModuleParachute" },
                } },
                new PartParityStatePart { Name = "mk1pod.v2", Modules =
                {
                    new PartParityStateModule { Index = 0, Name = "ModuleColorChanger" },
                } },
            };
            var keys = new List<PartParityCaseKey>();
            foreach (var p in parts)
                keys.AddRange(PartParityStateRules.Enumerate(p,
                    new PartParityCaseKey { Part = p.Name, Source = "Squad", Skin = "default" }).Cases);
            return keys;
        }

        private static ConfigNode Node(string k, string v)
        {
            var n = new ConfigNode("MODULE");
            n.AddValue(k, v);
            return n;
        }

        private static string[] Ids(List<PartParityPlannedCase> planned) => planned.Select(p => p.CaseId).ToArray();

        [Fact]
        public void PlanCases_WithoutTheNewArgs_IsExactlyTodaysSelection()
        {
            foreach (string[] kv in new[]
            {
                new string[0], new[] { "skins", "all" }, new[] { "shard", "2/3" }, new[] { "limit", "3" },
                new[] { "filter", "l*" }, new[] { "states", "none", "contexts", "none" },
            })
            {
                var a = Parse(kv);
                var today = TestCommandPartParitySweep.SelectCases(StowedKeys(), a);
                var planned = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), a);
                Assert.Equal(Ids(today), Ids(planned));
                Assert.Equal(today.Select(p => p.Index), planned.Select(p => p.Index));
            }
        }

        [Fact]
        public void PlanCases_AStateRun_PlansNoStowedAloneCase()
        {
            var planned = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("states", "all"));
            Assert.Equal(new[]
            {
                "liquidEngine2 jettisoned/stacked", "liquidEngine2 stowed/stacked",
                "longAntenna deployed/alone", "mk1pod.v2 lit/alone",
            }, planned.Select(p => p.Key.Part + " " + p.Key.State + "/" + p.Key.Context));
            Assert.DoesNotContain(planned, p => p.Key.State == "stowed" && p.Key.Context == "alone");

            // contexts=stacked: the context cases only, the stacked stowed shroud included.
            planned = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("contexts", "stacked"));
            Assert.Equal(new[] { "jettisoned", "stowed" }, planned.Select(p => p.Key.State));

            // states=jettisoned: the stacked stowed case is not that state.
            planned = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("states", "jettisoned"));
            Assert.Equal(new[] { "jettisoned" }, planned.Select(p => p.Key.State));

            // The air run: the chutes only; an orbit run never plans them.
            planned = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("contexts", "air", "states", "all"));
            Assert.Equal(new[] { "full", "semi" }, planned.Select(p => p.Key.State));
            Assert.All(planned, p => Assert.Equal("parachuteSingle", p.Key.Part));
            planned = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("states", "semi,full"));
            Assert.Empty(planned);
        }

        [Fact]
        public void PlanCases_AStateRun_KeepsTheSelectionRules()
        {
            var all = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("states", "all"));
            var shard = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("states", "all", "shard", "2/2"));
            Assert.Equal(all.Where(p => p.Index % 2 == 1).Select(p => p.CaseId), Ids(shard));
            var lim = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("states", "all", "limit", "1"));
            Assert.Equal(new[] { all[0].CaseId }, Ids(lim));
            var filt = TestCommandPartParitySweep.PlanCases(StowedKeys(), StateKeys(), Parse("states", "all", "filter", "long*"));
            Assert.Equal(new[] { "longAntenna" }, filt.Select(p => p.Key.Part));
        }

        // ----- controls -----

        [Fact]
        public void ControlSet_IsAFunctionOfTheArgs()
        {
            Assert.Equal(TestCommandPartParitySweep.ControlIds, TestCommandPartParitySweep.ControlSetFor(Parse()));
            Assert.Equal(TestCommandPartParitySweep.ControlIds, TestCommandPartParitySweep.ControlSetFor(Parse("controls", "true")));
            Assert.Equal(TestCommandPartParitySweep.ControlIds.Concat(new[] { "ctl-state-events-dropped" }),
                TestCommandPartParitySweep.ControlSetFor(Parse("states", "all")));
            Assert.Equal(new[] { "ctl-empty", "ctl-ghost-absent", "ctl-real-vs-real", "ctl-state-events-dropped-air" },
                TestCommandPartParitySweep.ControlSetFor(Parse("contexts", "air")));
            Assert.Equal(8, TestCommandPartParitySweep.ControlIds.Length);
            Assert.Equal(TestCommandPartParitySweep.AllControlIds.Length, TestCommandPartParitySweep.AllControlIds.Distinct().Count());
            foreach (string id in TestCommandPartParitySweep.AllControlIds)
            {
                Assert.True(PartParitySweepPlan.IsControl(id), id);
                Assert.NotNull(PartParitySweepPlan.ControlSubject(id));
                Assert.NotNull(PartParitySweepPlan.ControlRequired(id));
            }
            Assert.Contains("parachuteSingle", PartParitySweepPlan.ControlSubject("ctl-real-vs-real", true));
        }

        [Fact]
        public void ControlsState_ReadsTheRunsSet()
        {
            string[] set = TestCommandPartParitySweep.ControlSetFor(Parse("states", "all"));
            var eight = Enumerable.Repeat("ok", 8).ToList();
            Assert.Equal("ok", TestCommandPartParitySweep.ControlsState(true, eight));
            Assert.Equal("pending", TestCommandPartParitySweep.ControlsState(true, eight, set));
            Assert.Equal("ok", TestCommandPartParitySweep.ControlsState(true, Enumerable.Repeat("ok", 9).ToList(), set));
            Assert.Equal("ok", TestCommandPartParitySweep.ControlsState(true, Enumerable.Repeat("ok", 4).ToList(),
                TestCommandPartParitySweep.ControlSetFor(Parse("contexts", "air"))));
        }

        [Fact]
        public void StateSettings_RecordTheSelectionAndTheSet()
        {
            var s = TestCommandPartParitySweep.StateSettings(Parse("states", "lit,deployed"));
            Assert.Equal(new[] { "states", "contexts", "controlSet" }, s.Select(p => p.Key));
            Assert.Equal("deployed,lit", s[0].Value);
            Assert.Equal("none", s[1].Value);
            Assert.EndsWith(",ctl-wrong-skin,ctl-state-events-dropped", s[2].Value);
            s = TestCommandPartParitySweep.StateSettings(Parse());
            Assert.Equal(string.Join(",", TestCommandPartParitySweep.ControlIds), s[2].Value);
        }

        private static PartParityViewRecord View(int realOnlyBlob, int stowedBlob)
            => new PartParityViewRecord
            {
                View = "side-a",
                Measures = new PartParityViewMeasures { RealOnlyBlobPx = realOnlyBlob },
                StowedRef = new PartParityViewMeasures { RealOnlyBlobPx = stowedBlob },
            };

        [Fact]
        public void JudgeStateControl_NeedsTheStateTheEventsAndTwoShapeFails()
        {
            var fails = new List<PartParityViewRecord> { View(500, 500) };
            Assert.Equal("ok", PartParitySweepPlan.JudgeStateControl(true, 1, fails, "ok", out _));
            Assert.Equal("bad", PartParitySweepPlan.JudgeStateControl(false, 1, fails, "ok", out string d));
            Assert.Contains("never reached", d);
            Assert.Equal("bad", PartParitySweepPlan.JudgeStateControl(true, 0, fails, "ok", out d));
            Assert.Equal("bad", PartParitySweepPlan.JudgeStateControl(true, 1, new List<PartParityViewRecord> { View(0, 500) }, "ok", out d));
            Assert.Equal("bad", PartParitySweepPlan.JudgeStateControl(true, 1, new List<PartParityViewRecord> { View(500, 0) }, "ok", out d));
            Assert.Contains("stowedRef", d);
            Assert.Equal("bad", PartParitySweepPlan.JudgeStateControl(true, 1, fails, "state-timeout", out d));
        }

        // ----- setup statuses -----

        [Fact]
        public void TheNewSetupStatuses_AreSetupFaults()
        {
            Assert.Contains("action-failed", TestCommandPartParitySweep.SetupStatuses);
            Assert.Contains("state-timeout", TestCommandPartParitySweep.SetupStatuses);
            Assert.True(TestCommandPartParitySweep.IsSetupFault("action-failed"));
            Assert.True(TestCommandPartParitySweep.IsSetupFault("state-timeout"));
            Assert.Equal(new[] { "action-failed", "state-timeout" }, PartParityStateRules.PressGateStatuses);
        }

        // ----- the air host rule -----

        [Fact]
        public void AirAdmission_NeedsAHostAtThePad()
        {
            var air = Parse("contexts", "air");
            Assert.Null(TestCommandPartParitySweep.DecideHostAdmission(air, true, false, true, 120.0));
            Assert.Equal("host-not-at-pad", TestCommandPartParitySweep.DecideHostAdmission(air, true, false, true, 501.0));
            Assert.Equal("host-not-at-pad", TestCommandPartParitySweep.DecideHostAdmission(air, true, true, false, 10.0));
            Assert.Equal("host-not-at-pad", TestCommandPartParitySweep.DecideHostAdmission(air, true, false, true, double.NaN));
            Assert.Equal("no-active-vessel", TestCommandPartParitySweep.DecideHostAdmission(air, false, false, false, 0.0));
            // Orbit runs, stowed or state, keep the orbiting rule.
            Assert.Equal("host-not-orbiting", TestCommandPartParitySweep.DecideHostAdmission(Parse("states", "all"), true, false, true, 0.0));
            Assert.Null(TestCommandPartParitySweep.DecideHostAdmission(Parse(), true, true, false, double.NaN));
            Assert.Contains("host-not-at-pad", TestCommandPartParitySweep.Reasons);
        }

        [Fact]
        public void HostGuard_AirMode_KeepsPrelaunchOrLanded()
        {
            var b = new PartParityHostBaseline { Pid = 7, PartCount = 3, Situation = "PRELAUNCH", Air = true };
            var landed = new PartParityHostObservation { Present = true, Active = true, PartCount = 3, Situation = "LANDED" };
            Assert.Null(PartParityHostIntegrity.Decide(b, landed));
            var flying = new PartParityHostObservation { Present = true, Active = true, PartCount = 3, Situation = "FLYING" };
            Assert.Equal("situation", PartParityHostIntegrity.Decide(b, flying));
            // An orbit baseline still needs ORBITING.
            b.Air = false;
            Assert.Equal("situation", PartParityHostIntegrity.Decide(b, landed));
            // The mode round-trips; a baseline without it reads as orbit.
            b.Air = true;
            PartParityHostBaseline back = PartParityHostIntegrity.ParseBaseline(PartParityHostIntegrity.FormatBaseline(b));
            Assert.True(back.Air);
            Assert.False(PartParityHostIntegrity.ParseBaseline("pid=7\nparts=3\nsituation=ORBITING\n").Air);
        }

        // ----- files -----

        [Fact]
        public void AtlasRows_AreFourForAStateCase()
        {
            Assert.Equal(new[] { "real", "ghost" }, PartParityJson.AtlasRowsFor(false));
            Assert.Equal(new[] { "real", "ghost", "ghost-start", "ghost-stowed" }, PartParityJson.AtlasRowsFor(true));
            PartParityJson.AtlasRowsFor(true)[0] = "x";
            Assert.Equal("real", PartParityJson.StateAtlasRows[0]);
            Assert.False(TestCommandPartParitySweep.IsStateCase(new PartParityCaseKey { State = "stowed", Context = "stacked" }));
            Assert.True(TestCommandPartParitySweep.IsStateCase(new PartParityCaseKey { State = "deployed-m3" }));
        }

        /// <summary>The per-case tile list of each atlas row label, as the live capture names
        /// them (<c>WaveCase.RealTiles</c>, ...). The two state rows name the lists S3 adds; when
        /// S3 renames one, it edits this table in the same commit.</summary>
        private static readonly Dictionary<string, string> TileListOfRow = new Dictionary<string, string>
        {
            ["wc.RealTiles"] = "real",
            ["wc.GhostTiles"] = "ghost",
            ["wc.GhostStartTiles"] = "ghost-start",
            ["wc.GhostStowedTiles"] = "ghost-stowed",
        };

        /// <summary>
        /// The atlas bitmap is composed top-down from the tile lists in the order the capture
        /// passes them (<c>new[] { wc.RealTiles.ToArray(), wc.GhostTiles.ToArray() }</c> in
        /// <c>PartParityCaseCapture</c>), and the sidecar labels the rows with
        /// <c>PartParityJson.AtlasRowsFor</c>: the two must name the same rows in the same order,
        /// or the Python recompute decodes the real tile as the ghost. Every composition the
        /// capture holds must equal the stowed rows or the state rows, and the stowed one exists.
        /// </summary>
        [Fact]
        public void TheAtlasTileOrder_IsTheAtlasRowLabelOrder()
        {
            string path = System.IO.Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(),
                "Source", "Parsek", "TestCommands", "PartParityCaseCapture.cs");
            string text = PartParityStateRulesTests.StripComments(System.IO.File.ReadAllText(path));
            var compositions = System.Text.RegularExpressions.Regex.Matches(text,
                    @"new\[\]\s*\{\s*((?:wc\.\w+Tiles\.ToArray\(\)\s*,?\s*)+)\}")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"wc\.\w+Tiles")
                    .Cast<System.Text.RegularExpressions.Match>().Select(t => t.Value).ToArray())
                .ToList();
            Assert.NotEmpty(compositions);
            bool sawStowed = false;
            foreach (string[] lists in compositions)
            {
                foreach (string l in lists)
                    Assert.True(TileListOfRow.ContainsKey(l), "an atlas row from an unknown tile list: " + l);
                string[] rows = lists.Select(l => TileListOfRow[l]).ToArray();
                bool stowed = rows.SequenceEqual(PartParityJson.AtlasRowsFor(false));
                Assert.True(stowed || rows.SequenceEqual(PartParityJson.AtlasRowsFor(true)),
                    "atlas composed as [" + string.Join(", ", rows) + "], not an AtlasRowsFor order");
                sawStowed |= stowed;
            }
            Assert.True(sawStowed, "no stowed (real, ghost) composition in the capture");
        }

        private static void AssertKeysInOrder(string[] table, Dictionary<string, object> parsed, string json, int from = 0)
        {
            Assert.Equal(table.OrderBy(k => k, System.StringComparer.Ordinal),
                parsed.Keys.OrderBy(k => k, System.StringComparer.Ordinal));
            int last = from - 1;
            foreach (string key in table)
            {
                int at = json.IndexOf("\"" + key + "\":", last + 1, System.StringComparison.Ordinal);
                Assert.True(at > last, "key '" + key + "' out of table order");
                last = at;
            }
        }

        [Fact]
        public void AStateSidecar_WritesEveryNewBlockInTableOrder()
        {
            PartParityCaseSidecar c = PartParityJsonTests.SmallSidecar();
            c.State = "deployed";
            c.StowedCaseId = "longantenna--default--00000000";
            c.StructureStart = new PartParityStructureDiff { RealRenderers = 1, GhostRenderers = 1, Pairs = 1 };
            c.StateProof = new PartParityStateProof
            {
                Family = "deployable", Module = "ModuleDeployableAntenna", ModuleIndex = 0, State = "deployed",
                Action = "Extend", EventVisible = true, ActionResult = "invoked", Reached = true, WaitS = 2.5,
                EndpointField = "deployState", EndpointValue = "EXTENDED",
                Events = new List<PartParityStateEvent> { new PartParityStateEvent { Type = "DeployableExtended", Ut = 10.5, Pid = 100000, Midx = 0 } },
                GhostEvent = "applied", GhostStart = "applied", StartFirstFrameAtTarget = true,
                Nodes = new List<PartParityContextNode> { new PartParityContextNode { Node = "bottom", Part = "fuelTankSmallFlat" } },
            };
            c.Views[0].Start = new PartParityViewRecord { Measures = c.Views[0].Measures };
            c.Views[0].StowedRef = c.Views[0].Measures;
            string json = PartParityJson.WriteCase(c);
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            var top = json.Split('\n').Where(l => l.StartsWith("  \"")).Select(l => l.Substring(3, l.IndexOf('"', 3) - 3)).ToArray();
            Assert.Equal(PartParityJson.CaseKeys, top);

            var proof = (Dictionary<string, object>)root["stateProof"];
            int at = json.IndexOf("\"stateProof\":", System.StringComparison.Ordinal);
            AssertKeysInOrder(PartParityJson.StateProofKeys, proof, json, at);
            Assert.Equal(PartParityJson.EndpointKeys, ((Dictionary<string, object>)proof["endpoint"]).Keys);
            Assert.Equal(PartParityJson.StateEventKeys, ((Dictionary<string, object>)((List<object>)proof["events"])[0]).Keys);
            Assert.Equal(PartParityJson.StartFirstFrameKeys, ((Dictionary<string, object>)proof["startFirstFrame"]).Keys);
            Assert.Equal(PartParityJson.SunTrackerKeys, ((Dictionary<string, object>)proof["sunTracker"]).Keys);
            Assert.Equal(PartParityJson.CanopyKeys, ((Dictionary<string, object>)proof["canopy"]).Keys);
            Assert.Equal(PartParityJson.ContextNodeKeys, ((Dictionary<string, object>)((List<object>)proof["nodes"])[0]).Keys);
            Assert.Null(((Dictionary<string, object>)proof["canopy"])["tiltDeg"]);
            Assert.Equal(100000.0, ((Dictionary<string, object>)((List<object>)proof["events"])[0])["pid"]);

            var view = (Dictionary<string, object>)((List<object>)root["views"])[0];
            Assert.Equal(PartParityJson.ViewKeys, view.Keys);
            Assert.Equal(PartParityJson.StartKeys, ((Dictionary<string, object>)view["start"]).Keys);
            Assert.Equal(PartParityJson.StowedRefKeys, ((Dictionary<string, object>)view["stowedRef"]).Keys);
            Assert.Equal(PartParityJson.StructureKeys, ((Dictionary<string, object>)root["structureStart"]).Keys);
            Assert.Equal("longantenna--default--00000000", root["stowedCaseId"]);
        }

        [Fact]
        public void AStowedSidecar_WritesTheNewKeysAsNull()
        {
            string json = PartParityJson.WriteCase(PartParityJsonTests.SmallSidecar());
            var root = (Dictionary<string, object>)MiniJson.ParseValue(json);
            Assert.Null(root["stateProof"]);
            Assert.Null(root["structureStart"]);
            Assert.Null(root["stowedCaseId"]);
            var view = (Dictionary<string, object>)((List<object>)root["views"])[0];
            Assert.Null(view["start"]);
            Assert.Null(view["stowedRef"]);
            // The StartKeys are the view keys between the name and the two route blocks.
            Assert.Equal(PartParityJson.ViewKeys.Skip(1).Take(PartParityJson.ViewKeys.Length - 3), PartParityJson.StartKeys);
            Assert.All(PartParityJson.StowedRefKeys, k => Assert.Contains(k, PartParityJson.StartKeys));
        }

        [Fact]
        public void TheCaseSummary_CarriesTheStateSetup()
        {
            PartParityCaseSidecar c = PartParityJsonTests.SmallSidecar();
            c.StateProof = new PartParityStateProof { Family = "light", ActionResult = "refused", Reached = false };
            PartParityCaseSummary s = PartParitySweepPlan.Summarize(3, c);
            Assert.Equal("light", s.Family);
            Assert.Equal("refused", s.ActionResult);
            Assert.False(s.Reached);
            Assert.Null(PartParitySweepPlan.Summarize(3, PartParityJsonTests.SmallSidecar()).Family);
        }

        // ----- the committed stowed-id pin -----

        /// <summary>
        /// Stowed case ids cannot move (design D.2): these are copied verbatim from the archived
        /// manifests of the PP-1 / PP-2 flights of 2026-11-22 (results-archive
        /// <c>2026-11-22_0033_PP-1-part-parity-stock</c> and <c>_0045_PP-2-part-parity-modded</c>):
        /// base, variant (incl. a name holding a space) and B9 cases of both installs. Each one
        /// is recomputed from its key here; the archive is never read.
        /// </summary>
        public static IEnumerable<object[]> StowedIds()
        {
            yield return new object[] { "AdvancedCanard", "default", "advancedcanard--default--9301994f" };
            yield return new object[] { "CanardController", "default", "canardcontroller--default--9fc0ebbd" };
            yield return new object[] { "CargoStorageUnit", "default", "cargostorageunit--default--d794d67d" };
            yield return new object[] { "CircularIntake", "default", "circularintake--default--d5470c37" };
            yield return new object[] { "Decoupler.0", "Basic", "decoupler.0--basic--0106ee70" };
            yield return new object[] { "Decoupler.0", "YellowAndWhite", "decoupler.0--yellowandwhite--aa53ca8a" };
            yield return new object[] { "externalTankToroid", "Silver", "externaltanktoroid--silver--b9b0a26c" };
            yield return new object[] { "Size1p5.Size0.Adapter.01", "Dark Grey", "size1p5.size0.adapter.01--dark-grey--38f240ee" };
            yield return new object[] { "Size1p5.Size1.Adapter.01", "Dark Grey", "size1p5.size1.adapter.01--dark-grey--925ef0a5" };
            yield return new object[] { "mk1pod.v2", "Basic", "mk1pod.v2--basic--2271ad8f" };
            yield return new object[] { "mk1pod.v2", "White", "mk1pod.v2--white--f03c7fc2" };
            yield return new object[] { "mk1pod.v2", "GrayAndWhite", "mk1pod.v2--grayandwhite--a2656d3a" };
            yield return new object[] { "mk1pod.v2", "Dark", "mk1pod.v2--dark--fc073a35" };
            yield return new object[] { "CargoStorageUnit", "white", "cargostorageunit--white--25b90e0d" };
            yield return new object[] { "CargoStorageUnit", "metal", "cargostorageunit--metal--897bffbf" };
            yield return new object[] { "largeHeliBlade", "Counterclockwise", "largeheliblade--counterclockwise--7421384e" };
            yield return new object[] { "LiquidEngineLV-T91", "plumeSwitch=default", "liquidenginelv-t91--plumeswitch-default--09a57fdd" };
            yield return new object[] { "LiquidEngineLV-T91", "plumeSwitch=hypergolicCheetah", "liquidenginelv-t91--plumeswitch-hypergoliccheetah--7bb3daf0" };
            yield return new object[] { "LiquidEngineLV-T91", "plumeSwitch=keroloxCheetah", "liquidenginelv-t91--plumeswitch-keroloxcheetah--77625644" };
        }

        [Theory]
        [MemberData(nameof(StowedIds))]
        public void StowedCaseIds_AreUnchanged(string part, string skin, string id)
        {
            var key = new PartParityCaseKey { Part = part, Skin = skin };
            Assert.Equal("stowed", key.State);
            Assert.Equal("alone", key.Context);
            Assert.Equal(id, TestCommandPartParitySweep.CaseId(key));
        }
    }
}
