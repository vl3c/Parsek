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
        private static readonly string[] EngineModelNodeTypes =
            { "MODEL_MULTI_PARTICLE_PERSIST", "MODEL_MULTI_PARTICLE", "MODEL_PARTICLE" };
        private const double HotPathLogIntervalSeconds = 5.0;

        internal struct ModelFxConfigEntry
        {
            public string nodeType;
            public string transformName;
            public string modelName;
            public Vector3 localPos;
            public string rawLocalRotation;
            public string groupName;
        }

        internal struct PrefabFxConfigEntry
        {
            public string prefabName;
            public string transformName;
            public Vector3 localOffset;
            public string rawLocalRotation;
            public string groupName;
        }

        internal enum PrefabParticleRotationMode
        {
            UseConfiguredRotation,
            UseIdentityRotation,
            UseMinus90XRotation
        }

        private static void LogHotPathVerbose(string rateLimitKey, string message)
        {
            ParsekLog.VerboseRateLimited("EngineFx", rateLimitKey, message, HotPathLogIntervalSeconds);
        }

        internal static void FilterEffectGroups(
            ConfigNode[] allGroups,
            string[] allGroupNames,
            HashSet<string> moduleGroups,
            out ConfigNode[] effectGroups,
            out string[] effectGroupNames)
        {
            effectGroups = allGroups ?? new ConfigNode[0];
            effectGroupNames = allGroupNames ?? new string[0];
            if (effectGroups.Length == 0 ||
                moduleGroups == null ||
                moduleGroups.Count == 0)
            {
                return;
            }

            var filtered = new List<ConfigNode>();
            var filteredNames = new List<string>();
            int unnamedGroupFallbackCount = 0;
            for (int eg = 0; eg < effectGroups.Length; eg++)
            {
                string groupName;
                if (eg < effectGroupNames.Length)
                {
                    groupName = effectGroupNames[eg];
                }
                else
                {
                    groupName = "?";
                    unnamedGroupFallbackCount++;
                }

                if (!moduleGroups.Contains(groupName))
                    continue;

                filtered.Add(effectGroups[eg]);
                filteredNames.Add(groupName);
            }

            if (unnamedGroupFallbackCount > 0)
            {
                ParsekLog.Verbose("EngineFx",
                    $"FilterEffectGroups: {unnamedGroupFallbackCount} EFFECTS group names missing; using '?' fallback");
            }

            if (filtered.Count == 0)
            {
                ParsekLog.Verbose("EngineFx",
                    $"FilterEffectGroups: no module-group matches; keeping all {effectGroups.Length} EFFECTS groups");
                return;
            }

            effectGroups = filtered.ToArray();
            effectGroupNames = filteredNames.ToArray();
        }

        private static string[] BuildGroupNames(ConfigNode[] allGroups)
        {
            string[] allGroupNames = new string[allGroups.Length];
            for (int i = 0; i < allGroups.Length; i++)
                allGroupNames[i] = allGroups[i].name ?? "?";
            return allGroupNames;
        }

        internal static bool TryReadModelFxConfigEntry(
            string nodeType,
            ConfigNode modelNode,
            string groupName,
            out ModelFxConfigEntry entry)
        {
            entry = default(ModelFxConfigEntry);
            if (modelNode == null)
            {
                ParsekLog.Verbose("EngineFx",
                    $"TryReadModelFxConfigEntry: skipped null node (type={nodeType}, group='{groupName}')");
                return false;
            }

            string transformName = modelNode.GetValue("transformName");
            if (string.IsNullOrEmpty(transformName))
            {
                ParsekLog.Verbose("EngineFx",
                    $"TryReadModelFxConfigEntry: skipped node without transformName (type={nodeType}, group='{groupName}')");
                return false;
            }

            entry.nodeType = nodeType;
            entry.transformName = transformName;
            entry.modelName = modelNode.GetValue("modelName") ?? string.Empty;
            entry.groupName = groupName;
            entry.rawLocalRotation = modelNode.GetValue("localRotation");

            string localOffset = modelNode.GetValue("localPosition");
            if (string.IsNullOrEmpty(localOffset))
                localOffset = modelNode.GetValue("localOffset");
            GhostVisualBuilder.TryParseVector3(localOffset, out entry.localPos);
            return true;
        }

        internal static bool TryReadPrefabParticleConfigEntry(
            ConfigNode prefabNode,
            string groupName,
            out PrefabFxConfigEntry entry)
        {
            entry = default(PrefabFxConfigEntry);
            if (prefabNode == null)
            {
                ParsekLog.Verbose("EngineFx",
                    $"TryReadPrefabParticleConfigEntry: skipped null prefab node (group='{groupName}')");
                return false;
            }

            entry.prefabName = prefabNode.GetValue("prefabName");
            entry.transformName = prefabNode.GetValue("transformName");
            if (string.IsNullOrEmpty(entry.prefabName) || string.IsNullOrEmpty(entry.transformName))
            {
                ParsekLog.Verbose("EngineFx",
                    $"TryReadPrefabParticleConfigEntry: skipped prefab with missing identity (group='{groupName}')");
                return false;
            }

            string lower = entry.prefabName.ToLowerInvariant();
            if (lower.Contains("flameout") || lower.Contains("sparks") || lower.Contains("debris"))
            {
                ParsekLog.Verbose("EngineFx",
                    $"TryReadPrefabParticleConfigEntry: skipped transient prefab '{entry.prefabName}'");
                return false;
            }

            entry.groupName = groupName;
            entry.rawLocalRotation = prefabNode.GetValue("localRotation");

            // Stock PrefabParticleFX has no localPosition key: the Spark's config carries one and the
            // real flame sits on the transform.
            GhostVisualBuilder.TryParseVector3(prefabNode.GetValue("localOffset"), out entry.localOffset);
            return true;
        }

        /// <summary>
        /// Reads effect group names referenced by a single engine module.
        /// Returns the set of EFFECTS group names this module uses (for per-module filtering).
        /// Empty set means no filtering (base ModuleEngines without named effects).
        /// </summary>
        internal static HashSet<string> GetModuleEffectGroupNames(ModuleEngines engine)
        {
            var result = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var engineFX = engine as ModuleEnginesFX;
            if (engineFX == null)
                return result;

            if (!string.IsNullOrEmpty(engineFX.runningEffectName)) result.Add(engineFX.runningEffectName);
            if (!string.IsNullOrEmpty(engineFX.powerEffectName)) result.Add(engineFX.powerEffectName);
            if (!string.IsNullOrEmpty(engineFX.spoolEffectName)) result.Add(engineFX.spoolEffectName);
            if (!string.IsNullOrEmpty(engineFX.directThrottleEffectName)) result.Add(engineFX.directThrottleEffectName);
            return result;
        }

        /// <summary>
        /// Logs the FX emission direction relative to ground for a placed FX instance.
        /// ParticleSystems emit along local +Y by default; reports that axis in world space
        /// plus the angle from straight down (0 = correct for downward engine on pad).
        /// </summary>
        private static void LogFxDirection(string partName, int moduleIndex, string fxType,
            string transformName, string fxName, Transform fxTransform)
        {
            Vector3 emitWorld = fxTransform.up; // local +Y in world space
            float angle = Vector3.Angle(emitWorld, Vector3.down);
            Quaternion localRot = fxTransform.localRotation;
            ParsekLog.VerboseRateLimited("EngineFx", $"fxdir-{partName}-{moduleIndex}-{fxType}-{transformName}",
                $"#242 dir: '{partName}' midx={moduleIndex} " +
                $"type={fxType} transform='{transformName}' fx='{fxName}' " +
                $"emitWorld={emitWorld} angleFromDown={angle:F1} localRot={localRot.eulerAngles}");
        }

        /// <summary>
        /// Scans EFFECTS config groups for MODEL_MULTI_PARTICLE/MODEL_PARTICLE entries.
        /// Populates modelFxEntries with transform/model/offset/rotation tuples and extracts
        /// the first emission and speed curves found.
        /// </summary>
        internal static void ScanEffectsModelFxEntries(
            ConfigNode[] effectGroups, string[] effectGroupNames,
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)> modelFxEntries,
            ref FloatCurve emissionCurve,
            ref FloatCurve speedCurve)
        {
            for (int g = 0; g < effectGroups.Length; g++)
            {
                string groupName = g < effectGroupNames.Length ? effectGroupNames[g] : "?";
                for (int n = 0; n < EngineModelNodeTypes.Length; n++)
                {
                    string nodeType = EngineModelNodeTypes[n];
                    ConfigNode[] modelNodes = effectGroups[g].GetNodes(nodeType);
                    for (int mp = 0; mp < modelNodes.Length; mp++)
                    {
                        if (TryReadModelFxConfigEntry(
                            nodeType,
                            modelNodes[mp],
                            groupName,
                            out ModelFxConfigEntry entry))
                        {
                            // Stock ModelMultiParticleFX / ModelParticleFX: an absent localRotation is
                            // identity, whatever the model (TryParse leaves identity on a miss).
                            GhostVisualBuilder.TryParseFxLocalRotation(
                                entry.rawLocalRotation, out Quaternion mmpLocalRot);

                            modelFxEntries.Add((
                                entry.nodeType,
                                entry.transformName,
                                entry.modelName,
                                entry.localPos,
                                mmpLocalRot,
                                entry.groupName,
                                GhostEngineFxCurves.ForNode(nodeType, modelNodes[mp])));

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
        internal static void ScanEffectsPrefabParticleEntries(
            ConfigNode[] effectGroups, string[] effectGroupNames,
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)> prefabFxEntries)
        {
            for (int g = 0; g < effectGroups.Length; g++)
            {
                string groupName = g < effectGroupNames.Length ? effectGroupNames[g] : "?";
                ConfigNode[] ppNodes = effectGroups[g].GetNodes("PREFAB_PARTICLE");
                for (int pp = 0; pp < ppNodes.Length; pp++)
                {
                    if (!TryReadPrefabParticleConfigEntry(
                        ppNodes[pp],
                        groupName,
                        out PrefabFxConfigEntry entry))
                        continue;

                    Quaternion localRot = Quaternion.identity;
                    bool hasLocalRot = GhostVisualBuilder.TryParseFxLocalRotation(entry.rawLocalRotation, out localRot);

                    prefabFxEntries.Add((
                        entry.prefabName,
                        entry.transformName,
                        entry.localOffset,
                        localRot,
                        hasLocalRot,
                        entry.groupName,
                        GhostEngineFxCurves.ForNode("PREFAB_PARTICLE", ppNodes[pp])));
                }
            }
        }

        internal static PrefabParticleRotationMode ResolvePrefabParticleRotationMode(
            bool hasLocalRotation,
            Vector3 parentUp)
        {
            if (hasLocalRotation)
                return PrefabParticleRotationMode.UseConfiguredRotation;

            float yComponent = Mathf.Abs(parentUp.y);
            return yComponent > 0.5f
                ? PrefabParticleRotationMode.UseIdentityRotation
                : PrefabParticleRotationMode.UseMinus90XRotation;
        }

        internal static Quaternion ResolvePrefabParticleLocalRotation(
            bool hasLocalRotation,
            Quaternion configuredLocalRotation,
            Vector3 parentUp)
        {
            PrefabParticleRotationMode mode = ResolvePrefabParticleRotationMode(hasLocalRotation, parentUp);
            if (mode == PrefabParticleRotationMode.UseConfiguredRotation)
                return configuredLocalRotation;

            return mode == PrefabParticleRotationMode.UseIdentityRotation
                ? Quaternion.identity
                : Quaternion.Euler(-90f, 0f, 0f);
        }

        /// <summary>
        /// Stock <c>ModuleEngines.AutoPlaceFXGroup</c> (decompiled): a legacy system under its thrust
        /// transform is set to identity, then <c>Rotate(-90, 0, 0)</c>, whatever the part-frame
        /// rotation its <c>fx_*</c> key gave it (a separation motor points each of its nozzles
        /// another way). A literal because <c>Quaternion.Euler</c> is native and cannot run headless.
        /// </summary>
        internal static readonly Quaternion LegacyFxLocalRotation =
            new Quaternion(-0.70710677f, 0f, 0f, 0.70710677f);

        /// <summary>What stock's <c>PartLoader</c> adds to the name of an <c>fx_*</c> child whose key
        /// ends in <c>true</c> (decompiled).</summary>
        internal const string LegacyFxKeepPositionMark = "Keep Pos";

        /// <summary>
        /// Pure: where stock <c>ModuleEngines.AutoPlaceFXGroup</c> (decompiled) puts a legacy system
        /// under its thrust transform: the engine's <c>fxOffset</c>, or the position its key authored
        /// when the key asks to keep it (the Mainsail's two verniers).
        /// </summary>
        internal static Vector3 LegacyFxLocalPosition(string fxObjectName, Vector3 authoredPosition, Vector3 fxOffset)
            => fxObjectName != null && fxObjectName.Contains(LegacyFxKeepPositionMark) ? authoredPosition : fxOffset;

        /// <summary>
        /// Processes legacy PART-level fx_* child entries when no EFFECTS node is present.
        /// Clones particle systems from prefab children onto ghost thrust anchors.
        /// Returns true if any particle systems were added to the info.
        /// </summary>
        private static bool ProcessEngineLegacyFx(
            Part prefab, ModuleEngines engine, EngineGhostInfo info,
            string partName, int moduleIndex,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap,
            Dictionary<string, bool> selectedVariantGameObjects)
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

            // #242c: filter out thrustTransforms under disabled variant GameObjects.
            // engine.thrustTransforms is populated by KSP from ALL matching transforms
            // in the prefab, regardless of which variant is active.
            bool variantFilteredAllAnchors = false;
            if (selectedVariantGameObjects != null && selectedVariantGameObjects.Count > 0)
            {
                int preFilter = legacyAnchors.Count;
                legacyAnchors.RemoveAll(t =>
                    !GhostVisualBuilder.IsRendererEnabledByVariantRule(
                        t, modelRoot, selectedVariantGameObjects, out _, out _));
                if (legacyAnchors.Count < preFilter)
                {
                    LogHotPathVerbose($"variant-legacy-anchor-filter-{partName}-{moduleIndex}",
                        $"'{partName}' midx={moduleIndex}: " +
                        $"variant filter removed {preFilter - legacyAnchors.Count} of {preFilter} " +
                        $"legacy thrust anchors");
                    if (legacyAnchors.Count == 0)
                        variantFilteredAllAnchors = true;
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

                GhostEngineFxRuleKind legacyKind = ResolveLegacyFxRuleKind(
                    IsInFxGroup(prefab, "running", ps), IsInFxGroup(prefab, "power", ps));
                // Stock groups the child's own system, or every system under it when it has none.
                bool ownSystemOnly = ps.transform == child;
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
                        // Stock AutoPlaceFXGroup parents the group's system keeping its world scale.
                        fxClone.transform.SetParent(ghostLegacyParent, true);
                        Vector3 legacyLocalPos = LegacyFxLocalPosition(child.name, child.localPosition, legacyFxOffset);
                        fxClone.transform.localPosition = legacyLocalPos;
                        // A system stock does not move itself keeps the child's part-frame rotation.
                        Quaternion legacyLocalRot = ownSystemOnly
                            ? LegacyFxLocalRotation
                            : Quaternion.Inverse(srcLegacyAnchor.rotation) * child.rotation;
                        fxClone.transform.localRotation = legacyLocalRot;

                        GhostVisualBuilder.StripKspFxControllers(fxClone, info.kspEmitters, info.smokeTrailFades);

                        int addedSystems = GhostVisualBuilder.ConfigureGhostEngineParticleSystems(fxClone, info.particleSystems);
                        if (addedSystems > 0)
                        {
                            GhostFxEmissionProbe.AttachIfNew(fxClone, partName, moduleIndex, child.name);
                            AddLegacyFxRules(info, legacyKind, addedSystems, ownSystemOnly);
                            clonesAdded++;
                            GhostVisualBuilder.LogFxInstancePlacementDiagnostic(partName, moduleIndex, "LEGACY_CHILD",
                                srcLegacyAnchor.name, child.name, prefab.transform, ghostModelNode,
                                srcLegacyAnchor, ghostLegacyParent, fxClone.transform,
                                legacyLocalPos, legacyLocalRot, true);
                            ParsekLog.VerboseRateLimited("EngineFx", $"legacy-{partName}-{moduleIndex}",
                                $"Engine FX (legacy): '{partName}' midx={moduleIndex} fx='{child.name}' systems={addedSystems}");
                            LogFxDirection(partName, moduleIndex, "LEGACY", srcLegacyAnchor.name, child.name, fxClone.transform);
                        }
                        else
                        {
                            Object.Destroy(fxClone);
                        }
                    }
                }

                // #242c: skip fallback placement when variant filtering intentionally
                // emptied the anchor list — the active variant has no thrust transforms
                // for this engine, so placing FX at the model root would be incorrect.
                if (clonesAdded == 0 && !variantFilteredAllAnchors)
                {
                    GameObject fxClone = Object.Instantiate(child.gameObject);
                    fxClone.transform.SetParent(ghostModelNode.parent, false);
                    fxClone.transform.localPosition = child.localPosition;
                    fxClone.transform.localRotation = child.localRotation;
                    fxClone.transform.localScale = child.localScale;

                    GhostVisualBuilder.StripKspFxControllers(fxClone, info.kspEmitters, info.smokeTrailFades);

                    int addedSystems = GhostVisualBuilder.ConfigureGhostEngineParticleSystems(fxClone, info.particleSystems);
                    if (addedSystems > 0)
                    {
                        AddLegacyFxRules(info, legacyKind, addedSystems, ownSystemOnly);
                        Transform fallbackParent = fxClone.transform.parent != null
                            ? fxClone.transform.parent : ghostModelNode;
                        GhostVisualBuilder.LogFxInstancePlacementDiagnostic(partName, moduleIndex, "LEGACY_CHILD_FALLBACK",
                            child.name, child.name, prefab.transform, ghostModelNode,
                            child, fallbackParent, fxClone.transform,
                            child.localPosition, child.localRotation, true);
                        LogFxDirection(partName, moduleIndex, "LEGACY_FALLBACK", child.name, child.name, fxClone.transform);
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
        /// The group whose power sizes a legacy <c>fx_*</c> child. A child in neither is read as a
        /// running effect; so is one in both, which stock draws twice and no stock part authors.
        /// </summary>
        internal static GhostEngineFxRuleKind ResolveLegacyFxRuleKind(bool inRunning, bool inPower)
            => inPower && !inRunning ? GhostEngineFxRuleKind.LegacyPower : GhostEngineFxRuleKind.LegacyRunning;

        private static bool IsInFxGroup(Part prefab, string groupName, ParticleSystem system)
        {
            FXGroup group = prefab != null ? prefab.findFxGroup(groupName) : null;
            return group != null && group.fxEmittersNewSystem != null
                && group.fxEmittersNewSystem.Contains(system);
        }

        /// <summary>
        /// One rule per system of a legacy clone, over the asset's own size and lifetime ends
        /// (<c>FXGroup.begin</c>). The systems are the last <paramref name="added"/> of the info.
        /// </summary>
        private static void AddLegacyFxRules(
            EngineGhostInfo info, GhostEngineFxRuleKind kind, int added, bool firstOnly)
        {
            int first = info.particleSystems.Count - added;
            int end = firstOnly ? first + 1 : info.particleSystems.Count;
            for (int i = first; i < end; i++)
            {
                ParticleSystem ps = info.particleSystems[i];
                if (ps == null) continue;
                var main = ps.main;
                ParticleSystem.MinMaxCurve life = main.startLifetime;
                ParticleSystem.MinMaxCurve size = main.startSize;
                var rule = new GhostEngineFxRule { kind = kind, system = ps };
                GhostEngineFxPower.LegacyRange(life.constantMin, life.constantMax, out rule.minLife, out rule.maxLife);
                GhostEngineFxPower.LegacyRange(size.constantMin, size.constantMax, out rule.minSize, out rule.maxSize);
                info.fxRules.Add(rule);
            }
        }

        private static GhostEngineFxRule NewEffectsRule(
            GhostEngineFxRuleKind kind, ModuleEngines engine, string groupName, GhostEngineFxCurves curves)
        {
            var engineFX = engine as ModuleEnginesFX;
            return new GhostEngineFxRule
            {
                kind = kind,
                curves = curves,
                channel = engineFX != null
                    ? GhostEngineFxPower.ResolveChannel(groupName, engineFX.powerEffectName,
                        engineFX.runningEffectName, engineFX.spoolEffectName)
                    : GhostEngineFxChannel.Throttle,
                spoolIdle = engineFX != null && engineFX.useEngineResponseTime ? engineFX.engineSpoolIdle : 0f
            };
        }

        /// <summary>
        /// The rule of a model instance: stock drives the first <c>KSPParticleEmitter</c> of the
        /// model. False when the model has none, or the node carries no stock curves.
        /// </summary>
        private static bool TryAddModelFxRule(
            GameObject fxInstance, EngineGhostInfo info, ModuleEngines engine,
            string groupName, GhostEngineFxCurves curves)
        {
            if (curves == null) return false;
            KSPParticleEmitter emitter = fxInstance.GetComponentInChildren<KSPParticleEmitter>(true);
            if (emitter == null) return false;
            GhostEngineFxRule rule = NewEffectsRule(GhostEngineFxRuleKind.ModelEmitter, engine, groupName, curves);
            rule.emitter = emitter;
            rule.minEmission = emitter.minEmission;
            rule.maxEmission = emitter.maxEmission;
            rule.minEnergy = emitter.minEnergy;
            rule.maxEnergy = emitter.maxEnergy;
            rule.localVelocity = emitter.localVelocity;
            info.fxRules.Add(rule);
            return true;
        }

        /// <summary>
        /// The rule of a PREFAB_PARTICLE instance: stock drives the first particle system of the
        /// prefab, whose velocity over lifetime it switches on, and restarts it every physics step.
        /// False for an instance a <c>KSPParticleEmitter</c> feeds, or a node with no stock curves.
        /// </summary>
        private static bool TryAddPrefabFxRule(
            GameObject fxInstance, EngineGhostInfo info, ModuleEngines engine,
            string groupName, GhostEngineFxCurves curves)
        {
            if (curves == null || fxInstance.GetComponent<KSPParticleEmitter>() != null) return false;
            ParticleSystem ps = fxInstance.GetComponentInChildren<ParticleSystem>(true);
            if (ps == null) return false;
            var emission = ps.emission;
            ParticleSystem.MinMaxCurve life = ps.main.startLifetime;
            GhostEngineFxRule rule = NewEffectsRule(GhostEngineFxRuleKind.PrefabParticle, engine, groupName, curves);
            rule.system = ps;
            rule.hasBursts = emission.burstCount > 0;
            if (rule.hasBursts)
            {
                ParticleSystem.Burst burst = emission.GetBurst(0);
                rule.minRate = burst.minCount;
                rule.maxRate = burst.maxCount;
            }
            else
            {
                rule.minRate = rule.maxRate = emission.rateOverTime.constant;
            }
            rule.minLife = life.constantMin;
            rule.maxLife = life.constantMax;
            var velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            ps.gameObject.AddComponent<GhostPrefabFxRestart>().system = ps;
            info.fxRules.Add(rule);
            return true;
        }

        /// <summary>
        /// Resolves the engine's exhaust anchor TRANSFORM on the prefab: the first
        /// populated thrustTransforms entry, else the thrustVectorTransformName lookup
        /// (prefab ModuleEngines often have an EMPTY thrustTransforms list; the
        /// round-9 Twin-Boar re-anchor silently no-oped on exactly that).
        /// </summary>
        private static bool TryResolvePrefabExhaustAnchor(
            Part prefab, ModuleEngines engine, out Transform anchor)
        {
            anchor = null;
            if (engine != null && engine.thrustTransforms != null)
            {
                for (int t = 0; t < engine.thrustTransforms.Count; t++)
                {
                    if (engine.thrustTransforms[t] != null)
                    {
                        anchor = engine.thrustTransforms[t];
                        return true;
                    }
                }
            }

            if (prefab != null && engine != null &&
                !string.IsNullOrEmpty(engine.thrustVectorTransformName))
            {
                var anchors = GhostVisualBuilder.FindTransformsRecursive(
                    prefab.transform, engine.thrustVectorTransformName);
                if (anchors.Count > 0 && anchors[0] != null)
                {
                    anchor = anchors[0];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resolves the engine's exhaust direction in prefab world space: the exhaust
        /// anchor's forward (+Z; KSP convention points exhaust-ward). Used to aim
        /// the world-space emitter velocity floor in each FX transform's local frame.
        /// </summary>
        private static bool TryResolvePrefabExhaustDirection(
            Part prefab, ModuleEngines engine, out Vector3 worldDir)
        {
            worldDir = Vector3.zero;
            if (!TryResolvePrefabExhaustAnchor(prefab, engine, out Transform anchor))
                return false;
            worldDir = anchor.forward;
            return true;
        }

        /// <summary>
        /// Stock <c>ModelParticleFX.OnInitialize</c> (decompiled) nests the model's first
        /// <c>KSPParticleEmitter</c> under the mount, not the model root, so the emitter's own pose
        /// inside the model does not count. Returns the emitter's object, parentless and the rest of
        /// the model destroyed; the model itself when the emitter is its root or it has none.
        /// </summary>
        private static GameObject DetachModelParticleEmitter(GameObject model)
        {
            KSPParticleEmitter emitter = model.GetComponentInChildren<KSPParticleEmitter>(true);
            if (emitter == null || emitter.gameObject == model)
                return model;
            GameObject detached = emitter.gameObject;
            detached.transform.SetParent(null, true);
            Object.Destroy(model);
            return detached;
        }

        /// <summary>
        /// Instantiates model FX entries (MODEL_MULTI_PARTICLE/MODEL_PARTICLE) from EFFECTS config
        /// and parents them to the ghost's mirrored transform hierarchy.
        /// </summary>
        private static void ProcessEngineModelFxEntries(
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)> modelFxEntries,
            Part prefab, ModuleEngines engine, EngineGhostInfo info,
            string partName, int moduleIndex,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap,
            Dictionary<string, bool> selectedVariantGameObjects)
        {
            bool hasExhaustDir = TryResolvePrefabExhaustDirection(prefab, engine, out Vector3 exhaustWorldDir);
            if (!hasExhaustDir && modelFxEntries.Count > 0)
            {
                LogHotPathVerbose($"exhaust-dir-miss-{partName}-{moduleIndex}",
                    $"'{partName}' midx={moduleIndex}: no thrust transform resolvable; " +
                    "world-space emitter velocity floor unavailable for model FX");
            }

            float modelSizeBoost = ResolveModelFxSizeBoost(
                ReStockPatchFxIndex.HasAuthoredEffectsFor(prefab.partInfo?.name ?? partName),
                partName);

            for (int f = 0; f < modelFxEntries.Count; f++)
            {
                var (nodeType, transformName, modelName, mmpLocalPos, mmpLocalRot, groupName, fxCurves) = modelFxEntries[f];

                var fxTransforms = GhostVisualBuilder.FindTransformsRecursive(prefab.transform, transformName);
                // Stock nests an instance under every mount, the other variants' included.
                Transform firstMount = fxTransforms.Count > 0 ? fxTransforms[0] : null;
                // #242c: exclude transforms under disabled variant GameObjects
                if (selectedVariantGameObjects != null && selectedVariantGameObjects.Count > 0)
                {
                    int preFilter = fxTransforms.Count;
                    fxTransforms.RemoveAll(t =>
                        !GhostVisualBuilder.IsRendererEnabledByVariantRule(
                            t, modelRoot, selectedVariantGameObjects, out _, out _));
                    if (fxTransforms.Count < preFilter)
                        LogHotPathVerbose($"variant-model-filter-{partName}-{moduleIndex}-{transformName}",
                            $"'{partName}' midx={moduleIndex}: " +
                            $"variant filter removed {preFilter - fxTransforms.Count} of {preFilter} " +
                            $"'{transformName}' model FX transforms");
                }
                for (int t = 0; t < fxTransforms.Count; t++)
                {
                    Transform srcFxTransform = fxTransforms[t];

                    Transform ghostFxParent = GhostVisualBuilder.ResolveGhostFxParent(
                        srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                    if (ghostFxParent == null)
                    {
                        LogHotPathVerbose($"model-parent-miss-{partName}-{moduleIndex}-{transformName}",
                            $"'{partName}' midx={moduleIndex} " +
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
                            if (nodeType == "MODEL_PARTICLE")
                                fxInstance = DetachModelParticleEmitter(fxInstance);
                            Vector3 modelScale = fxInstance.transform.localScale;
                            fxInstance.transform.SetParent(ghostFxParent, false);
                            fxInstance.transform.localPosition = mmpLocalPos;
                            fxInstance.transform.localRotation = mmpLocalRot;
                            if (fxCurves != null)
                            {
                                // Stock's MODEL_PARTICLE has one mount; MODEL_MULTI_PARTICLE copies
                                // its first instance for the others.
                                bool single = nodeType == "MODEL_PARTICLE";
                                fxInstance.transform.localScale = GhostVisualBuilder.NestedModelFxScale(
                                    modelScale,
                                    single ? srcFxTransform.lossyScale : firstMount.lossyScale,
                                    srcFxTransform.lossyScale,
                                    single || srcFxTransform == firstMount, Vector3.one);
                            }

                            GhostVisualBuilder.StripKspFxControllers(fxInstance, info.kspEmitters, info.smokeTrailFades);

                            if (hasExhaustDir)
                            {
                                // Exhaust axis in the emitter's local frame: undo the
                                // instance's cfg rotation and the source FX transform's
                                // prefab-space rotation (the ghost mirrors both).
                                Vector3 exhaustLocal = Quaternion.Inverse(mmpLocalRot) *
                                    (Quaternion.Inverse(srcFxTransform.rotation) * exhaustWorldDir);
                                GhostVisualBuilder.ApplyWorldSpaceEmitterVelocityFloor(
                                    fxInstance, partName, moduleIndex, exhaustLocal);
                            }

                            int addedSystems = GhostVisualBuilder.ConfigureGhostEngineParticleSystems(fxInstance, info.particleSystems);
                            if (addedSystems > 0)
                            {
                                GhostFxEmissionProbe.AttachIfNew(fxInstance, partName, moduleIndex, modelName);
                                if (!TryAddModelFxRule(fxInstance, info, engine, groupName, fxCurves))
                                    GhostVisualBuilder.ApplyGhostEngineFxSizeBoost(
                                        fxInstance, modelSizeBoost);
                                GhostVisualBuilder.LogFxInstancePlacementDiagnostic(partName, moduleIndex, nodeType, transformName,
                                    modelName, prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent,
                                    fxInstance.transform, mmpLocalPos, mmpLocalRot, true);
                                LogHotPathVerbose($"model-cloned-{partName}-{moduleIndex}-{groupName}-{transformName}-{modelName}",
                                    $"cloned: '{partName}' midx={moduleIndex} " +
                                    $"group='{groupName}' type={nodeType} transform='{transformName}' model='{modelName}' " +
                                    $"systems={addedSystems} cfgRot={mmpLocalRot.eulerAngles}");
                                LogFxDirection(partName, moduleIndex, nodeType, transformName, modelName, fxInstance.transform);
                            }
                            else
                            {
                                LogHotPathVerbose($"model-empty-{partName}-{moduleIndex}-{modelName}",
                                    $"model has no ParticleSystem: '{modelName}' for '{partName}'");
                                Object.Destroy(fxInstance);
                            }
                        }
                        else
                        {
                            ParsekLog.VerboseRateLimited("GhostVisual",
                                $"engine-fx-model-missing-{partName}-{moduleIndex}-{modelName}",
                                $"    Engine FX model not found: '{modelName}' for '{partName}'",
                                HotPathLogIntervalSeconds);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Decides whether a prefab-name miss may probe KSP's builtin Effects/ assets:
        /// when the part is Waterfall-patched (config packs strip the donor parts the
        /// prefab cache is built from) OR when ReStock is installed (ReStock deletes
        /// the legacy fx_* keys on every covered engine, emptying the donor cache for
        /// names like fx_smokeTrail_light install-wide; jets' only running visual is
        /// that prefab). False on stock installs: the donor cache resolves there and
        /// stock paths must keep the deliberate fxPrefabFallbacks substitutions.
        /// </summary>
        internal static bool ShouldProbeBuiltinEffectsOnPrefabMiss(
            bool partHasWaterfallModule, bool reStockInstalled)
        {
            return partHasWaterfallModule || reStockInstalled;
        }

        /// <summary>
        /// Ghost size boost for MODEL_MULTI_PARTICLE instances. ReStock-authored model
        /// FX sizes are absolute (live KSP scales emission/lifetime/velocity with
        /// power, never particle size), so the stock-look boost must not inflate them;
        /// the per-part overrides were tuned against STOCK prefab plumes.
        /// </summary>
        internal static float ResolveModelFxSizeBoost(bool restockAuthoredEffects, string partName)
        {
            return restockAuthoredEffects ? 1f : GhostVisualBuilder.ResolveEngineFxSizeBoost(partName);
        }

        /// <summary>
        /// Resolves the local rotation for a PREFAB_PARTICLE instance without a cfg
        /// localRotation on a ReStock-authored part: aim the prefab's emission axis
        /// along the engine's exhaust axis expressed in the parent FX transform's
        /// frame. The stock parent-up heuristic mis-aims on ReStock rigs (Mammoth's
        /// smokePoint pointed the smoke trail straight up). The emission axis of the
        /// smokeTrail prefabs is local MINUS-Y: MEASURED by GhostFxEmissionProbe
        /// (round-5 log 2026-06-12): every instance aimed with +Y flowed exactly
        /// inverted (177-180 deg) while every heuristic instance (identity rotation,
        /// parent up vertical) flowed correctly, which is only consistent with -Y
        /// emission. Returns false when the cfg authored a rotation, the part is not
        /// ReStock-authored, or no exhaust axis is resolvable; callers then keep the
        /// stock heuristic.
        /// </summary>
        // A prefab-FX mount transform farther than this from the part root gets the
        // instance re-anchored to the engine's thrust transform. ReStock's Twin-Boar
        // rig nests smokePoint under stacked 20x scales, placing the smoke mount
        // ~200 m exhaust-ward of the part (the authored launch-smoke column far below
        // an ascending booster); on a static rotated showcase fixture that becomes an
        // orphan smoke column hanging 200 m to the side (round-8 probe: smoke instance
        // measured 199 m from the part's own flames). No stock rig and no other
        // ReStock rig mounts FX beyond a few meters, so the threshold is uncritical.
        internal const float FarFxMountReanchorMeters = 25f;

        internal static bool IsFarFxMount(float mountDistanceMeters)
        {
            return mountDistanceMeters > FarFxMountReanchorMeters;
        }

        // Below this angle between the FX transform's -Y and the engine's exhaust
        // axis, the rig is trusted as-is (identity, the live PrefabParticleFX
        // contract). LES is the case that needs it: its fxSmoke -Y is authored
        // straight down the stack (central smoke column) while the escape thrust
        // transform is deliberately canted ~30 degrees (Apollo-style); aiming the
        // smoke onto the canted thrust axis was wrong. Round-6 probe data: every
        // measured rig sits at either ~1 deg (trust) or ~90/180 deg (aim), so the
        // threshold is uncritical anywhere between.
        internal const float PrefabRigTrustAngleDegrees = 45f;

        internal static bool TryComputeExhaustAimedPrefabRotation(
            bool hasCfgRotation, bool restockAuthoredEffects, bool hasExhaustDir,
            Vector3 exhaustAxisParentLocal, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (hasCfgRotation || !restockAuthoredEffects || !hasExhaustDir)
                return false;
            if (exhaustAxisParentLocal.magnitude <= 0.001f)
                return false;

            Vector3 axis = exhaustAxisParentLocal.normalized;
            // The transform's own -Y is the prefab's emission axis under the live
            // identity contract. When it already roughly agrees with the exhaust
            // axis, the rig author oriented it deliberately; keep identity.
            float dotWithMinusY = Vector3.Dot(axis, Vector3.down);
            if (dotWithMinusY >= Mathf.Cos(PrefabRigTrustAngleDegrees * Mathf.Deg2Rad))
            {
                rotation = Quaternion.identity;
                return true;
            }

            rotation = ManagedFromToRotation(Vector3.down, axis);
            return true;
        }

        /// <summary>
        /// Managed from-to rotation (Quaternion.FromToRotation is a native ECall and
        /// cannot run under xUnit). Inputs need not be normalized.
        /// </summary>
        internal static Quaternion ManagedFromToRotation(Vector3 from, Vector3 to)
        {
            Vector3 f = from.normalized;
            Vector3 t = to.normalized;
            float dot = Vector3.Dot(f, t);
            if (dot > 0.999999f)
                return Quaternion.identity;
            if (dot < -0.999999f)
            {
                // Opposite vectors: rotate 180 degrees around any perpendicular axis.
                Vector3 ortho = Vector3.Cross(f, Vector3.right);
                if (ortho.sqrMagnitude < 1e-6f)
                    ortho = Vector3.Cross(f, Vector3.forward);
                ortho.Normalize();
                return new Quaternion(ortho.x, ortho.y, ortho.z, 0f);
            }

            Vector3 axis = Vector3.Cross(f, t);
            float s = Mathf.Sqrt((1f + dot) * 2f);
            float invS = 1f / s;
            return new Quaternion(axis.x * invS, axis.y * invS, axis.z * invS, s * 0.5f);
        }

        /// <summary>The RAPIER's own trail (both modes' EFFECTS name it). Its shader is additive,
        /// unlike <c>fx_smokeTrail_large</c>'s alpha-blended grey smoke.</summary>
        internal const string RapierTrailPrefabName = "fx_smokeTrail_aeroSpike";

        /// <summary>
        /// True for the one entry that loads stock's exact builtin asset and follows stock's
        /// <c>PrefabParticleFX</c> contract: the RAPIER's own trail. The shared lookup has no donor
        /// part for that name and would substitute <c>fx_smokeTrail_large</c> through
        /// <c>fxPrefabFallbacks</c>; other parts that name it keep that substitution.
        /// </summary>
        internal static bool IsRapierOwnTrail(string partName, string prefabName)
        {
            return string.Equals(partName, "RAPIER", System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GhostVisualBuilder.NormalizeFxPrefabName(prefabName),
                    RapierTrailPrefabName, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Adds the RAPIER's white flame core when the module has neither a model FX nor a white
        /// flame prefab, anchored on its trail's transform. Changes no existing entry.
        /// </summary>
        internal static bool TryAddRapierWhiteFlame(
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)> prefabFxEntries,
            int modelFxEntryCount,
            System.Func<string, bool> hasNamedTransform,
            string thrustVectorTransformName,
            out string flameTransform)
        {
            flameTransform = null;
            if (modelFxEntryCount > 0)
                return false;

            string smokeTransform = null;
            for (int i = prefabFxEntries.Count - 1; i >= 0; i--)
            {
                string existing = GhostVisualBuilder.NormalizeFxPrefabName(prefabFxEntries[i].prefabName);
                if (existing == null)
                    continue;
                if (string.Equals(existing, "fx_exhaustFlame_white", System.StringComparison.OrdinalIgnoreCase))
                    return false;
                if (smokeTransform == null &&
                    existing.IndexOf("smoketrail", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    smokeTransform = prefabFxEntries[i].transformName;
            }

            flameTransform = smokeTransform ?? "smokePoint";
            if (!hasNamedTransform(flameTransform))
            {
                flameTransform = "thrustTransform";
                if (!hasNamedTransform(flameTransform) &&
                    !string.IsNullOrEmpty(thrustVectorTransformName) &&
                    hasNamedTransform(thrustVectorTransformName))
                    flameTransform = thrustVectorTransformName;
            }

            // Quaternion.Euler(-90, 0, 0) as a literal: Euler is native and cannot run headless.
            prefabFxEntries.Add(("fx_exhaustFlame_white", flameTransform, Vector3.zero,
                new Quaternion(-0.70710677f, 0f, 0f, 0.70710677f), true, "fallback", null));
            return true;
        }

        /// <summary>
        /// Instantiates PREFAB_PARTICLE entries from EFFECTS config and parents them to the
        /// ghost's mirrored transform hierarchy. Handles special cases like RAPIER white flame scaling.
        /// </summary>
        private static void ProcessEnginePrefabFxEntries(
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)> prefabFxEntries,
            Part prefab, ModuleEngines engine, EngineGhostInfo info,
            string partName, int moduleIndex,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap,
            Dictionary<string, bool> selectedVariantGameObjects)
        {
            bool restockAuthoredEffects =
                ReStockPatchFxIndex.HasAuthoredEffectsFor(prefab.partInfo?.name ?? partName);
            bool hasExhaustDir = TryResolvePrefabExhaustDirection(prefab, engine, out Vector3 exhaustWorldDir);
            bool probeBuiltinEffects = ShouldProbeBuiltinEffectsOnPrefabMiss(
                WaterfallCompat.PartHasWaterfallModule(prefab),
                ReStockPatchFxIndex.IsReStockInstalled());

            for (int f = 0; f < prefabFxEntries.Count; f++)
            {
                var (prefabName, transformName, localOffset, localRot, hasLocalRot, groupName, fxCurves) = prefabFxEntries[f];
                string normalizedPrefabName = GhostVisualBuilder.NormalizeFxPrefabName(prefabName);
                bool isRapierWhiteFlame =
                    string.Equals(partName, "RAPIER", System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(normalizedPrefabName, "fx_exhaustFlame_white", System.StringComparison.OrdinalIgnoreCase);

                bool isRapierOwnTrail = IsRapierOwnTrail(partName, normalizedPrefabName);
                // An EFFECTS entry loads stock's own asset (PrefabParticleFX: "Effects/" + name) and
                // follows stock's rule; the shared lookup may answer with a substitute, whose rate,
                // lifetime and velocity are another effect's.
                GameObject fxPrefab = fxCurves != null || isRapierOwnTrail
                    ? GhostVisualBuilder.FindBuiltinEffectsPrefab(prefabName)
                    : null;
                bool isStockAsset = fxPrefab != null;
                if (fxPrefab == null && !isRapierOwnTrail)
                    fxPrefab = GhostVisualBuilder.FindFxPrefab(prefabName);
                if (isRapierOwnTrail)
                {
                    LogHotPathVerbose($"rapier-own-trail-{partName}-{moduleIndex}",
                        $"'{partName}' midx={moduleIndex}: own trail '{prefabName}' " +
                        (fxPrefab != null
                            ? "loaded from builtin Effects (no substitute)"
                            : "has no builtin Effects asset; no trail is drawn"));
                }
                else if (fxPrefab == null && probeBuiltinEffects)
                {
                    // Waterfall-patched part or ReStock install: the exact prefab's
                    // donors may all be patched away (config packs strip them; ReStock
                    // deletes the legacy fx_* keys outright). Try KSP's builtin
                    // Effects/ asset for the EXACT name first (preserves the true
                    // variant look), then the family-base cascade before giving up.
                    fxPrefab = GhostVisualBuilder.FindFxPrefabIncludingBuiltinEffects(
                        prefabName, out bool _);
                    if (fxPrefab == null)
                    {
                        List<string> candidates = PristinePartFxResolver.BuildLegacyFxNameCandidates(prefabName);
                        for (int c = 1; c < candidates.Count && fxPrefab == null; c++)
                        {
                            fxPrefab = GhostVisualBuilder.FindFxPrefabIncludingBuiltinEffects(
                                candidates[c], out bool _);
                            if (fxPrefab != null)
                            {
                                LogHotPathVerbose($"prefab-cascade-{partName}-{moduleIndex}-{prefabName}",
                                    $"waterfall fallback: '{partName}' midx={moduleIndex} " +
                                    $"cascaded prefab '{prefabName}' -> '{candidates[c]}'");
                            }
                        }
                    }
                }
                if (fxPrefab == null)
                {
                    LogHotPathVerbose($"prefab-missing-{partName}-{moduleIndex}-{prefabName}",
                        $"prefab not found: '{prefabName}' for '{partName}'");
                    continue;
                }

                var fxTransforms = GhostVisualBuilder.FindTransformsRecursive(prefab.transform, transformName);
                // #242c: exclude transforms under disabled variant GameObjects
                if (selectedVariantGameObjects != null && selectedVariantGameObjects.Count > 0)
                {
                    int preFilter = fxTransforms.Count;
                    fxTransforms.RemoveAll(t =>
                        !GhostVisualBuilder.IsRendererEnabledByVariantRule(
                            t, modelRoot, selectedVariantGameObjects, out _, out _));
                    if (fxTransforms.Count < preFilter)
                        LogHotPathVerbose($"variant-prefab-filter-{partName}-{moduleIndex}-{transformName}",
                            $"'{partName}' midx={moduleIndex}: " +
                            $"variant filter removed {preFilter - fxTransforms.Count} of {preFilter} " +
                            $"'{transformName}' prefab FX transforms");
                }
                if (fxTransforms.Count == 0)
                {
                    LogHotPathVerbose($"prefab-transform-miss-{partName}-{moduleIndex}-{transformName}",
                        $"'{partName}' midx={moduleIndex} " +
                        $"transform '{transformName}' not found on prefab");
                    continue;
                }
                for (int t = 0; t < fxTransforms.Count; t++)
                {
                    if (isRapierWhiteFlame && t > 0)
                        continue;

                    Transform srcFxTransform = fxTransforms[t];
                    bool reanchored = false;

                    // Far-mount re-anchor: an instance whose EFFECTIVE placement point
                    // sits tens of meters from the part reads as an orphan effect on a
                    // static ghost. The distance must be measured at TransformPoint of
                    // the cfg localOffset, NOT the bare mount transform: ReStock's
                    // Twin-Boar smokePoint sits 5 m from the part but carries lossyScale
                    // 200, so its localOffset of one meter places the instance 200 m out
                    // (round-11 forensics: mountDistPrefab=5 ghostInstDist=200
                    // srcLossyScale=200.0; live PrefabParticleFX applies the same raw
                    // localPosition, so the live column really is 200 m exhaust-ward).
                    float mountDistance =
                        (srcFxTransform.TransformPoint(localOffset) - prefab.transform.position).magnitude;
                    if (IsFarFxMount(mountDistance))
                    {
                        if (TryResolvePrefabExhaustAnchor(prefab, engine, out Transform exhaustAnchor))
                        {
                            LogHotPathVerbose($"prefab-farmount-{partName}-{moduleIndex}-{transformName}",
                                $"(prefab): '{partName}' midx={moduleIndex} mount '{transformName}' " +
                                $"sits {(int)mountDistance} m from the part root; re-anchoring " +
                                $"'{prefabName}' to exhaust anchor '{exhaustAnchor.name}'");
                            srcFxTransform = exhaustAnchor;
                            reanchored = true;
                        }
                        else
                        {
                            LogHotPathVerbose($"prefab-farmount-miss-{partName}-{moduleIndex}-{transformName}",
                                $"(prefab): '{partName}' midx={moduleIndex} mount '{transformName}' " +
                                $"sits {(int)mountDistance} m from the part root but no exhaust " +
                                "anchor is resolvable; instance stays on the far mount");
                        }
                    }

                    Transform ghostFxParent = GhostVisualBuilder.ResolveGhostFxParent(
                        srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                    if (ghostFxParent == null)
                    {
                        LogHotPathVerbose($"prefab-parent-miss-{partName}-{moduleIndex}-{transformName}",
                            $"'{partName}' midx={moduleIndex} " +
                            $"transform='{transformName}' (prefab) parent resolution failed");
                        continue;
                    }
                    GhostVisualBuilder.LogFxParentAlignmentDiagnostic(partName, moduleIndex, "PREFAB_PARTICLE", transformName,
                        prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent);

                    GameObject fxInstance = Object.Instantiate(fxPrefab);
                    // Stock nests an EFFECTS prefab keeping its world scale (NestToParent), so a
                    // rescaled part does not scale it: the RAPIER's default rescaleFactor 1.25 leaves
                    // its trail a local scale of 0.8.
                    fxInstance.transform.SetParent(ghostFxParent, isStockAsset);
                    fxInstance.transform.localPosition = localOffset;

                    Vector3 exhaustParentLocal = hasExhaustDir
                        ? Quaternion.Inverse(srcFxTransform.rotation) * exhaustWorldDir
                        : Vector3.zero;
                    if (isRapierOwnTrail || (isStockAsset && !reanchored))
                    {
                        // Stock PrefabParticleFX.OnInitialize: an absent localRotation is identity.
                        // The parent-up heuristic below depends on the ghost's world attitude.
                        fxInstance.transform.localRotation = hasLocalRot ? localRot : Quaternion.identity;
                    }
                    else if (TryComputeExhaustAimedPrefabRotation(
                        hasLocalRot, restockAuthoredEffects, hasExhaustDir,
                        exhaustParentLocal, out Quaternion exhaustAimedRot))
                    {
                        fxInstance.transform.localRotation = exhaustAimedRot;
                        LogHotPathVerbose($"prefab-exhaust-aim-{partName}-{moduleIndex}-{prefabName}",
                            $"(prefab): '{partName}' midx={moduleIndex} prefab='{prefabName}' " +
                            $"aimed along exhaust axis {exhaustParentLocal} " +
                            "(ReStock-authored part; stock parent-up heuristic mis-aims on ReStock rigs)");
                    }
                    else
                    {
                        fxInstance.transform.localRotation = ResolvePrefabParticleLocalRotation(
                            hasLocalRot,
                            localRot,
                            ghostFxParent.up);
                    }

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

                    GhostVisualBuilder.StripKspFxControllers(fxInstance, info.kspEmitters, info.smokeTrailFades);

                    int addedSystems = GhostVisualBuilder.ConfigureGhostEngineParticleSystems(fxInstance, info.particleSystems);
                    if (addedSystems > 0)
                    {
                        GhostFxEmissionProbe.AttachIfNew(fxInstance, partName, moduleIndex, prefabName);
                        // Round-10 placement forensics: the Twin-Boar smoke instance lands
                        // ~200 m out on the ghost while the prefab-side mount distance
                        // reads under the far-mount threshold; log both sides per
                        // INSTANCE (t) so the divergence point names itself.
                        float ghostMountDistance =
                            (fxInstance.transform.position - ghostModelNode.position).magnitude;
                        var ic = System.Globalization.CultureInfo.InvariantCulture;
                        LogHotPathVerbose($"prefab-place-{partName}-{moduleIndex}-{transformName}-{t}",
                            $"(prefab): '{partName}' midx={moduleIndex} inst={t}/{fxTransforms.Count} " +
                            $"'{prefabName}' mountDistPrefab={(int)mountDistance} " +
                            $"ghostInstDist={(int)ghostMountDistance} " +
                            $"srcLossyScale={srcFxTransform.lossyScale.x.ToString("F1", ic)} " +
                            $"ghostParentLossyScale={ghostFxParent.lossyScale.x.ToString("F1", ic)}");
                        // Stock PrefabParticleFX never scales particle size, so only a plume
                        // Parsek substitutes takes the boost.
                        if (!isStockAsset || !TryAddPrefabFxRule(fxInstance, info, engine, groupName, fxCurves))
                            GhostVisualBuilder.ApplyGhostEngineFxSizeBoost(
                                fxInstance, GhostVisualBuilder.ResolveEngineFxSizeBoost(partName));
                        GhostVisualBuilder.LogFxInstancePlacementDiagnostic(partName, moduleIndex, "PREFAB_PARTICLE", transformName,
                            prefabName, prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent,
                            fxInstance.transform, localOffset, localRot, hasLocalRot);
                        LogHotPathVerbose($"prefab-cloned-{partName}-{moduleIndex}-{groupName}-{transformName}-{prefabName}",
                            $"(prefab): '{partName}' midx={moduleIndex} " +
                            $"group='{groupName}' transform='{transformName}' prefab='{prefabName}' " +
                            $"systems={addedSystems} cfgRot={localRot.eulerAngles} hasCfgRot={hasLocalRot}");
                        LogFxDirection(partName, moduleIndex, "PREFAB_PARTICLE", transformName, prefabName, fxInstance.transform);
                    }
                    else
                    {
                        LogHotPathVerbose($"prefab-empty-{partName}-{moduleIndex}-{prefabName}",
                            $"(prefab): '{partName}' midx={moduleIndex} " +
                            $"prefab '{prefabName}' has no ParticleSystem");
                        Object.Destroy(fxInstance);
                    }
                }
            }
        }

        /// <summary>
        /// True for a prefab entry Parsek SYNTHESIZED rather than read off an EFFECTS
        /// node: the RAPIER's white flame core (`fallback`), the Waterfall last resort,
        /// and the two pristine legacy-key substitutions (those come from the part's
        /// legacy `fx_*` keys, not from EFFECTS). Everything else carries a real EFFECTS
        /// group name (post-MM scan, pristine on-disk EFFECTS, or ReStock's patch).
        /// </summary>
        internal static bool IsSupplementFxGroup(string groupName)
        {
            return string.Equals(groupName, "fallback", System.StringComparison.Ordinal)
                || string.Equals(groupName, "waterfall-lastresort", System.StringComparison.Ordinal)
                || string.Equals(groupName, "pristine-legacy", System.StringComparison.Ordinal)
                || string.Equals(groupName, "pristine-legacy-flame-fallback", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The build-time source class of one ghost engine module. An EFFECTS-node system
        /// wins over supplements.
        /// </summary>
        internal static EngineFxSource ClassifyEngineFxSource(
            int effectsNodeSystems, int supplementSystems, int legacySystems)
        {
            if (effectsNodeSystems > 0) return EngineFxSource.EffectsNode;
            if (legacySystems > 0) return EngineFxSource.Legacy;
            if (supplementSystems > 0) return EngineFxSource.Supplement;
            return EngineFxSource.None;
        }

        internal static List<EngineGhostInfo> TryBuildEngineFX(
            Part prefab, uint persistentId, string partName,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap,
            Dictionary<string, bool> selectedVariantGameObjects)
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

            // The hardcoded per-part FX tunings below were written for the STOCK look.
            // When ReStock authored this part's EFFECTS, the scanned entries already ARE
            // the install's correct look and the tunings must stand down (applies with
            // and without Waterfall; empty index on non-ReStock installs = no change).
            bool restockAuthoredEffects =
                ReStockPatchFxIndex.HasAuthoredEffectsFor(prefab.partInfo?.name ?? partName);

            var result = new List<EngineGhostInfo>();

            for (int e = 0; e < engineModules.Count; e++)
            {
                var (engine, moduleIndex) = engineModules[e];
                var info = new EngineGhostInfo
                {
                    partPersistentId = persistentId,
                    moduleIndex = moduleIndex,
                    partName = partName,
                    engine = engine
                };

                bool TuningStandsDown(string blockName)
                {
                    if (!restockAuthoredEffects)
                        return false;
                    LogHotPathVerbose($"restock-standdown-{partName}-{moduleIndex}-{blockName}",
                        $"'{partName}' midx={moduleIndex}: stock {blockName} tuning stands down " +
                        "(ReStock authored this part's EFFECTS)");
                    return true;
                }

                bool isRapierPart =
                    string.Equals(partName, "RAPIER", System.StringComparison.OrdinalIgnoreCase);

                // Try to read EFFECTS config from the part config
                ConfigNode partConfig = prefab.partInfo?.partConfig;
                if (partConfig == null)
                {
                    ParsekLog.Verbose("GhostVisual", $"    Engine '{partName}' midx={moduleIndex}: no partConfig — skipping FX");
                    continue;
                }

                ConfigNode effectsNode = partConfig.GetNode("EFFECTS");

                // Scan EFFECTS for particle FX entries (MODEL_MULTI_PARTICLE, MODEL_PARTICLE, PREFAB_PARTICLE)
                var modelFxEntries = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)>();
                var prefabFxEntries = new List<(
                    string prefabName,
                    string transformName,
                    Vector3 localOffset,
                    Quaternion localRotation,
                    bool hasLocalRotation,
                    string groupName, GhostEngineFxCurves fxCurves)>();
                FloatCurve emissionCurve = null;
                FloatCurve speedCurve = null;

                if (effectsNode != null)
                {
                    ConfigNode[] allGroups = effectsNode.GetNodes();
                    string[] allGroupNames = BuildGroupNames(allGroups);

                    // Per-module EFFECTS group filtering: only scan groups referenced by
                    // this engine module's effect name fields (multi-mode support).
                    HashSet<string> moduleGroups = GetModuleEffectGroupNames(engine);
                    ConfigNode[] effectGroups;
                    string[] effectGroupNames;

                    if (moduleGroups.Count > 0)
                    {
                        FilterEffectGroups(
                            allGroups,
                            allGroupNames,
                            moduleGroups,
                            out effectGroups,
                            out effectGroupNames);
                        LogHotPathVerbose($"effects-filter-{partName}-{moduleIndex}",
                            $"'{partName}' midx={moduleIndex}: " +
                            $"filtered {allGroups.Length} EFFECTS groups to {effectGroups.Length} " +
                            $"by module effects=[{string.Join(",", moduleGroups)}]");
                    }
                    else
                    {
                        effectGroups = allGroups;
                        effectGroupNames = allGroupNames;
                    }

                    ScanEffectsModelFxEntries(effectGroups, effectGroupNames, modelFxEntries, ref emissionCurve, ref speedCurve);
                    ScanEffectsPrefabParticleEntries(effectGroups, effectGroupNames, prefabFxEntries);
                }

                // Waterfall config packs (e.g. SWE) delete the stock EFFECTS particle nodes,
                // leaving the post-MM scan empty. Recover the pristine definitions from the
                // on-disk cfg, per module (multi-mode engines need this for midx > 0 too).
                // Captured BEFORE the RAPIER injection below so pristine model FX and the
                // injection compose like a stock install.
                int scannedEntryCount = modelFxEntries.Count + prefabFxEntries.Count;
                bool waterfallPart = WaterfallCompat.PartHasWaterfallModule(prefab);
                if (WaterfallCompat.ShouldAttemptPristineFxFallback(scannedEntryCount, waterfallPart))
                {
                    TryApplyPristineEngineFxFallback(
                        prefab, engine, moduleIndex, partName,
                        modelFxEntries, prefabFxEntries, ref emissionCurve, ref speedCurve);
                }
                else if (waterfallPart && scannedEntryCount > 0)
                {
                    // Gate open but the post-MM scan found something: SWE remnants (e.g.
                    // RAPIER's surviving aerospike spool smoke). Prefer the full pristine
                    // EFFECTS when it yields particles, so the ghost composes like stock
                    // instead of remnants + hardcoded supplements.
                    TryReplaceSweRemnantsWithPristine(
                        prefab, moduleIndex, partName,
                        modelFxEntries, prefabFxEntries, ref emissionCurve, ref speedCurve);
                }

                var namedTransformCache = new Dictionary<string, List<Transform>>(System.StringComparer.OrdinalIgnoreCase);

                // #242c: filter out transforms under disabled variant GameObjects.
                bool IsTransformEnabledByVariant(Transform t)
                {
                    if (selectedVariantGameObjects == null || selectedVariantGameObjects.Count == 0)
                        return true;
                    return GhostVisualBuilder.IsRendererEnabledByVariantRule(
                        t, modelRoot, selectedVariantGameObjects, out _, out _);
                }

                List<Transform> FindNamedTransformsCached(string transformName)
                {
                    if (string.IsNullOrEmpty(transformName))
                        return new List<Transform>();

                    if (namedTransformCache.TryGetValue(transformName, out List<Transform> cached))
                        return cached;

                    List<Transform> found = GhostVisualBuilder.FindTransformsRecursive(prefab.transform, transformName);
                    // #242c: exclude transforms under disabled variant GameObjects.
                    // IsTransformEnabledByVariant handles the null/empty guard internally.
                    int preFilter = found.Count;
                    found.RemoveAll(t => !IsTransformEnabledByVariant(t));
                    if (found.Count < preFilter)
                        LogHotPathVerbose($"variant-named-transform-filter-{partName}-{moduleIndex}-{transformName}",
                            $"'{partName}' midx={moduleIndex}: " +
                            $"variant filter removed {preFilter - found.Count} of {preFilter} " +
                            $"'{transformName}' transforms in FindNamedTransformsCached");
                    namedTransformCache[transformName] = found;
                    return found;
                }

                bool HasNamedTransform(string transformName)
                {
                    return FindNamedTransformsCached(transformName).Count > 0;
                }

                // RAPIER: a white flame core when no FX of the module draws a flame. The trail the
                // config names stays as it is (see IsRapierOwnTrail).
                if (isRapierPart && !TuningStandsDown("RAPIER plume forcing") &&
                    TryAddRapierWhiteFlame(
                        prefabFxEntries, modelFxEntries.Count, HasNamedTransform,
                        engine != null ? engine.thrustVectorTransformName : null,
                        out string rapierFlameTransform))
                {
                    LogHotPathVerbose($"fallback-rapier-flame-{partName}-{moduleIndex}-{rapierFlameTransform}",
                        $"fallback: '{partName}' midx={moduleIndex} " +
                        $"added Vector-style white flame on '{rapierFlameTransform}'");
                }

                if (modelFxEntries.Count == 0 && prefabFxEntries.Count == 0 && waterfallPart)
                {
                    // Waterfall-patched part with no pristine recovery either: the config
                    // pack deleted the legacy fx_* keys, so the legacy prefab-children path
                    // below cannot produce anything. Add the best-effort white flame so the
                    // ghost is not plume-less. Runs per module (unlike the midx==0-only
                    // legacy path); falls through to the normal entry processing.
                    // Exception, mirroring the "legacy handled by midx=0" contract: secondary
                    // modules of a legacy-keyed pristine part never had own FX, so no last
                    // resort there (the resolver call is cached, this is a dictionary hit).
                    bool legacySecondaryModule = false;
                    if (moduleIndex > 0)
                    {
                        var pristineForPart = PristinePartFxResolver.GetForPart(
                            prefab.partInfo?.name ?? partName, prefab.partInfo?.configFileFullName);
                        legacySecondaryModule =
                            pristineForPart != null && pristineForPart.LegacyFxPrefabNames.Count > 0;
                    }

                    if (legacySecondaryModule)
                    {
                        LogHotPathVerbose($"waterfall-lastresort-skip-{partName}-{moduleIndex}",
                            $"waterfall fallback: '{partName}' midx={moduleIndex} legacy-keyed part; " +
                            "secondary module gets no last-resort flame (legacy handled by midx=0)");
                    }
                    else
                    {
                        string lastResortTransform = "thrustTransform";
                        if (!HasNamedTransform(lastResortTransform) &&
                            engine != null &&
                            !string.IsNullOrEmpty(engine.thrustVectorTransformName) &&
                            HasNamedTransform(engine.thrustVectorTransformName))
                        {
                            lastResortTransform = engine.thrustVectorTransformName;
                        }
                        prefabFxEntries.Add((
                            "fx_exhaustFlame_white", lastResortTransform, Vector3.zero,
                            Quaternion.Euler(-90f, 0f, 0f), true, "waterfall-lastresort", null));
                        LogHotPathVerbose($"waterfall-lastresort-{partName}-{moduleIndex}",
                            $"waterfall fallback: '{partName}' midx={moduleIndex} pristine recovery yielded " +
                            $"nothing; added best-effort white flame on '{lastResortTransform}'");
                    }
                }

                if (modelFxEntries.Count == 0 && prefabFxEntries.Count == 0)
                {
                    // No EFFECTS node, or EFFECTS has no particle entries (e.g. Mainsail: AUDIO only).
                    // Fall through to legacy fx_* child search.
                    // Only process once per part — legacy FX are shared, not per-module.
                    if (moduleIndex > 0)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"engine-legacy-handled-{partName}-{moduleIndex}",
                            $"    Engine '{partName}' midx={moduleIndex}: legacy FX already handled by midx=0",
                            HotPathLogIntervalSeconds);
                        continue;
                    }

                    bool legacyAdded = ProcessEngineLegacyFx(
                        prefab, engine, info, partName, moduleIndex,
                        modelRoot, ghostModelNode, cloneMap, selectedVariantGameObjects);
                    if (legacyAdded)
                    {
                        info.legacySystemCount = info.particleSystems.Count;
                        info.fxSource = ClassifyEngineFxSource(0, 0, info.legacySystemCount);
                        result.Add(info);
                    }
                    else
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"engine-no-legacy-{partName}-{moduleIndex}",
                            $"    Engine '{partName}' midx={moduleIndex}: no legacy fx_* children found",
                            HotPathLogIntervalSeconds);
                    continue;
                }

                info.emissionCurve = emissionCurve;
                info.speedCurve = speedCurve;

                // Process model FX entries (MODEL_MULTI_PARTICLE/MODEL_PARTICLE variants).
                ProcessEngineModelFxEntries(modelFxEntries, prefab, engine, info, partName, moduleIndex,
                    modelRoot, ghostModelNode, cloneMap, selectedVariantGameObjects);

                // Process PREFAB_PARTICLE entries (Spark, Twitch, Pug, Juno, Wheesley, Goliath).
                // EFFECTS-derived entries first, Parsek's injected supplements second, so the
                // EFFECTS-node systems are the leading block of info.particleSystems (the
                // ignition witness counts them by position). Supplements were always appended
                // after the scanned entries, so the system order is unchanged.
                var effectsPrefabEntries = prefabFxEntries.FindAll(x => !IsSupplementFxGroup(x.groupName));
                var supplementPrefabEntries = prefabFxEntries.FindAll(x => IsSupplementFxGroup(x.groupName));
                ProcessEnginePrefabFxEntries(effectsPrefabEntries, prefab, engine, info, partName, moduleIndex,
                    modelRoot, ghostModelNode, cloneMap, selectedVariantGameObjects);
                info.effectsNodeSystemCount = info.particleSystems.Count;
                ProcessEnginePrefabFxEntries(supplementPrefabEntries, prefab, engine, info, partName, moduleIndex,
                    modelRoot, ghostModelNode, cloneMap, selectedVariantGameObjects);
                info.supplementSystemCount = info.particleSystems.Count - info.effectsNodeSystemCount;
                info.fxSource = ClassifyEngineFxSource(
                    info.effectsNodeSystemCount, info.supplementSystemCount, 0);

                if (info.particleSystems.Count > 0)
                    result.Add(info);
            }

            // S1: one magnitude-baseline capture per finished engine module, AFTER every FX branch
            // above (model / prefab / legacy / pristine / ReStock) has cloned, size-boosted and
            // velocity-floored its systems. Capturing inside StripKspFxControllers instead would
            // pin the PRE-size-boost values and runtime scaling would undo #383.
            for (int i = 0; i < result.Count; i++)
            {
                GhostVisualBuilder.CaptureFxMagnitudeBaselines(
                    result[i].kspEmitters, result[i].particleSystems, result[i].particleBaselines,
                    partName, result[i].moduleIndex);
                MarkStockRuledFx(result[i]);
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Takes every source a stock rule drives out of the S1 ratio
        /// (<see cref="GhostPlaybackLogic.ApplyFxMagnitudeScale"/>), which would write over it.
        /// </summary>
        private static void MarkStockRuledFx(EngineGhostInfo info)
        {
            int thrustRules = 0;
            for (int r = 0; r < info.fxRules.Count; r++)
            {
                GhostEngineFxRule rule = info.fxRules[r];
                if (rule.UsesThrust) thrustRules++;
                for (int e = 0; e < info.kspEmitters.Count; e++)
                {
                    KspEmitterRef emitterRef = info.kspEmitters[e];
                    if (rule.emitter == null || emitterRef.emitter != rule.emitter) continue;
                    emitterRef.stockRuled = true;
                    info.kspEmitters[e] = emitterRef;
                }
                ParticleSystem ruled = rule.emitter != null
                    ? rule.emitter.GetComponent<ParticleSystem>() : rule.system;
                for (int s = 0; s < info.particleBaselines.Count && s < info.particleSystems.Count; s++)
                {
                    if (ruled == null || info.particleSystems[s] != ruled) continue;
                    GhostFxMagnitudeBaseline baseline = info.particleBaselines[s];
                    baseline.stockRuled = true;
                    info.particleBaselines[s] = baseline;
                }
            }
            info.usesThrustFraction = thrustRules > 0;
            LogHotPathVerbose($"stock-rules-{info.partName}-{info.moduleIndex}",
                $"'{info.partName}' midx={info.moduleIndex}: {info.fxRules.Count} of {info.particleSystems.Count} " +
                $"particle systems follow a stock power rule ({thrustRules} by thrust)");
        }

        /// <summary>
        /// Scans the pristine EFFECTS node for one engine-module ordinal into the given
        /// entry lists. Pristine group filtering uses the PRISTINE module's effect names
        /// (the live module's fields point at renamed post-MM groups that do not exist in
        /// the pristine node). Shared by the empty-scan fallback and the SWE-remnant
        /// replacement.
        /// </summary>
        internal static void ScanPristineEffectsEntries(
            PristinePartFxResolver.PristinePartFxData pristine, int moduleIndex, string partName,
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)> modelFxEntries,
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)> prefabFxEntries,
            ref FloatCurve emissionCurve, ref FloatCurve speedCurve)
        {
            if (pristine.EffectsNode == null)
                return;

            if (moduleIndex >= pristine.EngineModuleEffectNames.Count)
            {
                // Structural mismatch (live engine module without a pristine counterpart,
                // e.g. another mod added an engine module). Scanning ALL groups here would
                // bleed other modes' FX onto this module; skip and let the white-flame
                // last resort handle it.
                LogHotPathVerbose($"pristine-ordinal-miss-{partName}-{moduleIndex}",
                    $"waterfall fallback: '{partName}' midx={moduleIndex} has no pristine engine " +
                    $"module counterpart (pristineEngineModules={pristine.EngineModuleEffectNames.Count}); " +
                    "skipping pristine EFFECTS recovery for this module");
                return;
            }

            ConfigNode[] allGroups = pristine.EffectsNode.GetNodes();
            string[] allGroupNames = BuildGroupNames(allGroups);

            HashSet<string> moduleGroups = pristine.EngineModuleEffectNames[moduleIndex];

            ConfigNode[] effectGroups;
            string[] effectGroupNames;
            if (moduleGroups.Count > 0)
            {
                FilterEffectGroups(allGroups, allGroupNames, moduleGroups,
                    out effectGroups, out effectGroupNames);
            }
            else
            {
                effectGroups = allGroups;
                effectGroupNames = allGroupNames;
            }

            ScanEffectsModelFxEntries(effectGroups, effectGroupNames, modelFxEntries,
                ref emissionCurve, ref speedCurve);
            ScanEffectsPrefabParticleEntries(effectGroups, effectGroupNames, prefabFxEntries);
        }

        /// <summary>
        /// Counts the ModuleEngines-derived modules on a prefab (the live midx domain).
        /// </summary>
        private static int CountEngineModules(Part prefab)
        {
            int count = 0;
            if (prefab == null || prefab.Modules == null)
                return count;
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                if (prefab.Modules[m] is ModuleEngines)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// ReStock recovery: scans the EFFECTS node ReStock authored for this part (read
        /// from ReStock's patch files on disk, see ReStockPatchFxIndex) into the entry
        /// lists. Group filtering resolves per engine-module ordinal through the
        /// patch-authored -> pristine -> all-groups chain; multi-module parts with no
        /// per-ordinal source skip recovery (anti-bleed, mirroring the pristine
        /// ordinal-miss rule). Returns true when any entries were added; the caller then
        /// skips the stock pristine stages because the install's correct look is
        /// ReStock's, not stock's.
        /// </summary>
        private static bool TryScanReStockEffectsEntries(
            Part prefab, int moduleIndex, string partName,
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)> modelFxEntries,
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)> prefabFxEntries,
            ref FloatCurve emissionCurve, ref FloatCurve speedCurve)
        {
            string runtimeName = prefab.partInfo?.name ?? partName;
            if (!ReStockPatchFxIndex.TryGetForPart(runtimeName, out var restock) ||
                restock.EffectsNode == null)
            {
                return false;
            }

            var pristine = PristinePartFxResolver.GetForPart(runtimeName, prefab.partInfo?.configFileFullName);
            if (!ReStockPatchFxIndex.TryResolveEngineGroupFilter(
                restock, pristine, moduleIndex, CountEngineModules(prefab),
                out HashSet<string> moduleGroups, out string filterSource))
            {
                LogHotPathVerbose($"restock-ordinal-miss-{partName}-{moduleIndex}",
                    $"restock recovery: '{partName}' midx={moduleIndex} has no per-ordinal " +
                    "effect-name source (patch or pristine) on a multi-engine-module part; " +
                    "skipping ReStock EFFECTS recovery for this module");
                return false;
            }

            ConfigNode[] allGroups = restock.EffectsNode.GetNodes();
            string[] allGroupNames = BuildGroupNames(allGroups);

            ConfigNode[] effectGroups;
            string[] effectGroupNames;
            if (moduleGroups != null && moduleGroups.Count > 0)
            {
                FilterEffectGroups(allGroups, allGroupNames, moduleGroups,
                    out effectGroups, out effectGroupNames);
            }
            else
            {
                effectGroups = allGroups;
                effectGroupNames = allGroupNames;
            }

            int modelBefore = modelFxEntries.Count;
            int prefabBefore = prefabFxEntries.Count;
            ScanEffectsModelFxEntries(effectGroups, effectGroupNames, modelFxEntries,
                ref emissionCurve, ref speedCurve);
            ScanEffectsPrefabParticleEntries(effectGroups, effectGroupNames, prefabFxEntries);

            int added = (modelFxEntries.Count - modelBefore) + (prefabFxEntries.Count - prefabBefore);
            LogHotPathVerbose($"restock-recovery-{partName}-{moduleIndex}",
                $"restock recovery: '{partName}' midx={moduleIndex} restockScan={added} " +
                $"(model={modelFxEntries.Count - modelBefore}, " +
                $"prefab={prefabFxEntries.Count - prefabBefore}, groupFilter={filterSource}) " +
                $"from '{restock.SourceFile}'" +
                (added == 0 ? "; falling through to pristine stages" : string.Empty));
            return added > 0;
        }

        /// <summary>
        /// SWE-remnant replacement: a config pack that deletes EFFECTS can still leave a
        /// stray particle behind (SWE keeps RAPIER's aerospike spool smoke), which used to
        /// block the empty-scan fallback and leave the ghost with remnants + hardcoded
        /// supplements instead of the stock composition. When the gate is open and a
        /// recovered EFFECTS (ReStock-authored first, else pristine) yields particles for
        /// this module, prefer it wholesale.
        /// Returns true when the entries were replaced.
        /// </summary>
        private static bool TryReplaceSweRemnantsWithPristine(
            Part prefab, int moduleIndex, string partName,
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)> modelFxEntries,
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)> prefabFxEntries,
            ref FloatCurve emissionCurve, ref FloatCurve speedCurve)
        {
            var replacementModel = new List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)>();
            var replacementPrefab = new List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)>();
            FloatCurve replacementEmission = null;
            FloatCurve replacementSpeed = null;
            string replacementSource;

            // ReStock-authored EFFECTS first: when ReStock authored the look, the
            // remnants must be replaced by IT, not by the stock pristine definitions.
            if (TryScanReStockEffectsEntries(prefab, moduleIndex, partName,
                replacementModel, replacementPrefab, ref replacementEmission, ref replacementSpeed))
            {
                replacementSource = "restock";
            }
            else
            {
                string runtimeName = prefab.partInfo?.name ?? partName;
                var pristine = PristinePartFxResolver.GetForPart(runtimeName, prefab.partInfo?.configFileFullName);
                if (pristine == null || !pristine.Found || pristine.EffectsNode == null)
                    return false;

                ScanPristineEffectsEntries(pristine, moduleIndex, partName,
                    replacementModel, replacementPrefab, ref replacementEmission, ref replacementSpeed);
                replacementSource = $"pristine '{pristine.SourcePath}'";
            }

            int replacementAdded = replacementModel.Count + replacementPrefab.Count;
            if (replacementAdded == 0)
                return false;

            int dropped = modelFxEntries.Count + prefabFxEntries.Count;
            modelFxEntries.Clear();
            prefabFxEntries.Clear();
            modelFxEntries.AddRange(replacementModel);
            prefabFxEntries.AddRange(replacementPrefab);
            emissionCurve = replacementEmission;
            speedCurve = replacementSpeed;

            LogHotPathVerbose($"pristine-remnant-replace-{partName}-{moduleIndex}",
                $"waterfall fallback: '{partName}' midx={moduleIndex} replaced {dropped} " +
                $"post-MM remnant entries with {replacementAdded} entries " +
                $"from {replacementSource}");
            return true;
        }

        /// <summary>
        /// Waterfall fallback: rebuilds engine FX entries from the pristine on-disk part
        /// config when the post-MM EFFECTS scan produced nothing (the config pack deleted
        /// the stock particle nodes; see PristinePartFxResolver). Returns true when any
        /// entries were recovered.
        /// </summary>
        private static bool TryApplyPristineEngineFxFallback(
            Part prefab, ModuleEngines engine, int moduleIndex, string partName,
            List<(string nodeType, string transformName, string modelName, Vector3 localPos, Quaternion localRot, string groupName, GhostEngineFxCurves fxCurves)> modelFxEntries,
            List<(string prefabName, string transformName, Vector3 localOffset, Quaternion localRotation, bool hasLocalRotation, string groupName, GhostEngineFxCurves fxCurves)> prefabFxEntries,
            ref FloatCurve emissionCurve, ref FloatCurve speedCurve)
        {
            // ReStock recovery first: when ReStock authored this part's EFFECTS, the
            // install-faithful look is ReStock's; the stock pristine definitions only
            // serve as the fallback when the ReStock scan yields nothing.
            if (TryScanReStockEffectsEntries(prefab, moduleIndex, partName,
                modelFxEntries, prefabFxEntries, ref emissionCurve, ref speedCurve))
            {
                return true;
            }

            string runtimeName = prefab.partInfo?.name ?? partName;
            var pristine = PristinePartFxResolver.GetForPart(runtimeName, prefab.partInfo?.configFileFullName);
            if (pristine == null || !pristine.Found)
            {
                LogHotPathVerbose($"pristine-miss-{partName}-{moduleIndex}",
                    $"waterfall fallback: '{partName}' midx={moduleIndex} pristine part data unavailable");
                return false;
            }

            int before = modelFxEntries.Count + prefabFxEntries.Count;
            ScanPristineEffectsEntries(pristine, moduleIndex, partName,
                modelFxEntries, prefabFxEntries, ref emissionCurve, ref speedCurve);
            int effectsScanAdded = modelFxEntries.Count + prefabFxEntries.Count - before;

            // Legacy-fx parts (e.g. Swivel, Poodle, Mainsail): no pristine EFFECTS particles;
            // the running FX live in top-level fx_* keys. KSP's part compiler turned those
            // into prefab children that the config pack's key deletion removed, so synthesize
            // PREFAB_PARTICLE-style entries at the engine thrust transform with the engine's
            // fxOffset (mirroring ProcessEngineLegacyFx placement; midx 0 only, matching
            // legacy semantics). Names that no longer resolve to a stock FX prefab (the pack
            // may also have stripped the donor parts the prefab cache is built from) are
            // dropped here so the white-flame last resort can engage instead.
            int legacySynthAdded = 0;
            int legacySynthUnresolvable = 0;
            int legacySynthSubstituted = 0;
            int legacyBuiltinResolved = 0;
            int legacyFlameFallback = 0;
            if (effectsScanAdded == 0 && moduleIndex == 0 && pristine.LegacyFxPrefabNames.Count > 0)
            {
                string anchor = engine != null && !string.IsNullOrEmpty(engine.thrustVectorTransformName)
                    ? engine.thrustVectorTransformName
                    : "thrustTransform";
                Vector3 anchorOffset = engine != null ? engine.fxOffset : Vector3.zero;
                bool flameWanted = false;
                bool flameResolved = false;
                var usedResolvedNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < pristine.LegacyFxPrefabNames.Count; i++)
                {
                    string wanted = pristine.LegacyFxPrefabNames[i];
                    bool isFlame = wanted.IndexOf("flame", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isFlame)
                        flameWanted = true;

                    // Resolve through the candidate cascade: exact name first, then with
                    // size/variant suffixes stripped (the exact prefab's donor parts may
                    // all be Waterfall-patched while the generic family survives on
                    // unpatched parts like SRBs).
                    string resolved = null;
                    bool resolvedFromBuiltin = false;
                    List<string> candidates = PristinePartFxResolver.BuildLegacyFxNameCandidates(wanted);
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        if (GhostVisualBuilder.FindFxPrefabIncludingBuiltinEffects(
                                candidates[c], out bool fromBuiltin) != null)
                        {
                            resolved = candidates[c];
                            resolvedFromBuiltin = fromBuiltin;
                            break;
                        }
                    }

                    if (resolved == null)
                    {
                        legacySynthUnresolvable++;
                        continue;
                    }
                    bool exactResolution = string.Equals(
                        resolved, wanted, System.StringComparison.OrdinalIgnoreCase);
                    if (!usedResolvedNames.Add(resolved) && !exactResolution)
                    {
                        // DIFFERENT wanted names collapsing to one cascade donor would
                        // stack identical substitute FX at the same anchor; one instance
                        // is enough. Exact resolutions are never collapsed: duplicate
                        // pristine keys (Mainsail's two yellow_mini verniers) mean the
                        // stock look genuinely has that many instances.
                        continue;
                    }
                    if (!exactResolution)
                    {
                        legacySynthSubstituted++;
                        LogHotPathVerbose($"pristine-legacy-subst-{partName}-{moduleIndex}-{wanted}",
                            $"waterfall fallback: '{partName}' midx={moduleIndex} " +
                            $"substituted legacy FX '{wanted}' -> '{resolved}'");
                    }
                    if (isFlame)
                        flameResolved = true;
                    if (resolvedFromBuiltin)
                        legacyBuiltinResolved++;
                    prefabFxEntries.Add((
                        resolved, anchor, anchorOffset,
                        Quaternion.Euler(-90f, 0f, 0f), true, "pristine-legacy", null));
                    legacySynthAdded++;
                }

                // Flame guarantee: a ghost engine that wanted a flame must show one even
                // when only the smoke trail (or nothing) resolved; partial recovery
                // otherwise leaves a flame-less plume and the zero-entries last resort
                // never engages.
                if (flameWanted && !flameResolved)
                {
                    prefabFxEntries.Add((
                        "fx_exhaustFlame_white", anchor, anchorOffset,
                        Quaternion.Euler(-90f, 0f, 0f), true, "pristine-legacy-flame-fallback", null));
                    legacyFlameFallback = 1;
                    legacySynthAdded++;
                    LogHotPathVerbose($"pristine-legacy-flamefallback-{partName}-{moduleIndex}",
                        $"waterfall fallback: '{partName}' midx={moduleIndex} no legacy flame " +
                        "resolved; added best-effort white flame");
                }
            }

            int added = effectsScanAdded + legacySynthAdded;
            LogHotPathVerbose($"pristine-fallback-{partName}-{moduleIndex}",
                $"waterfall fallback: '{partName}' midx={moduleIndex} pristine recovery added {added} " +
                $"entries (effectsScan={effectsScanAdded}, legacySynth={legacySynthAdded}, " +
                $"legacyUnresolvable={legacySynthUnresolvable}, substituted={legacySynthSubstituted}, " +
                $"builtinResolved={legacyBuiltinResolved}, flameFallback={legacyFlameFallback}) " +
                $"from '{pristine.SourcePath}'");
            return added > 0;
        }
    }
}
