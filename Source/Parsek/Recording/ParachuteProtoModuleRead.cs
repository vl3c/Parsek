using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Reads a parachute's canopy state from a persisted part module node (a proto module of
    /// an unloaded vessel, or a module a caller saved to a node), so the chute descent
    /// prediction sees every chute an unloaded vessel carries, not only the stock
    /// <c>ModuleParachute</c>. ConfigNode reads only: no reflection, no live module.
    /// <list type="bullet">
    /// <item><c>ModuleParachute</c> and its one stock subclass <c>ModuleEvaChute</c> (an EVA
    /// kerbal's personal chute, decompiled KSP 1.12.5): the persisted <c>persistentState</c>
    /// (STOWED / ACTIVE / SEMIDEPLOYED / DEPLOYED / CUT) and <c>deployAltitude</c> (full
    /// deployment height above the ground).</item>
    /// <item>RealChute's <c>RealChuteModule</c>: one <c>PARACHUTE</c> child node per canopy,
    /// each with <c>depState</c> (the name of RealChute's DeploymentStates: NONE, STOWED,
    /// PREDEPLOYED, LOWDEPLOYED, DEPLOYED, CUT) and <c>deploymentAlt</c> (full deployment
    /// height above the ground). PREDEPLOYED and LOWDEPLOYED read as SEMIDEPLOYED; the most
    /// open canopy decides. The descent planner reads its rate from the vessel's own vertical
    /// speed, as for a stock chute, so no RealChute field is needed for the rate.</item>
    /// </list>
    /// The returned state is a stock-style <c>persistentState</c> string, so a caller that
    /// classifies stock chutes classifies all of them the same way. Pure.
    /// </summary>
    internal static class ParachuteProtoModuleRead
    {
        internal const string StockParachuteModuleName = "ModuleParachute";
        internal const string StockEvaChuteModuleName = "ModuleEvaChute";
        internal const string RealChuteModuleName = "RealChuteModule";
        internal const string RealChuteCanopyNodeName = "PARACHUTE";

        private const string Deployed = "DEPLOYED";
        private const string SemiDeployed = "SEMIDEPLOYED";

        /// <summary>Pure: is <paramref name="moduleName"/> a parachute module this reader reads?</summary>
        internal static bool IsChuteModuleName(string moduleName)
        {
            return string.Equals(moduleName, StockParachuteModuleName, StringComparison.Ordinal)
                || string.Equals(moduleName, StockEvaChuteModuleName, StringComparison.Ordinal)
                || string.Equals(moduleName, RealChuteModuleName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Pure: the canopy state of one persisted chute module. False for a module that is not
        /// a chute (<see cref="IsChuteModuleName"/>) or has no values. Otherwise true, with
        /// <paramref name="persistentState"/> the stock-style state (null when the node names
        /// none) and <paramref name="deployAltitude"/> the full-deployment height above the
        /// ground (NaN when absent or unreadable).
        /// </summary>
        internal static bool TryReadCanopy(
            string moduleName, ConfigNode moduleValues,
            out string persistentState, out double deployAltitude)
        {
            persistentState = null;
            deployAltitude = double.NaN;
            if (moduleValues == null || !IsChuteModuleName(moduleName))
                return false;

            if (!string.Equals(moduleName, RealChuteModuleName, StringComparison.Ordinal))
            {
                persistentState = moduleValues.GetValue("persistentState");
                deployAltitude = ParseFinite(moduleValues.GetValue("deployAltitude"));
                return true;
            }

            ConfigNode[] canopies = moduleValues.GetNodes(RealChuteCanopyNodeName);
            int bestRank = -1;
            for (int i = 0; i < canopies.Length; i++)
            {
                ConfigNode canopy = canopies[i];
                string mapped = MapRealChuteState(canopy?.GetValue("depState"));
                int rank = Rank(mapped);
                if (rank <= bestRank)
                    continue;
                bestRank = rank;
                persistentState = mapped;
                deployAltitude = ParseFinite(canopy.GetValue("deploymentAlt"));
            }
            return true;
        }

        /// <summary>
        /// Pure: RealChute's <c>depState</c> as a stock <c>persistentState</c>. DEPLOYED stays;
        /// PREDEPLOYED / LOWDEPLOYED are a partly open canopy (SEMIDEPLOYED); STOWED, CUT and
        /// NONE pass through (they never count as open); an unknown name is null.
        /// </summary>
        internal static string MapRealChuteState(string depState)
        {
            if (string.IsNullOrEmpty(depState))
                return null;
            switch (depState.Trim().ToUpperInvariant())
            {
                case "DEPLOYED":
                    return Deployed;
                case "PREDEPLOYED":
                case "LOWDEPLOYED":
                    return SemiDeployed;
                case "STOWED":
                    return "STOWED";
                case "CUT":
                    return "CUT";
                case "NONE":
                    return "NONE";
                default:
                    return null;
            }
        }

        private static int Rank(string persistentState)
        {
            if (string.Equals(persistentState, Deployed, StringComparison.Ordinal))
                return 2;
            if (string.Equals(persistentState, SemiDeployed, StringComparison.Ordinal))
                return 1;
            return persistentState != null ? 0 : -1;
        }

        private static double ParseFinite(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return double.NaN;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                || double.IsNaN(v) || double.IsInfinity(v))
                return double.NaN;
            return v;
        }
    }
}
