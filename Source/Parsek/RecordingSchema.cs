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

        // Format 1 / generation 4 is the 1.0 save-format baseline. Recordings and
        // sidecars carrying a different generation are rejected on load (reasons
        // "generation-missing" / "generation-older" / "generation-newer"). Only a
        // shape change (a renamed or removed key, a changed layout) bumps it; an
        // additive enum member does not.
        public const int CurrentRecordingSchemaGeneration = 4;
    }
}
