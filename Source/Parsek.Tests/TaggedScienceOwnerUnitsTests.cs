using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests on the two units of <c>LedgerOrchestrator.ResolveTreePendingScienceOwnerRecordingId</c>:
    /// <see cref="LedgerOrchestrator.ResolveTaggedScienceOwnerRecordingId"/> and
    /// <see cref="LedgerOrchestrator.FindScienceWindowChainId"/>. The whole-method behaviour is
    /// pinned by <see cref="TreePendingScienceOwnerCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class TaggedScienceOwnerUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TaggedScienceOwnerUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static LedgerOrchestrator.RecordingScienceWindow W(string id, string chain, double start, double end) =>
            new LedgerOrchestrator.RecordingScienceWindow { RecordingId = id, ChainId = chain, StartUT = start, EndUT = end };

        private static PendingScienceSubject S(string tag, double ut) =>
            new PendingScienceSubject { subjectId = "subj", recordingId = tag, captureUT = ut };

        [Fact]
        public void Tagged_InsideWindow_KeepsTagSilently()
        {
            var tagged = W("rec-a", null, 100.0, 200.0);
            string owner = LedgerOrchestrator.ResolveTaggedScienceOwnerRecordingId(
                S("rec-a", 150.0), "rec-a", tagged, new[] { tagged, W("rec-b", null, 100.0, 200.0) }, "rec-b");

            Assert.Equal("rec-a", owner);
            Assert.Empty(logLines);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void Tagged_ImplausibleCapture_KeepsTagEvenWhenAnotherWindowCovers(double ut)
        {
            var tagged = W("rec-a", null, 100.0, 200.0);
            string owner = LedgerOrchestrator.ResolveTaggedScienceOwnerRecordingId(
                S("rec-a", ut), "rec-a", tagged, new[] { tagged, W("rec-b", null, -10.0, 1e300) }, null);

            Assert.Equal("rec-a", owner);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Tagged_OutsideAndUncovered_KeepsTagWithVerboseLine()
        {
            var tagged = W("rec-a", null, 100.0, 200.0);
            string owner = LedgerOrchestrator.ResolveTaggedScienceOwnerRecordingId(
                S("rec-a", 500.0), "rec-a", tagged, new[] { tagged }, null);

            Assert.Equal("rec-a", owner);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE]") && l.Contains("Tree science routing")
                && l.Contains("no other recording window covers it"));
        }

        [Fact]
        public void Tagged_OutsideCoveredByChainSibling_KeepsTag()
        {
            var tagged = W("rec-a", "chain-1", 100.0, 200.0);
            string owner = LedgerOrchestrator.ResolveTaggedScienceOwnerRecordingId(
                S("rec-a", 250.0), "rec-a", tagged,
                new[] { tagged, W("rec-b", "chain-1", 200.0, 300.0) }, null);

            Assert.Equal("rec-a", owner);
            Assert.Contains(logLines, l => l.Contains("is a chain sibling (chain=chain-1)"));
        }

        [Fact]
        public void Tagged_OutsideCoveredByOtherChain_ReroutesWithWarn()
        {
            var tagged = W("rec-a", "chain-1", 100.0, 200.0);
            string owner = LedgerOrchestrator.ResolveTaggedScienceOwnerRecordingId(
                S("rec-a", 250.0), "rec-a", tagged,
                new[] { tagged, W("rec-c", "chain-2", 200.0, 300.0) }, null);

            Assert.Equal("rec-c", owner);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("re-routing by capture UT to 'rec-c'"));
        }

        [Fact]
        public void Tagged_EmptyChainIds_AreNotSiblings()
        {
            var tagged = W("rec-a", null, 100.0, 200.0);
            string owner = LedgerOrchestrator.ResolveTaggedScienceOwnerRecordingId(
                S("rec-a", 250.0), "rec-a", tagged,
                new[] { tagged, W("rec-c", null, 200.0, 300.0) }, null);

            Assert.Equal("rec-c", owner);
        }

        [Fact]
        public void ChainId_FirstMatchingWindowWins()
        {
            var windows = new[]
            {
                W("rec-a", "chain-1", 0, 1),
                W("rec-b", "chain-2", 0, 1),
                W("rec-b", "chain-3", 0, 1),
            };

            Assert.Equal("chain-2", LedgerOrchestrator.FindScienceWindowChainId(windows, "rec-b"));
            Assert.Equal("chain-1", LedgerOrchestrator.FindScienceWindowChainId(windows, "rec-a"));
        }

        [Fact]
        public void ChainId_NoMatchOrNullChain_IsNull()
        {
            var windows = new[] { W("rec-a", null, 0, 1) };

            Assert.Null(LedgerOrchestrator.FindScienceWindowChainId(windows, "rec-z"));
            Assert.Null(LedgerOrchestrator.FindScienceWindowChainId(windows, "rec-a"));
            Assert.Null(LedgerOrchestrator.FindScienceWindowChainId(
                new LedgerOrchestrator.RecordingScienceWindow[0], "rec-a"));
        }
    }
}
