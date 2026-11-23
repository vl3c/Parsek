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
        public bool vesselThrottleUnrecorded;
    }

    /// <summary>What decides whether a recorded engine power tells the vessel's throttle.</summary>
    internal struct EngineThrottleSetup
    {
        /// <summary>The engine does not follow the vessel's throttle (independent or locked).</summary>
        public bool ignoresVesselThrottle;
        /// <summary>The thrust limiter, 0 to 1.</summary>
        public float limit;
    }

    /// <summary>The part's engines in recorder module order.</summary>
    internal sealed class PartEngineList
    {
        public List<string> engineIds = new List<string>();
        public List<EngineThrottleSetup> throttles = new List<EngineThrottleSetup>();
        public bool hasMultiMode;
    }

    /// <summary>One FXModuleThrottleEffects and the modules it sets by name.</summary>
    internal sealed class ThrottleGlowDriver
    {
        public string[] moduleIds;
        public int[] moduleIndices;
        public float responseSpeed;
        public int ordinal;
        public float powerScale = 1f;
        public bool vesselThrottleUnrecorded;
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
            string raw = FindModuleNode(snapshotPartNode, moduleName, ordinal)?.GetValue(key);
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

        /// <summary>Pure: the part node's <paramref name="ordinal"/>-th MODULE of that name, or null.</summary>
        private static ConfigNode FindModuleNode(ConfigNode partNode, string moduleName, int ordinal)
        {
            ConfigNode[] modules = partNode?.GetNodes("MODULE");
            int seen = 0;
            for (int i = 0; modules != null && i < modules.Length; i++)
                if (modules[i]?.GetValue("name") == moduleName && seen++ == ordinal)
                    return modules[i];
            return null;
        }

        /// <summary>Pure: whether stock's FXModuleAnimateThrottle / FXModuleThrottleEffects reads the
        /// vessel's main throttle (decompiled FixedUpdate), gated on the engine running or not.</summary>
        internal static bool GlowFollowsVesselThrottle(
            bool dependOnEngineState, bool dependOnOutput, bool dependOnThrottle)
            => !dependOnEngineState || (!dependOnOutput && !dependOnThrottle);

        /// <summary>Pure: an engine's throttle setup (decompiled <c>ModuleEngines.UpdateThrottle</c>)
        /// from its saved module node; absent, stock's defaults.</summary>
        internal static EngineThrottleSetup ReadEngineThrottleSetup(ConfigNode engineModuleNode, bool throttleLocked)
        {
            float percent = ParseFloatOr(engineModuleNode?.GetValue("thrustPercentage"), 100f);
            return new EngineThrottleSetup
            {
                ignoresVesselThrottle = throttleLocked
                    || ParseBoolOr(engineModuleNode?.GetValue("independentThrottle"), false),
                limit = percent >= 0f && percent <= 100f ? percent * 0.01f : 1f,
            };
        }

        /// <summary>
        /// Pure: how a glow reads the vessel's throttle off its engines' recorded power. A glow on
        /// its engine's own throttle takes the power as it is. Otherwise an engine on the main
        /// throttle runs at throttle times limiter, so the power divides by the limiter; and when
        /// every engine ignores the main throttle no recorded power tells it
        /// (<see cref="NozzleGlowInfo.vesselThrottle"/> does).
        /// </summary>
        internal static void ResolveVesselThrottleSource(
            bool followsVesselThrottle, int[] engineIndices, IList<EngineThrottleSetup> engines,
            out float powerScale, out bool unrecorded)
        {
            powerScale = 1f;
            unrecorded = false;
            if (!followsVesselThrottle || engineIndices.Length == 0) return;
            float limit = 0f;
            unrecorded = true;
            for (int i = 0; i < engineIndices.Length; i++)
            {
                EngineThrottleSetup engine = engines[engineIndices[i]];
                if (engine.ignoresVesselThrottle) continue;
                unrecorded = false;
                if (engine.limit > limit) limit = engine.limit;
            }
            if (limit > 0f) powerScale = 1f / limit;
        }

        /// <summary>Pure: the main throttle a vessel snapshot was taken at (stock
        /// <c>FlightCtrlState.Save</c>), clamped; NaN when absent or unreadable.</summary>
        internal static float ReadSnapshotMainThrottle(ConfigNode snapshotNode)
        {
            return float.TryParse(snapshotNode?.GetNode("CTRLSTATE")?.GetValue("mainThrottle"), NumberStyles.Float,
                       CultureInfo.InvariantCulture, out float throttle)
                   && !float.IsNaN(throttle)
                ? (throttle < 0f ? 0f : (throttle > 1f ? 1f : throttle))
                : float.NaN;
        }

        private static PartEngineList CollectPartEngines(Part prefab, ConfigNode partNode)
        {
            var engines = new PartEngineList();
            var ordinals = new Dictionary<string, int>();
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                PartModule module = prefab.Modules[m];
                if (module is MultiModeEngine) engines.hasMultiMode = true;
                if (!(module is ModuleEngines engine)) continue;
                ordinals.TryGetValue(module.moduleName, out int ordinal);
                ordinals[module.moduleName] = ordinal + 1;
                engines.engineIds.Add(engine.engineID);
                engines.throttles.Add(ReadEngineThrottleSetup(
                    FindModuleNode(partNode, module.moduleName, ordinal), engine.throttleLocked));
            }
            return engines;
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
            bool hasGlowModule = false;
            ModuleRCS rcs = null;
            int rcsIndex = -1, rcsSeen = 0;
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                PartModule module = prefab.Modules[m];
                if (module is ModuleRCS r)
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

            PartEngineList engines = CollectPartEngines(prefab, partNode);
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
                    plan.moduleIndices = ResolveNozzleGlowEngineIndices(
                        throttle.dependOnEngineState, throttle.preferMultiMode, throttle.engineName,
                        throttle.engineIndex, engines.engineIds, engines.hasMultiMode);
                    ResolveVesselThrottleSource(
                        GlowFollowsVesselThrottle(throttle.dependOnEngineState, throttle.dependOnOutput,
                            throttle.dependOnThrottle),
                        plan.moduleIndices, engines.throttles,
                        out plan.powerScale, out plan.vesselThrottleUnrecorded);
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
        private static List<ThrottleGlowDriver> CollectThrottleGlowDrivers(Part prefab, ConfigNode partNode)
        {
            bool any = false;
            for (int m = 0; m < prefab.Modules.Count && !any; m++)
                any = prefab.Modules[m] is FXModuleThrottleEffects;
            if (!any) return null;
            PartEngineList engines = CollectPartEngines(prefab, partNode);
            if (engines.engineIds.Count == 0) return null;

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
                        effects.engineIndex, engines.engineIds, engines.hasMultiMode),
                    responseSpeed = effects.responseSpeed,
                    ordinal = ordinal++,
                };
                if (driver.moduleIds.Length == 0 || driver.moduleIndices.Length == 0) continue;
                ResolveVesselThrottleSource(
                    GlowFollowsVesselThrottle(effects.dependOnEngineState, effects.dependOnOutput,
                        effects.dependOnThrottle),
                    driver.moduleIndices, engines.throttles,
                    out driver.powerScale, out driver.vesselThrottleUnrecorded);
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
                powerScale = driver.powerScale,
                vesselThrottleUnrecorded = driver.vesselThrottleUnrecorded,
                samples = curve.samples,
                setterOrder = ThrottleGlowSetterOrder(driver, curve.moduleId),
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

        private const int ThrottleGlowSetterNamesMax = 1000;

        /// <summary>Pure: where the driver's setter call for a module stands among the part's:
        /// FXModuleThrottleEffects in module order, each through its names in order.</summary>
        internal static int ThrottleGlowSetterOrder(ThrottleGlowDriver driver, string moduleId)
            => driver.ordinal * ThrottleGlowSetterNamesMax + System.Array.IndexOf(driver.moduleIds, moduleId);

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
                NozzleGlowInfo glow = BuildThrottleGlow(drivers, partNode, info.throttleGlow, persistentId);
                if (glow == null) continue;
                // Writers without materials of their own: the event walk moves their level, the
                // glow on top reads it. In the list before it, so they have moved when it does.
                var under = new List<NozzleGlowInfo>();
                for (int u = 0; info.throttleGlowUnder != null && u < info.throttleGlowUnder.Count; u++)
                {
                    NozzleGlowInfo writer = BuildThrottleGlow(drivers, partNode, info.throttleGlowUnder[u], persistentId);
                    if (writer == null) continue;
                    under.Add(writer);
                    AddNozzleGlow(ref heatInfo, writer, persistentId);
                }
                if (under.Count > 0) glow.under = under.ToArray();
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

        private static NozzleGlowInfo BuildThrottleGlow(
            List<ThrottleGlowDriver> drivers, ConfigNode partNode, ThrottleGlowCurve curve, uint persistentId)
        {
            ThrottleGlowDriver driver = FindThrottleGlowDriver(drivers, curve.moduleId);
            if (driver == null) return null;
            return NewThrottleGlow(persistentId, driver, curve,
                ReadPersistedGlowLevel(partNode, nameof(FXModuleThrottleEffects), driver.ordinal, "state", false),
                ColorChangerWritesOnlyWhileRamping());
        }

        /// <summary>ReStock's PartModule that shows an RCS nozzle's force as a colour.</summary>
        internal const string ReStockRcsGlowModule = "ModuleRestockRCSGlow";

        /// <summary>
        /// Pure: the glow of a one-nozzle ModuleRestockRCSGlow (decompiled Restock.dll: every physics
        /// step the curves at <c>thrustForces[i] / thrusterPower</c>, which is the recorded power), and
        /// the colour property it writes.
        /// </summary>
        internal static NozzleGlowInfo NewReStockRcsGlow(
            uint persistentId, int rcsIndex, ConfigNode moduleConfig, out string property)
        {
            property = moduleConfig?.GetValue("shaderColorParameter");
            if (string.IsNullOrEmpty(property)) property = "_EmissiveColor";
            return new NozzleGlowInfo
            {
                partPersistentId = persistentId,
                moduleName = ReStockRcsGlowModule,
                animationName = property,
                isRcs = true,
                isColorChanger = true,
                samples = moduleConfig != null ? SampleColorChangerCurves(moduleConfig) : null,
                moduleKeys = new[] { FlightRecorder.EncodeEngineKey(persistentId, rcsIndex) },
                modulePowers = new float[1],
                approachRate = float.PositiveInfinity,
            };
        }

        /// <summary>
        /// The glow of a ModuleRestockRCSGlow: stock's side writes the first MeshRenderer under each
        /// thruster transform of the part's first ModuleRCSFX. One nozzle only, as every RCS glow
        /// (<see cref="RcsGlowPowerIsPerNozzle"/>).
        /// </summary>
        private static void BuildReStockRcsGlow(
            Part prefab, Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap, ref HeatGhostInfo heatInfo,
            uint persistentId, string partName)
        {
            ModuleRCSFX rcs = null;
            int rcsIndex = -1, rcsSeen = 0;
            bool hasGlow = false;
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                PartModule module = prefab.Modules[m];
                if (module.moduleName == ReStockRcsGlowModule) hasGlow = true;
                if (!(module is ModuleRCS)) continue;
                if (rcs == null && module is ModuleRCSFX fx) { rcs = fx; rcsIndex = rcsSeen; }
                rcsSeen++;
            }
            if (!hasGlow || rcs == null) return;

            Transform[] thrusters = prefab.FindModelTransforms(rcs.thrusterTransformName);
            string outcome = "per-nozzle power not recorded";
            if (RcsGlowPowerIsPerNozzle(thrusters.Length))
            {
                NozzleGlowInfo glow = NewReStockRcsGlow(persistentId, rcsIndex,
                    FindModuleNode(prefab.partInfo?.partConfig, ReStockRcsGlowModule, 0), out string property);
                MeshRenderer[] meshes = thrusters[0].GetComponentsInChildren<MeshRenderer>(true);
                Renderer ghostRenderer = meshes.Length > 0 && glow.samples != null
                    ? ResolveGhostRenderer(meshes[0], modelRoot, ghostModelNode, cloneMap)
                    : null;
                Material[] slots = ghostRenderer != null ? ghostRenderer.sharedMaterials : null;
                if (slots == null || slots.Length == 0 || slots[0] == null || !slots[0].HasProperty(property))
                {
                    outcome = "no mesh with '" + property + "' on the ghost";
                }
                else
                {
                    slots[0] = new Material(slots[0]);
                    ghostRenderer.sharedMaterials = slots;
                    glow.colors.Add(new NozzleGlowColor
                    {
                        material = slots[0],
                        property = property,
                        samples = glow.samples,
                    });
                    AddNozzleGlow(ref heatInfo, glow, persistentId);
                    outcome = "bound";
                }
            }
            ParsekLog.VerboseRateLimited("GhostVisual", "restock-rcs-glow-" + partName,
                $"  Nozzle glow '{partName}' pid={persistentId}: {ReStockRcsGlowModule} {outcome} " +
                $"(rcsIndex={rcsIndex} thrusters={thrusters.Length})", 60.0);
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
            BuildReStockRcsGlow(prefab, modelRoot, ghostModelNode, cloneMap, ref heatInfo, persistentId, partName);
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
                    vesselThrottleUnrecorded = plan.vesselThrottleUnrecorded,
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
                if (g.vesselThrottleUnrecorded) sb.Append(" vesselThrottle=snapshot");
                if (g.under != null) sb.Append(" writersUnder=").Append(g.under.Length.ToString(CultureInfo.InvariantCulture));
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
        /// A glow whose vessel throttle no recorded power tells takes the snapshot's.
        /// </summary>
        internal static void StampPersistedGlowLevels(HeatGhostInfo heatInfo, ConfigNode snapshotNode, bool trusted)
        {
            if (heatInfo?.nozzleGlows == null) return;
            double ut = trusted ? ReadSnapshotUT(snapshotNode) : double.NaN;
            float throttle = trusted ? ReadSnapshotMainThrottle(snapshotNode) : float.NaN;
            for (int i = 0; i < heatInfo.nozzleGlows.Count; i++)
            {
                heatInfo.nozzleGlows[i].initialUT = ut;
                if (heatInfo.nozzleGlows[i].vesselThrottleUnrecorded)
                    heatInfo.nozzleGlows[i].vesselThrottle = throttle;
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
