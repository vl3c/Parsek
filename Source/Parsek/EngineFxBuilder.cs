using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Builds engine particle FX for ghost vessels.
    /// Extracted from GhostVisualBuilder — owns TryBuildEngineFX and its direct helpers.
    /// Calls back to GhostVisualBuilder for shared FX utilities (FindFxPrefab,
    /// StripKspFxControllers, ConfigureGhostEngineParticleSystems, etc.).
    /// </summary>
    internal static class EngineFxBuilder
    {
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
                            GhostVisualBuilder.TryParseVector3(mmpOffsetStr, out mmpLocalPos);

                            Quaternion mmpLocalRot = Quaternion.identity;
                            string mmpRotStr = modelNodes[mp].GetValue("localRotation");
                            if (!GhostVisualBuilder.TryParseFxLocalRotation(mmpRotStr, out mmpLocalRot))
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
                    GhostVisualBuilder.TryParseVector3(offsetStr, out localOffset);

                    Quaternion localRot = Quaternion.identity;
                    string rotStr = ppNodes[pp].GetValue("localRotation");
                    bool hasLocalRot = GhostVisualBuilder.TryParseFxLocalRotation(rotStr, out localRot);

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
                var namedAnchors = GhostVisualBuilder.FindTransformsRecursive(prefab.transform, engine.thrustVectorTransformName);
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
                        Transform ghostLegacyParent = GhostVisualBuilder.ResolveGhostFxParent(
                            srcLegacyAnchor, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                        if (ghostLegacyParent == null)
                            continue;

                        GameObject fxClone = Object.Instantiate(child.gameObject);
                        fxClone.transform.SetParent(ghostLegacyParent, false);
                        fxClone.transform.localPosition = legacyFxOffset;
                        Quaternion legacyLocalRot = Quaternion.Inverse(srcLegacyAnchor.rotation) * child.rotation;
                        fxClone.transform.localRotation = legacyLocalRot;
                        fxClone.transform.localScale = child.localScale;

                        GhostVisualBuilder.StripKspFxControllers(fxClone, info.kspEmitters);

                        int addedSystems = GhostVisualBuilder.ConfigureGhostEngineParticleSystems(fxClone, info.particleSystems);
                        if (addedSystems > 0)
                        {
                            clonesAdded++;
                            GhostVisualBuilder.LogFxInstancePlacementDiagnostic(partName, moduleIndex, "LEGACY_CHILD",
                                srcLegacyAnchor.name, child.name, prefab.transform, ghostModelNode,
                                srcLegacyAnchor, ghostLegacyParent, fxClone.transform,
                                legacyFxOffset, legacyLocalRot, true);
                            ParsekLog.VerboseRateLimited("EngineFx", $"legacy-{partName}-{moduleIndex}",
                                $"Engine FX (legacy): '{partName}' midx={moduleIndex} fx='{child.name}' systems={addedSystems}");
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

                    GhostVisualBuilder.StripKspFxControllers(fxClone, info.kspEmitters);

                    int addedSystems = GhostVisualBuilder.ConfigureGhostEngineParticleSystems(fxClone, info.particleSystems);
                    if (addedSystems > 0)
                    {
                        Transform fallbackParent = fxClone.transform.parent != null
                            ? fxClone.transform.parent : ghostModelNode;
                        GhostVisualBuilder.LogFxInstancePlacementDiagnostic(partName, moduleIndex, "LEGACY_CHILD_FALLBACK",
                            child.name, child.name, prefab.transform, ghostModelNode,
                            child, fallbackParent, fxClone.transform,
                            child.localPosition, child.localRotation, true);
                        ParsekLog.VerboseRateLimited("EngineFx", $"legacy-{partName}-{moduleIndex}",
                            $"Engine FX (legacy): '{partName}' midx={moduleIndex} fx='{child.name}' systems={addedSystems}");
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

                var fxTransforms = GhostVisualBuilder.FindTransformsRecursive(prefab.transform, transformName);
                for (int t = 0; t < fxTransforms.Count; t++)
                {
                    Transform srcFxTransform = fxTransforms[t];

                    Transform ghostFxParent = GhostVisualBuilder.ResolveGhostFxParent(
                        srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                    if (ghostFxParent == null)
                    {
                        ParsekLog.Log($"    Engine FX: '{partName}' midx={moduleIndex} " +
                            $"transform='{transformName}' parent resolution failed");
                        continue;
                    }
                    GhostVisualBuilder.LogFxParentAlignmentDiagnostic(partName, moduleIndex, nodeType, transformName,
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

                            GhostVisualBuilder.StripKspFxControllers(fxInstance, info.kspEmitters);

                            int addedSystems = GhostVisualBuilder.ConfigureGhostEngineParticleSystems(fxInstance, info.particleSystems);
                            if (addedSystems > 0)
                            {
                                GhostVisualBuilder.LogFxInstancePlacementDiagnostic(partName, moduleIndex, nodeType, transformName,
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
                string normalizedPrefabName = GhostVisualBuilder.NormalizeFxPrefabName(prefabName);
                bool isRapierWhiteFlame =
                    string.Equals(partName, "RAPIER", System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(normalizedPrefabName, "fx_exhaustFlame_white", System.StringComparison.OrdinalIgnoreCase);

                GameObject fxPrefab = GhostVisualBuilder.FindFxPrefab(prefabName);
                if (fxPrefab == null)
                {
                    ParsekLog.Log($"    Engine FX prefab not found: '{prefabName}' for '{partName}'");
                    continue;
                }

                var fxTransforms = GhostVisualBuilder.FindTransformsRecursive(prefab.transform, transformName);
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

                    Transform ghostFxParent = GhostVisualBuilder.ResolveGhostFxParent(
                        srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                    if (ghostFxParent == null)
                    {
                        ParsekLog.Log($"    Engine FX (prefab): '{partName}' midx={moduleIndex} " +
                            $"transform='{transformName}' parent resolution failed");
                        continue;
                    }
                    GhostVisualBuilder.LogFxParentAlignmentDiagnostic(partName, moduleIndex, "PREFAB_PARTICLE", transformName,
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

                    GhostVisualBuilder.StripKspFxControllers(fxInstance, info.kspEmitters);

                    int addedSystems = GhostVisualBuilder.ConfigureGhostEngineParticleSystems(fxInstance, info.particleSystems);
                    if (addedSystems > 0)
                    {
                        GhostVisualBuilder.LogFxInstancePlacementDiagnostic(partName, moduleIndex, "PREFAB_PARTICLE", transformName,
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

        internal static List<EngineGhostInfo> TryBuildEngineFX(
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

                    List<Transform> found = GhostVisualBuilder.FindTransformsRecursive(prefab.transform, transformName);
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
                        string existingPrefab = GhostVisualBuilder.NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
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
                        string existingPrefab = GhostVisualBuilder.NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
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
                        string existingPrefab = GhostVisualBuilder.NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
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
                        string existingPrefab = GhostVisualBuilder.NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
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
                            string existingPrefab = GhostVisualBuilder.NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
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
                        string existingPrefab = GhostVisualBuilder.NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
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
                        string existingPrefab = GhostVisualBuilder.NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
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
    }
}
