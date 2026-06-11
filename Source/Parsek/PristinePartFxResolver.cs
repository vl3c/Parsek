using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Recovers a part's ORIGINAL (pre-ModuleManager) effects definition from the
    /// pristine .cfg file on disk. ModuleManager patches GameDatabase nodes in memory
    /// only, so the source file still carries the stock EFFECTS node and legacy fx_*
    /// keys that Waterfall config packs delete from <c>partInfo.partConfig</c>.
    /// Used by the ghost FX builders when <see cref="WaterfallCompat"/> opens the gate.
    /// Results are cached per runtime part name (one disk read per part type per session).
    /// </summary>
    internal static class PristinePartFxResolver
    {
        /// <summary>Pristine FX-relevant data extracted from one PART node.</summary>
        internal sealed class PristinePartFxData
        {
            /// <summary>True when the PART node was located in the pristine file.</summary>
            public bool Found;
            public string SourcePath;
            /// <summary>Pristine EFFECTS node; null when the part never had one.</summary>
            public ConfigNode EffectsNode;
            /// <summary>
            /// Per-ordinal effect group names of the pristine ModuleEngines* modules
            /// (ordinal among engine modules, matching the live midx contract).
            /// Empty set = no named effects (FilterEffectGroups then keeps all groups).
            /// </summary>
            public List<HashSet<string>> EngineModuleEffectNames = new List<HashSet<string>>();
            /// <summary>Per-ordinal runningEffectName of the pristine ModuleRCS* modules.</summary>
            public List<string> RcsRunningEffectNames = new List<string>();
            /// <summary>
            /// Pristine legacy top-level fx_* key names that represent running/power
            /// exhaust FX (transients excluded). The key name doubles as the stock FX
            /// prefab name resolved via GhostVisualBuilder.FindFxPrefab.
            /// </summary>
            public List<string> LegacyFxPrefabNames = new List<string>();
        }

        private static readonly Dictionary<string, PristinePartFxData> cacheByPartName =
            new Dictionary<string, PristinePartFxData>(System.StringComparer.Ordinal);

        /// <summary>Test seam: maps a config file path to a parsed root node.</summary>
        internal static System.Func<string, ConfigNode> LoadFileRootOverrideForTesting;

        private static readonly string[] EngineEffectNameKeys =
            { "runningEffectName", "powerEffectName", "spoolEffectName", "directThrottleEffectName" };

        /// <summary>
        /// Returns the pristine FX data for a part, loading and parsing its source cfg
        /// on first request. Never returns null; check <c>Found</c>.
        /// </summary>
        internal static PristinePartFxData GetForPart(string runtimePartName, string configFileFullName)
        {
            if (string.IsNullOrEmpty(runtimePartName))
            {
                ParsekLog.Verbose("WaterfallCompat",
                    "GetForPart skipped: empty runtime part name (no pristine lookup possible)");
                return new PristinePartFxData { SourcePath = configFileFullName };
            }

            if (cacheByPartName.TryGetValue(runtimePartName, out PristinePartFxData cached))
                return cached;

            PristinePartFxData data;
            ConfigNode fileRoot = LoadFileRoot(runtimePartName, configFileFullName);
            if (fileRoot == null)
            {
                data = new PristinePartFxData { SourcePath = configFileFullName };
            }
            else
            {
                TryExtract(fileRoot, runtimePartName, configFileFullName, out data);
            }

            cacheByPartName[runtimePartName] = data;
            return data;
        }

        private static ConfigNode LoadFileRoot(string runtimePartName, string configFileFullName)
        {
            if (string.IsNullOrEmpty(configFileFullName))
            {
                ParsekLog.Warn("WaterfallCompat",
                    $"pristine cfg path unavailable for part '{runtimePartName}' (MM-generated part?); no fallback source");
                return null;
            }

            try
            {
                if (LoadFileRootOverrideForTesting != null)
                    return LoadFileRootOverrideForTesting(configFileFullName);

                if (!System.IO.File.Exists(configFileFullName))
                {
                    ParsekLog.Warn("WaterfallCompat",
                        $"pristine cfg file missing for part '{runtimePartName}': '{configFileFullName}'");
                    return null;
                }

                ConfigNode root = ConfigNode.Load(configFileFullName);
                if (root == null)
                {
                    ParsekLog.Warn("WaterfallCompat",
                        $"pristine cfg failed to parse for part '{runtimePartName}': '{configFileFullName}'");
                }
                return root;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("WaterfallCompat",
                    $"pristine cfg load threw for part '{runtimePartName}' ('{configFileFullName}'): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pure extraction: locates the PART node matching the runtime part name
        /// (KSP converts underscores to dots at compile time, so disk names are matched
        /// via diskName.Replace('_','.')) and pulls the FX-relevant pieces.
        /// </summary>
        internal static bool TryExtract(
            ConfigNode fileRoot, string runtimePartName, string sourcePath, out PristinePartFxData data)
        {
            data = new PristinePartFxData { SourcePath = sourcePath };
            if (fileRoot == null || string.IsNullOrEmpty(runtimePartName))
                return false;

            ConfigNode[] partNodes = fileRoot.GetNodes("PART");
            ConfigNode match = null;
            for (int i = 0; i < partNodes.Length; i++)
            {
                string diskName = partNodes[i].GetValue("name");
                if (string.IsNullOrEmpty(diskName))
                    continue;
                if (string.Equals(diskName.Replace('_', '.'), runtimePartName, System.StringComparison.Ordinal))
                {
                    match = partNodes[i];
                    break;
                }
            }

            if (match == null)
            {
                ParsekLog.Verbose("WaterfallCompat",
                    $"pristine PART node not found for '{runtimePartName}' in '{sourcePath}' " +
                    $"(partNodesScanned={partNodes.Length})");
                return false;
            }

            data.Found = true;
            data.EffectsNode = match.GetNode("EFFECTS");

            ConfigNode[] moduleNodes = match.GetNodes("MODULE");
            for (int m = 0; m < moduleNodes.Length; m++)
            {
                string moduleName = moduleNodes[m].GetValue("name");
                if (string.IsNullOrEmpty(moduleName))
                    continue;
                if (moduleName.StartsWith("ModuleEngines", System.StringComparison.Ordinal))
                {
                    data.EngineModuleEffectNames.Add(ReadEngineEffectNames(moduleNodes[m]));
                }
                else if (moduleName.StartsWith("ModuleRCS", System.StringComparison.Ordinal))
                {
                    string running = moduleNodes[m].GetValue("runningEffectName");
                    data.RcsRunningEffectNames.Add(string.IsNullOrEmpty(running) ? "running" : running);
                }
            }

            data.LegacyFxPrefabNames = ParseLegacyFxKeys(match, runtimePartName);

            ParsekLog.Verbose("WaterfallCompat",
                $"pristine FX data extracted for '{runtimePartName}' from '{sourcePath}': " +
                $"effectsGroups={(data.EffectsNode != null ? data.EffectsNode.CountNodes : 0)} " +
                $"engineModules={data.EngineModuleEffectNames.Count} " +
                $"rcsModules={data.RcsRunningEffectNames.Count} " +
                $"legacyFx={data.LegacyFxPrefabNames.Count}");
            return true;
        }

        /// <summary>
        /// Reads the named effect groups of one pristine ModuleEngines* node.
        /// Mirrors EngineFxBuilder.GetModuleEffectGroupNames, but from config values
        /// (the live module's fields point at post-MM group names that do not exist
        /// in the pristine EFFECTS node).
        /// </summary>
        internal static HashSet<string> ReadEngineEffectNames(ConfigNode moduleNode)
        {
            var result = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (moduleNode == null)
                return result;
            for (int k = 0; k < EngineEffectNameKeys.Length; k++)
            {
                string value = moduleNode.GetValue(EngineEffectNameKeys[k]);
                if (!string.IsNullOrEmpty(value))
                    result.Add(value);
            }
            return result;
        }

        /// <summary>
        /// Parses pristine legacy top-level fx_* keys into FX prefab names.
        /// Keeps running/power exhaust FX only, mirroring the name filtering of the
        /// legacy prefab-children path (ProcessEngineLegacyFx).
        /// </summary>
        internal static List<string> ParseLegacyFxKeys(ConfigNode partNode, string runtimePartName)
        {
            var result = new List<string>();
            if (partNode == null)
                return result;

            int skippedTransient = 0, skippedNonExhaust = 0, skippedEvent = 0, skippedLight = 0;
            for (int v = 0; v < partNode.values.Count; v++)
            {
                string key = partNode.values[v].name;
                if (string.IsNullOrEmpty(key) ||
                    !key.StartsWith("fx_", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                string lower = key.ToLowerInvariant();
                if (lower.Contains("flameout") || lower.Contains("sparks") || lower.Contains("debris"))
                {
                    skippedTransient++;
                    continue;
                }
                // fx_exhaustLight_* are Light prefabs, not particles; the legacy-children
                // path skips them via its ParticleSystem null check. ("light" alone would
                // wrongly kill fx_smokeTrail_light.)
                if (lower.Contains("exhaustlight"))
                {
                    skippedLight++;
                    continue;
                }
                if (!lower.Contains("flame") && !lower.Contains("exhaust") && !lower.Contains("smoke"))
                {
                    skippedNonExhaust++;
                    continue;
                }
                if (!LegacyFxValueIncludesRunningOrPower(partNode.values[v].value))
                {
                    skippedEvent++;
                    continue;
                }
                if (!result.Contains(key))
                    result.Add(key);
            }

            if (result.Count > 0 || skippedTransient > 0 || skippedNonExhaust > 0 ||
                skippedEvent > 0 || skippedLight > 0)
            {
                ParsekLog.Verbose("WaterfallCompat",
                    $"pristine legacy fx_* keys for '{runtimePartName}': kept={result.Count} " +
                    $"skippedTransient={skippedTransient} skippedNonExhaust={skippedNonExhaust} " +
                    $"skippedEvent={skippedEvent} skippedLight={skippedLight}");
            }
            return result;
        }

        /// <summary>
        /// Builds the resolution candidates for a legacy FX prefab name, most specific
        /// first: the exact name, then progressively stripping trailing underscore tokens
        /// (size/variant suffixes) down to the two-token family base. Size-suffixed
        /// variants (fx_exhaustFlame_blue_small, fx_exhaustFlame_yellow_medium) often lose
        /// their donor parts under Waterfall config packs while the generic family prefab
        /// survives on unpatched parts (e.g. SRBs).
        /// </summary>
        internal static List<string> BuildLegacyFxNameCandidates(string name)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(name))
                return result;

            result.Add(name);
            string[] tokens = name.Split('_');
            for (int len = tokens.Length - 1; len >= 2; len--)
            {
                string candidate = string.Join("_", tokens, 0, len);
                if (!result.Contains(candidate))
                    result.Add(candidate);
            }
            return result;
        }

        /// <summary>
        /// Legacy fx_* values are "x, y, z, dx, dy, dz, event[, event...]". Returns true
        /// when any non-numeric token is running/power, or when no event tokens exist.
        /// </summary>
        internal static bool LegacyFxValueIncludesRunningOrPower(string value)
        {
            if (string.IsNullOrEmpty(value))
                return true;

            string[] tokens = value.Split(',');
            bool sawEventToken = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Length == 0)
                    continue;
                if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    continue;
                sawEventToken = true;
                if (string.Equals(token, "running", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "power", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return !sawEventToken;
        }

        internal static void ResetForTesting()
        {
            cacheByPartName.Clear();
            LoadFileRootOverrideForTesting = null;
        }
    }
}
