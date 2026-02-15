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

    internal struct DeployableTransformState
    {
        public Transform t;
        public Vector3 stowedPos;
        public Quaternion stowedRot;
        public Vector3 stowedScale;
        public Vector3 deployedPos;
        public Quaternion deployedRot;
        public Vector3 deployedScale;
    }

    internal class DeployableGhostInfo
    {
        public uint partPersistentId;
        public List<DeployableTransformState> transforms;
    }

    internal class LightGhostInfo
    {
        public uint partPersistentId;
        public List<Light> lights;
    }

    internal class RcsGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public FloatCurve emissionCurve;
        public FloatCurve speedCurve;
    }

    internal class FairingGhostInfo
    {
        public uint partPersistentId;
        public GameObject fairingMeshObject;
    }

    internal static class GhostVisualBuilder
    {
        private static readonly Regex trailingNumericSuffixRegex =
            new Regex(@"^(.*)_\d+$", RegexOptions.Compiled);

        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos,
            out List<EngineGhostInfo> engineInfos,
            out List<DeployableGhostInfo> deployableInfos,
            out List<LightGhostInfo> lightInfos,
            out List<FairingGhostInfo> fairingInfos,
            out List<RcsGhostInfo> rcsInfos)
        {
            parachuteInfos = null;
            jettisonInfos = null;
            engineInfos = null;
            deployableInfos = null;
            lightInfos = null;
            fairingInfos = null;
            rcsInfos = null;
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
            var collectedDeployableInfos = new List<DeployableGhostInfo>();
            var collectedLightInfos = new List<LightGhostInfo>();
            var collectedFairingInfos = new List<FairingGhostInfo>();
            var collectedRcsInfos = new List<RcsGhostInfo>();

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
                    ParsekLog.Log($"  Ghost part SKIPPED (no name): raw='{rawPart ?? "null"}' index={i}");
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
                    ParsekLog.Log($"  Ghost part SKIPPED (no prefab): '{partName}' pid={persistentId}");
                    continue;
                }

                int meshCount = 0;
                ParachuteGhostInfo parachuteInfo;
                JettisonGhostInfo jettisonInfo;
                List<EngineGhostInfo> partEngineInfos;
                DeployableGhostInfo deployableInfo;
                LightGhostInfo lightInfo;
                FairingGhostInfo fairingInfo;
                List<RcsGhostInfo> partRcsInfos;
                bool partVisualAdded = AddPartVisuals(root.transform, partNode, ap.partPrefab,
                    persistentId, partName, out meshCount, out parachuteInfo, out jettisonInfo,
                    out partEngineInfos, out deployableInfo, out lightInfo, out fairingInfo,
                    out partRcsInfos);
                if (partVisualAdded)
                    visualCount++;
                else
                {
                    skippedMesh++;
                    ParsekLog.Log($"  Ghost part SKIPPED (no mesh): '{partName}' pid={persistentId}");
                }
                addedAnyVisual = addedAnyVisual || partVisualAdded;

                if (parachuteInfo != null)
                    collectedParachuteInfos.Add(parachuteInfo);
                if (jettisonInfo != null)
                    collectedJettisonInfos.Add(jettisonInfo);
                if (partEngineInfos != null)
                    collectedEngineInfos.AddRange(partEngineInfos);
                if (deployableInfo != null)
                    collectedDeployableInfos.Add(deployableInfo);
                if (lightInfo != null)
                    collectedLightInfos.Add(lightInfo);
                if (fairingInfo != null)
                    collectedFairingInfos.Add(fairingInfo);
                if (partRcsInfos != null)
                    collectedRcsInfos.AddRange(partRcsInfos);
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
            deployableInfos = collectedDeployableInfos.Count > 0 ? collectedDeployableInfos : null;
            lightInfos = collectedLightInfos.Count > 0 ? collectedLightInfos : null;
            fairingInfos = collectedFairingInfos.Count > 0 ? collectedFairingInfos : null;
            rcsInfos = collectedRcsInfos.Count > 0 ? collectedRcsInfos : null;
            return root;
        }

        // Backward-compat overload without rcs infos
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos,
            out List<EngineGhostInfo> engineInfos,
            out List<DeployableGhostInfo> deployableInfos,
            out List<LightGhostInfo> lightInfos,
            out List<FairingGhostInfo> fairingInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName,
                out parachuteInfos, out jettisonInfos, out engineInfos, out deployableInfos,
                out lightInfos, out fairingInfos, out _);
        }

        // Backward-compat overload without fairing/rcs infos
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos,
            out List<EngineGhostInfo> engineInfos,
            out List<DeployableGhostInfo> deployableInfos,
            out List<LightGhostInfo> lightInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName,
                out parachuteInfos, out jettisonInfos, out engineInfos, out deployableInfos,
                out lightInfos, out _, out _);
        }

        // Backward-compat overload without light/fairing/rcs infos
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos,
            out List<EngineGhostInfo> engineInfos,
            out List<DeployableGhostInfo> deployableInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName,
                out parachuteInfos, out jettisonInfos, out engineInfos, out deployableInfos, out _, out _, out _);
        }

        // Backward-compat overload without deployable/light/fairing/rcs infos
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos,
            out List<EngineGhostInfo> engineInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName,
                out parachuteInfos, out jettisonInfos, out engineInfos, out _, out _, out _, out _);
        }

        // Overload without info outputs for callers that don't need them (preview ghost)
        internal static GameObject BuildTimelineGhostFromSnapshot(RecordingStore.Recording rec, string rootName)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out _, out _, out _, out _, out _, out _, out _);
        }

        // Overload with parachute + jettison info (backward compat)
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out parachuteInfos, out jettisonInfos, out _, out _, out _, out _, out _);
        }

        // Overload with only parachute info for backward compat
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out parachuteInfos, out _, out _, out _, out _, out _, out _);
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
        /// Walk from descendant up to find the immediate child of ancestor.
        /// Returns null if descendant is not under ancestor.
        /// </summary>
        private static Transform FindImmediateChildOf(Transform descendant, Transform ancestor)
        {
            Transform cur = descendant;
            while (cur != null && cur.parent != ancestor)
                cur = cur.parent;
            return (cur != null && cur.parent == ancestor) ? cur : null;
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

            // If animation produced a near-zero scale, it failed to animate properly.
            // Use a conservative fallback. Threshold is very low because stock parachutes
            // (e.g. parachuteSingle) legitimately deploy at small scales like (0.1, 0.1, 0.1).
            if (sampled && scale.magnitude < 0.01f)
            {
                ParsekLog.Log($"  Animation produced near-zero scale ({scale}), using deployed canopy fallback");
                scale = Vector3.one;
                pos = Vector3.zero;
            }

            var result = (scale, pos);
            deployedCanopyCache[key] = result;
            ParsekLog.Log($"  Deployed canopy for '{key}': scale={scale} pos={pos}");
            return result;
        }

        // Cache: partName → list of (path, stowed state, deployed state) — sample once per part type
        private static readonly Dictionary<string, List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)>> deployableCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        internal static void ClearDeployableCache()
        {
            deployableCache.Clear();
        }

        // Cache: partName → list of gear animation transform states — sample once per part type
        private static readonly Dictionary<string, List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)>> gearCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        internal static void ClearGearCache()
        {
            gearCache.Clear();
        }

        // Cache: partName → list of cargo bay animation transform states — sample once per part type
        private static readonly Dictionary<string, List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)>> cargoBayCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        internal static void ClearCargoBayCache()
        {
            cargoBayCache.Clear();
        }

        /// <summary>
        /// Sample stowed and deployed transform states from a landing gear's animation.
        /// Uses animationTrfName to resolve the correct Animation component (avoids binding spotlight
        /// animation on parts like GearSmall that have multiple Animation components).
        /// deployedPosition determines which animation endpoint is "deployed" (1 for most gear, 0 for rover wheels).
        /// Returns null if no animation or no transform deltas.
        /// </summary>
        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleGearStates(
            Part prefab, string animationStateName, string animationTrfName, float deployedPosition)
        {
            string key = prefab.partInfo?.name ?? prefab.name;
            if (gearCache.TryGetValue(key, out var cached))
                return cached;

            if (string.IsNullOrEmpty(animationStateName))
            {
                ParsekLog.Log($"  Gear '{key}': no animationStateName — skipping animation sampling");
                gearCache[key] = null;
                return null;
            }

            Transform modelRoot = FindModelRoot(prefab);
            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);

            List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)> result = null;

            try
            {
                // Resolve Animation via animationTrfName first (prevents binding wrong animation)
                Animation anim = null;
                if (!string.IsNullOrEmpty(animationTrfName))
                {
                    Transform animTrf = FindTransformRecursive(tempClone.transform, animationTrfName);
                    if (animTrf != null)
                        anim = animTrf.GetComponent<Animation>();
                }
                // Fallback to any Animation on the clone
                if (anim == null)
                    anim = tempClone.GetComponentInChildren<Animation>(true);

                if (anim == null)
                {
                    ParsekLog.Log($"  Gear '{key}': no Animation component on model clone");
                    gearCache[key] = null;
                    return null;
                }

                AnimationState state = anim[animationStateName];
                if (state == null)
                {
                    ParsekLog.Log($"  Gear '{key}': animation '{animationStateName}' not found on clone");
                    gearCache[key] = null;
                    return null;
                }

                // Determine animation endpoints based on deployedPosition.
                // Most gear: deployedPosition=1 → stowed at time=0, deployed at time=1
                // Rover wheel: deployedPosition=0 → stowed at time=1, deployed at time=0
                float stowedTime = deployedPosition >= 0.5f ? 0f : 1f;
                float deployedTime = deployedPosition >= 0.5f ? 1f : 0f;

                // Sample stowed state
                state.enabled = true;
                state.speed = 0f;
                state.normalizedTime = stowedTime;
                state.weight = 1f;
                anim.Play(animationStateName);
                anim.Sample();

                var allTransforms = tempClone.GetComponentsInChildren<Transform>(true);
                var stowedStates = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    stowedStates[path] = (allTransforms[i].localPosition, allTransforms[i].localRotation, allTransforms[i].localScale);
                }

                // Sample deployed state
                state.normalizedTime = deployedTime;
                anim.Sample();

                result = new List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    if (!stowedStates.TryGetValue(path, out var stowed))
                        continue;

                    Vector3 dPos = allTransforms[i].localPosition;
                    Quaternion dRot = allTransforms[i].localRotation;
                    Vector3 dScale = allTransforms[i].localScale;

                    float posDelta = (dPos - stowed.pos).sqrMagnitude;
                    float rotDelta = Quaternion.Angle(dRot, stowed.rot);
                    float scaleDelta = (dScale - stowed.scale).sqrMagnitude;

                    if (posDelta > 0.0001f || rotDelta > 0.01f || scaleDelta > 0.0001f)
                    {
                        result.Add((path, stowed.pos, stowed.rot, stowed.scale, dPos, dRot, dScale));
                    }
                }

                if (result.Count == 0)
                {
                    ParsekLog.Log($"  Gear '{key}': animation '{animationStateName}' produced no transform deltas");
                    result = null;
                }
                else
                {
                    ParsekLog.Log($"  Gear '{key}': sampled {result.Count} animated transforms from '{animationStateName}'");
                }
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }

            gearCache[key] = result;
            return result;
        }

        /// <summary>
        /// Sample closed and open transform states from a cargo bay's animation.
        /// closedPosition determines which animation endpoint is "closed":
        ///   near 0 → closed at time=0, open at time=1 (service bays)
        ///   near 1 → closed at time=1, open at time=0 (Mk3/Mk2 cargo bays)
        /// Returns stowed=closed, deployed=open (normalized for DeployableGhostInfo reuse).
        /// Returns null if no animation or no transform deltas.
        /// </summary>
        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleCargoBayStates(
            Part prefab, string animationName, float closedPosition)
        {
            string key = prefab.partInfo?.name ?? prefab.name;
            if (cargoBayCache.TryGetValue(key, out var cached))
                return cached;

            if (string.IsNullOrEmpty(animationName))
            {
                ParsekLog.Log($"  CargoBay '{key}': no animationName — skipping animation sampling");
                cargoBayCache[key] = null;
                return null;
            }

            // Determine animation endpoints based on closedPosition
            float closedTime, openTime;
            if (closedPosition > 0.9f)
            {
                closedTime = 1f;
                openTime = 0f;
            }
            else if (closedPosition < 0.1f)
            {
                closedTime = 0f;
                openTime = 1f;
            }
            else
            {
                ParsekLog.Log($"  CargoBay '{key}': non-standard closedPosition={closedPosition} — skipping");
                cargoBayCache[key] = null;
                return null;
            }

            Transform modelRoot = FindModelRoot(prefab);
            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);

            List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)> result = null;

            try
            {
                // Search all Animation components for one containing the specific animationName clip
                Animation anim = null;
                foreach (var candidate in tempClone.GetComponentsInChildren<Animation>(true))
                {
                    if (candidate[animationName] != null)
                    {
                        anim = candidate;
                        break;
                    }
                }
                // Fallback to any Animation on the clone
                if (anim == null)
                    anim = tempClone.GetComponentInChildren<Animation>(true);

                if (anim == null)
                {
                    ParsekLog.Log($"  CargoBay '{key}': no Animation component on model clone");
                    cargoBayCache[key] = null;
                    return null;
                }

                AnimationState state = anim[animationName];
                if (state == null)
                {
                    ParsekLog.Log($"  CargoBay '{key}': animation '{animationName}' not found on clone");
                    cargoBayCache[key] = null;
                    return null;
                }

                // Sample closed state (stowed)
                state.enabled = true;
                state.speed = 0f;
                state.normalizedTime = closedTime;
                state.weight = 1f;
                anim.Play(animationName);
                anim.Sample();

                var allTransforms = tempClone.GetComponentsInChildren<Transform>(true);
                var stowedStates = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    stowedStates[path] = (allTransforms[i].localPosition, allTransforms[i].localRotation, allTransforms[i].localScale);
                }

                // Sample open state (deployed)
                state.normalizedTime = openTime;
                anim.Sample();

                result = new List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    if (!stowedStates.TryGetValue(path, out var stowed))
                        continue;

                    Vector3 dPos = allTransforms[i].localPosition;
                    Quaternion dRot = allTransforms[i].localRotation;
                    Vector3 dScale = allTransforms[i].localScale;

                    float posDelta = (dPos - stowed.pos).sqrMagnitude;
                    float rotDelta = Quaternion.Angle(dRot, stowed.rot);
                    float scaleDelta = (dScale - stowed.scale).sqrMagnitude;

                    if (posDelta > 0.0001f || rotDelta > 0.01f || scaleDelta > 0.0001f)
                    {
                        result.Add((path, stowed.pos, stowed.rot, stowed.scale, dPos, dRot, dScale));
                    }
                }

                if (result.Count == 0)
                {
                    ParsekLog.Log($"  CargoBay '{key}': animation '{animationName}' produced no transform deltas");
                    result = null;
                }
                else
                {
                    ParsekLog.Log($"  CargoBay '{key}': sampled {result.Count} animated transforms from '{animationName}'");
                }
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }

            cargoBayCache[key] = result;
            return result;
        }

        /// <summary>
        /// Sample stowed (time=0) and deployed (time=1) transform states from a deployable part's animation.
        /// Returns a list of (path, stowed, deployed) tuples for transforms that actually change.
        /// Returns null if the part has no animationName or no animation component.
        /// </summary>
        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleDeployableStates(
            Part prefab, ModuleDeployablePart deployable)
        {
            string key = prefab.partInfo?.name ?? prefab.name;
            if (deployableCache.TryGetValue(key, out var cached))
                return cached;

            string animName = deployable.animationName;
            if (string.IsNullOrEmpty(animName))
            {
                ParsekLog.Log($"  Deployable '{key}': no animationName — skipping animation sampling");
                deployableCache[key] = null;
                return null;
            }

            Transform modelRoot = FindModelRoot(prefab);
            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);

            List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)> result = null;

            try
            {
                Animation anim = tempClone.GetComponentInChildren<Animation>(true);
                if (anim == null)
                {
                    ParsekLog.Log($"  Deployable '{key}': no Animation component on model clone");
                    deployableCache[key] = null;
                    return null;
                }

                AnimationState state = anim[animName];
                if (state == null)
                {
                    ParsekLog.Log($"  Deployable '{key}': animation '{animName}' not found on clone");
                    deployableCache[key] = null;
                    return null;
                }

                // Sample stowed state (time=0)
                state.enabled = true;
                state.speed = 0f;
                state.normalizedTime = 0f;
                state.weight = 1f;
                anim.Play(animName);
                anim.Sample();

                // Capture all child transforms at stowed
                var allTransforms = tempClone.GetComponentsInChildren<Transform>(true);
                var stowedStates = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    stowedStates[path] = (allTransforms[i].localPosition, allTransforms[i].localRotation, allTransforms[i].localScale);
                }

                // Sample deployed state (time=1)
                state.normalizedTime = 1f;
                anim.Sample();

                // Compare and collect transforms that differ
                result = new List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    if (!stowedStates.TryGetValue(path, out var stowed))
                        continue;

                    Vector3 dPos = allTransforms[i].localPosition;
                    Quaternion dRot = allTransforms[i].localRotation;
                    Vector3 dScale = allTransforms[i].localScale;

                    // Delta filter: only include transforms that actually changed
                    float posDelta = (dPos - stowed.pos).sqrMagnitude;
                    float rotDelta = Quaternion.Angle(dRot, stowed.rot);
                    float scaleDelta = (dScale - stowed.scale).sqrMagnitude;

                    if (posDelta > 0.0001f || rotDelta > 0.01f || scaleDelta > 0.0001f)
                    {
                        result.Add((path, stowed.pos, stowed.rot, stowed.scale, dPos, dRot, dScale));
                    }
                }

                if (result.Count == 0)
                {
                    ParsekLog.Log($"  Deployable '{key}': animation '{animName}' produced no transform deltas");
                    result = null;
                }
                else
                {
                    ParsekLog.Log($"  Deployable '{key}': sampled {result.Count} animated transforms from '{animName}'");
                }
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }

            deployableCache[key] = result;
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

        /// <summary>
        /// Find a transform by slash-separated path relative to a root.
        /// e.g. "a/b/c" finds root → a → b → c.
        /// </summary>
        private static Transform FindTransformByPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            string[] parts = path.Split('/');
            Transform cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                cur = cur.Find(parts[i]);
                if (cur == null) return null;
            }
            return cur;
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

        private static List<RcsGhostInfo> TryBuildRcsFX(
            Part prefab, uint persistentId, string partName,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            // Find all ModuleRCS on the part (catches both ModuleRCS and ModuleRCSFX)
            int midx = 0;
            var rcsModules = new List<(ModuleRCS rcs, int moduleIndex)>();
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                var rcs = prefab.Modules[m] as ModuleRCS;
                if (rcs != null)
                {
                    rcsModules.Add((rcs, midx));
                    midx++;
                }
            }
            if (rcsModules.Count == 0) return null;

            var result = new List<RcsGhostInfo>();

            for (int r = 0; r < rcsModules.Count; r++)
            {
                var (rcs, moduleIndex) = rcsModules[r];
                var info = new RcsGhostInfo
                {
                    partPersistentId = persistentId,
                    moduleIndex = moduleIndex
                };

                ConfigNode partConfig = prefab.partInfo?.partConfig;
                if (partConfig == null)
                {
                    ParsekLog.Log($"    RCS '{partName}' midx={moduleIndex}: no partConfig — skipping FX");
                    continue;
                }

                ConfigNode effectsNode = partConfig.GetNode("EFFECTS");
                if (effectsNode == null)
                {
                    ParsekLog.Log($"    RCS '{partName}' midx={moduleIndex}: no EFFECTS node — skipping FX");
                    continue;
                }

                // Determine the running effect name from the module.
                // ModuleRCSFX exposes runningEffectName; base ModuleRCS defaults to "running".
                string runningEffect = "running";
                var rcsFX = rcs as ModuleRCSFX;
                if (rcsFX != null && !string.IsNullOrEmpty(rcsFX.runningEffectName))
                    runningEffect = rcsFX.runningEffectName;

                // Only scan the effect group matching runningEffectName
                // (prevents picking up engine FX on mixed engine+RCS parts)
                ConfigNode effectGroup = effectsNode.GetNode(runningEffect);
                if (effectGroup == null)
                {
                    ParsekLog.Log($"    RCS '{partName}' midx={moduleIndex}: no '{runningEffect}' effect group");
                    continue;
                }

                var fxTransformNames = new List<string>();
                var fxModelNames = new List<string>();
                FloatCurve emissionCurve = null;
                FloatCurve speedCurve = null;

                ConfigNode[] mmpNodes = effectGroup.GetNodes("MODEL_MULTI_PARTICLE_PERSIST");
                if (mmpNodes.Length == 0)
                    mmpNodes = effectGroup.GetNodes("MODEL_MULTI_PARTICLE");

                for (int mp = 0; mp < mmpNodes.Length; mp++)
                {
                    string transformName = mmpNodes[mp].GetValue("transformName");
                    string modelName = mmpNodes[mp].GetValue("modelName");
                    if (!string.IsNullOrEmpty(transformName))
                    {
                        fxTransformNames.Add(transformName);
                        fxModelNames.Add(modelName ?? "");

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

                if (fxTransformNames.Count == 0)
                {
                    ParsekLog.Log($"    RCS '{partName}' midx={moduleIndex}: no FX transforms in '{runningEffect}' group");
                    continue;
                }

                info.emissionCurve = emissionCurve;
                info.speedCurve = speedCurve;

                for (int f = 0; f < fxTransformNames.Count; f++)
                {
                    string transformName = fxTransformNames[f];
                    string modelName = fxModelNames[f];

                    var fxTransforms = FindTransformsRecursive(prefab.transform, transformName);
                    for (int t = 0; t < fxTransforms.Count; t++)
                    {
                        Transform srcFxTransform = fxTransforms[t];

                        Transform ghostFxParent;
                        bool isUnderModel = IsDescendantOf(srcFxTransform, modelRoot);
                        if (isUnderModel)
                        {
                            ghostFxParent = MirrorTransformChain(srcFxTransform, modelRoot, ghostModelNode, cloneMap);
                        }
                        else
                        {
                            GameObject fxHolder = new GameObject(srcFxTransform.name);
                            fxHolder.transform.SetParent(ghostModelNode, false);
                            fxHolder.transform.localPosition = srcFxTransform.localPosition;
                            fxHolder.transform.localRotation = srcFxTransform.localRotation;
                            fxHolder.transform.localScale = srcFxTransform.localScale;
                            ghostFxParent = fxHolder.transform;
                        }

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
                                    ParsekLog.Log($"    RCS FX cloned: '{partName}' midx={moduleIndex} " +
                                        $"transform='{transformName}' model='{modelName}'");
                                }
                            }
                            else
                            {
                                ParsekLog.Log($"    RCS FX model not found: '{modelName}' for '{partName}'");
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

        private static ConfigNode FindModuleNode(ConfigNode partNode, string moduleName)
        {
            var modules = partNode.GetNodes("MODULE");
            if (modules == null) return null;
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") == moduleName)
                    return modules[i];
            }
            return null;
        }

        internal static Mesh GenerateFairingConeMesh(
            List<(float h, float r)> sections, int nSides, Vector3 pivot, Vector3 axis)
        {
            // Sort sections by height
            sections.Sort((a, b) => a.h.CompareTo(b.h));

            nSides = Mathf.Min(nSides, 24);
            if (nSides < 3) nSides = 3;

            Quaternion axisRot = Quaternion.FromToRotation(Vector3.up, axis.normalized);

            // Build vertices and UVs: (nSides+1) verts per ring (duplicated seam), plus apex if last r ≈ 0
            int ringCount = sections.Count;
            bool hasApex = sections[ringCount - 1].r < 0.01f;
            int ringsToGenerate = hasApex ? ringCount - 1 : ringCount;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();

            for (int ring = 0; ring < ringsToGenerate; ring++)
            {
                float h = sections[ring].h;
                float r = sections[ring].r;
                float vCoord = ringCount > 1 ? (float)ring / (ringCount - 1) : 0f;

                for (int s = 0; s <= nSides; s++)
                {
                    float angle = (float)s / nSides * Mathf.PI * 2f;
                    float x = Mathf.Cos(angle) * r;
                    float z = Mathf.Sin(angle) * r;

                    Vector3 localPos = new Vector3(x, h, z);
                    Vector3 rotatedPos = axisRot * localPos + pivot;
                    vertices.Add(rotatedPos);

                    float uCoord = (float)s / nSides;
                    uvs.Add(new Vector2(uCoord, vCoord));
                }
            }

            // Apex vertex
            int apexIndex = -1;
            if (hasApex)
            {
                float apexH = sections[ringCount - 1].h;
                Vector3 apexLocal = new Vector3(0f, apexH, 0f);
                Vector3 apexPos = axisRot * apexLocal + pivot;
                apexIndex = vertices.Count;
                vertices.Add(apexPos);
                uvs.Add(new Vector2(0.5f, 1f));
            }

            // Build triangles
            var triangles = new List<int>();
            int vertsPerRing = nSides + 1;

            // Connect adjacent rings with triangle strips (CW winding for outward-facing normals)
            for (int ring = 0; ring < ringsToGenerate - 1; ring++)
            {
                int ringBase = ring * vertsPerRing;
                int nextBase = (ring + 1) * vertsPerRing;

                for (int s = 0; s < nSides; s++)
                {
                    int bl = ringBase + s;
                    int br = ringBase + s + 1;
                    int tl = nextBase + s;
                    int tr = nextBase + s + 1;

                    // CW winding (outward-facing normals from outside)
                    triangles.Add(bl);
                    triangles.Add(tl);
                    triangles.Add(br);

                    triangles.Add(br);
                    triangles.Add(tl);
                    triangles.Add(tr);
                }
            }

            // Connect last ring to apex
            if (hasApex && ringsToGenerate > 0)
            {
                int lastRingBase = (ringsToGenerate - 1) * vertsPerRing;
                for (int s = 0; s < nSides; s++)
                {
                    int bl = lastRingBase + s;
                    int br = lastRingBase + s + 1;

                    triangles.Add(bl);
                    triangles.Add(apexIndex);
                    triangles.Add(br);
                }
            }

            Mesh mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static FairingGhostInfo BuildFairingVisual(
            ConfigNode partNode, Part prefab, Transform modelNode,
            uint persistentId, string partName)
        {
            ConfigNode fairingModule = FindModuleNode(partNode, "ModuleProceduralFairing");
            if (fairingModule == null) return null;

            // Skip mesh for already-deployed fairings
            string fsmState = fairingModule.GetValue("fsm");
            if (fsmState == "st_flight_deployed") return null;

            var xNodes = fairingModule.GetNodes("XSECTION");
            if (xNodes == null || xNodes.Length < 2)
            {
                if (xNodes != null && xNodes.Length > 0)
                    ParsekLog.Log($"    Fairing '{partName}': only {xNodes.Length} XSECTION(s) — skipping cone mesh");
                return null;
            }

            var sections = new List<(float h, float r)>();
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < xNodes.Length; i++)
            {
                float h, r;
                if (float.TryParse(xNodes[i].GetValue("h"), NumberStyles.Float, ic, out h) &&
                    float.TryParse(xNodes[i].GetValue("r"), NumberStyles.Float, ic, out r))
                    sections.Add((h, r));
            }
            if (sections.Count < 2) return null;

            // Read geometry params from prefab module
            var fairingPrefab = prefab.FindModuleImplementing<ModuleProceduralFairing>();
            int nSides = fairingPrefab != null ? Mathf.Min(fairingPrefab.nSides, 24) : 24;
            Vector3 pivot = fairingPrefab != null ? fairingPrefab.pivot : Vector3.zero;
            Vector3 axis = fairingPrefab != null ? fairingPrefab.axis : Vector3.up;

            Mesh mesh = GenerateFairingConeMesh(sections, nSides, pivot, axis);

            GameObject go = new GameObject("fairing_panels");
            go.transform.SetParent(modelNode, false);
            go.AddComponent<MeshFilter>().mesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("KSP/Diffuse"))
            {
                color = new Color(0.85f, 0.85f, 0.85f)
            };

            ParsekLog.Log($"    Fairing detected: '{partName}' pid={persistentId}, " +
                $"cone mesh generated ({sections.Count} sections, {nSides} sides)");

            return new FairingGhostInfo
            {
                partPersistentId = persistentId,
                fairingMeshObject = go
            };
        }

        private static bool AddPartVisuals(Transform root, ConfigNode partNode, Part prefab,
            uint persistentId, string partName, out int meshCount,
            out ParachuteGhostInfo parachuteInfo, out JettisonGhostInfo jettisonInfo,
            out List<EngineGhostInfo> engineInfos, out DeployableGhostInfo deployableInfo,
            out LightGhostInfo lightInfo, out FairingGhostInfo fairingInfo,
            out List<RcsGhostInfo> rcsInfos)
        {
            meshCount = 0;
            parachuteInfo = null;
            jettisonInfo = null;
            engineInfos = null;
            deployableInfo = null;
            lightInfo = null;
            fairingInfo = null;
            rcsInfos = null;
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

                // If canopy exists on the prefab but wasn't cloned (e.g. EVA kerbals where
                // canopy lives under "model" but FindModelRoot returned "model01"), lazily
                // clone the canopy's visual root subtree so it enters cloneMap.
                if (srcCanopy != null && !cloneMap.ContainsKey(srcCanopy))
                {
                    Transform canopyVisualRoot = FindImmediateChildOf(srcCanopy, prefab.transform);
                    if (canopyVisualRoot != null && canopyVisualRoot != modelRoot)
                    {
                        ParsekLog.Log($"    Canopy '{canopyName}' is outside modelRoot '{modelRoot.name}'" +
                            $" — cloning subtree '{canopyVisualRoot.name}'");

                        GameObject subNode = new GameObject(canopyVisualRoot.name);
                        subNode.transform.SetParent(partRoot.transform, false);
                        subNode.transform.localPosition = canopyVisualRoot.localPosition;
                        subNode.transform.localRotation = canopyVisualRoot.localRotation;
                        subNode.transform.localScale = canopyVisualRoot.localScale;
                        cloneMap[canopyVisualRoot] = subNode.transform;

                        var subMR = canopyVisualRoot.GetComponentsInChildren<MeshRenderer>(true);
                        for (int r = 0; r < subMR.Length; r++)
                        {
                            var mr = subMR[r];
                            if (mr == null) continue;
                            var mf = mr.GetComponent<MeshFilter>();
                            if (mf == null || mf.sharedMesh == null) continue;

                            Transform leaf = MirrorTransformChain(mr.transform, canopyVisualRoot,
                                subNode.transform, cloneMap);
                            leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                            leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                            meshCount++;
                            added = true;
                            ParsekLog.Log($"      CANOPY-SUB MR[{r}] '{mr.gameObject.name}' " +
                                $"mesh={mf.sharedMesh.name}");
                        }

                        var subSMR = canopyVisualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                        for (int r = 0; r < subSMR.Length; r++)
                        {
                            var smr = subSMR[r];
                            if (smr == null || smr.sharedMesh == null) continue;

                            Transform leaf = MirrorTransformChain(smr.transform, canopyVisualRoot,
                                subNode.transform, cloneMap);
                            leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = smr.sharedMesh;
                            leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;
                            meshCount++;
                            added = true;
                            ParsekLog.Log($"      CANOPY-SUB SMR[{r}] '{smr.gameObject.name}' " +
                                $"mesh={smr.sharedMesh.name}");
                        }
                    }
                }

                // Look up ghost clones via cloneMap (deterministic, no name collisions).
                // For EVA kerbals, canopy subtree was lazily cloned above when detected
                // outside modelRoot, so both body and canopy now appear in cloneMap.
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
                    ParsekLog.Log($"    Parachute '{canopyName}' found on prefab but not in cloneMap " +
                        $"— will use fake canopy");
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

            // Detect RCS parts and clone FX particle systems
            rcsInfos = TryBuildRcsFX(prefab, persistentId, partName, modelRoot,
                modelNode.transform, cloneMap);

            // Detect deployable parts (solar panels, antennas, radiators) and pre-resolve transform states
            ModuleDeployablePart deployable = prefab.FindModuleImplementing<ModuleDeployablePart>();
            if (deployable != null)
            {
                var sampledStates = SampleDeployableStates(prefab, deployable);
                if (sampledStates != null)
                {
                    var resolvedTransforms = new List<DeployableTransformState>();
                    for (int s = 0; s < sampledStates.Count; s++)
                    {
                        var (path, sPos, sRot, sScale, dPos, dRot, dScale) = sampledStates[s];
                        // Resolve path to ghost transform via cloneMap
                        Transform ghostT = FindTransformByPath(modelNode.transform, path);
                        if (ghostT != null)
                        {
                            resolvedTransforms.Add(new DeployableTransformState
                            {
                                t = ghostT,
                                stowedPos = sPos,
                                stowedRot = sRot,
                                stowedScale = sScale,
                                deployedPos = dPos,
                                deployedRot = dRot,
                                deployedScale = dScale
                            });
                        }
                    }

                    if (resolvedTransforms.Count > 0)
                    {
                        deployableInfo = new DeployableGhostInfo
                        {
                            partPersistentId = persistentId,
                            transforms = resolvedTransforms
                        };
                        ParsekLog.Log($"    Deployable detected: '{partName}' pid={persistentId}, " +
                            $"{resolvedTransforms.Count}/{sampledStates.Count} transforms resolved");
                    }
                    else
                    {
                        ParsekLog.Log($"    Deployable '{partName}' pid={persistentId}: " +
                            $"sampled {sampledStates.Count} transforms but none resolved to ghost");
                    }
                }
            }

            // Detect landing gear / legs (ModuleWheels.ModuleWheelDeployment) — reuses DeployableGhostInfo
            if (deployableInfo == null)
            {
                for (int m = 0; m < prefab.Modules.Count; m++)
                {
                    var wheel = prefab.Modules[m] as ModuleWheels.ModuleWheelDeployment;
                    if (wheel == null) continue;

                    string animStateName = wheel.animationStateName;
                    string animTrfName = wheel.animationTrfName;
                    float depPos = wheel.deployedPosition;
                    var sampledStates = SampleGearStates(prefab, animStateName, animTrfName, depPos);
                    if (sampledStates != null)
                    {
                        var resolvedTransforms = new List<DeployableTransformState>();
                        for (int s = 0; s < sampledStates.Count; s++)
                        {
                            var (path, sPos, sRot, sScale, dPos, dRot, dScale) = sampledStates[s];
                            Transform ghostT = FindTransformByPath(modelNode.transform, path);
                            if (ghostT != null)
                            {
                                resolvedTransforms.Add(new DeployableTransformState
                                {
                                    t = ghostT,
                                    stowedPos = sPos,
                                    stowedRot = sRot,
                                    stowedScale = sScale,
                                    deployedPos = dPos,
                                    deployedRot = dRot,
                                    deployedScale = dScale
                                });
                            }
                        }

                        if (resolvedTransforms.Count > 0)
                        {
                            deployableInfo = new DeployableGhostInfo
                            {
                                partPersistentId = persistentId,
                                transforms = resolvedTransforms
                            };
                            ParsekLog.Log($"    Gear detected: '{partName}' pid={persistentId}, " +
                                $"{resolvedTransforms.Count}/{sampledStates.Count} transforms resolved");
                        }
                    }
                    break; // one deployment module per part
                }
            }
            else if (prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null)
            {
                ParsekLog.Log($"    WARNING: '{partName}' pid={persistentId} has both " +
                    $"ModuleDeployablePart and ModuleWheels.ModuleWheelDeployment — gear visuals skipped");
            }

            // Detect cargo bays (ModuleCargoBay + linked ModuleAnimateGeneric) — reuses DeployableGhostInfo
            if (deployableInfo == null)
            {
                ModuleCargoBay cargoBay = prefab.FindModuleImplementing<ModuleCargoBay>();
                if (cargoBay != null)
                {
                    int deployIdx = cargoBay.DeployModuleIndex;
                    ModuleAnimateGeneric animModule = (deployIdx >= 0 && deployIdx < prefab.Modules.Count)
                        ? prefab.Modules[deployIdx] as ModuleAnimateGeneric
                        : null;

                    if (animModule != null && !string.IsNullOrEmpty(animModule.animationName))
                    {
                        var sampledStates = SampleCargoBayStates(
                            prefab, animModule.animationName, cargoBay.closedPosition);
                        if (sampledStates != null)
                        {
                            var resolvedTransforms = new List<DeployableTransformState>();
                            for (int s = 0; s < sampledStates.Count; s++)
                            {
                                var (path, sPos, sRot, sScale, dPos, dRot, dScale) = sampledStates[s];
                                Transform ghostT = FindTransformByPath(modelNode.transform, path);
                                if (ghostT != null)
                                {
                                    resolvedTransforms.Add(new DeployableTransformState
                                    {
                                        t = ghostT,
                                        stowedPos = sPos,
                                        stowedRot = sRot,
                                        stowedScale = sScale,
                                        deployedPos = dPos,
                                        deployedRot = dRot,
                                        deployedScale = dScale
                                    });
                                }
                            }

                            if (resolvedTransforms.Count > 0)
                            {
                                deployableInfo = new DeployableGhostInfo
                                {
                                    partPersistentId = persistentId,
                                    transforms = resolvedTransforms
                                };

                                // Snap ghost to closed (stowed) state at build time.
                                // Mk3 cargo bays have closedPosition=1 so prefab default (animTime=0) is OPEN.
                                // Without this snap, the ghost would show open doors until the first event.
                                for (int i = 0; i < resolvedTransforms.Count; i++)
                                {
                                    var ts = resolvedTransforms[i];
                                    if (ts.t == null) continue;
                                    ts.t.localPosition = ts.stowedPos;
                                    ts.t.localRotation = ts.stowedRot;
                                    ts.t.localScale = ts.stowedScale;
                                }

                                ParsekLog.Log($"    CargoBay detected: '{partName}' pid={persistentId}, " +
                                    $"{resolvedTransforms.Count}/{sampledStates.Count} transforms resolved" +
                                    $" (closedPosition={cargoBay.closedPosition})");
                            }
                        }
                    }
                }
            }

            // Detect light parts and clone Light components for ghost playback
            ModuleLight lightModule = prefab.FindModuleImplementing<ModuleLight>();
            if (lightModule != null)
            {
                var prefabLights = modelRoot.GetComponentsInChildren<Light>(true);
                if (prefabLights != null && prefabLights.Length > 0)
                {
                    var clonedLights = new List<Light>();
                    for (int li = 0; li < prefabLights.Length; li++)
                    {
                        Light srcLight = prefabLights[li];
                        if (srcLight == null) continue;

                        // Mirror the transform chain to find the correct ghost parent
                        Transform ghostParent = MirrorTransformChain(
                            srcLight.transform, modelRoot, modelNode.transform, cloneMap);

                        // Create a new Light component on the ghost transform
                        Light ghostLight = ghostParent.gameObject.AddComponent<Light>();
                        ghostLight.type = srcLight.type;
                        ghostLight.color = srcLight.color;
                        ghostLight.intensity = srcLight.intensity;
                        ghostLight.range = srcLight.range;
                        ghostLight.spotAngle = srcLight.spotAngle;
                        ghostLight.shadows = LightShadows.None;
                        ghostLight.renderMode = LightRenderMode.ForceVertex;
                        ghostLight.enabled = false;
                        clonedLights.Add(ghostLight);
                    }

                    if (clonedLights.Count > 0)
                    {
                        lightInfo = new LightGhostInfo
                        {
                            partPersistentId = persistentId,
                            lights = clonedLights
                        };
                        ParsekLog.Log($"    Light detected: '{partName}' pid={persistentId}, " +
                            $"{clonedLights.Count} Light component(s) cloned");
                    }
                }
            }

            // Detect procedural fairings and generate simplified cone mesh
            fairingInfo = BuildFairingVisual(partNode, prefab, modelNode.transform, persistentId, partName);
            if (fairingInfo != null)
                added = true;

            if (!added)
            {
                Object.Destroy(partRoot);
                return false;
            }

            return true;
        }
    }
}
