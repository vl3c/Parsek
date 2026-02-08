using HarmonyLib;
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
        void Awake()
        {
            var harmony = new Harmony("com.parsek.mod");
            harmony.PatchAll(typeof(ParsekHarmony).Assembly);
            DontDestroyOnLoad(gameObject);
            ParsekLog.Log("Harmony patches applied");
        }
    }
}
