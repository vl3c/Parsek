using System;
using System.Collections.Generic;
using System.Reflection;

namespace Parsek.TestCommands
{
    /// <summary>One skin case of a part beyond its base skin (design-part-ghost-parity.md 3.2):
    /// one <c>ModulePartVariants</c> VARIANT, or one SUBTYPE of one <c>ModuleB9PartSwitch</c>
    /// module with every other module of the part left at its base.</summary>
    internal sealed class PartParitySkinOption
    {
        /// <summary><see cref="PartParityCaseRules.VariantModuleName"/> or
        /// <see cref="PartParityCaseRules.B9ModuleName"/>.</summary>
        internal string Module;

        /// <summary>The B9 module's <c>moduleID</c>; null for a variant or a B9 module without one.</summary>
        internal string ModuleId;

        /// <summary>The B9 module's position among the part's B9 modules in prefab order (the
        /// MODULE node the authoring writes into); -1 for a variant.</summary>
        internal int ModuleIndex = -1;

        /// <summary>The VARIANT or SUBTYPE name the case authors.</summary>
        internal string Value;

        /// <summary>The case key's skin: the variant name, or <c>&lt;moduleID&gt;=&lt;subtype&gt;</c>.</summary>
        internal string Skin;

        /// <summary>The case key's skin ordinal (1.., the base skin is 0).</summary>
        internal int Ordinal;
    }

    /// <summary>One <c>ModuleB9PartSwitch</c> MODULE node of a part config.</summary>
    internal sealed class PartParityB9Module
    {
        internal string ModuleId;
        internal int Index;
        internal readonly List<string> Subtypes = new List<string>();
    }

    /// <summary>The number of cases per skin kind (the step-start log line, design 7).</summary>
    internal sealed class PartParitySkinCounts
    {
        internal int Base;
        internal int Variant;
        internal int B9;
        internal int Total => Base + Variant + B9;
    }

    /// <summary>
    /// The pure skin rules of the sweep (design-part-ghost-parity.md 3.2): which skin cases a
    /// part config offers under <c>skins=all</c>, their case-key skin names, how one is written
    /// into the authored PART node, how the snapshot's skin is read back, and the skin proof's
    /// flags. Every rule reads ConfigNodes or plain objects only, so xUnit drives it.
    /// </summary>
    internal static class PartParitySkinRules
    {
        /// <summary>The key B9PartSwitch persists a module's subtype under (decompiled
        /// <c>ModuleB9PartSwitch.CurrentSubtypeName</c>, <c>[NodeData(name = "currentSubtype",
        /// persistent = true)]</c>, B9PartSwitch 2.21.0.4): the key its <c>OnLoad</c> reads and
        /// its <c>OnSave</c> writes, so the one an authored MODULE node and a snapshot carry.</summary>
        internal const string B9SubtypeKey = "currentSubtype";

        /// <summary>The key B9 (<c>CustomPartModule.moduleID</c>) routes a MODULE node by: a
        /// node whose <c>moduleID</c> names another B9 module of the part is loaded into that one.</summary>
        internal const string B9ModuleIdKey = "moduleID";

        /// <summary>The module id part of a B9 skin name when the module has no <c>moduleID</c>
        /// (B9 removes every unnamed module of a part that has several, so only a lone module is unnamed).</summary>
        internal const string B9UnnamedModuleLabel = "b9";

        internal const string B9SkinSeparator = "=";

        /// <summary>The live members a B9 module's subtype is read from by reflection, in order
        /// (no compile-time reference to B9PartSwitch): a field or property of this name, ignoring
        /// case (the installed DLL has the property <c>CurrentSubtypeName</c>) ...</summary>
        internal const string B9SubtypeNameMember = "currentSubtypeName";

        /// <summary>... else this member's <c>Name</c> (<c>ModuleB9PartSwitch.CurrentSubtype</c>,
        /// a <c>PartSubtype</c>).</summary>
        internal const string B9SubtypeObjectMember = "CurrentSubtype";
        internal const string B9SubtypeObjectNameMember = "Name";

