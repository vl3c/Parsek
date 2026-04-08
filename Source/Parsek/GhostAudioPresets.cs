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
        internal const float OneShotVolumeScale = 0.4f; // one-shot events (decouple/explosion) are quieter than looping engines

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
            { "XenonGas",             "sound_IonEngine" },
            { "ElectricCharge",       "sound_IonEngine" },
            { "IntakeAir",            "sound_jet_deep" },
            { "Fallback",             "sound_rocket_spurts" }
        };

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
                ParsekLog.Verbose("GhostAudio",
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

            ParsekLog.Verbose("GhostAudio",
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
                case PartEventType.Decoupled: return "sound_decoupler_fire";
                case PartEventType.Destroyed: return "sound_explosion_large";
                default: return null;
            }
        }

        /// <summary>
        /// Build the default volume curve for ghost engine audio.
        /// Linear: power 0 → volume 0, power 1 → volume 1.
        /// </summary>
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

        private static string LookupClip(string key)
        {
            string result;
            return presetMap.TryGetValue(key, out result) ? result : null;
        }
    }
}
