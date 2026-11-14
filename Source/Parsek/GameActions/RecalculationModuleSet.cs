using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The resource modules one recalculation walks, by tier (design doc 1.8): first-tier
    /// (independent, dispatched in registration order), the strategy transform, second-tier
    /// (dependent, in registration order) and facilities. <see cref="RecalculationEngine"/>
    /// takes the set as an argument and keeps no module state of its own, so a test walks
    /// its own set without touching any other test's. Production builds one set with the
    /// real modules in <c>LedgerOrchestrator.Initialize</c> and passes it.
    /// </summary>
    internal sealed class RecalculationModuleSet
    {
        /// <summary>The tier a module is registered at. Modules are dispatched in tier
        /// order: first-tier, strategy transform, second-tier, facilities.</summary>
        internal enum ModuleTier
        {
            FirstTier,
            SecondTier,
            Strategy,
            Facilities
        }

        private readonly List<IResourceModule> firstTier = new List<IResourceModule>();
        private readonly List<IResourceModule> secondTier = new List<IResourceModule>();
        private IResourceModule strategy;
        private IResourceModule facilities;

        internal List<IResourceModule> FirstTier => firstTier;
        internal List<IResourceModule> SecondTier => secondTier;
        internal IResourceModule Strategy => strategy;
        internal IResourceModule Facilities => facilities;

        /// <summary>How many modules the set holds, over every tier.</summary>
        internal int Count => CountOf(this);

        /// <summary>
        /// Adds a module at <paramref name="tier"/>. A module already in its tier is skipped;
        /// the strategy and facilities tiers hold one module each, and a second registration
        /// replaces the first.
        /// </summary>
        internal void Register(IResourceModule module, ModuleTier tier)
        {
            if (module == null)
            {
                ParsekLog.Warn("RecalcEngine", "RegisterModule called with null module, skipping");
                return;
            }

            switch (tier)
            {
                case ModuleTier.FirstTier:
                    if (firstTier.Contains(module))
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"First-tier module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    firstTier.Add(module);
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered first-tier module: {module.GetType().Name}, total={firstTier.Count}");
                    break;

                case ModuleTier.SecondTier:
                    if (secondTier.Contains(module))
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Second-tier module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    secondTier.Add(module);
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered second-tier module: {module.GetType().Name}, total={secondTier.Count}");
                    break;

                case ModuleTier.Strategy:
                    if (strategy == module)
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Strategy transform module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    strategy = module;
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered strategy transform module: {module.GetType().Name}");
                    break;

                case ModuleTier.Facilities:
                    if (facilities == module)
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Facilities module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    facilities = module;
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered facilities module: {module.GetType().Name}");
                    break;

                default:
                    ParsekLog.Warn("RecalcEngine", $"Unknown tier '{tier}' for module {module.GetType().Name}");
                    break;
            }
        }

        /// <summary>Removes every module.</summary>
        internal void Clear()
        {
            int total = Count;
            firstTier.Clear();
            secondTier.Clear();
            strategy = null;
            facilities = null;
            LogCleared(total);
        }

        /// <summary>
        /// The line a cleared set writes. <c>LedgerOrchestrator</c> writes it too when it
        /// drops its set for a fresh one, so a reset logs what it removed.
        /// </summary>
        internal static void LogCleared(int removed)
        {
            ParsekLog.Verbose("RecalcEngine", $"ClearModules: removed={removed}");
        }

        /// <summary>Null-safe count (0 for null).</summary>
        internal static int CountOf(RecalculationModuleSet set)
        {
            if (set == null)
                return 0;
            return (set.firstTier != null ? set.firstTier.Count : 0)
                + (set.secondTier != null ? set.secondTier.Count : 0)
                + (set.strategy != null ? 1 : 0)
                + (set.facilities != null ? 1 : 0);
        }
    }
}
