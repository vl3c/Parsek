using HarmonyLib;
using System;
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

            try
            {
                var assembly = typeof(ParsekHarmony).Assembly;
                var harmony = new Harmony("com.parsek.mod");
                harmony.PatchAll(assembly);
                initialized = true;
                DontDestroyOnLoad(gameObject);
                ParsekLog.Info("Init", $"SessionStart runUtc={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                ParsekLog.Info("Harmony", $"Harmony patches applied for assembly '{assembly.GetName().Name}'");
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("Harmony", $"Failed to apply Harmony patches: {ex.Message}");
            }
        }
    }
}
