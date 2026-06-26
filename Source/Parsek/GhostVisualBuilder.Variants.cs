using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostVisualBuilder
    {
        internal static bool TryFindSelectedVariantNode(
            Part prefab, ConfigNode partNode,
            out ConfigNode selectedVariantNode, out string selectedVariantName)
        {
            return TryFindSelectedVariantNode(prefab, partNode, out selectedVariantNode, out selectedVariantName, out _);
        }

        internal static bool TryFindSelectedVariantNode(
            Part prefab, ConfigNode partNode,
            out ConfigNode selectedVariantNode, out string selectedVariantName,
            out string resolutionPath)
        {
            selectedVariantNode = null;
            selectedVariantName = null;
            resolutionPath = null;

            if (prefab == null || prefab.partInfo == null || prefab.partInfo.partConfig == null)
                return false;

            ConfigNode variantModuleConfig = FindModuleNode(prefab.partInfo.partConfig, "ModulePartVariants");
            if (variantModuleConfig == null)
                return false;

            string requestedVariantName = null;
            bool variantFromSnapshot = false;

            // Prefer explicit variant saved on the part snapshot when present.
            requestedVariantName = ResolveVariantNameFromSnapshot(partNode);
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

            return MatchVariantNode(variantModuleConfig, requestedVariantName, variantFromSnapshot,
                out selectedVariantNode, out selectedVariantName, out resolutionPath);
        }

        /// <summary>
        /// Pure matching logic: given the variant MODULE config and a requested variant name,
        /// find the matching VARIANT node. Extracted for unit testability.
        /// </summary>
        internal static bool MatchVariantNode(
            ConfigNode variantModuleConfig,
            string requestedVariantName, bool variantFromSnapshot,
            out ConfigNode selectedVariantNode, out string selectedVariantName,
            out string resolutionPath)
        {
            selectedVariantNode = null;
            selectedVariantName = null;
            resolutionPath = null;

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

            resolutionPath = "first-fallback";

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

            // If the snapshot explicitly named a variant that doesn't match any VARIANT
            // node, this is the implicit base variant (e.g., KSP stores "Basic" as the
            // display name for the MODULE-level default appearance). The prefab already
            // has the correct base textures/geometry — return false so callers skip
            // variant rule application rather than falling through to variantNodes[0].
            if (selectedVariantNode == null && variantFromSnapshot)
            {
                selectedVariantName = requestedVariantName;
                resolutionPath = "base-implicit";
                return false;
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

        /// <summary>
        /// Resolves the selected variant name from a snapshot PART ConfigNode.
        /// KSP stores variant info in two places:
        ///   1. Inside the MODULE node as "selectedVariant" or "currentVariant" (rare)
        ///   2. At the PART level as "moduleVariantName" (common — where KSP actually
        ///      persists the selected variant in .craft / .sfs files)
        /// Returns null if no variant name is found.
        /// </summary>
        internal static string ResolveVariantNameFromSnapshot(ConfigNode partNode)
        {
            if (partNode == null)
                return null;

            ConfigNode snapshotVariantModule = FindModuleNode(partNode, "ModulePartVariants");
            string name = FirstNonEmptyConfigValue(
                snapshotVariantModule,
                "currentVariant",
                "selectedVariant",
                "variantName",
                "moduleVariantName");

            // Also check PART-level moduleVariantName — KSP writes the selected variant
            // here rather than inside the ModulePartVariants MODULE node.
            if (string.IsNullOrEmpty(name))
            {
                name = FirstNonEmptyConfigValue(
                    partNode,
                    "moduleVariantName");
            }

            return name;
        }

        private static bool TryGetSelectedVariantGameObjectStates(
            Part prefab,
            ConfigNode partNode,
            out string selectedVariantName,
            out Dictionary<string, bool> gameObjectStates)
        {
            return TryGetSelectedVariantGameObjectStates(prefab, partNode,
                out selectedVariantName, out gameObjectStates, out _);
        }

        private static bool TryGetSelectedVariantGameObjectStates(
            Part prefab,
            ConfigNode partNode,
            out string selectedVariantName,
            out Dictionary<string, bool> gameObjectStates,
            out string variantResolutionPath)
        {
            selectedVariantName = null;
            gameObjectStates = null;
            variantResolutionPath = null;

            if (!TryFindSelectedVariantNode(prefab, partNode,
                out ConfigNode selectedVariantNode, out selectedVariantName, out variantResolutionPath))
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

        /// <summary>
        /// Checks whether a transform is enabled by the selected variant's GAMEOBJECTS rules.
        /// Walks up from the transform to modelRoot, checking each ancestor against the rules.
        /// Returns true if no rule matches (default include). Used by mesh renderer filtering
        /// and FX transform filtering (#242c).
        /// </summary>
        internal static bool IsRendererEnabledByVariantRule(
            Transform targetTransform,
            Transform modelRoot,
            Dictionary<string, bool> gameObjectStates,
            out string matchedRuleName,
            out bool matchedRuleEnabled)
        {
            matchedRuleName = null;
            matchedRuleEnabled = true;

            if (targetTransform == null || gameObjectStates == null || gameObjectStates.Count == 0)
                return true;

            // Build ancestor name list from transform up to modelRoot
            var ancestorNames = new List<string>();
            Transform current = targetTransform;
            while (current != null)
            {
                ancestorNames.Add(current.name);
                if (current == modelRoot)
                    break;
                current = current.parent;
            }

            return IsAncestorChainEnabledByVariantRule(
                ancestorNames, gameObjectStates,
                out matchedRuleName, out matchedRuleEnabled);
        }

        /// <summary>
        /// Pure matching logic: given a list of ancestor names (bottom-up, from transform
        /// to modelRoot) and GAMEOBJECTS rules, returns whether the transform is enabled.
        /// First matching ancestor wins. Returns true if no rule matches (default include).
        /// Extracted for unit testability without Unity (#242c).
        /// </summary>
        internal static bool IsAncestorChainEnabledByVariantRule(
            List<string> ancestorNamesBottomUp,
            Dictionary<string, bool> gameObjectStates,
            out string matchedRuleName,
            out bool matchedRuleEnabled)
        {
            matchedRuleName = null;
            matchedRuleEnabled = true;

            if (ancestorNamesBottomUp == null || ancestorNamesBottomUp.Count == 0 ||
                gameObjectStates == null || gameObjectStates.Count == 0)
                return true;

            for (int i = 0; i < ancestorNamesBottomUp.Count; i++)
            {
                string name = ancestorNamesBottomUp[i];

                if (gameObjectStates.TryGetValue(name, out bool enabled))
                {
                    matchedRuleName = name;
                    matchedRuleEnabled = enabled;
                    return enabled;
                }

                // Multi-MODEL parts: KSP names model roots with the full GameDatabase path
                // plus "(Clone)" suffix (e.g. "SquadExpansion/.../Shroud3x0(Clone)").
                // Variant GAMEOBJECTS rules use short names (e.g. "Shroud3x0").
                // Try matching the last path segment after stripping "(Clone)".
                string shortName = ExtractShortTransformName(name);
                if (shortName != null && gameObjectStates.TryGetValue(shortName, out bool shortEnabled))
                {
                    matchedRuleName = shortName;
                    matchedRuleEnabled = shortEnabled;
                    return shortEnabled;
                }
            }

            return true;
        }
    }
}
