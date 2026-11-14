namespace Parsek
{
    /// <summary>
    /// Detection gate for Waterfall-patched parts.
    /// Waterfall config packs (e.g. Stock Waterfall Effects) delete the stock EFFECTS
    /// particle definitions that ghost FX are built from. When a part prefab carries a
    /// ModuleWaterfallFX module, the pristine-config ghost FX fallback
    /// (<see cref="PristinePartFxResolver"/>) is allowed to engage. With no Waterfall
    /// installed the gate can never fire, so stock installs are behavior-identical.
    /// Name-based checks only -- no compile-time Waterfall reference.
    /// </summary>
    internal static class WaterfallCompat
    {
        internal const string WaterfallModuleName = "ModuleWaterfallFX";

        private static bool loggedGateFirstHit;
        private static bool? waterfallAssemblyLoaded;

        /// <summary>
        /// True when this part prefab carries a ModuleWaterfallFX module, i.e. a
        /// Waterfall config pack patched it. Logs once per session on first hit.
        /// </summary>
        internal static bool PartHasWaterfallModule(Part prefab)
        {
            if (prefab == null || prefab.Modules == null)
                return false;

            bool has = prefab.Modules.Contains(WaterfallModuleName);
            if (has && !loggedGateFirstHit)
            {
                loggedGateFirstHit = true;
                ParsekLog.Info("WaterfallCompat",
                    $"Waterfall-patched part detected ('{prefab.partInfo?.name ?? prefab.name}'); " +
                    "pristine-config ghost FX fallback armed for this session");
            }
            return has;
        }

        /// <summary>
        /// Pure fallback decision: the pristine-config recovery only runs when the
        /// post-ModuleManager EFFECTS scan produced zero particle entries AND the part
        /// is Waterfall-patched. Gate closed (stock install) means no behavior change.
        /// </summary>
        internal static bool ShouldAttemptPristineFxFallback(int scannedEntryCount, bool partHasWaterfallModule)
        {
            return scannedEntryCount == 0 && partHasWaterfallModule;
        }

        /// <summary>
        /// True when the Waterfall plugin assembly is loaded. Cached per session;
        /// used by in-game tests to skip on stock installs.
        /// </summary>
        internal static bool IsWaterfallAssemblyLoaded()
        {
            if (waterfallAssemblyLoaded.HasValue)
                return waterfallAssemblyLoaded.Value;

            bool found = false;
            int scanned = 0;
            if (AssemblyLoader.loadedAssemblies != null)
            {
                for (int i = 0; i < AssemblyLoader.loadedAssemblies.Count; i++)
                {
                    var loaded = AssemblyLoader.loadedAssemblies[i];
                    if (loaded == null)
                        continue;
                    scanned++;
                    if (string.Equals(loaded.name, "Waterfall", System.StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
            }

            waterfallAssemblyLoaded = found;
            ParsekLog.Info("WaterfallCompat",
                $"Waterfall assembly {(found ? "detected" : "not detected")} (assembliesScanned={scanned})");
            return found;
        }

        internal static void ResetForTesting()
        {
            loggedGateFirstHit = false;
            waterfallAssemblyLoaded = null;
        }
    }
}
