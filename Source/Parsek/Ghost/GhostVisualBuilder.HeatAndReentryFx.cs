using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // Heat and reentry visuals: animate-heat / throttle / RCS animation sampling, heat material states and color ramp, reentry fire particles, fire shell and glow materials.
    internal static partial class GhostVisualBuilder
    {
        internal static void ClearAnimateHeatCache()
        {
            animateHeatCache.Clear();
            // S2's clip-length cache is prefab-derived exactly like this one and has exactly the
            // same lifetime, so it is dropped here rather than growing a second call site nobody
            // remembers to call. S7's loop-phase cache is prefab-derived on the same terms.
            animationClipLengthCache.Clear();
            converterLoopSampleCache.Clear();
            animationRestSampleCache.Clear();
            nozzleGlowSampleCache.Clear();
        }

        private static bool TryGetAnimateHeatAnimation(
            Part prefab, out string animationName)
        {
            animationName = null;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode animateHeatModule = FindModuleNode(partConfig, "ModuleAnimateHeat");
            if (animateHeatModule == null) return false;

            animationName = FirstNonEmptyConfigValue(
                animateHeatModule, "ThermalAnim", "thermalAnim", "animationName");

            return !string.IsNullOrEmpty(animationName);
        }

        /// <summary>
        /// Check if an animation name looks like a heat/emissive animation (vs. mechanical nozzle movement).
        /// Used to disambiguate when a part has multiple FXModuleAnimateThrottle instances.
        /// </summary>
        internal static bool IsHeatAnimationName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("heat") || lower.Contains("emissive") ||
                   lower.Contains("glow") || lower.Contains("color");
        }

        /// <summary>
        /// Search part config for FXModuleAnimateThrottle MODULE nodes and return the heat/emissive
        /// animation name. When multiple instances exist (e.g. Panther has 3), uses a name-based
        /// heuristic to pick the heat animation, falling back to lowest responseSpeed.
        /// </summary>
        internal static bool TryGetAnimateThrottleAnimation(
            Part prefab, out string animationName)
        {
            animationName = null;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            var modules = partConfig.GetNodes("MODULE");
            if (modules == null) return false;

            // Collect all FXModuleAnimateThrottle instances
            var candidates = new List<(string animName, float responseSpeed)>();
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") != "FXModuleAnimateThrottle")
                    continue;

                string animName = FirstNonEmptyConfigValue(modules[i], "animationName");
                if (string.IsNullOrEmpty(animName)) continue;

                float responseSpeed = 1f;
                string rsStr = modules[i].GetValue("responseSpeed");
                if (!string.IsNullOrEmpty(rsStr))
                    float.TryParse(rsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out responseSpeed);

                candidates.Add((animName, responseSpeed));
            }

            if (candidates.Count == 0) return false;

            string partName = prefab.partInfo?.name ?? prefab.name;

            if (candidates.Count == 1)
            {
                animationName = candidates[0].animName;
                return true;
            }

            // Multiple instances — try name heuristic first
            for (int i = 0; i < candidates.Count; i++)
            {
                if (IsHeatAnimationName(candidates[i].animName))
                {
                    animationName = candidates[i].animName;
                    return true;
                }
            }

            // No name matched — fall back to lowest responseSpeed (heat anims typically 0.001-0.002)
            float lowestSpeed = float.MaxValue;
            string lowestName = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].responseSpeed < lowestSpeed)
                {
                    lowestSpeed = candidates[i].responseSpeed;
                    lowestName = candidates[i].animName;
                }
            }

            animationName = lowestName;
            return !string.IsNullOrEmpty(animationName);
        }

        /// <summary>
        /// Search part config for FXModuleAnimateRCS MODULE node and return the animation name.
        /// RCS parts have at most one FXModuleAnimateRCS instance, so no multi-instance heuristic needed.
        /// </summary>
        internal static bool TryGetAnimateRcsAnimation(
            Part prefab, out string animationName)
        {
            animationName = null;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            var modules = partConfig.GetNodes("MODULE");
            if (modules == null) return false;

            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") != "FXModuleAnimateRCS")
                    continue;

                string animName = FirstNonEmptyConfigValue(modules[i], "animationName");
                if (string.IsNullOrEmpty(animName)) continue;

                animationName = animName;
                return true;
            }

            return false;
        }

        private static List<(string path,
            Vector3 coldPos, Quaternion coldRot, Vector3 coldScale,
            Vector3 medPos, Quaternion medRot, Vector3 medScale,
            Vector3 hotPos, Quaternion hotRot, Vector3 hotScale)> SampleAnimateHeatStates(
            Part prefab, string animationName)
        {
            string partKey = prefab.partInfo?.name ?? prefab.name;
            string cacheKey = partKey + "|" + (animationName ?? string.Empty);
            if (animateHeatCache.TryGetValue(cacheKey, out var cached))
                return cached;

            if (string.IsNullOrEmpty(animationName))
            {
                animateHeatCache[cacheKey] = null;
                return null;
            }

            var result = SampleHeatAnimation3State(prefab, animationName, partKey);
            animateHeatCache[cacheKey] = result;
            return result;
        }

        /// <summary>
        /// Samples a heat animation at 3 points: t=0 (cold), t=0.5 (medium), t=1.0 (hot).
        /// Returns only transforms where at least one sample differs from the others.
        /// </summary>
        private static List<(string path,
            Vector3 coldPos, Quaternion coldRot, Vector3 coldScale,
            Vector3 medPos, Quaternion medRot, Vector3 medScale,
            Vector3 hotPos, Quaternion hotRot, Vector3 hotScale)> SampleHeatAnimation3State(
            Part prefab, string animationName, string partKey)
        {
            Transform modelRoot = FindModelRoot(prefab);
            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);
            List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)> result = null;

            try
            {
                Animation anim = null;
                foreach (var candidate in tempClone.GetComponentsInChildren<Animation>(true))
                {
                    if (candidate[animationName] != null)
                    {
                        anim = candidate;
                        break;
                    }
                }

                if (anim == null)
                {
                    return null;
                }

                AnimationState animState = anim[animationName];
                animState.enabled = true;
                animState.weight = 1f;

                // Collect all transforms under the clone; a repeated path keeps its first transform.
                var allTransforms = tempClone.GetComponentsInChildren<Transform>(true);
                string[] heatPaths = GetTransformPaths(allTransforms, tempClone.transform);
                bool[] heatFirst = MarkFirstPathOccurrences(heatPaths);

                // Sample at t=0.0 (cold)
                animState.normalizedTime = 0f;
                anim.Sample();
                var coldSnap = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    Transform t = allTransforms[i];
                    if (t == tempClone.transform || !heatFirst[i]) continue;
                    string path = heatPaths[i];
                    coldSnap[path] = (t.localPosition, t.localRotation, t.localScale);
                }

                // Sample at t=0.5 (medium)
                animState.normalizedTime = 0.5f;
                anim.Sample();
                var medSnap = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    Transform t = allTransforms[i];
                    if (t == tempClone.transform || !heatFirst[i]) continue;
                    string path = heatPaths[i];
                    medSnap[path] = (t.localPosition, t.localRotation, t.localScale);
                }

                // Sample at t=1.0 (hot)
                animState.normalizedTime = 1f;
                anim.Sample();
                var hotSnap = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    Transform t = allTransforms[i];
                    if (t == tempClone.transform || !heatFirst[i]) continue;
                    string path = heatPaths[i];
                    hotSnap[path] = (t.localPosition, t.localRotation, t.localScale);
                }

                // Find transforms where at least one sample differs
                result = new List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>();
                foreach (var kvp in coldSnap)
                {
                    string path = kvp.Key;
                    if (!medSnap.TryGetValue(path, out var med) || !hotSnap.TryGetValue(path, out var hot))
                        continue;

                    var cold = kvp.Value;
                    bool anyDiff = cold.pos != med.pos || cold.pos != hot.pos ||
                                   cold.rot != med.rot || cold.rot != hot.rot ||
                                   cold.scale != med.scale || cold.scale != hot.scale;
                    if (!anyDiff) continue;

                    result.Add((path, cold.pos, cold.rot, cold.scale, med.pos, med.rot, med.scale, hot.pos, hot.rot, hot.scale));
                }

                if (result.Count == 0)
                    result = null;
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }

            return result;
        }

        /// <summary>
        /// Collect the FireShellMesh entries the reentry sheath redraws (each mesh filter with a
        /// mesh renderer beside it). When <paramref name="filterActiveInHierarchy"/> is true, only
        /// meshes on active GameObjects are included.
        /// </summary>
        private static List<FireShellMesh> CollectFireShellMeshes(
            MeshFilter[] meshFilters, bool filterActiveInHierarchy)
        {
            var shellMeshes = new List<FireShellMesh>();
            for (int m = 0; m < meshFilters.Length; m++)
            {
                MeshFilter mf = meshFilters[m];
                if (mf == null || mf.sharedMesh == null) continue;
                if (filterActiveInHierarchy && !mf.gameObject.activeInHierarchy) continue;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                shellMeshes.Add(new FireShellMesh { mesh = mf.sharedMesh, transform = mf.transform, renderer = mr });
            }
            return shellMeshes;
        }

        private static string TryGetHeatEmissiveProperty(Material material)
        {
            if (material == null)
                return null;

            string[] emissiveCandidates =
            {
                "_EmissiveColor",
                "_EmissionColor",
                "_Emissive"
            };

            for (int i = 0; i < emissiveCandidates.Length; i++)
            {
                string propertyName = emissiveCandidates[i];
                if (material.HasProperty(propertyName))
                    return propertyName;
            }

            return null;
        }

        /// <summary>
        /// Computes the heat color/emission ramp from a cold color and whether the material
        /// has a color / emissive property. Returns the hot color (cold lerped 0.45 toward the
        /// heat tint, or the cold color when there is no color property), the hot emission (the
        /// heat emission color, or black when there is no emissive property), and the medium
        /// midpoints (cold->hot at 0.5 for both). Pure leaf math over Color structs.
        /// </summary>
        internal static (Color hot, Color medium, Color hotEmission, Color mediumEmission)
            ComputeHeatColorRamp(Color coldColor, bool hasColorProperty, bool hasEmissiveProperty)
        {
            Color hotColor = hasColorProperty
                ? Color.Lerp(coldColor, HeatTintColor, 0.45f)
                : coldColor;

            Color coldEmission = Color.black;
            Color hotEmission = hasEmissiveProperty
                ? HeatEmissionColor
                : coldEmission;

            Color mediumColor = Color.Lerp(coldColor, hotColor, 0.5f);
            Color mediumEmission = Color.Lerp(coldEmission, hotEmission, 0.5f);

            return (hotColor, mediumColor, hotEmission, mediumEmission);
        }

        private static List<HeatMaterialState> BuildHeatMaterialStates(
            Transform modelNode, string partName, uint persistentId,
            List<HeatTransformState> affectedTransforms)
        {
            if (modelNode == null)
                return null;

            var renderers = modelNode.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return null;

            // Build set of transforms affected by heat animation so we only clone
            // materials on renderers that actually participate in the heat visual.
            // When null/empty, fall back to cloning all renderers (legacy behavior).
            HashSet<Transform> affectedSet = null;
            if (affectedTransforms != null && affectedTransforms.Count > 0)
            {
                affectedSet = new HashSet<Transform>();
                for (int a = 0; a < affectedTransforms.Count; a++)
                {
                    Transform root = affectedTransforms[a].t;
                    if (root == null) continue;
                    affectedSet.Add(root);
                    var descendants = root.GetComponentsInChildren<Transform>(true);
                    for (int d = 0; d < descendants.Length; d++)
                        affectedSet.Add(descendants[d]);
                }
            }

            var materialStates = new List<HeatMaterialState>();
            int clonedMaterials = 0;
            int trackedMaterials = 0;
            int skippedRenderers = 0;
            int clearedEmissives = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;

                // Skip renderers not under any heat-animated transform
                if (affectedSet != null && !affectedSet.Contains(renderer.transform))
                {
                    skippedRenderers++;
                    continue;
                }

                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                    continue;

                // Only clone materials that have trackable heat properties (_Color or emissive).
                // Untracked materials keep their original shared reference to avoid visual
                // divergence from orphaned material clones.
                var cloned = new Material[sourceMaterials.Length];
                bool hasTrackedMaterial = false;
                for (int m = 0; m < sourceMaterials.Length; m++)
                {
                    Material source = sourceMaterials[m];
                    if (source == null)
                    {
                        cloned[m] = null;
                        continue;
                    }

                    string emissiveProperty = TryGetHeatEmissiveProperty(source);

                    // In the fallback path (no resolved transforms), only track materials
                    // with emissive properties. _Color alone is too broad — every KSP material
                    // has it, including body/casing materials that should not get heat tinted.
                    // When transforms ARE resolved (affectedSet != null), _Color is safe because
                    // the renderer filter already limits to heat-animated meshes (nozzles).
                    string colorProperty = null;
                    if (affectedSet != null && source.HasProperty("_Color"))
                        colorProperty = "_Color";

                    if (colorProperty == null && emissiveProperty == null)
                    {
                        // Keep shared reference — no heat properties to animate
                        cloned[m] = source;
                        continue;
                    }

                    // Clone only materials that will be tracked
                    Material materialClone = new Material(source);
                    cloned[m] = materialClone;
                    clonedMaterials++;
                    hasTrackedMaterial = true;

                    // Clear inherited emissive immediately — prefab materials may have
                    // non-zero _EmissiveColor baked in, but KSP's stock thermal system
                    // drives that property at runtime from part.temperature. Ghost parts
                    // have no temperature simulation, so cold state = no emissive glow.
                    if (emissiveProperty != null)
                    {
                        materialClone.SetColor(emissiveProperty, Color.black);
                        clearedEmissives++;
                    }

                    Color coldColor = colorProperty != null
                        ? materialClone.GetColor(colorProperty)
                        : Color.white;
                    Color coldEmission = Color.black;

                    var (hotColor, mediumColor, hotEmission, mediumEmission) =
                        ComputeHeatColorRamp(coldColor, colorProperty != null, emissiveProperty != null);

                    materialStates.Add(new HeatMaterialState
                    {
                        material = materialClone,
                        colorProperty = colorProperty,
                        coldColor = coldColor,
                        mediumColor = mediumColor,
                        hotColor = hotColor,
                        emissiveProperty = emissiveProperty,
                        coldEmission = coldEmission,
                        mediumEmission = mediumEmission,
                        hotEmission = hotEmission
                    });
                    trackedMaterials++;
                }

                if (hasTrackedMaterial)
                    renderer.materials = cloned;
            }


            if (trackedMaterials > 0)
            {
                if (clearedEmissives > 0)
                    ParsekLog.VerboseRateLimited("GhostVisual",
                        $"emissive-clear-{partName}-{persistentId}",
                        $"Part '{partName}' pid={persistentId}: cleared {clearedEmissives} inherited emissive(s) to black",
                        5.0);
                return materialStates;
            }

            return null;
        }

        /// <summary>
        /// Pure: the clip samples a heat level shows. Stock <c>ModuleAnimateHeat</c> sets the clip's
        /// normalized time to its 0..1 heat; the recorded levels stand for 0, 0.5 and 1, the poses
        /// <see cref="SampleHeatAnimation3State"/> samples.
        /// </summary>
        internal static void SelectHeatClipColors(
            Color[] samples, out Color cold, out Color medium, out Color hot)
        {
            cold = samples[0];
            medium = samples[(samples.Length - 1) / 2];
            hot = samples[samples.Length - 1];
        }

        /// <summary>
        /// The colours a ModuleAnimateHeat clip writes, per renderer slot and property, on per-ghost
        /// material clones. Stock plays the clip on every Animation of the model that holds it
        /// (<c>ModuleAnimationSetter.AnimStartup</c>). Null when the clip animates no colour.
        /// </summary>
        private static List<HeatMaterialState> BuildClipHeatMaterialStates(
            Part prefab, Transform modelRoot, Transform modelNode, Dictionary<Transform, Transform> cloneMap,
            string animName, string partName, uint persistentId)
        {
            if (prefab == null || modelRoot == null || modelNode == null || cloneMap == null) return null;
            List<NozzleGlowColorSample> samples = GetClipColorSamples(
                modelRoot, animName, false, prefab.partInfo?.name ?? prefab.name);
            if (samples.Count == 0) return null;

            Renderer[] modelRenderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            var perGhost = new HashSet<Material>();
            List<HeatMaterialState> states = null;
            for (int i = 0; i < samples.Count; i++)
            {
                NozzleGlowColorSample s = samples[i];
                if (s.rendererIndex >= modelRenderers.Length) continue;
                Renderer ghostRenderer = ResolveGhostRenderer(
                    modelRenderers[s.rendererIndex], modelRoot, modelNode, cloneMap);
                if (ghostRenderer == null) continue;
                Material[] slots = ghostRenderer.sharedMaterials;
                if (slots == null || s.slot >= slots.Length || slots[s.slot] == null) continue;
                Material material = slots[s.slot];
                if (!material.HasProperty(s.property)) continue;
                if (!perGhost.Contains(material))
                {
                    material = new Material(material);
                    slots[s.slot] = material;
                    ghostRenderer.sharedMaterials = slots;
                    perGhost.Add(material);
                }
                SelectHeatClipColors(s.samples, out Color cold, out Color medium, out Color hot);
                if (states == null) states = new List<HeatMaterialState>();
                states.Add(new HeatMaterialState
                {
                    material = material,
                    emissiveProperty = s.property,
                    coldEmission = cold,
                    mediumEmission = medium,
                    hotEmission = hot
                });
            }
            ParsekLog.VerboseRateLimited("GhostVisual", $"heat-clip-colors-{partName}",
                $"Heat clip '{partName}' pid={persistentId}: '{animName}' colours={samples.Count} " +
                $"bound={(states != null ? states.Count : 0)}", 60.0);
            return states;
        }

        /// <summary>
        /// Creates reentry FX layers (glow materials + the mesh list stock's sheath redraws)
        /// on a ghost root GameObject.
        /// Called once at ghost spawn time; FX start dormant and are driven by UpdateReentryFx.
        /// </summary>
        internal static ReentryFxInfo TryBuildReentryFx(
            GameObject ghostRoot,
            Dictionary<uint, HeatGhostInfo> heatInfos,
            int ghostIndex,
            string vesselName,
            Dictionary<Material, string> ghostOwnedMaterials = null)
        {
            var info = new ReentryFxInfo();
            int skippedHeatCount = 0;

            // --- Layer A: Collect glow materials ---
            var renderers = ghostRoot.GetComponentsInChildren<Renderer>(true);
            skippedHeatCount = CollectReentryGlowMaterials(
                ghostRoot, heatInfos, renderers, info.glowMaterials, info.allClonedMaterials,
                ghostOwnedMaterials);

            // --- Measure vessel bounds for streak sizing ---
            // Reuse the renderers array already fetched for Layer A to avoid a second hierarchy scan
            float vesselLength = ComputeGhostLength(ghostRoot, renderers);
            info.vesselLength = vesselLength;

            // --- The sheath: the ghost meshes stock's FX shaders redraw (GhostAeroFxRenderer) ---
            BuildReentryFireShellOverlay(ghostRoot, info, ghostIndex);

            return info;
        }

        /// <summary>
        /// Collects the mesh / transform / renderer triples the reentry sheath redraws through
        /// stock's FX shaders (GhostAeroFxRenderer); the ghost's own materials are drawn, so no
        /// material is made here.
        /// </summary>
        private static void BuildReentryFireShellOverlay(
            GameObject ghostRoot, ReentryFxInfo info, int ghostIndex)
        {
            MeshFilter[] allMeshFilters = ghostRoot.GetComponentsInChildren<MeshFilter>(true);
            info.fireShellMeshes = CollectFireShellMeshes(allMeshFilters, true);

            ParsekLog.VerboseRateLimited("ReentryFx", "shell-build",
                $"{info.fireShellMeshes.Count} meshes collected for the reentry sheath on ghost #{ghostIndex}");
        }

        /// <summary>
        /// Walks all renderers on the ghost root, skips parts managed by HeatGhostInfo,
        /// clones materials, and builds HeatMaterialState entries for reentry glow.
        /// Populates the provided glowMaterials and allClonedMaterials lists in place.
        /// Returns the count of renderers skipped due to HeatGhostInfo management.
        /// </summary>
        /// <summary>
        /// Pure: whether the reentry glow leaves the part alone. It does for every heat-managed
        /// part, but not for one whose heat clip is missing or whose only glow is a colour changer:
        /// nothing else drives that part's materials (a colour changer's own are protected
        /// material by material), and stock lights it through its blackbody like any other.
        /// </summary>
        internal static bool HeatInfoOwnsPartMaterials(HeatGhostInfo info)
        {
            if (info == null) return false;
            if (!info.heatClipMissing && !info.throttleGlowsOnly) return true;
            for (int i = 0; info.nozzleGlows != null && i < info.nozzleGlows.Count; i++)
                if (!info.nozzleGlows[i].isColorChanger && !info.nozzleGlows[i].MovesOnly) return true;
            return false;
        }

        private static int CollectReentryGlowMaterials(
            GameObject ghostRoot,
            Dictionary<uint, HeatGhostInfo> heatInfos,
            Renderer[] renderers,
            List<HeatMaterialState> glowMaterials,
            List<Material> allClonedMaterials,
            Dictionary<Material, string> ghostOwnedMaterials)
        {
            int skippedHeatCount = 0;
            int reusedOwned = 0;
            var heatPartIds = new HashSet<uint>();
            if (heatInfos != null)
            {
                foreach (var kvp in heatInfos)
                    if (HeatInfoOwnsPartMaterials(kvp.Value)) heatPartIds.Add(kvp.Key);
            }

            if (renderers == null)
                return skippedHeatCount;

            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null) continue;

                // Skip particle and trail renderers — cloning their materials breaks
                // sprite sheet animation (TextureSheetAnimation UV state is lost),
                // causing particles to render the full texture atlas as a square.
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                    continue;

                // Walk up hierarchy to find ghost_part_{persistentId} parent
                uint partPid = 0;
                bool foundPart = false;
                Transform current = renderer.transform;
                while (current != null && current != ghostRoot.transform)
                {
                    if (current.name.StartsWith("ghost_part_"))
                    {
                        string pidStr = current.name.Substring("ghost_part_".Length);
                        uint parsed;
                        if (uint.TryParse(pidStr, out parsed))
                        {
                            partPid = parsed;
                            foundPart = true;
                        }
                        break;
                    }
                    current = current.parent;
                }

                // Skip renderers on parts managed by HeatGhostInfo
                if (foundPart && heatPartIds.Contains(partPid))
                {
                    skippedHeatCount++;
                    continue;
                }

                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                    continue;

                var cloned = new Material[sourceMaterials.Length];
                bool hasAnyMaterial = false;
                for (int m = 0; m < sourceMaterials.Length; m++)
                {
                    Material source = sourceMaterials[m];
                    if (source == null)
                    {
                        cloned[m] = null;
                        continue;
                    }

                    string ownerProperty = null;
                    bool owned = ghostOwnedMaterials != null
                        && ghostOwnedMaterials.TryGetValue(source, out ownerProperty);
                    string sourceEmissive = TryGetHeatEmissiveProperty(source);
                    ReentryGlowMaterialPlan plan = DecideReentryGlowMaterial(
                        owned, ownerProperty, sourceEmissive);

                    Material materialClone;
                    if (plan.cloneMaterial)
                    {
                        materialClone = new Material(source);
                        allClonedMaterials.Add(materialClone);
                    }
                    else
                    {
                        // Already a per-ghost instance another ghost info holds and writes
                        // (light lens / flare, colour changer): reuse it so that reference
                        // stays the drawn material; its owner destroys nothing and neither do we.
                        materialClone = source;
                        reusedOwned++;
                    }
                    cloned[m] = materialClone;
                    hasAnyMaterial = true;

                    string colorProperty = materialClone.HasProperty("_Color")
                        ? "_Color"
                        : null;
                    string emissiveProperty = plan.driveEmissive ? sourceEmissive : null;

                    // Clear inherited emissive — see BuildHeatMaterialStates comment
                    if (emissiveProperty != null)
                        materialClone.SetColor(emissiveProperty, Color.black);

                    if (colorProperty == null && emissiveProperty == null)
                        continue;

                    Color coldColor = colorProperty != null
                        ? materialClone.GetColor(colorProperty)
                        : Color.white;
                    Color hotColor = colorProperty != null
                        ? Color.Lerp(coldColor, HeatTintColor, 0.45f)
                        : coldColor;

                    Color coldEmission = Color.black;
                    Color hotEmission = emissiveProperty != null
                        ? ReentryHotEmissionLow
                        : coldEmission;

                    // Medium fields populated for struct consistency but unused by reentry FX,
                    // which uses continuous interpolation between cold/hot (not discrete states)
                    Color mediumColor = Color.Lerp(coldColor, hotColor, 0.5f);
                    Color mediumEmission = Color.Lerp(coldEmission, hotEmission, 0.5f);

                    glowMaterials.Add(new HeatMaterialState
                    {
                        material = materialClone,
                        colorProperty = colorProperty,
                        coldColor = coldColor,
                        mediumColor = mediumColor,
                        hotColor = hotColor,
                        emissiveProperty = emissiveProperty,
                        coldEmission = coldEmission,
                        mediumEmission = mediumEmission,
                        hotEmission = hotEmission
                    });
                }

                if (hasAnyMaterial)
                    renderer.sharedMaterials = cloned;
            }

            if (reusedOwned > 0)
            {
                ParsekLog.VerboseRateLimited("ReentryFx", "glow-reused-owned",
                    $"Reentry glow reused {reusedOwned} per-ghost material(s) owned by light / " +
                    $"colour-changer infos instead of re-cloning them", 60.0);
            }

            return skippedHeatCount;
        }

        /// <summary>What the reentry glow collector does with one material slot.</summary>
        internal struct ReentryGlowMaterialPlan
        {
            public bool cloneMaterial;
            public bool driveEmissive;
        }

        /// <summary>
        /// Pure: how the reentry glow treats one material. A material another ghost info
        /// already holds as a per-ghost instance (a light's lens / flare, a colour changer's
        /// cabin-light or ablation-char material) is REUSED, never re-cloned: a clone would
        /// leave that info writing to a material no renderer draws (a switched-on lens staying
        /// dark, a heat shield never darkening). Reuse was chosen over remapping every info
        /// through an old -> new map because it keeps one material per slot and needs no
        /// knowledge of each info's list shapes. The glow also leaves alone the emissive
        /// property the owner itself drives (the lens / cabin-light <c>_EmissiveColor</c>), so
        /// the reentry glow neither blacks a lit lens nor fights its owner; any other material
        /// is cloned and its emissive cleared and driven, as before.
        /// </summary>
        internal static ReentryGlowMaterialPlan DecideReentryGlowMaterial(
            bool ownedByGhostInfo, string ownerDrivenProperty, string emissiveProperty)
        {
            bool ownerDrivesEmissive = ownedByGhostInfo
                && !string.IsNullOrEmpty(emissiveProperty)
                && string.Equals(ownerDrivenProperty, emissiveProperty, System.StringComparison.Ordinal);
            return new ReentryGlowMaterialPlan
            {
                cloneMaterial = !ownedByGhostInfo,
                driveEmissive = !string.IsNullOrEmpty(emissiveProperty) && !ownerDrivesEmissive,
            };
        }

        /// <summary>
        /// Rebuilds the sheath's mesh list from currently active ghost parts. Call after
        /// decouple/destroy events so the sheath no longer draws removed parts.
        /// </summary>
        internal static void RebuildReentryMeshes(GameObject ghostRoot, ReentryFxInfo info)
        {
            if (info == null || ghostRoot == null) return;

            MeshFilter[] meshFilters = ghostRoot.GetComponentsInChildren<MeshFilter>(false); // false = only active
            info.fireShellMeshes = CollectFireShellMeshes(meshFilters, false);

            ParsekLog.Verbose("ReentryFx",
                $"rebuilt {info.fireShellMeshes.Count} reentry sheath meshes after decouple");
        }

        /// <summary>
        /// Computes the local-space length of a ghost vessel along its Y axis (nose-to-tail)
        /// from the combined bounds of all renderers. Returns a minimum of 2m.
        /// Accepts an optional pre-fetched renderer array to avoid duplicate hierarchy scans.
        /// </summary>
        internal static float ComputeGhostLength(GameObject ghostRoot, Renderer[] renderers = null)
        {
            if (renderers == null)
                renderers = ghostRoot.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                return 2f;
            }

            Bounds combined = default;
            bool initialized = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                // Skip particle system renderers — their bounds encompass all active
                // particles which can span hundreds of meters (engine plumes, RCS jets)
                if (renderers[i] is ParticleSystemRenderer) continue;
                Bounds b = renderers[i].bounds;
                if (b.size.sqrMagnitude < 0.0001f) continue;

                // Transform world bounds to local space of ghostRoot
                Vector3 localCenter = ghostRoot.transform.InverseTransformPoint(b.center);
                Vector3 localExtents = b.extents; // Approximate — rotation not critical for length estimate

                Bounds localBounds = new Bounds(localCenter, localExtents * 2f);
                if (!initialized)
                {
                    combined = localBounds;
                    initialized = true;
                }
                else
                {
                    combined.Encapsulate(localBounds);
                }
            }

            if (!initialized)
            {
                return 2f;
            }

            // Y axis is typically nose-to-tail in KSP vessel space
            float length = combined.size.y;
            return Mathf.Max(length, 2f);
        }

        /// <summary>
        /// The ghost reentry intensity: stock's <c>AerodynamicsFX</c> <c>FxScalar</c> for these
        /// conditions (<see cref="GhostAeroFx.ComputeFxScalar"/>), clamped to 0..1, and stock's
        /// thermal blend (<paramref name="state"/>: 0 = condensation, 1 = reentry heat).
        /// 0 = no effect (stock draws nothing), 1 = full effect. Pure; no Unity calls.
        /// </summary>
        internal static float ComputeReentryIntensity(float speed, float density, float machNumber,
            in AeroFxConstants constants, out float state)
        {
            state = 0f;
            if (float.IsNaN(speed) || speed < 0f) return 0f;
            if (float.IsNaN(density) || density < 0f) return 0f;
            if (float.IsNaN(machNumber) || machNumber < 0f) return 0f;
            float fx = GhostAeroFx.ComputeFxScalar(density, speed, machNumber, constants, out state);
            if (float.IsNaN(fx)) { state = 0f; return 0f; }
            return Mathf.Clamp01(fx);
        }
    }
}
