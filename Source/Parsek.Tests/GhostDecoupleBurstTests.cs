using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Y4 (design-part-ghost-parity.md G.7, G.8 item 4): which decoupler, if any, a recorded joint
    /// break fired, read from the ghost's snapshot, and the guards around the burst.
    /// </summary>
    [Collection("Sequential")]
    public class GhostDecoupleBurstTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostDecoupleBurstTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- snapshot authoring -----

        private sealed class P
        {
            public string Name;
            public uint Pid;
            public int Parent;
            public string[] Att = new string[0];
            public string Srf;
            public string[] Modules = new string[0];
            public bool Decoupled;
        }

        private static GhostDecoupleTopology ParseSnap(ConfigNode snapshot)
            => GhostDecoupleTopology.Parse(snapshot, GhostVisualBuilder.TryExtractPartName);

        private static ConfigNode Snapshot(params P[] parts)
        {
            var v = new ConfigNode("VESSEL");
            foreach (P p in parts)
            {
                ConfigNode n = v.AddNode("PART");
                n.AddValue("name", p.Name);
                n.AddValue("persistentId", p.Pid.ToString());
                n.AddValue("parent", p.Parent.ToString());
                n.AddValue("srfN", p.Srf ?? "srfAttach, -1");
                foreach (string a in p.Att) n.AddValue("attN", a);
                foreach (string m in p.Modules)
                {
                    ConfigNode mn = n.AddNode("MODULE");
                    mn.AddValue("name", m);
                    if (m.Contains("Decouple"))
                        mn.AddValue("isDecoupled", p.Decoupled ? "True" : "False");
                }
            }
            return v;
        }

        private static GhostDecouplerModuleFacts Mod(int index, string node, bool omni = false,
            string name = "ModuleDecouple", string group = "decouple")
            => new GhostDecouplerModuleFacts
            {
                ModuleIndex = index, ModuleName = name, ExplosiveNodeId = node, IsOmni = omni, FxGroupName = group,
            };

        private static Func<string, List<GhostDecouplerModuleFacts>> Modules(
            params KeyValuePair<string, GhostDecouplerModuleFacts>[] map)
        {
            return name =>
            {
                var list = new List<GhostDecouplerModuleFacts>();
                foreach (var kv in map) if (kv.Key == name) list.Add(kv.Value);
                return list;
            };
        }

        private static KeyValuePair<string, GhostDecouplerModuleFacts> On(string part, GhostDecouplerModuleFacts m)
            => new KeyValuePair<string, GhostDecouplerModuleFacts>(part, m);

        // tank (root) / Decoupler.1 hung below it by its top node / tank below the decoupler.
        private static GhostDecoupleTopology Stack(bool decoupled = false)
            => ParseSnap(Snapshot(
                new P { Name = "fuelTank", Pid = 100, Parent = 0, Att = new[] { "top, -1", "bottom, 1" } },
                new P { Name = "Decoupler.1", Pid = 200, Parent = 0, Att = new[] { "top, 0", "bottom, 2" },
                    Modules = new[] { "ModuleToggleCrossfeed", "ModuleDecouple" }, Decoupled = decoupled },
                new P { Name = "fuelTank", Pid = 300, Parent = 1, Att = new[] { "top, 1", "bottom, -1" } }));

        // ----- topology -----

        [Fact]
        public void Parse_NullSnapshot_IsNull()
        {
            Assert.Null(ParseSnap(null));
        }

        [Fact]
        public void Topology_ReadsParentsAttachNodesAndNames()
        {
            GhostDecoupleTopology t = ParseSnap(Snapshot(
                new P { Name = "mk1pod.v2_4012345678", Pid = 11, Parent = 0, Att = new[] { "bottom, 1, 0|0|0, 0|-1|0" } },
                new P { Name = "radialDecoupler", Pid = 22, Parent = 0, Srf = "srfAttach, 0,CylCOL" },
                new P { Name = "fuelTank", Pid = 33, Parent = 1, Att = new[] { "top, 1", "bottom, -1" } }));
            Assert.Equal(3, t.PartCount);
            Assert.Equal(0u, t.ParentPid(11));
            Assert.Equal(11u, t.ParentPid(22));
            Assert.Equal(22u, t.ParentPid(33));
            Assert.Equal(0u, t.ParentPid(999));
            Assert.Equal(22u, t.AttachedPid(11, "bottom"));
            Assert.Equal(11u, t.AttachedPid(22, "srf"));
            Assert.Equal(22u, t.AttachedPid(33, "top"));
            Assert.Equal(0u, t.AttachedPid(33, "bottom"));
            Assert.Equal(0u, t.AttachedPid(33, "side"));
            Assert.Equal(0u, t.AttachedPid(33, null));
            Assert.Equal("mk1pod.v2", t.PartName(11));
            Assert.Null(t.PartName(999));
            Assert.True(t.Contains(22));
            Assert.False(t.Contains(0));
        }

        [Fact]
        public void ModuleIsDecoupled_ReadsTheModuleAtItsIndex_ElseTheFirstOfItsName()
        {
            GhostDecoupleTopology t = Stack(decoupled: true);
            Assert.True(t.ModuleIsDecoupled(200, 1, "ModuleDecouple"));
            Assert.True(t.ModuleIsDecoupled(200, 7, "ModuleDecouple"));   // index off: by name
            Assert.False(t.ModuleIsDecoupled(200, 0, "ModuleAnchoredDecoupler"));
            Assert.False(Stack().ModuleIsDecoupled(200, 1, "ModuleDecouple"));
            Assert.False(t.ModuleIsDecoupled(100, 0, "ModuleDecouple"));
        }

        // ----- the decision -----

        [Fact]
        public void StackDecoupler_BelowItsStage_FiresOnItsOwnJoint()
        {
            GhostDecoupleBurstChoice c = GhostDecoupleBurst.ResolveFiredDecoupler(
                Stack(), 200, Modules(On("Decoupler.1", Mod(1, "top"))));
            Assert.True(c.Fires);
            Assert.Equal(GhostDecoupleBurstReason.ChildDecouplerFired, c.Reason);
            Assert.Equal(200u, c.DecouplerPid);
            Assert.Equal(100u, c.ParentPid);
            Assert.Equal("Decoupler.1", c.DecouplerPartName);
            Assert.Equal("decouple", c.Module.FxGroupName);
        }

        [Fact]
        public void StackDecoupler_ExplosiveNodeOnTheOtherSide_DoesNotFireForThatJoint()
        {
            // The tank under the decoupler broke off while the explosive node holds the tank above.
            GhostDecoupleBurstChoice c = GhostDecoupleBurst.ResolveFiredDecoupler(
                Stack(), 300, Modules(On("Decoupler.1", Mod(1, "top"))));
            Assert.False(c.Fires);
            Assert.Equal(GhostDecoupleBurstReason.DecouplerFacesElsewhere, c.Reason);
            Assert.Equal(200u, c.ParentPid);
        }

        [Fact]
        public void ParentDecoupler_WhoseExplosiveNodeHoldsTheChild_Fires()
        {
            // Mirror of the case above: the explosive node is the bottom one.
            GhostDecoupleBurstChoice c = GhostDecoupleBurst.ResolveFiredDecoupler(
                Stack(), 300, Modules(On("Decoupler.1", Mod(1, "bottom"))));
            Assert.True(c.Fires);
            Assert.Equal(GhostDecoupleBurstReason.ParentDecouplerFired, c.Reason);
            Assert.Equal(200u, c.DecouplerPid);
            // and the decoupler's own joint with the tank above is then not a decouple
            c = GhostDecoupleBurst.ResolveFiredDecoupler(Stack(), 200, Modules(On("Decoupler.1", Mod(1, "bottom"))));
            Assert.False(c.Fires);
            Assert.Equal(GhostDecoupleBurstReason.DecouplerFacesElsewhere, c.Reason);
        }

        [Fact]
        public void PlainJointBreak_DrawsNothing()
        {
            GhostDecoupleTopology t = ParseSnap(Snapshot(
                new P { Name = "fuelTank", Pid = 1, Parent = 0, Att = new[] { "bottom, 1" } },
                new P { Name = "trussPiece1x", Pid = 2, Parent = 0, Att = new[] { "top, 0" } }));
            GhostDecoupleBurstChoice c = GhostDecoupleBurst.ResolveFiredDecoupler(
                t, 2, Modules(On("Decoupler.1", Mod(1, "top"))));
            Assert.False(c.Fires);
            Assert.Equal(GhostDecoupleBurstReason.PlainJointBreak, c.Reason);
            Assert.Equal(0u, c.DecouplerPid);
            Assert.Equal(1u, c.ParentPid);
        }

        [Fact]
        public void RadialDecoupler_OnItsSurfaceNode_Fires()
        {
            GhostDecoupleTopology t = ParseSnap(Snapshot(
                new P { Name = "fuelTank", Pid = 1, Parent = 0 },
                new P { Name = "radialDecoupler", Pid = 2, Parent = 0, Srf = "srfAttach, 0,COL",
                    Modules = new[] { "ModuleAnchoredDecoupler" } },
                new P { Name = "solidBooster.sm.v2", Pid = 3, Parent = 1, Srf = "srfAttach, 1,collider" }));
            var mods = Modules(On("radialDecoupler", Mod(0, "srf", name: "ModuleAnchoredDecoupler")));
            GhostDecoupleBurstChoice c = GhostDecoupleBurst.ResolveFiredDecoupler(t, 2, mods);
            Assert.True(c.Fires);
            Assert.Equal(GhostDecoupleBurstReason.ChildDecouplerFired, c.Reason);
            Assert.Equal(2u, c.DecouplerPid);
            // the booster coming off the decoupler is not this decoupler's surface joint
            c = GhostDecoupleBurst.ResolveFiredDecoupler(t, 3, mods);
            Assert.False(c.Fires);
            Assert.Equal(GhostDecoupleBurstReason.DecouplerFacesElsewhere, c.Reason);
        }

        [Fact]
        public void OmniDecoupler_FiresOnEveryJointOfIt()
        {
            var mods = Modules(On("Decoupler.1", Mod(1, "top", omni: true)));
            GhostDecoupleBurstChoice up = GhostDecoupleBurst.ResolveFiredDecoupler(Stack(), 200, mods);
            GhostDecoupleBurstChoice down = GhostDecoupleBurst.ResolveFiredDecoupler(Stack(), 300, mods);
            Assert.Equal(GhostDecoupleBurstReason.ChildDecouplerFired, up.Reason);
            Assert.Equal(GhostDecoupleBurstReason.ParentDecouplerFired, down.Reason);
            Assert.Equal(200u, up.DecouplerPid);
            Assert.Equal(200u, down.DecouplerPid);
        }

        [Fact]
        public void DecouplerThatHadAlreadyFired_DrawsNothing()
        {
            GhostDecoupleBurstChoice c = GhostDecoupleBurst.ResolveFiredDecoupler(
                Stack(decoupled: true), 200, Modules(On("Decoupler.1", Mod(1, "top", omni: true))));
            Assert.False(c.Fires);
            Assert.Equal(GhostDecoupleBurstReason.DecouplerAlreadyFired, c.Reason);
        }

        [Fact]
        public void RootDecoupler_WithItsChildOnTheExplosiveNode_Fires()
        {
            GhostDecoupleTopology t = ParseSnap(Snapshot(
                new P { Name = "Decoupler.1", Pid = 7, Parent = 0, Att = new[] { "top, -1", "bottom, 1" },
                    Modules = new[] { "ModuleDecouple" } },
                new P { Name = "fuelTank", Pid = 8, Parent = 0, Att = new[] { "top, 0" } }));
            var mods = Modules(On("Decoupler.1", Mod(0, "bottom")));
            Assert.Equal(GhostDecoupleBurstReason.ParentDecouplerFired,
                GhostDecoupleBurst.ResolveFiredDecoupler(t, 8, mods).Reason);
            // the root itself has no parent joint: nothing to fire on
            GhostDecoupleBurstChoice root = GhostDecoupleBurst.ResolveFiredDecoupler(t, 7, mods);
            Assert.False(root.Fires);
            Assert.Equal(GhostDecoupleBurstReason.DecouplerFacesElsewhere, root.Reason);
        }

        [Fact]
        public void UnknownPartOrNoSnapshot_DrawsNothing()
        {
            var mods = Modules(On("Decoupler.1", Mod(1, "top")));
            Assert.Equal(GhostDecoupleBurstReason.PartNotInSnapshot,
                GhostDecoupleBurst.ResolveFiredDecoupler(Stack(), 999, mods).Reason);
            Assert.Equal(GhostDecoupleBurstReason.NoSnapshot,
                GhostDecoupleBurst.ResolveFiredDecoupler(null, 200, mods).Reason);
            Assert.Equal(GhostDecoupleBurstReason.PlainJointBreak,
                GhostDecoupleBurst.ResolveFiredDecoupler(Stack(), 200, null).Reason);
        }

        // ----- guards -----

        [Fact]
        public void IsRepeatBurst_OnlyTheSameDecouplerUtAndFrame()
        {
            Assert.True(GhostDecoupleBurst.IsRepeatBurst(5, 10.0, 42, 5, 10.0, 42));
            Assert.False(GhostDecoupleBurst.IsRepeatBurst(5, 10.0, 42, 5, 10.0, 43));   // a loop replays later
            Assert.False(GhostDecoupleBurst.IsRepeatBurst(5, 10.0, 42, 6, 10.0, 42));
            Assert.False(GhostDecoupleBurst.IsRepeatBurst(5, 10.0, 42, 5, 11.0, 42));
            Assert.False(GhostDecoupleBurst.IsRepeatBurst(0, double.NaN, -1, 5, 10.0, 42));
        }

        [Fact]
        public void LifetimeSeconds_IsClampedAndFinite()
        {
            Assert.Equal(2.0, (double)GhostDecoupleBurst.LifetimeSeconds(0.5f, 1.0f), 4);
            Assert.Equal(GhostDecoupleBurst.MinLifetimeSeconds, GhostDecoupleBurst.LifetimeSeconds(0f, 0f));
            Assert.Equal(GhostDecoupleBurst.MaxLifetimeSeconds, GhostDecoupleBurst.LifetimeSeconds(5f, 30f));
            Assert.Equal(GhostDecoupleBurst.DefaultLifetimeSeconds, GhostDecoupleBurst.LifetimeSeconds(float.NaN, 1f));
            Assert.Equal(GhostDecoupleBurst.DefaultLifetimeSeconds,
                GhostDecoupleBurst.LifetimeSeconds(float.PositiveInfinity, 1f));
        }

        [Fact]
        public void ReasonWords_AreDistinct()
        {
            var seen = new HashSet<string>();
            foreach (GhostDecoupleBurstReason r in Enum.GetValues(typeof(GhostDecoupleBurstReason)))
            {
                string w = GhostDecoupleBurst.ReasonWord(r);
                Assert.NotEqual("unknown", w);
                Assert.True(seen.Add(w), w);
            }
        }

        // ----- the state cache -----

        [Fact]
        public void State_ReadsTheTopologyOnce_AndAnewSnapshotDropsIt()
        {
            var state = new GhostPlaybackState { vesselName = "Stack" };
            Assert.Null(state.EnsureDecoupleTopology(GhostVisualBuilder.TryExtractPartName));
            ConfigNode snap = Snapshot(new P { Name = "fuelTank", Pid = 1, Parent = 0 });
            state.SetDecoupleSnapshot(snap);
            GhostDecoupleTopology first = state.EnsureDecoupleTopology(GhostVisualBuilder.TryExtractPartName);
            Assert.NotNull(first);
            Assert.Same(first, state.EnsureDecoupleTopology(GhostVisualBuilder.TryExtractPartName));
            Assert.Single(logLines.FindAll(l => l.Contains("[DecoupleFx]")
                && l.Contains("Read the joint-break topology of 'Stack': parts=1")));
            state.lastDecoupleBurstPid = 9;
            state.SetDecoupleSnapshot(snap);
            Assert.Equal(0u, state.lastDecoupleBurstPid);
            Assert.NotSame(first, state.EnsureDecoupleTopology(GhostVisualBuilder.TryExtractPartName));
        }

        [UnityEntryFact]
        public void SpawnDecoupleBurstForJoint_NoGhost_DoesNothing()
        {
            var state = new GhostPlaybackState();
            GhostPlaybackLogic.SpawnDecoupleBurstForJoint(state, null,
                FlightRecorder.BuildDecoupledPartEvent(1u, "fuelTank", 5.0));
            Assert.Empty(logLines);
            Assert.Null(state.decoupleTopology);
        }
    }
}
