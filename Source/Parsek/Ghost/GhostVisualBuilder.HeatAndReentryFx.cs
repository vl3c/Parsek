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

                // Collect all transforms under the clone
                var allTransforms = tempClone.GetComponentsInChildren<Transform>(true);

                // Sample at t=0.0 (cold)
                animState.normalizedTime = 0f;
                anim.Sample();
                var coldSnap = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    Transform t = allTransforms[i];
                    if (t == tempClone.transform) continue;
                    string path = GetTransformPath(t, tempClone.transform);
                    coldSnap[path] = (t.localPosition, t.localRotation, t.localScale);
                }

                // Sample at t=0.5 (medium)
                animState.normalizedTime = 0.5f;
                anim.Sample();
                var medSnap = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    Transform t = allTransforms[i];
                    if (t == tempClone.transform) continue;
                    string path = GetTransformPath(t, tempClone.transform);
                    medSnap[path] = (t.localPosition, t.localRotation, t.localScale);
                }

                // Sample at t=1.0 (hot)
                animState.normalizedTime = 1f;
                anim.Sample();
                var hotSnap = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    Transform t = allTransforms[i];
                    if (t == tempClone.transform) continue;
                    string path = GetTransformPath(t, tempClone.transform);
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
                Object.Destroy(tempClone);
            }

            return result;
        }

        /// <summary>
        /// Collect CombineInstance entries from ghost mesh filters, transforming each
        /// mesh into the ghost root's local space.
        /// When <paramref name="filterActiveInHierarchy"/> is true, only meshes on
        /// active GameObjects are included.
        /// </summary>
        private static List<CombineInstance> CombineGhostMeshFilters(
            MeshFilter[] meshFilters, Transform ghostRootTransform, bool filterActiveInHierarchy)
        {
            var combines = new System.Collections.Generic.List<CombineInstance>();
            Matrix4x4 rootWorldToLocal = ghostRootTransform.worldToLocalMatrix;
            for (int m = 0; m < meshFilters.Length; m++)
            {
                MeshFilter mf = meshFilters[m];
                if (mf == null || mf.sharedMesh == null) continue;
                if (filterActiveInHierarchy && !mf.gameObject.activeInHierarchy) continue;
                CombineInstance ci = default;
                ci.mesh = mf.sharedMesh;
                ci.transform = rootWorldToLocal * mf.transform.localToWorldMatrix;
                combines.Add(ci);
            }
            return combines;
        }

        /// <summary>
        /// Collect FireShellMesh entries from mesh filters for fire-shell overlay rendering.
        /// When <paramref name="filterActiveInHierarchy"/> is true, only meshes on
        /// active GameObjects are included.
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
                shellMeshes.Add(new FireShellMesh { mesh = mf.sharedMesh, transform = mf.transform });
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
        /// Creates reentry FX layers (glow materials + fire streak trails)
        /// on a ghost root GameObject.
        /// Called once at ghost spawn time; FX start dormant and are driven by UpdateReentryFx.
        /// </summary>
        internal static ReentryFxInfo TryBuildReentryFx(
            GameObject ghostRoot,
            Dictionary<uint, HeatGhostInfo> heatInfos,
            int ghostIndex,
            string vesselName)
        {
            var info = new ReentryFxInfo();
            int skippedHeatCount = 0;

            // --- Layer A: Collect glow materials ---
            var renderers = ghostRoot.GetComponentsInChildren<Renderer>(true);
            skippedHeatCount = CollectReentryGlowMaterials(
                ghostRoot, heatInfos, renderers, info.glowMaterials, info.allClonedMaterials);

            // --- Measure vessel bounds for streak sizing ---
            // Reuse the renderers array already fetched for Layer A to avoid a second hierarchy scan
            float vesselLength = ComputeGhostLength(ghostRoot, renderers);
            info.vesselLength = vesselLength;

            // --- Fire streak trails ---
            // --- Fire envelope particles (mesh-surface emission) ---
            // Emulates KSP's stock aeroFX approach: fire originates from the vessel's
            // own geometry surface. We combine all ghost meshes into one, use it as the
            // particle emission shape, and simulate in world space. As the vessel moves
            // through the air, particles stay in place and naturally streak backward
            // along the airflow — the same visual result as the stock replacement shader.
            Shader particleShader = Shader.Find("KSP/Particles/Additive");
            if (particleShader == null)
            {
                ParsekLog.Warn("ReentryFx",
                    $"Shader 'KSP/Particles/Additive' not found — fire particles will not be created for ghost #{ghostIndex}");
            }
            else
            {
                BuildReentryFireParticleSystem(ghostRoot, particleShader, vesselLength, info, ghostIndex);
            }

            // --- Fire shell overlay: collect mesh+transform pairs for DrawMesh ---
            BuildReentryFireShellOverlay(ghostRoot, info, ghostIndex);

            return info;
        }

        /// <summary>
        /// Creates the mesh-surface-emitting fire particle system for reentry FX.
        /// Combines all ghost meshes into a single emission shape, configures lifetime/color/noise,
        /// and attaches the result to the ghost root.
        /// </summary>
        private static void BuildReentryFireParticleSystem(
            GameObject ghostRoot, Shader particleShader, float vesselLength,
            ReentryFxInfo info, int ghostIndex)
        {
            // Combine all ghost meshes into a single emission shape
            MeshFilter[] meshFilters = ghostRoot.GetComponentsInChildren<MeshFilter>(true);
            var combines = CombineGhostMeshFilters(meshFilters, ghostRoot.transform, true);

            Mesh combinedMesh = null;
            if (combines.Count > 0)
            {
                combinedMesh = new Mesh();
                combinedMesh.CombineMeshes(combines.ToArray(), true, true);
            }

            GameObject fireObj = new GameObject("ReentryFire");
            fireObj.transform.SetParent(ghostRoot.transform, false);
            fireObj.transform.localPosition = Vector3.zero;
            fireObj.transform.localRotation = Quaternion.identity;

            ParticleSystem ps = fireObj.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(ReentryFireLifetimeMin, ReentryFireLifetimeMax);
            // Small outward push from surface normal — particles drift slightly away from hull
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 4f);
            main.startSize = new ParticleSystem.MinMaxCurve(vesselLength * 0.06f, vesselLength * 0.2f);
            main.maxParticles = ReentryFireMaxParticles;
            main.playOnAwake = false;
            main.prewarm = false;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.8f, 0.3f, 0.8f),
                new Color(1f, 0.5f, 0.15f, 0.9f));

            // Shape: emit from vessel mesh surface
            var shape = ps.shape;
            if (combinedMesh != null)
            {
                shape.shapeType = ParticleSystemShapeType.Mesh;
                shape.mesh = combinedMesh;
                shape.meshShapeType = ParticleSystemMeshShapeType.Triangle;
                shape.normalOffset = 0.05f;
            }
            else
            {
                // Fallback: sphere if no meshes available
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = vesselLength * 0.3f;
            }

            // Emission: driven by DriveReentryLayers
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTimeMultiplier = 0f;

            // Color over lifetime: bright yellow-white �� orange → deep red → fade out
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var fireGradient = new Gradient();
            fireGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1f, 0.9f, 0.5f), 0f),
                    new GradientColorKey(new Color(1f, 0.5f, 0.1f), 0.3f),
                    new GradientColorKey(new Color(0.8f, 0.2f, 0.05f), 0.7f),
                    new GradientColorKey(new Color(0.4f, 0.1f, 0.02f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.8f, 0f),
                    new GradientAlphaKey(0.5f, 0.3f),
                    new GradientAlphaKey(0.15f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLifetime.color = fireGradient;

            // Size over lifetime: particles expand as they cool
            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.5f, 1.2f), new Keyframe(1f, 1.6f)));

            // Noise: turbulence for organic fire look
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = vesselLength * 0.06f;
            noise.frequency = 3f;
            noise.scrollSpeed = 2f;
            noise.damping = true;

            // Material: additive shader with runtime soft-circle texture
            var psRenderer = fireObj.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            psRenderer.maxParticleSize = 10f;

            Texture2D softCircle = CreateSoftCircleTexture(32);
            info.generatedTexture = softCircle;

            var fireMat = new Material(particleShader);
            fireMat.mainTexture = softCircle;
            fireMat.SetColor("_TintColor", new Color(1f, 0.6f, 0.2f, 0.5f));
            psRenderer.material = fireMat;
            info.allClonedMaterials.Add(fireMat);

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(true);

            info.fireParticles = ps;
            info.combinedEmissionMesh = combinedMesh;

            ParsekLog.VerboseRateLimited("ReentryFx", "emission-build",
                $"combined {combines.Count} meshes into emission shape " +
                $"({(combinedMesh != null ? combinedMesh.vertexCount : 0)} verts) for ghost #{ghostIndex}");
        }

        /// <summary>
        /// Collects mesh+transform pairs for the fire shell overlay (DrawMesh re-draw of ghost
        /// geometry offset along velocity with additive blending). Creates the shell material.
        /// </summary>
        private static void BuildReentryFireShellOverlay(
            GameObject ghostRoot, ReentryFxInfo info, int ghostIndex)
        {
            MeshFilter[] allMeshFilters = ghostRoot.GetComponentsInChildren<MeshFilter>(true);
            info.fireShellMeshes = CollectFireShellMeshes(allMeshFilters, true);

            Shader shellShader = Shader.Find("KSP/Particles/Additive");
            if (shellShader != null)
            {
                var shellMat = new Material(shellShader);
                shellMat.SetColor("_TintColor", ReentryFireShellColor);
                info.fireShellMaterial = shellMat;
                info.allClonedMaterials.Add(shellMat);
            }

            ParsekLog.VerboseRateLimited("ReentryFx", "shell-build",
                $"{info.fireShellMeshes.Count} meshes collected for fire shell overlay on ghost #{ghostIndex}");
        }

        /// <summary>
        /// Walks all renderers on the ghost root, skips parts managed by HeatGhostInfo,
        /// clones materials, and builds HeatMaterialState entries for reentry glow.
        /// Populates the provided glowMaterials and allClonedMaterials lists in place.
        /// Returns the count of renderers skipped due to HeatGhostInfo management.
        /// </summary>
        private static int CollectReentryGlowMaterials(
            GameObject ghostRoot,
            Dictionary<uint, HeatGhostInfo> heatInfos,
            Renderer[] renderers,
            List<HeatMaterialState> glowMaterials,
            List<Material> allClonedMaterials)
        {
            int skippedHeatCount = 0;
            var heatPartIds = new HashSet<uint>();
            if (heatInfos != null)
            {
                foreach (var kvp in heatInfos)
                    heatPartIds.Add(kvp.Key);
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

                    Material materialClone = new Material(source);
                    cloned[m] = materialClone;
                    allClonedMaterials.Add(materialClone);
                    hasAnyMaterial = true;

                    string colorProperty = materialClone.HasProperty("_Color")
                        ? "_Color"
                        : null;
                    string emissiveProperty = TryGetHeatEmissiveProperty(materialClone);

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
                    renderer.materials = cloned;
            }

            return skippedHeatCount;
        }

        /// <summary>
        /// Rebuilds the combined emission mesh and fire shell mesh list from currently active
        /// ghost parts. Call after decouple/destroy events so particles and fire shell overlays
        /// no longer emit from removed parts.
        /// </summary>
        internal static void RebuildReentryMeshes(GameObject ghostRoot, ReentryFxInfo info)
        {
            if (info == null || ghostRoot == null) return;

            // Rebuild combined emission mesh
            MeshFilter[] meshFilters = ghostRoot.GetComponentsInChildren<MeshFilter>(false); // false = only active
            var combines = CombineGhostMeshFilters(meshFilters, ghostRoot.transform, false);

            // Destroy old combined mesh
            if (info.combinedEmissionMesh != null)
                Object.Destroy(info.combinedEmissionMesh);

            if (combines.Count > 0)
            {
                Mesh newMesh = new Mesh();
                newMesh.CombineMeshes(combines.ToArray(), true, true);
                info.combinedEmissionMesh = newMesh;

                if (info.fireParticles != null)
                {
                    var shape = info.fireParticles.shape;
                    shape.shapeType = ParticleSystemShapeType.Mesh;
                    shape.mesh = newMesh;
                }
            }
            else
            {
                info.combinedEmissionMesh = null;
            }

            // Rebuild fire shell mesh list from active parts only
            info.fireShellMeshes = CollectFireShellMeshes(meshFilters, false);

            ParsekLog.Verbose("ReentryFx", $"rebuilt emission mesh ({combines.Count} meshes, " +
                $"{(info.combinedEmissionMesh != null ? info.combinedEmissionMesh.vertexCount : 0)} verts) " +
                $"and {info.fireShellMeshes.Count} fire shell meshes after decouple");
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
        /// Computes reentry visual intensity matched to KSP stock aeroFX from Physics.cfg.
        /// Pure function — no Unity dependencies, no side effects.
        /// Returns 0-1 float: 0 = no effect, 1 = maximum reentry FX.
        ///
        /// KSP stock formula:
        ///   aeroFXStrength = velocity^3.5 * (0.0091 * density^0.5 + 0.09 * density^2)
        ///   Thermal FX (orange flames) starts at Mach 2.5, fully at Mach 3.75
        ///   Density fade below 0.0015 kg/m³
        /// </summary>
        internal static float ComputeReentryIntensity(float speed, float density, float machNumber)
        {
            // Guard: NaN or negative inputs
            if (float.IsNaN(speed) || speed < 0f)
                return 0f;
            if (float.IsNaN(density) || density < 0f)
                return 0f;
            if (float.IsNaN(machNumber) || machNumber < 0f)
                return 0f;

            // Mach gate: KSP thermal FX starts at Mach 2.5
            if (machNumber < AeroFxThermalStartMach)
                return 0f;

            // Density gate: FX fades near the edge of atmosphere
            if (density < AeroFxDensityFadeStart)
                return 0f;

            // Handle infinities
            if (float.IsPositiveInfinity(speed) || float.IsPositiveInfinity(density))
                return 1f;

            // KSP aeroFX strength: velocity^3.5 * (s1 * density^e1 + s2 * density^e2)
            double velTerm = System.Math.Pow(speed, AeroFxVelocityExponent);
            double densityTerm = AeroFxDensityScalar1 * System.Math.Pow(density, AeroFxDensityExponent1)
                               + AeroFxDensityScalar2 * System.Math.Pow(density, AeroFxDensityExponent2);
            double rawStrength = velTerm * densityTerm;

            // Normalize so intensity=1.0 at Mach 3.75 / ~20km altitude (density≈0.1).
            // Reentry heating is visible at high altitude, not sea level — calibrating to
            // sea level (density 1.225) produces a denominator 100x too large, making all
            // real reentry conditions round to zero intensity.
            double refSpeed = 340.0 * AeroFxThermalFullMach;
            double refDensity = 0.1;
            double refStrength = System.Math.Pow(refSpeed, AeroFxVelocityExponent)
                               * (AeroFxDensityScalar1 * System.Math.Pow(refDensity, AeroFxDensityExponent1)
                               +  AeroFxDensityScalar2 * System.Math.Pow(refDensity, AeroFxDensityExponent2));

            float intensity = (float)(rawStrength / refStrength);

            // Clamp
            if (intensity < 0f) intensity = 0f;
            if (intensity > 1f) intensity = 1f;

            return intensity;
        }
    }
}
