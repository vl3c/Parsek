namespace Parsek
{
    /// <summary>
    /// Merge-commit state for a <see cref="Recording"/>. This is the SINGLE
    /// source of truth for whether a re-fly child slot is open (re-flyable) or
    /// closed (sealed / concluded): a slot's open/closed is read from its
    /// effective chain+supersede tip recording's MergeState. There is no
    /// separate slot-level "sealed" bit (it was collapsed into this enum).
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="NotCommitted"/> — recording is still being produced (recorder active, or re-fly provisional with <c>SupersedeTargetId</c> set). The slot is open; excluded from ERS/ELS.</description></item>
    ///   <item><description><see cref="CommittedProvisional"/> — committed AND the slot is OPEN: a re-flyable Unfinished Flight (crashed terminal, stranded EVA, non-focus stable leaf) or a manually stashed stable leaf. Included in ERS/ELS.</description></item>
    ///   <item><description><see cref="Immutable"/> — committed AND the slot is CLOSED: sealed / concluded / canon. Permanent (Seal is a one-way <see cref="CommittedProvisional"/> -> <see cref="Immutable"/> transition; auto-seal on a stable / structural / safety re-fly outcome lands here directly). Preferred as a RELATIVE anchor and preserved as canon across a parent rewind. Once all of an RP's slots are Immutable the rewind point is reapable.</description></item>
    /// </list>
    /// A freshly created / loaded recording defaults to <see cref="Immutable"/>
    /// (a normal finished recording is concluded); the first commit demotes a
    /// slot's open-UF tip to <see cref="CommittedProvisional"/>.
    /// </summary>
    public enum MergeState
    {
        NotCommitted = 0,
        CommittedProvisional = 1,
        Immutable = 2
    }
}
