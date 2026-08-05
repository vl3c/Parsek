using HarmonyLib;
using System;
using System.Globalization;
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

        /// <summary>
        /// The stable SessionStart message body (without the <c>[Parsek][LEVEL][Sub]</c>
        /// prefix). Load-bearing contract, exactly like
        /// <c>TestCommandMissionMark.FormatMarkMessage</c>: the session-marker rule in
        /// <c>ParsekLogContractChecker</c> (SES-001 / SES-002), <c>ParsekKspLogParser</c>'s
        /// latest-session split, and the harness log fixtures all match
        /// <c>^SessionStart runUtc=&lt;digits&gt;$</c>. The in-game SES-002 cell validates
        /// THIS formatter's output rather than a string it builds itself, so a drift here
        /// reds the contract test instead of sailing past it.
        /// </summary>
        internal static string FormatSessionStartMessage(long runUtcSeconds)
            => "SessionStart runUtc=" + runUtcSeconds.ToString(CultureInfo.InvariantCulture);

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
            ParsekLog.Info("Init",
                FormatSessionStartMessage(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            ParsekLog.Info("Harmony", $"Harmony patches applied: {applied} succeeded, {failed} failed");
        }
    }
}
