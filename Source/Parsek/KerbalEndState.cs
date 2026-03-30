namespace Parsek
{
    /// <summary>
    /// Per-kerbal end state inferred from the recording's terminal state
    /// and the presence/absence of the kerbal in the vessel snapshot.
    /// Used to determine whether a kerbal should be treated as alive/recoverable
    /// or dead when the recording ends.
    /// </summary>
    public enum KerbalEndState
    {
        /// <summary>Kerbal is aboard a vessel that is still intact (orbiting, landed, splashed, suborbital).</summary>
        Aboard     = 0,
        /// <summary>Kerbal was killed (vessel destroyed with crew aboard).</summary>
        Dead       = 1,
        /// <summary>Kerbal was recovered with the vessel.</summary>
        Recovered  = 2,
        /// <summary>Kerbal's end state could not be determined (legacy recording, missing data).</summary>
        Unknown    = 3
    }
}
