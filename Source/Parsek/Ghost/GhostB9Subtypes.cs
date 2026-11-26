using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    /// <summary>One <c>TRANSFORM</c> node of a B9PartSwitch SUBTYPE: the transforms it matches by
    /// name and the offsets it applies to each of them. A null offset is absent from the node.</summary>
    internal sealed class B9TransformModifier
    {
        internal B9NameMatcher Name;
        internal Vector3? PositionOffset;
        internal Vector3? RotationOffsetEuler;
        internal Vector3? ScaleOffset;
    }

    /// <summary>
    /// The subtype one <c>ModuleB9PartSwitch</c> module of a part shows, and what the ghost
    /// takes from it: its <c>TRANSFORM</c> offsets and the <c>transform</c> enable lists of
    /// every subtype of the module.
    /// </summary>
    internal sealed class B9ModuleSelection
    {
        /// <summary>The module's <c>moduleID</c>; null for a lone unnamed module.</summary>
        internal string ModuleId;
        /// <summary>The module's position among the part's B9 modules (config order).</summary>
        internal int Index;
        /// <summary>The subtype the module shows; null when the module has no SUBTYPE.</summary>
        internal string Subtype;
        /// <summary><c>snapshot</c> (the snapshot's <c>currentSubtype</c>), <c>default</c> (no
        /// readable snapshot value: B9's own default choice) or <c>snapshot-unknown</c> (the
        /// snapshot names a subtype the config does not have; the default is shown).</summary>
        internal string ResolutionPath;
        /// <summary>False when the shown subtype also carries rules the ghost does not model
        /// (<c>TEXTURE</c>, <c>MATERIAL</c>, <c>MODULE</c>).</summary>
        internal bool Modelled;
        internal readonly List<B9TransformModifier> TransformModifiers = new List<B9TransformModifier>();
        /// <summary>The <c>transform</c> names the shown subtype enables.</summary>
        internal readonly List<B9NameMatcher> CurrentTransforms = new List<B9NameMatcher>();
        /// <summary>The <c>transform</c> names every other subtype of the module enables.</summary>
        internal readonly List<B9NameMatcher> OtherTransforms = new List<B9NameMatcher>();
    }

    /// <summary>
    /// B9PartSwitch's transform-name matcher (decompiled <c>StringMatcher.Parse</c>,
    /// B9PartSwitch 2.21.0.4): <c>/regex/</c> is an unanchored regex, a leading <c>\/</c> escapes
    /// a literal slash, a name with <c>*</c> or <c>?</c> is an anchored wildcard, anything else
    /// matches exactly.
    /// </summary>
    internal sealed class B9NameMatcher
    {
        private readonly string exact;
        private readonly Regex regex;

        internal string Source { get; }

        private B9NameMatcher(string source, string exact, Regex regex)
        {
            Source = source;
            this.exact = exact;
            this.regex = regex;
        }

        internal static B9NameMatcher Parse(string str)
        {
            if (str == null) return null;
            try
            {
                if (str.Length > 2 && str[0] == '/' && str[str.Length - 1] == '/')
                    return new B9NameMatcher(str, null, new Regex(str.Substring(1, str.Length - 2)));
                string s = str;
                if (s.Length > 2 && s[0] == '\\' && s[1] == '/')
                    s = s.Substring(1);
                if (s.IndexOf('*') != -1 || s.IndexOf('?') != -1)
                {
                    string pattern = Regex.Escape(s).Replace("\\*", ".*").Replace("\\?", ".");
                    return new B9NameMatcher(str, null, new Regex("^" + pattern + "$"));
                }
                return new B9NameMatcher(str, s, null);
            }
            catch (ArgumentException)
            {
                // B9 itself fails to load such a subtype; the ghost matches nothing for it.
                return null;
            }
        }

        internal bool Match(string name)
        {
            if (name == null) return false;
            return regex != null ? regex.IsMatch(name) : string.Equals(exact, name, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Pure rules for drawing a B9PartSwitch part's ghost the way the live part looks
    /// (decompiled B9PartSwitch 2.21.0.4). B9 leaves the part prefab untouched (its
    /// <c>OnLoadPrefab</c> only validates; subtypes are applied in <c>OnStart</c>), so the ghost,
    /// which clones the prefab, starts from the unswitched model and applies the shown subtype of
    /// every module on top:
    /// <list type="bullet">
    /// <item>each <c>TRANSFORM</c> of the shown subtype, on every transform below the part's
    /// <c>model</c> root (the root itself excluded) whose name it matches: <c>positionOffset</c>
    /// is added to <c>localPosition</c>, <c>rotationOffset</c> (Euler degrees) is multiplied into
    /// <c>localRotation</c>, <c>scaleOffset</c> multiplies <c>localScale</c> per axis (one number
    /// means all three axes);</item>
    /// <item>the <c>transform</c> enable lists: a transform is shown when, for every B9 module of
    /// the part, the shown subtype lists it or no other subtype of that module does
    /// (<c>ModuleB9PartSwitch.TransformShouldBeEnabled</c>, ANDed over modules by
    /// <c>PartExtensions.UpdateTransformEnabled</c>).</item>
    /// </list>
    /// The shown subtype is the snapshot's <c>currentSubtype</c> (the persisted key) when it names
    /// a SUBTYPE of the module; otherwise the first subtype with the highest
    /// <c>defaultSubtypePriority</c>, B9's default for a module without tanks. A snapshot node is
    /// matched to a config module by <c>moduleID</c> (B9's <c>CustomPartModule</c> routing), and a
    /// module without one takes the snapshot's B9 node at its own position. Texture, material and
    /// module-data rules are not modelled: a subtype carrying any is reported unmodelled.
    /// </summary>
    internal static class GhostB9Subtypes
    {
        internal const string ModuleName = "ModuleB9PartSwitch";
        internal const string SubtypeNodeName = "SUBTYPE";
        internal const string SubtypeKey = "currentSubtype";
        internal const string ModuleIdKey = "moduleID";
        internal const string PathSnapshot = "snapshot";
        internal const string PathDefault = "default";
        internal const string PathSnapshotUnknown = "snapshot-unknown";

        private static readonly string[] UnmodelledSubtypeNodes = { "TEXTURE", "MATERIAL", "MODULE" };
        private static readonly char[] ScaleSplit = { ',', '\t', ' ' };

        /// <summary>The shown subtype of every B9 module of the part config, in config order.
        /// Empty when the config has no B9 module.</summary>
        internal static List<B9ModuleSelection> Resolve(ConfigNode partConfig, ConfigNode snapshotPart)
        {
            var result = new List<B9ModuleSelection>();
            List<ConfigNode> configModules = ModuleNodes(partConfig);
            if (configModules.Count == 0) return result;
            List<ConfigNode> snapModules = ModuleNodes(snapshotPart);

            for (int i = 0; i < configModules.Count; i++)
            {
                ConfigNode module = configModules[i];
                string id = Trimmed(module.GetValue(ModuleIdKey));
                var sel = new B9ModuleSelection { ModuleId = id, Index = i };
                ConfigNode[] subtypes = module.GetNodes(SubtypeNodeName);
                if (subtypes.Length == 0)
                {
                    result.Add(sel);
                    continue;
                }

                ConfigNode snapNode = FindSnapshotNode(snapModules, id, i);
                string requested = Trimmed(snapNode != null ? snapNode.GetValue(SubtypeKey) : null);
                int shown = requested != null ? IndexOfSubtype(subtypes, requested) : -1;
                if (shown >= 0)
                    sel.ResolutionPath = PathSnapshot;
                else
                {
                    shown = DefaultSubtypeIndex(subtypes);
                    sel.ResolutionPath = requested != null ? PathSnapshotUnknown : PathDefault;
                }

                ConfigNode current = subtypes[shown];
                sel.Subtype = Trimmed(current.GetValue("name"));
                sel.Modelled = !HasUnmodelledRules(current);
                foreach (ConfigNode t in current.GetNodes("TRANSFORM"))
                {
                    B9TransformModifier mod = ParseTransformModifier(t);
                    if (mod != null) sel.TransformModifiers.Add(mod);
                }
                for (int s = 0; s < subtypes.Length; s++)
                {
                    List<B9NameMatcher> into = s == shown ? sel.CurrentTransforms : sel.OtherTransforms;
                    foreach (string name in subtypes[s].GetValues("transform"))
                    {
                        B9NameMatcher m = B9NameMatcher.Parse(Trimmed(name));
                        if (m != null) into.Add(m);
                    }
                }
                result.Add(sel);
            }
            return result;
        }

        /// <summary>The transforms, out of <paramref name="candidateNames"/> (the names below the
        /// part's model root), that the shown subtypes hide.</summary>
        internal static HashSet<string> HiddenTransformNames(
            List<B9ModuleSelection> modules, IEnumerable<string> candidateNames)
        {
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            if (modules == null || modules.Count == 0 || candidateNames == null) return hidden;
            foreach (string name in candidateNames)
            {
                if (name == null || hidden.Contains(name)) continue;
                if (!IsTransformShown(modules, name)) hidden.Add(name);
            }
            return hidden;
        }

        /// <summary>B9's enable rule for one transform name, ANDed over modules.</summary>
        internal static bool IsTransformShown(List<B9ModuleSelection> modules, string name)
        {
            if (modules == null) return true;
            for (int i = 0; i < modules.Count; i++)
            {
                B9ModuleSelection m = modules[i];
                if (AnyMatch(m.CurrentTransforms, name)) continue;
                if (AnyMatch(m.OtherTransforms, name)) return false;
            }
            return true;
        }

        /// <summary>The offsets to apply to a transform named <paramref name="name"/>, in the order
        /// B9 applies them (module order, then TRANSFORM node order). Empty when none matches.</summary>
        internal static List<B9TransformModifier> ModifiersFor(List<B9ModuleSelection> modules, string name)
        {
            var list = new List<B9TransformModifier>();
            if (modules == null || name == null) return list;
            foreach (B9ModuleSelection m in modules)
                foreach (B9TransformModifier mod in m.TransformModifiers)
                    if (mod.Name != null && mod.Name.Match(name)) list.Add(mod);
            return list;
        }

        /// <summary><paramref name="scale"/> multiplied per axis by the scale offset; the input
        /// unchanged for a null offset.</summary>
        internal static Vector3 ApplyScale(Vector3 scale, Vector3? offset)
        {
            if (!offset.HasValue) return scale;
            Vector3 o = offset.Value;
            return new Vector3(scale.x * o.x, scale.y * o.y, scale.z * o.z);
        }

        /// <summary><paramref name="position"/> plus the position offset.</summary>
        internal static Vector3 ApplyPosition(Vector3 position, Vector3? offset)
        {
            if (!offset.HasValue) return position;
            Vector3 o = offset.Value;
            return new Vector3(position.x + o.x, position.y + o.y, position.z + o.z);
        }

        /// <summary>One <c>moduleID=subtype(path)</c> token per module for the build log.</summary>
        internal static string Describe(List<B9ModuleSelection> modules)
        {
            if (modules == null || modules.Count == 0) return "<none>";
            var parts = new List<string>(modules.Count);
            foreach (B9ModuleSelection m in modules)
                parts.Add((m.ModuleId ?? "b9") + "=" + (m.Subtype ?? "<none>") + "(" + (m.ResolutionPath ?? "none")
                    + (m.Modelled ? "" : ",unmodelled") + ")");
            return string.Join(" ", parts.ToArray());
        }

        internal static B9TransformModifier ParseTransformModifier(ConfigNode node)
        {
            if (node == null) return null;
            B9NameMatcher name = B9NameMatcher.Parse(Trimmed(node.GetValue("name")));
            if (name == null) return null;
            var mod = new B9TransformModifier
            {
                Name = name,
                PositionOffset = ParseVector(node.GetValue("positionOffset")),
                RotationOffsetEuler = ParseVector(node.GetValue("rotationOffset")),
                ScaleOffset = ParseScale(node.GetValue("scaleOffset")),
            };
            if (!mod.PositionOffset.HasValue && !mod.RotationOffsetEuler.HasValue && !mod.ScaleOffset.HasValue)
                return null;
            return mod;
        }

        /// <summary>B9's <c>ScaleParser</c>: one number for all three axes, or three numbers split
        /// on commas, tabs or spaces. Null when absent or unreadable.</summary>
        internal static Vector3? ParseScale(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string[] a = value.Split(ScaleSplit, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 1 && TryFloat(a[0], out float v)) return new Vector3(v, v, v);
            if (a.Length == 3 && TryFloat(a[0], out float x) && TryFloat(a[1], out float y) && TryFloat(a[2], out float z))
                return new Vector3(x, y, z);
            return null;
        }

        /// <summary>A comma-separated three-component vector (<c>ConfigNode.ParseVector3</c>'s
        /// shape). Null when absent or unreadable.</summary>
        internal static Vector3? ParseVector(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string[] a = value.Split(',');
            if (a.Length == 3 && TryFloat(a[0], out float x) && TryFloat(a[1], out float y) && TryFloat(a[2], out float z))
                return new Vector3(x, y, z);
            return null;
        }

        private static bool TryFloat(string s, out float v)
            => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
               && !float.IsNaN(v) && !float.IsInfinity(v);

        private static bool AnyMatch(List<B9NameMatcher> matchers, string name)
        {
            for (int i = 0; i < matchers.Count; i++)
                if (matchers[i].Match(name)) return true;
            return false;
        }

        private static bool HasUnmodelledRules(ConfigNode subtype)
        {
            for (int i = 0; i < UnmodelledSubtypeNodes.Length; i++)
                if (subtype.HasNode(UnmodelledSubtypeNodes[i])) return true;
            return false;
        }

        private static int IndexOfSubtype(ConfigNode[] subtypes, string name)
        {
            for (int i = 0; i < subtypes.Length; i++)
                if (string.Equals(Trimmed(subtypes[i].GetValue("name")), name, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>The first subtype with the highest <c>defaultSubtypePriority</c> (absent = 0).</summary>
        internal static int DefaultSubtypeIndex(ConfigNode[] subtypes)
        {
            int best = 0;
            float bestPriority = float.NegativeInfinity;
            for (int i = 0; i < subtypes.Length; i++)
            {
                float p = 0f;
                string raw = subtypes[i].GetValue("defaultSubtypePriority");
                if (raw != null && !TryFloat(raw, out p)) p = 0f;
                if (p > bestPriority)
                {
                    bestPriority = p;
                    best = i;
                }
            }
            return best;
        }

        private static ConfigNode FindSnapshotNode(List<ConfigNode> snapModules, string moduleId, int index)
        {
            if (snapModules.Count == 0) return null;
            if (moduleId != null)
            {
                foreach (ConfigNode n in snapModules)
                    if (string.Equals(Trimmed(n.GetValue(ModuleIdKey)), moduleId, StringComparison.Ordinal)) return n;
                return null;
            }
            return index < snapModules.Count ? snapModules[index] : null;
        }

        private static List<ConfigNode> ModuleNodes(ConfigNode part)
        {
            var list = new List<ConfigNode>();
            if (part == null) return list;
            foreach (ConfigNode m in part.GetNodes("MODULE"))
                if (m != null && m.GetValue("name") == ModuleName) list.Add(m);
            return list;
        }

        private static string Trimmed(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
