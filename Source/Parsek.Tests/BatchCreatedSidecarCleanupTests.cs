using System.Collections.Generic;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The isolated batch teardown deletes the recording sidecars the batch itself created
    /// (overnight 2026-11-01: H62 / H68 / H70 left `.prec` files on a save naming none;
    /// owner ruling "if unused, delete"). Only files absent at batch start, parsed as a
    /// recording sidecar and unnamed by the running game go.
    /// </summary>
    public class BatchCreatedSidecarCleanupTests
    {
        private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string New = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Live = "cccccccccccccccccccccccccccccccc";

        private static List<string> Select(string[] current, string[] baseline, string[] memory,
            out int keptReferenced, out int keptUnrecognized)
        {
            return InGameTestRunner.SelectBatchCreatedRecordingSidecars(
                current, baseline, new HashSet<string>(memory), out keptReferenced, out keptUnrecognized);
        }

        // catches: the H62 leftover. A tree the batch recorded flushed its sidecars and
        // was dropped from memory by the restore's Cold load.
        [Fact]
        public void BatchCreatedUnnamedSidecars_AreSelected()
        {
            int refd, unrec;
            var doomed = Select(
                new[] { Base + ".prec", Base + "_vessel.craft", New + ".prec", New + "_vessel.craft", New + "_ghost.craft" },
                new[] { Base + ".prec", Base + "_vessel.craft" },
                new[] { Base },
                out refd, out unrec);
            Assert.Equal(new[] { New + ".prec", New + "_ghost.craft", New + "_vessel.craft" }, doomed.ToArray());
            Assert.Equal(0, refd);
            Assert.Equal(0, unrec);
        }

        // A baseline file is never selected, even when memory no longer names it.
        [Fact]
        public void BaselineFiles_AreNeverSelected_CaseInsensitive()
        {
            int refd, unrec;
            var doomed = Select(new[] { Base + ".PREC" }, new[] { Base + ".prec" }, new string[0], out refd, out unrec);
            Assert.Empty(doomed);
        }

        // A new file the running game still names is kept: deleting it would lose live data.
        [Fact]
        public void NewFilesNamedByMemory_AreKept()
        {
            int refd, unrec;
            var doomed = Select(new[] { Live + ".prec", Live + "_vessel.craft" }, new string[0], new[] { Live },
                out refd, out unrec);
            Assert.Empty(doomed);
            Assert.Equal(2, refd);
        }

        // A name that does not parse as a recording sidecar is left alone.
        [Fact]
        public void UnrecognizedNames_AreKept()
        {
            int refd, unrec;
            var doomed = Select(new[] { "notes.txt", "desktop.ini" }, new string[0], new string[0], out refd, out unrec);
            Assert.Empty(doomed);
            Assert.Equal(2, unrec);
        }

        [Fact]
        public void NullInputs_SelectNothing()
        {
            int refd, unrec;
            Assert.Empty(InGameTestRunner.SelectBatchCreatedRecordingSidecars(null, null, null, out refd, out unrec));
            var doomed = InGameTestRunner.SelectBatchCreatedRecordingSidecars(
                new[] { New + ".prec" }, null, null, out refd, out unrec);
            Assert.Equal(new[] { New + ".prec" }, doomed.ToArray());
        }
    }
}
