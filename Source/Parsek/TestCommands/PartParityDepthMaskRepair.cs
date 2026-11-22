using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The sweep's real-side depth-mask repair (design-part-ghost-parity.md 4.2, "Depth masks on
    /// the real part"). A ReStock-style depth-mask module (any part module whose name contains
    /// <c>DepthMask</c>, the same rule as <c>GhostVisualBuilder.GetDepthMaskTransformNames</c>)
    /// resolves its mask and body transforms and its depth shader in <c>OnLoad</c> ONLY while
    /// the part prefab is compiled (scene neither editor nor flight) and relies on the prefab's
    /// field values reaching every live part; its <c>OnStart</c> then gives the mask mesh the
    /// depth-only shader at queue 1999 and moves the body renderers ahead of it. Every sweep case
    /// part is its vessel's ROOT part, and stock <c>ProtoPartSnapshot.Load(loadAsRootPart: true)</c>
    /// rebuilds a root part on the vessel's GameObject: fresh modules, public fields copied from
    /// the instantiated clone, the clone then destroyed in <c>ConfigurePart</c>. The copied body
    /// reference (the part's own transform for an empty <c>bodyTransform</c>) names that
    /// destroyed clone root, so <c>OnStart</c> throws and the mask keeps its authored placeholder
    /// material, which photographs as an opaque cyan disc: the real picture is not the part a
    /// player sees on a non-root part.
    ///
    /// The repair runs inside the sweep capture only (never on a production path), on the live
    /// real part after it settled: it classifies each reference, and when one is dead it
    /// resolves the transforms on the live part exactly as the module's own prefab-time
    /// <c>OnLoad</c> does and re-runs the module's material update. No compile-time reference
    /// to any mod: the module, its fields and its update method are found by name.
    ///
    /// The module's own <c>onVariantApplied</c> / <c>onPartRepaired</c> handlers re-run the
    /// same update over every renderer under the part, which once the ghost is parented under
    /// the real part (design 4.2 step 3) would include the ghost's. They cannot fire there: a
    /// module repaired for a dead transform threw in <c>OnStart</c> BEFORE subscribing them (ReStock:
    /// <c>UpdateAllMaterials()</c> precedes both <c>Add</c> calls), the repair subscribes
    /// nothing, and nothing on the capture path applies a variant or a repair to a real part
    /// (<c>PartParityDepthMaskEventGateTests</c> reds on a capture-path file naming either
    /// event, <c>SetVariant</c> or <c>ApplyVariant</c>).
    /// </summary>
    internal static class PartParityDepthMaskRepair
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal const string ModuleNameMarker = "DepthMask";
        internal const string FieldMaskName = "maskTransform";
        internal const string FieldBodyName = "bodyTransform";
        internal const string FieldShaderName = "shaderName";
        internal const string FieldMaskObjects = "maskTransformObjects";
        internal const string FieldBodyObject = "bodyTransformObject";
        internal const string FieldDepthShader = "depthShader";
        internal const string MethodUpdate = "UpdateAllMaterials";

        /// <summary>The sidecar flag counting the modules the repair fixed:
        /// <c>depth-mask-repaired:&lt;count&gt;</c>.</summary>
        internal const string FlagRepairedPrefix = "depth-mask-repaired:";
        /// <summary>The sidecar flag counting the modules that needed the repair and did not
        /// get it: <c>depth-mask-unrepaired:&lt;count&gt;</c>. Such a case's setup status is
        /// <c>TestCommandPartParitySweep.SetupDepthMaskUnrepaired</c>, a setup fault (its real
        /// picture is not the part a player sees, so it is no finding about the ghost).</summary>
        internal const string FlagUnrepairedPrefix = "depth-mask-unrepaired:";

        /// <summary>The state of one Unity object reference on a module.</summary>
        internal enum RefState
        {
            /// <summary>The managed reference is null: the field never held an object.</summary>
            Null,
            /// <summary>A managed wrapper whose native object is gone (Unity's fake null).</summary>
            Destroyed,
            /// <summary>A live object under the module's own part.</summary>
            InPart,
            /// <summary>A live object that is not under the module's own part.</summary>
            OutsidePart,
            /// <summary>A live object with no hierarchy (an asset such as a shader).</summary>
            Alive,
        }

        /// <summary>What the repair did to one module.</summary>
        internal enum Outcome
        {
            /// <summary>Every reference was live and in the part: nothing done.</summary>
            Intact,
            /// <summary>Re-resolved on the live part and the module's update re-ran without
            /// throwing (the update sets the shader on every mask's renderer, or throws).</summary>
            Repaired,
            /// <summary>The live part has no transform of the mask's name, or no depth shader.</summary>
            Unresolved,
            /// <summary>The module lacks a member of the ReStock shape: left alone.</summary>
            Unsupported,
            /// <summary>The re-run update threw.</summary>
            Threw,
        }

        internal static string Token(RefState s)
        {
            switch (s)
            {
                case RefState.Null: return "null";
                case RefState.Destroyed: return "destroyed";
                case RefState.InPart: return "in-part";
                case RefState.OutsidePart: return "outside-part";
                default: return "alive";
            }
        }

        internal static string Token(Outcome o)
        {
            switch (o)
            {
                case Outcome.Intact: return "intact";
                case Outcome.Repaired: return "repaired";
                case Outcome.Unresolved: return "unresolved";
                case Outcome.Unsupported: return "unsupported";
                default: return "threw";
            }
        }

        /// <summary>A part module the repair looks at: its name contains <c>DepthMask</c>,
        /// case ignored (the ghost builder's depth-mask module rule).</summary>
        internal static bool IsDepthMaskModuleName(string moduleName)
            => !string.IsNullOrEmpty(moduleName)
               && moduleName.IndexOf(ModuleNameMarker, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The state of a reference from the three facts a live check reads.</summary>
        internal static RefState Classify(bool managedNull, bool unityNull, bool hasHierarchy, bool underPart)
        {
            if (managedNull) return RefState.Null;
            if (unityNull) return RefState.Destroyed;
            if (!hasHierarchy) return RefState.Alive;
            return underPart ? RefState.InPart : RefState.OutsidePart;
        }

        /// <summary>True when the module's own update would not do its job on this part: the
        /// body or any mask is not a live transform under the part, there is no mask, or the
        /// shader is not a live object.</summary>
        internal static bool NeedsRepair(RefState body, IList<RefState> masks, RefState shader)
        {
            if (body != RefState.InPart) return true;
            if (masks == null || masks.Count == 0) return true;
            foreach (RefState m in masks) if (m != RefState.InPart) return true;
            return shader != RefState.Alive;
        }

        /// <summary>The module's prefab-time body rule: an empty <c>bodyTransform</c> means the
        /// part's own transform; a named one that is not found falls back to it too.</summary>
        internal static bool UsePartTransformForBody(string bodyTransformName, bool namedFound)
            => string.IsNullOrEmpty(bodyTransformName) || !namedFound;

        /// <summary>True for an outcome where the module needed the repair and did not get it.</summary>
        internal static bool IsUnrepaired(Outcome o) => o != Outcome.Intact && o != Outcome.Repaired;

        /// <summary>The modules the repair fixed and the ones that needed it and did not get it.</summary>
        internal static void Count(IList<ModuleResult> results, out int repaired, out int unrepaired)
        {
            repaired = 0;
            unrepaired = 0;
            if (results == null) return;
            foreach (ModuleResult r in results)
            {
                if (r == null) continue;
                if (r.Outcome == Outcome.Repaired) repaired++;
                else if (IsUnrepaired(r.Outcome)) unrepaired++;
            }
        }

        /// <summary>One module's result, as the summary line and the flags read it.</summary>
        internal sealed class ModuleResult
        {
            internal int Index;
            internal string Module;
            internal string Mask;
            internal RefState Body;
            internal List<RefState> Masks = new List<RefState>();
            internal RefState Shader;
            internal RefState? PrefabBody;
            internal List<RefState> PrefabMasks;
            internal RefState? PrefabShader;
            internal Outcome Outcome;
            internal int MasksResolved;
            internal string Detail;
        }

        /// <summary>The flags a case carries for its results, each with its count: one per
        /// nonzero count, repaired first; none when every module was intact.</summary>
        internal static List<string> CaseFlags(IList<ModuleResult> results)
        {
            Count(results, out int repaired, out int unrepaired);
            var flags = new List<string>();
            if (repaired > 0) flags.Add(FlagRepairedPrefix + repaired.ToString(Ic));
            if (unrepaired > 0) flags.Add(FlagUnrepairedPrefix + unrepaired.ToString(Ic));
            return flags;
        }

        /// <summary>The setup detail of a case with an unrepaired module: each such module's
        /// index, name, outcome and detail; null when none is unrepaired.</summary>
        internal static string UnrepairedDetail(IList<ModuleResult> results)
        {
            if (results == null) return null;
            var sb = new StringBuilder();
            foreach (ModuleResult r in results)
            {
                if (r == null || !IsUnrepaired(r.Outcome)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append('m').Append(r.Index.ToString(Ic)).Append('=').Append(r.Module ?? "<null>")
                    .Append(':').Append(Token(r.Outcome));
                if (!string.IsNullOrEmpty(r.Detail)) sb.Append(':').Append(r.Detail);
            }
            return sb.Length > 0 ? "depth-mask " + sb : null;
        }

        private static string States(IList<RefState> states)
        {
            if (states == null) return "<none>";
            if (states.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < states.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Token(states[i]));
            }
            return sb.Append(']').ToString();
        }

        /// <summary>The one summary line per case (house logging rule: one line after the loop).</summary>
        internal static string SummaryLine(string caseId, string partName, IList<ModuleResult> results)
        {
            Count(results, out int repaired, out int unrepaired);
            int intact = 0;
            var sb = new StringBuilder();
            if (results != null)
            {
                foreach (ModuleResult r in results)
                {
                    if (r == null) continue;
                    if (r.Outcome == Outcome.Intact) intact++;
                    sb.Append(" | m").Append(r.Index.ToString(Ic)).Append('=').Append(r.Module ?? "<null>")
                        .Append(" mask=").Append(string.IsNullOrEmpty(r.Mask) ? "<none>" : r.Mask)
                        .Append(" outcome=").Append(Token(r.Outcome))
                        .Append(" live: body=").Append(Token(r.Body))
                        .Append(" masks=").Append(States(r.Masks))
                        .Append(" shader=").Append(Token(r.Shader))
                        .Append(" prefab: body=").Append(r.PrefabBody.HasValue ? Token(r.PrefabBody.Value) : "<none>")
                        .Append(" masks=").Append(States(r.PrefabMasks))
                        .Append(" shader=").Append(r.PrefabShader.HasValue ? Token(r.PrefabShader.Value) : "<none>");
                    if (r.Outcome != Outcome.Intact && r.Outcome != Outcome.Unsupported)
                        sb.Append(" resolved=").Append(r.MasksResolved.ToString(Ic));
                    if (!string.IsNullOrEmpty(r.Detail)) sb.Append(" detail=").Append(r.Detail);
                }
            }
            int total = results?.Count ?? 0;
            return $"case {caseId}: depth-mask repair part={partName ?? "<null>"} modules={total.ToString(Ic)} " +
                   $"repaired={repaired.ToString(Ic)} unrepaired={unrepaired.ToString(Ic)} intact={intact.ToString(Ic)}" + sb;
        }

        // ----- live -----

        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static FieldInfo FindField(Type t, string name)
        {
            for (Type x = t; x != null && x != typeof(PartModule); x = x.BaseType)
            {
                FieldInfo f = x.GetField(name, Inst | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static MethodInfo FindUpdate(Type t)
        {
            for (Type x = t; x != null && x != typeof(PartModule); x = x.BaseType)
            {
                MethodInfo m = x.GetMethod(MethodUpdate, Inst | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null) return m;
            }
            return null;
        }

        private static RefState ClassifyLive(UnityEngine.Object o, Transform partRoot)
        {
            bool managedNull = ReferenceEquals(o, null);
            bool unityNull = !managedNull && o == null;
            if (managedNull || unityNull) return Classify(managedNull, unityNull, false, false);
            Transform t = o as Transform;
            if (t == null) return Classify(false, false, false, false);
            return Classify(false, false, true, partRoot != null && t.IsChildOf(partRoot));
        }

        private static List<RefState> ClassifyAll(Transform[] arr, Transform partRoot)
        {
            var list = new List<RefState>();
            if (arr == null) return list;
            foreach (Transform t in arr) list.Add(ClassifyLive(t, partRoot));
            return list;
        }

        /// <summary>
        /// Checks every depth-mask module of the live real part and repairs the dead ones.
        /// Returns one result per depth-mask module, in module order; an empty list for a part
        /// with none (every stock part), and then nothing on the part is read beyond the
        /// module names.
        /// </summary>
        internal static List<ModuleResult> Repair(Part part)
        {
            var results = new List<ModuleResult>();
            if (part == null || part.Modules == null) return results;
            Part prefab = part.partInfo != null ? part.partInfo.partPrefab : null;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule m = part.Modules[i];
                if (m == null) continue;
                string name = m.moduleName ?? m.GetType().Name;
                if (!IsDepthMaskModuleName(name)) continue;
                results.Add(RepairModule(part, prefab, m, i, name));
            }
            return results;
        }

        private static ModuleResult RepairModule(Part part, Part prefab, PartModule m, int index, string name)
        {
            var r = new ModuleResult { Index = index, Module = name };
            Type t = m.GetType();
            FieldInfo fMask = FindField(t, FieldMaskName), fBody = FindField(t, FieldBodyName),
                fShaderName = FindField(t, FieldShaderName), fMasks = FindField(t, FieldMaskObjects),
                fBodyObj = FindField(t, FieldBodyObject), fShader = FindField(t, FieldDepthShader);
            MethodInfo update = FindUpdate(t);
            if (fMask == null || fMasks == null || fBodyObj == null || fShader == null || fShaderName == null
                || update == null || fMask.FieldType != typeof(string) || fMasks.FieldType != typeof(Transform[])
                || fBodyObj.FieldType != typeof(Transform) || !typeof(Shader).IsAssignableFrom(fShader.FieldType))
            {
                r.Outcome = Outcome.Unsupported;
                r.Detail = "members-missing";
                return r;
            }
            r.Mask = fMask.GetValue(m) as string;
            Transform root = part.transform;
            var masks = fMasks.GetValue(m) as Transform[];
            r.Body = ClassifyLive(fBodyObj.GetValue(m) as UnityEngine.Object, root);
            r.Masks = ClassifyAll(masks, root);
            r.Shader = ClassifyLive(fShader.GetValue(m) as UnityEngine.Object, root);

            // The prefab module of the same type at the same index: which side of the
            // prefab-to-live copy lost the references (the evidence line of the todo entry).
            PartModule pm = prefab != null && prefab.Modules != null && index < prefab.Modules.Count
                ? prefab.Modules[index] : null;
            if (pm != null && pm.GetType() == t)
            {
                Transform proot = prefab.transform;
                r.PrefabBody = ClassifyLive(fBodyObj.GetValue(pm) as UnityEngine.Object, proot);
                r.PrefabMasks = ClassifyAll(fMasks.GetValue(pm) as Transform[], proot);
                r.PrefabShader = ClassifyLive(fShader.GetValue(pm) as UnityEngine.Object, proot);
            }

            if (!NeedsRepair(r.Body, r.Masks, r.Shader))
            {
                r.Outcome = Outcome.Intact;
                return r;
            }

            // The module's own prefab-time OnLoad, on the live part.
            Transform[] resolved = string.IsNullOrEmpty(r.Mask) ? new Transform[0] : part.FindModelTransforms(r.Mask);
            string bodyName = fBody != null ? fBody.GetValue(m) as string : null;
            Transform named = string.IsNullOrEmpty(bodyName) ? null : part.FindModelTransform(bodyName);
            Transform body = UsePartTransformForBody(bodyName, named != null) ? part.partTransform : named;
            Shader shader = fShader.GetValue(m) as Shader;
            if (shader == null)
            {
                string shaderName = fShaderName.GetValue(m) as string;
                shader = string.IsNullOrEmpty(shaderName) ? null : Shader.Find(shaderName);
            }
            r.MasksResolved = resolved?.Length ?? 0;
            if (r.MasksResolved == 0 || shader == null || body == null)
            {
                r.Outcome = Outcome.Unresolved;
                r.Detail = r.MasksResolved == 0 ? "mask-not-found" : shader == null ? "shader-not-found" : "body-not-found";
                return r;
            }
            fMasks.SetValue(m, resolved);
            fBodyObj.SetValue(m, body);
            fShader.SetValue(m, shader);
            try
            {
                update.Invoke(m, null);
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                r.Outcome = Outcome.Threw;
                r.Detail = inner.GetType().Name;
                return r;
            }
            r.Outcome = Outcome.Repaired;
            return r;
        }
    }
}
