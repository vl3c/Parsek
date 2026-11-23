using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>What a nozzle glow clip needs from its module and the part, before any sampling.</summary>
    internal struct NozzleGlowPlan
    {
        public string moduleName;
        public string animationName;
        public bool isRcs;
        public int[] moduleIndices;
        public float powerScale;
        public float responseSpeed;
        public float initialLevel;
    }

    /// <summary>One FXModuleThrottleEffects and the modules it sets by name.</summary>
    internal sealed class ThrottleGlowDriver
    {
        public string[] moduleIds;
        public int[] moduleIndices;
        public float responseSpeed;
        public int ordinal;
    }

    internal static partial class GhostVisualBuilder
    {
        private struct NozzleGlowColorSample
        {
            public int rendererIndex;
            public int slot;
            public string property;
            public Color[] samples;
        }

        /// <summary>Samples per clip, evenly spaced from 0 to 1.</summary>
        internal const int NozzleGlowSampleCount = 17;

        // partKey|anim|scope -> the clip's animated colours. Prefab-derived, cleared with the
        // other animation caches in ClearAnimateHeatCache.
        private static readonly Dictionary<string, List<NozzleGlowColorSample>> nozzleGlowSampleCache =
            new Dictionary<string, List<NozzleGlowColorSample>>();

        /// <summary>
        /// Pure: the recorder module indices (the k-th ModuleEngines of the part) stock's
        /// FXModuleAnimateThrottle follows, per <c>PartModuleList.FindEngineInterface</c>
        /// (decompiled): the MultiModeEngine when <paramref name="preferMultiMode"/> finds one,
        /// which forwards to whichever mode runs (every engine of the part); else the first engine
        /// named <paramref name="engineName"/>; else, with no name, the
        /// <paramref name="engineIndex"/>-th. A module that resolves nothing, or does not depend
        /// on the engine, follows the vessel throttle in stock, which no event records: the part's
        /// own engines stand in for it. Empty when the part has no engine.
        /// </summary>
        internal static int[] ResolveNozzleGlowEngineIndices(
            bool dependOnEngineState, bool preferMultiMode, string engineName, int engineIndex,
            IList<string> engineIds, bool hasMultiModeEngine)
        {
            int count = engineIds != null ? engineIds.Count : 0;
            if (count == 0) return new int[0];
            if (dependOnEngineState && !(preferMultiMode && hasMultiModeEngine))
            {
                if (!string.IsNullOrEmpty(engineName))
                {
                    for (int i = 0; i < count; i++)
                        if (engineIds[i] == engineName) return new[] { i };
                }
                else if (engineIndex >= 0 && engineIndex < count)
                {
                    return new[] { engineIndex };
                }
            }
            var all = new int[count];
            for (int i = 0; i < count; i++) all[i] = i;
            return all;
        }

        /// <summary>
        /// Pure: whether stock's FXModuleAnimateRCS runs (decompiled OnStart): every Animation of
        /// the model holds the clip, and there is one per thruster transform; otherwise it
        /// disables itself (or throws) and no nozzle glows.
        /// </summary>
        internal static bool RcsGlowClipsMatchThrusters(int animations, int animationsWithClip, int thrusterTransforms)
            => animations > 0 && animationsWithClip == animations && animations == thrusterTransforms;

        /// <summary>Pure: the clip time the recorded part persisted for its
        /// <paramref name="ordinal"/>-th module of that name: <c>animState</c> of an
        /// FXModuleAnimateThrottle, the mean of an FXModuleAnimateRCS's <c>animStates</c>.
        /// 0 when absent or unreadable.</summary>
        internal static float ReadPersistedGlowLevel(
            ConfigNode snapshotPartNode, string moduleName, int ordinal, bool isRcs)
            => ReadPersistedGlowLevel(snapshotPartNode, moduleName, ordinal, isRcs ? "animStates" : "animState", isRcs);

        private static float ReadPersistedGlowLevel(
            ConfigNode snapshotPartNode, string moduleName, int ordinal, string key, bool isList)
        {
            ConfigNode[] modules = snapshotPartNode?.GetNodes("MODULE");
            if (modules == null) return 0f;
            int seen = 0;
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i]?.GetValue("name") != moduleName) continue;
                if (seen++ != ordinal) continue;
                string raw = modules[i].GetValue(key);
                if (string.IsNullOrEmpty(raw)) return 0f;
                string[] parts = isList ? raw.Split(',') : new[] { raw };
                float sum = 0f;
                for (int p = 0; p < parts.Length; p++)
                {
                    if (!float.TryParse(parts[p], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                        || float.IsNaN(v) || float.IsInfinity(v))
                        return 0f;
                    sum += v < 0f ? 0f : (v > 1f ? 1f : v);
                }
                return sum / parts.Length;
            }
            return 0f;
        }

        /// <summary>
        /// Pure: whether a ghost can show an RCS module's glow. The recording holds one power per
        /// module, the mean over its nozzles, while stock heats each nozzle by its own force: on
        /// RCSBlock.v2 (2 of 9 nozzles firing) the mean tinted all four visible nozzles a dull red
        /// where stock lights two of them bright, no closer than a dark block (flown 2026-11-23).
        /// So only a one-nozzle module glows until the nozzles are recorded (todo
        /// RCS-PER-NOZZLE-RECORDING).
        /// </summary>
        internal static bool RcsGlowPowerIsPerNozzle(int thrusterTransforms) => thrusterTransforms == 1;

        /// <summary>
        /// The glow clips of the part: every FXModuleAnimateThrottle that moves its clip's time
        /// (<c>affectTime</c>; the turbofan's fan spin does not), and an FXModuleAnimateRCS whose
        /// animations stock accepts and whose one nozzle's power the recording holds.
        /// </summary>
        private static List<NozzleGlowPlan> CollectNozzleGlowPlans(
            Part prefab, ConfigNode partNode, Transform modelRoot)
        {
            List<string> engineIds = null;
            bool hasMultiMode = false, hasGlowModule = false;
            ModuleRCS rcs = null;
            int rcsIndex = -1, rcsSeen = 0;
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                PartModule module = prefab.Modules[m];
                if (module is ModuleEngines engine)
                {
                    if (engineIds == null) engineIds = new List<string>();
                    engineIds.Add(engine.engineID);
                }
                else if (module is MultiModeEngine)
                {
                    hasMultiMode = true;
                }
                else if (module is ModuleRCS r)
                {
                    if (rcs == null) { rcs = r; rcsIndex = rcsSeen; }
                    rcsSeen++;
                }
                else if (module is FXModuleAnimateThrottle || module is FXModuleAnimateRCS)
                {
                    hasGlowModule = true;
                }
            }
            if (!hasGlowModule) return null;

            List<NozzleGlowPlan> plans = null;
            var ordinals = new Dictionary<string, int>();
            var seenClips = new HashSet<string>();
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                PartModule module = prefab.Modules[m];
                var throttle = module as FXModuleAnimateThrottle;
                var rcsGlow = module as FXModuleAnimateRCS;
                if (throttle == null && rcsGlow == null) continue;
                ordinals.TryGetValue(module.moduleName, out int ordinal);
                ordinals[module.moduleName] = ordinal + 1;

                var plan = new NozzleGlowPlan { moduleName = module.moduleName };
                if (throttle != null)
                {
                    if (!throttle.affectTime || string.IsNullOrEmpty(throttle.animationName)) continue;
                    plan.animationName = throttle.animationName;
                    plan.responseSpeed = throttle.responseSpeed;
                    plan.powerScale = 1f;
                    plan.moduleIndices = ResolveNozzleGlowEngineIndices(
                        throttle.dependOnEngineState, throttle.preferMultiMode, throttle.engineName,
                        throttle.engineIndex, engineIds, hasMultiMode);
                }
                else
                {
                    if (!rcsGlow.affectTime || string.IsNullOrEmpty(rcsGlow.animationName) || rcs == null) continue;
                    Animation[] animations = modelRoot.GetComponentsInChildren<Animation>(true);
                    int withClip = 0;
                    for (int a = 0; a < animations.Length; a++)
                        if (animations[a][rcsGlow.animationName] != null) withClip++;
                    int thrusters = prefab.FindModelTransforms(rcs.thrusterTransformName).Length;
                    bool runsInStock = RcsGlowClipsMatchThrusters(animations.Length, withClip, thrusters);
                    if (!runsInStock || !RcsGlowPowerIsPerNozzle(thrusters))
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", "nozzle-glow-rcs-" + prefab.name,
                            $"  Nozzle glow '{prefab.name}': FXModuleAnimateRCS '{rcsGlow.animationName}' no glow, " +
                            (runsInStock ? "per-nozzle power not recorded " : "does not run in stock ") +
                            $"(animations={animations.Length} withClip={withClip} thrusters={thrusters})", 60.0);
                        continue;
                    }
                    plan.isRcs = true;
                    plan.animationName = rcsGlow.animationName;
                    plan.responseSpeed = rcsGlow.responseSpeed;
                    // Stock: Clamp01(thrustForces[i] * thrustForceMult), a force; the recorded
                    // power is a fraction of thrusterPower.
                    plan.powerScale = rcs.thrusterPower * rcsGlow.thrustForceMult;
                    plan.moduleIndices = new[] { rcsIndex };
                }
                if (plan.moduleIndices.Length == 0 || !seenClips.Add(plan.animationName)) continue;
                plan.initialLevel = ReadPersistedGlowLevel(partNode, plan.moduleName, ordinal, plan.isRcs);
                if (plans == null) plans = new List<NozzleGlowPlan>();
                plans.Add(plan);
            }
            return plans;
        }

        /// <summary>
        /// The part's FXModuleThrottleEffects that set modules by name, each with the engines it
        /// follows; null when there is none. ReStock ships two per engine, one for the bell's heat
        /// (a 10 to 40 s ramp) and one for the throat.
        /// </summary>
        private static List<ThrottleGlowDriver> CollectThrottleGlowDrivers(Part prefab)
        {
            List<string> engineIds = null;
            bool hasMultiMode = false, any = false;
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                PartModule module = prefab.Modules[m];
                if (module is ModuleEngines engine)
                {
                    if (engineIds == null) engineIds = new List<string>();
                    engineIds.Add(engine.engineID);
                }
                else if (module is MultiModeEngine)
                {
                    hasMultiMode = true;
                }
                else if (module is FXModuleThrottleEffects)
                {
                    any = true;
                }
            }
            if (!any || engineIds == null) return null;

            List<ThrottleGlowDriver> drivers = null;
            int ordinal = 0;
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                if (!(prefab.Modules[m] is FXModuleThrottleEffects effects)) continue;
                var driver = new ThrottleGlowDriver
                {
                    moduleIds = SplitFxModuleNames(effects.fxModuleNames),
                    moduleIndices = ResolveNozzleGlowEngineIndices(
                        effects.dependOnEngineState, effects.preferMultiMode, effects.engineName,
                        effects.engineIndex, engineIds, hasMultiMode),
                    responseSpeed = effects.responseSpeed,
                    ordinal = ordinal++,
                };
                if (driver.moduleIds.Length == 0 || driver.moduleIndices.Length == 0) continue;
                if (drivers == null) drivers = new List<ThrottleGlowDriver>();
                drivers.Add(driver);
            }
            return drivers;
        }

        /// <summary>Pure: stock <c>PartModuleFXSetter.OnStart</c>: split on ',', each trimmed.</summary>
        internal static string[] SplitFxModuleNames(string fxModuleNames)
        {
            if (string.IsNullOrEmpty(fxModuleNames)) return new string[0];
            string[] names = fxModuleNames.Split(',');
            for (int i = 0; i < names.Length; i++) names[i] = names[i].Trim();
            return names;
        }

        private static HashSet<string> ThrottleDrivenModuleIds(List<ThrottleGlowDriver> drivers)
        {
            if (drivers == null) return null;
            var ids = new HashSet<string>();
            for (int d = 0; d < drivers.Count; d++)
                ids.UnionWith(drivers[d].moduleIds);
            return ids;
        }

        /// <summary>The Harmony id of KSPCommunityFixes, whose ModuleColorChangerOptimization
        /// replaces <c>ModuleColorChanger.Update</c> with one that writes only while its ramp moves.</summary>
        internal const string KspCommunityFixesHarmonyId = "KSPCommunityFixes";

        private static bool? colorChangerWritesOnlyWhileRamping;

        private static bool ColorChangerWritesOnlyWhileRamping()
        {
            if (colorChangerWritesOnlyWhileRamping.HasValue) return colorChangerWritesOnlyWhileRamping.Value;
            System.Reflection.MethodInfo update = HarmonyLib.AccessTools.Method(typeof(ModuleColorChanger), "Update");
            HarmonyLib.Patches patches = update != null ? HarmonyLib.Harmony.GetPatchInfo(update) : null;
            bool patched = patches != null && patches.Owners.Contains(KspCommunityFixesHarmonyId);
            colorChangerWritesOnlyWhileRamping = patched;
            ParsekLog.Verbose("GhostVisual",
                $"  Throttle glow: ModuleColorChanger.Update writes {(patched ? "only while its ramp moves (KSPCommunityFixes)" : "every frame (stock)")}");
            return patched;
        }

        /// <summary>
        /// Pure: the glow of one throttle-driven colour changer. A useRate module shows its ramp,
        /// which stock restarts at <c>animState</c>'s end on load (it does not persist where it
        /// stood); another shows the setter's level.
        /// </summary>
        internal static NozzleGlowInfo NewThrottleGlow(
            uint persistentId, ThrottleGlowDriver driver, ThrottleGlowCurve curve,
            float persistedState, bool settledShowsScalar)
        {
            var glow = new NozzleGlowInfo
            {
                partPersistentId = persistentId,
                moduleName = nameof(FXModuleThrottleEffects),
                animationName = curve.moduleId,
                isColorChanger = true,
                moduleKeys = new ulong[driver.moduleIndices.Length],
                modulePowers = new float[driver.moduleIndices.Length],
                powerScale = 1f,
                approachRate = GhostPlaybackLogic.ComputeNozzleGlowRate(driver.responseSpeed),
                initialLevel = persistedState,
                ramps = curve.useRate,
                rampRate = curve.animRate,
                settledShowsScalar = settledShowsScalar,
                initialRamp = curve.useRate && curve.animState ? 1f : 0f,
            };
            for (int k = 0; k < driver.moduleIndices.Length; k++)
                glow.moduleKeys[k] = FlightRecorder.EncodeEngineKey(persistentId, driver.moduleIndices[k]);
            return glow;
        }

        /// <summary>The first driver that names the module, or null.</summary>
        internal static ThrottleGlowDriver FindThrottleGlowDriver(List<ThrottleGlowDriver> drivers, string moduleId)
        {
            if (drivers == null) return null;
            for (int d = 0; d < drivers.Count; d++)
                if (System.Array.IndexOf(drivers[d].moduleIds, moduleId) >= 0) return drivers[d];
            return null;
        }

        /// <summary>
        /// The glows of the throttle-driven colour changers, on the material clones
        /// <see cref="BuildColorChangerInfos"/> made for them.
        /// </summary>
        private static int BuildThrottleGlows(
            List<ThrottleGlowDriver> drivers, ConfigNode partNode, ref HeatGhostInfo heatInfo,
            List<ColorChangerGhostInfo> colorChangerInfos, uint persistentId)
        {
            if (drivers == null || colorChangerInfos == null) return 0;
            int built = 0;
            for (int c = 0; c < colorChangerInfos.Count; c++)
            {
                ColorChangerGhostInfo info = colorChangerInfos[c];
                if (info?.throttleGlow == null) continue;
                ThrottleGlowDriver driver = FindThrottleGlowDriver(drivers, info.throttleGlow.moduleId);
                if (driver == null) continue;
                NozzleGlowInfo glow = NewThrottleGlow(persistentId, driver, info.throttleGlow,
                    ReadPersistedGlowLevel(partNode, nameof(FXModuleThrottleEffects), driver.ordinal, "state", false),
                    ColorChangerWritesOnlyWhileRamping());
                for (int i = 0; i < info.materials.Count; i++)
                    glow.colors.Add(new NozzleGlowColor
                    {
                        material = info.materials[i].material,
                        property = info.shaderProperty,
                        samples = info.throttleGlow.samples,
                    });
                AddNozzleGlow(ref heatInfo, glow, persistentId);
                built++;
            }
            return built;
        }

        private static void AddNozzleGlow(ref HeatGhostInfo heatInfo, NozzleGlowInfo glow, uint persistentId)
        {
            if (heatInfo == null)
                heatInfo = new HeatGhostInfo
                {
                    partPersistentId = persistentId,
                    throttleGlowsOnly = glow.isColorChanger,
                };
            if (heatInfo.nozzleGlows == null) heatInfo.nozzleGlows = new List<NozzleGlowInfo>();
            heatInfo.nozzleGlows.Add(glow);
        }

        /// <summary>
        /// Builds the part's nozzle glows and puts them on its heat info (created when the part
        /// has none, which also keeps the reentry glow off these materials). Runs after the light,
        /// colour-changer and rest-state writes, reuses their per-ghost materials, and leaves a
        /// property a light or a colour changer drives to it. A colour changer an
        /// FXModuleThrottleEffects drives is a glow of its own.
        /// </summary>
        private static void BuildNozzleGlows(
            Part prefab, ConfigNode partNode, Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap, ref HeatGhostInfo heatInfo, LightGhostInfo lightInfo,
            List<ColorChangerGhostInfo> colorChangerInfos, List<ThrottleGlowDriver> throttleDrivers,
            uint persistentId, string partName)
        {
            if (prefab == null || modelRoot == null || ghostModelNode == null || cloneMap == null) return;
            int throttleGlows = BuildThrottleGlows(
                throttleDrivers, partNode, ref heatInfo, colorChangerInfos, persistentId);
            if (throttleDrivers != null)
                ParsekLog.VerboseRateLimited("GhostVisual", $"throttle-glow-{partName}",
                    $"Throttle glow '{partName}' pid={persistentId}: drivers={throttleDrivers.Count} " +
                    $"bound={throttleGlows}" + DescribeNozzleGlows(heatInfo), 60.0);
            List<NozzleGlowPlan> plans = CollectNozzleGlowPlans(prefab, partNode, modelRoot);
            if (plans == null) return;

            string partKey = prefab.partInfo?.name ?? prefab.name;
            Renderer[] modelRenderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            var owned = CollectPartOwnedMaterialProperties(heatInfo, lightInfo, colorChangerInfos);
            var perGhost = new HashSet<Material>(owned.Keys);
            int bound = 0, colorsLeftToOwner = 0;
            for (int p = 0; p < plans.Count; p++)
            {
                NozzleGlowPlan plan = plans[p];
                List<NozzleGlowColorSample> samples = GetNozzleGlowSamples(modelRoot, plan, partKey);
                if (samples.Count == 0) continue;
                var glow = new NozzleGlowInfo
                {
                    partPersistentId = persistentId,
                    moduleName = plan.moduleName,
                    animationName = plan.animationName,
                    isRcs = plan.isRcs,
                    moduleKeys = new ulong[plan.moduleIndices.Length],
                    modulePowers = new float[plan.moduleIndices.Length],
                    powerScale = plan.powerScale,
                    approachRate = GhostPlaybackLogic.ComputeNozzleGlowRate(plan.responseSpeed),
                    initialLevel = plan.initialLevel,
                };
                for (int k = 0; k < plan.moduleIndices.Length; k++)
                    glow.moduleKeys[k] = FlightRecorder.EncodeEngineKey(persistentId, plan.moduleIndices[k]);

                for (int i = 0; i < samples.Count; i++)
                {
                    NozzleGlowColorSample s = samples[i];
                    if (s.rendererIndex >= modelRenderers.Length) continue;
                    Renderer ghostRenderer = ResolveGhostRenderer(
                        modelRenderers[s.rendererIndex], modelRoot, ghostModelNode, cloneMap);
                    if (ghostRenderer == null) continue;
                    Material[] slots = ghostRenderer.sharedMaterials;
                    if (slots == null || s.slot >= slots.Length || slots[s.slot] == null) continue;
                    Material material = slots[s.slot];
                    if (!material.HasProperty(s.property)) continue;
                    if (owned.TryGetValue(material, out HashSet<string> props) && props.Contains(s.property))
                    {
                        colorsLeftToOwner++;
                        continue;
                    }
                    if (!perGhost.Contains(material))
                    {
                        material = new Material(material);
                        slots[s.slot] = material;
                        ghostRenderer.sharedMaterials = slots;
                        perGhost.Add(material);
                    }
                    glow.colors.Add(new NozzleGlowColor
                    {
                        material = material,
                        property = s.property,
                        samples = s.samples,
                    });
                }
                if (glow.colors.Count == 0) continue;
                AddNozzleGlow(ref heatInfo, glow, persistentId);
                bound++;
            }

            ParsekLog.VerboseRateLimited("GhostVisual", $"nozzle-glow-{partName}",
                $"Nozzle glow '{partName}' pid={persistentId}: clips={plans.Count} bound={bound} " +
                $"colorsLeftToOwner={colorsLeftToOwner}" + DescribeNozzleGlows(heatInfo), 60.0);
        }

        private static string DescribeNozzleGlows(HeatGhostInfo heatInfo)
        {
            if (heatInfo?.nozzleGlows == null) return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < heatInfo.nozzleGlows.Count; i++)
            {
                NozzleGlowInfo g = heatInfo.nozzleGlows[i];
                sb.Append(" [").Append(g.moduleName).Append(" '").Append(g.animationName).Append("' colors=")
                    .Append(g.colors.Count.ToString(CultureInfo.InvariantCulture)).Append(" modules=")
                    .Append(g.moduleKeys.Length.ToString(CultureInfo.InvariantCulture)).Append(" scale=")
                    .Append(g.powerScale.ToString("0.###", CultureInfo.InvariantCulture)).Append(" rate=")
                    .Append(g.approachRate.ToString("0.####", CultureInfo.InvariantCulture)).Append(" initial=")
                    .Append(g.initialLevel.ToString("0.###", CultureInfo.InvariantCulture));
                if (g.ramps)
                    sb.Append(" ramp=").Append(g.initialRamp.ToString("0.###", CultureInfo.InvariantCulture))
                        .Append('@').Append(g.rampRate.ToString("0.####", CultureInfo.InvariantCulture));
                sb.Append(']');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Stamps the UT the persisted levels were read at, the snapshot vessel's <c>lastUT</c>
        /// (NaN when absent). A build from the end-of-recording snapshot carries the glow of the
        /// wrong end: its nozzles start cold (<see cref="SnapshotBaselineTrustedForBuildType"/>).
        /// </summary>
        internal static void StampPersistedGlowLevels(HeatGhostInfo heatInfo, ConfigNode snapshotNode, bool trusted)
        {
            if (heatInfo?.nozzleGlows == null) return;
            double ut = trusted ? ReadSnapshotUT(snapshotNode) : double.NaN;
            for (int i = 0; i < heatInfo.nozzleGlows.Count; i++)
            {
                heatInfo.nozzleGlows[i].initialUT = ut;
                if (trusted) continue;
                heatInfo.nozzleGlows[i].initialLevel = 0f;
                heatInfo.nozzleGlows[i].initialRamp = 0f;
            }
        }

        /// <summary>Pure: when a vessel snapshot was taken (stock <c>ProtoVessel</c>'s lastUT);
        /// NaN when absent, unreadable or never stepped (0 or negative).</summary>
        internal static double ReadSnapshotUT(ConfigNode snapshotNode)
        {
            return double.TryParse(snapshotNode?.GetValue("lastUT"), NumberStyles.Float,
                       CultureInfo.InvariantCulture, out double ut)
                   && !double.IsInfinity(ut) && ut > 0.0
                ? ut
                : double.NaN;
        }

        private static List<NozzleGlowColorSample> GetNozzleGlowSamples(
            Transform modelRoot, NozzleGlowPlan plan, string partKey)
        {
            // Stock FXModuleAnimateThrottle drives the first Animation that holds the clip,
            // FXModuleAnimateRCS one per nozzle.
            return GetClipColorSamples(modelRoot, plan.animationName, !plan.isRcs, partKey);
        }

        private static List<NozzleGlowColorSample> GetClipColorSamples(
            Transform modelRoot, string animationName, bool firstAnimationOnly, string partKey)
        {
            string key = partKey + "|" + animationName + (firstAnimationOnly ? "|first" : "|all");
            if (nozzleGlowSampleCache.TryGetValue(key, out List<NozzleGlowColorSample> cached)) return cached;
            List<NozzleGlowColorSample> samples =
                SampleNozzleGlowClip(modelRoot, animationName, firstAnimationOnly, partKey);
            nozzleGlowSampleCache[key] = samples;
            return samples;
        }

        private static List<NozzleGlowColorSample> SampleNozzleGlowClip(
            Transform modelRoot, string animName, bool firstAnimationOnly, string partKey)
        {
            var result = new List<NozzleGlowColorSample>();
            GameObject tempClone = Object.Instantiate(modelRoot.gameObject);
            try
            {
                var animations = new List<Animation>();
                var states = new List<AnimationState>();
                Animation[] all = tempClone.GetComponentsInChildren<Animation>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    AnimationState state = all[i] != null ? all[i][animName] : null;
                    if (state == null) continue;
                    animations.Add(all[i]);
                    states.Add(state);
                    if (firstAnimationOnly) break;
                }
                if (states.Count == 0)
                {
                    ParsekLog.Verbose("GhostVisual",
                        $"  NozzleGlow '{partKey}': clip '{animName}' not found on the model");
                    return result;
                }

                Renderer[] renderers = tempClone.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < states.Count; i++)
                {
                    states[i].enabled = true;
                    states[i].speed = 0f;
                    states[i].weight = 1f;
                }

                var reads = new Dictionary<string, (Color block, Color material, int r, int slot, string prop)>[NozzleGlowSampleCount];
                for (int n = 0; n < NozzleGlowSampleCount; n++)
                {
                    SampleAt(animations, states, n / (float)(NozzleGlowSampleCount - 1));
                    reads[n] = ReadRendererColors(renderers);
                }

                foreach (KeyValuePair<string, (Color block, Color material, int r, int slot, string prop)> kv in reads[0])
                {
                    bool fromBlock = false, fromMaterial = false, complete = true;
                    for (int n = 1; n < NozzleGlowSampleCount; n++)
                    {
                        if (!reads[n].TryGetValue(kv.Key, out var at)) { complete = false; break; }
                        fromBlock |= IsAnimatedColor(kv.Value.block, at.block);
                        fromMaterial |= IsAnimatedColor(kv.Value.material, at.material);
                    }
                    if (!complete || (!fromBlock && !fromMaterial)) continue;
                    var samples = new Color[NozzleGlowSampleCount];
                    for (int n = 0; n < NozzleGlowSampleCount; n++)
                    {
                        var at = reads[n][kv.Key];
                        samples[n] = fromBlock ? at.block : at.material;
                    }
                    result.Add(new NozzleGlowColorSample
                    {
                        rendererIndex = kv.Value.r,
                        slot = kv.Value.slot,
                        property = kv.Value.prop,
                        samples = samples,
                    });
                }
                ParsekLog.Verbose("GhostVisual",
                    $"  NozzleGlow '{partKey}': clip '{animName}' on {animations.Count} animation(s) animates {result.Count} colour(s)");
            }
            finally
            {
                Object.DestroyImmediate(tempClone);
            }
            return result;
        }
    }
}
