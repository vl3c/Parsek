using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    internal class JettisonGhostInfo
    {
        public uint partPersistentId;
        public Transform jettisonTransform;
    }

    internal class ParachuteGhostInfo
    {
        public uint partPersistentId;
        public Transform canopyTransform;
        public Transform capTransform;
        public Vector3 deployedCanopyScale;
        public Vector3 deployedCanopyPos;
    }

    internal class EngineGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public FloatCurve emissionCurve;
        public FloatCurve speedCurve;
    }

    internal static class GhostVisualBuilder
    {
        private static readonly Regex trailingNumericSuffixRegex =
            new Regex(@"^(.*)_\d+$", RegexOptions.Compiled);

        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos,
            out List<EngineGhostInfo> engineInfos)
        {
            parachuteInfos = null;
            jettisonInfos = null;
            engineInfos = null;
            ConfigNode snapshotNode = GetGhostSnapshot(rec);
            if (snapshotNode == null)
                return null;

            var partNodes = snapshotNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0)
                return null;

            GameObject root = new GameObject(rootName);
            bool addedAnyVisual = false;
            int visualCount = 0;
            int skippedName = 0;
            int skippedPrefab = 0;
            int skippedMesh = 0;
            var collectedParachuteInfos = new List<ParachuteGhostInfo>();
            var collectedJettisonInfos = new List<JettisonGhostInfo>();
            var collectedEngineInfos = new List<EngineGhostInfo>();

            for (int i = 0; i < partNodes.Length; i++)
            {
                ConfigNode partNode = partNodes[i];
                // Proto snapshots use "name" for part ID in PART nodes.
                // Some synthetic builders may also emit "part"; support both.
                string rawPart = partNode.GetValue("name") ?? partNode.GetValue("part");
                string partName = TryExtractPartName(rawPart);
                if (string.IsNullOrEmpty(partName))
                {
                    skippedName++;
                    continue;
                }

                // Read persistentId for ghost part naming (enables O(1) lookup during playback)
                string pidStr = partNode.GetValue("persistentId");
                uint persistentId = 0;
                if (pidStr != null)
                    uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out persistentId);

                AvailablePart ap = PartLoader.getPartInfoByName(partName);
                if (ap == null || ap.partPrefab == null)
                {
                    skippedPrefab++;
                    continue;
                }

                int meshCount = 0;
                ParachuteGhostInfo parachuteInfo;
                JettisonGhostInfo jettisonInfo;
                List<EngineGhostInfo> partEngineInfos;
                bool partVisualAdded = AddPartVisuals(root.transform, partNode, ap.partPrefab,
                    persistentId, partName, out meshCount, out parachuteInfo, out jettisonInfo,
                    out partEngineInfos);
                if (partVisualAdded)
                    visualCount++;
                else
                    skippedMesh++;
                addedAnyVisual = addedAnyVisual || partVisualAdded;

                if (parachuteInfo != null)
                    collectedParachuteInfos.Add(parachuteInfo);
                if (jettisonInfo != null)
                    collectedJettisonInfos.Add(jettisonInfo);
                if (partEngineInfos != null)
                    collectedEngineInfos.AddRange(partEngineInfos);
            }

            ParsekLog.Log($"Ghost built: {visualCount}/{partNodes.Length} parts with visuals" +
                (skippedName > 0 ? $", {skippedName} bad name" : "") +
                (skippedPrefab > 0 ? $", {skippedPrefab} no prefab" : "") +
                (skippedMesh > 0 ? $", {skippedMesh} no mesh" : ""));

            if (!addedAnyVisual)
            {
                Object.Destroy(root);
                return null;
            }

            parachuteInfos = collectedParachuteInfos.Count > 0 ? collectedParachuteInfos : null;
            jettisonInfos = collectedJettisonInfos.Count > 0 ? collectedJettisonInfos : null;
            engineInfos = collectedEngineInfos.Count > 0 ? collectedEngineInfos : null;
            return root;
        }

        // Overload without info outputs for callers that don't need them (preview ghost)
        internal static GameObject BuildTimelineGhostFromSnapshot(RecordingStore.Recording rec, string rootName)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out _, out _, out _);
        }

        // Overload with parachute + jettison info (backward compat)
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out parachuteInfos, out jettisonInfos, out _);
        }

        // Overload with only parachute info for backward compat
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out parachuteInfos, out _, out _);
        }

        internal static ConfigNode GetGhostSnapshot(RecordingStore.Recording rec)
        {
            if (rec == null) return null;
            return rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
        }

        internal static string TryExtractPartName(string rawPart)
        {
            if (string.IsNullOrEmpty(rawPart))
                return null;

            // Snapshot format often uses "partName_123456789". Keep the full
            // name unless there is a trailing numeric suffix.
            var match = trailingNumericSuffixRegex.Match(rawPart);
            return match.Success ? match.Groups[1].Value : rawPart;
        }

        internal static bool TryParseVector3(string value, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrEmpty(value))
                return false;

            string[] parts = value.Split(',');
            if (parts.Length != 3)
                return false;

            float x, y, z;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;

            result = new Vector3(x, y, z);
            return true;
        }

        internal static bool TryParseQuaternion(string value, out Quaternion result)
        {
            result = Quaternion.identity;
            if (string.IsNullOrEmpty(value))
                return false;

            string[] parts = value.Split(',');
            if (parts.Length != 4)
                return false;

            float x, y, z, w;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            if (!float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w)) return false;

            result = new Quaternion(x, y, z, w);
            return true;
        }

        internal static string GetPartPositionRaw(ConfigNode partNode)
        {
            if (partNode == null) return null;
            return partNode.GetValue("pos") ?? partNode.GetValue("position");
        }

        internal static string GetPartRotationRaw(ConfigNode partNode)
        {
            if (partNode == null) return null;
            return partNode.GetValue("rot") ?? partNode.GetValue("rotation");
        }

        internal static Dictionary<uint, List<uint>> BuildPartSubtreeMap(ConfigNode snapshotNode)
        {
            var map = new Dictionary<uint, List<uint>>();
            if (snapshotNode == null) return map;

            var partNodes = snapshotNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0) return map;

            // First pass: collect persistentIds in order (index → pid)
            var pidByIndex = new uint[partNodes.Length];
            for (int i = 0; i < partNodes.Length; i++)
            {
                string pidStr = partNodes[i].GetValue("persistentId");
                if (pidStr != null)
                    uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pidByIndex[i]);
            }

            // Second pass: build parent → children map
            for (int i = 0; i < partNodes.Length; i++)
            {
                string parentStr = partNodes[i].GetValue("parent");
                if (parentStr == null) continue;

                int parentIdx;
                if (!int.TryParse(parentStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out parentIdx))
                    continue;
                if (parentIdx < 0 || parentIdx >= partNodes.Length) continue;
                if (parentIdx == i) continue; // root part references itself

                uint parentPid = pidByIndex[parentIdx];
                uint childPid = pidByIndex[i];
                if (parentPid == 0 || childPid == 0) continue;

                List<uint> children;
                if (!map.TryGetValue(parentPid, out children))
                {
                    children = new List<uint>();
                    map[parentPid] = children;
                }
                children.Add(childPid);
            }

            return map;
        }

        /// <summary>
        /// Find the visual model root transform for a part prefab.
        /// KSP stores visual content under a "model" child. KerbalEVA uses "model01".
        /// Falls back to the prefab root if neither exists.
        /// </summary>
        private static Transform FindModelRoot(Part prefab)
        {
            // KerbalEVA has body mesh under "model01"; "model" only has accessories
            // (parachute, backpack). Check model01 first to get the actual kerbal.
            Transform modelRoot = prefab.transform.Find("model01");
            if (modelRoot != null) return modelRoot;
            modelRoot = prefab.transform.Find("model");
            if (modelRoot != null) return modelRoot;
            return prefab.transform;
        }

        /// <summary>
        /// Mirror the transform hierarchy from modelRoot down to a renderer's transform,
        /// creating intermediate GameObjects under partRoot with matching local transforms.
        /// Uses cloneMap to deduplicate shared parent nodes.
        /// Returns the cloned leaf transform.
        /// </summary>
        private static Transform MirrorTransformChain(Transform rendererTransform, Transform modelRoot,
            Transform partRootTransform, Dictionary<Transform, Transform> cloneMap)
        {
            // Build path from renderer up to modelRoot
            var chain = new List<Transform>();
            Transform cur = rendererTransform;
            while (cur != null && cur != modelRoot)
            {
                chain.Add(cur);
                cur = cur.parent;
            }
            chain.Reverse(); // root-first order

            Transform parent = partRootTransform;
            for (int i = 0; i < chain.Count; i++)
            {
                Transform src = chain[i];
                if (cloneMap.TryGetValue(src, out Transform existing))
                {
                    parent = existing;
                    continue;
                }
                GameObject clone = new GameObject(src.gameObject.name);
                clone.transform.SetParent(parent, false);
                clone.transform.localPosition = src.localPosition;
                clone.transform.localRotation = src.localRotation;
                clone.transform.localScale = src.localScale;
                cloneMap[src] = clone.transform;
                parent = clone.transform;
            }

            return parent;
        }

        /// <summary>
        /// Create a fake parachute canopy (flattened hemisphere) and parent it to
        /// the ghost part identified by persistentId. Returns the canopy GameObject.
        /// </summary>
        internal static GameObject CreateFakeCanopy(GameObject ghostRoot, uint partPersistentId)
        {
            if (ghostRoot == null) return null;

            Transform partTransform = ghostRoot.transform.Find($"ghost_part_{partPersistentId}");
            if (partTransform == null) return null;

            GameObject canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            canopy.name = $"fake_canopy_{partPersistentId}";

            // Disable collider
            var col = canopy.GetComponent<Collider>();
            if (col != null) col.enabled = false;

            // Scale: wide and flat (parachute dome shape)
            canopy.transform.SetParent(partTransform, false);
            canopy.transform.localPosition = new Vector3(0f, 4f, 0f); // above the part
            canopy.transform.localScale = new Vector3(5.2f, 2f, 5.2f);

            // Orange-white parachute color
            var renderer = canopy.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("KSP/Emissive/Diffuse");
                if (shader != null)
                {
                    Material mat = new Material(shader);
                    mat.color = new Color(1f, 0.6f, 0.2f, 1f);
                    mat.SetColor("_EmissiveColor", new Color(1f, 0.6f, 0.2f, 0.3f));
                    renderer.material = mat;
                }
            }

            return canopy;
        }

        // Cache: partName → (scale, pos) — sample once per part type, reuse across ghosts
        private static readonly Dictionary<string, (Vector3 scale, Vector3 pos)> deployedCanopyCache =
            new Dictionary<string, (Vector3, Vector3)>();

        internal static void ClearDeployedCanopyCache()
        {
            deployedCanopyCache.Clear();
        }

        private static (Vector3 scale, Vector3 pos) SampleDeployedCanopy(Part prefab, ModuleParachute chute)
        {
            string key = prefab.partInfo?.name ?? prefab.name;
            if (deployedCanopyCache.TryGetValue(key, out var cached))
                return cached;

            // Use fullyDeployedAnimation for the open dome shape (visually correct for ghost).
            // Fall back to semiDeployedAnimation if fullyDeployed is missing.
            string animName = chute.fullyDeployedAnimation;
            if (string.IsNullOrEmpty(animName))
                animName = chute.semiDeployedAnimation;

            Vector3 scale = Vector3.one;
            Vector3 pos = Vector3.zero;
            bool sampled = false;

            if (!string.IsNullOrEmpty(animName))
            {
                // Clone ONLY the model subtree — avoids Part/PartModule Awake() side effects
                Transform prefabModel = prefab.transform.Find("model") ?? prefab.transform;
                GameObject tempClone = Object.Instantiate(prefabModel.gameObject);

                try
                {
                    Animation anim = tempClone.GetComponentInChildren<Animation>(true);
                    if (anim != null)
                    {
                        AnimationState state = anim[animName];
                        if (state != null)
                        {
                            state.enabled = true;
                            state.speed = 0f;
                            state.normalizedTime = 1f;
                            state.weight = 1f;
                            anim.Play(animName);
                            anim.Sample();

                            string canopyName = chute.canopyName;
                            Transform canopy = !string.IsNullOrEmpty(canopyName)
                                ? FindTransformRecursive(tempClone.transform, canopyName) : null;
                            if (canopy != null)
                            {
                                scale = canopy.localScale;
                                pos = canopy.localPosition;
                                sampled = true;
                                ParsekLog.Log($"  Animation '{animName}' sampled canopy: scale={scale} pos={pos}");
                            }
                        }
                        else
                        {
                            ParsekLog.Log($"  Animation '{animName}' not found on clone for '{key}'");
                        }
                    }
                    else
                    {
                        ParsekLog.Log($"  No Animation component on model clone for '{key}'");
                    }
                }
                finally
                {
                    Object.DestroyImmediate(tempClone);
                }
            }

            // If animation didn't produce a usable scale, use a reasonable deployed-canopy default
            if (sampled && scale.magnitude < 0.5f)
            {
                ParsekLog.Log($"  Animation produced unusable scale ({scale}), using deployed canopy fallback");
                scale = Vector3.one * 30f;
                pos = Vector3.zero;
            }

            var result = (scale, pos);
            deployedCanopyCache[key] = result;
            ParsekLog.Log($"  Deployed canopy for '{key}': scale={scale} pos={pos}");
            return result;
        }

        private static string GetTransformPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            Transform cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        internal static Transform FindTransformRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindTransformRecursive(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Recursively dump the full transform hierarchy of a part prefab for diagnostics.
        /// Logs each transform with its depth, components, and active state.
        /// </summary>
        private static void DumpTransformHierarchy(Transform t, int depth, string partName)
        {
            string indent = new string(' ', depth * 2);
            var components = t.gameObject.GetComponents<Component>();
            var compNames = new List<string>();
            foreach (var c in components)
            {
                if (c == null) continue;
                string cName = c.GetType().Name;
                if (cName == "Transform") continue; // skip ubiquitous Transform
                compNames.Add(cName);
            }
            string compStr = compNames.Count > 0 ? " [" + string.Join(", ", compNames) + "]" : "";
            string activeStr = t.gameObject.activeSelf ? "" : " (INACTIVE)";
            ParsekLog.Log($"    HIERARCHY {partName}: {indent}{t.name}{compStr}{activeStr}");
            for (int i = 0; i < t.childCount; i++)
                DumpTransformHierarchy(t.GetChild(i), depth + 1, partName);
        }

        private static List<EngineGhostInfo> TryBuildEngineFX(
            Part prefab, uint persistentId, string partName,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            // Find all ModuleEngines on the part
            int midx = 0;
            var engineModules = new List<(ModuleEngines engine, int moduleIndex)>();
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                var eng = prefab.Modules[m] as ModuleEngines;
                if (eng != null)
                {
                    engineModules.Add((eng, midx));
                    midx++;
                }
            }
            if (engineModules.Count == 0) return null;

            var result = new List<EngineGhostInfo>();

            for (int e = 0; e < engineModules.Count; e++)
            {
                var (engine, moduleIndex) = engineModules[e];
                var info = new EngineGhostInfo
                {
                    partPersistentId = persistentId,
                    moduleIndex = moduleIndex
                };

                // Try to read EFFECTS config from the part config
                ConfigNode partConfig = prefab.partInfo?.partConfig;
                if (partConfig == null)
                {
                    ParsekLog.Log($"    Engine '{partName}' midx={moduleIndex}: no partConfig — skipping FX");
                    continue;
                }

                ConfigNode effectsNode = partConfig.GetNode("EFFECTS");
                if (effectsNode == null)
                {
                    // Legacy FX fallback: stock early-career parts (Flea SRB, LV-T30, etc.)
                    // use fx_* prefab children instead of modern EFFECTS configs.
                    // Only process once per part — legacy FX are shared, not per-module.
                    if (moduleIndex > 0)
                    {
                        ParsekLog.Log($"    Engine '{partName}' midx={moduleIndex}: legacy FX already handled by midx=0");
                        continue;
                    }

                    for (int c = 0; c < prefab.transform.childCount; c++)
                    {
                        Transform child = prefab.transform.GetChild(c);
                        string childName = child.name.ToLowerInvariant();
                        if (!childName.StartsWith("fx_")) continue;

                        // Whitelist: only continuous thrust FX (flame, exhaust, smoke)
                        if (childName.Contains("flameout") || childName.Contains("sparks") || childName.Contains("debris"))
                            continue;
                        if (!childName.Contains("flame") && !childName.Contains("exhaust") && !childName.Contains("smoke"))
                            continue;

                        var ps = child.GetComponentInChildren<ParticleSystem>();
                        if (ps == null) continue;

                        GameObject fxClone = Object.Instantiate(child.gameObject);
                        fxClone.transform.SetParent(ghostModelNode.parent, false);
                        fxClone.transform.localPosition = child.localPosition;
                        fxClone.transform.localRotation = child.localRotation;
                        fxClone.transform.localScale = child.localScale;

                        // SmokeTrailControl expects real vessel/part state — destroy it on the ghost
                        var smokeTrail = fxClone.GetComponent("SmokeTrailControl");
                        if (smokeTrail != null)
                            Object.Destroy(smokeTrail);

                        var clonedPs = fxClone.GetComponentInChildren<ParticleSystem>();
                        if (clonedPs != null)
                        {
                            var emission = clonedPs.emission;
                            emission.rateOverTimeMultiplier = 0;
                            clonedPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            info.particleSystems.Add(clonedPs);
                            ParsekLog.Log($"    Engine FX (legacy): '{partName}' midx={moduleIndex} fx='{child.name}'");
                        }
                    }

                    if (info.particleSystems.Count > 0)
                        result.Add(info);
                    else
                        ParsekLog.Log($"    Engine '{partName}' midx={moduleIndex}: no legacy fx_* children found");
                    continue;
                }

                // Scan all effect groups for MODEL_MULTI_PARTICLE entries.
                // We can't reliably filter by runningEffectName from a prefab context,
                // so we collect all particle FX from the EFFECTS node.
                var fxTransformNames = new List<string>();
                var fxModelNames = new List<string>();
                FloatCurve emissionCurve = null;
                FloatCurve speedCurve = null;

                ConfigNode[] effectGroups = effectsNode.GetNodes();

                for (int g = 0; g < effectGroups.Length; g++)
                {
                    ConfigNode[] mmpNodes = effectGroups[g].GetNodes("MODEL_MULTI_PARTICLE_PERSIST");
                    if (mmpNodes.Length == 0)
                        mmpNodes = effectGroups[g].GetNodes("MODEL_MULTI_PARTICLE");

                    for (int mp = 0; mp < mmpNodes.Length; mp++)
                    {
                        string transformName = mmpNodes[mp].GetValue("transformName");
                        string modelName = mmpNodes[mp].GetValue("modelName");
                        if (!string.IsNullOrEmpty(transformName))
                        {
                            fxTransformNames.Add(transformName);
                            fxModelNames.Add(modelName ?? "");

                            // Parse emission and speed curves from the first entry
                            if (emissionCurve == null)
                            {
                                ConfigNode emNode = mmpNodes[mp].GetNode("emission");
                                if (emNode != null)
                                {
                                    emissionCurve = new FloatCurve();
                                    emissionCurve.Load(emNode);
                                }
                                ConfigNode spNode = mmpNodes[mp].GetNode("speed");
                                if (spNode != null)
                                {
                                    speedCurve = new FloatCurve();
                                    speedCurve.Load(spNode);
                                }
                            }
                        }
                    }
                }

                if (fxTransformNames.Count == 0)
                {
                    ParsekLog.Log($"    Engine '{partName}' midx={moduleIndex}: no FX transforms found in EFFECTS");
                    continue;
                }

                info.emissionCurve = emissionCurve;
                info.speedCurve = speedCurve;

                for (int f = 0; f < fxTransformNames.Count; f++)
                {
                    string transformName = fxTransformNames[f];
                    string modelName = fxModelNames[f];

                    // Find matching transform(s) in prefab — may be multiple (multi-nozzle engines)
                    var fxTransforms = FindTransformsRecursive(prefab.transform, transformName);
                    for (int t = 0; t < fxTransforms.Count; t++)
                    {
                        Transform srcFxTransform = fxTransforms[t];

                        // Clone the FX transform chain into the ghost
                        Transform ghostFxParent;
                        // Check if the FX transform is under modelRoot (common case)
                        bool isUnderModel = IsDescendantOf(srcFxTransform, modelRoot);
                        if (isUnderModel)
                        {
                            ghostFxParent = MirrorTransformChain(srcFxTransform, modelRoot, ghostModelNode, cloneMap);
                        }
                        else
                        {
                            // FX transform is outside model subtree — create under ghost model node directly
                            GameObject fxHolder = new GameObject(srcFxTransform.name);
                            fxHolder.transform.SetParent(ghostModelNode, false);
                            fxHolder.transform.localPosition = srcFxTransform.localPosition;
                            fxHolder.transform.localRotation = srcFxTransform.localRotation;
                            fxHolder.transform.localScale = srcFxTransform.localScale;
                            ghostFxParent = fxHolder.transform;
                        }

                        // Instantiate FX model prefab if available
                        if (!string.IsNullOrEmpty(modelName))
                        {
                            GameObject fxPrefab = GameDatabase.Instance.GetModelPrefab(modelName);
                            if (fxPrefab != null)
                            {
                                GameObject fxInstance = Object.Instantiate(fxPrefab);
                                fxInstance.transform.SetParent(ghostFxParent, false);
                                fxInstance.transform.localPosition = Vector3.zero;
                                fxInstance.transform.localRotation = Quaternion.identity;

                                var ps = fxInstance.GetComponentInChildren<ParticleSystem>();
                                if (ps != null)
                                {
                                    var emission = ps.emission;
                                    emission.rateOverTimeMultiplier = 0;
                                    info.particleSystems.Add(ps);
                                    ParsekLog.Log($"    Engine FX cloned: '{partName}' midx={moduleIndex} " +
                                        $"transform='{transformName}' model='{modelName}'");
                                }
                            }
                            else
                            {
                                ParsekLog.Log($"    Engine FX model not found: '{modelName}' for '{partName}'");
                            }
                        }
                    }
                }

                if (info.particleSystems.Count > 0)
                    result.Add(info);
            }

            return result.Count > 0 ? result : null;
        }

        private static List<Transform> FindTransformsRecursive(Transform parent, string name)
        {
            var results = new List<Transform>();
            FindTransformsRecursiveHelper(parent, name, results);
            return results;
        }

        private static void FindTransformsRecursiveHelper(Transform parent, string name, List<Transform> results)
        {
            if (parent.name == name) results.Add(parent);
            for (int i = 0; i < parent.childCount; i++)
                FindTransformsRecursiveHelper(parent.GetChild(i), name, results);
        }

        private static bool IsDescendantOf(Transform child, Transform ancestor)
        {
            Transform cur = child;
            while (cur != null)
            {
                if (cur == ancestor) return true;
                cur = cur.parent;
            }
            return false;
        }

        private static bool AddPartVisuals(Transform root, ConfigNode partNode, Part prefab,
            uint persistentId, string partName, out int meshCount,
            out ParachuteGhostInfo parachuteInfo, out JettisonGhostInfo jettisonInfo,
            out List<EngineGhostInfo> engineInfos)
        {
            meshCount = 0;
            parachuteInfo = null;
            jettisonInfo = null;
            engineInfos = null;
            Transform modelRoot = FindModelRoot(prefab);

            // Dump full hierarchy for engine parts to diagnose missing nozzle meshes.
            // Search from the Part root (not just modelRoot) to catch siblings of "model".
            if (prefab.FindModuleImplementing<ModuleEngines>() != null)
            {
                ParsekLog.Log($"  ENGINE PART HIERARCHY DUMP for '{partName}' pid={persistentId}:");
                DumpTransformHierarchy(prefab.transform, 0, partName);

                // Also log MeshRenderers found from Part root vs modelRoot
                var allMR = prefab.GetComponentsInChildren<MeshRenderer>(true);
                var modelMR = modelRoot.GetComponentsInChildren<MeshRenderer>(true);
                if (allMR.Length != modelMR.Length)
                {
                    ParsekLog.Log($"  WARNING: Part root has {allMR.Length} MeshRenderers but " +
                        $"modelRoot '{modelRoot.name}' has only {modelMR.Length}! " +
                        $"Missing renderers are OUTSIDE the model subtree.");
                    foreach (var mr in allMR)
                    {
                        bool isUnderModel = false;
                        Transform cur = mr.transform;
                        while (cur != null && cur != prefab.transform)
                        {
                            if (cur == modelRoot) { isUnderModel = true; break; }
                            cur = cur.parent;
                        }
                        if (!isUnderModel)
                        {
                            var mf = mr.GetComponent<MeshFilter>();
                            string meshName = mf?.sharedMesh?.name ?? "(no mesh)";
                            ParsekLog.Log($"    OUTSIDE-MODEL MR: '{mr.gameObject.name}' mesh={meshName} " +
                                $"path={GetTransformPath(mr.transform, prefab.transform)}");
                        }
                    }
                }
            }

            var meshRenderers = modelRoot.GetComponentsInChildren<MeshRenderer>(true);
            var skinnedRenderers = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if ((meshRenderers == null || meshRenderers.Length == 0) &&
                (skinnedRenderers == null || skinnedRenderers.Length == 0))
                return false;

            int totalMR = meshRenderers != null ? meshRenderers.Length : 0;
            int totalSMR = skinnedRenderers != null ? skinnedRenderers.Length : 0;
            ParsekLog.Log($"  Part '{partName}' pid={persistentId}: modelRoot='{modelRoot.name}' " +
                $"modelScale={modelRoot.localScale}, " +
                $"{totalMR} MeshRenderers, {totalSMR} SkinnedMeshRenderers");

            // Name by persistentId for O(1) lookup during playback; fall back to part name
            string partLabel = persistentId != 0
                ? persistentId.ToString()
                : (prefab.partInfo?.name ?? "unknown");
            GameObject partRoot = new GameObject($"ghost_part_{partLabel}");
            partRoot.transform.SetParent(root, false);

            Vector3 localPos;
            if (TryParseVector3(GetPartPositionRaw(partNode), out localPos))
                partRoot.transform.localPosition = localPos;

            Quaternion localRot;
            if (TryParseQuaternion(GetPartRotationRaw(partNode), out localRot))
                partRoot.transform.localRotation = localRot;

            bool added = false;

            // Clone map: maps prefab transforms → ghost cloned transforms.
            // Deduplicates shared parent nodes across multiple renderers.
            var cloneMap = new Dictionary<Transform, Transform>();

            // Preserve the model root's own local transform (position/rotation/scale).
            // Many KSP parts have non-identity transforms on the "model" child.
            // partRoot already has snapshot pos/rot, so add an intermediate node.
            GameObject modelNode = new GameObject(modelRoot.name);
            modelNode.transform.SetParent(partRoot.transform, false);
            modelNode.transform.localPosition = modelRoot.localPosition;
            modelNode.transform.localRotation = modelRoot.localRotation;
            modelNode.transform.localScale = modelRoot.localScale;
            cloneMap[modelRoot] = modelNode.transform;

            for (int r = 0; r < meshRenderers.Length; r++)
            {
                var mr = meshRenderers[r];
                if (mr == null) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                Transform leaf = MirrorTransformChain(mr.transform, modelRoot, modelNode.transform, cloneMap);
                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                meshCount++;
                ParsekLog.Log($"    MR[{r}] '{mr.gameObject.name}' mesh={mf.sharedMesh.name} " +
                    $"localPos={leaf.localPosition} localScale={leaf.localScale}");
                added = true;
            }

            for (int r = 0; r < skinnedRenderers.Length; r++)
            {
                var smr = skinnedRenderers[r];
                if (smr == null || smr.sharedMesh == null) continue;

                Transform leaf = MirrorTransformChain(smr.transform, modelRoot, modelNode.transform, cloneMap);
                // Use shared bind-pose mesh as a static ghost visual.
                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = smr.sharedMesh;
                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;
                meshCount++;
                ParsekLog.Log($"    SMR[{r}] '{smr.gameObject.name}' mesh={smr.sharedMesh.name} " +
                    $"localPos={leaf.localPosition} localScale={leaf.localScale}");
                added = true;
            }

            // Detect parachute parts via cloneMap (after cloneMap is fully populated)
            ModuleParachute chute = prefab.FindModuleImplementing<ModuleParachute>();
            if (chute != null)
            {
                string canopyName = chute.canopyName;
                string capName = chute.capName;

                Transform srcCanopy = !string.IsNullOrEmpty(canopyName)
                    ? prefab.FindModelTransform(canopyName) : null;
                Transform srcCap = !string.IsNullOrEmpty(capName)
                    ? prefab.FindModelTransform(capName) : null;

                // Look up ghost clones via cloneMap (deterministic, no name collisions).
                // NOTE: For EVA kerbals, FindModelRoot uses "model01" (body) but canopy is
                // under "model" (accessories), so srcCanopy won't be in cloneMap.
                // This is expected — EVA falls through to fake sphere fallback.
                Transform ghostCanopy = null, ghostCap = null;
                if (srcCanopy != null) cloneMap.TryGetValue(srcCanopy, out ghostCanopy);
                if (srcCap != null) cloneMap.TryGetValue(srcCap, out ghostCap);

                if (ghostCanopy != null)
                {
                    var (scale, pos) = SampleDeployedCanopy(prefab, chute);
                    parachuteInfo = new ParachuteGhostInfo
                    {
                        partPersistentId = persistentId,
                        canopyTransform = ghostCanopy,
                        capTransform = ghostCap,
                        deployedCanopyScale = scale,
                        deployedCanopyPos = pos
                    };
                    ParsekLog.Log($"    Parachute detected: canopy='{canopyName}' cap='{capName}' " +
                        $"deployScale={scale}");
                }
                else if (srcCanopy != null)
                {
                    ParsekLog.Log($"    Parachute '{canopyName}' found on prefab but not in cloneMap — will use fake canopy");
                }
            }

            // Detect jettison parts (shrouds/fairings) via cloneMap
            ModuleJettison jettison = prefab.FindModuleImplementing<ModuleJettison>();
            if (jettison != null)
            {
                string jettisonName = jettison.jettisonName;
                if (!string.IsNullOrEmpty(jettisonName))
                {
                    Transform srcJettison = prefab.FindModelTransform(jettisonName);
                    Transform ghostJettison = null;
                    if (srcJettison != null)
                        cloneMap.TryGetValue(srcJettison, out ghostJettison);

                    if (ghostJettison != null)
                    {
                        jettisonInfo = new JettisonGhostInfo
                        {
                            partPersistentId = persistentId,
                            jettisonTransform = ghostJettison
                        };
                        ParsekLog.Log($"    Jettison detected: '{jettisonName}' pid={persistentId}");
                    }
                    else if (srcJettison != null)
                    {
                        ParsekLog.Log($"    Jettison '{jettisonName}' found on prefab but not in cloneMap");
                    }
                }
            }

            // Detect engine parts and clone FX particle systems
            engineInfos = TryBuildEngineFX(prefab, persistentId, partName, modelRoot,
                modelNode.transform, cloneMap);

            if (!added)
            {
                Object.Destroy(partRoot);
                return false;
            }

            return true;
        }
    }
}
