namespace Parsek
{
    /// <summary>
    /// Recognizes a pre-attached docking-port Undock at the moment KSP fires
    /// <c>GameEvents.onPartDeCouple</c>.
    ///
    /// <para>Stock (decompiled ModuleDockingNode, KSP 1.12.5): a port with a part attached to
    /// its docking node in the editor enters the "PreAttached" FSM state, whose OnEnter shows
    /// the "Undock" event. <c>Undock()</c> then sees <c>undockPreAttached</c> and calls
    /// <c>Decouple()</c>, which calls <c>Part.decouple()</c> on the port or on the part on its
    /// reference node. <c>Part.decouple</c> fires <c>onPartDeCouple(part)</c> first, while
    /// <c>part.parent</c> and the node's <c>attachedPart</c> are still set, then destroys the
    /// part's attachJoint. It never fires <c>onPartUndock</c> or <c>onVesselsUndocking</c>, so
    /// without this check the separation reads "Decoupled" although the player clicked
    /// Undock. The "Decouple Node" event is declared <c>active = false</c> and nothing in the
    /// module turns it on.</para>
    /// </summary>
    internal static class DockingPortSeparation
    {
        /// <summary>
        /// Pure decision. A decoupling part is a pre-attached port undock when a docking node
        /// in the PreAttached state faces the other side of the joint (on either part), and
        /// neither side carries a decoupler: a decoupler stacked on a port's docking face also
        /// leaves the port PreAttached, and its firing must keep reading "Decoupled".
        /// </summary>
        internal static bool IsPreAttachedPortSeparation(
            bool partHasPreAttachedNodeFacingParent,
            bool parentHasPreAttachedNodeFacingPart,
            bool eitherSideHasDecoupler)
        {
            return (partHasPreAttachedNodeFacingParent || parentHasPreAttachedNodeFacingPart)
                && !eitherSideHasDecoupler;
        }

        /// <summary>Live read for an <c>onPartDeCouple</c> handler.</summary>
        internal static bool IsPreAttachedPortDecouple(Part part)
        {
            if (part == null || part.parent == null) return false;
            Part parent = part.parent;
            bool eitherSideHasDecoupler =
                part.FindModuleImplementing<ModuleDecouplerBase>() != null
                || parent.FindModuleImplementing<ModuleDecouplerBase>() != null;
            return IsPreAttachedPortSeparation(
                HasPreAttachedNodeFacing(part, parent),
                HasPreAttachedNodeFacing(parent, part),
                eitherSideHasDecoupler);
        }

        private static bool HasPreAttachedNodeFacing(Part portPart, Part other)
        {
            var nodes = portPart.FindModulesImplementing<ModuleDockingNode>();
            if (nodes == null) return false;
            for (int i = 0; i < nodes.Count; i++)
            {
                ModuleDockingNode node = nodes[i];
                if (node == null || node.fsm == null || node.st_preattached == null) continue;
                if (node.fsm.CurrentState != node.st_preattached) continue;
                if (node.referenceNode != null && node.referenceNode.attachedPart == other)
                    return true;
            }
            return false;
        }
    }
}
