namespace Parsek
{
    /// <summary>
    /// Merge-commit state for a <see cref="Recording"/>.
    ///
    /// Tri-state replaces the binary committed/uncommitted flag (design doc
    /// <c>docs/parsek-rewind-staging-design.md</c> section 5.5). Legacy saves
    /// that carried the boolean <c>committed</c> field migrate to this enum on
    /// load: <c>committed = True</c> maps to <see cref="Immutable"/> and
    /// <c>committed = False</c> maps to <see cref="NotCommitted"/>. Legacy saves
    /// without that field default to <see cref="Immutable"/> (the pre-feature
    /// invariant: every recording reachable from a committed tree was already
    /// immutable).
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="NotCommitted"/> — recording is still being produced (recorder active, or re-fly provisional with <c>SupersedeTargetId</c> set).</description></item>
    ///   <item><description><see cref="CommittedProvisional"/> — session merge has stamped the recording but the rewind slot remains available (BG-recorded children under an RP, or a re-fly that terminated in a crash and is still re-rewindable).</description></item>
    ///   <item><description><see cref="Immutable"/> — recording is sealed. Never mutated after this point; the rewind slot is closed.</description></item>
    /// </list>
    /// </summary>
    public enum MergeState
    {
        NotCommitted = 0,
        CommittedProvisional = 1,
        Immutable = 2
    }
}
