using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    internal class JettisonGhostInfo
    {
        public uint partPersistentId;
        public List<Transform> jettisonTransforms;
    }

    internal class ParachuteGhostInfo
    {
        public uint partPersistentId;
        public Transform canopyTransform;
        public Transform capTransform;
        public Vector3 deployedCanopyScale;
        public Vector3 deployedCanopyPos;
        public Quaternion deployedCanopyRot;
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

    internal struct HeatMaterialState
    {
        public Material material;
        public string colorProperty;
        public Color coldColor;
        public Color hotColor;
        public string emissiveProperty;
        public Color coldEmission;
        public Color hotEmission;
    }

    internal class HeatGhostInfo
    {
        public uint partPersistentId;
        public List<DeployableTransformState> transforms;
        public List<HeatMaterialState> materialStates;
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
        public float emissionScale = 1f;
        public float speedScale = 1f;
    }

    internal enum RoboticVisualMode
    {
        Rotational,
        Linear,
        RotorRpm
    }

    internal class RoboticGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public string moduleName;
        public Transform servoTransform;
        public Vector3 axisLocal = Vector3.up;
        public Vector3 stowedPos;
        public Quaternion stowedRot;
        public RoboticVisualMode visualMode;
        public float currentValue;
        public bool active;
        public double lastUpdateUT = double.NaN;
    }

    internal struct FxModelDefinition
    {
        public string transformName;
        public string modelName;
        public Vector3 localOffset;
        public Quaternion localRotation;
        public Vector3 localScale;
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
        private const string LightsShowcaseRecordingPrefix = "Part Showcase - Light";
        private const string RcsShowcaseRecordingPrefix = "Part Showcase - RCS";
        private const float LightsShowcaseVisualYOffset = 2f;
        private const float LightMinimumIntensity = 1f;
        private const float LightMinimumRange = 8f;
        private const float LightShowcaseIntensityScale = 2f;
        private const float LightShowcaseRangeScale = 2f;
        private const float LightShowcaseMinimumIntensity = 2f;
        private const float LightShowcaseMinimumRange = 25f;
        private const float RcsShowcaseEmissionScale = 1f;
        private const float RcsShowcaseSpeedScale = 1f;
        private static readonly Color HeatTintColor = new Color(1f, 0.45f, 0.2f, 1f);
        private static readonly Color HeatEmissionColor = new Color(1.5f, 0.6f, 0.15f, 1f);

        // Cache for PREFAB_PARTICLE fx_* prefabs found on PartLoader part prefabs.
        // Built once from PartLoader.LoadedPartsList (stable prefab templates).
        private static Dictionary<string, GameObject> fxPrefabCache;

        /// <summary>
        /// Find a KSP fx_* prefab by name. These exist as children of legacy part
        /// prefabs (e.g., fx_exhaustFlame_blue(Clone) on liquidEngine's prefab transform).
        /// We scan PartLoader.LoadedPartsList once and cache the results.
        /// Self-heals: if a cached reference becomes Unity-null, removes and re-scans.
        /// </summary>
        private static GameObject FindFxPrefab(string prefabName)
        {
            if (fxPrefabCache == null)
                RebuildFxPrefabCache();

            GameObject result;
            if (fxPrefabCache.TryGetValue(prefabName, out result))
            {
                // Self-heal: if cached object was destroyed, remove and try rebuild
                if (result == null)
                {
                    fxPrefabCache.Remove(prefabName);
                    RebuildFxPrefabCache();
                    fxPrefabCache.TryGetValue(prefabName, out result);
                }
                return result;
            }

            // Fallback: some fx_* prefabs (fx_exhaustFlame_yellow_tiny_Z,
            // fx_smokeTrail_veryLarge, fx_smokeTrail_aeroSpike) are Unity
            // built-in Resources in resources.assets, not part prefab children.
            result = Resources.Load<GameObject>(prefabName);
            if (result != null)
            {
                fxPrefabCache[prefabName] = result;
                ParsekLog.Log($"  FX prefab loaded from Resources: '{prefabName}'");
            }
            return result;
        }

        private static void RebuildFxPrefabCache()
        {
            fxPrefabCache = new Dictionary<string, GameObject>();
            if (PartLoader.LoadedPartsList == null) return;

            for (int p = 0; p < PartLoader.LoadedPartsList.Count; p++)
            {
                Part prefab = PartLoader.LoadedPartsList[p]?.partPrefab;
                if (prefab == null) continue;

                for (int c = 0; c < prefab.transform.childCount; c++)
                {
                    Transform child = prefab.transform.GetChild(c);
                    string name = child.name;
                    // Strip "(Clone)" suffix if present
                    if (name.EndsWith("(Clone)"))
                        name = name.Substring(0, name.Length - 7);
                    if (name.StartsWith("fx_") && !fxPrefabCache.ContainsKey(name)
                        && child.GetComponentInChildren<ParticleSystem>() != null)
                    {
                        fxPrefabCache[name] = child.gameObject;
                    }
                }
            }
            ParsekLog.Log($"  FX prefab cache built from PartLoader: {fxPrefabCache.Count} entries");
        }

        /// <summary>Clear the fx prefab cache (e.g., on scene change).</summary>
        internal static void ClearFxPrefabCache()
        {
            fxPrefabCache = null;
        }

        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos,
            out List<EngineGhostInfo> engineInfos,
            out List<DeployableGhostInfo> deployableInfos,
            out List<HeatGhostInfo> heatInfos,
            out List<LightGhostInfo> lightInfos,
            out List<FairingGhostInfo> fairingInfos,
            out List<RcsGhostInfo> rcsInfos,
            out List<RoboticGhostInfo> roboticInfos)
        {
            parachuteInfos = null;
            jettisonInfos = null;
            engineInfos = null;
            deployableInfos = null;
            heatInfos = null;
            lightInfos = null;
            fairingInfos = null;
            rcsInfos = null;
            roboticInfos = null;
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
            var collectedHeatInfos = new List<HeatGhostInfo>();
            var collectedLightInfos = new List<LightGhostInfo>();
            var collectedFairingInfos = new List<FairingGhostInfo>();
            var collectedRcsInfos = new List<RcsGhostInfo>();
            var collectedRoboticInfos = new List<RoboticGhostInfo>();

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

                AvailablePart ap = ResolveAvailablePart(partName);
                if (ap == null || ap.partPrefab == null)
                {
                    skippedPrefab++;
                    ParsekLog.Log($"  Ghost part SKIPPED (no prefab): '{partName}' pid={persistentId}");
                    continue;
                }
                string resolvedName = ap.partPrefab.partInfo?.name ?? ap.name;
                if (!string.IsNullOrEmpty(resolvedName) &&
                    !string.Equals(resolvedName, partName, System.StringComparison.OrdinalIgnoreCase))
                {
                    ParsekLog.Log($"  Ghost part lookup alias: '{partName}' -> '{resolvedName}'");
                }

                int meshCount = 0;
                ParachuteGhostInfo parachuteInfo;
                JettisonGhostInfo jettisonInfo;
                List<EngineGhostInfo> partEngineInfos;
                DeployableGhostInfo deployableInfo;
                HeatGhostInfo heatInfo;
                LightGhostInfo lightInfo;
                FairingGhostInfo fairingInfo;
                List<RcsGhostInfo> partRcsInfos;
                List<RoboticGhostInfo> partRoboticInfos;
                bool raiseLightVisualOnly =
                    !string.IsNullOrEmpty(rec.VesselName) &&
                    rec.VesselName.StartsWith(LightsShowcaseRecordingPrefix, System.StringComparison.Ordinal);
                bool raiseRcsVisualOnly =
                    !string.IsNullOrEmpty(rec.VesselName) &&
                    rec.VesselName.StartsWith(RcsShowcaseRecordingPrefix, System.StringComparison.Ordinal);
                bool partVisualAdded = AddPartVisuals(root.transform, partNode, ap.partPrefab,
                    persistentId, partName, out meshCount, out parachuteInfo, out jettisonInfo,
                    out partEngineInfos, out deployableInfo, out heatInfo, out lightInfo, out fairingInfo,
                    out partRcsInfos, out partRoboticInfos, raiseLightVisualOnly, raiseRcsVisualOnly);
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
                if (heatInfo != null)
                    collectedHeatInfos.Add(heatInfo);
                if (lightInfo != null)
                    collectedLightInfos.Add(lightInfo);
                if (fairingInfo != null)
                    collectedFairingInfos.Add(fairingInfo);
                if (partRcsInfos != null)
                    collectedRcsInfos.AddRange(partRcsInfos);
                if (partRoboticInfos != null)
                    collectedRoboticInfos.AddRange(partRoboticInfos);
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
            heatInfos = collectedHeatInfos.Count > 0 ? collectedHeatInfos : null;
            lightInfos = collectedLightInfos.Count > 0 ? collectedLightInfos : null;
            fairingInfos = collectedFairingInfos.Count > 0 ? collectedFairingInfos : null;
            rcsInfos = collectedRcsInfos.Count > 0 ? collectedRcsInfos : null;
            roboticInfos = collectedRoboticInfos.Count > 0 ? collectedRoboticInfos : null;
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
                out parachuteInfos, out jettisonInfos, out engineInfos, out deployableInfos, out _,
                out lightInfos, out fairingInfos, out _, out _);
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
                out parachuteInfos, out jettisonInfos, out engineInfos, out deployableInfos, out _,
                out lightInfos, out _, out _, out _);
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
                out parachuteInfos, out jettisonInfos, out engineInfos, out deployableInfos, out _, out _, out _, out _, out _);
        }

        // Backward-compat overload without deployable/light/fairing/rcs infos
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos,
            out List<EngineGhostInfo> engineInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName,
                out parachuteInfos, out jettisonInfos, out engineInfos, out _, out _, out _, out _, out _, out _);
        }

        // Overload without info outputs for callers that don't need them (preview ghost)
        internal static GameObject BuildTimelineGhostFromSnapshot(RecordingStore.Recording rec, string rootName)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        }

        // Overload with parachute + jettison info (backward compat)
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos,
            out List<JettisonGhostInfo> jettisonInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out parachuteInfos, out jettisonInfos, out _, out _, out _, out _, out _, out _, out _);
        }

        // Overload with only parachute info for backward compat
        internal static GameObject BuildTimelineGhostFromSnapshot(
            RecordingStore.Recording rec, string rootName,
            out List<ParachuteGhostInfo> parachuteInfos)
        {
            return BuildTimelineGhostFromSnapshot(rec, rootName, out parachuteInfos, out _, out _, out _, out _, out _, out _, out _, out _);
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

        internal static AvailablePart ResolveAvailablePart(string partName)
        {
            if (string.IsNullOrEmpty(partName))
                return null;

            string trimmed = partName.Trim();
            if (trimmed.Length == 0)
                return null;

            AvailablePart ap = TryGetAvailablePartByName(trimmed);
            if (ap != null)
                return ap;

            string dotted = trimmed.Replace('_', '.');
            if (!string.Equals(dotted, trimmed, System.StringComparison.Ordinal))
            {
                ap = TryGetAvailablePartByName(dotted);
                if (ap != null)
                    return ap;
            }

            string underscored = trimmed.Replace('.', '_');
            if (!string.Equals(underscored, trimmed, System.StringComparison.Ordinal))
            {
                ap = TryGetAvailablePartByName(underscored);
                if (ap != null)
                    return ap;
            }

            List<AvailablePart> loadedParts = PartLoader.LoadedPartsList;
            if (loadedParts == null || loadedParts.Count == 0)
                return null;

            for (int i = 0; i < loadedParts.Count; i++)
            {
                AvailablePart candidate = loadedParts[i];
                if (candidate == null || candidate.partPrefab == null)
                    continue;

                if (PartNameMatches(candidate, trimmed) ||
                    PartNameMatches(candidate, dotted) ||
                    PartNameMatches(candidate, underscored))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static AvailablePart TryGetAvailablePartByName(string lookupName)
        {
            if (string.IsNullOrEmpty(lookupName))
                return null;

            AvailablePart ap = PartLoader.getPartInfoByName(lookupName);
            return (ap != null && ap.partPrefab != null) ? ap : null;
        }

        private static bool PartNameMatches(AvailablePart candidate, string lookupName)
        {
            if (candidate == null || string.IsNullOrEmpty(lookupName))
                return false;

            if (string.Equals(candidate.name, lookupName, System.StringComparison.OrdinalIgnoreCase))
                return true;

            string infoName = candidate.partPrefab?.partInfo?.name;
            if (!string.IsNullOrEmpty(infoName) &&
                string.Equals(infoName, lookupName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string prefabName = candidate.partPrefab?.name;
            if (!string.IsNullOrEmpty(prefabName) &&
                string.Equals(prefabName, lookupName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
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

        // Cache: partName → (scale, pos, rot) — sample once per part type, reuse across ghosts
        private static readonly Dictionary<string, (Vector3 scale, Vector3 pos, Quaternion rot)> deployedCanopyCache =
            new Dictionary<string, (Vector3, Vector3, Quaternion)>();

        internal static void ClearDeployedCanopyCache()
        {
            deployedCanopyCache.Clear();
        }

        private static (Vector3 scale, Vector3 pos, Quaternion rot) SampleDeployedCanopy(Part prefab, ModuleParachute chute)
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
            Quaternion rot = Quaternion.identity;
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
                                // Root-relative position/rotation accounts for animated
                                // intermediate transforms (critical for EVA kerbals where
                                // the deploy animation moves parent bones, not canopy itself)
                                pos = tempClone.transform.InverseTransformPoint(canopy.position);
                                rot = Quaternion.Inverse(tempClone.transform.rotation) * canopy.rotation;
                                sampled = true;
                                ParsekLog.Log($"  Animation '{animName}' sampled canopy: scale={scale} " +
                                    $"rootPos={pos} rootRot={rot.eulerAngles}");
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
                rot = Quaternion.identity;
            }

            var result = (scale, pos, rot);
            deployedCanopyCache[key] = result;
            ParsekLog.Log($"  Deployed canopy for '{key}': scale={scale} pos={pos} rot={rot.eulerAngles}");
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

        // Cache: partName(+anim) → list of ladder animation transform states — sample once per part type
        private static readonly Dictionary<string, List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)>> ladderCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        internal static void ClearLadderCache()
        {
            ladderCache.Clear();
        }

        // Cache: partName → list of cargo bay animation transform states — sample once per part type
        private static readonly Dictionary<string, List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)>> cargoBayCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        internal static void ClearCargoBayCache()
        {
            cargoBayCache.Clear();
        }

        // Cache: partName(+anim) -> sampled ModuleAnimateHeat transform states
        private static readonly Dictionary<string, List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)>> animateHeatCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        internal static void ClearAnimateHeatCache()
        {
            animateHeatCache.Clear();
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

        private static bool TryGetRetractableLadderAnimation(
            Part prefab, out string animationName, out string animationRootName)
        {
            animationName = null;
            animationRootName = null;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode ladderModule = FindModuleNode(partConfig, "RetractableLadder");
            if (ladderModule == null) return false;

            animationName = ladderModule.GetValue("ladderRetractAnimationName");
            animationRootName = ladderModule.GetValue("ladderAnimationRootName");
            return !string.IsNullOrEmpty(animationName);
        }

        private static bool TryGetAnimationGroupDeployAnimation(
            Part prefab, out string animationName)
        {
            animationName = null;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode animationGroupModule = FindModuleNode(partConfig, "ModuleAnimationGroup");
            if (animationGroupModule == null) return false;

            animationName = animationGroupModule.GetValue("deployAnimationName");
            return !string.IsNullOrEmpty(animationName);
        }

        private static bool TryGetStandaloneAnimateGenericDeployAnimation(
            Part prefab, out string animationName)
        {
            animationName = null;
            if (prefab == null || prefab.Modules == null) return false;

            // Skip modules already handled by dedicated visual paths.
            if (prefab.FindModuleImplementing<ModuleDeployablePart>() != null) return false;
            if (prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null) return false;
            if (prefab.FindModuleImplementing<ModuleCargoBay>() != null) return false;

            bool hasAnimationGroup = false;
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                PartModule module = prefab.Modules[m];
                if (module == null) continue;
                if (string.Equals(module.moduleName, "RetractableLadder", System.StringComparison.Ordinal))
                    return false;
                if (string.Equals(module.moduleName, "ModuleAnimationGroup", System.StringComparison.Ordinal))
                    hasAnimationGroup = true;
            }
            if (hasAnimationGroup) return false;

            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                ModuleAnimateGeneric animateModule = prefab.Modules[m] as ModuleAnimateGeneric;
                if (animateModule == null) continue;
                if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                animationName = animateModule.animationName;
                return true;
            }

            return false;
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

        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleAnimateHeatStates(
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

            var sampledStates = SampleLadderStates(prefab, animationName, null);
            animateHeatCache[cacheKey] = sampledStates;
            return sampledStates;
        }

        /// <summary>
        /// Sample stowed and deployed transform states for stock RetractableLadder modules.
        /// Ladders expose "ladderRetractAnimationName" rather than ModuleDeployablePart, and
        /// clip direction is not guaranteed, so stowed endpoint is inferred from prefab defaults.
        /// </summary>
        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleLadderStates(
            Part prefab, string animationName, string animationRootName)
        {
            string partKey = prefab.partInfo?.name ?? prefab.name;
            string cacheKey = partKey + "|" + (animationName ?? string.Empty) + "|" + (animationRootName ?? string.Empty);
            if (ladderCache.TryGetValue(cacheKey, out var cached))
                return cached;

            if (string.IsNullOrEmpty(animationName))
            {
                ParsekLog.Log($"  Ladder '{partKey}': no ladderRetractAnimationName — skipping animation sampling");
                ladderCache[cacheKey] = null;
                return null;
            }

            Transform modelRoot = FindModelRoot(prefab);
            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);
            List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)> result = null;

            try
            {
                Animation anim = null;
                if (!string.IsNullOrEmpty(animationRootName))
                {
                    Transform animRoot = FindTransformRecursive(tempClone.transform, animationRootName);
                    if (animRoot != null)
                        anim = animRoot.GetComponent<Animation>();
                    else
                        ParsekLog.Log($"  Ladder '{partKey}': animation root '{animationRootName}' not found on clone");
                }

                if (anim == null)
                {
                    foreach (var candidate in tempClone.GetComponentsInChildren<Animation>(true))
                    {
                        if (candidate[animationName] != null)
                        {
                            anim = candidate;
                            break;
                        }
                    }
                }

                if (anim == null)
                {
                    ParsekLog.Log($"  Ladder '{partKey}': no Animation component on model clone");
                    ladderCache[cacheKey] = null;
                    return null;
                }

                AnimationState state = anim[animationName];
                if (state == null)
                {
                    ParsekLog.Log($"  Ladder '{partKey}': animation '{animationName}' not found on clone");
                    ladderCache[cacheKey] = null;
                    return null;
                }

                var allTransforms = tempClone.GetComponentsInChildren<Transform>(true);
                var defaultStates = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    defaultStates[path] = (allTransforms[i].localPosition, allTransforms[i].localRotation, allTransforms[i].localScale);
                }

                state.enabled = true;
                state.speed = 0f;
                state.weight = 1f;

                state.normalizedTime = 0f;
                anim.Play(animationName);
                anim.Sample();
                var time0States = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    time0States[path] = (allTransforms[i].localPosition, allTransforms[i].localRotation, allTransforms[i].localScale);
                }

                state.normalizedTime = 1f;
                anim.Sample();
                var time1States = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    time1States[path] = (allTransforms[i].localPosition, allTransforms[i].localRotation, allTransforms[i].localScale);
                }

                float score0 = 0f;
                float score1 = 0f;
                foreach (var kv in defaultStates)
                {
                    if (!time0States.TryGetValue(kv.Key, out var t0) ||
                        !time1States.TryGetValue(kv.Key, out var t1))
                        continue;

                    float rot0 = Quaternion.Angle(kv.Value.rot, t0.rot);
                    float rot1 = Quaternion.Angle(kv.Value.rot, t1.rot);
                    score0 += (t0.pos - kv.Value.pos).sqrMagnitude +
                        (t0.scale - kv.Value.scale).sqrMagnitude + (rot0 * rot0 * 0.0001f);
                    score1 += (t1.pos - kv.Value.pos).sqrMagnitude +
                        (t1.scale - kv.Value.scale).sqrMagnitude + (rot1 * rot1 * 0.0001f);
                }

                bool stowedIsTime0 = score0 <= score1;
                var stowedStates = stowedIsTime0 ? time0States : time1States;
                var deployedStates = stowedIsTime0 ? time1States : time0States;

                result = new List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>();
                for (int i = 0; i < allTransforms.Length; i++)
                {
                    string path = GetTransformPath(allTransforms[i], tempClone.transform);
                    if (!stowedStates.TryGetValue(path, out var stowed) ||
                        !deployedStates.TryGetValue(path, out var deployed))
                        continue;

                    float posDelta = (deployed.pos - stowed.pos).sqrMagnitude;
                    float rotDelta = Quaternion.Angle(deployed.rot, stowed.rot);
                    float scaleDelta = (deployed.scale - stowed.scale).sqrMagnitude;

                    if (posDelta > 0.0001f || rotDelta > 0.01f || scaleDelta > 0.0001f)
                    {
                        result.Add((path, stowed.pos, stowed.rot, stowed.scale,
                            deployed.pos, deployed.rot, deployed.scale));
                    }
                }

                if (result.Count == 0)
                {
                    ParsekLog.Log($"  Ladder '{partKey}': animation '{animationName}' produced no transform deltas");
                    result = null;
                }
                else
                {
                    ParsekLog.Log($"  Ladder '{partKey}': sampled {result.Count} animated transforms from '{animationName}' " +
                        $"(stowed=time{(stowedIsTime0 ? "0" : "1")})");
                }
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }

            ladderCache[cacheKey] = result;
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

        // KSP model transforms can have names containing '/' (e.g.
        // "Squad/Parts/Thermal/FoldingRadiators/foldingRadSmall(Clone)").
        // Use \x01 as path separator so Split() doesn't break those names.
        private const char PathSep = '\x01';

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
            return string.Join(PathSep.ToString(), parts);
        }

        /// <summary>
        /// Find a transform by PathSep-separated path relative to a root.
        /// Each segment is a direct child name (may contain '/' in KSP models).
        /// Uses manual child iteration instead of Transform.Find() because
        /// Unity's Find() also treats '/' as a path separator.
        /// </summary>
        private static Transform FindTransformByPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            string[] parts = path.Split(PathSep);
            Transform cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                Transform found = null;
                for (int c = 0; c < cur.childCount; c++)
                {
                    if (cur.GetChild(c).name == parts[i])
                    {
                        found = cur.GetChild(c);
                        break;
                    }
                }
                if (found == null) return null;
                cur = found;
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

                // Scan EFFECTS for particle FX entries (MODEL_MULTI_PARTICLE and PREFAB_PARTICLE)
                var fxTransformNames = new List<string>();
                var fxModelNames = new List<string>();
                var prefabFxEntries = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation)>();
                FloatCurve emissionCurve = null;
                FloatCurve speedCurve = null;

                if (effectsNode != null)
                {
                    ConfigNode[] effectGroups = effectsNode.GetNodes();

                    // Scan for MODEL_MULTI_PARTICLE / MODEL_MULTI_PARTICLE_PERSIST
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

                    // Scan for PREFAB_PARTICLE (used by Spark, Twitch, Pug, Juno, Wheesley, Goliath)
                    for (int g = 0; g < effectGroups.Length; g++)
                    {
                        ConfigNode[] ppNodes = effectGroups[g].GetNodes("PREFAB_PARTICLE");
                        for (int pp = 0; pp < ppNodes.Length; pp++)
                        {
                            string prefabName = ppNodes[pp].GetValue("prefabName");
                            string transformName = ppNodes[pp].GetValue("transformName");
                            if (string.IsNullOrEmpty(prefabName) || string.IsNullOrEmpty(transformName))
                                continue;

                            // Skip flameout/engage/disengage prefabs — only want running FX
                            string lower = prefabName.ToLowerInvariant();
                            if (lower.Contains("flameout") || lower.Contains("sparks") || lower.Contains("debris"))
                                continue;

                            // Parse localOffset (comma-separated x,y,z)
                            Vector3 localOffset = Vector3.zero;
                            string offsetStr = ppNodes[pp].GetValue("localOffset");
                            if (!string.IsNullOrEmpty(offsetStr))
                            {
                                string[] parts = offsetStr.Split(',');
                                if (parts.Length >= 3 &&
                                    float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ox) &&
                                    float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float oy) &&
                                    float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float oz))
                                    localOffset = new Vector3(ox, oy, oz);
                            }

                            // Parse localRotation (x,y,z,angle format — axis-angle, not quaternion)
                            Quaternion localRot = Quaternion.identity;
                            string rotStr = ppNodes[pp].GetValue("localRotation");
                            if (!string.IsNullOrEmpty(rotStr))
                            {
                                string[] parts = rotStr.Split(',');
                                if (parts.Length >= 4 &&
                                    float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float rx) &&
                                    float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ry) &&
                                    float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float rz) &&
                                    float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float angle))
                                {
                                    var axis = new Vector3(rx, ry, rz);
                                    if (axis.sqrMagnitude > 0.0001f)
                                        localRot = Quaternion.AngleAxis(angle, axis);
                                }
                            }

                            prefabFxEntries.Add((prefabName, transformName, localOffset, localRot));
                        }
                    }
                }

                if (fxTransformNames.Count == 0 && prefabFxEntries.Count == 0)
                {
                    // No EFFECTS node, or EFFECTS has no particle entries (e.g. Mainsail: AUDIO only).
                    // Fall through to legacy fx_* child search.
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

                info.emissionCurve = emissionCurve;
                info.speedCurve = speedCurve;

                // Process MODEL_MULTI_PARTICLE entries
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

                // Process PREFAB_PARTICLE entries (Spark, Twitch, Pug, Juno, Wheesley, Goliath)
                for (int f = 0; f < prefabFxEntries.Count; f++)
                {
                    var (prefabName, transformName, localOffset, localRot) = prefabFxEntries[f];

                    // Find the fx_* prefab in memory (children of legacy parts, not Unity Resources)
                    GameObject fxPrefab = FindFxPrefab(prefabName);
                    if (fxPrefab == null)
                    {
                        ParsekLog.Log($"    Engine FX prefab not found: '{prefabName}' for '{partName}'");
                        continue;
                    }

                    // Find the attachment transform(s) on the part
                    var fxTransforms = FindTransformsRecursive(prefab.transform, transformName);
                    if (fxTransforms.Count == 0)
                    {
                        ParsekLog.Log($"    Engine FX (prefab): '{partName}' midx={moduleIndex} " +
                            $"transform '{transformName}' not found on prefab");
                        continue;
                    }
                    for (int t = 0; t < fxTransforms.Count; t++)
                    {
                        Transform srcFxTransform = fxTransforms[t];

                        // Same transform chain mirroring as MODEL_MULTI_PARTICLE
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

                        // Clone the prefab and apply localOffset/localRotation from config
                        GameObject fxInstance = Object.Instantiate(fxPrefab);
                        fxInstance.transform.SetParent(ghostFxParent, false);
                        fxInstance.transform.localPosition = localOffset;
                        fxInstance.transform.localRotation = localRot;

                        // SmokeTrailControl expects real vessel/part state — destroy it on the ghost
                        var smokeTrail = fxInstance.GetComponent("SmokeTrailControl");
                        if (smokeTrail != null)
                            Object.Destroy(smokeTrail);

                        var ps = fxInstance.GetComponentInChildren<ParticleSystem>();
                        if (ps != null)
                        {
                            var emission = ps.emission;
                            emission.rateOverTimeMultiplier = 0;
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            info.particleSystems.Add(ps);
                            ParsekLog.Log($"    Engine FX (prefab): '{partName}' midx={moduleIndex} " +
                                $"transform='{transformName}' prefab='{prefabName}'");
                        }
                        else
                        {
                            ParsekLog.Log($"    Engine FX (prefab): '{partName}' midx={moduleIndex} " +
                                $"prefab '{prefabName}' has no ParticleSystem");
                            Object.Destroy(fxInstance);
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
            Dictionary<Transform, Transform> cloneMap,
            bool raiseRcsVisualOnly)
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
                    moduleIndex = moduleIndex,
                    emissionScale = raiseRcsVisualOnly ? RcsShowcaseEmissionScale : 1f,
                    speedScale = raiseRcsVisualOnly ? RcsShowcaseSpeedScale : 1f
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

                var fxDefinitions = new List<FxModelDefinition>();
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
                        Vector3 localOffset = Vector3.zero;
                        Vector3 localScale = Vector3.one;
                        Quaternion localRotation = Quaternion.identity;
                        Vector3 parsedVector;
                        if (TryParseVector3(mmpNodes[mp].GetValue("localOffset"), out parsedVector))
                            localOffset = parsedVector;
                        if (TryParseVector3(mmpNodes[mp].GetValue("localScale"), out parsedVector))
                            localScale = parsedVector;
                        if (TryParseVector3(mmpNodes[mp].GetValue("localRotation"), out parsedVector))
                            localRotation = Quaternion.Euler(parsedVector);

                        fxDefinitions.Add(new FxModelDefinition
                        {
                            transformName = transformName,
                            modelName = modelName ?? "",
                            localOffset = localOffset,
                            localRotation = localRotation,
                            localScale = localScale
                        });

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

                if (fxDefinitions.Count == 0)
                {
                    ParsekLog.Log($"    RCS '{partName}' midx={moduleIndex}: no FX transforms in '{runningEffect}' group");
                    continue;
                }

                info.emissionCurve = emissionCurve;
                info.speedCurve = speedCurve;

                for (int f = 0; f < fxDefinitions.Count; f++)
                {
                    string transformName = fxDefinitions[f].transformName;
                    string modelName = fxDefinitions[f].modelName;

                    var fxTransforms = FindTransformsRecursive(prefab.transform, transformName);
                    if (fxTransforms.Count == 0)
                    {
                        ParsekLog.Log($"    RCS '{partName}' midx={moduleIndex}: transform '{transformName}' not found on prefab");
                        continue;
                    }

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
                                fxInstance.SetActive(true);
                                fxInstance.transform.SetParent(ghostFxParent, false);
                                fxInstance.transform.localPosition = fxDefinitions[f].localOffset;
                                fxInstance.transform.localRotation = fxDefinitions[f].localRotation;
                                fxInstance.transform.localScale = fxDefinitions[f].localScale;

                                var ps = fxInstance.GetComponentInChildren<ParticleSystem>();
                                if (ps != null)
                                {
                                    var main = ps.main;
                                    // Keep showcase/startup visuals deterministic: cloned FX must not
                                    // auto-emit before the first explicit RCS event is applied.
                                    main.playOnAwake = false;
                                    main.prewarm = false;

                                    var emission = ps.emission;
                                    emission.enabled = true;
                                    emission.rateOverTimeMultiplier = 0;
                                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                    ps.Clear(true);

                                    if (raiseRcsVisualOnly)
                                    {
                                        var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
                                        if (psRenderer != null)
                                        {
                                            psRenderer.enabled = true;
                                        }
                                    }
                                    info.particleSystems.Add(ps);
                                    var diagRenderer = ps.GetComponent<ParticleSystemRenderer>();
                                    var diagMain = ps.main;
                                    ParsekLog.Log($"    RCS FX cloned: '{partName}' midx={moduleIndex} " +
                                        $"transform='{transformName}' model='{modelName}' " +
                                        $"offset={fxDefinitions[f].localOffset} " +
                                        $"rot={fxDefinitions[f].localRotation.eulerAngles} " +
                                        $"scale={fxDefinitions[f].localScale} " +
                                        $"active={ps.gameObject.activeInHierarchy} " +
                                        $"sim={diagMain.simulationSpace} playOnAwake={diagMain.playOnAwake} prewarm={diagMain.prewarm} " +
                                        $"renderer={(diagRenderer != null && diagRenderer.enabled)}");
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
                {
                    ParsekLog.Log($"    RCS module ready: '{partName}' midx={moduleIndex} " +
                        $"particles={info.particleSystems.Count} " +
                        $"emissionScale={info.emissionScale:F1} speedScale={info.speedScale:F2}");
                    result.Add(info);
                }
                else
                {
                    ParsekLog.Log($"    RCS '{partName}' midx={moduleIndex}: no particle systems cloned");
                }
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
            if (partNode == null || string.IsNullOrEmpty(moduleName))
                return null;
            var modules = partNode.GetNodes("MODULE");
            if (modules == null) return null;
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") == moduleName)
                    return modules[i];
            }
            return null;
        }

        private static string FirstNonEmptyConfigValue(ConfigNode node, params string[] keys)
        {
            if (node == null || keys == null || keys.Length == 0)
                return null;

            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                if (string.IsNullOrEmpty(key))
                    continue;

                string raw = node.GetValue(key);
                if (string.IsNullOrEmpty(raw))
                    continue;

                string trimmed = raw.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    return trimmed;
            }

            return null;
        }

        private static bool TryParseLooseBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(raw))
                return false;

            string t = raw.Trim();
            if (bool.TryParse(t, out value))
                return true;

            if (string.Equals(t, "1", System.StringComparison.Ordinal))
            {
                value = true;
                return true;
            }

            if (string.Equals(t, "0", System.StringComparison.Ordinal))
            {
                value = false;
                return true;
            }

            return false;
        }

        private static bool TryGetAeroSurfaceDeployInfo(
            Part prefab, out string transformName, out float deployAngleDegrees)
        {
            transformName = null;
            deployAngleDegrees = 0f;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode aeroModule = FindModuleNode(partConfig, "ModuleAeroSurface");
            if (aeroModule == null) return false;

            transformName = aeroModule.GetValue("transformName");
            if (string.IsNullOrEmpty(transformName)) return false;

            string angleRaw = aeroModule.GetValue("ctrlSurfaceRange");
            if (string.IsNullOrEmpty(angleRaw) ||
                !float.TryParse(angleRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out deployAngleDegrees))
            {
                deployAngleDegrees = 0f;
            }

            // Reasonable fallback for modules with missing/invalid range config.
            if (Mathf.Abs(deployAngleDegrees) < 0.01f)
                deployAngleDegrees = 35f;

            return true;
        }

        private static bool TryGetControlSurfaceDeployInfo(
            Part prefab, out string transformName, out float deployAngleDegrees)
        {
            transformName = null;
            deployAngleDegrees = 0f;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode controlModule = FindModuleNode(partConfig, "ModuleControlSurface");
            if (controlModule == null) return false;

            transformName = controlModule.GetValue("transformName");
            if (string.IsNullOrEmpty(transformName)) return false;

            string angleRaw = controlModule.GetValue("ctrlSurfaceRange");
            if (string.IsNullOrEmpty(angleRaw) ||
                !float.TryParse(angleRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out deployAngleDegrees))
            {
                deployAngleDegrees = 0f;
            }

            // Reasonable fallback for modules with missing/invalid range config.
            if (Mathf.Abs(deployAngleDegrees) < 0.01f)
                deployAngleDegrees = 20f;

            return true;
        }

        private static bool TryGetRobotArmScannerDeployAnimations(
            Part prefab, out List<string> animationNames)
        {
            animationNames = null;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode scannerModule = FindModuleNode(partConfig, "ModuleRobotArmScanner");
            if (scannerModule == null) return false;

            var candidates = new List<string>(3);

            // Priority:
            // 1) editorReachAnimationName often contains the full visible arm articulation preview
            // 2) unpackAnimationName provides a clear stowed/deployed pair
            // 3) animationName is the scan loop (fallback if others are unavailable)
            string[] keys = { "editorReachAnimationName", "unpackAnimationName", "animationName" };
            for (int i = 0; i < keys.Length; i++)
            {
                string name = scannerModule.GetValue(keys[i]);
                if (string.IsNullOrEmpty(name)) continue;

                bool exists = false;
                for (int c = 0; c < candidates.Count; c++)
                {
                    if (string.Equals(candidates[c], name, System.StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    candidates.Add(name);
            }

            if (candidates.Count == 0)
                return false;

            animationNames = candidates;
            return true;
        }

        private static bool TryGetSelectedVariantGameObjectStates(
            Part prefab,
            ConfigNode partNode,
            out string selectedVariantName,
            out Dictionary<string, bool> gameObjectStates)
        {
            selectedVariantName = null;
            gameObjectStates = null;

            if (prefab == null || prefab.partInfo == null || prefab.partInfo.partConfig == null)
                return false;

            ConfigNode variantModuleConfig = FindModuleNode(prefab.partInfo.partConfig, "ModulePartVariants");
            if (variantModuleConfig == null)
                return false;

            string requestedVariantName = null;

            // Prefer explicit variant saved on the part snapshot when present.
            ConfigNode snapshotVariantModule = FindModuleNode(partNode, "ModulePartVariants");
            requestedVariantName = FirstNonEmptyConfigValue(
                snapshotVariantModule,
                "currentVariant",
                "selectedVariant",
                "variantName",
                "moduleVariantName");

            // Fall back to runtime module fields if exposed.
            if (string.IsNullOrEmpty(requestedVariantName))
            {
                var variantModule = prefab.FindModuleImplementing<ModulePartVariants>();
                if (variantModule != null)
                {
                    TryGetModuleStringField(variantModule, "currentVariant", out requestedVariantName);
                    if (string.IsNullOrEmpty(requestedVariantName))
                        TryGetModuleStringField(variantModule, "selectedVariant", out requestedVariantName);
                    if (string.IsNullOrEmpty(requestedVariantName))
                        TryGetModuleStringField(variantModule, "variantName", out requestedVariantName);
                }
            }

            string baseVariantName = FirstNonEmptyConfigValue(variantModuleConfig, "baseVariant");
            ConfigNode[] variantNodes = variantModuleConfig.GetNodes("VARIANT");
            if (variantNodes == null || variantNodes.Length == 0)
                return false;

            ConfigNode selectedVariantNode = null;
            string selectedLower = !string.IsNullOrEmpty(requestedVariantName)
                ? requestedVariantName.ToLowerInvariant()
                : null;
            string baseLower = !string.IsNullOrEmpty(baseVariantName)
                ? baseVariantName.ToLowerInvariant()
                : null;

            for (int i = 0; i < variantNodes.Length; i++)
            {
                string name = FirstNonEmptyConfigValue(variantNodes[i], "name");
                if (string.IsNullOrEmpty(name))
                    continue;

                string lower = name.ToLowerInvariant();
                if (!string.IsNullOrEmpty(selectedLower) && lower == selectedLower)
                {
                    selectedVariantNode = variantNodes[i];
                    break;
                }
            }

            if (selectedVariantNode == null && !string.IsNullOrEmpty(baseLower))
            {
                for (int i = 0; i < variantNodes.Length; i++)
                {
                    string name = FirstNonEmptyConfigValue(variantNodes[i], "name");
                    if (string.IsNullOrEmpty(name))
                        continue;

                    if (name.ToLowerInvariant() == baseLower)
                    {
                        selectedVariantNode = variantNodes[i];
                        break;
                    }
                }
            }

            if (selectedVariantNode == null)
                selectedVariantNode = variantNodes[0];

            selectedVariantName = FirstNonEmptyConfigValue(selectedVariantNode, "name") ?? "<unnamed>";
            ConfigNode gameObjectsNode = selectedVariantNode.GetNode("GAMEOBJECTS");
            if (gameObjectsNode == null || gameObjectsNode.values == null || gameObjectsNode.values.Count == 0)
                return false;

            var states = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < gameObjectsNode.values.Count; i++)
            {
                var valueNode = gameObjectsNode.values[i];
                if (valueNode == null || string.IsNullOrEmpty(valueNode.name))
                    continue;

                string key = valueNode.name.Trim();
                if (key.Length == 0)
                    continue;

                if (!TryParseLooseBool(valueNode.value, out bool enabled))
                    continue;

                states[key] = enabled;
            }

            if (states.Count == 0)
                return false;

            gameObjectStates = states;
            return true;
        }

        private static bool IsRendererEnabledByVariantRule(
            Transform rendererTransform,
            Transform modelRoot,
            Dictionary<string, bool> gameObjectStates,
            out string matchedRuleName,
            out bool matchedRuleEnabled)
        {
            matchedRuleName = null;
            matchedRuleEnabled = true;

            if (rendererTransform == null || gameObjectStates == null || gameObjectStates.Count == 0)
                return true;

            Transform current = rendererTransform;
            while (current != null)
            {
                if (gameObjectStates.TryGetValue(current.name, out bool enabled))
                {
                    matchedRuleName = current.name;
                    matchedRuleEnabled = enabled;
                    return enabled;
                }

                if (current == modelRoot)
                    break;

                current = current.parent;
            }

            return true;
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

        private static bool HasRoboticModules(Part prefab)
        {
            if (prefab == null || prefab.Modules == null)
                return false;

            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule module = prefab.Modules[i];
                if (module == null)
                    continue;

                if (FlightRecorder.IsRoboticModuleName(module.moduleName))
                    return true;
            }

            return false;
        }

        private static object TryGetModuleFieldValue(PartModule module, string fieldName)
        {
            if (module == null || module.Fields == null || string.IsNullOrEmpty(fieldName))
                return null;

            try
            {
                BaseField field = module.Fields[fieldName];
                return field != null ? field.GetValue(module) : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetModuleStringField(
            PartModule module, string fieldName, out string value)
        {
            value = null;
            object raw = TryGetModuleFieldValue(module, fieldName);
            if (raw == null)
                return false;

            value = raw.ToString();
            if (string.IsNullOrEmpty(value))
                return false;

            value = value.Trim();
            return value.Length > 0;
        }

        private static bool TryGetModuleFloatField(
            PartModule module, string fieldName, out float value)
        {
            value = 0f;
            object raw = TryGetModuleFieldValue(module, fieldName);
            if (raw == null)
                return false;

            if (raw is float f)
            {
                value = f;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (raw is double d)
            {
                value = (float)d;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (raw is int i)
            {
                value = i;
                return true;
            }

            string text = raw.ToString();
            if (string.IsNullOrEmpty(text))
                return false;
            text = text.Trim();

            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return !float.IsNaN(value) && !float.IsInfinity(value);

            string[] split = text.Split(',');
            if (split.Length > 0 &&
                float.TryParse(split[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            return false;
        }

        private static bool TryGetModuleIntField(
            PartModule module, string fieldName, out int value)
        {
            value = 0;
            object raw = TryGetModuleFieldValue(module, fieldName);
            if (raw == null)
                return false;

            if (raw is int i)
            {
                value = i;
                return true;
            }

            if (raw is uint ui)
            {
                value = (int)ui;
                return true;
            }

            if (raw is float f && !float.IsNaN(f) && !float.IsInfinity(f))
            {
                value = (int)f;
                return true;
            }

            if (raw is double d && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                value = (int)d;
                return true;
            }

            string text = raw.ToString();
            if (string.IsNullOrEmpty(text))
                return false;

            text = text.Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseRoboticAxis(string axisName, out Vector3 axisLocal)
        {
            axisLocal = Vector3.up;
            if (string.IsNullOrEmpty(axisName))
                return false;

            string normalized = axisName.Trim().ToUpperInvariant();
            if (normalized.Length == 0)
                return false;

            bool negative = normalized.StartsWith("-");
            if (negative)
                normalized = normalized.Substring(1);

            switch (normalized)
            {
                case "X":
                    axisLocal = negative ? -Vector3.right : Vector3.right;
                    return true;
                case "Y":
                    axisLocal = negative ? -Vector3.up : Vector3.up;
                    return true;
                case "Z":
                    axisLocal = negative ? -Vector3.forward : Vector3.forward;
                    return true;
                default:
                    return false;
            }
        }

        private static RoboticVisualMode GetRoboticVisualMode(string moduleName)
        {
            if (string.Equals(moduleName, "ModuleRoboticServoPiston", System.StringComparison.Ordinal))
                return RoboticVisualMode.Linear;

            if (string.Equals(moduleName, "ModuleWheelSuspension", System.StringComparison.Ordinal))
                return RoboticVisualMode.Linear;

            if (string.Equals(moduleName, "ModuleRoboticServoRotor", System.StringComparison.Ordinal))
                return RoboticVisualMode.RotorRpm;

            if (string.Equals(moduleName, "ModuleWheelMotor", System.StringComparison.Ordinal) ||
                string.Equals(moduleName, "ModuleWheelMotorSteering", System.StringComparison.Ordinal))
            {
                return RoboticVisualMode.RotorRpm;
            }

            return RoboticVisualMode.Rotational;
        }

        private static bool TryGetWheelBaseModule(
            Part prefab, PartModule module, out PartModule baseModule)
        {
            baseModule = null;
            if (prefab == null || module == null || prefab.Modules == null)
                return false;

            if (TryGetModuleIntField(module, "baseModuleIndex", out int baseModuleIndex) &&
                baseModuleIndex >= 0 &&
                baseModuleIndex < prefab.Modules.Count)
            {
                baseModule = prefab.Modules[baseModuleIndex];
                if (baseModule != null)
                    return true;
            }

            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule candidate = prefab.Modules[i];
                if (candidate == null)
                    continue;
                if (string.Equals(candidate.moduleName, "ModuleWheelBase", System.StringComparison.Ordinal))
                {
                    baseModule = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveRoboticTransform(
            Part prefab,
            PartModule module,
            string moduleName,
            out string transformName,
            out Vector3 axis,
            out string transformSource)
        {
            transformName = null;
            axis = Vector3.up;
            transformSource = null;

            if (string.Equals(moduleName, "ModuleWheelSuspension", System.StringComparison.Ordinal))
            {
                if (TryGetModuleStringField(module, "suspensionTransformName", out transformName))
                {
                    axis = Vector3.up;
                    transformSource = "suspensionTransformName";
                    return true;
                }
                return false;
            }

            if (string.Equals(moduleName, "ModuleWheelSteering", System.StringComparison.Ordinal))
            {
                if (TryGetModuleStringField(module, "caliperTransformName", out transformName))
                {
                    axis = Vector3.up;
                    transformSource = "caliperTransformName";
                    return true;
                }
                return false;
            }

            if (string.Equals(moduleName, "ModuleWheelMotor", System.StringComparison.Ordinal) ||
                string.Equals(moduleName, "ModuleWheelMotorSteering", System.StringComparison.Ordinal))
            {
                if (TryGetModuleStringField(module, "wheelTransformName", out transformName))
                {
                    axis = Vector3.right;
                    transformSource = "wheelTransformName";
                    return true;
                }

                if (TryGetWheelBaseModule(prefab, module, out PartModule wheelBase) &&
                    TryGetModuleStringField(wheelBase, "wheelTransformName", out transformName))
                {
                    axis = Vector3.right;
                    transformSource = "ModuleWheelBase.wheelTransformName";
                    return true;
                }

                return false;
            }

            if (!TryGetModuleStringField(module, "servoTransformName", out transformName))
                return false;

            transformSource = "servoTransformName";
            if (TryGetModuleStringField(module, "mainAxis", out string axisName))
                TryParseRoboticAxis(axisName, out axis);
            return true;
        }

        private static bool TryGetRoboticCurrentValue(
            PartModule module, string moduleName, out float value)
        {
            value = 0f;

            string[] fieldNames;
            if (string.Equals(moduleName, "ModuleRoboticServoPiston", System.StringComparison.Ordinal))
            {
                fieldNames = new[] { "currentPosition", "position", "targetPosition" };
            }
            else if (string.Equals(moduleName, "ModuleWheelSuspension", System.StringComparison.Ordinal))
            {
                fieldNames = new[]
                {
                    "currentSuspensionOffset",
                    "suspensionOffset",
                    "compression",
                    "suspensionCompression",
                    "suspensionTravel"
                };
            }
            else if (string.Equals(moduleName, "ModuleWheelSteering", System.StringComparison.Ordinal))
            {
                fieldNames = new[] { "steeringAngle", "currentSteering", "steerAngle", "steeringInput" };
            }
            else if (string.Equals(moduleName, "ModuleRoboticServoRotor", System.StringComparison.Ordinal))
            {
                fieldNames = new[] { "currentRPM", "rpm", "targetRPM", "rpmLimit" };
            }
            else if (string.Equals(moduleName, "ModuleWheelMotor", System.StringComparison.Ordinal) ||
                string.Equals(moduleName, "ModuleWheelMotorSteering", System.StringComparison.Ordinal))
            {
                fieldNames = new[]
                {
                    "currentRPM",
                    "rpm",
                    "wheelRPM",
                    "motorRPM",
                    "targetRPM",
                    "driveOutput",
                    "motorOutput",
                    "wheelSpeed"
                };
            }
            else
            {
                fieldNames = new[] { "currentAngle", "angle", "targetAngle" };
            }

            for (int i = 0; i < fieldNames.Length; i++)
            {
                if (TryGetModuleFloatField(module, fieldNames[i], out value))
                    return true;
            }

            if (string.Equals(moduleName, "ModuleWheelMotorSteering", System.StringComparison.Ordinal) &&
                TryGetModuleFloatField(module, "steeringAngle", out value))
            {
                return true;
            }

            return false;
        }

        private static List<RoboticGhostInfo> TryBuildRoboticInfos(
            Part prefab,
            uint persistentId,
            string partName,
            Transform modelRoot,
            Transform modelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            if (prefab == null || prefab.Modules == null)
                return null;

            var infos = new List<RoboticGhostInfo>();
            int roboticModuleIndex = 0;
            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule module = prefab.Modules[i];
                if (module == null)
                    continue;

                string moduleName = module.moduleName;
                if (!FlightRecorder.IsRoboticModuleName(moduleName))
                    continue;

                int moduleIndex = roboticModuleIndex;
                roboticModuleIndex++;

                if (!TryResolveRoboticTransform(
                    prefab, module, moduleName, out string servoTransformName, out Vector3 axis, out string transformSource))
                {
                    ParsekLog.Log($"    Robotics '{partName}' midx={moduleIndex}: missing transform binding on {moduleName}");
                    continue;
                }

                Transform sourceServo = prefab.FindModelTransform(servoTransformName);
                if (sourceServo == null)
                {
                    ParsekLog.Log($"    Robotics '{partName}' midx={moduleIndex}: servo transform '{servoTransformName}' not found");
                    continue;
                }

                if (!cloneMap.TryGetValue(sourceServo, out Transform ghostServo) || ghostServo == null)
                {
                    if (IsDescendantOf(sourceServo, modelRoot))
                    {
                        ghostServo = MirrorTransformChain(
                            sourceServo, modelRoot, modelNode, cloneMap);
                    }
                    else
                    {
                        ParsekLog.Log($"    Robotics '{partName}' midx={moduleIndex}: servo '{servoTransformName}' outside model root");
                        continue;
                    }
                }

                float currentValue = 0f;
                TryGetRoboticCurrentValue(module, moduleName, out currentValue);

                var info = new RoboticGhostInfo
                {
                    partPersistentId = persistentId,
                    moduleIndex = moduleIndex,
                    moduleName = moduleName,
                    servoTransform = ghostServo,
                    axisLocal = axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.up,
                    stowedPos = ghostServo.localPosition,
                    stowedRot = ghostServo.localRotation,
                    visualMode = GetRoboticVisualMode(moduleName),
                    currentValue = currentValue,
                    active = false
                };

                infos.Add(info);
                ParsekLog.Log($"    Robotics detected: '{partName}' pid={persistentId} midx={moduleIndex} " +
                    $"module={moduleName} servo={servoTransformName} source={transformSource} axis={info.axisLocal} seed={currentValue:F3}");
            }

            return infos.Count > 0 ? infos : null;
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

        private static List<HeatMaterialState> BuildHeatMaterialStates(
            Transform modelNode, string partName, uint persistentId)
        {
            if (modelNode == null)
                return null;

            var renderers = modelNode.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return null;

            var materialStates = new List<HeatMaterialState>();
            int clonedMaterials = 0;
            int trackedMaterials = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;

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
                    hasAnyMaterial = true;
                    clonedMaterials++;

                    string colorProperty = materialClone.HasProperty("_Color")
                        ? "_Color"
                        : null;
                    string emissiveProperty = TryGetHeatEmissiveProperty(materialClone);

                    if (colorProperty == null && emissiveProperty == null)
                        continue;

                    Color coldColor = colorProperty != null
                        ? materialClone.GetColor(colorProperty)
                        : Color.white;
                    Color hotColor = colorProperty != null
                        ? Color.Lerp(coldColor, HeatTintColor, 0.45f)
                        : coldColor;

                    Color coldEmission = emissiveProperty != null
                        ? materialClone.GetColor(emissiveProperty)
                        : Color.black;
                    Color hotEmission = emissiveProperty != null
                        ? coldEmission + HeatEmissionColor
                        : coldEmission;

                    materialStates.Add(new HeatMaterialState
                    {
                        material = materialClone,
                        colorProperty = colorProperty,
                        coldColor = coldColor,
                        hotColor = hotColor,
                        emissiveProperty = emissiveProperty,
                        coldEmission = coldEmission,
                        hotEmission = hotEmission
                    });
                    trackedMaterials++;
                }

                if (hasAnyMaterial)
                    renderer.materials = cloned;
            }

            if (trackedMaterials > 0)
            {
                ParsekLog.Log($"    AnimateHeat materials detected: '{partName}' pid={persistentId}, " +
                    $"tracked={trackedMaterials} cloned={clonedMaterials}");
                return materialStates;
            }

            if (clonedMaterials > 0)
            {
                ParsekLog.Log($"    AnimateHeat '{partName}' pid={persistentId}: " +
                    $"cloned {clonedMaterials} material(s) but no _Color/emissive properties found");
            }

            return null;
        }

        private static bool AddPartVisuals(Transform root, ConfigNode partNode, Part prefab,
            uint persistentId, string partName, out int meshCount,
            out ParachuteGhostInfo parachuteInfo, out JettisonGhostInfo jettisonInfo,
            out List<EngineGhostInfo> engineInfos, out DeployableGhostInfo deployableInfo,
            out HeatGhostInfo heatInfo,
            out LightGhostInfo lightInfo, out FairingGhostInfo fairingInfo,
            out List<RcsGhostInfo> rcsInfos, out List<RoboticGhostInfo> roboticInfos,
            bool raiseLightVisualOnly, bool raiseRcsVisualOnly)
        {
            meshCount = 0;
            parachuteInfo = null;
            jettisonInfo = null;
            engineInfos = null;
            deployableInfo = null;
            heatInfo = null;
            lightInfo = null;
            fairingInfo = null;
            rcsInfos = null;
            roboticInfos = null;
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

            // Parts with ModulePartVariants have multiple visual variants where only
            // one set of GameObjects should be visible (e.g. Poodle v2: DoubleBell vs
            // SingleBell). Active-state alone is unreliable on some prefabs, so we use
            // selected/default VARIANT GAMEOBJECT rules from part config when available,
            // with active-state filtering as an additional safeguard.
            bool hasPartVariants = prefab.FindModuleImplementing<ModulePartVariants>() != null;

            var meshRenderers = modelRoot.GetComponentsInChildren<MeshRenderer>(true);
            var skinnedRenderers = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if ((meshRenderers == null || meshRenderers.Length == 0) &&
                (skinnedRenderers == null || skinnedRenderers.Length == 0))
                return false;

            int totalMR = meshRenderers != null ? meshRenderers.Length : 0;
            int totalSMR = skinnedRenderers != null ? skinnedRenderers.Length : 0;
            bool hasVariantGameObjectRules = false;
            string selectedVariantName = null;
            Dictionary<string, bool> selectedVariantGameObjects = null;
            if (hasPartVariants)
            {
                hasVariantGameObjectRules = TryGetSelectedVariantGameObjectStates(
                    prefab,
                    partNode,
                    out selectedVariantName,
                    out selectedVariantGameObjects);
            }
            int activeMR = 0;
            int activeSMR = 0;
            if (meshRenderers != null)
            {
                for (int i = 0; i < meshRenderers.Length; i++)
                {
                    var mr = meshRenderers[i];
                    if (mr != null && mr.gameObject.activeInHierarchy)
                        activeMR++;
                }
            }
            if (skinnedRenderers != null)
            {
                for (int i = 0; i < skinnedRenderers.Length; i++)
                {
                    var smr = skinnedRenderers[i];
                    if (smr != null && smr.gameObject.activeInHierarchy && smr.sharedMesh != null)
                        activeSMR++;
                }
            }
            bool filterInactiveVariantRenderers = hasPartVariants && (activeMR > 0 || activeSMR > 0);
            ParsekLog.Log($"  Part '{partName}' pid={persistentId}: modelRoot='{modelRoot.name}' " +
                $"modelScale={modelRoot.localScale}, " +
                $"{totalMR} MeshRenderers, {totalSMR} SkinnedMeshRenderers" +
                (hasPartVariants ? " (has ModulePartVariants)" : ""));
            if (hasPartVariants && hasVariantGameObjectRules)
            {
                ParsekLog.Log($"  Variant selection: '{selectedVariantName}' with " +
                    $"{selectedVariantGameObjects.Count} GAMEOBJECT rules");
            }
            if (hasPartVariants && !filterInactiveVariantRenderers)
            {
                ParsekLog.Log($"  Variant active-state fallback: no active variant renderers found " +
                    $"(active MR={activeMR}, active SMR={activeSMR})");
            }

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
            ParsekLog.Log($"  [DIAG] part '{partName}' modelRoot '{modelRoot.name}' localRot={modelRoot.localRotation} localPos={modelRoot.localPosition} localScale={modelRoot.localScale}");
            cloneMap[modelRoot] = modelNode.transform;

            // For light showcase recordings, lift only light-part visuals so probes stay
            // fixed while the lamp geometry sits clearly above the probe body.
            if (raiseLightVisualOnly && prefab.FindModuleImplementing<ModuleLight>() != null)
                modelNode.transform.localPosition += new Vector3(0f, LightsShowcaseVisualYOffset, 0f);

            int variantSkipped = 0;
            int variantRuleSkipped = 0;
            for (int r = 0; r < meshRenderers.Length; r++)
            {
                var mr = meshRenderers[r];
                if (mr == null) continue;
                if (filterInactiveVariantRenderers && !mr.gameObject.activeInHierarchy)
                {
                    variantSkipped++;
                    continue;
                }
                if (hasVariantGameObjectRules &&
                    !IsRendererEnabledByVariantRule(
                        mr.transform, modelRoot, selectedVariantGameObjects,
                        out string _, out bool _))
                {
                    variantRuleSkipped++;
                    continue;
                }
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
            if (variantSkipped > 0)
                ParsekLog.Log($"  Skipped {variantSkipped} MeshRenderers on inactive variant objects");
            if (variantRuleSkipped > 0)
                ParsekLog.Log($"  Skipped {variantRuleSkipped} MeshRenderers by selected variant GAMEOBJECT rules");

            for (int r = 0; r < skinnedRenderers.Length; r++)
            {
                var smr = skinnedRenderers[r];
                if (smr == null || smr.sharedMesh == null) continue;
                if (filterInactiveVariantRenderers && !smr.gameObject.activeInHierarchy) continue;
                if (hasVariantGameObjectRules &&
                    !IsRendererEnabledByVariantRule(
                        smr.transform, modelRoot, selectedVariantGameObjects,
                        out string _, out bool _))
                    continue;

                Transform leaf = MirrorTransformChain(smr.transform, modelRoot, modelNode.transform, cloneMap);

                // Preserve skinned meshes as skinned renderers so bone-driven animations
                // (e.g. drills) still articulate on ghosts.
                Transform[] ghostBones = null;
                int resolvedBones = 0;
                bool usedPartRootFallbackForBones = false;
                if (smr.bones != null && smr.bones.Length > 0)
                {
                    ghostBones = new Transform[smr.bones.Length];
                    for (int b = 0; b < smr.bones.Length; b++)
                    {
                        Transform srcBone = smr.bones[b];
                        if (srcBone == null) continue;

                        Transform ghostBone;
                        if (!cloneMap.TryGetValue(srcBone, out ghostBone))
                        {
                            if (IsDescendantOf(srcBone, modelRoot))
                            {
                                ghostBone = MirrorTransformChain(srcBone, modelRoot, modelNode.transform, cloneMap);
                            }
                            else if (IsDescendantOf(srcBone, prefab.transform))
                            {
                                // EVA rigs can keep bones outside modelRoot (e.g. model01).
                                // Fall back to cloning from the full part hierarchy.
                                ghostBone = MirrorTransformChain(srcBone, prefab.transform, partRoot.transform, cloneMap);
                                usedPartRootFallbackForBones = true;
                            }
                        }

                        ghostBones[b] = ghostBone;
                        if (ghostBone != null)
                            resolvedBones++;
                    }
                }

                Transform ghostRootBone = null;
                Transform srcRootBone = smr.rootBone;
                if (srcRootBone != null)
                {
                    if (!cloneMap.TryGetValue(srcRootBone, out ghostRootBone))
                    {
                        if (IsDescendantOf(srcRootBone, modelRoot))
                        {
                            ghostRootBone = MirrorTransformChain(srcRootBone, modelRoot, modelNode.transform, cloneMap);
                        }
                        else if (IsDescendantOf(srcRootBone, prefab.transform))
                        {
                            ghostRootBone = MirrorTransformChain(srcRootBone, prefab.transform, partRoot.transform, cloneMap);
                            usedPartRootFallbackForBones = true;
                        }
                    }
                }

                var ghostSmr = leaf.gameObject.AddComponent<SkinnedMeshRenderer>();
                ghostSmr.sharedMesh = smr.sharedMesh;
                ghostSmr.sharedMaterials = smr.sharedMaterials;
                ghostSmr.bones = ghostBones ?? new Transform[0];
                ghostSmr.rootBone = ghostRootBone != null ? ghostRootBone : leaf;
                ghostSmr.quality = smr.quality;
                ghostSmr.updateWhenOffscreen = true;
                ghostSmr.localBounds = smr.localBounds;
                ghostSmr.shadowCastingMode = smr.shadowCastingMode;
                ghostSmr.receiveShadows = smr.receiveShadows;
                meshCount++;
                ParsekLog.Log($"    SMR[{r}] '{smr.gameObject.name}' mesh={smr.sharedMesh.name} " +
                    $"localPos={leaf.localPosition} localScale={leaf.localScale} " +
                    $"bones={resolvedBones}/{(smr.bones != null ? smr.bones.Length : 0)}");
                if (usedPartRootFallbackForBones)
                {
                    ParsekLog.Log($"      SMR[{r}] '{smr.gameObject.name}': used part-root fallback for external bone transforms");
                }
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
                // clone only the canopy and cap transforms so they enter cloneMap.
                // We intentionally skip all other meshes in the subtree (backpack, storage,
                // parachute housing, etc.) — only the canopy/cap are needed for playback.
                Transform canopySubtreeRoot = null;
                if (srcCanopy != null && !cloneMap.ContainsKey(srcCanopy))
                {
                    Transform canopyVisualRoot = FindImmediateChildOf(srcCanopy, prefab.transform);
                    if (canopyVisualRoot != null && canopyVisualRoot != modelRoot)
                    {
                        ParsekLog.Log($"    Canopy '{canopyName}' is outside modelRoot '{modelRoot.name}'" +
                            $" — cloning canopy/cap only from '{canopyVisualRoot.name}'");

                        GameObject subNode = new GameObject(canopyVisualRoot.name);
                        subNode.transform.SetParent(partRoot.transform, false);
                        subNode.transform.localPosition = canopyVisualRoot.localPosition;
                        subNode.transform.localRotation = canopyVisualRoot.localRotation;
                        subNode.transform.localScale = canopyVisualRoot.localScale;
                        cloneMap[canopyVisualRoot] = subNode.transform;
                        canopySubtreeRoot = subNode.transform;

                        // Clone only the canopy and cap meshes, not the entire subtree
                        Transform[] targets = srcCap != null
                            ? new[] { srcCanopy, srcCap }
                            : new[] { srcCanopy };
                        foreach (var target in targets)
                        {
                            var mr = target.GetComponent<MeshRenderer>();
                            var mf = target.GetComponent<MeshFilter>();
                            if (mr != null && mf != null && mf.sharedMesh != null)
                            {
                                Transform leaf = MirrorTransformChain(target, canopyVisualRoot,
                                    subNode.transform, cloneMap);
                                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                                meshCount++;
                                added = true;
                                ParsekLog.Log($"      CANOPY-CLONE '{target.gameObject.name}' " +
                                    $"mesh={mf.sharedMesh.name}");
                            }
                            else
                            {
                                // No mesh directly on the transform — still mirror the chain
                                // so it enters cloneMap for lookup
                                MirrorTransformChain(target, canopyVisualRoot,
                                    subNode.transform, cloneMap);
                                ParsekLog.Log($"      CANOPY-CLONE '{target.gameObject.name}' (no mesh, chain only)");
                            }
                        }
                    }
                }

                // Look up ghost clones via cloneMap (deterministic, no name collisions).
                // If canopy was outside modelRoot, only canopy/cap were cloned above.
                Transform ghostCanopy = null, ghostCap = null;
                if (srcCanopy != null) cloneMap.TryGetValue(srcCanopy, out ghostCanopy);
                if (srcCap != null) cloneMap.TryGetValue(srcCap, out ghostCap);

                if (ghostCanopy != null)
                {
                    var (scale, pos, rot) = SampleDeployedCanopy(prefab, chute);
                    parachuteInfo = new ParachuteGhostInfo
                    {
                        partPersistentId = persistentId,
                        canopyTransform = ghostCanopy,
                        capTransform = ghostCap,
                        deployedCanopyScale = scale,
                        deployedCanopyPos = pos,
                        deployedCanopyRot = rot
                    };
                    ghostCanopy.localScale = Vector3.zero;

                    if (canopySubtreeRoot != null && partName.StartsWith("kerbalEVA"))
                    {
                        // EVA deploy animation (fullyDeploySmall) only captures chute-module
                        // movement — the kerbal body pose change that swings the backpack
                        // overhead is a separate animation we can't sample. Override with a
                        // position above the kerbal's head and dome-down rotation.
                        ghostCanopy.SetParent(partRoot.transform, false);
                        ghostCanopy.localPosition = Vector3.zero;
                        ghostCanopy.localRotation = Quaternion.identity;
                        parachuteInfo.deployedCanopyPos = new Vector3(0f, 1f, 0f);
                        parachuteInfo.deployedCanopyRot = Quaternion.Euler(270f, 0f, 0f);
                        ParsekLog.Log($"    EVA parachute: overriding deployed pos=(0,1,0) rot=(270,0,0) " +
                            $"(animation sampled pos={pos} rot={rot.eulerAngles})");
                    }
                    else if (canopySubtreeRoot != null)
                    {
                        // Non-EVA part with canopy outside modelRoot: reparent under
                        // subtree root so root-relative deployed position works.
                        ghostCanopy.SetParent(canopySubtreeRoot, false);
                        ghostCanopy.localPosition = Vector3.zero;
                        ghostCanopy.localRotation = Quaternion.identity;
                    }

                    ParsekLog.Log($"    Parachute detected: canopy='{canopyName}' cap='{capName}' " +
                        $"deployScale={parachuteInfo.deployedCanopyScale} " +
                        $"deployPos={parachuteInfo.deployedCanopyPos} " +
                        $"deployRot={parachuteInfo.deployedCanopyRot.eulerAngles} " +
                        $"parent='{ghostCanopy.parent.name}'");
                }
                else if (srcCanopy != null)
                {
                    ParsekLog.Log($"    Parachute '{canopyName}' found on prefab but not in cloneMap " +
                        $"— will use fake canopy");
                }
            }

            // Detect jettison parts (shrouds/fairings) via cloneMap.
            // Some parts expose multiple ModuleJettison modules and/or comma-separated
            // jettisonName values (e.g. fairingL,fairingR) that must all toggle together.
            var ghostJettisonTransforms = new List<Transform>();
            var resolvedJettisonNames = new List<string>();
            for (int moduleIndex = 0; moduleIndex < prefab.Modules.Count; moduleIndex++)
            {
                var jettison = prefab.Modules[moduleIndex] as ModuleJettison;
                if (jettison == null) continue;

                string jettisonNameList = jettison.jettisonName;
                if (string.IsNullOrWhiteSpace(jettisonNameList)) continue;

                string[] jettisonNames = jettisonNameList.Split(
                    new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                for (int nameIndex = 0; nameIndex < jettisonNames.Length; nameIndex++)
                {
                    string jettisonName = jettisonNames[nameIndex].Trim();
                    if (string.IsNullOrEmpty(jettisonName)) continue;

                    Transform srcJettison = prefab.FindModelTransform(jettisonName);
                    if (srcJettison == null)
                    {
                        ParsekLog.Log($"    Jettison '{jettisonName}' not found on prefab for module {moduleIndex} ({partName})");
                        continue;
                    }

                    Transform ghostJettison;
                    if (!cloneMap.TryGetValue(srcJettison, out ghostJettison) || ghostJettison == null)
                    {
                        ParsekLog.Log($"    Jettison '{jettisonName}' found on prefab but not in cloneMap");
                        continue;
                    }

                    if (ghostJettisonTransforms.Contains(ghostJettison)) continue;
                    ghostJettisonTransforms.Add(ghostJettison);
                    resolvedJettisonNames.Add(jettisonName);
                }
            }

            if (ghostJettisonTransforms.Count > 0)
            {
                jettisonInfo = new JettisonGhostInfo
                {
                    partPersistentId = persistentId,
                    jettisonTransforms = ghostJettisonTransforms
                };
                ParsekLog.Log($"    Jettison detected: '{string.Join(", ", resolvedJettisonNames.ToArray())}' pid={persistentId}");
            }

            // Detect engine parts and clone FX particle systems
            engineInfos = TryBuildEngineFX(prefab, persistentId, partName, modelRoot,
                modelNode.transform, cloneMap);

            // Detect RCS parts and clone FX particle systems
            rcsInfos = TryBuildRcsFX(prefab, persistentId, partName, modelRoot,
                modelNode.transform, cloneMap, raiseRcsVisualOnly);

            string ladderAnimName;
            string ladderAnimRootName;
            bool hasRetractableLadder = TryGetRetractableLadderAnimation(
                prefab, out ladderAnimName, out ladderAnimRootName);
            string animationGroupDeployAnimName;
            bool hasAnimationGroupDeploy = TryGetAnimationGroupDeployAnimation(
                prefab, out animationGroupDeployAnimName);
            string standaloneAnimateGenericAnimName;
            bool hasStandaloneAnimateGenericDeploy = TryGetStandaloneAnimateGenericDeployAnimation(
                prefab, out standaloneAnimateGenericAnimName);
            string animateHeatAnimName;
            bool hasAnimateHeat = TryGetAnimateHeatAnimation(
                prefab, out animateHeatAnimName);
            string aeroSurfaceTransformName;
            float aeroSurfaceDeployAngle;
            bool hasAeroSurfaceDeploy = TryGetAeroSurfaceDeployInfo(
                prefab, out aeroSurfaceTransformName, out aeroSurfaceDeployAngle);
            string controlSurfaceTransformName;
            float controlSurfaceDeployAngle;
            bool hasControlSurfaceDeploy = TryGetControlSurfaceDeployInfo(
                prefab, out controlSurfaceTransformName, out controlSurfaceDeployAngle);
            List<string> robotArmScannerAnimCandidates;
            bool hasRobotArmScannerDeploy = TryGetRobotArmScannerDeployAnimations(
                prefab, out robotArmScannerAnimCandidates);
            bool hasRoboticModules = HasRoboticModules(prefab);

            // If the part has any animated modules (deployable, gear, ladder, animation-group, cargo bay), ensure
            // the full model transform hierarchy is mirrored into the ghost.  The mesh-cloning
            // phase above only creates transforms leading to MeshRenderers/SkinnedMeshRenderers.
            // Animations often move intermediate non-mesh transforms that would otherwise be
            // missing, causing FindTransformByPath to fail during deploy/retract playback.
            bool needsFullHierarchy =
                prefab.FindModuleImplementing<ModuleDeployablePart>() != null ||
                prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null ||
                prefab.FindModuleImplementing<ModuleCargoBay>() != null ||
                hasRetractableLadder ||
                hasAnimationGroupDeploy ||
                hasStandaloneAnimateGenericDeploy ||
                hasAnimateHeat ||
                hasAeroSurfaceDeploy ||
                hasControlSurfaceDeploy ||
                hasRobotArmScannerDeploy ||
                hasRoboticModules;

            if (needsFullHierarchy)
            {
                var allModelTransforms = modelRoot.GetComponentsInChildren<Transform>(true);
                int created = 0;
                for (int t = 0; t < allModelTransforms.Length; t++)
                {
                    Transform src = allModelTransforms[t];
                    if (src == modelRoot) continue;  // already mapped
                    if (cloneMap.ContainsKey(src)) continue;  // already cloned

                    // MirrorTransformChain walks from src up to modelRoot, creating
                    // any missing intermediate nodes and registering them in cloneMap.
                    MirrorTransformChain(src, modelRoot, modelNode.transform, cloneMap);
                    created++;
                }
                ParsekLog.Log($"    EnsureFullHierarchy '{partName}': " +
                    (created > 0
                        ? $"created {created} missing intermediate transforms for animation support"
                        : $"all {allModelTransforms.Length} model transforms already in cloneMap"));
            }

            if (hasRoboticModules)
                roboticInfos = TryBuildRoboticInfos(
                    prefab, persistentId, partName, modelRoot, modelNode.transform, cloneMap);

            // Detect deployable parts (solar panels, antennas, radiators) and pre-resolve transform states
            ModuleDeployablePart deployable = prefab.FindModuleImplementing<ModuleDeployablePart>();
            if (deployable != null)
            {
                var sampledStates = SampleDeployableStates(prefab, deployable);
                if (sampledStates != null)
                {
                    var resolvedTransforms = new List<DeployableTransformState>();
                    int unresolved = 0;
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
                        else
                        {
                            unresolved++;
                            if (unresolved <= 5)
                                ParsekLog.Log($"    [DIAG] Deployable '{partName}': unresolved path '{path}'");
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

            // Detect stock retractable ladders (RetractableLadder module) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasRetractableLadder)
            {
                var sampledStates = SampleLadderStates(prefab, ladderAnimName, ladderAnimRootName);
                if (sampledStates != null)
                {
                    var resolvedTransforms = new List<DeployableTransformState>();
                    int unresolved = 0;
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
                        else
                        {
                            unresolved++;
                            if (unresolved <= 5)
                                ParsekLog.Log($"    [DIAG] Ladder '{partName}': unresolved path '{path}'");
                        }
                    }

                    if (resolvedTransforms.Count > 0)
                    {
                        deployableInfo = new DeployableGhostInfo
                        {
                            partPersistentId = persistentId,
                            transforms = resolvedTransforms
                        };
                        ParsekLog.Log($"    Ladder detected: '{partName}' pid={persistentId}, " +
                            $"{resolvedTransforms.Count}/{sampledStates.Count} transforms resolved");
                    }
                    else
                    {
                        ParsekLog.Log($"    Ladder '{partName}' pid={persistentId}: " +
                            $"sampled {sampledStates.Count} transforms but none resolved to ghost");
                    }
                }
            }

            // Detect ModuleAnimationGroup deploy animations (e.g. drills) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasAnimationGroupDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, animationGroupDeployAnimName, null);
                if (sampledStates != null)
                {
                    var resolvedTransforms = new List<DeployableTransformState>();
                    int unresolved = 0;
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
                        else
                        {
                            unresolved++;
                            if (unresolved <= 5)
                                ParsekLog.Log($"    [DIAG] AnimationGroup '{partName}': unresolved path '{path}'");
                        }
                    }

                    if (resolvedTransforms.Count > 0)
                    {
                        deployableInfo = new DeployableGhostInfo
                        {
                            partPersistentId = persistentId,
                            transforms = resolvedTransforms
                        };
                        ParsekLog.Log($"    AnimationGroup deployable detected: '{partName}' pid={persistentId}, " +
                            $"{resolvedTransforms.Count}/{sampledStates.Count} transforms resolved");
                    }
                }
            }

            // Detect standalone ModuleAnimateGeneric deploy animations (e.g. science/inflatable parts)
            if (deployableInfo == null && hasStandaloneAnimateGenericDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, standaloneAnimateGenericAnimName, null);
                if (sampledStates != null)
                {
                    var resolvedTransforms = new List<DeployableTransformState>();
                    int unresolved = 0;
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
                        else
                        {
                            unresolved++;
                            if (unresolved <= 5)
                                ParsekLog.Log($"    [DIAG] AnimateGeneric '{partName}': unresolved path '{path}'");
                        }
                    }

                    if (resolvedTransforms.Count > 0)
                    {
                        deployableInfo = new DeployableGhostInfo
                        {
                            partPersistentId = persistentId,
                            transforms = resolvedTransforms
                        };
                        ParsekLog.Log($"    AnimateGeneric deployable detected: '{partName}' pid={persistentId}, " +
                            $"{resolvedTransforms.Count}/{sampledStates.Count} transforms resolved");
                    }
                }
            }

            // Detect ModuleAeroSurface visual transforms (airbrakes/control surfaces).
            if (deployableInfo == null && hasAeroSurfaceDeploy)
            {
                var sourceTransforms = FindTransformsRecursive(modelRoot, aeroSurfaceTransformName);
                if (sourceTransforms != null && sourceTransforms.Count > 0)
                {
                    var resolvedTransforms = new List<DeployableTransformState>();
                    for (int i = 0; i < sourceTransforms.Count; i++)
                    {
                        Transform sourceTransform = sourceTransforms[i];
                        if (sourceTransform == null) continue;

                        Transform ghostT;
                        if (!cloneMap.TryGetValue(sourceTransform, out ghostT) || ghostT == null)
                        {
                            string path = GetTransformPath(sourceTransform, modelRoot);
                            ghostT = FindTransformByPath(modelNode.transform, path);
                        }
                        if (ghostT == null) continue;

                        Vector3 stowedPos = ghostT.localPosition;
                        Quaternion stowedRot = ghostT.localRotation;
                        Vector3 stowedScale = ghostT.localScale;
                        Quaternion deployedRot = stowedRot * Quaternion.AngleAxis(aeroSurfaceDeployAngle, Vector3.right);

                        resolvedTransforms.Add(new DeployableTransformState
                        {
                            t = ghostT,
                            stowedPos = stowedPos,
                            stowedRot = stowedRot,
                            stowedScale = stowedScale,
                            deployedPos = stowedPos,
                            deployedRot = deployedRot,
                            deployedScale = stowedScale
                        });
                    }

                    if (resolvedTransforms.Count > 0)
                    {
                        deployableInfo = new DeployableGhostInfo
                        {
                            partPersistentId = persistentId,
                            transforms = resolvedTransforms
                        };
                        ParsekLog.Log($"    AeroSurface deployable detected: '{partName}' pid={persistentId}, " +
                            $"transform='{aeroSurfaceTransformName}' angle={aeroSurfaceDeployAngle:F1} " +
                            $"resolved={resolvedTransforms.Count}");
                    }
                }
                else
                {
                    ParsekLog.Log($"    AeroSurface '{partName}' pid={persistentId}: " +
                        $"transform '{aeroSurfaceTransformName}' not found under modelRoot");
                }
            }

            // Detect ModuleControlSurface visual transforms (elevons/rudders/prop blades).
            if (deployableInfo == null && hasControlSurfaceDeploy)
            {
                var sourceTransforms = FindTransformsRecursive(modelRoot, controlSurfaceTransformName);
                if (sourceTransforms != null && sourceTransforms.Count > 0)
                {
                    var resolvedTransforms = new List<DeployableTransformState>();
                    for (int i = 0; i < sourceTransforms.Count; i++)
                    {
                        Transform sourceTransform = sourceTransforms[i];
                        if (sourceTransform == null) continue;

                        Transform ghostT;
                        if (!cloneMap.TryGetValue(sourceTransform, out ghostT) || ghostT == null)
                        {
                            string path = GetTransformPath(sourceTransform, modelRoot);
                            ghostT = FindTransformByPath(modelNode.transform, path);
                        }
                        if (ghostT == null) continue;

                        Vector3 stowedPos = ghostT.localPosition;
                        Quaternion stowedRot = ghostT.localRotation;
                        Vector3 stowedScale = ghostT.localScale;
                        Quaternion deployedRot = stowedRot * Quaternion.AngleAxis(controlSurfaceDeployAngle, Vector3.right);

                        resolvedTransforms.Add(new DeployableTransformState
                        {
                            t = ghostT,
                            stowedPos = stowedPos,
                            stowedRot = stowedRot,
                            stowedScale = stowedScale,
                            deployedPos = stowedPos,
                            deployedRot = deployedRot,
                            deployedScale = stowedScale
                        });
                    }

                    if (resolvedTransforms.Count > 0)
                    {
                        deployableInfo = new DeployableGhostInfo
                        {
                            partPersistentId = persistentId,
                            transforms = resolvedTransforms
                        };
                        ParsekLog.Log($"    ControlSurface deployable detected: '{partName}' pid={persistentId}, " +
                            $"transform='{controlSurfaceTransformName}' angle={controlSurfaceDeployAngle:F1} " +
                            $"resolved={resolvedTransforms.Count}");
                    }
                }
                else
                {
                    ParsekLog.Log($"    ControlSurface '{partName}' pid={persistentId}: " +
                        $"transform '{controlSurfaceTransformName}' not found under modelRoot");
                }
            }

            // Detect ModuleRobotArmScanner deploy/unpack animation (Breaking Ground ROC arms).
            if (deployableInfo == null && hasRobotArmScannerDeploy)
            {
                string selectedAnimName = null;
                List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
                    Vector3 dPos, Quaternion dRot, Vector3 dScale)> sampledStates = null;
                int bestCount = -1;

                for (int i = 0; i < robotArmScannerAnimCandidates.Count; i++)
                {
                    string candidateAnim = robotArmScannerAnimCandidates[i];
                    var candidateStates = SampleLadderStates(prefab, candidateAnim, null);
                    int candidateCount = candidateStates != null ? candidateStates.Count : 0;
                    ParsekLog.Log($"    RobotArmScanner '{partName}' candidate anim='{candidateAnim}' sampled={candidateCount}");
                    if (candidateCount > bestCount)
                    {
                        bestCount = candidateCount;
                        sampledStates = candidateStates;
                        selectedAnimName = candidateAnim;
                    }
                }

                if (sampledStates != null)
                {
                    var resolvedTransforms = new List<DeployableTransformState>();
                    int unresolved = 0;
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
                        else
                        {
                            unresolved++;
                            if (unresolved <= 5)
                                ParsekLog.Log($"    [DIAG] RobotArmScanner '{partName}': unresolved path '{path}'");
                        }
                    }

                    if (resolvedTransforms.Count > 0)
                    {
                        deployableInfo = new DeployableGhostInfo
                        {
                            partPersistentId = persistentId,
                            transforms = resolvedTransforms
                        };
                        ParsekLog.Log($"    RobotArmScanner deployable detected: '{partName}' pid={persistentId}, " +
                            $"anim='{selectedAnimName}' {resolvedTransforms.Count}/{sampledStates.Count} transforms resolved");
                    }
                }
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

                    // Fallback: if DeployModuleIndex didn't resolve to ModuleAnimateGeneric
                    // (common when KSP inserts internal modules that shift indices), search
                    // for any ModuleAnimateGeneric on the part.
                    if (animModule == null)
                    {
                        animModule = prefab.FindModuleImplementing<ModuleAnimateGeneric>();
                        if (animModule != null)
                            ParsekLog.Log($"    CargoBay '{partName}': DeployModuleIndex={deployIdx} " +
                                $"didn't resolve to ModuleAnimateGeneric (Modules.Count={prefab.Modules.Count}), " +
                                $"using fallback FindModuleImplementing");
                    }

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
                                else if (s < 5)
                                {
                                    ParsekLog.Log($"    [DIAG] CargoBay '{partName}': unresolved path '{path}'");
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

            // Detect ModuleAnimateHeat visual states (thermal glow / heat-driven animation).
            if (hasAnimateHeat)
            {
                List<DeployableTransformState> resolvedHeatTransforms = null;
                var sampledHeatStates = SampleAnimateHeatStates(prefab, animateHeatAnimName);
                if (sampledHeatStates != null && sampledHeatStates.Count > 0)
                {
                    resolvedHeatTransforms = new List<DeployableTransformState>();
                    int unresolved = 0;
                    for (int s = 0; s < sampledHeatStates.Count; s++)
                    {
                        var (path, sPos, sRot, sScale, dPos, dRot, dScale) = sampledHeatStates[s];
                        Transform ghostT = FindTransformByPath(modelNode.transform, path);
                        if (ghostT != null)
                        {
                            resolvedHeatTransforms.Add(new DeployableTransformState
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
                        else
                        {
                            unresolved++;
                            if (unresolved <= 5)
                                ParsekLog.Log($"    [DIAG] AnimateHeat '{partName}': unresolved path '{path}'");
                        }
                    }
                }

                List<HeatMaterialState> heatMaterialStates =
                    BuildHeatMaterialStates(modelNode.transform, partName, persistentId);

                if ((resolvedHeatTransforms != null && resolvedHeatTransforms.Count > 0) ||
                    (heatMaterialStates != null && heatMaterialStates.Count > 0))
                {
                    heatInfo = new HeatGhostInfo
                    {
                        partPersistentId = persistentId,
                        transforms = resolvedHeatTransforms,
                        materialStates = heatMaterialStates
                    };

                    if (heatMaterialStates != null)
                    {
                        for (int i = 0; i < heatMaterialStates.Count; i++)
                        {
                            HeatMaterialState materialState = heatMaterialStates[i];
                            if (materialState.material == null) continue;

                            if (!string.IsNullOrEmpty(materialState.colorProperty))
                                materialState.material.SetColor(materialState.colorProperty, materialState.coldColor);
                            if (!string.IsNullOrEmpty(materialState.emissiveProperty))
                                materialState.material.SetColor(materialState.emissiveProperty, materialState.coldEmission);
                        }
                    }

                    ParsekLog.Log($"    AnimateHeat detected: '{partName}' pid={persistentId}, anim='{animateHeatAnimName}' " +
                        $"transforms={(resolvedHeatTransforms != null ? resolvedHeatTransforms.Count : 0)} " +
                        $"materials={(heatMaterialStates != null ? heatMaterialStates.Count : 0)}");
                }
                else
                {
                    ParsekLog.Log($"    AnimateHeat '{partName}' pid={persistentId}: no transform/material deltas");
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

                        float clonedIntensity = srcLight.intensity;
                        float clonedRange = srcLight.range;
                        if (clonedIntensity <= 0.001f)
                            clonedIntensity = LightMinimumIntensity;
                        if (clonedRange <= 0.001f)
                            clonedRange = LightMinimumRange;

                        if (raiseLightVisualOnly)
                        {
                            clonedIntensity = Mathf.Max(
                                clonedIntensity * LightShowcaseIntensityScale,
                                LightShowcaseMinimumIntensity);
                            clonedRange = Mathf.Max(
                                clonedRange * LightShowcaseRangeScale,
                                LightShowcaseMinimumRange);
                        }

                        // Create a new Light component on the ghost transform
                        Light ghostLight = ghostParent.gameObject.AddComponent<Light>();
                        ghostLight.type = srcLight.type;
                        ghostLight.color = srcLight.color;
                        ghostLight.intensity = clonedIntensity;
                        ghostLight.range = clonedRange;
                        ghostLight.spotAngle = srcLight.spotAngle;
                        ghostLight.cullingMask = srcLight.cullingMask;
                        ghostLight.shadows = LightShadows.None;
                        ghostLight.renderMode = raiseLightVisualOnly
                            ? LightRenderMode.ForcePixel
                            : srcLight.renderMode;
                        ghostLight.enabled = false;
                        clonedLights.Add(ghostLight);
                        ParsekLog.Log(
                            $"      Light clone[{li}] '{srcLight.name}': " +
                            $"srcI={srcLight.intensity:F2} srcR={srcLight.range:F1} " +
                            $"-> ghostI={clonedIntensity:F2} ghostR={clonedRange:F1} " +
                            $"mode={ghostLight.renderMode}");
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
