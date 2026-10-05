namespace Parsek
{
    /// <summary>
    /// One-shot note that the player clicked Undock on a pre-attached (VAB-joined) docking
    /// port, read by the recorders' <c>onPartDeCouple</c> handlers.
    ///
    /// <para>Stock (decompiled ModuleDockingNode, KSP 1.12.5): a port with a part on its
    /// docking node in the editor enters the "PreAttached" FSM state, whose OnEnter shows the
    /// "Undock" event. <c>Undock()</c> then sees <c>undockPreAttached</c> and calls
    /// <c>Decouple()</c>, which calls <c>Part.decouple()</c> on the port or on the part on its
    /// reference node, so <c>onPartDeCouple</c> fires and <c>onPartUndock</c> never does.
    /// Staging the same port (<c>OnActive</c>, when <c>stagingEnabled</c>) takes the very same
    /// <c>Decouple()</c> path with the port still PreAttached, so the FSM state cannot tell a
    /// click from staging. The intent is armed at the click instead, by
    /// <see cref="Patches.DockingNodeUndockIntentPatch"/> (a Prefix on <c>Undock()</c>); a
    /// staged port never arms it and keeps reading as a decouple.</para>
    /// </summary>
    internal static class DockingPortSeparation
    {
        private static uint armedPortPartPid;
        private static uint armedOtherPartPid;
        private static double armedUT = double.NaN;

        /// <summary>Arms the note for the port part and the part on its docking node.</summary>
        internal static void ArmPreAttachedUndock(uint portPartPid, uint otherPartPid, double ut)
        {
            armedPortPartPid = portPartPid;
            armedOtherPartPid = otherPartPid;
            armedUT = ut;
            ParsekLog.Verbose("DockUndockIntent",
                $"Armed pre-attached Undock intent: portPid={portPartPid} otherPid={otherPartPid} " +
                $"ut={ut.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Pure decision: an <c>onPartDeCouple</c> part is a clicked pre-attached Undock when
        /// the note is armed at the same UT (Undock and its Part.decouple run synchronously in
        /// one call) and the decoupling part is the port or the part on its docking node.
        /// </summary>
        internal static bool MatchesPreAttachedUndock(
            uint armedPortPid, uint armedOtherPid, double armedAtUT,
            uint decoupledPartPid, double ut)
        {
            if (armedPortPid == 0 || decoupledPartPid == 0) return false;
            if (armedAtUT != ut) return false;
            return decoupledPartPid == armedPortPid
                || (armedOtherPid != 0 && decoupledPartPid == armedOtherPid);
        }

        /// <summary>
        /// Consumes the note when <paramref name="decoupledPartPid"/> matches it. Returns true
        /// for a clicked pre-attached Undock (cause UNDOCK); false for anything else, including
        /// a staged port (never armed) and a stale note from an earlier UT (cleared).
        /// </summary>
        internal static bool TryConsumePreAttachedUndock(uint decoupledPartPid, double ut, string consumer)
        {
            if (armedPortPartPid == 0) return false;
            bool sameUT = armedUT == ut;
            bool match = MatchesPreAttachedUndock(
                armedPortPartPid, armedOtherPartPid, armedUT, decoupledPartPid, ut);
            ParsekLog.Verbose("DockUndockIntent",
                $"{consumer}: decouple pid={decoupledPartPid} vs armed portPid={armedPortPartPid} " +
                $"otherPid={armedOtherPartPid} sameUT={(sameUT ? "true" : "false")} " +
                $"=> {(match ? "UNDOCK (consumed)" : "DECOUPLE")}");
            if (match || !sameUT)
                Clear();
            return match;
        }

        internal static void Clear()
        {
            armedPortPartPid = 0;
            armedOtherPartPid = 0;
            armedUT = double.NaN;
        }
    }
}
