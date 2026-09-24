using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The one place a ledger strategy id becomes the name the player reads: the Career
    /// window's Strategies rows and the Timeline's strategy activate / deactivate rows.
    ///
    /// A ledger strategy id is stock's strategy config name
    /// (<c>Strategy.Config.Name</c>, e.g. <c>OutsourcedResearchCfg</c>). It is named by
    /// stock's own localized title (<c>StrategyConfig.Title</c>, e.g. "Outsourced R&amp;D")
    /// when the live strategy system can answer, else by the humanized id with the
    /// <c>Cfg</c> suffix dropped ("Outsourced Research"), so a headless build, a scene with
    /// no strategy system and a removed modded strategy all still read as words.
    /// </summary>
    internal static class StrategyDisplayNames
    {
        /// <summary>Row text when a strategy action carries no id at all.</summary>
        internal const string UnknownStrategyText = "unknown";

        /// <summary>
        /// Test seam for the live title lookup. When non-null it replaces the
        /// <c>StrategySystem</c> read; returning null or empty takes the fallback.
        /// </summary>
        internal static Func<string, string> TitleLookupForTesting;

        // Stock titles resolved once per id. Only successful lookups are cached, so an id
        // looked up before the strategy system loads is retried on the next build.
        private static readonly Dictionary<string, string> titleCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void ResetForTesting()
        {
            TitleLookupForTesting = null;
            titleCache.Clear();
        }

        /// <summary>
        /// The strategy's display name: stock's localized title when KSP can answer, else
        /// <see cref="HumanizeStrategyId"/>. Never returns an empty string.
        /// </summary>
        internal static string Resolve(string strategyId)
        {
            if (string.IsNullOrEmpty(strategyId)) return UnknownStrategyText;
            var lookup = TitleLookupForTesting;
            string stock;
            if (lookup != null)
            {
                try { stock = lookup(strategyId); }
                catch (Exception ex)
                {
                    ParsekLog.VerboseRateLimited("UI",
                        "StrategyDisplayNames.lookupThrew",
                        $"StrategyDisplayNames: strategy title lookup threw id={strategyId} ex={ex.GetType().Name}");
                    stock = null;
                }
            }
            else
            {
                if (titleCache.TryGetValue(strategyId, out string cached))
                    return cached;
                stock = LookupStockTitle(strategyId);
                if (IsUsableStockTitle(stock))
                    titleCache[strategyId] = stock;
            }
            if (IsUsableStockTitle(stock)) return stock;

            // Rate-limited per id so a retired or modded strategy does not re-log the
            // fallback on every ledger invalidation.
            ParsekLog.VerboseRateLimited("UI",
                "StrategyDisplayNames.fallback." + strategyId,
                $"StrategyDisplayNames: strategy title fallback id={strategyId}");
            return HumanizeStrategyId(strategyId);
        }

        /// <summary>
        /// Fallback name when stock cannot answer: the config name without its
        /// <c>Cfg</c> suffix, PascalCase split ("OutsourcedResearchCfg" ->
        /// "Outsourced Research", "AgressiveNegotiations" -> "Agressive Negotiations").
        /// </summary>
        internal static string HumanizeStrategyId(string strategyId)
        {
            if (string.IsNullOrEmpty(strategyId)) return UnknownStrategyText;
            string core = strategyId;
            if (core.Length > 3 && core.EndsWith("Cfg", StringComparison.Ordinal))
                core = core.Substring(0, core.Length - 3);
            string spaced = CareerStateWindowUI.SpaceBeforeCapitals(core);
            if (string.IsNullOrEmpty(spaced)) return strategyId;
            return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1);
        }

        private static bool IsUsableStockTitle(string title)
        {
            // An unresolved localization tag comes back verbatim ("#autoLOC_501158").
            return !string.IsNullOrEmpty(title) && title[0] != '#';
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string LookupStockTitle(string strategyId)
        {
            try
            {
                return LookupStockTitleCore(strategyId);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("UI",
                    "StrategyDisplayNames.lookupThrew",
                    $"StrategyDisplayNames: strategy title lookup threw id={strategyId} ex={ex.GetType().Name}");
                return null;
            }
        }

        // Separate NoInlining core: mono resolves a KSP type's failing initializer when it
        // JITs the CALLING method, so the read that can throw headlessly sits one frame
        // below the try/catch that handles it.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string LookupStockTitleCore(string strategyId)
        {
            var system = Strategies.StrategySystem.Instance;
            if (system == null || system.Strategies == null) return null;
            var list = system.Strategies;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null || s.Config == null) continue;
                if (s.Config.Name == strategyId) return s.Config.Title;
            }
            return null;
        }
    }
}
