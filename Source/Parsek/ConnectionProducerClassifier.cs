namespace Parsek
{
    /// <summary>
    /// Classifies which stock connection producer created a part couple
    /// (docs/dev/design-logistics-claw-producer.md section 2.1). Called once per
    /// onPartCouple in ParsekFlight while both event parts still resolve; the
    /// result is stamped on the merge branch data and the route connection window
    /// so admission can gate by producer instead of assuming every couple is a
    /// docking-port dock.
    ///
    /// Classification contract (from the decompile findings,
    /// docs/dev/research/claw-grapple-coupling-internals.md):
    /// - a docking-port dock has ModuleDockingNode on BOTH endpoints;
    /// - a claw couple has ModuleGrappleNode on EITHER endpoint (the grabbed side
    ///   is an arbitrary part: a PotatoRoid, a tank wall, a pod), and which side
    ///   is "from" depends on Vessel.GetDominantVessel, so both ends are tested;
    /// - anything else (modded coupling producers) is Unknown and fails closed at
    ///   route admission (RouteAnalysisStatus.UnsupportedConnectionKind).
    /// </summary>
    internal static class ConnectionProducerClassifier
    {
        /// <summary>
        /// Pure classification core, unit-testable without Unity. Grapple wins
        /// over dock when both module kinds appear across the pair (a claw
        /// grabbing a docking port is still a grapple: the claw made the couple).
        /// </summary>
        internal static RouteConnectionKind ClassifyCore(
            bool fromHasDockingNode,
            bool toHasDockingNode,
            bool fromHasGrappleNode,
            bool toHasGrappleNode)
        {
            if (fromHasGrappleNode || toHasGrappleNode)
                return RouteConnectionKind.Grapple;
            if (fromHasDockingNode && toHasDockingNode)
                return RouteConnectionKind.DockingPort;
            return RouteConnectionKind.Unknown;
        }

        /// <summary>
        /// Live classification from the onPartCouple event parts.
        /// FindModuleImplementing covers modded subclasses of the stock modules.
        /// </summary>
        internal static RouteConnectionKind Classify(Part from, Part to)
        {
            return ClassifyCore(
                HasDockingNode(from),
                HasDockingNode(to),
                HasGrappleNode(from),
                HasGrappleNode(to));
        }

        /// <summary>
        /// True when either couple endpoint belongs to an EVA kerbal vessel. A
        /// claw grabbing a kerbal fires a real Part.Couple (findings 1.4) but is
        /// not a logistics transfer; capture suppresses the route window for it.
        /// </summary>
        internal static bool InvolvesEvaVessel(Part from, Part to)
        {
            if (from != null && from.vessel != null && from.vessel.isEVA)
                return true;
            if (to != null && to.vessel != null && to.vessel.isEVA)
                return true;
            return false;
        }

        private static bool HasDockingNode(Part part)
        {
            return part != null && part.FindModuleImplementing<ModuleDockingNode>() != null;
        }

        private static bool HasGrappleNode(Part part)
        {
            return part != null && part.FindModuleImplementing<ModuleGrappleNode>() != null;
        }
    }
}
