using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace Parsek
{
    /// <summary>One animation clip the live part holds at a fixed rest time.</summary>
    internal struct AnimationRestCandidate
    {
        public string moduleName;
        public string animationName;
        public float restNormalizedTime;
    }

    internal static partial class GhostVisualBuilder
    {
        private struct AnimatedColorSample
        {
            public int rendererIndex;
            public int slot;
            public string property;
            public Color cold;
            public Color medium;
            public Color hot;
            public Color rest;
        }

        private sealed class AnimationRestSample
        {
            public bool found;
            public bool movesTransforms;
            public List<AnimatedColorSample> colors = new List<AnimatedColorSample>();
            public List<KeyValuePair<int, bool>> activeStates = new List<KeyValuePair<int, bool>>();
        }

        // partKey|anim|rest -> the clip's material / active-state values at rest. Prefab-derived,
        // cleared with the other animation caches in ClearAnimateHeatCache.
        private static readonly Dictionary<string, AnimationRestSample> animationRestSampleCache =
            new Dictionary<string, AnimationRestSample>();

        internal const string AnimationRestModuleAnimateThrottle = "FXModuleAnimateThrottle";
        internal const string AnimationRestModuleAnimateGeneric = "ModuleAnimateGeneric";

        /// <summary>
        /// The clips a live part holds at a known rest time (KSP 1.12.5, decompiled):
        /// FXModuleAnimateThrottle samples its clip at the engine's level, 0 with the engine
        /// off; ModuleAnimateGeneric's OnStart sets <c>normalizedTime = animTime</c> (persisted,
        /// default 0, matched to the snapshot module by declaration order and clamped to
        /// [0, 1]). A clip named twice is listed once (first module wins).
        /// </summary>
        internal static List<AnimationRestCandidate> CollectAnimationRestCandidates(
            ConfigNode prefabPartConfig, ConfigNode snapshotPartNode)
        {
            var result = new List<AnimationRestCandidate>();
            if (prefabPartConfig == null) return result;
            ConfigNode[] modules = prefabPartConfig.GetNodes("MODULE");
            if (modules == null) return result;

            var snapshotGeneric = new List<ConfigNode>();
            ConfigNode[] snapshotModules = snapshotPartNode?.GetNodes("MODULE");
            if (snapshotModules != null)
            {
                for (int i = 0; i < snapshotModules.Length; i++)
                    if (snapshotModules[i]?.GetValue("name") == AnimationRestModuleAnimateGeneric)
                        snapshotGeneric.Add(snapshotModules[i]);
            }

            var seen = new HashSet<string>();
            int genericIndex = 0;
            for (int m = 0; m < modules.Length; m++)
            {
                if (modules[m] == null) continue;
                string name = modules[m].GetValue("name");
                float rest;
                if (name == AnimationRestModuleAnimateThrottle)
                {
                    rest = 0f;
                }
                else if (name == AnimationRestModuleAnimateGeneric)
                {
                    ConfigNode snapshot = genericIndex < snapshotGeneric.Count ? snapshotGeneric[genericIndex] : null;
                    genericIndex++;
                    rest = ResolveAnimateGenericRestTime(snapshot?.GetValue("animTime"));
                }
                else
                {
                    continue;
                }

                string anim = modules[m].GetValue("animationName");
                if (string.IsNullOrEmpty(anim) || !seen.Add(anim)) continue;
                result.Add(new AnimationRestCandidate
                {
                    moduleName = name,
                    animationName = anim,
                    restNormalizedTime = rest,
                });
            }
            return result;
        }

        /// <summary>The persisted animTime clamped to [0, 1]; missing or unparsable is 0.</summary>
        internal static float ResolveAnimateGenericRestTime(string animTime)
        {
            if (string.IsNullOrEmpty(animTime)
                || !float.TryParse(animTime, NumberStyles.Float, CultureInfo.InvariantCulture, out float t)
                || float.IsNaN(t))
                return 0f;
            return t < 0f ? 0f : (t > 1f ? 1f : t);
        }

        /// <summary>
        /// A colour the clip animates: it differs between the clip's two ends. A property the
        /// clip holds constant, or does not touch, reads the same at both ends and is skipped.
        /// </summary>
        internal static bool IsAnimatedColor(Color atStart, Color atEnd)
        {
            const float eps = 1e-4f;
            return System.Math.Abs(atStart.r - atEnd.r) > eps || System.Math.Abs(atStart.g - atEnd.g) > eps
                || System.Math.Abs(atStart.b - atEnd.b) > eps || System.Math.Abs(atStart.a - atEnd.a) > eps;
        }

        /// <summary>
        /// Writes the rest state of the part's material / active-state animation clips onto the
        /// ghost, once, at build time. Only clips that move NO transform between their two ends
        /// are applied (ionEngine 'colorAnimation', ReStock SurfaceScanner 'screenon'): a clip
        /// that moves transforms belongs to the deployable / heat pose sampling, which stays the
        /// only pose authority, so nothing here ever writes a transform. The values are read on a
        /// temporary prefab clone and written as plain material colours (a clone per drawn
        /// material, reused when another ghost info already owns it), never as a property block,
        /// so later material writes by the heat, light and colour-changer drivers still show.
        /// A (material, property) a colour changer or light lens / flare drives is left to it.
        /// For the FXModuleAnimateThrottle clip that is also the part's heat animation, each
        /// animated colour joins the heat info (cold / medium / hot = the clip at 0 / 0.5 / 1),
        /// so a running engine's ghost still lights the way the real one does.
        /// </summary>
        internal static void ApplyAnimationRestStates(
            Part prefab, ConfigNode partNode, Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap, string heatAnimName,
            ref HeatGhostInfo heatInfo, LightGhostInfo lightInfo,
            List<ColorChangerGhostInfo> colorChangerInfos, uint persistentId, string partName)
        {
            if (prefab == null || modelRoot == null || ghostModelNode == null || cloneMap == null)
                return;
            List<AnimationRestCandidate> candidates =
                CollectAnimationRestCandidates(prefab.partInfo?.partConfig, partNode);
            if (candidates.Count == 0) return;

            string partKey = prefab.partInfo?.name ?? prefab.name;
            Renderer[] modelRenderers = null;
            Transform[] modelTransforms = null;
            var owned = CollectPartOwnedMaterialProperties(heatInfo, lightInfo, colorChangerInfos);
            var ownedMaterials = new HashSet<Material>(owned.Keys);

            int applied = 0, skippedMovesTransforms = 0, skippedNoClip = 0;
            int colorsWritten = 0, colorsLeftToOwner = 0, activeWritten = 0, heatStates = 0;
            for (int c = 0; c < candidates.Count; c++)
            {
                AnimationRestCandidate candidate = candidates[c];
                AnimationRestSample sample = GetAnimationRestSample(
                    prefab, modelRoot, partKey, candidate.animationName, candidate.restNormalizedTime);
                if (sample == null || !sample.found)
                {
                    skippedNoClip++;
                    continue;
                }
                if (sample.movesTransforms)
                {
                    skippedMovesTransforms++;
                    continue;
                }
                applied++;

                if (modelRenderers == null)
                {
                    modelRenderers = modelRoot.GetComponentsInChildren<Renderer>(true);
                    modelTransforms = modelRoot.GetComponentsInChildren<Transform>(true);
                }

                bool feedsHeat = candidate.moduleName == AnimationRestModuleAnimateThrottle
                    && candidate.animationName == heatAnimName;
                for (int i = 0; i < sample.colors.Count; i++)
                {
                    AnimatedColorSample s = sample.colors[i];
                    if (s.rendererIndex >= modelRenderers.Length) continue;
                    Renderer ghostRenderer = ResolveGhostRenderer(modelRenderers[s.rendererIndex], modelRoot,
                        ghostModelNode, cloneMap);
                    if (ghostRenderer == null) continue;
                    Material[] slots = ghostRenderer.sharedMaterials;
                    if (slots == null || s.slot >= slots.Length || slots[s.slot] == null) continue;
                    Material material = slots[s.slot];
                    if (!material.HasProperty(s.property)) continue;
                    if (owned.TryGetValue(material, out HashSet<string> props) && props.Contains(s.property))
                    {
                        colorsLeftToOwner++;
                        continue;
                    }
                    if (!ownedMaterials.Contains(material))
                    {
                        material = new Material(material);
                        ownedMaterials.Add(material);
                        slots[s.slot] = material;
                        ghostRenderer.materials = slots;
                    }
                    material.SetColor(s.property, s.rest);
                    colorsWritten++;

                    if (feedsHeat)
                    {
                        if (heatInfo == null)
                            heatInfo = new HeatGhostInfo { partPersistentId = persistentId };
                        if (heatInfo.materialStates == null)
                            heatInfo.materialStates = new List<HeatMaterialState>();
                        heatInfo.materialStates.Add(new HeatMaterialState
                        {
                            material = material,
                            colorProperty = s.property,
                            coldColor = s.cold,
                            mediumColor = s.medium,
                            hotColor = s.hot,
                        });
                        heatStates++;
                    }
                }

                for (int i = 0; i < sample.activeStates.Count; i++)
                {
                    int index = sample.activeStates[i].Key;
                    if (index >= modelTransforms.Length) continue;
                    Transform ghost = modelTransforms[index] == modelRoot
                        ? ghostModelNode
                        : (cloneMap.TryGetValue(modelTransforms[index], out Transform t) ? t : null);
                    if (ghost == null) continue;
                    ghost.gameObject.SetActive(sample.activeStates[i].Value);
                    activeWritten++;
                }
            }

            ParsekLog.VerboseRateLimited("GhostVisual", $"anim-rest-{partName}",
                $"Animation rest state '{partName}' pid={persistentId}: candidates={candidates.Count} " +
                $"applied={applied} skippedMovesTransforms={skippedMovesTransforms} " +
                $"skippedNoClip={skippedNoClip} colorsWritten={colorsWritten} " +
                $"colorsLeftToOwner={colorsLeftToOwner} activeWritten={activeWritten} " +
                $"heatStates={heatStates}", 60.0);
        }

        private static Renderer ResolveGhostRenderer(Renderer modelRenderer, Transform modelRoot,
            Transform ghostModelNode, Dictionary<Transform, Transform> cloneMap)
        {
            if (modelRenderer == null) return null;
            Transform ghost = modelRenderer.transform == modelRoot
                ? ghostModelNode
                : (cloneMap.TryGetValue(modelRenderer.transform, out Transform t) ? t : null);
            return ghost != null ? ghost.GetComponent<Renderer>() : null;
        }

        private static Dictionary<Material, HashSet<string>> CollectPartOwnedMaterialProperties(
            HeatGhostInfo heatInfo, LightGhostInfo lightInfo, List<ColorChangerGhostInfo> colorChangerInfos)
        {
            var owned = new Dictionary<Material, HashSet<string>>();
            void Add(Material m, string property)
            {
                if (m == null) return;
                if (!owned.TryGetValue(m, out HashSet<string> set))
                {
                    set = new HashSet<string>();
                    owned[m] = set;
                }
                if (property != null) set.Add(property);
            }

            if (heatInfo?.materialStates != null)
                for (int i = 0; i < heatInfo.materialStates.Count; i++)
                    Add(heatInfo.materialStates[i].material, null);
            if (lightInfo?.lensMaterials != null)
                for (int i = 0; i < lightInfo.lensMaterials.Count; i++)
                    Add(lightInfo.lensMaterials[i], "_EmissiveColor");
            if (lightInfo?.flareMaterials != null)
                for (int i = 0; i < lightInfo.flareMaterials.Count; i++)
                    Add(lightInfo.flareMaterials[i], "_TintColor");
            if (colorChangerInfos != null)
            {
                for (int c = 0; c < colorChangerInfos.Count; c++)
                {
                    ColorChangerGhostInfo info = colorChangerInfos[c];
                    if (info?.materials == null) continue;
                    for (int i = 0; i < info.materials.Count; i++)
                        Add(info.materials[i].material, info.shaderProperty);
                }
            }
            return owned;
        }

        private static AnimationRestSample GetAnimationRestSample(
            Part prefab, Transform modelRoot, string partKey, string animName, float restTime)
        {
            string key = partKey + "|" + animName + "|"
                + restTime.ToString("R", CultureInfo.InvariantCulture);
            if (animationRestSampleCache.TryGetValue(key, out AnimationRestSample cached))
                return cached;
            AnimationRestSample sample = SampleAnimationRestState(modelRoot, animName, restTime, partKey);
            animationRestSampleCache[key] = sample;
            return sample;
        }

        private static AnimationRestSample SampleAnimationRestState(
            Transform modelRoot, string animName, float restTime, string partKey)
        {
            var sample = new AnimationRestSample();
            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);
            try
            {
                var animations = new List<Animation>();
                var states = new List<AnimationState>();
                Animation[] all = tempClone.GetComponentsInChildren<Animation>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    AnimationState state = all[i] != null ? all[i][animName] : null;
                    if (state == null) continue;
                    animations.Add(all[i]);
                    states.Add(state);
                }
                if (states.Count == 0)
                {
                    ParsekLog.Verbose("GhostVisual",
                        $"  AnimationRest '{partKey}': clip '{animName}' not found on the model");
                    return sample;
                }
                sample.found = true;

                Transform[] transforms = tempClone.GetComponentsInChildren<Transform>(true);
                Renderer[] renderers = tempClone.GetComponentsInChildren<Renderer>(true);
                var defaultActive = new bool[transforms.Length];
                for (int i = 0; i < transforms.Length; i++)
                    defaultActive[i] = transforms[i].gameObject.activeSelf;

                for (int i = 0; i < states.Count; i++)
                {
                    states[i].enabled = true;
                    states[i].speed = 0f;
                    states[i].weight = 1f;
                }

                SampleAt(animations, states, 0f);
                var pose0 = CaptureLocalPoses(transforms);
                var colors0 = ReadRendererColors(renderers);
                SampleAt(animations, states, 1f);
                var pose1 = CaptureLocalPoses(transforms);
                var colors1 = ReadRendererColors(renderers);
                for (int i = 0; i < transforms.Length; i++)
                {
                    if ((pose0[i].pos - pose1[i].pos).sqrMagnitude > 0.0001f
                        || Quaternion.Angle(pose0[i].rot, pose1[i].rot) > 0.01f
                        || (pose0[i].scale - pose1[i].scale).sqrMagnitude > 0.0001f)
                    {
                        sample.movesTransforms = true;
                        break;
                    }
                }
                if (sample.movesTransforms)
                {
                    ParsekLog.Verbose("GhostVisual",
                        $"  AnimationRest '{partKey}': clip '{animName}' moves transforms; left to the pose sampling");
                    return sample;
                }

                SampleAt(animations, states, 0.5f);
                var colorsMid = ReadRendererColors(renderers);
                SampleAt(animations, states, restTime);
                var colorsRest = ReadRendererColors(renderers);

                foreach (KeyValuePair<string, (Color block, Color material, int r, int slot, string prop)> kv in colors0)
                {
                    if (!colors1.TryGetValue(kv.Key, out var end)) continue;
                    if (!colorsMid.TryGetValue(kv.Key, out var mid)) continue;
                    if (!colorsRest.TryGetValue(kv.Key, out var rest)) continue;
                    bool fromBlock = IsAnimatedColor(kv.Value.block, end.block);
                    bool fromMaterial = IsAnimatedColor(kv.Value.material, end.material);
                    if (!fromBlock && !fromMaterial) continue;
                    sample.colors.Add(new AnimatedColorSample
                    {
                        rendererIndex = kv.Value.r,
                        slot = kv.Value.slot,
                        property = kv.Value.prop,
                        cold = fromBlock ? kv.Value.block : kv.Value.material,
                        medium = fromBlock ? mid.block : mid.material,
                        hot = fromBlock ? end.block : end.material,
                        rest = fromBlock ? rest.block : rest.material,
                    });
                }

                for (int i = 0; i < transforms.Length; i++)
                {
                    bool active = transforms[i].gameObject.activeSelf;
                    if (active != defaultActive[i])
                        sample.activeStates.Add(new KeyValuePair<int, bool>(i, active));
                }

                ParsekLog.Verbose("GhostVisual",
                    $"  AnimationRest '{partKey}': clip '{animName}' at t={restTime.ToString("0.###", CultureInfo.InvariantCulture)} " +
                    $"animates {sample.colors.Count} colour(s), {sample.activeStates.Count} active state(s)");
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }
            return sample;
        }

        private static void SampleAt(List<Animation> animations, List<AnimationState> states, float t)
        {
            for (int i = 0; i < states.Count; i++)
            {
                states[i].normalizedTime = t;
                animations[i].Sample();
            }
        }

        private static (Vector3 pos, Quaternion rot, Vector3 scale)[] CaptureLocalPoses(Transform[] transforms)
        {
            var poses = new (Vector3, Quaternion, Vector3)[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
                poses[i] = (transforms[i].localPosition, transforms[i].localRotation, transforms[i].localScale);
            return poses;
        }

        /// <summary>
        /// Every Color shader property of every material slot, read both from the renderer's
        /// property block (where legacy animation writes material curves) and from the material
        /// itself (in case the clip instanced it), keyed renderer|slot|property.
        /// </summary>
        private static Dictionary<string, (Color block, Color material, int r, int slot, string prop)>
            ReadRendererColors(Renderer[] renderers)
        {
            var result = new Dictionary<string, (Color, Color, int, int, string)>();
            var block = new MaterialPropertyBlock();
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null || renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                    continue;
                block.Clear();
                renderer.GetPropertyBlock(block);
                Material[] materials = renderer.sharedMaterials;
                if (materials == null) continue;
                for (int s = 0; s < materials.Length; s++)
                {
                    Material material = materials[s];
                    if (material == null || material.shader == null) continue;
                    Shader shader = material.shader;
                    int count = shader.GetPropertyCount();
                    for (int p = 0; p < count; p++)
                    {
                        if (shader.GetPropertyType(p) != ShaderPropertyType.Color) continue;
                        string prop = shader.GetPropertyName(p);
                        result[r.ToString(CultureInfo.InvariantCulture) + "|" + s.ToString(CultureInfo.InvariantCulture) + "|" + prop] =
                            (block.isEmpty ? Color.clear : block.GetColor(prop), material.GetColor(prop), r, s, prop);
                    }
                }
            }
            return result;
        }
    }
}