        /// <summary>The real skin is not the ghost's (a ghost defect, raised even when the
        /// pictures match).</summary>
        internal const string FlagSkinMismatch = "skin-mismatch";

        /// <summary>The ghost builder does not model this switcher (the B9 subtype is never
        /// applied to the ghost), so the case's ghost skin is null by construction.</summary>
        internal const string FlagSkinNotModelledPrefix = "skin-not-modelled:";
        internal const string ModelledTokenB9 = "b9";

        private const string VariantsModule = PartParityCaseRules.VariantModuleName;
        private const string B9Module = PartParityCaseRules.B9ModuleName;

        // ----- enumeration -----

        /// <summary>Every MODULE node of <paramref name="partConfig"/> named <paramref name="module"/>, in order.</summary>
        internal static List<ConfigNode> ModuleNodes(ConfigNode partConfig, string module)
        {
            var list = new List<ConfigNode>();
            if (partConfig == null) return list;
            foreach (ConfigNode m in partConfig.GetNodes("MODULE"))
                if (m != null && m.GetValue("name") == module) list.Add(m);
            return list;
        }

        /// <summary>The VARIANT names of the part's first <c>ModulePartVariants</c> node, in
        /// config order, empty and duplicate (case-insensitive) names dropped.</summary>
        internal static List<string> VariantNames(ConfigNode partConfig)
        {
            var names = new List<string>();
            List<ConfigNode> nodes = ModuleNodes(partConfig, VariantsModule);
            if (nodes.Count == 0) return names;
            foreach (ConfigNode v in nodes[0].GetNodes("VARIANT"))
                AddUniqueIgnoreCase(names, v?.GetValue("name"));
            return names;
        }

        /// <summary>Every <c>ModuleB9PartSwitch</c> node of the part, in config order (the
        /// prefab's module order), with its <c>moduleID</c> and SUBTYPE names.</summary>
        internal static List<PartParityB9Module> B9Modules(ConfigNode partConfig)
        {
            var list = new List<PartParityB9Module>();
            List<ConfigNode> nodes = ModuleNodes(partConfig, B9Module);
            for (int i = 0; i < nodes.Count; i++)
            {
                string id = nodes[i].GetValue(B9ModuleIdKey);
                var m = new PartParityB9Module { ModuleId = string.IsNullOrEmpty(id) ? null : id.Trim(), Index = i };
                foreach (ConfigNode st in nodes[i].GetNodes("SUBTYPE"))
                    AddUniqueOrdinal(m.Subtypes, st?.GetValue("name"));
                list.Add(m);
            }
            return list;
        }

        /// <summary>The case-key skin of one B9 subtype: <c>&lt;moduleID&gt;=&lt;subtype&gt;</c>,
        /// <c>b9=&lt;subtype&gt;</c> for an unnamed module. The case id maps <c>=</c> to <c>-</c>.</summary>
        internal static string B9SkinName(string moduleId, string subtype)
            => (string.IsNullOrEmpty(moduleId) ? B9UnnamedModuleLabel : moduleId) + B9SkinSeparator + (subtype ?? string.Empty);

