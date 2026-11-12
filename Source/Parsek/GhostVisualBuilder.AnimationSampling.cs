using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // Animation state sampling at build time: parachute canopy, deploy / loop / gear / ladder / cargo bay / deployable animations and the transform snapshots they diff.
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Create a fake parachute canopy (flattened hemisphere) and parent it to
        /// the ghost part identified by persistentId. Returns the canopy GameObject.
        /// </summary>
        internal static GameObject CreateFakeCanopy(GameObject ghostRoot, uint partPersistentId)
        {
            if (ghostRoot == null) return null;

            Transform partTransform = FindGhostPartTransform(ghostRoot, partPersistentId);
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

                // S2: the ONE place a real KSP AnimationState is in hand for every deployable
                // family (gear, bays, panels, ladders, drills all route through here), so the clip
                // length is captured once, here, rather than at four call sites. Stored in its own
                // dictionary rather than widened into the existing sample tuple: that tuple's shape
                // is repeated in six signatures and a 7-element -> 8-element change would touch all
                // of them for one float.
                // Written HERE, before the delta-extraction below can bail: a length is a
                // property of the clip, and a clip that produced no usable transform deltas for
                // one family is still the right duration for the family that does resolve. The
                // key is per PART, so a multi-family part is last-write-wins - bounded by the
                // [0.25, 30] s clamp, and the family that actually resolves samples last.
                animationClipLengthCache[partKey] =
                    GhostPlaybackLogic.ClampDeployableClipSeconds(state.length);

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

        /// <summary>
        /// S7: samples a LOOPING animation clip at <see cref="ConverterLoopPhaseCount"/> evenly
        /// spaced normalized times, keeping every transform the clip actually moves.
        ///
        /// A sibling of <see cref="SampleAnimationStates"/> rather than an extension of it: that
        /// method's whole contract is a stowed/deployed PAIR (it even scores which endpoint is
        /// "stowed"), and a cyclic clip has no such pair — its endpoints coincide. Same temp-clone
        /// mechanics, same DestroyImmediate in a finally.
        ///
        /// A transform is kept when ANY sampled phase differs from phase 0 beyond the same
        /// thresholds <see cref="CollectTransformDeltas"/> uses, so a clip that animates nothing
        /// (or that could not be found) yields null and the part simply has no running loop.
        /// </summary>
        private static List<(string path, ConverterLoopPose[] phases)> SampleAnimationLoopStates(
            Part prefab, string animName, string label, out float clipSeconds)
        {
            clipSeconds = GhostPlaybackLogic.ClampDeployableClipSeconds(0f);
            if (prefab == null || string.IsNullOrEmpty(animName)) return null;

            string partKey = prefab.partInfo?.name ?? prefab.name;
            string cacheKey = partKey + "|" + animName;
            if (converterLoopSampleCache.TryGetValue(cacheKey, out var cached))
            {
                clipSeconds = cached.clipSeconds;
                return cached.samples;
            }

            Transform modelRoot = FindModelRoot(prefab);
            if (modelRoot == null)
            {
                converterLoopSampleCache[cacheKey] = (null, clipSeconds);
                return null;
            }

            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);
            List<(string path, ConverterLoopPose[] phases)> result = null;
            try
            {
                Animation anim = FindAnimation(tempClone, animName, null,
                    AnimLookup.Simple, label, partKey);
                AnimationState state = anim != null ? anim[animName] : null;
                if (state == null)
                {
                    ParsekLog.Verbose("GhostVisual",
                        $"  {label} '{partKey}': running animation '{animName}' not found on model clone");
                    converterLoopSampleCache[cacheKey] = (null, clipSeconds);
                    return null;
                }

                clipSeconds = GhostPlaybackLogic.ClampDeployableClipSeconds(state.length);

                var allTransforms = tempClone.GetComponentsInChildren<Transform>(true);
                state.enabled = true;
                state.speed = 0f;
                state.weight = 1f;
                anim.Play(animName);

                // Phase i is sampled at i/N, NOT i/(N-1): the clip is cyclic, so normalized time 1
                // is the same pose as 0 and including both would spend a phase slot on a duplicate
                // and stall the loop for one interval. The drive wraps from the last phase back to
                // phase 0.
                var perPhase = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>[ConverterLoopPhaseCount];
                for (int phase = 0; phase < ConverterLoopPhaseCount; phase++)
                {
                    state.normalizedTime = (float)phase / ConverterLoopPhaseCount;
                    anim.Sample();
                    perPhase[phase] = SnapshotTransformStates(allTransforms, tempClone.transform);
                }

                result = new List<(string, ConverterLoopPose[])>();
                foreach (var kv in perPhase[0])
                {
                    string path = kv.Key;
                    var poses = new ConverterLoopPose[ConverterLoopPhaseCount];
                    bool moves = false;
                    bool complete = true;

                    for (int phase = 0; phase < ConverterLoopPhaseCount; phase++)
                    {
                        if (!perPhase[phase].TryGetValue(path, out var s)) { complete = false; break; }
                        poses[phase] = new ConverterLoopPose { pos = s.pos, rot = s.rot, scale = s.scale };

                        if (!moves && phase > 0)
                        {
                            moves = (s.pos - kv.Value.pos).sqrMagnitude > 0.0001f
                                || Quaternion.Angle(s.rot, kv.Value.rot) > 0.01f
                                || (s.scale - kv.Value.scale).sqrMagnitude > 0.0001f;
                        }
                    }

                    if (complete && moves)
                        result.Add((path, poses));
                }

                if (result.Count == 0)
                {
                    ParsekLog.Verbose("GhostVisual",
                        $"  {label} '{partKey}': running animation '{animName}' moves nothing over {ConverterLoopPhaseCount} phases");
                    result = null;
                }
                else
                {
                    ParsekLog.Verbose("GhostVisual",
                        $"  {label} '{partKey}': sampled {result.Count} looping transform(s) from '{animName}' " +
                        $"over {ConverterLoopPhaseCount} phases (clip={clipSeconds.ToString("F2", CultureInfo.InvariantCulture)}s)");
                }
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }

            converterLoopSampleCache[cacheKey] = (result, clipSeconds);
            return result;
        }

        /// <summary>
        /// S7: builds one part's running loop from its ModuleAnimationGroup
        /// <c>activeAnimationName</c>.
        ///
        /// Stock ground truth, read off the shipped configs: RadialDrill pairs
        /// <c>Drill_Deploy</c> with <c>Drill_Running</c>, MiniDrill pairs <c>Deploy</c> with
        /// <c>Drill</c>, and the large ISRU and the orbital scanner have an EMPTY
        /// <c>deployAnimationName</c> with only a running one (<c>ProcessorLarge_running</c> /
        /// <c>miniscanner</c>). That last shape is why this reads <c>activeAnimationName</c>
        /// DIRECTLY instead of going through <c>TryGetAnimationGroupDeployAnimation</c>, which
        /// requires a non-empty deploy name and would have skipped them.
        /// </summary>
        private static ConverterLoopGhostInfo TryBuildConverterLoopInfo(
            Part prefab, PartModule module, uint persistentId, string partName,
            Transform modelNodeTransform)
        {
            if (!TryGetModuleStringField(module, "activeAnimationName", out string activeAnim))
                return null;
            if (string.IsNullOrEmpty(activeAnim))
                return null;

            var samples = SampleAnimationLoopStates(
                prefab, activeAnim, "ConverterLoop", out float clipSeconds);
            if (samples == null || samples.Count == 0)
                return null;

            var resolved = new List<ConverterLoopTransformState>();
            for (int i = 0; i < samples.Count; i++)
            {
                Transform ghostT = FindTransformByPath(modelNodeTransform, samples[i].path);
                if (ghostT == null) continue;
                resolved.Add(new ConverterLoopTransformState
                {
                    t = ghostT,
                    phases = samples[i].phases
                });
            }

            if (resolved.Count == 0)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"converter-loop-unresolved-{partName}",
                    $"    ConverterLoop '{partName}' pid={persistentId}: none of {samples.Count} looping " +
                    $"transform(s) from '{activeAnim}' resolved onto the ghost", 60.0);
                return null;
            }

            return new ConverterLoopGhostInfo
            {
                partPersistentId = persistentId,
                transforms = resolved,
                clipLengthSeconds = clipSeconds
            };
        }

        /// <summary>
        /// The sampled clip length for a part, or the default when the part's animation was never
        /// sampled (surface-deployable families synthesise their poses without an Animation
        /// component, so they legitimately have none).
        /// </summary>
        internal static float ResolveDeployableClipSeconds(string partKey)
        {
            if (!string.IsNullOrEmpty(partKey)
                && animationClipLengthCache.TryGetValue(partKey, out float seconds))
            {
                return seconds;
            }
            return GhostPlaybackLogic.DefaultDeployableClipSeconds;
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
            Part prefab, string animationName, float closedPosition, float? deployLimitStop)
        {
            string partKey = prefab.partInfo?.name ?? prefab.name;
            // A deploy-limited instance samples a different open pose from the same part at
            // full deploy, so its cache key carries the stop. '#' never appears in a part
            // name, and the ladder's compound keys use '|', so no collision either way.
            string key = deployLimitStop.HasValue
                ? partKey + "#cargoStop=" + deployLimitStop.Value.ToString("R", CultureInfo.InvariantCulture)
                : partKey;
            if (animationSampleCache.TryGetValue(key, out var cached))
                return cached;

            if (string.IsNullOrEmpty(animationName))
            {
                ParsekLog.Verbose("GhostVisual", $"  CargoBay '{partKey}': no animationName - skipping animation sampling");
                animationSampleCache[key] = null;
                return null;
            }

            // Animation endpoints from closedPosition; a deploy-limited bay's open pose is
            // sampled at its limit stop, where the real doors stopped.
            if (!TryResolveCargoBaySampleTimes(closedPosition, deployLimitStop,
                    out float closedTime, out float openTime))
            {
                ParsekLog.Verbose("GhostVisual", $"  CargoBay '{partKey}': non-standard closedPosition={closedPosition} - skipping");
                animationSampleCache[key] = null;
                return null;
            }
            if (deployLimitStop.HasValue)
            {
                ParsekLog.Verbose("GhostVisual",
                    $"  CargoBay '{partKey}': deploy-limited open pose sampled at animTime=" +
                    openTime.ToString("F3", CultureInfo.InvariantCulture) +
                    " closedTime=" + closedTime.ToString("F0", CultureInfo.InvariantCulture));
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
            // THE SAME predicate the recorder screens with, so "the recorder does not seed it"
            // and "the builder samples no pose for it" cannot drift apart.
            if (PartStateSeeder.DeployableHasNoPoseAnimation(animName))
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
    }
}
