using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
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

        // Reentry FX thresholds — matched to KSP stock Physics.cfg aeroFX parameters
        // KSP shows thermal FX (orange flames) starting at Mach 2.5, fully orange at Mach 3.75
        // aeroFXStrength ∝ velocity^3.5 * (0.0091 * density^0.5 + 0.09 * density^2)
        // Density fade starts at 0.0015 kg/m³ (near edge of atmosphere)
        internal const float AeroFxThermalStartMach = 2.5f;
        internal const float AeroFxThermalFullMach = 3.75f;
        internal const float AeroFxVelocityExponent = 3.5f;
        internal const float AeroFxDensityScalar1 = 0.0091f;
        internal const float AeroFxDensityExponent1 = 0.5f;
        internal const float AeroFxDensityScalar2 = 0.09f;
        internal const float AeroFxDensityExponent2 = 2f;
        internal const float AeroFxDensityFadeStart = 0.0015f;
        internal static readonly Color ReentryHotEmissionLow = new Color(1.5f, 0.6f, 0.15f, 1f);
        internal static readonly Color ReentryHotEmissionHigh = new Color(2.0f, 1.5f, 0.8f, 1f);
        internal const float ReentrySmoothingRate = 18.0f;
        internal const float ReentryLayerAThreshold = 0.02f;
        internal const float ReentryFireThreshold = 0.02f;

        // Fire envelope particle configuration
        internal const float ReentryFireLifetimeMin = 0.1f;
        internal const float ReentryFireLifetimeMax = 0.35f;
        internal const int ReentryFireMaxParticles = 1500;
        internal const float ReentryFireEmissionMin = 300f;
        internal const float ReentryFireEmissionMax = 2000f;

        // Fire shell overlay: ghost meshes drawn at offsets along velocity
        // Approximates KSP's FXCamera replacement shader (vertex displacement along airflow)
        internal const int ReentryFireShellPasses = 4;
        internal const float ReentryFireShellMaxOffset = 0.05f; // fraction of vesselLength
        internal static readonly Color ReentryFireShellColor = new Color(1f, 0.45f, 0.12f, 1f);

        // Cache for PREFAB_PARTICLE fx_* prefabs found on PartLoader part prefabs.
        // Built once from PartLoader.LoadedPartsList (stable prefab templates).
        private static Dictionary<string, GameObject> fxPrefabCache;
        private static bool fxLoadedObjectScanCompleted;
        private static readonly Dictionary<string, string[]> fxPrefabFallbacks =
            new Dictionary<string, string[]>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "fx_exhaustFlame_yellow_tiny_Z", new[] { "fx_exhaustFlame_yellow_tiny" } },
                { "fx_smokeTrail_veryLarge", new[] { "fx_smokeTrail_large", "fx_smokeTrail_light" } },
                // RAPIER's smokePoint frame matches stock large-smoke setups more closely.
                { "fx_smokeTrail_aeroSpike", new[] { "fx_smokeTrail_large", "fx_smokeTrail_light" } }
            };

        /// <summary>
        /// Groups a flat list of ColorChangerGhostInfo by partPersistentId.
        /// Used at ghost build time to populate GhostPlaybackState.colorChangerInfos.
        /// </summary>
        internal static Dictionary<uint, List<ColorChangerGhostInfo>> GroupColorChangersByPartId(
            List<ColorChangerGhostInfo> list)
        {
            var dict = new Dictionary<uint, List<ColorChangerGhostInfo>>();
            if (list == null) return dict;
            for (int i = 0; i < list.Count; i++)
            {
                uint pid = list[i].partPersistentId;
                List<ColorChangerGhostInfo> bucket;
                if (!dict.TryGetValue(pid, out bucket))
                {
                    bucket = new List<ColorChangerGhostInfo>();
                    dict[pid] = bucket;
                }
                bucket.Add(list[i]);
            }
            return dict;
        }

        /// <summary>
        /// Find a KSP fx_* prefab by name. These exist as children of legacy part
        /// prefabs (e.g., fx_exhaustFlame_blue(Clone) on liquidEngine's prefab transform).
        /// We scan PartLoader.LoadedPartsList once and cache the results.
        /// Self-heals: if a cached reference becomes Unity-null, removes and re-scans.
        /// </summary>
        internal static GameObject FindFxPrefab(string prefabName)
        {
            prefabName = NormalizeFxPrefabName(prefabName);
            if (string.IsNullOrEmpty(prefabName))
                return null;

            if (fxPrefabCache == null)
                RebuildFxPrefabCache();

            GameObject result;
            if (TryGetFxPrefabFromCache(prefabName, out result))
            {
                return result;
            }

            result = TryResolveFxPrefabExact(prefabName);
            if (result != null)
            {
                fxPrefabCache[prefabName] = result;
                return result;
            }

            if (fxPrefabFallbacks.TryGetValue(prefabName, out string[] fallbackNames))
            {
                for (int i = 0; i < fallbackNames.Length; i++)
                {
                    string fallbackName = NormalizeFxPrefabName(fallbackNames[i]);
                    if (string.IsNullOrEmpty(fallbackName) ||
                        string.Equals(fallbackName, prefabName, System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!TryGetFxPrefabFromCache(fallbackName, out result))
                    {
                        result = TryResolveFxPrefabExact(fallbackName);
                        if (result != null)
                            fxPrefabCache[fallbackName] = result;
                    }

                    if (result != null)
                    {
                        fxPrefabCache[prefabName] = result;
                        ParsekLog.Verbose("GhostVisual", $"FX prefab substitution: '{prefabName}' -> '{fallbackName}'");
                        return result;
                    }
                }
            }

            return null;
        }

        internal static string NormalizeFxPrefabName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
                return rawName;

            string name = rawName.Trim();
            if (name.EndsWith("(Clone)"))
                name = name.Substring(0, name.Length - 7);
            return name;
        }

        private static bool TryGetFxPrefabFromCache(string prefabName, out GameObject result)
        {
            result = null;
            if (fxPrefabCache == null)
                return false;

            if (!fxPrefabCache.TryGetValue(prefabName, out result))
                return false;

            // Self-heal: if cached object was destroyed, remove and rebuild.
            if (result == null)
            {
                fxPrefabCache.Remove(prefabName);
                RebuildFxPrefabCache();
                return fxPrefabCache.TryGetValue(prefabName, out result) && result != null;
            }

            return true;
        }

        private static GameObject TryResolveFxPrefabExact(string prefabName)
        {
            string[] resourcePaths = { prefabName, $"FX/{prefabName}", $"fx/{prefabName}" };
            for (int i = 0; i < resourcePaths.Length; i++)
            {
                string resourcePath = resourcePaths[i];
                GameObject result = Resources.Load<GameObject>(resourcePath);
                if (result != null)
                {
                    ParsekLog.Verbose("GhostVisual", $"FX prefab loaded from Resources: '{prefabName}' (path='{resourcePath}')");
                    return result;
                }
            }

            GameObject modelPrefab = GameDatabase.Instance != null
                ? GameDatabase.Instance.GetModelPrefab(prefabName)
                : null;
            if (modelPrefab != null)
            {
                ParsekLog.Verbose("GhostVisual", $"FX prefab loaded from GameDatabase model: '{prefabName}'");
                return modelPrefab;
            }

            PopulateFxPrefabCacheFromLoadedObjects();
            if (TryGetFxPrefabFromCache(prefabName, out GameObject cached))
            {
                ParsekLog.Verbose("GhostVisual", $"FX prefab resolved from loaded objects: '{prefabName}'");
                return cached;
            }

            return null;
        }

        private static void PopulateFxPrefabCacheFromLoadedObjects()
        {
            if (fxLoadedObjectScanCompleted)
                return;

            fxLoadedObjectScanCompleted = true;
            if (fxPrefabCache == null)
                fxPrefabCache = new Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);

            GameObject[] loadedObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            int added = 0;
            for (int i = 0; i < loadedObjects.Length; i++)
            {
                GameObject go = loadedObjects[i];
                if (go == null)
                    continue;

                string name = NormalizeFxPrefabName(go.name);
                if (string.IsNullOrEmpty(name) || !name.StartsWith("fx_"))
                    continue;
                if (fxPrefabCache.ContainsKey(name))
                    continue;
                if (go.GetComponentInChildren<ParticleSystem>(true) == null)
                    continue;

                fxPrefabCache[name] = go;
                added++;
            }

            ParsekLog.Info("GhostVisual", $"FX prefab cache extended from loaded objects: +{added} entries");
        }

        private static void RebuildFxPrefabCache()
        {
            fxPrefabCache = new Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);
            fxLoadedObjectScanCompleted = false;
            if (PartLoader.LoadedPartsList == null) return;

            for (int p = 0; p < PartLoader.LoadedPartsList.Count; p++)
            {
                Part prefab = PartLoader.LoadedPartsList[p]?.partPrefab;
                if (prefab == null) continue;

                for (int c = 0; c < prefab.transform.childCount; c++)
                {
                    Transform child = prefab.transform.GetChild(c);
                    string name = NormalizeFxPrefabName(child.name);
                    if (name.StartsWith("fx_") && !fxPrefabCache.ContainsKey(name)
                        && child.GetComponentInChildren<ParticleSystem>() != null)
                    {
                        fxPrefabCache[name] = child.gameObject;
                    }
                }
            }
            ParsekLog.Info("GhostVisual", $"FX prefab cache built from PartLoader: {fxPrefabCache.Count} entries");
        }

        /// <summary>Clear the fx prefab cache (e.g., on scene change).</summary>
        internal static void ClearFxPrefabCache()
        {
            fxPrefabCache = null;
            fxLoadedObjectScanCompleted = false;
        }

        /// <summary>
        /// Builds a ghost GameObject from a recording's vessel snapshot, returning
        /// all per-module-type ghost info lists bundled in a GhostBuildResult.
        /// Returns null if the snapshot is missing or produces zero visuals.
        /// </summary>
        internal static GhostBuildResult BuildTimelineGhostFromSnapshot(
            IPlaybackTrajectory rec, string rootName)
        {
            ConfigNode snapshotNode = GetGhostSnapshot(rec);
            if (snapshotNode == null)
            {
                ParsekLog.Info("GhostVisual",
                    $"Ghost build aborted for '{rec?.VesselName ?? "unknown"}': no snapshot node");
                return null;
            }

            var partNodes = snapshotNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0)
            {
                ParsekLog.Info("GhostVisual",
                    $"Ghost build aborted for '{rec?.VesselName ?? "unknown"}': snapshot has no PART nodes");
                return null;
            }

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
            var collectedColorChangerInfos = new List<ColorChangerGhostInfo>();

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

                AvailablePart ap = ResolveAvailablePart(partName);
                if (ap == null || ap.partPrefab == null)
                {
                    skippedPrefab++;
                    continue;
                }
                string resolvedName = ap.partPrefab.partInfo?.name ?? ap.name;

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
                List<ColorChangerGhostInfo> partColorChangerInfos;
                bool raiseLightVisualOnly =
                    !string.IsNullOrEmpty(rec.VesselName) &&
                    rec.VesselName.StartsWith(LightsShowcaseRecordingPrefix, System.StringComparison.Ordinal);
                bool raiseRcsVisualOnly =
                    !string.IsNullOrEmpty(rec.VesselName) &&
                    rec.VesselName.StartsWith(RcsShowcaseRecordingPrefix, System.StringComparison.Ordinal);
                bool partVisualAdded = AddPartVisuals(root.transform, partNode, ap.partPrefab,
                    persistentId, partName, out meshCount, out parachuteInfo, out jettisonInfo,
                    out partEngineInfos, out deployableInfo, out heatInfo, out lightInfo, out fairingInfo,
                    out partRcsInfos, out partRoboticInfos, out partColorChangerInfos,
                    raiseLightVisualOnly, raiseRcsVisualOnly);
                if (partVisualAdded)
                    visualCount++;
                else
                {
                    skippedMesh++;
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
                if (partColorChangerInfos != null)
                    collectedColorChangerInfos.AddRange(partColorChangerInfos);
            }

            ParsekLog.VerboseRateLimited("GhostVisual", $"ghost_built_{rootName}",
                $"Ghost built: {visualCount}/{partNodes.Length} parts with visuals" +
                (skippedName > 0 ? $", {skippedName} bad name" : "") +
                (skippedPrefab > 0 ? $", {skippedPrefab} no prefab" : "") +
                (skippedMesh > 0 ? $", {skippedMesh} no mesh" : ""), 60.0);

            if (!addedAnyVisual)
            {
                Object.Destroy(root);
                ParsekLog.Warn("GhostVisual",
                    $"Ghost build produced zero visuals for '{rec?.VesselName ?? "unknown"}' (parts={partNodes.Length})");
                return null;
            }

            return new GhostBuildResult
            {
                root = root,
                parachuteInfos = collectedParachuteInfos.Count > 0 ? collectedParachuteInfos : null,
                jettisonInfos = collectedJettisonInfos.Count > 0 ? collectedJettisonInfos : null,
                engineInfos = collectedEngineInfos.Count > 0 ? collectedEngineInfos : null,
                deployableInfos = collectedDeployableInfos.Count > 0 ? collectedDeployableInfos : null,
                heatInfos = collectedHeatInfos.Count > 0 ? collectedHeatInfos : null,
                lightInfos = collectedLightInfos.Count > 0 ? collectedLightInfos : null,
                fairingInfos = collectedFairingInfos.Count > 0 ? collectedFairingInfos : null,
                rcsInfos = collectedRcsInfos.Count > 0 ? collectedRcsInfos : null,
                roboticInfos = collectedRoboticInfos.Count > 0 ? collectedRoboticInfos : null,
                colorChangerInfos = collectedColorChangerInfos.Count > 0 ? collectedColorChangerInfos : null,
            };
        }

        internal static ConfigNode GetGhostSnapshot(IPlaybackTrajectory rec)
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

            ParsekLog.Warn("GhostVisual", $"ResolveAvailablePart failed for '{partName}' — all lookups exhausted (direct, dotted, underscored, brute-force)");
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

        internal static bool TryParseFxLocalRotation(string value, out Quaternion result)
        {
            result = Quaternion.identity;
            if (string.IsNullOrEmpty(value))
                return false;

            string[] parts = value.Split(',');
            if (parts.Length == 3)
            {
                if (!TryParseVector3(value, out Vector3 euler))
                    return false;
                result = Quaternion.Euler(euler);
                return true;
            }

            if (parts.Length >= 4 &&
                float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ay) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float az) &&
                float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float angle))
            {
                Vector3 axis = new Vector3(ax, ay, az);
                result = axis.sqrMagnitude > 0.0001f
                    ? Quaternion.AngleAxis(angle, axis)
                    : Quaternion.identity;
                return true;
            }

            return false;
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

        internal static Transform ResolveGhostFxParent(
            Transform srcFxTransform, Transform prefabRoot, Transform modelRoot,
            Transform ghostModelNode, Dictionary<Transform, Transform> cloneMap)
        {
            if (srcFxTransform == null || ghostModelNode == null)
                return null;

            if (IsDescendantOf(srcFxTransform, modelRoot))
                return MirrorTransformChain(srcFxTransform, modelRoot, ghostModelNode, cloneMap);

            Transform ghostPartRoot = ghostModelNode.parent != null ? ghostModelNode.parent : ghostModelNode;
            if (prefabRoot != null && IsDescendantOf(srcFxTransform, prefabRoot))
                return MirrorTransformChain(srcFxTransform, prefabRoot, ghostPartRoot, cloneMap);

            // Last-resort fallback for unexpected hierarchy shapes.
            GameObject fxHolder = new GameObject(srcFxTransform.name);
            fxHolder.transform.SetParent(ghostModelNode, false);
            fxHolder.transform.localPosition = srcFxTransform.localPosition;
            fxHolder.transform.localRotation = srcFxTransform.localRotation;
            fxHolder.transform.localScale = srcFxTransform.localScale;
            cloneMap[srcFxTransform] = fxHolder.transform;
            return fxHolder.transform;
        }

        internal static float ComputeDirectionAngleDegrees(Vector3 source, Vector3 target)
        {
            double sourceSq =
                (source.x * source.x) +
                (source.y * source.y) +
                (source.z * source.z);
            double targetSq =
                (target.x * target.x) +
                (target.y * target.y) +
                (target.z * target.z);
            if (sourceSq <= 0.000001 || targetSq <= 0.000001)
                return float.NaN;

            double dot =
                (source.x * target.x) +
                (source.y * target.y) +
                (source.z * target.z);
            double normalized = dot / System.Math.Sqrt(sourceSq * targetSq);
            if (normalized < -1.0) normalized = -1.0;
            else if (normalized > 1.0) normalized = 1.0;

            return (float)(System.Math.Acos(normalized) * (180.0 / System.Math.PI));
        }

        internal static float ComputeQuaternionAngleDegrees(Quaternion source, Quaternion target)
        {
            double dot = System.Math.Abs(
                (source.x * target.x) +
                (source.y * target.y) +
                (source.z * target.z) +
                (source.w * target.w));

            if (dot > 1.0) dot = 1.0;

            return (float)(System.Math.Acos(dot) * 2.0 * (180.0 / System.Math.PI));
        }

        private static string FormatAngleDegrees(float angleDegrees)
        {
            if (float.IsNaN(angleDegrees) || float.IsInfinity(angleDegrees))
                return "n/a";

            return angleDegrees.ToString("F3", CultureInfo.InvariantCulture);
        }

        internal static string BuildFxFrameDiagnostic(
            Vector3 sourcePartLocalPos,
            Quaternion sourcePartLocalRot,
            Vector3 sourceForward,
            Vector3 sourceUp,
            Vector3 targetPartLocalPos,
            Quaternion targetPartLocalRot,
            Vector3 targetForward,
            Vector3 targetUp)
        {
            Vector3 deltaPos = targetPartLocalPos - sourcePartLocalPos;
            float deltaRot = ComputeQuaternionAngleDegrees(sourcePartLocalRot, targetPartLocalRot);
            float forwardAngle = ComputeDirectionAngleDegrees(sourceForward, targetForward);
            float upAngle = ComputeDirectionAngleDegrees(sourceUp, targetUp);

            return $"deltaPos={deltaPos} deltaPosMag={deltaPos.magnitude.ToString("F4", CultureInfo.InvariantCulture)} " +
                $"deltaRot={deltaRot.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"fwdAngle={FormatAngleDegrees(forwardAngle)} upAngle={FormatAngleDegrees(upAngle)}";
        }

        internal static void LogFxParentAlignmentDiagnostic(
            string partName, int moduleIndex, string fxKind, string transformName,
            Transform prefabRoot, Transform ghostModelNode,
            Transform srcFxTransform, Transform ghostFxParent)
        {
            if (prefabRoot == null || ghostModelNode == null ||
                srcFxTransform == null || ghostFxParent == null)
                return;

            Transform ghostPartRoot = ghostModelNode.parent != null ? ghostModelNode.parent : ghostModelNode;

            Vector3 srcPartLocalPos = prefabRoot.InverseTransformPoint(srcFxTransform.position);
            Vector3 ghostPartLocalPos = ghostPartRoot.InverseTransformPoint(ghostFxParent.position);

            Quaternion srcPartLocalRot = Quaternion.Inverse(prefabRoot.rotation) * srcFxTransform.rotation;
            Quaternion ghostPartLocalRot = Quaternion.Inverse(ghostPartRoot.rotation) * ghostFxParent.rotation;
            string diagnostic = BuildFxFrameDiagnostic(
                srcPartLocalPos, srcPartLocalRot, srcFxTransform.forward, srcFxTransform.up,
                ghostPartLocalPos, ghostPartLocalRot, ghostFxParent.forward, ghostFxParent.up);

            ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                $"    [DIAG] FX parent align '{partName}' midx={moduleIndex} " +
                $"type={fxKind} transform='{transformName}' {diagnostic} " +
                $"srcLocalRot={srcFxTransform.localRotation.eulerAngles} parentLocalRot={ghostFxParent.localRotation.eulerAngles}", 60.0);
        }

        internal static void LogFxInstancePlacementDiagnostic(
            string partName,
            int moduleIndex,
            string fxKind,
            string transformName,
            string fxAssetName,
            Transform prefabRoot,
            Transform ghostModelNode,
            Transform srcFxTransform,
            Transform ghostFxParent,
            Transform fxTransform,
            Vector3 configuredLocalOffset,
            Quaternion configuredLocalRotation,
            bool hasConfiguredLocalRotation)
        {
            if (prefabRoot == null || ghostModelNode == null ||
                srcFxTransform == null || ghostFxParent == null || fxTransform == null)
                return;

            Transform ghostPartRoot = ghostModelNode.parent != null ? ghostModelNode.parent : ghostModelNode;

            Vector3 srcPartLocalPos = prefabRoot.InverseTransformPoint(srcFxTransform.position);
            Quaternion srcPartLocalRot = Quaternion.Inverse(prefabRoot.rotation) * srcFxTransform.rotation;

            Vector3 parentPartLocalPos = ghostPartRoot.InverseTransformPoint(ghostFxParent.position);
            Quaternion parentPartLocalRot = Quaternion.Inverse(ghostPartRoot.rotation) * ghostFxParent.rotation;

            Vector3 fxPartLocalPos = ghostPartRoot.InverseTransformPoint(fxTransform.position);
            Quaternion fxPartLocalRot = Quaternion.Inverse(ghostPartRoot.rotation) * fxTransform.rotation;

            string sourceToFx = BuildFxFrameDiagnostic(
                srcPartLocalPos, srcPartLocalRot, srcFxTransform.forward, srcFxTransform.up,
                fxPartLocalPos, fxPartLocalRot, fxTransform.forward, fxTransform.up);
            string parentToFx = BuildFxFrameDiagnostic(
                parentPartLocalPos, parentPartLocalRot, ghostFxParent.forward, ghostFxParent.up,
                fxPartLocalPos, fxPartLocalRot, fxTransform.forward, fxTransform.up);

            string safeAssetName = string.IsNullOrEmpty(fxAssetName) ? "<none>" : fxAssetName;
            ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                $"    [DIAG] FX placement '{partName}' midx={moduleIndex} " +
                $"type={fxKind} transform='{transformName}' asset='{safeAssetName}' " +
                $"cfgOffset={configuredLocalOffset} cfgRot={configuredLocalRotation.eulerAngles} hasCfgRot={hasConfiguredLocalRotation} " +
                $"parent='{ghostFxParent.name}' fx='{fxTransform.name}' " +
                $"fxLocalPos={fxTransform.localPosition} fxLocalRot={fxTransform.localRotation.eulerAngles} " +
                $"sourceToFx=({sourceToFx}) parentToFx=({parentToFx})", 60.0);
        }

        /// <summary>
        /// Configure KSP FX controller components on cloned particle system GameObjects.
        /// KSPParticleEmitter is KEPT alive (it handles material setup and particle creation)
        /// but set to emit=false initially. It is collected into kspEmitterSink for later
        /// control via reflection in SetEngineEmission/SetRcsEmission.
        /// Unity's emission module is disabled separately (in ConfigureGhostEngineParticleSystems)
        /// to prevent Unity from creating its own material-less "bubble" particles.
        /// SmokeTrailControl is STRIPPED — tested keeping alive (audit #113) but it sets material
        /// alpha to 0 on ghosts, making smoke invisible. Needs vessel context to work correctly.
        /// ModelMultiParticlePersistFX/ModelParticleFX are EffectBehaviour subclasses that reference
        /// Host (the Part). KEPT ALIVE — stripping kills smoke trails. Any NREs are non-fatal.
        /// FXPrefab registers particles with FloatingOrigin — pollutes global state on ghosts.
        ///
        /// Audit #113: FXModuleAnimateThrottle and FXModuleAnimateRCS are PartModules (not
        /// MonoBehaviours) requiring Part/Vessel context. They are reimplemented via HeatGhostInfo
        /// animation sampling, which is architecturally correct: one-shot build cost cached per
        /// part type, near-zero runtime cost (3-level quantized transform/material snaps on event
        /// boundaries), correct multi-instance disambiguation. Native modules are infeasible on
        /// ghosts and the reimplementation is intentionally minimal per the visual efficiency
        /// design principle.
        /// </summary>
        internal static void StripKspFxControllers(GameObject fxClone, List<KspEmitterRef> kspEmitterSink)
        {
            if (fxClone == null) return;

            MonoBehaviour[] behaviours = fxClone.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null) continue;
                string typeName = behaviours[i].GetType().Name;
                switch (typeName)
                {
                    case "KSPParticleEmitter":
                        // Cache FieldInfo at capture time to avoid per-frame reflection in
                        // SetKspEmittersEnabled (called from SetEngineEmission/SetRcsEmission).
                        var emitField = behaviours[i].GetType().GetField("emit");
                        if (emitField != null)
                        {
                            emitField.SetValue(behaviours[i], false);
                            kspEmitterSink?.Add(new KspEmitterRef
                            {
                                emitter = behaviours[i],
                                emitField = emitField
                            });
                        }
                        break;
                    case "SmokeTrailControl":
                        // Stripped — tested keeping alive (audit #113) but it makes smoke
                        // invisible on ghosts. SmokeTrailControl sets material alpha to 0
                        // without valid vessel context.
                        Object.Destroy(behaviours[i]);
                        break;
                    case "ModelMultiParticlePersistFX":
                    case "ModelParticleFX":
                        // EffectBehaviour subclasses that reference Host (Part).
                        // Audit #113 stripped these assuming NRE, but they were alive
                        // pre-audit and smoke trails worked. Stripping kills smoke.
                        // Keep alive — any NREs are caught by Unity and don't crash.
                        break;
                    case "FXPrefab":
                        Object.Destroy(behaviours[i]);
                        break;
                }
            }
        }

        internal static int ConfigureGhostEngineParticleSystems(
            GameObject fxInstance, List<ParticleSystem> sink)
        {
            if (fxInstance == null || sink == null)
                return 0;

            ParticleSystem[] systems = fxInstance.GetComponentsInChildren<ParticleSystem>(true);
            int added = 0;
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null)
                    continue;

                var main = ps.main;
                main.playOnAwake = false;
                main.prewarm = false;

                // Only disable Unity's emission module when KSPParticleEmitter is present
                // on this FX object — KSPParticleEmitter handles particle creation via
                // EmitParticle(), so Unity's emission produces duplicate "bubble" artifacts
                // (bug #105). Smoke trail FX (fx_smokeTrail_*) have NO KSPParticleEmitter
                // and rely on Unity's emission module — disabling it kills all smoke.
                bool hasKspEmitter = fxInstance.GetComponent("KSPParticleEmitter") != null;
                var emission = ps.emission;
                if (hasKspEmitter)
                    emission.enabled = false;

                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(true);

                ParticleSystemRenderer[] renderers = ps.GetComponentsInChildren<ParticleSystemRenderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    if (renderers[r] != null)
                        renderers[r].enabled = false;
                }

                sink.Add(ps);
                added++;
            }

            return added;
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

        // Cache: partName → (semi-deployed + deployed canopy states) — sample once per part type, reuse across ghosts
        private static readonly Dictionary<string, (Vector3 sdScale, Vector3 sdPos, Quaternion sdRot, bool semiDeployedSampled,
            Vector3 dScale, Vector3 dPos, Quaternion dRot)> canopyCache =
            new Dictionary<string, (Vector3, Vector3, Quaternion, bool, Vector3, Vector3, Quaternion)>();

        internal static void ClearCanopyCache()
        {
            canopyCache.Clear();
        }

        /// <summary>
        /// Sample both semi-deployed (streamer) and fully deployed (dome) canopy states from animations.
        /// KSP parachute animations are sequential:
        ///   semiDeployedAnimation@time=1 → streamer (textile on wires, no air)
        ///   fullyDeployedAnimation@time=1 → inflated dome
        /// Stowed state is invisible (canopy mesh starts at zero scale in prefabs).
        /// </summary>
        private static (Vector3 sdScale, Vector3 sdPos, Quaternion sdRot, bool semiDeployedSampled,
            Vector3 dScale, Vector3 dPos, Quaternion dRot) SampleCanopyStates(Part prefab, ModuleParachute chute)
        {
            string key = prefab.partInfo?.name ?? prefab.name;
            if (canopyCache.TryGetValue(key, out var cached))
                return cached;

            string semiAnim = chute.semiDeployedAnimation;
            string fullAnim = chute.fullyDeployedAnimation;

            string deployedAnimName = !string.IsNullOrEmpty(fullAnim) ? fullAnim
                : !string.IsNullOrEmpty(semiAnim) ? semiAnim : null;

            Vector3 sdScale = Vector3.zero, sdPos = Vector3.zero;
            Quaternion sdRot = Quaternion.identity;
            bool semiDeployedSampled = false;

            Vector3 dScale = Vector3.one, dPos = Vector3.zero;
            Quaternion dRot = Quaternion.identity;
            bool deployedSampled = false;

            string canopyName = chute.canopyName;

            if (!string.IsNullOrEmpty(semiAnim) || !string.IsNullOrEmpty(deployedAnimName))
            {
                Transform prefabModel = prefab.transform.Find("model") ?? prefab.transform;
                GameObject tempClone = Object.Instantiate(prefabModel.gameObject);

                try
                {
                    Animation anim = tempClone.GetComponentInChildren<Animation>(true);
                    if (anim != null)
                    {
                        // Sample semi-deployed state: semiDeployedAnimation@time=1 (streamer)
                        if (!string.IsNullOrEmpty(semiAnim))
                        {
                            AnimationState semiState = anim[semiAnim];
                            if (semiState != null)
                            {
                                anim.Stop();
                                semiState.enabled = true;
                                semiState.speed = 0f;
                                semiState.normalizedTime = 1f;
                                semiState.weight = 1f;
                                anim.Play(semiAnim);
                                anim.Sample();

                                Transform canopy = !string.IsNullOrEmpty(canopyName)
                                    ? FindTransformRecursive(tempClone.transform, canopyName) : null;
                                if (canopy != null && canopy.localScale.magnitude > 0.01f)
                                {
                                    sdScale = canopy.localScale;
                                    sdPos = tempClone.transform.InverseTransformPoint(canopy.position);
                                    sdRot = Quaternion.Inverse(tempClone.transform.rotation) * canopy.rotation;
                                    semiDeployedSampled = true;
                                }
                            }
                        }

                        // Sample deployed state: fullyDeployedAnimation@time=1 (dome)
                        if (!string.IsNullOrEmpty(deployedAnimName))
                        {
                            anim.Stop();
                            AnimationState deployState = anim[deployedAnimName];
                            if (deployState != null)
                            {
                                deployState.enabled = true;
                                deployState.speed = 0f;
                                deployState.normalizedTime = 1f;
                                deployState.weight = 1f;
                                anim.Play(deployedAnimName);
                                anim.Sample();

                                Transform canopy = !string.IsNullOrEmpty(canopyName)
                                    ? FindTransformRecursive(tempClone.transform, canopyName) : null;
                                if (canopy != null)
                                {
                                    dScale = canopy.localScale;
                                    dPos = tempClone.transform.InverseTransformPoint(canopy.position);
                                    dRot = Quaternion.Inverse(tempClone.transform.rotation) * canopy.rotation;
                                    deployedSampled = true;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    Object.DestroyImmediate(tempClone);
                }
            }

            // Deployed fallback: if animation produced near-zero scale
            if (deployedSampled && dScale.magnitude < 0.01f)
            {
                dScale = Vector3.one;
                dPos = Vector3.zero;
                dRot = Quaternion.identity;
            }

            var result = (sdScale, sdPos, sdRot, semiDeployedSampled, dScale, dPos, dRot);
            canopyCache[key] = result;
            return result;
        }

        // Unified cache for all animation-sampled transform states (deployable, gear, ladder, cargo bay).
        // Key collision is impossible: Deployable/Gear/CargoBay use plain part names but a part enters
        // exactly one code path (priority cascade in TryBuildDeployableInfo). Ladder uses compound keys
        // with '|' separators that never match plain part names.
        private static readonly Dictionary<string, List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)>> animationSampleCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        internal static void ClearAnimationSampleCache()
        {
            animationSampleCache.Clear();
        }

        /// <summary>
        /// How to locate the Animation component on a cloned prefab model.
        /// </summary>
        private enum AnimLookup
        {
            /// <summary>GetComponentInChildren (simplest — used by ModuleDeployablePart).</summary>
            Simple,
            /// <summary>Try named transform first, fallback to GetComponentInChildren (used by landing gear).</summary>
            ByTransformName,
            /// <summary>Search all Animation components for one containing the clip, fallback to GetComponentInChildren (used by cargo bays).</summary>
            ByClipSearch,
            /// <summary>Try named transform, then search all for clip. Intentionally NO GetComponentInChildren fallback (used by ladders/animation groups).</summary>
            ByTransformThenClipSearch
        }

        /// <summary>
        /// Find the Animation component on a cloned model using the specified lookup strategy.
        /// Returns null if no suitable Animation is found.
        /// </summary>
        private static Animation FindAnimation(
            GameObject clone, string animName, string transformName,
            AnimLookup mode, string label, string partKey)
        {
            Animation anim = null;

            switch (mode)
            {
                case AnimLookup.Simple:
                    anim = clone.GetComponentInChildren<Animation>(true);
                    break;

                case AnimLookup.ByTransformName:
                    if (!string.IsNullOrEmpty(transformName))
                    {
                        Transform trf = FindTransformRecursive(clone.transform, transformName);
                        if (trf != null)
                            anim = trf.GetComponent<Animation>();
                    }
                    if (anim == null)
                        anim = clone.GetComponentInChildren<Animation>(true);
                    break;

                case AnimLookup.ByClipSearch:
                    foreach (var candidate in clone.GetComponentsInChildren<Animation>(true))
                    {
                        if (candidate[animName] != null)
                        {
                            anim = candidate;
                            break;
                        }
                    }
                    if (anim == null)
                        anim = clone.GetComponentInChildren<Animation>(true);
                    break;

                case AnimLookup.ByTransformThenClipSearch:
                    // Try named transform first
                    if (!string.IsNullOrEmpty(transformName))
                    {
                        Transform animRoot = FindTransformRecursive(clone.transform, transformName);
                        if (animRoot != null)
                            anim = animRoot.GetComponent<Animation>();
                        else
                            ParsekLog.Verbose("GhostVisual", $"  {label} '{partKey}': animation root '{transformName}' not found on clone");
                    }
                    // Fallback: search all Animation components for one containing the clip
                    // Intentionally NO GetComponentInChildren fallback — if clip isn't found, return null
                    if (anim == null)
                    {
                        foreach (var candidate in clone.GetComponentsInChildren<Animation>(true))
                        {
                            if (candidate[animName] != null)
                            {
                                anim = candidate;
                                break;
                            }
                        }
                    }
                    break;
            }

            return anim;
        }

        /// <summary>
        /// Core animation sampling: clones prefab model, finds Animation via the specified strategy,
        /// samples at two time points (or uses 3-snapshot scoring for endpoint detection), and returns
        /// transform deltas between stowed and deployed states.
        /// Returns null if no animation found, no AnimationState, or no deltas produced.
        /// Does NOT handle caching — callers manage their own cache read/write.
        /// When <paramref name="useScoring"/> is true, <paramref name="stowedTime"/> and
        /// <paramref name="deployedTime"/> are ignored — endpoints are determined by scoring
        /// both animation endpoints against the prefab's default transform state.
        /// </summary>
        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleAnimationStates(
            Part prefab, string animName, string label,
            AnimLookup lookupMode, string lookupTransformName,
            float stowedTime, float deployedTime, bool useScoring)
        {
            string partKey = prefab.partInfo?.name ?? prefab.name;
            Transform modelRoot = FindModelRoot(prefab);
            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);
            List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)> result = null;

            try
            {
                Animation anim = FindAnimation(tempClone, animName, lookupTransformName,
                    lookupMode, label, partKey);
                if (anim == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"  {label} '{partKey}': no Animation component on model clone");
                    return null;
                }

                AnimationState state = anim[animName];
                if (state == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"  {label} '{partKey}': animation '{animName}' not found on clone");
                    return null;
                }

                var allTransforms = tempClone.GetComponentsInChildren<Transform>(true);

                if (useScoring)
                {
                    // 3-snapshot scoring: capture default state BEFORE setting anim properties
                    // (ordering is critical — captures prefab defaults before any animation influence)
                    var defaultStates = SnapshotTransformStates(allTransforms, tempClone.transform);

                    state.enabled = true;
                    state.speed = 0f;
                    state.weight = 1f;

                    state.normalizedTime = 0f;
                    anim.Play(animName);
                    anim.Sample();
                    var time0States = SnapshotTransformStates(allTransforms, tempClone.transform);

                    state.normalizedTime = 1f;
                    anim.Sample();
                    var time1States = SnapshotTransformStates(allTransforms, tempClone.transform);

                    // Score: which endpoint is closer to the default (= stowed)?
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
                    float scoredDeployedTime = stowedIsTime0 ? 1f : 0f;

                    // Re-sample at deployed time so transforms hold deployed state
                    state.normalizedTime = scoredDeployedTime;
                    anim.Sample();

                    result = CollectTransformDeltas(allTransforms, tempClone.transform, stowedStates);

                    if (result.Count == 0)
                    {
                        ParsekLog.Verbose("GhostVisual", $"  {label} '{partKey}': animation '{animName}' produced no transform deltas");
                        result = null;
                    }
                    else
                    {
                        ParsekLog.Verbose("GhostVisual", $"  {label} '{partKey}': sampled {result.Count} animated transforms from '{animName}' " +
                            $"(stowed=time{(stowedIsTime0 ? "0" : "1")})");
                    }
                }
                else
                {
                    // Simple 2-point sampling
                    state.enabled = true;
                    state.speed = 0f;
                    state.normalizedTime = stowedTime;
                    state.weight = 1f;
                    anim.Play(animName);
                    anim.Sample();

                    var stowedStates = SnapshotTransformStates(allTransforms, tempClone.transform);

                    state.normalizedTime = deployedTime;
                    anim.Sample();

                    result = CollectTransformDeltas(allTransforms, tempClone.transform, stowedStates);

                    if (result.Count == 0)
                    {
                        ParsekLog.Verbose("GhostVisual", $"  {label} '{partKey}': animation '{animName}' produced no transform deltas");
                        result = null;
                    }
                    else
                    {
                        ParsekLog.Verbose("GhostVisual", $"  {label} '{partKey}': sampled {result.Count} animated transforms from '{animName}'");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }

            return result;
        }

        // Cache: partName(+anim) -> sampled 3-state ModuleAnimateHeat transform states
        private static readonly Dictionary<string, List<(string path,
            Vector3 coldPos, Quaternion coldRot, Vector3 coldScale,
            Vector3 medPos, Quaternion medRot, Vector3 medScale,
            Vector3 hotPos, Quaternion hotRot, Vector3 hotScale)>> animateHeatCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        internal static void ClearAnimateHeatCache()
        {
            animateHeatCache.Clear();
        }

        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleGearStates(
            Part prefab, string animationStateName, string animationTrfName, float deployedPosition)
        {
            string key = prefab.partInfo?.name ?? prefab.name;
            if (animationSampleCache.TryGetValue(key, out var cached))
                return cached;

            if (string.IsNullOrEmpty(animationStateName))
            {
                ParsekLog.Verbose("GhostVisual", $"  Gear '{key}': no animationStateName — skipping animation sampling");
                animationSampleCache[key] = null;
                return null;
            }

            // Most gear: deployedPosition=1 → stowed=0, deployed=1
            // Rover wheel: deployedPosition=0 → stowed=1, deployed=0
            float stowedTime = deployedPosition >= 0.5f ? 0f : 1f;
            float deployedTime = deployedPosition >= 0.5f ? 1f : 0f;

            var result = SampleAnimationStates(prefab, animationStateName, "Gear",
                AnimLookup.ByTransformName, animationTrfName, stowedTime, deployedTime, false);
            animationSampleCache[key] = result;
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

        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleLadderStates(
            Part prefab, string animationName, string animationRootName)
        {
            string partKey = prefab.partInfo?.name ?? prefab.name;
            string cacheKey = partKey + "|" + (animationName ?? string.Empty) + "|" + (animationRootName ?? string.Empty);
            if (animationSampleCache.TryGetValue(cacheKey, out var cached))
                return cached;

            if (string.IsNullOrEmpty(animationName))
            {
                ParsekLog.Verbose("GhostVisual", $"  Ladder '{partKey}': no ladderRetractAnimationName — skipping animation sampling");
                animationSampleCache[cacheKey] = null;
                return null;
            }

            var result = SampleAnimationStates(prefab, animationName, "Ladder",
                AnimLookup.ByTransformThenClipSearch, animationRootName, 0f, 0f, useScoring: true);
            animationSampleCache[cacheKey] = result;
            return result;
        }

        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleCargoBayStates(
            Part prefab, string animationName, float closedPosition)
        {
            string key = prefab.partInfo?.name ?? prefab.name;
            if (animationSampleCache.TryGetValue(key, out var cached))
                return cached;

            if (string.IsNullOrEmpty(animationName))
            {
                ParsekLog.Verbose("GhostVisual", $"  CargoBay '{key}': no animationName — skipping animation sampling");
                animationSampleCache[key] = null;
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
                ParsekLog.Verbose("GhostVisual", $"  CargoBay '{key}': non-standard closedPosition={closedPosition} — skipping");
                animationSampleCache[key] = null;
                return null;
            }

            var result = SampleAnimationStates(prefab, animationName, "CargoBay",
                AnimLookup.ByClipSearch, null, closedTime, openTime, false);
            animationSampleCache[key] = result;
            return result;
        }

        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> SampleDeployableStates(
            Part prefab, ModuleDeployablePart deployable)
        {
            string key = prefab.partInfo?.name ?? prefab.name;
            if (animationSampleCache.TryGetValue(key, out var cached))
                return cached;

            string animName = deployable.animationName;
            if (string.IsNullOrEmpty(animName))
            {
                ParsekLog.Verbose("GhostVisual", $"  Deployable '{key}': no animationName — skipping animation sampling");
                animationSampleCache[key] = null;
                return null;
            }

            var result = SampleAnimationStates(prefab, animName, "Deployable",
                AnimLookup.Simple, null, 0f, 1f, false);
            animationSampleCache[key] = result;
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
        /// Snapshot local position, rotation and scale of every transform in
        /// <paramref name="allTransforms"/> into a dictionary keyed by the
        /// PathSep-separated path relative to <paramref name="root"/>.
        /// </summary>
        private static Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)> SnapshotTransformStates(
            Transform[] allTransforms, Transform root)
        {
            var states = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
            for (int i = 0; i < allTransforms.Length; i++)
            {
                string path = GetTransformPath(allTransforms[i], root);
                states[path] = (allTransforms[i].localPosition, allTransforms[i].localRotation, allTransforms[i].localScale);
            }
            return states;
        }

        /// <summary>
        /// Compare current transform states against a previously captured snapshot and
        /// return a list of (path, stowed, deployed) tuples for transforms that changed
        /// beyond the threshold.  Thresholds: pos sqrMag > 0.0001, rot angle > 0.01,
        /// scale sqrMag > 0.0001.
        /// </summary>
        private static List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)> CollectTransformDeltas(
            Transform[] allTransforms, Transform root,
            Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)> stowedStates)
        {
            var result = new List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>();
            for (int i = 0; i < allTransforms.Length; i++)
            {
                string path = GetTransformPath(allTransforms[i], root);
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

        /// <summary>
        /// Resolve a source bone transform to its ghost counterpart.
        /// Lookup order: cloneMap direct hit, then MirrorTransformChain from modelRoot,
        /// then MirrorTransformChain from prefab.transform (EVA rig fallback).
        /// Sets <paramref name="usedPartRootFallback"/> to true when the prefab fallback path is taken.
        /// </summary>
        private static Transform ResolveGhostBone(
            Transform srcBone, Transform modelRoot, Transform modelNodeTransform,
            Transform partRootTransform, Part prefab,
            Dictionary<Transform, Transform> cloneMap, ref bool usedPartRootFallback)
        {
            Transform ghostBone;
            if (!cloneMap.TryGetValue(srcBone, out ghostBone))
            {
                if (IsDescendantOf(srcBone, modelRoot))
                {
                    ghostBone = MirrorTransformChain(srcBone, modelRoot, modelNodeTransform, cloneMap);
                }
                else if (IsDescendantOf(srcBone, prefab.transform))
                {
                    ghostBone = MirrorTransformChain(srcBone, prefab.transform, partRootTransform, cloneMap);
                    usedPartRootFallback = true;
                }
            }
            return ghostBone;
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
            ParsekLog.VerboseRateLimited("GhostVisual", $"hierarchy_{partName}_{depth}_{t.name}",
                $"    HIERARCHY {partName}: {indent}{t.name}{compStr}{activeStr}", 60.0);
            for (int i = 0; i < t.childCount; i++)
                DumpTransformHierarchy(t.GetChild(i), depth + 1, partName);
        }


        /// <summary>
        /// Builds the smoke child particle system for SpawnExplosionFx.
        /// Returns the created ParticleSystem, or null if the shader was not found.
        /// </summary>
        private static ParticleSystem BuildExplosionSmokeChild(
            GameObject parentObj, float vesselLength)
        {
            Shader alphaShader = Shader.Find("KSP/Particles/Alpha Blended");
            if (alphaShader == null)
            {
                ParsekLog.Warn("GhostVisual", "BuildExplosionSmokeChild shader 'KSP/Particles/Alpha Blended' not found — skipping smoke child");
                return null;
            }

            var smokeObj = new GameObject("Smoke");
            smokeObj.transform.SetParent(parentObj.transform, false);

            var smokePs = smokeObj.AddComponent<ParticleSystem>();
            var smokeMain = smokePs.main;
            smokeMain.simulationSpace = ParticleSystemSimulationSpace.World;
            smokeMain.startLifetime = new ParticleSystem.MinMaxCurve(2.0f, 3.5f);
            smokeMain.startSpeed = new ParticleSystem.MinMaxCurve(vesselLength * 0.25f, vesselLength * 1f);
            smokeMain.startSize = new ParticleSystem.MinMaxCurve(vesselLength * 0.3f, vesselLength * 0.8f);
            smokeMain.maxParticles = 400;
            smokeMain.playOnAwake = false;
            smokeMain.loop = false;
            smokeMain.gravityModifier = 0.03f;
            smokeMain.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.3f, 0.3f, 0.3f, 0.6f),
                new Color(0.15f, 0.12f, 0.1f, 0.5f));

            var smokeShape = smokePs.shape;
            smokeShape.shapeType = ParticleSystemShapeType.Sphere;
            smokeShape.radius = vesselLength * 0.3f;

            var smokeEmission = smokePs.emission;
            smokeEmission.enabled = true;
            smokeEmission.rateOverTime = 0f;
            smokeEmission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0.1f, 160, 240)
            });

            var smokeColor = smokePs.colorOverLifetime;
            smokeColor.enabled = true;
            var smokeGradient = new Gradient();
            smokeGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.4f, 0.35f, 0.3f), 0f),
                    new GradientColorKey(new Color(0.25f, 0.22f, 0.2f), 0.4f),
                    new GradientColorKey(new Color(0.15f, 0.13f, 0.12f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.0f, 0f),
                    new GradientAlphaKey(0.5f, 0.1f),
                    new GradientAlphaKey(0.4f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            smokeColor.color = smokeGradient;

            var smokeSize = smokePs.sizeOverLifetime;
            smokeSize.enabled = true;
            smokeSize.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.5f),
                    new Keyframe(0.3f, 1.5f),
                    new Keyframe(1f, 3.0f)));

            var smokeNoise = smokePs.noise;
            smokeNoise.enabled = true;
            smokeNoise.strength = vesselLength * 0.2f;
            smokeNoise.frequency = 1.5f;
            smokeNoise.scrollSpeed = 0.8f;
            smokeNoise.damping = true;

            var smokeRenderer = smokeObj.GetComponent<ParticleSystemRenderer>();
            smokeRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            smokeRenderer.maxParticleSize = 12f;

            if (cachedExplosionTexture == null)
                cachedExplosionTexture = CreateSoftCircleTexture(32);
            var smokeMat = new Material(alphaShader);
            smokeMat.mainTexture = cachedExplosionTexture;
            smokeMat.SetColor("_TintColor", new Color(0.3f, 0.25f, 0.2f, 0.5f));
            smokeRenderer.material = smokeMat;

            var smokeCleanup = smokeObj.AddComponent<MaterialCleanup>();
            smokeCleanup.material = smokeMat;

            return smokePs;
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
                    continue;
                }

                ConfigNode effectsNode = partConfig.GetNode("EFFECTS");
                if (effectsNode == null)
                {
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
                        TryParseFxLocalRotation(mmpNodes[mp].GetValue("localRotation"), out localRotation);

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
                        continue;
                    }

                    for (int t = 0; t < fxTransforms.Count; t++)
                    {
                        Transform srcFxTransform = fxTransforms[t];

                        Transform ghostFxParent = ResolveGhostFxParent(
                            srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                        if (ghostFxParent == null)
                        {
                            ParsekLog.Verbose("GhostVisual", $"RCS '{partName}' midx={moduleIndex}: " +
                                $"transform='{transformName}' parent resolution failed");
                            continue;
                        }
                        LogFxParentAlignmentDiagnostic(
                            partName, moduleIndex, "RCS_MODEL_MULTI_PARTICLE", transformName,
                            prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent);

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

                                StripKspFxControllers(fxInstance, info.kspEmitters);

                                // Configure ALL particle systems in the FX hierarchy (not just the first).
                                // KSP RCS FX models have multiple child systems (plume + glow/smoke).
                                // Using singular GetComponentInChildren only stopped the first one —
                                // the rest kept playOnAwake=true and auto-played on ghost activation.
                                var allPs = fxInstance.GetComponentsInChildren<ParticleSystem>(true);
                                for (int p = 0; p < allPs.Length; p++)
                                {
                                    var ps = allPs[p];
                                    var main = ps.main;
                                    main.playOnAwake = false;
                                    main.prewarm = false;

                                    // Only disable Unity emission when KSPParticleEmitter is present
                                    // (same logic as engine FX — smoke FX need Unity emission).
                                    bool hasKspEmitter = fxInstance.GetComponent("KSPParticleEmitter") != null;
                                    var emission = ps.emission;
                                    if (hasKspEmitter)
                                        emission.enabled = false;
                                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                    ps.Clear(true);

                                    // Disable renderer at build time (matching engine FX pattern).
                                    // SetRcsEmission re-enables renderers when RCS events fire.
                                    var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
                                    if (psRenderer != null)
                                        psRenderer.enabled = raiseRcsVisualOnly;

                                    info.particleSystems.Add(ps);
                                }
                                if (allPs.Length > 0)
                                {
                                    LogFxInstancePlacementDiagnostic(
                                        partName,
                                        moduleIndex,
                                        "RCS_MODEL_MULTI_PARTICLE",
                                        transformName,
                                        modelName,
                                        prefab.transform,
                                        ghostModelNode,
                                        srcFxTransform,
                                        ghostFxParent,
                                        fxInstance.transform,
                                        fxDefinitions[f].localOffset,
                                        fxDefinitions[f].localRotation,
                                        true);
                                }
                            }
                        }
                    }
                }

                if (info.particleSystems.Count > 0)
                {
                    result.Add(info);
                }
            }

            return result.Count > 0 ? result : null;
        }

        internal static List<Transform> FindTransformsRecursive(Transform parent, string name)
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

        /// <summary>
        /// Extracts damagedTransformName values from all ModuleWheelDamage MODULE nodes
        /// in the given part config. Returns an empty set if no damage modules exist or
        /// if none specify a damagedTransformName.
        /// </summary>
        internal static HashSet<string> GetDamagedWheelTransformNames(ConfigNode partConfig)
        {
            var names = new HashSet<string>();
            if (partConfig == null) return names;

            var modules = partConfig.GetNodes("MODULE");
            if (modules == null) return names;

            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") != "ModuleWheelDamage")
                    continue;
                string damagedName = modules[i].GetValue("damagedTransformName");
                if (!string.IsNullOrEmpty(damagedName))
                    names.Add(damagedName);
            }
            return names;
        }

        /// <summary>
        /// Extracts rootObject values from all ModuleStructuralNode MODULE nodes
        /// in the given part config. These are the internal truss/cap transforms
        /// that should be hidden when procedural fairings are intact.
        /// Returns an empty set if no structural node modules exist.
        /// </summary>
        internal static HashSet<string> GetFairingInternalStructureNames(ConfigNode partConfig)
        {
            var names = new HashSet<string>();
            if (partConfig == null) return names;

            var modules = partConfig.GetNodes("MODULE");
            if (modules == null) return names;

            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") != "ModuleStructuralNode")
                    continue;
                string rootObject = modules[i].GetValue("rootObject");
                if (!string.IsNullOrEmpty(rootObject))
                    names.Add(rootObject);
            }
            return names;
        }

        /// <summary>
        /// Finds already-cloned transforms in the ghost model hierarchy that match
        /// fairing internal structure names (truss/cap objects from ModuleStructuralNode)
        /// and permanently hides them. Prefab Cap/Truss meshes are at placeholder scale
        /// (2000,10,2000) — the procedural truss mesh replaces them visually.
        /// </summary>
        internal static void HideFairingInternalStructure(
            FairingGhostInfo fairingInfo, ConfigNode partConfig,
            Transform ghostModelNode, uint persistentId, string partName)
        {
            if (fairingInfo == null || ghostModelNode == null) return;

            HashSet<string> structureNames = GetFairingInternalStructureNames(partConfig);
            if (structureNames.Count == 0)
            {
                return;
            }

            // Permanently hide prefab Cap/Truss clones — they are at meaningless prefab default scale
            // (2000,10,2000). The procedural truss mesh on FairingGhostInfo.trussStructureObject
            // provides the correct post-jettison visual instead.
            var hidden = new List<GameObject>();
            CollectMatchingTransforms(ghostModelNode, structureNames, hidden);

            for (int i = 0; i < hidden.Count; i++)
                hidden[i].SetActive(false);

        }

        /// <summary>
        /// Recursively walks a transform hierarchy collecting GameObjects whose name
        /// matches one of the given names.
        /// </summary>
        private static void CollectMatchingTransforms(Transform root, HashSet<string> names, List<GameObject> results)
        {
            if (root == null) return;
            if (names.Contains(root.name))
                results.Add(root.gameObject);
            for (int i = 0; i < root.childCount; i++)
                CollectMatchingTransforms(root.GetChild(i), names, results);
        }

        /// <summary>
        /// Checks whether a renderer's transform (or any of its ancestors up to but not
        /// including the hierarchy root) has a name matching one of the damaged wheel
        /// transform names. The damaged mesh may be a child of the named transform.
        /// </summary>
        internal static bool IsRendererOnDamagedTransform(Transform rendererTransform, HashSet<string> damagedNames)
        {
            if (rendererTransform == null || damagedNames == null || damagedNames.Count == 0)
                return false;

            Transform cur = rendererTransform;
            while (cur != null)
            {
                if (damagedNames.Contains(cur.name))
                    return true;
                cur = cur.parent;
            }
            return false;
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

        internal static bool TryFindSelectedVariantNode(
            Part prefab, ConfigNode partNode,
            out ConfigNode selectedVariantNode, out string selectedVariantName)
        {
            selectedVariantNode = null;
            selectedVariantName = null;

            if (prefab == null || prefab.partInfo == null || prefab.partInfo.partConfig == null)
                return false;

            ConfigNode variantModuleConfig = FindModuleNode(prefab.partInfo.partConfig, "ModulePartVariants");
            if (variantModuleConfig == null)
                return false;

            string requestedVariantName = null;
            bool variantFromSnapshot = false;

            // Prefer explicit variant saved on the part snapshot when present.
            ConfigNode snapshotVariantModule = FindModuleNode(partNode, "ModulePartVariants");
            requestedVariantName = FirstNonEmptyConfigValue(
                snapshotVariantModule,
                "currentVariant",
                "selectedVariant",
                "variantName",
                "moduleVariantName");
            variantFromSnapshot = !string.IsNullOrEmpty(requestedVariantName);

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

            string selectedLower = !string.IsNullOrEmpty(requestedVariantName)
                ? requestedVariantName.ToLowerInvariant()
                : null;
            string baseLower = !string.IsNullOrEmpty(baseVariantName)
                ? baseVariantName.ToLowerInvariant()
                : null;

            string resolutionPath = "first-fallback";

            for (int i = 0; i < variantNodes.Length; i++)
            {
                string name = FirstNonEmptyConfigValue(variantNodes[i], "name");
                if (string.IsNullOrEmpty(name))
                    continue;

                string lower = name.ToLowerInvariant();
                if (!string.IsNullOrEmpty(selectedLower) && lower == selectedLower)
                {
                    selectedVariantNode = variantNodes[i];
                    resolutionPath = variantFromSnapshot ? "snapshot" : "runtime";
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
                        resolutionPath = "base";
                        break;
                    }
                }
            }

            if (selectedVariantNode == null)
                selectedVariantNode = variantNodes[0];

            selectedVariantName = FirstNonEmptyConfigValue(selectedVariantNode, "name") ?? "<unnamed>";
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

            if (!TryFindSelectedVariantNode(prefab, partNode, out ConfigNode selectedVariantNode, out selectedVariantName))
                return false;

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

        internal static bool TryGetSelectedVariantTextureRules(
            Part prefab, ConfigNode partNode,
            out string selectedVariantName, out List<VariantTextureRule> textureRules)
        {
            selectedVariantName = null;
            textureRules = null;

            if (!TryFindSelectedVariantNode(prefab, partNode, out ConfigNode selectedVariantNode, out selectedVariantName))
                return false;

            ConfigNode[] textureNodes = selectedVariantNode.GetNodes("TEXTURE");
            if (textureNodes == null || textureNodes.Length == 0)
                return false;

            var rules = new List<VariantTextureRule>();
            for (int t = 0; t < textureNodes.Length; t++)
            {
                var texNode = textureNodes[t];
                var rule = new VariantTextureRule
                {
                    materialName = texNode.GetValue("materialName"),
                    shaderName = texNode.GetValue("shader"),
                    transformName = texNode.GetValue("transformName"),
                    properties = new List<(string key, string value)>()
                };

                for (int v = 0; v < texNode.values.Count; v++)
                {
                    var val = texNode.values[v];
                    if (val == null || string.IsNullOrEmpty(val.name))
                        continue;

                    string key = val.name.Trim();
                    if (key == "shader" || key == "materialName" || key == "transformName")
                        continue;

                    string value = val.value != null ? val.value.Trim() : "";
                    rule.properties.Add((key, value));
                }

                rules.Add(rule);
            }

            if (rules.Count == 0)
                return false;

            textureRules = rules;
            return true;
        }

        internal static VariantPropertyType ClassifyVariantProperty(string key)
        {
            if (string.IsNullOrEmpty(key))
                return VariantPropertyType.Skip;

            if (key == "shader" || key == "materialName" || key == "transformName")
                return VariantPropertyType.Skip;

            if (key.EndsWith("URL", System.StringComparison.Ordinal))
                return VariantPropertyType.Texture;

            if (key == "_BumpMap" || key == "_Emissive" || key == "_SpecMap")
                return VariantPropertyType.Texture;

            if (key == "_Shininess" || key == "_Opacity" || key == "_RimFalloff" ||
                key == "_AmbientMultiplier" || key == "_TemperatureColor")
                return VariantPropertyType.Float;

            if (key == "color" || key == "_Color" ||
                key.IndexOf("Color", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return VariantPropertyType.Color;

            return VariantPropertyType.Skip;
        }

        internal static bool DoesRendererMatchTextureRule(
            string materialName, string transformName, VariantTextureRule rule)
        {
            if (!string.IsNullOrEmpty(rule.materialName))
            {
                if (string.IsNullOrEmpty(materialName))
                    return false;
                // StartsWith because Unity appends " (Instance)" to cloned material names
                if (!materialName.StartsWith(rule.materialName, System.StringComparison.Ordinal))
                    return false;
            }

            if (!string.IsNullOrEmpty(rule.transformName))
            {
                if (string.IsNullOrEmpty(transformName))
                    return false;
                if (transformName != rule.transformName)
                    return false;
            }

            return true;
        }

        internal static string TextureUrlToShaderProperty(string key)
        {
            if (key == "_BumpMap" || key == "_Emissive" || key == "_SpecMap")
                return key;

            if (key.EndsWith("URL", System.StringComparison.Ordinal))
            {
                string stripped = key.Substring(0, key.Length - 3);
                if (stripped.Length == 0)
                    return "_MainTex";

                // KSP shader properties use abbreviated "Tex" suffix (e.g., _MainTex, _BackTex)
                // so strip "ture" (4 chars) from "Texture" to keep "Tex"
                if (stripped.EndsWith("Texture", System.StringComparison.Ordinal))
                    stripped = stripped.Substring(0, stripped.Length - 4);

                return "_" + char.ToUpperInvariant(stripped[0]) + stripped.Substring(1);
            }

            return key;
        }

        internal static int ApplyVariantTextureRules(
            Transform modelRoot, List<VariantTextureRule> rules,
            string partName, uint persistentId)
        {
            if (modelRoot == null || rules == null || rules.Count == 0)
                return 0;

            var renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return 0;

            int totalApplied = 0;

            for (int ri = 0; ri < renderers.Length; ri++)
            {
                Renderer renderer = renderers[ri];
                if (renderer == null) continue;

                Material[] mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                string rendererTransformName = renderer.transform.name;
                bool anySlotChanged = false;

                for (int mi = 0; mi < mats.Length; mi++)
                {
                    Material mat = mats[mi];
                    if (mat == null) continue;

                    string matName = mat.name;

                    for (int ruleIdx = 0; ruleIdx < rules.Count; ruleIdx++)
                    {
                        var rule = rules[ruleIdx];

                        if (!DoesRendererMatchTextureRule(matName, rendererTransformName, rule))
                            continue;

                        Material cloned = new Material(mat);
                        mats[mi] = cloned;
                        mat = cloned;
                        anySlotChanged = true;

                        if (!string.IsNullOrEmpty(rule.shaderName))
                        {
                            Shader shader = Shader.Find(rule.shaderName);
                            // Fallback: some KSP shaders (e.g., "KSP/Emissive Specular") aren't
                            // findable by Shader.Find but exist on prefab materials. Search the
                            // ghost renderers for a material that already has this shader.
                            if (shader == null)
                                shader = FindShaderOnRenderers(renderers, rule.shaderName);
                            if (shader != null)
                            {
                                cloned.shader = shader;
                            }
                            else
                            {
                                ParsekLog.VerboseRateLimited("GhostVisual",
                                    $"shader-notfound-{rule.shaderName}",
                                    $"Part '{partName}' pid={persistentId}: shader not found: '{rule.shaderName}' (not in Shader.Find or any renderer material)");
                            }
                        }

                        if (rule.properties != null)
                        {
                            for (int pi = 0; pi < rule.properties.Count; pi++)
                            {
                                var prop = rule.properties[pi];
                                var propType = ClassifyVariantProperty(prop.key);

                                switch (propType)
                                {
                                    case VariantPropertyType.Texture:
                                    {
                                        string shaderProp = TextureUrlToShaderProperty(prop.key);
                                        bool isNormalMap = prop.key == "_BumpMap";
                                        Texture2D tex = GameDatabase.Instance.GetTexture(prop.value, isNormalMap);
                                        if (tex != null)
                                        {
                                            cloned.SetTexture(shaderProp, tex);
                                            totalApplied++;
                                        }
                                        else
                                        {
                                            ParsekLog.Warn("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                                                $"texture not found: '{prop.value}'");
                                        }
                                        break;
                                    }
                                    case VariantPropertyType.Color:
                                    {
                                        string colorProp = prop.key == "color" ? "_Color" : prop.key;
                                        if (TryParseKspColor(prop.value, out Color color))
                                        {
                                            cloned.SetColor(colorProp, color);
                                            totalApplied++;
                                        }
                                        else
                                        {
                                            ParsekLog.Warn("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                                                $"failed to parse color: {prop.key}={prop.value}");
                                        }
                                        break;
                                    }
                                    case VariantPropertyType.Float:
                                    {
                                        if (float.TryParse(prop.value, NumberStyles.Float,
                                            CultureInfo.InvariantCulture, out float floatVal))
                                        {
                                            cloned.SetFloat(prop.key, floatVal);
                                            totalApplied++;
                                        }
                                        else
                                        {
                                            ParsekLog.Warn("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                                                $"failed to parse float: {prop.key}={prop.value}");
                                        }
                                        break;
                                    }
                                    case VariantPropertyType.Skip:
                                        break;
                                }
                            }
                        }

                        break;
                    }
                }

                if (anySlotChanged)
                    renderer.sharedMaterials = mats;
            }

            return totalApplied;
        }

        /// <summary>
        /// Fallback shader lookup: searches renderer materials for a shader matching by name.
        /// Handles KSP shaders (e.g., "KSP/Emissive Specular") that exist on loaded materials
        /// but aren't findable via Shader.Find().
        /// </summary>
        private static Shader FindShaderOnRenderers(Renderer[] renderers, string shaderName)
        {
            if (renderers == null || string.IsNullOrEmpty(shaderName)) return null;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                var mats = renderers[i].sharedMaterials;
                if (mats == null) continue;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] != null && mats[m].shader != null &&
                        string.Equals(mats[m].shader.name, shaderName, System.StringComparison.Ordinal))
                        return mats[m].shader;
                }
            }
            return null;
        }

        internal static bool TryParseKspColor(string raw, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(raw))
                return false;

            string trimmed = raw.Trim();

            if (trimmed.StartsWith("#", System.StringComparison.Ordinal))
                return ColorUtility.TryParseHtmlString(trimmed, out color);

            string[] parts = trimmed.Split(',');
            if (parts.Length < 3)
                return false;

            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
                return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g))
                return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                return false;

            float a = 1f;
            if (parts.Length >= 4)
                float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a);

            color = new Color(r, g, b, a);
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

            // Connect last ring to apex (pointy nosecone)
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

            // Conical cap for truncated cone (top ring has non-zero radius, e.g. capRadius).
            // Creates a pointed cone from the last ring to an apex above it, matching the
            // shape of a real fairing nosecone. Apex height = lastR * 1.5 above last ring.
            if (!hasApex && ringsToGenerate > 0)
            {
                float capH = sections[ringCount - 1].h;
                float capR = sections[ringCount - 1].r;
                float coneApexH = capH + capR * 1.5f;
                Vector3 coneApex = axisRot * new Vector3(0f, coneApexH, 0f) + pivot;
                int coneApexIdx = vertices.Count;
                vertices.Add(coneApex);
                uvs.Add(new Vector2(0.5f, 1f));

                int lastRingBase = (ringsToGenerate - 1) * vertsPerRing;
                for (int s = 0; s < nSides; s++)
                {
                    int bl = lastRingBase + s;
                    int br = lastRingBase + s + 1;

                    triangles.Add(bl);
                    triangles.Add(coneApexIdx);
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

            // Use the base adapter's material (includes variant textures) instead of hardcoded gray.
            // The first active MeshRenderer on the ghost model node is the base adapter mesh
            // (Cap/Truss are further down in the hierarchy).
            var mr = go.AddComponent<MeshRenderer>();
            var baseMr = modelNode.GetComponentInChildren<MeshRenderer>();
            if (baseMr != null && baseMr.sharedMaterial != null)
            {
                mr.sharedMaterial = baseMr.sharedMaterial;
            }
            else
            {
                mr.material = new Material(Shader.Find("KSP/Diffuse"))
                {
                    color = new Color(0.85f, 0.85f, 0.85f)
                };
            }

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
                {
                    return true;
                }
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
                    continue;
                }

                Transform sourceServo = prefab.FindModelTransform(servoTransformName);
                if (sourceServo == null)
                {
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

                    Color mediumColor = Color.Lerp(coldColor, hotColor, 0.5f);
                    Color mediumEmission = Color.Lerp(coldEmission, hotEmission, 0.5f);

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
                return materialStates;
            }

            return null;
        }

        #region Extracted helpers for AddPartVisuals

        /// <summary>
        /// Detects ModuleParachute on the prefab, clones canopy/cap transforms into the ghost,
        /// and builds a ParachuteGhostInfo with sampled semi-deployed and deployed states.
        /// Returns null if the part has no parachute or the canopy transform could not be resolved.
        /// </summary>
        private static ParachuteGhostInfo TryBuildParachuteInfo(
            Part prefab, string partName, uint persistentId,
            Dictionary<Transform, Transform> cloneMap, Transform modelRoot,
            Transform partRootTransform, ref int meshCount, ref bool added)
        {
            ModuleParachute chute = prefab.FindModuleImplementing<ModuleParachute>();
            if (chute == null)
                return null;

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

                    GameObject subNode = new GameObject(canopyVisualRoot.name);
                    subNode.transform.SetParent(partRootTransform, false);
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
                        }
                        else
                        {
                            // No mesh directly on the transform — still mirror the chain
                            // so it enters cloneMap for lookup
                            MirrorTransformChain(target, canopyVisualRoot,
                                subNode.transform, cloneMap);
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
                var (sdScale, sdPos, sdRot, semiOk, dScale, dPos, dRot) = SampleCanopyStates(prefab, chute);
                var parachuteInfo = new ParachuteGhostInfo
                {
                    partPersistentId = persistentId,
                    canopyTransform = ghostCanopy,
                    capTransform = ghostCap,
                    deployedCanopyScale = dScale,
                    deployedCanopyPos = dPos,
                    deployedCanopyRot = dRot,
                    semiDeployedCanopyScale = sdScale,
                    semiDeployedCanopyPos = sdPos,
                    semiDeployedCanopyRot = sdRot,
                    semiDeployedSampled = semiOk
                };

                // Stowed = invisible (canopy mesh starts at zero scale in KSP prefabs)
                ghostCanopy.localScale = Vector3.zero;

                if (canopySubtreeRoot != null && partName.StartsWith("kerbalEVA"))
                {
                    // EVA deploy animation (fullyDeploySmall) only captures chute-module
                    // movement — the kerbal body pose change that swings the backpack
                    // overhead is a separate animation we can't sample. Override with a
                    // position above the kerbal's head and dome-down rotation.
                    ghostCanopy.SetParent(partRootTransform, false);
                    ghostCanopy.localPosition = Vector3.zero;
                    ghostCanopy.localRotation = Quaternion.identity;
                    ghostCanopy.localScale = Vector3.zero;
                    // EVA: both semi-deployed and deployed appear above the kerbal's head
                    parachuteInfo.semiDeployedCanopyPos = new Vector3(0f, 1f, 0f);
                    parachuteInfo.semiDeployedCanopyRot = Quaternion.Euler(270f, 0f, 0f);
                    parachuteInfo.deployedCanopyPos = new Vector3(0f, 1f, 0f);
                    parachuteInfo.deployedCanopyRot = Quaternion.Euler(270f, 0f, 0f);
                }
                else if (canopySubtreeRoot != null)
                {
                    // Non-EVA part with canopy outside modelRoot: reparent under
                    // subtree root so root-relative positions work.
                    ghostCanopy.SetParent(canopySubtreeRoot, false);
                    ghostCanopy.localPosition = Vector3.zero;
                    ghostCanopy.localRotation = Quaternion.identity;
                }

                return parachuteInfo;
            }
            else if (srcCanopy != null)
            {
            }

            return null;
        }

        /// <summary>
        /// Samples heat animation states and builds material states for ModuleAnimateHeat,
        /// FXModuleAnimateThrottle, or FXModuleAnimateRCS. Resolves sampled transforms onto
        /// the ghost model node and initializes materials to cold state.
        /// Returns null if no heat transforms or materials were resolved.
        /// </summary>
        private static HeatGhostInfo TryBuildHeatInfo(
            Part prefab, string heatAnimName, string heatSource,
            Transform modelNodeTransform, string partName, uint persistentId)
        {
            List<HeatTransformState> resolvedHeatTransforms = null;
            var sampledHeatStates = SampleAnimateHeatStates(prefab, heatAnimName);
            if (sampledHeatStates != null && sampledHeatStates.Count > 0)
            {
                resolvedHeatTransforms = new List<HeatTransformState>();
                int unresolved = 0;
                for (int s = 0; s < sampledHeatStates.Count; s++)
                {
                    var (path, coldPos, coldRot, coldScale, medPos, medRot, medScale, hotPos, hotRot, hotScale) = sampledHeatStates[s];
                    Transform ghostT = FindTransformByPath(modelNodeTransform, path);
                    if (ghostT != null)
                    {
                        resolvedHeatTransforms.Add(new HeatTransformState
                        {
                            t = ghostT,
                            coldPos = coldPos,
                            coldRot = coldRot,
                            coldScale = coldScale,
                            mediumPos = medPos,
                            mediumRot = medRot,
                            mediumScale = medScale,
                            hotPos = hotPos,
                            hotRot = hotRot,
                            hotScale = hotScale
                        });
                    }
                    else
                    {
                        unresolved++;
                        if (unresolved <= 5)
                            ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                                $"    [DIAG] {heatSource} '{partName}': unresolved path '{path}'", 60.0);
                    }
                }
            }

            List<HeatMaterialState> heatMaterialStates =
                BuildHeatMaterialStates(modelNodeTransform, partName, persistentId, resolvedHeatTransforms);

            if ((resolvedHeatTransforms != null && resolvedHeatTransforms.Count > 0) ||
                (heatMaterialStates != null && heatMaterialStates.Count > 0))
            {
                var heatInfo = new HeatGhostInfo
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

                return heatInfo;
            }
            return null;
        }

        /// <summary>
        /// Runs the deployable animation cascade: tries ModuleDeployablePart, gear, ladder,
        /// animation-group, animate-generic, aero-surface, control-surface, robot-arm-scanner,
        /// and cargo-bay detection in priority order. Returns the first successfully resolved
        /// DeployableGhostInfo, or null if none matched.
        /// </summary>
        private static DeployableGhostInfo TryBuildDeployableInfo(
            Part prefab, uint persistentId, string partName,
            Transform modelRoot, Transform modelNodeTransform,
            Dictionary<Transform, Transform> cloneMap,
            bool hasRetractableLadder, string ladderAnimName, string ladderAnimRootName,
            bool hasAnimationGroupDeploy, string animationGroupDeployAnimName,
            bool hasStandaloneAnimateGenericDeploy, string standaloneAnimateGenericAnimName,
            bool hasAeroSurfaceDeploy, string aeroSurfaceTransformName, float aeroSurfaceDeployAngle,
            bool hasControlSurfaceDeploy, string controlSurfaceTransformName, float controlSurfaceDeployAngle,
            bool hasRobotArmScannerDeploy, List<string> robotArmScannerAnimCandidates)
        {
            // Detect deployable parts (solar panels, antennas, radiators) and pre-resolve transform states
            DeployableGhostInfo deployableInfo = null;
            ModuleDeployablePart deployable = prefab.FindModuleImplementing<ModuleDeployablePart>();
            if (deployable != null)
            {
                var sampledStates = SampleDeployableStates(prefab, deployable);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "Deployable", logUnresolved: true);
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
                    deployableInfo = ResolveSampledStatesToDeployableInfo(
                        sampledStates, modelNodeTransform, persistentId, partName, "Gear", logUnresolved: false);
                    break; // one deployment module per part
                }
            }
            else if (prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null)
            {
            }

            // Detect stock retractable ladders (RetractableLadder module) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasRetractableLadder)
            {
                var sampledStates = SampleLadderStates(prefab, ladderAnimName, ladderAnimRootName);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "Ladder", logUnresolved: true);
            }

            // Detect ModuleAnimationGroup deploy animations (e.g. drills) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasAnimationGroupDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, animationGroupDeployAnimName, null);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "AnimationGroup", logUnresolved: true);
            }

            // Detect standalone ModuleAnimateGeneric deploy animations (e.g. science/inflatable parts)
            if (deployableInfo == null && hasStandaloneAnimateGenericDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, standaloneAnimateGenericAnimName, null);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "AnimateGeneric", logUnresolved: true);
            }

            // Detect ModuleAeroSurface visual transforms (airbrakes/control surfaces).
            if (deployableInfo == null && hasAeroSurfaceDeploy)
            {
                deployableInfo = BuildSurfaceDeployableInfo(
                    modelRoot, modelNodeTransform, cloneMap,
                    aeroSurfaceTransformName, aeroSurfaceDeployAngle,
                    persistentId, partName, "AeroSurface");
            }

            // Detect ModuleControlSurface visual transforms (elevons/rudders/prop blades).
            if (deployableInfo == null && hasControlSurfaceDeploy)
            {
                deployableInfo = BuildSurfaceDeployableInfo(
                    modelRoot, modelNodeTransform, cloneMap,
                    controlSurfaceTransformName, controlSurfaceDeployAngle,
                    persistentId, partName, "ControlSurface");
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
                    if (candidateCount > bestCount)
                    {
                        bestCount = candidateCount;
                        sampledStates = candidateStates;
                        selectedAnimName = candidateAnim;
                    }
                }

                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "RobotArmScanner", logUnresolved: true);
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
                    }

                    if (animModule != null && !string.IsNullOrEmpty(animModule.animationName))
                    {
                        var sampledStates = SampleCargoBayStates(
                            prefab, animModule.animationName, cargoBay.closedPosition);
                        deployableInfo = ResolveSampledStatesToDeployableInfo(
                            sampledStates, modelNodeTransform, persistentId, partName, "CargoBay", logUnresolved: true);

                        if (deployableInfo != null)
                        {
                            // Snap ghost to closed (stowed) state at build time.
                            // Mk3 cargo bays have closedPosition=1 so prefab default (animTime=0) is OPEN.
                            // Without this snap, the ghost would show open doors until the first event.
                            var resolvedTransforms = deployableInfo.transforms;
                            for (int i = 0; i < resolvedTransforms.Count; i++)
                            {
                                var ts = resolvedTransforms[i];
                                if (ts.t == null) continue;
                                ts.t.localPosition = ts.stowedPos;
                                ts.t.localRotation = ts.stowedRot;
                                ts.t.localScale = ts.stowedScale;
                            }

                        }
                    }
                }
            }

            return deployableInfo;
        }

        /// <summary>
        /// Resolves sampled animation states to ghost transforms and builds a DeployableGhostInfo.
        /// Deduplicates the repeated resolve-loop pattern used by deployable, gear, ladder,
        /// animation-group, animate-generic, and robot-arm-scanner detection paths.
        /// Returns null if sampledStates is null or no transforms could be resolved.
        /// </summary>
        private static DeployableGhostInfo ResolveSampledStatesToDeployableInfo(
            List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
                Vector3 dPos, Quaternion dRot, Vector3 dScale)> sampledStates,
            Transform modelNodeTransform,
            uint persistentId,
            string partName,
            string moduleLabel,
            bool logUnresolved)
        {
            if (sampledStates == null)
                return null;

            var resolvedTransforms = new List<DeployableTransformState>();
            int unresolved = 0;
            for (int s = 0; s < sampledStates.Count; s++)
            {
                var (path, sPos, sRot, sScale, dPos, dRot, dScale) = sampledStates[s];
                Transform ghostT = FindTransformByPath(modelNodeTransform, path);
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
                else if (logUnresolved)
                {
                    unresolved++;
                    if (unresolved <= 5)
                        ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                            $"    [DIAG] {moduleLabel} '{partName}': unresolved path '{path}'", 60.0);
                }
            }

            if (resolvedTransforms.Count > 0)
            {
                return new DeployableGhostInfo
                {
                    partPersistentId = persistentId,
                    transforms = resolvedTransforms
                };
            }

            return null;
        }

        /// <summary>
        /// Builds a LightGhostInfo by cloning Light components from the prefab model into the ghost.
        /// Returns null if no lights were found or cloned.
        /// </summary>
        private static LightGhostInfo BuildLightGhostInfo(
            Part prefab, Transform modelRoot, Transform modelNodeTransform,
            Dictionary<Transform, Transform> cloneMap,
            uint persistentId, string partName,
            bool raiseLightVisualOnly)
        {
            ModuleLight lightModule = prefab.FindModuleImplementing<ModuleLight>();
            if (lightModule == null)
                return null;

            var prefabLights = modelRoot.GetComponentsInChildren<Light>(true);
            if (prefabLights == null || prefabLights.Length == 0)
                return null;

            var clonedLights = new List<Light>();
            for (int li = 0; li < prefabLights.Length; li++)
            {
                Light srcLight = prefabLights[li];
                if (srcLight == null) continue;

                // Mirror the transform chain to find the correct ghost parent
                Transform ghostParent = MirrorTransformChain(
                    srcLight.transform, modelRoot, modelNodeTransform, cloneMap);

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
            }

            if (clonedLights.Count > 0)
            {
                return new LightGhostInfo
                {
                    partPersistentId = persistentId,
                    lights = clonedLights
                };
            }

            return null;
        }

        /// <summary>
        /// Clones SkinnedMeshRenderers from the prefab model into the ghost, preserving bone
        /// bindings and skinning quality. Returns the number of SkinnedMeshRenderers cloned.
        /// </summary>
        private static int CloneSkinnedMeshRenderers(
            SkinnedMeshRenderer[] skinnedRenderers,
            Transform modelRoot, Transform modelNodeTransform, Transform partRootTransform,
            Part prefab, Dictionary<Transform, Transform> cloneMap,
            bool filterInactiveVariantRenderers,
            bool hasVariantGameObjectRules,
            Dictionary<string, bool> selectedVariantGameObjects,
            HashSet<string> damagedWheelNames,
            uint persistentId, string partName,
            ref int meshCount, ref int damagedSkipped, ref int nullMeshSkipped)
        {
            int skinnedCloned = 0;
            int partRootFallbackCount = 0;
            for (int r = 0; r < skinnedRenderers.Length; r++)
            {
                var smr = skinnedRenderers[r];
                if (smr == null) continue;
                if (smr.sharedMesh == null)
                {
                    nullMeshSkipped++;
                    ParsekLog.Warn("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                        $"SkinnedMeshRenderer '{smr.name}' has null sharedMesh — tire mesh " +
                        $"may be procedurally generated at runtime. Ghost will be missing this mesh.");
                    continue;
                }
                if (filterInactiveVariantRenderers && !smr.gameObject.activeInHierarchy) continue;
                if (hasVariantGameObjectRules &&
                    !IsRendererEnabledByVariantRule(
                        smr.transform, modelRoot, selectedVariantGameObjects,
                        out string _, out bool _))
                    continue;
                if (IsRendererOnDamagedTransform(smr.transform, damagedWheelNames))
                {
                    damagedSkipped++;
                    continue;
                }

                Transform leaf = MirrorTransformChain(smr.transform, modelRoot, modelNodeTransform, cloneMap);

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

                        Transform ghostBone = ResolveGhostBone(
                            srcBone, modelRoot, modelNodeTransform,
                            partRootTransform, prefab, cloneMap,
                            ref usedPartRootFallbackForBones);

                        ghostBones[b] = ghostBone;
                        if (ghostBone != null)
                            resolvedBones++;
                    }
                }

                Transform ghostRootBone = null;
                Transform srcRootBone = smr.rootBone;
                if (srcRootBone != null)
                {
                    ghostRootBone = ResolveGhostBone(
                        srcRootBone, modelRoot, modelNodeTransform,
                        partRootTransform, prefab, cloneMap,
                        ref usedPartRootFallbackForBones);
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
                skinnedCloned++;
                if (usedPartRootFallbackForBones)
                    partRootFallbackCount++;
            }

            return skinnedCloned;
        }

        /// <summary>
        /// Resolves a surface-transform-based deployable (AeroSurface or ControlSurface) by finding
        /// the named transforms and computing deployed rotation from the deploy angle.
        /// Returns null if no transforms were resolved.
        /// </summary>
        private static DeployableGhostInfo BuildSurfaceDeployableInfo(
            Transform modelRoot, Transform modelNodeTransform,
            Dictionary<Transform, Transform> cloneMap,
            string transformName, float deployAngleDegrees,
            uint persistentId, string partName, string moduleLabel)
        {
            var sourceTransforms = FindTransformsRecursive(modelRoot, transformName);
            if (sourceTransforms == null || sourceTransforms.Count == 0)
            {
                return null;
            }

            var resolvedTransforms = new List<DeployableTransformState>();
            for (int i = 0; i < sourceTransforms.Count; i++)
            {
                Transform sourceTransform = sourceTransforms[i];
                if (sourceTransform == null) continue;

                Transform ghostT;
                if (!cloneMap.TryGetValue(sourceTransform, out ghostT) || ghostT == null)
                {
                    string path = GetTransformPath(sourceTransform, modelRoot);
                    ghostT = FindTransformByPath(modelNodeTransform, path);
                }
                if (ghostT == null) continue;

                Vector3 stowedPos = ghostT.localPosition;
                Quaternion stowedRot = ghostT.localRotation;
                Vector3 stowedScale = ghostT.localScale;
                Quaternion deployedRot = stowedRot * Quaternion.AngleAxis(deployAngleDegrees, Vector3.right);

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
                return new DeployableGhostInfo
                {
                    partPersistentId = persistentId,
                    transforms = resolvedTransforms
                };
            }

            return null;
        }

        #endregion

        private static bool HasCModuleLinkedMesh(ConfigNode partConfig)
        {
            if (partConfig == null) return false;
            ConfigNode[] modules = partConfig.GetNodes("MODULE");
            for (int m = 0; m < modules.Length; m++)
            {
                if (modules[m].GetValue("name") == "CModuleLinkedMesh")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Parses compound part target data from a snapshot PART node.
        /// Returns false (no-op) for non-compound parts or corrupt data.
        /// Pure method — no Unity Transform access, directly testable.
        /// </summary>
        internal static bool TryParseCompoundPartData(
            ConfigNode partNode, ConfigNode partConfig, bool isCompoundPart,
            uint persistentId, string partName,
            out CompoundPartData data)
        {
            data = new CompoundPartData
            {
                lineObjName = "obj_line",
                targetAnchorName = "obj_targetAnchor",
                targetCapName = "obj_targetCap"
            };

            // Only process parts that have CModuleLinkedMesh — avoids false positives
            // from non-compound parts that happen to have a PARTDATA node.
            if (!HasCModuleLinkedMesh(partConfig))
                return false;

            ConfigNode partData = partNode.GetNode("PARTDATA");
            if (partData == null)
            {
                // Expected no-op for non-compound parts. Log if the prefab IS a compound
                // part — that would indicate corrupt/missing snapshot data.
                if (isCompoundPart)
                {
                }
                return false;
            }

            if (!TryParseVector3(partData.GetValue("pos"), out data.targetPos))
            {
                return false;
            }

            if (!TryParseQuaternion(partData.GetValue("rot"), out data.targetRot))
            {
                data.targetRot = Quaternion.identity;
            }

            // Read transform names from CModuleLinkedMesh part config (varies: fuel line
            // uses obj_line, strut uses obj_strut).
            if (partConfig != null)
            {
                ConfigNode[] modules = partConfig.GetNodes("MODULE");
                for (int m = 0; m < modules.Length; m++)
                {
                    if (modules[m].GetValue("name") == "CModuleLinkedMesh")
                    {
                        data.lineObjName = modules[m].GetValue("lineObjName") ?? data.lineObjName;
                        data.targetAnchorName = modules[m].GetValue("targetAnchorName") ?? data.targetAnchorName;
                        data.targetCapName = modules[m].GetValue("targetCapName") ?? data.targetCapName;
                        break;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Fixes up compound part visuals (fuel lines, struts) by repositioning the
        /// target anchor and stretching the line mesh using PARTDATA from the snapshot.
        /// In KSP, CModuleLinkedMesh handles this at runtime; ghosts need manual fixup.
        /// </summary>
        private static void TryFixupCompoundPartVisuals(
            Part prefab, ConfigNode partNode, Transform partRoot,
            Dictionary<Transform, Transform> cloneMap,
            uint persistentId, string partName)
        {
            CompoundPartData data;
            if (!TryParseCompoundPartData(partNode, prefab.partInfo?.partConfig,
                    prefab is CompoundPart, persistentId, partName, out data))
                return;

            // Find source transforms on prefab, then look up ghost clones in cloneMap
            Transform srcLine = prefab.FindModelTransform(data.lineObjName);
            Transform srcTargetAnchor = prefab.FindModelTransform(data.targetAnchorName);
            Transform srcTargetCap = prefab.FindModelTransform(data.targetCapName);

            Transform ghostLine = null, ghostTargetAnchor = null, ghostTargetCap = null;
            if (srcLine != null) cloneMap.TryGetValue(srcLine, out ghostLine);
            if (srcTargetAnchor != null) cloneMap.TryGetValue(srcTargetAnchor, out ghostTargetAnchor);
            if (srcTargetCap != null) cloneMap.TryGetValue(srcTargetCap, out ghostTargetCap);

            if (ghostLine == null)
            {
                return;
            }

            // PARTDATA.pos is in compound part local space (= partRoot space).
            // Move target anchor to the correct position — endCap is a child, moves with it.
            if (ghostTargetAnchor != null)
            {
                ghostTargetAnchor.position = partRoot.TransformPoint(data.targetPos);
                ghostTargetAnchor.rotation = partRoot.rotation * data.targetRot;
            }

            // Stretch line mesh from its position to the target end.
            // Mirrors CModuleLinkedMesh.TrackAnchor: LookRotation + Z-scale.
            Vector3 lineWorldPos = ghostLine.position;
            Vector3 targetWorldPos;
            if (ghostTargetCap != null)
                targetWorldPos = ghostTargetCap.position;
            else if (ghostTargetAnchor != null)
                targetWorldPos = ghostTargetAnchor.position;
            else
                targetWorldPos = partRoot.TransformPoint(data.targetPos);

            // NOTE: delta points from target toward source (line - target), matching
            // CModuleLinkedMesh.TrackAnchor which computes (line.position - endCap.position).
            // The stock line mesh faces -Z in the prefab, so LookRotation along this reversed
            // vector produces the correct orientation. Do not "fix" by swapping operands.
            Vector3 delta = lineWorldPos - targetWorldPos;
            float distance = delta.magnitude;
            const float lineMinimumLength = 0.01f;

            if (distance > lineMinimumLength)
            {
                ghostLine.rotation = Quaternion.LookRotation(delta.normalized, partRoot.forward);
                // prefab.scaleFactor is the stock prefab value (typically rescaleFactor, default
                // 1.25). Mods like TweakScale may change scaleFactor on the live part, but the
                // prefab retains the original value — acceptable for ghost visuals.
                ghostLine.localScale = new Vector3(
                    ghostLine.localScale.x,
                    ghostLine.localScale.y,
                    distance * prefab.scaleFactor);

                if (ghostTargetCap != null)
                    ghostTargetCap.rotation = ghostLine.rotation;

            }
            else
            {
                ghostLine.gameObject.SetActive(false);
            }
        }

        private static bool AddPartVisuals(Transform root, ConfigNode partNode, Part prefab,
            uint persistentId, string partName, out int meshCount,
            out ParachuteGhostInfo parachuteInfo, out JettisonGhostInfo jettisonInfo,
            out List<EngineGhostInfo> engineInfos, out DeployableGhostInfo deployableInfo,
            out HeatGhostInfo heatInfo,
            out LightGhostInfo lightInfo, out FairingGhostInfo fairingInfo,
            out List<RcsGhostInfo> rcsInfos, out List<RoboticGhostInfo> roboticInfos,
            out List<ColorChangerGhostInfo> colorChangerInfos,
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
            colorChangerInfos = null;
            Transform modelRoot = FindModelRoot(prefab);

            // Dump full hierarchy for engine parts to diagnose missing nozzle meshes.
            // Search from the Part root (not just modelRoot) to catch siblings of "model".
            if (prefab.FindModuleImplementing<ModuleEngines>() != null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"engine_dump_{partName}",
                    $"  ENGINE PART HIERARCHY DUMP for '{partName}' pid={persistentId}:", 60.0);
                DumpTransformHierarchy(prefab.transform, 0, partName);

                // Also log MeshRenderers found from Part root vs modelRoot
                var allMR = prefab.GetComponentsInChildren<MeshRenderer>(true);
                var modelMR = modelRoot.GetComponentsInChildren<MeshRenderer>(true);
                if (allMR.Length != modelMR.Length)
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"engine_mr_warn_{partName}",
                        $"  WARNING: Part root has {allMR.Length} MeshRenderers but " +
                        $"modelRoot '{modelRoot.name}' has only {modelMR.Length}! " +
                        $"Missing renderers are OUTSIDE the model subtree.", 60.0);
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
                            ParsekLog.VerboseRateLimited("GhostVisual", $"outside_mr_{partName}_{mr.gameObject.name}",
                                $"    OUTSIDE-MODEL MR: '{mr.gameObject.name}' mesh={meshName} " +
                                $"path={GetTransformPath(mr.transform, prefab.transform)}", 60.0);
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
            ParsekLog.VerboseRateLimited("GhostVisual", $"part_summary_{partName}",
                $"  Part '{partName}' pid={persistentId}: modelRoot='{modelRoot.name}' " +
                $"modelScale={modelRoot.localScale}, " +
                $"{totalMR} MeshRenderers, {totalSMR} SkinnedMeshRenderers" +
                (hasPartVariants ? " (has ModulePartVariants)" : ""), 60.0);
            if (hasPartVariants && hasVariantGameObjectRules)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_sel_{partName}",
                    $"  Variant selection: '{selectedVariantName}' with " +
                    $"{selectedVariantGameObjects.Count} GAMEOBJECT rules", 60.0);
            }
            if (hasPartVariants && !filterInactiveVariantRenderers && !hasVariantGameObjectRules)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_fb_{partName}",
                    $"  Variant fallback: no active variant renderers and no GAMEOBJECT rules " +
                    $"— including all renderers (active MR={activeMR}, active SMR={activeSMR})", 60.0);
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
            ParsekLog.VerboseRateLimited("GhostVisual", $"part_diag_{partName}",
                $"  [DIAG] part '{partName}' modelRoot '{modelRoot.name}' localRot={modelRoot.localRotation} localPos={modelRoot.localPosition} localScale={modelRoot.localScale}", 60.0);
            cloneMap[modelRoot] = modelNode.transform;

            // For light showcase recordings, lift only light-part visuals so probes stay
            // fixed while the lamp geometry sits clearly above the probe body.
            if (raiseLightVisualOnly && prefab.FindModuleImplementing<ModuleLight>() != null)
                modelNode.transform.localPosition += new Vector3(0f, LightsShowcaseVisualYOffset, 0f);

            // Collect damaged wheel transform names from part config (ModuleWheelDamage).
            // Damaged meshes are initially inactive in the prefab but GetComponentsInChildren(true)
            // collects them. Filter them out to prevent the ghost rendering both intact and damaged
            // wheel meshes simultaneously.
            ConfigNode partConfig = prefab.partInfo?.partConfig;
            HashSet<string> damagedWheelNames = GetDamagedWheelTransformNames(partConfig);
            if (damagedWheelNames.Count > 0)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"wheel_damage_{partName}",
                    $"  Part '{partName}' pid={persistentId}: found {damagedWheelNames.Count} " +
                    $"ModuleWheelDamage damagedTransformName(s): [{string.Join(", ", damagedWheelNames)}]", 60.0);
            }

            int variantSkipped = 0;
            int variantRuleSkipped = 0;
            int damagedSkipped = 0;
            int skinnedCloned = 0;
            int nullMeshSkipped = 0;
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
                if (IsRendererOnDamagedTransform(mr.transform, damagedWheelNames))
                {
                    damagedSkipped++;
                    continue;
                }
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                Transform leaf = MirrorTransformChain(mr.transform, modelRoot, modelNode.transform, cloneMap);
                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                meshCount++;
                added = true;
            }
            if (variantSkipped > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_skip_{partName}",
                    $"  Skipped {variantSkipped} MeshRenderers on inactive variant objects", 60.0);
            if (variantRuleSkipped > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_ruleskip_{partName}",
                    $"  Skipped {variantRuleSkipped} MeshRenderers by selected variant GAMEOBJECT rules", 60.0);

            skinnedCloned = CloneSkinnedMeshRenderers(
                skinnedRenderers, modelRoot, modelNode.transform, partRoot.transform,
                prefab, cloneMap,
                filterInactiveVariantRenderers, hasVariantGameObjectRules, selectedVariantGameObjects,
                damagedWheelNames, persistentId, partName,
                ref meshCount, ref damagedSkipped, ref nullMeshSkipped);
            if (skinnedCloned > 0)
                added = true;

            // Summary log for renderer cloning results — helps diagnose missing ghost parts
            int meshCloned = meshCount - skinnedCloned;
            ParsekLog.VerboseRateLimited("GhostVisual", $"clone_summary_{partName}",
                $"  Part '{partName}' pid={persistentId}: cloned {meshCloned} MeshRenderers, " +
                $"{skinnedCloned} SkinnedMeshRenderers, skipped {nullMeshSkipped} null-mesh SMRs, " +
                $"skipped {damagedSkipped} damaged-wheel renderers", 60.0);

            if (hasPartVariants)
            {
                if (TryGetSelectedVariantTextureRules(prefab, partNode,
                    out string texVariantName, out List<VariantTextureRule> texRules))
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"variant_tex_{partName}",
                        $"Part '{partName}' pid={persistentId}: variant '{texVariantName}' has {texRules.Count} TEXTURE rules");
                    int applied = ApplyVariantTextureRules(
                        modelNode.transform, texRules, partName, persistentId);
                }
            }

            // Fix up compound part visuals (fuel lines, struts) — CModuleLinkedMesh
            // normally stretches the line mesh at runtime; ghosts need manual fixup.
            TryFixupCompoundPartVisuals(prefab, partNode, partRoot.transform,
                cloneMap, persistentId, partName);

            // Detect parachute parts via cloneMap (after cloneMap is fully populated)
            parachuteInfo = TryBuildParachuteInfo(prefab, partName, persistentId,
                cloneMap, modelRoot, partRoot.transform, ref meshCount, ref added);

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
                        continue;
                    }

                    Transform ghostJettison;
                    if (!cloneMap.TryGetValue(srcJettison, out ghostJettison) || ghostJettison == null)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", $"jettison_miss_{partName}_{jettisonName}",
                            $"    Jettison '{jettisonName}' found on prefab but not in cloneMap", 60.0);
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
            }

            // Detect engine parts and clone FX particle systems
            engineInfos = EngineFxBuilder.TryBuildEngineFX(prefab, persistentId, partName, modelRoot,
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
            string animateThrottleAnimName = null;
            bool hasAnimateThrottle = false;
            if (!hasAnimateHeat)
            {
                hasAnimateThrottle = TryGetAnimateThrottleAnimation(prefab, out animateThrottleAnimName);
            }
            string animateRcsAnimName = null;
            bool hasAnimateRcs = false;
            if (!hasAnimateHeat && !hasAnimateThrottle)
            {
                hasAnimateRcs = TryGetAnimateRcsAnimation(prefab, out animateRcsAnimName);
            }
            bool hasAnyHeatAnim = hasAnimateHeat || hasAnimateThrottle || hasAnimateRcs;
            string heatAnimName = hasAnimateHeat ? animateHeatAnimName
                : hasAnimateThrottle ? animateThrottleAnimName
                : animateRcsAnimName;
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
                hasAnyHeatAnim ||
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
                ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                    $"    EnsureFullHierarchy '{partName}': " +
                    (created > 0
                        ? $"created {created} missing intermediate transforms for animation support"
                        : $"all {allModelTransforms.Length} model transforms already in cloneMap"), 60.0);
            }

            if (hasRoboticModules)
                roboticInfos = TryBuildRoboticInfos(
                    prefab, persistentId, partName, modelRoot, modelNode.transform, cloneMap);

            // Detect deployable parts via animation cascade (solar panels, gear, ladders, etc.)
            deployableInfo = TryBuildDeployableInfo(prefab, persistentId, partName,
                modelRoot, modelNode.transform, cloneMap,
                hasRetractableLadder, ladderAnimName, ladderAnimRootName,
                hasAnimationGroupDeploy, animationGroupDeployAnimName,
                hasStandaloneAnimateGenericDeploy, standaloneAnimateGenericAnimName,
                hasAeroSurfaceDeploy, aeroSurfaceTransformName, aeroSurfaceDeployAngle,
                hasControlSurfaceDeploy, controlSurfaceTransformName, controlSurfaceDeployAngle,
                hasRobotArmScannerDeploy, robotArmScannerAnimCandidates);

            // Detect ModuleAnimateHeat / FXModuleAnimateThrottle / FXModuleAnimateRCS visual states
            if (hasAnyHeatAnim)
            {
                string heatSource = hasAnimateHeat ? "ModuleAnimateHeat"
                    : hasAnimateThrottle ? "FXModuleAnimateThrottle"
                    : "FXModuleAnimateRCS";
                heatInfo = TryBuildHeatInfo(prefab, heatAnimName, heatSource,
                    modelNode.transform, partName, persistentId);
            }

            // Detect light parts and clone Light components for ghost playback
            lightInfo = BuildLightGhostInfo(prefab, modelRoot, modelNode.transform, cloneMap,
                persistentId, partName, raiseLightVisualOnly);

            // Detect ModuleColorChanger: cabin lights (Pattern A) and heat shield char (Pattern B)
            colorChangerInfos = BuildColorChangerInfos(partNode, modelNode.transform, persistentId, partName);

            // Detect procedural fairings and generate simplified cone mesh
            fairingInfo = BuildFairingVisual(partNode, prefab, modelNode.transform, persistentId, partName);
            if (fairingInfo != null)
            {
                added = true;
                // Hide internal truss/cap structure that would poke through the fairing cone
                HideFairingInternalStructure(fairingInfo, prefab.partInfo?.partConfig,
                    modelNode.transform, persistentId, partName);
            }

            if (!added)
            {
                Object.Destroy(partRoot);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Scans part config for ModuleColorChanger instances and builds ColorChangerGhostInfo
        /// for each. Pattern A (toggleInFlight=True, _EmissiveColor) handles cabin lights.
        /// Pattern B (toggleInFlight=False, _BurnColor) handles heat shield ablation char.
        /// Returns null if no ColorChanger modules are detected.
        /// </summary>
        internal static List<ColorChangerGhostInfo> BuildColorChangerInfos(
            ConfigNode partNode, Transform ghostModelNode, uint persistentId, string partName)
        {
            if (partNode == null || ghostModelNode == null)
                return null;

            var moduleNodes = partNode.GetNodes("MODULE");
            if (moduleNodes == null || moduleNodes.Length == 0)
                return null;

            List<ColorChangerGhostInfo> results = null;

            for (int m = 0; m < moduleNodes.Length; m++)
            {
                string moduleName = moduleNodes[m].GetValue("name");
                if (moduleName != "ModuleColorChanger")
                    continue;

                string shaderProperty = moduleNodes[m].GetValue("shaderProperty");
                if (string.IsNullOrEmpty(shaderProperty))
                {
                    continue;
                }

                string toggleStr = moduleNodes[m].GetValue("toggleInFlight");
                bool toggleInFlight = false;
                if (!string.IsNullOrEmpty(toggleStr))
                    bool.TryParse(toggleStr, out toggleInFlight);

                bool isCabinLight = toggleInFlight && shaderProperty == "_EmissiveColor";
                bool isAblationChar = !toggleInFlight && shaderProperty == "_BurnColor";

                if (!isCabinLight && !isAblationChar)
                {
                    continue;
                }

                // Evaluate color curves from config to get off/on colors
                // Pattern A: off = curves at t=0 (black), on = curves at t=1 (warm glow)
                // Pattern B: off = curves at t=0 (unburnt), on = curves at t=1 (fully charred)
                Color offColor = EvaluateColorCurves(moduleNodes[m], 0f);
                Color onColor = EvaluateColorCurves(moduleNodes[m], 1f);

                // Find renderers on the ghost model that have this shader property
                var renderers = ghostModelNode.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0)
                {
                    continue;
                }

                var materialStates = CollectColorChangerMaterials(
                    renderers, shaderProperty, offColor, onColor);

                if (materialStates.Count > 0)
                {
                    if (results == null)
                        results = new List<ColorChangerGhostInfo>();

                    var info = new ColorChangerGhostInfo
                    {
                        partPersistentId = persistentId,
                        shaderProperty = shaderProperty,
                        isCabinLight = isCabinLight,
                        materials = materialStates
                    };
                    results.Add(info);

                    // Initialize to off state
                    for (int i = 0; i < materialStates.Count; i++)
                    {
                        if (materialStates[i].material != null)
                            materialStates[i].material.SetColor(shaderProperty, offColor);
                    }

                }
            }

            return results;
        }

        /// <summary>
        /// Scans all renderers for materials with the given shader property, clones matching
        /// materials, and builds ColorChangerMaterialState entries. Swaps renderer material
        /// arrays in place for renderers that had any cloned materials.
        /// </summary>
        private static List<ColorChangerMaterialState> CollectColorChangerMaterials(
            Renderer[] renderers, string shaderProperty, Color offColor, Color onColor)
        {
            var materialStates = new List<ColorChangerMaterialState>();

            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null) continue;

                // Skip particle and trail renderers
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                    continue;

                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                    continue;

                bool clonedAny = false;
                var cloned = new Material[sourceMaterials.Length];
                for (int mi = 0; mi < sourceMaterials.Length; mi++)
                {
                    Material source = sourceMaterials[mi];
                    if (source == null)
                    {
                        cloned[mi] = null;
                        continue;
                    }

                    if (!source.HasProperty(shaderProperty))
                    {
                        cloned[mi] = source;
                        continue;
                    }

                    Material materialClone = new Material(source);
                    cloned[mi] = materialClone;
                    clonedAny = true;

                    materialStates.Add(new ColorChangerMaterialState
                    {
                        material = materialClone,
                        offColor = offColor,
                        onColor = onColor
                    });
                }

                if (clonedAny)
                    renderer.materials = cloned;
            }

            return materialStates;
        }

        /// <summary>
        /// Evaluates redCurve/greenCurve/blueCurve/alphaCurve from a ModuleColorChanger config
        /// at a given time t. Returns a Color with the evaluated RGBA values.
        /// </summary>
        internal static Color EvaluateColorCurves(ConfigNode moduleNode, float t)
        {
            float r = EvaluateSingleCurve(moduleNode.GetNode("redCurve"), t);
            float g = EvaluateSingleCurve(moduleNode.GetNode("greenCurve"), t);
            float b = EvaluateSingleCurve(moduleNode.GetNode("blueCurve"), t);
            float a = EvaluateSingleCurve(moduleNode.GetNode("alphaCurve"), t, defaultValue: 1f);
            return new Color(r, g, b, a);
        }

        /// <summary>
        /// Evaluates a single FloatCurve-style ConfigNode at time t.
        /// Each key line is "time value [inTangent outTangent]".
        /// Uses simple linear interpolation between keys.
        /// </summary>
        internal static float EvaluateSingleCurve(ConfigNode curveNode, float t, float defaultValue = 0f)
        {
            if (curveNode == null)
                return defaultValue;

            string[] keys = curveNode.GetValues("key");
            if (keys == null || keys.Length == 0)
                return defaultValue;

            // Parse key entries: "time value [inTangent outTangent]"
            var parsed = new List<(float time, float value)>();
            for (int i = 0; i < keys.Length; i++)
            {
                string[] parts = keys[i].Split(new[] { ' ', '\t' },
                    System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    ParsekLog.Warn("GhostVisual", $"EvaluateSingleCurve key[{i}] has fewer than 2 parts: '{keys[i]}'");
                    continue;
                }

                float keyTime, keyValue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out keyTime) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out keyValue))
                {
                    parsed.Add((keyTime, keyValue));
                }
                else
                {
                    ParsekLog.Warn("GhostVisual", $"EvaluateSingleCurve key[{i}] failed to parse time/value: '{keys[i]}'");
                }
            }

            if (parsed.Count == 0)
                return defaultValue;

            // Sort by time
            parsed.Sort((a, b) => a.time.CompareTo(b.time));

            // Clamp to range
            if (t <= parsed[0].time)
                return parsed[0].value;
            if (t >= parsed[parsed.Count - 1].time)
                return parsed[parsed.Count - 1].value;

            // Linear interpolation between adjacent keys
            for (int i = 0; i < parsed.Count - 1; i++)
            {
                if (t >= parsed[i].time && t <= parsed[i + 1].time)
                {
                    float span = parsed[i + 1].time - parsed[i].time;
                    if (span <= 0f)
                        return parsed[i].value;
                    float fraction = (t - parsed[i].time) / span;
                    return Mathf.Lerp(parsed[i].value, parsed[i + 1].value, fraction);
                }
            }

            return parsed[parsed.Count - 1].value;
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

                // Color over lifetime: bright yellow-white → orange → deep red → fade out
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

                ParsekLog.Verbose("ReentryFx", $"combined {combines.Count} meshes into emission shape " +
                    $"({(combinedMesh != null ? combinedMesh.vertexCount : 0)} verts) for ghost #{ghostIndex}");
            }

            // --- Fire shell overlay: collect mesh+transform pairs for DrawMesh ---
            // Used to re-draw the ghost geometry offset along velocity with additive blending,
            // approximating KSP's FXCamera replacement shader vertex displacement.
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

                ParsekLog.Verbose("ReentryFx", $"{info.fireShellMeshes.Count} meshes collected for fire shell overlay on ghost #{ghostIndex}");
            }

            return info;
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
                        ? coldEmission + ReentryHotEmissionLow
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

        // Cached soft-circle texture for explosion FX (avoids per-explosion allocation in looping playback)
        private static Texture2D cachedExplosionTexture;

        /// <summary>
        /// Spawns a small smoke puff + spark burst at a part's world position.
        /// Used when a Decoupled or Destroyed part event is applied during ghost playback.
        /// Much smaller than the vessel explosion — just visual feedback for part separation.
        /// The returned GameObject auto-destroys after particles expire.
        /// </summary>
        internal static GameObject SpawnPartPuffFx(Vector3 worldPosition, float partScale)
        {
            Shader alphaShader = Shader.Find("KSP/Particles/Alpha Blended");
            if (alphaShader == null)
            {
                ParsekLog.Warn("PartPuffFx", "Shader 'KSP/Particles/Alpha Blended' not found — puff will not be created");
                return null;
            }

            // Target: ~1/4 the visual impact of the vessel explosion.
            // Explosion uses vesselLength (10-30m) as scale; parts are much smaller.
            // Use a minimum of 2m so the puff is always visible.
            float scale = Mathf.Clamp(partScale, 2f, 10f);

            var obj = new GameObject("GhostPartPuffFx");
            obj.transform.position = worldPosition;

            // --- Smoke puff ---
            var smokePs = obj.AddComponent<ParticleSystem>();
            var smokeMain = smokePs.main;
            smokeMain.simulationSpace = ParticleSystemSimulationSpace.World;
            smokeMain.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.2f);
            smokeMain.startSpeed = new ParticleSystem.MinMaxCurve(scale * 0.3f, scale * 0.8f);
            smokeMain.startSize = new ParticleSystem.MinMaxCurve(scale * 0.15f, scale * 0.4f);
            smokeMain.maxParticles = 100;
            smokeMain.playOnAwake = false;
            smokeMain.loop = false;
            smokeMain.gravityModifier = 0.03f;
            smokeMain.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.4f, 0.4f, 0.4f, 0.6f),
                new Color(0.25f, 0.22f, 0.2f, 0.5f));

            var smokeShape = smokePs.shape;
            smokeShape.shapeType = ParticleSystemShapeType.Sphere;
            smokeShape.radius = scale * 0.1f;

            var smokeEmission = smokePs.emission;
            smokeEmission.enabled = true;
            smokeEmission.rateOverTime = 0f;
            smokeEmission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0f, 40, 60)
            });

            var smokeColor = smokePs.colorOverLifetime;
            smokeColor.enabled = true;
            var smokeGradient = new Gradient();
            smokeGradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.5f, 0.45f, 0.4f), 0f),
                    new GradientColorKey(new Color(0.3f, 0.28f, 0.25f), 0.5f),
                    new GradientColorKey(new Color(0.2f, 0.18f, 0.16f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.5f, 0.1f),
                    new GradientAlphaKey(0.3f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            smokeColor.color = smokeGradient;

            var smokeSize = smokePs.sizeOverLifetime;
            smokeSize.enabled = true;
            smokeSize.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.5f),
                    new Keyframe(0.5f, 1.5f),
                    new Keyframe(1f, 2.5f)));

            if (cachedExplosionTexture == null)
                cachedExplosionTexture = CreateSoftCircleTexture(32);
            var smokeMat = new Material(alphaShader);
            smokeMat.mainTexture = cachedExplosionTexture;
            smokeMat.SetColor("_TintColor", new Color(0.35f, 0.3f, 0.25f, 0.5f));
            var smokeRenderer = obj.GetComponent<ParticleSystemRenderer>();
            smokeRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            smokeRenderer.maxParticleSize = 10f;
            smokeRenderer.material = smokeMat;

            var smokeCleanup = obj.AddComponent<MaterialCleanup>();
            smokeCleanup.material = smokeMat;

            // --- Small spark burst (additive) ---
            Shader additiveShader = Shader.Find("KSP/Particles/Additive");
            if (additiveShader != null)
            {
                var sparkObj = new GameObject("Sparks");
                sparkObj.transform.SetParent(obj.transform, false);

                var sparkPs = sparkObj.AddComponent<ParticleSystem>();
                var sparkMain = sparkPs.main;
                sparkMain.simulationSpace = ParticleSystemSimulationSpace.World;
                sparkMain.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);
                sparkMain.startSpeed = new ParticleSystem.MinMaxCurve(scale * 0.5f, scale * 1.5f);
                sparkMain.startSize = new ParticleSystem.MinMaxCurve(scale * 0.03f, scale * 0.1f);
                sparkMain.maxParticles = 50;
                sparkMain.playOnAwake = false;
                sparkMain.loop = false;
                sparkMain.gravityModifier = 0.3f;
                sparkMain.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(1f, 0.9f, 0.5f, 0.8f),
                    new Color(1f, 0.6f, 0.2f, 0.6f));

                var sparkShape = sparkPs.shape;
                sparkShape.shapeType = ParticleSystemShapeType.Sphere;
                sparkShape.radius = scale * 0.08f;

                var sparkEmission = sparkPs.emission;
                sparkEmission.enabled = true;
                sparkEmission.rateOverTime = 0f;
                sparkEmission.SetBursts(new ParticleSystem.Burst[]
                {
                    new ParticleSystem.Burst(0f, 15, 30)
                });

                var sparkColor = sparkPs.colorOverLifetime;
                sparkColor.enabled = true;
                var sparkGradient = new Gradient();
                sparkGradient.SetKeys(
                    new GradientColorKey[]
                    {
                        new GradientColorKey(new Color(1f, 0.95f, 0.7f), 0f),
                        new GradientColorKey(new Color(1f, 0.5f, 0.1f), 0.5f),
                        new GradientColorKey(new Color(0.5f, 0.2f, 0.05f), 1f)
                    },
                    new GradientAlphaKey[]
                    {
                        new GradientAlphaKey(0.8f, 0f),
                        new GradientAlphaKey(0.3f, 0.5f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                sparkColor.color = sparkGradient;

                var sparkMat = new Material(additiveShader);
                sparkMat.mainTexture = cachedExplosionTexture;
                sparkMat.SetColor("_TintColor", new Color(1f, 0.7f, 0.3f, 0.5f));
                var sparkRenderer = sparkObj.GetComponent<ParticleSystemRenderer>();
                sparkRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                sparkRenderer.maxParticleSize = 8f;
                sparkRenderer.material = sparkMat;

                var sparkCleanup = sparkObj.AddComponent<MaterialCleanup>();
                sparkCleanup.material = sparkMat;

                sparkPs.Play();
            }

            smokePs.Play();
            Object.Destroy(obj, 3f);


            return obj;
        }

        /// <summary>
        /// Spawns a fire-and-forget explosion particle effect at the given world position.
        /// The returned GameObject auto-destroys after particles expire.
        /// Used when ghost playback reaches the end of a destroyed recording.
        /// </summary>
        internal static GameObject SpawnExplosionFx(Vector3 worldPosition, float vesselLength)
        {
            Shader particleShader = Shader.Find("KSP/Particles/Additive");
            if (particleShader == null)
            {
                ParsekLog.Warn("ExplosionFx", "Shader 'KSP/Particles/Additive' not found — explosion will not be created");
                return null;
            }

            var obj = new GameObject("GhostExplosionFx");
            obj.transform.position = worldPosition;

            var ps = obj.AddComponent<ParticleSystem>();

            // Main module
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(vesselLength * 1f, vesselLength * 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(vesselLength * 0.08f, vesselLength * 0.35f);
            main.maxParticles = 400;
            main.playOnAwake = false;
            main.prewarm = false;
            main.loop = false;
            main.gravityModifier = 0.15f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.9f, 0.5f, 0.9f),
                new Color(1f, 0.6f, 0.2f, 0.9f));

            // Shape: sphere burst
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = vesselLength * 0.2f;

            // Emission: single burst
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0f, 200, 300)
            });

            // Color over lifetime: bright yellow-white → orange → red-brown → fade
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.6f), 0f),
                    new GradientColorKey(new Color(1f, 0.5f, 0.1f), 0.25f),
                    new GradientColorKey(new Color(0.8f, 0.25f, 0.05f), 0.6f),
                    new GradientColorKey(new Color(0.3f, 0.1f, 0.02f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.9f, 0f),
                    new GradientAlphaKey(0.7f, 0.25f),
                    new GradientAlphaKey(0.2f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLifetime.color = gradient;

            // Size over lifetime: expanding fireball
            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 1f),
                    new Keyframe(0.4f, 1.5f),
                    new Keyframe(1f, 2.0f)));

            // Noise: turbulence for organic explosion look
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = vesselLength * 0.15f;
            noise.frequency = 2.5f;
            noise.scrollSpeed = 1.5f;
            noise.damping = true;

            // Renderer: additive shader with soft-circle texture
            var psRenderer = obj.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            psRenderer.maxParticleSize = 10f;

            if (cachedExplosionTexture == null)
                cachedExplosionTexture = CreateSoftCircleTexture(32);

            var mat = new Material(particleShader);
            mat.mainTexture = cachedExplosionTexture;
            mat.SetColor("_TintColor", new Color(1f, 0.7f, 0.3f, 0.5f));
            psRenderer.material = mat;

            // Attach self-cleanup so the Material is destroyed when the GO is destroyed
            var cleanup = obj.AddComponent<MaterialCleanup>();
            cleanup.material = mat;

            // --- Smoke child particle system ---
            var smokePs = BuildExplosionSmokeChild(obj, vesselLength);
            if (smokePs != null)
                smokePs.Play();

            ps.Play();

            // Auto-destroy after max smoke lifetime + buffer
            Object.Destroy(obj, 5.0f);

            ParsekLog.Verbose("ExplosionFx",
                $"Spawned at ({worldPosition.x:F1},{worldPosition.y:F1},{worldPosition.z:F1}) vesselLength={vesselLength:F1}m");

            return obj;
        }

        /// <summary>
        /// Creates a soft-circle texture at runtime for additive particle rendering.
        /// White center fading to transparent edges — avoids sprite sheet issues.
        /// </summary>
        private static Texture2D CreateSoftCircleTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float center = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center)) / center;
                    float alpha = Mathf.Clamp01(1f - dist);
                    alpha *= alpha; // quadratic falloff for soft edges
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply(false, true); // makeNoLongerReadable for GPU-only
            return tex;
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

        /// <summary>
        /// Builds a ghost GameObject for a planted flag from the flag part prefab.
        /// Returns the ghost (initially inactive). Applies flag texture to the mesh_flag quad.
        /// </summary>
        /// <summary>
        /// Spawns a real KSP flag vessel at the recorded position via ProtoVessel.
        /// Returns the flag vessel's root GameObject, or null on failure.
        /// The flag part is "prebuilt" with no .mu mesh — meshes are generated at runtime
        /// by FlagDecalBackground, so we must spawn a real vessel rather than clone a ghost.
        /// </summary>
        internal static GameObject SpawnFlagVessel(FlagEvent evt)
        {
            try
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == evt.bodyName);
                if (body == null)
                {
                    ParsekLog.Warn("GhostVisual",
                        $"Cannot spawn flag vessel: body '{evt.bodyName}' not found");
                    return null;
                }

                // Build flag part node with FlagSite module in "Placed" state
                uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                ConfigNode partNode = ProtoVessel.CreatePartNode("flag", flightId);
                string flagURL = !string.IsNullOrEmpty(evt.flagURL) ? evt.flagURL : "Squad/Flags/default";
                partNode.SetValue("flag", flagURL, true);

                ConfigNode moduleNode = new ConfigNode("MODULE");
                moduleNode.AddValue("name", "FlagSite");
                moduleNode.AddValue("state", "Placed");
                moduleNode.AddValue("PlaqueText", evt.plaqueText ?? "");
                moduleNode.AddValue("placedBy", evt.placedBy ?? "");
                partNode.AddNode(moduleNode);

                // Build orbit (required by ProtoVessel even for landed vessels)
                Orbit orbit = new Orbit(0, 0, body.Radius + evt.altitude, 0, 0, 0, 0, body);
                ConfigNode[] partNodes = new ConfigNode[] { partNode };

                string vesselName = !string.IsNullOrEmpty(evt.flagSiteName) ? evt.flagSiteName : "Flag";
                ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                    vesselName, VesselType.Flag, orbit, 0, partNodes);

                // Set landed position
                var ic = CultureInfo.InvariantCulture;
                vesselNode.SetValue("sit", Vessel.Situations.LANDED.ToString(), true);
                vesselNode.SetValue("landed", "True", true);
                vesselNode.SetValue("splashed", "False", true);
                vesselNode.SetValue("lat", evt.latitude.ToString("R", ic), true);
                vesselNode.SetValue("lon", evt.longitude.ToString("R", ic), true);
                vesselNode.SetValue("alt", evt.altitude.ToString("R", ic), true);
                vesselNode.SetValue("landedAt", body.name, true);

                // Set surface-relative rotation
                Quaternion surfRot = new Quaternion(evt.rotX, evt.rotY, evt.rotZ, evt.rotW);
                vesselNode.SetValue("rot", KSPUtil.WriteQuaternion(surfRot), true);

                // Spawn via ProtoVessel
                ProtoVessel pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Warn("GhostVisual",
                        $"Flag vessel spawn failed: ProtoVessel.Load() produced null vesselRef");
                    return null;
                }

                ParsekLog.Verbose("GhostVisual",
                    $"Spawned flag vessel: '{vesselName}' by '{evt.placedBy}' " +
                    $"at ({evt.latitude:F4},{evt.longitude:F4},{evt.altitude:F1}) on {evt.bodyName}");

                return pv.vesselRef.gameObject;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("GhostVisual",
                    $"Failed to spawn flag vessel: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a simple sphere GameObject as a fallback ghost visual
        /// when no vessel snapshot is available.
        /// </summary>
        internal static GameObject CreateGhostSphere(string name, Color color)
        {
            GameObject ghost = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ghost.name = name;
            ghost.transform.localScale = Vector3.one * 12f; // 12m diameter

            Collider collider = ghost.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            Renderer renderer = ghost.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("KSP/Emissive/Diffuse");
                if (shader != null)
                {
                    Material mat = new Material(shader);
                    mat.color = color;
                    mat.SetColor("_EmissiveColor", color);
                    renderer.material = mat;
                }
                else
                {
                    ParsekLog.Warn("GhostVisual", "Could not find KSP/Emissive/Diffuse shader, using default");
                }
            }

            return ghost;
        }

    }
}