        /// <summary>
        /// The skin cases beyond the base skin (ordinal 0, <see cref="PartParityCaseRules.DefaultCaseKey"/>)
        /// that <c>skins=all</c> adds, in ordinal order:
        /// <list type="number">
        /// <item>every VARIANT of the <c>ModulePartVariants</c> node other than
        /// <paramref name="baseVariant"/> (case-insensitive), in config order; none when the part
        /// has no live variant module (<paramref name="baseVariant"/> null);</item>
        /// <item>then, per <c>ModuleB9PartSwitch</c> module in prefab order, EVERY one of its
        /// SUBTYPEs, the default included (the base case authors no subtype, so the default's own
        /// case is the one that proves the authoring), each with every other module left at its
        /// base: one module at a time, never the cross product.</item>
        /// </list>
        /// A skin name already taken (a duplicate VARIANT / SUBTYPE) is dropped.
        /// </summary>
        internal static List<PartParitySkinOption> EnumerateSkinOptions(ConfigNode partConfig, string baseVariant)
        {
            var options = new List<PartParitySkinOption>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(baseVariant)) taken.Add(baseVariant);
            int ordinal = 1;
            if (!string.IsNullOrEmpty(baseVariant))
            {
                foreach (string v in VariantNames(partConfig))
                {
                    if (string.Equals(v, baseVariant, StringComparison.OrdinalIgnoreCase) || !taken.Add(v)) continue;
                    options.Add(new PartParitySkinOption { Module = VariantsModule, Value = v, Skin = v, Ordinal = ordinal++ });
                }
            }
            foreach (PartParityB9Module m in B9Modules(partConfig))
            {
                foreach (string st in m.Subtypes)
                {
                    string skin = B9SkinName(m.ModuleId, st);
                    if (!taken.Add(skin)) continue;
                    options.Add(new PartParitySkinOption
                    {
                        Module = B9Module, ModuleId = m.ModuleId, ModuleIndex = m.Index,
                        Value = st, Skin = skin, Ordinal = ordinal++,
                    });
                }
            }
            return options;
        }

        /// <summary>Every case key of one part: the base (default-skin) key, then one per skin option.</summary>
        internal static List<PartParityCaseKey> CaseKeys(PartParityPartEntry e)
        {
            var keys = new List<PartParityCaseKey> { PartParityCaseRules.DefaultCaseKey(e) };
            if (e?.SkinOptions == null) return keys;
            foreach (PartParitySkinOption o in e.SkinOptions)
            {
                keys.Add(new PartParityCaseKey
                {
                    Part = e.Name, Source = e.Source, Skin = o.Skin, SkinOrdinal = o.Ordinal, SkinOption = o,
                });
            }
            return keys;
        }

        /// <summary>Counts keys by skin kind (base, variant, B9).</summary>
        internal static PartParitySkinCounts Count(IEnumerable<PartParityCaseKey> keys)
        {
            var c = new PartParitySkinCounts();
            if (keys == null) return c;
            foreach (PartParityCaseKey k in keys)
            {
                if (k == null) continue;
                if (k.SkinOption == null) c.Base++;
                else if (k.SkinOption.Module == B9Module) c.B9++;
                else c.Variant++;
            }
            return c;
        }

        /// <summary>The step-start line of skin case counts: the install's cases by kind, then
        /// the cases the step's selection planned by kind.</summary>
        internal static string FormatCounts(string skins, PartParitySkinCounts all, PartParitySkinCounts planned)
        {
            all = all ?? new PartParitySkinCounts();
            planned = planned ?? new PartParitySkinCounts();
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "skin cases: skins=" + (skins ?? "default")
                + " install=" + all.Total.ToString(ic) + " (base=" + all.Base.ToString(ic)
                + " variant=" + all.Variant.ToString(ic) + " b9=" + all.B9.ToString(ic) + ")"
                + " planned=" + planned.Total.ToString(ic) + " (base=" + planned.Base.ToString(ic)
                + " variant=" + planned.Variant.ToString(ic) + " b9=" + planned.B9.ToString(ic) + ")";
        }

        /// <summary>The variant the real side is authored with: the option's own VARIANT, else
        /// the part's base variant (a B9 case leaves the variant at its base).</summary>
        internal static string RealVariant(PartParitySkinOption option, string baseVariant)
            => option != null && option.Module == VariantsModule ? option.Value : baseVariant;

        // ----- authoring -----

