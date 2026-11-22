using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the P6 state-case contract (design-part-ghost-parity.md Appendix D,
    /// D.1 / D.2): the family predicates and exclusions over synthetic parts and over the
    /// committed fixtures of both harness installs (the counts pinned here are the D.1
    /// tables), the state words and the tiebreak, the done-signal readers, timeouts and
    /// settles, the ghost-side read and the wave bound.
    /// </summary>
    public class PartParityStateRulesTests
    {
        // ----- builders -----

        private static PartParityStateModule Mod(int index, string name, string chain = null, params string[] kv)
        {
            var m = new PartParityStateModule { Index = index, Name = name };
            m.TypeChain.Add(name);
            if (chain != null) foreach (string t in chain.Split(',')) if (!m.TypeChain.Contains(t)) m.TypeChain.Add(t);
            m.TypeChain.Add("PartModule");
            if (kv.Length > 0)
            {
                m.Config = new ConfigNode("MODULE");
                m.Config.AddValue("name", name);
                for (int i = 0; i < kv.Length; i += 2) m.Config.AddValue(kv[i], kv[i + 1]);
            }
            return m;
        }

        private static PartParityStatePart Part(string name, string nodes, params PartParityStateModule[] mods)
        {
            var p = new PartParityStatePart { Name = name };
            p.Modules.AddRange(mods);
            if (!string.IsNullOrEmpty(nodes)) p.StackNodes.AddRange(nodes.Split(','));
            return p;
        }

        private static PartParityCaseKey Base(string part)
            => new PartParityCaseKey { Part = part, Source = "Squad", Skin = "default" };

        private static PartParityStateEnumeration Enumerate(PartParityStatePart p)
            => PartParityStateRules.Enumerate(p, Base(p.Name));

        private static string Pair(PartParityCaseKey k) => k.State + "/" + k.Context;

        // ----- families over synthetic parts -----

        [Fact]
        public void Deployable_WithAnAnimation_IsOneDeployedCase_AndAFixedPanelIsNoPose()
        {
            var r = Enumerate(Part("longAntenna", "bottom",
                Mod(0, "ModuleDeployableAntenna", "ModuleDeployablePart", "animationName", "antenna")));
            Assert.Equal(new[] { "deployed/alone" }, r.Cases.Select(Pair));
            PartParityStateOption o = r.Cases[0].StateOption;
            Assert.Equal(PartParityStateRules.FamilyDeployable, o.Family);
            Assert.Equal("Extend", o.Action);
            Assert.False(o.ActionIsKspAction);
            Assert.Equal(0, o.ModuleIndex);
            Assert.Empty(r.Exclusions);

            r = Enumerate(Part("solarPanels5", "", Mod(0, "ModuleDeployableSolarPanel", "ModuleDeployablePart")));
            Assert.Empty(r.Cases);
            Assert.Equal(new[] { "no-pose" }, r.Exclusions.Select(e => e.Reason));
        }

        [Fact]
        public void RobotArmScanner_IsADeployablePart_ButExcludedP7Robotics()
        {
            var r = Enumerate(Part("RobotArmScanner.S1", "",
                Mod(0, "ModuleRobotArmScanner", "ModuleDeployablePart", "animationName", "ROCScan1")));
            Assert.Empty(r.Cases);
            Assert.Equal(new[] { "p7-robotics" }, r.Exclusions.Select(e => e.Reason));
        }

        [Fact]
        public void GroundPart_ExcludesItsAnimationGroup_ButKeepsItsLight()
        {
            var r = Enumerate(Part("DeployedSeismicSensor", "",
                Mod(0, "ModuleGroundExperiment", "ModuleGroundSciencePart,ModuleGroundPart,ModuleCargoPart"),
                Mod(1, "ModuleAnimationGroup"), Mod(2, "ModuleLight")));
            Assert.Equal(new[] { "lit/alone" }, r.Cases.Select(Pair));
            var e = Assert.Single(r.Exclusions);
            Assert.Equal("ground-deploy", e.Reason);
            Assert.Equal(1, e.ModuleIndex);
        }

        [Fact]
        public void GearAndLight_OnOnePart_AreTwoCases_NeverTheCrossProduct()
        {
            var r = Enumerate(Part("GearSmall", "",
                Mod(0, "ModuleWheelBase"), Mod(1, "ModuleWheelDeployment"), Mod(2, "ModuleLight")));
            Assert.Equal(new[] { "deployed/alone", "lit/alone" }, r.Cases.Select(Pair));
            Assert.Equal(new[] { "gear", "light" }, r.Cases.Select(k => k.StateOption.Family));
            Assert.Equal(new[] { "EventToggle", "LightsOn" }, r.Cases.Select(k => k.StateOption.Action));
        }

        [Fact]
        public void LadderAndAnimationGroup_MatchByExactName()
        {
            var r = Enumerate(Part("telescopicLadder", "", Mod(0, "RetractableLadder"), Mod(1, "RetractableLadder")));
            Assert.Equal(new[] { "deployed/alone" }, r.Cases.Select(Pair));
            Assert.Equal(0, r.Cases[0].StateOption.ModuleIndex);

            // Every animation group module is a case; two share a word, so both take the tiebreak.
            r = Enumerate(Part("Drill", "", Mod(3, "ModuleAnimationGroup"), Mod(7, "ModuleAnimationGroup")));
            Assert.Equal(new[] { "deployed-m3/alone", "deployed-m7/alone" }, r.Cases.Select(Pair));
            Assert.All(r.Cases, k => Assert.Equal("deployed", k.StateOption.State));
            Assert.All(r.Cases, k => Assert.Equal("DeployModule", k.StateOption.Action));
        }

        [Fact]
        public void CargoBay_PressesItsLinkedAnimateGeneric_AndAFairingOrServiceBayIsExcluded()
        {
            var r = Enumerate(Part("mk2CargoBayS", "",
                Mod(0, "ModuleLiftingSurface"), Mod(1, "ModuleAnimateGeneric", null, "animationName", "Mk2BayS"),
                Mod(2, "ModuleCargoBay", null, "DeployModuleIndex", "1")));
            Assert.Equal(new[] { "open/alone" }, r.Cases.Select(Pair));
            Assert.Equal(1, r.Cases[0].StateOption.ModuleIndex);
            Assert.Equal("Toggle", r.Cases[0].StateOption.Action);
            Assert.Empty(r.Exclusions); // the linked module is the bay's, never "not-polled"

            r = Enumerate(Part("fairingSize1", "", Mod(0, "ModuleProceduralFairing"),
                Mod(1, "ModuleCargoBay", null, "DeployModuleIndex", "0")));
            Assert.Equal(new[] { "stowed/fgv-1-plain-capped", "deployed/fgv-1-plain-capped",
                "stowed/fgv-4-colour-variant", "deployed/fgv-4-colour-variant" }, r.Cases.Select(Pair));
            Assert.Equal(new[] { "fairing-bay" }, r.Exclusions.Select(e => e.Reason));
            r = Enumerate(Part("fairingSize3", "", Mod(0, "ModuleProceduralFairing"),
                Mod(1, "ModuleCargoBay", null, "DeployModuleIndex", "0")));
            Assert.Empty(r.Cases);
            Assert.Equal(new[] { "fairing-bay", "fairing-no-context" }, r.Exclusions.Select(e => e.Reason));

            // DeployModuleIndex absent reads stock's default 0.
            r = Enumerate(Part("ServiceModule18", "top,bottom", Mod(0, "ModuleServiceModule"), Mod(1, "ModuleCargoBay")));
            Assert.Equal(new[] { "bay-deploy-not-animated" }, r.Exclusions.Select(e => e.Reason));
        }

        [Fact]
        public void AnimateGeneric_OnlyOnAPartWithNoDedicatedHandler()
        {
            // Two standalone animate generics share a word: both take the tiebreak.
            var r = Enumerate(Part("doors", "", Mod(2, "ModuleAnimateGeneric", null, "animationName", "a"),
                Mod(5, "ModuleAnimateGeneric")));
            Assert.Equal(new[] { "deployed-m2/alone", "deployed-m5/alone" }, r.Cases.Select(Pair));

            // A thrust reverser on an engine with ModuleAnimateHeat: the heat module owns only its
            // heat clip, so the reverser is a standalone case (todo THRUST-REVERSER-NOT-RECORDED).
            r = Enumerate(Part("JetEngine", "bottom", Mod(0, "ModuleEnginesFX", "ModuleEngines"),
                Mod(1, "ModuleAnimateHeat", "ModuleAnimationSetter"),
                Mod(2, "ModuleAnimateGeneric", null, "animationName", "TF1ThrustReverser")));
            Assert.Equal(new[] { "deployed/alone" }, r.Cases.Where(k => k.StateOption.Family == "animate-generic").Select(Pair));
            Assert.Equal(new[] { "p8-fx", "p8-heat" }, r.Exclusions.Select(e => e.Reason));

            // A dedicated handler still hides every animate generic but a bay's own deploy module.
            r = Enumerate(Part("ladderdoor", "", Mod(0, "RetractableLadder"),
                Mod(1, "ModuleAnimateGeneric", null, "animationName", "a")));
            Assert.Equal(new[] { "not-polled" }, r.Exclusions.Select(e => e.Reason));

            // An explicitly empty animationName is not polled either.
            r = Enumerate(Part("blank", "", Mod(0, "ModuleAnimateGeneric", null, "animationName", "")));
            Assert.Equal(new[] { "no-animation" }, r.Exclusions.Select(e => e.Reason));
        }

        [Fact]
        public void Airbrake_IsAKspAction_AndNeverAControlSurfaceCase()
        {
            var r = Enumerate(Part("airbrake1", "", Mod(0, "ModuleAeroSurface", "ModuleControlSurface,ModuleLiftingSurface")));
            Assert.Equal(new[] { "deployed/alone" }, r.Cases.Select(Pair));
            Assert.Equal("ActionToggleBrakes", r.Cases[0].StateOption.Action);
            Assert.True(r.Cases[0].StateOption.ActionIsKspAction);
            Assert.Empty(r.Exclusions);

            r = Enumerate(Part("canard", "", Mod(0, "ModuleControlSurface", "ModuleLiftingSurface")));
            Assert.Empty(r.Cases);
            Assert.Equal(new[] { "cs-deploy-sign" }, r.Exclusions.Select(e => e.Reason));
        }

        [Fact]
        public void CabinLight_OnlyWithoutAModuleLight_AndOnlyWhenToggleableInFlight()
        {
            var r = Enumerate(Part("mk1pod.v2", "", Mod(0, "ModuleCommand"),
                Mod(1, "ModuleColorChanger", null, "toggleInFlight", "False"),
                Mod(2, "ModuleColorChanger"), Mod(3, "ModuleColorChanger")));
            Assert.Equal(new[] { "lit/alone" }, r.Cases.Select(Pair));
            Assert.Equal("cabin-light", r.Cases[0].StateOption.Family);
            Assert.Equal(2, r.Cases[0].StateOption.ModuleIndex);
            Assert.Equal("ToggleEvent", r.Cases[0].StateOption.Action);

            r = Enumerate(Part("spotLight1", "", Mod(0, "ModuleLight"), Mod(1, "ModuleColorChanger")));
            Assert.Equal(new[] { "light" }, r.Cases.Select(k => k.StateOption.Family));
        }

        [Fact]
        public void Parachute_IsSemiAndFull_InTheAir()
        {
            var r = Enumerate(Part("parachuteSingle", "bottom", Mod(0, "ModuleParachute"), Mod(1, "ModuleDragModifier")));
            Assert.Equal(new[] { "semi/air", "full/air" }, r.Cases.Select(Pair));
            Assert.All(r.Cases, k => Assert.Equal("Deploy", k.StateOption.Action));
        }

        [Fact]
        public void Jettison_FollowsEachModulesOwnNodeConfig()
        {
            // Default node: a stacked stowed case and a stacked jettisoned case, one filler on "bottom".
            var r = Enumerate(Part("liquidEngine2", "top,bottom", Mod(0, "ModuleEnginesFX", "ModuleEngines"),
                Mod(1, "ModuleJettison", null, "jettisonName", "fairing")));
            Assert.Equal(new[] { "stowed/stacked", "jettisoned/stacked" }, r.Cases.Select(Pair));
            Assert.Null(r.Cases[0].StateOption.Action);
            Assert.Equal("Jettison", r.Cases[1].StateOption.Action);
            Assert.All(r.Cases, k => Assert.Equal(new[] { "bottom" }, k.StateOption.NodeNames));

            // ignoreNodes on every module: the shroud shows alone, one jettisoned case.
            r = Enumerate(Part("ServiceModule18", "top,bottom",
                Mod(2, "ModuleJettison", null, "ignoreNodes", "true"), Mod(3, "ModuleJettison", null, "ignoreNodes", "True")));
            Assert.Equal(new[] { "jettisoned/alone" }, r.Cases.Select(Pair));
            Assert.Equal(new[] { 2, 3 }, r.Cases[0].StateOption.ModuleIndices);

            // checkBottomNode: stock refuses with the node occupied, so the stacked stowed case only.
            r = Enumerate(Part("turboFanSize2", "top",
                Mod(4, "ModuleJettison", null, "bottomNodeName", "top", "checkBottomNode", "true")));
            Assert.Equal(new[] { "stowed/stacked" }, r.Cases.Select(Pair));
            Assert.Equal(new[] { "top" }, r.Cases[0].StateOption.NodeNames);
            Assert.Equal(new[] { "jettison-blocked-by-node" }, r.Exclusions.Select(e => e.Reason));

            // A node the prefab lacks excludes the part's stacked cases.
            r = Enumerate(Part("HeatShield0", "bottom,top", Mod(0, "ModuleJettison", null, "bottomNodeName", "bottom02")));
            Assert.Empty(r.Cases);
            Assert.Equal(new[] { "jettison-node-missing" }, r.Exclusions.Select(e => e.Reason));

            // Two modules naming two nodes: one filler per distinct node.
            r = Enumerate(Part("twin", "bottom,bottom02", Mod(0, "ModuleJettison"),
                Mod(1, "ModuleJettison", null, "bottomNodeName", "bottom02"), Mod(2, "ModuleJettison")));
            Assert.Equal(new[] { "bottom", "bottom02" }, r.Cases[0].StateOption.NodeNames);
        }

        [Fact]
        public void Robotics_AndHeat_AndFx_AreNamedExclusions()
        {
            var r = Enumerate(Part("hinge", "", Mod(0, "ModuleRoboticServoHinge", "BaseServo"),
                Mod(1, "ModuleAblator"), Mod(2, "ModuleRCSFX", "ModuleRCS")));
            Assert.Empty(r.Cases);
            Assert.Equal(new[] { "p7-robotics", "p8-heat", "p8-fx" }, r.Exclusions.Select(e => e.Reason));
        }

        [Fact]
        public void StateCases_CarryTheBaseKey_AndTheirIdsHaveTheDocumentedShape()
        {
            var baseKey = new PartParityCaseKey { Part = "longAntenna", Source = "Squad", Skin = "default", SkinOrdinal = 0 };
            var r = PartParityStateRules.Enumerate(Part("longAntenna", "bottom",
                Mod(0, "ModuleDeployableAntenna", "ModuleDeployablePart", "animationName", "antenna")), baseKey);
            PartParityCaseKey k = Assert.Single(r.Cases);
            Assert.Equal("Squad", k.Source);
            Assert.Equal(0, k.SkinOrdinal);
            Assert.StartsWith("longantenna--default--deployed--", TestCommandPartParitySweep.CaseId(k));

            r = Enumerate(Part("parachuteSingle", "", Mod(0, "ModuleParachute")));
            Assert.StartsWith("parachutesingle--default--semi--air--", TestCommandPartParitySweep.CaseId(r.Cases[0]));

            r = Enumerate(Part("liquidEngine2", "bottom", Mod(0, "ModuleJettison")));
            Assert.StartsWith("liquidengine2--default--stowed--stacked--", TestCommandPartParitySweep.CaseId(r.Cases[0]));
            Assert.StartsWith("liquidengine2--default--jettisoned--stacked--", TestCommandPartParitySweep.CaseId(r.Cases[1]));

            // The tiebreak word lands in the id.
            r = Enumerate(Part("Drill", "", Mod(3, "ModuleAnimationGroup"), Mod(7, "ModuleAnimationGroup")));
            Assert.StartsWith("drill--default--deployed-m3--", TestCommandPartParitySweep.CaseId(r.Cases[0]));
        }

        [Fact]
        public void TheStateAndContextWords_AreTheSweepDefaults()
        {
            Assert.Equal(TestCommandPartParitySweep.DefaultState, PartParityStateRules.StateStowed);
            Assert.Equal(TestCommandPartParitySweep.DefaultContext, PartParityStateRules.ContextAlone);
            Assert.Equal(PartParityStateRules.KnownStates.Length, PartParityStateRules.KnownStates.Distinct().Count());
            Assert.Equal(12, PartParityStateRules.Families.Length);
            Assert.All(PartParityStateRules.ExclusionReasons, r => Assert.Equal(r.ToLowerInvariant(), r));
        }

        // ----- the committed fixtures (the D.1 counts) -----

        private static List<PartParityStatePart> LoadFixture(string profile)
        {
            string path = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek.Tests",
                "Fixtures", "PartParity", "state-parts-" + profile + ".txt");
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
                    foreach (string kv in f[4].Split('|'))
                    {
                        int eq = kv.IndexOf('=');
                        m.Config.AddValue(kv.Substring(0, eq), kv.Substring(eq + 1));
                    }
                }
                cur.Modules.Add(m);
            }
            return parts;
        }

        private static PartParityStateEnumeration EnumerateFixture(string profile)
        {
            var all = new PartParityStateEnumeration();
            foreach (var p in LoadFixture(profile)) all.AddRange(Enumerate(p));
            return all;
        }

        private static Dictionary<string, int> ByFamily(PartParityStateEnumeration r)
            => r.Cases.GroupBy(k => k.StateOption.Family).ToDictionary(g => g.Key, g => g.Count());

        private static Dictionary<string, int> ByReason(PartParityStateEnumeration r)
            => r.Exclusions.GroupBy(e => e.Reason).ToDictionary(g => g.Key, g => g.Count());

        [Fact]
        public void StockMinimal_EnumeratesTheD1Counts()
        {
            var r = EnumerateFixture("stock-minimal");
            var expected = new Dictionary<string, int>
            {
                ["deployable"] = 19, ["gear"] = 8, ["ladder"] = 2, ["animation-group"] = 5,
                ["cargo-bay"] = 8, ["animate-generic"] = 12, ["airbrake"] = 1, ["light"] = 15,
                ["cabin-light"] = 26, ["parachute"] = 10, ["jettison"] = 66 + 4, ["fairing"] = 9,
            };
            Assert.Equal(expected.OrderBy(p => p.Key), ByFamily(r).OrderBy(p => p.Key));
            Assert.Equal(96, r.Cases.Count(k => k.StateOption.Family != "parachute" && k.StateOption.Family != "jettison"
                && k.StateOption.Fgv == null));
            AssertFgvCases(r);
            Assert.Equal(37, r.Cases.Where(k => k.StateOption.Family == "jettison").Select(k => k.Part).Distinct().Count());
            Dictionary<string, int> reasons = ByReason(r);
            Assert.Equal(9, reasons["ground-deploy"]);
            Assert.Equal(2, reasons["no-pose"]);
            Assert.Equal(5, reasons["fairing-bay"]);
            Assert.Equal(3, reasons["fairing-no-context"]);
            Assert.Equal(3, reasons["bay-deploy-not-animated"]);
            Assert.Equal(24, reasons["cs-deploy-sign"]);
            Assert.Equal(5, reasons["jettison-blocked-by-node"]);
            Assert.False(reasons.ContainsKey("not-polled")); // the two thrust reversers are cases now
            Assert.False(reasons.ContainsKey("jettison-node-missing"));
            AssertIdsUnique(r);
        }

        [Fact]
        public void ModdedCompat_EnumeratesTheD1Counts()
        {
            var r = EnumerateFixture("modded-compat");
            var expected = new Dictionary<string, int>
            {
                ["deployable"] = 22, ["gear"] = 9, ["ladder"] = 2, ["animation-group"] = 4,
                ["cargo-bay"] = 9, ["animate-generic"] = 17, ["airbrake"] = 1, ["light"] = 18,
                ["cabin-light"] = 37, ["parachute"] = 10, ["jettison"] = 108 + 4, ["fairing"] = 9,
            };
            Assert.Equal(expected.OrderBy(p => p.Key), ByFamily(r).OrderBy(p => p.Key));
            Assert.Equal(119, r.Cases.Count(k => k.StateOption.Family != "parachute" && k.StateOption.Family != "jettison"
                && k.StateOption.Fgv == null));
            AssertFgvCases(r);
            Assert.Equal(60, r.Cases.Where(k => k.StateOption.Family == "jettison").Select(k => k.Part).Distinct().Count());
            Dictionary<string, int> reasons = ByReason(r);
            Assert.Equal(9, reasons["ground-deploy"]);
            Assert.Equal(6, reasons["fairing-no-context"]);
            Assert.Equal(5, reasons["bay-deploy-not-animated"]);
            Assert.Equal(7, reasons["jettison-blocked-by-node"]);
            Assert.False(reasons.ContainsKey("not-polled")); // the two thrust reversers are cases now
            Assert.False(reasons.ContainsKey("jettison-node-missing"));
            AssertIdsUnique(r);
        }

        /// <summary>FGV's six configurations as 13 cases (D.10 S8), the same on both installs:
        /// each context's subject, family, states and press.</summary>
        private static void AssertFgvCases(PartParityStateEnumeration r)
        {
            var fgv = r.Cases.Where(k => k.StateOption.Fgv != null).ToList();
            Assert.Equal(new[]
            {
                "fairingSize1 stowed/fgv-1-plain-capped -", "fairingSize1 deployed/fgv-1-plain-capped DeployFairing",
                "fairingSize1 stowed/fgv-4-colour-variant -", "fairingSize1 deployed/fgv-4-colour-variant DeployFairing",
                "fairingSize2 stowed/fgv-2-interstage-truss -", "fairingSize2 deployed/fgv-2-interstage-truss DeployFairing",
                "fairingSize2 separated/fgv-2-interstage-truss DeployFairing+Separate",
                "fairingSize2 stowed/fgv-3-open-top -", "fairingSize2 deployed/fgv-3-open-top DeployFairing",
                "EnginePlate2 stowed/fgv-5-engine-plate -", "EnginePlate2 separated/fgv-5-engine-plate Separate",
                "liquidEngine2.v2 stowed/fgv-6-engine-shroud -", "liquidEngine2.v2 separated/fgv-6-engine-shroud Separate",
            }.OrderBy(x => x, StringComparer.Ordinal),
                fgv.Select(k => k.Part + " " + Pair(k) + " " + (k.StateOption.Action == null ? "-"
                    : (k.StateOption.PreAction != null ? k.StateOption.PreAction + "+" : "") + k.StateOption.Action))
                    .OrderBy(x => x, StringComparer.Ordinal));
            Assert.All(fgv.Where(k => k.Context.StartsWith("fgv-5", StringComparison.Ordinal)
                    || k.Context.StartsWith("fgv-6", StringComparison.Ordinal)),
                k => Assert.Equal("jettison", k.StateOption.Family));
            Assert.Equal(13, fgv.Count);
        }

        [Fact]
        public void FgvContexts_AreTheSixConfigurationsInFgvOrder_AndSelectByTheirClass()
        {
            Assert.Equal(new[]
            {
                "fgv-1-plain-capped", "fgv-2-interstage-truss", "fgv-3-open-top", "fgv-4-colour-variant",
                "fgv-5-engine-plate", "fgv-6-engine-shroud",
            }, PartParityStateRules.FairingContexts);
            Assert.Equal(PartParityStateRules.FairingContexts, PartParityStateRules.FgvContextTable.Select(c => c.Id));
            for (int i = 0; i < PartParityStateRules.FgvContextTable.Length; i++)
                Assert.Equal(i + 1, PartParityStateRules.FgvContextTable[i].Number);
            Assert.Equal("fgv", PartParityStateRules.ContextClass("fgv-3-open-top"));
            Assert.Equal("stacked", PartParityStateRules.ContextClass("stacked"));
            Assert.Null(PartParityStateRules.ContextClass(null));
            Assert.Same(PartParityStateRules.FgvContextTable[4], PartParityStateRules.FgvContext("fgv-5-engine-plate"));
            Assert.Null(PartParityStateRules.FgvContext("stacked"));
            Assert.Contains("fgv", PartParityStateRules.OrbitContexts);
            Assert.Contains("separated", PartParityStateRules.KnownStates);
        }

        [Fact]
        public void FgvSeparation_IsReachedWhenThePartLeaves_AndThrownTransformsFollowTheState()
        {
            var sep = new PartParityStateOption { Family = "jettison", State = "separated", Action = "Separate" };
            Assert.False(PartParityStateRules.StateReached(sep, new PartParityStateObservation()));
            Assert.False(PartParityStateRules.StateReached(sep, new PartParityStateObservation { SeparatedPartGone = false }));
            // The jettison modules' own done signal does not stand in for the separation.
            Assert.False(PartParityStateRules.StateReached(sep,
                new PartParityStateObservation { JettisonModules = 1, JettisonedModules = 1 }));
            Assert.True(PartParityStateRules.StateReached(sep, new PartParityStateObservation { SeparatedPartGone = true }));
            Assert.Equal(PartParityStateBucket.Quick, PartParityStateRules.BucketFor("fairing"));

            Assert.True(PartParityStateRules.ThrowsJettisonTransforms(new PartParityStateOption { Family = "jettison", State = "jettisoned" }));
            Assert.True(PartParityStateRules.ThrowsJettisonTransforms(sep));
            Assert.True(PartParityStateRules.ThrowsJettisonTransforms(new PartParityStateOption { Family = "fairing", State = "deployed" }));
            Assert.False(PartParityStateRules.ThrowsJettisonTransforms(new PartParityStateOption { Family = "fairing", State = "stowed" }));
            Assert.False(PartParityStateRules.ThrowsJettisonTransforms(new PartParityStateOption { Family = "deployable", State = "deployed" }));
            Assert.False(PartParityStateRules.ThrowsJettisonTransforms(new PartParityStateOption { Family = "jettison", State = "stowed" }));
            Assert.False(PartParityStateRules.ThrowsJettisonTransforms(null));

            // A deployed fairing ghost reads its shell's absence or hiding (the driver's On).
            var dep = new PartParityStateOption { Family = "fairing", State = "deployed", Action = "DeployFairing" };
            Assert.Equal("applied", PartParityStateRules.GhostStateReached(dep, new PartParityGhostStateInput { HasInfo = true, On = true }));
            Assert.Equal("not-reached", PartParityStateRules.GhostStateReached(dep, new PartParityGhostStateInput { HasInfo = true, On = false }));
            Assert.Equal("applied", PartParityStateRules.GhostStateReached(
                new PartParityStateOption { Family = "fairing", State = "stowed" }, new PartParityGhostStateInput { HasInfo = true }));
        }

        private static void AssertIdsUnique(PartParityStateEnumeration r)
        {
            var ids = r.Cases.Select(TestCommandPartParitySweep.CaseId).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(ids, id => Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id), id));
        }

        [Fact]
        public void FormatCounts_NamesFamiliesAndReasonsInTableOrder()
        {
            var r = Enumerate(Part("GearSmall", "", Mod(0, "ModuleWheelDeployment"), Mod(1, "ModuleLight"),
                Mod(2, "ModuleControlSurface")));
            string line = PartParityStateRules.FormatCounts("all", "none", r.Cases, 1, r.Exclusions);
            Assert.Equal("state cases: states=all contexts=none install=2 (gear=1 light=1) planned=1 excluded=1 (cs-deploy-sign=1)", line);
        }

        // ----- done signals, waits -----

        private static PartParityStateOption Opt(string family, string state = "deployed", string action = "x")
            => new PartParityStateOption { Family = family, State = state, Action = action };

        [Fact]
        public void StateReached_ReadsEachFamilysStockField()
        {
            Assert.True(PartParityStateRules.StateReached(Opt("deployable"), new PartParityStateObservation { DeployState = "EXTENDED" }));
            Assert.False(PartParityStateRules.StateReached(Opt("deployable"), new PartParityStateObservation { DeployState = "EXTENDING" }));
            Assert.True(PartParityStateRules.StateReached(Opt("gear"), new PartParityStateObservation { GearState = "Deployed" }));
            Assert.False(PartParityStateRules.StateReached(Opt("gear"), new PartParityStateObservation { GearState = "Deploying" }));
            foreach (string f in new[] { "ladder", "animation-group", "animate-generic", "airbrake" })
            {
                Assert.True(PartParityStateRules.StateReached(Opt(f), new PartParityStateObservation { ClassifierDeployed = true }), f);
                Assert.False(PartParityStateRules.StateReached(Opt(f), new PartParityStateObservation()), f);
            }
            // closedPosition 1: open at animTime 0.
            Assert.True(PartParityStateRules.StateReached(Opt("cargo-bay", "open"),
                new PartParityStateObservation { CargoAnimTime = 0f, CargoClosedPosition = 1f }));
            Assert.False(PartParityStateRules.StateReached(Opt("cargo-bay", "open"),
                new PartParityStateObservation { CargoAnimTime = 0.5f, CargoClosedPosition = 1f }));
            Assert.True(PartParityStateRules.StateReached(Opt("light", "lit"), new PartParityStateObservation { LightOn = true }));
            Assert.True(PartParityStateRules.StateReached(Opt("parachute", "semi"),
                new PartParityStateObservation { ParachuteState = "SEMIDEPLOYED", AnimationPlaying = false }));
            Assert.False(PartParityStateRules.StateReached(Opt("parachute", "semi"),
                new PartParityStateObservation { ParachuteState = "SEMIDEPLOYED", AnimationPlaying = true }));
            Assert.False(PartParityStateRules.StateReached(Opt("parachute", "full"),
                new PartParityStateObservation { ParachuteState = "SEMIDEPLOYED", AnimationPlaying = false }));
            Assert.True(PartParityStateRules.StateReached(Opt("jettison", "jettisoned"),
                new PartParityStateObservation { JettisonModules = 2, JettisonedModules = 2 }));
            Assert.False(PartParityStateRules.StateReached(Opt("jettison", "jettisoned"),
                new PartParityStateObservation { JettisonModules = 2, JettisonedModules = 1 }));
            Assert.True(PartParityStateRules.StateReached(Opt("fairing"), new PartParityStateObservation { FairingScalar = 0.5f }));
            // A context case presses nothing: reached at once.
            Assert.True(PartParityStateRules.StateReached(Opt("jettison", "stowed", null), null));
        }

        [Fact]
        public void Timeouts_FollowTheBucket()
        {
            Assert.Equal(20.0, PartParityStateRules.StateTimeoutSeconds(Opt("deployable"), double.NaN, 1.0));
            Assert.Equal(8.0, PartParityStateRules.StateTimeoutSeconds(Opt("deployable"), 3.0, 1.0));
            Assert.Equal(20.0, PartParityStateRules.StateTimeoutSeconds(Opt("deployable"), 30.0, -1.0));
            Assert.Equal(5.0, PartParityStateRules.StateTimeoutSeconds(Opt("light", "lit"), 3.0, 1.0));
            Assert.Equal(5.0, PartParityStateRules.StateTimeoutSeconds(Opt("airbrake"), 3.0, 1.0));
            Assert.Equal(10.0, PartParityStateRules.StateTimeoutSeconds(Opt("parachute", "semi"), 3.0, 1.0));
            Assert.Equal(15.0, PartParityStateRules.StateTimeoutSeconds(Opt("parachute", "full"), 3.0, 1.0));
            Assert.Equal(1.0, PartParityStateRules.PostDoneSettleSeconds("light", 1.0));
            Assert.Equal(2.5, PartParityStateRules.PostDoneSettleSeconds("cabin-light", 0.5));
            Assert.Equal(5.0, PartParityStateRules.PostDoneSettleSeconds("cabin-light", 0.1));
            Assert.Equal(0.5, PartParityStateRules.PostDoneSettleSeconds("deployable", 1.0));
            Assert.True(PartParityStateRules.SunTrackerSettled(0.05));
            Assert.False(PartParityStateRules.SunTrackerSettled(0.2));
            Assert.True(PartParityStateRules.AirbrakeSettled(0.01));
            Assert.False(PartParityStateRules.AirbrakeSettled(0.05));
            Assert.True(PartParityStateRules.CanopyCaptureReady(1.0, 1.5));
            Assert.False(PartParityStateRules.CanopyCaptureReady(2.0, 1.5));
        }

        [Fact]
        public void WaveWorstCase_IsTheBucketsBound()
        {
            Assert.Equal(71.7, PartParitySweepPlan.WaveWorstCaseSeconds(PartParityStateBucket.Long, 4, true), 6);
            Assert.Equal(46.7, PartParitySweepPlan.WaveWorstCaseSeconds(PartParityStateBucket.Long, 4, false,
                new[] { "deployable", "gear" }), 6);
            Assert.Equal(31.7, PartParitySweepPlan.WaveWorstCaseSeconds(PartParityStateBucket.Quick, 4, false,
                new[] { "jettison" }), 6);
            Assert.Equal(32.2, PartParitySweepPlan.WaveWorstCaseSeconds(PartParityStateBucket.Quick, 4, false,
                new[] { "light", "light" }), 6);
            // The airbrake's and the cabin light's 5 s settle caps bound a quick wave holding one.
            Assert.Equal(36.2, PartParitySweepPlan.WaveWorstCaseSeconds(PartParityStateBucket.Quick, 4, false,
                new[] { "light", "airbrake" }), 6);
            Assert.Equal(36.2, PartParitySweepPlan.WaveWorstCaseSeconds(PartParityStateBucket.Quick, 4, false,
                new[] { "cabin-light" }), 6);
            Assert.Equal(60.0, PartParitySweepPlan.WaveWorstCaseSeconds(PartParityStateBucket.Air, 5, false,
                new[] { "parachute" }), 6);
            Assert.Equal(5.0, PartParityStateRules.SettleCapSeconds("airbrake"));
            Assert.Equal(5.0, PartParityStateRules.SettleCapSeconds("cabin-light"));
            Assert.True(PartParitySweepPlan.ShouldStartStateWave(0.0, 480, 1000.0));
            Assert.True(PartParitySweepPlan.ShouldStartStateWave(400.0, 480, 56.7));
            Assert.False(PartParitySweepPlan.ShouldStartStateWave(430.0, 480, 56.7));
        }

        // ----- the ghost side -----

        [Fact]
        public void GhostStateReached_ReadsTheGhostsOwnPlaybackState()
        {
            var o = Opt("deployable");
            Assert.Equal("no-info", PartParityStateRules.GhostStateReached(o, new PartParityGhostStateInput()));
            Assert.Equal("pose-not-sampled", PartParityStateRules.GhostStateReached(o,
                new PartParityGhostStateInput { HasInfo = true, PoseSampled = false }));
            Assert.Equal("applied", PartParityStateRules.GhostStateReached(o,
                new PartParityGhostStateInput { HasInfo = true, DeployFraction = 1f, CurrentDeployed = true, TransitionActive = false }));
            Assert.Equal("not-reached", PartParityStateRules.GhostStateReached(o,
                new PartParityGhostStateInput { HasInfo = true, DeployFraction = 0.6f, CurrentDeployed = true }));
            Assert.Equal("not-reached", PartParityStateRules.GhostStateReached(o,
                new PartParityGhostStateInput { HasInfo = true, DeployFraction = 1f, CurrentDeployed = true, TransitionActive = true }));
            Assert.Equal("applied", PartParityStateRules.GhostStateReached(Opt("parachute", "semi"),
                new PartParityGhostStateInput { HasInfo = true, ParachuteApplied = "semi" }));
            Assert.Equal("not-reached", PartParityStateRules.GhostStateReached(Opt("parachute", "full"),
                new PartParityGhostStateInput { HasInfo = true, ParachuteApplied = "semi" }));
            Assert.Equal("applied", PartParityStateRules.GhostStateReached(Opt("light", "lit"),
                new PartParityGhostStateInput { HasInfo = true, On = true }));
            Assert.Equal("applied", PartParityStateRules.GhostStateReached(Opt("jettison", "jettisoned"),
                new PartParityGhostStateInput { HasInfo = true, JettisonTransforms = 2, JettisonHidden = 2 }));
            Assert.Equal("not-reached", PartParityStateRules.GhostStateReached(Opt("jettison", "jettisoned"),
                new PartParityGhostStateInput { HasInfo = true, JettisonTransforms = 0, JettisonHidden = 0 }));
            Assert.Null(PartParityStateRules.StateNotModelledFlag("event", "applied"));
            Assert.Equal("state-not-modelled:start:no-info", PartParityStateRules.StateNotModelledFlag("start", "no-info"));
        }

        // ----- the poller predicates (the same predicate the recorder uses) -----

        private static string RecordingDir()
            => Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Recording");

        /// <summary>C# source with every comment removed (string and char literals kept), so a
        /// predicate quoted in a comment can never satisfy a cell.</summary>
        internal static string StripComments(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                char n = i + 1 < text.Length ? text[i + 1] : '\0';
                if (c == '/' && n == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
                if (c == '/' && n == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    int j = i + 1;
                    while (j < text.Length && text[j] != c && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                    j = Math.Min(j + 1, text.Length);
                    sb.Append(text, i, j - i);
                    i = j;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>The body (outer braces included) of the method whose declaration starts
        /// with <paramref name="signature"/> in comment-stripped <paramref name="text"/>; braces
        /// inside string literals do not count. Null when the method is absent.</summary>
        internal static string MethodBody(string text, string signature)
        {
            int at = text.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return null;
            int open = text.IndexOf('{', at);
            int depth = 0;
            bool inString = false;
            char quote = '\0';
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) inString = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inString = true; quote = c; continue; }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return text.Substring(open, i - open + 1);
            }
            return null;
        }

        private static string PollerBody(string poller)
        {
            string text = StripComments(File.ReadAllText(Path.Combine(RecordingDir(), "PartStatePoller.cs")));
            string body = MethodBody(text, "internal static void " + poller + "(");
            Assert.True(body != null, "PartStatePoller." + poller + " moved; this cell is vacuous");
            return body;
        }

        /// <summary>The recorder's light check: <c>PartStatePoller.PollLights</c> once the active
        /// recorder's body moves there (D.10 S2), else <c>FlightRecorder.CheckLightState</c>.</summary>
        private static string LightCheckBody()
        {
            string poller = StripComments(File.ReadAllText(Path.Combine(RecordingDir(), "PartStatePoller.cs")));
            string body = MethodBody(poller, "internal static void PollLights(");
            if (body != null) return body;
            string recorder = StripComments(File.ReadAllText(Path.Combine(RecordingDir(), "FlightRecorder.PartModuleStates.cs")));
            body = MethodBody(recorder, "private void CheckLightState(");
            Assert.True(body != null, "the recorder's light check moved; this cell is vacuous");
            return body;
        }

        /// <summary>
        /// Each family's predicate, asserted inside the body of the poller that owns it (comments
        /// stripped), together with the first-per-part rule where the family depends on it: a
        /// poller that keeps only its first module ends its module loop with <c>break;</c>, one
        /// that polls every module has none. A changed predicate or a moved break reds here.
        /// </summary>
        public static IEnumerable<object[]> PollerPredicates()
        {
            yield return new object[] { "PollDeployables", new[] { "p.FindModuleImplementing<ModuleDeployablePart>()",
                "PartStateSeeder.DeployableHasNoPoseAnimation(deployable.animationName)" }, null };
            yield return new object[] { "PollGear", new[] { "p.Modules[m] as ModuleWheels.ModuleWheelDeployment" }, true };
            yield return new object[] { "PollLadders", new[] {
                "string.Equals(module.moduleName, \"RetractableLadder\", StringComparison.Ordinal)" }, true };
            yield return new object[] { "PollAnimationGroups", new[] {
                "string.Equals(module.moduleName, \"ModuleAnimationGroup\", StringComparison.Ordinal)" }, false };
            yield return new object[] { "PollCargoBays", new[] { "p.FindModuleImplementing<ModuleCargoBay>()",
                "int deployIdx = cargo.DeployModuleIndex;", "p.Modules[deployIdx] as ModuleAnimateGeneric" }, null };
            yield return new object[] { "PollAnimateGeneric", new[] { "if (FlightRecorder.HasDedicatedAnimateHandler(p)) continue;",
                "p.Modules[m] as ModuleAnimateGeneric", "if (string.IsNullOrEmpty(animateModule.animationName)) continue;" }, false };
            yield return new object[] { "PollAeroSurfaces", new[] {
                "string.Equals(module.moduleName, \"ModuleAeroSurface\", StringComparison.Ordinal)" }, false };
            yield return new object[] { "PollParachutes", new[] { "p.FindModuleImplementing<ModuleParachute>()" }, null };
            yield return new object[] { "PollJettisons", new[] { "p.Modules[m] as ModuleJettison" }, null };
            yield return new object[] { "PollFairings", new[] { "p.FindModuleImplementing<ModuleProceduralFairing>()" }, null };
        }

        [Theory]
        [MemberData(nameof(PollerPredicates))]
        public void EveryFamilyPredicate_IsTheRecordersOwn(string poller, string[] tokens, bool? firstPerPart)
        {
            string body = PollerBody(poller);
            foreach (string token in tokens)
                Assert.True(body.Contains(token), poller + " no longer carries the predicate: " + token);
            if (firstPerPart == true)
                Assert.True(body.Contains("break;"), poller + " no longer keeps only the first module of a part");
            if (firstPerPart == false)
                Assert.False(body.Contains("break;"), poller + " now stops at a part's first module");
        }

        [Fact]
        public void TheLightFamilies_AreTheRecordersLightCheck()
        {
            string body = LightCheckBody();
            foreach (string token in new[]
            {
                "p.FindModuleImplementing<ModuleLight>()", "if (!hasModuleLight)", "as ModuleColorChanger",
                "if (!cc.toggleInFlight) continue;",
            })
                Assert.True(body.Contains(token), "the light check no longer carries: " + token);
            // The cabin light is the FIRST toggleable colour changer: the loop breaks after it.
            string cabin = body.Substring(body.IndexOf("if (!hasModuleLight)", StringComparison.Ordinal));
            Assert.True(cabin.Contains("break;"), "the cabin-light loop no longer stops at its first module");
        }

        [Fact]
        public void TheDedicatedHandlerMirror_IsTheRecordersList()
        {
            string text = string.Join("\n", Directory.GetFiles(RecordingDir(), "FlightRecorder*.cs")
                .Select(f => StripComments(File.ReadAllText(f))));
            string body = MethodBody(text, "internal static bool HasDedicatedAnimateHandler(Part p)");
            Assert.True(body != null, "FlightRecorder.HasDedicatedAnimateHandler moved; this cell is vacuous");
            var types = System.Text.RegularExpressions.Regex.Matches(body, @"FindModuleImplementing<(?:\w+\.)*(\w+)>")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToArray();
            var names = System.Text.RegularExpressions.Regex.Matches(body, @"moduleName, ""(\w+)""")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(PartParityStateRules.DedicatedHandlerTypes, types);
            Assert.Equal(PartParityStateRules.DedicatedHandlerModuleNames, names);
        }
    }
}
