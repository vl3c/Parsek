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
        private static readonly Dictionary<string, Vector3> fxModelRotationFallbackEuler =
            new Dictionary<string, Vector3>(System.StringComparer.OrdinalIgnoreCase)
            {
                // These stock model FX are authored with implicit -90deg X orientation in KSP's
                // runtime FX pipeline when localRotation is omitted in EFFECTS config.
                { "Squad/FX/Monoprop_small", new Vector3(-90f, 0f, 0f) },
                { "Squad/FX/Monoprop_medium", new Vector3(-90f, 0f, 0f) },
                { "Squad/FX/shockExhaust_blue_small", new Vector3(-90f, 0f, 0f) },
                { "Squad/FX/shockExhaust_red_small", new Vector3(-90f, 0f, 0f) }
            };
        private static readonly string[] EngineModelNodeTypes =
            { "MODEL_MULTI_PARTICLE_PERSIST", "MODEL_MULTI_PARTICLE", "MODEL_PARTICLE" };

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
        private static GameObject FindFxPrefab(string prefabName)
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
                        ParsekLog.Log($"  FX prefab substitution: '{prefabName}' -> '{fallbackName}'");
                        return result;
                    }
                }
            }

            return null;
        }

        private static string NormalizeFxPrefabName(string rawName)
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
                    ParsekLog.Log($"  FX prefab loaded from Resources: '{prefabName}' (path='{resourcePath}')");
                    return result;
                }
            }

            GameObject modelPrefab = GameDatabase.Instance != null
                ? GameDatabase.Instance.GetModelPrefab(prefabName)
                : null;
            if (modelPrefab != null)
            {
                ParsekLog.Log($"  FX prefab loaded from GameDatabase model: '{prefabName}'");
                return modelPrefab;
            }

            PopulateFxPrefabCacheFromLoadedObjects();
            if (TryGetFxPrefabFromCache(prefabName, out GameObject cached))
            {
                ParsekLog.Log($"  FX prefab resolved from loaded objects: '{prefabName}'");
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

            ParsekLog.Log($"  FX prefab cache extended from loaded objects: +{added} entries");
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
            ParsekLog.Log($"  FX prefab cache built from PartLoader: {fxPrefabCache.Count} entries");
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
            Recording rec, string rootName)
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
                    ParsekLog.Verbose("GhostVisual", $"  Ghost part SKIPPED (no name): raw='{rawPart ?? "null"}' index={i}");
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
                    ParsekLog.Verbose("GhostVisual", $"  Ghost part SKIPPED (no prefab): '{partName}' pid={persistentId}");
                    continue;
                }
                string resolvedName = ap.partPrefab.partInfo?.name ?? ap.name;
                if (!string.IsNullOrEmpty(resolvedName) &&
                    !string.Equals(resolvedName, partName, System.StringComparison.OrdinalIgnoreCase))
                {
                    ParsekLog.Verbose("GhostVisual", $"  Ghost part lookup alias: '{partName}' -> '{resolvedName}'");
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
                    ParsekLog.Verbose("GhostVisual", $"  Ghost part SKIPPED (no mesh): '{partName}' pid={persistentId}");
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

        internal static ConfigNode GetGhostSnapshot(Recording rec)
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

        private static bool TryGetFxModelFallbackRotation(string modelName, out Quaternion result)
        {
            result = Quaternion.identity;
            if (string.IsNullOrEmpty(modelName))
                return false;

            string normalized = modelName.Trim();
            if (!fxModelRotationFallbackEuler.TryGetValue(normalized, out Vector3 fallbackEuler))
                return false;

            result = Quaternion.Euler(fallbackEuler);
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

        private static Transform ResolveGhostFxParent(
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

        private static void LogFxParentAlignmentDiagnostic(
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

        private static void LogFxInstancePlacementDiagnostic(
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

        private static int ConfigureGhostEngineParticleSystems(
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

                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTimeMultiplier = 0f;

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
                                    ParsekLog.Verbose("GhostVisual", $"  Semi-deployed canopy sampled via '{semiAnim}'@1: scale={sdScale} " +
                                        $"rootPos={sdPos} rootRot={sdRot.eulerAngles}");
                                }
                                else
                                {
                                    ParsekLog.Verbose("GhostVisual", $"  Semi-deploy animation '{semiAnim}'@1 gave near-zero scale, skipping");
                                }
                            }
                            else
                            {
                                ParsekLog.Verbose("GhostVisual", $"  Semi-deploy animation '{semiAnim}' not found on clone for '{key}'");
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
                                    ParsekLog.Verbose("GhostVisual", $"  Deployed canopy sampled via '{deployedAnimName}'@1: scale={dScale} " +
                                        $"rootPos={dPos} rootRot={dRot.eulerAngles}");
                                }
                            }
                            else
                            {
                                ParsekLog.Verbose("GhostVisual", $"  Deploy animation '{deployedAnimName}' not found on clone for '{key}'");
                            }
                        }
                    }
                    else
                    {
                        ParsekLog.Verbose("GhostVisual", $"  No Animation component on model clone for '{key}'");
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
                ParsekLog.Verbose("GhostVisual", $"  Deploy animation produced near-zero scale ({dScale}), using deployed canopy fallback");
                dScale = Vector3.one;
                dPos = Vector3.zero;
                dRot = Quaternion.identity;
            }

            var result = (sdScale, sdPos, sdRot, semiDeployedSampled, dScale, dPos, dRot);
            canopyCache[key] = result;
            ParsekLog.Verbose("GhostVisual", $"  Canopy states for '{key}': semiDeployed={semiDeployedSampled} sdScale={sdScale} " +
                $"deployed dScale={dScale} dPos={dPos} dRot={dRot.eulerAngles}");
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
                ParsekLog.Verbose("GhostVisual", $"  Gear '{key}': no animationStateName — skipping animation sampling");
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
                    ParsekLog.Verbose("GhostVisual", $"  Gear '{key}': no Animation component on model clone");
                    gearCache[key] = null;
                    return null;
                }

                AnimationState state = anim[animationStateName];
                if (state == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"  Gear '{key}': animation '{animationStateName}' not found on clone");
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
                    ParsekLog.Verbose("GhostVisual", $"  Gear '{key}': animation '{animationStateName}' produced no transform deltas");
                    result = null;
                }
                else
                {
                    ParsekLog.Verbose("GhostVisual", $"  Gear '{key}': sampled {result.Count} animated transforms from '{animationStateName}'");
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
                ParsekLog.Verbose("GhostVisual",
                    $"Part '{partName}': using FXModuleAnimateThrottle animation '{animationName}' for heat ghost");
                return true;
            }

            // Multiple instances — try name heuristic first
            for (int i = 0; i < candidates.Count; i++)
            {
                if (IsHeatAnimationName(candidates[i].animName))
                {
                    animationName = candidates[i].animName;
                    ParsekLog.Verbose("GhostVisual",
                        $"Part '{partName}': {candidates.Count} FXModuleAnimateThrottle instances, selected '{animationName}' (heat animation heuristic)");
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
            ParsekLog.Verbose("GhostVisual",
                $"Part '{partName}': {candidates.Count} FXModuleAnimateThrottle instances, none match heat heuristic, using lowest responseSpeed '{animationName}'");
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
                string partName = prefab.partInfo?.name ?? prefab.name;
                ParsekLog.Verbose("GhostVisual",
                    $"Part '{partName}': using FXModuleAnimateRCS animation '{animationName}' for heat ghost");
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
                    ParsekLog.Verbose("GhostVisual", $"  Heat3State '{partKey}': animation '{animationName}' not found on clone");
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

                ParsekLog.Verbose("GhostVisual",
                    $"[GhostVisual] Part '{partKey}': sampled 3-state heat animation '{animationName}' at t=0/0.5/1, " +
                    $"{result.Count} transform deltas");

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
                ParsekLog.Verbose("GhostVisual", $"  Ladder '{partKey}': no ladderRetractAnimationName — skipping animation sampling");
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
                        ParsekLog.Verbose("GhostVisual", $"  Ladder '{partKey}': animation root '{animationRootName}' not found on clone");
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
                    ParsekLog.Verbose("GhostVisual", $"  Ladder '{partKey}': no Animation component on model clone");
                    ladderCache[cacheKey] = null;
                    return null;
                }

                AnimationState state = anim[animationName];
                if (state == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"  Ladder '{partKey}': animation '{animationName}' not found on clone");
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
                    ParsekLog.Verbose("GhostVisual", $"  Ladder '{partKey}': animation '{animationName}' produced no transform deltas");
                    result = null;
                }
                else
                {
                    ParsekLog.Verbose("GhostVisual", $"  Ladder '{partKey}': sampled {result.Count} animated transforms from '{animationName}' " +
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
                ParsekLog.Verbose("GhostVisual", $"  CargoBay '{key}': no animationName — skipping animation sampling");
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
                ParsekLog.Verbose("GhostVisual", $"  CargoBay '{key}': non-standard closedPosition={closedPosition} — skipping");
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
                    ParsekLog.Verbose("GhostVisual", $"  CargoBay '{key}': no Animation component on model clone");
                    cargoBayCache[key] = null;
                    return null;
                }

                AnimationState state = anim[animationName];
                if (state == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"  CargoBay '{key}': animation '{animationName}' not found on clone");
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
                    ParsekLog.Verbose("GhostVisual", $"  CargoBay '{key}': animation '{animationName}' produced no transform deltas");
                    result = null;
                }
                else
                {
                    ParsekLog.Verbose("GhostVisual", $"  CargoBay '{key}': sampled {result.Count} animated transforms from '{animationName}'");
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
                ParsekLog.Verbose("GhostVisual", $"  Deployable '{key}': no animationName — skipping animation sampling");
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
                    ParsekLog.Verbose("GhostVisual", $"  Deployable '{key}': no Animation component on model clone");
                    deployableCache[key] = null;
                    return null;
                }

                AnimationState state = anim[animName];
                if (state == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"  Deployable '{key}': animation '{animName}' not found on clone");
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
                    ParsekLog.Verbose("GhostVisual", $"  Deployable '{key}': animation '{animName}' produced no transform deltas");
                    result = null;
                }
                else
                {
                    ParsekLog.Verbose("GhostVisual", $"  Deployable '{key}': sampled {result.Count} animated transforms from '{animName}'");
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
            ParsekLog.VerboseRateLimited("GhostVisual", $"hierarchy_{partName}_{depth}_{t.name}",
                $"    HIERARCHY {partName}: {indent}{t.name}{compStr}{activeStr}", 60.0);
            for (int i = 0; i < t.childCount; i++)
                DumpTransformHierarchy(t.GetChild(i), depth + 1, partName);
        }

        #region Extracted helpers for TryBuildEngineFX

        /// <summary>
        /// Scans EFFECTS config groups for MODEL_MULTI_PARTICLE/MODEL_PARTICLE entries.
        /// Populates modelFxEntries with transform/model/offset/rotation tuples and extracts
        /// the first emission and speed curves found.
        /// </summary>
        private static void ScanEffectsModelFxEntries(
            ConfigNode[] effectGroups,
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot)> modelFxEntries,
            ref FloatCurve emissionCurve,
            ref FloatCurve speedCurve)
        {
            for (int g = 0; g < effectGroups.Length; g++)
            {
                for (int n = 0; n < EngineModelNodeTypes.Length; n++)
                {
                    string nodeType = EngineModelNodeTypes[n];
                    ConfigNode[] modelNodes = effectGroups[g].GetNodes(nodeType);
                    for (int mp = 0; mp < modelNodes.Length; mp++)
                    {
                        string transformName = modelNodes[mp].GetValue("transformName");
                        string modelName = modelNodes[mp].GetValue("modelName");
                        if (!string.IsNullOrEmpty(transformName))
                        {
                            Vector3 mmpLocalPos = Vector3.zero;
                            string mmpOffsetStr = modelNodes[mp].GetValue("localPosition");
                            if (string.IsNullOrEmpty(mmpOffsetStr))
                                mmpOffsetStr = modelNodes[mp].GetValue("localOffset");
                            TryParseVector3(mmpOffsetStr, out mmpLocalPos);

                            Quaternion mmpLocalRot = Quaternion.identity;
                            string mmpRotStr = modelNodes[mp].GetValue("localRotation");
                            if (!TryParseFxLocalRotation(mmpRotStr, out mmpLocalRot))
                            {
                                if (TryGetFxModelFallbackRotation(modelName, out mmpLocalRot))
                                {
                                    ParsekLog.Log($"    Engine FX model rotation fallback: '{modelName}' euler={mmpLocalRot.eulerAngles}");
                                }
                            }

                            modelFxEntries.Add((nodeType, transformName, modelName ?? "", mmpLocalPos, mmpLocalRot));

                            if (emissionCurve == null)
                            {
                                ConfigNode emNode = modelNodes[mp].GetNode("emission");
                                if (emNode != null)
                                {
                                    emissionCurve = new FloatCurve();
                                    emissionCurve.Load(emNode);
                                }
                                ConfigNode spNode = modelNodes[mp].GetNode("speed");
                                if (spNode != null)
                                {
                                    speedCurve = new FloatCurve();
                                    speedCurve.Load(spNode);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Scans EFFECTS config groups for PREFAB_PARTICLE entries (used by Spark, Twitch, Pug, etc.).
        /// Populates prefabFxEntries with prefab/transform/offset/rotation tuples.
        /// Skips flameout/sparks/debris prefabs -- only wants running FX.
        /// </summary>
        private static void ScanEffectsPrefabParticleEntries(
            ConfigNode[] effectGroups,
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation)> prefabFxEntries)
        {
            for (int g = 0; g < effectGroups.Length; g++)
            {
                ConfigNode[] ppNodes = effectGroups[g].GetNodes("PREFAB_PARTICLE");
                for (int pp = 0; pp < ppNodes.Length; pp++)
                {
                    string prefabName = ppNodes[pp].GetValue("prefabName");
                    string transformName = ppNodes[pp].GetValue("transformName");
                    if (string.IsNullOrEmpty(prefabName) || string.IsNullOrEmpty(transformName))
                        continue;

                    string lower = prefabName.ToLowerInvariant();
                    if (lower.Contains("flameout") || lower.Contains("sparks") || lower.Contains("debris"))
                        continue;

                    Vector3 localOffset = Vector3.zero;
                    string offsetStr = ppNodes[pp].GetValue("localOffset");
                    if (string.IsNullOrEmpty(offsetStr))
                        offsetStr = ppNodes[pp].GetValue("localPosition");
                    TryParseVector3(offsetStr, out localOffset);

                    Quaternion localRot = Quaternion.identity;
                    string rotStr = ppNodes[pp].GetValue("localRotation");
                    bool hasLocalRot = TryParseFxLocalRotation(rotStr, out localRot);

                    prefabFxEntries.Add((prefabName, transformName, localOffset, localRot, hasLocalRot));
                }
            }
        }

        /// <summary>
        /// Processes legacy PART-level fx_* child entries when no EFFECTS node is present.
        /// Clones particle systems from prefab children onto ghost thrust anchors.
        /// Returns true if any particle systems were added to the info.
        /// </summary>
        private static bool ProcessEngineLegacyFx(
            Part prefab, ModuleEngines engine, EngineGhostInfo info,
            string partName, int moduleIndex,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            var legacyAnchors = new List<Transform>();
            if (engine != null && engine.thrustTransforms != null)
            {
                for (int t = 0; t < engine.thrustTransforms.Count; t++)
                {
                    Transform anchor = engine.thrustTransforms[t];
                    if (anchor != null && !legacyAnchors.Contains(anchor))
                        legacyAnchors.Add(anchor);
                }
            }
            if (legacyAnchors.Count == 0 && engine != null &&
                !string.IsNullOrEmpty(engine.thrustVectorTransformName))
            {
                var namedAnchors = FindTransformsRecursive(prefab.transform, engine.thrustVectorTransformName);
                for (int t = 0; t < namedAnchors.Count; t++)
                {
                    Transform anchor = namedAnchors[t];
                    if (anchor != null && !legacyAnchors.Contains(anchor))
                        legacyAnchors.Add(anchor);
                }
            }
            Vector3 legacyFxOffset = engine != null ? engine.fxOffset : Vector3.zero;

            for (int c = 0; c < prefab.transform.childCount; c++)
            {
                Transform child = prefab.transform.GetChild(c);
                string childName = child.name.ToLowerInvariant();
                if (!childName.StartsWith("fx_")) continue;

                if (childName.Contains("flameout") || childName.Contains("sparks") || childName.Contains("debris"))
                    continue;
                if (!childName.Contains("flame") && !childName.Contains("exhaust") && !childName.Contains("smoke"))
                    continue;

                var ps = child.GetComponentInChildren<ParticleSystem>(true);
                if (ps == null) continue;

                int clonesAdded = 0;
                if (legacyAnchors.Count > 0)
                {
                    for (int t = 0; t < legacyAnchors.Count; t++)
                    {
                        Transform srcLegacyAnchor = legacyAnchors[t];
                        Transform ghostLegacyParent = ResolveGhostFxParent(
                            srcLegacyAnchor, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                        if (ghostLegacyParent == null)
                            continue;

                        GameObject fxClone = Object.Instantiate(child.gameObject);
                        fxClone.transform.SetParent(ghostLegacyParent, false);
                        fxClone.transform.localPosition = legacyFxOffset;
                        Quaternion legacyLocalRot = Quaternion.Inverse(srcLegacyAnchor.rotation) * child.rotation;
                        fxClone.transform.localRotation = legacyLocalRot;
                        fxClone.transform.localScale = child.localScale;

                        var smokeTrail = fxClone.GetComponent("SmokeTrailControl");
                        if (smokeTrail != null)
                            Object.Destroy(smokeTrail);

                        int addedSystems = ConfigureGhostEngineParticleSystems(fxClone, info.particleSystems);
                        if (addedSystems > 0)
                        {
                            clonesAdded++;
                            LogFxInstancePlacementDiagnostic(partName, moduleIndex, "LEGACY_CHILD",
                                srcLegacyAnchor.name, child.name, prefab.transform, ghostModelNode,
                                srcLegacyAnchor, ghostLegacyParent, fxClone.transform,
                                legacyFxOffset, legacyLocalRot, true);
                            ParsekLog.Log($"    Engine FX (legacy): '{partName}' midx={moduleIndex} " +
                                $"fx='{child.name}' anchor='{srcLegacyAnchor.name}' offset={legacyFxOffset} systems={addedSystems}");
                        }
                        else
                        {
                            Object.Destroy(fxClone);
                        }
                    }
                }

                if (clonesAdded == 0)
                {
                    GameObject fxClone = Object.Instantiate(child.gameObject);
                    fxClone.transform.SetParent(ghostModelNode.parent, false);
                    fxClone.transform.localPosition = child.localPosition;
                    fxClone.transform.localRotation = child.localRotation;
                    fxClone.transform.localScale = child.localScale;

                    var smokeTrail = fxClone.GetComponent("SmokeTrailControl");
                    if (smokeTrail != null)
                        Object.Destroy(smokeTrail);

                    int addedSystems = ConfigureGhostEngineParticleSystems(fxClone, info.particleSystems);
                    if (addedSystems > 0)
                    {
                        Transform fallbackParent = fxClone.transform.parent != null
                            ? fxClone.transform.parent : ghostModelNode;
                        LogFxInstancePlacementDiagnostic(partName, moduleIndex, "LEGACY_CHILD_FALLBACK",
                            child.name, child.name, prefab.transform, ghostModelNode,
                            child, fallbackParent, fxClone.transform,
                            child.localPosition, child.localRotation, true);
                        ParsekLog.Log($"    Engine FX (legacy): '{partName}' midx={moduleIndex} " +
                            $"fx='{child.name}' systems={addedSystems}");
                    }
                    else
                    {
                        Object.Destroy(fxClone);
                    }
                }
            }

            return info.particleSystems.Count > 0;
        }

        /// <summary>
        /// Instantiates model FX entries (MODEL_MULTI_PARTICLE/MODEL_PARTICLE) from EFFECTS config
        /// and parents them to the ghost's mirrored transform hierarchy.
        /// </summary>
        private static void ProcessEngineModelFxEntries(
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot)> modelFxEntries,
            Part prefab, EngineGhostInfo info,
            string partName, int moduleIndex,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            for (int f = 0; f < modelFxEntries.Count; f++)
            {
                var (nodeType, transformName, modelName, mmpLocalPos, mmpLocalRot) = modelFxEntries[f];

                var fxTransforms = FindTransformsRecursive(prefab.transform, transformName);
                for (int t = 0; t < fxTransforms.Count; t++)
                {
                    Transform srcFxTransform = fxTransforms[t];

                    Transform ghostFxParent = ResolveGhostFxParent(
                        srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                    if (ghostFxParent == null)
                    {
                        ParsekLog.Log($"    Engine FX: '{partName}' midx={moduleIndex} " +
                            $"transform='{transformName}' parent resolution failed");
                        continue;
                    }
                    LogFxParentAlignmentDiagnostic(partName, moduleIndex, nodeType, transformName,
                        prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent);

                    if (!string.IsNullOrEmpty(modelName))
                    {
                        GameObject fxPrefab = GameDatabase.Instance.GetModelPrefab(modelName);
                        if (fxPrefab != null)
                        {
                            GameObject fxInstance = Object.Instantiate(fxPrefab);
                            fxInstance.SetActive(true);
                            fxInstance.transform.SetParent(ghostFxParent, false);
                            fxInstance.transform.localPosition = mmpLocalPos;
                            fxInstance.transform.localRotation = mmpLocalRot;

                            int addedSystems = ConfigureGhostEngineParticleSystems(fxInstance, info.particleSystems);
                            if (addedSystems > 0)
                            {
                                LogFxInstancePlacementDiagnostic(partName, moduleIndex, nodeType, transformName,
                                    modelName, prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent,
                                    fxInstance.transform, mmpLocalPos, mmpLocalRot, true);
                                Vector3 srcFwd = srcFxTransform.forward;
                                Vector3 srcUp = srcFxTransform.up;
                                Quaternion srcLocalRot = srcFxTransform.localRotation;
                                ParsekLog.Log($"    Engine FX cloned: '{partName}' midx={moduleIndex} " +
                                    $"type={nodeType} transform='{transformName}' model='{modelName}' " +
                                    $"systems={addedSystems} " +
                                    $"srcLocalRot={srcLocalRot} srcFwd={srcFwd} srcUp={srcUp}");
                            }
                            else
                            {
                                ParsekLog.Log($"    Engine FX model has no ParticleSystem: '{modelName}' for '{partName}'");
                                Object.Destroy(fxInstance);
                            }
                        }
                        else
                        {
                            ParsekLog.Verbose("GhostVisual", $"    Engine FX model not found: '{modelName}' for '{partName}'");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Instantiates PREFAB_PARTICLE entries from EFFECTS config and parents them to the
        /// ghost's mirrored transform hierarchy. Handles special cases like RAPIER white flame scaling.
        /// </summary>
        private static void ProcessEnginePrefabFxEntries(
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation)> prefabFxEntries,
            Part prefab, EngineGhostInfo info,
            string partName, int moduleIndex,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            for (int f = 0; f < prefabFxEntries.Count; f++)
            {
                var (prefabName, transformName, localOffset, localRot, hasLocalRot) = prefabFxEntries[f];
                string normalizedPrefabName = NormalizeFxPrefabName(prefabName);
                bool isRapierWhiteFlame =
                    string.Equals(partName, "RAPIER", System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(normalizedPrefabName, "fx_exhaustFlame_white", System.StringComparison.OrdinalIgnoreCase);

                GameObject fxPrefab = FindFxPrefab(prefabName);
                if (fxPrefab == null)
                {
                    ParsekLog.Log($"    Engine FX prefab not found: '{prefabName}' for '{partName}'");
                    continue;
                }

                var fxTransforms = FindTransformsRecursive(prefab.transform, transformName);
                if (fxTransforms.Count == 0)
                {
                    ParsekLog.Log($"    Engine FX (prefab): '{partName}' midx={moduleIndex} " +
                        $"transform '{transformName}' not found on prefab");
                    continue;
                }
                for (int t = 0; t < fxTransforms.Count; t++)
                {
                    if (isRapierWhiteFlame && t > 0)
                        continue;

                    Transform srcFxTransform = fxTransforms[t];

                    Transform ghostFxParent = ResolveGhostFxParent(
                        srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                    if (ghostFxParent == null)
                    {
                        ParsekLog.Log($"    Engine FX (prefab): '{partName}' midx={moduleIndex} " +
                            $"transform='{transformName}' parent resolution failed");
                        continue;
                    }
                    LogFxParentAlignmentDiagnostic(partName, moduleIndex, "PREFAB_PARTICLE", transformName,
                        prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent);

                    GameObject fxInstance = Object.Instantiate(fxPrefab);
                    fxInstance.transform.SetParent(ghostFxParent, false);
                    fxInstance.transform.localPosition = localOffset;
                    if (hasLocalRot)
                        fxInstance.transform.localRotation = localRot;

                    if (isRapierWhiteFlame)
                    {
                        fxInstance.transform.localScale *= 0.35f;
                        ParticleSystem[] rapierWhiteSystems = fxInstance.GetComponentsInChildren<ParticleSystem>(true);
                        for (int psIndex = 0; psIndex < rapierWhiteSystems.Length; psIndex++)
                        {
                            var main = rapierWhiteSystems[psIndex].main;
                            main.startSizeMultiplier *= 0.45f;
                            main.startSpeedMultiplier *= 0.75f;
                        }
                    }

                    var smokeTrail = fxInstance.GetComponent("SmokeTrailControl");
                    if (smokeTrail != null)
                        Object.Destroy(smokeTrail);

                    int addedSystems = ConfigureGhostEngineParticleSystems(fxInstance, info.particleSystems);
                    if (addedSystems > 0)
                    {
                        LogFxInstancePlacementDiagnostic(partName, moduleIndex, "PREFAB_PARTICLE", transformName,
                            prefabName, prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent,
                            fxInstance.transform, localOffset, localRot, hasLocalRot);
                        ParsekLog.Log($"    Engine FX (prefab): '{partName}' midx={moduleIndex} " +
                            $"transform='{transformName}' prefab='{prefabName}' systems={addedSystems}");
                    }
                    else
                    {
                        ParsekLog.Log($"    Engine FX (prefab): '{partName}' midx={moduleIndex} " +
                            $"prefab '{prefabName}' has no ParticleSystem");
                        Object.Destroy(fxInstance);
                    }
                }
            }
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
                return null;

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

        #endregion

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

                // LES particle FX (LES_Thruster) produces excessive particles at playback distance.
                // Skip particle FX — the nozzle glow from FXModuleAnimateThrottle provides sufficient visual.
                if (string.Equals(partName, "LaunchEscapeSystem", System.StringComparison.Ordinal))
                {
                    ParsekLog.Verbose("GhostVisual",
                        $"    Skipping engine particle FX for '{partName}' pid={persistentId}: LES uses nozzle glow only");
                    continue;
                }

                bool isRapierPart =
                    string.Equals(partName, "RAPIER", System.StringComparison.OrdinalIgnoreCase);
                if (isRapierPart && moduleIndex > 0)
                {
                    // RAPIER has multi-mode engine modules sharing nozzle transforms; recording events
                    // target midx=0, so skip duplicate module FX to avoid doubled plumes.
                    ParsekLog.Log($"    Engine '{partName}' midx={moduleIndex}: skipped duplicate multi-mode FX module");
                    continue;
                }

                // Try to read EFFECTS config from the part config
                ConfigNode partConfig = prefab.partInfo?.partConfig;
                if (partConfig == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"    Engine '{partName}' midx={moduleIndex}: no partConfig — skipping FX");
                    continue;
                }

                ConfigNode effectsNode = partConfig.GetNode("EFFECTS");

                // Scan EFFECTS for particle FX entries (MODEL_MULTI_PARTICLE, MODEL_PARTICLE, PREFAB_PARTICLE)
                var modelFxEntries = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot)>();
                var prefabFxEntries = new List<(
                    string prefabName,
                    string transformName,
                    Vector3 localOffset,
                    Quaternion localRotation,
                    bool hasLocalRotation)>();
                FloatCurve emissionCurve = null;
                FloatCurve speedCurve = null;

                if (effectsNode != null)
                {
                    ConfigNode[] effectGroups = effectsNode.GetNodes();
                    ScanEffectsModelFxEntries(effectGroups, modelFxEntries, ref emissionCurve, ref speedCurve);
                    ScanEffectsPrefabParticleEntries(effectGroups, prefabFxEntries);
                }

                var namedTransformCache = new Dictionary<string, List<Transform>>(System.StringComparer.OrdinalIgnoreCase);

                List<Transform> FindNamedTransformsCached(string transformName)
                {
                    if (string.IsNullOrEmpty(transformName))
                        return new List<Transform>();

                    if (namedTransformCache.TryGetValue(transformName, out List<Transform> cached))
                        return cached;

                    List<Transform> found = FindTransformsRecursive(prefab.transform, transformName);
                    namedTransformCache[transformName] = found;
                    return found;
                }

                bool HasNamedTransform(string transformName)
                {
                    return FindNamedTransformsCached(transformName).Count > 0;
                }

                bool HasPrefabMatch(System.Func<string, bool> matcher)
                {
                    for (int i = 0; i < prefabFxEntries.Count; i++)
                    {
                        string existingPrefab = NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
                        if (!string.IsNullOrEmpty(existingPrefab) && matcher(existingPrefab))
                            return true;
                    }

                    return false;
                }

                string ResolveFallbackTransform(
                    string preferredTransform,
                    bool includeEngineTransform,
                    params string[] alternateTransforms)
                {
                    if (!string.IsNullOrEmpty(preferredTransform) && HasNamedTransform(preferredTransform))
                        return preferredTransform;

                    if (includeEngineTransform &&
                        engine != null &&
                        !string.IsNullOrEmpty(engine.thrustVectorTransformName) &&
                        HasNamedTransform(engine.thrustVectorTransformName))
                    {
                        return engine.thrustVectorTransformName;
                    }

                    for (int i = 0; i < alternateTransforms.Length; i++)
                    {
                        string candidate = alternateTransforms[i];
                        if (!string.IsNullOrEmpty(candidate) && HasNamedTransform(candidate))
                            return candidate;
                    }

                    return preferredTransform;
                }

                Vector3 ResolveFallbackOffset(
                    string fallbackTransform,
                    Vector3 defaultOffset,
                    bool matchFxTransformAlias)
                {
                    for (int i = 0; i < modelFxEntries.Count; i++)
                    {
                        if (string.Equals(modelFxEntries[i].transformName, fallbackTransform, System.StringComparison.OrdinalIgnoreCase) ||
                            (matchFxTransformAlias &&
                             string.Equals(modelFxEntries[i].transformName, "FXTransform", System.StringComparison.OrdinalIgnoreCase)))
                        {
                            return modelFxEntries[i].localPos;
                        }
                    }

                    return defaultOffset;
                }

                void AddPrefabFallbackIfMissing(
                    System.Func<string, bool> hasExistingPrefab,
                    string prefabName,
                    string preferredTransform,
                    bool includeEngineTransform,
                    string[] alternateTransforms,
                    Vector3 defaultOffset,
                    bool matchFxTransformAlias,
                    Quaternion localRotation,
                    bool hasLocalRotation,
                    string description)
                {
                    if (HasPrefabMatch(hasExistingPrefab))
                        return;

                    string fallbackTransform = ResolveFallbackTransform(
                        preferredTransform,
                        includeEngineTransform,
                        alternateTransforms);
                    Vector3 fallbackOffset = ResolveFallbackOffset(
                        fallbackTransform,
                        defaultOffset,
                        matchFxTransformAlias);

                    prefabFxEntries.Add((prefabName, fallbackTransform, fallbackOffset, localRotation, hasLocalRotation));
                    string rotationSuffix = hasLocalRotation ? $" rot={localRotation.eulerAngles}" : "";
                    ParsekLog.Log($"    Engine FX fallback: '{partName}' midx={moduleIndex} " +
                        $"added {description} on '{fallbackTransform}' offset={fallbackOffset}{rotationSuffix}");
                }

                // Ant/Spider configs only define MODEL_MULTI_PARTICLE Monoprop_small in running FX.
                // Add a Twitch-style prefab flame fallback so they render a visible plume like Twitch.
                bool isAntOrSpider =
                    string.Equals(partName, "microEngine.v2", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "microEngine_v2", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "microEngine", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "radialEngineMini.v2", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "radialEngineMini_v2", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "radialEngineMini", System.StringComparison.OrdinalIgnoreCase);
                if (isAntOrSpider)
                {
                    AddPrefabFallbackIfMissing(
                        existingPrefab =>
                            string.Equals(existingPrefab, "fx_exhaustFlame_yellow_tiny_Z", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(existingPrefab, "fx_exhaustFlame_yellow_tiny", System.StringComparison.OrdinalIgnoreCase),
                        prefabName: "fx_exhaustFlame_yellow_tiny_Z",
                        preferredTransform: "FXTransform",
                        includeEngineTransform: true,
                        alternateTransforms: new[] { "thrustTransform" },
                        defaultOffset: new Vector3(0f, 0f, 0.08f),
                        matchFxTransformAlias: true,
                        localRotation: Quaternion.identity,
                        hasLocalRotation: false,
                        description: "Twitch plume prefab");
                }

                // Kickback: force Thumper-style FX so smoke/flame match solidBooster1-1 visuals.
                // This avoids the stock veryLarge/large smoke prefab orientation mismatch.
                bool isKickback =
                    string.Equals(partName, "MassiveBooster", System.StringComparison.OrdinalIgnoreCase);
                if (isKickback)
                {
                    int removedKickbackModelFx = modelFxEntries.Count;
                    modelFxEntries.Clear();

                    int removedKickbackPrefabs = 0;
                    for (int i = prefabFxEntries.Count - 1; i >= 0; i--)
                    {
                        string existingPrefab = NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
                        if (existingPrefab == null)
                            continue;

                        if (existingPrefab.IndexOf("smoketrail", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            prefabFxEntries.RemoveAt(i);
                            removedKickbackPrefabs++;
                            continue;
                        }

                        if (existingPrefab.IndexOf("exhaustflame", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            prefabFxEntries.RemoveAt(i);
                            removedKickbackPrefabs++;
                        }
                    }

                    string kickbackTransform = "thrustTransform";
                    if (engine != null && !string.IsNullOrEmpty(engine.thrustVectorTransformName))
                        kickbackTransform = engine.thrustVectorTransformName;
                    if (!HasNamedTransform(kickbackTransform))
                    {
                        if (HasNamedTransform("thrustTransform"))
                            kickbackTransform = "thrustTransform";
                        else if (HasNamedTransform("smokePoint"))
                            kickbackTransform = "smokePoint";
                        else if (HasNamedTransform("fxPoint"))
                            kickbackTransform = "fxPoint";
                    }

                    Vector3 kickbackOffset = engine != null ? engine.fxOffset : new Vector3(0f, 0f, 0.35f);
                    if (kickbackOffset.sqrMagnitude <= 0.000001f)
                        kickbackOffset = new Vector3(0f, 0f, 0.35f);
                    Quaternion kickbackThumperLocalRot = Quaternion.Euler(-90f, 0f, 0f);
                    var kickbackAnchors = FindNamedTransformsCached(kickbackTransform);
                    if (kickbackAnchors.Count > 0)
                    {
                        Transform diagAnchor = kickbackAnchors[0];
                        Vector3 anchorPartLocalPos = prefab.transform.InverseTransformPoint(diagAnchor.position);
                        Quaternion anchorPartLocalRot = Quaternion.Inverse(prefab.transform.rotation) * diagAnchor.rotation;
                        ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                            $"    [DIAG] Kickback fallback anchor '{partName}' midx={moduleIndex} " +
                            $"transform='{kickbackTransform}' anchors={kickbackAnchors.Count} " +
                            $"anchorPartLocalPos={anchorPartLocalPos} anchorPartLocalRot={anchorPartLocalRot.eulerAngles} " +
                            $"anchorFwd={diagAnchor.forward} anchorUp={diagAnchor.up} " +
                            $"targetOffset={kickbackOffset} targetRot={kickbackThumperLocalRot.eulerAngles}", 60.0);
                    }
                    else
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                            $"    [DIAG] Kickback fallback anchor '{partName}' midx={moduleIndex} " +
                            $"transform='{kickbackTransform}' anchors=0 targetOffset={kickbackOffset} " +
                            $"targetRot={kickbackThumperLocalRot.eulerAngles}", 60.0);
                    }

                    prefabFxEntries.Add(("fx_smokeTrail_medium",
                        kickbackTransform, kickbackOffset, kickbackThumperLocalRot, true));
                    prefabFxEntries.Add(("fx_exhaustFlame_yellow",
                        kickbackTransform, kickbackOffset, kickbackThumperLocalRot, true));
                    ParsekLog.Log($"    Engine FX fallback: '{partName}' midx={moduleIndex} " +
                        $"forced Thumper-style plume on '{kickbackTransform}' offset={kickbackOffset} " +
                        $"rot={kickbackThumperLocalRot.eulerAngles} hasRot=true " +
                        $"(removed MODEL={removedKickbackModelFx}, PREFAB={removedKickbackPrefabs})");
                }

                // Puff (omsEngine) often renders only Monoprop_big model FX; add a compact blue flame core.
                bool isPuff =
                    string.Equals(partName, "omsEngine", System.StringComparison.OrdinalIgnoreCase);
                if (isPuff)
                {
                    AddPrefabFallbackIfMissing(
                        existingPrefab => existingPrefab.IndexOf("exhaustflame", System.StringComparison.OrdinalIgnoreCase) >= 0,
                        prefabName: "fx_exhaustFlame_blue_small",
                        preferredTransform: "FXTransform",
                        includeEngineTransform: true,
                        alternateTransforms: new[] { "thrustTransform" },
                        defaultOffset: new Vector3(0f, 0f, 0.12f),
                        matchFxTransformAlias: true,
                        localRotation: Quaternion.identity,
                        hasLocalRotation: false,
                        description: "Puff blue flame prefab");
                }

                // Rhino: force a compact Mainsail-like plume profile (small yellow flame + light smoke),
                // aligned to the actual nozzle thrust axis.
                bool isRhino =
                    string.Equals(partName, "Size3AdvancedEngine", System.StringComparison.OrdinalIgnoreCase);
                if (isRhino)
                {
                    int removedRhinoModelFx = modelFxEntries.Count;
                    modelFxEntries.Clear();

                    int removedRhinoPrefabs = 0;
                    for (int i = prefabFxEntries.Count - 1; i >= 0; i--)
                    {
                        string existingPrefab = NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
                        if (existingPrefab == null)
                            continue;

                        if (existingPrefab.IndexOf("smoketrail", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                            existingPrefab.IndexOf("exhaustflame", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            prefabFxEntries.RemoveAt(i);
                            removedRhinoPrefabs++;
                        }
                    }

                    string rhinoPreferredTransform =
                        (engine != null && !string.IsNullOrEmpty(engine.thrustVectorTransformName))
                            ? engine.thrustVectorTransformName
                            : "thrustTransform";
                    string rhinoTransform = ResolveFallbackTransform(
                        preferredTransform: rhinoPreferredTransform,
                        includeEngineTransform: true,
                        alternateTransforms: new[] { "thrustTransform", "smokePoint", "fxPoint" });
                    Vector3 rhinoOffset = ResolveFallbackOffset(
                        rhinoTransform,
                        defaultOffset: engine != null ? engine.fxOffset : Vector3.zero,
                        matchFxTransformAlias: false);
                    Quaternion rhinoPlumeRotation = Quaternion.Euler(-90f, 0f, 0f);

                    prefabFxEntries.Add(("fx_smokeTrail_light", rhinoTransform, rhinoOffset, rhinoPlumeRotation, true));
                    prefabFxEntries.Add(("fx_exhaustFlame_yellow_medium", rhinoTransform, rhinoOffset, rhinoPlumeRotation, true));
                    ParsekLog.Log($"    Engine FX fallback: '{partName}' midx={moduleIndex} " +
                        $"forced compact Mainsail-like plume on '{rhinoTransform}' offset={rhinoOffset} " +
                        $"(removed MODEL={removedRhinoModelFx}, PREFAB={removedRhinoPrefabs})");
                }

                // Rhino/Mammoth/Twin-Boar can show smoke without a visible core flame in ghost playback.
                // Add a white flame prefab fallback used by stock medium/large plume setups.
                bool isHeavyLargeEngine =
                    string.Equals(partName, "Size3AdvancedEngine", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "Size3EngineCluster", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "Size2LFB.v2", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "Size2LFB_v2", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partName, "Size2LFB", System.StringComparison.OrdinalIgnoreCase);
                if (isHeavyLargeEngine)
                {
                    string preferredTransform = "thrustTransform";
                    if (string.Equals(partName, "Size3AdvancedEngine", System.StringComparison.OrdinalIgnoreCase))
                        preferredTransform = "fxPoint";

                    // White flame prefab is authored in a Y-up frame; on these thrust/fx transforms
                    // it needs a -90deg X adjustment to align with nozzle direction.
                    Quaternion fallbackRotation = Quaternion.Euler(-90f, 0f, 0f);
                    AddPrefabFallbackIfMissing(
                        existingPrefab => existingPrefab.IndexOf("exhaustflame", System.StringComparison.OrdinalIgnoreCase) >= 0,
                        prefabName: "fx_exhaustFlame_white",
                        preferredTransform: preferredTransform,
                        includeEngineTransform: true,
                        alternateTransforms: new[] { "fxPoint", "thrustTransform" },
                        defaultOffset: Vector3.zero,
                        matchFxTransformAlias: false,
                        localRotation: fallbackRotation,
                        hasLocalRotation: true,
                        description: "white flame prefab");
                }

                // Vector (SSME) can look underpowered with only blue_small + model flame.
                // Add a Skipper-style blue flame core on the same transform as its stock blue_small prefab.
                bool isVector =
                    string.Equals(partName, "SSME", System.StringComparison.OrdinalIgnoreCase);
                if (isVector)
                {
                    bool hasSkipperStyleFlame = false;
                    for (int i = 0; i < prefabFxEntries.Count; i++)
                    {
                        string existingPrefab = NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
                        if (string.Equals(existingPrefab, "fx_exhaustFlame_blue", System.StringComparison.OrdinalIgnoreCase))
                        {
                            hasSkipperStyleFlame = true;
                            break;
                        }
                    }

                    if (!hasSkipperStyleFlame)
                    {
                        string fallbackTransform = "thrustTransformYup";
                        Vector3 fallbackOffset = Vector3.zero;
                        Quaternion fallbackRotation = Quaternion.identity;
                        bool fallbackHasLocalRotation = false;

                        bool copiedFromExistingBlueSmall = false;
                        for (int i = 0; i < prefabFxEntries.Count; i++)
                        {
                            string existingPrefab = NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
                            if (string.Equals(existingPrefab, "fx_exhaustFlame_blue_small", System.StringComparison.OrdinalIgnoreCase))
                            {
                                fallbackTransform = prefabFxEntries[i].transformName;
                                fallbackOffset = prefabFxEntries[i].localOffset;
                                fallbackRotation = prefabFxEntries[i].localRotation;
                                fallbackHasLocalRotation = prefabFxEntries[i].hasLocalRotation;
                                copiedFromExistingBlueSmall = true;
                                break;
                            }
                        }

                        if (!copiedFromExistingBlueSmall &&
                            !HasNamedTransform(fallbackTransform))
                        {
                            if (HasNamedTransform("thrustTransform"))
                                fallbackTransform = "thrustTransform";
                            else if (engine != null &&
                                !string.IsNullOrEmpty(engine.thrustVectorTransformName) &&
                                HasNamedTransform(engine.thrustVectorTransformName))
                                fallbackTransform = engine.thrustVectorTransformName;
                        }

                        prefabFxEntries.Add(("fx_exhaustFlame_blue", fallbackTransform, fallbackOffset, fallbackRotation, fallbackHasLocalRotation));
                        ParsekLog.Log($"    Engine FX fallback: '{partName}' midx={moduleIndex} " +
                            $"added Skipper-style blue flame prefab on '{fallbackTransform}' offset={fallbackOffset} rot={fallbackRotation.eulerAngles}");
                    }
                }

                // RAPIER can resolve to dark/perpendicular smoke when aeroSpike smoke prefab falls back.
                // Force a Vector-like visible plume: single large smoke + white flame core.
                if (isRapierPart)
                {
                    string rapierSmokeTransform = "smokePoint";
                    Vector3 rapierSmokeOffset = new Vector3(0f, 0f, 1f);
                    Quaternion rapierSmokeRotation = Quaternion.identity;
                    bool rapierSmokeHasLocalRotation = false;
                    bool copiedRapierSmokeAnchor = false;

                    int removedRapierSmoke = 0;
                    for (int i = prefabFxEntries.Count - 1; i >= 0; i--)
                    {
                        string existingPrefab = NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
                        if (existingPrefab == null ||
                            existingPrefab.IndexOf("smoketrail", System.StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        if (!copiedRapierSmokeAnchor)
                        {
                            rapierSmokeTransform = prefabFxEntries[i].transformName;
                            rapierSmokeOffset = prefabFxEntries[i].localOffset;
                            rapierSmokeRotation = prefabFxEntries[i].localRotation;
                            rapierSmokeHasLocalRotation = prefabFxEntries[i].hasLocalRotation;
                            copiedRapierSmokeAnchor = true;
                        }

                        prefabFxEntries.RemoveAt(i);
                        removedRapierSmoke++;
                    }

                    if (!copiedRapierSmokeAnchor)
                    {
                        if (!HasNamedTransform(rapierSmokeTransform))
                        {
                            if (HasNamedTransform("thrustTransform"))
                            {
                                rapierSmokeTransform = "thrustTransform";
                                rapierSmokeOffset = engine != null ? engine.fxOffset : Vector3.zero;
                            }
                            else if (engine != null &&
                                !string.IsNullOrEmpty(engine.thrustVectorTransformName) &&
                                HasNamedTransform(engine.thrustVectorTransformName))
                            {
                                rapierSmokeTransform = engine.thrustVectorTransformName;
                                rapierSmokeOffset = engine.fxOffset;
                            }
                        }
                    }

                    prefabFxEntries.Add(("fx_smokeTrail_large", rapierSmokeTransform, rapierSmokeOffset,
                        rapierSmokeRotation, rapierSmokeHasLocalRotation));
                    ParsekLog.Log($"    Engine FX fallback: '{partName}' midx={moduleIndex} " +
                        $"replaced {removedRapierSmoke} smoke entries with Vector-style smoke on '{rapierSmokeTransform}' " +
                        $"offset={rapierSmokeOffset} rot={rapierSmokeRotation.eulerAngles}");

                    bool hasWhiteFlame = false;
                    for (int i = 0; i < prefabFxEntries.Count; i++)
                    {
                        string existingPrefab = NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
                        if (string.Equals(existingPrefab, "fx_exhaustFlame_white", System.StringComparison.OrdinalIgnoreCase))
                        {
                            hasWhiteFlame = true;
                            break;
                        }
                    }

                    if (!hasWhiteFlame)
                    {
                        // Keep it compact: anchor the added white core to the same smokePoint frame.
                        string flameTransform = rapierSmokeTransform;
                        Vector3 flameOffset = Vector3.zero;
                        if (!HasNamedTransform(flameTransform))
                        {
                            flameTransform = "thrustTransform";
                            if (!HasNamedTransform(flameTransform) &&
                                engine != null &&
                                !string.IsNullOrEmpty(engine.thrustVectorTransformName) &&
                                HasNamedTransform(engine.thrustVectorTransformName))
                            {
                                flameTransform = engine.thrustVectorTransformName;
                            }
                        }

                        Quaternion flameRotation = Quaternion.Euler(-90f, 0f, 0f);
                        prefabFxEntries.Add(("fx_exhaustFlame_white", flameTransform, flameOffset, flameRotation, true));
                        ParsekLog.Log($"    Engine FX fallback: '{partName}' midx={moduleIndex} " +
                            $"added Vector-style white flame on '{flameTransform}' offset={flameOffset} rot={flameRotation.eulerAngles}");
                    }
                }

                if (modelFxEntries.Count == 0 && prefabFxEntries.Count == 0)
                {
                    // No EFFECTS node, or EFFECTS has no particle entries (e.g. Mainsail: AUDIO only).
                    // Fall through to legacy fx_* child search.
                    // Only process once per part — legacy FX are shared, not per-module.
                    if (moduleIndex > 0)
                    {
                        ParsekLog.Verbose("GhostVisual", $"    Engine '{partName}' midx={moduleIndex}: legacy FX already handled by midx=0");
                        continue;
                    }

                    bool legacyAdded = ProcessEngineLegacyFx(
                        prefab, engine, info, partName, moduleIndex,
                        modelRoot, ghostModelNode, cloneMap);
                    if (legacyAdded)
                        result.Add(info);
                    else
                        ParsekLog.Verbose("GhostVisual", $"    Engine '{partName}' midx={moduleIndex}: no legacy fx_* children found");
                    continue;
                }

                info.emissionCurve = emissionCurve;
                info.speedCurve = speedCurve;

                // Process model FX entries (MODEL_MULTI_PARTICLE/MODEL_PARTICLE variants).
                ProcessEngineModelFxEntries(modelFxEntries, prefab, info, partName, moduleIndex,
                    modelRoot, ghostModelNode, cloneMap);

                // Process PREFAB_PARTICLE entries (Spark, Twitch, Pug, Juno, Wheesley, Goliath)
                ProcessEnginePrefabFxEntries(prefabFxEntries, prefab, info, partName, moduleIndex,
                    modelRoot, ghostModelNode, cloneMap);

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
                    ParsekLog.Verbose("GhostVisual", $"    RCS '{partName}' midx={moduleIndex}: no partConfig — skipping FX");
                    continue;
                }

                ConfigNode effectsNode = partConfig.GetNode("EFFECTS");
                if (effectsNode == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"    RCS '{partName}' midx={moduleIndex}: no EFFECTS node — skipping FX");
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
                    ParsekLog.Verbose("GhostVisual", $"    RCS '{partName}' midx={moduleIndex}: no '{runningEffect}' effect group");
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
                    ParsekLog.Verbose("GhostVisual", $"    RCS '{partName}' midx={moduleIndex}: no FX transforms in '{runningEffect}' group");
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
                        ParsekLog.Verbose("GhostVisual", $"    RCS '{partName}' midx={moduleIndex}: transform '{transformName}' not found on prefab");
                        continue;
                    }

                    for (int t = 0; t < fxTransforms.Count; t++)
                    {
                        Transform srcFxTransform = fxTransforms[t];

                        Transform ghostFxParent = ResolveGhostFxParent(
                            srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                        if (ghostFxParent == null)
                        {
                            ParsekLog.Log($"    RCS '{partName}' midx={moduleIndex}: " +
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

                                    var emission = ps.emission;
                                    emission.enabled = true;
                                    emission.rateOverTimeMultiplier = 0;
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
                                    var diagPs = allPs[0];
                                    var diagRenderer = diagPs.GetComponent<ParticleSystemRenderer>();
                                    var diagMain = diagPs.main;
                                    ParsekLog.Verbose("GhostVisual", $"    RCS FX cloned: '{partName}' midx={moduleIndex} " +
                                        $"transform='{transformName}' model='{modelName}' " +
                                        $"offset={fxDefinitions[f].localOffset} " +
                                        $"rot={fxDefinitions[f].localRotation.eulerAngles} " +
                                        $"scale={fxDefinitions[f].localScale} " +
                                        $"active={diagPs.gameObject.activeInHierarchy} " +
                                        $"sim={diagMain.simulationSpace} playOnAwake={diagMain.playOnAwake} prewarm={diagMain.prewarm} " +
                                        $"renderer={(diagRenderer != null && diagRenderer.enabled)} " +
                                        $"totalSystems={allPs.Length}");
                                }
                            }
                            else
                            {
                                ParsekLog.Verbose("GhostVisual", $"    RCS FX model not found: '{modelName}' for '{partName}'");
                            }
                        }
                    }
                }

                if (info.particleSystems.Count > 0)
                {
                    ParsekLog.Verbose("GhostVisual", $"    RCS module ready: '{partName}' midx={moduleIndex} " +
                        $"particles={info.particleSystems.Count} " +
                        $"emissionScale={info.emissionScale:F1} speedScale={info.speedScale:F2}");
                    result.Add(info);
                }
                else
                {
                    ParsekLog.Verbose("GhostVisual", $"    RCS '{partName}' midx={moduleIndex}: no particle systems cloned");
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
        /// Reads the showMesh field from ModuleStructuralNodeToggle in the snapshot
        /// partNode. This is a KSPField serialized at runtime indicating whether
        /// internal structure should be visible when fairings are jettisoned.
        /// Defaults to true if the module or field is not found.
        /// </summary>
        internal static bool GetFairingShowMesh(ConfigNode partNode)
        {
            if (partNode == null) return true;

            ConfigNode toggleModule = FindModuleNode(partNode, "ModuleStructuralNodeToggle");
            if (toggleModule == null) return true;

            string showMeshVal = toggleModule.GetValue("showMesh");
            if (string.IsNullOrEmpty(showMeshVal)) return true;

            bool result;
            if (bool.TryParse(showMeshVal, out result))
                return result;
            return true;
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
                ParsekLog.Verbose("GhostVisual", $"    Fairing '{partName}' pid={persistentId}: " +
                    "no ModuleStructuralNode rootObject names found — no internal structure to hide");
                return;
            }

            // Permanently hide prefab Cap/Truss clones — they are at meaningless prefab default scale
            // (2000,10,2000). The procedural truss mesh on FairingGhostInfo.trussStructureObject
            // provides the correct post-jettison visual instead.
            var hidden = new List<GameObject>();
            CollectMatchingTransforms(ghostModelNode, structureNames, hidden);

            for (int i = 0; i < hidden.Count; i++)
                hidden[i].SetActive(false);

            ParsekLog.Verbose("GhostVisual", $"    Fairing '{partName}' pid={persistentId}: " +
                $"found {structureNames.Count} structure names [{string.Join(", ", structureNames)}], " +
                $"hidden {hidden.Count} prefab structure transforms (procedural truss used instead)");
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
                        ParsekLog.Verbose("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                            $"cloned material '{matName}' for variant texture");

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
                                ParsekLog.Verbose("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                                    $"applied shader='{rule.shaderName}'");
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
                                            ParsekLog.Verbose("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                                                $"applied {prop.key}={prop.value} (type=Texture, prop={shaderProp})");
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
                                            ParsekLog.Verbose("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                                                $"applied {prop.key}={prop.value} (type=Color)");
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
                                            ParsekLog.Verbose("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                                                $"applied {prop.key}={prop.value} (type=Float)");
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

        /// <summary>
        /// Generates a procedural truss structure mesh from XSECTION data: horizontal cap discs
        /// at each internal ring boundary and vertical struts connecting adjacent rings.
        /// Shown after fairing jettison to represent the exposed interstage structure.
        /// </summary>
        internal static Mesh GenerateFairingTrussMesh(
            List<(float h, float r)> sections, int nSides, Vector3 pivot, Vector3 axis)
        {
            if (sections.Count < 2) return null;

            sections.Sort((a, b) => a.h.CompareTo(b.h));
            nSides = Mathf.Min(nSides, 24);
            if (nSides < 3) nSides = 3;

            Quaternion axisRot = Quaternion.FromToRotation(Vector3.up, axis.normalized);
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            // --- Horizontal cap discs at each internal XSECTION boundary ---
            // Skip the bottom (index 0) and top (last index) — caps are only at interior boundaries.
            for (int i = 1; i < sections.Count - 1; i++)
            {
                float h = sections[i].h;
                float r = sections[i].r;
                if (r < 0.01f) continue;

                // Center vertex of the disc
                int centerIdx = vertices.Count;
                vertices.Add(axisRot * new Vector3(0f, h, 0f) + pivot);
                uvs.Add(new Vector2(0.5f, 0.5f));

                // Ring vertices
                int ringStart = vertices.Count;
                for (int s = 0; s <= nSides; s++)
                {
                    float angle = (float)s / nSides * Mathf.PI * 2f;
                    float x = Mathf.Cos(angle) * r;
                    float z = Mathf.Sin(angle) * r;
                    vertices.Add(axisRot * new Vector3(x, h, z) + pivot);
                    uvs.Add(new Vector2((Mathf.Cos(angle) + 1f) * 0.5f, (Mathf.Sin(angle) + 1f) * 0.5f));
                }

                // Triangle fan (both sides visible via doubled winding)
                for (int s = 0; s < nSides; s++)
                {
                    int left = ringStart + s;
                    int right = ringStart + s + 1;
                    // Top face
                    triangles.Add(centerIdx);
                    triangles.Add(left);
                    triangles.Add(right);
                    // Bottom face
                    triangles.Add(centerIdx);
                    triangles.Add(right);
                    triangles.Add(left);
                }
            }

            // --- Vertical struts connecting adjacent rings ---
            // Place struts at evenly-spaced azimuthal positions.
            int strutCount = Mathf.Max(nSides / 3, 6);
            float strutHalfWidth = 0.03f; // thin struts as fraction of local scale

            for (int s = 0; s < strutCount; s++)
            {
                float angle = (float)s / strutCount * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                // Perpendicular direction for strut width
                float perpCos = Mathf.Cos(angle + Mathf.PI * 0.5f);
                float perpSin = Mathf.Sin(angle + Mathf.PI * 0.5f);

                for (int i = 0; i < sections.Count - 1; i++)
                {
                    float h0 = sections[i].h;
                    float r0 = sections[i].r;
                    float h1 = sections[i + 1].h;
                    float r1 = sections[i + 1].r;

                    if (r0 < 0.01f || r1 < 0.01f) continue;

                    // Inset struts slightly inside the cone surface so they don't z-fight
                    float inset = 0.97f;
                    float w0 = r0 * strutHalfWidth;
                    float w1 = r1 * strutHalfWidth;

                    // Four corners of the strut quad
                    Vector3 bl = axisRot * new Vector3(cos * r0 * inset - perpCos * w0, h0, sin * r0 * inset - perpSin * w0) + pivot;
                    Vector3 br = axisRot * new Vector3(cos * r0 * inset + perpCos * w0, h0, sin * r0 * inset + perpSin * w0) + pivot;
                    Vector3 tl = axisRot * new Vector3(cos * r1 * inset - perpCos * w1, h1, sin * r1 * inset - perpSin * w1) + pivot;
                    Vector3 tr = axisRot * new Vector3(cos * r1 * inset + perpCos * w1, h1, sin * r1 * inset + perpSin * w1) + pivot;

                    int baseIdx = vertices.Count;
                    vertices.Add(bl); vertices.Add(br); vertices.Add(tl); vertices.Add(tr);
                    uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f));
                    uvs.Add(new Vector2(0f, 1f)); uvs.Add(new Vector2(1f, 1f));

                    // Both sides visible
                    triangles.Add(baseIdx); triangles.Add(baseIdx + 2); triangles.Add(baseIdx + 1);
                    triangles.Add(baseIdx + 1); triangles.Add(baseIdx + 2); triangles.Add(baseIdx + 3);
                    triangles.Add(baseIdx); triangles.Add(baseIdx + 1); triangles.Add(baseIdx + 2);
                    triangles.Add(baseIdx + 1); triangles.Add(baseIdx + 3); triangles.Add(baseIdx + 2);
                }
            }

            if (vertices.Count == 0) return null;

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
                    ParsekLog.Verbose("GhostVisual", $"    Fairing '{partName}': only {xNodes.Length} XSECTION(s) — skipping cone mesh");
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

            ParsekLog.Verbose("GhostVisual", $"    Fairing detected: '{partName}' pid={persistentId}, " +
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
                    ParsekLog.Verbose("GhostVisual", $"    Robotics '{partName}' midx={moduleIndex}: missing transform binding on {moduleName}");
                    continue;
                }

                Transform sourceServo = prefab.FindModelTransform(servoTransformName);
                if (sourceServo == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"    Robotics '{partName}' midx={moduleIndex}: servo transform '{servoTransformName}' not found");
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
                        ParsekLog.Verbose("GhostVisual", $"    Robotics '{partName}' midx={moduleIndex}: servo '{servoTransformName}' outside model root");
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
                ParsekLog.Verbose("GhostVisual", $"    Robotics detected: '{partName}' pid={persistentId} midx={moduleIndex} " +
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
                ParsekLog.Verbose("GhostVisual", $"    AnimateHeat '{partName}' pid={persistentId}: " +
                    $"filtering renderers to {affectedSet.Count} affected transforms from {affectedTransforms.Count} heat anim target(s)");
            }
            else
            {
                ParsekLog.Verbose("GhostVisual", $"    AnimateHeat '{partName}' pid={persistentId}: " +
                    $"no affected transforms provided, cloning all renderers (fallback)");
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

                    Material materialClone = new Material(source);
                    cloned[m] = materialClone;
                    clonedMaterials++;

                    string colorProperty = materialClone.HasProperty("_Color")
                        ? "_Color"
                        : null;
                    string emissiveProperty = TryGetHeatEmissiveProperty(materialClone);

                    if (colorProperty == null && emissiveProperty == null)
                        continue;

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

                // Only replace renderer materials if at least one is tracked for heat animation.
                // Renderers with no trackable properties keep their original sharedMaterials,
                // preventing color contamination from orphaned material clones.
                if (hasTrackedMaterial)
                    renderer.materials = cloned;
            }

            if (skippedRenderers > 0)
            {
                ParsekLog.Verbose("GhostVisual", $"    AnimateHeat '{partName}' pid={persistentId}: " +
                    $"skipped {skippedRenderers} renderer(s) outside heat-animated subtree");
            }

            if (trackedMaterials > 0)
            {
                ParsekLog.Verbose("GhostVisual", $"    AnimateHeat materials detected: '{partName}' pid={persistentId}, " +
                    $"tracked={trackedMaterials} cloned={clonedMaterials}");
                return materialStates;
            }

            if (clonedMaterials > 0)
            {
                ParsekLog.Verbose("GhostVisual", $"    AnimateHeat '{partName}' pid={persistentId}: " +
                    $"cloned {clonedMaterials} material(s) but no _Color/emissive properties found");
            }

            return null;
        }

        #region Extracted helpers for AddPartVisuals

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
                ParsekLog.Verbose("GhostVisual", $"    {moduleLabel} detected: '{partName}' pid={persistentId}, " +
                    $"{resolvedTransforms.Count}/{sampledStates.Count} transforms resolved");
                return new DeployableGhostInfo
                {
                    partPersistentId = persistentId,
                    transforms = resolvedTransforms
                };
            }

            if (logUnresolved)
            {
                ParsekLog.Verbose("GhostVisual", $"    {moduleLabel} '{partName}' pid={persistentId}: " +
                    $"sampled {sampledStates.Count} transforms but none resolved to ghost");
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
                ParsekLog.Verbose("GhostVisual",
                    $"      Light clone[{li}] '{srcLight.name}': " +
                    $"srcI={srcLight.intensity:F2} srcR={srcLight.range:F1} " +
                    $"-> ghostI={clonedIntensity:F2} ghostR={clonedRange:F1} " +
                    $"mode={ghostLight.renderMode}");
            }

            if (clonedLights.Count > 0)
            {
                ParsekLog.Verbose("GhostVisual", $"    Light detected: '{partName}' pid={persistentId}, " +
                    $"{clonedLights.Count} Light component(s) cloned");
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
                    ParsekLog.VerboseRateLimited("GhostVisual", $"dmg_smr_{partName}_{r}",
                        $"    Part '{partName}' pid={persistentId}: skipping damaged wheel " +
                        $"SkinnedMeshRenderer '{smr.name}' (ModuleWheelDamage.damagedTransformName match)", 60.0);
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

                        Transform ghostBone;
                        if (!cloneMap.TryGetValue(srcBone, out ghostBone))
                        {
                            if (IsDescendantOf(srcBone, modelRoot))
                            {
                                ghostBone = MirrorTransformChain(srcBone, modelRoot, modelNodeTransform, cloneMap);
                            }
                            else if (IsDescendantOf(srcBone, prefab.transform))
                            {
                                // EVA rigs can keep bones outside modelRoot (e.g. model01).
                                // Fall back to cloning from the full part hierarchy.
                                ghostBone = MirrorTransformChain(srcBone, prefab.transform, partRootTransform, cloneMap);
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
                            ghostRootBone = MirrorTransformChain(srcRootBone, modelRoot, modelNodeTransform, cloneMap);
                        }
                        else if (IsDescendantOf(srcRootBone, prefab.transform))
                        {
                            ghostRootBone = MirrorTransformChain(srcRootBone, prefab.transform, partRootTransform, cloneMap);
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
                skinnedCloned++;
                ParsekLog.VerboseRateLimited("GhostVisual", $"smr_{partName}_{r}",
                    $"    SMR[{r}] '{smr.gameObject.name}' mesh={smr.sharedMesh.name} " +
                    $"localPos={leaf.localPosition} localScale={leaf.localScale} " +
                    $"bones={resolvedBones}/{(smr.bones != null ? smr.bones.Length : 0)}", 60.0);
                if (usedPartRootFallbackForBones)
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"smr_fb_{partName}_{r}",
                        $"      SMR[{r}] '{smr.gameObject.name}': used part-root fallback for external bone transforms", 60.0);
                }
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
                ParsekLog.Verbose("GhostVisual", $"    {moduleLabel} '{partName}' pid={persistentId}: " +
                    $"transform '{transformName}' not found under modelRoot");
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
                ParsekLog.Verbose("GhostVisual", $"    {moduleLabel} deployable detected: '{partName}' pid={persistentId}, " +
                    $"transform='{transformName}' angle={deployAngleDegrees:F1} " +
                    $"resolved={resolvedTransforms.Count}");
                return new DeployableGhostInfo
                {
                    partPersistentId = persistentId,
                    transforms = resolvedTransforms
                };
            }

            return null;
        }

        #endregion

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
                    ParsekLog.VerboseRateLimited("GhostVisual", $"dmg_mr_{partName}_{r}",
                        $"    Part '{partName}' pid={persistentId}: skipping damaged wheel " +
                        $"MeshRenderer '{mr.gameObject.name}' (ModuleWheelDamage.damagedTransformName match)", 60.0);
                    continue;
                }
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                Transform leaf = MirrorTransformChain(mr.transform, modelRoot, modelNode.transform, cloneMap);
                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                meshCount++;
                ParsekLog.VerboseRateLimited("GhostVisual", $"mr_{partName}_{r}",
                    $"    MR[{r}] '{mr.gameObject.name}' mesh={mf.sharedMesh.name} " +
                    $"localPos={leaf.localPosition} localScale={leaf.localScale}", 60.0);
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
                    ParsekLog.Info("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                        $"variant '{texVariantName}' has {texRules.Count} TEXTURE rules");
                    int applied = ApplyVariantTextureRules(
                        modelNode.transform, texRules, partName, persistentId);
                    ParsekLog.Verbose("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                        $"applied {applied} variant texture properties");
                }
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
                        ParsekLog.Verbose("GhostVisual", $"    Canopy '{canopyName}' is outside modelRoot '{modelRoot.name}'" +
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
                                ParsekLog.Verbose("GhostVisual", $"      CANOPY-CLONE '{target.gameObject.name}' " +
                                    $"mesh={mf.sharedMesh.name}");
                            }
                            else
                            {
                                // No mesh directly on the transform — still mirror the chain
                                // so it enters cloneMap for lookup
                                MirrorTransformChain(target, canopyVisualRoot,
                                    subNode.transform, cloneMap);
                                ParsekLog.Verbose("GhostVisual", $"      CANOPY-CLONE '{target.gameObject.name}' (no mesh, chain only)");
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
                    parachuteInfo = new ParachuteGhostInfo
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
                        ghostCanopy.SetParent(partRoot.transform, false);
                        ghostCanopy.localPosition = Vector3.zero;
                        ghostCanopy.localRotation = Quaternion.identity;
                        ghostCanopy.localScale = Vector3.zero;
                        // EVA: both semi-deployed and deployed appear above the kerbal's head
                        parachuteInfo.semiDeployedCanopyPos = new Vector3(0f, 1f, 0f);
                        parachuteInfo.semiDeployedCanopyRot = Quaternion.Euler(270f, 0f, 0f);
                        parachuteInfo.deployedCanopyPos = new Vector3(0f, 1f, 0f);
                        parachuteInfo.deployedCanopyRot = Quaternion.Euler(270f, 0f, 0f);
                        ParsekLog.Verbose("GhostVisual", $"    EVA parachute: overriding semi/deployed pos=(0,1,0) rot=(270,0,0) " +
                            $"(animation sampled sdScale={sdScale} dScale={dScale})");
                    }
                    else if (canopySubtreeRoot != null)
                    {
                        // Non-EVA part with canopy outside modelRoot: reparent under
                        // subtree root so root-relative positions work.
                        ghostCanopy.SetParent(canopySubtreeRoot, false);
                        ghostCanopy.localPosition = Vector3.zero;
                        ghostCanopy.localRotation = Quaternion.identity;
                    }

                    ParsekLog.Verbose("GhostVisual", $"    Parachute detected: canopy='{canopyName}' cap='{capName}' " +
                        $"semiDeployed={parachuteInfo.semiDeployedSampled} sdScale={parachuteInfo.semiDeployedCanopyScale} " +
                        $"deployScale={parachuteInfo.deployedCanopyScale} " +
                        $"deployPos={parachuteInfo.deployedCanopyPos} " +
                        $"deployRot={parachuteInfo.deployedCanopyRot.eulerAngles} " +
                        $"parent='{ghostCanopy.parent.name}'");
                }
                else if (srcCanopy != null)
                {
                    ParsekLog.Verbose("GhostVisual", $"    Parachute '{canopyName}' found on prefab but not in cloneMap " +
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
                        ParsekLog.Verbose("GhostVisual", $"    Jettison '{jettisonName}' not found on prefab for module {moduleIndex} ({partName})");
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
                ParsekLog.Verbose("GhostVisual", $"    Jettison detected: '{string.Join(", ", resolvedJettisonNames.ToArray())}' pid={persistentId}");
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

            // Detect deployable parts (solar panels, antennas, radiators) and pre-resolve transform states
            ModuleDeployablePart deployable = prefab.FindModuleImplementing<ModuleDeployablePart>();
            if (deployable != null)
            {
                var sampledStates = SampleDeployableStates(prefab, deployable);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNode.transform, persistentId, partName, "Deployable", logUnresolved: true);
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
                        sampledStates, modelNode.transform, persistentId, partName, "Gear", logUnresolved: false);
                    break; // one deployment module per part
                }
            }
            else if (prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null)
            {
                ParsekLog.Verbose("GhostVisual", $"    WARNING: '{partName}' pid={persistentId} has both " +
                    $"ModuleDeployablePart and ModuleWheels.ModuleWheelDeployment — gear visuals skipped");
            }

            // Detect stock retractable ladders (RetractableLadder module) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasRetractableLadder)
            {
                var sampledStates = SampleLadderStates(prefab, ladderAnimName, ladderAnimRootName);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNode.transform, persistentId, partName, "Ladder", logUnresolved: true);
            }

            // Detect ModuleAnimationGroup deploy animations (e.g. drills) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasAnimationGroupDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, animationGroupDeployAnimName, null);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNode.transform, persistentId, partName, "AnimationGroup", logUnresolved: true);
            }

            // Detect standalone ModuleAnimateGeneric deploy animations (e.g. science/inflatable parts)
            if (deployableInfo == null && hasStandaloneAnimateGenericDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, standaloneAnimateGenericAnimName, null);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNode.transform, persistentId, partName, "AnimateGeneric", logUnresolved: true);
            }

            // Detect ModuleAeroSurface visual transforms (airbrakes/control surfaces).
            if (deployableInfo == null && hasAeroSurfaceDeploy)
            {
                deployableInfo = BuildSurfaceDeployableInfo(
                    modelRoot, modelNode.transform, cloneMap,
                    aeroSurfaceTransformName, aeroSurfaceDeployAngle,
                    persistentId, partName, "AeroSurface");
            }

            // Detect ModuleControlSurface visual transforms (elevons/rudders/prop blades).
            if (deployableInfo == null && hasControlSurfaceDeploy)
            {
                deployableInfo = BuildSurfaceDeployableInfo(
                    modelRoot, modelNode.transform, cloneMap,
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
                    ParsekLog.Verbose("GhostVisual", $"    RobotArmScanner '{partName}' candidate anim='{candidateAnim}' sampled={candidateCount}");
                    if (candidateCount > bestCount)
                    {
                        bestCount = candidateCount;
                        sampledStates = candidateStates;
                        selectedAnimName = candidateAnim;
                    }
                }

                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNode.transform, persistentId, partName, "RobotArmScanner", logUnresolved: true);
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
                            ParsekLog.Verbose("GhostVisual", $"    CargoBay '{partName}': DeployModuleIndex={deployIdx} " +
                                $"didn't resolve to ModuleAnimateGeneric (Modules.Count={prefab.Modules.Count}), " +
                                $"using fallback FindModuleImplementing");
                    }

                    if (animModule != null && !string.IsNullOrEmpty(animModule.animationName))
                    {
                        var sampledStates = SampleCargoBayStates(
                            prefab, animModule.animationName, cargoBay.closedPosition);
                        deployableInfo = ResolveSampledStatesToDeployableInfo(
                            sampledStates, modelNode.transform, persistentId, partName, "CargoBay", logUnresolved: true);

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

                            ParsekLog.Verbose("GhostVisual", $"    CargoBay '{partName}' pid={persistentId}: " +
                                $"closedPosition={cargoBay.closedPosition}");
                        }
                    }
                }
            }

            // Detect ModuleAnimateHeat / FXModuleAnimateThrottle visual states (thermal glow / heat-driven animation).
            if (hasAnyHeatAnim)
            {
                string heatSource = hasAnimateHeat ? "ModuleAnimateHeat"
                    : hasAnimateThrottle ? "FXModuleAnimateThrottle"
                    : "FXModuleAnimateRCS";
                List<HeatTransformState> resolvedHeatTransforms = null;
                var sampledHeatStates = SampleAnimateHeatStates(prefab, heatAnimName);
                if (sampledHeatStates != null && sampledHeatStates.Count > 0)
                {
                    resolvedHeatTransforms = new List<HeatTransformState>();
                    int unresolved = 0;
                    for (int s = 0; s < sampledHeatStates.Count; s++)
                    {
                        var (path, coldPos, coldRot, coldScale, medPos, medRot, medScale, hotPos, hotRot, hotScale) = sampledHeatStates[s];
                        Transform ghostT = FindTransformByPath(modelNode.transform, path);
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
                    BuildHeatMaterialStates(modelNode.transform, partName, persistentId, resolvedHeatTransforms);

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

                    ParsekLog.Verbose("GhostVisual", $"    {heatSource} detected: '{partName}' pid={persistentId}, anim='{heatAnimName}' " +
                        $"transforms={(resolvedHeatTransforms != null ? resolvedHeatTransforms.Count : 0)} " +
                        $"materials={(heatMaterialStates != null ? heatMaterialStates.Count : 0)}");
                    if (heatMaterialStates != null && heatMaterialStates.Count > 0)
                    {
                        ParsekLog.Verbose("GhostVisual",
                            $"[GhostVisual] Part '{partName}' pid={persistentId}: computed medium heat colors for {heatMaterialStates.Count} materials");
                    }
                }
                else
                {
                    ParsekLog.Verbose("GhostVisual", $"    {heatSource} '{partName}' pid={persistentId}: no transform/material deltas");
                }
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
                    ParsekLog.Verbose("GhostVisual",
                        $"    ColorChanger '{partName}' pid={persistentId}: skipped (no shaderProperty)");
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
                    ParsekLog.Verbose("GhostVisual",
                        $"    ColorChanger '{partName}' pid={persistentId}: skipped " +
                        $"(toggle={toggleInFlight}, property={shaderProperty})");
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
                    ParsekLog.Verbose("GhostVisual",
                        $"    ColorChanger '{partName}' pid={persistentId}: no renderers on ghost model");
                    continue;
                }

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

                    string patternType = isCabinLight ? "cabin" : "char";
                    ParsekLog.Verbose("GhostVisual",
                        $"    Part '{partName}' pid={persistentId}: built ColorChanger ghost info " +
                        $"({materialStates.Count} materials, property={shaderProperty}, type={patternType})");
                }
                else
                {
                    ParsekLog.Verbose("GhostVisual",
                        $"    ColorChanger '{partName}' pid={persistentId}: no materials with property '{shaderProperty}'");
                }
            }

            return results;
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
                if (parts.Length < 2) continue;

                float keyTime, keyValue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out keyTime) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out keyValue))
                {
                    parsed.Add((keyTime, keyValue));
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
            var heatPartIds = new HashSet<uint>();
            if (heatInfos != null)
            {
                foreach (var kvp in heatInfos)
                    heatPartIds.Add(kvp.Key);
            }

            var renderers = ghostRoot.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
            {
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
                        info.allClonedMaterials.Add(materialClone);
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

                        info.glowMaterials.Add(new HeatMaterialState
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
            }

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
                var combines = new System.Collections.Generic.List<CombineInstance>();
                Matrix4x4 rootWorldToLocal = ghostRoot.transform.worldToLocalMatrix;
                for (int m = 0; m < meshFilters.Length; m++)
                {
                    MeshFilter mf = meshFilters[m];
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (!mf.gameObject.activeInHierarchy) continue;
                    CombineInstance ci = default;
                    ci.mesh = mf.sharedMesh;
                    ci.transform = rootWorldToLocal * mf.transform.localToWorldMatrix;
                    combines.Add(ci);
                }

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
                    ParsekLog.Verbose("ReentryFx",
                        $"No mesh filters found on ghost #{ghostIndex} — using sphere emission fallback (expected for SMR-only parts)");
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

                ParsekLog.Log($"  ReentryFx: combined {combines.Count} meshes into emission shape " +
                    $"({(combinedMesh != null ? combinedMesh.vertexCount : 0)} verts) for ghost #{ghostIndex}");
            }

            // --- Fire shell overlay: collect mesh+transform pairs for DrawMesh ---
            // Used to re-draw the ghost geometry offset along velocity with additive blending,
            // approximating KSP's FXCamera replacement shader vertex displacement.
            {
                MeshFilter[] allMeshFilters = ghostRoot.GetComponentsInChildren<MeshFilter>(true);
                var shellMeshes = new List<FireShellMesh>();
                for (int m = 0; m < allMeshFilters.Length; m++)
                {
                    MeshFilter mf = allMeshFilters[m];
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (!mf.gameObject.activeInHierarchy) continue;
                    shellMeshes.Add(new FireShellMesh { mesh = mf.sharedMesh, transform = mf.transform });
                }
                info.fireShellMeshes = shellMeshes;

                Shader shellShader = Shader.Find("KSP/Particles/Additive");
                if (shellShader != null)
                {
                    var shellMat = new Material(shellShader);
                    shellMat.SetColor("_TintColor", ReentryFireShellColor);
                    info.fireShellMaterial = shellMat;
                    info.allClonedMaterials.Add(shellMat);
                }

                ParsekLog.Log($"  ReentryFx: {shellMeshes.Count} meshes collected for fire shell overlay on ghost #{ghostIndex}");
            }

            // --- Logging ---
            ParsekLog.Verbose("ReentryFx",
                $"Built for ghost #{ghostIndex} \"{vesselName}\" — fireParticles={info.fireParticles != null}, " +
                $"fireShell={info.fireShellMeshes?.Count ?? 0} meshes, " +
                $"glow materials={info.glowMaterials.Count}, vesselLength={vesselLength:F1}m");
            if (skippedHeatCount > 0)
                ParsekLog.Verbose("ReentryFx",
                    $"Skipped {skippedHeatCount} renderers already managed by HeatGhostInfo for ghost #{ghostIndex}");

            return info;
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
            var combines = new System.Collections.Generic.List<CombineInstance>();
            Matrix4x4 rootWorldToLocal = ghostRoot.transform.worldToLocalMatrix;
            for (int m = 0; m < meshFilters.Length; m++)
            {
                MeshFilter mf = meshFilters[m];
                if (mf == null || mf.sharedMesh == null) continue;
                CombineInstance ci = default;
                ci.mesh = mf.sharedMesh;
                ci.transform = rootWorldToLocal * mf.transform.localToWorldMatrix;
                combines.Add(ci);
            }

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
            var shellMeshes = new List<FireShellMesh>();
            for (int m = 0; m < meshFilters.Length; m++)
            {
                MeshFilter mf = meshFilters[m];
                if (mf == null || mf.sharedMesh == null) continue;
                shellMeshes.Add(new FireShellMesh { mesh = mf.sharedMesh, transform = mf.transform });
            }
            info.fireShellMeshes = shellMeshes;

            ParsekLog.Log($"  ReentryFx: rebuilt emission mesh ({combines.Count} meshes, " +
                $"{(info.combinedEmissionMesh != null ? info.combinedEmissionMesh.vertexCount : 0)} verts) " +
                $"and {shellMeshes.Count} fire shell meshes after decouple");
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
                return 2f;

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

            if (!initialized) return 2f;

            // Y axis is typically nose-to-tail in KSP vessel space
            float length = combined.size.y;
            return Mathf.Max(length, 2f);
        }

        // Cached soft-circle texture for explosion FX (avoids per-explosion allocation in looping playback)
        private static Texture2D cachedExplosionTexture;

        /// <summary>
        /// Self-cleanup component: destroys dynamically created Materials when the
        /// GameObject is destroyed (prevents Material leak on fire-and-forget objects).
        /// </summary>
        private class MaterialCleanup : MonoBehaviour
        {
            public Material material;
            void OnDestroy()
            {
                if (material != null)
                    Destroy(material);
            }
        }

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

            ParsekLog.Verbose("PartPuffFx",
                $"Created at ({worldPosition.x:F1},{worldPosition.y:F1},{worldPosition.z:F1}) scale={scale:F2}" +
                $" (additive sparks={additiveShader != null})");

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

            ParsekLog.Info("ExplosionFx",
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
                    ParsekLog.Warn("GhostBuild",
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
                    ParsekLog.Warn("GhostBuild",
                        $"Flag vessel spawn failed: ProtoVessel.Load() produced null vesselRef");
                    return null;
                }

                ParsekLog.Verbose("GhostBuild",
                    $"Spawned flag vessel: '{vesselName}' by '{evt.placedBy}' " +
                    $"at ({evt.latitude:F4},{evt.longitude:F4},{evt.altitude:F1}) on {evt.bodyName}");

                return pv.vesselRef.gameObject;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("GhostBuild",
                    $"Failed to spawn flag vessel: {ex.Message}");
                return null;
            }
        }

    }
}
