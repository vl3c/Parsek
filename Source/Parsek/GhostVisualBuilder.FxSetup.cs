using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // FX prefab lookup and cache, FX parenting and placement diagnostics, the world-space emitter velocity floor, FX magnitude baselines, KSP FX controller stripping and engine particle configuration.
    internal static partial class GhostVisualBuilder
    {
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

        /// <summary>
        /// Waterfall-gated resolution: FindFxPrefab first, then KSP's builtin
        /// "Effects/{name}" Resources path -- the exact path PartLoader uses to compile
        /// legacy fx_* keys, unreachable by ModuleManager deletes, so size-variant flame
        /// assets (fx_exhaustFlame_yellow_medium etc.) resolve even when every donor part
        /// was patched away. Used ONLY by the Waterfall pristine-fallback sites; never on
        /// stock paths (adding "Effects/" to the shared probe list would bypass the
        /// deliberate fxPrefabFallbacks substitutions).
        /// </summary>
        internal static GameObject FindFxPrefabIncludingBuiltinEffects(
            string prefabName, out bool fromBuiltinEffects)
        {
            fromBuiltinEffects = false;
            GameObject result = FindFxPrefab(prefabName);
            if (result != null)
                return result;

            string normalized = NormalizeFxPrefabName(prefabName);
            if (string.IsNullOrEmpty(normalized))
                return null;

            // Separate cache, NEVER the shared fxPrefabCache: a shared insert would make
            // stock-path FindFxPrefab resolution depend on whether a Waterfall-gated build
            // ran first (and could bypass the deliberate fxPrefabFallbacks substitutions).
            // Provenance is preserved so builtinResolved counters stay truthful on every
            // build, not just the first.
            if (builtinFxPrefabCache.TryGetValue(normalized, out GameObject cached))
            {
                if (cached != null)
                {
                    fromBuiltinEffects = true;
                    return cached;
                }
                builtinFxPrefabCache.Remove(normalized);
            }

            GameObject builtin = Resources.Load<GameObject>($"Effects/{normalized}");
            if (builtin == null)
                return null;

            if (!HasAnyParticleComponent(builtin))
            {
                ParsekLog.Verbose("GhostVisual",
                    $"builtin Effects prefab '{normalized}' has no particle components; ignoring");
                return null;
            }

            builtinFxPrefabCache[normalized] = builtin;
            fromBuiltinEffects = true;
            ParsekLog.Verbose("GhostVisual",
                $"FX prefab loaded from builtin Effects: '{normalized}'");
            return builtin;
        }

        /// <summary>
        /// A raw (never-instantiated) builtin asset may carry only a KSPParticleEmitter;
        /// its Awake adds the ParticleSystem at instantiation, which every consumer does.
        /// </summary>
        private static bool HasAnyParticleComponent(GameObject prefabRoot)
        {
            if (prefabRoot.GetComponentInChildren<ParticleSystem>(true) != null)
                return true;

            Component[] components = prefabRoot.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] != null && components[i].GetType().Name == "KSPParticleEmitter")
                    return true;
            }
            return false;
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
            builtinFxPrefabCache.Clear();
            fxLoadedObjectScanCompleted = false;
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
            if (!ParsekLog.IsVerboseEnabled) return;
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
            if (!ParsekLog.IsVerboseEnabled) return;
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
        /// Decides the minimum-flow velocity for a world-space, near-static particle
        /// emitter on a ghost. Returns false (velocity unchanged) for local-space
        /// emitters and for world-space emitters that already carry real velocity.
        /// The flow axis is the ENGINE'S EXHAUST AXIS expressed in the emitter's
        /// local frame, NOT the asset's authored axis: ReStock's per-part FX rigs
        /// orient fxTransformSmoke differently across SRBs (Clydesdale/Thoroughbred
        /// vs Hammer/Flea), so an assumed asset axis squirts sideways on some parts
        /// (same lesson as the legacy fx_* thrust-anchor-local rotation fix).
        /// </summary>
        internal static bool TryComputeWorldSpaceEmitterVelocityFloor(
            bool useWorldSpace, Vector3 localVelocity, Vector3 exhaustAxisEmitterLocal,
            out Vector3 flooredVelocity)
        {
            flooredVelocity = localVelocity;
            if (!useWorldSpace)
                return false;

            if (localVelocity.magnitude >= WorldSpaceEmitterSlowSpeedThreshold)
                return false;

            Vector3 axis = exhaustAxisEmitterLocal.magnitude > 0.001f
                ? exhaustAxisEmitterLocal.normalized
                : Vector3.down;
            flooredVelocity = axis * WorldSpaceEmitterFloorSpeed;
            return true;
        }

        /// <summary>
        /// Applies the world-space emitter velocity floor to every KSPParticleEmitter
        /// under a freshly cloned engine FX instance. Call AFTER StripKspFxControllers
        /// (the clone is ghost-owned by then). No-op for local-space emitters, i.e.
        /// every stock FX asset.
        /// </summary>
        internal static void ApplyWorldSpaceEmitterVelocityFloor(
            GameObject fxInstance, string partName, int moduleIndex, Vector3 exhaustAxisEmitterLocal)
        {
            if (fxInstance == null)
                return;

            var emitters = fxInstance.GetComponentsInChildren<KSPParticleEmitter>(true);
            for (int i = 0; i < emitters.Length; i++)
            {
                if (emitters[i] == null)
                    continue;
                if (!TryComputeWorldSpaceEmitterVelocityFloor(
                    emitters[i].useWorldSpace, emitters[i].localVelocity,
                    exhaustAxisEmitterLocal, out Vector3 floored))
                {
                    continue;
                }

                ParsekLog.VerboseRateLimited("GhostVisual",
                    $"ws-emitter-floor-{partName}-{moduleIndex}-{fxInstance.name}",
                    $"world-space emitter velocity floor: '{partName}' midx={moduleIndex} " +
                    $"fx='{fxInstance.name}' localVelocity {emitters[i].localVelocity} -> {floored} " +
                    $"(exhaust axis, emitter-local: {exhaustAxisEmitterLocal}; trail-by-motion " +
                    "asset would pool particles at the nozzle on a static ghost)",
                    5.0);
                emitters[i].localVelocity = floored;
            }
        }

        /// <summary>A named emitter field, but only if <c>GhostPlaybackLogic</c> can actually WRITE a
        /// scaled scalar back into it. A field of an unexpressible type answers null (no scaling for
        /// that field) instead of being cached for the applier to fail on every frame.</summary>
        private static System.Reflection.FieldInfo ResolveScalarMagnitudeField(
            System.Type emitterType, string fieldName, string partName, int moduleIndex)
        {
            System.Reflection.FieldInfo field = emitterType?.GetField(fieldName);
            if (field == null) return null;
            if (GhostPlaybackLogic.IsSupportedMagnitudeScalarFieldType(field.FieldType))
                return field;

            ParsekLog.VerboseRateLimited("GhostVisual",
                $"fx-baseline-unwritable-{fieldName}-{partName}-{moduleIndex}",
                $"FX magnitude field '{fieldName}' on '{partName}' midx={moduleIndex} is " +
                $"{field.FieldType.Name}, which cannot hold a scaled magnitude; that field stays " +
                "at its baseline", 60.0);
            return null;
        }

        private static System.Reflection.FieldInfo ResolveVectorMagnitudeField(
            System.Type emitterType, string fieldName, string partName, int moduleIndex)
        {
            System.Reflection.FieldInfo field = emitterType?.GetField(fieldName);
            if (field == null) return null;
            if (GhostPlaybackLogic.IsSupportedMagnitudeVectorFieldType(field.FieldType))
                return field;

            ParsekLog.VerboseRateLimited("GhostVisual",
                $"fx-baseline-unwritable-{fieldName}-{partName}-{moduleIndex}",
                $"FX magnitude field '{fieldName}' on '{partName}' midx={moduleIndex} is " +
                $"{field.FieldType.Name}, not a Vector3; that field stays at its baseline", 60.0);
            return null;
        }

        /// <summary>
        /// S1 (plume magnitude): capture the build-time magnitude of every emitter and particle
        /// system on one finished engine / RCS FX info, so playback can write
        /// <c>baseline * throttleRatio</c> instead of an absolute value.
        ///
        /// ONE capture point per info, called after the whole FX build for that module has run.
        /// That placement is load-bearing: <c>ApplyGhostEngineFxSizeBoost</c> (#383) and
        /// <c>ApplyWorldSpaceEmitterVelocityFloor</c> both mutate exactly the fields captured here
        /// and both run AFTER <c>StripKspFxControllers</c>, so capturing at strip time would pin a
        /// pre-fix baseline and runtime scaling would quietly undo those fixes.
        ///
        /// The three emitter fields are resolved reflectively even though <c>KSPParticleEmitter</c>
        /// is compile-time reachable: a KSP build that renames one then degrades that emitter to
        /// "not captured" (no scaling, today's boolean behaviour) instead of failing to build. They
        /// are also TYPE-CHECKED here rather than at write time — see
        /// <see cref="ResolveScalarMagnitudeField"/>.
        /// </summary>
        internal static void CaptureFxMagnitudeBaselines(
            List<KspEmitterRef> emitters, List<ParticleSystem> systems,
            List<GhostFxMagnitudeBaseline> baselineSink, string partName, int moduleIndex)
        {
            int capturedEmitters = 0;

            if (emitters != null)
            {
                for (int i = 0; i < emitters.Count; i++)
                {
                    KspEmitterRef r = emitters[i];
                    if (r.emitter == null || r.magnitudeBaselineCaptured) continue;

                    System.Type t = r.emitter.GetType();
                    // Resolved AND type-checked here, once, so the per-event applier never has to
                    // discover at write time that a field it cached cannot hold what it computed.
                    // The check is not academic: minEmission / maxEmission are declared INT on
                    // KSPParticleEmitter, and the 2026-08-11 H36 flight caught the applier throwing
                    // on every one of them. A field whose type the applier cannot express is
                    // dropped to null here — the emitter still captures its other fields and simply
                    // does not scale that one.
                    r.minEmissionField = ResolveScalarMagnitudeField(t, "minEmission", partName, moduleIndex);
                    r.maxEmissionField = ResolveScalarMagnitudeField(t, "maxEmission", partName, moduleIndex);
                    r.localVelocityField = ResolveVectorMagnitudeField(t, "localVelocity", partName, moduleIndex);

                    // World-space-ness rides along with the baseline: the captured localVelocity may
                    // BE the minimum-flow floor applied just above, and the playback scaler has to
                    // know not to scale it back under the pooling threshold the floor cleared.
                    r.baselineUseWorldSpace =
                        r.emitter is KSPParticleEmitter kspEmitter && kspEmitter.useWorldSpace;

                    try
                    {
                        // Convert.ToSingle, not a cast: the emission fields are int on stock KSP and
                        // an unboxing cast to float would throw on the very first one. The baseline
                        // is kept as a float on purpose — the RATIO is float arithmetic, and the
                        // applier re-quantises to the field's own type at write time.
                        if (r.minEmissionField != null)
                            r.baselineMinEmission = System.Convert.ToSingle(
                                r.minEmissionField.GetValue(r.emitter), CultureInfo.InvariantCulture);
                        if (r.maxEmissionField != null)
                            r.baselineMaxEmission = System.Convert.ToSingle(
                                r.maxEmissionField.GetValue(r.emitter), CultureInfo.InvariantCulture);
                        if (r.localVelocityField != null)
                            r.baselineLocalVelocity = (Vector3)r.localVelocityField.GetValue(r.emitter);
                        r.magnitudeBaselineCaptured =
                            r.minEmissionField != null || r.maxEmissionField != null
                            || r.localVelocityField != null;
                    }
                    catch (System.Exception ex)
                    {
                        r.magnitudeBaselineCaptured = false;
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"fx-baseline-fail-{partName}-{moduleIndex}",
                            $"FX magnitude baseline capture failed for '{partName}' " +
                            $"midx={moduleIndex}: {ex.Message}; plume stays boolean", 60.0);
                    }

                    emitters[i] = r;
                    if (r.magnitudeBaselineCaptured) capturedEmitters++;
                }
            }

            if (systems != null && baselineSink != null)
            {
                baselineSink.Clear();
                for (int i = 0; i < systems.Count; i++)
                {
                    ParticleSystem ps = systems[i];
                    if (ps == null)
                    {
                        baselineSink.Add(default);
                        continue;
                    }
                    var main = ps.main;
                    baselineSink.Add(new GhostFxMagnitudeBaseline
                    {
                        startSpeedMultiplier = main.startSpeedMultiplier,
                        startSizeMultiplier = main.startSizeMultiplier,
                        captured = true
                    });
                }
            }

            ParsekLog.VerboseRateLimited("GhostVisual",
                $"fx-baseline-{partName}-{moduleIndex}",
                $"FX magnitude baselines: '{partName}' midx={moduleIndex} " +
                $"emitters={capturedEmitters}/{emitters?.Count ?? 0} " +
                $"systems={baselineSink?.Count ?? 0}", 30.0);
        }

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

        /// <summary>
        /// Returns the build-time engine FX size scale for a given part name. Falls back
        /// to the default <see cref="GhostEngineFxSizeBoost"/> for engines without a
        /// registered override.
        /// </summary>
        internal static float ResolveEngineFxSizeBoost(string partName)
        {
            if (partName != null && engineFxSizeOverride.TryGetValue(partName, out float scale))
                return scale;
            return GhostEngineFxSizeBoost;
        }

        /// <summary>
        /// Uniformly bumps startSizeMultiplier and startLifetimeMultiplier on every
        /// ParticleSystem under an engine FX instance so ghost flames render at roughly
        /// stock full-thrust size. Touches the multiplier fields only, so each system's
        /// underlying startSize / startLifetime curve mode (Constant / Curve / TwoConstants /
        /// TwoCurves) is preserved — no risk of flattening animated size curves the way
        /// rewriting startSize with a new MinMaxCurve would.
        /// </summary>
        internal static void ApplyGhostEngineFxSizeBoost(GameObject fxInstance, float scale)
        {
            if (fxInstance == null || Mathf.Approximately(scale, 1f))
                return;
            ParticleSystem[] systems = fxInstance.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null)
                    continue;
                var main = systems[i].main;
                main.startSizeMultiplier *= scale;
                main.startLifetimeMultiplier *= scale;
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
    }
}
