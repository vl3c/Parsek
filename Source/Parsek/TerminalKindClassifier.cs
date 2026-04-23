namespace Parsek
{
    /// <summary>
    /// Coarse terminal-outcome classification used by the re-fly session merge
    /// rule (design doc section 6.6 step 2).
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="InFlight"/> — recording never reached a terminal state, or ended with the vessel still orbiting / sub-orbital.</description></item>
    ///   <item><description><see cref="Landed"/> — landed, splashed, recovered, docked, or boarded. Merge rule commits <see cref="MergeState.Immutable"/>.</description></item>
    ///   <item><description><see cref="Crashed"/> — destroyed / BG-crash. Merge rule commits <see cref="MergeState.CommittedProvisional"/> so the slot stays rewindable (design §6.6 step 2, §7.17, §7.43).</description></item>
    /// </list>
    /// </summary>
    internal enum TerminalKind
    {
        InFlight = 0,
        Landed = 1,
        Crashed = 2,
    }

    /// <summary>
    /// Maps a <see cref="Recording.TerminalStateValue"/> to a coarse
    /// <see cref="TerminalKind"/>. Used by
    /// <see cref="SupersedeCommit.CommitSupersede"/> to decide whether a
    /// re-fly provisional commits as <see cref="MergeState.Immutable"/>
    /// (Landed / stable) or <see cref="MergeState.CommittedProvisional"/>
    /// (Crashed / still re-flyable).
    /// </summary>
    internal static class TerminalKindClassifier
    {
        /// <summary>
        /// Returns the <see cref="TerminalKind"/> for the given recording.
        /// Null recording yields <see cref="TerminalKind.InFlight"/>.
        /// </summary>
        public static TerminalKind Classify(Recording rec)
        {
            if (rec == null) return TerminalKind.InFlight;
            var t = rec.TerminalStateValue;
            if (!t.HasValue) return TerminalKind.InFlight;
            switch (t.Value)
            {
                case TerminalState.Destroyed:
                    return TerminalKind.Crashed;
                case TerminalState.Landed:
                case TerminalState.Splashed:
                case TerminalState.Recovered:
                case TerminalState.Docked:
                case TerminalState.Boarded:
                    return TerminalKind.Landed;
                case TerminalState.Orbiting:
                case TerminalState.SubOrbital:
                default:
                    return TerminalKind.InFlight;
            }
        }
    }
}
