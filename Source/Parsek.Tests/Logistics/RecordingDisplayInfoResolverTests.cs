using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="RecordingStore.TryResolveRecordingDisplayInfo"/>, the literal-
    /// free by-id name/tree/order accessor the Logistics window calls for H5 (names
    /// instead of 8-char GUID fragments). It is the one non-pure piece of H5: it
    /// touches the static committed store, so these tests carry
    /// <c>[Collection("Sequential")]</c> and reset the store. The pure formatter
    /// (<c>LogisticsDeliveryPresentation.FormatSourceRecordingDisplay</c>) is covered
    /// separately. Keeping the raw-list read inside the already-allowlisted
    /// RecordingStore.cs is what keeps the ERS/ELS grep gate green without
    /// allowlisting the window.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingDisplayInfoResolverTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingDisplayInfoResolverTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // A committed recording with a vessel name, an owning tree, and a TreeOrder
        // round-trips its display fields. The tree name is distinct from the recording
        // name so the test proves the tree clause comes from RecordingTree.TreeName.
        [Fact]
        public void Resolve_CommittedRecording_ReturnsNameTreeAndOrder()
        {
            var rec = new Recording
            {
                RecordingId = "rec-h5-1",
                VesselName = "Mun Fuel Run",
                TreeOrder = 2
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec, treeName: "Munar Logistics");

            bool ok = RecordingStore.TryResolveRecordingDisplayInfo(
                "rec-h5-1", out string name, out string treeName, out int treeOrder);

            Assert.True(ok);
            Assert.Equal("Mun Fuel Run", name);
            Assert.Equal("Munar Logistics", treeName);
            Assert.Equal(2, treeOrder);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("resolved=true") && l.Contains("Munar Logistics"));
        }

        // An empty vessel name resolves to the "Untitled" fallback (matches the
        // Recordings table display).
        [Fact]
        public void Resolve_EmptyVesselName_UntitledFallback()
        {
            var rec = new Recording
            {
                RecordingId = "rec-h5-2",
                VesselName = "",
                TreeOrder = 0
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec, treeName: "Some Tree");

            bool ok = RecordingStore.TryResolveRecordingDisplayInfo(
                "rec-h5-2", out string name, out string treeName, out int treeOrder);

            Assert.True(ok);
            Assert.Equal("Untitled", name);
            Assert.Equal("Some Tree", treeName);
            Assert.Equal(0, treeOrder);
        }

        // An id not in the committed store resolves to false with the not-committed log.
        [Fact]
        public void Resolve_UnknownId_ReturnsFalse()
        {
            bool ok = RecordingStore.TryResolveRecordingDisplayInfo(
                "no-such-id", out string name, out string treeName, out int treeOrder);

            Assert.False(ok);
            Assert.Null(name);
            Assert.Null(treeName);
            Assert.Equal(-1, treeOrder);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("resolved=false"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Resolve_NullOrEmptyId_ReturnsFalse(string id)
        {
            bool ok = RecordingStore.TryResolveRecordingDisplayInfo(
                id, out string name, out string treeName, out int treeOrder);

            Assert.False(ok);
            Assert.Null(name);
            Assert.Null(treeName);
            Assert.Equal(-1, treeOrder);
        }
    }
}
