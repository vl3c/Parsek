namespace Parsek
{
    /// <summary>
    /// Shared predicates from issue #15 (a Re-Fly puts a vessel the player had already
    /// RECOVERED back in the world while the recovery's career rewards stay in the ledger).
    /// <c>ReFlyInvokeHold</c> (which holds back, at invoke, a vessel a committed recording
    /// recovers after the rewind UT) and <see cref="RecoveredRecordingEvidence"/> both read
    /// <see cref="VesselOutlivedTerminal"/>.
    ///
    /// <para>
    /// <b>Identity is guid-based, and a pid-only match is FORBIDDEN in
    /// <see cref="IsPositivelySameLaunch"/>.</b> KSP bakes <c>persistentId</c> into the .craft
    /// and reuses it on every launch, so a bare pid match cannot tell a fresh launch of the
    /// same craft from the recorded one. Everywhere else in the codebase an unknown guid
    /// degrades to pid-only, which is the right call for a visibility or dedup decision; a
    /// decision that takes funds out of the player's account requires a POSITIVE guid match
    /// on both sides and matches nothing when either side's guid is unknown.
    /// </para>
    /// </summary>
    internal static class ResurrectionRetirementEligibility
    {
        /// <summary>
        /// True when a recording's terminal leaves the recorded vessel in the world, so a LATER
        /// recovery of it is possible: no verdict yet, or a situation verdict (Orbiting,
        /// Landed, Splashed, SubOrbital). False for Recovered (handled as its own evidence)
        /// and for every verdict that ended the vessel (Destroyed, Docked, Boarded,
        /// Disassembled).
        /// </summary>
        internal static bool VesselOutlivedTerminal(TerminalState? terminal)
        {
            if (!terminal.HasValue) return true;
            switch (terminal.Value)
            {
                case TerminalState.Orbiting:
                case TerminalState.Landed:
                case TerminalState.Splashed:
                case TerminalState.SubOrbital:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True only when a live vessel is POSITIVELY the same launch the recording captured:
        /// pid equal AND both launch guids known AND equal. Unlike
        /// <see cref="VesselLaunchIdentity.LiveVesselIsRecordedLaunch"/> this does NOT fall
        /// back to pid-only on an unknown guid - see the class remarks for why. The
        /// implementation lives with its degrading sibling as
        /// <see cref="VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch"/>; this name is
        /// kept because the #15 rationale reads from here.
        /// </summary>
        internal static bool IsPositivelySameLaunch(Recording rec, uint livePid, string liveGuid)
        {
            return VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch(rec, livePid, liveGuid);
        }
    }
}
