using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Static preset map for ghost audio clip selection.
    /// Maps engine propellant type + thrust class to stock audio clips.
    /// Independent of EFFECTS AUDIO config (RSE deletes those).
    /// </summary>
    internal static class GhostAudioPresets
    {
        internal const float HeavyThrustThreshold = 300f; // kN
        internal const float MediumThrustThreshold = 50f;  // kN
        internal const int MaxAudioSourcesPerGhost = 4;
        internal const float OneShotVolumeScale = 0.5f;
        private const double ResolutionLogIntervalSeconds = 5.0;

        private static readonly Dictionary<string, string> presetMap = new Dictionary<string, string>
        {
            { "LiquidFuel_Heavy",      "sound_rocket_hard" },
            { "LiquidFuel_Medium",     "sound_rocket_hard" },
            { "LiquidFuel_Light",      "sound_rocket_spurts" },
            { "SolidFuel_Heavy",       "sound_rocket_hard" },
            { "SolidFuel_Medium",      "sound_rocket_hard" },
            { "SolidFuel_Light",       "sound_rocket_spurts" },
            { "MonoPropellant_Heavy",  "sound_rocket_spurts" },
            { "MonoPropellant_Medium", "sound_rocket_mini" },
            { "MonoPropellant_Light",  "sound_rocket_mini" },
            // Ion engines: stock cfg references `clip = sound_IonEngine` in EFFECTS
            // (see GameData/Squad/Parts/Engine/ionEngine/ionEngine.cfg), but that
            // asset is NOT surfaced through `GameDatabase.GetAudioClip` in KSP 1.12
            // — confirmed by enumerating GameData/Squad/Sounds (no `sound_IonEngine`
            // .wav/.ogg) and by the playtest log
            // logs/2026-04-16_2226_pr316-v3-small-engine/KSP.log lines
            // 15772/18092/22369/26985/35917/41246/45772 (same 7 misses dedupe'd by
            // #421's per-(ghost,pid,clip) WARN gate). Substitute the quietest
            // available rocket clip rather than emit a permanent "AudioClip not
            // found" WARN. The dedupe machinery in
            // `GhostVisualBuilder.WarnMissingAudioClipOnce` stays as a safety net
            // for any future genuinely-missing clip; for the ion case the mapping
            // itself is the fix. Bug #423.
            { "XenonGas",             "sound_rocket_mini" },
            { "ElectricCharge",       "sound_rocket_mini" },
            { "IntakeAir",            "sound_jet_deep" },
            { "Fallback",             "sound_rocket_spurts" }
        };

        private static void LogResolvedClip(string prefabName, int moduleIndex, string message)
        {
            ParsekLog.VerboseRateLimited("GhostAudio",
                $"resolve-clip-{prefabName}-{moduleIndex}",
                message,
                ResolutionLogIntervalSeconds);
        }

        /// <summary>
        /// Classify the primary propellant type from a ModuleEngines propellant list.
        /// Returns: LiquidFuel, SolidFuel, MonoPropellant, XenonGas, ElectricCharge, IntakeAir, or Unknown.
        /// </summary>
        internal static string ClassifyPropellantType(IList<Propellant> propellants)
        {
            if (propellants == null || propellants.Count == 0) return "Unknown";

            // Collect flags in a single pass, resolve after.
            bool hasLiquidFuel = false;
            bool hasMonoPropellant = false;
            bool hasSolidFuel = false;
            bool hasXenonGas = false;
            bool hasElectricCharge = false;
            bool hasIntakeAir = false;

            for (int i = 0; i < propellants.Count; i++)
            {
                string name = propellants[i].name;
                if (name == "IntakeAir") hasIntakeAir = true;
                else if (name == "XenonGas") hasXenonGas = true;
                else if (name == "ElectricCharge") hasElectricCharge = true;
                else if (name == "SolidFuel") hasSolidFuel = true;
                else if (name == "LiquidFuel") hasLiquidFuel = true;
                else if (name == "MonoPropellant") hasMonoPropellant = true;
            }

            // Priority: IntakeAir (jets) > XenonGas (ion) > SolidFuel > LiquidFuel > MonoPropellant > pure EC (ion)
            if (hasIntakeAir) return "IntakeAir";
            if (hasXenonGas) return "XenonGas";
            if (hasSolidFuel) return "SolidFuel";
            if (hasLiquidFuel) return "LiquidFuel";
            if (hasMonoPropellant) return "MonoPropellant";
            if (hasElectricCharge) return "ElectricCharge";

            return "Unknown";
        }

        /// <summary>
        /// Classify engine thrust class: Heavy (>300kN), Medium (>50kN), Light (<=50kN).
        /// </summary>
        internal static string ClassifyThrustClass(float maxThrust)
        {
            if (maxThrust > HeavyThrustThreshold) return "Heavy";
            if (maxThrust > MediumThrustThreshold) return "Medium";
            return "Light";
        }

        /// <summary>
        /// Resolve the audio clip path for an engine module on a part prefab.
        /// Returns null if no clip could be resolved.
        /// </summary>
        internal static string ResolveEngineAudioClip(Part prefab, int moduleIndex)
        {
            if (prefab == null) return LookupClip("Fallback");

            var engines = prefab.Modules.GetModules<ModuleEngines>();
            if (engines == null || moduleIndex < 0 || moduleIndex >= engines.Count)
            {
                ParsekLog.Warn("GhostAudio",
                    $"ResolveEngineAudioClip: no ModuleEngines[{moduleIndex}] on '{prefab.name}', using fallback");
                return LookupClip("Fallback");
            }

            var engine = engines[moduleIndex];
            string propType = ClassifyPropellantType(engine.propellants);
            string thrustClass = ClassifyThrustClass(engine.maxThrust);

            // Ion/jet engines don't vary by thrust class
            if (propType == "XenonGas" || propType == "ElectricCharge" || propType == "IntakeAir")
            {
                string clip = LookupClip(propType);
                LogResolvedClip(prefab.name, moduleIndex,
                    $"Resolved '{prefab.name}' midx={moduleIndex}: propellant={propType} → clip='{clip}'");
                return clip;
            }

            string key = propType + "_" + thrustClass;
            string result = LookupClip(key);
            if (result == null)
            {
                ParsekLog.Warn("GhostAudio",
                    $"Unknown preset key '{key}' for '{prefab.name}' midx={moduleIndex}, using fallback");
                result = LookupClip("Fallback");
            }

            LogResolvedClip(prefab.name, moduleIndex,
                $"Resolved '{prefab.name}' midx={moduleIndex}: {propType}_{thrustClass} thrust={engine.maxThrust:F0}kN → clip='{result}'");
            return result;
        }

        /// <summary>
        /// Resolve the audio clip path for a one-shot event (decouple, explosion).
        /// Returns null for event types that have no associated sound.
        /// </summary>
        internal static string ResolveOneShotClip(PartEventType eventType)
        {
            switch (eventType)
            {
                case PartEventType.Destroyed: return "sound_explosion_large";
                default: return null;
            }
        }

        /// <summary>
        /// Build the volume curve for a ghost engine based on clip type.
        /// Rockets get 2x boost to compensate for SHIP_VOLUME (~0.5) + atmosphere attenuation.
        /// Ion is now mapped to a rocket clip per #423, so it follows the same boosted curve;
        /// jets stay at 1x — already audible at stock levels.
        /// </summary>
        internal static FloatCurve BuildVolumeCurve(string clipPath)
        {
            bool isRocket = clipPath != null && clipPath.Contains("rocket");
            bool isJet = clipPath != null && clipPath.Contains("jet");
            var curve = new FloatCurve();
            if (isJet)
            {
                curve.Add(0f, 0f, 0f, 0.4f);
                curve.Add(1f, 0.4f, 0.4f, 0f);
            }
            else
            {
                curve.Add(0f, 0f, 0f, 1f);
                curve.Add(1f, 1f, 1f, 0f);
            }
            return curve;
        }

        internal static FloatCurve BuildDefaultVolumeCurve()
        {
            var curve = new FloatCurve();
            curve.Add(0f, 0f, 0f, 1f);
            curve.Add(1f, 1f, 1f, 0f);
            return curve;
        }

        /// <summary>
        /// Build the default pitch curve for ghost engine audio.
        /// power 0 → pitch 0.4, power 1 → pitch 1.0.
        /// </summary>
        internal static FloatCurve BuildDefaultPitchCurve()
        {
            var curve = new FloatCurve();
            curve.Add(0f, 0.4f, 0f, 0.6f);
            curve.Add(1f, 1.0f, 0.6f, 0f);
            return curve;
        }

        // internal (not private) so unit tests can assert preset-map entries
        // directly without standing up a real `Part` (which requires Unity runtime).
        internal static string LookupClip(string key)
        {
            string result;
            return presetMap.TryGetValue(key, out result) ? result : null;
        }
    }
}
