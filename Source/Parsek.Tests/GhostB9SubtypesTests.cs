using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure B9PartSwitch rules the ghost builder applies (<see cref="GhostB9Subtypes"/>,
    /// decompiled B9PartSwitch 2.21.0.4): which subtype each module shows, the TRANSFORM offsets
    /// it carries, the transform enable lists, and B9's name matcher. The fixture is the shape the
    /// UtilityWeight mod's patches give <c>UtilityWeight_OsmiumTank</c> on a B9 install: three
    /// named modules (mass, size, cost), the size subtypes scaling <c>ModelOsmiumTank</c>.
    /// </summary>
    public class GhostB9SubtypesTests
    {
        private static ConfigNode B9Module(ConfigNode part, string moduleId)
        {
            ConfigNode m = part.AddNode("MODULE");
            m.AddValue("name", GhostB9Subtypes.ModuleName);
            if (moduleId != null) m.AddValue("moduleID", moduleId);
            return m;
        }

        private static ConfigNode Subtype(ConfigNode module, string name, string priority = null)
        {
            ConfigNode s = module.AddNode("SUBTYPE");
            s.AddValue("name", name);
            if (priority != null) s.AddValue("defaultSubtypePriority", priority);
            return s;
        }

        private static ConfigNode SizeSubtype(ConfigNode module, string name, string scale, string priority = null)
        {
            ConfigNode s = Subtype(module, name, priority);
            s.AddValue("transform", "ModelOsmiumTank");
            ConfigNode t = s.AddNode("TRANSFORM");
            t.AddValue("name", "ModelOsmiumTank");
            t.AddValue("scaleOffset", scale);
            return s;
        }

        /// <summary>UtilityWeight_OsmiumTank with B9PartSwitch installed (trimmed).</summary>
        private static ConfigNode UtilityWeightConfig()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "UtilityWeight_OsmiumTank");
            ConfigNode mass = B9Module(part, "B9PartSwitchMass");
            Subtype(mass, "0t");
            Subtype(mass, "5t");
            ConfigNode size = B9Module(part, "B9PartSwitchSize");
            SizeSubtype(size, "0.3125m", "0.25,0.25,0.25");
            SizeSubtype(size, "1.25m", "1,1,1", "1");
            SizeSubtype(size, "2.5m", "2,2,2");
            SizeSubtype(size, "5.0m", "4,4,4");
            ConfigNode cost = B9Module(part, "B9PartSwitchCost");
            Subtype(cost, "0 Funds", "1");
            Subtype(cost, "10 Funds");
            return part;
        }

        private static ConfigNode SnapshotPart(params (string id, string subtype)[] modules)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "UtilityWeight.OsmiumTank");
            foreach (var (id, subtype) in modules)
            {
                ConfigNode m = part.AddNode("MODULE");
                m.AddValue("name", GhostB9Subtypes.ModuleName);
                if (id != null) m.AddValue("moduleID", id);
                if (subtype != null) m.AddValue("currentSubtype", subtype);
            }
            return part;
        }

        // ----- Resolve -----

        [Fact]
        public void Resolve_NoB9Module_IsEmpty()
        {
            var part = new ConfigNode("PART");
            part.AddNode("MODULE").AddValue("name", "ModuleCommand");
            Assert.Empty(GhostB9Subtypes.Resolve(part, null));
            Assert.Empty(GhostB9Subtypes.Resolve(null, null));
        }

        [Fact]
        public void Resolve_TheSnapshotSubtypeOfEachModule_MatchedByModuleId()
        {
            // The snapshot lists the modules in another order: routing is by moduleID.
            ConfigNode snap = SnapshotPart(("B9PartSwitchCost", "10 Funds"), ("B9PartSwitchSize", "2.5m"),
                ("B9PartSwitchMass", "5t"));
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(UtilityWeightConfig(), snap);
            Assert.Equal(new[] { "B9PartSwitchMass", "B9PartSwitchSize", "B9PartSwitchCost" }, sel.Select(s => s.ModuleId));
            Assert.Equal(new[] { "5t", "2.5m", "10 Funds" }, sel.Select(s => s.Subtype));
            Assert.All(sel, s => Assert.Equal(GhostB9Subtypes.PathSnapshot, s.ResolutionPath));
            Assert.All(sel, s => Assert.True(s.Modelled));
            Assert.Equal(new[] { 0, 1, 2 }, sel.Select(s => s.Index));
        }

        [Fact]
        public void Resolve_NoSnapshotValue_TakesTheFirstHighestPriority()
        {
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(UtilityWeightConfig(), SnapshotPart());
            Assert.Equal(new[] { "0t", "1.25m", "0 Funds" }, sel.Select(s => s.Subtype));
            Assert.All(sel, s => Assert.Equal(GhostB9Subtypes.PathDefault, s.ResolutionPath));
            Assert.Equal(new[] { "0t", "1.25m", "0 Funds" }, GhostB9Subtypes.Resolve(UtilityWeightConfig(), null).Select(s => s.Subtype));
        }

        [Fact]
        public void Resolve_ASnapshotNamingAnUnknownSubtype_ShowsTheDefault()
        {
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(UtilityWeightConfig(),
                SnapshotPart(("B9PartSwitchSize", "7.5m")));
            B9ModuleSelection size = sel[1];
            Assert.Equal("1.25m", size.Subtype);
            Assert.Equal(GhostB9Subtypes.PathSnapshotUnknown, size.ResolutionPath);
        }

        [Fact]
        public void Resolve_AnUnnamedModule_TakesTheSnapshotNodeAtItsPosition()
        {
            var part = new ConfigNode("PART");
            ConfigNode m = B9Module(part, null);
            Subtype(m, "a");
            Subtype(m, "b");
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(part, SnapshotPart((null, "b")));
            Assert.Single(sel);
            Assert.Null(sel[0].ModuleId);
            Assert.Equal("b", sel[0].Subtype);
        }

        [Fact]
        public void Resolve_AModuleWithoutSubtypes_HasNoSubtype()
        {
            var part = new ConfigNode("PART");
            B9Module(part, "empty");
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(part, null);
            Assert.Single(sel);
            Assert.Null(sel[0].Subtype);
            Assert.Null(sel[0].ResolutionPath);
            Assert.Empty(sel[0].TransformModifiers);
        }

        [Theory]
        [InlineData("TEXTURE")]
        [InlineData("MATERIAL")]
        [InlineData("MODULE")]
        public void Resolve_ASubtypeWithTextureMaterialOrModuleRules_IsUnmodelled(string node)
        {
            var part = new ConfigNode("PART");
            ConfigNode m = B9Module(part, "plumeSwitch");
            Subtype(m, "default");
            Subtype(m, "hydrolox").AddNode(node);
            Assert.True(GhostB9Subtypes.Resolve(part, SnapshotPart(("plumeSwitch", "default")))[0].Modelled);
            Assert.False(GhostB9Subtypes.Resolve(part, SnapshotPart(("plumeSwitch", "hydrolox")))[0].Modelled);
        }

        [Fact]
        public void DefaultSubtypeIndex_FirstMaximumWins_AbsentIsZero()
        {
            var m = new ConfigNode("MODULE");
            Subtype(m, "a");
            Subtype(m, "b", "2");
            Subtype(m, "c", "2");
            Subtype(m, "d", "-1");
            Assert.Equal(1, GhostB9Subtypes.DefaultSubtypeIndex(m.GetNodes("SUBTYPE")));
            var flat = new ConfigNode("MODULE");
            Subtype(flat, "x");
            Subtype(flat, "y");
            Assert.Equal(0, GhostB9Subtypes.DefaultSubtypeIndex(flat.GetNodes("SUBTYPE")));
        }

        // ----- TRANSFORM offsets -----

        [Fact]
        public void ModifiersFor_TheShownSizeSubtypeScalesTheModelTransform()
        {
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(UtilityWeightConfig(),
                SnapshotPart(("B9PartSwitchSize", "5.0m")));
            List<B9TransformModifier> mods = GhostB9Subtypes.ModifiersFor(sel, "ModelOsmiumTank");
            Assert.Single(mods);
            Assert.Equal(new Vector3(4f, 4f, 4f), mods[0].ScaleOffset.Value);
            Assert.Null(mods[0].PositionOffset);
            Assert.Null(mods[0].RotationOffsetEuler);
            Assert.Empty(GhostB9Subtypes.ModifiersFor(sel, "model"));
            Assert.Empty(GhostB9Subtypes.ModifiersFor(sel, null));

            // The real model scale (MODEL scale = 2.37,1.25,2.37) times the subtype's scale.
            Vector3 scaled = GhostB9Subtypes.ApplyScale(new Vector3(2.37f, 1.25f, 2.37f), mods[0].ScaleOffset);
            Assert.Equal(9.48, (double)scaled.x, 4);
            Assert.Equal(5.0, (double)scaled.y, 4);
            Assert.Equal(9.48, (double)scaled.z, 4);
        }

        [Fact]
        public void ModifiersFor_AnEarlierModuleHoldsTheRotationLock_EvenFromAnotherSubtype()
        {
            // Module "a" rotates Arm only in its hidden subtype; module "b" rotates and moves Arm
            // in its shown one. B9 refuses b's rotator (a's lock) and keeps b's move.
            var part = new ConfigNode("PART");
            ConfigNode a = B9Module(part, "a");
            Subtype(a, "plain");
            ConfigNode turned = Subtype(a, "turned");
            ConfigNode ta = turned.AddNode("TRANSFORM");
            ta.AddValue("name", "Arm");
            ta.AddValue("rotationOffset", "0,45,0");
            ConfigNode b = B9Module(part, "b");
            ConfigNode bs = Subtype(b, "moved");
            ConfigNode tb = bs.AddNode("TRANSFORM");
            tb.AddValue("name", "Arm");
            tb.AddValue("rotationOffset", "90,0,0");
            tb.AddValue("positionOffset", "0,1,0");
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(part, SnapshotPart(("a", "plain"), ("b", "moved")));
            List<B9TransformModifier> mods = GhostB9Subtypes.ModifiersFor(sel, "Arm");
            Assert.Single(mods);
            Assert.Null(mods[0].RotationOffsetEuler);
            Assert.Equal(new Vector3(0f, 1f, 0f), mods[0].PositionOffset.Value);

            // With a's turned subtype shown, a's rotation applies and b's is still refused.
            sel = GhostB9Subtypes.Resolve(part, SnapshotPart(("a", "turned"), ("b", "moved")));
            mods = GhostB9Subtypes.ModifiersFor(sel, "Arm");
            Assert.Equal(2, mods.Count);
            Assert.Equal(new Vector3(0f, 45f, 0f), mods[0].RotationOffsetEuler.Value);
            Assert.Null(mods[1].RotationOffsetEuler);
        }

        [Fact]
        public void ModifiersFor_OneModuleKeepsEveryRotationOfItsSubtype()
        {
            var part = new ConfigNode("PART");
            ConfigNode m = B9Module(part, "a");
            ConfigNode st = Subtype(m, "x");
            foreach (string r in new[] { "0,10,0", "0,20,0" })
            {
                ConfigNode t = st.AddNode("TRANSFORM");
                t.AddValue("name", "Arm");
                t.AddValue("rotationOffset", r);
            }
            List<B9TransformModifier> mods = GhostB9Subtypes.ModifiersFor(GhostB9Subtypes.Resolve(part, null), "Arm");
            Assert.Equal(new[] { 10f, 20f }, mods.Select(x => x.RotationOffsetEuler.Value.y));
        }

        [Fact]
        public void ParseTransformModifier_ReadsEachOffset_AndDropsAnEmptyOrNamelessNode()
        {
            var t = new ConfigNode("TRANSFORM");
            t.AddValue("name", "Arm*");
            t.AddValue("positionOffset", "0, 0.5, -1");
            t.AddValue("rotationOffset", "0,90,0");
            t.AddValue("scaleOffset", "1.5");
            B9TransformModifier mod = GhostB9Subtypes.ParseTransformModifier(t);
            Assert.Equal(new Vector3(0f, 0.5f, -1f), mod.PositionOffset.Value);
            Assert.Equal(new Vector3(0f, 90f, 0f), mod.RotationOffsetEuler.Value);
            Assert.Equal(new Vector3(1.5f, 1.5f, 1.5f), mod.ScaleOffset.Value);
            Assert.True(mod.Name.Match("ArmLeft"));

            var empty = new ConfigNode("TRANSFORM");
            empty.AddValue("name", "x");
            Assert.Null(GhostB9Subtypes.ParseTransformModifier(empty));
            var nameless = new ConfigNode("TRANSFORM");
            nameless.AddValue("scaleOffset", "2");
            Assert.Null(GhostB9Subtypes.ParseTransformModifier(nameless));
            Assert.Null(GhostB9Subtypes.ParseTransformModifier(null));
        }

        [Theory]
        [InlineData("2", 2f, 2f, 2f)]
        [InlineData("0.25,0.25,0.25", 0.25f, 0.25f, 0.25f)]
        [InlineData("1 2\t3", 1f, 2f, 3f)]
        [InlineData(" 1, 2, 3 ", 1f, 2f, 3f)]
        public void ParseScale_OneOrThreeNumbers(string raw, float x, float y, float z)
        {
            Assert.Equal(new Vector3(x, y, z), GhostB9Subtypes.ParseScale(raw).Value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("1,2")]
        [InlineData("a")]
        [InlineData("NaN")]
        public void ParseScale_UnreadableIsNull(string raw)
        {
            Assert.Null(GhostB9Subtypes.ParseScale(raw));
        }

        [Fact]
        public void ParseVectors_AreCultureInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), GhostB9Subtypes.ParseScale("0.5").Value);
                Assert.Equal(new Vector3(0.25f, 1.5f, 0f), GhostB9Subtypes.ParseVector("0.25,1.5,0").Value);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void ApplyPositionAndScale_NullOffsetIsIdentity()
        {
            var v = new Vector3(1f, 2f, 3f);
            Assert.Equal(v, GhostB9Subtypes.ApplyScale(v, null));
            Assert.Equal(v, GhostB9Subtypes.ApplyPosition(v, null));
            Assert.Equal(new Vector3(1f, 2.5f, 2f), GhostB9Subtypes.ApplyPosition(v, new Vector3(0f, 0.5f, -1f)));
            Assert.Equal(new Vector3(2f, 1f, 9f), GhostB9Subtypes.ApplyScale(v, new Vector3(2f, 0.5f, 3f)));
        }

        // ----- transform enable lists -----

        private static ConfigNode ToggleConfig()
        {
            var part = new ConfigNode("PART");
            ConfigNode shell = B9Module(part, "shell");
            Subtype(shell, "short").AddValue("transform", "ShortShell");
            ConfigNode tall = Subtype(shell, "tall");
            tall.AddValue("transform", "TallShell");
            tall.AddValue("transform", "Shared");
            Subtype(shell, "both").AddValue("transform", "Shared");
            ConfigNode cap = B9Module(part, "cap");
            Subtype(cap, "plain");
            Subtype(cap, "domed").AddValue("transform", "Dome");
            return part;
        }

        [Fact]
        public void HiddenTransformNames_OtherSubtypesTransformsHide_TheShownAndUnmanagedStay()
        {
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(ToggleConfig(),
                SnapshotPart(("shell", "tall"), ("cap", "plain")));
            HashSet<string> hidden = GhostB9Subtypes.HiddenTransformNames(sel,
                new[] { "ShortShell", "TallShell", "Shared", "Dome", "Body", "ShortShell" });
            Assert.Equal(new[] { "Dome", "ShortShell" }, hidden.OrderBy(n => n, System.StringComparer.Ordinal));
        }

        [Fact]
        public void IsTransformShown_ANameListedByTheShownSubtypeStays_EvenIfAnotherListsItToo()
        {
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(ToggleConfig(),
                SnapshotPart(("shell", "both"), ("cap", "domed")));
            Assert.True(GhostB9Subtypes.IsTransformShown(sel, "Shared"));
            Assert.False(GhostB9Subtypes.IsTransformShown(sel, "TallShell"));
            Assert.False(GhostB9Subtypes.IsTransformShown(sel, "ShortShell"));
            Assert.True(GhostB9Subtypes.IsTransformShown(sel, "Dome"));
            Assert.True(GhostB9Subtypes.IsTransformShown(sel, "Body"));
            Assert.True(GhostB9Subtypes.IsTransformShown(null, "Body"));
        }

        [Fact]
        public void IsTransformShown_IsAndedOverModules()
        {
            // Both modules list "Fin": the first shows it, the second's shown subtype does not.
            var part = new ConfigNode("PART");
            ConfigNode a = B9Module(part, "a");
            Subtype(a, "on").AddValue("transform", "Fin");
            Subtype(a, "off");
            ConfigNode b = B9Module(part, "b");
            Subtype(b, "x");
            Subtype(b, "y").AddValue("transform", "Fin");
            Assert.False(GhostB9Subtypes.IsTransformShown(
                GhostB9Subtypes.Resolve(part, SnapshotPart(("a", "on"), ("b", "x"))), "Fin"));
            Assert.True(GhostB9Subtypes.IsTransformShown(
                GhostB9Subtypes.Resolve(part, SnapshotPart(("a", "on"), ("b", "y"))), "Fin"));
        }

        [Fact]
        public void HiddenTransformNames_TheUtilityWeightSizesHideNothing()
        {
            // Every size subtype lists the same transform, so the shown one always keeps it.
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(UtilityWeightConfig(),
                SnapshotPart(("B9PartSwitchSize", "0.3125m")));
            Assert.Empty(GhostB9Subtypes.HiddenTransformNames(sel, new[] { "ModelOsmiumTank", "mesh" }));
        }

        // ----- name matcher -----

        [Theory]
        [InlineData("Tank", "Tank", true)]
        [InlineData("Tank", "TankCap", false)]
        [InlineData("Tank*", "TankCap", true)]
        [InlineData("Tank*", "XTank", false)]
        [InlineData("T?nk", "Tunk", true)]
        [InlineData("/^Fin[0-9]$/", "Fin3", true)]
        [InlineData("/Fin/", "LeftFin2", true)]
        [InlineData("\\/Fin/", "/Fin/", true)]
        [InlineData("\\/Fin/", "Fin", false)]
        [InlineData("a.b", "aXb", false)]
        public void NameMatcher_FollowsB9StringMatcher(string pattern, string name, bool expected)
        {
            Assert.Equal(expected, B9NameMatcher.Parse(pattern).Match(name));
        }

        [Fact]
        public void NameMatcher_ABadRegexMatchesNothing()
        {
            Assert.Null(B9NameMatcher.Parse("/[unclosed/"));
            Assert.Null(B9NameMatcher.Parse(null));
            Assert.False(B9NameMatcher.Parse("x").Match(null));
        }

        // ----- record and log -----

        [Fact]
        public void ToAppliedB9Subtypes_CopiesEachModule_NullForNone()
        {
            List<B9ModuleSelection> sel = GhostB9Subtypes.Resolve(UtilityWeightConfig(),
                SnapshotPart(("B9PartSwitchSize", "2.5m")));
            List<GhostAppliedB9Subtype> applied = GhostVisualBuilder.ToAppliedB9Subtypes(sel);
            Assert.Equal(3, applied.Count);
            Assert.Equal("B9PartSwitchSize", applied[1].moduleId);
            Assert.Equal("2.5m", applied[1].subtype);
            Assert.Equal(GhostB9Subtypes.PathSnapshot, applied[1].resolutionPath);
            Assert.True(applied[1].modelled);
            Assert.Equal(GhostB9Subtypes.PathDefault, applied[0].resolutionPath);
            Assert.Null(GhostVisualBuilder.ToAppliedB9Subtypes(new List<B9ModuleSelection>()));
            Assert.Null(GhostVisualBuilder.ToAppliedB9Subtypes(null));
        }

        [Fact]
        public void Describe_NamesEveryModule()
        {
            var part = new ConfigNode("PART");
            ConfigNode m = B9Module(part, null);
            Subtype(m, "a");
            Subtype(m, "b").AddNode("TEXTURE");
            Assert.Equal("b9=b(snapshot,unmodelled)",
                GhostB9Subtypes.Describe(GhostB9Subtypes.Resolve(part, SnapshotPart((null, "b")))));
            Assert.Equal("<none>", GhostB9Subtypes.Describe(null));
        }
    }
}