        /// <summary>
        /// Writes a B9 option into an authored PART node (the MODULE nodes in prefab order,
        /// <c>GhostCoOrbitalRig.AuthorPartNode</c>): the option's <c>moduleID</c> and
        /// <see cref="B9SubtypeKey"/> into the <see cref="PartParitySkinOption.ModuleIndex"/>-th
        /// <c>ModuleB9PartSwitch</c> MODULE node. False (nothing written) for a variant option
        /// or when the node has no such MODULE.
        /// </summary>
        internal static bool AuthorB9Subtype(ConfigNode partNode, PartParitySkinOption option)
        {
            if (partNode == null || option == null || option.Module != B9Module || option.ModuleIndex < 0) return false;
            List<ConfigNode> nodes = ModuleNodes(partNode, B9Module);
            if (option.ModuleIndex >= nodes.Count) return false;
            ConfigNode mn = nodes[option.ModuleIndex];
            if (!string.IsNullOrEmpty(option.ModuleId)) mn.SetValue(B9ModuleIdKey, option.ModuleId, true);
            mn.SetValue(B9SubtypeKey, option.Value, true);
            return true;
        }

        // ----- skin proof -----

        /// <summary>
        /// The skin proof's identity and request before anything is read back: an option names
        /// its module, module id and value; a base case of a variant part names
        /// <c>ModulePartVariants</c> and its base variant (authored, as P2 did); a base case of a
        /// B9-only part names its first B9 module with no request (the base case authors no
        /// subtype); any other part names nothing.
        /// </summary>
        internal static PartParitySkinProof NewProof(PartParitySkinOption option, string realVariant, IList<PartParityB9Module> b9Modules)
        {
            if (option != null)
                return new PartParitySkinProof { Module = option.Module, ModuleId = option.ModuleId, Requested = option.Value };
            if (realVariant != null)
                return new PartParitySkinProof { Module = VariantsModule, Requested = realVariant };
            if (b9Modules != null && b9Modules.Count > 0)
                return new PartParitySkinProof { Module = B9Module, ModuleId = b9Modules[0].ModuleId };
            return new PartParitySkinProof();
        }

        /// <summary>The proof a case that never ran can state (a crash record): a skin option's
        /// module, module id and value; else the key's skin as the request, as P2 wrote it.</summary>
        internal static PartParitySkinProof RequestOnlyProof(PartParityCaseKey key)
        {
            PartParitySkinOption o = key?.SkinOption;
            if (o != null) return new PartParitySkinProof { Module = o.Module, ModuleId = o.ModuleId, Requested = o.Value };
            return new PartParitySkinProof { Requested = key?.Skin };
        }

        /// <summary>
        /// The skin the snapshot (<c>TryBackupSnapshot</c>'s PART node) carries for the proof's
        /// module: the part's <c>moduleVariantName</c> for a variant; the
        /// <see cref="B9SubtypeKey"/> of the B9 MODULE node whose <c>moduleID</c> is the proof's
        /// (the first B9 node when the proof names none). Null when absent.
        /// </summary>
        internal static string SnapshotSkin(ConfigNode snapshotPart, PartParitySkinProof proof)
        {
            if (snapshotPart == null || proof == null || proof.Module == null) return null;
            if (proof.Module == VariantsModule) return snapshotPart.GetValue("moduleVariantName");
            if (proof.Module != B9Module) return null;
            ConfigNode node = FindB9Node(ModuleNodes(snapshotPart, B9Module), proof.ModuleId);
            return node != null ? node.GetValue(B9SubtypeKey) : null;
        }

        private static ConfigNode FindB9Node(List<ConfigNode> nodes, string moduleId)
        {
            if (nodes.Count == 0) return null;
            if (string.IsNullOrEmpty(moduleId)) return nodes[0];
            foreach (ConfigNode n in nodes)
                if (n.GetValue(B9ModuleIdKey) == moduleId) return n;
            return null;
        }

        /// <summary>The ghost skin of the proof's module from what the builder recorded: the
        /// variant it resolved; null for B9 (the builder applies no B9 subtype) and for a part
        /// it recorded nothing for.</summary>
        internal static string GhostSkin(PartParitySkinProof proof, string builderVariant)
            => proof != null && proof.Module == VariantsModule ? builderVariant : null;

