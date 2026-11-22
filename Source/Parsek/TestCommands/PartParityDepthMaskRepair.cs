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

        /// <summary>The sidecar flag of a case whose real part needed and got the repair:
        /// <c>depth-mask-repaired:&lt;modules&gt;</c>.</summary>
        internal const string FlagRepairedPrefix = "depth-mask-repaired:";
        /// <summary>The sidecar flag of a case with a depth-mask module the repair could not
        /// bring to its shader: <c>depth-mask-unrepaired:&lt;modules&gt;</c>.</summary>
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
            /// <summary>Re-resolved and the update re-run; every mask now carries the module's shader.</summary>
            Repaired,
            /// <summary>Re-resolved and re-run, but a mask does not carry the module's shader.</summary>
            NotDepthShaded,
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
                case Outcome.NotDepthShaded: return "not-depth-shaded";
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

        /// <summary>The outcome after a re-run from what was resolved and how many masks carry
        /// the module's shader.</summary>
        internal static Outcome AfterRerun(int masksResolved, bool shaderResolved, int masksDepthShaded)
        {
            if (masksResolved == 0 || !shaderResolved) return Outcome.Unresolved;
            return masksDepthShaded == masksResolved ? Outcome.Repaired : Outcome.NotDepthShaded;
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
            internal int MasksDepthShaded;
            internal string Detail;
        }

        /// <summary>The flag a case carries for its results, or null when no module needed a
        /// repair (intact modules alone add no flag).</summary>
        internal static string CaseFlag(IList<ModuleResult> results)
        {
            if (results == null) return null;
            int repaired = 0, unrepaired = 0;
            foreach (ModuleResult r in results)
            {
                if (r == null || r.Outcome == Outcome.Intact) continue;
                if (r.Outcome == Outcome.Repaired) repaired++;
                else unrepaired++;
            }
            if (unrepaired > 0) return FlagUnrepairedPrefix + unrepaired.ToString(Ic);
            if (repaired > 0) return FlagRepairedPrefix + repaired.ToString(Ic);
            return null;
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
            int repaired = 0, intact = 0, other = 0;
            var sb = new StringBuilder();
            if (results != null)
            {
                foreach (ModuleResult r in results)
                {
                    if (r == null) continue;
                    if (r.Outcome == Outcome.Repaired) repaired++;
                    else if (r.Outcome == Outcome.Intact) intact++;
                    else other++;
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
                        sb.Append(" resolved=").Append(r.MasksResolved.ToString(Ic))
                            .Append(" depthShaded=").Append(r.MasksDepthShaded.ToString(Ic));
                    if (!string.IsNullOrEmpty(r.Detail)) sb.Append(" detail=").Append(r.Detail);
                }
            }
            int total = results?.Count ?? 0;
            return $"case {caseId}: depth-mask repair part={partName ?? "<null>"} modules={total.ToString(Ic)} " +
                   $"repaired={repaired.ToString(Ic)} intact={intact.ToString(Ic)} other={other.ToString(Ic)}" + sb;
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

        /// <summary>The masks whose renderer now carries the module's own shader. Compared by
        /// object, not by name: a patch may name its shader <c>Depth Mask</c> (ReStock's airScoop)
        /// where the default is <c>DepthMask</c>.</summary>
        private static int CountDepthShaded(Transform[] masks, Shader shader)
        {
            int n = 0;
            if (masks == null || shader == null) return 0;
            foreach (Transform t in masks)
            {
                if (t == null) continue;
                Renderer r = t.GetComponent<Renderer>();
                Material m = r != null ? r.sharedMaterial : null;
                if (m != null && m.shader == shader) n++;
            }
            return n;
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
            r.MasksDepthShaded = CountDepthShaded(resolved, shader);
            r.Outcome = AfterRerun(r.MasksResolved, true, r.MasksDepthShaded);
            return r;
        }
    }
}
