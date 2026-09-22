using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECORDING nodes shaped by the production writer for metadata round-trip cells.
    /// <para>
    /// <see cref="RecordingTree.LoadRecordingFrom"/> rejects a node that lacks the
    /// current <c>recordingFormatVersion</c> / <c>recordingSchemaGeneration</c> stamps
    /// before it reads any other key, so a hand-built "missing key" node passes every
    /// default-value assertion without the loader ever running. These helpers write
    /// the node through <see cref="RecordingTree.SaveRecordingInto"/> (which stamps the
    /// current contract and omits every sparse default key) and assert the load got
    /// past the gate.
    /// </para>
    /// </summary>
    internal static class RecordingCodecTestNodes
    {
        /// <summary>
        /// A production-written node for a recording with nothing set but its id:
        /// carries the current schema stamps and none of the sparse keys.
        /// </summary>
        internal static ConfigNode BareCurrentContract(string recordingId)
        {
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, new Recording { RecordingId = recordingId });
            return node;
        }

        /// <summary>
        /// Loads <paramref name="node"/> through the production path into a fresh
        /// <see cref="Recording"/> and asserts the schema gate accepted it.
        /// </summary>
        internal static Recording LoadPastSchemaGate(ConfigNode node)
        {
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, loaded.RecordingFormatVersion);
            return loaded;
        }
    }
}
