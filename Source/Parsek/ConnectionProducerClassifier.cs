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
        /// Pure classification core, unit-testable without Unity. Precedence
        /// (review fix): a DOCK PAIR (ModuleDockingNode on BOTH ends) wins even
        /// when a grapple module is also present - a port-to-port couple is
        /// most plausibly the docking FSM, and stock claw grabs never form a
        /// dock pair (the claw part carries no docking node, so grabbing a
        /// docking-port PART yields dock-on-one-end only, which classifies
        /// Grapple below). The ambiguous cell is a MODDED combo part (dock +
        /// grapple modules on the claw's own side) docking normally; dock-pair
        /// precedence classifies it DockingPort, and when that guess is wrong
        /// the consequence direction is stricter (an empty window rejects
        /// instead of skipping), never looser.
        /// </summary>
        internal static RouteConnectionKind ClassifyCore(
            bool fromHasDockingNode,
            bool toHasDockingNode,
            bool fromHasGrappleNode,
            bool toHasGrappleNode)
        {
            if (fromHasDockingNode && toHasDockingNode)
                return RouteConnectionKind.DockingPort;
            if (fromHasGrappleNode || toHasGrappleNode)
                return RouteConnectionKind.Grapple;
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
