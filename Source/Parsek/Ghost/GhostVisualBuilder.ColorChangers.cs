using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// What a ModuleColorChanger does on the live part, as far as the ghost reproduces it.
    /// </summary>
    internal enum ColorChangerPattern
    {
        /// <summary>Stock never writes it (no shader property, or useRate off and no ablator).</summary>
        None,
        /// <summary>An in-flight toggle on _EmissiveColor (a pod's cabin lights).</summary>
        CabinLight,
        /// <summary>A ModuleAblator's char module (charModuleName == moduleID), any property.</summary>
        AblationChar,
        /// <summary>Any other useRate module: Update writes the curves at its rest state.</summary>
        RestState,
        /// <summary>An FXModuleThrottleEffects names it (ReStock's nozzle glow): its colour follows
        /// the engine (<c>BuildNozzleGlows</c>), starting from its rest state.</summary>
        ThrottleGlow,
    }

    /// <summary>The parsed renderer filter of one ModuleColorChanger (stock OnLoad + Start).</summary>
    internal sealed class ColorChangerRendererFilter
    {
        public List<string> includedRenderers = new List<string>();
        public List<string> excludedRenderers = new List<string>();
        public bool useMaterialsList;
        public string[] materialsNames = new string[0];
    }

    internal static partial class GhostVisualBuilder
    {
        private sealed class ColorChangerWritePlan
        {
            public int declarationIndex;
            public ColorChangerPattern pattern;
            public string shaderProperty;
            public Color offColor;
            public Color onColor;
            public ColorChangerRendererFilter filter;
            public ThrottleGlowCurve throttleGlow;
            public List<ColorChangerMaterialState> materials = new List<ColorChangerMaterialState>();
        }

        /// <summary>
        /// Builds one ColorChangerGhostInfo per ModuleColorChanger stock writes, and writes
        /// each one's rest colour at build time. Stock KSP 1.12.5 (decompiled
        /// ModuleColorChanger / ModuleAblator):
        /// <list type="bullet">
        /// <item><description>A useRate module (the default) writes the curves at
        /// <c>currentRateState</c> from its Update every frame; Start sets that to
        /// <c>animState ? 1 : 0</c> and FixedUpdate ramps it toward animState, so the rest
        /// colour is the curves at that end. ReStock engines replace FXModuleAnimateThrottle with
        /// two such modules (heatColor, throttleColor) at black / clear at rest, which an
        /// FXModuleThrottleEffects drives by name (<paramref name="throttleDrivenIds"/>).</description></item>
        /// <item><description>A ModuleAblator whose <c>charModuleName</c> names this module
        /// writes the char scalar every Update, whatever the property and after the cabin
        /// light module (the KV pods' window shows the char colour, not the light's).</description></item>
        /// <item><description>Every module writes the PART's shared <c>part.mpb</c> to its own
        /// renderers (<c>includedRenderer</c> / <c>excludedRenderer</c>, <c>useMaterialsList</c>);
        /// <see cref="ResolveColorChangerRendererSources"/> computes what each renderer ends up
        /// showing.</description></item>
        /// </list>
        /// <paramref name="prefabPartConfig"/> supplies the config-only fields a persisted
        /// snapshot node cannot carry - see <see cref="ResolveColorChangerConfigNodes"/>.
        /// </summary>
        internal static List<ColorChangerGhostInfo> BuildColorChangerInfos(
            ConfigNode partNode, ConfigNode prefabPartConfig, Transform ghostModelNode,
            uint persistentId, string partName, ICollection<string> throttleDrivenIds = null)
        {
            if (partNode == null || ghostModelNode == null)
                return null;

            List<ConfigNode> colorChangerNodes =
                ResolveColorChangerConfigNodes(partNode, prefabPartConfig);
            if (colorChangerNodes.Count == 0)
                return null;
            List<ConfigNode> snapshotNodes = CollectColorChangerModuleNodes(partNode);

            var plans = new List<ColorChangerWritePlan>();
            int skippedNoShaderProperty = 0;
            int skippedNeverWritten = 0;
            // Stock's setter drives the first module of the name (PartModuleList.GetScalarModule).
            HashSet<string> driven = throttleDrivenIds != null ? new HashSet<string>(throttleDrivenIds) : null;
            for (int m = 0; m < colorChangerNodes.Count; m++)
            {
                ConfigNode node = colorChangerNodes[m];
                string shaderProperty = node.GetValue("shaderProperty");
                if (string.IsNullOrEmpty(shaderProperty))
                {
                    skippedNoShaderProperty++;
                    continue;
                }

                bool toggleInFlight = ParseBoolOr(node.GetValue("toggleInFlight"), false);
                bool useRate = ParseBoolOr(node.GetValue("useRate"), true);
                bool ablatorDriven = TryResolveAblationCharScalars(
                    partNode, prefabPartConfig, node.GetValue("moduleID"),
                    out float currentChar, out float charredChar);
                string moduleId = ColorChangerModuleId(node);
                bool throttleDriven = driven != null && driven.Remove(moduleId);
                ColorChangerPattern pattern = ClassifyColorChangerPattern(
                    toggleInFlight, shaderProperty, useRate, ablatorDriven, throttleDriven);
                if (pattern == ColorChangerPattern.None)
                {
                    skippedNeverWritten++;
                    continue;
                }

                bool animState = ResolveColorChangerAnimState(
                    m < snapshotNodes.Count ? snapshotNodes[m] : null, node);
                ResolveColorChangerScalars(pattern, animState, currentChar, charredChar,
                    out float offScalar, out float onScalar);
                plans.Add(new ColorChangerWritePlan
                {
                    declarationIndex = m,
                    pattern = pattern,
                    shaderProperty = shaderProperty,
                    offColor = EvaluateColorChangerColor(node, offScalar),
                    onColor = EvaluateColorChangerColor(node, onScalar),
                    filter = ParseColorChangerRendererFilter(node),
                    throttleGlow = pattern == ColorChangerPattern.ThrottleGlow
                        ? new ThrottleGlowCurve
                        {
                            moduleId = moduleId,
                            animRate = ParseFloatOr(node.GetValue("animRate"), 1f),
                            useRate = useRate,
                            animState = animState,
                            samples = SampleColorChangerCurves(node),
                        }
                        : null,
                });
            }

            List<ColorChangerGhostInfo> results = null;
            int renderersWritten = 0;
            int renderersFiltered = 0;
            if (plans.Count > 0)
            {
                var patterns = new List<ColorChangerPattern>(plans.Count);
                for (int i = 0; i < plans.Count; i++) patterns.Add(plans[i].pattern);
                int[] frameOrder = ResolveColorChangerFrameOrder(patterns);

                var renderers = new List<Renderer>();
                Renderer[] all = ghostModelNode.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < all.Length; r++)
                {
                    if (all[r] == null) continue;
                    if (all[r] is ParticleSystemRenderer || all[r] is TrailRenderer) continue;
                    renderers.Add(all[r]);
                }

                var writerProperties = new List<string>(plans.Count);
                var writerKeeps = new List<bool[]>(plans.Count);
                for (int w = 0; w < frameOrder.Length; w++)
                {
                    ColorChangerWritePlan plan = plans[frameOrder[w]];
                    writerProperties.Add(plan.shaderProperty);
                    var keeps = new bool[renderers.Count];
                    for (int r = 0; r < renderers.Count; r++)
                        keeps[r] = ColorChangerKeepsRenderer(
                            plan.filter, renderers[r].name, SharedMaterialNames(renderers[r]));
                    writerKeeps.Add(keeps);
                }

                List<Dictionary<string, int>> sources = ResolveColorChangerRendererSources(
                    writerProperties, writerKeeps, renderers.Count);

                // Clones made here; a second property on the same slot reuses its clone so
                // every plan holds the material the renderer actually draws.
                var owned = new HashSet<Material>();
                for (int r = 0; r < renderers.Count; r++)
                {
                    Dictionary<string, int> rendererSources = sources[r];
                    if (rendererSources.Count == 0)
                    {
                        renderersFiltered++;
                        continue;
                    }

                    Material[] current = renderers[r].sharedMaterials;
                    if (current == null || current.Length == 0) continue;
                    var slots = (Material[])current.Clone();
                    bool clonedAny = false;
                    bool wroteAny = false;
                    for (int s = 0; s < slots.Length; s++)
                    {
                        if (slots[s] == null) continue;
                        foreach (KeyValuePair<string, int> source in rendererSources)
                        {
                            if (!slots[s].HasProperty(source.Key)) continue;
                            if (!owned.Contains(slots[s]))
                            {
                                slots[s] = new Material(slots[s]);
                                owned.Add(slots[s]);
                                clonedAny = true;
                            }
                            ColorChangerWritePlan plan = plans[frameOrder[source.Value]];
                            plan.materials.Add(new ColorChangerMaterialState
                            {
                                material = slots[s],
                                offColor = plan.offColor,
                                onColor = plan.onColor,
                            });
                            slots[s].SetColor(source.Key, plan.offColor);
                            wroteAny = true;
                        }
                    }
                    if (clonedAny)
                        renderers[r].materials = slots;
                    if (wroteAny)
                        renderersWritten++;
                }

                for (int i = 0; i < plans.Count; i++)
                {
                    ColorChangerWritePlan plan = plans[i];
                    if (plan.materials.Count == 0) continue;
                    if (results == null)
                        results = new List<ColorChangerGhostInfo>();
                    results.Add(new ColorChangerGhostInfo
                    {
                        partPersistentId = persistentId,
                        shaderProperty = plan.shaderProperty,
                        isCabinLight = plan.pattern == ColorChangerPattern.CabinLight,
                        isAblationChar = plan.pattern == ColorChangerPattern.AblationChar,
                        materials = plan.materials,
                        throttleGlow = plan.throttleGlow,
                    });
                }
            }

            int cabin = 0, charCount = 0, rest = 0, throttle = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].pattern == ColorChangerPattern.CabinLight) cabin++;
                else if (plans[i].pattern == ColorChangerPattern.AblationChar) charCount++;
                else if (plans[i].pattern == ColorChangerPattern.ThrottleGlow) throttle++;
                else rest++;
            }

            // Batch counting convention: one summary per part, never one line per module.
            ParsekLog.VerboseRateLimited("GhostVisual", $"colorchanger-{partName}",
                $"ColorChanger resolve '{partName}' pid={persistentId}: " +
                $"modules={colorChangerNodes.Count} built={(results != null ? results.Count : 0)} " +
                $"cabinLight={cabin} ablationChar={charCount} restState={rest} throttleGlow={throttle} " +
                $"noShaderProperty={skippedNoShaderProperty} neverWritten={skippedNeverWritten} " +
                $"renderersWritten={renderersWritten} renderersFiltered={renderersFiltered}", 60.0);

            return results;
        }

        private static List<string> SharedMaterialNames(Renderer renderer)
        {
            var names = new List<string>();
            Material[] materials = renderer.sharedMaterials;
            if (materials == null) return names;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null)
                    names.Add(materials[i].name);
            }
            return names;
        }

        /// <summary>
        /// Which stock writer a ModuleColorChanger has. An ablator-driven module is the char
        /// whatever its property (the KV pods drive _EmissiveColor); an in-flight
        /// _EmissiveColor toggle is a cabin light; one an FXModuleThrottleEffects names is a nozzle
        /// glow, useRate or not (the setter writes it either way); any other module with useRate on (the stock
        /// default) is written by its own Update at its rest state; useRate off with no ablator
        /// is never written by stock, so the ghost leaves the asset's material alone.
        /// </summary>
        internal static ColorChangerPattern ClassifyColorChangerPattern(
            bool toggleInFlight, string shaderProperty, bool useRate, bool ablatorDriven,
            bool throttleDriven = false)
        {
            if (string.IsNullOrEmpty(shaderProperty)) return ColorChangerPattern.None;
            if (ablatorDriven) return ColorChangerPattern.AblationChar;
            if (toggleInFlight && shaderProperty == "_EmissiveColor") return ColorChangerPattern.CabinLight;
            if (throttleDriven) return ColorChangerPattern.ThrottleGlow;
            if (useRate) return ColorChangerPattern.RestState;
            return ColorChangerPattern.None;
        }

        /// <summary>The module's <c>moduleID</c>, or stock's field default.</summary>
        internal static string ColorChangerModuleId(ConfigNode moduleNode)
        {
            string id = moduleNode?.GetValue("moduleID");
            return string.IsNullOrEmpty(id) ? "colorChanger" : id;
        }

        internal static Color[] SampleColorChangerCurves(ConfigNode moduleNode)
        {
            var samples = new Color[NozzleGlowSampleCount];
            for (int n = 0; n < samples.Length; n++)
                samples[n] = EvaluateColorChangerColor(moduleNode, n / (float)(samples.Length - 1));
            return samples;
        }

        /// <summary>
        /// The persisted <c>animState</c> (a persistent KSPField, so the snapshot carries it),
        /// else the config's, else stock's default false.
        /// </summary>
        internal static bool ResolveColorChangerAnimState(ConfigNode snapshotModule, ConfigNode configModule)
        {
            string raw = snapshotModule?.GetValue("animState");
            if (string.IsNullOrEmpty(raw))
                raw = configModule?.GetValue("animState");
            return ParseBoolOr(raw, false);
        }

        /// <summary>
        /// The curve times of the ghost's off and on colours. Cabin light: 0 / 1 (the snapshot
        /// baseline then applies the recorded power). Ablation char: the snapshot's current
        /// char / fully charred. Rest state: both at stock's rest end, animState ? 1 : 0.
        /// </summary>
        internal static void ResolveColorChangerScalars(ColorChangerPattern pattern, bool animState,
            float currentChar, float charredChar, out float offScalar, out float onScalar)
        {
            switch (pattern)
            {
                case ColorChangerPattern.CabinLight:
                    offScalar = 0f;
                    onScalar = 1f;
                    return;
                case ColorChangerPattern.AblationChar:
                    offScalar = currentChar;
                    onScalar = charredChar;
                    return;
                default:
                    offScalar = animState ? 1f : 0f;
                    onScalar = offScalar;
                    return;
            }
        }

        /// <summary>
        /// Stock <c>SetState(float)</c>: the curves at t, or with <c>alphaOnly</c> the fixed
        /// <c>redColor</c> / <c>greenColor</c> / <c>blueColor</c> (default 0) and the alpha curve.
        /// </summary>
        internal static Color EvaluateColorChangerColor(ConfigNode moduleNode, float t)
        {
            Color color = EvaluateColorCurves(moduleNode, t);
            if (ParseBoolOr(moduleNode.GetValue("alphaOnly"), false))
            {
                color.r = ParseFloatOr(moduleNode.GetValue("redColor"), 0f);
                color.g = ParseFloatOr(moduleNode.GetValue("greenColor"), 0f);
                color.b = ParseFloatOr(moduleNode.GetValue("blueColor"), 0f);
            }
            return color;
        }

        /// <summary>
        /// Stock <c>OnLoad</c>: every <c>includedRenderer</c> / <c>excludedRenderer</c> value
        /// (singular keys; the plural spelling some configs use is not read), and when both are
        /// present the included list is cleared. <c>materialsNames</c> is split on ',' without
        /// trimming, as stock does.
        /// </summary>
        internal static ColorChangerRendererFilter ParseColorChangerRendererFilter(ConfigNode moduleNode)
        {
            var filter = new ColorChangerRendererFilter();
            if (moduleNode == null) return filter;
            string[] included = moduleNode.GetValues("includedRenderer");
            if (included != null) filter.includedRenderers.AddRange(included);
            string[] excluded = moduleNode.GetValues("excludedRenderer");
            if (excluded != null) filter.excludedRenderers.AddRange(excluded);
            if (filter.excludedRenderers.Count > 0 && filter.includedRenderers.Count > 0)
                filter.includedRenderers.Clear();
            filter.useMaterialsList = ParseBoolOr(moduleNode.GetValue("useMaterialsList"), false);
            string materialsNames = moduleNode.GetValue("materialsNames") ?? "";
            filter.materialsNames = materialsNames.Split(',');
            return filter;
        }

        /// <summary>
        /// Stock <c>Start</c>: <c>ProcessMaterialsList</c> (when useMaterialsList, keep a renderer
        /// with a material whose name contains any listed name) then <c>EditRenderers</c>
        /// (drop excluded names, then keep only included names; exact renderer names).
        /// </summary>
        internal static bool ColorChangerKeepsRenderer(ColorChangerRendererFilter filter,
            string rendererName, IList<string> materialNames)
        {
            if (filter == null) return true;
            if (filter.useMaterialsList && filter.materialsNames.Length > 0)
            {
                bool matched = false;
                for (int i = 0; materialNames != null && i < materialNames.Count && !matched; i++)
                {
                    string materialName = materialNames[i] ?? "";
                    for (int n = 0; n < filter.materialsNames.Length; n++)
                    {
                        if (materialName.Contains(filter.materialsNames[n]))
                        {
                            matched = true;
                            break;
                        }
                    }
                }
                if (!matched) return false;
            }
            if (filter.excludedRenderers.Count > 0 && filter.excludedRenderers.Contains(rendererName))
                return false;
            if (filter.includedRenderers.Count > 0 && !filter.includedRenderers.Contains(rendererName))
                return false;
            return true;
        }

        /// <summary>
        /// The order the writers run in one frame: every module's own Update in declaration
        /// order, then the ablator-driven char modules (written from ModuleAblator.Update, which
        /// the KV pod atlas shows lands after the cabin light).
        /// </summary>
        internal static int[] ResolveColorChangerFrameOrder(IList<ColorChangerPattern> patterns)
        {
            var order = new List<int>();
            for (int i = 0; i < patterns.Count; i++)
                if (patterns[i] != ColorChangerPattern.AblationChar) order.Add(i);
            for (int i = 0; i < patterns.Count; i++)
                if (patterns[i] == ColorChangerPattern.AblationChar) order.Add(i);
            return order.ToArray();
        }

        /// <summary>
        /// What each renderer shows in steady state, as property -> index of the writer (in
        /// frame order) whose value it holds. Every writer sets its own property on the part's
        /// ONE shared MaterialPropertyBlock and then applies the WHOLE block to its renderers, so
        /// a renderer takes the block as it stood at the last writer that includes it: that
        /// writer's own property, plus every other property at its latest setter before it in
        /// the frame (or, when none comes before, the last setter of the previous frame). A
        /// renderer no writer includes gets nothing.
        /// </summary>
        internal static List<Dictionary<string, int>> ResolveColorChangerRendererSources(
            IList<string> writerProperties, IList<bool[]> writerKeepsRenderer, int rendererCount)
        {
            int writers = writerProperties.Count;
            var blockAtWriter = new List<Dictionary<string, int>>(writers);
            for (int j = 0; j < writers; j++)
            {
                var block = new Dictionary<string, int>();
                // Previous frame first, then this frame up to and including j.
                for (int i = 0; i < writers; i++)
                    block[writerProperties[i]] = i;
                for (int i = 0; i <= j; i++)
                    block[writerProperties[i]] = i;
                blockAtWriter.Add(block);
            }

            var result = new List<Dictionary<string, int>>(rendererCount);
            for (int r = 0; r < rendererCount; r++)
            {
                int last = -1;
                for (int j = 0; j < writers; j++)
                {
                    bool[] keeps = writerKeepsRenderer[j];
                    if (keeps != null && r < keeps.Length && keeps[r])
                        last = j;
                }
                result.Add(last < 0
                    ? new Dictionary<string, int>()
                    : new Dictionary<string, int>(blockAtWriter[last]));
            }
            return result;
        }
    }
}