        /// <summary>The ghost-defect flags of a proof whose case built a ghost:
        /// <c>skin-not-modelled:b9</c> for a B9 module the builder recorded no subtype for;
        /// otherwise <c>skin-mismatch</c> when the real skin is not the ghost's (variant names
        /// compare case-insensitively, the way the builder matches a VARIANT node). Empty for a
        /// proof that names no module.</summary>
        internal static List<string> GhostFlags(PartParitySkinProof proof)
        {
            var flags = new List<string>();
            if (proof == null || proof.Module == null) return flags;
            if (proof.Module == B9Module && proof.Ghost == null)
            {
                flags.Add(FlagSkinNotModelledPrefix + ModelledTokenB9);
                return flags;
            }
            if (!SkinNamesMatch(proof.Real, proof.Ghost)) flags.Add(FlagSkinMismatch);
            return flags;
        }

        /// <summary>The setup check: a requested skin the live module does not show (exact).</summary>
        internal static bool SkinNotApplied(PartParitySkinProof proof)
            => proof != null && proof.Requested != null && !string.Equals(proof.Real, proof.Requested, StringComparison.Ordinal);

        internal static bool SkinNamesMatch(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ----- live reads by reflection (plain objects, so xUnit drives them) -----

        /// <summary>
        /// A B9 module's current subtype by reflection: <see cref="B9SubtypeNameMember"/> (field
        /// or property, case ignored), else <see cref="B9SubtypeObjectMember"/>'s
        /// <see cref="B9SubtypeObjectNameMember"/>. <paramref name="member"/> names what was
        /// read (<c>property CurrentSubtypeName</c>, <c>property CurrentSubtype.Name</c>, ...),
        /// or <c>none</c>; a getter that throws reads null and names the exception.
        /// </summary>
        internal static string ReadB9Subtype(object module, out string member)
        {
            member = "none";
            if (module == null) return null;
            if (TryReadMember(module, B9SubtypeNameMember, out object v, out string m1))
            {
                member = m1;
                return v as string;
            }
            if (TryReadMember(module, B9SubtypeObjectMember, out object subtype, out string m2))
            {
                member = m2;
                if (subtype == null) return null;
                if (TryReadMember(subtype, B9SubtypeObjectNameMember, out object name, out string m3))
                {
                    member = m2 + "." + m3.Substring(m3.IndexOf(' ') + 1);
                    return name as string;
                }
                return null;
            }
            return null;
        }

        /// <summary>A string field or property of <paramref name="target"/> by name, case
        /// ignored (B9's <c>CustomPartModule.moduleID</c>); null when absent or not a string.</summary>
        internal static string ReadStringMember(object target, string name)
            => TryReadMember(target, name, out object v, out string _) ? v as string : null;

        private const BindingFlags MemberFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

        private static bool TryReadMember(object target, string name, out object value, out string member)
        {
            value = null;
            member = null;
            if (target == null || string.IsNullOrEmpty(name)) return false;
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = FindField(t, name);
                if (f != null)
                {
                    member = "field " + f.Name;
                    value = SafeGet(() => f.GetValue(target), ref member);
                    return true;
                }
                PropertyInfo p = FindProperty(t, name);
                if (p != null && p.GetIndexParameters().Length == 0 && p.CanRead)
                {
                    member = "property " + p.Name;
                    value = SafeGet(() => p.GetValue(target, null), ref member);
                    return true;
                }
            }
            return false;
        }

        private static FieldInfo FindField(Type t, string name)
        {
            foreach (FieldInfo f in t.GetFields(MemberFlags | BindingFlags.DeclaredOnly))
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        private static PropertyInfo FindProperty(Type t, string name)
        {
            foreach (PropertyInfo p in t.GetProperties(MemberFlags | BindingFlags.DeclaredOnly))
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private static object SafeGet(Func<object> get, ref string member)
        {
            try { return get(); }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                member += " (threw " + inner.GetType().Name + ")";
                return null;
            }
        }

        private static void AddUniqueIgnoreCase(List<string> list, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            string t = v.Trim();
            if (t.Length == 0) return;
            foreach (string s in list)
                if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(t);
        }

        private static void AddUniqueOrdinal(List<string> list, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            string t = v.Trim();
            if (t.Length > 0 && !list.Contains(t)) list.Add(t);
        }
    }
}
