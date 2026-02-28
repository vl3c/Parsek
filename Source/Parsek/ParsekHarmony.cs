using HarmonyLib;
using System;
using System.Linq;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Harmony patcher entry point. Runs once at game startup and applies
    /// all Harmony patches in this assembly.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ParsekHarmony : MonoBehaviour
    {
        private static bool initialized;

        void Awake()
        {
            if (initialized)
            {
                ParsekLog.Warn("Harmony", "Awake called after initialization; skipping duplicate PatchAll");
                return;
            }

            var assembly = typeof(ParsekHarmony).Assembly;
            var harmony = new Harmony("com.parsek.mod");

            // Apply patches individually so one failure doesn't block the rest
            int applied = 0;
            int failed = 0;
            var patchTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0);

            foreach (var patchType in patchTypes)
            {
                try
                {
                    harmony.CreateClassProcessor(patchType).Patch();
                    applied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    ParsekLog.Error("Harmony", $"Failed to apply patch {patchType.Name}: {ex.Message}");
                }
            }

            initialized = true;
            DontDestroyOnLoad(gameObject);
            ParsekLog.Info("Init", $"SessionStart runUtc={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            ParsekLog.Info("Harmony", $"Harmony patches applied: {applied} succeeded, {failed} failed");
        }
    }
}
