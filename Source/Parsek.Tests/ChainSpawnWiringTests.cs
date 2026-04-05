using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the chain-tip detection helper (FindChainTipForRecording) and
    /// spawn routing wiring added in Task 6b-3a. These test the pure decision
    /// logic — actual spawn requires KSP runtime.
    /// </summary>
    [Collection("Sequential")]
    public class ChainSpawnWiringTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainSpawnWiringTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region Helpers

        static Recording MakeRecording(string id, uint vesselPid,
            double startUT = 0, double endUT = 100, string vesselName = "")
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselName = vesselName,
                Points = new List<TrajectoryPoint>()
            };
        }

        static GhostChain MakeChain(uint vesselPid, string tipRecordingId,
            bool terminated = false, double spawnUT = 2000.0)
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = vesselPid,
                TipRecordingId = tipRecordingId,
                SpawnUT = spawnUT,
                IsTerminated = terminated
            };
            chain.Links.Add(new ChainLink { recordingId = "link-1", ut = 1000.0 });
            chain.Links.Add(new ChainLink { recordingId = tipRecordingId, ut = spawnUT });
            return chain;
        }

        #endregion

        #region FindChainTipForRecording

        [Fact]
        public void FindChainTipForRecording_NullChains_ReturnsNull()
        {
            var rec = MakeRecording("rec-1", 100);

            var result = ParsekFlight.FindChainTipForRecording(null, rec);

            Assert.Null(result);
        }

        [Fact]
        public void FindChainTipForRecording_EmptyChains_ReturnsNull()
        {
            var chains = new Dictionary<uint, GhostChain>();
            var rec = MakeRecording("rec-1", 100);

            var result = ParsekFlight.FindChainTipForRecording(chains, rec);

            Assert.Null(result);
        }

        [Fact]
        public void FindChainTipForRecording_NoMatch_ReturnsNull()
        {
            var chain = MakeChain(100, "tip-rec");
            var chains = new Dictionary<uint, GhostChain> { { 100, chain } };

            // Recording with different ID -- not a chain tip
            var rec = MakeRecording("unrelated-rec", 999);

            var result = ParsekFlight.FindChainTipForRecording(chains, rec);

            Assert.Null(result);
        }

        [Fact]
        public void FindChainTipForRecording_MatchesTip_ReturnsChain()
        {
            var chain = MakeChain(100, "tip-rec");
            var chains = new Dictionary<uint, GhostChain> { { 100, chain } };

            var rec = MakeRecording("tip-rec", 100);

            var result = ParsekFlight.FindChainTipForRecording(chains, rec);

            Assert.NotNull(result);
            Assert.Same(chain, result);
            Assert.Equal("tip-rec", result.TipRecordingId);
            Assert.Equal((uint)100, result.OriginalVesselPid);
        }

        [Fact]
        public void FindChainTipForRecording_TerminatedTip_ReturnsNull()
        {
            var chain = MakeChain(100, "tip-rec", terminated: true);
            var chains = new Dictionary<uint, GhostChain> { { 100, chain } };

            var rec = MakeRecording("tip-rec", 100);

            var result = ParsekFlight.FindChainTipForRecording(chains, rec);

            Assert.Null(result);
        }

        [Fact]
        public void FindChainTipForRecording_IntermediateLink_ReturnsNull()
        {
            // Intermediate link has a different recording ID than the tip
            var chain = MakeChain(100, "tip-rec");
            var chains = new Dictionary<uint, GhostChain> { { 100, chain } };

            // "link-1" is an intermediate link, not the tip
            var rec = MakeRecording("link-1", 100);

            var result = ParsekFlight.FindChainTipForRecording(chains, rec);

            Assert.Null(result);
        }

        [Fact]
        public void FindChainTipForRecording_MultipleChains_FindsCorrectTip()
        {
            var chain1 = MakeChain(100, "tip-A");
            var chain2 = MakeChain(200, "tip-B");
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, chain1 },
                { 200, chain2 }
            };

            var recA = MakeRecording("tip-A", 100);
            var recB = MakeRecording("tip-B", 200);

            Assert.Same(chain1, ParsekFlight.FindChainTipForRecording(chains, recA));
            Assert.Same(chain2, ParsekFlight.FindChainTipForRecording(chains, recB));
        }

        [Fact]
        public void FindChainTipForRecording_TipMatchesDifferentVesselPid_StillReturnsChain()
        {
            // The tip recording ID match is by recording ID, not vessel PID.
            // A recording may have a different vessel PID but still be the chain tip.
            var chain = MakeChain(100, "tip-rec");
            var chains = new Dictionary<uint, GhostChain> { { 100, chain } };

            // Different vessel PID (e.g., post-dock combined vessel) but same recording ID
            var rec = MakeRecording("tip-rec", 300);

            var result = ParsekFlight.FindChainTipForRecording(chains, rec);

            Assert.NotNull(result);
            Assert.Same(chain, result);
        }

        #endregion
    }
}
