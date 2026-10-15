using System;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix / postfix pairs on <see cref="ResourceMap.UnlockPlanet"/> and
    /// <see cref="ResourceMap.UnlockBiome"/>, the two stock writes behind a resource scan
    /// (RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED). Decompiled KSP 1.12.5: <c>UnlockPlanet</c> is
    /// called by <c>ModuleOrbitalSurveyor.CompleteSurvey</c> (and the Making History
    /// editor), <c>UnlockBiome</c> by <c>ModuleBiomeScanner.RunAnalysis</c>; both return
    /// early when the body or biome is already unlocked and fire no event, and only
    /// <c>CompleteSurvey</c> has a GameEvent of its own. Patching the writes instead of the
    /// callers captures every source, a mod scanner included.
    ///
    /// The prefix records whether the target was locked; the postfix emits only for a
    /// locked-to-unlocked transition, so a repeat scan and Parsek's own re-apply (also
    /// suppressed by <see cref="GameStateRecorder.IsReplayingActions"/>) write nothing.
    /// Never rethrows into the stock scan.
    /// </summary>
    [HarmonyPatch(typeof(ResourceMap), nameof(ResourceMap.UnlockPlanet))]
    internal static class ResourceScanUnlockPlanetPatch
    {
        private const string Tag = "ResourceScanCapture";

        internal static void Prefix(ResourceMap __instance, int planetId, out bool __state)
        {
            __state = false;
            try
            {
                __state = __instance != null && !__instance.IsPlanetScanned(planetId);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"UnlockPlanet prefix threw reading the scan state: {ex.Message}");
            }
        }

        internal static void Postfix(ResourceMap __instance, int planetId, bool __state)
        {
            if (ParsekGameModeGate.CheckInert("ResourceScanUnlockPlanetPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                if (!__state)
                {
                    ParsekLog.Verbose(Tag,
                        $"UnlockPlanet postfix: planetId={planetId} was already scanned - skipped");
                    return;
                }
                if (GameStateRecorder.IsReplayingActions)
                {
                    ParsekLog.Verbose(Tag, "UnlockPlanet postfix: suppressed during action replay");
                    return;
                }
                if (__instance == null || !__instance.IsPlanetScanned(planetId))
                {
                    ParsekLog.Verbose(Tag,
                        $"UnlockPlanet postfix: planetId={planetId} still not scanned - skipped");
                    return;
                }

                string bodyName = ResourceScanBodies.NameForPlanetId(planetId);
                if (bodyName == null)
                {
                    ParsekLog.Warn(Tag,
                        $"UnlockPlanet postfix: no body has flightGlobalsIndex={planetId} - skipped");
                    return;
                }

                GameStateRecorder.OnResourceScanUnlocked(
                    Planetarium.GetUniversalTime(), bodyName, null);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"UnlockPlanet postfix threw while capturing the scan: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(ResourceMap), nameof(ResourceMap.UnlockBiome))]
    internal static class ResourceScanUnlockBiomePatch
    {
        private const string Tag = "ResourceScanCapture";

        internal static void Prefix(ResourceMap __instance, int planetId, string biomeName, out bool __state)
        {
            __state = false;
            try
            {
                __state = __instance != null && !__instance.IsBiomeUnlocked(planetId, biomeName);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"UnlockBiome prefix threw reading the scan state: {ex.Message}");
            }
        }

        internal static void Postfix(ResourceMap __instance, int planetId, string biomeName, bool __state)
        {
            if (ParsekGameModeGate.CheckInert("ResourceScanUnlockBiomePatch.Postfix")) return; // S9 game-mode gate
            try
            {
                if (!__state)
                {
                    ParsekLog.Verbose(Tag,
                        $"UnlockBiome postfix: planetId={planetId} biome='{biomeName}' was already unlocked - skipped");
                    return;
                }
                if (GameStateRecorder.IsReplayingActions)
                {
                    ParsekLog.Verbose(Tag, "UnlockBiome postfix: suppressed during action replay");
                    return;
                }
                if (string.IsNullOrEmpty(biomeName)
                    || __instance == null || !__instance.IsBiomeUnlocked(planetId, biomeName))
                {
                    ParsekLog.Verbose(Tag,
                        $"UnlockBiome postfix: planetId={planetId} biome='{biomeName}' still locked - skipped");
                    return;
                }

                string bodyName = ResourceScanBodies.NameForPlanetId(planetId);
                if (bodyName == null)
                {
                    ParsekLog.Warn(Tag,
                        $"UnlockBiome postfix: no body has flightGlobalsIndex={planetId} - skipped");
                    return;
                }

                GameStateRecorder.OnResourceScanUnlocked(
                    Planetarium.GetUniversalTime(), bodyName, biomeName);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"UnlockBiome postfix threw while capturing the scan: {ex}");
            }
        }
    }

    /// <summary>
    /// Body name &lt;-&gt; stock planet id (<c>CelestialBody.flightGlobalsIndex</c>, the id
    /// <see cref="ResourceMap"/> keys on). The ledger stores the name, which survives a body
    /// list that orders differently on another install. <c>FlightGlobals</c> reads stay in
    /// no-inlining cores so the mono test host never JITs them from a caller.
    /// </summary>
    internal static class ResourceScanBodies
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string NameForPlanetId(int planetId)
        {
            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b != null && b.flightGlobalsIndex == planetId)
                    return b.bodyName;
            }
            return null;
        }

        /// <summary>The planet id of <paramref name="bodyName"/>, or -1 when no body has that name.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int PlanetIdForName(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return -1;
            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return -1;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b != null && string.Equals(b.bodyName, bodyName, StringComparison.Ordinal))
                    return b.flightGlobalsIndex;
            }
            return -1;
        }
    }
}
