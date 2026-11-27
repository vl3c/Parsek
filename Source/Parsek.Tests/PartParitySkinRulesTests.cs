using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure skin rules of the part parity sweep (<see cref="PartParitySkinRules"/>,
    /// design-part-ghost-parity.md 3.2): the <c>skins=all</c> enumeration over ConfigNode part
    /// configs (every <c>ModulePartVariants</c> VARIANT, every <c>ModuleB9PartSwitch</c> SUBTYPE
    /// one module at a time), the case-key skin names and ids, the B9 authoring, the snapshot
    /// read-back, the skin proof's flags, the reflection read of a B9 module's subtype, and the
    /// builder's applied-skin accessor.
    /// </summary>
    public class PartParitySkinRulesTests
    {
        // ----- fixtures -----

        private static ConfigNode Module(ConfigNode part, string name)
        {
            ConfigNode m = part.AddNode("MODULE");
            m.AddValue("name", name);
            return m;
        }

        private static ConfigNode VariantPart(params string[] variants)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "fuelTank");
            Module(part, "ModuleFuelJettison");
            ConfigNode mv = Module(part, PartParityCaseRules.VariantModuleName);
            mv.AddValue("baseVariant", variants.Length > 0 ? variants[0] : "");
            foreach (string v in variants)
                mv.AddNode("VARIANT").AddValue("name", v);
            return part;
        }

        /// <summary>The shape of the RestockWaterfallExpansion Cheetah patch: one
        /// <c>plumeSwitch</c> B9 module with three SUBTYPEs, after a Waterfall FX module.</summary>
        private static ConfigNode CheetahLike()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "LiquidEngineLV-T91");
            Module(part, "ModuleEnginesFX");
            Module(part, "ModuleWaterfallFX").AddValue("moduleID", "CheetahFX");
            ConfigNode b9 = Module(part, PartParityCaseRules.B9ModuleName);
            b9.AddValue("moduleID", "plumeSwitch");
            foreach (string st in new[] { "default", "hypergolicCheetah", "keroloxCheetah" })
                b9.AddNode("SUBTYPE").AddValue("name", st);
            return part;
        }

        private static ConfigNode TwoB9Modules()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "multi");
            ConfigNode a = Module(part, PartParityCaseRules.B9ModuleName);
            a.AddValue("moduleID", "meshSwitch");
            a.AddNode("SUBTYPE").AddValue("name", "Short");
            a.AddNode("SUBTYPE").AddValue("name", "Long");
            Module(part, "ModuleCommand");
            ConfigNode b = Module(part, PartParityCaseRules.B9ModuleName);
            b.AddValue("moduleID", "texSwitch");
            b.AddNode("SUBTYPE").AddValue("name", "White");
            b.AddNode("SUBTYPE").AddValue("name", "Black");
            return part;
        }

        /// <summary>An authored PART node: one MODULE per config module, names only (what
        /// <c>GhostCoOrbitalRig.AuthorPartNode</c> writes before customize).</summary>
        private static ConfigNode AuthoredFrom(ConfigNode config)
        {
            var p = new ConfigNode("PART");
            p.AddValue("name", config.GetValue("name"));
            foreach (ConfigNode m in config.GetNodes("MODULE"))
                Module(p, m.GetValue("name")).AddValue("isEnabled", "True");
            return p;
        }

        private static PartParityPartEntry Entry(ConfigNode config, string baseVariant)
        {
            return new PartParityPartEntry
            {
                Name = config.GetValue("name"), Source = "Squad", BaseVariant = baseVariant,
                SkinOptions = PartParitySkinRules.EnumerateSkinOptions(config, baseVariant),
                B9Modules = PartParitySkinRules.B9Modules(config),
            };
        }

        // ----- enumeration -----

        [Fact]
        public void VariantNames_InConfigOrder_DuplicatesAndEmptiesDropped()
        {
            ConfigNode part = VariantPart("Orange", "White", "white", "");
            Assert.Equal(new[] { "Orange", "White" }, PartParitySkinRules.VariantNames(part));
            Assert.Empty(PartParitySkinRules.VariantNames(CheetahLike()));
            Assert.Empty(PartParitySkinRules.VariantNames(null));
        }

        [Fact]
        public void B9Modules_ReadsEveryModuleWithItsIdAndSubtypes()
        {
            List<PartParityB9Module> mods = PartParitySkinRules.B9Modules(TwoB9Modules());
            Assert.Equal(2, mods.Count);
            Assert.Equal("meshSwitch", mods[0].ModuleId);
            Assert.Equal(0, mods[0].Index);
            Assert.Equal(new[] { "Short", "Long" }, mods[0].Subtypes);
            Assert.Equal("texSwitch", mods[1].ModuleId);
            Assert.Equal(1, mods[1].Index);
            Assert.Equal(new[] { "White", "Black" }, mods[1].Subtypes);
        }

        [Fact]
        public void Variants_EveryVariantButTheBase_InConfigOrder()
        {
            List<PartParitySkinOption> o = PartParitySkinRules.EnumerateSkinOptions(
                VariantPart("Orange", "White", "BlackAndWhite", "Dark"), "White");
            Assert.Equal(new[] { "Orange", "BlackAndWhite", "Dark" }, o.Select(x => x.Skin));
            Assert.Equal(new[] { 1, 2, 3 }, o.Select(x => x.Ordinal));
            Assert.All(o, x => Assert.Equal(PartParityCaseRules.VariantModuleName, x.Module));
            Assert.All(o, x => Assert.Equal(x.Skin, x.Value));
            Assert.All(o, x => Assert.Equal(-1, x.ModuleIndex));
            Assert.All(o, x => Assert.Null(x.ModuleId));
        }

        [Fact]
        public void Variants_TheBaseIsMatchedIgnoringCase()
        {
            List<PartParitySkinOption> o = PartParitySkinRules.EnumerateSkinOptions(VariantPart("Orange", "White"), "orange");
            Assert.Equal(new[] { "White" }, o.Select(x => x.Skin));
        }

        [Fact]
        public void Variants_NoLiveVariantModule_NoVariantCases()
        {
            // BuildEntry leaves BaseVariant null when the prefab has no ModulePartVariants.
            Assert.Empty(PartParitySkinRules.EnumerateSkinOptions(VariantPart("Orange", "White"), null));
        }

        [Fact]
        public void B9_EverySubtypeOfTheModule_TheDefaultIncluded_NamedByModuleId()
        {
            List<PartParitySkinOption> o = PartParitySkinRules.EnumerateSkinOptions(CheetahLike(), null);
            Assert.Equal(new[] { "plumeSwitch=default", "plumeSwitch=hypergolicCheetah", "plumeSwitch=keroloxCheetah" },
                o.Select(x => x.Skin));
            Assert.Equal(new[] { "default", "hypergolicCheetah", "keroloxCheetah" }, o.Select(x => x.Value));
            Assert.Equal(new[] { 1, 2, 3 }, o.Select(x => x.Ordinal));
            Assert.All(o, x => Assert.Equal(PartParityCaseRules.B9ModuleName, x.Module));
            Assert.All(o, x => Assert.Equal("plumeSwitch", x.ModuleId));
            Assert.All(o, x => Assert.Equal(0, x.ModuleIndex));
        }

        [Fact]
        public void B9_SeveralModules_OneModuleAtATime_NeverTheCrossProduct()
        {
            List<PartParitySkinOption> o = PartParitySkinRules.EnumerateSkinOptions(TwoB9Modules(), null);
            // 2 + 2 cases, not 2 x 2.
            Assert.Equal(new[] { "meshSwitch=Short", "meshSwitch=Long", "texSwitch=White", "texSwitch=Black" },
                o.Select(x => x.Skin));
            Assert.Equal(new[] { 0, 0, 1, 1 }, o.Select(x => x.ModuleIndex));
        }

        [Fact]
        public void B9_UnnamedModule_UsesTheB9Label()
        {
            var part = new ConfigNode("PART");
            ConfigNode b9 = Module(part, PartParityCaseRules.B9ModuleName);
            b9.AddNode("SUBTYPE").AddValue("name", "A");
            List<PartParitySkinOption> o = PartParitySkinRules.EnumerateSkinOptions(part, null);
            Assert.Single(o);
            Assert.Equal("b9=A", o[0].Skin);
            Assert.Null(o[0].ModuleId);
            Assert.Equal("b9=A", PartParitySkinRules.B9SkinName(null, "A"));
            Assert.Equal("plumeSwitch=A", PartParitySkinRules.B9SkinName("plumeSwitch", "A"));
        }

        [Fact]
        public void VariantsThenB9_OrdinalsContinue()
        {
            ConfigNode part = VariantPart("Orange", "White");
            ConfigNode b9 = Module(part, PartParityCaseRules.B9ModuleName);
            b9.AddValue("moduleID", "plume");
            b9.AddNode("SUBTYPE").AddValue("name", "x");
            List<PartParitySkinOption> o = PartParitySkinRules.EnumerateSkinOptions(part, "Orange");
            Assert.Equal(new[] { "White", "plume=x" }, o.Select(x => x.Skin));
            Assert.Equal(new[] { 1, 2 }, o.Select(x => x.Ordinal));
        }

        [Fact]
        public void CaseKeys_BaseFirstThenEveryOption_IdsUniqueAndValidLabels()
        {
            PartParityPartEntry e = Entry(CheetahLike(), null);
            List<PartParityCaseKey> keys = PartParitySkinRules.CaseKeys(e);
            Assert.Equal(4, keys.Count);
            Assert.Null(keys[0].SkinOption);
            Assert.Equal(TestCommandPartParitySweep.DefaultSkinName, keys[0].Skin);
            Assert.Equal(0, keys[0].SkinOrdinal);
            List<string> ids = keys.Select(TestCommandPartParitySweep.CaseId).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(ids, id => Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id), id));
            // '=' maps to '-', the hash keeps the unmapped tuple apart.
            Assert.StartsWith("liquidenginelv-t91--plumeswitch-hypergoliccheetah--", ids[2]);
        }

        [Fact]
        public void CaseKeys_TheBaseKeyIsExactlyTheP2DefaultKey()
        {
            PartParityPartEntry e = Entry(VariantPart("Orange", "White"), "Orange");
            PartParityCaseKey k = PartParitySkinRules.CaseKeys(e)[0];
            PartParityCaseKey p2 = PartParityCaseRules.DefaultCaseKey(e);
            Assert.Equal(TestCommandPartParitySweep.CaseId(p2), TestCommandPartParitySweep.CaseId(k));
            Assert.Equal("Orange", k.Skin);
        }

        [Fact]
        public void SkinsDefault_SelectsOnlyTheBaseKeys_SkinsAll_SelectsEverySkin()
        {
            var keys = new List<PartParityCaseKey>();
            keys.AddRange(PartParitySkinRules.CaseKeys(Entry(VariantPart("Orange", "White", "Dark"), "Orange")));
            keys.AddRange(PartParitySkinRules.CaseKeys(Entry(CheetahLike(), null)));
            var def = TestCommandPartParitySweep.SelectCases(keys, new PartParitySweepArgs());
            Assert.Equal(new[] { "LiquidEngineLV-T91/default", "fuelTank/Orange" }, def.Select(c => c.Key.Part + "/" + c.Key.Skin));
            var all = TestCommandPartParitySweep.SelectCases(keys, new PartParitySweepArgs { Skins = PartParitySkinSelection.All });
            Assert.Equal(new[]
            {
                "LiquidEngineLV-T91/default", "LiquidEngineLV-T91/plumeSwitch=default",
                "LiquidEngineLV-T91/plumeSwitch=hypergolicCheetah", "LiquidEngineLV-T91/plumeSwitch=keroloxCheetah",
                "fuelTank/Orange", "fuelTank/White", "fuelTank/Dark",
            }, all.Select(c => c.Key.Part + "/" + c.Key.Skin));
            Assert.All(all, c => Assert.Same(c.Key.SkinOption, keys.First(k => k == c.Key).SkinOption));
        }

        [Fact]
        public void Count_ByKind_AndTheLogLine()
        {
            var keys = new List<PartParityCaseKey>();
            keys.AddRange(PartParitySkinRules.CaseKeys(Entry(VariantPart("Orange", "White", "Dark"), "Orange")));
            keys.AddRange(PartParitySkinRules.CaseKeys(Entry(CheetahLike(), null)));
            PartParitySkinCounts c = PartParitySkinRules.Count(keys);
            Assert.Equal(2, c.Base);
            Assert.Equal(2, c.Variant);
            Assert.Equal(3, c.B9);
            Assert.Equal(7, c.Total);
            var planned = PartParitySkinRules.Count(keys.Where(k => k.SkinOption == null));
            Assert.Equal("skin cases: skins=default install=7 (base=2 variant=2 b9=3) planned=2 (base=2 variant=0 b9=0)",
                PartParitySkinRules.FormatCounts("default", c, planned));
            Assert.Equal(0, PartParitySkinRules.Count(null).Total);
        }

        [Fact]
        public void RealVariant_AVariantOptionAuthorsItself_AB9OptionKeepsTheBase()
        {
            var v = new PartParitySkinOption { Module = PartParityCaseRules.VariantModuleName, Value = "Dark" };
            var b = new PartParitySkinOption { Module = PartParityCaseRules.B9ModuleName, Value = "x" };
            Assert.Equal("Dark", PartParitySkinRules.RealVariant(v, "Orange"));
            Assert.Equal("Orange", PartParitySkinRules.RealVariant(b, "Orange"));
            Assert.Equal("Orange", PartParitySkinRules.RealVariant(null, "Orange"));
            Assert.Null(PartParitySkinRules.RealVariant(b, null));
        }

        // ----- authoring -----

        [Fact]
        public void AuthorB9Subtype_WritesTheIdAndSubtypeIntoTheKthB9Module()
        {
            ConfigNode config = TwoB9Modules();
            ConfigNode authored = AuthoredFrom(config);
            PartParitySkinOption opt = PartParitySkinRules.EnumerateSkinOptions(config, null)[3]; // texSwitch=Black
            Assert.True(PartParitySkinRules.AuthorB9Subtype(authored, opt));
            List<ConfigNode> b9 = PartParitySkinRules.ModuleNodes(authored, PartParityCaseRules.B9ModuleName);
            Assert.Null(b9[0].GetValue(PartParitySkinRules.B9SubtypeKey));
            Assert.Null(b9[0].GetValue(PartParitySkinRules.B9ModuleIdKey));
            Assert.Equal("texSwitch", b9[1].GetValue("moduleID"));
            Assert.Equal("Black", b9[1].GetValue("currentSubtype"));
            // The persisted key B9 2.21 reads and writes.
            Assert.Equal("currentSubtype", PartParitySkinRules.B9SubtypeKey);
        }

        [Fact]
        public void AuthorB9Subtype_RefusesAVariantNullOrMissingModule()
        {
            ConfigNode authored = AuthoredFrom(CheetahLike());
            Assert.False(PartParitySkinRules.AuthorB9Subtype(authored, null));
            Assert.False(PartParitySkinRules.AuthorB9Subtype(authored,
                new PartParitySkinOption { Module = PartParityCaseRules.VariantModuleName, Value = "x" }));
            Assert.False(PartParitySkinRules.AuthorB9Subtype(authored,
                new PartParitySkinOption { Module = PartParityCaseRules.B9ModuleName, ModuleIndex = 1, Value = "x" }));
            Assert.False(PartParitySkinRules.AuthorB9Subtype(null,
                new PartParitySkinOption { Module = PartParityCaseRules.B9ModuleName, ModuleIndex = 0, Value = "x" }));
            Assert.Null(PartParitySkinRules.ModuleNodes(authored, PartParityCaseRules.B9ModuleName)[0].GetValue("currentSubtype"));
        }

        [Fact]
        public void AuthorB9Subtype_UnnamedModuleWritesNoModuleId()
        {
            var config = new ConfigNode("PART");
            config.AddValue("name", "lone");
            Module(config, PartParityCaseRules.B9ModuleName).AddNode("SUBTYPE").AddValue("name", "A");
            ConfigNode authored = AuthoredFrom(config);
            Assert.True(PartParitySkinRules.AuthorB9Subtype(authored, PartParitySkinRules.EnumerateSkinOptions(config, null)[0]));
            ConfigNode m = PartParitySkinRules.ModuleNodes(authored, PartParityCaseRules.B9ModuleName)[0];
            Assert.Null(m.GetValue("moduleID"));
            Assert.Equal("A", m.GetValue("currentSubtype"));
        }

        // ----- skin proof -----

        [Fact]
        public void NewProof_OptionVariantBaseB9BaseOrNothing()
        {
            var opt = new PartParitySkinOption { Module = PartParityCaseRules.B9ModuleName, ModuleId = "plumeSwitch", Value = "kero" };
            PartParitySkinProof p = PartParitySkinRules.NewProof(opt, "Orange", null);
            Assert.Equal(PartParityCaseRules.B9ModuleName, p.Module);
            Assert.Equal("plumeSwitch", p.ModuleId);
            Assert.Equal("kero", p.Requested);

            p = PartParitySkinRules.NewProof(null, "Orange", PartParitySkinRules.B9Modules(CheetahLike()));
            Assert.Equal(PartParityCaseRules.VariantModuleName, p.Module);
            Assert.Equal("Orange", p.Requested);
            Assert.Null(p.ModuleId);

            // A B9-only part's base case authors no subtype: no request, the first module named.
            p = PartParitySkinRules.NewProof(null, null, PartParitySkinRules.B9Modules(CheetahLike()));
            Assert.Equal(PartParityCaseRules.B9ModuleName, p.Module);
            Assert.Equal("plumeSwitch", p.ModuleId);
            Assert.Null(p.Requested);

            p = PartParitySkinRules.NewProof(null, null, null);
            Assert.Null(p.Module);
            Assert.Null(p.Requested);
        }

        [Fact]
        public void RequestOnlyProof_ForACrashRecord()
        {
            var opt = new PartParitySkinOption { Module = PartParityCaseRules.B9ModuleName, ModuleId = "p", Value = "v", Skin = "p=v" };
            PartParitySkinProof p = PartParitySkinRules.RequestOnlyProof(new PartParityCaseKey { Part = "x", Skin = "p=v", SkinOption = opt });
            Assert.Equal(PartParityCaseRules.B9ModuleName, p.Module);
            Assert.Equal("p", p.ModuleId);
            Assert.Equal("v", p.Requested);
            p = PartParitySkinRules.RequestOnlyProof(new PartParityCaseKey { Part = "x", Skin = "White" });
            Assert.Null(p.Module);
            Assert.Equal("White", p.Requested);
            PartParityCaseSidecar crashed = PartParityJson.BuildCrashedSidecar(
                new PartParityCaseKey { Part = "x", Skin = "p=v", SkinOption = opt }, "x--p-v--00000000", "died");
            Assert.Equal("v", crashed.Skin.Requested);
            Assert.Equal("p", crashed.Skin.ModuleId);
        }

        [Fact]
        public void SnapshotSkin_ReadsTheVariantKeyOrTheB9ModuleById()
        {
            var snap = new ConfigNode("PART");
            snap.AddValue("moduleVariantName", "Dark");
            ConfigNode a = Module(snap, PartParityCaseRules.B9ModuleName);
            a.AddValue("moduleID", "meshSwitch");
            a.AddValue("currentSubtype", "Long");
            ConfigNode b = Module(snap, PartParityCaseRules.B9ModuleName);
            b.AddValue("moduleID", "texSwitch");
            b.AddValue("currentSubtype", "Black");

            Assert.Equal("Dark", PartParitySkinRules.SnapshotSkin(snap,
                new PartParitySkinProof { Module = PartParityCaseRules.VariantModuleName }));
            Assert.Equal("Black", PartParitySkinRules.SnapshotSkin(snap,
                new PartParitySkinProof { Module = PartParityCaseRules.B9ModuleName, ModuleId = "texSwitch" }));
            Assert.Equal("Long", PartParitySkinRules.SnapshotSkin(snap,
                new PartParitySkinProof { Module = PartParityCaseRules.B9ModuleName }));
            Assert.Null(PartParitySkinRules.SnapshotSkin(snap,
                new PartParitySkinProof { Module = PartParityCaseRules.B9ModuleName, ModuleId = "nope" }));
            Assert.Null(PartParitySkinRules.SnapshotSkin(snap, new PartParitySkinProof()));
            Assert.Null(PartParitySkinRules.SnapshotSkin(null, new PartParitySkinProof { Module = PartParityCaseRules.VariantModuleName }));
        }

        [Fact]
        public void GhostSkin_TheBuilderVariantForAVariant()
        {
            var applied = new GhostAppliedSkin { variantName = "Dark" };
            Assert.Equal("Dark", PartParitySkinRules.GhostSkin(
                new PartParitySkinProof { Module = PartParityCaseRules.VariantModuleName }, applied));
            Assert.Null(PartParitySkinRules.GhostSkin(
                new PartParitySkinProof { Module = PartParityCaseRules.B9ModuleName }, applied));
            Assert.Null(PartParitySkinRules.GhostSkin(new PartParitySkinProof(), applied));
            Assert.Null(PartParitySkinRules.GhostSkin(null, applied));
            Assert.Null(PartParitySkinRules.GhostSkin(
                new PartParitySkinProof { Module = PartParityCaseRules.VariantModuleName }, null));
        }

        [Fact]
        public void GhostSkin_TheAppliedB9SubtypeOfTheProofsModule_NullWhenUnmodelledOrMissing()
        {
            var applied = new GhostAppliedSkin
            {
                b9Subtypes = new List<GhostAppliedB9Subtype>
                {
                    new GhostAppliedB9Subtype { moduleId = "B9PartSwitchMass", subtype = "5t", modelled = true },
                    new GhostAppliedB9Subtype { moduleId = "B9PartSwitchSize", subtype = "2.5m", modelled = true },
                    new GhostAppliedB9Subtype { moduleId = "plumeSwitch", subtype = "hydrolox", modelled = false },
                },
            };
            PartParitySkinProof B9(string id) => new PartParitySkinProof { Module = PartParityCaseRules.B9ModuleName, ModuleId = id };
            Assert.Equal("2.5m", PartParitySkinRules.GhostSkin(B9("B9PartSwitchSize"), applied));
            Assert.Equal("5t", PartParitySkinRules.GhostSkin(B9(null), applied));
            Assert.Null(PartParitySkinRules.GhostSkin(B9("plumeSwitch"), applied));
            Assert.Null(PartParitySkinRules.GhostSkin(B9("nope"), applied));
            Assert.Null(PartParitySkinRules.GhostSkin(B9("B9PartSwitchSize"), new GhostAppliedSkin { variantName = "Dark" }));
        }

        [Theory]
        [InlineData("ModulePartVariants", "Dark", "Dark", "")]
        [InlineData("ModulePartVariants", "Dark", "dark", "")]
        [InlineData("ModulePartVariants", "Dark", "Orange", "skin-mismatch")]
        [InlineData("ModulePartVariants", "Dark", null, "skin-mismatch")]
        [InlineData("ModulePartVariants", null, null, "")]
        [InlineData("ModuleB9PartSwitch", "kero", null, "skin-not-modelled:b9")]
        [InlineData("ModuleB9PartSwitch", null, null, "skin-not-modelled:b9")]
        [InlineData("ModuleB9PartSwitch", "kero", "kero", "")]
        [InlineData("ModuleB9PartSwitch", "kero", "hyper", "skin-mismatch")]
        [InlineData(null, "x", "y", "")]
        public void GhostFlags(string module, string real, string ghost, string expected)
        {
            var proof = new PartParitySkinProof { Module = module, Real = real, Ghost = ghost };
            Assert.Equal(expected, string.Join(",", PartParitySkinRules.GhostFlags(proof)));
        }

        [Fact]
        public void GhostFlags_ARealSkinDifferentFromTheGhostIsFlaggedEvenWhenTheRequestMatched()
        {
            var proof = new PartParitySkinProof
            {
                Module = PartParityCaseRules.VariantModuleName, Requested = "Dark", Real = "Dark", Snapshot = "Dark", Ghost = "Orange",
            };
            Assert.False(PartParitySkinRules.SkinNotApplied(proof));
            Assert.Equal(new[] { PartParitySkinRules.FlagSkinMismatch }, PartParitySkinRules.GhostFlags(proof));
        }

        [Theory]
        [InlineData("Dark", "Dark", false)]
        [InlineData("Dark", "dark", true)]
        [InlineData("Dark", null, true)]
        [InlineData(null, "Dark", false)]
        [InlineData(null, null, false)]
        public void SkinNotApplied_ExactCompareOfRequestAndReal(string requested, string real, bool expected)
        {
            Assert.Equal(expected, PartParitySkinRules.SkinNotApplied(new PartParitySkinProof { Requested = requested, Real = real }));
        }

        // ----- reflection read of a B9 module -----

        private class FakeCustomPartModule
        {
            public string moduleID = "plumeSwitch";
        }

        private sealed class FakeB9WithProperty : FakeCustomPartModule
        {
            public string CurrentSubtypeName => "hypergolicCheetah";
        }

        private sealed class FakeB9WithField
        {
            public string currentSubtypeName = "keroloxCheetah";
        }

        private sealed class FakeSubtype
        {
            public string Name { get; set; }
        }

        private sealed class FakeB9WithObjectOnly
        {
            public FakeSubtype CurrentSubtype => new FakeSubtype { Name = "default" };
        }

        private sealed class FakeB9WithNullObject
        {
            public FakeSubtype CurrentSubtype => null;
        }

        private sealed class FakeB9Throwing
        {
            public string CurrentSubtypeName => throw new ArgumentOutOfRangeException("index");
        }

        [Fact]
        public void ReadB9Subtype_PropertyByCaseInsensitiveName()
        {
            Assert.Equal("hypergolicCheetah", PartParitySkinRules.ReadB9Subtype(new FakeB9WithProperty(), out string member));
            Assert.Equal("property CurrentSubtypeName", member);
        }

        [Fact]
        public void ReadB9Subtype_Field()
        {
            Assert.Equal("keroloxCheetah", PartParitySkinRules.ReadB9Subtype(new FakeB9WithField(), out string member));
            Assert.Equal("field currentSubtypeName", member);
        }

        [Fact]
        public void ReadB9Subtype_FallsBackToTheSubtypeObjectsName()
        {
            Assert.Equal("default", PartParitySkinRules.ReadB9Subtype(new FakeB9WithObjectOnly(), out string member));
            Assert.Equal("property CurrentSubtype.Name", member);
            Assert.Null(PartParitySkinRules.ReadB9Subtype(new FakeB9WithNullObject(), out member));
            Assert.Equal("property CurrentSubtype", member);
        }

        [Fact]
        public void ReadB9Subtype_NothingOrAThrowingGetterReadsNull()
        {
            Assert.Null(PartParitySkinRules.ReadB9Subtype(new object(), out string member));
            Assert.Equal("none", member);
            Assert.Null(PartParitySkinRules.ReadB9Subtype(null, out member));
            Assert.Null(PartParitySkinRules.ReadB9Subtype(new FakeB9Throwing(), out member));
            Assert.Equal("property CurrentSubtypeName (threw ArgumentOutOfRangeException)", member);
        }

        [Fact]
        public void ReadStringMember_FindsABaseClassField()
        {
            Assert.Equal("plumeSwitch", PartParitySkinRules.ReadStringMember(new FakeB9WithProperty(), "moduleID"));
            Assert.Null(PartParitySkinRules.ReadStringMember(new FakeB9WithField(), "moduleID"));
            Assert.Null(PartParitySkinRules.ReadStringMember(null, "moduleID"));
        }

        // ----- the builder's applied-skin accessor -----

        [Fact]
        public void GhostBuildResult_TryGetAppliedSkin()
        {
            var none = new GhostBuildResult();
            Assert.False(none.TryGetAppliedSkin(100000, out GhostAppliedSkin s));
            Assert.Null(s);
            var build = new GhostBuildResult
            {
                appliedSkins = new Dictionary<uint, GhostAppliedSkin>
                {
                    [100000] = new GhostAppliedSkin { variantName = "Dark", variantResolutionPath = "snapshot" },
                    [101111] = null,
                },
            };
            Assert.True(build.TryGetAppliedSkin(100000, out s));
            Assert.Equal("Dark", s.variantName);
            Assert.Equal("snapshot", s.variantResolutionPath);
            Assert.False(build.TryGetAppliedSkin(101111, out s));
            Assert.False(build.TryGetAppliedSkin(7, out s));
        }
    }
}
