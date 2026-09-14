namespace Parsek
{
    /// <summary>
    /// Owns the recording format/schema constants. They live here rather than on
    /// <see cref="RecordingStore"/> so the <see cref="Recording"/> data type can stamp
    /// itself without referencing the store that holds it (the store sits above
    /// Recording in the dependency graph, and the field initializers made the edge
    /// bidirectional). <see cref="RecordingStore"/> forwards both names, so
    /// <c>RecordingStore.CurrentRecordingFormatVersion</c> /
    /// <c>RecordingStore.CurrentRecordingSchemaGeneration</c> remain the spelling the
    /// existing callers and the docs use.
    /// </summary>
    internal static class RecordingSchema
    {
        public const int CurrentRecordingFormatVersion = 1;

        // Schema generation discriminator. Bumped on every clean-slate schema
        // reset; recordings/sidecars carrying a different generation are rejected
        // on load (reasons "generation-older" / "generation-newer") so a loader
        // never sees a shape it was not built for. Pre-1.0 dev: backwards
        // compatibility is explicitly NOT a goal, so each bump deletes the
        // tolerance seams that only existed to read the prior generation.
        //
        // Generation 2 landed the parent-anchor contract extension to
        // controlled-decoupled children (the on-disk truth table widened to
        // admit the previously-unreachable row IsDebris=false,
        // ParentAnchorRecordingId=non-null).
        //
        // Generation 3 is the clean-slate reset that retired the last batch of
        // pre-reset compatibility seams: the legacy v5 world-offset RELATIVE
        // contract, the committed-bool to MergeState migration, the Phase-F
        // tree-resource residual seam, the legacy rewind-suppression marker
        // normalizer, and the no-op format-version contract-upgrade helpers.
        // Generation 2 and older recordings are rejected with reason
        // "generation-older".
        //
        // Generation 4 renamed the parent-anchor ConfigNode key from
        // "debrisParentRecordingId" to "parentAnchorRecordingId" (the
        // DebrisParentRecordingId field renamed to ParentAnchorRecordingId).
        // Generation 3 and older recordings carry the old key and are rejected
        // with reason "generation-older".
        public const int CurrentRecordingSchemaGeneration = 4;
    }
}
